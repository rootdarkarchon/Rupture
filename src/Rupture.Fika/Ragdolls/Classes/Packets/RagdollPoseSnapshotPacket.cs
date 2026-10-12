using System;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollPoseSnapshotPacket : INetSerializable
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

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(SnapshotSequence);
		writer.Put(AuthorityTime);
		writer.Put(SimulationDeltaTime);
		writer.Put(ChunkIndex);
		writer.Put(ChunkCount);
		writer.Put(TotalPoseCount);
		writer.PutUnmanagedArray(Poses ?? Array.Empty<RagdollPose>());
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		SnapshotSequence = reader.GetUInt();
		AuthorityTime = reader.GetFloat();
		SimulationDeltaTime = reader.GetFloat();
		ChunkIndex = reader.GetByte();
		ChunkCount = reader.GetByte();
		TotalPoseCount = reader.GetByte();
		Poses = reader.GetUnmanagedArray<RagdollPose>();
	}
    public static implicit operator RagdollPoseSnapshotData(RagdollPoseSnapshotPacket value) => new RagdollPoseSnapshotData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        SnapshotSequence = value.SnapshotSequence,
        AuthorityTime = value.AuthorityTime,
        SimulationDeltaTime = value.SimulationDeltaTime,
        ChunkIndex = value.ChunkIndex,
        ChunkCount = value.ChunkCount,
        TotalPoseCount = value.TotalPoseCount,
        Poses = value.Poses,
    };
    public static implicit operator RagdollPoseSnapshotPacket(RagdollPoseSnapshotData value) => new RagdollPoseSnapshotPacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        SnapshotSequence = value.SnapshotSequence,
        AuthorityTime = value.AuthorityTime,
        SimulationDeltaTime = value.SimulationDeltaTime,
        ChunkIndex = value.ChunkIndex,
        ChunkCount = value.ChunkCount,
        TotalPoseCount = value.TotalPoseCount,
        Poses = value.Poses,
    };

}
