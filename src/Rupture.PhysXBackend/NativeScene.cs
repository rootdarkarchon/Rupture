using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

internal sealed class NativeScene : IDisposable
{
	private readonly NativeApi _api;
	private readonly ExternalBackendOptions _options;
	private readonly Dictionary<MaterialKey, ulong> _materials = new Dictionary<MaterialKey, ulong>();
	private readonly Dictionary<string, ulong> _meshes = new Dictionary<string, ulong>(StringComparer.Ordinal);
	private readonly Dictionary<string, ulong> _heightFields = new Dictionary<string, ulong>(StringComparer.Ordinal);
	private readonly Dictionary<ulong, List<ulong>> _transientMeshesByActor = new Dictionary<ulong, List<ulong>>();
	private IntPtr _handle;
	internal long MeshMemoryHits { get; private set; }
	internal long MeshDiskHits { get; private set; }
	internal long MeshCooks { get; private set; }
	internal long CookedBytes { get; private set; }
	internal int MaterialCount => _materials.Count;

	internal NativeScene(NativeApi api, ExternalBackendOptions options, NativeApi.UnitySceneSettings sourceSceneSettings)
	{
		_api = api;
		_options = options;
		NativeApi.SceneDesc desc = NativeApi.SceneDesc.Create(options.DispatcherThreads, sourceSceneSettings);
		desc.GraphicalCasings = options.GraphicalCasings ? 1u : 0u;
		_api.Check(_api.CreateScene(ref desc, out _handle), "rptpx_create_scene");
	}

	internal NativeActor CreateActor(ActorSnapshot snapshot)
	{
		NativeApi.BodyDesc body = snapshot.Body;
		_api.Check(_api.CreateActor(_handle, ref body, out ulong actorHandle), "rptpx_create_actor");
		NativeActor actor = new NativeActor(actorHandle);
		try
		{
			foreach (ShapeSnapshot shape in snapshot.Shapes)
			{
				ulong shapeHandle = AttachShape(actorHandle, shape, "rptpx_attach_shape", actor);
				actor.ShapeHandles.Add(shapeHandle);
			}
			if (snapshot.UpdateMassPropertiesFromShapes)
			{
				_api.Check(_api.UpdateMassAndInertia(_handle, actorHandle, body.Mass, out NativeApi.Vec3 center), "rptpx_update_mass_and_inertia");
				actor.PhysicalCenterOfMass = center.ToUnity();
			}
			if (actor.TransientMeshHandles.Count > 0)
				_transientMeshesByActor[actor.Handle] = new List<ulong>(actor.TransientMeshHandles);
			return actor;
		}
		catch
		{
			_api.DestroyActor(_handle, actorHandle);
			foreach (ulong mesh in actor.TransientMeshHandles) _api.DestroyMesh(_handle, mesh);
			throw;
		}
	}

	internal List<NativeActor> CreateBatch(IReadOnlyList<ActorSnapshot> snapshots)
	{
		List<NativeActor> actors = new List<NativeActor>(snapshots.Count);
		try
		{
			foreach (ActorSnapshot snapshot in snapshots) actors.Add(CreateActor(snapshot));
			return actors;
		}
		catch
		{
			foreach (NativeActor actor in actors) DestroyActor(actor.Handle);
			throw;
		}
	}

	internal ulong CreateJoint(JointSnapshot snapshot)
	{
		switch (snapshot.Kind)
		{
		case JointSnapshotKind.Character:
			NativeApi.D6JointDesc d6 = snapshot.D6;
			_api.Check(_api.CreateD6Joint(_handle, ref d6, out ulong d6Handle), "rptpx_create_d6_joint");
			return d6Handle;
		case JointSnapshotKind.Fixed:
			NativeApi.FixedJointDesc fixedDesc = snapshot.Fixed;
			_api.Check(_api.CreateFixedJoint(_handle, ref fixedDesc, out ulong fixedHandle), "rptpx_create_fixed_joint");
			return fixedHandle;
		case JointSnapshotKind.Distance:
			NativeApi.DistanceJointDesc distance = snapshot.Distance;
			_api.Check(_api.CreateDistanceJoint(_handle, ref distance, out ulong distanceHandle), "rptpx_create_distance_joint");
			return distanceHandle;
		default:
			throw new NotSupportedException("Unsupported joint snapshot.");
		}
	}

	internal void ApplyForce(NativeApi.ForceCommand command)
	{
		_api.Check(_api.ApplyForces(_handle, new[] { command }, 1), "rptpx_apply_forces");
	}

