using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

public sealed partial class ExternalPhysXRuntime
{
	public bool TryCreateCorpseGrip(IExternalPhysicsBody connectedBody, uint ownerGroup, float slack,
		out IExternalPhysicsBody hand, out IExternalPhysicsJoint tether, out string failure)
	{
		hand = null;
		tether = null;
		failure = null;
		if (!AtControlBoundary(out failure) || !OwnsControlBody(connectedBody, out ExternalBody connected) ||
			connected.IsKinematic || !Finite(slack) || slack <= 0f)
		{
			failure ??= "invalid corpse grip body or slack";
			return false;
		}

		// Capture all data before invoking the owner; no Unity object crosses it.
		Vector3 point = connected.WorldCenterOfMass;
		Vector3 anchor = Quaternion.Inverse(connected.Rotation) * (point - connected.Position);
		ActorSnapshot snapshot = new ActorSnapshot
		{
			Body = new NativeApi.BodyDesc
			{
				StructSize = (uint)Marshal.SizeOf<NativeApi.BodyDesc>(), Kind = 2,
				Pose = new NativeApi.Pose(point, Quaternion.identity), Mass = 1f, Flags = 2,
				MaxAngularVelocity = 7f, MaxDepenetrationVelocity = 10f,
				SolverPositionIterations = 6, SolverVelocityIterations = 1,
				InertiaTensor = new NativeApi.Vec3(Vector3.one), InertiaRotation = new NativeApi.Quat(Quaternion.identity)
			}
		};
		ExternalBody created = null;
		ulong jointHandle = 0;
		try
		{
			InvokeOwner(() =>
			{
				NativeActor actor = _scene.CreateActor(snapshot); // no shapes or query colliders
				try
				{
					JointSnapshot joint = new JointSnapshot
					{
						Kind = JointSnapshotKind.Distance,
						Distance = new NativeApi.DistanceJointDesc
						{
							StructSize = (uint)Marshal.SizeOf<NativeApi.DistanceJointDesc>(),
							Actor0 = actor.Handle, Actor1 = connected.Handle,
							LocalPose0 = new NativeApi.Pose(Vector3.zero, Quaternion.identity),
							LocalPose1 = new NativeApi.Pose(anchor, Quaternion.identity),
							MinimumDistance = 0f, MaximumDistance = slack, Tolerance = Math.Min(0.001f, slack * 0.1f),
							BreakForce = float.MaxValue, BreakTorque = float.MaxValue,
							MassScale = 1f, ConnectedMassScale = 1f, EnablePreprocessing = 0
						}
					};
					jointHandle = _scene.CreateJoint(joint);
					created = new ExternalBody(this, actor.Handle, Vector3.zero, snapshot.Body);
				}
				catch
				{
					if (jointHandle != 0) _scene.DestroyJoint(jointHandle);
					_scene.DestroyActor(actor.Handle);
					throw;
				}
			});
			RegisterBody(created);
			ExternalJoint createdJoint = new ExternalJoint(jointHandle);
			_joints.Add(jointHandle, createdJoint);
			_jointSnapshot = null;
			hand = created;
			tether = createdJoint;
			_activity.Changed();
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
	}

	public bool TryMoveCorpseGrip(IExternalPhysicsBody hand, Vector3 position, out string failure)
	{
		failure = null;
		if (!AtControlBoundary(out failure) || !OwnsControlBody(hand, out ExternalBody body) ||
			!body.IsKinematic || !Finite(position.x) || !Finite(position.y) || !Finite(position.z))
		{
			failure ??= "invalid kinematic grip target";
			return false;
		}
		NativeApi.Pose pose = new NativeApi.Pose(position, body.Rotation);
		// A kinematic target contributes the hand's velocity to the constraint.
		// Do not change the published cache before CompleteStep reads it back.
		EnqueueOwner(() => _scene?.SetActorPose(body.Handle, pose, kinematicTarget: true));
		return true;
	}

	private bool AtControlBoundary(out string failure)
	{
		failure = null;
		if (!IsRaidReady) { failure = Failure ?? "external scene is not ready"; return false; }
		lock (_ownerGate)
		{
			if (_stepWork != null) { failure = "corpse grip requires a completed-step boundary"; return false; }
		}
		return true;
	}

	private bool OwnsControlBody(IExternalPhysicsBody candidate, out ExternalBody body)
	{
		body = candidate as ExternalBody;
		return body != null && body.IsValid && _bodies.TryGetValue(body.Handle, out ExternalBody current) && ReferenceEquals(body, current);
	}

	private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
