using System;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public enum RagdollMotionKind : byte
{
	CoreBody = 0,
	Weapon = 1,
	DetachedGib = 2
}

public struct RagdollPose
{
	public byte IsValid;
	public byte IsSleeping;
	public RagdollMotionKind Kind;
	public byte Reserved;
	public byte BodyIndex;
	public byte EffectIndex;
	public uint EventSequence;
	public Vector3 Position;
	public Quaternion Rotation;
	public Vector3 Velocity;
	public Vector3 AngularVelocity;
}

public struct RagdollSettleData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint SnapshotSequence;
	public float AuthorityTime;
	public float SimulationDeltaTime;
	public RagdollPose[] Poses;
}
