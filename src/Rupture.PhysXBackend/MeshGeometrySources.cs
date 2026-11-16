using System;
using System.Collections.Generic;
using System.Diagnostics;
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
	/// <summary>Retires every negative "no native shape" entry without remapping anything.</summary>
	void ClearNoShapeCache();
	MeshGeometry Read(MeshCollider collider);
	HeightFieldGeometry Read(TerrainCollider collider);
	/// <summary>Whole-scene shape remaps this raid.</summary>
	long ShapeMapRefreshes { get; }
	/// <summary>Bounds-limited overlap mappings this raid.</summary>
	long TargetedShapeMappings { get; }
	/// <summary>Unity-cooked meshes copied to managed memory this raid.</summary>
	long MeshExtractions { get; }
	/// <summary>Whether <c>Physics.SyncTransforms</c> already ran this frame, so a mapping needs no further sync.</summary>
	bool TransformsSyncedThisFrame { get; }
	/// <summary>Peak-held cost of one <c>Physics.SyncTransforms</c> in stopwatch ticks.</summary>
	long SyncTransformsEstimateTicks { get; }
	/// <summary>Cumulative stopwatch ticks spent in <c>Physics.SyncTransforms</c> this raid; a slice subtracts its delta to isolate its own cost.</summary>
	long SyncTransformsTotalTicks { get; }
	/// <summary>Longest single <c>Physics.SyncTransforms</c> this raid, in milliseconds.</summary>
	double SyncTransformsMaxMilliseconds { get; }
}

