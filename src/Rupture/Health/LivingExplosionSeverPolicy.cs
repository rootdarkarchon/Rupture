using System;
using System.Collections.Generic;
using Rupture.Impacts;

namespace Rupture.Health;

internal static class LivingExplosionSeverPolicy
{
    internal const float GuaranteedRadius = .5f;
    internal static float Probability(float distance, float radius, float damage, float maximumHealth)
    {
        if (!LimbImpactPolicy.Positive(radius) || !LimbImpactPolicy.Positive(damage) ||
            !LimbImpactPolicy.Positive(maximumHealth) || float.IsNaN(distance) || float.IsInfinity(distance) ||
            distance < 0f || distance >= radius) return 0f;
        if (distance <= GuaranteedRadius) return 1f;
        float remaining = 1f - (distance - GuaranteedRadius) / (radius - GuaranteedRadius);
        return remaining * remaining * remaining * Math.Min(1f, damage / maximumHealth);
    }
    internal static bool Select(float distance, float radius, float damage, float maximumHealth, float roll)
    {
        if (float.IsNaN(roll) || float.IsInfinity(roll) || roll < 0f || roll > 1f) return false;
        float chance = Probability(distance, radius, damage, maximumHealth);
        return chance >= 1f || roll < chance;
    }
    internal static bool Remember(Dictionary<long, byte> events, long sequence, int leg)
    {
        if (sequence <= 0 || (uint)leg > 1) return false;
        events.TryGetValue(sequence, out byte mask);
        byte bit = (byte)(1 << leg);
        if ((mask & bit) != 0) return false;
        events[sequence] = (byte)(mask | bit);
        if (events.Count > 128)
        {
            long oldest = long.MaxValue;
            foreach (long key in events.Keys) if (key < oldest) oldest = key;
            events.Remove(oldest);
        }
        return true;
    }
}
