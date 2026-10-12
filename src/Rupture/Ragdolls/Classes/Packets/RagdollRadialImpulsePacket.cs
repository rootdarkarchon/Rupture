using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollRadialImpulseData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public Vector3 Origin;
	public float Radius;
	public float Force;
}
