using UnityEngine;

namespace Rupture.Dismemberment.Classes.Packets;

public enum BloodStreamOperation : byte
{
	Start = 1,
	Stop = 2
}

public enum BloodStreamTriggerKind : byte
{
	HeavyWound = 1,
	KillEntry = 2,
	HeadKillPending = 3,
	DismembermentStump = 4,
	PostMortemStumpFlow = 5,
	LightWound = 6,
	LivingSeverStump = 7
}

public struct BloodStreamData
{
	public BloodStreamOperation Operation;
	public BloodStreamTriggerKind TriggerKind;
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public uint StreamId;
	public EBodyPart BodyPart;
	public EBodyPartColliderType ColliderType;
	public string AnchorBone;
	public Vector3 LocalPoint;
	public Vector3 LocalDirection;
	public float Duration;
	public float PeakSpeed;
	public float Radius;
	public float PulsePeriod;
	public uint Seed;
}

public struct BloodStreamRequestData
{
	public BloodStreamData Descriptor;
}
