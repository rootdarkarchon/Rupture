using System;
using Fika.Core.Networking.LiteNetLib.Utils;
using Rupture.Health;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;

namespace Rupture.Impacts;

internal struct BotSeverPacket : INetSerializable
{
    public string Session;
    public BotSeverData Data;
    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Session); writer.Put(Data.ProfileId); writer.Put(Data.PlayerId);
        writer.Put(Data.Revision); writer.Put(Data.PoseSequence); writer.Put(Data.SeveredMask); writer.Put(Data.TreatedMask);
        writer.Put(Data.Phase); writer.Put(Data.Remaining); writer.PutUnmanaged(Data.RootPosition); writer.PutUnmanaged(Data.RootRotation);
        writer.Put((byte)Data.LeftLevel); writer.Put((byte)Data.RightLevel);
        writer.Put(Data.LeftEvent); writer.Put(Data.RightEvent); writer.Put(Data.LeftSeed); writer.Put(Data.RightSeed);
        writer.PutUnmanaged(Data.LeftDirection); writer.PutUnmanaged(Data.RightDirection);
        int count = Data.BonePaths?.Length ?? 0;
        if (count > 32) throw new FormatException("Living pose exceeds 32 bones.");
        writer.Put((byte)count);
        for (int i = 0; i < count; i++)
        { writer.Put(Data.BonePaths[i]); writer.PutUnmanaged(Data.Positions[i]); writer.PutUnmanaged(Data.Rotations[i]); }
    }
    public void Deserialize(NetDataReader reader)
    {
        Session = reader.GetString(32); Data = new BotSeverData { ProfileId = reader.GetString(128), PlayerId = reader.GetInt(),
            Revision = reader.GetUInt(), PoseSequence = reader.GetUInt(), SeveredMask = reader.GetByte(), TreatedMask = reader.GetByte(),
            Phase = reader.GetByte(), Remaining = reader.GetFloat(), RootPosition = reader.GetUnmanaged<Vector3>(),
            RootRotation = reader.GetUnmanaged<Quaternion>(), LeftLevel = (LimbSeverLevel)reader.GetByte(), RightLevel = (LimbSeverLevel)reader.GetByte(),
            LeftEvent = reader.GetUInt(), RightEvent = reader.GetUInt(), LeftSeed = reader.GetUInt(), RightSeed = reader.GetUInt(),
            LeftDirection = reader.GetUnmanaged<Vector3>(), RightDirection = reader.GetUnmanaged<Vector3>() };
        int count = reader.GetByte();
        if (count > 32) throw new FormatException("Living pose exceeds 32 bones.");
        Data.BonePaths = new string[count]; Data.Positions = new Vector3[count]; Data.Rotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        { Data.BonePaths[i] = reader.GetString(256); Data.Positions[i] = reader.GetUnmanaged<Vector3>(); Data.Rotations[i] = reader.GetUnmanaged<Quaternion>(); }
        if (!BotSeverStateService.Valid(Data)) throw new FormatException("Invalid living sever state.");
    }
}
