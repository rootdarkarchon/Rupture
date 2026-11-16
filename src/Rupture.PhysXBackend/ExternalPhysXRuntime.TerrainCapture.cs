using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

/// <summary>
/// ProximityCache keeps every active terrain captured for the whole raid: each terrain's heightfield is read once and its native
/// actor is created and held by a permanent internal consumer, so no kill, prefetch or acquisition ever reads a terrain.
/// </summary>
/// <remarks>
/// The capture runs from the per-frame environment update as one ordinary maintenance <c>CaptureJob</c> of kind
/// <see cref="CaptureKind.Terrain"/> per terrain, one new job per frame. Its main-thread cost is one native heightfield read in a
/// single slice (<c>Physics.SyncTransforms</c> if the frame has not run it yet, the terrain probe mapping, the extraction; about
/// 8 to 10 ms per terrain on a raid map in the measured raids, reported as <c>terrainCapture=...maxMs</c>), plus a snapshot build
/// and a commit that are far below 1 ms. The owner thread creates the actor asynchronously. The environment update starts as
/// soon as the scene is ready, which in a raid is the map-loading window (the plugin runs it while the raid world exists and
/// before the raid simulation starts), so the cost lands before the first kill.
/// <para>
/// Terrains of late scenes get the same treatment: <see cref="NotifySceneLoaded"/> asks for a rescan, and the scene's own
/// capture job waits (<see cref="TerrainCaptureBlocksScenes"/>) until the terrains it would scan are claimed or captured. A terrain that
/// only becomes active later reaches the capture through the deferred-shape audit. The capture waits for a deferrable frame
/// (<see cref="ExternalBackendOptions.DeferrableFrame"/>) for at most <see cref="MaximumTerrainDeferralFrames"/> frames.
/// </para>
/// <para>
/// The managed heightfield stays in <see cref="MeshGeometryRepository"/> after the actor exists. It is not released because it is
/// still needed: a static-to-kinematic promotion, a scale rebuild and a re-capture of a removed actor snapshot the collider again
/// (<c>ActorSnapshot.AddEnvironmentShape</c>), which would otherwise read the terrain from native memory on the main thread, and the
/// owner thread may read the shared instance while a creation is in flight. It costs about the terrain's sample bytes
/// (<c>terrainCache=...bytes</c>, 8.4 MB for the two terrains of the measured raid).
/// </para>
/// A full prebake captures terrains with everything else in its initial bake and its late-scene captures, so nothing here runs there.
/// </remarks>
public sealed partial class ExternalPhysXRuntime
{
	/// <summary>The permanent consumer that holds every terrain actor; rig, casing and prefetch consumer ids never reach it (see ExternalEnvironmentPrefetchContracts).</summary>
	private const uint TerrainConsumerId = uint.MaxValue;

	/// <summary>How many frames a pending terrain capture waits for a deferrable frame before it runs anyway.</summary>
	private const int MaximumTerrainDeferralFrames = 120;

	/// <summary>Captures attempted per terrain before it is reported failed (a terrain whose native shape is not there yet at map load succeeds on a later attempt), and the frames between attempts.</summary>
	private const int MaximumTerrainAttempts = 3;
	private const int TerrainRetryFrames = 120;

	/// <summary>How often the active terrains are enumerated again besides the late-scene notifications, so a terrain that is enabled later joins the capture; the enumeration is allocation-free.</summary>
	private const float TerrainRescanIntervalSeconds = 1f;

	private readonly List<TerrainEntry> _terrains = new List<TerrainEntry>(4);
	private readonly List<Terrain> _terrainScratch = new List<Terrain>(8);
	private bool _terrainRescan = true;
	private float _nextTerrainRescan;
	private int _terrainDeferredFrames;
	private long _terrainStepTicks, _terrainStepMaxTicks;

	private enum TerrainState : byte
	{
		/// <summary>Not captured and no job running.</summary>
		Pending,
		/// <summary>A capture job is running and will claim the collider.</summary>
		Running,
		/// <summary>The actor exists and the permanent consumer references it.</summary>
		Held,
		/// <summary>The capture failed or ended without an actor; a corridor that needs the terrain still captures it on demand.</summary>
		Failed
	}