    internal void CasingCommands(ExternalCasingFrame frame)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _api.Check(_api.CasingAudio(_handle, frame.Listener, frame.Audio ? 1u : 0u), "rptpx_casing_audio");
        _api.Check(_api.CasingCommands(_handle, frame.Creates, (uint)frame.CreateCount,
            frame.Removes, (uint)frame.RemoveCount, frame.Impacts, (uint)frame.ImpactCount), "rptpx_casing_commands");
        frame.CommandMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
    }
    internal void ReadCasings(ExternalCasingFrame frame)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int result = _api.CasingRead(_handle, frame.States, (uint)frame.States.Length, out uint states,
            frame.Contacts, (uint)frame.Contacts.Length, out uint contacts);
        if (result == NativeApi.BufferTooSmall)
        {
            if (states > frame.States.Length) Array.Resize(ref frame.States, checked((int)states));
            if (contacts > frame.Contacts.Length) Array.Resize(ref frame.Contacts, checked((int)contacts));
            result = _api.CasingRead(_handle, frame.States, (uint)frame.States.Length, out states,
                frame.Contacts, (uint)frame.Contacts.Length, out contacts);
        }
        _api.Check(result, "rptpx_casing_read");
        frame.StateCount = (int)states; frame.ContactCount = (int)contacts;
        frame.ReadMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
    }

	private NativeApi.BodyState[] _bodyStates = Array.Empty<NativeApi.BodyState>();
	private readonly BodyStatePublication _publication = new BodyStatePublication();
	internal NativeApi.BodyState[] Step(float deltaTime, out int published, out bool awake)
	{
		// Exactly one step is in flight. CompleteStep consumes this array before
		// the next submission, so stable populations can reuse their publication.
		while (true)
		{
			int result = _api.Step(_handle, deltaTime, _bodyStates, (uint)_bodyStates.Length, out uint written);
			if (result == NativeApi.BufferTooSmall)
			{
				Array.Resize(ref _bodyStates, checked((int)written));
				continue;
			}
			_api.Check(result, "rptpx_step");
			if (written != _bodyStates.Length) Array.Resize(ref _bodyStates, checked((int)written));
			published = _publication.Filter(_bodyStates, out awake);
			return _bodyStates;
		}
	}

	internal void SetBodyState(ulong handle, NativeApi.BodyState state, bool kinematic, bool wake)
	{
		_api.Check(_api.SetActorState(_handle, handle, ref state, kinematic ? 1u : 0u, wake ? 1u : 0u), "rptpx_set_actor_state");
	}

	internal void SetActorPose(ulong handle, NativeApi.Pose pose, bool kinematicTarget)
	{
		_api.Check(_api.SetActorPose(_handle, handle, ref pose, kinematicTarget ? 1u : 0u), "rptpx_set_actor_pose");
	}

	internal void SetShapeEnabled(ulong handle, bool enabled)
	{
		_api.Check(_api.SetShapeEnabled(_handle, handle, enabled ? 1u : 0u), "rptpx_set_shape_enabled");
	}

	internal void IgnoreShapePair(ulong left, ulong right)
	{
		_api.Check(_api.IgnoreShapePair(_handle, left, right), "rptpx_ignore_shape_pair");
	}

	internal void ReplaceActorShapes(NativeActor actor, ActorSnapshot replacement)
	{
		List<ulong> newHandles = new List<ulong>(replacement.Shapes.Count);
		try
		{
			foreach (ShapeSnapshot shape in replacement.Shapes)
			{
				ulong handle = AttachShape(actor.Handle, shape, "rptpx_attach_shape(scale-rebuild)", actor);
				newHandles.Add(handle);
			}
		}
		catch
		{
			foreach (ulong handle in newHandles) _api.DestroyShape(_handle, handle);
			throw;
		}
		foreach (ulong handle in actor.ShapeHandles) _api.DestroyShape(_handle, handle);
		actor.ShapeHandles.Clear();
		actor.ShapeHandles.AddRange(newHandles);
	}

	internal void DestroyJoint(ulong handle)
	{
		if (handle != 0) _api.DestroyJoint(_handle, handle);
	}

	internal bool IsJointBroken(ulong handle)
	{
		_api.Check(_api.GetJointBroken(_handle, handle, out uint broken), "rptpx_get_joint_broken");
		return broken != 0;
	}

	internal void DestroyActor(ulong handle)
	{
		if (handle == 0) return;
		_api.DestroyActor(_handle, handle);
		_publication.Remove(handle);
		if (!_transientMeshesByActor.TryGetValue(handle, out List<ulong> meshes)) return;
		_transientMeshesByActor.Remove(handle);
		foreach (ulong mesh in meshes) _api.DestroyMesh(_handle, mesh);
	}

	public void Dispose()
	{
		if (_handle != IntPtr.Zero)
		{
			_api.DestroyScene(_handle);
			_handle = IntPtr.Zero;
		}
	}

	private NativeApi.ShapeDesc BuildShape(ShapeSnapshot shape, NativeActor owner)
	{
		ulong geometryHandle = shape.HeightField != null ? GetHeightField(shape.HeightField) : shape.Mesh == null ? 0 :
			GetMesh(shape.Mesh, shape.TransientMesh);
		if (shape.TransientMesh && geometryHandle != 0) owner.TransientMeshHandles.Add(geometryHandle);
		MaterialKey material = shape.Materials != null && shape.Materials.Length != 0 ? shape.Materials[0] : shape.Material;
		return new NativeApi.ShapeDesc
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.ShapeDesc>(), GeometryKind = shape.GeometryKind,
			MeshHandle = geometryHandle, MaterialHandle = GetMaterial(material),
			LocalPose = shape.LocalPose, Dimensions = new NativeApi.Vec3(shape.Dimensions),
			MeshScale = new NativeApi.Vec3(shape.Mesh?.NativeScale ?? Vector3.one),
			GeometryFlags = shape.GeometryFlags,
			LayerBits = shape.LayerBits, CollidesWithBits = shape.CollidesWithBits,
			OwnerGroup = shape.OwnerGroup, ShapeId = unchecked((uint)shape.ColliderId),
			ContactOffset = shape.ContactOffset, Enabled = shape.Enabled ? 1u : 0u
		};
	}

	private ulong AttachShape(ulong actorHandle, ShapeSnapshot shape, string operation, NativeActor owner = null)
	{
		NativeApi.ShapeDesc desc = BuildShape(shape, owner ?? throw new ArgumentNullException(nameof(owner)));
		if (shape.Materials == null || shape.Materials.Length <= 1)
		{
			_api.Check(_api.AttachShape(_handle, actorHandle, ref desc, out ulong handle), operation);
			return handle;
		}
		ulong[] materialHandles = new ulong[shape.Materials.Length];
		for (int index = 0; index < materialHandles.Length; index++) materialHandles[index] = GetMaterial(shape.Materials[index]);
		_api.Check(_api.AttachShapeMaterials(_handle, actorHandle, ref desc, materialHandles, (uint)materialHandles.Length, out ulong multiHandle), operation + "_materials");
		return multiHandle;
	}

	private ulong GetMaterial(MaterialKey key)
	{
		if (_materials.TryGetValue(key, out ulong handle)) return handle;
		NativeApi.MaterialDesc desc = new NativeApi.MaterialDesc
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.MaterialDesc>(), StaticFriction = key.StaticFriction,
			DynamicFriction = key.DynamicFriction, Restitution = key.Bounciness,
			FrictionCombine = key.FrictionCombine, RestitutionCombine = key.BounceCombine
		};
		_api.Check(_api.CreateMaterial(_handle, ref desc, out handle), "rptpx_create_material");
		_materials.Add(key, handle);
		return handle;
	}

	private ulong GetMesh(MeshGeometry geometry, bool transient = false)
	{
		if (transient)
		{
			byte[] transientCooked = Cook(geometry);
			_api.Check(_api.CreateMeshFromCooked(_handle, geometry.Convex ? 1u : 0u, transientCooked,
				(uint)transientCooked.Length, out ulong transientHandle), "rptpx_create_transient_mesh_from_cooked");
			MeshCooks++;
			CookedBytes += transientCooked.Length;
			return transientHandle;
		}
		string key = geometry.CacheKey(_options.EftBuild ?? "unknown");
		if (_meshes.TryGetValue(key, out ulong handle))
		{
			MeshMemoryHits++;
			return handle;
		}
		string cachePath = Path.Combine(_options.CacheRoot, key + ".pxc");
		byte[] cooked;
		if (File.Exists(cachePath))
		{
			cooked = File.ReadAllBytes(cachePath);
			if (cooked.Length == 0) throw new InvalidDataException($"Cooked mesh cache entry is empty: {cachePath}");
			MeshDiskHits++;
		}
		else
		{
			cooked = Cook(geometry);
			string temporary = cachePath + ".tmp-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Thread.CurrentThread.ManagedThreadId;
			File.WriteAllBytes(temporary, cooked);
			try
			{
				if (!File.Exists(cachePath)) File.Move(temporary, cachePath);
			}
			finally
			{
				if (File.Exists(temporary)) File.Delete(temporary);
			}
			MeshCooks++;
		}
		_api.Check(_api.CreateMeshFromCooked(_handle, geometry.Convex ? 1u : 0u, cooked, (uint)cooked.Length, out handle), "rptpx_create_mesh_from_cooked");
		_meshes.Add(key, handle);
		CookedBytes += cooked.Length;
		return handle;
	}

	private ulong GetHeightField(HeightFieldGeometry geometry)
	{
		string key = geometry.CacheKey(_options.EftBuild ?? "unknown");
		if (_heightFields.TryGetValue(key, out ulong handle))
		{
			MeshMemoryHits++;
			return handle;
		}
		GCHandle samplePin = default;
		try
		{
			samplePin = GCHandle.Alloc(geometry.Samples, GCHandleType.Pinned);
			NativeApi.HeightFieldDesc desc = new NativeApi.HeightFieldDesc
			{
				StructSize = (uint)Marshal.SizeOf<NativeApi.HeightFieldDesc>(),
				Rows = geometry.Rows,
				Columns = geometry.Columns,
				SampleStride = geometry.SampleStride,
				Samples = samplePin.AddrOfPinnedObject(),
				SampleBytes = (uint)geometry.Samples.Length,
				ConvexEdgeThreshold = geometry.ConvexEdgeThreshold,
				Flags = geometry.HeightFieldFlags
			};
			_api.Check(_api.CreateHeightField(_handle, ref desc, out handle), "rptpx_create_heightfield");
		}
		finally
		{
			if (samplePin.IsAllocated) samplePin.Free();
		}
		_heightFields.Add(key, handle);
		return handle;
	}

	private byte[] Cook(MeshGeometry geometry)
	{
		NativeApi.Vec3[] vertices = geometry.Vertices.Select(vertex => new NativeApi.Vec3(vertex)).ToArray();
		uint[] indices = geometry.Indices;
		GCHandle vertexPin = default;
		GCHandle indexPin = default;
		try
		{
			vertexPin = GCHandle.Alloc(vertices, GCHandleType.Pinned);
			if (indices.Length != 0) indexPin = GCHandle.Alloc(indices, GCHandleType.Pinned);
			NativeApi.MeshDesc desc = new NativeApi.MeshDesc
			{
				StructSize = (uint)Marshal.SizeOf<NativeApi.MeshDesc>(), Convex = geometry.Convex ? 1u : 0u,
				Vertices = vertexPin.AddrOfPinnedObject(), VertexCount = (uint)vertices.Length,
				Indices = indexPin.IsAllocated ? indexPin.AddrOfPinnedObject() : IntPtr.Zero, IndexCount = (uint)indices.Length, CookingFlags = geometry.CookingOptions
			};
			int query = _api.CookMesh(_handle, ref desc, IntPtr.Zero, 0, out uint required);
			if (query != NativeApi.BufferTooSmall || required == 0) _api.Check(query, "rptpx_cook_mesh(size)");
			byte[] cooked = new byte[required];
			GCHandle outputPin = GCHandle.Alloc(cooked, GCHandleType.Pinned);
			try
			{
				_api.Check(_api.CopyCookedMesh(_handle, IntPtr.Zero, outputPin.AddrOfPinnedObject(), required, out uint written), "rptpx_cook_mesh(copy)");
				if (written != required) throw new InvalidOperationException("Cooked mesh result size changed during copy.");
			}
			finally { outputPin.Free(); }
			return cooked;
		}
		finally
		{
			if (vertexPin.IsAllocated) vertexPin.Free();
			if (indexPin.IsAllocated) indexPin.Free();
		}
	}
}

