using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Dismemberment.Classes.Packets;

public struct DismembermentPacket : INetSerializable
{
	public int PlayerId;
	public uint DeathSequence;
	public uint EventSequence;
	public Vector3 Direction;
	public EBodyPart BodyPart;
	public LimbSeverLevel SeverLevel;
	public uint IrregularitySeed;
	public bool InitialDeathSpawn;

	public void Serialize(NetDataWriter writer)
	{
		writer.Put(PlayerId);
		writer.Put(DeathSequence);
		writer.Put(EventSequence);
		writer.PutUnmanaged(Direction);
		writer.Put((byte)BodyPart);
		writer.Put((byte)SeverLevel);
		writer.Put(IrregularitySeed);
		writer.Put(InitialDeathSpawn);
	}

	public void Deserialize(NetDataReader reader)
	{
		PlayerId = reader.GetInt();
		DeathSequence = reader.GetUInt();
		EventSequence = reader.GetUInt();
		Direction = reader.GetUnmanaged<Vector3>();
		BodyPart = (EBodyPart)reader.GetByte();
		SeverLevel = (LimbSeverLevel)reader.GetByte();
		IrregularitySeed = reader.GetUInt();
		InitialDeathSpawn = reader.GetBool();
	}
    public static implicit operator DismembermentData(DismembermentPacket value) => new DismembermentData
    {
        PlayerId = value.PlayerId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        Direction = value.Direction,
        BodyPart = value.BodyPart,
        SeverLevel = value.SeverLevel,
        IrregularitySeed = value.IrregularitySeed,
        InitialDeathSpawn = value.InitialDeathSpawn,
    };
    public static implicit operator DismembermentPacket(DismembermentData value) => new DismembermentPacket
    {
        PlayerId = value.PlayerId,
        DeathSequence = value.DeathSequence,
        EventSequence = value.EventSequence,
        Direction = value.Direction,
        BodyPart = value.BodyPart,
        SeverLevel = value.SeverLevel,
        IrregularitySeed = value.IrregularitySeed,
        InitialDeathSpawn = value.InitialDeathSpawn,
    };

}
