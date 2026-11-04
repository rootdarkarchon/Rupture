#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Rupture;
using Rupture.Ragdolls.Classes;
using UnityEngine;

public static partial class LethalImpulseRuntimeTests
{
    private static void ProductionSettings(bool lethal, bool velocity, bool angular)
    {
        RuptureConfiguration.CorrectedLethalImpulse.Value = lethal;
        RuptureConfiguration.InheritRootVelocity.Value = velocity;
        RuptureConfiguration.AngularResistance.Value = angular;
    }
    private static void ProductionChecks()
    {
        LethalImpulseExperiment.Reset();
        try
        {
            foreach (bool stockFirst in new[] { false, true })
            foreach (bool coldAdmission in new[] { false, true })
            {
                ProductionSettings(true, true, true);
                using (var c = new Case())
                {
                    var record = c.Record;
                    Check(record.Production && record.Policy.Physical && record.Policy.Source == LethalImpulseSource.RuptureOnly,
                        "configured gameplay opens a physical Rupture-only transaction without console commands");
                    if (stockFirst) c.Stock(); else c.Completed();
                    record.Policy.HeldAt -= 1f;
                    LethalImpulseExperiment.Update();
                    Check(!record.Policy.Finished, "cold same-frame elapsed wall time does not release the first source");
                    if (coldAdmission) c.Admit();
                    if (stockFirst) c.Completed(); else c.Stock();
                    if (!coldAdmission) c.Admit();
                    c.ExternalStep();
                    Check(record.Policy.Finished && !record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, .9f),
                        "cold production handoff commits exactly 3.6 Ns in either source/admission order");
                }
            }
            ProductionSettings(true, true, true);
            using (var c = new Case())
            {
                var record = c.Stock();
                ProductionSettings(false, false, false);
                c.Completed(); c.Admit(); c.ExternalStep();
                Check(!record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, .9f),
                    "a preset switch cannot change an already captured lethal impulse transaction");
            }
            foreach (string hit in new[] { "primary", "secondary", "child", "sphere" })
            {
                ProductionSettings(true, true, true);
                using (var c = new Case(capture: false, secondary: hit == "secondary", childHit: hit == "child", sphere: hit == "sphere"))
                {
                    var shot = MakeShot(c); var record = CaptureTerminal(c, shot);
                    c.Stock(); c.Admit();
                    var later = AddLaterBody(c); FinishAt(shot, later); c.ExternalStep();
                    Check(record.Policy.Finished && !record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, .6f),
                        "production tracked contact retains exactly 2.4 Ns despite changed completion body: " + hit);
                    Check(record.Policy.StockRejected == (hit == "secondary" || hit == "child"),
                        "production stock-armed/unarmed evidence remains exact: " + hit);
                }
            }
            ProductionSettings(true, true, true);
            using (var c = new Case())
            {
                var record = c.Stock();
                record.Policy.HeldAt -= 1f; record.Policy.HeldFrame = Time.frameCount - 2;
                LethalImpulseExperiment.Update(); c.Completed(); c.Admit(); c.ExternalStep();
                Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f),
                    "genuinely missing callback still times out and preserves both original impulses across late admission");
            }
            ProductionSettings(false, true, false);
            using (var c = new Case())
            {
                c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
                c.Stock(); c.Completed(); c.NativeStep(); c.Admit(); c.ExternalStep();
                Check(c.Record == null && VelocityRecords.Count == 1 && AngularRecords.Count == 0 &&
                    Near(ShadowRagdollWorld.Native.Velocity.x, 6f) && Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f),
                    "old profile keeps inherited movement with original stock plus 4x impulse and passive joints");
            }
            foreach (bool angular in new[] { false, true })
            foreach (bool inherited in new[] { false, true })
            {
                if (!angular && !inherited) continue;
                ProductionSettings(false, inherited, angular);
                using (var c = new Case(capture: false))
                {
                    if (angular) AddAngularJoint(c);
                    c.Ragdoll._velocity = new Vector3(4, -6, 0); c.Ragdoll.Start();
                    var root = (RootVelocityExperiment.Record)VelocityRecords[0];
                    Check(root.Production && root.Lethal == null && root.Inherited == inherited,
                        "production inheritance/resistance capture works independently without a terminal bullet");
                    // A profile switch affects new deaths only.
                    ProductionSettings(false, false, false);
                    c.Admit(); c.ExternalStep();
                    Vector3 expected = inherited ? root.Raw : root.Seed;
                    Check((ShadowRagdollWorld.Native.Velocity - expected).sqrMagnitude < .00001f,
                        "production first-step linear selection preserves falling velocity and survives a profile switch");
                    if (angular)
                    {
                        var drive = (AngularDriveExperiment.Record)AngularRecords[0];
                        Check(drive.Active && drive.Residual && drive.Joints.Length == 1,
                            "production resistance is independent of lethal selection and movement correction");
                        drive.StartedAt -= .5f; AngularDriveExperiment.BeforeExternalStep();
                        Check(drive.Closed && !drive.Active, "production resistance clears despite a mid-death preset switch");
                    }
                    Vector3 before = ShadowRagdollWorld.Native.Velocity;
                    c.ExternalStep();
                    Check((ShadowRagdollWorld.Native.Velocity - before).sqrMagnitude < .00001f,
                        "configured movement inheritance never repeats on subsequent steps");
                }
            }
            ProductionSettings(true, true, true);
            using (var c = new Case())
            {
                c.Stock(); ProductionSettings(false, false, false);
                using (var next = new Case())
                    Check(next.Record == null, "old profile opens no new lethal transaction while another corpse is pending");
                // Case disposal resets fixture state; snapshot retention itself is
                // exercised by the independent velocity/resistance cases above.
            }
            foreach (string role in new[] { "remote", "host-peers", "headless", "half-host" })
            {
                ProductionSettings(true, true, true);
                MultiplayerRuntime.Current = new FixtureRuntime { IsRemoteClient = role == "remote", HasClient = role == "remote",
                    IsNetworkAuthority = role == "host-peers" || role == "headless" || role == "half-host",
                    HasServer = role == "host-peers" || role == "headless", IsHeadless = role == "headless", HasRemotePeers = role == "host-peers" };
                bool authority = role == "host-peers" || role == "headless";
                using (var c = new Case())
                {
                    c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
                    Check((c.Record != null) == authority && (VelocityRecords.Count != 0) == authority,
                        "only a coherent simulation authority creates production death corrections: " + role);
                    c.Stock(); c.Completed();
                    if (!authority) c.NativeStep();
                    c.Admit(); c.ExternalStep();
                    Check(Near(ShadowRagdollWorld.Native.Velocity.z, authority ? .9f : 14.85f),
                        "network role selects one physical impulse or preserves existing prediction path: " + role);
                }
            }
            MultiplayerRuntime.Current = new StandaloneRuntime();
            ProductionSettings(true, true, true);
            var simultaneous = new List<Case>();
            try
            {
                for (int i = 0; i < 40; i++)
                {
                    var c = new Case(); simultaneous.Add(c); AddAngularJoint(c);
                    c.Ragdoll._velocity = Vector3.right * 6; c.Ragdoll.Start();
                }
                Check(simultaneous[39].Record != null && VelocityRecords.Count == 40 && AngularRecords.Count == 40,
                    "diagnostic 32-record limits cannot disable production impulse, velocity or resistance capture");
                var last = simultaneous[39]; last.Stock(); last.Completed(); last.Admit();
                Check(!last.Record.Policy.Fallback && ShadowRagdollWorld.Native.Queued == 1 && Near(last.Record.RuptureImpulse.magnitude, 3.6f),
                    "death beyond the old history capacity still receives exactly one physical impulse");
                last.ExternalStep();
            }
            finally { foreach (var c in simultaneous) c.Dispose(); }
        }
        finally
        {
            ProductionSettings(false, false, false);
            MultiplayerRuntime.Current = new StandaloneRuntime();
            LethalImpulseExperiment.Reset();
        }
    }
}
#endif