internal sealed class NativeActor
{
	internal ulong Handle { get; private set; }
	internal readonly List<ulong> ShapeHandles = new List<ulong>();
	internal readonly List<ulong> TransientMeshHandles = new List<ulong>();
	internal Vector3? PhysicalCenterOfMass;
	internal NativeActor(ulong handle) { Handle = handle; }
	internal void Adopt(NativeActor actor)
	{
		ShapeHandles.AddRange(actor.ShapeHandles);
		TransientMeshHandles.AddRange(actor.TransientMeshHandles);
		PhysicalCenterOfMass = actor.PhysicalCenterOfMass;
		Handle = actor.Handle;
	}
}

internal readonly struct MaterialKey : IEquatable<MaterialKey>
{
	internal readonly float StaticFriction, DynamicFriction, Bounciness;
	internal readonly uint FrictionCombine, BounceCombine;
	internal MaterialKey(PhysicMaterial material)
	{
		StaticFriction = material?.staticFriction ?? 0.6f;
		DynamicFriction = material?.dynamicFriction ?? 0.6f;
		Bounciness = material?.bounciness ?? 0f;
		FrictionCombine = MapCombine(material?.frictionCombine ?? PhysicMaterialCombine.Average);
		BounceCombine = MapCombine(material?.bounceCombine ?? PhysicMaterialCombine.Average);
	}
	internal MaterialKey(NativeApi.MaterialDesc material)
	{
		StaticFriction = material.StaticFriction;
		DynamicFriction = material.DynamicFriction;
		Bounciness = material.Restitution;
		FrictionCombine = material.FrictionCombine;
		BounceCombine = material.RestitutionCombine;
	}
	public bool Equals(MaterialKey other) => StaticFriction.Equals(other.StaticFriction) && DynamicFriction.Equals(other.DynamicFriction) && Bounciness.Equals(other.Bounciness) && FrictionCombine == other.FrictionCombine && BounceCombine == other.BounceCombine;
	public override bool Equals(object value) => value is MaterialKey other && Equals(other);
	public override int GetHashCode() => (((StaticFriction.GetHashCode() * 397) ^ DynamicFriction.GetHashCode()) * 397 ^ Bounciness.GetHashCode()) * 397 ^ (int)(FrictionCombine * 7 + BounceCombine);
	private static uint MapCombine(PhysicMaterialCombine value)
	{
		switch (value)
		{
		case PhysicMaterialCombine.Minimum: return 1;
		case PhysicMaterialCombine.Multiply: return 2;
		case PhysicMaterialCombine.Maximum: return 3;
		default: return 0;
		}
	}
}

