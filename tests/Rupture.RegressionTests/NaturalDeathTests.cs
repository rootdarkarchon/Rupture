using System;
using System.Linq;
using System.Reflection;
using EFT;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

internal static class NaturalDeathTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    internal static void Run(Assembly assembly)
    {
        Type impacts = assembly.GetType("Rupture.Impacts.ImpactStateService", true);
        Type victim = impacts.GetNestedType("VictimState", Flags);
        void Set(object state, string field, object value) => state.GetType().GetField(field, Flags).SetValue(state, value);
        object Get(object state, string field) => state.GetType().GetField(field, Flags).GetValue(state);
        foreach (string cause in new[] { "HeavyBleeding", "LightBleeding", "Landmine", "Explosion", "Sniper", "Poison", "Undefined" })
        {
            if (!Enum.TryParse(cause, out EDamageType type)) continue;
            object state = Activator.CreateInstance(victim, true);
            Set(state, "DeathQueued", true); Set(state, "QueuedDeathCause", type);
            object[] args = { state, default(EFT.Ballistics.DamageInfo) };
            Check((bool)impacts.GetMethod("ConsumeQueuedDeath", Flags).Invoke(null, args), "unhandled death supplies lifecycle: " + cause);
            var damage = (EFT.Ballistics.DamageInfo)args[1];
            Check(damage.DamageType == type && damage.Damage == 0 && damage.MasterOrigin == Vector3.zero && damage.Direction == Vector3.zero,
                "fallback preserves cause without inventing spatial damage: " + cause);
            Check(!(bool)impacts.GetMethod("ConsumeQueuedDeath", Flags).Invoke(null, args), "queued death is consumed once: " + cause);
        }
        foreach (string handled in new[] { "DeathSent", "DeathApplied", "AwaitingDeathSnapshot" })
        {
            object state = Activator.CreateInstance(victim, true); Set(state, "DeathQueued", true); Set(state, handled, true);
            Check(!(bool)impacts.GetMethod("ConsumeQueuedDeath", Flags).Invoke(null, new object[] { state, default(EFT.Ballistics.DamageInfo) }),
                "fallback respects damage handling and owner snapshot authority: " + handled);
        }

        Type policy = assembly.GetType("Rupture.Impacts.DeathImpactPolicy", true);
        Type sampleType = assembly.GetType("Rupture.Impacts.ImpactData", true);
        object sample = Activator.CreateInstance(sampleType);
        Set(sample, "Projectile", 12UL); Set(sample, "MassGrams", 10f); Set(sample, "Speed", 800f);
        Set(sample, "HasMasterOrigin", true); Set(sample, "MasterOrigin", new Vector3(50, 10, 50));
        object Prepare(EFT.Ballistics.DamageInfo damage, bool scoped = false, Vector3 origin = default) =>
            policy.GetMethod("PrepareSample", Flags).Invoke(null, new object[] { damage, sample, scoped, origin });
        bool Spatial(EFT.Ballistics.DamageInfo damage, object captured) => (bool)policy.GetMethod("HasSpatialExplosion", Flags).Invoke(null, new[] { (object)damage, captured });
        var mine = new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Landmine, Damage = 100 };
        object unknown = Prepare(mine);
        Check(!(bool)Get(unknown, "HasMasterOrigin") && !Spatial(mine, unknown), "synthetic minefield death cannot borrow stale projectile origin or imply a world-zero blast");
        object worldZero = Prepare(mine, true);
        Check((bool)Get(worldZero, "HasMasterOrigin") && Spatial(mine, worldZero) && (ulong)Get(worldZero, "Projectile") == 0,
            "explicit world-zero explosion retains spatial effects without projectile momentum");
        mine.MasterOrigin = new Vector3(2, 3, 4);
        object native = Prepare(mine, true, new Vector3(9, 9, 9));
        Check((Vector3)Get(native, "MasterOrigin") == mine.MasterOrigin && Spatial(mine, native), "native radial center takes precedence over unrelated samples and scopes");
        mine.MasterOrigin = new Vector3(float.NaN, 0, 0);
        Check(!Spatial(mine, Prepare(mine)), "nonfinite blast origins cannot drive spatial effects");
        mine.Damage = 0; mine.MasterOrigin = Vector3.zero;
        Check(!(bool)Get(Prepare(mine, true), "HasMasterOrigin") && !Spatial(mine, worldZero), "environmental lifecycle-only fallback cannot synthesize explosion damage");
        var fragment = new EFT.Ballistics.DamageInfo { DamageType = EDamageType.GrenadeFragment, Damage = 100 };
        object restored = Prepare(fragment);
        Check((Vector3)Get(restored, "MasterOrigin") == (Vector3)Get(sample, "MasterOrigin") && (ulong)Get(restored, "Projectile") == 0,
            "fragment envelope preserves its launch origin without adopting bullet gore eligibility");
        object bullet = Prepare(new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet, Damage = 100 });
        Check((ulong)Get(bullet, "Projectile") == 12UL && (float)Get(bullet, "Speed") == 800f, "ordinary lethal bullet retains its captured momentum");

        Type coordinator = assembly.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator", true);
        object ragdoll = Activator.CreateInstance(coordinator.GetNestedType("RagdollState", Flags), true);
        Set(ragdoll, "AuthorityMotionEpochActive", true);
        foreach (var identity in new[] { (-1, 0u), (4, 0u), (4, 7u) })
        {
            Set(ragdoll, "PlayerId", identity.Item1); Set(ragdoll, "DeathSequence", identity.Item2);
            coordinator.GetMethod("SendSettle", Flags).Invoke(null, new[] { ragdoll });
            Check(!(bool)Get(ragdoll, "SettleSent") && (bool)Get(ragdoll, "AuthorityMotionEpochActive"),
                "missing settle identity or transport remains retryable: " + identity);
        }
        using var module = AssemblyDefinition.ReadAssembly(assembly.Location);
        MethodDefinition Method(Type type, string name) => module.MainModule.GetType(type.FullName).Methods.Single(m => m.Name == name);
        var update = Method(impacts, "Update").Body.Instructions;
        Check(update.Select(i => i.Operand).OfType<MethodReference>().First().Name == "ProcessPendingDeaths", "death fallback runs before the once-per-second prune gate");
        var pendingInstructions = Method(impacts, "ProcessPendingDeaths").Body.Instructions;
        var pending = pendingInstructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(pending.Any(m => m.Name == "get_IsAlive") && pending.Any(m => m.Name == "TryGetValue") &&
            pendingInstructions.Any(i => i.OpCode == OpCodes.Bne_Un || i.OpCode == OpCodes.Bne_Un_S),
            "fallback checks revival and compares the queued lifetime against the current victim registry");
        var settle = Method(coordinator, "SendSettle").Body.Instructions.ToArray();
        int send = Array.FindIndex(settle, i => i.Operand is MethodReference m && m.Name == "Send");
        int sent = Array.FindIndex(settle, i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "SettleSent");
        Check(send >= 0 && sent > send, "settle completion is committed after the transport call");
        var hooks = module.MainModule.GetType("Rupture.Impacts.ImpactHooks");
        Check(hooks.Methods.Single(m => m.Name == "Install").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "OnDead") &&
            typeof(Player).GetMethod("OnDead").GetParameters().Single().Name == "damageType", "common Player.OnDead hook matches the installed EFT ABI");
    }
}
