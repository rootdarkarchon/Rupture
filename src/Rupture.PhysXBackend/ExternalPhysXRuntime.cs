using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

public sealed partial class ExternalPhysXRuntime : IExternalPhysicsRuntime, IExternalCorpseControlRuntime, IExternalAngularDriveRuntime, IExternalBuildDiagnosticsRuntime,
	IExternalEnvironmentPrefetchRuntime, IExternalCreationBatchRuntime
{
	private const float EnvironmentRadius = 2f;
	private const int InitialOverlapCapacity = 256;
	private const int MaximumOverlapCapacity = 4096;
	private const int StaticAuditPerFrame = 16;
	private const int EnvironmentShapeAuditPerFrame = 64;
	private const int DeferredUnityShapeAuditPerFrame = 128;
	private const int MaximumRetainedStaticEnvironment = 2048;
	// The full-raid native build legitimately runs for seconds on the owner, so it
	// is polled across frames instead of sharing the 500 ms synchronous watchdog.
	private const int InitialBakeWatchdogMilliseconds = 120000;
	private readonly object _ownerGate = new object();
	private readonly Queue<OwnerWork> _ownerQueue = new Queue<OwnerWork>();
	private readonly AutoResetEvent _ownerSignal = new AutoResetEvent(false);
	private readonly Dictionary<ulong, ExternalBody> _bodies = new Dictionary<ulong, ExternalBody>();
	private readonly Dictionary<ulong, ExternalJoint> _joints = new Dictionary<ulong, ExternalJoint>();
	private ExternalJoint[] _jointSnapshot;
	private readonly SimulationActivity _activity = new SimulationActivity();
	public bool NeedsSimulation => _activity.NeedsStep;
	private readonly Dictionary<int, ulong> _bodyShapesByCollider = new Dictionary<int, ulong>();
	private readonly HashSet<string> _reportedSkippedBodyShapes = new HashSet<string>(StringComparer.Ordinal);
	private readonly Dictionary<int, EnvironmentActor> _environmentByCollider = new Dictionary<int, EnvironmentActor>();
	private readonly List<EnvironmentActor> _environmentActors = new List<EnvironmentActor>();
	private readonly List<EnvironmentActor> _movableEnvironmentActors = new List<EnvironmentActor>();
	private EnvironmentPollCadence _movablePollCadence;
	private long _movablePolls, _movableRootChecks;
	private bool _movablePolledThisFrame, _worstMovablePolled;
	private int _worstJobs, _worstActors, _worstMovable, _worstPromotions, _worstLateScenes;
	private readonly Dictionary<uint, HashSet<EnvironmentActor>> _proximityActorsByConsumer = new Dictionary<uint, HashSet<EnvironmentActor>>();
	private readonly LinkedList<EnvironmentActor> _retainedStaticEnvironment = new LinkedList<EnvironmentActor>();
	private readonly Queue<Scene> _lateScenes = new Queue<Scene>();
	private readonly HashSet<int> _lateSceneHandles = new HashSet<int>();
	private readonly List<Bounds> _recentlyMovedBounds = new List<Bounds>();
	private readonly List<DeferredUnityShape> _deferredUnityShapes = new List<DeferredUnityShape>();
	private readonly HashSet<int> _deferredUnityShapeIds = new HashSet<int>();
	private readonly List<Collider> _proximityUnknownScratch = new List<Collider>(64);
	private readonly HashSet<int> _proximityColliderIdsScratch = new HashSet<int>();
	private readonly HashSet<EnvironmentActor> _proximityActorsScratch = new HashSet<EnvironmentActor>();
	private readonly List<EnvironmentActor> _proximityRemovalsScratch = new List<EnvironmentActor>(64);
	private readonly EnvironmentHierarchyFilter _environmentHierarchy = new EnvironmentHierarchyFilter();
	private long _casingEnvironmentQueries, _casingEmptyCaptures;
	private Thread _ownerThread;
	private NativeApi _api;
	private NativeScene _scene;
	private IMeshGeometrySource _meshGeometrySource;
	private ExternalBackendOptions _options;
	private StepWork _stepWork;
	private volatile OwnerWork _executingWork;
    private ExternalCasingFrame _queuedCasingFrame, _completedCasingFrame;
    public void QueueCasingFrame(ExternalCasingFrame frame)
    {
        if (_options?.GraphicalCasings != true || _stepWork != null || _queuedCasingFrame != null)
            throw new InvalidOperationException("Casing batch requires a graphical completed-step boundary.");
        _queuedCasingFrame = frame ?? throw new ArgumentNullException(nameof(frame));
    }
    public ExternalCasingFrame TakeCasingFrame()
    {
        var frame = _completedCasingFrame;
        _completedCasingFrame = null;
        return frame;
    }
	private Collider[] _overlapBuffer = new Collider[InitialOverlapCapacity];
	private volatile bool _disposed;
	private volatile bool _ownerThreadUnresponsive;
	private bool _raidStarted;
	private bool _ready;
	private string _failure;
	private volatile string _ownerFailure;
	private string _lastBatchFailure;
	private int _raidStartedFrame;
	private string _lastSceneSignature;
	private int _stableSceneFrames;
	private int _staticAuditIndex;
	private int _shapeAuditActorIndex;
	private int _shapeAuditIndex;
	private int _deferredUnityShapeAuditIndex;
	private long _stepSequence;
	private long _completedSequence;
	private int _initialColliderCount;
	private int _initialActorCount;
	private int _lateSceneAdditions;
	private int _runtimeSpawnAdditions;
	private long _movablePoseUpdates;
	private long _scaleRebuilds;
	private int _bakeFailures;
	private double _bakeMilliseconds;
	private long _rawGeometryBytes;
	private OwnerWork _bakeWork;
	private List<ActorSnapshot> _bakeSnapshots;
	private List<NativeActor> _bakeCreated;
	private readonly Stopwatch _bakeStopwatch = new Stopwatch();
	private bool _bodyPathWarmupQueued;
	private long _noShapeOmissions;

	public bool IsInitialized => _api != null && !_disposed;
	public bool IsRaidReady => _ready && string.IsNullOrEmpty(Failure);
	public bool IsFullPrebake => _options?.EnvironmentMode == ExternalEnvironmentMode.PrebakeEntireRaid;
	public string Failure => _failure ?? _ownerFailure;
	public IReadOnlyList<Bounds> RecentlyMovedBounds => _recentlyMovedBounds;
	public string Status
	{
		get
		{
			int movable = 0;
			int disabledShapes = 0;
			int shapes = 0;
			foreach (EnvironmentActor actor in _environmentActors)
			{
				if (actor.Movable) movable++;
				shapes += actor.Shapes.Count;
				foreach (EnvironmentShape shape in actor.Shapes)
					if (!shape.LastEnabled) disabledShapes++;
			}
			NativeScene scene = _scene;
			string graphicalBodies = "";
			if (_options?.GraphicalCasings == true)
			{
				int enabled = 0, total = 0, kinematic = 0, empty = 0;
				foreach (ExternalBody body in _bodies.Values)
				{
					int bodyEnabled = 0;
					foreach (bool shapeEnabled in body.ShapeEnabledByHandle.Values)
					{
						total++;
						if (body.CollisionEnabled && shapeEnabled) bodyEnabled++;
					}
					enabled += bodyEnabled;
					if (bodyEnabled == 0) empty++;
					if (body.IsKinematic) kinematic++;
				}
				graphicalBodies = $"bodyShapeCommands=enabled:{enabled}/total:{total}/bodiesWithoutCollision:{empty}/kinematic:{kinematic}, ";
				graphicalBodies += $"casingCoverage=queries:{_casingEnvironmentQueries}/hierarchyVisits:{_environmentHierarchy.Visits}/hierarchyHits:{_environmentHierarchy.Hits}/emptyCaptures:{_casingEmptyCaptures}, ";
			}
			return $"backend=ExternalPhysX, ready={IsRaidReady}, meshExtraction=UnityPhysXExtraction/{_meshGeometrySource?.Status ?? "uninitialized"}, initialBake={(IsFullPrebake ? (_ready ? "complete" : (_failure != null ? "failed" : (_bakeWork != null ? "building" : "waiting"))) : "not-selected")}, " +
				$"lateBake={_lateScenes.Count}, environmentActors={_environmentActors.Count}/static:{_environmentActors.Count - movable}/movable:{movable}/retained:{_retainedStaticEnvironment.Count}, " +
				$"environmentShapes={shapes}/disabled:{disabledShapes}/deferredUnityShapes:{_deferredUnityShapeIds.Count}, bodies={_bodies.Count}, joints={_joints.Count}, materials={scene?.MaterialCount ?? 0}, movablePoseUpdates={_movablePoseUpdates}, scaleRebuilds={_scaleRebuilds}, " +
				$"movablePolling=4Hz/polls:{_movablePolls}/rootChecks:{_movableRootChecks}, " +
				graphicalBodies + DescribePipeline() + $"lateSceneAdditions={_lateSceneAdditions}, runtimeSpawnAdditions={_runtimeSpawnAdditions}, " +
				$"cache=memory:{scene?.MeshMemoryHits ?? 0}/disk:{scene?.MeshDiskHits ?? 0}/cooked:{scene?.MeshCooks ?? 0}/rawBytes:{_rawGeometryBytes}/cookedBytes:{scene?.CookedBytes ?? 0}, " +
				$"initialColliders={_initialColliderCount}, initialActors={_initialActorCount}, bakeMs={_bakeMilliseconds:F1}, bakeFailures={_bakeFailures}, failure={Failure ?? "none"}";
		}
	}

	/// <summary>
	/// Cumulative per-raid counters of the geometry work a corpse build can trigger. Source counters are main-thread
	/// state; the cook and cache counters are written by the owner thread and read here as aligned 64-bit values.
	/// </summary>
	public ExternalBuildCounters ReadBuildCounters()
	{
		IMeshGeometrySource source = _meshGeometrySource;
		NativeScene scene = _scene;
		return new ExternalBuildCounters(source?.ShapeMapRefreshes ?? 0L, source?.TargetedShapeMappings ?? 0L, source?.MeshExtractions ?? 0L,
			scene?.MeshCooks ?? 0L, scene?.MeshDiskHits ?? 0L, scene?.MeshMemoryHits ?? 0L, _noShapeOmissions);
	}

	public void Initialize(ExternalBackendOptions options)
	{
		if (options == null) throw new ArgumentNullException(nameof(options));
		if (options.DispatcherThreads < 0 || options.DispatcherThreads > 4) throw new ArgumentOutOfRangeException(nameof(options.DispatcherThreads));
		if (string.IsNullOrWhiteSpace(options.BackendRoot) || string.IsNullOrWhiteSpace(options.CacheRoot)) throw new ArgumentException("Backend and cache roots are required.");
		_options = options;
		Directory.CreateDirectory(options.CacheRoot);
		_api = new NativeApi(options.BackendRoot, options.GraphicalCasings);
		_meshGeometrySource = new UnityPhysXMeshGeometrySource(_api, options.EnvironmentMask, options.Log, options.ScheduleBootstrapProbe);
		_ownerThread = new Thread(OwnerLoop)
		{
			IsBackground = true,
			Name = "RPT External PhysX Scene Owner",
			// The main thread blocks on owner work (body/joint creation, step completion); BelowNormal let the game's own threads preempt it under load and stretch those waits.
			Priority = System.Threading.ThreadPriority.AboveNormal
		};
		_ownerThread.Start();
		Log(ExternalLogLevel.Info, $"External PhysX backend loaded from {options.BackendRoot}; PhysX=4.1.2, dispatcherThreads={options.DispatcherThreads}, environment={options.EnvironmentMode}, meshExtraction=UnityPhysXExtraction.");
	}

	public void BeginRaid()
	{
		ResetRaid();
		if (_ownerThreadUnresponsive)
			throw new InvalidOperationException("The external PhysX scene-owner thread did not stop after the previous watchdog timeout.");
		_raidStarted = true;
		_raidStartedFrame = Time.frameCount;
		_lastSceneSignature = LoadedSceneSignature();
		_stableSceneFrames = 0;
		_meshGeometrySource.BeginRaid();
		MeshGeometryRepository.Configure(_meshGeometrySource);
		if (!IsFullPrebake)
		{
			Log(ExternalLogLevel.Info, $"External PhysX ProximityCache raid scene is waiting for source-scene settings from {_meshGeometrySource.Name}.");
		}
		else
		{
			Log(ExternalLogLevel.Info, "External PhysX full-environment prebake is waiting for the loaded raid scene set to remain stable for two frames.");
		}
	}

	public void NotifySceneLoaded(Scene scene)
	{
		if (!_raidStarted || !scene.IsValid() || !scene.isLoaded) return;
		int handle = scene.handle;
		if (_lateSceneHandles.Add(handle))
		{
			_lateScenes.Enqueue(scene);
			_terrainRescan = true;
		}
	}

	public void CaptureEnvironmentChanges()
	{
		if (!_raidStarted || _disposed) return;
		long started = Stopwatch.GetTimestamp();
		long syncStarted = _meshGeometrySource.SyncTransformsTotalTicks;
		_environmentTimings.BeginFrame();
		_snapshotRebuildTicks = 0L;
		_snapshotRebuilt = false;
		_movablePolledThisFrame = false;
		try { CaptureEnvironmentChangesCore(); }
		finally
		{
			_environmentTimings.Add(EnvironmentStep.JobsSync, _meshGeometrySource.SyncTransformsTotalTicks - syncStarted);
			if (_environmentTimings.EndFrame(Stopwatch.GetTimestamp() - started)) RememberWorstFrameContext();
		}
	}

	/// <summary>
	/// One environment update, step by step; every step is timed into <see cref="_environmentTimings"/> so a long update can be
	/// attributed to one of them. Terrain capture starts before late scenes (their jobs must not reach an unclaimed terrain first)
	/// and takes the hold of committed terrains again right after the jobs ran, before the shape audit can see them unreferenced.
	/// </summary>
	private void CaptureEnvironmentChangesCore()
	{
		long mark = Stopwatch.GetTimestamp();
		_meshGeometrySource.Update();
		mark = _environmentTimings.Lap(EnvironmentStep.Source, mark);
		if (!string.IsNullOrEmpty(_meshGeometrySource.Failure))
		{
			_failure = $"{_meshGeometrySource.Name} failed: {_meshGeometrySource.Failure}";
			_ready = false;
			return;
		}
		if (!_meshGeometrySource.IsReady) return;
		if (!_ready && _failure == null && !IsFullPrebake)
		{
			NativeApi.UnitySceneSettings sourceSceneSettings = _meshGeometrySource.SourceSceneSettings;
			InvokeOwner(() => _scene = new NativeScene(_api, _options, sourceSceneSettings));
			_ready = true;
			QueueBodyPathWarmup();
			Log(ExternalLogLevel.Lifecycle, $"External PhysX ProximityCache raid scene is ready with source parity ({sourceSceneSettings}) after {_meshGeometrySource.Name} initialization ({_meshGeometrySource.Status}).");
			mark = _environmentTimings.Lap(EnvironmentStep.Startup, mark);
		}
		if (!_ready && _failure == null && IsFullPrebake)
		{
			if (_bakeWork != null)
			{
				PollInitialEnvironment();
				_environmentTimings.Lap(EnvironmentStep.Startup, mark);
				return;
			}
			string signature = LoadedSceneSignature();
			if (string.Equals(signature, _lastSceneSignature, StringComparison.Ordinal)) _stableSceneFrames++;
			else
			{
				_lastSceneSignature = signature;
				_stableSceneFrames = 0;
			}
			if (Time.frameCount > _raidStartedFrame && _stableSceneFrames >= 2) BuildInitialEnvironment();
			_environmentTimings.Lap(EnvironmentStep.Startup, mark);
			return;
		}
		if (!_ready) return;

		UpdateTerrainCapture();
		mark = _environmentTimings.Lap(EnvironmentStep.Terrain, mark);
		while (_lateScenes.Count != 0 && !TerrainCaptureBlocksScenes())
		{
			Scene scene = _lateScenes.Dequeue();
			_lateSceneHandles.Remove(scene.handle);
			if (scene.IsValid() && scene.isLoaded) StartSceneCapture(scene);
		}
		mark = _environmentTimings.Lap(EnvironmentStep.Scenes, mark);

		AuditDeferredUnityShapes();
		_environmentTimings.Lap(EnvironmentStep.Deferred, mark);
		RunMaintenanceCapture();
		if (!_ready) return;
		mark = Stopwatch.GetTimestamp();
		HoldCapturedTerrains();
		mark = _environmentTimings.Lap(EnvironmentStep.Terrain, mark);
		AuditStaticActors();
		mark = _environmentTimings.Lap(EnvironmentStep.StaticAudit, mark);
		CaptureMovableChanges();
		mark = _environmentTimings.Lap(EnvironmentStep.Movable, mark);
		AuditEnvironmentShapes();
		_environmentTimings.Lap(EnvironmentStep.ShapeAudit, mark);
	}

	/// <summary>What the longest environment update so far was holding, to read next to its step breakdown.</summary>
	private void RememberWorstFrameContext()
	{
		_worstJobs = _jobs.Count;
		_worstActors = _environmentActors.Count;
		_worstMovable = _movableEnvironmentActors.Count;
		_worstPromotions = _promotions.Count;
		_worstLateScenes = _lateScenes.Count;
		_worstMovablePolled = _movablePolledThisFrame;
	}

	private string DescribeWorstFrameContext() =>
		$"envWorstFrameContext=jobs:{_worstJobs}/actors:{_worstActors}/movable:{_worstMovable}/promotions:{_worstPromotions}/lateScenes:{_worstLateScenes}/movablePoll:{_worstMovablePolled}, ";

	private bool _stageCasingEnvironment;
	public bool EnsureCasingEnvironment(uint consumerId, Vector3 center, Vector3 sweepEnd, out string failure)
	{
		if (_options?.GraphicalCasings != true) throw new InvalidOperationException("Casing environment requires a graphical consumer.");
		_stageCasingEnvironment = true;
		_casingEnvironmentQueries++;
		// These caches last only for one synchronous acquisition, so reparenting
		// and added components are observed on the very next query.
		_environmentHierarchy.BeginQuery();
		try { return EnsureEnvironment(consumerId, center, sweepEnd, out _, out failure); }
		finally { _stageCasingEnvironment = false; _environmentHierarchy.EndQuery(); }
	}

	public bool EnsureEnvironment(uint consumerId, Vector3 center, Vector3 sweepEnd, out ExternalEnvironmentAcquireResult result, out string failure)
	{
		result = default;
		failure = null;
		if (!IsRaidReady)
		{
			failure = Failure ?? "the external environment is not ready";
			return false;
		}
		if (IsFullPrebake) return EnsureFullPrebakeEnvironment(center, sweepEnd, out result, out failure);
		if (consumerId == 0)
		{
			failure = "the proximity-cache consumer id is invalid";
			return false;
		}
		if (TryReuseLastAcquisition(consumerId, center, sweepEnd, out result)) return true;

		// Shared ancestors of the corridor's colliders are classified once for this acquisition.
		_environmentHierarchy.BeginQuery();
		try
		{
			// A prefetched region is all reuse: no unknown collider, so no mapping, extraction or owner wait below.
			int reused = ClassifyRegion(center, sweepEnd, AcquisitionClaimResolution, skipBodyShapes: false);
			if (_proximityUnknownScratch.Count != 0) _meshGeometrySource.PrepareProximity(center, sweepEnd, EnvironmentRadius, _proximityUnknownScratch);
			int created = CaptureProximityUnknown("proximity", false);
			if (!string.IsNullOrEmpty(_lastBatchFailure))
			{
				failure = _lastBatchFailure;
				return false;
			}
			if (created != 0) _syncEnvironmentCaptures++;
			else _environmentFastPaths++;
			BindClassifiedRegion(consumerId);
			result = new ExternalEnvironmentAcquireResult(created, reused, _environmentByCollider.Count);
			RememberAcquisition(consumerId, center, sweepEnd, reused + created);
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
		finally { _environmentHierarchy.EndQuery(); }
	}

	/// <summary>
	/// The same consumer asking for the same corridor again in the frame it was acquired in (a refresh that coincides with a
	/// wake or a fallback) is already bound. Anything that could invalidate that, a released consumer or a removed actor,
	/// clears the memo; a collider that appeared between the two calls of one frame is picked up by the next refresh.
	/// </summary>
	private bool TryReuseLastAcquisition(uint consumerId, Vector3 center, Vector3 sweepEnd, out ExternalEnvironmentAcquireResult result)
	{
		result = default;
		if (_lastAcquireFrame != Time.frameCount || _lastAcquireConsumer != consumerId || _lastAcquireCasing != _stageCasingEnvironment ||
			_lastAcquireCenter != center || _lastAcquireSweepEnd != sweepEnd) return false;
		_environmentFastPaths++;
		result = new ExternalEnvironmentAcquireResult(0, _lastAcquireKnown, _environmentByCollider.Count);
		return true;
	}

	private void RememberAcquisition(uint consumerId, Vector3 center, Vector3 sweepEnd, int known)
	{
		_lastAcquireFrame = Time.frameCount;
		_lastAcquireConsumer = consumerId;
		_lastAcquireCasing = _stageCasingEnvironment;
		_lastAcquireCenter = center;
		_lastAcquireSweepEnd = sweepEnd;
		_lastAcquireKnown = known;
	}

	/// <summary>
	/// Full prebake: unknown colliders are runtime-spawned objects. The acquisition has the same guarantee as the
	/// ProximityCache one: when it returns, every active eligible collider of its corridor has a native actor, whether it was
	/// unowned or claimed by a staged late-scene or deferred capture. It captures them synchronously, bounded by the corridor
	/// (<see cref="ClaimResolution.TakeOver"/>); a casing acquisition drives the claiming jobs instead
	/// (<see cref="ClaimResolution.Drive"/>) and stays synchronous as before.
	/// </summary>
	private bool EnsureFullPrebakeEnvironment(Vector3 center, Vector3 sweepEnd, out ExternalEnvironmentAcquireResult result, out string failure)
	{
		result = default;
		failure = null;
		_environmentHierarchy.BeginQuery();
		try
		{
			int reused = ClassifyRegion(center, sweepEnd, AcquisitionClaimResolution, skipBodyShapes: true);
			if (_proximityUnknownScratch.Count != 0) _meshGeometrySource.PrepareProximity(center, sweepEnd, EnvironmentRadius, _proximityUnknownScratch);
			int created = CaptureProximityUnknown("runtime-proximity", true);
			if (!string.IsNullOrEmpty(_lastBatchFailure))
			{
				failure = _lastBatchFailure;
				return false;
			}
			result = new ExternalEnvironmentAcquireResult(created, reused, _environmentByCollider.Count);
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
		finally { _environmentHierarchy.EndQuery(); }
	}

	/// <summary>A casing acquisition needs everything in its region created before its step; every other acquisition takes over just its corridor's claims.</summary>
	private ClaimResolution AcquisitionClaimResolution => _stageCasingEnvironment ? ClaimResolution.Drive : ClaimResolution.TakeOver;

	/// <summary>
	/// Queries the corridor and sorts its eligible colliders into <c>_proximityColliderIdsScratch</c> (all of them),
	/// <c>_proximityUnknownScratch</c> (nobody owns them) and the reused count. A collider an asynchronous job has already
	/// claimed is never captured twice: <paramref name="resolution"/> decides whether the claiming job is driven to
	/// completion or only the corridor's claimed colliders are taken over (the corridor is then classified again, and the
	/// colliders taken from a job that had not built their snapshot yet come back as unknown), or whether the collider is left to its job.
	/// </summary>
	private int ClassifyRegion(Vector3 center, Vector3 sweepEnd, ClaimResolution resolution, bool skipBodyShapes)
	{
		for (int pass = 0; ; pass++)
		{
			int count = QueryEnvironment(center, sweepEnd);
			_proximityUnknownScratch.Clear();
			_proximityColliderIdsScratch.Clear();
			_pendingJobsScratch.Clear();
			_takeOverIdsScratch.Clear();
			_regionClaimedCount = 0;
			int reused = 0;
			for (int index = 0; index < count; index++)
			{
				Collider collider = _overlapBuffer[index];
				if (!Eligible(collider, includeDisabled: false)) continue;
				int colliderId = collider.GetInstanceID();
				_proximityColliderIdsScratch.Add(colliderId);
				if (_environmentByCollider.ContainsKey(colliderId)) reused++;
				else if (skipBodyShapes && _bodyShapesByCollider.ContainsKey(colliderId)) continue;
				else if (_claims.TryGetValue(colliderId, out CaptureJob pending))
				{
					if (resolution == ClaimResolution.Leave)
					{
						_regionClaimedCount++;
						continue;
					}
					if (!_pendingJobsScratch.Contains(pending)) _pendingJobsScratch.Add(pending);
					_takeOverIdsScratch.Add(colliderId);
				}
				else _proximityUnknownScratch.Add(collider);
			}
			if (_pendingJobsScratch.Count == 0) return reused;
			if (pass != 0) throw new InvalidOperationException("A pending environment capture did not settle.");
			if (resolution == ClaimResolution.Drive)
			{
				for (int index = 0; index < _pendingJobsScratch.Count; index++) DriveJob(_pendingJobsScratch[index]);
				_takeOverIdsScratch.Clear();
			}
			else TakeOverClaims();
		}
	}

	private int CaptureProximityUnknown(string source, bool countAsRuntime)
	{
		if (_proximityUnknownScratch.Count != 0) return AddEnvironmentBatch(CaptureColliders(_proximityUnknownScratch), source, countAsRuntime);
		_lastBatchFailure = null;
		if (_stageCasingEnvironment) _casingEmptyCaptures++;
		return 0;
	}

	public void ReleaseEnvironment(uint consumerId)
	{
		if (consumerId == 0) return;
		// A capture still pending for this consumer stops owning anything for it: whatever it commits later is parked, not bound.
		if (_acquisitions.TryGetValue(consumerId, out PrefetchConsumer request)) DropAcquisition(consumerId, request);
		if (IsFullPrebake) return;
		// A promoted prefetch that has not settled must not hand references to a consumer that is gone.
		DetachJobsOf(consumerId);
		ReleaseConsumerReferences(consumerId);
	}

	public bool TryCreateBody(Rigidbody nativeBody, IReadOnlyList<Collider> colliders, uint ownerGroup, bool kinematic, out IExternalPhysicsBody body, out string failure)
	{
		return TryCreateBody(nativeBody, colliders, ownerGroup, kinematic,
			ExternalCollisionProfile.DeadbodyAndEnvironment, out body, out failure);
	}

	public bool TryCreateBody(Rigidbody nativeBody, IReadOnlyList<Collider> colliders, uint ownerGroup, bool kinematic,
		ExternalCollisionProfile collisionProfile, out IExternalPhysicsBody body, out string failure)
	{
		body = null;
		failure = null;
		if (!IsRaidReady || nativeBody == null)
		{
			failure = Failure ?? "external scene is not ready";
			return false;
		}
		try
		{
			ActorSnapshot snapshot = ActorSnapshot.CaptureBody(nativeBody, colliders, ownerGroup, kinematic,
				_options.DeadbodyLayer, _options.EnvironmentMask, collisionProfile);
			_noShapeOmissions += snapshot.SkippedUnsimulatedShapes.Count;
			if (InfoLoggingActive)
				foreach (string skipped in snapshot.SkippedUnsimulatedShapes)
					if (_reportedSkippedBodyShapes.Add(skipped))
						Log(ExternalLogLevel.Info, $"External body {nativeBody.name} omits MeshCollider {skipped}: Unity simulates no native shape for it.");
			Vector3 centerOfMass = nativeBody.centerOfMass;
			BodyCreationItem item = new BodyCreationItem(this, snapshot, BodyShapeKind.Mesh, centerOfMass, null);
			SubmitCreation(item);
			body = item.Body;
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			if (!(exception is InvalidOperationException) && !(exception is NotSupportedException))
			{
				string trace = exception.StackTrace ?? string.Empty;
				int end = trace.IndexOf('\n');
				failure += " at " + (end < 0 ? trace : trace.Substring(0, end)).Trim();
			}
			return false;
		}
	}

	public IExternalBodyCreation BeginCreateTransientConvexBody(Rigidbody nativeBody,
		IReadOnlyList<ExternalTransientConvexShape> shapes, uint ownerGroup, bool kinematic,
		ExternalCollisionProfile collisionProfile, out string failure)
	{
		failure = null;
		if (!IsRaidReady || nativeBody == null || shapes == null || shapes.Count == 0)
		{
			failure = Failure ?? "external scene is not ready or the transient convex source is empty";
			return null;
		}
		try
		{
			ActorSnapshot snapshot = ActorSnapshot.CaptureTransientConvexBody(nativeBody, shapes, ownerGroup,
				kinematic, _options.DeadbodyLayer, _options.EnvironmentMask, collisionProfile);
			BodyCreationTicket ticket = new BodyCreationTicket(this, snapshot, nativeBody.centerOfMass);
			DelegateWork work = new DelegateWork(() =>
			{
				if (ticket.CancelRequested) return;
				NativeActor created = _scene.CreateActor(snapshot);
				if (!ticket.Result.TryPublish(created)) _scene.DestroyActor(created.Handle);
			}, nameof(BeginCreateTransientConvexBody));
			ticket.Work = work;
			EnqueueFifo(work);
			return ticket;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return null;
		}
	}

	// Called on the consuming main thread after synchronous creation or a
	// successful transient-ticket take. This is registration, not publication.
	private void RegisterBody(ExternalBody created)
	{
		_bodies[created.Handle] = created;
		foreach (KeyValuePair<int, ulong> shape in created.ShapeHandlesByCollider)
			_bodyShapesByCollider[shape.Key] = shape.Value;
	}

	// Several native query colliders can represent one standalone weapon shape.
	// Read Unity identities on main, after the owner has finished native creation.
	private static void BindCompoundColliders(ExternalBody created, IReadOnlyList<Collider> sourceColliders, ulong shapeHandle)
	{
		created.ShapeEnabledByHandle[shapeHandle] = true;
		foreach (Collider collider in sourceColliders)
		{
			if (collider != null) created.ShapeHandlesByCollider[collider.GetInstanceID()] = shapeHandle;
		}
	}

	private ExternalBody MaterializeBody(BodyCreationTicket ticket, NativeActor native)
	{
		ExternalBody created = new ExternalBody(this, native.Handle, ticket.CenterOfMass, ticket.Snapshot.Body,
			native.PhysicalCenterOfMass);
		for (int index = 0; index < ticket.Snapshot.Shapes.Count && index < native.ShapeHandles.Count; index++)
		{
			int colliderId = ticket.Snapshot.Shapes[index].ColliderId;
			created.ShapeHandlesByCollider[colliderId] = native.ShapeHandles[index];
			created.ShapeEnabledByHandle[native.ShapeHandles[index]] = ticket.Snapshot.Shapes[index].Enabled;
		}
		RegisterBody(created);
		return created;
	}

	public bool TryCreateBoxBody(Rigidbody nativeBody, IReadOnlyList<Collider> sourceColliders, ExternalBoxShape box, uint ownerGroup, bool kinematic, out IExternalPhysicsBody body, out string failure)
	{
		body = null;
		failure = null;
		if (!IsRaidReady || nativeBody == null || sourceColliders == null || sourceColliders.Count == 0)
		{
			failure = Failure ?? "external scene is not ready or the box source is empty";
			return false;
		}
		try
		{
			ActorSnapshot snapshot = ActorSnapshot.CaptureBoxBody(nativeBody, box, ownerGroup, kinematic, _options.DeadbodyLayer, _options.EnvironmentMask);
			BodyCreationItem item = new BodyCreationItem(this, snapshot, BodyShapeKind.Box, box.Center, sourceColliders);
			SubmitCreation(item);
			body = item.Body;
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
	}

	public bool TryCreateRoundedBoxBody(Rigidbody nativeBody, IReadOnlyList<Collider> sourceColliders, ExternalRoundedBoxShape roundedBox, uint ownerGroup, bool kinematic, out IExternalPhysicsBody body, out string failure)
	{
		body = null;
		failure = null;
		if (!IsRaidReady || nativeBody == null || sourceColliders == null || sourceColliders.Count == 0 || roundedBox == null)
		{
			failure = Failure ?? "external scene is not ready or the rounded-box weapon source is empty";
			return false;
		}
		try
		{
			ActorSnapshot snapshot = ActorSnapshot.CaptureRoundedBoxBody(nativeBody, roundedBox, ownerGroup, kinematic, _options.DeadbodyLayer, _options.EnvironmentMask);
			BodyCreationItem item = new BodyCreationItem(this, snapshot, BodyShapeKind.RoundedBox, snapshot.Body.CenterOfMass.ToUnity(), sourceColliders);
			SubmitCreation(item);
			body = item.Body;
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
	}

	public bool TryCreateJoint(Joint nativeJoint, IExternalPhysicsBody body, IExternalPhysicsBody connectedBody, out IExternalPhysicsJoint joint, out string failure)
	{
		joint = null;
		failure = null;
		if (nativeJoint == null || body is not ExternalBody child || (connectedBody != null && connectedBody is not ExternalBody))
		{
			failure = "joint body mapping is invalid";
			return false;
		}
		try
		{
			ExternalBody connected = connectedBody as ExternalBody;
			// The body handles are filled in on the owner, so a joint between bodies of an open batch can be queued.
			JointSnapshot snapshot = JointSnapshot.Capture(nativeJoint, 0, 0);
			JointCreationItem item = new JointCreationItem(snapshot, child, connected, !nativeJoint.enableCollision && connected != null);
			SubmitCreation(item);
			joint = item.Joint;
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
	}

	public void IgnoreCollision(Collider left, Collider right)
	{
		if (left == null || right == null || _scene == null) return;
		int leftId = left.GetInstanceID();
		int rightId = right.GetInstanceID();
		CreationBatchScope batch = _creationBatch;
		if (batch != null && (batch.ContainsCollider(leftId) || batch.ContainsCollider(rightId)))
			throw new InvalidOperationException("A collider belongs to a body of the open creation batch, which has no native shapes until the batch is disposed.");
		if (!_bodyShapesByCollider.TryGetValue(leftId, out ulong leftHandle) ||
			!_bodyShapesByCollider.TryGetValue(rightId, out ulong rightHandle)) return;
		QueueIgnoreShapePair(leftHandle, rightHandle);
	}

	public void DestroyJoint(IExternalPhysicsJoint joint)
	{
		if (joint is not ExternalJoint external || !external.IsValid || _scene == null) return;
		external.Invalidate();
		_joints.Remove(external.Handle);
		_jointSnapshot = null;
		QueueDestroyJoint(external.Handle);
	}

	public void DestroyBody(IExternalPhysicsBody body)
	{
		if (body is not ExternalBody external || !external.IsValid || _scene == null) return;
		_bodies.Remove(external.Handle);
		foreach (KeyValuePair<int, ulong> shape in external.ShapeHandlesByCollider)
			if (_bodyShapesByCollider.TryGetValue(shape.Key, out ulong mapped) && mapped == shape.Value) _bodyShapesByCollider.Remove(shape.Key);
		external.Invalidate();
		QueueDestroyActor(external.Handle);
	}

	public bool TryApplyBodyStates(IReadOnlyList<ExternalBodyState> states, out string failure)
	{
		failure = null;
		if (!IsRaidReady || states == null || states.Count == 0)
		{
			failure = Failure ?? "external scene is not ready or the state batch is empty";
			return false;
		}
		lock (_ownerGate)
		{
			if (_stepWork != null)
			{
				failure = "cannot replace body states while an external simulation step is in flight";
				return false;
			}
		}

		ExternalBody[] bodies = new ExternalBody[states.Count];
		NativeApi.BodyState[] nativeStates = new NativeApi.BodyState[states.Count];
		bool[] kinematic = new bool[states.Count];
		bool[] sleeping = new bool[states.Count];
		for (int index = 0; index < states.Count; index++)
		{
			ExternalBodyState state = states[index];
			if (state.Body is not ExternalBody body || !body.IsValid ||
				!_bodies.TryGetValue(body.Handle, out ExternalBody current) || !ReferenceEquals(body, current))
			{
				failure = $"body state {index} has a stale or foreign actor handle";
				return false;
			}
			bodies[index] = body;
			kinematic[index] = state.IsKinematic;
			sleeping[index] = state.IsSleeping;
		}

		for (int index = 0; index < states.Count; index++)
		{
			bodies[index].PublishAuthoritative(states[index]);
			nativeStates[index] = bodies[index].SnapshotNativeState();
		}
		// One owner operation replaces the complete transmitted body set. The next
		// step is enqueued only after this operation, so PhysX cannot simulate a
		// mixture of old and authoritative limb states.
		EnqueueOwner(() =>
		{
			for (int index = 0; index < bodies.Length; index++)
				_scene?.SetBodyState(bodies[index].Handle, nativeStates[index], kinematic[index], wake: !sleeping[index]);
		});
		return true;
	}

	public bool SubmitStep(float deltaTime, out string failure)
	{
		failure = null;
		if (!IsRaidReady || deltaTime <= 0f)
		{
			failure = Failure ?? "external scene is not ready";
			return false;
		}
		lock (_ownerGate)
		{
			if (_stepWork != null)
			{
				failure = "an external simulation step is already in flight";
				return false;
			}
			ExternalJoint[] joints = _jointSnapshot ?? (_jointSnapshot = _joints.Values.ToArray());
			ExternalCasingFrame casingFrame = _queuedCasingFrame;
			_queuedCasingFrame = null;
			// The single step item is reused: the previous step was consumed by CompleteStep before _stepWork cleared.
			StepWork work = _stepWorkItem ?? (_stepWorkItem = new StepWork { Name = nameof(SubmitStep) });
			work.Reset();
			work.Name = nameof(SubmitStep);
			work.Sequence = ++_stepSequence;
			work.DeltaTime = deltaTime;
			work.CommandVersion = _activity.Version;
			work.QueuedBehindAsync = _asyncSliceRunning;
			work.QueuedTimestamp = Stopwatch.GetTimestamp();
			work.BodyCount = _bodies.Count;
			work.JointCount = joints.Length;
			work.Joints = joints;
			work.CasingFrame = casingFrame;
			_stepWork = work;
			_ownerQueue.Enqueue(work);
			_ownerSignal.Set();
		}
		return true;
	}

	public bool CompleteStep(int timeoutMilliseconds, out ExternalStepResult result, out string failure)
	{
		result = default;
		failure = null;
		StepWork work;
		lock (_ownerGate) work = _stepWork;
		if (work == null)
		{
			result = new ExternalStepResult(false, false, 0d, 0d, true, _completedSequence);
			return true;
		}
		bool wasReady = work.IsCompleted;
		long waitStarted = Stopwatch.GetTimestamp();
		bool completed = wasReady || work.Wait(Math.Max(0, timeoutMilliseconds));
		double mainWait = (Stopwatch.GetTimestamp() - waitStarted) * 1000d / Stopwatch.Frequency;
		if (!completed)
		{
			result = new ExternalStepResult(false, true, work.Stopwatch.Elapsed.TotalMilliseconds, mainWait, wasReady, work.Sequence);
			failure = $"external PhysX scene owner did not complete step {work.Sequence} within {timeoutMilliseconds} ms; {DescribeOwner(work)}";
			return false;
		}
		lock (_ownerGate)
		{
			if (ReferenceEquals(_stepWork, work)) _stepWork = null;
		}
		if (work.Exception != null)
		{
			failure = work.Exception.GetType().Name + ": " + work.Exception.Message;
			result = new ExternalStepResult(false, false, work.Stopwatch.Elapsed.TotalMilliseconds, mainWait, wasReady, work.Sequence);
			return false;
		}
		for (int i = 0; i < work.PublishedBodies; i++)
		{
			NativeApi.BodyState state = work.StepStates[i];
			if (_bodies.TryGetValue(state.Actor, out ExternalBody body)) body.Publish(state);
		}
		_activity.Complete(work.CommandVersion, work.AwakeBodies);
		_completedCasingFrame = work.CasingFrame;
		_completedSequence = work.Sequence;
		result = new ExternalStepResult(true, false, work.Stopwatch.Elapsed.TotalMilliseconds, mainWait, wasReady, work.Sequence,
			work.PublishedBodies, (work.StepStates?.Length ?? 0) - work.PublishedBodies);
		return true;
	}

	public void ResetRaid()
	{
		_queuedCasingFrame = _completedCasingFrame = null;
		// Static cache with a TerrainCallbacks subscription: it must not outlive the raid on the quarantine early returns below.
		MeshGeometryRepository.Clear();
		if (_api == null || _disposed) return;
		if (_ownerThreadUnresponsive) return;
		_raidGeneration++;
		_creationBatch = null;
		OwnerWork bake = _bakeWork;
		if (bake != null && !bake.Wait(Math.Max(0, InitialBakeWatchdogMilliseconds - (int)_bakeStopwatch.ElapsedMilliseconds)))
		{
			QuarantineInitialBake(bake);
			return;
		}
		StepWork step;
		lock (_ownerGate) step = _stepWork;
		if (step != null && !step.Wait(500))
		{
			_ownerThreadUnresponsive = true;
			_failure = "External PhysX scene-owner thread did not complete the in-flight step within the 500 ms watchdog.";
			_ready = false;
			_raidStarted = false;
			Log(ExternalLogLevel.Error, _failure + " Native resources will remain loaded to avoid unloading code beneath the worker thread.");
			return;
		}
		try
		{
			InvokeOwner(() =>
			{
				_scene?.Dispose();
				_scene = null;
			});
		}
		catch (TimeoutException) { return; }
		_bodies.Clear();
		foreach (ExternalJoint joint in _joints.Values) joint.Invalidate();
		_joints.Clear();
		_jointSnapshot = null; _activity.Reset();
		_bodyShapesByCollider.Clear();
		_environmentByCollider.Clear();
		_environmentHierarchy.Reset(); _casingEnvironmentQueries = _casingEmptyCaptures = 0;
		_environmentActors.Clear();
		_movableEnvironmentActors.Clear();
		_movablePollCadence = default; _movablePolls = _movableRootChecks = 0;
		_proximityActorsByConsumer.Clear();
		_retainedStaticEnvironment.Clear();
		_deferredUnityShapes.Clear();
		_deferredUnityShapeIds.Clear();
		_meshGeometrySource?.ResetRaid();
		_lateScenes.Clear();
		_lateSceneHandles.Clear();
		_stepWork = null;
		_raidStarted = false;
		_ready = false;
		_failure = null;
		_ownerFailure = null;
		_lastBatchFailure = null;
		_stableSceneFrames = 0;
		_staticAuditIndex = 0;
		_shapeAuditActorIndex = 0;
		_shapeAuditIndex = 0;
		_deferredUnityShapeAuditIndex = 0;
		_initialColliderCount = 0;
		_initialActorCount = 0;
		_lateSceneAdditions = 0;
		_runtimeSpawnAdditions = 0;
		_movablePoseUpdates = 0;
		_scaleRebuilds = 0;
		_bakeFailures = 0;
		_bakeMilliseconds = 0d;
		_rawGeometryBytes = 0L;
		_bakeWork = null;
		_bakeSnapshots = null;
		_bakeCreated = null;
		_bakeStopwatch.Reset();
		_bodyPathWarmupQueued = false;
		_noShapeOmissions = 0L;
		ResetEnvironmentPipeline();
	}

	public void Dispose()
	{
		if (_disposed) return;
		ResetRaid();
		_disposed = true;
		_ownerSignal.Set();
		bool ownerStopped = _ownerThread == null || _ownerThread.Join(1000);
		_meshGeometrySource?.Dispose();
		_meshGeometrySource = null;
		if (ownerStopped)
		{
			_ownerSignal.Dispose();
			_api?.Dispose();
			_api = null;
		}
		else
		{
			_ownerThreadUnresponsive = true;
			Log(ExternalLogLevel.Error, "External PhysX scene-owner thread remained unresponsive during disposal; the native module is intentionally left loaded until process exit.");
		}
	}

	internal void QueueBodyState(ExternalBody body, bool wake)
	{
		if (body == null || !body.IsValid) return;
		NativeApi.BodyState state = body.SnapshotNativeState();
		QueueSetBodyState(body.Handle, state, body.IsKinematic, wake);
	}

	internal void QueueForce(ExternalBody body, uint operation, Vector3 force, Vector3 point, ForceMode mode)
	{
		if (body == null || !body.IsValid) return;
		NativeApi.ForceCommand command = new NativeApi.ForceCommand
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.ForceCommand>(), Operation = operation, Actor = body.Handle,
			Force = new NativeApi.Vec3(force), Position = new NativeApi.Vec3(point), Mode = MapForceMode(mode), Wake = 1
		};
		QueueApplyForce(command);
	}

	internal void QueueBodyCollision(ExternalBody body, bool enabled)
	{
		if (body == null || !body.IsValid) return;
		body.RequireNativeHandle();
		foreach (KeyValuePair<ulong, bool> shape in body.ShapeEnabledByHandle)
			QueueShapeEnabled(shape.Key, enabled && shape.Value);
	}

	private void BuildInitialEnvironment()
	{
		_bakeStopwatch.Restart();
		try
		{
			List<Collider> colliders = EnumerateLoadedColliders().ToList();
			Log(ExternalLogLevel.Info, $"External PhysX full bake started: eligibleColliders={colliders.Count}, loadedScenes={SceneManager.sceneCount}.");
			_meshGeometrySource.PrepareAll();
			List<ActorSnapshot> snapshots = CaptureColliders(colliders, (completed, total) =>
				Log(ExternalLogLevel.Info, $"External PhysX full bake capture progress: actors={completed}/{total}, elapsedMs={_bakeStopwatch.Elapsed.TotalMilliseconds:F1}."));
			List<MeshGeometry> uniqueMeshes = snapshots.SelectMany(actor => actor.Shapes).Select(shape => shape.Mesh).Where(mesh => mesh != null)
				.GroupBy(mesh => mesh.CacheKey(_options.EftBuild ?? "unknown"), StringComparer.Ordinal).Select(group => group.First()).ToList();
			List<HeightFieldGeometry> uniqueHeightFields = snapshots.SelectMany(actor => actor.Shapes).Select(shape => shape.HeightField).Where(heightField => heightField != null)
				.GroupBy(heightField => heightField.CacheKey(_options.EftBuild ?? "unknown"), StringComparer.Ordinal).Select(group => group.First()).ToList();
			_rawGeometryBytes = uniqueMeshes.Sum(mesh => (long)mesh.Vertices.Length * 12L + (long)mesh.Indices.Length * 4L) + uniqueHeightFields.Sum(heightField => (long)heightField.Samples.Length);
			Log(ExternalLogLevel.Info, $"External PhysX full bake capture complete: colliders={snapshots.Sum(actor => actor.Shapes.Count)}, actors={snapshots.Count}, uniqueMeshes={uniqueMeshes.Count}, uniqueHeightFields={uniqueHeightFields.Count}, estimatedRawGeometryBytes={_rawGeometryBytes}, elapsedMs={_bakeStopwatch.Elapsed.TotalMilliseconds:F1}.");
			if (_disposed || _ownerThreadUnresponsive) throw new InvalidOperationException("External PhysX owner is unavailable.");
			NativeApi.UnitySceneSettings sourceSceneSettings = _meshGeometrySource.SourceSceneSettings;
			DelegateWork work = new DelegateWork(() =>
			{
				// Owner-only: the staged scene replaces _scene only after every actor exists.
				NativeScene staging = new NativeScene(_api, _options, sourceSceneSettings);
				try { _bakeCreated = staging.CreateBatch(snapshots); }
				catch
				{
					staging.Dispose();
					throw;
				}
				NativeScene previous = _scene;
				_scene = staging;
				previous?.Dispose();
			}, nameof(BuildInitialEnvironment));
			_bakeSnapshots = snapshots;
			_bakeWork = work;
			lock (_ownerGate) { _activity.Changed(); _ownerQueue.Enqueue(work); }
			_ownerSignal.Set();
			Log(ExternalLogLevel.Info, $"External PhysX full bake native build queued; EFT native corpse physics remains active until it commits. elapsedMs={_bakeStopwatch.Elapsed.TotalMilliseconds:F1}.");
		}
		catch (Exception exception)
		{
			FailInitialEnvironment(exception);
		}
	}

	private void PollInitialEnvironment()
	{
		OwnerWork work = _bakeWork;
		if (!work.IsCompleted)
		{
			if (_bakeStopwatch.ElapsedMilliseconds >= InitialBakeWatchdogMilliseconds) QuarantineInitialBake(work);
			return;
		}
		List<ActorSnapshot> snapshots = _bakeSnapshots;
		List<NativeActor> created = _bakeCreated;
		_bakeWork = null;
		_bakeSnapshots = null;
		_bakeCreated = null;
		try
		{
			if (work.Exception != null)
				throw new InvalidOperationException($"External PhysX owner operation failed: {DescribeException(work.Exception)}", work.Exception);
			CommitEnvironment(snapshots, created);
			_ready = true;
			QueueBodyPathWarmup();
			_initialColliderCount = snapshots.Sum(actor => actor.Shapes.Count);
			_initialActorCount = snapshots.Count;
			_bakeMilliseconds = _bakeStopwatch.Elapsed.TotalMilliseconds;
			_bakeStopwatch.Stop();
			Log(ExternalLogLevel.Lifecycle, $"External PhysX full bake committed atomically: colliders={_initialColliderCount}, actors={_initialActorCount}, deferredInactiveUnityShapes={_deferredUnityShapeIds.Count}, nativeBuildMs={work.Stopwatch.Elapsed.TotalMilliseconds:F1}, elapsedMs={_bakeMilliseconds:F1}, {Status}.");
		}
		catch (Exception exception)
		{
			FailInitialEnvironment(exception);
		}
	}

	private void FailInitialEnvironment(Exception exception)
	{
		_bakeStopwatch.Stop();
		_bakeFailures++;
		_failure = exception.GetType().Name + ": " + exception.Message;
		_ready = false;
		_environmentActors.Clear();
		_movableEnvironmentActors.Clear();
		_environmentByCollider.Clear();
		EnqueueOwner(() =>
		{
			_scene?.Dispose();
			_scene = null;
		});
		Log(ExternalLogLevel.Error, $"External PhysX full bake failed; the incomplete scene was discarded and Unity fallback is required for this raid. {_failure}");
	}

	private void QuarantineInitialBake(OwnerWork work)
	{
		_ownerThreadUnresponsive = true;
		_ready = false;
		_raidStarted = false;
		_bakeWork = null;
		_bakeSnapshots = null;
		_bakeCreated = null;
		_failure = $"External PhysX full bake native build exceeded the {InitialBakeWatchdogMilliseconds} ms watchdog. The backend is quarantined until process exit. " + DescribeOwner(work);
		Log(ExternalLogLevel.Error, _failure);
	}

	private int AddEnvironmentBatch(List<ActorSnapshot> snapshots, string source, bool countAsRuntime)
	{
		_lastBatchFailure = null;
		if (snapshots.Count == 0) return 0;
		List<NativeActor> created = null;
		try
		{
			if (_stageCasingEnvironment)
			{
				// Register shared identities now, materialize on the owner before the
				// casing command/step queued later at this completed-step boundary.
				// Handles are read only on the owner until that step completes.
				created = snapshots.Select(_ => new NativeActor(0)).ToList();
				List<NativeActor> staged = created;
				EnqueueOwner(() =>
				{
					List<NativeActor> actual = _scene.CreateBatch(snapshots);
					for (int index = 0; index < actual.Count; index++) staged[index].Adopt(actual[index]);
				});
			}
			else InvokeOwner(() => created = _scene.CreateBatch(snapshots));
			CommitEnvironment(snapshots, created);
			int shapes = snapshots.Sum(actor => actor.Shapes.Count);
			if (countAsRuntime) _runtimeSpawnAdditions += shapes;
			if (InfoLoggingActive)
			{
				int heightFields = snapshots.Sum(actor => actor.Shapes.Count(shape => shape.HeightField != null));
				Log(ExternalLogLevel.Info, $"External PhysX {source} environment batch committed atomically: actors={snapshots.Count}, colliders={shapes}, heightfields={heightFields}, total={_environmentByCollider.Count}.");
			}
			return shapes;
		}
		catch (Exception exception)
		{
			_lastBatchFailure = exception.GetType().Name + ": " + exception.Message;
			Log(ExternalLogLevel.Warning, $"External PhysX {source} environment batch failed without partial actor commit: {_lastBatchFailure}");
			return 0;
		}
	}

	private void FailEnvironmentUpdate(string source, string failure)
	{
		_failure = $"{source} environment transaction failed: {failure}";
		_ready = false;
		_bakeFailures++;
		Log(ExternalLogLevel.Error, $"External PhysX {_failure}; Unity fallback is required for this raid.");
	}

	private void CommitEnvironment(List<ActorSnapshot> snapshots, List<NativeActor> actors, List<EnvironmentActor> committed = null)
	{
		if (snapshots.Count != actors.Count) throw new InvalidOperationException("Native environment transaction returned an unexpected actor count.");
		for (int index = 0; index < snapshots.Count; index++)
		{
			EnvironmentActor environment = new EnvironmentActor(snapshots[index], actors[index]);
			_environmentActors.Add(environment);
			committed?.Add(environment);
			if (environment.Movable) _movableEnvironmentActors.Add(environment);
			foreach (EnvironmentShape shape in environment.Shapes)
				if (shape.Collider != null)
				{
					_environmentByCollider[shape.ColliderId] = environment;
					_deferredUnityShapeIds.Remove(shape.ColliderId);
				}
		}
	}

	private void AuditDeferredUnityShapes()
	{
		if (_deferredUnityShapeIds.Count == 0 || HasActiveJob(CaptureKind.Deferred)) return;
		int checkedCount = 0;
		while (checkedCount < DeferredUnityShapeAuditPerFrame && _deferredUnityShapes.Count != 0)
		{
			if (_deferredUnityShapeAuditIndex >= _deferredUnityShapes.Count) _deferredUnityShapeAuditIndex = 0;
			DeferredUnityShape deferred = _deferredUnityShapes[_deferredUnityShapeAuditIndex];
			Collider collider = deferred.Collider;
			if (collider == null)
			{
				_deferredUnityShapeIds.Remove(deferred.ColliderId);
				_deferredUnityShapes.RemoveAt(_deferredUnityShapeAuditIndex);
				checkedCount++;
				continue;
			}
			int colliderId = collider.GetInstanceID();
			if (!_deferredUnityShapeIds.Contains(colliderId))
			{
				_deferredUnityShapes.RemoveAt(_deferredUnityShapeAuditIndex);
				checkedCount++;
				continue;
			}
			_deferredUnityShapeAuditIndex++;
			checkedCount++;
			if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
			if (_claims.ContainsKey(colliderId)) continue;
			if (!IsFullPrebake && collider is TerrainCollider terrainCollider)
			{
				// ProximityCache: a terrain that became active after the last rescan joins the permanent terrain capture instead of being read by a deferred job.
				TrackTerrain(terrainCollider);
				continue;
			}
			// At most one capture per frame, staged across frames and submitted without waiting.
			StartDeferredCapture(collider);
			return;
		}
	}

	private void UpdateProximityConsumer(uint consumerId, HashSet<EnvironmentActor> desired)
	{
		_lastAcquireFrame = -1;
		if (!_proximityActorsByConsumer.TryGetValue(consumerId, out HashSet<EnvironmentActor> current))
		{
			current = new HashSet<EnvironmentActor>();
			_proximityActorsByConsumer[consumerId] = current;
		}
		_proximityRemovalsScratch.Clear();
		foreach (EnvironmentActor actor in current)
			if (!desired.Contains(actor)) _proximityRemovalsScratch.Add(actor);
		foreach (EnvironmentActor actor in _proximityRemovalsScratch)
		{
			current.Remove(actor);
			ReleaseProximityActor(actor);
		}
		foreach (EnvironmentActor actor in desired)
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

	private void ReleaseProximityActor(EnvironmentActor actor)
	{
		if (actor == null || actor.References <= 0) return;
		actor.References--;
		if (actor.References != 0) return;
		SetEnvironmentActorEnabled(actor, false);
		// Movable actors stay registered and continue to mirror their authoritative
		// Unity transform, but their shapes remain disabled outside every corridor.
		if (actor.Movable || actor.Root == null) return;
		RemoveFromRetainedStaticEnvironment(actor);
		actor.RetainedNode = _retainedStaticEnvironment.AddLast(actor);
	}

	private void SetEnvironmentActorEnabled(EnvironmentActor actor, bool enabled)
	{
		foreach (EnvironmentShape shape in actor.Shapes)
		{
			bool shapeEnabled = enabled && shape.Collider != null && shape.Collider.enabled && shape.Collider.gameObject.activeInHierarchy;
			shape.LastEnabled = shapeEnabled;
			QueueEnvironmentShapeEnabled(shape, shapeEnabled);
		}
	}

	private void TrimRetainedStaticEnvironment()
	{
		while (_retainedStaticEnvironment.Count > MaximumRetainedStaticEnvironment)
		{
			EnvironmentActor actor = _retainedStaticEnvironment.First.Value;
			RemoveFromRetainedStaticEnvironment(actor);
			RemoveEnvironmentActor(actor);
		}
	}

	private void RemoveFromRetainedStaticEnvironment(EnvironmentActor actor)
	{
		if (actor?.RetainedNode == null) return;
		_retainedStaticEnvironment.Remove(actor.RetainedNode);
		actor.RetainedNode = null;
	}

	private void RemoveEnvironmentActor(EnvironmentActor actor)
	{
		if (actor == null) return;
		actor.Removed = true;
		RemoveFromRetainedStaticEnvironment(actor);
		_lastAcquireFrame = -1;
		foreach (HashSet<EnvironmentActor> actors in _proximityActorsByConsumer.Values) actors.Remove(actor);
		foreach (EnvironmentShape shape in actor.Shapes)
			if (_environmentByCollider.TryGetValue(shape.ColliderId, out EnvironmentActor mapped) && ReferenceEquals(mapped, actor)) _environmentByCollider.Remove(shape.ColliderId);
		_movableEnvironmentActors.Remove(actor);
		_environmentActors.Remove(actor);
		QueueDestroyEnvironmentActor(actor);
	}

	private void CaptureMovableChanges()
	{
		// Bounds are a one-frame wake notification, not a cached poll result.
		// Clear even between polls so old movement is never replayed as a wake.
		_recentlyMovedBounds.Clear();
		if (!_movablePollCadence.TryBegin(Time.realtimeSinceStartup)) return;
		_movablePolls++;
		_movablePolledThisFrame = true;
		bool rebuildDeferred = false;
		for (int index = _movableEnvironmentActors.Count - 1; index >= 0; index--)
		{
			_movableRootChecks++;
			EnvironmentActor actor = _movableEnvironmentActors[index];
			if (actor.Root == null)
			{
				RemoveEnvironmentActor(actor);
				continue;
			}
			// A pending rebuild is revisited although the transform no longer reports a change: the probe consumed it when the rebuild was deferred.
			if (!actor.ScaleRebuildPending && !actor.TransformProbe.Changed(actor.Root)) continue;
			actor.Root.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
			Vector3 scale = actor.Root.lossyScale;
			if ((position - actor.LastPosition).sqrMagnitude > 0.000001f || Quaternion.Angle(rotation, actor.LastRotation) > 0.01f)
			{
				actor.LastPosition = position;
				actor.LastRotation = rotation;
				NativeApi.Pose pose = new NativeApi.Pose(position, rotation);
				QueueEnvironmentActorPose(actor, pose);
				_movablePoseUpdates++;
				foreach (EnvironmentShape shape in actor.Shapes)
					if (shape.Collider != null) _recentlyMovedBounds.Add(shape.Collider.bounds);
			}
			if ((scale - actor.LastScale).sqrMagnitude <= 0.000001f)
			{
				actor.ScaleRebuildPending = false;
				continue;
			}
			// A rebuild snapshots every shape of the actor on the main thread: past the update's budget it waits for the next poll,
			// which is requested for the next frame instead of the next quarter second.
			if (SnapshotRebuildBudgetSpent)
			{
				actor.ScaleRebuildPending = true;
				rebuildDeferred = true;
				continue;
			}
			actor.ScaleRebuildPending = false;
			actor.LastScale = scale;
			RebuildActorShapes(actor);
		}
		if (rebuildDeferred) _movablePollCadence.Expedite();
	}

	private void AuditEnvironmentShapes()
	{
		int checkedCount = 0;
		while (checkedCount < EnvironmentShapeAuditPerFrame && _environmentActors.Count != 0)
		{
			if (_shapeAuditActorIndex >= _environmentActors.Count)
			{
				_shapeAuditActorIndex = 0;
				_shapeAuditIndex = 0;
			}
			EnvironmentActor actor = _environmentActors[_shapeAuditActorIndex];
			if (_shapeAuditIndex >= actor.Shapes.Count)
			{
				_shapeAuditActorIndex++;
				_shapeAuditIndex = 0;
				continue;
			}
			EnvironmentShape shape = actor.Shapes[_shapeAuditIndex++];
			checkedCount++;
			bool actorEnabled = IsFullPrebake || actor.References > 0;
			bool enabled = actorEnabled && shape.Collider != null && shape.Collider.enabled && shape.Collider.gameObject.activeInHierarchy;
			if (enabled == shape.LastEnabled) continue;
			shape.LastEnabled = enabled;
			QueueEnvironmentShapeEnabled(shape, enabled);
		}
	}

	private void AuditStaticActors()
	{
		if (_environmentActors.Count == 0) return;
		int checkedCount = 0;
		while (checkedCount < StaticAuditPerFrame && checkedCount < _environmentActors.Count)
		{
			if (_staticAuditIndex >= _environmentActors.Count) _staticAuditIndex = 0;
			EnvironmentActor actor = _environmentActors[_staticAuditIndex++];
			checkedCount++;
			if (actor.Movable) continue;
			if (actor.Root == null)
			{
				RemoveEnvironmentActor(actor);
				continue;
			}
			if ((actor.Root.position - actor.LastPosition).sqrMagnitude <= 0.000001f && Quaternion.Angle(actor.Root.rotation, actor.LastRotation) <= 0.01f && (actor.Root.lossyScale - actor.LastScale).sqrMagnitude <= 0.000001f) continue;
			// Capped so a burst of moved statics cannot queue unbounded replacements; the audit revisits the actor next lap.
			if (_promotions.Count >= MaximumPendingPromotions) continue;
			// A promotion snapshots every shape of the actor on the main thread. Past the update's budget the actor is the next one
			// visited in the following frame, so a burst of moved statics is spread over frames without skipping anyone.
			if (SnapshotRebuildBudgetSpent)
			{
				_staticAuditIndex--;
				break;
			}
			bool replacementQueued = TryPromoteStaticActor(actor);
			actor.Movable = true;
			_movableEnvironmentActors.Add(actor);
			RemoveFromRetainedStaticEnvironment(actor);
			// The replacement is built at the scale the actor has now. Keeping the old scale here would make the movable poll rebuild
			// the static actor as well, only for the swap to discard that work.
			if (replacementQueued) actor.LastScale = actor.Root.lossyScale;
			Log(ExternalLogLevel.Warning, replacementQueued
				? $"External PhysX static integrity audit reclassified {actor.Root.name} as transform-driven movable geometry and queued a kinematic replacement actor; the static actor stays until it commits."
				: $"External PhysX static integrity audit reclassified {actor.Root.name} as transform-driven movable geometry; static-pose propagation remains active because kinematic replacement was unavailable.");
		}
	}

	private int QueryEnvironment(Vector3 center, Vector3 sweepEnd, float radius = EnvironmentRadius)
	{
		while (true)
		{
			int count = (sweepEnd - center).sqrMagnitude <= 0.0001f
				? Physics.OverlapSphereNonAlloc(center, radius, _overlapBuffer, _options.EnvironmentMask, QueryTriggerInteraction.Ignore)
				: Physics.OverlapCapsuleNonAlloc(center, sweepEnd, radius, _overlapBuffer, _options.EnvironmentMask, QueryTriggerInteraction.Ignore);
			if (count < _overlapBuffer.Length) return count;
			if (_overlapBuffer.Length >= MaximumOverlapCapacity) throw new InvalidOperationException($"Environment overlap exceeded {MaximumOverlapCapacity} colliders.");
			_overlapBuffer = new Collider[Mathf.Min(MaximumOverlapCapacity, _overlapBuffer.Length * 2)];
		}
	}

	/// <summary>
	/// Synchronous capture of a collider list into actor snapshots, for the full-bake startup and the EnsureEnvironment
	/// fallback. Classification is shared with the staged jobs; see <see cref="ClassifyForCapture"/>.
	/// </summary>
	private List<ActorSnapshot> CaptureColliders(IReadOnlyList<Collider> colliders, Action<int, int> progress = null)
	{
		CaptureGroups groups = new CaptureGroups();
		foreach (Collider collider in colliders) ClassifyForCapture(collider, groups, null);
		List<GroupBuilder> ordered = groups.Ordered;
		List<ActorSnapshot> snapshots = new List<ActorSnapshot>(ordered.Count);
		int completed = 0;
		int progressStride = Math.Max(1, (int)Math.Ceiling(ordered.Count / 10d));
		foreach (GroupBuilder group in ordered)
		{
			snapshots.Add(ActorSnapshot.CaptureEnvironment(group.Root, group.Colliders, group.Movable, _options.DeadbodyLayer));
			completed++;
			if (progress != null && (completed == ordered.Count || completed % progressStride == 0)) progress(completed, ordered.Count);
		}
		return snapshots;
	}

	private void DeferUnityShape(Collider collider)
	{
		if (collider == null) return;
		int colliderId = collider.GetInstanceID();
		if (_deferredUnityShapeIds.Add(colliderId)) _deferredUnityShapes.Add(new DeferredUnityShape(colliderId, collider));
	}

	private IEnumerable<Collider> EnumerateLoadedColliders()
	{
		for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
		{
			Scene scene = SceneManager.GetSceneAt(sceneIndex);
			if (!scene.IsValid() || !scene.isLoaded) continue;
			foreach (GameObject root in scene.GetRootGameObjects())
				foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
					if (Eligible(collider, includeDisabled: true)) yield return collider;
		}
	}

	private bool Eligible(Collider collider, bool includeDisabled)
	{
		if (collider == null || collider.isTrigger || (!includeDisabled && (!collider.enabled || !collider.gameObject.activeInHierarchy))) return false;
		if (((1 << collider.gameObject.layer) & _options.EnvironmentMask) == 0) return false;
		if ((collider is not BoxCollider && collider is not SphereCollider && collider is not CapsuleCollider && collider is not MeshCollider && collider is not TerrainCollider) ||
			(collider is MeshCollider mesh && mesh.sharedMesh == null) || (collider is TerrainCollider terrain && terrain.terrainData == null))
		{
			if (_stageCasingEnvironment && collider.enabled && collider.gameObject.activeInHierarchy)
				throw new NotSupportedException("Required casing environment geometry is unsupported: " + collider.GetType().Name + " / " + collider.name);
			return false;
		}
		return _environmentHierarchy.Allows(collider.transform);
	}

	/// <summary>Main-thread scratch for <see cref="MovementRoot"/>; <c>GetComponents&lt;Component&gt;()</c> would allocate an array per ancestor.</summary>
	private static readonly List<Component> ComponentScratch = new List<Component>(16);

	private static Transform MovementRoot(Collider collider, out bool movable)
	{
		Rigidbody attached = collider.attachedRigidbody;
		if (attached != null)
		{
			movable = true;
			return attached.transform;
		}
		Transform movementRoot = null;
		for (Transform current = collider.transform; current != null; current = current.parent)
		{
			bool explicitMovementRoot = current.GetComponent<Animator>() != null || current.GetComponent<Animation>() != null;
			if (!explicitMovementRoot)
			{
				current.GetComponents(ComponentScratch);
				foreach (Component component in ComponentScratch)
				{
					string name = component?.GetType().Name ?? string.Empty;
					if (name.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0 ||
						name.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Vehicle", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						explicitMovementRoot = true;
						break;
					}
				}
			}
			if (explicitMovementRoot && movementRoot == null) movementRoot = current;
		}
		// Tarkov leaves most baked map hierarchy objects unmarked as static, and generic
		// Interactive components also occur above fixed map geometry. Treating either as
		// proof of motion makes the whole proximity environment kinematic. Known movers and
		// rigidbodies start kinematic; the bounded integrity audit promotes any other root
		// that actually changes transform without accumulating per-frame false positives.
		movable = movementRoot != null;
		return movementRoot ?? collider.transform;
	}

	private string LoadedSceneSignature()
	{
		List<string> scenes = new List<string>();
		for (int index = 0; index < SceneManager.sceneCount; index++)
		{
			Scene scene = SceneManager.GetSceneAt(index);
			if (scene.IsValid() && scene.isLoaded) scenes.Add(scene.handle + ":" + scene.name);
		}
		scenes.Sort(StringComparer.Ordinal);
		return string.Join("|", scenes);
	}

	/// <summary>
	/// Queues best-effort owner work: unlike <see cref="EnqueueOwner"/> it neither wakes the simulation nor latches a
	/// failure into <see cref="_ownerFailure"/>, so warmup can never fail a raid. The work must catch its own errors.
	/// </summary>
	private void EnqueueQuietOwner(Action action, string name)
	{
		if (_disposed || _ownerThreadUnresponsive) return;
		DelegateWork work = DelegateWork.Rent(action, name, latchException: false, fireAndForget: true);
		lock (_ownerGate) _ownerQueue.Enqueue(work);
		_ownerSignal.Set();
	}

	/// <summary>
	/// Once per raid, after the scene is ready: creates and destroys a throwaway two-capsule actor pair joined by a D6 on
	/// the owner thread, so the first real corpse build does not pay the cold native body/joint creation cost on its
	/// own owner round trip. The main thread only builds plain snapshots and enqueues; it never waits.
	/// </summary>
	private void QueueBodyPathWarmup()
	{
		if (_bodyPathWarmupQueued) return;
		_bodyPathWarmupQueued = true;
		// Gate evaluated here on main; the owner thread must not read the config-backed delegate.
		bool verbose = InfoLoggingActive;
		ActorSnapshot first = ActorSnapshot.CreateWarmupCapsule(Vector3.zero, _options.DeadbodyLayer, _options.EnvironmentMask);
		ActorSnapshot second = ActorSnapshot.CreateWarmupCapsule(new Vector3(0f, 0.25f, 0f), _options.DeadbodyLayer, _options.EnvironmentMask);
		EnqueueQuietOwner(() => WarmBodyPath(first, second, verbose), nameof(QueueBodyPathWarmup));
	}

	/// <summary>Owner thread: creates then destroys the warmup actors and joint, absorbing and (debug) logging any failure.</summary>
	private void WarmBodyPath(ActorSnapshot firstSnapshot, ActorSnapshot secondSnapshot, bool verbose)
	{
		NativeScene scene = _scene;
		if (scene == null) return;
		long started = Stopwatch.GetTimestamp();
		NativeActor first = null;
		NativeActor second = null;
		ulong joint = 0;
		Exception failure = null;
		try
		{
			first = scene.CreateActor(firstSnapshot);
			second = scene.CreateActor(secondSnapshot);
			joint = scene.CreateJoint(JointSnapshot.CreateWarmupD6(first.Handle, second.Handle));
		}
		catch (Exception exception) { failure = exception; }
		try { scene.DestroyJoint(joint); }
		catch (Exception exception) { failure ??= exception; }
		try { if (second != null) scene.DestroyActor(second.Handle); }
		catch (Exception exception) { failure ??= exception; }
		try { if (first != null) scene.DestroyActor(first.Handle); }
		catch (Exception exception) { failure ??= exception; }
		if (!verbose) return;
		double milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
		Log(ExternalLogLevel.Info, failure == null
			? $"External PhysX body-path warmup completed on the owner thread in {milliseconds:F2} ms."
			: $"External PhysX body-path warmup failed after {milliseconds:F2} ms and was ignored: {DescribeException(failure)}");
	}

	private void Log(ExternalLogLevel level, string message) => _options?.Log?.Invoke(level, message);

	/// <summary>Per-kill and per-capture Info text is formatted only while the host reports debug logging; an unset gate keeps it on.</summary>
	private bool InfoLoggingActive => _options?.InfoLoggingEnabled?.Invoke() ?? true;

	private static uint MapForceMode(ForceMode mode)
	{
		switch (mode)
		{
		case ForceMode.Impulse: return 1;
		case ForceMode.VelocityChange: return 2;
		case ForceMode.Acceleration: return 3;
		default: return 0;
		}
	}

	private sealed class BodyCreationTicket : IExternalBodyCreation
	{
		private readonly ExternalPhysXRuntime _runtime;
		private bool _handedOff;
		private IExternalPhysicsBody _body;
		internal readonly ActorSnapshot Snapshot;
		internal readonly Vector3 CenterOfMass;
		internal OwnerWork Work;
		internal readonly OwnedResult<NativeActor> Result = new OwnedResult<NativeActor>();
		internal volatile bool CancelRequested;
		public double WorkerMilliseconds => Work?.Stopwatch.Elapsed.TotalMilliseconds ?? 0d;

		internal BodyCreationTicket(ExternalPhysXRuntime runtime, ActorSnapshot snapshot, Vector3 centerOfMass)
		{
			_runtime = runtime;
			Snapshot = snapshot;
			CenterOfMass = centerOfMass;
		}

		public ExternalBodyCreationStatus Poll(out IExternalPhysicsBody body, out string failure)
		{
			body = null;
			failure = null;
			if (_handedOff)
			{
				body = _body;
				return ExternalBodyCreationStatus.Succeeded;
			}
			if (CancelRequested) return ExternalBodyCreationStatus.Cancelled;
			if (Work == null || !Work.IsCompleted) return ExternalBodyCreationStatus.Pending;
			if (Work.Exception != null)
			{
				failure = DescribeException(Work.Exception);
				return ExternalBodyCreationStatus.Failed;
			}
			NativeActor native = Result.Take();
			if (native == null)
			{
				failure = "external owner completed without creating the transient actor";
				return ExternalBodyCreationStatus.Failed;
			}
			try
			{
				body = _runtime.MaterializeBody(this, native);
				_body = body;
				_handedOff = true;
				return ExternalBodyCreationStatus.Succeeded;
			}
			catch (Exception exception)
			{
				failure = exception.GetType().Name + ": " + exception.Message;
				_runtime.QueueDestroyActor(native.Handle);
				Cancel();
				return ExternalBodyCreationStatus.Failed;
			}
		}

		public void Cancel()
		{
			if (_handedOff || CancelRequested) return;
			CancelRequested = true;
			NativeActor native = Result.Take();
			if (native != null) _runtime.QueueDestroyActor(native.Handle);
		}

		public void Dispose() => Cancel();
	}

	private sealed class DeferredUnityShape
	{
		internal readonly int ColliderId;
		internal readonly Collider Collider;
		internal DeferredUnityShape(int colliderId, Collider collider) { ColliderId = colliderId; Collider = collider; }
	}
}
