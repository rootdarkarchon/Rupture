using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

internal interface IMeshGeometrySource : IDisposable
{
	string Name { get; }
	string Status { get; }
	bool IsReady { get; }
	NativeApi.UnitySceneSettings SourceSceneSettings { get; }
	string Failure { get; }
	void BeginRaid();
	void Update();
	void ResetRaid();
	void PrepareProximity(Vector3 point0, Vector3 point1, float radius, IReadOnlyList<Collider> colliders);
	void PrepareAll();
	MeshGeometry Read(MeshCollider collider);
	HeightFieldGeometry Read(TerrainCollider collider);
}

internal sealed class UnityPhysXMeshGeometrySource : IMeshGeometrySource
{
	private const string SupportedUnityVersion = "2022.3.43f1";
	private const float ProbeSlowWarningFixedSeconds = 15f;
	private const float ProbeDiagnosticIntervalSeconds = 10f;
	private const int MaximumProbeDiagnostics = 6;
	private readonly NativeApi _api;
	private readonly int _mainThreadId;
	private readonly int _environmentMask;
	private readonly Action<ExternalLogLevel, string> _log;
	private readonly FieldInfo _actorField;
	private readonly FieldInfo _otherActorField;
	private readonly ResolveShapeToColliderDelegate _resolveShapeToCollider;
	private readonly Dictionary<int, List<IntPtr>> _shapesByCollider = new Dictionary<int, List<IntPtr>>();
	private readonly object _captureGate = new object();
	private GameObject _probeRoot;
	private GameObject _probeDynamicRoot;
	private Rigidbody _probeStationaryBody;
	private Rigidbody _probeBody;
	private SphereCollider _probeStationaryCollider;
	private SphereCollider _probeDynamicCollider;
	private Vector3 _probePosition;
	private IntPtr _capturedActor;
	private IntPtr _unityScene;
	private NativeApi.UnitySceneSettings _sourceSceneSettings;
	private bool _hasSourceSceneSettings;
	private volatile string _failure;
	private int _startedFrame;
	private float _startedFixedTime;
	private float _startedRealtime;
	private float _lastPulsedFixedTime;
	private float _nextDiagnosticRealtime;
	private int _contactCallbacks;
	private int _diagnosticsEmitted;
	private int _ignoredNearbyColliders;
	private int _mappingRefreshes;
	private int _extractedMeshes;
	private int _extractedHeightFields;
	private int _mappedShapes;
	private int _mappingFrame = -1;
	private int _probePulseCount;
	private bool _subscribed;
	private bool _slowWarningLogged;

	private delegate Collider ResolveShapeToColliderDelegate(IntPtr shape);

	internal UnityPhysXMeshGeometrySource(NativeApi api, int environmentMask, Action<ExternalLogLevel, string> log)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		_mainThreadId = Thread.CurrentThread.ManagedThreadId;
		_environmentMask = environmentMask;
		_log = log;
		if (!string.Equals(Application.unityVersion, SupportedUnityVersion, StringComparison.Ordinal))
			throw new NotSupportedException($"Unity PhysX extraction supports Unity {SupportedUnityVersion}; this player reports {Application.unityVersion}.");

		const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
		_actorField = typeof(ModifiableContactPair).GetField("actor", flags);
		_otherActorField = typeof(ModifiableContactPair).GetField("otherActor", flags);
		if (_actorField?.FieldType != typeof(IntPtr) || _otherActorField?.FieldType != typeof(IntPtr))
			throw new MissingFieldException("UnityEngine.ModifiableContactPair native actor fields do not match the validated Unity ABI.");