internal sealed class ShapeSnapshot
{
	internal Collider Collider;
	internal int ColliderId;
	internal uint GeometryKind;
	internal NativeApi.Pose LocalPose;
	internal Vector3 Dimensions;
	internal MeshGeometry Mesh;
	internal HeightFieldGeometry HeightField;
	internal MaterialKey Material;
	internal MaterialKey[] Materials;
	internal uint GeometryFlags;
	internal uint LayerBits, CollidesWithBits, OwnerGroup;
	internal float ContactOffset;
	internal bool Enabled;
	internal bool TransientMesh;
}

internal sealed class ActorSnapshot
{
	internal Transform Root;
	internal bool Movable;
	internal NativeApi.BodyDesc Body;
	internal readonly List<ShapeSnapshot> Shapes = new List<ShapeSnapshot>();
	internal bool UpdateMassPropertiesFromShapes;

	internal static ActorSnapshot CaptureEnvironment(Transform root, IReadOnlyList<Collider> colliders, bool movable, int deadbodyLayer)
	{
		ActorSnapshot snapshot = new ActorSnapshot { Root = root, Movable = movable };
		snapshot.Body = StaticOrKinematic(root, movable);
		uint deadbodyBits = deadbodyLayer >= 0 ? 1u << deadbodyLayer : uint.MaxValue;
		foreach (Collider collider in colliders) snapshot.Shapes.Add(CaptureShape(collider, root, 0, 1u << collider.gameObject.layer, deadbodyBits));
		return snapshot;
	}

