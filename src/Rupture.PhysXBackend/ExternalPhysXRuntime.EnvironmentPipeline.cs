using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

/// <summary>
/// Staged environment capture. Every asynchronous capture (corridor prefetch, late scenes, runtime-spawned colliders,
/// deferred Unity shapes) is one <c>CaptureJob</c> that advances in budgeted slices on the main thread:
/// Enumerate, Query, Scan, Snapshot, Submit, Wait, Settle.
/// </summary>
/// <remarks>
/// Scan maps a collider's native shape and copies its Unity-cooked geometry. Native shape pointers are valid only for the
/// frame that mapped them, so every frame's first Scan call maps the corridor again (<c>PrepareProximity</c>) and extracts in
/// the same call; slices are cut by collider and never carry a pointer across frames.
/// Submit hands the snapshots to the scene owner in chunks of <c>AsyncChunkActors</c> without waiting (low-priority queue, see
/// ExternalPhysXRuntime.Owner.cs). The owner creates a chunk in slices: a slice loads one .pxc cooked mesh or heightfield (the cache
/// is not preloaded) or creates one actor shell with a bounded number of shapes whose meshes the earlier slices made resident, and
/// the owner serves its FIFO between slices, so a step that arrives behind a running chunk waits for one slice, not the chunk.
/// Wait polls the chunks. Only the main thread commits: it registers the created actors after observing the chunk complete,
/// so no managed state names an actor the owner has not produced yet.
/// Settle waits for the jobs this one depends on, then gives the consumer its references.
/// A collider is claimed by the job that scans it (<c>_claims</c>) until that job commits, which keeps a second job or the
/// synchronous <c>EnsureEnvironment</c> path from creating a duplicate actor. A synchronous acquisition resolves a claimed
/// collider either by driving the claiming job (<c>DriveJob</c>, casings) or by taking over just the corridor's claimed
/// colliders (<c>TakeOverClaims</c>, rigs), which is bounded by the corridor and still bounded by the 500 ms watchdog.
/// </remarks>
public sealed partial class ExternalPhysXRuntime
{
	/// <summary>Actors per asynchronous chunk; a chunk becomes visible to the main thread as a whole, so this bounds how many actors commit together.</summary>
	private const int AsyncChunkActors = 4;
	private const int MaximumOutstandingChunks = 3;
	/// <summary>Main-thread budget of the jobs that do not belong to a prefetch consumer, per frame.</summary>
	private const double MaintenanceCaptureBudgetMilliseconds = 1d;
	/// <summary>
	/// How far a re-requested prefetch corridor's endpoints may move before the capture is redone. The capture queries
	/// <see cref="PrefetchRadiusMargin"/> (the same distance) of extra radius, which makes a corridor whose endpoints lie within
	/// this distance of the requested ones a subset of the captured region: a Ready prefetch covers it.
	/// </summary>
	private const float PrefetchRefreshDistance = 0.5f;
	private const float PrefetchRadiusMargin = PrefetchRefreshDistance;
	/// <summary>An asynchronous acquisition is exact: Ready is decided by classifying the very corridor, so only a sliver of movement keeps its running capture.</summary>
	private const float AcquireCorridorTolerance = 0.05f;
	private const int MaximumAcquireAttempts = 3;
	private const float AcquireRequestTimeToLiveSeconds = 5f;
	/// <summary>Floor of the vertex count a mesh read is costed by, so the fixed per-read cost of small meshes does not inflate the per-vertex estimate.</summary>
	private const int MeshCostMinimumVertices = 512;
	private const int MaximumPendingPromotions = 8;
	private const int MaximumPooledJobs = 8;
	/// <summary>Estimated owner work per chunk (see <see cref="EstimateCreationWork"/>; about a few thousand mesh triangles of cold-cache cooking); keeps the latency until a chunk can commit short. A step does not wait for it, the owner slices a chunk.</summary>
	private const int AsyncChunkWork = 12000;
	private const int ShapeBaseWork = 64;
	/// <summary>Depth below a scene root down to which <see cref="StepEnumerate"/> visits nodes one at a time; deeper subtrees are gathered by one native call each.</summary>
	private const int EnumerateSplitDepth = 4;
	/// <summary>Main-thread budget of the actor snapshot rebuilds of one environment update (see <see cref="SnapshotRebuildBudgetSpent"/>).</summary>
	private static readonly long SnapshotRebuildBudgetTicks = Stopwatch.Frequency / 1000L;

	private readonly List<CaptureJob> _jobs = new List<CaptureJob>();
	private readonly Dictionary<int, CaptureJob> _claims = new Dictionary<int, CaptureJob>();
	private readonly List<ChunkWork> _inFlightChunks = new List<ChunkWork>();
	private readonly Dictionary<uint, PrefetchConsumer> _prefetchConsumers = new Dictionary<uint, PrefetchConsumer>();
	private readonly List<uint> _prefetchExpiryScratch = new List<uint>();
	private readonly List<PendingPromotion> _promotions = new List<PendingPromotion>();
	private readonly EnvironmentStepTimings _environmentTimings = new EnvironmentStepTimings();
	private readonly List<Collider> _rebuildColliderScratch = new List<Collider>(64);
	private long _snapshotRebuildTicks;
	private bool _snapshotRebuilt;
	private readonly List<CaptureJob> _pendingJobsScratch = new List<CaptureJob>();
	private readonly List<Collider> _mappingScratch = new List<Collider>(64);
	private readonly List<Collider> _rootColliderScratch = new List<Collider>(64);
	private readonly HashSet<EnvironmentActor> _desiredActorsScratch = new HashSet<EnvironmentActor>();
	/// <summary>Finished prefetch jobs, kept so a bot whose corridor refreshes while it fights does not allocate a job and its lists each time.</summary>
	private readonly Stack<CaptureJob> _jobPool = new Stack<CaptureJob>();
	private readonly HashSet<int> _takeOverIdsScratch = new HashSet<int>();
	private readonly List<ActorSnapshot> _takeOverSnapshotsScratch = new List<ActorSnapshot>();
	private long _prefetchPromotedReady, _prefetchPromotedPending, _prefetchFailures, _prefetchActorsCommitted;
	private long _asyncActorsCommitted, _syncEnvironmentCaptures, _environmentFastPaths;
	private readonly Dictionary<uint, PrefetchConsumer> _acquisitions = new Dictionary<uint, PrefetchConsumer>();
	private readonly List<uint> _acquireExpiryScratch = new List<uint>();
	/// <summary>Peak-held main-thread costs (mesh read per 1000 vertices, terrain read, corridor mapping without its sync) that decide whether the next collider still fits the slice budget.</summary>
	private PeakCost _meshReadCost, _terrainReadCost, _mappingCost;
	/// <summary>Real slices (query, mapping, extraction, snapshot shape, commit, settle) run since the current <see cref="RunJobs"/> began; the first one always runs.</summary>
	private int _sliceCountThisRun;
	/// <summary>Colliders of the last <see cref="ClassifyRegion"/> pass that an unfinished job has claimed (counted when the resolution is <see cref="ClaimResolution.Leave"/>).</summary>
	private int _regionClaimedCount;
	private long _acquireReady, _acquireFailed, _acquireJobs;
	private long _pumpCalls, _pumpOverruns, _maintenanceCalls, _maintenanceOverruns;
	private double _pumpMaxMilliseconds, _maintenanceMaxMilliseconds;
	private readonly long[] _sliceMaxTicks = new long[SliceStageCount];
	private long _sliceLargestTicks;
	private SliceStage _sliceLargestStage;
	/// <summary>The last successful proximity acquisition, so the same consumer asking for the same corridor again in the same frame costs nothing.</summary>
	private uint _lastAcquireConsumer;
	private Vector3 _lastAcquireCenter, _lastAcquireSweepEnd;
	private int _lastAcquireFrame = -1;
	private bool _lastAcquireCasing;
	private int _lastAcquireKnown;

	private enum CaptureKind : byte { Prefetch, Scene, Deferred, Terrain }

	/// <summary>How a synchronous acquisition treats colliders that an asynchronous job has claimed but not committed.</summary>
	private enum ClaimResolution : byte
	{
		/// <summary>Leave them to their job (an acquisition that does not need them).</summary>
		Leave,
		/// <summary>Run the claiming jobs to completion (casings).</summary>
		Drive,
		/// <summary>Take over only the corridor's claimed colliders, see <see cref="TakeOverClaims"/>.</summary>
		TakeOver
	}

	/// <summary>Declaration order matters: phases up to <see cref="Snapshot"/> are the main-thread heavy ones that obey the budget.</summary>
	private enum CapturePhase : byte { Enumerate, Query, Scan, Snapshot, Submit, Wait, Settle, Done, Failed }

	/// <summary>Kinds of main-thread slice whose longest duration is kept for the status line.</summary>
	private enum SliceStage : byte { Enumerate, Query, Map, Extract, Terrain, Classify, Snapshot, Commit, Settle }

	private const int SliceStageCount = 9;
	private static readonly string[] SliceStageNames = { "enumerate", "query", "map", "extract", "terrain", "classify", "snapshot", "commit", "settle" };

	#region Prefetch API

