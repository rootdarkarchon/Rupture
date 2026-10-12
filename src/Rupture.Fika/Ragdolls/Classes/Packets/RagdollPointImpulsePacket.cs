using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollPointImpulsePacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public string BodyPath;
	public Vector3 LocalPoint;
	public Vector3 Impulse;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.Put(BodyPath ?? string.Empty);
		writer.PutUnmanaged(LocalPoint);
		writer.PutUnmanaged(Impulse);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		BodyPath = reader.GetString();
		LocalPoint = reader.GetUnmanaged<Vector3>();
		Impulse = reader.GetUnmanaged<Vector3>();
	}
    public static implicit operator RagdollPointImpulseData(RagdollPointImpulsePacket value) => new RagdollPointImpulseData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        BodyPath = value.BodyPath,
        LocalPoint = value.LocalPoint,
        Impulse = value.Impulse,
    };
    public static implicit operator RagdollPointImpulsePacket(RagdollPointImpulseData value) => new RagdollPointImpulsePacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        BodyPath = value.BodyPath,
        LocalPoint = value.LocalPoint,
        Impulse = value.Impulse,
    };

}
