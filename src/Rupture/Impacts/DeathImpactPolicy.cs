using EFT;
using UnityEngine;

namespace Rupture.Impacts;

internal static class DeathImpactPolicy
{
    internal static bool IsExplosion(EDamageType type) => type == EDamageType.Landmine ||
        type == EDamageType.Explosion || type == EDamageType.GrenadeFragment;

    internal static ImpactData PrepareSample(EFT.Ballistics.DamageInfo damage, ImpactData sample, bool hasScopedOrigin, Vector3 scopedOrigin)
    {
        if (ImpactStateService.Eligible(damage)) return sample;
        if (!IsExplosion(damage.DamageType) || !LimbImpactPolicy.Positive(damage.Damage)) return default;
        // Fragment envelopes retain an explicitly captured launch origin. Radial
        // damage owns its center independently of an enclosing projectile scope.
        if (damage.DamageType == EDamageType.GrenadeFragment && sample.HasMasterOrigin && ExplosionSeverPolicy.Finite(sample.MasterOrigin))
            return new ImpactData { MasterOrigin = sample.MasterOrigin, HasMasterOrigin = true };
        if (ExplosionSeverPolicy.Finite(damage.MasterOrigin) && damage.MasterOrigin != Vector3.zero)
            return new ImpactData { MasterOrigin = damage.MasterOrigin, HasMasterOrigin = true };
        if (hasScopedOrigin && ExplosionSeverPolicy.Finite(scopedOrigin))
            return new ImpactData { MasterOrigin = scopedOrigin, HasMasterOrigin = true };
        // Minefield damage can omit every spatial field. Zero is only a known
        // world origin when an explicit native explosion scope supplied it.
        return default;
    }

    internal static bool HasSpatialExplosion(EFT.Ballistics.DamageInfo damage, ImpactData sample) =>
        IsExplosion(damage.DamageType) && LimbImpactPolicy.Positive(damage.Damage) && ExplosionSeverPolicy.Finite(damage.MasterOrigin) &&
        (damage.MasterOrigin != Vector3.zero || (sample.HasMasterOrigin && ExplosionSeverPolicy.Finite(sample.MasterOrigin) && sample.MasterOrigin == damage.MasterOrigin));
}
