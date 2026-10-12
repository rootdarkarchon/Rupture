using EFT;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollStartData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public EBodyPart BodyPart;
	public Vector3 HitDirection;
	public int DynamicDuration;
	public bool HasInitialVelocity;
	public Vector3 InitialVelocity;
}
