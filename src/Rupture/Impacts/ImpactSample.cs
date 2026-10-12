using EFT;
using UnityEngine;

namespace Rupture.Impacts;

internal struct ImpactData
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

    internal static ImpactData Capture(EFT.Ballistics.Shot shot)
    {
        if (shot == null) return default;
        ImpactData origin = new ImpactData { MasterOrigin = shot.MasterOrigin, HasMasterOrigin = ExplosionSeverPolicy.Finite(shot.MasterOrigin) };
        if (shot.Ammo == null || shot.IsFlyingOutOfTime || shot.AvoidAdditionalDamage || !shot.IsForwardHit) return origin;
        return new ImpactData { Projectile = ProjectileIdentity(shot), MassGrams = shot.BulletMassGram,
            Speed = shot.VelocityMagnitude, Diameter = shot.BulletDiameterMilimeters, FireIndex = shot.FireIndex,
            Caliber = (shot.Ammo as EFT.InventoryLogic.Ammo)?.Caliber, MasterOrigin = origin.MasterOrigin, HasMasterOrigin = origin.HasMasterOrigin };
    }

    internal static ulong ProjectileIdentity(EFT.Ballistics.Shot shot)
    {
        if (shot?.Ammo == null) return 0;
        EFT.Ballistics.Shot root = shot;
        string fragments = string.Empty;
        while (root.Parent != null)
        {
            // Penetration/ricochet continuations are the same projectile. True
            // fragmentation siblings share FragmentIndex but have different seeds.
            if (root.Parent.BulletState == EFT.Ballistics.Shot.EBulletState.FragmentationHit)
                fragments += ":" + root.RandomSeed;
            root = root.Parent;
        }
        string identity = shot.PlayerProfileID + ":" + shot.Ammo.Id + ":" + root.FireIndex + ":" + root.FragmentIndex + fragments;
        ulong hash = 14695981039346656037UL;
        foreach (char ch in identity) { hash ^= ch; hash *= 1099511628211UL; }
        return hash == 0 ? 1UL : hash;
    }


}
