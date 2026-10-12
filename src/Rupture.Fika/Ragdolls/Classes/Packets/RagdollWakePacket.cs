using Fika.Core.Networking.LiteNetLib.Utils;

namespace Rupture.Ragdolls.Classes.Packets;

public struct RagdollWakePacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public byte ReactivateSettled;
	public int DynamicDuration;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.Put(ReactivateSettled);
		writer.Put(DynamicDuration);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		ReactivateSettled = reader.GetByte();
		DynamicDuration = reader.GetInt();
	}
    public static implicit operator RagdollWakeData(RagdollWakePacket value) => new RagdollWakeData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        ReactivateSettled = value.ReactivateSettled,
        DynamicDuration = value.DynamicDuration,
    };
    public static implicit operator RagdollWakePacket(RagdollWakeData value) => new RagdollWakePacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        ReactivateSettled = value.ReactivateSettled,
        DynamicDuration = value.DynamicDuration,
    };

}
