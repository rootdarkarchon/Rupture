using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Packets;

public struct BloodStreamPacket : INetSerializable
{
	public BloodStreamOperation Operation;
	public BloodStreamTriggerKind TriggerKind;
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public uint StreamId;
	public EBodyPart BodyPart;
	public EBodyPartColliderType ColliderType;
	public string AnchorBone;
	public Vector3 LocalPoint;
	public Vector3 LocalDirection;
	public float Duration;
	public float PeakSpeed;
	public float Radius;
	public float PulsePeriod;
	public uint Seed;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put((byte)Operation);
		writer.Put((byte)TriggerKind);
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.Put(StreamId);
		writer.Put((byte)BodyPart);
		writer.Put((byte)ColliderType);
		writer.Put(AnchorBone ?? string.Empty);
		writer.PutUnmanaged(LocalPoint);
		writer.PutUnmanaged(LocalDirection);
		writer.Put(Duration);
		writer.Put(PeakSpeed);
		writer.Put(Radius);
		writer.Put(PulsePeriod);
		writer.Put(Seed);
	}

	public void Deserialize(NetDataReader reader)
	{
		Operation = (BloodStreamOperation)reader.GetByte();
		TriggerKind = (BloodStreamTriggerKind)reader.GetByte();
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		StreamId = reader.GetUInt();
		BodyPart = (EBodyPart)reader.GetByte();
		ColliderType = (EBodyPartColliderType)reader.GetByte();
		AnchorBone = reader.GetString();
		LocalPoint = reader.GetUnmanaged<Vector3>();
		LocalDirection = reader.GetUnmanaged<Vector3>();
		Duration = reader.GetFloat();
		PeakSpeed = reader.GetFloat();
		Radius = reader.GetFloat();
		PulsePeriod = reader.GetFloat();
		Seed = reader.GetUInt();
	}
    public static implicit operator BloodStreamData(BloodStreamPacket value) => new BloodStreamData
    {
        Operation = value.Operation,
        TriggerKind = value.TriggerKind,
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        StreamId = value.StreamId,
        BodyPart = value.BodyPart,
        ColliderType = value.ColliderType,
        AnchorBone = value.AnchorBone,
        LocalPoint = value.LocalPoint,
        LocalDirection = value.LocalDirection,
        Duration = value.Duration,
        PeakSpeed = value.PeakSpeed,
        Radius = value.Radius,
        PulsePeriod = value.PulsePeriod,
        Seed = value.Seed,
    };
    public static implicit operator BloodStreamPacket(BloodStreamData value) => new BloodStreamPacket
    {
        Operation = value.Operation,
        TriggerKind = value.TriggerKind,
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        StreamId = value.StreamId,
        BodyPart = value.BodyPart,
        ColliderType = value.ColliderType,
        AnchorBone = value.AnchorBone,
        LocalPoint = value.LocalPoint,
        LocalDirection = value.LocalDirection,
        Duration = value.Duration,
        PeakSpeed = value.PeakSpeed,
        Radius = value.Radius,
        PulsePeriod = value.PulsePeriod,
        Seed = value.Seed,
    };

}

public struct BloodStreamRequestPacket : INetSerializable
{
	public BloodStreamPacket Descriptor;

	public void Serialize(NetDataWriter writer)
	{
		Descriptor.Serialize(writer);
	}

	public void Deserialize(NetDataReader reader)
	{
		Descriptor.Deserialize(reader);
	}
    public static implicit operator BloodStreamRequestData(BloodStreamRequestPacket value) => new BloodStreamRequestData
    {
        Descriptor = value.Descriptor,
    };
    public static implicit operator BloodStreamRequestPacket(BloodStreamRequestData value) => new BloodStreamRequestPacket
    {
        Descriptor = value.Descriptor,
    };

}