	internal static ActorSnapshot CaptureBody(Rigidbody body, IReadOnlyList<Collider> colliders, uint ownerGroup, bool kinematic,
		int deadbodyLayer, int environmentMask, ExternalCollisionProfile collisionProfile)
	{
		ActorSnapshot snapshot = new ActorSnapshot { Root = body.transform, Movable = true };
		snapshot.Body = new NativeApi.BodyDesc
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.BodyDesc>(), Kind = kinematic ? 2u : 1u,
			Pose = new NativeApi.Pose(body.position, body.rotation), LinearVelocity = new NativeApi.Vec3(body.velocity), AngularVelocity = new NativeApi.Vec3(body.angularVelocity),
			Mass = body.mass, LinearDamping = body.drag, AngularDamping = body.angularDrag, SleepThreshold = body.sleepThreshold,
			MaxAngularVelocity = body.maxAngularVelocity, MaxDepenetrationVelocity = body.maxDepenetrationVelocity,
			SolverPositionIterations = (uint)body.solverIterations, SolverVelocityIterations = (uint)body.solverVelocityIterations,
			// EFT authority corpses use Discrete, but our elapsed-time steps can be
			// much longer than EFT's fixed step (especially on headless). Always
			// enable swept CCD on the external actor so it cannot skip thin floors.
			// The native shell and its fallback collision mode remain EFT-owned.
			Flags = 1u,
			CenterOfMass = new NativeApi.Vec3(body.centerOfMass), InertiaTensor = new NativeApi.Vec3(body.inertiaTensor), InertiaRotation = new NativeApi.Quat(body.inertiaTensorRotation)
		};
		if (!body.useGravity) snapshot.Body.Flags |= 2u;
		if ((body.constraints & RigidbodyConstraints.FreezePositionX) != 0) snapshot.Body.Flags |= 4u;
		if ((body.constraints & RigidbodyConstraints.FreezePositionY) != 0) snapshot.Body.Flags |= 8u;
		if ((body.constraints & RigidbodyConstraints.FreezePositionZ) != 0) snapshot.Body.Flags |= 16u;
		if ((body.constraints & RigidbodyConstraints.FreezeRotationX) != 0) snapshot.Body.Flags |= 32u;
		if ((body.constraints & RigidbodyConstraints.FreezeRotationY) != 0) snapshot.Body.Flags |= 64u;
		if ((body.constraints & RigidbodyConstraints.FreezeRotationZ) != 0) snapshot.Body.Flags |= 128u;
		uint deadbodyBits = deadbodyLayer >= 0 ? 1u << deadbodyLayer : uint.MaxValue;
		uint collides = (uint)environmentMask;
		if (collisionProfile == ExternalCollisionProfile.DeadbodyAndEnvironment) collides |= deadbodyBits;
		foreach (Collider collider in colliders) snapshot.Shapes.Add(CaptureShape(collider, body.transform, ownerGroup, deadbodyBits, collides));
		return snapshot;
	}

	internal static ActorSnapshot CaptureTransientConvexBody(Rigidbody body,
		IReadOnlyList<ExternalTransientConvexShape> shapes, uint ownerGroup, bool kinematic,
		int deadbodyLayer, int environmentMask, ExternalCollisionProfile collisionProfile)
	{
		ActorSnapshot snapshot = CaptureBody(body, Array.Empty<Collider>(), ownerGroup, kinematic,
			deadbodyLayer, environmentMask, collisionProfile);
		uint deadbodyBits = deadbodyLayer >= 0 ? 1u << deadbodyLayer : uint.MaxValue;
		uint collides = (uint)environmentMask;
		if (collisionProfile == ExternalCollisionProfile.DeadbodyAndEnvironment) collides |= deadbodyBits;
		foreach (ExternalTransientConvexShape source in shapes)
		{
			if (source?.Vertices == null || source.Vertices.Length < 4 || source.Vertices.Length > 255)
				throw new InvalidDataException("Transient convex shape has an invalid vertex count.");
			Vector3[] vertices = (Vector3[])source.Vertices.Clone();
			uint[] indices = source.Indices != null ? (uint[])source.Indices.Clone() : Array.Empty<uint>();
			if (vertices.Any(vertex => !Finite(vertex)) || indices.Any(index => index >= vertices.Length))
				throw new InvalidDataException("Transient convex shape contains non-finite vertices or invalid indices.");
			Bounds bounds = new Bounds(vertices[0], Vector3.zero);
			for (int index = 1; index < vertices.Length; index++) bounds.Encapsulate(vertices[index]);
			MeshGeometry geometry = new MeshGeometry
			{
				Vertices = vertices,
				Indices = indices,
				Layout = "transient-limb-proxy/v1",
				RawHash = MeshGeometry.HashGeometry(vertices, indices),
				Convex = true,
				CookingOptions = 0,
				NativeScale = Vector3.one,
				CookScaleIdentity = "transient-root-local",
				LocalBounds = bounds
			};
			snapshot.Shapes.Add(new ShapeSnapshot
			{
				Collider = null,
				ColliderId = source.QueryColliderId,
				GeometryKind = 5,
				LocalPose = new NativeApi.Pose(source.LocalPosition, source.LocalRotation),
				Dimensions = Vector3.zero,
				Mesh = geometry,
				Material = new MaterialKey(source.Material),
				LayerBits = deadbodyBits,
				CollidesWithBits = collides,
				OwnerGroup = ownerGroup,
				ContactOffset = Mathf.Clamp(source.ContactOffset, 0.001f, 0.02f),
				Enabled = true,
				TransientMesh = true
			});
		}
		if (snapshot.Shapes.Count == 0) throw new InvalidDataException("Transient convex body has no shapes.");
		return snapshot;
	}

	internal static ActorSnapshot CaptureBoxBody(Rigidbody body, ExternalBoxShape box, uint ownerGroup, bool kinematic, int deadbodyLayer, int environmentMask)
	{
		float mass = Mathf.Max(0.01f, body.mass);
		Vector3 scale = Abs(body.transform.lossyScale);
		Vector3 center = Quaternion.Inverse(body.rotation) * (body.transform.TransformPoint(box.Center) - body.position);
		Vector3 size = new Vector3(
			Mathf.Max(0.02f, Mathf.Abs(box.Size.x) * scale.x),
			Mathf.Max(0.02f, Mathf.Abs(box.Size.y) * scale.y),
			Mathf.Max(0.02f, Mathf.Abs(box.Size.z) * scale.z));
		float inertiaScale = mass / 12f;
		Vector3 inertia = new Vector3(
			Mathf.Max(0.000001f, inertiaScale * (size.y * size.y + size.z * size.z)),
			Mathf.Max(0.000001f, inertiaScale * (size.x * size.x + size.z * size.z)),
			Mathf.Max(0.000001f, inertiaScale * (size.x * size.x + size.y * size.y)));
		ActorSnapshot snapshot = new ActorSnapshot { Root = body.transform, Movable = true, UpdateMassPropertiesFromShapes = true };
		snapshot.Body = new NativeApi.BodyDesc
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.BodyDesc>(), Kind = kinematic ? 2u : 1u,
			Pose = new NativeApi.Pose(body.position, body.rotation), LinearVelocity = new NativeApi.Vec3(body.velocity), AngularVelocity = new NativeApi.Vec3(body.angularVelocity),
			Mass = mass, LinearDamping = body.drag, AngularDamping = body.angularDrag, SleepThreshold = body.sleepThreshold,
			MaxAngularVelocity = body.maxAngularVelocity, MaxDepenetrationVelocity = body.maxDepenetrationVelocity,
			SolverPositionIterations = (uint)body.solverIterations, SolverVelocityIterations = (uint)body.solverVelocityIterations,
			Flags = 1u | (!body.useGravity ? 2u : 0u),
			CenterOfMass = new NativeApi.Vec3(center), InertiaTensor = new NativeApi.Vec3(inertia), InertiaRotation = new NativeApi.Quat(Quaternion.identity)
		};
		uint deadbodyBits = deadbodyLayer >= 0 ? 1u << deadbodyLayer : uint.MaxValue;
		snapshot.Shapes.Add(new ShapeSnapshot
		{
			Collider = null, ColliderId = 0, GeometryKind = 1,
			LocalPose = new NativeApi.Pose(center, Quaternion.identity), Dimensions = size * 0.5f,
			Material = new MaterialKey(box.Material), LayerBits = deadbodyBits,
			CollidesWithBits = (uint)environmentMask | deadbodyBits, OwnerGroup = ownerGroup,
			ContactOffset = Mathf.Clamp(box.ContactOffset, 0.001f, 0.02f), Enabled = true
		});
		return snapshot;
	}

	internal static ActorSnapshot CaptureRoundedBoxBody(Rigidbody body, ExternalRoundedBoxShape roundedBox, uint ownerGroup, bool kinematic, int deadbodyLayer, int environmentMask)
	{
		float mass = Mathf.Max(0.01f, body.mass);
		Vector3 scale = Abs(body.transform.lossyScale);
		Vector3 center = Quaternion.Inverse(body.rotation) * (body.transform.TransformPoint(roundedBox.Center) - body.position);
		Vector3 size = new Vector3(
			Mathf.Max(0.02f, Mathf.Abs(roundedBox.Size.x) * scale.x),
			Mathf.Max(0.02f, Mathf.Abs(roundedBox.Size.y) * scale.y),
			Mathf.Max(0.02f, Mathf.Abs(roundedBox.Size.z) * scale.z));
		MeshGeometry geometry = MeshGeometryReader.CreateWeaponRoundedBox(size);
		float inertiaScale = mass / 12f;
		Vector3 inertia = new Vector3(
			Mathf.Max(0.000001f, inertiaScale * (size.y * size.y + size.z * size.z)),
			Mathf.Max(0.000001f, inertiaScale * (size.x * size.x + size.z * size.z)),
			Mathf.Max(0.000001f, inertiaScale * (size.x * size.x + size.y * size.y)));
		ActorSnapshot snapshot = new ActorSnapshot { Root = body.transform, Movable = true, UpdateMassPropertiesFromShapes = true };
		snapshot.Body = new NativeApi.BodyDesc
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.BodyDesc>(), Kind = kinematic ? 2u : 1u,
			Pose = new NativeApi.Pose(body.position, body.rotation), LinearVelocity = new NativeApi.Vec3(body.velocity), AngularVelocity = new NativeApi.Vec3(body.angularVelocity),
			Mass = mass, LinearDamping = body.drag, AngularDamping = body.angularDrag, SleepThreshold = body.sleepThreshold,
			MaxAngularVelocity = body.maxAngularVelocity, MaxDepenetrationVelocity = body.maxDepenetrationVelocity,
			SolverPositionIterations = (uint)body.solverIterations, SolverVelocityIterations = (uint)body.solverVelocityIterations,
			Flags = 1u | (!body.useGravity ? 2u : 0u),
			CenterOfMass = new NativeApi.Vec3(center), InertiaTensor = new NativeApi.Vec3(inertia), InertiaRotation = new NativeApi.Quat(Quaternion.identity)
		};
		uint deadbodyBits = deadbodyLayer >= 0 ? 1u << deadbodyLayer : uint.MaxValue;
		snapshot.Shapes.Add(new ShapeSnapshot
		{
			Collider = null, ColliderId = 0, GeometryKind = 5, LocalPose = new NativeApi.Pose(center, Quaternion.identity),
			Dimensions = Vector3.zero, Mesh = geometry, Material = new MaterialKey(roundedBox.Material), LayerBits = deadbodyBits,
			CollidesWithBits = (uint)environmentMask | deadbodyBits, OwnerGroup = ownerGroup,
			ContactOffset = Mathf.Clamp(roundedBox.ContactOffset, 0.001f, 0.02f), Enabled = true
		});
		return snapshot;
	}

	private static NativeApi.BodyDesc StaticOrKinematic(Transform root, bool kinematic)
	{
		return new NativeApi.BodyDesc
		{
			StructSize = (uint)Marshal.SizeOf<NativeApi.BodyDesc>(), Kind = kinematic ? 2u : 0u,
			Pose = new NativeApi.Pose(root.position, root.rotation), LinearVelocity = new NativeApi.Vec3(Vector3.zero), AngularVelocity = new NativeApi.Vec3(Vector3.zero),
			Mass = 1f, SleepThreshold = Physics.sleepThreshold, MaxAngularVelocity = 7f, MaxDepenetrationVelocity = 10f,
			SolverPositionIterations = 6, SolverVelocityIterations = 1, Flags = 2,
			CenterOfMass = new NativeApi.Vec3(Vector3.zero), InertiaTensor = new NativeApi.Vec3(Vector3.one), InertiaRotation = new NativeApi.Quat(Quaternion.identity)
		};
	}

	private static ShapeSnapshot CaptureShape(Collider collider, Transform actorRoot, uint ownerGroup, uint layerBits, uint collidesWith)
	{
		Transform transform = collider.transform;
		Vector3 scale = transform.lossyScale;
		Quaternion localRotation = Quaternion.Inverse(actorRoot.rotation) * transform.rotation;
		Vector3 localPosition;
		Vector3 dimensions;
		uint geometry;
		MeshGeometry mesh = null;
		HeightFieldGeometry heightField = null;
		MaterialKey[] materials = null;
		uint geometryFlags = 0;
		if (collider is BoxCollider box)
		{
			geometry = 1;
			localPosition = Quaternion.Inverse(actorRoot.rotation) * (transform.TransformPoint(box.center) - actorRoot.position);
			dimensions = Vector3.Scale(box.size * 0.5f, Abs(scale));
		}
		else if (collider is SphereCollider sphere)
		{
			geometry = 2;
			localPosition = Quaternion.Inverse(actorRoot.rotation) * (transform.TransformPoint(sphere.center) - actorRoot.position);
			dimensions = new Vector3(sphere.radius * MaxAbs(scale), 0f, 0f);
		}
		else if (collider is CapsuleCollider capsule)
		{
			geometry = 3;
			localPosition = Quaternion.Inverse(actorRoot.rotation) * (transform.TransformPoint(capsule.center) - actorRoot.position);
			float axis = capsule.direction == 0 ? Mathf.Abs(scale.x) : capsule.direction == 1 ? Mathf.Abs(scale.y) : Mathf.Abs(scale.z);
			float radial = capsule.direction == 0 ? Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)) : capsule.direction == 1 ? Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)) : Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
			float radius = capsule.radius * radial;
			dimensions = new Vector3(radius, Mathf.Max(0f, capsule.height * axis * 0.5f - radius), 0f);
			if (capsule.direction == 1) localRotation *= Quaternion.Euler(0f, 0f, 90f);
			else if (capsule.direction == 2) localRotation *= Quaternion.Euler(0f, -90f, 0f);
		}
		else if (collider is MeshCollider meshCollider)
		{
			geometry = meshCollider.convex ? 5u : 4u;
			localPosition = Quaternion.Inverse(actorRoot.rotation) * (transform.position - actorRoot.position);
			dimensions = Vector3.zero;
			mesh = MeshGeometryRepository.Get(meshCollider).BakeNegativeScale(scale);
		}
		else if (collider is TerrainCollider terrainCollider)
		{
			geometry = 6;
			heightField = MeshGeometryRepository.Get(terrainCollider);
			Vector3 worldPosition = heightField.WorldPose.Position.ToUnity();
			Quaternion worldRotation = heightField.WorldPose.Rotation.ToUnity();
			localPosition = Quaternion.Inverse(actorRoot.rotation) * (worldPosition - actorRoot.position);
			localRotation = Quaternion.Inverse(actorRoot.rotation) * worldRotation;
			dimensions = new Vector3(heightField.HeightScale, heightField.RowScale, heightField.ColumnScale);
			geometryFlags = heightField.GeometryFlags;
			materials = heightField.Materials.Select(material => new MaterialKey(material)).ToArray();
		}
		else throw new NotSupportedException($"Collider {collider.GetType().FullName} is unsupported.");

		return new ShapeSnapshot
		{
			Collider = collider, ColliderId = collider.GetInstanceID(), GeometryKind = geometry,
			LocalPose = new NativeApi.Pose(localPosition, localRotation), Dimensions = dimensions, Mesh = mesh, HeightField = heightField,
			Material = new MaterialKey(collider.sharedMaterial), LayerBits = layerBits, CollidesWithBits = collidesWith,
			Materials = materials, GeometryFlags = geometryFlags, OwnerGroup = ownerGroup, ContactOffset = Mathf.Max(0.0001f, collider.contactOffset),
			Enabled = collider.enabled && collider.gameObject.activeInHierarchy
		};
	}

	private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
	private static float MaxAbs(Vector3 value) => Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
	private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
		!float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}

