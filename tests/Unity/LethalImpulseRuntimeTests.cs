#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EFT;
using EFT.Interactive;
using Rupture;
using Rupture.Impacts;
using Rupture.Ragdolls.Classes;
using UnityEngine;

[UnityEditor.InitializeOnLoad]
public static partial class LethalImpulseRuntimeTests
{
    private const string Pending = "RuptureLethalImpulseRuntime";
    private static int _cases;
    private static readonly List<string> Measurements = new List<string>();
    static LethalImpulseRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool(Pending, false)) return;
            UnityEditor.SessionState.SetBool(Pending, false);
            try { Execute(); UnityEditor.EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); }
        };
    }
    public static void RunBatch() { UnityEditor.SessionState.SetBool(Pending, true); UnityEditor.EditorApplication.EnterPlaymode(); }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); _cases++; Debug.Log("PASS: " + name); }
    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0002f;
    private static bool SkipNativeSimulation() => false;
    private static EFT.Ballistics.Shot MakeShot(Case c) => new EFT.Ballistics.Shot
    {
        Ammo = new EFT.InventoryLogic.Ammo(), HitCollider = c.Collider, HitPoint = Vector3.zero,
        Direction = Vector3.forward, CurrentDirection = Vector3.forward, BulletMassGram = 3f, VelocityMagnitude = 800f
    };
    private static LethalImpulseExperiment.Record CaptureTerminal(Case c, EFT.Ballistics.Shot shot, bool track = true)
    {
        if (track) ShotImpactProcessor.Track(shot);
        var previous = LethalImpulseExperiment.EnterShot(shot,
            new ImpactData { Projectile = shot.Projectile, MassGrams = shot.BulletMassGram, Speed = shot.VelocityMagnitude });
        try
        {
            RecordTerminal(c, shot);
        }
        finally { LethalImpulseExperiment.RestoreShot(previous); }
        return c.Record;
    }
    private static void RecordTerminal(Case c, EFT.Ballistics.Shot shot)
    {
        LethalImpulseExperiment.Death(c.Player, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet,
            HitCollider = c.Collider, HitPoint = Vector3.zero, Direction = Vector3.forward },
            new ImpactData { Projectile = shot.Projectile, MassGrams = shot.BulletMassGram, Speed = shot.VelocityMagnitude });
        LethalImpulseExperiment.Bind(c.Ragdoll);
    }
    private static Rigidbody AddLaterBody(Case c)
    {
        var go = new GameObject("later-spine-contact"); go.transform.SetParent(c.Root.transform, false);
        go.transform.localPosition = Vector3.right * 10f;
        var body = go.AddComponent<Rigidbody>(); body.mass = 8f; body.useGravity = false; body.drag = body.angularDrag = 0;
        go.AddComponent<BoxCollider>(); return body;
    }
    private static void FinishAt(EFT.Ballistics.Shot shot, Rigidbody later)
    {
        shot.HitCollider = later.GetComponent<Collider>(); shot.HitPoint = Vector3.right * 0.05f;
        shot.CurrentDirection = Vector3.right; shot.BulletMassGram = 4f; shot.VelocityMagnitude = 900f; shot.IsShotFinished = true;
        ShotImpactProcessor.Update();
    }
    private sealed class Case : IDisposable
    {
        internal readonly GameObject Root;
        internal readonly Player Player;
        internal readonly Corpse Corpse;
        internal readonly Rigidbody Body;
        internal readonly Collider Collider;
        internal readonly CorpseRagdoll Ragdoll;
        internal LethalImpulseExperiment.Record Record => (LethalImpulseExperiment.Record)typeof(LethalImpulseExperiment)
            .GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { Ragdoll });
        internal Case(bool capture = true, bool valid = true, bool secondary = false, bool childHit = false, bool sphere = false)
        {
            Root = new GameObject("impulse-fixture");
            Player = Root.AddComponent<Player>(); Player.ProfileId = "fixture";
            Corpse = Root.AddComponent<Corpse>(); Corpse.PlayerProfileID = Player.ProfileId;
            Body = Root.AddComponent<Rigidbody>(); Body.useGravity = false; Body.mass = 4f;
            Body.drag = Body.angularDrag = 0; Body.maxAngularVelocity = 1000f;
            Collider = sphere ? (Collider)Root.AddComponent<SphereCollider>() : Root.AddComponent<BoxCollider>();
            var spawner = Root.AddComponent<RigidbodySpawner>(); spawner.Rigidbody = Body;
            if (secondary) Collider = Root.AddComponent<CapsuleCollider>();
            if (childHit)
            {
                var hit = new GameObject("secondary-hit-child"); hit.transform.SetParent(Root.transform, false);
                Collider = hit.AddComponent<CapsuleCollider>();
            }
            Ragdoll = new CorpseRagdoll { _owner = Corpse, Body = Body, Collider = Collider,
                _rigidbodySpawners = new[] { spawner } }; Corpse.Ragdoll = Ragdoll;
            if (capture)
            {
                LethalImpulseExperiment.Death(Player, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet,
                    HitCollider = Collider, HitPoint = Vector3.zero, Direction = Vector3.forward },
                    new ImpactData { Projectile = valid ? 42UL : 0UL, MassGrams = 4f, Speed = 900f });
                LethalImpulseExperiment.Bind(Ragdoll);
            }
        }
        internal LethalImpulseExperiment.Record Stock()
        {
            Ragdoll.ApplyImpulse(Collider, Vector3.forward, Vector3.zero, 45f);
            Ragdoll.TryToApplyImpulse();
            return Record;
        }
        internal LethalImpulseExperiment.Record Completed(ulong projectile = 42, Vector3? point = null)
        {
            Vector3 position = point ?? Vector3.zero;
            var record = LethalImpulseExperiment.Completed(Corpse, Collider, Body, projectile, Vector3.forward * 14.4f,
                position, 4f, 900f, 1f, out bool handled);
            if (!handled)
            {
                bool applied = RagdollCoordinator.TryApplyCorpsePointImpulse(Corpse, Collider, Vector3.forward * 14.4f, position, out _);
                LethalImpulseExperiment.AfterCompleted(record, applied);
            }
            return record;
        }
        internal void Admit()
        {
            LethalImpulseExperiment.CaptureAdmissionMotion(Ragdoll, Body, Body.velocity, Body.angularVelocity);
            ShadowRagdollWorld.Admit(Corpse, Body);
            LethalImpulseExperiment.Admission(Ragdoll, true);
        }
        internal void NativeStep()
        { Physics.Simulate(1f / 60f); }
        internal void ExternalStep()
        {
            RootVelocityExperiment.BeforeExternalStep();
            AngularDriveExperiment.BeforeExternalStep();
            LethalImpulseExperiment.ExternalStepSubmitted(); RootVelocityExperiment.ExternalStepSubmitted();
            AngularDriveExperiment.ExternalStepSubmitted();
            ShadowRagdollWorld.Native.Step();
            LethalImpulseExperiment.ExternalStepCompleted(); RootVelocityExperiment.ExternalStepCompleted();
            AngularDriveExperiment.ExternalStepCompleted();
        }
        internal Vector3 ExternalStepAndVelocity() { ExternalStep(); return ShadowRagdollWorld.Native.Velocity; }
        public void Dispose()
        {
            LethalImpulseExperiment.Reset(); ShotImpactProcessor.Reset(); ShadowRagdollWorld.Clear();
            UnityEngine.Object.DestroyImmediate(Root);
        }
    }
    private static void Execute()
    {
        Physics.simulationMode = SimulationMode.Script;
        Rupture.Ragdolls.Patches.LethalImpulseHooks.Install();
        Check(Rupture.Ragdolls.Patches.LethalImpulseHooks.Available, "production Harmony impulse and simulation hooks install in actual Unity runtime");
        RootVelocityChecks();
        AngularDriveChecks();
        foreach (bool tracing in new[] { false, true })
        {
            if (tracing) LethalImpulseExperiment.Baseline(); else LethalImpulseExperiment.Off();
            using (var c = new Case(capture: false))
            {
                var shot = MakeShot(c); shot.BulletMassGram = 4; shot.VelocityMagnitude = 900; ShotImpactProcessor.Track(shot);
                shot.BulletMassGram = 3; shot.VelocityMagnitude = 800;
                var prior = LethalImpulseExperiment.EnterShot(shot, new ImpactData { Projectile = 42, MassGrams = 3, Speed = 800 });
                LethalImpulseExperiment.RestoreShot(prior);
                shot.BulletMassGram = shot.VelocityMagnitude = 0; shot.CurrentDirection = Vector3.zero; shot.IsShotFinished = true;
                ShotImpactProcessor.Update(); c.NativeStep();
                Check(Near(c.Body.velocity.z, 3.6f), "collision binding cannot change baseline flight-sampling cadence or impulse, tracing=" + tracing);
            }
        }
        LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
        using (var c = new Case(capture: false))
        {
            var shot = MakeShot(c); ShotImpactProcessor.Track(shot);
            var previous = LethalImpulseExperiment.EnterShot(shot, new ImpactData { Projectile = 42, MassGrams = 3, Speed = 800 });
            try
            {
                LethalImpulseExperiment.Death(c.Player, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet,
                    HitCollider = c.Collider, Direction = Vector3.forward, FireIndex = 1 },
                    new ImpactData { Projectile = 42, MassGrams = 3, Speed = 800 });
                LethalImpulseExperiment.Bind(c.Ragdoll);
            }
            finally { LethalImpulseExperiment.RestoreShot(previous); }
            c.Stock(); shot.IsShotFinished = true; ShotImpactProcessor.Update(); c.NativeStep();
            Check(c.Record.Projectile == 0 && c.Record.ContactShot == null && c.Record.Policy.Fallback && Near(c.Body.velocity.z, 13.65f),
                "terminal fire-index mismatch cannot select either source and preserves both original impulses");
        }
        foreach (string mode in new[] { "baseline", "stock", "rupture4", "rupture1" })
        foreach (bool stockPresent in new[] { true, false })
        foreach (bool differentBody in new[] { true, false })
        foreach (bool admitFirst in new[] { true, false })
        foreach (bool head in new[] { true, false })
        {
            if (mode == "baseline") LethalImpulseExperiment.Baseline();
            else if (mode == "stock") LethalImpulseExperiment.StockOnly();
            else LethalImpulseExperiment.RuptureOnly();
            if (mode == "rupture1") LethalImpulseExperiment.Physical();
            using (var c = new Case(capture: false, childHit: !stockPresent, sphere: head))
            {
                var shot = MakeShot(c); var record = CaptureTerminal(c, shot);
                c.Root.name = head ? "head" : "chest";
                Check(record.ContactShot == shot && record.ShotInstance != 0,
                    "terminal callback freezes tracked contact identity and incoming momentum");
                var later = differentBody ? AddLaterBody(c) : c.Body;
                c.Stock();
                if (admitFirst) { c.NativeStep(); c.Admit(); }
                FinishAt(shot, later); c.NativeStep();
                if (!admitFirst) c.Admit();
                c.ExternalStep();
                bool original = mode == "baseline" || mode == "stock" && !stockPresent;
                float expectedZ = mode == "rupture1" ? 0.6f : mode == "rupture4" ? 2.4f : stockPresent ? 11.25f : 0;
                Check(Near(ShadowRagdollWorld.Native.Velocity.z, expectedZ) &&
                    Near(ShadowRagdollWorld.Native.Velocity.x, original && !differentBody ? 3.6f : 0) &&
                    (!differentBody || Near(later.velocity.x, original ? 1.8f : 0)),
                    "production completion preserves lethal target/momentum or exact original route: " + mode +
                    ", stock=" + stockPresent + ", changedBody=" + differentBody + ", admitFirst=" + admitFirst + ", head=" + head);
                Check(mode == "baseline" || mode == "stock" && !stockPresent ? record.Policy.Fallback || mode == "baseline" :
                    record.Policy.Finished && !record.Policy.Fallback && record.RupturePoint == Vector3.zero,
                    "captured contact selects once while baseline/absent-stock-only remain original");
            }
        }
        foreach (string failure in new[] { "admission", "timeout", "backend", "peer", "removed-collider" })
        foreach (bool stockPresent in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
            using (var c = new Case(capture: false, childHit: !stockPresent))
            {
                var shot = MakeShot(c); var record = CaptureTerminal(c, shot); var later = AddLaterBody(c); c.Stock();
                if (failure == "backend") ShadowRagdollWorld.Enabled = false;
                if (failure == "removed-collider") UnityEngine.Object.DestroyImmediate(c.Collider);
                FinishAt(shot, later);
                if (failure == "timeout") { record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); }
                if (failure == "peer")
                {
                    MultiplayerRuntime.Current = new FixtureRuntime { IsNetworkAuthority = true, HasServer = true, HasRemotePeers = true };
                    c.Admit();
                }
                else LethalImpulseExperiment.Admission(c.Ragdoll, false);
                c.NativeStep();
                float headZ = failure == "peer" ? c.ExternalStepAndVelocity().z : c.Body.velocity.z;
                Check(record.Policy.Fallback && Near(later.velocity.x, 1.8f) && Near(headZ, stockPresent ? 11.25f : 0),
                    "snapshot fallback restores ORIGINAL body, point, direction and amplified impulse: " + failure + ", stock=" + stockPresent);
                ShotImpactProcessor.Update(); c.NativeStep();
                Check(Near(later.velocity.x, 1.8f), "repeated completion Update cannot replay original fallback twice");
            }
            ShadowRagdollWorld.Enabled = true; MultiplayerRuntime.Current = new StandaloneRuntime();
        }
        LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
        using (var c = new Case(capture: false, childHit: true))
        {
            var root = MakeShot(c); ShotImpactProcessor.Track(root);
            var child = MakeShot(c); child.Parent = root;
            var record = CaptureTerminal(c, child, track: false); c.Stock();
            FinishAt(root, AddLaterBody(c)); c.Admit(); c.ExternalStep();
            Check(record.ContactShot == root && !record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 0.6f),
                "penetration continuation binds to its tracked physical projectile without borrowing its later endpoint");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(capture: false))
        {
            var root = MakeShot(c); root.BulletState = EFT.Ballistics.Shot.EBulletState.FragmentationHit; ShotImpactProcessor.Track(root);
            var fragment = MakeShot(c); fragment.Parent = root; fragment.Projectile = 77;
            var record = CaptureTerminal(c, fragment, track: false);
            Check(record.ContactShot == null, "an untracked fragmentation sibling cannot borrow the root completion");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(capture: false))
        {
            var shot = MakeShot(c); var record = CaptureTerminal(c, shot); var instance = record.ShotInstance;
            ShotImpactProcessor.Track(shot);
            Check(!ShotImpactProcessor.TryGetTrackedContact(shot, 42, out _, out _), "ambiguous duplicate shot lifetimes cannot provide a contact binding");
            ShotImpactProcessor.Reset(); ShotImpactProcessor.Track(shot);
            Check(ShotImpactProcessor.TryGetTrackedContact(shot, 42, out _, out uint next) && next != instance,
                "pooled Shot references receive distinct tracked instances even across processor reset");
            c.Stock(); FinishAt(shot, AddLaterBody(c)); c.Admit();
            record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); c.ExternalStep();
            Check(record.Policy.Fallback && !record.RuptureSeen, "a reused Shot instance cannot select an earlier lethal contact");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(capture: false))
        {
            var shot = MakeShot(c); var record = CaptureTerminal(c, shot); c.Stock();
            ShotImpactProcessor.Reset(); ShotImpactProcessor.Track(shot); shot.IsShotFinished = true;
            ShotImpactProcessor.Update(); c.NativeStep();
            Check(record.Policy.Fallback && Near(c.Body.velocity.z, 13.65f),
                "same point and projectile cannot override mismatched tracked lifetime; both original impulses survive");
        }
        LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
        using (var c = new Case(capture: false))
        {
            var shot = MakeShot(c); var record = CaptureTerminal(c, shot); c.Stock();
            shot.Ammo = new EFT.InventoryLogic.Ammo(); shot.FireIndex = 99;
            Check(!ShotImpactProcessor.TryGetTrackedContact(shot, 42, out _, out _), "changed birth fingerprint detects pool reuse before a new Track call");
            shot.IsShotFinished = true; ShotImpactProcessor.Update(); c.NativeStep();
            Check(record.Policy.Fallback && !record.RuptureSeen && Near(c.Body.velocity.z, 13.65f),
                "pooled object with matching cached identity/point cannot borrow frozen lethal momentum");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(capture: false))
        {
            var a = MakeShot(c); var b = MakeShot(c); b.Projectile = 77; b.BulletMassGram = 9;
            ShotImpactProcessor.Track(a); ShotImpactProcessor.Track(b);
            var outer = LethalImpulseExperiment.EnterShot(a, new ImpactData { Projectile = 42, MassGrams = 3, Speed = 800 });
            try
            {
                var inner = LethalImpulseExperiment.EnterShot(b, new ImpactData { Projectile = 77, MassGrams = 9, Speed = 800 });
                try { throw new InvalidOperationException("fixture collision exception"); }
                catch (InvalidOperationException) { }
                finally { LethalImpulseExperiment.RestoreShot(inner); }
                RecordTerminal(c, a);
                Check(c.Record.ContactShot == a && Near(c.Record.ContactMass, 3), "nested/throwing collision restores the correct outer lethal contact");
            }
            finally { LethalImpulseExperiment.RestoreShot(outer); }
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(capture: false))
        {
            var shot = MakeShot(c); ShotImpactProcessor.Track(shot); RecordTerminal(c, shot);
            Check(c.Record.ContactShot == null, "a death outside the collision scope cannot borrow stale contact data");
        }
        LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
        using (var c = new Case(capture: false))
        {
            var shot = MakeShot(c); ShotImpactProcessor.Track(shot); shot.IsShotFinished = true;
            ShotImpactProcessor.Update(); c.NativeStep();
            Check(Near(c.Body.velocity.z, 2.4f) && c.Record == null, "nonlethal/no-terminal-record production completion retains the ordinary amplified impulse");
        }
        foreach (string runtimeKind in new[] { "standalone", "fika-loaded", "fika-solo-host" })
        foreach (string mode in new[] { "baseline", "stock", "rupture4", "rupture1" })
        foreach (bool stockFirst in new[] { true, false })
        foreach (bool admitFirst in new[] { true, false })
        {
            LethalImpulseExperiment.Reset();
            MultiplayerRuntime.Current = runtimeKind == "standalone" ? new StandaloneRuntime() :
                new FixtureRuntime { IsNetworkAuthority = runtimeKind == "fika-solo-host", HasServer = runtimeKind == "fika-solo-host" };
            if (mode == "baseline") LethalImpulseExperiment.Baseline();
            else if (mode == "stock") LethalImpulseExperiment.StockOnly();
            else LethalImpulseExperiment.RuptureOnly();
            if (mode == "rupture1") LethalImpulseExperiment.Physical();
            using (var c = new Case())
            {
                if (admitFirst) c.Admit();
                if (stockFirst) { c.Stock(); c.Completed(); } else { c.Completed(); c.Stock(); }
                if (!admitFirst)
                {
                    if (mode != "baseline") Check(Near(c.Body.velocity.z, 0f), "held originals do not mutate native velocity");
                    c.Admit();
                }
                c.ExternalStep();
                // Baseline pre-admission queues are intentionally NOT transferred.
                float expected = mode == "baseline" ? admitFirst ? 14.85f : 0f : mode == "stock" ? 11.25f : mode == "rupture4" ? 3.6f : 0.9f;
                Check(Near(ShadowRagdollWorld.Native.Velocity.z, expected), mode + " source momentum, stockFirst=" + stockFirst + ", admitFirst=" + admitFirst + ", runtime=" + runtimeKind);
                Check(ShadowRagdollWorld.Native.Queued == (mode == "baseline" ? admitFirst ? 2 : 0 : 1), "selected impulse enqueued exactly once");
            }
        }
        // Reproduce EFT's exact GetComponent<Collider>() identity gate with real
        // Unity components: another collider on the spawner, or a child hitbox.
        foreach (bool secondary in new[] { true, false })
        foreach (bool physical in new[] { false, true })
        foreach (bool descriptorFirst in new[] { true, false })
        foreach (bool admitFirst in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly();
            if (physical) LethalImpulseExperiment.Physical();
            using (var c = new Case(secondary: secondary, childHit: !secondary))
            {
                if (admitFirst) c.Admit();
                LethalImpulseExperiment.Record record;
                if (descriptorFirst) { record = c.Stock(); c.Completed(); }
                else { record = c.Completed(); c.Stock(); }
                Check(!c.Ragdoll._hasImpulse && !record.StockSeen && record.Policy.StockRejected,
                    "exact EFT collider miss confirms unarmed stock, secondary=" + secondary);
                if (!admitFirst) c.Admit();
                Check(record.Policy.Finished && !record.Policy.Fallback && ShadowRagdollWorld.Native.Queued == 1,
                    "confirmed-unarmed Rupture-only commits at admission without waiting 250ms");
                c.ExternalStep();
                Check(Near(ShadowRagdollWorld.Native.Velocity.z, physical ? 0.9f : 3.6f),
                    "confirmed-unarmed gain=" + (physical ? 1 : 4) + ", descriptorFirst=" + descriptorFirst + ", admitFirst=" + admitFirst);
            }
        }
        foreach (bool stockMode in new[] { true, false })
        foreach (bool descriptorFirst in new[] { true, false })
        {
            if (stockMode) LethalImpulseExperiment.StockOnly(); else LethalImpulseExperiment.Baseline();
            using (var c = new Case(secondary: true))
            {
                LethalImpulseExperiment.Record record;
                if (descriptorFirst) { record = c.Stock(); c.Completed(); }
                else { record = c.Completed(); c.Stock(); }
                c.NativeStep();
                Check(record.Policy.Fallback && Near(c.Body.velocity.z, 3.6f),
                    "baseline/stock-only cannot manufacture absent stock or delay sole original Rupture impulse");
            }
        }
        foreach (bool timeout in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
            using (var c = new Case(secondary: true))
            {
                var record = c.Stock(); c.Completed();
                if (timeout) { record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); }
                else LethalImpulseExperiment.Admission(c.Ragdoll, false);
                c.NativeStep();
                Check(record.Policy.Fallback && Near(c.Body.velocity.z, 3.6f),
                    "confirmed-unarmed admission failure/timeout releases original amplified value");
            }
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(secondary: true))
        {
            var blocker = new HarmonyLib.Harmony("rupture.lethal.fixture.skip-descriptor");
            blocker.Patch(HarmonyLib.AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
                new[] { typeof(Collider), typeof(Vector3), typeof(Vector3), typeof(float) }),
                prefix: new HarmonyLib.HarmonyMethod(typeof(LethalImpulseRuntimeTests), nameof(SkipNativeSimulation)) { priority = HarmonyLib.Priority.Last });
            try
            {
                var record = c.Stock(); c.Completed(); c.Admit();
                Check(!record.Policy.StockRejected && !record.Policy.Finished && ShadowRagdollWorld.Native.Queued == 0,
                    "a descriptor skipped by another mod never proves stock absence");
                record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); c.ExternalStep();
                Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 3.6f), "skipped descriptor fails open once");
            }
            finally { blocker.UnpatchSelf(); }
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(secondary: true))
        {
            c.Ragdoll._hasImpulse = true; // an older descriptor already exists
            c.Ragdoll.ApplyImpulse(c.Collider, Vector3.forward, Vector3.zero, 45f);
            var record = c.Completed();
            Check(c.Ragdoll._hasImpulse && !record.Policy.StockRejected, "pre-existing pending stock is preserved and cannot count as absent");
        }
        LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
        using (var c = new Case(secondary: true))
        {
            c.Ragdoll.ApplyImpulse(c.Body, Vector3.forward, Vector3.zero, 45f); c.NativeStep();
            var record = c.Stock(); c.Completed(); c.NativeStep(); c.Admit(); c.ExternalStep();
            Check(record.Policy.Fallback && !record.Policy.StockRejected && Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f),
                "previously integrated stock cannot become absent or be removed by an unarmed descriptor");
        }
        LethalImpulseExperiment.Off();
        using (var c = new Case(capture: false, secondary: true))
        {
            c.Ragdoll.ApplyImpulse(c.Root.GetComponent<BoxCollider>(), Vector3.forward, Vector3.zero, 45f);
            LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
            LethalImpulseExperiment.Death(c.Player, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet,
                HitCollider = c.Collider, HitPoint = Vector3.zero, Direction = Vector3.forward },
                new ImpactData { Projectile = 42, MassGrams = 4, Speed = 900 });
            LethalImpulseExperiment.Bind(c.Ragdoll);
            c.Ragdoll.ApplyImpulse(c.Collider, Vector3.forward, Vector3.zero, 45f);
            var record = c.Completed(); c.Ragdoll.TryToApplyImpulse(); c.NativeStep();
            Check(record.Policy.Fallback && !record.Policy.StockRejected && Near(c.Body.velocity.z, 14.85f),
                "real older pending descriptor survives a failed lookup and releases both originals");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(secondary: true))
        {
            var record = c.Stock(); c.Completed();
            c.Ragdoll.ApplyImpulse(c.Collider, Vector3.forward, Vector3.zero, 45f);
            c.Admit(); c.ExternalStep();
            Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 3.6f),
                "repeated descriptors invalidate absence before admission and replay original once");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(secondary: true))
        {
            var record = c.Stock(); c.Completed();
            MultiplayerRuntime.Current = new FixtureRuntime { IsNetworkAuthority = true, HasServer = true, HasRemotePeers = true };
            c.Admit(); c.ExternalStep();
            Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 3.6f),
                "peer joining prevents confirmed-unarmed commit and releases original impulse");
        }
        MultiplayerRuntime.Current = new StandaloneRuntime();
        LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
        using (var c = new Case(secondary: true))
        {
            c.Stock(); c.Completed(); c.Admit(); c.ExternalStep();
            c.Ragdoll.ApplyImpulse(c.Body, Vector3.forward, Vector3.zero, 45f); c.ExternalStep();
            var events = (string[])typeof(LethalImpulseExperiment).GetField("Events", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(Near(ShadowRagdollWorld.Native.Velocity.z, 12.15f) && Array.Exists(events, e => e != null && e.Contains("comparison-invalidated")),
                "unexpected late stock remains unchanged and explicitly invalidates the comparison");
            c.Completed(77); c.ExternalStep();
            Check(Near(ShadowRagdollWorld.Native.Velocity.z, 15.75f), "subsequent corpse hits retain amplified momentum after a confirmed-unarmed selection");
        }
        LethalImpulseExperiment.Baseline();
        using (var c = new Case())
        {
            var stock = c.Stock(); c.Completed();
            Check(stock.StockNativePending && stock.RuptureNativePending && Near(c.Body.velocity.z, 0), "AddForce calls are pending before native simulation");
            c.NativeStep();
            Check(stock.StockNativeSteps == 1 && stock.RuptureNativeSteps == 1 && Near(c.Body.velocity.z, 14.85f), "both originals integrate into the same native body");
            c.Admit(); c.ExternalStep();
            Check(Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f), "integrated native momentum survives standalone handoff without reinjection");
            Measurements.Add("native baseline: queued velocity=0; integrated velocity=14.85 m/s; external inherited velocity=14.85 m/s (4 kg, 45+14.4 N s)");
        }
        LethalImpulseExperiment.Baseline();
        using (var c = new Case())
        {
            c.Stock(); c.NativeStep(); c.Admit(); c.Completed(); c.ExternalStep();
            Check(Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f), "native-integrated stock plus later external Rupture impulse overlap");
        }
        foreach (bool failAdmission in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly(); LethalImpulseExperiment.Physical();
            using (var c = new Case())
            {
                var record = c.Stock(); c.Completed();
                if (failAdmission) LethalImpulseExperiment.Admission(c.Ragdoll, false);
                else
                {
                    record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update();
                }
                c.NativeStep();
                Check(Near(c.Body.velocity.z, 14.85f), "failed admission/timeout replays ORIGINAL stock and amplified Rupture, physical selection excluded");
                Check(record.Policy.Fallback && !record.Policy.StockHeld && !record.Policy.RuptureHeld, "fallback drains held originals once");
                LethalImpulseExperiment.Admission(c.Ragdoll, false); c.NativeStep();
                Check(Near(c.Body.velocity.z, 14.85f), "repeated failure does not replay twice");
            }
        }
        foreach (bool stockOnly in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly();
            using (var c = new Case())
            {
                var record = stockOnly ? c.Stock() : c.Completed();
                record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); c.NativeStep();
                Check(Near(c.Body.velocity.z, stockOnly ? 11.25f : 3.6f), "one-sided timeout cannot silently lose sole observed impulse");
                if (stockOnly) c.Completed(); else c.Stock(); c.NativeStep();
                Check(Near(c.Body.velocity.z, 14.85f), "late counterpart remains baseline after timeout");
            }
        }
        foreach (bool integrated in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly();
            using (var c = new Case())
            {
                var record = c.Stock(); c.Completed();
                record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update();
                if (integrated) c.NativeStep();
                c.Admit(); c.ExternalStep();
                Check(Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f), "late admission preserves fallback originals, integrated=" + integrated);
                Check(ShadowRagdollWorld.Native.Queued == (integrated ? 0 : 2), "fallback migrates queued forces only; never reinjects integrated momentum");
            }
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case(valid: false))
        { c.Stock(); c.Completed(); c.NativeStep(); Check(Near(c.Body.velocity.z, 14.85f), "invalid ballistic identity fails open"); }
        foreach (bool explosion in new[] { true, false })
        {
            LethalImpulseExperiment.RuptureOnly();
            using (var c = new Case(capture: false))
            {
                if (explosion) LethalImpulseExperiment.Death(c.Player,
                    new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Explosion, HitCollider = c.Collider },
                    new ImpactData { Projectile = 42, MassGrams = 4, Speed = 900 });
                c.Stock(); c.Completed(); c.NativeStep();
                Check(Near(c.Body.velocity.z, 14.85f), "explosion or no terminal death record cannot acquire an experimental transaction");
            }
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            var record = c.Stock(); c.Completed(point: Vector3.right);
            record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); c.NativeStep();
            Check(Near(c.Body.velocity.z, 14.85f), "same projectile at another impact point is not treated as the lethal completion");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            var record = c.Stock(); record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2;
            c.Admit(); c.Completed(); c.ExternalStep();
            Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f), "admission after hold deadline fails open even before diagnostic Update");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            var record = c.Stock(); c.Completed(); c.Admit(); c.ExternalStep();
            LethalImpulseExperiment.End(c.Ragdoll, "settlement"); LethalImpulseExperiment.Admission(c.Ragdoll, true); c.ExternalStep();
            Check(Near(ShadowRagdollWorld.Native.Velocity.z, 3.6f) && ShadowRagdollWorld.Native.Queued == 1,
                "settlement and repeated admission cannot replay originals after successful selection");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            var record = c.Stock(); c.Completed(77);
            record.Policy.HeldAt = Time.realtimeSinceStartup - 1f; record.Policy.HeldFrame = Time.frameCount - 2; LethalImpulseExperiment.Update(); c.NativeStep();
            Check(Near(c.Body.velocity.z, 14.85f), "distinct postmortem projectile is not suppressed by lethal transaction");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            c.Stock(); c.Completed(); c.Admit(); c.ExternalStep();
            c.Completed(77); c.ExternalStep();
            Check(Near(ShadowRagdollWorld.Native.Velocity.z, 7.2f), "subsequent corpse impact retains current amplified momentum");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        { c.Stock(); c.Completed(); LethalImpulseExperiment.Off(); c.NativeStep(); Check(Near(c.Body.velocity.z, 14.85f), "turning experiment off releases held originals"); }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            var record = c.Stock(); c.Completed();
            UnityEngine.Object.DestroyImmediate(c.Body);
            LethalImpulseExperiment.Off();
            Check(record.Policy.Fallback && !record.Policy.StockHeld && !record.Policy.RuptureHeld && ShadowRagdollWorld.Native == null,
                "destroyed anatomy cancels held transaction without exceptions or creating a replacement connection");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            c.Stock(); c.Completed(); ShadowRagdollWorld.Enabled = false;
            LethalImpulseExperiment.Admission(c.Ragdoll, false); c.NativeStep();
            Check(Near(c.Body.velocity.z, 14.85f), "backend failure preserves native fallback"); ShadowRagdollWorld.Enabled = true;
        }
        LethalImpulseExperiment.Baseline();
        using (var c = new Case())
        {
            c.Admit(); var record = c.Stock();
            // Completion of an older in-flight step is NOT evidence for a force
            // queued behind that step. Only the subsequent submitted step counts.
            LethalImpulseExperiment.ExternalStepCompleted();
            Check(record.StockExternalPending, "older completion cannot consume newly queued external evidence");
            c.ExternalStep(); Check(!record.StockExternalPending && !record.StockExternalInFlight, "matching submitted step drains external evidence");
        }
        LethalImpulseExperiment.Baseline();
        using (var c = new Case())
        {
            var record = c.Stock();
            var blocker = new HarmonyLib.Harmony("rupture.lethal.fixture.skip");
            blocker.Patch(HarmonyLib.AccessTools.Method(typeof(Physics), nameof(Physics.Simulate), new[] { typeof(float) }),
                prefix: new HarmonyLib.HarmonyMethod(typeof(LethalImpulseRuntimeTests), nameof(SkipNativeSimulation)) { priority = HarmonyLib.Priority.First });
            try
            {
                c.NativeStep();
                Check(record.StockNativeSteps == 0 && record.StockNativePending && Near(c.Body.velocity.z, 0),
                    "a simulation skipped by another Harmony prefix is not reported as integrated");
            }
            finally { blocker.UnpatchSelf(); }
            Physics.defaultPhysicsScene.Simulate(1f / 60f);
            Check(record.StockNativeSteps == 1 && Near(c.Body.velocity.z, 11.25f), "default PhysicsScene wrapper also records real integration");
        }
        LethalImpulseExperiment.RuptureOnly();
        using (var c = new Case())
        {
            var record = c.Stock(); c.Completed();
            var other = new GameObject("unrelated-actor");
            try
            {
                record.RuptureBody = other.AddComponent<Rigidbody>();
                c.Admit(); c.ExternalStep();
                Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f),
                    "different resolved source bodies cannot commit single-source selection");
            }
            finally { UnityEngine.Object.DestroyImmediate(other); }
        }
        foreach (string role in new[] { "headless", "remote", "authority", "server", "client", "host-with-peers" })
        {
            var runtime = new FixtureRuntime();
            void SetNetworkRole()
            {
                runtime.IsHeadless = role == "headless";
                runtime.IsRemoteClient = role == "remote";
                runtime.IsNetworkAuthority = role == "authority" || role == "host-with-peers";
                runtime.HasServer = role == "server" || role == "host-with-peers";
                runtime.HasClient = role == "client";
                runtime.HasRemotePeers = role == "host-with-peers";
            }
            MultiplayerRuntime.Current = runtime;
            foreach (bool stockMode in new[] { true, false })
            {
                LethalImpulseExperiment.Reset(); SetNetworkRole();
                if (stockMode) LethalImpulseExperiment.StockOnly(); else LethalImpulseExperiment.RuptureOnly();
                Check(!LethalImpulseExperiment.Enabled, "active network role rejects comparison: " + role + ", stock=" + stockMode);
            }
            LethalImpulseExperiment.Baseline();
            using (var c = new Case())
            {
                c.Stock(); c.Completed(); c.NativeStep();
                Check(Near(c.Body.velocity.z, 14.85f), "network baseline retains both original impulses: " + role);
            }
            MultiplayerRuntime.Current = runtime = new FixtureRuntime { IsNetworkAuthority = true, HasServer = true };
            LethalImpulseExperiment.RuptureOnly();
            using (var c = new Case())
            {
                var record = c.Stock(); c.Completed(); SetNetworkRole(); c.Admit(); c.ExternalStep();
                Check(record.Policy.Fallback && Near(ShadowRagdollWorld.Native.Velocity.z, 14.85f),
                    "network role appearing before commit releases held originals: " + role);
            }
        }
        LethalImpulseExperiment.Reset();
        MultiplayerRuntime.Current = new StandaloneRuntime();
        using (var c = new Case(capture: false))
        {
            LethalImpulseExperiment.Death(c.Player, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet }, default, allowCreate: false);
            Check(!LethalImpulseExperiment.Enabled, "late damage notification cannot enable or open a terminal experiment");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                LethalImpulseExperiment.Update();
                LethalImpulseExperiment.Death(c.Player, default, default);
                LethalImpulseExperiment.BeforeStock(c.Ragdoll, c.Body, Vector3.forward, Vector3.zero, 45f, out _);
                var queueState = LethalImpulseExperiment.BeforeStockQueue(c.Ragdoll);
                LethalImpulseExperiment.StockQueued(c.Ragdoll, c.Collider, Vector3.forward, Vector3.zero, 45f, queueState, true);
                LethalImpulseExperiment.BeforeNativeStep(); LethalImpulseExperiment.AfterNativeStep(true);
                LethalImpulseExperiment.Completed(c.Corpse, c.Collider, c.Body, 42, Vector3.forward, Vector3.zero, 4, 900, 1, out _);
                var contact = LethalImpulseExperiment.EnterShot(null, default); LethalImpulseExperiment.RestoreShot(contact);
                RootVelocityExperiment.BeforeStart(c.Ragdoll, Vector3.right * 6);
                RootVelocityExperiment.AfterStart(null, Vector3.zero, true);
                RootVelocityExperiment.BeforeExternalStep(); RootVelocityExperiment.ExternalStepSubmitted(); RootVelocityExperiment.ExternalStepCompleted();
                AngularDriveExperiment.Capture(null);AngularDriveExperiment.BeforeExternalStep();AngularDriveExperiment.ExternalStepSubmitted();AngularDriveExperiment.ExternalStepCompleted();
                AngularDriveExperiment.CorpseImpact(c.Corpse,false,0,null);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(bytes == 0, "disabled instrumentation hot paths allocate zero bytes across 10000 iterations");
            Measurements.Add("disabled instrumentation: " + bytes + " allocated bytes across 10000 iterations; this is not a raid frametime measurement");
        }
        LethalImpulseExperiment.Baseline();
        using (var c = new Case(capture: false))
        {
            for (int i = 0; i < 80; i++)
            {
                Player player = c.Root.AddComponent<Player>(); player.ProfileId = "bounded-" + i;
                LethalImpulseExperiment.Death(player, new EFT.Ballistics.DamageInfo { DamageType = EDamageType.Bullet }, default);
            }
            var records = (System.Collections.ICollection)typeof(LethalImpulseExperiment).GetField("Records", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(records.Count == 32, "death tracking capacity is bounded and overflow fails open");
            LethalImpulseExperiment.Dump();
        }
        ProductionChecks();
        string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json");
        File.WriteAllText(path, "{\"passed\":true,\"cases\":" + _cases + ",\"unityVersion\":\"" + Application.unityVersion + "\",\"measurements\":[\"" + string.Join("\",\"", Measurements) + "\"]}");
        Rupture.Ragdolls.Patches.LethalImpulseHooks.Shutdown();
        Rupture.Ragdolls.Patches.RootVelocityHooks.Shutdown();
    }
}

