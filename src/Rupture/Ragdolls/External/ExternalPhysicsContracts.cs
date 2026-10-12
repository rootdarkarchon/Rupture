using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Rupture.Ragdolls.External;

public enum ExternalEnvironmentMode
{
	ProximityCache = 0,
	PrebakeEntireRaid = 1
}

public enum ExternalLogLevel
{
	Info = 0,
	Warning = 1,
	Error = 2,
	Lifecycle = 3
}

public enum ExternalCollisionProfile
{
	DeadbodyAndEnvironment = 0,
	EnvironmentOnly = 1
}

public enum ExternalBodyCreationStatus
{
	Pending = 0,
	Succeeded = 1,
	Failed = 2,
	Cancelled = 3
}

public sealed class ExternalTransientConvexShape
{
	public int QueryColliderId { get; set; }
	public Vector3[] Vertices { get; set; }
	public uint[] Indices { get; set; }
	public Vector3 LocalPosition { get; set; }
	public Quaternion LocalRotation { get; set; } = Quaternion.identity;
	public PhysicMaterial Material { get; set; }
	public float ContactOffset { get; set; } = 0.01f;
}

public interface IExternalBodyCreation : IDisposable
{
	double WorkerMilliseconds { get; }
	ExternalBodyCreationStatus Poll(out IExternalPhysicsBody body, out string failure);
	void Cancel();
}

public sealed class ExternalBackendOptions
{
	public bool GraphicalCasings { get; set; }
	public string BackendRoot { get; set; }
	public string CacheRoot { get; set; }
	public string EftBuild { get; set; }
	public ExternalEnvironmentMode EnvironmentMode { get; set; }
	public int DispatcherThreads { get; set; }
	public int DeadbodyLayer { get; set; }
	public int EnvironmentMask { get; set; }
	public Action<ExternalLogLevel, string> Log { get; set; }
}

public readonly struct ExternalEnvironmentAcquireResult
{
	public ExternalEnvironmentAcquireResult(int created, int reused, int total)
	{
		Created = created;
		Reused = reused;
		Total = total;
	}

	public int Created { get; }
	public int Reused { get; }
	public int Total { get; }
}

public readonly struct ExternalStepResult
{
	public ExternalStepResult(bool completed, bool timedOut, double milliseconds, double mainWaitMilliseconds, bool wasReady, long sequence,
		int publishedBodies = 0, int unchangedBodies = 0)
	{
		Completed = completed;
		TimedOut = timedOut;
		Milliseconds = milliseconds;
		MainWaitMilliseconds = mainWaitMilliseconds;
		WasReady = wasReady;
		Sequence = sequence;
		PublishedBodies = publishedBodies; UnchangedBodies = unchangedBodies;
	}

	public bool Completed { get; }
	public bool TimedOut { get; }
	public double Milliseconds { get; }
	public double MainWaitMilliseconds { get; }
	public bool WasReady { get; }
	public long Sequence { get; }
	public int PublishedBodies { get; }
	public int UnchangedBodies { get; }
}

public readonly struct ExternalBoxShape
{
	public ExternalBoxShape(Vector3 center, Vector3 size, PhysicMaterial material, float contactOffset)
	{
		Center = center;
		Size = size;
		Material = material;
		ContactOffset = contactOffset;
	}

	public Vector3 Center { get; }
	public Vector3 Size { get; }
	public PhysicMaterial Material { get; }
	public float ContactOffset { get; }
}

public sealed class ExternalRoundedBoxShape
{
	public ExternalRoundedBoxShape(Vector3 center, Vector3 size, PhysicMaterial material, float contactOffset)
	{
		Center = center;
		Size = size;
		Material = material;
		ContactOffset = contactOffset;
	}

	public Vector3 Center { get; }
	public Vector3 Size { get; }
	public PhysicMaterial Material { get; }
	public float ContactOffset { get; }
}

public interface IExternalPhysicsBody
{
	ulong Handle { get; }
	Vector3 Position { get; set; }
	Quaternion Rotation { get; set; }
	Vector3 Velocity { get; set; }
	Vector3 AngularVelocity { get; set; }
	Vector3 CenterOfMass { get; }
	Vector3 WorldCenterOfMass { get; }
	bool IsKinematic { get; set; }
	bool CollisionEnabled { get; set; }
	bool IsSleeping { get; }
	bool IsValid { get; }
	void WakeUp();
	void AddForce(Vector3 force, ForceMode mode);
	void AddForceAtPosition(Vector3 force, Vector3 worldPosition, ForceMode mode);
	void AddTorque(Vector3 torque, ForceMode mode);
}

