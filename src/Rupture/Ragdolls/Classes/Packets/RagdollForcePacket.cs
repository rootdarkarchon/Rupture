using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollForceData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public string BodyPath;
	public byte Operation;
	public Vector3 Force;
	public Vector3 Point;
	public float Scalar;
	public float Radius;
	public float UpwardsModifier;
	public byte ForceMode;
}
