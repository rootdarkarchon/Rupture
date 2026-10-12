using System;
using System.Reflection;
using UnityEngine;

internal static class ExplosionSeverTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    internal static void Run(Assembly assembly)
    {
        Type policy = assembly.GetType("Rupture.Impacts.ExplosionSeverPolicy", true);
        float Chance(float distance) => (float)policy.GetMethod("Probability", Flags).Invoke(null, new object[] { distance });
        bool Select(float distance, float roll) => (bool)policy.GetMethod("Select", Flags).Invoke(null, new object[] { distance, roll });
        string Level(float roll) => policy.GetMethod("Level", Flags).Invoke(null, new object[] { roll }).ToString();
        float Distance(Vector3 origin, Vector3 a, Vector3 b, Vector3 c)
        {
            object[] args = { origin, a, b, c, default(Vector3) };
            return (float)policy.GetMethod("Distance", Flags).Invoke(null, args);
        }

        Check(Chance(0f) == 1f && Chance(0.6f) == 1f && Select(0.6f, 1f), "inner 0.6 m guarantees selection even at the inclusive random endpoint");
        Check(Chance(2f) == 0f && Chance(20f) == 0f && !Select(2f, 0f), "outer 2 m excludes limbs even with a zero random roll");
        Check(Math.Abs(Chance(1.3f) - 0.5f) < 0.00001f, "falloff midpoint has 50 percent probability");
        float previous = 1f;
        for (int i = 0; i <= 2000; i++)
        {
            float chance = Chance(i / 1000f);
            if (chance < 0f || chance > previous) throw new Exception("Explosion falloff is not bounded and monotonic.");
            previous = chance;
        }
        Check(true, "probability decreases monotonically across the complete blast range");
        foreach (float invalid in new[] { -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Check(Chance(invalid) == 0f && !Select(invalid, 0f), "invalid distance fails closed: " + invalid);
        Check(!Select(0f, float.NaN) && !Select(0f, -1f) && !Select(0f, 1.1f), "invalid random values cannot select a limb");

        // Standing anatomical segments, grenade between the feet. Both sides
        // are separate inputs; hands remain above the guaranteed zone.
        Vector3 blast = new Vector3(0, 0.05f, 0);
        float leftLeg = Distance(blast, new Vector3(-0.16f, 0.95f, 0), new Vector3(-0.16f, 0.5f, 0), new Vector3(-0.16f, 0.1f, 0));
        float rightLeg = Distance(blast, new Vector3(0.16f, 0.95f, 0), new Vector3(0.16f, 0.5f, 0), new Vector3(0.16f, 0.1f, 0));
        float leftArm = Distance(blast, new Vector3(-0.3f, 1.5f, 0), new Vector3(-0.35f, 1.2f, 0), new Vector3(-0.3f, 0.9f, 0));
        float rightArm = Distance(blast, new Vector3(0.3f, 1.5f, 0), new Vector3(0.35f, 1.2f, 0), new Vector3(0.3f, 0.9f, 0));
        Check(Select(leftLeg, 1f) && Select(rightLeg, 1f), "grenade between standing feet guarantees both leg selections");
        Check(Chance(leftArm) > 0f && Chance(leftArm) < 1f && Chance(rightArm) > 0f && Chance(rightArm) < 1f,
            "standing arms have independent nonzero, nonguaranteed chances");
        Check(!Select(leftArm, 1f) && !Select(rightArm, 1f) && Select(leftArm, 0f) && !Select(rightArm, 1f) &&
            !Select(leftArm, 1f) && Select(rightArm, 0f) && Select(leftArm, 0f) && Select(rightArm, 0f),
            "independent arm rolls allow neither, left, right, or both");
        Vector3 distantBlast = new Vector3(5, 0.05f, 0);
        Check(!Select(Distance(distantBlast, new Vector3(0, 1, 0), new Vector3(0, 0.5f, 0), new Vector3(0, 0.1f, 0)), 0f),
            "distant fragment origin cannot cause close-blast leg severing");
        Check(Math.Abs(Distance(new Vector3(0.3f, 0.75f, 0), new Vector3(0, 1, 0), new Vector3(0, 0.5f, 0), new Vector3(0, 0, 0)) - 0.3f) < 0.00001f,
            "distance uses the nearest point along a segment rather than a cut or bone endpoint");
        Check(Math.Abs(Distance(new Vector3(0, 0.3f, 0.75f), new Vector3(0, 0, 1), new Vector3(0, 0, 0.5f), new Vector3(0, 0, 0)) - 0.3f) < 0.00001f,
            "lying limb geometry follows the same distance policy");
        Check(Distance(new Vector3(1, 0, 0), default, default, default) == 1f &&
            float.IsPositiveInfinity(Distance(new Vector3(float.NaN, 0, 0), default, default, default)),
            "degenerate and invalid segment inputs are handled without NaN probabilities");
        int proximal = 0;
        for (int i = 0; i < 1000; i++) if (Level((i + 0.5f) / 1000f) == "Proximal") proximal++;
        Check(proximal == 500 && Level(0.5f) == "Distal" && Level(1f) == "Distal", "cut-level roll splits evenly between proximal and distal independently of distance");

        Type scope = assembly.GetType("Rupture.Impacts.ExplosionEventScope", true);
        object Enter(Vector3 center) => scope.GetMethod("Enter", Flags).Invoke(null, new object[] { center });
        void Restore(object previousScope) => scope.GetMethod("Restore", Flags).Invoke(null, new[] { previousScope });
        long Sequence(Vector3 center) => (long)scope.GetMethod("SequenceFor", Flags).Invoke(null, new object[] { center });
        bool Current(long sequence) => (bool)scope.GetMethod("IsCurrent", Flags).Invoke(null, new object[] { sequence });
        object outer = Enter(blast);
        long first = Sequence(blast);
        Type coordinator = assembly.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator", true);
        Type stateType = coordinator.GetNestedType("RagdollState", BindingFlags.NonPublic);
        object state = Activator.CreateInstance(stateType, true);
        stateType.GetField("InitialExplosionImpactPending", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(state, true);
        stateType.GetField("InitialExplosionEvent", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(state, first);
        bool Consume() => (bool)coordinator.GetMethod("ConsumeInitialExplosionImpact", Flags).Invoke(null, new[] { state });
        try
        {
            Check(first != 0 && Current(first) && Sequence(distantBlast) == 0, "initial corpse suppression matches the active explosion center");
            object inner = Enter(blast);
            long second = Sequence(blast);
            try { Check(second != first && !Current(first) && Current(second) && !Consume(), "a nested explosion at the same position is a distinct event"); }
            finally { Restore(inner); }
            Check(Current(first), "nested explosion completion restores the outer event");
            Check(Consume() && !Consume(), "the real coordinator consumes lethal suppression once without losing it to a nested blast");
        }
        finally { Restore(outer); }
        Check(Sequence(blast) == 0 && !Current(first), "delayed fragment deaths cannot retain initial blast suppression");
        object later = Enter(blast);
        try { Check(!Current(first), "a later grenade at the same position is not suppressed by the lethal blast"); }
        finally { Restore(later); }
    }
}