internal sealed class UnityPhysXMeshGeometrySource : IMeshGeometrySource
{
	private const string SupportedUnityVersion = "2022.3.43f1";
	private const float ProbeSlowWarningFixedSeconds = 15f;
	private const float ProbeDiagnosticIntervalSeconds = 10f;
	private const int MaximumProbeDiagnostics = 6;
	private const int MaximumScheduledProbeUpdates = 120;
	private const int InitialOverlapCapacity = 256;
	private const int MaximumPooledLists = 4096;
	// Covers pose lag between Physics.SyncTransforms and the query inside one frame.
	private const float TargetedMappingPadding = 0.25f;
	// Unity shape-creation rules outside NoShapeSignature (cooking failures, engine-internal state) are re-checked after this many frames.
	private const int NoShapeRetryFrames = 300;
	// A sphere centered on the terrain surface always overlaps its heightfield; the radius covers the gap between the
	// interpolated surface height and the cooked triangulation.
	private const float TerrainProbeRadius = 1.25f;
	// Probe points as fractions of the terrain's world half-extents: the center first, then the four quarter points in
	// case a terrain hole sits under the center.
	private static readonly Vector2[] TerrainProbeOffsets =
	{
		Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f), new Vector2(0.5f, -0.5f), new Vector2(-0.5f, -0.5f)
	};
	private static readonly uint MeshInfoSize = (uint)Marshal.SizeOf<NativeApi.UnityMeshInfo>();
	private static readonly uint HeightFieldInfoSize = (uint)Marshal.SizeOf<NativeApi.UnityHeightFieldInfo>();
	private readonly NativeApi _api;
	private readonly int _mainThreadId;
	private readonly int _environmentMask;
	private readonly Action<ExternalLogLevel, string> _log;
	private readonly Func<Rigidbody, IDisposable> _scheduleProbe;
	private IDisposable _probeSimulationLease;
	private int _scheduledProbeUpdates;
	private int _lastScheduledProbeUpdateFrame = -1;
	private readonly FieldInfo _actorField;
	private readonly FieldInfo _otherActorField;
	private readonly ResolveShapeToColliderDelegate _resolveShapeToCollider;
	/// <summary>Native shapes of mesh and terrain colliders mapped in <see cref="_mappingFrame"/>; see <see cref="BeginMappingFrame"/> for the staleness rule.</summary>
	private readonly Dictionary<int, List<IntPtr>> _shapesByCollider = new Dictionary<int, List<IntPtr>>();
	private readonly Stack<List<IntPtr>> _listPool = new Stack<List<IntPtr>>();
	/// <summary>
	/// Enabled colliders Unity has no native shape for, keyed by instance id. An entry is trusted only while the collider's
	/// <see cref="NoShapeSignature"/> is unchanged and before it expires; <see cref="PrepareAll"/> and <see cref="ResetRaid"/> clear it.
	/// </summary>
	private readonly Dictionary<int, NoShapeSignature> _noShapeColliders = new Dictionary<int, NoShapeSignature>();
	private readonly Dictionary<ulong, string> _layoutByKey = new Dictionary<ulong, string>();
	private IntPtr[] _overlapScratch = new IntPtr[InitialOverlapCapacity];
	private IntPtr[] _enumerateScratch = Array.Empty<IntPtr>();
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
	private long _targetedMappings;
	private int _extractedMeshes;
	private int _extractedHeightFields;
	private int _mappedShapes;
	private int _mappingFrame = -1;
	private int _syncedFrame = -1;
	private PeakCost _syncCost;
	private long _syncTotalTicks, _syncMaxTicks, _syncCount;
	private int _probePulseCount;
	private bool _subscribed;
	private bool _slowWarningLogged;

	private delegate Collider ResolveShapeToColliderDelegate(IntPtr shape);

	internal UnityPhysXMeshGeometrySource(NativeApi api, int environmentMask, Action<ExternalLogLevel, string> log,
		Func<Rigidbody, IDisposable> scheduleProbe = null)
	{
		_api = api ?? throw new ArgumentNullException(nameof(api));
		_mainThreadId = Thread.CurrentThread.ManagedThreadId;
		_environmentMask = environmentMask;
		_log = log;
		_scheduleProbe = scheduleProbe;
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
			? $"ready/{_sourceSceneSettings}/mappedShapes:{_mappedShapes}/refreshes:{_mappingRefreshes}/targeted:{_targetedMappings}/noShapeCached:{_noShapeColliders.Count}/extractedMeshes:{_extractedMeshes}/extractedHeightFields:{_extractedHeightFields}/syncs:{_syncCount}/syncMaxMs:{SyncTransformsMaxMilliseconds:F2}"
			: "waiting-for-default-scene-contact";
	public long ShapeMapRefreshes => _mappingRefreshes;
	public long TargetedShapeMappings => _targetedMappings;
	public long MeshExtractions => _extractedMeshes;
	public bool TransformsSyncedThisFrame => _syncedFrame == Time.frameCount;
	public long SyncTransformsEstimateTicks => _syncCost.Ticks;
	public long SyncTransformsTotalTicks => _syncTotalTicks;
	public double SyncTransformsMaxMilliseconds => _syncMaxTicks * 1000d / Stopwatch.Frequency;
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
		// Give the initial overlapping pair a simulation opportunity before the
		// retry pulse separates it. Render updates can precede the first fixed tick.
		_lastPulsedFixedTime = Time.fixedTime;
		_nextDiagnosticRealtime = _startedRealtime + ProbeDiagnosticIntervalSeconds;
		Physics.ContactModifyEvent += OnContactModify;
		Physics.ContactModifyEventCCD += OnContactModify;
		_subscribed = true;
		try
		{
			CreateProbe();
			ScheduleProbe();
			Log(ExternalLogLevel.Info, $"Unity PhysX extraction bootstrap probe created: scene={_probeRoot.scene.name}, position={Format(_probeRoot.transform.position)}, {ProbeStateStatus()}, ignoredNearby={_ignoredNearbyColliders}; waiting for a completed default-scene physics step.");
		}
		catch
		{
			DestroyProbe();
			throw;
		}
	}

	public void Update()
	{
		AssertMainThread();
		if (_failure != null) { DestroyProbe(); return; }
		if (IsReady) return;
		try { ScheduleProbe(); }
		catch (Exception exception)
		{
			_failure = "Probe scheduling failed: " + exception.Message;
			DestroyProbe();
			return;
		}
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
				// Nothing is mapped here: shape pointers live for one frame (see BeginMappingFrame), so a bootstrap map would be
				// dropped before its first consumer. PrepareProximity, Read and PrepareAll map what they need when they need it.
				int contacts = Volatile.Read(ref _contactCallbacks);
				DestroyProbe();
				Log(ExternalLogLevel.Lifecycle, $"Unity PhysX extraction bootstrap ready: {_sourceSceneSettings}, contacts={contacts}, renderFrames={Time.frameCount - _startedFrame}, fixedSeconds={Time.fixedTime - _startedFixedTime:F2}.");
			}
			catch (Exception exception)
			{
				_failure = exception.GetType().Name + ": " + exception.Message;
				_unityScene = IntPtr.Zero;
				_hasSourceSceneSettings = false;
				Log(ExternalLogLevel.Error, "Unity PhysX extraction native scene traversal failed: " + _failure);
			}
			finally { DestroyProbe(); }
			return;
		}
		// Bound our support entry even if a broken callback/scheduler contract never
		// produces contacts. Paused frames do not consume simulation opportunities.
		if (_probeSimulationLease != null && Time.deltaTime > 0f && Time.frameCount != _lastScheduledProbeUpdateFrame)
		{
			_lastScheduledProbeUpdateFrame = Time.frameCount;
			if (++_scheduledProbeUpdates >= MaximumScheduledProbeUpdates)
			{
				_failure = $"The scheduled Unity PhysX bootstrap probe produced no contact after {_scheduledProbeUpdates} updates.";
				DestroyProbe();
				Log(ExternalLogLevel.Error, _failure);
				return;
			}
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
		// Unity simulates no shape for a disabled or inactive collider, so there is nothing to map; seeing it disabled
		// also retires a stale negative entry so the collider is retried once it is enabled again.
		if (!collider.enabled || !collider.gameObject.activeInHierarchy)
		{
			_noShapeColliders.Remove(colliderId);
			throw new UnityPhysXShapeUnavailableException(collider, "the MeshCollider is disabled or inactive, so Unity simulates no native shape for it", noNativeShape: true);
		}

		List<IntPtr> shapes = ResolveShapes(collider, colliderId);
		if (shapes == null)
			throw new UnityPhysXShapeUnavailableException(collider, "Unity has no active native PhysX shape for this MeshCollider", noNativeShape: true);

		foreach (IntPtr shape in shapes)
		{
			NativeApi.UnityMeshInfo info = new NativeApi.UnityMeshInfo { StructSize = MeshInfoSize };
			int query = _api.ExtractUnityMesh(shape, ref info, null, 0, null, 0);
			if (query == NativeApi.InvalidArgument) continue;
			if (query != NativeApi.BufferTooSmall) _api.Check(query, "rptpx_unity_extract_mesh(size)");
			if ((collider.convex && info.GeometryKind != 5) || (!collider.convex && info.GeometryKind != 4)) continue;
			if (info.VertexCount < 3 || info.IndexCount < 3 || info.VertexCount > int.MaxValue || info.IndexCount > int.MaxValue)
				throw new InvalidDataException($"Unity PhysX returned invalid topology counts {info.VertexCount}/{info.IndexCount} for {collider.name}.");

			Vector3[] vertices = new Vector3[info.VertexCount];
			uint[] indices = new uint[info.IndexCount];
			ExtractMesh(shape, ref info, vertices, indices);
			for (int index = 0; index < vertices.Length; index++)
				if (!Finite(vertices[index])) throw new InvalidDataException($"Unity PhysX vertex {index} for {collider.name} is not finite.");
			for (int index = 0; index < indices.Length; index++)
				if (indices[index] >= vertices.Length) throw new InvalidDataException($"Unity PhysX index {index} for {collider.name} is outside {vertices.Length} vertices.");

			_extractedMeshes++;
			return new MeshGeometry
			{
				Vertices = vertices,
				Indices = indices,
				Layout = LayoutOf(ref info),
				RawHash = MeshGeometry.HashGeometry(vertices, indices),
				Convex = info.GeometryKind == 5,
				CookingOptions = (uint)collider.cookingOptions
			};
		}

		throw new UnityPhysXShapeUnavailableException(collider, $"none of Unity's {shapes.Count} native shapes matched convex={collider.convex}");
	}

	/// <summary>
	/// Copies the post-cook topology into the managed arrays. Vector3 and RptPxVec3 share one verified 12-byte layout,
	/// so the vertices land straight in Vector3[]; the intermediate Vec3[] copy remains only for a failed layout probe.
	/// </summary>
	private void ExtractMesh(IntPtr shape, ref NativeApi.UnityMeshInfo info, Vector3[] vertices, uint[] indices)
	{
		if (_api.ExtractUnityMeshVectors != null)
		{
			_api.Check(_api.ExtractUnityMeshVectors(shape, ref info, vertices, info.VertexCount, indices, info.IndexCount), "rptpx_unity_extract_mesh");
			return;
		}

		NativeApi.Vec3[] nativeVertices = new NativeApi.Vec3[vertices.Length];
		_api.Check(_api.ExtractUnityMesh(shape, ref info, nativeVertices, info.VertexCount, indices, info.IndexCount), "rptpx_unity_extract_mesh");
		for (int index = 0; index < nativeVertices.Length; index++) vertices[index] = nativeVertices[index].ToUnity();
	}

	/// <summary>The cooking-identity layout text, built once per (geometry kind, index format, mesh flags) instead of per extraction.</summary>
	private string LayoutOf(ref NativeApi.UnityMeshInfo info)
	{
		bool convex = info.GeometryKind == 5;
		ulong key = (convex ? 1UL << 63 : 0UL) | ((ulong)info.IndexFormat << 32) | info.MeshFlags;
		if (_layoutByKey.TryGetValue(key, out string layout)) return layout;
		layout = $"UnityPhysX4.1/postcook/{(convex ? "convex" : "triangle")}/index:{info.IndexFormat}/meshFlags:{info.MeshFlags}";
		_layoutByKey.Add(key, layout);
		return layout;
	}

	public void PrepareProximity(Vector3 point0, Vector3 point1, float radius, IReadOnlyList<Collider> colliders)
	{
		AssertMainThread();
		Update();
		if (!IsReady) throw new InvalidOperationException(Failure ?? "the Unity PhysX scene bootstrap is not ready");
		if (colliders == null || colliders.Count == 0) return;
		BeginMappingFrame();

		// Merge into the frame map; a corridor whose mesh and terrain colliders are all mapped (or known to have no
		// native shape) costs no overlap and no transform sync.
		bool unmapped = false;
		for (int index = 0; index < colliders.Count && !unmapped; index++) unmapped = NeedsMapping(colliders[index]);
		if (!unmapped) return;

		SyncTransformsOncePerFrame();
		OverlapAndMerge(point0, point1, radius);
		_targetedMappings++;
	}

	public void PrepareAll()
	{
		AssertMainThread();
		Update();
		if (!IsReady) throw new InvalidOperationException(Failure ?? "the Unity PhysX scene bootstrap is not ready");
		_noShapeColliders.Clear();
		RefreshShapeMap(syncTransforms: true);
	}

	/// <summary>
	/// Retires the negative shape cache without the whole-scene remap <see cref="PrepareAll"/> performs. A staged late-scene
	/// capture maps each collider on demand across frames, so it only needs entries that predate the scene dropped.
	/// </summary>
	public void ClearNoShapeCache()
	{
		AssertMainThread();
		_noShapeColliders.Clear();
	}

	public HeightFieldGeometry Read(TerrainCollider collider)
	{
		AssertMainThread();
		if (collider == null) throw new ArgumentNullException(nameof(collider));
		Update();
		if (!IsReady) throw new UnityPhysXShapeUnavailableException(collider, Failure ?? "the Unity PhysX scene bootstrap is not ready");

		int colliderId = collider.GetInstanceID();
		if (!collider.enabled || !collider.gameObject.activeInHierarchy)
		{
			_noShapeColliders.Remove(colliderId);
			throw new UnityPhysXShapeUnavailableException(collider, "the TerrainCollider is disabled or inactive, so Unity simulates no native shape for it", noNativeShape: true);
		}
		List<IntPtr> shapes = ResolveShapes(collider, colliderId);
		if (shapes == null)
			throw new UnityPhysXShapeUnavailableException(collider, "Unity has no active native PhysX shape for this TerrainCollider");

		foreach (IntPtr shape in shapes)
		{
			NativeApi.UnityHeightFieldInfo info = new NativeApi.UnityHeightFieldInfo { StructSize = HeightFieldInfoSize };
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
		ClearShapeMap();
		_noShapeColliders.Clear();
		_enumerateScratch = Array.Empty<IntPtr>();
		_mappingRefreshes = 0;
		_targetedMappings = 0;
		_extractedMeshes = 0;
		_extractedHeightFields = 0;
		_mappingFrame = -1;
		_syncedFrame = -1;
		_syncCost.Reset();
		_syncTotalTicks = _syncMaxTicks = _syncCount = 0L;
		_contactCallbacks = 0;
		_diagnosticsEmitted = 0;
		_ignoredNearbyColliders = 0;
		_probePulseCount = 0;
		_scheduledProbeUpdates = 0;
		_lastScheduledProbeUpdateFrame = -1;
		_slowWarningLogged = false;
	}

	public void Dispose() => ResetRaid();

	private void DestroyProbe()
	{
		IDisposable lease = _probeSimulationLease;
		_probeSimulationLease = null;
		try { lease?.Dispose(); }
		finally { DestroyProbeObjects(); }
	}

	private void DestroyProbeObjects()
	{
		if (_subscribed)
		{
			Physics.ContactModifyEvent -= OnContactModify;
			Physics.ContactModifyEventCCD -= OnContactModify;
			_subscribed = false;
		}
		// Destroy is deferred: disable immediately so no live probe remains this frame.
		if (_probeRoot != null) { _probeRoot.SetActive(false); UnityEngine.Object.Destroy(_probeRoot); }
		if (_probeDynamicRoot != null) { _probeDynamicRoot.SetActive(false); UnityEngine.Object.Destroy(_probeDynamicRoot); }
		_probeRoot = null;
		_probeDynamicRoot = null;
		_probeStationaryBody = null;
		_probeBody = null;
		_probeStationaryCollider = null;
		_probeDynamicCollider = null;
	}

	private void ScheduleProbe()
	{
		if (_probeSimulationLease == null && _probeBody != null)
			_probeSimulationLease = _scheduleProbe?.Invoke(_probeBody);
	}

	/// <summary>
	/// Whole-scene remap of every Unity shape (~125k on a raid map). Only <see cref="PrepareAll"/> may call it; corpse
	/// builds and proximity captures map targeted through <see cref="ResolveShapes"/> and <see cref="OverlapAndMerge"/> instead.
	/// </summary>
	private void RefreshShapeMap(bool syncTransforms)
	{
		if (_unityScene == IntPtr.Zero) throw new InvalidOperationException("Unity PhysX scene pointer is unavailable.");
		if (syncTransforms)
		{
			SyncTransformsNow();
		}
		int query = _api.EnumerateUnityShapes(_unityScene, null, 0, out uint required);
		if (query != NativeApi.BufferTooSmall && !(query == NativeApi.Ok && required == 0)) _api.Check(query, "rptpx_unity_enumerate_shapes(size)");
		if (_enumerateScratch.Length < required) _enumerateScratch = new IntPtr[required];
		uint written = 0;
		if (required != 0)
			_api.Check(_api.EnumerateUnityShapes(_unityScene, _enumerateScratch, required, out written), "rptpx_unity_enumerate_shapes");

		ClearShapeMap();
		for (uint index = 0; index < written; index++) MergeShape(_enumerateScratch[index]);
		_mappingRefreshes++;
		_mappingFrame = Time.frameCount;
	}

	/// <summary>
	/// Native shape pointers are valid only for the frame that mapped them: the first touch of a new
	/// <see cref="Time.frameCount"/> drops the whole map. Every consumer extracts immediately after mapping, in the same
	/// synchronous call chain, so nothing legitimate needs a pointer to outlive its frame, while a pointer kept across
	/// frames could reference a PxShape Unity has since released (collider disabled, destroyed or rebuilt). Within a
	/// frame the map only grows, and an enabled/active check precedes every lookup so a shape Unity released
	/// mid-frame by disabling its collider is never dereferenced.
	/// </summary>
	private void BeginMappingFrame()
	{
		int frame = Time.frameCount;
		if (_mappingFrame == frame) return;
		ClearShapeMap();
		_mappingFrame = frame;
	}

	private void ClearShapeMap()
	{
		foreach (KeyValuePair<int, List<IntPtr>> entry in _shapesByCollider) ReturnList(entry.Value);
		_shapesByCollider.Clear();
		_mappedShapes = 0;
	}

	private List<IntPtr> RentList() => _listPool.Count != 0 ? _listPool.Pop() : new List<IntPtr>(2);

	private void ReturnList(List<IntPtr> list)
	{
		list.Clear();
		if (_listPool.Count < MaximumPooledLists) _listPool.Push(list);
	}

	/// <summary>One Physics.SyncTransforms per frame, and only once a mapping actually needs native poses.</summary>
	private void SyncTransformsOncePerFrame()
	{
		if (_syncedFrame == Time.frameCount) return;
		SyncTransformsNow();
	}

	/// <summary>Runs and times <c>Physics.SyncTransforms</c>; the cost scales with the transforms that moved, so it is measured instead of assumed.</summary>
	private void SyncTransformsNow()
	{
		long started = Stopwatch.GetTimestamp();
		Physics.SyncTransforms();
		long elapsed = Stopwatch.GetTimestamp() - started;
		_syncedFrame = Time.frameCount;
		_syncTotalTicks += elapsed;
		_syncCount++;
		if (elapsed > _syncMaxTicks) _syncMaxTicks = elapsed;
		_syncCost.Observe(elapsed);
	}

	/// <summary>
	/// The native shapes Unity simulates for a collider in this frame, or null when it has none. Shapes already mapped this
	/// frame win over a negative entry (a proximity overlap or <see cref="PrepareAll"/> may have found a shape the entry
	/// predates); otherwise a still-valid negative entry answers without any query, and only then does a targeted overlap
	/// run. Never remaps the whole scene.
	/// </summary>
	private List<IntPtr> ResolveShapes(Collider collider, int colliderId)
	{
		BeginMappingFrame();
		if (TryGetMapped(colliderId, out List<IntPtr> shapes)) return shapes;
		NoShapeSignature signature = new NoShapeSignature(collider);
		if (IsKnownNoShape(colliderId, signature)) return null;
		MapTargeted(collider, colliderId);
		if (TryGetMapped(colliderId, out shapes)) return shapes;
		_noShapeColliders[colliderId] = signature.ExpiringAt(Time.frameCount + NoShapeRetryFrames);
		return null;
	}

	private bool TryGetMapped(int colliderId, out List<IntPtr> shapes)
	{
		if (!_shapesByCollider.TryGetValue(colliderId, out shapes) || shapes.Count == 0) return false;
		if (_noShapeColliders.Count != 0) _noShapeColliders.Remove(colliderId);
		return true;
	}

	/// <summary>
	/// Maps one collider through a scene overlap. The native bridge offers no per-actor shape lookup (only
	/// rptpx_unity_enumerate_shapes for the whole scene and rptpx_unity_overlap_shapes, and the native ABI is fixed), so a
	/// shape Unity does not expose to scene queries cannot be reached this way and reads as "no native shape"; the negative
	/// entry is retried when the collider's rigidbody or cooking state changes, or after <see cref="NoShapeRetryFrames"/>.
	/// </summary>
	private void MapTargeted(Collider collider, int colliderId)
	{
		if (_unityScene == IntPtr.Zero) throw new InvalidOperationException("Unity PhysX scene pointer is unavailable.");
		SyncTransformsOncePerFrame();
		Bounds bounds = collider.bounds;
		Vector3 center = bounds.center;
		Vector3 extents = bounds.extents;
		float radius = extents.magnitude + TargetedMappingPadding;
		float probe = center.x + center.y + center.z + radius;
		if (float.IsNaN(probe) || float.IsInfinity(probe))
			throw new UnityPhysXShapeUnavailableException(collider, "its world bounds are not finite, so Unity has no usable native shape for it", noNativeShape: true);
		// A terrain's bounds span the whole map section, so a bounds sphere would overlap nearly every shape in the scene.
		if (collider is TerrainCollider && MapTerrainByProbes(collider, colliderId, center, extents)) return;
		OverlapAndMerge(center, center, radius);
		_targetedMappings++;
	}

	/// <summary>
	/// Maps a terrain with small spheres centered on its surface instead of its bounds. The bounds overlap stays only as the
	/// fallback when every probe lands in a terrain hole or the terrain has no <see cref="Terrain"/> component.
	/// </summary>
	private bool MapTerrainByProbes(Collider collider, int colliderId, Vector3 center, Vector3 extents)
	{
		Terrain terrain = collider.GetComponent<Terrain>();
		if (terrain == null) return false;
		// SampleHeight is relative to the terrain's own origin.
		float originHeight = terrain.GetPosition().y;
		for (int index = 0; index < TerrainProbeOffsets.Length; index++)
		{
			Vector2 offset = TerrainProbeOffsets[index];
			Vector3 point = new Vector3(center.x + offset.x * extents.x, 0f, center.z + offset.y * extents.z);
			point.y = terrain.SampleHeight(point) + originHeight;
			OverlapAndMerge(point, point, TerrainProbeRadius);
			_targetedMappings++;
			if (_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> mapped) && mapped.Count != 0) return true;
		}
		return false;
	}

	private void OverlapAndMerge(Vector3 point0, Vector3 point1, float radius)
	{
		NativeApi.Vec3 nativePoint0 = new NativeApi.Vec3(point0);
		NativeApi.Vec3 nativePoint1 = new NativeApi.Vec3(point1);
		IntPtr[] shapes = _overlapScratch;
		int query = _api.OverlapUnityShapes(_unityScene, ref nativePoint0, ref nativePoint1, radius,
			shapes, (uint)shapes.Length, out uint writtenOrRequired);
		if (query == NativeApi.BufferTooSmall)
		{
			// The scratch grows to the largest overlap seen and is kept, so steady state allocates nothing.
			_overlapScratch = shapes = new IntPtr[Mathf.NextPowerOfTwo(checked((int)writtenOrRequired))];
			query = _api.OverlapUnityShapes(_unityScene, ref nativePoint0, ref nativePoint1, radius,
				shapes, (uint)shapes.Length, out writtenOrRequired);
		}
		_api.Check(query, "rptpx_unity_overlap_shapes");
		int count = (int)Math.Min(writtenOrRequired, (uint)shapes.Length);
		for (int index = 0; index < count; index++) MergeShape(shapes[index]);
	}

	/// <summary>
	/// Adds a native shape to the frame map. Only mesh and terrain colliders are kept: they are the sole consumers of
	/// native shape pointers, and primitive colliders would only inflate the map and the pooled lists.
	/// </summary>
	private void MergeShape(IntPtr shape)
	{
		if (shape == IntPtr.Zero) return;
		Collider collider = _resolveShapeToCollider(shape);
		if (collider == null || !(collider is MeshCollider || collider is TerrainCollider)) return;
		int colliderId = collider.GetInstanceID();
		if (!_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> mapped))
			_shapesByCollider.Add(colliderId, mapped = RentList());
		if (mapped.Contains(shape)) return;
		mapped.Add(shape);
		_mappedShapes++;
	}

	/// <summary>
	/// Whether a proximity request must run an overlap for this collider. A disabled or inactive collider needs none (Unity
	/// simulates no shape for it and <see cref="Read(MeshCollider)"/> reports that without mapping), nor does one that is mapped
	/// this frame or still validly known to have no shape.
	/// </summary>
	private bool NeedsMapping(Collider collider)
	{
		if (collider == null || !(collider is MeshCollider || collider is TerrainCollider)) return false;
		if (!collider.enabled || !collider.gameObject.activeInHierarchy) return false;
		int colliderId = collider.GetInstanceID();
		if (_shapesByCollider.TryGetValue(colliderId, out List<IntPtr> mapped) && mapped.Count != 0) return false;
		return !IsKnownNoShape(colliderId, new NoShapeSignature(collider));
	}

	private bool IsKnownNoShape(int colliderId, NoShapeSignature signature)
	{
		if (!_noShapeColliders.TryGetValue(colliderId, out NoShapeSignature known)) return false;
		if (Time.frameCount < known.ExpiresFrame && known.SameState(signature)) return true;
		_noShapeColliders.Remove(colliderId);
		return false;
	}

	/// <summary>
	/// What decides whether Unity creates a native shape for an enabled, active collider: its mesh (or terrain data), the
	/// convex flag and cooking options, trigger state, and the attached rigidbody's identity, kinematic state and
	/// detectCollisions (a non-convex mesh is rejected on a non-kinematic body and gains its shape once the body turns
	/// kinematic). A changed signature retires a negative entry at once; <see cref="ExpiresFrame"/> bounds anything the
	/// signature does not capture. The enabled/active state is not part of it: entries are created only for enabled, active
	/// colliders and are dropped as soon as the collider is seen disabled or inactive.
	/// </summary>
	private readonly struct NoShapeSignature
	{
		private readonly int _assetId;
		private readonly int _cookingOptions;
		private readonly int _bodyId;
		private readonly bool _convex;
		private readonly bool _trigger;
		private readonly bool _bodyKinematic;
		private readonly bool _bodyDetectsCollisions;
		internal readonly int ExpiresFrame;

		internal NoShapeSignature(Collider collider)
		{
			Rigidbody body = collider.attachedRigidbody;
			MeshCollider mesh = collider as MeshCollider;
			if (mesh != null)
			{
				Mesh sharedMesh = mesh.sharedMesh;
				_assetId = sharedMesh != null ? sharedMesh.GetInstanceID() : 0;
				_convex = mesh.convex;
				_cookingOptions = (int)mesh.cookingOptions;
			}
			else
			{
				TerrainData data = (collider as TerrainCollider)?.terrainData;
				_assetId = data != null ? data.GetInstanceID() : 0;
				_convex = false;
				_cookingOptions = 0;
			}
			_trigger = collider.isTrigger;
			_bodyId = body != null ? body.GetInstanceID() : 0;
			_bodyKinematic = body != null && body.isKinematic;
			_bodyDetectsCollisions = body == null || body.detectCollisions;
			ExpiresFrame = 0;
		}

		private NoShapeSignature(NoShapeSignature state, int expiresFrame)
		{
			_assetId = state._assetId;
			_cookingOptions = state._cookingOptions;
			_bodyId = state._bodyId;
			_convex = state._convex;
			_trigger = state._trigger;
			_bodyKinematic = state._bodyKinematic;
			_bodyDetectsCollisions = state._bodyDetectsCollisions;
			ExpiresFrame = expiresFrame;
		}

		internal NoShapeSignature ExpiringAt(int expiresFrame) => new NoShapeSignature(this, expiresFrame);

		internal bool SameState(NoShapeSignature other) =>
			_assetId == other._assetId && _cookingOptions == other._cookingOptions && _bodyId == other._bodyId &&
			_convex == other._convex && _trigger == other._trigger && _bodyKinematic == other._bodyKinematic &&
			_bodyDetectsCollisions == other._bodyDetectsCollisions;
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
			$"simulationMode={Physics.simulationMode}, invokeCallbacks={Physics.invokeCollisionCallbacks}, timeScale={Time.timeScale:F2}, scheduled={_probeSimulationLease != null}, scheduledUpdates={_scheduledProbeUpdates}, " +
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

/// <summary>
/// A collider has no usable Unity-owned native shape. The message is built on demand: the omitted-shape path throws
/// per collider per corpse build, and only a failure that is actually reported needs the (allocating) text.
/// </summary>
internal sealed class UnityPhysXShapeUnavailableException : InvalidOperationException
{
	private readonly Collider _collider;
	private readonly string _reason;

	internal UnityPhysXShapeUnavailableException(Collider collider, string reason, bool noNativeShape = false)
	{
		_collider = collider;
		_reason = reason;
		NoNativeShape = noNativeShape;
	}

	public override string Message => _collider != null
		? $"Unity PhysX shape extraction is unavailable for {_collider.GetType().Name} {_collider.name}: {_reason}."
		: $"Unity PhysX shape extraction is unavailable for Collider <destroyed>: {_reason}.";

	/// <summary>Unity itself simulates nothing for this collider (disabled, inactive, or rejected).</summary>
	internal bool NoNativeShape { get; }
}
