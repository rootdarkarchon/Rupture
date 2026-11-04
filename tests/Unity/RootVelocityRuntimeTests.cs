#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Rupture;
using Rupture.Ragdolls.Classes;
using UnityEngine;

public static partial class LethalImpulseRuntimeTests
{
    private static IList VelocityRecords => (IList)typeof(RootVelocityExperiment).GetField("Records", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
    private static RootVelocityExperiment.Record StartVelocity(Case c, Vector3 raw)
    {
        var shot = MakeShot(c); CaptureTerminal(c, shot);
        c.Ragdoll._velocity = raw; c.Ragdoll.Start();
        return (RootVelocityExperiment.Record)VelocityRecords[VelocityRecords.Count - 1];
    }
    private static void SelectVelocityImpulse(Case c)
    {
        c.Stock(); c.Completed(); c.Admit();
        Check(c.Record.Policy.Finished && !c.Record.Policy.Fallback && c.Record.Policy.Physical &&
            c.Record.Policy.Source == LethalImpulseSource.RuptureOnly, "velocity fixture selects verified Rupture-only 1x");
    }
    private static void RootVelocityChecks()
    {
        Rupture.Ragdolls.Patches.RootVelocityHooks.Install();
        Check(Rupture.Ragdolls.Patches.RootVelocityHooks.Available, "production raw velocity Start hooks install in actual Unity");
        foreach (Vector3 raw in new[] { Vector3.zero, Vector3.right * 1.5f, Vector3.right * 6f,
            new Vector3(3f, -4f, 2f), Vector3.down * 6f, Vector3.up * 0.2f, Vector3.up * 6f })
        foreach (bool inherited in new[] { false, true })
        {
            if (inherited) RootVelocityExperiment.Inherited(); else RootVelocityExperiment.Baseline();
            using (var c = new Case(capture: false))
            {
                var record = StartVelocity(c, raw);
                Check(record.Started && (c.Body.velocity - RootVelocityExperiment.StockSeed(raw)).sqrMagnitude < 0.000001f,
                    "capture observes actual stock clamp including upward special case, raw=" + raw);
                c.Body.angularVelocity = Vector3.up * 2f;
                c.Body.AddForce(Vector3.right * 0.8f, ForceMode.Impulse); c.NativeStep();
                Vector3 nativeVelocity = c.Body.velocity, nativeAngular = c.Body.angularVelocity;
                SelectVelocityImpulse(c);
                Check((ShadowRagdollWorld.Native.Velocity - nativeVelocity).sqrMagnitude < 0.000001f,
                    "admission retains integrated native motion before optional correction");
                RootVelocityExperiment.BeforeExternalStep();
                Vector3 expected = nativeVelocity + (inherited ? raw - record.Seed : Vector3.zero);
                Check(record.Prepared && (ShadowRagdollWorld.Native.Velocity - expected).sqrMagnitude < 0.000001f &&
                    (ShadowRagdollWorld.Native.Angular - nativeAngular).sqrMagnitude < 0.000001f,
                    "boundary adds missing linear component exactly once with angular motion unchanged, inherited=" + inherited);
                c.ExternalStep();
                expected += Vector3.forward * 0.9f; // 3.6 Ns / 4 kg; legacy fixture completion API at physical gain.
                Check((ShadowRagdollWorld.Native.Velocity - expected).sqrMagnitude < 0.000001f && !record.InFlight,
                    "native bridge preserves selected queued impulse through linear velocity state write");
                c.ExternalStep();
                Check((ShadowRagdollWorld.Native.Velocity - expected).sqrMagnitude < 0.000001f,
                    "subsequent external steps never repeat root correction");
            }
        }
        foreach (string failure in new[] { "pending", "fallback", "admission-failed", "invalidated", "peers", "stale", "end", "restart", "native-mismatch", "overspeed", "invalid" })
        {
            RootVelocityExperiment.Inherited();
            using (var c = new Case(capture: false))
            {
                Vector3 raw = failure == "overspeed" ? Vector3.right * 16f : failure == "invalid" ? new Vector3(float.NaN, 0, 0) : Vector3.right * 6f;
                var record = StartVelocity(c, raw);
                if (failure == "invalid") { Check(record.Evaluated && !record.Started, "nonfinite root input is excluded"); continue; }
                if (failure == "native-mismatch") { record.Started = false; RootVelocityExperiment.AfterStart(record, Vector3.zero, true); }
                if (failure == "restart") c.Ragdoll.Start();
                if (failure == "pending") c.Admit();
                else if (failure == "admission-failed")
                { c.Stock(); c.Completed(); LethalImpulseExperiment.Admission(c.Ragdoll, false); c.Admit(); }
                else SelectVelocityImpulse(c);
                if (failure == "fallback") c.Record.Policy.Fallback = true;
                if (failure == "invalidated") c.Record.ComparisonInvalidated = true;
                if (failure == "peers") MultiplayerRuntime.Current.HasRemotePeers = true;
                if (failure == "stale") record.CreatedAt -= 0.6f;
                if (failure == "end") RootVelocityExperiment.End(c.Ragdoll, "fixture-admission-abort");
                Vector3 before = ShadowRagdollWorld.Native.Velocity;
                RootVelocityExperiment.BeforeExternalStep();
                Check(!record.Prepared && (ShadowRagdollWorld.Native.Velocity - before).sqrMagnitude < 0.000001f,
                    "unsafe comparison leaves inherited motion unchanged: " + failure);
                if (failure == "pending")
                {
                    c.Stock(); c.Completed(); RootVelocityExperiment.BeforeExternalStep();
                    Check(!record.Prepared, "late lethal source selection cannot correct a rig after its first boundary");
                }
                MultiplayerRuntime.Current.HasRemotePeers = false;
            }
        }
        RootVelocityExperiment.Inherited();
        using (var c = new Case(capture: false))
        {
            LethalImpulseExperiment.Amplified(); LethalImpulseExperiment.StockOnly(); LethalImpulseExperiment.Baseline();
            var record = StartVelocity(c, Vector3.right * 6);
            Check(c.Record.Policy.Physical && c.Record.Policy.Source == LethalImpulseSource.RuptureOnly,
                "velocity experiment rejects amplified and alternate source console changes");
            RootVelocityExperiment.Off();
            c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
            Check(!RootVelocityExperiment.Enabled && !LethalImpulseExperiment.Enabled && VelocityRecords.Count == 0 && Near(c.Body.velocity.x, 2),
                "off clears pending captures and restores ordinary defaults for new deaths");
            RootVelocityExperiment.Inherited(); LethalImpulseExperiment.Reset();
            Check(!RootVelocityExperiment.Enabled && !LethalImpulseExperiment.Enabled, "raid reset restores both experiments to defaults");
        }
        using (var c = new Case(capture: false))
        {
            RootVelocityExperiment.Inherited(); c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
            Check(VelocityRecords.Count == 0, "uncorrelated deaths cannot receive root corrections");
        }
        foreach (string coreState in new[] { "retained", "removed", "kinematic", "invalid", "independent", "write-failure" })
        {
            RootVelocityExperiment.Inherited();
            using (var c = new Case(capture: false))
            {
                StartVelocity(c, Vector3.right * 6f); SelectVelocityImpulse(c);
                var extraBody = AddLaterBody(c); extraBody.velocity = Vector3.left;
                using (var extra = new LethalImpulseNativeFixture(extraBody, ShadowRagdollWorld.Native))
                using (var weapon = new LethalImpulseNativeFixture(extraBody, ShadowRagdollWorld.Native))
                {
                    var rig = ShadowRagdollWorld.RigsByRagdoll[c.Ragdoll];
                    var proxy = new ExternalShadowBodyProxy { Native = extra, isKinematic = coreState == "kinematic", FailWriteOnce = coreState == "write-failure" };
                    proxy.ExternalBody.IsValid = coreState != "invalid";
                    var body = new ShadowRagdollWorld.ShadowBody { Native = extraBody, Proxy = proxy, CountsForSleep = coreState != "independent" };
                    rig.CoreBodies.Add(body);
                    if (coreState == "removed") rig.PermanentlyDisabledBodies.Add(body);
                    Vector3 firstBefore = ShadowRagdollWorld.Native.Velocity, extraBefore = extra.Velocity, weaponBefore = weapon.Velocity;
                    bool applied = ShadowRagdollWorld.TryAdjustRootVelocity(c.Ragdoll, Vector3.right * 4f, true, out int count, out _, out _, out _);
                    bool success = coreState == "retained" || coreState == "removed";
                    Check(applied == success && (ShadowRagdollWorld.Native.Velocity - firstBefore - (success ? Vector3.right * 4 : Vector3.zero)).sqrMagnitude < 0.000001f &&
                        (extra.Velocity - extraBefore - (coreState == "retained" ? Vector3.right * 4 : Vector3.zero)).sqrMagnitude < 0.000001f && weapon.Velocity == weaponBefore &&
                        (!success || count == (coreState == "removed" ? 1 : 2)),
                        "exact production transfer validates all retained core bodies, excludes weapons/removed limbs and rolls back: " + coreState);
                    var cached = (Array)typeof(ShadowRagdollWorld).GetField("RootVelocityBodies", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    bool empty = true; foreach (object entry in cached) empty &= entry == null;
                    Check(empty, "transfer scratch references cleared after " + coreState);
                }
            }
        }
        foreach (bool armed in new[] { true, false })
        {
            RootVelocityExperiment.Inherited();
            using (var c = new Case(capture: false, childHit: !armed))
            {
                var shot = MakeShot(c); CaptureTerminal(c, shot);
                c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
                c.Stock(); c.Admit(); shot.IsShotFinished = true; ShotImpactProcessor.Update(); c.ExternalStep();
                Check(Near(ShadowRagdollWorld.Native.Velocity.x, 6f) && Near(ShadowRagdollWorld.Native.Velocity.z, 0.6f) &&
                    !c.Record.Policy.Fallback && c.Record.Policy.StockRejected == !armed,
                    "production tracked completed-shot path combines inherited velocity with 1x impulse, stockArmed=" + armed);
                Vector3 before = ShadowRagdollWorld.Native.Velocity;
                RagdollCoordinator.TryApplyCorpsePointImpulse(c.Corpse, c.Collider, Vector3.right * 4, Vector3.zero, out _); c.ExternalStep();
                Check((ShadowRagdollWorld.Native.Velocity - before - Vector3.right).sqrMagnitude < 0.000001f,
                    "later corpse impulse integrates once without a repeated velocity correction");
            }
        }
        RootVelocityExperiment.Inherited();
        var bounded = new System.Collections.Generic.List<Case>();
        try
        {
            for (int i = 0; i < 40; i++)
            {
                var c = new Case(capture: false); bounded.Add(c);
                var shot = MakeShot(c); CaptureTerminal(c, shot); c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
            }
            Check(VelocityRecords.Count == 32, "velocity capture capacity remains bounded at 32 under overflow");
            for (int i = 0; i < 400; i++) bounded[0].Ragdoll.Start();
            int events = (int)typeof(RootVelocityExperiment).GetField("_count", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(events == 256, "velocity event journal wraps at 256 entries");
            foreach (RootVelocityExperiment.Record record in VelocityRecords) record.CreatedAt -= 4.1f;
            RootVelocityExperiment.BeforeExternalStep();
            Check(VelocityRecords.Count == 0, "unadmitted and retired captures expire within the bounded retention window");
        }
        finally { foreach (var c in bounded) c.Dispose(); }
        Measurements.Add("root velocity: production capture/selection/lifecycle and extracted production multi-core transfer with native bridge; full EFT articulated admission and visible movement still require in-game acceptance");
    }
}
#endif
