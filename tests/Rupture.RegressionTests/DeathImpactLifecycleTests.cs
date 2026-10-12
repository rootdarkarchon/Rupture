using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;

internal static class DeathImpactLifecycleTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    internal static void Run(Assembly assembly)
    {
        Type coordinator = assembly.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator", true);
        Type ragdollType = coordinator.GetNestedType("RagdollState", Flags);
        Type impacts = assembly.GetType("Rupture.Impacts.ImpactStateService", true);
        Type victimType = impacts.GetNestedType("VictimState", Flags);
        Type targetType = assembly.GetType("Rupture.Ragdolls.Classes.CorpseInteractionTarget", true);
        Type policy = assembly.GetType("Rupture.Impacts.LimbImpactPolicy", true);
        var victims = (IDictionary)impacts.GetField("States", Flags).GetValue(null);
        var ragdolls = (IDictionary)coordinator.GetField("StatesByProfile", Flags).GetValue(null);
        MethodInfo replace = coordinator.GetMethod("DetachStateForDeathRegistration", Flags);
        MethodInfo detach = coordinator.GetMethod("DetachState", Flags);
        object Invoke(object instance, string method, params object[] args) =>
            policy.GetMethod(method, Flags).Invoke(instance, args);
        void Set(object instance, string name, object value) => instance.GetType().GetField(name, Flags).SetValue(instance, value);
        bool Flag(object instance, string name) => (bool)instance.GetType().GetField(name, Flags).GetValue(instance);

        // Fika TryApply resolves the already-created corpse before registering the
        // owner's death. This binds Player while DeathSequence remains zero.
        object owner = RuntimeHelpers.GetUninitializedObject(typeof(EFT.Player));
        object otherOwner = RuntimeHelpers.GetUninitializedObject(typeof(EFT.Player));
        object boundCorpse = Activator.CreateInstance(ragdollType, true);
        Set(boundCorpse, "Player", owner);
        MethodInfo duplicate = coordinator.GetMethod("IsDuplicateDeathRegistration", Flags);
        bool Duplicate(object player) => (bool)duplicate.Invoke(null, new[] { boundCorpse, player });
        Check(!Duplicate(owner), "owner-bound provisional corpse must receive its first death sequence and lifecycle start");
        Set(boundCorpse, "DeathSequence", 7u);
        Check(Duplicate(owner), "repeated owner snapshot cannot allocate another death sequence");
        Check(!Duplicate(otherOwner), "another player instance must not reuse the previous death registration");
        Set(boundCorpse, "ObservedAliveAfterDeath", true);
        Check(!Duplicate(owner), "a new death after revival is not suppressed");

        // Exercise the production registration cleanup, with plain managed state and
        // no Unity objects. The wiring check below ensures RegisterDeath uses this path.
        foreach (bool imported in new[] { false, true })
        {
            string profile = "death-impact-regression-" + imported;
            object victim = Activator.CreateInstance(victimType, true);
            object provisional = Activator.CreateInstance(ragdollType, true);
            Set(provisional, "ProfileId", profile);
            if (imported) Set(provisional, "Player", owner);
            Set(victim, "DeathSent", !imported);
            Set(victim, "DeathApplied", true);
            Array limbs = (Array)victimType.GetField("Limbs", Flags).GetValue(victim);
            object leg = limbs.GetValue(2);
            if (imported)
            {
                foreach (object limb in limbs) Invoke(limb, "Import", 12f, 12f);
                Invoke(leg, "Remember", 42UL);
            }
            else
            {
                Invoke(limbs.GetValue(0), "Add", 41UL, 3.6f, 12f);
                Invoke(leg, "Add", 42UL, 28.3f * 0.001f * 537.5f, 12f);
            }
            victims.Add(profile, victim);
            ragdolls.Add(profile, provisional);
            try
            {
                object lifetime = ragdollType.GetField("ImpactLifetime", Flags).GetValue(provisional);
                Check(ReferenceEquals(replace.Invoke(null, new[] { provisional, imported ? owner : null }), lifetime),
                    "first death registration preserves the pending corpse impact lifetime");
                Check(!ragdolls.Contains(profile) && ReferenceEquals(victims[profile], victim),
                    "death registration replaces provisional bookkeeping without deleting " + (imported ? "owner snapshot" : "lethal impact"));
                Check(Flag(victim, "DeathApplied") && Flag(victim, "DeathSent") == !imported &&
                    !Flag(victim, "AwaitingDeathSnapshot"), "death handoff flags survive registration");
                Check(!(bool)Invoke(leg, "Add", 42UL, 15.21f, 12f), "lethal projectile cannot contribute again after registration");
                Check((float)policy.GetProperty("Total", Flags).GetValue(limbs.GetValue(0)) == (imported ? 12f : 3.6f),
                    "other limb buildup survives the lethal transition");

                object target = Activator.CreateInstance(targetType);
                Set(target, "ProfileId", profile);
                var begin = impacts.GetMethod("BeginSever", Flags);
                Check((bool)begin.Invoke(null, new[] { target, (object)EBodyPart.LeftLeg, 1f }),
                    "12ga lethal leg impact admits sever immediately after death registration");
                Check(!(bool)begin.Invoke(null, new[] { target, (object)EBodyPart.LeftLeg, 0f }),
                    "duplicate lethal evaluation cannot reserve a second sever");
                impacts.GetMethod("Complete", Flags).Invoke(null, new[] { target, (object)EBodyPart.LeftLeg, false });
                Check((bool)Invoke(leg, "Add", 43UL, 15.21f, 12f) &&
                    (bool)begin.Invoke(null, new[] { target, (object)EBodyPart.LeftLeg, 1f }),
                    "distinct postmortem projectile can retry a rejected lethal cut");
                impacts.GetMethod("Complete", Flags).Invoke(null, new[] { target, (object)EBodyPart.LeftLeg, true });
                Check(!(bool)Invoke(leg, "Add", 44UL, 15.21f, 12f), "committed lethal sever remains final for corpse impacts");

                // Simulate the registered replacement and its eventual real teardown.
                object registered = Activator.CreateInstance(ragdollType, true);
                Set(registered, "ProfileId", profile);
                Set(registered, "DeathSequence", 1u);
                ragdolls.Add(profile, registered);
                detach.Invoke(null, new[] { registered, (object)true });
                Check(!victims.Contains(profile) && !ragdolls.Contains(profile), "real corpse teardown still clears impact state");
            }
            finally { victims.Remove(profile); ragdolls.Remove(profile); }
        }

        foreach (string reason in new[] { "registered death", "observed alive again", "different player" })
        {
            string profile = "death-impact-retirement-" + reason;
            object old = Activator.CreateInstance(ragdollType, true);
            Set(old, "ProfileId", profile);
            if (reason == "registered death") Set(old, "DeathSequence", 9u);
            if (reason == "observed alive again") Set(old, "ObservedAliveAfterDeath", true);
            if (reason == "different player") Set(old, "Player", RuntimeHelpers.GetUninitializedObject(typeof(EFT.Player)));
            victims.Add(profile, Activator.CreateInstance(victimType, true));
            ragdolls.Add(profile, old);
            try
            {
                Check(replace.Invoke(null, new[] { old, null }) == null, "new death must not inherit pending impacts: " + reason);
                Check(!victims.Contains(profile), "replacement still clears stale impact state: " + reason);
            }
            finally { victims.Remove(profile); ragdolls.Remove(profile); }
        }

        using var module = AssemblyDefinition.ReadAssembly(assembly.Location);
        var registration = module.MainModule.GetType(coordinator.FullName).Methods.Single(m => m.Name == "RegisterDeath");
        var calls = registration.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(calls.Any(m => m.Name == replace.Name) && !calls.Any(m => m.Name == detach.Name),
            "compiled RegisterDeath uses the tested provisional-state replacement path");
        Check(calls.Any(m => m.Name == duplicate.Name),
            "compiled RegisterDeath uses sequence-aware duplicate detection");
        using var bridge = AssemblyDefinition.ReadAssembly(Program.Bridge.Location);
        var reconcileCalls = bridge.MainModule.GetType("Rupture.Impacts.FikaImpactNetwork")
            .Methods.Single(m => m.Name == "TryApply").Body.Instructions
            .Select(i => i.Operand).OfType<MethodReference>().ToArray();
        int bindOwner = Array.FindLastIndex(reconcileCalls, m => m.Name == "TryGetInteractionTarget");
        Check(bindOwner >= 0 && bindOwner < Array.FindIndex(reconcileCalls, m => m.Name == "ApplyDeath"),
            "Fika owner death reconciliation exercises target binding before death registration");
        Check((bool)detach.GetParameters()[1].DefaultValue, "ordinary detach defaults to full impact cleanup");
    }
}
