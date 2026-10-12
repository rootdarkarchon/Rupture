using EFT;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollStartPacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public EBodyPart BodyPart;
	public Vector3 HitDirection;
	public int DynamicDuration;
	public bool HasInitialVelocity;
	public Vector3 InitialVelocity;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put((byte)BodyPart);
		writer.PutUnmanaged(HitDirection);
		writer.Put(DynamicDuration);
		writer.Put(HasInitialVelocity);
		writer.PutUnmanaged(InitialVelocity);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		BodyPart = (EBodyPart)reader.GetByte();
		HitDirection = reader.GetUnmanaged<Vector3>();
		DynamicDuration = reader.GetInt();
		HasInitialVelocity = reader.GetBool();
		InitialVelocity = reader.GetUnmanaged<Vector3>();
	}
    public static implicit operator RagdollStartData(RagdollStartPacket value) => new RagdollStartData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        BodyPart = value.BodyPart,
        HitDirection = value.HitDirection,
        DynamicDuration = value.DynamicDuration,
        HasInitialVelocity = value.HasInitialVelocity,
        InitialVelocity = value.InitialVelocity,
    };
    public static implicit operator RagdollStartPacket(RagdollStartData value) => new RagdollStartPacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        BodyPart = value.BodyPart,
        HitDirection = value.HitDirection,
        DynamicDuration = value.DynamicDuration,
        HasInitialVelocity = value.HasInitialVelocity,
        InitialVelocity = value.InitialVelocity,
    };

}
