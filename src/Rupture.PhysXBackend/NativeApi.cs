using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

internal sealed class NativeApi : IDisposable
{
	internal const uint AbiVersion = 10;
	internal const uint PhysXVersion = 0x04010200;
	internal const int Ok = 0;
	internal const int InvalidArgument = 1;
	internal const int BufferTooSmall = 4;
	private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
	private const uint LoadLibrarySearchSystem32 = 0x00000800;

	private IntPtr _module;
	internal readonly GetAbiDelegate GetAbi;
	internal readonly GetLastErrorDelegate GetLastError;
	internal readonly GetUnitySceneFromActorDelegate GetUnitySceneFromActor;
	internal readonly GetUnitySceneSettingsDelegate GetUnitySceneSettings;
	internal readonly EnumerateUnityShapesDelegate EnumerateUnityShapes;
	internal readonly OverlapUnityShapesDelegate OverlapUnityShapes;
	internal readonly ExtractUnityMeshDelegate ExtractUnityMesh;
	internal readonly ExtractUnityHeightFieldDelegate ExtractUnityHeightField;
	internal readonly CreateSceneDelegate CreateScene;
	internal readonly DestroySceneDelegate DestroyScene;
	internal readonly CreateMaterialDelegate CreateMaterial;
	internal readonly CookMeshDelegate CookMesh;
	internal readonly CopyCookedMeshDelegate CopyCookedMesh;
	internal readonly CreateMeshFromCookedDelegate CreateMeshFromCooked;
	internal readonly CreateHeightFieldDelegate CreateHeightField;
	internal readonly CreateActorDelegate CreateActor;
	internal readonly AttachShapeDelegate AttachShape;
	internal readonly AttachShapeMaterialsDelegate AttachShapeMaterials;
	internal readonly UpdateMassAndInertiaDelegate UpdateMassAndInertia;
	internal readonly DestroyHandleDelegate DestroyShape;
	internal readonly DestroyHandleDelegate DestroyMesh;
	internal readonly DestroyHandleDelegate DestroyActor;
	internal readonly DestroyHandleDelegate DestroyJoint;
	internal readonly GetJointBrokenDelegate GetJointBroken;
	internal readonly SetActorPoseDelegate SetActorPose;
	internal readonly SetActorStateDelegate SetActorState;
	internal readonly SetShapeEnabledDelegate SetShapeEnabled;
	internal readonly IgnoreShapePairDelegate IgnoreShapePair;
	internal readonly CreateD6JointDelegate CreateD6Joint;
	internal readonly CreateFixedJointDelegate CreateFixedJoint;
	internal readonly CreateDistanceJointDelegate CreateDistanceJoint;
	internal readonly ApplyForcesDelegate ApplyForces;
	internal readonly StepDelegate Step;
	internal readonly CasingCommandsDelegate CasingCommands;
	internal readonly CasingReadDelegate CasingRead;
	internal readonly CasingAudioDelegate CasingAudio;

