using System;
using UnityEngine;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Impacts;

// Distance selects a limb; the independent level roll selects its initial cut.
// Keep this value-only policy usable by the managed regression harness.
internal static class ExplosionSeverPolicy
{
    internal const float GuaranteedRadius = 0.6f;
    internal const float OuterRadius = 2f;

    internal static float Probability(float distance)
    {
        if (!Finite(distance) || distance < 0f || distance >= OuterRadius) return 0f;
        if (distance <= GuaranteedRadius) return 1f;
        float t = (distance - GuaranteedRadius) / (OuterRadius - GuaranteedRadius);
        return 1f - t * t * (3f - 2f * t);
    }

    internal static bool Select(float distance, float roll)
    {
        if (!Finite(roll) || roll < 0f || roll > 1f) return false;
        float chance = Probability(distance);
        // Unity's random value includes 1: the guaranteed zone must still win.
        return chance >= 1f || roll < chance;
    }

    internal static LimbSeverLevel Level(float roll) =>
        roll < 0.5f ? LimbSeverLevel.Proximal : LimbSeverLevel.Distal;

    internal static float Distance(Vector3 origin, Vector3 proximal, Vector3 joint, Vector3 distal,
        out Vector3 nearest)
    {
        nearest = default;
        if (!Finite(origin) || !Finite(proximal) || !Finite(joint) || !Finite(distal)) return float.PositiveInfinity;
        Vector3 upper = ClosestPoint(origin, proximal, joint);
        Vector3 lower = ClosestPoint(origin, joint, distal);
        nearest = (origin - upper).sqrMagnitude < (origin - lower).sqrMagnitude ? upper : lower;
        return (float)Math.Sqrt((origin - nearest).sqrMagnitude);
    }

    private static Vector3 ClosestPoint(Vector3 point, Vector3 start, Vector3 end)
    {
        Vector3 segment = end - start;
        float lengthSquared = segment.sqrMagnitude;
        if (lengthSquared <= 0f) return start;
        float t = Math.Max(0f, Math.Min(1f, Vector3.Dot(point - start, segment) / lengthSquared));
        return start + t * segment;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