internal static class MeshGeometryRepository
{
	private static readonly Dictionary<string, MeshGeometry> Cache = new Dictionary<string, MeshGeometry>(StringComparer.Ordinal);
	private static readonly Dictionary<int, HeightFieldCacheEntry> HeightFieldFrameCache = new Dictionary<int, HeightFieldCacheEntry>();
	private static IMeshGeometrySource _source;

	internal static void Configure(IMeshGeometrySource source)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		Cache.Clear();
		HeightFieldFrameCache.Clear();
	}

	internal static void Clear()
	{
		Cache.Clear();
		HeightFieldFrameCache.Clear();
	}

	internal static MeshGeometry Get(MeshCollider collider)
	{
		if (_source == null) throw new InvalidOperationException("The external mesh geometry source is not configured.");
		Mesh mesh = collider.sharedMesh;
		string key = $"{_source.Name}:{mesh.GetInstanceID()}:{collider.convex}:{(uint)collider.cookingOptions}";
		if (!Cache.TryGetValue(key, out MeshGeometry geometry))
		{
			geometry = _source.Read(collider);
			Cache.Add(key, geometry);
		}
		return geometry;
	}

	internal static HeightFieldGeometry Get(TerrainCollider collider)
	{
		if (_source == null) throw new InvalidOperationException("The external Unity PhysX geometry source is not configured.");
		int colliderId = collider.GetInstanceID();
		if (HeightFieldFrameCache.TryGetValue(colliderId, out HeightFieldCacheEntry cached) && cached.Frame == Time.frameCount) return cached.Geometry;
		HeightFieldGeometry geometry = _source.Read(collider);
		HeightFieldFrameCache[colliderId] = new HeightFieldCacheEntry(Time.frameCount, geometry);
		return geometry;
	}

	private sealed class HeightFieldCacheEntry
	{
		internal readonly int Frame;
		internal readonly HeightFieldGeometry Geometry;
		internal HeightFieldCacheEntry(int frame, HeightFieldGeometry geometry) { Frame = frame; Geometry = geometry; }
	}
}