public interface IExternalPhysicsJoint
{
	ulong Handle { get; }
	bool IsValid { get; }
	bool IsBroken { get; }
}

public readonly struct ExternalBodyState
{
	public ExternalBodyState(IExternalPhysicsBody body, Vector3 position, Quaternion rotation,
		Vector3 velocity, Vector3 angularVelocity, bool isKinematic, bool isSleeping)
	{
		Body = body;
		Position = position;
		Rotation = rotation;
		Velocity = velocity;
		AngularVelocity = angularVelocity;
		IsKinematic = isKinematic;
		IsSleeping = isSleeping;
	}

	public IExternalPhysicsBody Body { get; }
	public Vector3 Position { get; }
	public Quaternion Rotation { get; }
	public Vector3 Velocity { get; }
	public Vector3 AngularVelocity { get; }
	public bool IsKinematic { get; }
	public bool IsSleeping { get; }
}

public interface IExternalPhysicsRuntime : IDisposable
{
	void QueueCasingFrame(ExternalCasingFrame frame);
	ExternalCasingFrame TakeCasingFrame();
	bool IsInitialized { get; }
	bool IsRaidReady { get; }
	bool NeedsSimulation { get; }
	bool IsFullPrebake { get; }
	string Failure { get; }
	string Status { get; }
	IReadOnlyList<Bounds> RecentlyMovedBounds { get; }
	void Initialize(ExternalBackendOptions options);
	void BeginRaid();
	void NotifySceneLoaded(Scene scene);
	void CaptureEnvironmentChanges();
	bool EnsureEnvironment(uint consumerId, Vector3 center, Vector3 sweepEnd, out ExternalEnvironmentAcquireResult result, out string failure);
	bool EnsureCasingEnvironment(uint consumerId, Vector3 center, Vector3 sweepEnd, out string failure);
	void ReleaseEnvironment(uint consumerId);
	bool TryCreateBody(Rigidbody nativeBody, IReadOnlyList<Collider> colliders, uint ownerGroup, bool kinematic, out IExternalPhysicsBody body, out string failure);
	bool TryCreateBody(Rigidbody nativeBody, IReadOnlyList<Collider> colliders, uint ownerGroup, bool kinematic,
		ExternalCollisionProfile collisionProfile, out IExternalPhysicsBody body, out string failure);
	IExternalBodyCreation BeginCreateTransientConvexBody(Rigidbody nativeBody,
		IReadOnlyList<ExternalTransientConvexShape> shapes, uint ownerGroup, bool kinematic,
		ExternalCollisionProfile collisionProfile, out string failure);
	bool TryCreateRoundedBoxBody(Rigidbody nativeBody, IReadOnlyList<Collider> sourceColliders, ExternalRoundedBoxShape roundedBox, uint ownerGroup, bool kinematic, out IExternalPhysicsBody body, out string failure);
	bool TryCreateBoxBody(Rigidbody nativeBody, IReadOnlyList<Collider> sourceColliders, ExternalBoxShape box, uint ownerGroup, bool kinematic, out IExternalPhysicsBody body, out string failure);
	bool TryCreateJoint(Joint nativeJoint, IExternalPhysicsBody body, IExternalPhysicsBody connectedBody, out IExternalPhysicsJoint joint, out string failure);
	void IgnoreCollision(Collider left, Collider right);
	void DestroyJoint(IExternalPhysicsJoint joint);
	void DestroyBody(IExternalPhysicsBody body);
	bool TryApplyBodyStates(IReadOnlyList<ExternalBodyState> states, out string failure);
	bool SubmitStep(float deltaTime, out string failure);
	bool CompleteStep(int timeoutMilliseconds, out ExternalStepResult result, out string failure);
	void ResetRaid();
}

// Optional managed extension. Older backends keep their existing runtime ABI;
// corpse control is advertised only when this extension is present.
public interface IExternalCorpseControlRuntime
{
	bool TryCreateCorpseGrip(IExternalPhysicsBody connectedBody, uint ownerGroup, float slack,
		out IExternalPhysicsBody hand, out IExternalPhysicsJoint tether, out string failure);
	bool TryMoveCorpseGrip(IExternalPhysicsBody hand, Vector3 position, out string failure);
}