namespace EFT
{
    public enum EDamageType { Bullet, Explosion }
    public enum EBodyPart { Head, Chest }
    public class Player : MonoBehaviour { public string ProfileId; }
}
namespace EFT.Ballistics
{
    public struct DamageInfo { public EDamageType DamageType; public Collider HitCollider; public Vector3 HitPoint, Direction; public int FireIndex; }
    public class Shot
    {
        public enum EBulletState { Flying, FragmentationHit }
        public Shot Parent;
        public EBulletState BulletState;
        public EFT.InventoryLogic.Item Ammo;
        public Collider HitCollider;
        public Vector3 HitPoint, Direction, CurrentDirection;
        public float BulletMassGram, VelocityMagnitude;
        public bool IsShotFinished;
        public ulong Projectile = 42;
        public int FireIndex, FragmentIndex, RandomSeed;
    }
}
namespace EFT.InventoryLogic
{
    public class Item { }
    public class Ammo : Item { public string Caliber = "fixture"; }
}
public class RigidbodySpawner : MonoBehaviour { public Rigidbody Rigidbody; }
public class CharacterJointSpawner : MonoBehaviour { }
namespace EFT.Interactive
{
    public class LootItem : MonoBehaviour { }
    public class Corpse : MonoBehaviour { public string PlayerProfileID; public CorpseRagdoll Ragdoll; }
    public class CorpseRagdoll
    {
        internal Vector3 _velocity;
        public void Start()
        {
            foreach (var spawner in _rigidbodySpawners)
            {
                Vector3 normalized = _velocity.normalized;
                _velocity = normalized.Equals(Vector3.up) ? normalized : Vector3.ClampMagnitude(_velocity, 2f);
                spawner.Rigidbody.isKinematic = false; spawner.Rigidbody.velocity = _velocity;
            }
        }
        public MonoBehaviour _owner; public bool _hasImpulse; internal Rigidbody Body; internal Collider Collider;
        public RigidbodySpawner[] _rigidbodySpawners;
        public CharacterJointSpawner[] _jointSpawners = Array.Empty<CharacterJointSpawner>();
        private RigidbodySpawner _impulseRigidbodySpawner;
        private Vector3 _impulseDirection, _impulsePoint;
        private float _impulseThrust;
        public bool FindRigidbodySpawner(Collider collider, out RigidbodySpawner spawner)
        {
            if (collider != null)
                foreach (var candidate in _rigidbodySpawners)
                    if (candidate.GetComponent<Collider>() == collider) { spawner = candidate; return true; }
            spawner = null; return false;
        }
        public void ApplyImpulse(Collider collider, Vector3 direction, Vector3 point, float thrust)
        {
            if (!FindRigidbodySpawner(collider, out var spawner)) return;
            _hasImpulse = true; _impulseRigidbodySpawner = spawner;
            _impulseDirection = direction; _impulsePoint = point; _impulseThrust = thrust;
        }
        public void TryToApplyImpulse()
        {
            if (!_hasImpulse) return;
            _hasImpulse = false;
            ApplyImpulse(_impulseRigidbodySpawner.Rigidbody, _impulseDirection, _impulsePoint, _impulseThrust);
        }
        public void ApplyImpulse(Rigidbody rigidbody, Vector3 direction, Vector3 point, float thrust)
        { Rupture.Ragdolls.Classes.RagdollCoordinator.TryApplyPointImpulse(rigidbody, direction * thrust, point); }
    }
}
namespace Rupture.Impacts
{
    internal struct ImpactData
    {
        internal ulong Projectile; internal int FireIndex; internal float MassGrams, Speed;
        internal float Momentum => MassGrams * 0.001f * Speed; internal bool Valid => Projectile != 0 && Momentum > 0;
        internal static ulong ProjectileIdentity(EFT.Ballistics.Shot shot) => shot.Projectile;
    }
    internal static class ImpactStateService { internal static bool Eligible(EFT.Ballistics.DamageInfo d) => d.DamageType == EDamageType.Bullet; }
}
namespace Rupture
{
    internal class FixtureRuntime
    {
        internal bool IsHeadless, IsRemoteClient, IsNetworkAuthority, HasServer, HasClient, HasRemotePeers;
    }
    internal sealed class StandaloneRuntime : FixtureRuntime { }
    internal static class MultiplayerRuntime { internal static FixtureRuntime Current = new StandaloneRuntime(); }
    internal sealed class FixtureLog
    { internal void Initialization(object m) => Debug.Log(m); internal void InitializationWarning(object m) => Debug.LogWarning(m); internal void LogWarning(object m) => Debug.LogWarning(m); internal void LogInfo(object m) => Debug.Log(m); }
    internal static class RupturePlugin { internal static FixtureLog Log = new FixtureLog(); }
    internal static class RuptureConfiguration
    {
        internal class FloatSetting { internal float Value = 1f; }
        internal class BoolSetting { internal bool Value; }
        internal static FloatSetting ShotIntensity = new FloatSetting();
        internal static BoolSetting CorrectedLethalImpulse = new BoolSetting(), InheritRootVelocity = new BoolSetting(), AngularResistance = new BoolSetting();
    }
}
namespace Rupture.Combined.Patches { }
namespace Rupture.Dismemberment.Classes.HeadWounds
{
    internal static class HeadWoundSystem
    {
        internal static bool TryCompleteLethalHeadshot(EFT.Ballistics.Shot shot, float mass, float speed, Vector3 direction) => false;
    }
}
namespace Rupture.Ragdolls.Classes
{
    internal struct CorpseInteractionTarget { internal Transform VisualRoot; }
    internal static class RagdollCoordinator
    {
        internal static void PrepareCorpseImpact(Corpse corpse, bool explosion, object awakenedStates, out CorpseInteractionTarget target, out bool postMortem)
        { target = default; postMortem = false; }
        internal static bool TryResolveCorpseBodyPart(CorpseInteractionTarget target, Collider collider, out EBodyPart part, out Collider canonical)
        { part = default; canonical = null; return false; }
        internal static bool IsTrackedBody(Rigidbody body) => false;
        internal static bool TryApplyIndependentWeaponPointImpulse(Corpse corpse, Collider collider, Vector3 impulse, Vector3 point, out string name)
        { name = null; return false; }
        internal static bool TryApplyPointImpulse(Rigidbody body, Vector3 impulse, Vector3 point)
        {
            if (body == null) return false;
            if (ShadowRagdollWorld.IsExternalBody(body)) { ShadowRagdollWorld.Native.Add(impulse, point); return true; }
            if (body.isKinematic) return false;
            body.AddForceAtPosition(impulse, point, ForceMode.Impulse); return true;
        }
        internal static bool TryApplyCorpsePointImpulse(Corpse corpse, Collider collider, Vector3 impulse, Vector3 point, out string name)
        { name = "fixture"; return collider != null && TryApplyPointImpulse(collider.attachedRigidbody, impulse, point); }
    }
    internal interface IShadowBodyProxy { Vector3 velocity { get; set; } bool isKinematic { get; set; } Quaternion rotation { get; } Vector3 angularVelocity { get; } }
    internal sealed class ExternalShadowBodyProxy : IShadowBodyProxy
    {
        internal sealed class FixtureHandle { internal bool IsValid = true; }
        internal readonly FixtureHandle ExternalBody = new FixtureHandle();
        internal LethalImpulseNativeFixture Native;
        internal bool FailWriteOnce;
        public bool isKinematic { get; set; }
        public Quaternion rotation => Native.Rotation;
        public Vector3 angularVelocity => Native.Angular;
        public Vector3 velocity
        {
            get => Native.Velocity;
            set { if (FailWriteOnce) { FailWriteOnce = false; throw new InvalidOperationException("fixture-write"); } Native.SetVelocity(value); }
        }
    }
    internal static partial class ShadowRagdollWorld
    {
        internal sealed class ShadowBody
        { internal Rigidbody Native; internal IShadowBodyProxy Proxy; internal bool CountsForSleep = true; }
        internal sealed class ShadowRig
        {
            internal Corpse Corpse;
            internal bool Active = true, Committed = true;
            internal readonly List<ShadowBody> CoreBodies = new List<ShadowBody>();
            internal readonly List<ExternalJointBinding> ExternalJoints = new List<ExternalJointBinding>();
            internal readonly Dictionary<Rigidbody,ShadowBody> BodiesByNative = new Dictionary<Rigidbody,ShadowBody>();
            internal readonly HashSet<ShadowBody> PermanentlyDisabledBodies = new HashSet<ShadowBody>();
        }
        internal sealed class ExternalJointBinding
        { internal Rupture.Ragdolls.External.IExternalPhysicsJoint Joint; internal CharacterJoint NativeJoint; internal ShadowBody Body,ConnectedBody; }
        internal static bool IsDragHeld(Corpse corpse) => FixtureDragHeld;
        internal static bool FixtureDragHeld;
        private static Rupture.PhysXBackend.ExternalPhysXRuntime _externalRuntime;
        internal static Rupture.PhysXBackend.ExternalPhysXRuntime FixtureBackend => _externalRuntime;
        internal static void EnsureAngularFixtureCapability() { if (_externalRuntime == null) _externalRuntime = new Rupture.PhysXBackend.ExternalPhysXRuntime(IntPtr.Zero); }
        private static readonly List<LethalImpulseNativeFixture> AdditionalActors = new List<LethalImpulseNativeFixture>();
        internal static readonly Dictionary<CorpseRagdoll, ShadowRig> RigsByRagdoll = new Dictionary<CorpseRagdoll, ShadowRig>();
        private static bool IsFinite(Vector3 v) => RootVelocityExperiment.Finite(v);
        internal static bool Enabled = true;
        internal static LethalImpulseNativeFixture Native;
        private static Corpse _corpse; private static Rigidbody _body;
        internal static bool IsExternalBody(Rigidbody body) => Native != null && body == _body;
        internal static bool TryApplyLocalDetachedGibPointImpulse(Collider collider, Vector3 impulse, Vector3 point, out string name)
        { name = null; return false; }
        internal static bool TryResolveHitCorpse(Collider collider, out Corpse corpse)
        { corpse = collider != null ? collider.GetComponentInParent<Corpse>() : null; return corpse != null; }
        internal static bool IsIndependentWeaponHit(Corpse corpse, Collider collider) => false;
        internal static bool TryResolveHitBody(Corpse corpse, Collider collider, out Rigidbody body)
        { body = collider != null ? collider.attachedRigidbody : null; return body != null; }
        internal static bool IsActive(Corpse corpse) => Native != null && corpse == _corpse;
        internal static bool HasActiveLethalExperimentBody(Corpse corpse, Rigidbody body) => IsActive(corpse) && body != null && body == _body;
        internal static void Admit(Corpse corpse, Rigidbody body)
        {
            _corpse = corpse; _body = body; Native = new LethalImpulseNativeFixture(body); body.isKinematic = true;
            var rig = new ShadowRig { Corpse = corpse };
            var main = new ShadowBody { Native = body, Proxy = new ExternalShadowBodyProxy { Native = Native } };
            rig.CoreBodies.Add(main); rig.BodiesByNative[body] = main;
            foreach (var spawner in corpse.Ragdoll._rigidbodySpawners)
            {
                if (spawner.Rigidbody == body) continue;
                var extra = new LethalImpulseNativeFixture(spawner.Rigidbody, Native); AdditionalActors.Add(extra);
                var mapped = new ShadowBody { Native = spawner.Rigidbody, Proxy = new ExternalShadowBodyProxy { Native = extra } };
                rig.CoreBodies.Add(mapped); rig.BodiesByNative[mapped.Native] = mapped; spawner.Rigidbody.isKinematic = true;
            }
            _externalRuntime?.Dispose(); _externalRuntime = new Rupture.PhysXBackend.ExternalPhysXRuntime(Native.SceneHandle);
            foreach (var spawner in corpse.Ragdoll._jointSpawners)
            {
                var source = spawner.GetComponent<CharacterJoint>();
                var child = rig.BodiesByNative[source.GetComponent<Rigidbody>()]; var parent = rig.BodiesByNative[source.connectedBody];
                var childNative = ((ExternalShadowBodyProxy)child.Proxy).Native; var parentNative = ((ExternalShadowBodyProxy)parent.Proxy).Native;
                var joint = _externalRuntime.Create(source, childNative.ActorHandle, parentNative.ActorHandle);
                rig.ExternalJoints.Add(new ExternalJointBinding { Joint = joint, NativeJoint = source, Body = child, ConnectedBody = parent });
            }
            RigsByRagdoll[corpse.Ragdoll] = rig;
        }
        internal static bool TryApplyLethalExperimentImpulse(Corpse corpse, Rigidbody body, Vector3 impulse, Vector3 point)
        { if (!IsActive(corpse) || body != _body || body == null) return false; Native.Add(impulse, point); return true; }
        internal static bool TryReadLethalExperimentMotion(Rigidbody body, out Vector3 velocity, out Vector3 angular)
        { velocity = IsExternalBody(body) ? Native.Velocity : Vector3.zero; angular = IsExternalBody(body) ? Native.Angular : Vector3.zero; return IsExternalBody(body); }
        internal static void Clear()
        {
            _externalRuntime?.Dispose(); _externalRuntime = null;
            foreach (var extra in AdditionalActors) extra.Dispose(); AdditionalActors.Clear();
            Native?.Dispose(); Native = null; _corpse = null; _body = null; RigsByRagdoll.Clear(); FixtureDragHeld = false;
        }
    }
}
#endif
