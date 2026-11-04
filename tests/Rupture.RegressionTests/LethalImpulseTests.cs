using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

internal static class LethalImpulseTests
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        Type math = assembly.GetType("Rupture.Ragdolls.Classes.PhysicsMath", true);
        float Calculate(float mass, float speed, float multiplier, float gain) =>
            (float)math.GetMethod("CalculateProjectileImpulseWithGain", flags).Invoke(null, new object[] { mass, speed, multiplier, gain });
        Check(Math.Abs(Calculate(4f, 900f, 1f, 1f) - 3.6f) < 0.0001f &&
            Math.Abs(Calculate(4f, 900f, 1f, 4f) - 14.4f) < 0.0001f, "physical and amplified gains differ independently by four");
        Check(Calculate(100f, 10000f, 10f, 1f) == 500f && Calculate(4, 900, 0, 1) == 0 &&
            Calculate(4, 900, 1, float.NaN) == 0, "experimental gain retains cap and invalid/zero-input behavior");
        Type source = assembly.GetType("Rupture.Ragdolls.Classes.LethalImpulseSource", true);
        Type policy = assembly.GetType("Rupture.Ragdolls.Classes.LethalImpulsePolicy", true);
        object Make(string name) => Activator.CreateInstance(policy, flags, null, new[] { Enum.Parse(source, name), (object)false }, null);
        bool Can(object p, bool match, bool solo, bool backend) => (bool)policy.GetMethod("CanHold", flags).Invoke(p, new object[] { match, solo, backend });
        object baseline = Make("Baseline"), rpt = Make("RuptureOnly");
        Check(!Can(baseline, true, true, true), "baseline never holds either original impulse");
        Check(!Can(rpt, false, true, true) && !Can(rpt, true, false, true) && !Can(rpt, true, true, false),
            "ambiguous identity, multiplayer and unavailable backend all fail open");
        Check(Can(rpt, true, true, true), "correlated standalone death may open transaction");
        policy.GetMethod("Held", flags).Invoke(rpt, new object[] { true, 10f, -1 });
        policy.GetField("Admitted", flags).SetValue(rpt, true);
        Check(!(bool)policy.GetProperty("Ready", flags).GetValue(rpt), "admission and one source cannot commit");
        policy.GetMethod("Held", flags).Invoke(rpt, new object[] { false, 10.1f, -1 });
        Check((bool)policy.GetProperty("Ready", flags).GetValue(rpt), "both held sources and admission permit commit");
        Check(!(bool)policy.GetMethod("Expired", flags).Invoke(rpt, new object[] { 10.249f, -1 }) &&
            (bool)policy.GetMethod("Expired", flags).Invoke(rpt, new object[] { 10.25f, -1 }), "250ms deadline is measured from first hold");
        object cold = Make("RuptureOnly");
        policy.GetMethod("Held", flags).Invoke(cold, new object[] { true, 10f, 100 });
        Check(!(bool)policy.GetMethod("Expired", flags).Invoke(cold, new object[] { 11f, 100 }) &&
            !(bool)policy.GetMethod("Expired", flags).Invoke(cold, new object[] { 11f, 101 }) &&
            (bool)policy.GetMethod("Expired", flags).Invoke(cold, new object[] { 11f, 102 }),
            "cold same/next-frame stalls receive a callback opportunity; missing counterparts still expire");
        policy.GetMethod("Finish", flags).Invoke(rpt, new object[] { true });
        Check(!Can(rpt, true, true, true) && !(bool)policy.GetProperty("Ready", flags).GetValue(rpt),
            "fallback latch prevents late events starting another transaction");
        foreach (string mode in new[] { "Baseline", "StockOnly", "RuptureOnly" })
        {
            object p = Make(mode);
            policy.GetField("Admitted", flags).SetValue(p, true);
            policy.GetMethod("Held", flags).Invoke(p, new object[] { false, 10f, -1 });
            Check(!(bool)policy.GetProperty("Ready", flags).GetValue(p), "timeout alone cannot prove absent stock: " + mode);
            policy.GetField("StockRejected", flags).SetValue(p, true);
            Check((bool)policy.GetProperty("Ready", flags).GetValue(p) == (mode == "RuptureOnly"),
                "verified absent stock permits only Rupture-only selection: " + mode);
        }
        var stockArgs = new[] { typeof(UnityEngine.Rigidbody), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(float) };
        Check(typeof(EFT.Interactive.CorpseRagdoll).GetMethod("ApplyImpulse", stockArgs) != null &&
            typeof(EFT.Player).GetField("LastDamageInfo", flags)?.FieldType == typeof(EFT.Ballistics.DamageInfo),
            "installed EFT stock impulse and death-field injection ABI verified");
        using var module = ModuleDefinition.ReadModule(assembly.Location);
        bool Calls(string type, string method, string target) => module.GetType(type).Methods.Where(m => m.Name == method && m.HasBody)
            .SelectMany(m => m.Body.Instructions).Any(i => i.Operand is MethodReference called && called.Name == target);
        const string experiment = "Rupture.Ragdolls.Classes.LethalImpulseExperiment";
        Check(new[] { "IsHeadless", "IsRemoteClient", "IsNetworkAuthority", "HasServer", "HasClient", "HasRemotePeers" }.All(
                role => Calls(experiment, "get_LocalComparisonAllowed", "get_" + role)) &&
            !module.GetType(experiment).Methods.Single(m => m.Name == "get_LocalComparisonAllowed").Body.Instructions.Any(
                i => i.Operand is TypeReference type && type.Name == "StandaloneRuntime"),
            "comparison eligibility uses live network roles instead of the installed adapter type");
        Check(new[] { "BeforeStock", "Complete", "Resolve" }.All(
                method => Calls(experiment, method, "Allowed")),
            "live network eligibility is rechecked at selection, each hold and commit");
        Check(Calls("Rupture.Ragdolls.Classes.ShotImpactProcessor", "ProcessCompletedShot", "CompletedShot") &&
            Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "ProcessPendingBuilds", "Admission"),
            "production completion and admission paths notify the transaction");
        Check(Calls(experiment, "Resolve", "TryApplyPointImpulse") && Calls(experiment, "Resolve", "HasActiveLethalExperimentBody"),
            "production selection requires the exact retained actor and uses the existing authority impulse transport");
        Check(Calls("Rupture.Impacts.ImpactHooks", "ShotPrefix", "EnterShot") &&
            Calls("Rupture.Impacts.ImpactHooks", "ShotFinally", "RestoreShot") &&
            Calls(experiment, "EnterShot", "TryGetTrackedContact") && Calls(experiment, "Death", "CaptureContact"),
            "lethal contact is bound to a tracked shot inside the collision scope and restored on exit");
        Check(Calls("Rupture.Ragdolls.Classes.ShotImpactProcessor", "CaptureFlightState", "ProjectileIdentity"),
            "diagnostic projectile identity is retained while the shot and parent chain are live");
        Check(Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "Simulate", "ExternalStepSubmitted") &&
            Calls("Rupture.Ragdolls.Classes.ShadowRagdollWorld", "CompleteExternalStep", "ExternalStepCompleted"),
            "external evidence is fenced by submission and completed readback");
        const string world = "Rupture.Ragdolls.Classes.ShadowRagdollWorld", velocity = "Rupture.Ragdolls.Classes.RootVelocityExperiment";
        Check(typeof(EFT.Interactive.CorpseRagdoll).GetField("_velocity", flags)?.FieldType == typeof(UnityEngine.Vector3) &&
            typeof(EFT.Interactive.CorpseRagdoll).GetMethod("Start", flags, null, Type.EmptyTypes, null) != null,
            "installed EFT raw root velocity capture ABI verified");
        Check(Calls("Rupture.RupturePlugin", "Awake", "Install") && Calls(world, "Simulate", "BeforeExternalStep") &&
            Calls(world, "CompleteExternalStep", "ExternalStepCompleted") && Calls(experiment, "Reset", "Reset"),
            "velocity experiment is wired to owner boundaries and raid reset");
        var transfer = module.GetType(world).Methods.Single(m => m.Name == "TryAdjustRootVelocity").Body.Instructions;
        Check(transfer.Any(i => i.Operand is FieldReference f && f.Name == "CoreBodies") &&
            transfer.Any(i => i.Operand is FieldReference f && f.Name == "PermanentlyDisabledBodies") &&
            transfer.Any(i => i.Operand is MethodReference m && m.Name == "get_IsValid") &&
            !transfer.Any(i => i.Operand is MethodReference m && (m.Name == "set_angularVelocity" || m.Name == "set_isKinematic")),
            "production root transfer selects retained valid core actors and writes neither angular motion nor kinematic state");
        var simulation = module.GetType(world).Methods.Single(m => m.Name == "Simulate").Body.Instructions;
        int Index(string method) => simulation.Select((i, n) => new { i, n }).First(x => x.i.Operand is MethodReference m && m.Name == method).n;
        Check(Index("BeforeExternalStep") < Index("SubmitStep") && Index("CompleteExternalStep") < Index("BeforeExternalStep") &&
            Calls(velocity, "Select", "FixForVelocity") && Calls(experiment, "Amplified", "get_Enabled"),
            "velocity correction follows completion, precedes submission and fixes lethal selection at 1x");
    }
}
