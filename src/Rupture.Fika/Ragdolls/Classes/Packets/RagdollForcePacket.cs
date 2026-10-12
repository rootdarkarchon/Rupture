using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollForcePacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public string BodyPath;
	public byte Operation;
	public Vector3 Force;
	public Vector3 Point;
	public float Scalar;
	public float Radius;
	public float UpwardsModifier;
	public byte ForceMode;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.Put(BodyPath ?? string.Empty);
		writer.Put(Operation);
		writer.PutUnmanaged(Force);
		writer.PutUnmanaged(Point);
		writer.Put(Scalar);
		writer.Put(Radius);
		writer.Put(UpwardsModifier);
		writer.Put(ForceMode);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		BodyPath = reader.GetString();
		Operation = reader.GetByte();
		Force = reader.GetUnmanaged<Vector3>();
		Point = reader.GetUnmanaged<Vector3>();
		Scalar = reader.GetFloat();
		Radius = reader.GetFloat();
		UpwardsModifier = reader.GetFloat();
		ForceMode = reader.GetByte();
	}
    public static implicit operator RagdollForceData(RagdollForcePacket value) => new RagdollForceData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        BodyPath = value.BodyPath,
        Operation = value.Operation,
        Force = value.Force,
        Point = value.Point,
        Scalar = value.Scalar,
        Radius = value.Radius,
        UpwardsModifier = value.UpwardsModifier,
        ForceMode = value.ForceMode,
    };
    public static implicit operator RagdollForcePacket(RagdollForceData value) => new RagdollForcePacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        BodyPath = value.BodyPath,
        Operation = value.Operation,
        Force = value.Force,
        Point = value.Point,
        Scalar = value.Scalar,
        Radius = value.Radius,
        UpwardsModifier = value.UpwardsModifier,
        ForceMode = value.ForceMode,
    };

}
