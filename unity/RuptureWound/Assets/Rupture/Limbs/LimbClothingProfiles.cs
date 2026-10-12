using System;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs
{
internal static partial class LimbClothingProfiles
{
    internal const float LooseThreshold = 1.10f;

    internal readonly struct Profile
    {
        internal readonly bool Leg, Gib;
        internal readonly LimbSeverLevel Level;
        internal readonly float Offset, Diameter, Minor, BoneLength;
        internal Profile(bool leg, LimbSeverLevel level, bool gib, float offset,
            float diameter, float minor, float boneLength)
        { Leg = leg; Level = level; Gib = gib; Offset = offset; Diameter = diameter; Minor = minor; BoneLength = boneLength; }
    }

    internal readonly struct BareRegion
    {
        internal readonly string Mesh, Source;
        internal readonly bool Leg, Gib;
        internal readonly LimbSeverLevel Level;
        internal BareRegion(string mesh, string source, bool leg, LimbSeverLevel level, bool gib)
        { Mesh = mesh; Source = source; Leg = leg; Level = level; Gib = gib; }
    }

    internal sealed class Context
    {
        internal Vector3 BoneCenter;
        internal float SegmentLengthWorld, Offset;
        internal string Mesh, Source;
        internal string[] BoneNames;
        internal Matrix4x4 ToBone = Matrix4x4.identity, FromBone = Matrix4x4.identity;
        internal Core SharedCore;
    }

    // Stored in the selected segment's bone-local frame so companion renderers
    // with different bind transforms/LODs attach to the same anatomical ellipse.
    internal sealed class Core
    {
        internal Vector3 Center, Major, Minor;
        internal Vector3[] BackRing;
        internal bool KeepPrimaryUnchanged;
        internal ProceduralLimbWoundGeometry.Surface Interior;
        internal Matrix4x4 InteriorToBone;
        internal string[] BoneNames;
    }

    internal static bool NeedsIdentity(string mesh)
    {
        foreach (BareRegion region in Bare)
            if (string.Equals(region.Mesh, mesh, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static bool IsBare(Context context, bool leg, LimbSeverLevel level, bool gib)
    {
        foreach (BareRegion region in Bare)
            if (region.Leg == leg && region.Level == level && region.Gib == gib &&
                string.Equals(region.Mesh, context.Mesh, StringComparison.Ordinal) &&
                string.Equals(region.Source, context.Source, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static bool TryGet(bool leg, LimbSeverLevel level, bool gib, float offset, out Profile profile)
    {
        foreach (Profile candidate in Values)
            if (candidate.Leg == leg && candidate.Level == level && candidate.Gib == gib &&
                Mathf.Abs(candidate.Offset - offset) < 0.0001f)
            { profile = candidate; return true; }
        profile = default;
        return false;
    }
}
}