		MethodInfo resolver = typeof(Physics).GetMethod("ResolveShapeToCollider", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);
		if (resolver == null || resolver.ReturnType != typeof(Collider))
			throw new MissingMethodException("UnityEngine.Physics.ResolveShapeToCollider(IntPtr) is unavailable.");
		_resolveShapeToCollider = (ResolveShapeToColliderDelegate)Delegate.CreateDelegate(typeof(ResolveShapeToColliderDelegate), resolver);
	}

	public string Name => "UnityPhysXExtraction";
	public string Status => _failure != null
		? "failed:" + _failure
		: IsReady
			? $"ready/{_sourceSceneSettings}/mappedShapes:{_mappedShapes}/refreshes:{_mappingRefreshes}/extractedMeshes:{_extractedMeshes}/extractedHeightFields:{_extractedHeightFields}"
			: "waiting-for-default-scene-contact";
	public bool IsReady => _unityScene != IntPtr.Zero && _hasSourceSceneSettings && _failure == null;
	public NativeApi.UnitySceneSettings SourceSceneSettings => IsReady
		? _sourceSceneSettings
		: throw new InvalidOperationException("Unity PhysX source-scene settings are unavailable before extraction bootstrap completes.");
	public string Failure => _failure;

	public void BeginRaid()
	{
		AssertMainThread();
		ResetRaid();
		_startedFrame = Time.frameCount;
		_startedFixedTime = Time.fixedTime;
		_startedRealtime = Time.realtimeSinceStartup;
		_lastPulsedFixedTime = float.NegativeInfinity;
		_nextDiagnosticRealtime = _startedRealtime + ProbeDiagnosticIntervalSeconds;
		Physics.ContactModifyEvent += OnContactModify;
		Physics.ContactModifyEventCCD += OnContactModify;
		_subscribed = true;
		CreateProbe();
		Log(ExternalLogLevel.Info, $"Unity PhysX extraction bootstrap probe created: scene={_probeRoot.scene.name}, position={Format(_probeRoot.transform.position)}, {ProbeStateStatus()}, ignoredNearby={_ignoredNearbyColliders}; waiting for a completed default-scene physics step.");
	}

	public void Update()
	{
		AssertMainThread();
		if (_failure != null || IsReady) return;
		PulseProbeAfterFixedStep();
		IntPtr actor;
		lock (_captureGate) actor = _capturedActor;
		if (actor != IntPtr.Zero)
		{
			try
			{
				_api.Check(_api.GetUnitySceneFromActor(actor, out _unityScene), "rptpx_unity_get_scene_from_actor");
				_sourceSceneSettings = NativeApi.UnitySceneSettings.Create();
				_api.Check(_api.GetUnitySceneSettings(_unityScene, ref _sourceSceneSettings), "rptpx_unity_get_scene_settings");
				_hasSourceSceneSettings = true;
				RefreshShapeMap(syncTransforms: true);
				int contacts = Volatile.Read(ref _contactCallbacks);
				DestroyProbe();
				Log(ExternalLogLevel.Lifecycle, $"Unity PhysX extraction bootstrap ready: {_sourceSceneSettings}, contacts={contacts}, mappedShapes={_mappedShapes}, renderFrames={Time.frameCount - _startedFrame}, fixedSeconds={Time.fixedTime - _startedFixedTime:F2}.");
			}
			catch (Exception exception)
			{
				_failure = exception.GetType().Name + ": " + exception.Message;
				_unityScene = IntPtr.Zero;
				_hasSourceSceneSettings = false;
				Log(ExternalLogLevel.Error, "Unity PhysX extraction native scene traversal failed: " + _failure);
			}
			return;
		}

		float fixedSeconds = Mathf.Max(0f, Time.fixedTime - _startedFixedTime);
		if (!_slowWarningLogged && fixedSeconds >= ProbeSlowWarningFixedSeconds)
		{
			_slowWarningLogged = true;
			Log(ExternalLogLevel.Warning,
				$"Unity PhysX extraction bootstrap is still waiting after {fixedSeconds:F2}s of fixed time; the raid will remain eligible instead of being disabled by a timing-dependent deadline ({ProbeStateStatus()}).");
		}
		MaybeLogPending(fixedSeconds);
	}

	public MeshGeometry Read(MeshCollider collider)
	{
		AssertMainThread();
		if (collider == null) throw new ArgumentNullException(nameof(collider));
		Update();
		if (!IsReady) throw new UnityPhysXShapeUnavailableException(collider, Failure ?? "the Unity PhysX scene bootstrap is not ready");
		int colliderId = collider.GetInstanceID();
		if (!_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> shapes) || shapes.Count == 0)
		{
			RefreshShapeMap(syncTransforms: true);
			if (!_shapesByCollider.TryGetValue(colliderId, out shapes) || shapes.Count == 0)
				throw new UnityPhysXShapeUnavailableException(collider, "Unity has no active native PhysX shape for this MeshCollider");
		}

		foreach (IntPtr shape in shapes)
		{
			NativeApi.UnityMeshInfo info = new NativeApi.UnityMeshInfo { StructSize = (uint)Marshal.SizeOf<NativeApi.UnityMeshInfo>() };
			int query = _api.ExtractUnityMesh(shape, ref info, null, 0, null, 0);
			if (query == NativeApi.InvalidArgument) continue;
			if (query != NativeApi.BufferTooSmall) _api.Check(query, "rptpx_unity_extract_mesh(size)");
			if ((collider.convex && info.GeometryKind != 5) || (!collider.convex && info.GeometryKind != 4)) continue;
			if (info.VertexCount < 3 || info.IndexCount < 3 || info.VertexCount > int.MaxValue || info.IndexCount > int.MaxValue)
				throw new InvalidDataException($"Unity PhysX returned invalid topology counts {info.VertexCount}/{info.IndexCount} for {collider.name}.");

			NativeApi.Vec3[] nativeVertices = new NativeApi.Vec3[info.VertexCount];
			uint[] indices = new uint[info.IndexCount];
			_api.Check(_api.ExtractUnityMesh(shape, ref info, nativeVertices, info.VertexCount, indices, info.IndexCount), "rptpx_unity_extract_mesh");
			Vector3[] vertices = new Vector3[nativeVertices.Length];
			for (int index = 0; index < nativeVertices.Length; index++)
			{
				vertices[index] = nativeVertices[index].ToUnity();
				if (!Finite(vertices[index])) throw new InvalidDataException($"Unity PhysX vertex {index} for {collider.name} is not finite.");
			}
			for (int index = 0; index < indices.Length; index++)
				if (indices[index] >= vertices.Length) throw new InvalidDataException($"Unity PhysX index {index} for {collider.name} is outside {vertices.Length} vertices.");

			_extractedMeshes++;
			return new MeshGeometry
			{
				Vertices = vertices,
				Indices = indices,
				Layout = $"UnityPhysX4.1/postcook/{(info.GeometryKind == 5 ? "convex" : "triangle")}/index:{info.IndexFormat}/meshFlags:{info.MeshFlags}",
				RawHash = MeshGeometry.HashGeometry(vertices, indices),
				Convex = info.GeometryKind == 5,
				CookingOptions = (uint)collider.cookingOptions
			};
		}

		throw new UnityPhysXShapeUnavailableException(collider, $"none of Unity's {shapes.Count} native shapes matched convex={collider.convex}");
	}

	public void PrepareProximity(Vector3 point0, Vector3 point1, float radius, IReadOnlyList<Collider> colliders)
	{
		AssertMainThread();
		Update();
		if (!IsReady) throw new InvalidOperationException(Failure ?? "the Unity PhysX scene bootstrap is not ready");
		if (colliders == null || colliders.Count == 0) return;
		_shapesByCollider.Clear();
		_mappedShapes = 0;

		HashSet<int> requested = new HashSet<int>();
		for (int index = 0; index < colliders.Count; index++)
		{
			Collider collider = colliders[index];
			if (collider is MeshCollider || collider is TerrainCollider) requested.Add(collider.GetInstanceID());
		}
		if (requested.Count == 0) return;
		Physics.SyncTransforms();

		NativeApi.Vec3 nativePoint0 = new NativeApi.Vec3(point0);
		NativeApi.Vec3 nativePoint1 = new NativeApi.Vec3(point1);
		IntPtr[] shapes = new IntPtr[256];
		int query = _api.OverlapUnityShapes(_unityScene, ref nativePoint0, ref nativePoint1, radius,
			shapes, (uint)shapes.Length, out uint writtenOrRequired);
		if (query == NativeApi.BufferTooSmall)
		{
			shapes = new IntPtr[checked((int)writtenOrRequired)];
			_api.Check(_api.OverlapUnityShapes(_unityScene, ref nativePoint0, ref nativePoint1, radius,
				shapes, (uint)shapes.Length, out writtenOrRequired), "rptpx_unity_overlap_shapes");
		}
		else
		{
			_api.Check(query, "rptpx_unity_overlap_shapes");
		}
		if (writtenOrRequired != shapes.Length) Array.Resize(ref shapes, checked((int)writtenOrRequired));

		foreach (IntPtr shape in shapes)
		{
			if (shape == IntPtr.Zero) continue;
			Collider collider = _resolveShapeToCollider(shape);
			if (collider == null || !requested.Contains(collider.GetInstanceID())) continue;
			int colliderId = collider.GetInstanceID();
			if (!_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> mapped))
				_shapesByCollider.Add(colliderId, mapped = new List<IntPtr>(1));
			mapped.Add(shape);
			_mappedShapes++;
		}
		_mappingRefreshes++;
		_mappingFrame = Time.frameCount;
	}

	public void PrepareAll()
	{
		AssertMainThread();
		Update();
		if (!IsReady) throw new InvalidOperationException(Failure ?? "the Unity PhysX scene bootstrap is not ready");
		RefreshShapeMap(syncTransforms: true);
	}

	public HeightFieldGeometry Read(TerrainCollider collider)
	{
		AssertMainThread();
		if (collider == null) throw new ArgumentNullException(nameof(collider));
		Update();
		if (!IsReady) throw new UnityPhysXShapeUnavailableException(collider, Failure ?? "the Unity PhysX scene bootstrap is not ready");

		int colliderId = collider.GetInstanceID();
		if (!_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> shapes) || shapes.Count == 0)
		{
			RefreshShapeMap(syncTransforms: true);
			if (!_shapesByCollider.TryGetValue(colliderId, out shapes) || shapes.Count == 0)
				throw new UnityPhysXShapeUnavailableException(collider, "Unity has no active native PhysX shape for this TerrainCollider");
		}

		foreach (IntPtr shape in shapes)
		{
			NativeApi.UnityHeightFieldInfo info = new NativeApi.UnityHeightFieldInfo { StructSize = (uint)Marshal.SizeOf<NativeApi.UnityHeightFieldInfo>() };
			int query = _api.ExtractUnityHeightField(shape, ref info, null, 0, null, 0);
			if (query == NativeApi.InvalidArgument) continue;
			if (query != NativeApi.BufferTooSmall) _api.Check(query, "rptpx_unity_extract_heightfield(size)");
			if (info.GeometryKind != 6 || info.Rows < 2 || info.Columns < 2 || info.SampleStride < 4 || info.MaterialCount == 0 ||
				info.SampleBytes != checked(info.Rows * info.Columns * info.SampleStride) || info.SampleBytes > int.MaxValue || info.MaterialCount > int.MaxValue)
				throw new InvalidDataException($"Unity PhysX returned invalid heightfield metadata {info.Rows}x{info.Columns}/stride:{info.SampleStride}/bytes:{info.SampleBytes}/materials:{info.MaterialCount} for {collider.name}.");
			if (!Finite(info.WorldPose.Position.ToUnity()) || !Finite(info.WorldPose.Rotation.ToUnity()) ||
				!FinitePositive(info.HeightScale) || !FinitePositive(info.RowScale) || !FinitePositive(info.ColumnScale) ||
				float.IsNaN(info.ConvexEdgeThreshold) || float.IsInfinity(info.ConvexEdgeThreshold) || info.ConvexEdgeThreshold < 0f)
				throw new InvalidDataException($"Unity PhysX returned invalid heightfield pose or scale for {collider.name}.");

			byte[] samples = new byte[checked((int)info.SampleBytes)];
			NativeApi.MaterialDesc[] materials = new NativeApi.MaterialDesc[checked((int)info.MaterialCount)];
			_api.Check(_api.ExtractUnityHeightField(shape, ref info, samples, info.SampleBytes, materials, info.MaterialCount), "rptpx_unity_extract_heightfield");
			_extractedHeightFields++;
			Log(ExternalLogLevel.Info,
				$"Unity PhysX heightfield extracted: collider={collider.name}, samples={info.Rows}x{info.Columns}, bytes={info.SampleBytes}, materials={info.MaterialCount}, " +
				$"scale=height:{info.HeightScale:R}/row:{info.RowScale:R}/column:{info.ColumnScale:R}, identity=0x{info.HeightFieldIdentity:X16}, timestamp={info.Timestamp}.");
			return new HeightFieldGeometry
			{
				Samples = samples,
				Materials = materials,
				Rows = info.Rows,
				Columns = info.Columns,
				SampleStride = info.SampleStride,
				NativeIdentity = info.HeightFieldIdentity,
				Timestamp = info.Timestamp,
				WorldPose = info.WorldPose,
				HeightScale = info.HeightScale,
				RowScale = info.RowScale,
				ColumnScale = info.ColumnScale,
				ConvexEdgeThreshold = info.ConvexEdgeThreshold,
				HeightFieldFlags = info.HeightFieldFlags,
				GeometryFlags = info.GeometryFlags
			};
		}

		throw new UnityPhysXShapeUnavailableException(collider, $"none of Unity's {shapes.Count} native shapes was a heightfield");
	}

	public void ResetRaid()
	{
		AssertMainThread();
		DestroyProbe();
		lock (_captureGate) _capturedActor = IntPtr.Zero;
		_unityScene = IntPtr.Zero;
		_sourceSceneSettings = default;
		_hasSourceSceneSettings = false;
		_failure = null;
		_shapesByCollider.Clear();
		_mappingRefreshes = 0;
		_extractedMeshes = 0;
		_extractedHeightFields = 0;
		_mappedShapes = 0;
		_mappingFrame = -1;
		_contactCallbacks = 0;
		_diagnosticsEmitted = 0;
		_ignoredNearbyColliders = 0;
		_probePulseCount = 0;
		_slowWarningLogged = false;
	}

	public void Dispose() => ResetRaid();

	private void DestroyProbe()
	{
		if (_subscribed)
		{
			Physics.ContactModifyEvent -= OnContactModify;
			Physics.ContactModifyEventCCD -= OnContactModify;
			_subscribed = false;
		}
		if (_probeRoot != null) UnityEngine.Object.Destroy(_probeRoot);
		if (_probeDynamicRoot != null) UnityEngine.Object.Destroy(_probeDynamicRoot);
		_probeRoot = null;
		_probeDynamicRoot = null;
		_probeStationaryBody = null;
		_probeBody = null;
		_probeStationaryCollider = null;
		_probeDynamicCollider = null;
	}

	private void RefreshShapeMap(bool syncTransforms)
	{
		if (_unityScene == IntPtr.Zero) throw new InvalidOperationException("Unity PhysX scene pointer is unavailable.");
		if (syncTransforms) Physics.SyncTransforms();
		int query = _api.EnumerateUnityShapes(_unityScene, null, 0, out uint required);
		if (query != NativeApi.BufferTooSmall && !(query == NativeApi.Ok && required == 0)) _api.Check(query, "rptpx_unity_enumerate_shapes(size)");
		IntPtr[] shapes = new IntPtr[required];
		if (required != 0)
		{
			_api.Check(_api.EnumerateUnityShapes(_unityScene, shapes, required, out uint written), "rptpx_unity_enumerate_shapes");
			if (written != required) Array.Resize(ref shapes, checked((int)written));
		}

		_shapesByCollider.Clear();
		_mappedShapes = 0;
		foreach (IntPtr shape in shapes)
		{
			if (shape == IntPtr.Zero) continue;
			Collider collider = _resolveShapeToCollider(shape);
			if (collider == null) continue;
			int colliderId = collider.GetInstanceID();
			if (!_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> mapped))
				_shapesByCollider.Add(colliderId, mapped = new List<IntPtr>(1));
			mapped.Add(shape);
			_mappedShapes++;
		}
		_mappingRefreshes++;
		_mappingFrame = Time.frameCount;
	}

	private void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
	{
		if (!scene.Equals(Physics.defaultPhysicsScene) || pairs.Length == 0) return;
		Interlocked.Increment(ref _contactCallbacks);
		try
		{
			ModifiableContactPair pair = pairs[0];
			IntPtr actor = (IntPtr)_actorField.GetValue(pair);
			if (actor == IntPtr.Zero) actor = (IntPtr)_otherActorField.GetValue(pair);
			if (actor == IntPtr.Zero) return;
			lock (_captureGate)
				if (_capturedActor == IntPtr.Zero) _capturedActor = actor;
		}
		catch (Exception exception)
		{
			lock (_captureGate)
				if (_failure == null) _failure = "Contact bootstrap failed: " + exception.GetType().Name + ": " + exception.Message;
		}
	}

	private void CreateProbe()
	{
		_probeRoot = new GameObject("__RPT_UnityPhysXGeometryProbe_Stationary") { hideFlags = HideFlags.HideAndDontSave };
		_probeDynamicRoot = new GameObject("__RPT_UnityPhysXGeometryProbe_Dynamic") { hideFlags = HideFlags.HideAndDontSave };
		Scene targetScene = SceneManager.GetActiveScene();
		for (int index = 0; index < SceneManager.sceneCount; index++)
		{
			Scene candidate = SceneManager.GetSceneAt(index);
			if (candidate.IsValid() && candidate.isLoaded && candidate.GetPhysicsScene().Equals(Physics.defaultPhysicsScene))
			{
				targetScene = candidate;
				break;
			}
		}
		if (targetScene.IsValid() && targetScene.isLoaded)
		{
			SceneManager.MoveGameObjectToScene(_probeRoot, targetScene);
			SceneManager.MoveGameObjectToScene(_probeDynamicRoot, targetScene);
		}
		_probePosition = FindProbePosition();
		_probeRoot.transform.position = _probePosition;
		_probeDynamicRoot.transform.position = _probePosition + new Vector3(0.4f, 0f, 0f);
		int layer = FindSelfCollidingLayer(_environmentMask);
		_probeRoot.layer = layer;
		_probeDynamicRoot.layer = layer;

		_probeStationaryCollider = _probeRoot.AddComponent<SphereCollider>();
		_probeStationaryCollider.radius = 0.5f;
		_probeStationaryCollider.hasModifiableContacts = true;
		_probeStationaryCollider.providesContacts = true;
		_probeStationaryBody = _probeRoot.AddComponent<Rigidbody>();
		_probeStationaryBody.useGravity = false;
		_probeStationaryBody.isKinematic = true;
		_probeStationaryBody.detectCollisions = true;

		_probeDynamicCollider = _probeDynamicRoot.AddComponent<SphereCollider>();
		_probeDynamicCollider.radius = 0.5f;
		_probeDynamicCollider.hasModifiableContacts = true;
		_probeDynamicCollider.providesContacts = true;
		_probeBody = _probeDynamicRoot.AddComponent<Rigidbody>();
		_probeBody.useGravity = false;
		_probeBody.isKinematic = false;
		_probeBody.detectCollisions = true;
		_probeBody.sleepThreshold = 0f;
		_probeBody.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezePositionZ |
			RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;
		_probeBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
		_probeBody.WakeUp();
		Physics.IgnoreCollision(_probeStationaryCollider, _probeDynamicCollider, false);
		Physics.SyncTransforms();
		IgnoreNearbyEnvironmentColliders();
	}

	private void IgnoreNearbyEnvironmentColliders()
	{
		Collider[] nearby = Physics.OverlapSphere(_probeRoot.transform.position, 1.25f, Physics.AllLayers, QueryTriggerInteraction.Collide);
		foreach (Collider collider in nearby)
		{
			if (collider == null || collider == _probeStationaryCollider || collider == _probeDynamicCollider) continue;
			Physics.IgnoreCollision(_probeStationaryCollider, collider, true);
			Physics.IgnoreCollision(_probeDynamicCollider, collider, true);
			_ignoredNearbyColliders++;
		}
	}

	private void PulseProbeAfterFixedStep()
	{
		if (_probeBody == null || Time.fixedTime <= _lastPulsedFixedTime) return;
		_lastPulsedFixedTime = Time.fixedTime;
		// Alternate a separated step with a penetrating step. This forces PhysX to
		// repeatedly destroy and recreate the pair instead of relying on a single
		// persistent overlap surviving headless raid initialization.
		_probePulseCount++;
		bool overlapStep = (_probePulseCount & 1) == 0;
		_probeBody.position = _probePosition + new Vector3(overlapStep ? 0.4f : 1.25f, 0f, 0f);
		_probeBody.rotation = Quaternion.identity;
		_probeBody.velocity = overlapStep ? new Vector3(-0.05f, 0f, 0f) : Vector3.zero;
		_probeBody.angularVelocity = Vector3.zero;
		_probeBody.WakeUp();
		Physics.IgnoreCollision(_probeStationaryCollider, _probeDynamicCollider, false);
		Physics.SyncTransforms();
	}

	private void MaybeLogPending(float fixedSeconds)
	{
		float realtime = Time.realtimeSinceStartup;
		if (_diagnosticsEmitted >= MaximumProbeDiagnostics || realtime < _nextDiagnosticRealtime) return;
		_diagnosticsEmitted++;
		_nextDiagnosticRealtime = realtime + ProbeDiagnosticIntervalSeconds;
		Log(ExternalLogLevel.Info, $"Unity PhysX extraction bootstrap pending: renderFrames={Time.frameCount - _startedFrame}, fixedSeconds={fixedSeconds:F2}, realtimeSeconds={realtime - _startedRealtime:F1}, callbacks={Volatile.Read(ref _contactCallbacks)}, {ProbeStateStatus()}.");
	}

	private static int FindSelfCollidingLayer(int excludedMask)
	{
		for (int layer = 31; layer >= 0; layer--)
			if ((excludedMask & 1 << layer) == 0 && !Physics.GetIgnoreLayerCollision(layer, layer)) return layer;
		for (int layer = 31; layer >= 0; layer--)
			if (!Physics.GetIgnoreLayerCollision(layer, layer)) return layer;
		return 0;
	}

	private static Vector3 FindProbePosition()
	{
		Camera camera = Camera.main;
		// A live camera is necessarily inside the raid's useful physics region.
		// Keeping the hidden pair here avoids dropping it outside a bounded MBP
		// broadphase; nearby gameplay colliders are explicitly ignored below.
		if (camera != null) return camera.transform.position;
		for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
		{
			Scene scene = SceneManager.GetSceneAt(sceneIndex);
			if (!scene.IsValid() || !scene.isLoaded || !scene.GetPhysicsScene().Equals(Physics.defaultPhysicsScene)) continue;
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				Collider collider = root.GetComponentInChildren<Collider>(false);
				if (collider != null && collider.enabled)
					return collider.bounds.center;
			}
		}
		return Vector3.zero;
	}

	private string ProbeOverlapStatus()
	{
		if (_probeStationaryCollider == null || _probeDynamicCollider == null) return "unavailable";
		bool overlapping = Physics.ComputePenetration(
			_probeStationaryCollider, _probeStationaryCollider.transform.position, _probeStationaryCollider.transform.rotation,
			_probeDynamicCollider, _probeDynamicCollider.transform.position, _probeDynamicCollider.transform.rotation,
			out _, out float distance);
		return overlapping ? $"overlap:{distance:F3}m" : "separated";
	}

	private string ProbeStateStatus()
	{
		int stationaryLayer = _probeRoot != null ? _probeRoot.layer : -1;
		int dynamicLayer = _probeDynamicRoot != null ? _probeDynamicRoot.layer : -1;
		bool layerIgnored = stationaryLayer < 0 || dynamicLayer < 0 || Physics.GetIgnoreLayerCollision(stationaryLayer, dynamicLayer);
		bool pairIgnored = _probeStationaryCollider == null || _probeDynamicCollider == null ||
			Physics.GetIgnoreCollision(_probeStationaryCollider, _probeDynamicCollider);
		return $"pair={ProbeOverlapStatus()}, layers={stationaryLayer}/{dynamicLayer}/ignored:{layerIgnored}, pairIgnored={pairIgnored}, " +
			$"simulationMode={Physics.simulationMode}, invokeCallbacks={Physics.invokeCollisionCallbacks}, timeScale={Time.timeScale:F2}, " +
			$"probeActive={(_probeRoot != null && _probeRoot.activeInHierarchy && _probeDynamicRoot != null && _probeDynamicRoot.activeInHierarchy)}, probeAwake={(_probeBody != null && !_probeBody.IsSleeping())}";
	}

	private void AssertMainThread()
	{
		if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
			throw new InvalidOperationException("Unity PhysX geometry extraction must run on the Unity main thread between simulation steps.");
	}

	private void Log(ExternalLogLevel level, string message) => _log?.Invoke(level, message);

	private static string Format(Vector3 value) => $"({value.x:F2},{value.y:F2},{value.z:F2})";

	private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
	private static bool Finite(Quaternion value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z) && !float.IsNaN(value.w) && !float.IsInfinity(value.w);
	private static bool FinitePositive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
}

internal sealed class UnityPhysXShapeUnavailableException : InvalidOperationException
{
	internal UnityPhysXShapeUnavailableException(Collider collider, string reason)
		: base($"Unity PhysX shape extraction is unavailable for {collider?.GetType().Name ?? "Collider"} {collider?.name ?? "<destroyed>"}: {reason}.") { }
}
