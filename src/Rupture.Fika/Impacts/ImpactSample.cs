using EFT;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace Rupture.Impacts;

internal struct ImpactSample : INetSerializable
{
    public ulong Projectile;
    public float MassGrams;
    public float Speed;
    public float Diameter;
    public int FireIndex;
    public string Caliber;
    public Vector3 MasterOrigin;
    public bool HasMasterOrigin;
    public float Momentum => LimbImpactPolicy.Momentum(MassGrams, Speed);
    public bool Valid => Projectile != 0 && Momentum > 0;



    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Projectile); writer.Put(MassGrams); writer.Put(Speed); writer.Put(Diameter);
        writer.Put(FireIndex); writer.Put(Caliber ?? string.Empty);
        writer.PutUnmanaged(MasterOrigin);
        writer.Put(HasMasterOrigin);
    }
    public void Deserialize(NetDataReader reader)
    {
        Projectile = reader.GetULong(); MassGrams = reader.GetFloat(); Speed = reader.GetFloat();
        Diameter = reader.GetFloat(); FireIndex = reader.GetInt(); Caliber = reader.GetString();
        MasterOrigin = reader.GetUnmanaged<Vector3>();
        HasMasterOrigin = reader.GetBool();
    }

    public static implicit operator ImpactData(ImpactSample value) => new ImpactData
    {
        Projectile = value.Projectile,
        MassGrams = value.MassGrams,
        Speed = value.Speed,
        Diameter = value.Diameter,
        FireIndex = value.FireIndex,
        Caliber = value.Caliber,
        MasterOrigin = value.MasterOrigin,
        HasMasterOrigin = value.HasMasterOrigin,
    };
    public static implicit operator ImpactSample(ImpactData value) => new ImpactSample
    {
        Projectile = value.Projectile,
        MassGrams = value.MassGrams,
        Speed = value.Speed,
        Diameter = value.Diameter,
        FireIndex = value.FireIndex,
        Caliber = value.Caliber,
        MasterOrigin = value.MasterOrigin,
        HasMasterOrigin = value.HasMasterOrigin,
    };
}