	public void RequestEnvironmentPrefetch(uint prefetchConsumerId, Vector3 center, Vector3 sweepEnd, float ttlSeconds)
	{
		if (prefetchConsumerId == 0 || !IsRaidReady) return;
		float expiresAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, ttlSeconds);
		bool exists = _prefetchConsumers.TryGetValue(prefetchConsumerId, out PrefetchConsumer consumer);
		if (IsFullPrebake)
		{
			// The whole raid is baked: there is nothing to capture, so the prefetch is ready at once.
			if (!exists) _prefetchConsumers.Add(prefetchConsumerId, consumer = new PrefetchConsumer(prefetchConsumerId));
			consumer.State = ExternalEnvironmentPrefetchState.Ready;
			consumer.ExpiresAt = expiresAt;
			return;
		}
		if (exists)
		{
			consumer.ExpiresAt = expiresAt;
			float distance = PrefetchRefreshDistance * PrefetchRefreshDistance;
			if (consumer.State != ExternalEnvironmentPrefetchState.Failed && (center - consumer.Center).sqrMagnitude <= distance &&
				(sweepEnd - consumer.SweepEnd).sqrMagnitude <= distance) return;
			// The old region's job keeps running so its actors still fill the cache; it just stops owning references.
			if (consumer.Job != null)
			{
				consumer.Job.ConsumerId = 0;
				consumer.Job.Consumer = null;
			}
		}
		else _prefetchConsumers.Add(prefetchConsumerId, consumer = new PrefetchConsumer(prefetchConsumerId));
		consumer.Center = center;
		consumer.SweepEnd = sweepEnd;
		consumer.ExpiresAt = expiresAt;
		consumer.State = ExternalEnvironmentPrefetchState.Pending;
		CaptureJob job = RentJob(CaptureKind.Prefetch, "prefetch");
		job.HasCorridor = true;
		job.QueryRadius = EnvironmentRadius + PrefetchRadiusMargin;
		job.Center = center;
		job.SweepEnd = sweepEnd;
		job.ConsumerId = prefetchConsumerId;
		job.Consumer = consumer;
		job.Phase = CapturePhase.Query;
		consumer.Job = job;
		_jobs.Add(job);
	}

	public ExternalEnvironmentPrefetchState GetEnvironmentPrefetchState(uint prefetchConsumerId) =>
		_prefetchConsumers.TryGetValue(prefetchConsumerId, out PrefetchConsumer consumer) ? consumer.State : ExternalEnvironmentPrefetchState.None;

	public double PumpEnvironmentPrefetch(double budgetMilliseconds)
	{
		long started = Stopwatch.GetTimestamp();
		ExpirePrefetchConsumers();
		int stepped = 0;
		// Full-prebake raids never hold prefetch-kind jobs: TryAcquireEnvironment fails there instead of creating one.
		if (IsRaidReady && _jobs.Count != 0)
		{
			ReapChunks();
			stepped = RunJobs(started + MillisecondsToTicks(budgetMilliseconds), prefetch: true);
		}
		double elapsed = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
		if (stepped != 0) AccountBudget(elapsed, budgetMilliseconds, ref _pumpCalls, ref _pumpOverruns, ref _pumpMaxMilliseconds);
		return elapsed;
	}

	/// <summary>Counts a budgeted call and, when it ran past its budget, an overrun; the largest call is kept.</summary>
	private static void AccountBudget(double elapsed, double budget, ref long calls, ref long overruns, ref double maximum)
	{
		calls++;
		if (elapsed > budget) overruns++;
		if (elapsed > maximum) maximum = elapsed;
	}

	private void RecordSlice(SliceStage stage, long startedTimestamp) => RecordSliceTicks(stage, Stopwatch.GetTimestamp() - startedTimestamp);

	private void RecordSliceTicks(SliceStage stage, long ticks)
	{
		if (ticks > _sliceMaxTicks[(int)stage]) _sliceMaxTicks[(int)stage] = ticks;
		if (ticks <= _sliceLargestTicks) return;
		_sliceLargestTicks = ticks;
		_sliceLargestStage = stage;
	}

	public void PromoteEnvironmentPrefetch(uint prefetchConsumerId, uint consumerId)
	{
		if (!_prefetchConsumers.TryGetValue(prefetchConsumerId, out PrefetchConsumer consumer)) return;
		_prefetchConsumers.Remove(prefetchConsumerId);
		if (IsFullPrebake) return;
		if (consumerId == 0 || consumerId == prefetchConsumerId)
		{
			DetachPrefetchJob(consumer);
			ReleaseConsumerReferences(prefetchConsumerId);
			return;
		}
		if (consumer.State == ExternalEnvironmentPrefetchState.Ready) _prefetchPromotedReady++;
		else if (consumer.State == ExternalEnvironmentPrefetchState.Pending) _prefetchPromotedPending++;
		CaptureJob job = consumer.Job;
		if (job != null && job.Phase < CapturePhase.Done)
		{
			// The unfinished job gives its references to the real consumer when it settles; the consumer's own EnsureEnvironment
			// resolves whatever of it is still claimed.
			job.ConsumerId = consumerId;
			job.Additive = true;
			job.Consumer = null;
		}
		if (!_proximityActorsByConsumer.TryGetValue(prefetchConsumerId, out HashSet<EnvironmentActor> held)) return;
		_lastAcquireFrame = -1;
		_proximityActorsByConsumer.Remove(prefetchConsumerId);
		if (!_proximityActorsByConsumer.TryGetValue(consumerId, out HashSet<EnvironmentActor> target))
			_proximityActorsByConsumer[consumerId] = target = new HashSet<EnvironmentActor>();
		foreach (EnvironmentActor actor in held)
		{
			// A reference the real consumer already holds is surplus; an unheld one simply changes owner.
			if (!target.Add(actor)) ReleaseProximityActor(actor);
		}
		TrimRetainedStaticEnvironment();
	}

	public void CancelEnvironmentPrefetch(uint prefetchConsumerId)
	{
		if (!_prefetchConsumers.TryGetValue(prefetchConsumerId, out PrefetchConsumer consumer)) return;
		_prefetchConsumers.Remove(prefetchConsumerId);
		DetachPrefetchJob(consumer);
		ReleaseConsumerReferences(prefetchConsumerId);
	}

	private static void DetachPrefetchJob(PrefetchConsumer consumer)
	{
		CaptureJob job = consumer.Job;
		if (job == null) return;
		job.ConsumerId = 0;
		job.Consumer = null;
		consumer.Job = null;
	}

	private void ExpirePrefetchConsumers()
	{
		if (_prefetchConsumers.Count == 0 && _acquisitions.Count == 0) return;
		float now = Time.realtimeSinceStartup;
		if (_prefetchConsumers.Count != 0)
		{
			_prefetchExpiryScratch.Clear();
			foreach (KeyValuePair<uint, PrefetchConsumer> entry in _prefetchConsumers)
				if (now > entry.Value.ExpiresAt) _prefetchExpiryScratch.Add(entry.Key);
			for (int index = 0; index < _prefetchExpiryScratch.Count; index++) CancelEnvironmentPrefetch(_prefetchExpiryScratch[index]);
		}
		if (_acquisitions.Count == 0) return;
		// A caller that stopped asking has abandoned its acquisition; the job still fills the cache but owns nothing.
		_acquireExpiryScratch.Clear();
		foreach (KeyValuePair<uint, PrefetchConsumer> entry in _acquisitions)
			if (now > entry.Value.ExpiresAt) _acquireExpiryScratch.Add(entry.Key);
		for (int index = 0; index < _acquireExpiryScratch.Count; index++)
		{
			uint consumerId = _acquireExpiryScratch[index];
			if (_acquisitions.TryGetValue(consumerId, out PrefetchConsumer request)) DropAcquisition(consumerId, request);
		}
	}

	private static long MillisecondsToTicks(double milliseconds) =>
		milliseconds <= 0d ? 0L : (long)(milliseconds * Stopwatch.Frequency / 1000d);

	#endregion

	#region Acquisition API

	/// <summary>
	/// Non-blocking acquisition for a consumer (see <see cref="IExternalEnvironmentPrefetchRuntime.TryAcquireEnvironment"/>).
	/// The only main-thread work on the Ready path is the overlap query and the dictionary classification of
	/// <see cref="ClassifyRegion"/> with its query-local hierarchy memo: no mapping, <c>Physics.SyncTransforms</c>, extraction or
	/// owner wait. Everything else is a staged job that <see cref="PumpEnvironmentPrefetch"/> advances within its budget.
	/// </summary>
	public ExternalEnvironmentPrefetchState TryAcquireEnvironment(uint consumerId, Vector3 center, Vector3 sweepEnd, bool casing)
	{
		if (consumerId == 0 || !IsRaidReady || (casing && _options?.GraphicalCasings != true)) return ExternalEnvironmentPrefetchState.Failed;
		_acquisitions.TryGetValue(consumerId, out PrefetchConsumer request);
		if (request != null && request.State == ExternalEnvironmentPrefetchState.Failed)
		{
			DropAcquisition(consumerId, request);
			_acquireFailed++;
			return ExternalEnvironmentPrefetchState.Failed;
		}
		if (!TryClassifyForAcquisition(center, sweepEnd, casing, out int unknown, out int claimed))
		{
			if (request != null) DropAcquisition(consumerId, request);
			_acquireFailed++;
			return ExternalEnvironmentPrefetchState.Failed;
		}
		if (IsFullPrebake && (unknown != 0 || claimed != 0))
		{
			// A full-prebake core never pumps the prefetch jobs, so an asynchronous capture would stall: report Failed at once
			// and let the caller capture synchronously. No job, request or claim is created.
			_acquireFailed++;
			return ExternalEnvironmentPrefetchState.Failed;
		}
		if (unknown == 0 && claimed == 0)
		{
			// Everything is already captured, possibly by a synchronous acquisition that followed a Pending result.
			if (request != null) DropAcquisition(consumerId, request);
			if (!IsFullPrebake) BindClassifiedRegion(consumerId);
			_acquireReady++;
			return ExternalEnvironmentPrefetchState.Ready;
		}
		float expiresAt = Time.realtimeSinceStartup + AcquireRequestTimeToLiveSeconds;
		if (request != null) request.ExpiresAt = expiresAt;
		bool sameCorridor = request != null && request.Casing == casing && Within(center, request.Center, AcquireCorridorTolerance) &&
			Within(sweepEnd, request.SweepEnd, AcquireCorridorTolerance);
		if (unknown == 0)
		{
			// Only claimed colliders: their jobs finish on their own and a capture of ours would have nothing to take. A request
			// that moved to another corridor stops owning its older corridor's job, whose commit would add stale references.
			if (request != null && !sameCorridor)
			{
				DetachPrefetchJob(request);
				request.Center = center;
				request.SweepEnd = sweepEnd;
				request.Casing = casing;
				request.State = ExternalEnvironmentPrefetchState.Pending;
				request.Attempts = 0;
			}
			return ExternalEnvironmentPrefetchState.Pending;
		}
		if (request != null)
		{
			CaptureJob running = request.Job;
			if (sameCorridor && running != null && running.Phase < CapturePhase.Done) return ExternalEnvironmentPrefetchState.Pending;
			// A finished capture that still leaves unknown colliders (a trimmed actor, a collider activated meanwhile) is retried a few times, then reported.
			if (sameCorridor && request.Attempts >= MaximumAcquireAttempts)
			{
				DropAcquisition(consumerId, request);
				_acquireFailed++;
				return ExternalEnvironmentPrefetchState.Failed;
			}
			DetachPrefetchJob(request);
			if (!sameCorridor) request.Attempts = 0;
		}
		else _acquisitions.Add(consumerId, request = new PrefetchConsumer(consumerId) { ExpiresAt = expiresAt });
		request.Center = center;
		request.SweepEnd = sweepEnd;
		request.Casing = casing;
		request.State = ExternalEnvironmentPrefetchState.Pending;
		request.Attempts++;
		StartAcquireJob(request);
		return ExternalEnvironmentPrefetchState.Pending;
	}

	/// <summary>
	/// Classifies the exact corridor without resolving claims: <paramref name="unknown"/> colliders nobody owns,
	/// <paramref name="claimed"/> colliders an unfinished job will still capture. False when classification threw (for a casing
	/// request, unsupported required geometry); the synchronous path reports that failure itself.
	/// </summary>
	private bool TryClassifyForAcquisition(Vector3 center, Vector3 sweepEnd, bool casing, out int unknown, out int claimed)
	{
		unknown = claimed = 0;
		bool previousCasingStage = _stageCasingEnvironment;
		_stageCasingEnvironment = casing;
		_environmentHierarchy.BeginQuery();
		try
		{
			// Body-owned colliders are skipped like the capture jobs skip them, otherwise one could never leave the unknown set.
			ClassifyRegion(center, sweepEnd, ClaimResolution.Leave, skipBodyShapes: true);
			unknown = _proximityUnknownScratch.Count;
			claimed = _regionClaimedCount;
			return true;
		}
		catch (Exception exception)
		{
			if (InfoLoggingActive) Log(ExternalLogLevel.Info, $"External PhysX asynchronous environment acquisition classification failed: {exception.GetType().Name}: {exception.Message}");
			return false;
		}
		finally
		{
			_environmentHierarchy.EndQuery();
			_stageCasingEnvironment = previousCasingStage;
		}
	}

	private void StartAcquireJob(PrefetchConsumer request)
	{
		CaptureJob job = RentJob(CaptureKind.Prefetch, request.Casing ? "casing-acquire" : "acquire");
		job.HasCorridor = true;
		job.Casing = request.Casing;
		job.Center = request.Center;
		job.SweepEnd = request.SweepEnd;
		job.QueryRadius = EnvironmentRadius;
		job.ConsumerId = request.Id;
		// The consumer may still hold the actors of its previous corridor; its next Ready call replaces that set.
		job.Additive = true;
		job.Consumer = request;
		job.Phase = CapturePhase.Query;
		request.Job = job;
		_jobs.Add(job);
		_acquireJobs++;
	}

	/// <summary>Forgets an acquisition and releases its capture's hold on the consumer; the capture keeps filling the cache.</summary>
	private void DropAcquisition(uint consumerId, PrefetchConsumer request)
	{
		_acquisitions.Remove(consumerId);
		DetachPrefetchJob(request);
	}

	private static bool Within(Vector3 left, Vector3 right, float distance) => (left - right).sqrMagnitude <= distance * distance;

	/// <summary>Binds the colliders of the last <see cref="ClassifyRegion"/> to a consumer: the references of every environment actor among them.</summary>
	private void BindClassifiedRegion(uint consumerId)
	{
		_proximityActorsScratch.Clear();
		foreach (int colliderId in _proximityColliderIdsScratch)
			if (_environmentByCollider.TryGetValue(colliderId, out EnvironmentActor actor)) _proximityActorsScratch.Add(actor);
		UpdateProximityConsumer(consumerId, _proximityActorsScratch);
	}

	#endregion

	#region Job scheduling

	private int RunJobs(long deadline, bool prefetch)
	{
		bool steppedHeavy = false;
		int stepped = 0;
		_sliceCountThisRun = 0;
		for (int index = 0; index < _jobs.Count; index++)
		{
			CaptureJob job = _jobs[index];
			if (job.Phase >= CapturePhase.Done)
			{
				_jobs.RemoveAt(index--);
				RetireJob(job);
				continue;
			}
			if ((job.Kind == CaptureKind.Prefetch) != prefetch) continue;
			bool heavy = job.Phase <= CapturePhase.Snapshot;
			// Every call advances at least one heavy slice so a tiny budget still makes progress.
			if (heavy && steppedHeavy && Stopwatch.GetTimestamp() >= deadline) continue;
			// Committing is main-thread work too: once something ran and the deadline passed, the rest waits for the next call.
			if (job.Phase == CapturePhase.Wait && _sliceCountThisRun != 0 && Stopwatch.GetTimestamp() >= deadline) continue;
			steppedHeavy |= heavy;
			stepped++;
			StepJob(job, deadline);
			if (job.Phase >= CapturePhase.Done)
			{
				_jobs.RemoveAt(index--);
				RetireJob(job);
			}
		}
		return stepped;
	}

	private void StepJob(CaptureJob job, long deadline)
	{
		if (job.Kind != CaptureKind.Terrain)
		{
			AdvanceJob(job, deadline);
			return;
		}
		long started = Stopwatch.GetTimestamp();
		AdvanceJob(job, deadline);
		RecordTerrainStep(Stopwatch.GetTimestamp() - started);
	}

	private void AdvanceJob(CaptureJob job, long deadline)
	{
		try
		{
			while (job.Phase < CapturePhase.Done)
			{
				bool advanced;
				switch (job.Phase)
				{
				case CapturePhase.Enumerate: advanced = StepEnumerate(job, deadline); break;
				case CapturePhase.Query: advanced = StepQuery(job); break;
				case CapturePhase.Scan: advanced = StepScan(job, deadline); break;
				case CapturePhase.Snapshot: advanced = StepSnapshot(job, deadline); break;
				case CapturePhase.Submit: advanced = StepSubmit(job); break;
				case CapturePhase.Wait: advanced = StepWait(job, deadline); break;
				case CapturePhase.Settle: advanced = StepSettle(job); break;
				default: advanced = false; break;
				}
				if (!advanced) return;
				if (job.Phase <= CapturePhase.Snapshot && Stopwatch.GetTimestamp() >= deadline) return;
			}
		}
		catch (Exception exception)
		{
			FailJob(job, exception.GetType().Name + ": " + exception.Message);
		}
	}

	/// <summary>
	/// Runs a job to completion on the calling thread, waiting for its chunks and for the jobs it depends on. Used by the
	/// casing acquisition (<see cref="ClaimResolution.Drive"/>), which needs everything its region touches created before its
	/// next step; its chunks go to the strict FIFO because the caller is blocked on them anyway, and every wait is bounded by
	/// the owner watchdog. Corridor acquisitions of rigs do not drive jobs, see <see cref="TakeOverClaims"/>.
	/// </summary>
	private void DriveJob(CaptureJob job)
	{
		job.SubmitUnbounded = true;
		for (int guard = 0; job.Phase < CapturePhase.Done; guard++)
		{
			if (guard > 100000) throw new InvalidOperationException("An environment capture job did not terminate.");
			StepJob(job, long.MaxValue);
			if (job.Phase == CapturePhase.Wait)
			{
				// The job's earlier chunks sit in the low-priority queue; moving them to the FIFO keeps this wait from
				// queueing behind other jobs' chunks and static promotions.
				for (int index = 0; index < job.Chunks.Count; index++)
					if (!job.Chunks[index].IsCompleted) MoveAsyncToFifo(job.Chunks[index]);
				for (int index = 0; index < job.Chunks.Count; index++) AwaitOwner(job.Chunks[index]);
			}
			else if (job.Phase == CapturePhase.Settle)
			{
				for (int index = 0; index < job.DependsOn.Count; index++)
				{
					CaptureJob dependency = job.DependsOn[index];
					if (!dependency.Committed && dependency.Phase < CapturePhase.Done) DriveJob(dependency);
				}
			}
		}
	}

	/// <summary>
	/// Gathers a late scene's colliders. The upper <see cref="EnumerateSplitDepth"/> levels of every root are walked node by node, so a root
	/// with thousands of children spreads over many slices; below that depth a node's whole subtree is one native
	/// <c>GetComponentsInChildren</c> call, which is far cheaper per node. A single call over a whole root can cost tens of milliseconds
	/// on a map-sized scene, and the deadline can only be honoured between calls.
	/// </summary>
	private bool StepEnumerate(CaptureJob job, long deadline)
	{
		long started = Stopwatch.GetTimestamp();
		List<Transform> nodes = job.EnumerateNodes;
		List<int> depths = job.EnumerateDepths;
		GameObject[] roots = job.Roots;
		while (true)
		{
			if (nodes.Count == 0)
			{
				if (job.RootCursor >= roots.Length) break;
				GameObject root = roots[job.RootCursor++];
				if (root == null) continue;
				nodes.Add(root.transform);
				depths.Add(0);
			}
			int last = nodes.Count - 1;
			Transform node = nodes[last];
			int depth = depths[last];
			nodes.RemoveAt(last);
			depths.RemoveAt(last);
			if (node == null) continue;
			if (depth >= EnumerateSplitDepth) node.GetComponentsInChildren(true, _rootColliderScratch);
			else
			{
				node.GetComponents(_rootColliderScratch);
				for (int child = 0; child < node.childCount; child++)
				{
					nodes.Add(node.GetChild(child));
					depths.Add(depth + 1);
				}
			}
			job.Candidates.AddRange(_rootColliderScratch);
			if (Stopwatch.GetTimestamp() < deadline) continue;
			RecordSlice(SliceStage.Enumerate, started);
			return false;
		}
		_rootColliderScratch.Clear();
		job.Roots = null;
		job.Phase = CapturePhase.Scan;
		RecordSlice(SliceStage.Enumerate, started);
		return true;
	}

	private bool StepQuery(CaptureJob job)
	{
		long started = Stopwatch.GetTimestamp();
		int count = QueryEnvironment(job.Center, job.SweepEnd, job.QueryRadius);
		for (int index = 0; index < count; index++) job.Candidates.Add(_overlapBuffer[index]);
		job.Phase = CapturePhase.Scan;
		_sliceCountThisRun++;
		RecordSlice(SliceStage.Query, started);
		return true;
	}

	/// <summary>
	/// Classifies the job's candidates one collider at a time. A scan slice memoizes the hierarchy classification (one query)
	/// and, for a casing job, applies casing eligibility to its own colliders only.
	/// </summary>
	private bool StepScan(CaptureJob job, long deadline)
	{
		bool previousCasingStage = _stageCasingEnvironment;
		_stageCasingEnvironment = previousCasingStage || job.Casing;
		_environmentHierarchy.BeginQuery();
		try { return ScanCandidates(job, deadline); }
		finally
		{
			_environmentHierarchy.EndQuery();
			_stageCasingEnvironment = previousCasingStage;
		}
	}

	private bool ScanCandidates(CaptureJob job, long deadline)
	{
		List<Collider> candidates = job.Candidates;
		while (job.CandidateCursor < candidates.Count)
		{
			Collider collider = candidates[job.CandidateCursor];
			long started = Stopwatch.GetTimestamp();
			if (Eligible(collider, includeDisabled: true))
			{
				if (RequiresGeometryRead(collider))
				{
					if (!ScanExtraction(job, collider, deadline)) return false;
				}
				else
				{
					ClassifyEligible(collider, job.Groups, job);
					RecordSlice(SliceStage.Classify, started);
				}
			}
			job.CandidateCursor++;
			if (job.CandidateCursor < candidates.Count && Stopwatch.GetTimestamp() >= deadline) return false;
		}
		job.Phase = CapturePhase.Snapshot;
		return true;
	}

	/// <summary>
	/// One collider whose geometry still has to be read from a native shape: it maps the corridor first when this frame has not
	/// (and extracts in the same slice, because a shape pointer must not outlive its frame), and it is the largest unit of
	/// main-thread work a scan has. When something already ran in this <see cref="RunJobs"/> call and the measured cost of this
	/// read (mapping, <c>Physics.SyncTransforms</c> if it has not run this frame, mesh or terrain extraction) would not fit the
	/// deadline, it returns false and the read becomes the first slice of the next call, where it always runs.
	/// </summary>
	private bool ScanExtraction(CaptureJob job, Collider collider, long deadline)
	{
		bool maps = !job.HasCorridor || job.MappedFrame != Time.frameCount;
		if (_sliceCountThisRun != 0 && Stopwatch.GetTimestamp() + EstimateExtractionTicks(collider, maps) > deadline) return false;
		if (job.HasCorridor && maps) MapCorridor(job);
		IMeshGeometrySource source = _meshGeometrySource;
		long syncBefore = source.SyncTransformsTotalTicks;
		long started = Stopwatch.GetTimestamp();
		ClassifyEligible(collider, job.Groups, job);
		long elapsed = Stopwatch.GetTimestamp() - started;
		// A sync inside the read is modelled by the source; subtracting it keeps the geometry cost free of it.
		long own = Math.Max(0L, elapsed - (source.SyncTransformsTotalTicks - syncBefore));
		if (collider is MeshCollider mesh)
		{
			_meshReadCost.Observe(own * 1000L / Math.Max(mesh.sharedMesh.vertexCount, MeshCostMinimumVertices));
			RecordSliceTicks(SliceStage.Extract, elapsed);
		}
		else
		{
			_terrainReadCost.Observe(own);
			RecordSliceTicks(SliceStage.Terrain, elapsed);
		}
		_sliceCountThisRun++;
		return true;
	}

	private long EstimateExtractionTicks(Collider collider, bool maps)
	{
		IMeshGeometrySource source = _meshGeometrySource;
		long estimate = 0L;
		if (maps)
		{
			estimate += _mappingCost.Ticks;
			if (!source.TransformsSyncedThisFrame) estimate += source.SyncTransformsEstimateTicks;
		}
		if (collider is MeshCollider mesh) estimate += _meshReadCost.Ticks * Math.Max(mesh.sharedMesh.vertexCount, MeshCostMinimumVertices) / 1000L;
		else estimate += _terrainReadCost.Ticks;
		return estimate;
	}

	/// <summary>
	/// Maps the native mesh and terrain shapes of the corridor's remaining candidates that still need a read, once for this
	/// frame, so the extraction that follows in the same slice finds them; a shape pointer must never outlive its frame (see
	/// UnityPhysXMeshGeometrySource.BeginMappingFrame). A corridor whose geometry is all cached, known or claimed maps nothing:
	/// no overlap and no <c>Physics.SyncTransforms</c>.
	/// </summary>
	private void MapCorridor(CaptureJob job)
	{
		long started = Stopwatch.GetTimestamp();
		job.MappedFrame = Time.frameCount;
		_mappingScratch.Clear();
		for (int index = job.CandidateCursor; index < job.Candidates.Count; index++)
		{
			Collider collider = job.Candidates[index];
			if ((collider is MeshCollider || collider is TerrainCollider) && Eligible(collider, includeDisabled: true) && RequiresGeometryRead(collider))
				_mappingScratch.Add(collider);
		}
		bool mapped = _mappingScratch.Count != 0;
		if (mapped)
		{
			long syncBefore = _meshGeometrySource.SyncTransformsTotalTicks;
			_meshGeometrySource.PrepareProximity(job.Center, job.SweepEnd, job.QueryRadius, _mappingScratch);
			long elapsed = Stopwatch.GetTimestamp() - started;
			_mappingCost.Observe(Math.Max(0L, elapsed - (_meshGeometrySource.SyncTransformsTotalTicks - syncBefore)));
			_sliceCountThisRun++;
		}
		_mappingScratch.Clear();
		if (mapped) RecordSlice(SliceStage.Map, started);
	}

	/// <summary>
	/// Builds each group's actor snapshot one collider at a time (<see cref="CaptureJob.PartialSnapshot"/>), so a group of hundreds
	/// of colliders cannot overrun the slice. A group completes with the colliders whose claim still names this job.
	/// </summary>
	private bool StepSnapshot(CaptureJob job, long deadline)
	{
		List<GroupBuilder> groups = job.Groups.Ordered;
		while (job.GroupCursor < groups.Count)
		{
			GroupBuilder group = groups[job.GroupCursor];
			List<Collider> colliders = group.Colliders;
			bool abandoned = group.Root == null;
			while (!abandoned && job.ShapeCursor < colliders.Count)
			{
				// The root can be destroyed between slices; its half-built snapshot is dropped like an unbuilt group.
				if (group.Root == null)
				{
					abandoned = true;
					break;
				}
				Collider collider = colliders[job.ShapeCursor++];
				// Colliders destroyed since they were scanned, and colliders a corridor acquisition took over, are dropped.
				if (collider == null || !OwnsClaim(job, collider.GetInstanceID())) continue;
				long started = Stopwatch.GetTimestamp();
				if (job.PartialSnapshot == null) job.PartialSnapshot = ActorSnapshot.BeginEnvironment(group.Root, group.Movable);
				job.PartialSnapshot.AddEnvironmentShape(collider, _options.DeadbodyLayer);
				_sliceCountThisRun++;
				RecordSlice(SliceStage.Snapshot, started);
				if (job.ShapeCursor < colliders.Count && Stopwatch.GetTimestamp() >= deadline) return false;
			}
			CompleteGroupSnapshot(job, abandoned);
			if (job.GroupCursor < groups.Count && Stopwatch.GetTimestamp() >= deadline) return false;
		}
		job.Phase = CapturePhase.Submit;
		return true;
	}

	private void CompleteGroupSnapshot(CaptureJob job, bool abandoned)
	{
		ActorSnapshot snapshot = job.PartialSnapshot;
		job.PartialSnapshot = null;
		job.ShapeCursor = 0;
		job.GroupCursor++;
		if (snapshot == null || abandoned) return;
		// A corridor acquisition may have taken a collider over after its shape was added: a taken collider is created by that
		// acquisition's own actor, so it must not appear in this job's snapshot as well.
		List<ShapeSnapshot> shapes = snapshot.Shapes;
		for (int index = shapes.Count - 1; index >= 0; index--)
			if (!OwnsClaim(job, shapes[index].ColliderId)) shapes.RemoveAt(index);
		if (shapes.Count == 0) return;
		job.Snapshots.Add(snapshot);
		foreach (ShapeSnapshot shape in shapes) job.SnapshotColliderIds.Add(shape.ColliderId);
	}

	private bool StepSubmit(CaptureJob job)
	{
		if (_disposed || _ownerThreadUnresponsive) throw new InvalidOperationException("External PhysX owner is unavailable.");
		while (job.SubmitCursor < job.Snapshots.Count)
		{
			if (!job.SubmitUnbounded && _inFlightChunks.Count >= MaximumOutstandingChunks)
			{
				ReapChunks();
				if (_inFlightChunks.Count >= MaximumOutstandingChunks) return false;
			}
			// A chunk is capped by estimated owner work as well as actor count, so one in-flight item stays short even when a
			// cold mesh cache makes the owner read or cook its geometry; an actor above the cap is a chunk of its own.
			int count = 0;
			int work = 0;
			while (count < AsyncChunkActors && job.SubmitCursor + count < job.Snapshots.Count)
			{
				int estimate = EstimateCreationWork(job.Snapshots[job.SubmitCursor + count]);
				if (count != 0 && work + estimate > AsyncChunkWork) break;
				work += estimate;
				count++;
			}
			ChunkWork chunk = new ChunkWork(job.Snapshots.GetRange(job.SubmitCursor, count));
			job.SubmitCursor += count;
			job.Chunks.Add(chunk);
			_inFlightChunks.Add(chunk);
			if (job.SubmitUnbounded)
			{
				chunk.Generation = _raidGeneration;
				EnqueueFifo(chunk);
			}
			else EnqueueAsync(chunk);
		}
		job.Phase = CapturePhase.Wait;
		return true;
	}

	/// <summary>Rough owner cost of creating an actor: a fixed amount per shape plus the geometry a cold mesh cache would have to read or cook.</summary>
	private static int EstimateCreationWork(ActorSnapshot snapshot)
	{
		long work = 0;
		for (int index = 0; index < snapshot.Shapes.Count; index++)
		{
			ShapeSnapshot shape = snapshot.Shapes[index];
			work += ShapeBaseWork;
			if (shape.Mesh != null) work += shape.Mesh.Indices != null ? shape.Mesh.Indices.Length : shape.Mesh.Vertices.Length * 3;
			else if (shape.HeightField?.Samples != null) work += shape.HeightField.Samples.Length / 4;
		}
		return (int)Math.Min(work, int.MaxValue);
	}

	private bool StepWait(CaptureJob job, long deadline)
	{
		for (int index = 0; index < job.Chunks.Count; index++)
			if (!job.Chunks[index].IsCompleted) return false;
		string failure = null;
		for (int index = 0; index < job.Chunks.Count && failure == null; index++)
			if (job.Chunks[index].Exception != null) failure = DescribeException(job.Chunks[index].Exception);
		if (failure != null && job.Atomic)
		{
			FailJob(job, failure);
			return true;
		}
		// Every chunk has completed, so which of them commit is already decided: registering them across slices cannot change
		// the outcome of an atomic job.
		if (!CommitChunks(job, deadline)) return false;
		FinishCommit(job, failure);
		return true;
	}

	/// <summary>
	/// Registers the actors of the completed, successfully created chunks the job has not committed yet, one chunk (at most
	/// <see cref="AsyncChunkActors"/> actors) per slice. The first chunk of a call always commits; false when the deadline
	/// passed with chunks left, and the next call continues. An atomic job (late scene, deferred shape) never gets here with a
	/// failed chunk: all of its created actors are destroyed instead, so it commits entirely or not at all; the exception is a
	/// chunk a corridor acquisition took over (<see cref="TakeOverClaims"/>), whose actors are already registered, which is
	/// harmless because that job's failure fails the raid's external environment anyway.
	/// </summary>
	private bool CommitChunks(CaptureJob job, long deadline)
	{
		for (int index = 0; index < job.Chunks.Count; index++)
		{
			ChunkWork chunk = job.Chunks[index];
			if (chunk.Committed || chunk.Exception != null) continue;
			long started = Stopwatch.GetTimestamp();
			CommitChunk(job, chunk);
			_sliceCountThisRun++;
			RecordSlice(SliceStage.Commit, started);
			if (Stopwatch.GetTimestamp() >= deadline && HasUncommittedChunk(job, index + 1)) return false;
		}
		return true;
	}

	private static bool HasUncommittedChunk(CaptureJob job, int from)
	{
		for (int index = from; index < job.Chunks.Count; index++)
			if (!job.Chunks[index].Committed && job.Chunks[index].Exception == null) return true;
		return false;
	}

	private void FinishCommit(CaptureJob job, string failure)
	{
		job.Committed = true;
		UnclaimJob(job);
		_asyncActorsCommitted += job.CommittedActorCount;
		switch (job.Kind)
		{
		case CaptureKind.Scene: _lateSceneAdditions += job.CommittedShapeCount; break;
		case CaptureKind.Prefetch: _prefetchActorsCommitted += job.CommittedActorCount; break;
		case CaptureKind.Terrain: break;
		default: _runtimeSpawnAdditions += job.CommittedShapeCount; break;
		}
		if (failure != null) job.Failure = failure;
		if (InfoLoggingActive)
			Log(ExternalLogLevel.Info, $"External PhysX {job.Source} environment batch committed asynchronously: actors={job.CommittedActorCount}, colliders={job.CommittedShapeCount}, heightfields={job.CommittedHeightFields}, total={_environmentByCollider.Count}, elapsedMs={(Stopwatch.GetTimestamp() - job.StartedTimestamp) * 1000d / Stopwatch.Frequency:F1}.");
		job.Phase = CapturePhase.Settle;
	}

	/// <summary>
	/// Registers one created chunk's actors. The actors exist natively now, so a step submitted after this includes them even
	/// if every body was asleep. Their colliders stop being claimed: a claim means "this job will still capture it", and an
	/// actor that is later removed (retained-cache trim) must not leave a claim that no job can ever resolve.
	/// </summary>
	private void CommitChunk(CaptureJob job, ChunkWork chunk)
	{
		CommitEnvironment(chunk.Snapshots, chunk.Created, job.CommittedActors);
		chunk.Committed = true;
		job.CommittedActorCount += chunk.Snapshots.Count;
		foreach (ActorSnapshot snapshot in chunk.Snapshots)
		{
			job.CommittedShapeCount += snapshot.Shapes.Count;
			foreach (ShapeSnapshot shape in snapshot.Shapes)
			{
				if (OwnsClaim(job, shape.ColliderId)) _claims.Remove(shape.ColliderId);
				if (shape.HeightField != null && InfoLoggingActive) job.CommittedHeightFields++;
			}
		}
		_activity.Changed();
	}

	private bool StepSettle(CaptureJob job)
	{
		bool dependencyFailed = false;
		for (int index = 0; index < job.DependsOn.Count; index++)
		{
			CaptureJob dependency = job.DependsOn[index];
			if (dependency.Committed) continue;
			if (dependency.Phase == CapturePhase.Failed) dependencyFailed = true;
			else return false;
		}
		long settleStarted = Stopwatch.GetTimestamp();
		ApplyJobReferences(job);
		if (job.Kind == CaptureKind.Prefetch && !IsFullPrebake) ParkUnreferenced(job.CommittedActors);
		job.CommittedActors.Clear();
		_sliceCountThisRun++;
		RecordSlice(SliceStage.Settle, settleStarted);
		string failure = job.Failure ?? (dependencyFailed ? "a capture this region depends on failed" : null);
		PrefetchConsumer consumer = job.Consumer;
		if (consumer != null && ReferenceEquals(consumer.Job, job))
		{
			consumer.Job = null;
			consumer.Failure = failure;
			consumer.State = failure == null ? ExternalEnvironmentPrefetchState.Ready : ExternalEnvironmentPrefetchState.Failed;
			if (failure != null) _prefetchFailures++;
		}
		if (dependencyFailed && job.FailsRaid)
		{
			job.Phase = CapturePhase.Failed;
			FailEnvironmentUpdate(job.Source, failure);
			return true;
		}
		job.Phase = CapturePhase.Done;
		return true;
	}

	private void ApplyJobReferences(CaptureJob job)
	{
		if (job.ConsumerId == 0 || IsFullPrebake) return;
		_desiredActorsScratch.Clear();
		for (int index = 0; index < job.RegionColliderIds.Count; index++)
			if (_environmentByCollider.TryGetValue(job.RegionColliderIds[index], out EnvironmentActor actor)) _desiredActorsScratch.Add(actor);
		// A promoted job adds to what its real consumer already holds; a prefetch consumer's set is replaced, which releases
		// the actors of the region it moved away from.
		if (job.Additive) AddProximityReferences(job.ConsumerId, _desiredActorsScratch);
		else UpdateProximityConsumer(job.ConsumerId, _desiredActorsScratch);
		_desiredActorsScratch.Clear();
	}

	/// <summary>
	/// A prefetch's new actors that nobody references stay in the cache disabled, exactly as a released corridor leaves them.
	/// Late-scene and deferred actors are not parked: they stay registered like any other environment actor and the shape audit
	/// disables them, so a large scene is not trimmed back out of the retained cache in the settle that created it.
	/// </summary>
	private void ParkUnreferenced(List<EnvironmentActor> actors)
	{
		for (int index = 0; index < actors.Count; index++)
		{
			EnvironmentActor actor = actors[index];
			if (actor.References != 0 || actor.Removed) continue;
			actor.References = 1;
			ReleaseProximityActor(actor);
		}
		TrimRetainedStaticEnvironment();
	}

	private void FailJob(CaptureJob job, string failure)
	{
		job.Failure = failure;
		job.Phase = CapturePhase.Failed;
		UnclaimJob(job);
		for (int index = 0; index < job.Chunks.Count; index++)
		{
			ChunkWork chunk = job.Chunks[index];
			if (chunk.Committed) continue;
			if (!chunk.IsCompleted) chunk.Abandoned = true;
			else if (chunk.Created != null) DestroyCreated(chunk.Created);
		}
		PrefetchConsumer consumer = job.Consumer;
		if (consumer != null && ReferenceEquals(consumer.Job, job))
		{
			consumer.Job = null;
			consumer.State = ExternalEnvironmentPrefetchState.Failed;
			consumer.Failure = failure;
		}
		if (job.Kind == CaptureKind.Prefetch) _prefetchFailures++;
		job.CommittedActors.Clear();
		Log(ExternalLogLevel.Warning, $"External PhysX {job.Source} environment capture failed without actor commit: {failure}");
		if (job.FailsRaid) FailEnvironmentUpdate(job.Source, failure);
	}

	private void UnclaimJob(CaptureJob job)
	{
		for (int index = 0; index < job.ClaimedIds.Count; index++)
		{
			int colliderId = job.ClaimedIds[index];
			if (_claims.TryGetValue(colliderId, out CaptureJob owner) && ReferenceEquals(owner, job)) _claims.Remove(colliderId);
		}
		job.ClaimedIds.Clear();
	}

	private void DestroyCreated(List<NativeActor> created)
	{
		for (int index = 0; index < created.Count; index++) QueueDestroyActor(created[index].Handle);
	}

	/// <summary>Drops completed chunks from the in-flight list; the actors of a chunk whose job failed meanwhile are destroyed.</summary>
	private void ReapChunks()
	{
		for (int index = _inFlightChunks.Count - 1; index >= 0; index--)
		{
			ChunkWork chunk = _inFlightChunks[index];
			if (!chunk.IsCompleted) continue;
			_inFlightChunks.RemoveAt(index);
			if (chunk.Abandoned && chunk.Created != null) DestroyCreated(chunk.Created);
		}
	}

	#endregion

	#region Capture classification

	/// <summary>
	/// Classifies one collider of a capture: skips ineligible, already known, body-owned and claimed colliders, defers inactive
	/// mesh and terrain colliders, validates and caches the geometry of active ones, and groups the rest by movement root.
	/// A <paramref name="job"/> also records the corridor's colliders and claims what it takes; the synchronous path passes none.
	/// </summary>
	private void ClassifyForCapture(Collider collider, CaptureGroups groups, CaptureJob job)
	{
		if (!Eligible(collider, includeDisabled: true)) return;
		ClassifyEligible(collider, groups, job);
	}

	/// <summary>
	/// Whether classifying an eligible collider has to read its geometry from a native shape: it is active, nobody owns or
	/// claims it, and its Unity-cooked mesh or terrain heightfield is not in memory (a terrain is read once per raid). Everything
	/// else is dictionary bookkeeping.
	/// </summary>
	private bool RequiresGeometryRead(Collider collider)
	{
		if (!collider.enabled || !collider.gameObject.activeInHierarchy) return false;
		int colliderId = collider.GetInstanceID();
		if (_environmentByCollider.ContainsKey(colliderId) || _bodyShapesByCollider.ContainsKey(colliderId) || _claims.ContainsKey(colliderId)) return false;
		if (collider is MeshCollider mesh) return !MeshGeometryRepository.IsCached(mesh);
		return collider is TerrainCollider terrain && !MeshGeometryRepository.IsCached(terrain);
	}

	/// <summary>The classification of <see cref="ClassifyForCapture"/> for a collider that already passed <see cref="Eligible"/>.</summary>
	private void ClassifyEligible(Collider collider, CaptureGroups groups, CaptureJob job)
	{
		int colliderId = collider.GetInstanceID();
		bool active = collider.enabled && collider.gameObject.activeInHierarchy;
		if (job != null && job.Kind == CaptureKind.Prefetch && active) job.RegionColliderIds.Add(colliderId);
		if (_environmentByCollider.ContainsKey(colliderId) || _bodyShapesByCollider.ContainsKey(colliderId)) return;
		if (_claims.TryGetValue(colliderId, out CaptureJob owner))
		{
			if (job != null && !ReferenceEquals(owner, job) && !job.DependsOn.Contains(owner))
			{
				job.DependsOn.Add(owner);
				owner.Dependents++;
			}
			return;
		}
		if (collider is MeshCollider || collider is TerrainCollider)
		{
			if (!active)
			{
				DeferUnityShape(collider);
				return;
			}
			// Validate and cache the current Unity-owned native shape before it is
			// grouped into an atomic actor transaction. An active missing shape is
			// a hard failure; silently committing an incomplete collision scene is
			// less safe than leaving EFT's native corpse physics active for this raid.
			if (collider is MeshCollider unityMesh) MeshGeometryRepository.Get(unityMesh);
			else MeshGeometryRepository.Get((TerrainCollider)collider);
		}
		Transform root = MovementRoot(collider, out bool movable);
		groups.Add(root, movable, collider);
		if (job == null) return;
		_claims[colliderId] = job;
		job.ClaimedIds.Add(colliderId);
	}

	#endregion

	#region Corridor takeover

	/// <summary>
	/// A synchronous corridor acquisition (corpse rig, living rig, gib) must find complete geometry in its corridor, but must
	/// not run whole asynchronous jobs: a late scene holds thousands of colliders. For the corridor's claimed colliders only
	/// (<c>_takeOverIdsScratch</c>, owners in <c>_pendingJobsScratch</c>) it takes the claim over:
	/// a collider whose actor snapshot does not exist yet is simply unclaimed, so the job skips it when it builds its
	/// snapshots and the acquisition captures it synchronously with the unowned colliders; a collider that already has a
	/// snapshot is covered by creating that actor now (unsubmitted snapshots as one FIFO chunk, submitted chunks moved to
	/// the FIFO and awaited) and registering it ahead of its job. No collider gets a second actor, and the work is bounded
	/// by the corridor, not by the job. A chunk registered early stays in its job, which skips it when it commits.
	/// </summary>
	private void TakeOverClaims()
	{
		for (int jobIndex = 0; jobIndex < _pendingJobsScratch.Count; jobIndex++)
		{
			CaptureJob job = _pendingJobsScratch[jobIndex];
			bool built = false;
			foreach (int colliderId in _takeOverIdsScratch)
			{
				if (!OwnsClaim(job, colliderId)) continue;
				if (job.Phase <= CapturePhase.Snapshot && !job.SnapshotColliderIds.Contains(colliderId)) _claims.Remove(colliderId);
				else built = true;
			}
			if (built) CommitTakenSnapshots(job);
		}
		_takeOverIdsScratch.Clear();
	}

	private bool OwnsClaim(CaptureJob job, int colliderId) => _claims.TryGetValue(colliderId, out CaptureJob owner) && ReferenceEquals(owner, job);

	/// <summary>Creates and registers the actors of a job's built snapshots that cover the corridor's claimed colliders.</summary>
	private void CommitTakenSnapshots(CaptureJob job)
	{
		List<ActorSnapshot> taken = _takeOverSnapshotsScratch;
		taken.Clear();
		List<ActorSnapshot> snapshots = job.Snapshots;
		for (int index = job.SubmitCursor; index < snapshots.Count; index++)
		{
			ActorSnapshot snapshot = snapshots[index];
			if (!CoversTakenCollider(snapshot)) continue;
			// Swapping the snapshot below the submit cursor keeps it out of the job's own chunks.
			snapshots[index] = snapshots[job.SubmitCursor];
			snapshots[job.SubmitCursor++] = snapshot;
			taken.Add(snapshot);
		}
		if (taken.Count != 0)
		{
			ChunkWork chunk = new ChunkWork(new List<ActorSnapshot>(taken)) { Generation = _raidGeneration };
			taken.Clear();
			job.Chunks.Add(chunk);
			_inFlightChunks.Add(chunk);
			EnqueueFifo(chunk);
		}
		for (int index = 0; index < job.Chunks.Count; index++)
		{
			ChunkWork chunk = job.Chunks[index];
			if (chunk.Committed || !CoversTakenCollider(chunk.Snapshots)) continue;
			AwaitChunk(chunk);
			if (chunk.Exception != null)
				throw new InvalidOperationException("An environment capture this region depends on failed: " + DescribeException(chunk.Exception));
			CommitChunk(job, chunk);
		}
	}

	private bool CoversTakenCollider(List<ActorSnapshot> snapshots)
	{
		for (int index = 0; index < snapshots.Count; index++)
			if (CoversTakenCollider(snapshots[index])) return true;
		return false;
	}

	private bool CoversTakenCollider(ActorSnapshot snapshot)
	{
		List<ShapeSnapshot> shapes = snapshot.Shapes;
		for (int index = 0; index < shapes.Count; index++)
			if (_takeOverIdsScratch.Contains(shapes[index].ColliderId)) return true;
		return false;
	}

	/// <summary>Blocks on a chunk under the owner watchdog; a chunk still in the asynchronous queue is moved to the FIFO first.</summary>
	private void AwaitChunk(ChunkWork chunk)
	{
		if (!chunk.IsCompleted) MoveAsyncToFifo(chunk);
		AwaitOwner(chunk);
	}

	#endregion

	#region Starting jobs

	private CaptureJob RentJob(CaptureKind kind, string source)
	{
		CaptureJob job = _jobPool.Count != 0 ? _jobPool.Pop() : new CaptureJob();
		job.Begin(kind, source);
		return job;
	}

	/// <summary>
	/// Called when a finished job leaves <c>_jobs</c>. Only small prefetch jobs that no other job waits on are pooled: repeated
	/// corridor refreshes of a fighting bot would otherwise allocate one job and its lists each, while a late scene's candidate
	/// lists are better released than kept.
	/// </summary>
	private void RetireJob(CaptureJob job)
	{
		if (job.Kind != CaptureKind.Prefetch || job.Dependents != 0 || _jobPool.Count >= MaximumPooledJobs) return;
		job.Reset();
		_jobPool.Push(job);
	}

	private void DetachJobsOf(uint consumerId)
	{
		for (int index = 0; index < _jobs.Count; index++)
			if (_jobs[index].ConsumerId == consumerId) _jobs[index].ConsumerId = 0;
	}

	private bool HasActiveJob(CaptureKind kind)
	{
		for (int index = 0; index < _jobs.Count; index++)
			if (_jobs[index].Kind == kind && _jobs[index].Phase < CapturePhase.Done) return true;
		return false;
	}

	private void StartDeferredCapture(Collider collider)
	{
		CaptureJob job = RentJob(CaptureKind.Deferred, "deferred-unity-shape");
		job.Atomic = true;
		job.FailsRaid = true;
		job.Phase = CapturePhase.Scan;
		job.Candidates.Add(collider);
		_jobs.Add(job);
	}

	/// <summary>
	/// A late scene is captured across frames and committed atomically. Its colliders are mapped on demand instead of by a
	/// whole-scene remap, so only the negative shape cache is retired up front.
	/// </summary>
	private void StartSceneCapture(Scene scene)
	{
		_meshGeometrySource.ClearNoShapeCache();
		CaptureJob job = RentJob(CaptureKind.Scene, "late-scene");
		job.Atomic = true;
		job.FailsRaid = true;
		job.Phase = CapturePhase.Enumerate;
		job.Roots = scene.GetRootGameObjects();
		_jobs.Add(job);
	}

	/// <summary>Per-frame maintenance of everything that is not a prefetch consumer's work.</summary>
	private void RunMaintenanceCapture()
	{
		long mark = Stopwatch.GetTimestamp();
		ExpirePrefetchConsumers();
		if (_inFlightChunks.Count != 0) ReapChunks();
		mark = _environmentTimings.Lap(EnvironmentStep.Expire, mark);
		if (_jobs.Count != 0)
		{
			long started = Stopwatch.GetTimestamp();
			if (RunJobs(started + MillisecondsToTicks(MaintenanceCaptureBudgetMilliseconds), prefetch: false) != 0)
				AccountBudget((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency, MaintenanceCaptureBudgetMilliseconds,
					ref _maintenanceCalls, ref _maintenanceOverruns, ref _maintenanceMaxMilliseconds);
		}
		mark = _environmentTimings.Lap(EnvironmentStep.Jobs, mark);
		PollPromotions();
		_environmentTimings.Lap(EnvironmentStep.Promotions, mark);
	}

	#endregion

	#region Reference holding

	private void AddProximityReferences(uint consumerId, HashSet<EnvironmentActor> additions)
	{
		_lastAcquireFrame = -1;
		if (!_proximityActorsByConsumer.TryGetValue(consumerId, out HashSet<EnvironmentActor> current))
			_proximityActorsByConsumer[consumerId] = current = new HashSet<EnvironmentActor>();
		foreach (EnvironmentActor actor in additions)
		{
			if (!current.Add(actor)) continue;
			if (actor.References == 0)
			{
				RemoveFromRetainedStaticEnvironment(actor);
				SetEnvironmentActorEnabled(actor, true);
			}
			actor.References++;
		}
		TrimRetainedStaticEnvironment();
	}

	private void ReleaseConsumerReferences(uint consumerId)
	{
		_lastAcquireFrame = -1;
		if (!_proximityActorsByConsumer.TryGetValue(consumerId, out HashSet<EnvironmentActor> actors)) return;
		_proximityActorsByConsumer.Remove(consumerId);
		foreach (EnvironmentActor actor in actors) ReleaseProximityActor(actor);
		TrimRetainedStaticEnvironment();
	}

	#endregion

	#region Static-to-kinematic promotion

	/// <summary>
	/// Starts the asynchronous replacement of a static actor whose transform moved. The old actor stays in the scene, with
	/// static-pose propagation, until the owner reports the kinematic replacement created; only then does the main thread swap.
	/// </summary>
	/// <remarks>
	/// Until the swap, pose, enable-state and scale-rebuild commands for the actor reach the old static native actor. That is
	/// valid: <c>rptpx_set_actor_pose</c> falls back to <c>setGlobalPose</c> for an actor that is not a kinematic body,
	/// <c>rptpx_set_shape_enabled</c> and <c>rptpx_attach_shape</c> work on a rigid static, and none of them latches
	/// <c>_ownerFailure</c>. <see cref="CompletePromotion"/> applies the latest pose, scale and enable state at the swap.
	/// </remarks>
	private bool TryPromoteStaticActor(EnvironmentActor actor)
	{
		long started = Stopwatch.GetTimestamp();
		try
		{
			List<Collider> colliders = _rebuildColliderScratch;
			colliders.Clear();
			foreach (EnvironmentShape shape in actor.Shapes)
				if (shape.Collider != null) colliders.Add(shape.Collider);
			if (colliders.Count == 0 || _disposed || _ownerThreadUnresponsive) return false;
			ActorSnapshot replacement = ActorSnapshot.CaptureEnvironment(actor.Root, colliders, movable: true, _options.DeadbodyLayer);
			bool enabled = IsFullPrebake || actor.References > 0;
			foreach (ShapeSnapshot shape in replacement.Shapes) shape.Enabled &= enabled;
			PromotionWork work = new PromotionWork(replacement);
			_promotions.Add(new PendingPromotion(actor, work, actor.Root.lossyScale) { PreviousScale = actor.LastScale });
			EnqueueAsync(work);
			return true;
		}
		catch (Exception exception)
		{
			Log(ExternalLogLevel.Warning, $"External PhysX could not replace moved static actor {actor.Root?.name}: {exception.GetType().Name}: {exception.Message}");
			return false;
		}
		finally
		{
			_rebuildColliderScratch.Clear();
			ChargeSnapshotRebuild(started, EnvironmentStep.Promote);
		}
	}

	private void PollPromotions()
	{
		for (int index = _promotions.Count - 1; index >= 0; index--)
		{
			PendingPromotion promotion = _promotions[index];
			if (!promotion.Work.IsCompleted) continue;
			_promotions.RemoveAt(index);
			CompletePromotion(promotion);
		}
	}

	private void CompletePromotion(PendingPromotion promotion)
	{
		EnvironmentActor actor = promotion.Actor;
		if (promotion.Work.Exception != null)
		{
			Log(ExternalLogLevel.Warning, $"External PhysX could not replace moved static actor {actor.Root?.name}: {DescribeException(promotion.Work.Exception)}");
			// The audit recorded the current scale when it queued this promotion; the old static actor never got it, so the movable poll must rebuild it.
			if (!actor.Removed && actor.Root != null)
			{
				actor.LastScale = promotion.PreviousScale;
				actor.ScaleRebuildPending = true;
			}
			return;
		}
		NativeActor promoted = promotion.Work.Promoted;
		if (actor.Removed || actor.Root == null)
		{
			QueueDestroyActor(promoted.Handle);
			return;
		}
		NativeActor previous = actor.Native;
		actor.ReplaceNative(promoted);
		QueueDestroyActor(previous.Handle);
		// The replacement was built from a snapshot taken when the promotion started: bring its pose, scale and shape
		// enable state up to date with what the actor did meanwhile.
		actor.Root.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
		Vector3 scale = actor.Root.lossyScale;
		QueueActorPose(promoted.Handle, new NativeApi.Pose(position, rotation), kinematicTarget: true);
		actor.LastPosition = position;
		actor.LastRotation = rotation;
		if ((scale - promotion.SnapshotScale).sqrMagnitude > 0.000001f) RebuildActorShapes(actor);
		actor.LastScale = scale;
		SetEnvironmentActorEnabled(actor, IsFullPrebake || actor.References > 0);
	}

	/// <summary>Rebuilds an actor's shapes at the current transform scale (a moved actor whose scale changed).</summary>
	private void RebuildActorShapes(EnvironmentActor actor)
	{
		long started = Stopwatch.GetTimestamp();
		List<Collider> colliders = _rebuildColliderScratch;
		colliders.Clear();
		foreach (EnvironmentShape shape in actor.Shapes)
			if (shape.Collider != null) colliders.Add(shape.Collider);
		if (colliders.Count != 0)
		{
			// The snapshot copies what it needs from the colliders, so the scratch list is free again once it is built.
			ActorSnapshot replacement = ActorSnapshot.CaptureEnvironment(actor.Root, colliders, true, _options.DeadbodyLayer);
			EnqueueOwner(() => _scene?.ReplaceActorShapes(actor.Native, replacement));
			_scaleRebuilds++;
		}
		colliders.Clear();
		ChargeSnapshotRebuild(started, EnvironmentStep.Rebuild);
	}

	/// <summary>
	/// Whether this environment update has spent its main-thread budget on actor snapshot rebuilds (static promotions and scale
	/// rebuilds). A snapshot captures every shape of an actor, so a burst of moved actors would otherwise land in one frame; the first
	/// rebuild of an update always runs, and the audits retry what they skipped on their next visit.
	/// </summary>
	private bool SnapshotRebuildBudgetSpent => _snapshotRebuilt && _snapshotRebuildTicks >= SnapshotRebuildBudgetTicks;

	private void ChargeSnapshotRebuild(long startedTimestamp, EnvironmentStep step)
	{
		long elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
		_snapshotRebuilt = true;
		_snapshotRebuildTicks += elapsed;
		_environmentTimings.Add(step, elapsed);
	}

	#endregion

	#region Reset

	private void ResetEnvironmentPipeline()
	{
		_jobs.Clear();
		_claims.Clear();
		_inFlightChunks.Clear();
		_prefetchConsumers.Clear();
		_prefetchExpiryScratch.Clear();
		_acquisitions.Clear();
		_acquireExpiryScratch.Clear();
		_promotions.Clear();
		_pendingJobsScratch.Clear();
		_mappingScratch.Clear();
		_rootColliderScratch.Clear();
		_desiredActorsScratch.Clear();
		_takeOverIdsScratch.Clear();
		_takeOverSnapshotsScratch.Clear();
		_prefetchPromotedReady = _prefetchPromotedPending = _prefetchFailures = _prefetchActorsCommitted = 0L;
		_asyncActorsCommitted = _syncEnvironmentCaptures = _environmentFastPaths = 0L;
		_acquireReady = _acquireFailed = _acquireJobs = 0L;
		_pumpCalls = _pumpOverruns = _maintenanceCalls = _maintenanceOverruns = 0L;
		_pumpMaxMilliseconds = _maintenanceMaxMilliseconds = 0d;
		Array.Clear(_sliceMaxTicks, 0, _sliceMaxTicks.Length);
		_sliceLargestTicks = 0L;
		_sliceLargestStage = SliceStage.Enumerate;
		_meshReadCost.Reset();
		_terrainReadCost.Reset();
		_mappingCost.Reset();
		_sliceCountThisRun = 0;
		_regionClaimedCount = 0;
		_lastAcquireFrame = -1;
		_asyncItems = _stepAsyncWaits = 0L;
		_asyncItemMaxMilliseconds = _stepAsyncWaitMaxMilliseconds = 0d;
		ResetTerrainCapture();
		_environmentTimings.Reset();
		_snapshotRebuildTicks = 0L;
		_snapshotRebuilt = false;
		_rebuildColliderScratch.Clear();
		_movablePolledThisFrame = _worstMovablePolled = false;
		_worstJobs = _worstActors = _worstMovable = _worstPromotions = _worstLateScenes = 0;
	}

	private string DescribePipeline()
	{
		int ready = 0;
		int failed = 0;
		foreach (PrefetchConsumer consumer in _prefetchConsumers.Values)
		{
			if (consumer.State == ExternalEnvironmentPrefetchState.Ready) ready++;
			else if (consumer.State == ExternalEnvironmentPrefetchState.Failed) failed++;
		}
		return $"prefetch=consumers:{_prefetchConsumers.Count}/ready:{ready}/failed:{failed}/promotedReady:{_prefetchPromotedReady}/promotedPending:{_prefetchPromotedPending}/failures:{_prefetchFailures}/actors:{_prefetchActorsCommitted}, " +
			$"acquire=pending:{_acquisitions.Count}/ready:{_acquireReady}/failed:{_acquireFailed}/jobs:{_acquireJobs}, " +
			$"envPipeline=jobs:{_jobs.Count}/claims:{_claims.Count}/chunks:{_inFlightChunks.Count}/asyncActors:{_asyncActorsCommitted}/syncCaptures:{_syncEnvironmentCaptures}/fastPaths:{_environmentFastPaths}/promotions:{_promotions.Count}, " +
			DescribePump() + _environmentTimings.Describe() + DescribeWorstFrameContext() + DescribeOwnerAsync() + DescribeTerrain();
	}

	/// <summary>
	/// Budget accounting of the staged capture: calls and overruns of the prefetch pump and of the maintenance capture, the
	/// longest call, the longest single slice with its stage, and every stage's longest slice. An overrun is a call whose total
	/// ran past its budget; the largest slice bounds how far one call can overshoot.
	/// </summary>
	private string DescribePump()
	{
		System.Text.StringBuilder stages = new System.Text.StringBuilder();
		for (int index = 0; index < SliceStageCount; index++)
			stages.Append(index == 0 ? "" : "/").Append(SliceStageNames[index]).Append(':').Append((_sliceMaxTicks[index] * 1000d / Stopwatch.Frequency).ToString("F1"));
		return $"pump=calls:{_pumpCalls}/overruns:{_pumpOverruns}/maxMs:{_pumpMaxMilliseconds:F1}/maintenanceCalls:{_maintenanceCalls}/maintenanceOverruns:{_maintenanceOverruns}/maintenanceMaxMs:{_maintenanceMaxMilliseconds:F1}" +
			$"/largestSliceMs:{_sliceLargestTicks * 1000d / Stopwatch.Frequency:F1}({SliceStageNames[(int)_sliceLargestStage]})/sliceMaxMs:{stages}" +
			$"/cost:meshPer1kVerticesMs:{_meshReadCost.Ticks * 1000d / Stopwatch.Frequency:F2}/terrainMs:{_terrainReadCost.Ticks * 1000d / Stopwatch.Frequency:F2}/mappingMs:{_mappingCost.Ticks * 1000d / Stopwatch.Frequency:F2}, ";
	}

	#endregion

	#region Types

	private sealed class GroupBuilder
	{
		internal Transform Root;
		internal bool Movable;
		internal readonly List<Collider> Colliders = new List<Collider>();
	}

	/// <summary>Colliders of one capture grouped by movement root; each group becomes one actor.</summary>
	private sealed class CaptureGroups
	{
		private readonly Dictionary<int, GroupBuilder> _byRoot = new Dictionary<int, GroupBuilder>();
		private readonly Stack<GroupBuilder> _spare = new Stack<GroupBuilder>();
		internal readonly List<GroupBuilder> Ordered = new List<GroupBuilder>();

		internal void Add(Transform root, bool movable, Collider collider)
		{
			int key = root.GetInstanceID();
			if (!_byRoot.TryGetValue(key, out GroupBuilder group))
			{
				group = _spare.Count != 0 ? _spare.Pop() : new GroupBuilder();
				_byRoot.Add(key, group);
				Ordered.Add(group);
			}
			group.Root = root;
			group.Movable = movable;
			group.Colliders.Add(collider);
		}

		/// <summary>Empties the groups and keeps their builders for the next capture of a pooled job.</summary>
		internal void Clear()
		{
			for (int index = 0; index < Ordered.Count; index++)
			{
				GroupBuilder group = Ordered[index];
				group.Root = null;
				group.Movable = false;
				group.Colliders.Clear();
				_spare.Push(group);
			}
			_byRoot.Clear();
			Ordered.Clear();
		}
	}

	/// <summary>A prefetch registration, or the pending asynchronous acquisition of a consumer (<see cref="_acquisitions"/>) that its capture job reports its outcome to.</summary>
	private sealed class PrefetchConsumer
	{
		internal readonly uint Id;
		internal Vector3 Center, SweepEnd;
		internal float ExpiresAt;
		internal ExternalEnvironmentPrefetchState State = ExternalEnvironmentPrefetchState.Pending;
		internal string Failure;
		internal CaptureJob Job;
		/// <summary>Acquisitions only: casing semantics, and how many captures were started for the current corridor.</summary>
		internal bool Casing;
		internal int Attempts;
		internal PrefetchConsumer(uint id) { Id = id; }
	}

	private sealed class CaptureJob
	{
		internal CaptureKind Kind;
		internal string Source;
		internal CapturePhase Phase;
		/// <summary>All chunks commit or none do (except chunks a corridor acquisition took over, see <see cref="TakeOverClaims"/>).</summary>
		internal bool Atomic;
		/// <summary>A failure fails the raid's external environment instead of only this capture.</summary>
		internal bool FailsRaid;
		internal bool HasCorridor;
		/// <summary>Casing eligibility applies to the colliders this job scans (<see cref="Eligible"/> rejects unsupported required geometry).</summary>
		internal bool Casing;
		internal bool SubmitUnbounded;
		internal bool Committed;
		/// <summary>The consumer's references are added to its set instead of replacing it (a promoted prefetch).</summary>
		internal bool Additive;
		internal Vector3 Center, SweepEnd;
		/// <summary>Radius of the corridor query; a prefetch widens it by <see cref="PrefetchRadiusMargin"/>, an acquisition is exact.</summary>
		internal float QueryRadius = EnvironmentRadius;
		/// <summary>The consumer that receives references when the job settles; zero when nobody does.</summary>
		internal uint ConsumerId;
		internal PrefetchConsumer Consumer;
		internal GameObject[] Roots;
		internal int RootCursor;
		/// <summary>Transforms of the current root still to be visited, with their depth below the root (<see cref="StepEnumerate"/>).</summary>
		internal readonly List<Transform> EnumerateNodes = new List<Transform>();
		internal readonly List<int> EnumerateDepths = new List<int>();
		internal readonly List<Collider> Candidates = new List<Collider>();
		internal int CandidateCursor;
		internal readonly List<int> RegionColliderIds = new List<int>();
		internal readonly List<int> ClaimedIds = new List<int>();
		internal readonly CaptureGroups Groups = new CaptureGroups();
		internal int GroupCursor;
		/// <summary>The actor snapshot of the group at <see cref="GroupCursor"/>, built one collider at a time up to <see cref="ShapeCursor"/>.</summary>
		internal ActorSnapshot PartialSnapshot;
		internal int ShapeCursor;
		internal readonly List<ActorSnapshot> Snapshots = new List<ActorSnapshot>();
		/// <summary>Colliders whose actor snapshot exists; a claimed collider outside this set can still be taken from the job.</summary>
		internal readonly HashSet<int> SnapshotColliderIds = new HashSet<int>();
		internal int SubmitCursor;
		internal readonly List<ChunkWork> Chunks = new List<ChunkWork>();
		internal readonly List<CaptureJob> DependsOn = new List<CaptureJob>();
		/// <summary>Jobs that name this one in <see cref="DependsOn"/>; a job with dependents is never pooled.</summary>
		internal int Dependents;
		internal readonly List<EnvironmentActor> CommittedActors = new List<EnvironmentActor>();
		internal int CommittedActorCount, CommittedShapeCount, CommittedHeightFields;
		internal int MappedFrame = -1;
		internal string Failure;
		internal long StartedTimestamp;

		internal void Begin(CaptureKind kind, string source)
		{
			Kind = kind;
			Source = source;
			StartedTimestamp = Stopwatch.GetTimestamp();
		}

		/// <summary>Returns the job to its freshly constructed state; only called once nothing references it any more.</summary>
		internal void Reset()
		{
			Phase = CapturePhase.Enumerate;
			Atomic = FailsRaid = HasCorridor = Casing = SubmitUnbounded = Committed = Additive = false;
			QueryRadius = EnvironmentRadius;
			Center = SweepEnd = default;
			ConsumerId = 0;
			Consumer = null;
			Roots = null;
			RootCursor = 0;
			EnumerateNodes.Clear();
			EnumerateDepths.Clear();
			Candidates.Clear();
			CandidateCursor = 0;
			RegionColliderIds.Clear();
			ClaimedIds.Clear();
			Groups.Clear();
			GroupCursor = 0;
			PartialSnapshot = null;
			ShapeCursor = 0;
			Snapshots.Clear();
			SnapshotColliderIds.Clear();
			SubmitCursor = 0;
			Chunks.Clear();
			DependsOn.Clear();
			Dependents = 0;
			CommittedActors.Clear();
			CommittedActorCount = CommittedShapeCount = CommittedHeightFields = 0;
			MappedFrame = -1;
			Failure = null;
		}
	}

	/// <summary>
	/// Environment actors created on the owner without anyone waiting; the main thread commits them once the item completes. The
	/// owner runs it one <see cref="NativeScene.CreateSlice"/> step per turn (see <see cref="OwnerWork.RunSlice"/>), so no single turn
	/// loads more than one geometry or creates more than one bounded batch of shapes.
	/// </summary>
	private abstract class SlicedCreationWork : OwnerWork
	{
		private readonly ActorCreationCursor _cursor;
		/// <summary>The created actors in snapshot order; null until every actor exists and when creation failed (the scene already destroyed what it had made).</summary>
		internal List<NativeActor> Created;

		protected SlicedCreationWork(List<ActorSnapshot> snapshots, string name)
		{
			_cursor = new ActorCreationCursor(snapshots);
			Name = name;
		}

		internal sealed override bool RunSlice(ExternalPhysXRuntime runtime)
		{
			NativeScene scene = runtime._scene ?? throw new InvalidOperationException("External PhysX scene is unavailable.");
			Phase = "environment creation";
			if (!scene.CreateSlice(_cursor)) return false;
			Created = _cursor.Created;
			return true;
		}

		internal sealed override void Run(ExternalPhysXRuntime runtime)
		{
			while (!RunSlice(runtime)) { }
		}
	}

	/// <summary>A few environment actors created together on the owner without anyone waiting; the main thread commits them.</summary>
	private sealed class ChunkWork : SlicedCreationWork
	{
		internal readonly List<ActorSnapshot> Snapshots;
		/// <summary>Its job failed first: the actors are destroyed when the chunk completes instead of committed.</summary>
		internal bool Abandoned;
		internal bool Committed;

		internal ChunkWork(List<ActorSnapshot> snapshots) : base(snapshots, "EnvironmentChunk")
		{
			Snapshots = snapshots;
		}
	}

	private sealed class PromotionWork : SlicedCreationWork
	{
		internal PromotionWork(ActorSnapshot snapshot) : base(new List<ActorSnapshot>(1) { snapshot }, "StaticActorPromotion") { }

		/// <summary>The kinematic replacement actor, valid once the item completed without an exception.</summary>
		internal NativeActor Promoted => Created[0];
	}

	private sealed class PendingPromotion
	{
		internal readonly EnvironmentActor Actor;
		internal readonly PromotionWork Work;
		internal readonly Vector3 SnapshotScale;
		/// <summary>The actor's <c>LastScale</c> before the audit replaced it with the promotion's scale; restored when the promotion fails.</summary>
		internal Vector3 PreviousScale;

		internal PendingPromotion(EnvironmentActor actor, PromotionWork work, Vector3 snapshotScale)
		{
			Actor = actor;
			Work = work;
			SnapshotScale = snapshotScale;
		}
	}

	#endregion
}
