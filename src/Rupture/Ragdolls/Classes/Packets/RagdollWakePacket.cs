
namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollWakeData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public byte ReactivateSettled;
	public int DynamicDuration;
}
