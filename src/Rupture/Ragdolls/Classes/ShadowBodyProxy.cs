using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.Ragdolls.Classes;

internal interface IShadowBodyProxy
{
	Rigidbody UnityBody { get; }
	GameObject gameObject { get; }
	Transform transform { get; }
	Vector3 position { get; set; }
	Quaternion rotation { get; set; }
	Vector3 velocity { get; set; }
	Vector3 angularVelocity { get; set; }
	Vector3 centerOfMass { get; }
	Vector3 worldCenterOfMass { get; }
	bool isKinematic { get; set; }
	CollisionDetectionMode collisionDetectionMode { get; set; }
	bool IsSleeping();
	void WakeUp();
	void AddForce(Vector3 force, ForceMode mode);
	void AddRelativeForce(Vector3 force, ForceMode mode);
	void AddForceAtPosition(Vector3 force, Vector3 position, ForceMode mode);
	void AddExplosionForce(float force, Vector3 position, float radius, float upwardsModifier, ForceMode mode);
	void AddTorque(Vector3 torque, ForceMode mode);
	void AddRelativeTorque(Vector3 torque, ForceMode mode);
	Vector3 TransformPoint(Vector3 point);
}

internal sealed class UnityShadowBodyProxy : IShadowBodyProxy
{
	internal UnityShadowBodyProxy(Rigidbody body) { UnityBody = body; }
	public Rigidbody UnityBody { get; }
	public GameObject gameObject => UnityBody != null ? UnityBody.gameObject : null;
	public Transform transform => UnityBody != null ? UnityBody.transform : null;
	public Vector3 position { get => UnityBody.position; set => UnityBody.position = value; }
	public Quaternion rotation { get => UnityBody.rotation; set => UnityBody.rotation = value; }
	public Vector3 velocity { get => UnityBody.velocity; set => UnityBody.velocity = value; }
	public Vector3 angularVelocity { get => UnityBody.angularVelocity; set => UnityBody.angularVelocity = value; }
	public Vector3 centerOfMass => UnityBody.centerOfMass;
	public Vector3 worldCenterOfMass => UnityBody.worldCenterOfMass;
	public bool isKinematic { get => UnityBody.isKinematic; set => UnityBody.isKinematic = value; }
	public CollisionDetectionMode collisionDetectionMode { get => UnityBody.collisionDetectionMode; set => UnityBody.collisionDetectionMode = value; }
	public bool IsSleeping() => UnityBody.IsSleeping();
	public void WakeUp() => UnityBody.WakeUp();
	public void AddForce(Vector3 force, ForceMode mode) => UnityBody.AddForce(force, mode);
	public void AddRelativeForce(Vector3 force, ForceMode mode) => UnityBody.AddRelativeForce(force, mode);
	public void AddForceAtPosition(Vector3 force, Vector3 point, ForceMode mode) => UnityBody.AddForceAtPosition(force, point, mode);
	public void AddExplosionForce(float force, Vector3 point, float radius, float upwardsModifier, ForceMode mode) => UnityBody.AddExplosionForce(force, point, radius, upwardsModifier, mode);
	public void AddTorque(Vector3 torque, ForceMode mode) => UnityBody.AddTorque(torque, mode);
	public void AddRelativeTorque(Vector3 torque, ForceMode mode) => UnityBody.AddRelativeTorque(torque, mode);
	public Vector3 TransformPoint(Vector3 point) => UnityBody.transform.TransformPoint(point);
}

internal sealed class ExternalShadowBodyProxy : IShadowBodyProxy
{
	private readonly IExternalPhysicsBody _body;
	private readonly Rigidbody _native;
	private readonly Vector3 _nativeScale;
	private CollisionDetectionMode _collisionDetectionMode;

	internal ExternalShadowBodyProxy(IExternalPhysicsBody body, Rigidbody native)
	{
		_body = body;
		_native = native;
		_nativeScale = native != null ? native.transform.lossyScale : Vector3.one;
		_collisionDetectionMode = native != null ? native.collisionDetectionMode : CollisionDetectionMode.Discrete;
	}

	internal IExternalPhysicsBody ExternalBody => _body;
	public Rigidbody UnityBody => null;
	public GameObject gameObject => null;
	public Transform transform => _native != null ? _native.transform : null;
	public Vector3 position { get => _body.Position; set => _body.Position = value; }
	public Quaternion rotation { get => _body.Rotation; set => _body.Rotation = value; }
	public Vector3 velocity { get => _body.Velocity; set => _body.Velocity = value; }
	public Vector3 angularVelocity { get => _body.AngularVelocity; set => _body.AngularVelocity = value; }
	public Vector3 centerOfMass => _body.CenterOfMass;
	public Vector3 worldCenterOfMass => _body.WorldCenterOfMass;
	public bool isKinematic { get => _body.IsKinematic; set => _body.IsKinematic = value; }
	public CollisionDetectionMode collisionDetectionMode { get => _collisionDetectionMode; set => _collisionDetectionMode = value; }
	public bool IsSleeping() => _body.IsSleeping;
	public void WakeUp() => _body.WakeUp();
	public void AddForce(Vector3 force, ForceMode mode) => _body.AddForce(force, mode);
	public void AddRelativeForce(Vector3 force, ForceMode mode) => _body.AddForce(_body.Rotation * force, mode);
	public void AddForceAtPosition(Vector3 force, Vector3 point, ForceMode mode) => _body.AddForceAtPosition(force, point, mode);
	public void AddExplosionForce(float force, Vector3 point, float radius, float upwardsModifier, ForceMode mode)
	{
		Vector3 origin = point - Vector3.up * upwardsModifier;
		Vector3 offset = _body.WorldCenterOfMass - origin;
		float distance = offset.magnitude;
		if (radius > 0f && distance > radius) return;
		float falloff = radius > 0f ? Mathf.Clamp01(1f - distance / radius) : 1f;
		_body.AddForce((distance > 0.0001f ? offset / distance : Vector3.up) * (force * falloff), mode);
	}
	public void AddTorque(Vector3 torque, ForceMode mode) => _body.AddTorque(torque, mode);
	public void AddRelativeTorque(Vector3 torque, ForceMode mode) => _body.AddTorque(_body.Rotation * torque, mode);
	public Vector3 TransformPoint(Vector3 point)
	{
		return _body.Position + _body.Rotation * Vector3.Scale(point, _nativeScale);
	}
}