	private sealed class TerrainEntry
	{
		internal readonly TerrainCollider Collider;
		internal readonly int ColliderId;
		internal TerrainState State;
		internal int Attempts, RetryFrame;
		internal CaptureJob Job;
		internal EnvironmentActor Actor;

		internal TerrainEntry(TerrainCollider collider)
		{
			Collider = collider;
			ColliderId = collider.GetInstanceID();
		}
	}

	#region Capture

	/// <summary>
	/// Enumerates the active terrains when asked to, takes the hold of every terrain whose actor exists and starts the capture of
	/// the next pending one. Runs before late scenes start, so a scene's job never reaches an unclaimed terrain first.
	/// </summary>
	private void UpdateTerrainCapture()
	{
		if (IsFullPrebake) return;
		if (_terrainRescan || Time.realtimeSinceStartup >= _nextTerrainRescan) RescanTerrains();
		TerrainEntry pending = null;
		for (int index = _terrains.Count - 1; index >= 0; index--)
		{
			TerrainEntry entry = _terrains[index];
			if (entry.Collider == null)
			{
				// The terrain was destroyed (unloaded scene); its actor leaves with the static audit once its root is gone.
				_terrains.RemoveAt(index);
				continue;
			}
			ReconcileTerrain(entry);
			if (entry.State == TerrainState.Pending && Time.frameCount >= entry.RetryFrame) pending = entry;
		}
		if (pending == null)
		{
			_terrainDeferredFrames = 0;
			return;
		}
		if (_options.DeferrableFrame?.Invoke() == false && ++_terrainDeferredFrames <= MaximumTerrainDeferralFrames) return;
		_terrainDeferredFrames = 0;
		StartTerrainCapture(pending);
	}

	/// <summary>Takes the hold of the terrains a job committed this frame, before the shape audit can see an unreferenced actor.</summary>
	private void HoldCapturedTerrains()
	{
		for (int index = 0; index < _terrains.Count; index++)
			if (_terrains[index].Collider != null) ReconcileTerrain(_terrains[index]);
	}

	/// <summary>
	/// Whether a late scene must wait: a terrain is still running its capture or waiting for its first one, so the scene's job could
	/// read it first. A terrain waiting to retry a failed capture does not hold scenes back.
	/// </summary>
	private bool TerrainCaptureBlocksScenes()
	{
		if (IsFullPrebake) return false;
		for (int index = 0; index < _terrains.Count; index++)
			if (_terrains[index].State == TerrainState.Running || (_terrains[index].State == TerrainState.Pending && _terrains[index].Attempts == 0)) return true;
		return false;
	}

	private void RescanTerrains()
	{
		_terrainRescan = false;
		_nextTerrainRescan = Time.realtimeSinceStartup + TerrainRescanIntervalSeconds;
		MeshGeometryRepository.PruneDestroyedTerrains();
		Terrain.GetActiveTerrains(_terrainScratch);
		for (int index = 0; index < _terrainScratch.Count; index++)
		{
			TerrainCollider collider = _terrainScratch[index].GetComponent<TerrainCollider>();
			if (collider != null) TrackTerrain(collider);
		}
		_terrainScratch.Clear();
	}

	/// <summary>Registers an active, environment-layer terrain collider once.</summary>
	private void TrackTerrain(TerrainCollider collider)
	{
		if (collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.terrainData == null ||
			((1 << collider.gameObject.layer) & _options.EnvironmentMask) == 0) return;
		int colliderId = collider.GetInstanceID();
		for (int index = 0; index < _terrains.Count; index++)
			if (_terrains[index].ColliderId == colliderId) return;
		_terrains.Add(new TerrainEntry(collider));
	}

