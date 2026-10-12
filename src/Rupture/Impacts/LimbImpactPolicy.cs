using System;
using System.Collections.Generic;

namespace Rupture.Impacts;

// Pure policy: no Unity physics, ammo tables, presentation multipliers, or RNG ownership.
internal sealed class LimbImpactPolicy
{
    internal const float DefaultThreshold = 12f;
    internal const float MaximumThreshold = 120f;
    internal float Total { get; private set; }
    internal bool Pending { get; private set; }
    internal bool Severed { get; private set; }
    internal uint HealingRevision { get; private set; }
    private readonly HashSet<ulong> _projectiles = new HashSet<ulong>();

    internal static bool Positive(float value) => value > 0 && !float.IsNaN(value) && !float.IsInfinity(value);
    internal static float Momentum(float massGrams, float speed) =>
        Positive(massGrams) && Positive(speed) && Positive(massGrams * 0.001f * speed) ? massGrams * 0.001f * speed : 0f;
    internal static float Chance(float total, float threshold = DefaultThreshold)
    {
        if (!Positive(total)) return 0f;
        float fraction = Math.Min(1f, total / ValidThreshold(threshold));
        return fraction * fraction;
    }

    private static float ValidThreshold(float threshold) => Positive(threshold) ? threshold : DefaultThreshold;

    internal bool Add(ulong projectile, float momentum, float threshold = DefaultThreshold)
    {
        if (Severed || projectile == 0 || !Positive(momentum) || !_projectiles.Add(projectile)) return false;
        Total = Math.Min(ValidThreshold(threshold), Total + momentum);
        return true;
    }

    internal void Remember(ulong projectile) { if (projectile != 0) _projectiles.Add(projectile); }

    internal bool Begin(bool dead, float roll, float threshold = DefaultThreshold)
    {
        float chance = Chance(Total, threshold);
        if (!dead || Pending || Severed || Total <= 0 || float.IsNaN(roll) || roll < 0 || roll > 1 || (chance < 1 && roll >= chance)) return false;
        Pending = true;
        return true;
    }

    internal void Complete(bool committed) { Pending = false; Severed |= committed; }
    internal void ResetBuildup()
    {
        Total = 0f; HealingRevision++;
        // Keep identities and pending ownership until its cut transaction completes.
    }
    internal void Heal(bool alive, float delta, float current, float maximum)
    {
        if (alive && Positive(delta) && Positive(maximum) && Positive(current) && current >= maximum) Total = 0;
        // Retain projectile identities so a delayed/repeated callback cannot restore healed buildup.
    }
    internal void Import(float total, float threshold = DefaultThreshold)
    {
        if (!Pending && !Severed) Total = Positive(total) ? Math.Min(ValidThreshold(threshold), total) : 0;
    }
}
