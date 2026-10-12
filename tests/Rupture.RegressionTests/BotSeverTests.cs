using System;
using System.Linq;
using System.Reflection;
using EFT.HealthSystem;
using EFT;
using Fika.Core.Networking.LiteNetLib.Utils;
using Mono.Cecil;
using UnityEngine;

internal static class BotSeverTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static void Run(Assembly core, Assembly bridge)
    {
        void Check(bool condition, string label)
        { if (!condition) throw new Exception("Living sever: " + label); Console.WriteLine("PASS: living sever " + label); }
        Type policyType = core.GetType("Rupture.Health.BotSeverPolicy", true);
        Type rolePolicy = core.GetType("Rupture.Health.BotSeverRolePolicy", true);
        bool RoleAllowed(WildSpawnType? role, bool special) => (bool)rolePolicy.GetMethod("Allowed", Flags).Invoke(null, new object[] { role, special });
        var ordinary = new[] { WildSpawnType.assault, WildSpawnType.marksman, WildSpawnType.cursedAssault,
            WildSpawnType.assaultGroup, WildSpawnType.pmcBEAR, WildSpawnType.pmcUSEC };
        foreach (WildSpawnType role in Enum.GetValues(typeof(WildSpawnType)))
        {
            Check(RoleAllowed(role, false) == ordinary.Contains(role), "default ordinary-bot whitelist: " + role);
            Check(RoleAllowed(role, true), "explicit special AI override admits humanoid role: " + role);
        }
        Check(!RoleAllowed(null, false) && !RoleAllowed(null, true), "missing role metadata cannot authorize a live cut");
        Check(!RoleAllowed((WildSpawnType)9999, false), "unknown future roles remain protected by default");
        object policy = Activator.CreateInstance(policyType, true);
        object Call(string name, params object[] args) => policyType.GetMethod(name, Flags).Invoke(policy, args);
        byte Mask(string name) => (byte)policyType.GetProperty(name, Flags).GetValue(policy);
        foreach (float hp in new[] { 80f, 1f, .01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Check(!(bool)Call("WasBlacked", hp), "a positive or invalid pre-hit health cannot authorize severing: " + hp);
        Check((bool)Call("WasBlacked", 0f), "a subsequent hit on an already blacked leg can authorize severing");
        Type impactType = core.GetType("Rupture.Impacts.LimbImpactPolicy", true);
        object corpseImpact = Activator.CreateInstance(impactType, true), livingImpact = Activator.CreateInstance(impactType, true);
        MethodInfo add = impactType.GetMethod("Add", Flags);
        object blackedImpact = Activator.CreateInstance(impactType, true);
        bool Accumulate(ulong projectile, float hp) => (bool)Call("Accumulate", blackedImpact, projectile, 6f, 24f, hp);
        float BlackedTotal() => (float)impactType.GetProperty("Total", Flags).GetValue(blackedImpact);
        Check(!Accumulate(10, 80f) && !Accumulate(11, 1f) && BlackedTotal() == 0f,
            "healthy hits and the blackening shot contribute no living-leg momentum");
        Check(!Accumulate(11, 0f) && BlackedTotal() == 0f,
            "a repeated blackening-shot callback cannot retroactively start buildup");
        Check(Accumulate(12, 0f) && BlackedTotal() == 6f,
            "the first distinct hit on a blacked leg starts buildup from zero");
        Check(!Accumulate(12, 0f) && BlackedTotal() == 6f, "post-blackening buildup still deduplicates projectiles");
        Check(Accumulate(13, 0f) && Accumulate(14, 0f) && Accumulate(15, 0f) && BlackedTotal() == 24f,
            "only post-blackening shots can reach the living severing threshold");
        foreach (float recovery in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
        { Call("Healed", blackedImpact, true, recovery); Check(BlackedTotal() == 24f, "non-healing changes do not reset buildup: " + recovery); }
        Call("Healed", blackedImpact, false, 1f);
        Check(BlackedTotal() == 24f, "dead actors do not acquire a living healing reset");
        uint healingRevision = (uint)impactType.GetProperty("HealingRevision", Flags).GetValue(blackedImpact);
        Check((bool)impactType.GetMethod("Begin", Flags).Invoke(blackedImpact, new object[] { true, 0f, 24f }),
            "fixture reserves a sever before healing");
        Call("Healed", blackedImpact, true, .001f);
        Check(BlackedTotal() == 0f && (uint)impactType.GetProperty("HealingRevision", Flags).GetValue(blackedImpact) != healingRevision,
            "even tiny partial regeneration resets buildup and invalidates a pending reservation");
        impactType.GetMethod("Complete", Flags).Invoke(blackedImpact, new object[] { false });
        Check(!Accumulate(15, 0f) && Accumulate(16, 0f) && BlackedTotal() == 6f,
            "healing retains projectile deduplication and subsequent black-leg hits start fresh");
        for (ulong shot = 1; shot <= 4; shot++)
        { add.Invoke(corpseImpact, new object[] { shot, 6f, 12f }); add.Invoke(livingImpact, new object[] { shot, 6f, 24f }); }
        Check((float)impactType.GetProperty("Total", Flags).GetValue(corpseImpact) == 12f &&
            (float)impactType.GetProperty("Total", Flags).GetValue(livingImpact) == 24f,
            "independent living buildup reaches 24 while corpse buildup remains capped at 12");
        Check(!(bool)Call("Treat", 0) && !(bool)Call("Sever", -1) && !(bool)Call("Sever", 2), "intact and invalid limbs cannot be treated or severed");
        Check((bool)Call("Sever", 0) && Mask("SeveredMask") == 1 && (bool)Call("Untreated", 0), "first leg becomes untreated");
        Check(!(bool)Call("Sever", 0) && (uint)policyType.GetProperty("Revision", Flags).GetValue(policy) == 1, "duplicate sever does not restart an injury");
        Check((bool)Call("Treat", 0) && Mask("TreatedMask") == 1 && Mask("SeveredMask") == 1, "surgery treats bleeding without restoring anatomy");
        Check(!(bool)Call("Treat", 0) && !(bool)Call("Sever", 0) && !(bool)Call("Untreated", 0), "later damage cannot reopen a treated stump");
        Check((bool)Call("Sever", 1) && Mask("SeveredMask") == 3 && (bool)Call("Untreated", 1), "second leg independently bleeds");
        Check(!(bool)Call("Import", (byte)3, (byte)0, 4u), "network state cannot undo successful surgery");
        Check(!(bool)Call("Import", (byte)1, (byte)1, 4u), "network state cannot regrow a leg");
        Check(!(bool)Call("Import", (byte)3, (byte)1, 1u), "stale revision is rejected");
        Check(!(bool)Call("Import", (byte)1, (byte)2, 4u), "treatment outside sever mask is rejected");
        Check((bool)Call("Import", (byte)3, (byte)3, 4u) && Mask("TreatedMask") == 3, "authority treatment snapshot applies monotonically");
        foreach (float amount in new[] { .1f, 1f, 7.5f })
            Check(Math.Abs((float)Call("Damage", amount) - amount * 4f) < .00001f, "matched heavy-bleed damage is multiplied fourfold: " + amount);
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Check((float)Call("Damage", invalid) == 0f, "invalid bleed damage cannot enter health processing");
        object[] tick = { .25f, 1f, 0f };
        MethodInfo tickMethod = policyType.GetMethod("Tick", Flags);
        for (int i = 0; i < 3; i++) Check(!(bool)tickMethod.Invoke(null, tick), "bleed waits for its native interval");
        Check((bool)tickMethod.Invoke(null, tick) && (float)tick[2] == 0f, "bleed fires at the native interval");
        tick = new object[] { 30f, 1f, 0f };
        Check((bool)tickMethod.Invoke(null, tick) && (float)tick[2] <= 1f, "a hitch does not schedule an unbounded catch-up burst");

        Type dataType = core.GetType("Rupture.Health.BotSeverData", true);
        Type service = core.GetType("Rupture.Health.BotSeverStateService", true);
        object data = Activator.CreateInstance(dataType);
        void Set(string name, object value) => dataType.GetField(name).SetValue(data, value);
        bool Valid() => (bool)service.GetMethod("Valid", Flags).Invoke(null, new[] { data });
        Set("ProfileId", "bot-profile"); Set("PlayerId", 17); Set("Revision", 1u); Set("PoseSequence", 1u);
        Set("SeveredMask", (byte)1); Set("LeftEvent", 23u); Set("LeftSeed", 42u); Set("RootRotation", Quaternion.identity);
        Type levelType = dataType.GetField("LeftLevel").FieldType;
        Set("LeftLevel", Enum.Parse(levelType, "Proximal"));
        Check(Valid(), "bounded prone state is valid without a pose allocation");
        Set("LeftLevel", Enum.ToObject(levelType, 0)); Check(!Valid(), "undefined zero cut level rejected");
        Set("LeftLevel", Enum.Parse(levelType, "Distal")); Check(Valid(), "distal cut level accepted using the actual limb enum");
        Set("SeveredMask", (byte)3); Set("RightEvent", 24u); Set("RightLevel", Enum.Parse(levelType, "Distal"));
        Check(Valid(), "both distal leg cut levels accepted");
        Set("RightLevel", Enum.ToObject(levelType, 3)); Check(!Valid(), "undefined right cut level rejected");
        Set("SeveredMask", (byte)1);
        Set("TreatedMask", (byte)2); Check(!Valid(), "invalid treatment mask rejected"); Set("TreatedMask", (byte)0);
        Set("Remaining", float.NaN); Check(!Valid(), "nonfinite fall clock rejected"); Set("Remaining", 0f);
        Set("RootRotation", new Quaternion(0, 0, 0, 0)); Check(!Valid(), "invalid root rotation rejected"); Set("RootRotation", Quaternion.identity);
        Set("BonePaths", new[] { "Bip001 Pelvis" }); Set("Positions", new[] { new Vector3(1, 1, 1) });
        Set("Rotations", new[] { Quaternion.identity }); Set("Phase", (byte)1); Set("Remaining", .8f);
        Check(Valid(), "falling pose uses bounded bone identities");
        Set("Positions", new[] { new Vector3(100, 0, 0) }); Check(!Valid(), "distant pose rejected before native transforms change");
        Set("Positions", new[] { new Vector3(1, 1, 1) });
        Set("LeftDirection", new Vector3(float.NaN, 0, 0)); Check(!Valid(), "nonfinite sever direction rejected");
        Set("LeftDirection", Vector3.right);
        Set("BonePaths", new[] { "." }); Check(Valid(), "skeleton root path is a valid pose identity");
        Set("BonePaths", new[] { "Bip001 Pelvis" });
        Type packetType = bridge.GetType("Rupture.Impacts.BotSeverPacket", true);
        object packet = Activator.CreateInstance(packetType);
        packetType.GetField("Session").SetValue(packet, new string('a', 32)); packetType.GetField("Data").SetValue(packet, data);
        var writer = new NetDataWriter(); ((INetSerializable)packet).Serialize(writer);
        object decoded = Activator.CreateInstance(packetType); var reader = new NetDataReader(writer.CopyData());
        ((INetSerializable)decoded).Deserialize(reader);
        object received = packetType.GetField("Data").GetValue(decoded);
        Check(reader.AvailableBytes == 0 && (byte)dataType.GetField("SeveredMask").GetValue(received) == 1 &&
            ((string[])dataType.GetField("BonePaths").GetValue(received)).Single() == "Bip001 Pelvis" &&
            dataType.GetField("LeftLevel").GetValue(received).Equals(Enum.Parse(levelType, "Distal")), "distal state and pose wire roundtrip completely");
        Set("BonePaths", Array.Empty<string>()); Set("Positions", Array.Empty<Vector3>()); Set("Rotations", Array.Empty<Quaternion>());
        packetType.GetField("Data").SetValue(packet, data); writer.Reset(); ((INetSerializable)packet).Serialize(writer);
        byte[] wire = writer.CopyData(); wire[wire.Length - 1] = 255;
        bool rejected = false;
        try { ((INetSerializable)Activator.CreateInstance(packetType)).Deserialize(new NetDataReader(wire)); }
        catch { rejected = true; }
        Check(rejected, "malformed wire cannot populate an unsafe pose");

        using var module = AssemblyDefinition.ReadAssembly(core.Location);
        using var game = AssemblyDefinition.ReadAssembly(typeof(ActiveHealthController).Assembly.Location);
        var hooks = module.MainModule.GetType("Rupture.Health.BotSeverHooks");
        string[] Calls(string type, string method) => module.MainModule.GetType(type).Methods.Single(m => m.Name == method).Body.Instructions
            .Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        Check(Calls(service.FullName, "Eligible").Contains("get_IsAI") && Calls(service.FullName, "Eligible").Contains("get_IsYourPlayer"), "eligibility explicitly excludes human players");
        Check(Calls(service.FullName, "Eligible").Contains("Allowed") && Calls(service.FullName, "Eligible").Contains("get_LivingLegSeveringSpecialAI") &&
            Calls(service.FullName, "BeforeCutCommit").Contains("Eligible"),
            "authority checks special AI eligibility before buildup and rechecks before committing a queued cut");
        Check(Calls("Rupture.Impacts.ImpactHooks", "DamagePrefix").Contains("GetBodyPartHealth") &&
            Calls(service.FullName, "Damaged").Contains("WasBlacked"), "live severing reads pre-damage health rather than the blackening hit result");
        Check(Calls("Rupture.Impacts.ImpactStateService", "RecordDamage").Contains("get_LivingLegMomentumThreshold") &&
            Calls("Rupture.Impacts.ImpactStateService", "RecordDamage").Contains("Accumulate") &&
            Calls(service.FullName, "Damaged").Contains("get_LivingLegMomentumThreshold") &&
            module.MainModule.GetType("Rupture.Impacts.ImpactStateService").Methods.Single(m => m.Name == "RecordDamage")
                .Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "LivingLegs"),
            "live momentum accumulation and attempts both use the independent ledger and threshold");
        Check(hooks.Methods.Single(m => m.Name == "SurgeryPrefix").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_interrupted"), "interrupted item effects cannot authorize treatment");
        Check(Calls(hooks.FullName, "RestorePrefix").Contains("get_HealthController") && Calls(hooks.FullName, "RestorePrefix").Contains("get_BodyPart"), "surgery authorization is scoped to the exact health controller and limb");
        Check(Calls(hooks.FullName, "RestorePostfix").Contains("Restored") && Calls(hooks.FullName, "FullRestorePostfix").Contains("Restored"),
            "successful surgery and full restoration reset living buildup");
        Check(Calls(hooks.FullName, "TreatmentPostfix").Contains("Restored") &&
            Calls(hooks.FullName, "WasTreated").Contains("get_Existing") &&
            hooks.Methods.Single(m => m.Name == "CanTreat").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "HpResource") &&
            hooks.Methods.Single(m => m.Name == "TreatmentPostfix").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_interrupted"),
            "non-HP leg treatments require uninterrupted completion, an existing condition, and sufficient item resource");
        Check(Calls("Rupture.Impacts.ImpactHooks", "HealPrefix").Contains("GetBodyPartHealth") &&
            Calls("Rupture.Impacts.ImpactStateService", "Healed").Contains("Healed"),
            "healing reset uses actual health recovery including partial and passive gains");
        Check(Calls(service.FullName, "BeforeCutCommit").Contains("get_HealingRevision"),
            "a healed then re-blacked leg cannot commit an older sever reservation");
        Check(!Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "BeginLivingFall").Any(n => n == "OnDead" || n == "Kill" || n == "ToggleDowned"), "temporary ragdoll never fabricates death or downing");
        Check(Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "BeginLivingFall").Contains("RemovedLivingBone") &&
            Calls("Rupture.Ragdolls.Classes.RagdollCoordinator", "DisableLimbColliders").Contains("SeverLivingLeg") &&
            module.MainModule.GetType("Rupture.Ragdolls.Classes.RagdollCoordinator").Methods.Single(m => m.Name == "GetLivingLimbColliders")
                .Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "BodyPartColliders"),
            "living cuts prune solver topology and resolve query colliders without a corpse cache");
        var native = game.MainModule.GetType("EFT.HealthSystem.ActiveHealthController");
        Check(native.Methods.Any(m => m.Name == "NetworkSyncBodyHealth" && m.IsPublic) && native.Methods.Any(m => m.Name == "FullRestoreBodyPart"), "installed health ABI supports capped synchronized treatment");
        Check(Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "Simulate").Count(n => n == "SubmitStep") == 1,
            "living physics shares the existing single scene step owner");
        Check(Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "CompleteExternalStep").Contains("PublishLivingPhysics"), "living poses come from completed external steps");
        Check(Calls(service.FullName, "Death").Contains("CancelLivingFall"), "terminal death retires living physics before corpse construction");
        Check(Calls(service.FullName, "Death").Contains("CancelLivingCuts") &&
            Calls(service.FullName, "Death").Contains("PreserveLivingSeverForCorpse"),
            "terminal death cancels pending living geometry and transfers committed anatomy before corpse construction");
        Check(Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "TryCreateBody").Contains("IsPreseveredBody") &&
            Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "TryCreateBody").Contains("GetAttachedBodyColliders"),
            "corpse admission distinguishes missing anatomy from an invalid intact collider source");
        var cuts = module.MainModule.GetType("Rupture.Dismemberment.Classes.Limbs.LimbDismembermentSystem");
        Check(cuts.Methods.Single(m => m.Name == "TryCreateGib").Body.Instructions.Any(i =>
            i.Operand is MethodReference method && method.Name == "set_enabled" && i.Previous.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0),
            "uncommitted detached geometry is hidden during asynchronous admission");
        var commitInstructions = cuts.Methods.Single(m => m.Name == "CommitPrepared").Body.Instructions;
        int ownershipBoundary = commitInstructions.ToList().FindIndex(i => i.Operand is FieldReference f && f.Name == "Committed");
        int gibReveal = commitInstructions.ToList().FindIndex(i => i.Operand is FieldReference f && f.Name == "GibRenderer");
        Check(ownershipBoundary >= 0 && gibReveal > ownershipBoundary && commitInstructions.Skip(gibReveal).Any(i =>
            i.Operand is MethodReference method && method.Name == "set_enabled" && i.Previous.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_1),
            "gib visibility is acquired only after the body mesh commit boundary");
        Check(Calls("Rupture.Dismemberment.Classes.Limbs.LimbDismembermentSystem", "CancelPending").Contains("CancelLivingGib"),
            "cancelled geometry owns cancellation of its asynchronous detached body");
        Check(Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "FailExternalRaid").Contains("ResetLivingFalls"),
            "backend failure releases living rigs before resetting the native scene");
        Check(Calls(hooks.FullName, "VaultPrefix").Contains("Restricted"), "vault restrictions use permanent anatomy state");
        string[] namedTargets = { "RestoreBodyPart", "FullRestoreBodyPart", "DoFracture", "DoBleed" };
        foreach (string target in namedTargets)
            Check(native.Methods.Any(m => m.Name == target && m.Parameters.Any(p => p.Name == "bodyPart")),
                "installed health patch parameter exists: " + target);
        var movement = game.MainModule.GetType("EFT.MovementContext");
        Check(movement.Methods.Any(m => m.Name == "TryVaulting" && m.ReturnType.FullName == "System.Boolean" && m.Parameters.Count == 0),
            "installed vault restriction target is the boolean entry point");
        Check(movement.Methods.Any(m => m.Name == "SetPoseLevel" && m.Parameters.Any(p => p.Name == "poseLevel")) &&
            movement.Fields.Any(f => f.Name == "_player" && f.FieldType.FullName == "EFT.Player"),
            "installed stance patch parameters and player binding match");
    }
}
