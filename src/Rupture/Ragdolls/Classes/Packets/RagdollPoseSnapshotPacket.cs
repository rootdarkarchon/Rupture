using System;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollPoseSnapshotData
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint SnapshotSequence;
	public float AuthorityTime;
	public float SimulationDeltaTime;
	public byte ChunkIndex;
	public byte ChunkCount;
	public byte TotalPoseCount;
	public RagdollPose[] Poses;
}
