using System;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollSettlePacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint SnapshotSequence;
	public float AuthorityTime;
	public float SimulationDeltaTime;
	public RagdollPose[] Poses;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(SnapshotSequence);
		writer.Put(AuthorityTime);
		writer.Put(SimulationDeltaTime);
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
		Poses = reader.GetUnmanagedArray<RagdollPose>();
	}
    public static implicit operator RagdollSettleData(RagdollSettlePacket value) => new RagdollSettleData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        SnapshotSequence = value.SnapshotSequence,
        AuthorityTime = value.AuthorityTime,
        SimulationDeltaTime = value.SimulationDeltaTime,
        Poses = value.Poses,
    };
    public static implicit operator RagdollSettlePacket(RagdollSettleData value) => new RagdollSettlePacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        SnapshotSequence = value.SnapshotSequence,
        AuthorityTime = value.AuthorityTime,
        SimulationDeltaTime = value.SimulationDeltaTime,
        Poses = value.Poses,
    };

}
