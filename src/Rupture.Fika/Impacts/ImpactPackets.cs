using EFT;
using System;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Impacts;

internal struct RuptureHelloPacket : INetSerializable
{
    public int Protocol;
    public string Version;
    public string Session;
    public bool IsHeadlessRequester;
    public uint ConfigurationRevision;
    public string[] VisualConfiguration;
    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Protocol); writer.Put(Version); writer.Put(Session); writer.Put(IsHeadlessRequester); writer.Put(ConfigurationRevision);
        writer.Put((byte)(VisualConfiguration?.Length ?? 0));
        if (VisualConfiguration != null) foreach (string value in VisualConfiguration) writer.Put(value);
    }
    public void Deserialize(NetDataReader reader)
    {
        Protocol = reader.GetInt();
        if (Protocol != FikaImpactNetwork.Protocol) throw new FormatException("Unsupported Rupture configuration protocol.");
        Version = reader.GetString(32); Session = reader.GetString(32); IsHeadlessRequester = reader.GetBool();
        ConfigurationRevision = reader.GetUInt();
        int count = reader.GetByte();
        if (count != 0 && count != RuptureConfiguration.SynchronizedCount) throw new FormatException("Invalid visual configuration count.");
        VisualConfiguration = count == 0 ? null : new string[count];
        for (int i = 0; i < count; i++) VisualConfiguration[i] = reader.GetString(64);
    }
}

// The victim network ID, unmodified stock DamagePacket bytes and momentum travel as one message.
// Stock damage is deserialized and executed once, never reconstructed or reapplied.
internal struct RuptureDamageEnvelope : INetSerializable
{
    public string Session;
    public uint Sequence;
    public ImpactSample Sample;
    public byte[] Damage;
    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Session); writer.Put(Sequence); Sample.Serialize(writer); writer.PutBytesWithLength(Damage);
    }
    public void Deserialize(NetDataReader reader)
    {
        Session = reader.GetString(32); Sequence = reader.GetUInt(); Sample.Deserialize(reader); Damage = reader.GetBytesWithLength();
    }
}

internal enum ImpactStateOperation : byte { Death = 1, Corpse = 2 }
internal struct RuptureImpactStatePacket : INetSerializable
{
    public string Session;
    public uint Sequence;
    public ImpactStateOperation Operation;
    public string ProfileId;
    public int PlayerId;
    public uint DeathSequence;
    public EBodyPart Part;
    public EBodyPartColliderType ColliderType;
    public EDamageType DamageType;
    public float Damage;
    public float Penetration;
    public Vector3 Point;
    public Vector3 Direction;
    public Vector3 MasterOrigin;
    public ImpactSample Sample;
    public float LeftArm, RightArm, LeftLeg, RightLeg;
    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Session); writer.Put(Sequence); writer.Put((byte)Operation); writer.Put(ProfileId); writer.Put(PlayerId);
        writer.Put(DeathSequence); writer.Put((byte)Part); writer.Put((int)ColliderType); writer.Put((int)DamageType);
        writer.Put(Damage); writer.Put(Penetration); writer.PutUnmanaged(Point); writer.PutUnmanaged(Direction); Sample.Serialize(writer);
        writer.Put(LeftArm); writer.Put(RightArm); writer.Put(LeftLeg); writer.Put(RightLeg);
        writer.PutUnmanaged(MasterOrigin);
    }
    public void Deserialize(NetDataReader reader)
    {
        Session = reader.GetString(32); Sequence = reader.GetUInt(); Operation = (ImpactStateOperation)reader.GetByte();
        ProfileId = reader.GetString(128); PlayerId = reader.GetInt(); DeathSequence = reader.GetUInt();
        Part = (EBodyPart)reader.GetByte(); ColliderType = (EBodyPartColliderType)reader.GetInt(); DamageType = (EDamageType)reader.GetInt();
        Damage = reader.GetFloat(); Penetration = reader.GetFloat(); Point = reader.GetUnmanaged<Vector3>(); Direction = reader.GetUnmanaged<Vector3>();
        Sample.Deserialize(reader); LeftArm = reader.GetFloat(); RightArm = reader.GetFloat(); LeftLeg = reader.GetFloat(); RightLeg = reader.GetFloat();
        MasterOrigin = reader.GetUnmanaged<Vector3>();
    }
}