	private void StartTerrainCapture(TerrainEntry entry)
	{
		CaptureJob job = RentJob(CaptureKind.Terrain, "terrain");
		job.Atomic = true;
		job.Phase = CapturePhase.Scan;
		job.Candidates.Add(entry.Collider);
		_jobs.Add(job);
		entry.Job = job;
		entry.State = TerrainState.Running;
	}

	/// <summary>
	/// Advances one terrain: an actor that exists (committed by the job, or by a synchronous acquisition that got there first) is
	/// held; a job that ended without one marks the terrain failed; a held actor that was removed while its collider lives is captured again.
	/// </summary>
	private void ReconcileTerrain(TerrainEntry entry)
	{
		if (entry.State == TerrainState.Held)
		{
			if (!entry.Actor.Removed) return;
			entry.Actor = null;
			entry.State = TerrainState.Pending;
			return;
		}
		// A terrain that failed here can still have been captured by a corridor acquisition later; its actor is held like any other.
		if (_environmentByCollider.TryGetValue(entry.ColliderId, out EnvironmentActor actor))
		{
			HoldTerrain(entry, actor);
			return;
		}
		if (entry.State == TerrainState.Failed) return;
		if (entry.State != TerrainState.Running || entry.Job.Phase < CapturePhase.Done) return;
		entry.Job = null;
		entry.Attempts++;
		entry.State = entry.Attempts < MaximumTerrainAttempts ? TerrainState.Pending : TerrainState.Failed;
		entry.RetryFrame = Time.frameCount + TerrainRetryFrames;
		if (InfoLoggingActive) Log(ExternalLogLevel.Info, $"External PhysX terrain capture of {entry.Collider.name} ended without an environment actor (attempt {entry.Attempts}/{MaximumTerrainAttempts}); a corridor that needs it captures it on demand.");
	}

	private void HoldTerrain(TerrainEntry entry, EnvironmentActor actor)
	{
		_desiredActorsScratch.Clear();
		_desiredActorsScratch.Add(actor);
		AddProximityReferences(TerrainConsumerId, _desiredActorsScratch);
		_desiredActorsScratch.Clear();
		entry.Actor = actor;
		entry.Job = null;
		entry.State = TerrainState.Held;
	}

	/// <summary>Accounts one main-thread step of a terrain job (the heightfield read dominates it).</summary>
	private void RecordTerrainStep(long ticks)
	{
		_terrainStepTicks += ticks;
		if (ticks > _terrainStepMaxTicks) _terrainStepMaxTicks = ticks;
		_environmentTimings.Add(EnvironmentStep.JobsTerrain, ticks);
	}

	#endregion

	#region Reporting and reset

	/// <summary>
	/// <c>terrainCapture=captured/count/failed/maxMs/startupMs</c>: terrains holding an actor, terrains tracked, terrains that could not
	/// be captured, the longest single main-thread step of a terrain job, and the cumulative main-thread time of all terrain jobs
	/// (the raid-start cost, plus any later terrain), followed by the heightfield cache counters (<c>reads</c> equal to <c>count</c>
	/// means no terrain was read outside this capture).
	/// </summary>
	private string DescribeTerrain()
	{
		if (IsFullPrebake) return $"terrainCapture=full-prebake, {MeshGeometryRepository.Describe()}, ";
		int held = 0;
		int failed = 0;
		for (int index = 0; index < _terrains.Count; index++)
		{
			if (_terrains[index].State == TerrainState.Held) held++;
			else if (_terrains[index].State == TerrainState.Failed) failed++;
		}
		return $"terrainCapture=captured:{held}/count:{_terrains.Count}/failed:{failed}/maxMs:{_terrainStepMaxTicks * 1000d / Stopwatch.Frequency:F1}/startupMs:{_terrainStepTicks * 1000d / Stopwatch.Frequency:F1}, " +
			MeshGeometryRepository.Describe() + ", ";
	}

	private void ResetTerrainCapture()
	{
		_terrains.Clear();
		_terrainScratch.Clear();
		_terrainRescan = true;
		_terrainDeferredFrames = 0;
		_terrainStepTicks = _terrainStepMaxTicks = 0L;
	}

	#endregion
}