internal enum JointSnapshotKind { Character, Fixed, Distance }

internal sealed class JointSnapshot
{
	internal JointSnapshotKind Kind;
	internal NativeApi.D6JointDesc D6;
	internal NativeApi.FixedJointDesc Fixed;
	internal NativeApi.DistanceJointDesc Distance;

	internal static JointSnapshot Capture(Joint joint, ulong body, ulong connected)
	{
		Quaternion childFrameRotation = JointFrame(joint.axis, joint is CharacterJoint character ? character.swingAxis : Vector3.up);
		NativeApi.Pose childPose = new NativeApi.Pose(joint.anchor, childFrameRotation);
		Quaternion worldFrameRotation = joint.transform.rotation * childFrameRotation;
		Quaternion connectedFrameRotation = joint.connectedBody != null
			? Quaternion.Inverse(joint.connectedBody.rotation) * worldFrameRotation
			: worldFrameRotation;
		NativeApi.Pose connectedPose = new NativeApi.Pose(joint.connectedAnchor, connectedFrameRotation);
		float breakForce = float.IsInfinity(joint.breakForce) ? float.MaxValue : joint.breakForce;
		float breakTorque = float.IsInfinity(joint.breakTorque) ? float.MaxValue : joint.breakTorque;
		if (joint is CharacterJoint source)
		{
			return new JointSnapshot
			{
				Kind = JointSnapshotKind.Character,
				D6 = new NativeApi.D6JointDesc
				{
					StructSize = (uint)Marshal.SizeOf<NativeApi.D6JointDesc>(), Actor0 = body, Actor1 = connected,
					LocalPose0 = childPose, LocalPose1 = connectedPose, LinearLimit = new NativeApi.Vec3(Vector3.zero),
					TwistLower = Mathf.Min(source.lowTwistLimit.limit, source.highTwistLimit.limit) * Mathf.Deg2Rad,
					TwistUpper = Mathf.Max(source.lowTwistLimit.limit, source.highTwistLimit.limit) * Mathf.Deg2Rad,
					SwingY = Mathf.Max(0f, source.swing1Limit.limit * Mathf.Deg2Rad), SwingZ = Mathf.Max(0f, source.swing2Limit.limit * Mathf.Deg2Rad),
					TwistStiffness = Mathf.Max(0f, source.twistLimitSpring.spring), TwistDamping = Mathf.Max(0f, source.twistLimitSpring.damper),
					TwistRestitution = Mathf.Clamp01(Mathf.Max(source.lowTwistLimit.bounciness, source.highTwistLimit.bounciness)),
					TwistContactDistance = Mathf.Max(0f, Mathf.Max(source.lowTwistLimit.contactDistance, source.highTwistLimit.contactDistance) * Mathf.Deg2Rad),
					SwingStiffness = Mathf.Max(0f, source.swingLimitSpring.spring), SwingDamping = Mathf.Max(0f, source.swingLimitSpring.damper),
					SwingRestitution = Mathf.Clamp01(Mathf.Max(source.swing1Limit.bounciness, source.swing2Limit.bounciness)),
					SwingContactDistance = Mathf.Max(0f, Mathf.Max(source.swing1Limit.contactDistance, source.swing2Limit.contactDistance) * Mathf.Deg2Rad),
					BreakForce = breakForce, BreakTorque = breakTorque, Projection = source.enableProjection ? 1u : 0u,
					ProjectionLinearTolerance = source.projectionDistance,
					ProjectionAngularTolerance = source.projectionAngle * Mathf.Deg2Rad,
					MassScale = source.massScale, ConnectedMassScale = source.connectedMassScale, EnablePreprocessing = source.enablePreprocessing ? 1u : 0u
				}
			};
		}
		if (joint is FixedJoint)
		{
			return new JointSnapshot
			{
				Kind = JointSnapshotKind.Fixed,
				Fixed = new NativeApi.FixedJointDesc
				{
					StructSize = (uint)Marshal.SizeOf<NativeApi.FixedJointDesc>(), Actor0 = body, Actor1 = connected,
					LocalPose0 = childPose, LocalPose1 = connectedPose, BreakForce = breakForce, BreakTorque = breakTorque,
					Projection = 1, MassScale = joint.massScale, ConnectedMassScale = joint.connectedMassScale, EnablePreprocessing = joint.enablePreprocessing ? 1u : 0u
				}
			};
		}
		if (joint is SpringJoint spring)
		{
			return new JointSnapshot
			{
				Kind = JointSnapshotKind.Distance,
				Distance = new NativeApi.DistanceJointDesc
				{
					StructSize = (uint)Marshal.SizeOf<NativeApi.DistanceJointDesc>(), Actor0 = body, Actor1 = connected,
					LocalPose0 = childPose, LocalPose1 = connectedPose, MinimumDistance = spring.minDistance, MaximumDistance = spring.maxDistance,
					Stiffness = spring.spring, Damping = spring.damper, Tolerance = spring.tolerance, BreakForce = breakForce, BreakTorque = breakTorque,
					MassScale = spring.massScale, ConnectedMassScale = spring.connectedMassScale, EnablePreprocessing = spring.enablePreprocessing ? 1u : 0u
				}
			};
		}
		throw new NotSupportedException($"Joint {joint.GetType().FullName} is unsupported.");
	}

