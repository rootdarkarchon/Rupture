using System;

namespace Rupture.Health;

// Persistent anatomy and treatment are distinct: later damage never reopens a treated stump.
internal sealed class BotSeverPolicy
{
    internal const float BleedMultiplier = 4f;
    internal const float FallSeconds = 1f;
    internal const float BlendSeconds = .2f;
    internal const float DefaultMomentumThreshold = 24f;
    internal static bool WasBlacked(float healthBeforeHit) => healthBeforeHit <= 0f && !float.IsInfinity(healthBeforeHit);
    internal static bool CanCommit(bool explosion, bool explosionsOnly, float health, uint reservedHealing, uint currentHealing) =>
        reservedHealing == currentHealing && (explosion || !explosionsOnly && WasBlacked(health));
    internal static bool Accumulate(Impacts.LimbImpactPolicy impact, ulong projectile, float momentum, float threshold, float healthBeforeHit)
    {
        if (!WasBlacked(healthBeforeHit))
        {
            // Remember ignored hits so a repeated callback after blackening cannot
            // retroactively count the projectile that caused it.
            impact.Remember(projectile);
            return false;
        }
        return impact.Add(projectile, momentum, threshold);
    }
    internal static void Healed(Impacts.LimbImpactPolicy impact, bool alive, float recoveredHealth)
    {
        if (alive && Impacts.LimbImpactPolicy.Positive(recoveredHealth)) impact.ResetBuildup();
    }
    internal byte SeveredMask { get; private set; }
    internal byte TreatedMask { get; private set; }
    internal uint Revision { get; private set; }
    internal bool Sever(int leg)
    {
        if ((uint)leg > 1 || (SeveredMask & (1 << leg)) != 0) return false;
        SeveredMask |= (byte)(1 << leg); Revision++; return true;
    }
    internal bool Treat(int leg)
    {
        if (!Untreated(leg)) return false;
        TreatedMask |= (byte)(1 << leg); Revision++; return true;
    }
    internal bool Severed(int leg) => (uint)leg < 2 && (SeveredMask & (1 << leg)) != 0;
    internal bool Untreated(int leg) => Severed(leg) && (TreatedMask & (1 << leg)) == 0;
    internal bool Import(byte severed, byte treated, uint revision)
    {
        if (!Valid(severed, treated) || revision == 0 || revision < Revision ||
            (severed & SeveredMask) != SeveredMask || (treated & TreatedMask) != TreatedMask) return false;
        SeveredMask = severed; TreatedMask = treated; Revision = revision; return true;
    }
    internal static bool Valid(byte severed, byte treated) => severed != 0 && severed <= 3 && (treated & ~severed) == 0;
    internal static float Damage(float nativeDamage) => nativeDamage > 0 && !float.IsInfinity(nativeDamage) &&
        !float.IsNaN(nativeDamage) ? nativeDamage * BleedMultiplier : 0f;
    // Do not catch up a stalled frame with an unbounded lethal burst.
    internal static bool Tick(float delta, float interval, ref float elapsed)
    {
        if (!(delta > 0) || float.IsInfinity(delta) || !(interval > 0) || float.IsInfinity(interval)) return false;
        elapsed += delta;
        if (elapsed < interval) return false;
        elapsed = Math.Min(elapsed - interval, interval); return true;
    }
}
