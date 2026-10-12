using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Packets;

public struct HeadWoundPacket : INetSerializable
{
	public int PlayerId;
	public string ProfileId;
	public uint DeathSequence;
	public uint EventSequence;
	public byte WoundOrdinal;
	public bool IsLethal;
	public Vector3 EntryLocal;
	public Vector3 DirectionLocal;
	public float EntryRadius;
	public float ExitRadius;
	public float ConeDepth;
	public uint IrregularitySeed;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(ProfileId ?? string.Empty);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.Put(WoundOrdinal);
		writer.Put(IsLethal);
		writer.PutUnmanaged(EntryLocal);
		writer.PutUnmanaged(DirectionLocal);
		writer.Put(EntryRadius);
		writer.Put(ExitRadius);
		writer.Put(ConeDepth);
		writer.Put(IrregularitySeed);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		ProfileId = reader.GetString();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		WoundOrdinal = reader.GetByte();
		IsLethal = reader.GetBool();
		EntryLocal = reader.GetUnmanaged<Vector3>();
		DirectionLocal = reader.GetUnmanaged<Vector3>();
		EntryRadius = reader.GetFloat();
		ExitRadius = reader.GetFloat();
		ConeDepth = reader.GetFloat();
		IrregularitySeed = reader.GetUInt();
	}
    public static implicit operator HeadWoundData(HeadWoundPacket value) => new HeadWoundData
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        WoundOrdinal = value.WoundOrdinal,
        IsLethal = value.IsLethal,
        EntryLocal = value.EntryLocal,
        DirectionLocal = value.DirectionLocal,
        EntryRadius = value.EntryRadius,
        ExitRadius = value.ExitRadius,
        ConeDepth = value.ConeDepth,
        IrregularitySeed = value.IrregularitySeed,
    };
    public static implicit operator HeadWoundPacket(HeadWoundData value) => new HeadWoundPacket
    {
        PlayerId = value.PlayerId,
        ProfileId = value.ProfileId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        WoundOrdinal = value.WoundOrdinal,
        IsLethal = value.IsLethal,
        EntryLocal = value.EntryLocal,
        DirectionLocal = value.DirectionLocal,
        EntryRadius = value.EntryRadius,
        ExitRadius = value.ExitRadius,
        ConeDepth = value.ConeDepth,
        IrregularitySeed = value.IrregularitySeed,
    };

}