	private static Quaternion JointFrame(Vector3 axis, Vector3 swingAxis)
	{
		if (axis.sqrMagnitude < 0.000001f) axis = Vector3.right;
		Quaternion align = Quaternion.FromToRotation(Vector3.right, axis.normalized);
		Vector3 expectedSwing = align * Vector3.up;
		Vector3 projected = Vector3.ProjectOnPlane(swingAxis, axis).normalized;
		if (projected.sqrMagnitude < 0.000001f) return align;
		return Quaternion.AngleAxis(Vector3.SignedAngle(expectedSwing, projected, axis), axis) * align;
	}
}

internal sealed class EnvironmentActor
{
	internal NativeActor Native { get; private set; }
	internal readonly Transform Root;
	internal readonly List<EnvironmentShape> Shapes = new List<EnvironmentShape>();
	internal bool Movable;
	internal int References;
	internal LinkedListNode<EnvironmentActor> RetainedNode;
	internal Vector3 LastPosition, LastScale;
	internal Quaternion LastRotation;
	internal EnvironmentTransformProbe TransformProbe;

	internal EnvironmentActor(ActorSnapshot snapshot, NativeActor native)
	{
		Native = native;
		Root = snapshot.Root;
		Movable = snapshot.Movable;
		LastPosition = Root != null ? Root.position : Vector3.zero;
		LastRotation = Root != null ? Root.rotation : Quaternion.identity;
		LastScale = Root != null ? Root.lossyScale : Vector3.one;
		for (int index = 0; index < snapshot.Shapes.Count; index++) Shapes.Add(new EnvironmentShape(snapshot.Shapes[index], native, index));
	}

	internal void ReplaceNative(NativeActor native)
	{
		Native = native ?? throw new ArgumentNullException(nameof(native));
		for (int index = 0; index < Shapes.Count; index++) Shapes[index].Rebind(native, index);
	}
}

internal sealed class EnvironmentShape
{
	private NativeActor _actor;
	private int _index;
	internal readonly Collider Collider;
	internal readonly int ColliderId;
	internal bool LastEnabled;
	internal ulong NativeHandle => _index < _actor.ShapeHandles.Count ? _actor.ShapeHandles[_index] : 0;
	internal EnvironmentShape(ShapeSnapshot snapshot, NativeActor actor, int index)
	{
		_actor = actor;
		_index = index;
		Collider = snapshot.Collider;
		ColliderId = snapshot.ColliderId;
		LastEnabled = snapshot.Enabled;
	}
	internal void Rebind(NativeActor actor, int index)
	{
		_actor = actor;
		_index = index;
	}
}
