using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

// Cached body/joint state and queued commands. Native handles remain owned by
// the runtime scene; invalidating a wrapper does not release native resources.
internal sealed class ExternalBody : IExternalPhysicsBody
{
	private readonly ExternalPhysXRuntime _runtime;
	private Vector3 _position, _velocity, _angularVelocity;
	private Quaternion _rotation;
	private bool _kinematic, _sleeping, _valid = true;
	private bool _collisionEnabled = true;
	private readonly Vector3 _centerOfMass;
	private readonly Vector3 _physicalCenterOfMass;
	internal readonly Dictionary<int, ulong> ShapeHandlesByCollider = new Dictionary<int, ulong>();
	internal readonly Dictionary<ulong, bool> ShapeEnabledByHandle = new Dictionary<ulong, bool>();

	internal ExternalBody(ExternalPhysXRuntime runtime, ulong handle, Vector3 centerOfMass, NativeApi.BodyDesc initial, Vector3? physicalCenterOfMass = null)
	{
		_runtime = runtime;
		Handle = handle;
		_centerOfMass = centerOfMass;
		_physicalCenterOfMass = physicalCenterOfMass ?? centerOfMass;
		_position = initial.Pose.Position.ToUnity();
		_rotation = initial.Pose.Rotation.ToUnity();
		_velocity = initial.LinearVelocity.ToUnity();
		_angularVelocity = initial.AngularVelocity.ToUnity();
		_kinematic = initial.Kind == 2;
	}

	public ulong Handle { get; }
	public Vector3 Position { get => _position; set { _position = value; _runtime.QueueBodyState(this, false); } }
	public Quaternion Rotation { get => _rotation; set { _rotation = value; _runtime.QueueBodyState(this, false); } }
	public Vector3 Velocity { get => _velocity; set { _velocity = value; _runtime.QueueBodyState(this, true); } }
	public Vector3 AngularVelocity { get => _angularVelocity; set { _angularVelocity = value; _runtime.QueueBodyState(this, true); } }
	public Vector3 CenterOfMass => _centerOfMass;
	public Vector3 WorldCenterOfMass => _position + _rotation * _physicalCenterOfMass;
	public bool IsKinematic { get => _kinematic; set { _kinematic = value; _runtime.QueueBodyState(this, !value); } }
	public bool CollisionEnabled { get => _collisionEnabled; set { if (_collisionEnabled == value) return; _collisionEnabled = value; _runtime.QueueBodyCollision(this, value); } }
	public bool IsSleeping => _sleeping;
	public bool IsValid => _valid;
	public void WakeUp() { _sleeping = false; _runtime.QueueBodyState(this, true); }
	public void AddForce(Vector3 force, ForceMode mode) => _runtime.QueueForce(this, 1, force, Vector3.zero, mode);
	public void AddForceAtPosition(Vector3 force, Vector3 worldPosition, ForceMode mode) => _runtime.QueueForce(this, 2, force, worldPosition, mode);
	public void AddTorque(Vector3 torque, ForceMode mode) => _runtime.QueueForce(this, 3, torque, Vector3.zero, mode);

	internal NativeApi.BodyState SnapshotNativeState() => new NativeApi.BodyState
	{
		StructSize = (uint)Marshal.SizeOf<NativeApi.BodyState>(), Actor = Handle,
		Pose = new NativeApi.Pose(_position, _rotation), LinearVelocity = new NativeApi.Vec3(_velocity), AngularVelocity = new NativeApi.Vec3(_angularVelocity), Sleeping = _sleeping ? 1u : 0u
	};

	internal void Publish(NativeApi.BodyState state)
	{
		_position = state.Pose.Position.ToUnity();
		_rotation = state.Pose.Rotation.ToUnity();
		_velocity = state.LinearVelocity.ToUnity();
		_angularVelocity = state.AngularVelocity.ToUnity();
		_sleeping = state.Sleeping != 0;
	}

	internal void PublishAuthoritative(ExternalBodyState state)
	{
		_position = state.Position;
		_rotation = state.Rotation;
		_velocity = state.Velocity;
		_angularVelocity = state.AngularVelocity;
		_kinematic = state.IsKinematic;
		_sleeping = state.IsSleeping;
	}

	internal void Invalidate() => _valid = false;
}

internal sealed class ExternalJoint : IExternalPhysicsJoint
{
	private int _broken;
	internal ExternalJoint(ulong handle) { Handle = handle; }
	public ulong Handle { get; }
	public bool IsValid { get; private set; } = true;
	public bool IsBroken => Volatile.Read(ref _broken) != 0;
	internal void PublishBroken(bool broken) { if (broken) Volatile.Write(ref _broken, 1); }
	internal void Invalidate() => IsValid = false;
}
