using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollRadialImpulsePacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public Vector3 Origin;
	public float Radius;
	public float Force;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.PutUnmanaged(Origin);
		writer.Put(Radius);
		writer.Put(Force);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		Origin = reader.GetUnmanaged<Vector3>();
		Radius = reader.GetFloat();
		Force = reader.GetFloat();
	}
    public static implicit operator RagdollRadialImpulseData(RagdollRadialImpulsePacket value) => new RagdollRadialImpulseData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        Origin = value.Origin,
        Radius = value.Radius,
        Force = value.Force,
    };
    public static implicit operator RagdollRadialImpulsePacket(RagdollRadialImpulseData value) => new RagdollRadialImpulsePacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        Origin = value.Origin,
        Radius = value.Radius,
        Force = value.Force,
    };

}
