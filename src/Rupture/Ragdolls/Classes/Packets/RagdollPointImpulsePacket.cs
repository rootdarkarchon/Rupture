using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollPointImpulseData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public string BodyPath;
	public Vector3 LocalPoint;
	public Vector3 Impulse;
}
