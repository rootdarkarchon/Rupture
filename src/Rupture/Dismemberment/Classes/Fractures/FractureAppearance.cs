using System;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Dismemberment.Classes.Fractures
{

// Only profile identity and the canonical body-part number enter this descriptor.
// Separate avalanched streams prevent segment/count/orientation correlations.
internal readonly struct FractureAppearance
{
    internal readonly LimbSeverLevel Level;
    internal readonly bool IsLeg;
    internal readonly int BoneCount, SiteCount, Site;
    internal readonly float Fraction, Angle, Tilt;
    internal readonly uint ShapeSeed;

    internal FractureAppearance(string profileId, int bodyPart)
    {
        if (string.IsNullOrEmpty(profileId) || bodyPart < 3 || bodyPart > 6)
            throw new ArgumentException("Fractures require a stable profile and an arm or leg.");
        uint seed = 2166136261u;
        unchecked
        {
            for (int i = 0; i < profileId.Length; i++) seed = (seed ^ profileId[i]) * 16777619u;
            seed = (seed ^ (uint)bodyPart) * 16777619u;
        }
        IsLeg = bodyPart >= 5;
        Level = Unit(seed, 0x347A2E19u) < 0.5f ? LimbSeverLevel.Proximal : LimbSeverLevel.Distal;
        BoneCount = ProceduralLimbWoundGeometry.AnatomicalBoneCount(Level);
        SiteCount = Unit(seed, 0xC49B0173u) < 0.5f ? 2 : 1;
        Site = 0;
        // Keep a modest gap around the upper-segment midpoint (37.5-42.5%
        // versus 57.5-62.5%). Lower sites retain the existing gib fraction.
        Fraction = Level == LimbSeverLevel.Proximal
            ? LimbSeverMath.ProximalSegmentFraction - 0.1f + (Unit(seed, 0x714F8C29u) - 0.5f) * 0.05f
            : LimbSeverMath.DistalSegmentFraction + (Unit(seed, 0x714F8C29u) - 0.5f) * 0.1f;
        Angle = (Unit(seed, 0xE872A531u) - 0.5f) * 70f;
        Tilt = 20f + Unit(seed, 0x2B63D907u) * 10f;
        ShapeSeed = Mix(seed ^ 0x953EB62Du);
    }

    private FractureAppearance(FractureAppearance source, int site)
    {
        Level = source.Level; IsLeg = source.IsLeg; BoneCount = source.BoneCount;
        SiteCount = source.SiteCount; Site = site;
        Fraction = site == 0 ? source.Fraction : 1f - source.Fraction;
        Angle = source.Angle; Tilt = source.Tilt; ShapeSeed = source.ShapeSeed;
    }

    internal FractureAppearance ForSite(int site)
    {
        if (Site != 0 || site < 0 || site >= SiteCount) throw new ArgumentOutOfRangeException(nameof(site));
        return new FractureAppearance(this, site);
    }

    internal uint BloodSeed => Mix(ShapeSeed ^ ((uint)Site * 0x61C88647u));

    internal uint BoneSeed(int index) => Mix(ShapeSeed ^ ((uint)index * 0x9E3779B9u));
    private static float Unit(uint seed, uint salt) => (Mix(seed ^ salt) >> 8) / 16777216f;
    private static uint Mix(uint value)
    {
        unchecked { value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15;
            value *= 0x846CA68Bu; return value ^ (value >> 16); }
    }
}
}
