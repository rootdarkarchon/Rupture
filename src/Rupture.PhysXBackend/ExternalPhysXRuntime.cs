using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

public sealed partial class ExternalPhysXRuntime : IExternalPhysicsRuntime, IExternalCorpseControlRuntime
{
	private const float EnvironmentRadius = 2f;
	private const int InitialOverlapCapacity = 256;
	private const int MaximumOverlapCapacity = 4096;
	private const int StaticAuditPerFrame = 16;
	private const int EnvironmentShapeAuditPerFrame = 64;
	private const int DeferredUnityShapeAuditPerFrame = 128;
	private const int MaximumRetainedStaticEnvironment = 2048;
	private readonly object _ownerGate = new object();
	private readonly Queue<OwnerWork> _ownerQueue = new Queue<OwnerWork>();
	private readonly AutoResetEvent _ownerSignal = new AutoResetEvent(false);
	private readonly Dictionary<ulong, ExternalBody> _bodies = new Dictionary<ulong, ExternalBody>();
	private readonly Dictionary<ulong, ExternalJoint> _joints = new Dictionary<ulong, ExternalJoint>();
	private ExternalJoint[] _jointSnapshot;
	private readonly SimulationActivity _activity = new SimulationActivity();
	public bool NeedsSimulation => _activity.NeedsStep;
	private readonly Dictionary<int, ulong> _bodyShapesByCollider = new Dictionary<int, ulong>();
	private readonly Dictionary<int, EnvironmentActor> _environmentByCollider = new Dictionary<int, EnvironmentActor>();
	private readonly List<EnvironmentActor> _environmentActors = new List<EnvironmentActor>();
	private readonly List<EnvironmentActor> _movableEnvironmentActors = new List<EnvironmentActor>();
	private EnvironmentPollCadence _movablePollCadence;
	private long _movablePolls, _movableRootChecks;
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
	private OwnerWork _stepWork;
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
			return $"backend=ExternalPhysX, ready={IsRaidReady}, meshExtraction=UnityPhysXExtraction/{_meshGeometrySource?.Status ?? "uninitialized"}, initialBake={(IsFullPrebake ? (_ready ? "complete" : (_failure == null ? "waiting" : "failed")) : "not-selected")}, " +
				$"lateBake={_lateScenes.Count}, environmentActors={_environmentActors.Count}/static:{_environmentActors.Count - movable}/movable:{movable}/retained:{_retainedStaticEnvironment.Count}, " +
				$"environmentShapes={shapes}/disabled:{disabledShapes}/deferredUnityShapes:{_deferredUnityShapeIds.Count}, bodies={_bodies.Count}, joints={_joints.Count}, materials={scene?.MaterialCount ?? 0}, movablePoseUpdates={_movablePoseUpdates}, scaleRebuilds={_scaleRebuilds}, " +
				$"movablePolling=4Hz/polls:{_movablePolls}/rootChecks:{_movableRootChecks}, " +
				graphicalBodies + $"lateSceneAdditions={_lateSceneAdditions}, runtimeSpawnAdditions={_runtimeSpawnAdditions}, " +
				$"cache=memory:{scene?.MeshMemoryHits ?? 0}/disk:{scene?.MeshDiskHits ?? 0}/cooked:{scene?.MeshCooks ?? 0}/rawBytes:{_rawGeometryBytes}/cookedBytes:{scene?.CookedBytes ?? 0}, " +
				$"initialColliders={_initialColliderCount}, initialActors={_initialActorCount}, bakeMs={_bakeMilliseconds:F1}, bakeFailures={_bakeFailures}, failure={Failure ?? "none"}";
		}
	}

	public void Initialize(ExternalBackendOptions options)
	{
		if (options == null) throw new ArgumentNullException(nameof(options));
		if (options.DispatcherThreads < 0 || options.DispatcherThreads > 4) throw new ArgumentOutOfRangeException(nameof(options.DispatcherThreads));
		if (string.IsNullOrWhiteSpace(options.BackendRoot) || string.IsNullOrWhiteSpace(options.CacheRoot)) throw new ArgumentException("Backend and cache roots are required.");
		_options = options;
		Directory.CreateDirectory(options.CacheRoot);
		_api = new NativeApi(options.BackendRoot, options.GraphicalCasings);
		_meshGeometrySource = new UnityPhysXMeshGeometrySource(_api, options.EnvironmentMask, options.Log);
		_ownerThread = new Thread(OwnerLoop)
		{
			IsBackground = true,
			Name = "RPT External PhysX Scene Owner",
			Priority = System.Threading.ThreadPriority.BelowNormal
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
		if (_lateSceneHandles.Add(handle)) _lateScenes.Enqueue(scene);
	}

	public void CaptureEnvironmentChanges()
	{
		if (!_raidStarted || _disposed) return;
		_meshGeometrySource.Update();
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
			Log(ExternalLogLevel.Lifecycle, $"External PhysX ProximityCache raid scene is ready with source parity ({sourceSceneSettings}) after {_meshGeometrySource.Name} initialization ({_meshGeometrySource.Status}).");
		}
		if (!_ready && _failure == null && IsFullPrebake)
		{
			string signature = LoadedSceneSignature();
			if (string.Equals(signature, _lastSceneSignature, StringComparison.Ordinal)) _stableSceneFrames++;
			else
			{
				_lastSceneSignature = signature;
				_stableSceneFrames = 0;
			}
			if (Time.frameCount > _raidStartedFrame && _stableSceneFrames >= 2) BuildInitialEnvironment();
			return;
		}
		if (!_ready) return;

		while (_lateScenes.Count != 0)
		{
			Scene scene = _lateScenes.Dequeue();
			_lateSceneHandles.Remove(scene.handle);
			if (scene.IsValid() && scene.isLoaded)
			{
				AddEnvironmentBatch(CaptureScene(scene), "late-scene", countAsRuntime: false);
				if (!string.IsNullOrEmpty(_lastBatchFailure))
				{
					FailEnvironmentUpdate("late-scene", _lastBatchFailure);
					return;
				}
			}
		}

		AuditDeferredUnityShapes();
		AuditStaticActors();
		CaptureMovableChanges();
		AuditEnvironmentShapes();
	}

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
		if (IsFullPrebake)
		{
			try
			{
				int count = QueryEnvironment(center, sweepEnd);
				_proximityUnknownScratch.Clear();
				int reused = 0;
				for (int index = 0; index < count; index++)
				{
					Collider collider = _overlapBuffer[index];
					if (!Eligible(collider, includeDisabled: false)) continue;
					int colliderId = collider.GetInstanceID();
					if (_environmentByCollider.ContainsKey(colliderId)) reused++;
					else if (!_bodyShapesByCollider.ContainsKey(colliderId)) _proximityUnknownScratch.Add(collider);
				}
				_meshGeometrySource.PrepareProximity(center, sweepEnd, EnvironmentRadius, _proximityUnknownScratch);
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
		}
		if (consumerId == 0)
		{
			failure = "the proximity-cache consumer id is invalid";
			return false;
		}

		try
		{
			int count = QueryEnvironment(center, sweepEnd);
			_proximityUnknownScratch.Clear();
			_proximityColliderIdsScratch.Clear();
			int reused = 0;
			for (int index = 0; index < count; index++)
			{
				Collider collider = _overlapBuffer[index];
				if (!Eligible(collider, includeDisabled: false)) continue;
				_proximityColliderIdsScratch.Add(collider.GetInstanceID());
				if (_environmentByCollider.ContainsKey(collider.GetInstanceID())) reused++;
				else _proximityUnknownScratch.Add(collider);
			}
			_meshGeometrySource.PrepareProximity(center, sweepEnd, EnvironmentRadius, _proximityUnknownScratch);
			int created = CaptureProximityUnknown("proximity", false);
			if (!string.IsNullOrEmpty(_lastBatchFailure))
			{
				failure = _lastBatchFailure;
				return false;
			}
			_proximityActorsScratch.Clear();
			foreach (int colliderId in _proximityColliderIdsScratch)
				if (_environmentByCollider.TryGetValue(colliderId, out EnvironmentActor actor)) _proximityActorsScratch.Add(actor);
			UpdateProximityConsumer(consumerId, _proximityActorsScratch);
			result = new ExternalEnvironmentAcquireResult(created, reused, _environmentByCollider.Count);
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
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
		if (consumerId == 0 || IsFullPrebake || !_proximityActorsByConsumer.TryGetValue(consumerId, out HashSet<EnvironmentActor> actors)) return;
		_proximityActorsByConsumer.Remove(consumerId);
		foreach (EnvironmentActor actor in actors) ReleaseProximityActor(actor);
		TrimRetainedStaticEnvironment();
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
			Vector3 centerOfMass = nativeBody.centerOfMass;
			ExternalBody created = null;
			InvokeOwner(() =>
			{
				NativeActor native = _scene.CreateActor(snapshot);
				created = new ExternalBody(this, native.Handle, centerOfMass, snapshot.Body);
				for (int index = 0; index < snapshot.Shapes.Count && index < native.ShapeHandles.Count; index++)
				{
					int colliderId = snapshot.Shapes[index].ColliderId;
					created.ShapeHandlesByCollider[colliderId] = native.ShapeHandles[index];
					created.ShapeEnabledByHandle[native.ShapeHandles[index]] = snapshot.Shapes[index].Enabled;
				}
			});
			RegisterBody(created);
			body = created;
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
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
			OwnerWork work = new OwnerWork(() =>
			{
				if (ticket.CancelRequested) return null;
				NativeActor created = _scene.CreateActor(snapshot);
				if (!ticket.Result.TryPublish(created)) _scene.DestroyActor(created.Handle);
				return null;
			}, 0);
			ticket.Work = work;
			lock (_ownerGate) { _activity.Changed(); _ownerQueue.Enqueue(work); }
			_ownerSignal.Set();
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
			ExternalBody created = null;
			ulong shapeHandle = 0;
			InvokeOwner(() =>
			{
				NativeActor native = _scene.CreateActor(snapshot);
				created = new ExternalBody(this, native.Handle, box.Center, snapshot.Body, snapshot.Body.CenterOfMass.ToUnity());
				shapeHandle = native.ShapeHandles[0];
			});
			BindCompoundColliders(created, sourceColliders, shapeHandle);
			RegisterBody(created);
			body = created;
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
			ExternalBody created = null;
			ulong shapeHandle = 0;
			InvokeOwner(() =>
			{
				NativeActor native = _scene.CreateActor(snapshot);
				Vector3 physicalCenter = native.PhysicalCenterOfMass ?? snapshot.Body.CenterOfMass.ToUnity();
				created = new ExternalBody(this, native.Handle, physicalCenter, snapshot.Body, physicalCenter);
				shapeHandle = native.ShapeHandles[0];
			});
			BindCompoundColliders(created, sourceColliders, shapeHandle);
			RegisterBody(created);
			body = created;
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
			JointSnapshot snapshot = JointSnapshot.Capture(nativeJoint, child.Handle, connected?.Handle ?? 0);
			ulong handle = 0;
			InvokeOwner(() =>
			{
				handle = _scene.CreateJoint(snapshot);
				try
				{
					if (!nativeJoint.enableCollision && connected != null)
						foreach (ulong left in child.ShapeHandlesByCollider.Values)
							foreach (ulong right in connected.ShapeHandlesByCollider.Values) _scene.IgnoreShapePair(left, right);
				}
				catch
				{
					_scene.DestroyJoint(handle);
					handle = 0;
					throw;
				}
			});
			ExternalJoint created = new ExternalJoint(handle);
			_joints[handle] = created;
			_jointSnapshot = null;
			joint = created;
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
		if (!_bodyShapesByCollider.TryGetValue(left.GetInstanceID(), out ulong leftHandle) ||
			!_bodyShapesByCollider.TryGetValue(right.GetInstanceID(), out ulong rightHandle)) return;
		EnqueueOwner(() => _scene?.IgnoreShapePair(leftHandle, rightHandle));
	}

	public void DestroyJoint(IExternalPhysicsJoint joint)
	{
		if (joint is not ExternalJoint external || !external.IsValid || _scene == null) return;
		external.Invalidate();
		_joints.Remove(external.Handle);
		_jointSnapshot = null;
		EnqueueOwner(() => _scene.DestroyJoint(external.Handle));
	}

	public void DestroyBody(IExternalPhysicsBody body)
	{
		if (body is not ExternalBody external || !external.IsValid || _scene == null) return;
		_bodies.Remove(external.Handle);
		foreach (KeyValuePair<int, ulong> shape in external.ShapeHandlesByCollider)
			if (_bodyShapesByCollider.TryGetValue(shape.Key, out ulong mapped) && mapped == shape.Value) _bodyShapesByCollider.Remove(shape.Key);
		external.Invalidate();
		EnqueueOwner(() => _scene.DestroyActor(external.Handle));
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
			OwnerWork work = null;
			work = new OwnerWork(() =>
			{
				if (casingFrame != null && _ownerFailure != null) throw new InvalidOperationException(_ownerFailure);
				if (casingFrame != null) work.Phase = "casing commands";
				if (casingFrame != null) _scene.CasingCommands(casingFrame);
				long simulationStarted = casingFrame != null ? Stopwatch.GetTimestamp() : 0;
				work.Phase = "native simulation and body readback";
				NativeApi.BodyState[] states = _scene.Step(deltaTime, out work.PublishedBodies, out work.AwakeBodies);
				if (casingFrame != null) casingFrame.SimulationMilliseconds = (Stopwatch.GetTimestamp() - simulationStarted) * 1000d / Stopwatch.Frequency;
				if (casingFrame != null) work.Phase = "casing readback";
				if (casingFrame != null) _scene.ReadCasings(casingFrame);
				work.Phase = "joint publication";
				foreach (ExternalJoint joint in joints)
					if (joint.IsValid) joint.PublishBroken(_scene.IsJointBroken(joint.Handle));
				return states;
			}, ++_stepSequence);
			work.DeltaTime = deltaTime;
			work.CommandVersion = _activity.Version;
			work.BodyCount = _bodies.Count;
			work.JointCount = joints.Length;
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
		OwnerWork work;
		lock (_ownerGate) work = _stepWork;
		if (work == null)
		{
			result = new ExternalStepResult(false, false, 0d, 0d, true, _completedSequence);
			return true;
		}
		bool wasReady = work.Completion.Task.IsCompleted;
		long waitStarted = Stopwatch.GetTimestamp();
		bool completed = wasReady || work.Completion.Task.Wait(Math.Max(0, timeoutMilliseconds));
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
		if (_api == null || _disposed) return;
		if (_ownerThreadUnresponsive) return;
		OwnerWork step;
		lock (_ownerGate) step = _stepWork;
		if (step != null && !step.Completion.Task.Wait(500))
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
		MeshGeometryRepository.Clear();
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
		EnqueueOwner(() => _scene?.SetBodyState(body.Handle, state, body.IsKinematic, wake));
	}

	internal void QueueForce(ExternalBody body, uint operation, Vector3 force, Vector3 point, ForceMode mode)
	{
		if (body == null || !body.IsValid) return;
		NativeApi.ForceCommand command = new NativeApi.ForceCommand
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.ForceCommand>(), Operation = operation, Actor = body.Handle,
			Force = new NativeApi.Vec3(force), Position = new NativeApi.Vec3(point), Mode = MapForceMode(mode), Wake = 1
		};
		EnqueueOwner(() => _scene?.ApplyForce(command));
	}

	internal void QueueBodyCollision(ExternalBody body, bool enabled)
	{
		if (body == null || !body.IsValid) return;
		foreach (KeyValuePair<ulong, bool> shape in body.ShapeEnabledByHandle)
		{
			ulong handle = shape.Key;
			bool shapeEnabled = enabled && shape.Value;
			EnqueueOwner(() => _scene?.SetShapeEnabled(handle, shapeEnabled));
		}
	}

	private void BuildInitialEnvironment()
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		NativeScene staging = null;
		try
		{
			List<Collider> colliders = EnumerateLoadedColliders().ToList();
			Log(ExternalLogLevel.Info, $"External PhysX full bake started: eligibleColliders={colliders.Count}, loadedScenes={SceneManager.sceneCount}.");
			_meshGeometrySource.PrepareAll();
			List<ActorSnapshot> snapshots = CaptureColliders(colliders, (completed, total) =>
				Log(ExternalLogLevel.Info, $"External PhysX full bake capture progress: actors={completed}/{total}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}."));
			List<MeshGeometry> uniqueMeshes = snapshots.SelectMany(actor => actor.Shapes).Select(shape => shape.Mesh).Where(mesh => mesh != null)
				.GroupBy(mesh => mesh.CacheKey(_options.EftBuild ?? "unknown"), StringComparer.Ordinal).Select(group => group.First()).ToList();
			List<HeightFieldGeometry> uniqueHeightFields = snapshots.SelectMany(actor => actor.Shapes).Select(shape => shape.HeightField).Where(heightField => heightField != null)
				.GroupBy(heightField => heightField.CacheKey(_options.EftBuild ?? "unknown"), StringComparer.Ordinal).Select(group => group.First()).ToList();
			_rawGeometryBytes = uniqueMeshes.Sum(mesh => (long)mesh.Vertices.Length * 12L + (long)mesh.Indices.Length * 4L) + uniqueHeightFields.Sum(heightField => (long)heightField.Samples.Length);
			Log(ExternalLogLevel.Info, $"External PhysX full bake capture complete: colliders={snapshots.Sum(actor => actor.Shapes.Count)}, actors={snapshots.Count}, uniqueMeshes={uniqueMeshes.Count}, uniqueHeightFields={uniqueHeightFields.Count}, estimatedRawGeometryBytes={_rawGeometryBytes}, elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F1}.");
			List<NativeActor> created = null;
			InvokeOwner(() =>
			{
				staging = new NativeScene(_api, _options, _meshGeometrySource.SourceSceneSettings);
				created = staging.CreateBatch(snapshots);
				NativeScene previous = _scene;
				_scene = staging;
				staging = null;
				previous?.Dispose();
			});
			CommitEnvironment(snapshots, created);
			_ready = true;
			_initialColliderCount = snapshots.Sum(actor => actor.Shapes.Count);
			_initialActorCount = snapshots.Count;
			_bakeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
			Log(ExternalLogLevel.Lifecycle, $"External PhysX full bake committed atomically: colliders={_initialColliderCount}, actors={_initialActorCount}, deferredInactiveUnityShapes={_deferredUnityShapeIds.Count}, elapsedMs={_bakeMilliseconds:F1}, {Status}.");
		}
		catch (Exception exception)
		{
			_bakeFailures++;
			_failure = exception.GetType().Name + ": " + exception.Message;
			_ready = false;
			InvokeOwner(() =>
			{
				staging?.Dispose();
				staging = null;
				_scene?.Dispose();
				_scene = null;
			});
			Log(ExternalLogLevel.Error, $"External PhysX full bake failed; the incomplete scene was discarded and Unity fallback is required for this raid. {_failure}");
		}
		finally
		{
			stopwatch.Stop();
		}
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
			int heightFields = snapshots.Sum(actor => actor.Shapes.Count(shape => shape.HeightField != null));
			if (countAsRuntime) _runtimeSpawnAdditions += shapes;
			else if (source == "late-scene") _lateSceneAdditions += shapes;
			Log(ExternalLogLevel.Info, $"External PhysX {source} environment batch committed atomically: actors={snapshots.Count}, colliders={shapes}, heightfields={heightFields}, total={_environmentByCollider.Count}.");
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

	private void CommitEnvironment(List<ActorSnapshot> snapshots, List<NativeActor> actors)
	{
		if (snapshots.Count != actors.Count) throw new InvalidOperationException("Native environment transaction returned an unexpected actor count.");
		for (int index = 0; index < snapshots.Count; index++)
		{
			EnvironmentActor environment = new EnvironmentActor(snapshots[index], actors[index]);
			_environmentActors.Add(environment);
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
		if (_deferredUnityShapeIds.Count == 0) return;
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
			AddEnvironmentBatch(CaptureColliders(new Collider[] { collider }), "deferred-unity-shape", countAsRuntime: true);
			if (!string.IsNullOrEmpty(_lastBatchFailure))
			{
				FailEnvironmentUpdate("deferred-unity-shape", _lastBatchFailure);
				return;
			}
		}
	}

	private void UpdateProximityConsumer(uint consumerId, HashSet<EnvironmentActor> desired)
	{
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
			EnqueueOwner(() => _scene?.SetShapeEnabled(shape.NativeHandle, shapeEnabled));
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
		RemoveFromRetainedStaticEnvironment(actor);
		foreach (HashSet<EnvironmentActor> actors in _proximityActorsByConsumer.Values) actors.Remove(actor);
		foreach (EnvironmentShape shape in actor.Shapes)
			if (_environmentByCollider.TryGetValue(shape.ColliderId, out EnvironmentActor mapped) && ReferenceEquals(mapped, actor)) _environmentByCollider.Remove(shape.ColliderId);
		_movableEnvironmentActors.Remove(actor);
		_environmentActors.Remove(actor);
		EnqueueOwner(() => _scene?.DestroyActor(actor.Native.Handle));
	}

	private void CaptureMovableChanges()
	{
		// Bounds are a one-frame wake notification, not a cached poll result.
		// Clear even between polls so old movement is never replayed as a wake.
		_recentlyMovedBounds.Clear();
		if (!_movablePollCadence.TryBegin(Time.realtimeSinceStartup)) return;
		_movablePolls++;
		for (int index = _movableEnvironmentActors.Count - 1; index >= 0; index--)
		{
			_movableRootChecks++;
			EnvironmentActor actor = _movableEnvironmentActors[index];
			if (actor.Root == null)
			{
				RemoveEnvironmentActor(actor);
				continue;
			}
			if (!actor.TransformProbe.Changed(actor.Root)) continue;
			actor.Root.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
			Vector3 scale = actor.Root.lossyScale;
			if ((position - actor.LastPosition).sqrMagnitude > 0.000001f || Quaternion.Angle(rotation, actor.LastRotation) > 0.01f)
			{
				actor.LastPosition = position;
				actor.LastRotation = rotation;
				NativeApi.Pose pose = new NativeApi.Pose(position, rotation);
				EnqueueOwner(() => _scene?.SetActorPose(actor.Native.Handle, pose, kinematicTarget: true));
				_movablePoseUpdates++;
				foreach (EnvironmentShape shape in actor.Shapes)
					if (shape.Collider != null) _recentlyMovedBounds.Add(shape.Collider.bounds);
			}
			if ((scale - actor.LastScale).sqrMagnitude > 0.000001f)
			{
				actor.LastScale = scale;
				List<Collider> replacementColliders = actor.Shapes.Select(shape => shape.Collider).Where(collider => collider != null).ToList();
				ActorSnapshot replacement = replacementColliders.Count == 0 ? null : ActorSnapshot.CaptureEnvironment(actor.Root, replacementColliders, true, _options.DeadbodyLayer);
				if (replacement != null)
				{
					EnqueueOwner(() => _scene?.ReplaceActorShapes(actor.Native, replacement));
					_scaleRebuilds++;
				}
			}
		}
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
			EnqueueOwner(() => _scene?.SetShapeEnabled(shape.NativeHandle, enabled));
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
			bool promotedToKinematic = TryPromoteStaticActor(actor);
			actor.Movable = true;
			_movableEnvironmentActors.Add(actor);
			RemoveFromRetainedStaticEnvironment(actor);
			Log(ExternalLogLevel.Warning, promotedToKinematic
				? $"External PhysX static integrity audit reclassified {actor.Root.name} as transform-driven movable geometry and replaced its static actor with a kinematic actor."
				: $"External PhysX static integrity audit reclassified {actor.Root.name} as transform-driven movable geometry; static-pose propagation remains active because kinematic replacement was unavailable.");
		}
	}

	private bool TryPromoteStaticActor(EnvironmentActor actor)
	{
		try
		{
			List<Collider> colliders = actor.Shapes.Select(shape => shape.Collider).Where(collider => collider != null).ToList();
			if (colliders.Count == 0) return false;
			ActorSnapshot replacement = ActorSnapshot.CaptureEnvironment(actor.Root, colliders, movable: true, _options.DeadbodyLayer);
			bool enabled = IsFullPrebake || actor.References > 0;
			foreach (ShapeSnapshot shape in replacement.Shapes) shape.Enabled &= enabled;
			NativeActor previous = actor.Native;
			NativeActor promoted = null;
			InvokeOwner(() =>
			{
				promoted = _scene.CreateActor(replacement);
				_scene.DestroyActor(previous.Handle);
			});
			actor.ReplaceNative(promoted);
			return true;
		}
		catch (Exception exception)
		{
			Log(ExternalLogLevel.Warning, $"External PhysX could not replace moved static actor {actor.Root?.name}: {exception.GetType().Name}: {exception.Message}");
			return false;
		}
	}

	private int QueryEnvironment(Vector3 center, Vector3 sweepEnd)
	{
		while (true)
		{
			int count = (sweepEnd - center).sqrMagnitude <= 0.0001f
				? Physics.OverlapSphereNonAlloc(center, EnvironmentRadius, _overlapBuffer, _options.EnvironmentMask, QueryTriggerInteraction.Ignore)
				: Physics.OverlapCapsuleNonAlloc(center, sweepEnd, EnvironmentRadius, _overlapBuffer, _options.EnvironmentMask, QueryTriggerInteraction.Ignore);
			if (count < _overlapBuffer.Length) return count;
			if (_overlapBuffer.Length >= MaximumOverlapCapacity) throw new InvalidOperationException($"Environment overlap exceeded {MaximumOverlapCapacity} colliders.");
			_overlapBuffer = new Collider[Mathf.Min(MaximumOverlapCapacity, _overlapBuffer.Length * 2)];
		}
	}

	private List<ActorSnapshot> CaptureScene(Scene scene)
	{
		List<Collider> colliders = new List<Collider>();
		foreach (GameObject root in scene.GetRootGameObjects()) colliders.AddRange(root.GetComponentsInChildren<Collider>(true));
		_meshGeometrySource.PrepareAll();
		return CaptureColliders(colliders);
	}

	private List<ActorSnapshot> CaptureColliders(IReadOnlyList<Collider> colliders, Action<int, int> progress = null)
	{
		Dictionary<int, List<Collider>> groups = new Dictionary<int, List<Collider>>();
		Dictionary<int, Transform> roots = new Dictionary<int, Transform>();
		Dictionary<int, bool> movable = new Dictionary<int, bool>();
		foreach (Collider collider in colliders)
		{
			int colliderId = collider != null ? collider.GetInstanceID() : 0;
			if (!Eligible(collider, includeDisabled: true) || _environmentByCollider.ContainsKey(colliderId) || _bodyShapesByCollider.ContainsKey(colliderId)) continue;
			if (collider is MeshCollider || collider is TerrainCollider)
			{
				if (!collider.enabled || !collider.gameObject.activeInHierarchy)
				{
					DeferUnityShape(collider);
					continue;
				}
				// Validate and cache the current Unity-owned native shape before it is
				// grouped into an atomic actor transaction. An active missing shape is
				// a hard failure; silently committing an incomplete collision scene is
				// less safe than leaving EFT's native corpse physics active for this raid.
				if (collider is MeshCollider unityMesh) MeshGeometryRepository.Get(unityMesh);
				else MeshGeometryRepository.Get((TerrainCollider)collider);
			}
			Transform root = MovementRoot(collider, out bool isMovable);
			int key = root.GetInstanceID();
			if (!groups.TryGetValue(key, out List<Collider> group)) groups[key] = group = new List<Collider>();
			group.Add(collider);
			roots[key] = root;
			movable[key] = isMovable;
		}
		List<ActorSnapshot> snapshots = new List<ActorSnapshot>(groups.Count);
		int completed = 0;
		int progressStride = Math.Max(1, (int)Math.Ceiling(groups.Count / 10d));
		foreach (KeyValuePair<int, List<Collider>> group in groups)
		{
			snapshots.Add(ActorSnapshot.CaptureEnvironment(roots[group.Key], group.Value, movable[group.Key], _options.DeadbodyLayer));
			completed++;
			if (progress != null && (completed == groups.Count || completed % progressStride == 0)) progress(completed, groups.Count);
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
				foreach (Component component in current.GetComponents<Component>())
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

	private void OwnerLoop()
	{
		while (!_disposed && !_ownerThreadUnresponsive)
		{
			OwnerWork work = null;
			lock (_ownerGate)
			{
				if (_ownerQueue.Count != 0) work = _ownerQueue.Dequeue();
			}
			if (work == null)
			{
				_ownerSignal.WaitOne(100);
				continue;
			}
			work.Stopwatch.Start();
			_executingWork = work;
			try
			{
				object value = work.Action();
				if (value is NativeApi.BodyState[] states) work.StepStates = states;
			}
			catch (Exception exception)
			{
				work.Exception = exception;
				if (work.LatchException) _ownerFailure = exception.GetType().Name + ": " + exception.Message;
			}
			finally
			{
				work.Stopwatch.Stop();
				if (work.Stopwatch.ElapsedMilliseconds >= 100)
					Log(ExternalLogLevel.Warning, $"External PhysX slow owner operation: name={work.Name}, phase={work.Phase}, executionMs={work.Stopwatch.Elapsed.TotalMilliseconds:F3}, dt={work.DeltaTime:F6}, bodies={work.BodyCount}, joints={work.JointCount}, completed=True, quarantined={_ownerThreadUnresponsive}.");
				_executingWork = null;
				work.Completion.TrySetResult(true);
			}
		}
		// A quarantined owner may eventually return from native code. It alone can
		// then release its scene; the main thread never frees resources beneath it.
		try { _scene?.Dispose(); _scene = null; }
		catch (Exception exception) { Log(ExternalLogLevel.Error, "External PhysX owner cleanup failed: " + exception.Message); }
		lock (_ownerGate)
			while (_ownerQueue.Count > 0)
			{
				OwnerWork abandoned = _ownerQueue.Dequeue();
				abandoned.Exception = new InvalidOperationException("External PhysX owner stopped before this operation.");
				abandoned.Completion.TrySetResult(true);
			}
	}

	private void InvokeOwner(Action action, [CallerMemberName] string name = null)
	{
		if (_disposed || _ownerThreadUnresponsive)
			throw new InvalidOperationException("External PhysX owner is unavailable.");
		OwnerWork work = new OwnerWork(() => { action(); return null; }, 0, name: name);
		lock (_ownerGate) { _activity.Changed(); _ownerQueue.Enqueue(work); }
		_ownerSignal.Set();
		if (!work.Completion.Task.Wait(500))
		{
			_ownerThreadUnresponsive = true;
			_ready = false;
			_raidStarted = false;
			_failure = "External PhysX scene-owner operation exceeded the 500 ms watchdog. The backend is quarantined until process exit. " + DescribeOwner(work);
			Log(ExternalLogLevel.Error, _failure);
			throw new TimeoutException(_failure);
		}
		if (work.Exception != null)
			throw new InvalidOperationException($"External PhysX owner operation failed: {DescribeException(work.Exception)}", work.Exception);
	}

	private string DescribeOwner(OwnerWork waiting)
	{
		OwnerWork executing = _executingWork;
		int queued;
		lock (_ownerGate) queued = _ownerQueue.Count;
		return $"waiting={waiting?.Name}, waitingStarted={waiting?.Stopwatch.IsRunning}, executing={executing?.Name ?? "none"}, " +
			$"phase={executing?.Phase ?? "none"}, executionMs={executing?.Stopwatch.Elapsed.TotalMilliseconds ?? 0:F3}, " +
			$"dt={executing?.DeltaTime ?? 0:F6}, bodies={executing?.BodyCount ?? 0}, joints={executing?.JointCount ?? 0}, queued={queued}";
	}

	private static string DescribeException(Exception exception)
	{
		if (exception == null) return "unknown failure";
		System.Text.StringBuilder description = new System.Text.StringBuilder();
		int depth = 0;
		for (Exception current = exception; current != null && depth < 8; current = current.InnerException, depth++)
		{
			if (description.Length != 0) description.Append(" -> ");
			description.Append(current.GetType().Name).Append(": ").Append(current.Message);
		}
		return description.ToString();
	}

	private void EnqueueOwner(Action action, [CallerMemberName] string name = null)
	{
		if (_disposed || _ownerThreadUnresponsive) return;
		OwnerWork work = new OwnerWork(() => { action(); return null; }, 0, latchException: true, name: name);
		lock (_ownerGate) { _activity.Changed(); _ownerQueue.Enqueue(work); }
		_ownerSignal.Set();
	}

	private void Log(ExternalLogLevel level, string message) => _options?.Log?.Invoke(level, message);

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

	private sealed class OwnerWork
	{
		internal readonly Func<object> Action;
		internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		internal readonly Stopwatch Stopwatch = new Stopwatch();
		internal readonly long Sequence;
		internal Exception Exception;
		internal NativeApi.BodyState[] StepStates;
		internal ExternalCasingFrame CasingFrame;
		internal readonly bool LatchException;
		internal readonly string Name;
		internal volatile string Phase = "operation";
		internal float DeltaTime;
		internal int BodyCount, JointCount;
		internal int PublishedBodies;
		internal bool AwakeBodies;
		internal long CommandVersion;
		internal OwnerWork(Func<object> action, long sequence, bool latchException = false, [CallerMemberName] string name = null)
		{ Action = action; Sequence = sequence; LatchException = latchException; Name = name; }
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
			if (Work == null || !Work.Completion.Task.IsCompleted) return ExternalBodyCreationStatus.Pending;
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
				_runtime.EnqueueOwner(() => _runtime._scene?.DestroyActor(native.Handle));
				Cancel();
				return ExternalBodyCreationStatus.Failed;
			}
		}

		public void Cancel()
		{
			if (_handedOff || CancelRequested) return;
			CancelRequested = true;
			NativeActor native = Result.Take();
			if (native != null) _runtime.EnqueueOwner(() => _runtime._scene?.DestroyActor(native.Handle));
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