	internal NativeApi(string backendRoot, bool graphicalCasings)
	{
		string libraryPath = Path.GetFullPath(Path.Combine(backendRoot, "rpt_physx_bridge.dll"));
		if (!libraryPath.StartsWith(Path.GetFullPath(backendRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(libraryPath))
		{
			throw new FileNotFoundException("The isolated PhysX native bridge is missing.", libraryPath);
		}
		_module = LoadLibraryExW(libraryPath, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
		if (_module == IntPtr.Zero)
		{
			throw new InvalidOperationException($"LoadLibraryExW failed for {libraryPath} with Win32 error {Marshal.GetLastWin32Error()}.");
		}

		GetAbi = Load<GetAbiDelegate>("rptpx_get_abi");
		GetLastError = Load<GetLastErrorDelegate>("rptpx_get_last_error");
		GetUnitySceneFromActor = Load<GetUnitySceneFromActorDelegate>("rptpx_unity_get_scene_from_actor");
		GetUnitySceneSettings = Load<GetUnitySceneSettingsDelegate>("rptpx_unity_get_scene_settings");
		EnumerateUnityShapes = Load<EnumerateUnityShapesDelegate>("rptpx_unity_enumerate_shapes");
		OverlapUnityShapes = Load<OverlapUnityShapesDelegate>("rptpx_unity_overlap_shapes");
		ExtractUnityMesh = Load<ExtractUnityMeshDelegate>("rptpx_unity_extract_mesh");
		ExtractUnityHeightField = Load<ExtractUnityHeightFieldDelegate>("rptpx_unity_extract_heightfield");
		CreateScene = Load<CreateSceneDelegate>("rptpx_create_scene");
		DestroyScene = Load<DestroySceneDelegate>("rptpx_destroy_scene");
		CreateMaterial = Load<CreateMaterialDelegate>("rptpx_create_material");
		CookMesh = Load<CookMeshDelegate>("rptpx_cook_mesh");
		CopyCookedMesh = Load<CopyCookedMeshDelegate>("rptpx_cook_mesh");
		CreateMeshFromCooked = Load<CreateMeshFromCookedDelegate>("rptpx_create_mesh_from_cooked");
		CreateHeightField = Load<CreateHeightFieldDelegate>("rptpx_create_heightfield");
		CreateActor = Load<CreateActorDelegate>("rptpx_create_actor");
		AttachShape = Load<AttachShapeDelegate>("rptpx_attach_shape");
		AttachShapeMaterials = Load<AttachShapeMaterialsDelegate>("rptpx_attach_shape_materials");
		UpdateMassAndInertia = Load<UpdateMassAndInertiaDelegate>("rptpx_update_mass_and_inertia");
		DestroyShape = Load<DestroyHandleDelegate>("rptpx_destroy_shape");
		DestroyMesh = Load<DestroyHandleDelegate>("rptpx_destroy_mesh");
		DestroyActor = Load<DestroyHandleDelegate>("rptpx_destroy_actor");
		DestroyJoint = Load<DestroyHandleDelegate>("rptpx_destroy_joint");
		GetJointBroken = Load<GetJointBrokenDelegate>("rptpx_get_joint_broken");
		SetActorPose = Load<SetActorPoseDelegate>("rptpx_set_actor_pose");
		SetActorState = Load<SetActorStateDelegate>("rptpx_set_actor_state");
		SetShapeEnabled = Load<SetShapeEnabledDelegate>("rptpx_set_shape_enabled");
		IgnoreShapePair = Load<IgnoreShapePairDelegate>("rptpx_ignore_shape_pair");
		CreateD6Joint = Load<CreateD6JointDelegate>("rptpx_create_d6_joint");
		CreateFixedJoint = Load<CreateFixedJointDelegate>("rptpx_create_fixed_joint");
		CreateDistanceJoint = Load<CreateDistanceJointDelegate>("rptpx_create_distance_joint");
		ApplyForces = Load<ApplyForcesDelegate>("rptpx_apply_forces");
		Step = Load<StepDelegate>("rptpx_step");
		if (graphicalCasings)
		{
			CasingCommands = Load<CasingCommandsDelegate>("rptpx_casing_commands");
			CasingRead = Load<CasingReadDelegate>("rptpx_casing_read");
			CasingAudio = Load<CasingAudioDelegate>("rptpx_casing_audio");
		}

		Check(GetAbi(out uint abi, out uint physx), "rptpx_get_abi");
		if (abi != AbiVersion || physx != PhysXVersion)
		{
			throw new InvalidDataException($"External PhysX ABI mismatch: managed expects ABI {AbiVersion}/PhysX 0x{PhysXVersion:X8}, native reports ABI {abi}/PhysX 0x{physx:X8}.");
		}
	}

	internal string LastError
	{
		get
		{
			IntPtr pointer = GetLastError();
			return pointer == IntPtr.Zero ? "unknown native PhysX failure" : Marshal.PtrToStringAnsi(pointer);
		}
	}

	internal void Check(int result, string operation)
	{
		if (result != Ok)
		{
			throw new InvalidOperationException($"{operation} failed with {result}: {LastError}");
		}
	}

	public void Dispose()
	{
		if (_module != IntPtr.Zero)
		{
			FreeLibrary(_module);
			_module = IntPtr.Zero;
		}
	}

	private T Load<T>(string name) where T : Delegate
	{
		IntPtr export = GetProcAddress(_module, name);
		if (export == IntPtr.Zero)
		{
			throw new EntryPointNotFoundException($"{name} is missing from rpt_physx_bridge.dll.");
		}
		return Marshal.GetDelegateForFunctionPointer<T>(export);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

	[DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
	private static extern IntPtr GetProcAddress(IntPtr module, string name);

	[DllImport("kernel32.dll")]
	private static extern bool FreeLibrary(IntPtr module);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetAbiDelegate(out uint abi, out uint physx);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetLastErrorDelegate();
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetUnitySceneFromActorDelegate(IntPtr actor, out IntPtr unityScene);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetUnitySceneSettingsDelegate(IntPtr unityScene, ref UnitySceneSettings settings);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EnumerateUnityShapesDelegate(IntPtr unityScene, [Out] IntPtr[] shapes, uint capacity, out uint writtenOrRequired);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int OverlapUnityShapesDelegate(IntPtr unityScene, ref Vec3 point0, ref Vec3 point1, float radius, [Out] IntPtr[] shapes, uint capacity, out uint writtenOrRequired);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ExtractUnityMeshDelegate(IntPtr shape, ref UnityMeshInfo info, [Out] Vec3[] vertices, uint vertexCapacity, [Out] uint[] indices, uint indexCapacity);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ExtractUnityHeightFieldDelegate(IntPtr shape, ref UnityHeightFieldInfo info, [Out] byte[] samples, uint sampleCapacity, [Out] MaterialDesc[] materials, uint materialCapacity);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateSceneDelegate(ref SceneDesc desc, out IntPtr scene);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroySceneDelegate(IntPtr scene);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateMaterialDelegate(IntPtr scene, ref MaterialDesc desc, out ulong material);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CookMeshDelegate(IntPtr scene, ref MeshDesc desc, IntPtr output, uint capacity, out uint required);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CopyCookedMeshDelegate(IntPtr scene, IntPtr desc, IntPtr output, uint capacity, out uint required);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateMeshFromCookedDelegate(IntPtr scene, uint convex, byte[] data, uint length, out ulong mesh);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateHeightFieldDelegate(IntPtr scene, ref HeightFieldDesc desc, out ulong heightField);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateActorDelegate(IntPtr scene, ref BodyDesc desc, out ulong actor);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AttachShapeDelegate(IntPtr scene, ulong actor, ref ShapeDesc desc, out ulong shape);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AttachShapeMaterialsDelegate(IntPtr scene, ulong actor, ref ShapeDesc desc, [In] ulong[] materials, uint materialCount, out ulong shape);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int UpdateMassAndInertiaDelegate(IntPtr scene, ulong actor, float mass, out Vec3 centerOfMass);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DestroyHandleDelegate(IntPtr scene, ulong handle);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GetJointBrokenDelegate(IntPtr scene, ulong handle, out uint broken);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetActorPoseDelegate(IntPtr scene, ulong actor, ref Pose pose, uint kinematicTarget);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetActorStateDelegate(IntPtr scene, ulong actor, ref BodyState state, uint kinematic, uint wake);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SetShapeEnabledDelegate(IntPtr scene, ulong shape, uint enabled);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IgnoreShapePairDelegate(IntPtr scene, ulong shape0, ulong shape1);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateD6JointDelegate(IntPtr scene, ref D6JointDesc desc, out ulong joint);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateFixedJointDelegate(IntPtr scene, ref FixedJointDesc desc, out ulong joint);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateDistanceJointDelegate(IntPtr scene, ref DistanceJointDesc desc, out ulong joint);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ApplyForcesDelegate(IntPtr scene, [In] ForceCommand[] commands, uint count);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int StepDelegate(IntPtr scene, float deltaTime, [Out] BodyState[] states, uint capacity, out uint writtenOrRequired);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CasingAudioDelegate(IntPtr scene, Vector3 listener, uint enabled);
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CasingCommandsDelegate(IntPtr scene,
        [In] CasingDescriptor[] creates, uint createCount, [In] ulong[] removes, uint removeCount, [In] CasingImpact[] impacts, uint impactCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CasingReadDelegate(IntPtr scene,
        [Out] CasingState[] states, uint stateCapacity, out uint stateCount, [Out] CasingContact[] contacts, uint contactCapacity, out uint contactCount);

	[StructLayout(LayoutKind.Sequential)]
	internal struct Vec3
	{
		internal float X, Y, Z;
		internal Vec3(Vector3 value) { X = value.x; Y = value.y; Z = value.z; }
		internal Vector3 ToUnity() => new Vector3(X, Y, Z);
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct Quat
	{
		internal float X, Y, Z, W;
		internal Quat(Quaternion value) { X = value.x; Y = value.y; Z = value.z; W = value.w; }
		internal Quaternion ToUnity() => new Quaternion(X, Y, Z, W);
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct Pose
	{
		internal Vec3 Position;
		internal Quat Rotation;
		internal Pose(Vector3 position, Quaternion rotation) { Position = new Vec3(position); Rotation = new Quat(rotation); }
		internal Pose(Transform transform) : this(transform.position, transform.rotation) { }
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct SceneDesc
	{
		internal uint StructSize, DispatcherThreads;
		internal Vec3 Gravity;
		internal float BounceThresholdVelocity;
		internal uint SolverType, FrictionType, SceneFlags, GraphicalCasings;
		internal static SceneDesc Create(int threads, UnitySceneSettings source) => new SceneDesc
		{
			StructSize = (uint)Marshal.SizeOf<SceneDesc>(), DispatcherThreads = (uint)threads,
			Gravity = new Vec3(Physics.gravity), BounceThresholdVelocity = Physics.bounceThreshold,
			SolverType = source.SolverType, FrictionType = source.FrictionType,
			// PxSceneFlag::eENABLE_CCD is required by our swept corpse actors even
			// when the source Unity scene has no continuous bodies of its own.
			SceneFlags = source.SceneFlags | (1u << 1)
		};
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct UnitySceneSettings
	{
		internal uint StructSize, SolverType, FrictionType, SceneFlags;
		internal static UnitySceneSettings Create() => new UnitySceneSettings { StructSize = (uint)Marshal.SizeOf<UnitySceneSettings>() };
		public override string ToString() => $"solver={SolverType}, friction={FrictionType}, flags=0x{SceneFlags:X}";
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct MaterialDesc
	{
		internal uint StructSize;
		internal float StaticFriction, DynamicFriction, Restitution;
		internal uint FrictionCombine, RestitutionCombine;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct BodyDesc
	{
		internal uint StructSize, Kind;
		internal Pose Pose;
		internal Vec3 LinearVelocity, AngularVelocity;
		internal float Mass, LinearDamping, AngularDamping, SleepThreshold, MaxAngularVelocity, MaxDepenetrationVelocity;
		internal uint SolverPositionIterations, SolverVelocityIterations, Flags;
		internal Vec3 CenterOfMass, InertiaTensor;
		internal Quat InertiaRotation;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct ShapeDesc
	{
		internal uint StructSize, GeometryKind;
		internal ulong MeshHandle, MaterialHandle;
		internal Pose LocalPose;
		internal Vec3 Dimensions, MeshScale;
		internal uint GeometryFlags;
		internal uint LayerBits, CollidesWithBits, OwnerGroup, ShapeId;
		internal float ContactOffset;
		internal uint Enabled;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct MeshDesc
	{
		internal uint StructSize, Convex;
		internal IntPtr Vertices;
		internal uint VertexCount;
		internal IntPtr Indices;
		internal uint IndexCount, CookingFlags;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct UnityMeshInfo
	{
		internal uint StructSize, GeometryKind, VertexCount, IndexCount;
		internal ulong MeshIdentity;
		internal Vec3 MeshScale;
		internal Quat MeshScaleRotation;
		internal uint MeshFlags, IndexFormat;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct UnityHeightFieldInfo
	{
		internal uint StructSize, GeometryKind, Rows, Columns;
		internal uint SampleStride, SampleBytes, MaterialCount, GeometryFlags;
		internal ulong HeightFieldIdentity;
		internal Pose WorldPose;
		internal float HeightScale, RowScale, ColumnScale, ConvexEdgeThreshold;
		internal uint HeightFieldFlags, Timestamp;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct HeightFieldDesc
	{
		internal uint StructSize, Rows, Columns, SampleStride;
		internal IntPtr Samples;
		internal uint SampleBytes;
		internal float ConvexEdgeThreshold;
		internal uint Flags;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct D6JointDesc
	{
		internal uint StructSize;
		internal ulong Actor0, Actor1;
		internal Pose LocalPose0, LocalPose1;
		internal Vec3 LinearLimit;
		internal float TwistLower, TwistUpper, SwingY, SwingZ;
		internal float TwistStiffness, TwistDamping, TwistRestitution, TwistContactDistance;
		internal float SwingStiffness, SwingDamping, SwingRestitution, SwingContactDistance;
		internal float BreakForce, BreakTorque;
		internal uint Projection;
		internal float ProjectionLinearTolerance, ProjectionAngularTolerance;
		internal float MassScale, ConnectedMassScale;
		internal uint EnablePreprocessing;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct FixedJointDesc
	{
		internal uint StructSize;
		internal ulong Actor0, Actor1;
		internal Pose LocalPose0, LocalPose1;
		internal float BreakForce, BreakTorque;
		internal uint Projection;
		internal float MassScale, ConnectedMassScale;
		internal uint EnablePreprocessing;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct DistanceJointDesc
	{
		internal uint StructSize;
		internal ulong Actor0, Actor1;
		internal Pose LocalPose0, LocalPose1;
		internal float MinimumDistance, MaximumDistance, Stiffness, Damping, Tolerance, BreakForce, BreakTorque;
		internal float MassScale, ConnectedMassScale;
		internal uint EnablePreprocessing;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct ForceCommand
	{
		internal uint StructSize, Operation;
		internal ulong Actor;
		internal Vec3 Force, Position;
		internal uint Mode, Wake;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct BodyState
	{
		internal uint StructSize, Sleeping;
		internal ulong Actor;
		internal Pose Pose;
		internal Vec3 LinearVelocity, AngularVelocity;
	}
}
