#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Rupture;
using Rupture.Casings;
using Rupture.Ragdolls.External;
using UnityEngine;

[UnityEditor.InitializeOnLoad]
public static class CasingRuntimeTests
{
    static CasingRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool("CasingTests", false)) return;
            UnityEditor.SessionState.SetBool("CasingTests", false);
            try { Run(); UnityEditor.EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); UnityEditor.EditorApplication.Exit(1); }
        };
    }
    public static void RunBatch()
    {
        var tags = new UnityEditor.SerializedObject(UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        tags.FindProperty("layers").GetArrayElementAtIndex(8).stringValue = "Deadbody"; tags.ApplyModifiedProperties();
        UnityEditor.SessionState.SetBool("CasingTests", true); UnityEditor.EditorApplication.EnterPlaymode();
    }
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; Debug.Log("PASS: " + message); }
    private static void Run()
    {
        Check(CasingGeometry.TryFitBounds(new Bounds(Vector3.zero, new Vector3(.01f, .04f, .01f)), out var fit) &&
            Mathf.Abs(fit.Radius - .005f) < 1e-6f && Mathf.Abs(fit.HalfHeight - .015f) < 1e-6f,
            "capsule preserves casing diameter and length");
        Check(Vector3.Distance(fit.ShapePose.Rotation * Vector3.right, Vector3.up) < .0001f, "capsule axis follows longest mesh dimension");
        Check(fit.Box == 1, "retained primitive uses the measured box policy");
        Check(!CasingGeometry.TryFitBounds(new Bounds(Vector3.zero, Vector3.zero), out _), "degenerate casing geometry rejected");
        var runtime = new CasingBackendFixture();
        Rupture.Ragdolls.Classes.ShadowRagdollWorld.LocalSimulationRuntime = runtime;
        var manager = new CasingWorld();
        var world = new GameObject("world").AddComponent<EFT.GameWorld>();
        RuptureConfiguration.MaximumCasings.Value = 2;
        EFT.AssetsManager.AmmoPoolObject Spawn(bool used, bool jam = false, Vector3 at = default, object listener = null)
        {
            var root = new GameObject("cartridge"); root.transform.position = at;
            root.AddComponent<Rigidbody>().useGravity = false;
            var visual = root.AddComponent<EFT.AssetsManager.AmmoPoolObject>();
            visual.Shell = root.AddComponent<EFT.Shell>(); visual.Shell.CollisionListener = listener;
            // Names from the installed M855 bundle; EFT matches substrings,
            // not an exact transform named "shell" or "full".
            var shell = GameObject.CreatePrimitive(PrimitiveType.Cube); shell.name = "patron_556x45_m855_shell_LOD0";
            shell.transform.SetParent(root.transform, false); shell.transform.localScale = new Vector3(.01f, .04f, .01f);
            var full = GameObject.CreatePrimitive(PrimitiveType.Cube); full.name = "patron_556x45_m855_full_LOD0"; full.transform.SetParent(root.transform, false);
            full.transform.localScale = Vector3.one * 10; full.SetActive(false);
            world._ammoShells.Add(visual); manager.SetUsed(visual, jam ? false : used); manager.Spawn(world, visual);
            manager.Activated(visual.Shell, root.transform.position, Vector3.right, new Vector3(0, 2, 0), Vector3.forward);
            if (jam) manager.SetUsed(visual, used);
            manager.Admit(visual, 1);
            return visual;
        }
        void Tick() { manager.BeforeStep(runtime); runtime.Complete(); }
        var live = Spawn(false);
        Check(manager.AllowReturn(live) && !manager.HasWork(), "ejected live round remains native");
        live.transform.Find("patron_556x45_m855_full_LOD0").gameObject.SetActive(true);
        Check(CasingGeometry.TryFit(live, out var namedFit, out _) && namedFit.Radius < .01f,
            "prefixed shell LOD names fit while active full-cartridge geometry is excluded");
        EFTHardSettings.Instance.Shells.velocityRotation = 90;
        EFTHardSettings.Instance.Shells.velocityMult = 2;
        EFTHardSettings.Instance.Shells.playMult = .5f;
        var first = Spawn(true);
        Check(!manager.AllowReturn(first) && !first.Shell.enabled && !first.enabled, "admission suppresses native movement and pool return");
        Check(first.GetComponent<Rigidbody>().isKinematic && !first.GetComponent<Rigidbody>().detectCollisions,
            "existing Unity physics is parked during retained simulation");
        Tick();
        Check(runtime.LastCreates == 1 && runtime.LastDescriptor.Radius < .01f, "spent shell mesh used and full cartridge excluded");
        Check(Vector3.Distance(runtime.LastDescriptor.Velocity, Vector3.up) < .0001f &&
            Vector3.Distance(runtime.LastDescriptor.AngularVelocity, Vector3.up * (200f * Mathf.Deg2Rad)) < .0001f,
            "native configured ejection adjustments and angular units are preserved");
        EFTHardSettings.Instance.Shells.velocityRotation = 0;
        EFTHardSettings.Instance.Shells.velocityMult = EFTHardSettings.Instance.Shells.playMult = 1;
        Check(!runtime.MainThreadObjectInCommands, "primitive creation contains no Unity object references");
        Check(world._ammoShells.Contains(first), "native world ownership retained until actor success");
        Tick();
        Check(!world._ammoShells.Contains(first) && first.transform.parent.name == "Rupture casings", "successful creation transfers cleanup and presentation ownership");
        Check(runtime.EnvironmentAcquires > 0, "casing-only world obtains environment support");
        manager.ReceiveCompleted(runtime);
        Check(!manager.HasWork() && manager.IsOwned(first), "retained sleeping casing does not require another step");
        runtime.InjectState(new CasingState { Id=runtime.LastDescriptor.Id, Pose=runtime.LastDescriptor.Pose, Flags=0 });
        manager.ReceiveCompleted(runtime);
        Check(manager.HasWork(), "environment-driven casing wake restarts simulation");
        runtime.InjectState(new CasingState { Id=runtime.LastDescriptor.Id, Pose=runtime.LastDescriptor.Pose, Flags=1 });
        manager.ReceiveCompleted(runtime);Tick();manager.ReceiveCompleted(runtime);
        Check(!manager.HasWork(), "final casing sleep drains without a perpetual frame");
        var second = Spawn(true, true); Tick(); Tick();
        Check(!world._ammoShells.Contains(second), "spent jam is admitted despite later SetUsed callback");
        var shot = new EFT.Ballistics.Shot { HitPoint = Vector3.zero, HitNormal = Vector3.up, BulletMassGram = 8, VelocityMagnitude = 400 };
        manager.NewShot(shot); manager.Trace(shot, Vector3.left, Vector3.right, true);
        Check(manager.HasWork(), "shot query wakes retained sleeping casings");Tick();
        Check(runtime.LastImpacts == 1 && runtime.LastImpact.End == Vector3.zero && runtime.LastImpact.Strength > 3,
            "direct query clips at native blocking hit and carries projectile momentum");
        var third = Spawn(true);
        Check(!first.gameObject.activeSelf && first.Returns == 0 && !manager.AllowReturn(first), "eviction hides oldest casing but holds pool guard until physics removal");
        Tick(); Tick();
        Check(first.Returns == 1 && second.Returns == 0, "owner removal acknowledgement releases oldest casing to pool");
        RuptureConfiguration.MaximumCasings.Value = 1; Tick(); Tick();
        Check(second.Returns == 1 && third.Returns == 0, "runtime capacity reduction evicts oldest excess");
        RuptureConfiguration.PersistentCasings.Value = false; Tick(); Tick();
        Check(third.Returns == 1, "disabling releases retained casings");
        RuptureConfiguration.PersistentCasings.Value = true;
        var failed = Spawn(true); runtime.FailCreation = true; Tick(); Tick();
        Check(world._ammoShells.Contains(failed) && failed.Shell.enabled && failed.enabled && manager.AllowReturn(failed), "failed actor creation restores native countdown and physics");
        Check(!failed.GetComponent<Rigidbody>().isKinematic && failed.GetComponent<Rigidbody>().detectCollisions,
            "failed admission restores original Unity body settings");
        runtime.FailCreation = false;
        runtime.FailEnvironment = true;
        var unsupported = Spawn(true); Tick();
        Check(unsupported.enabled && unsupported.Shell.enabled && world._ammoShells.Contains(unsupported),
            "unsupported required environment returns candidate to native behavior");
        runtime.FailEnvironment = false;
        var pending = Spawn(true); Tick(); manager.Reset();
        Check(pending.Returns == 1, "raid reset drains pending visual before pool disposal");
        var next = Spawn(true); Tick(); Tick();
        Check(next.Returns == 0 && !world._ammoShells.Contains(next), "stale completion cannot claim next raid generation");
        manager.Reset();
        Check(runtime.EnvironmentReleases > 0, "eviction and reset release shared environment references");
        // Real Unity object destruction exercises retained identity cleanup.
        var destroyed = Spawn(true); Tick(); Tick(); UnityEngine.Object.DestroyImmediate(destroyed.gameObject); Tick();
        Check(runtime.LastRemoves > 0, "destroyed presentation retires native casing");
        manager.Reset();
        var disabled = Spawn(true); Tick(); Tick();
        RuptureConfiguration.MaximumCasings.Value = 0; Tick(); Tick();
        Check(disabled.Returns == 1 && !manager.HasWork(), "zero capacity disables and drains takeover");
        RuptureConfiguration.MaximumCasings.Value = 2;
        var backendFailed = Spawn(true); Tick(); Tick(); manager.BackendFailed();
        var afterFailure = Spawn(true);
        Check(backendFailed.Returns == 1 && manager.AllowReturn(afterFailure), "backend failure retires ownership and disables further raid admission");
        manager.Reset();
        // Asynchronous environment path of a prefetch-capable backend: no synchronous capture while cells are ready or pending.
        runtime.SyncCasingEnvironments = runtime.TryAcquires = 0;
        var prefetched = Spawn(true); Tick();
        Check(runtime.LastCreates == 1 && runtime.TryAcquires > 0 && runtime.SyncCasingEnvironments == 0,
            "bound environment cells admit a casing without a synchronous capture");
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Pending;
        var held = Spawn(true, false, new Vector3(50, 0, 0)); Tick();
        Check(runtime.LastCreates == 0 && runtime.SyncCasingEnvironments == 0 && world._ammoShells.Contains(held) && manager.HasWork(),
            "a pending urgent cell holds a new casing out of the native scene instead of capturing synchronously");
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Ready; Tick();
        Check(runtime.LastCreates == 1 && runtime.SyncCasingEnvironments == 0, "a held casing is created once its environment is bound");
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Pending;
        var expired = Spawn(true, false, new Vector3(100, 0, 0)); Tick();
        Check(runtime.LastCreates == 0 && runtime.SyncCasingEnvironments == 0, "a casing is held while its environment stays pending");
        System.Threading.Thread.Sleep(250); Tick();
        Check(runtime.LastCreates == 1 && runtime.SyncCasingEnvironments > 0, "a casing held beyond the window captures its environment synchronously");
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Failed; runtime.SyncCasingEnvironments = 0;
        var failedAsync = Spawn(true, false, new Vector3(150, 0, 0)); Tick();
        Check(runtime.LastCreates == 1 && runtime.SyncCasingEnvironments > 0, "a failed asynchronous acquisition falls back to the synchronous capture");
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Ready; manager.Reset();
        var target = Spawn(true); Tick(); Tick();
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Pending;
        manager.Explosion(new FixtureExplosive(), new Vector3(2, 0, 0)); Tick();
        Check(runtime.LastImpacts == 0 && manager.HasWork(), "a blast waits for pending occlusion geometry instead of passing through it");
        runtime.AcquireState = ExternalEnvironmentPrefetchState.Ready; Tick();
        Check(runtime.LastImpacts == 1, "a deferred blast is submitted once its occlusion geometry is bound");
        manager.ReceiveCompleted(runtime);
        int releasesBefore = runtime.EnvironmentReleases;
        System.Threading.Thread.Sleep(1100); manager.HasWork();
        Check(runtime.EnvironmentReleases > releasesBefore, "exposure cells are released after their retention even though the idle world takes no step");
        Check(CasingWorld.UrgentSeconds(.15f) >= .15f && CasingWorld.UrgentSeconds(1f / 60f) >= .05f && CasingWorld.UrgentSeconds(.15f) >= .1f,
            "the urgent coverage window spans a long frame's whole simulated step");
        // Local player area prefetch.
        manager.Reset();
        runtime.AreaRequests = runtime.AreaCancels = 0;
        var playerObject = new GameObject("player").AddComponent<EFT.Player>();
        playerObject.Id = 7; playerObject.Position = new Vector3(3, 0, 4); playerObject.Velocity = new Vector3(2, 0, 0);
        world.MainPlayer = playerObject; Comfort.Common.Singleton<EFT.GameWorld>.Instance = world;
        manager.HasWork();
        Check(runtime.AreaRequests == 1 && runtime.LastAreaConsumer == (0x8000_0000u | 7u) &&
            Vector3.Distance(runtime.LastAreaCenter, new Vector3(3, 1, 4)) < .0001f && Vector3.Distance(runtime.LastAreaSweepEnd, new Vector3(5, 1, 4)) < .0001f,
            "the local player's area prefetch uses the bot consumer formula and a corridor along their movement");
        manager.HasWork();
        Check(runtime.AreaRequests == 1, "the area prefetch is not re-requested inside its refresh interval");
        playerObject.Position += new Vector3(2, 0, 0); System.Threading.Thread.Sleep(550); manager.HasWork();
        Check(runtime.AreaRequests == 2, "the area prefetch refreshes after the player moved a metre");
        manager.Reset();
        Check(runtime.AreaCancels == 1, "raid reset cancels the area prefetch");
        Comfort.Common.Singleton<EFT.GameWorld>.Instance = null;
        // Contact audio: the local shooter flag, the per-frame cap and the pooled-source warm-up reach the audio boundary.
        RuptureConfiguration.CasingSounds.Value = true; RuptureConfiguration.MaximumCasings.Value = 64;
        var listenerObject = new GameObject("listener") { tag = "MainCamera" }; listenerObject.AddComponent<Camera>();
        CasingAudio.WarmCalls = 0;
        for (int i = 0; i < 5; i++) manager.HasWork();
        Check(CasingAudio.WarmCalls == 3, "pooled audio sources are warmed one step per frame until the warm-up reports done");
        // A warm-up that stops early returns its borrowed sources: the setting turned off, a backend failure, a raid reset.
        manager.Reset(); CasingAudio.WarmCalls = 0; manager.HasWork();
        Check(CasingAudio.HoldsSources, "a warm-up step keeps its borrowed source until the group is stocked");
        int cancelsBefore = CasingAudio.Cancels;
        RuptureConfiguration.CasingSounds.Value = false; manager.HasWork();
        Check(CasingAudio.Cancels == cancelsBefore + 1 && !CasingAudio.HoldsSources, "turning casing sounds off mid warm-up returns the borrowed sources");
        RuptureConfiguration.CasingSounds.Value = true; CasingAudio.WarmCalls = 0; manager.HasWork(); cancelsBefore = CasingAudio.Cancels;
        manager.BackendFailed();
        Check(CasingAudio.Cancels == cancelsBefore + 1 && !CasingAudio.HoldsSources, "a backend failure mid warm-up returns the borrowed sources");
        manager.Reset(); CasingAudio.WarmCalls = 0; manager.HasWork(); cancelsBefore = CasingAudio.Cancels;
        manager.Reset();
        Check(CasingAudio.Cancels == cancelsBefore + 1 && !CasingAudio.HoldsSources, "raid reset returns borrowed warm-up sources");
        ulong ShotId(bool local)
        {
            Spawn(true, false, Vector3.zero, local ? new LocalShellListener() : null); Tick();
            ulong id = runtime.LastDescriptor.Id; manager.ReceiveCompleted(runtime); return id;
        }
        ulong remoteId = ShotId(false), localId = ShotId(true);
        CasingAudio.Calls.Clear();
        runtime.InjectContacts(new CasingContact { Id = remoteId, Position = Vector3.forward, Normal = Vector3.up, Speed = 1 },
            new CasingContact { Id = localId, Position = Vector3.forward, Normal = Vector3.up, Speed = 2 });
        manager.ReceiveCompleted(runtime);
        Check(CasingAudio.Calls.Count == 2 && CasingAudio.Calls[0] == (localId, true) && CasingAudio.Calls[1] == (remoteId, false),
            "contact sounds play loudest first and only a local player's casing skips occlusion");
        runtime.InjectContacts(new CasingContact { Id = localId, Position = Vector3.forward, Normal = Vector3.up, Speed = 2 });
        manager.ReceiveCompleted(runtime);
        Check(CasingAudio.Calls.Count == 2, "a casing inside its sound cooldown does not play again");
        var crowd = new CasingContact[14];
        for (int i = 0; i < crowd.Length; i++) crowd[i] = new CasingContact { Id = ShotId(false), Position = Vector3.forward, Normal = Vector3.up, Speed = 1 };
        CasingAudio.Calls.Clear();
        runtime.InjectContacts(crowd); manager.ReceiveCompleted(runtime);
        Check(CasingAudio.Calls.Count == 12, "one frame starts at most twelve contact sounds");
        RuptureConfiguration.CasingSounds.Value = false; RuptureConfiguration.MaximumCasings.Value = 2;
        string report = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json");
        File.WriteAllText(report, "{\"passed\":true,\"cases\":" + _checks + ",\"unityVersion\":\"" + Application.unityVersion + "\"}");
    }
}

// EFT/backend boundary doubles. Production geometry, ownership and batch code run unchanged.
namespace HarmonyLib { internal static class AccessTools { internal static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); } }
namespace Comfort.Common { public static class Singleton<T> where T : class { public static T Instance; public static bool Instantiated => Instance != null; } }
namespace EFT
{
    public enum ECaliber { Shell9Mm, Shell556Mm, Shell12Cal, ShellHeavy }
    public class GameWorld : MonoBehaviour { public List<AssetsManager.AmmoPoolObject> _ammoShells = new List<AssetsManager.AmmoPoolObject>(); public Player MainPlayer; }
    public class Player : MonoBehaviour { public int Id; public Vector3 Position, Velocity; }
    public class Shell : MonoBehaviour
    {
        public ECaliber _caliber = ECaliber.Shell556Mm;
        public object CollisionListener;
        public void DisablePhysics() { enabled = false; }
    }
    public interface IExplosiveItem { bool IsDummy { get; } float MaxExplosionDistance { get; } float GetStrength { get; } }
}
namespace EFT.AssetsManager
{
    public class AssetPoolObject : MonoBehaviour
    {
        public string Name => name;
        public static void ReturnToPool(GameObject obj, bool immediate) { var v = obj.GetComponent<AmmoPoolObject>(); v.Returns++; obj.SetActive(false); }
    }
    public class AmmoPoolObject : AssetPoolObject
    {
        public EFT.Shell Shell;
        public bool ShouldBeDestroyed;
        public int Returns;
        public void StartAutoDestroyCountDown(float value) { enabled = true; }
    }
}
public class EFTHardSettings
{
    public static EFTHardSettings Instance = new EFTHardSettings();
    public ShellSettings Shells = new ShellSettings();
    public class ShellSettings { public float velocityRotation, velocityMult = 1, playMult = 1; }
}
namespace EFT.Ballistics
{
public class Shot
{
    public Vector3 HitPoint, HitNormal;
    public Collider HitCollider;
    public float BulletMassGram, VelocityMagnitude;
}
}
namespace Rupture
{
    public class TestSetting<T> { public T Value; public TestSetting(T value) { Value = value; } }
    public static class RuptureConfiguration
    {
        public static TestSetting<bool> PersistentCasings = new TestSetting<bool>(true), CasingSounds = new TestSetting<bool>(false),
            CasingImpactScatter = new TestSetting<bool>(true), EnableDebugLogging = new TestSetting<bool>(false);
        public static TestSetting<int> MaximumCasings = new TestSetting<int>(4096);
    }
    public static class RupturePlugin { public static TestLog Log = new TestLog(); }
    public class TestLog { public void LogInfo(object message) { Debug.Log(message); } public void LogWarning(object message) { Debug.LogWarning(message); } public void LogError(object message) { Debug.LogError(message); } }
}
namespace Rupture.Casings
{
    public sealed class LocalShellListener { }
    internal static class CasingAudio
    {
        internal static readonly List<(ulong Id, bool Local)> Calls = new List<(ulong Id, bool Local)>();
        internal static int WarmCalls, Cancels;
        internal static bool Play(CasingContact c, EFT.ECaliber caliber, bool localShooter) { Calls.Add((c.Id, localShooter)); return true; }
        internal static bool IsLocalShooter(object listener) => listener is LocalShellListener;
        internal static bool Holding;
        internal static bool HoldsSources => Holding;
        internal static bool WarmPool() { bool done = ++WarmCalls >= 3; Holding = !done; return done; }
        internal static void CancelWarmup() { Cancels++; Holding = false; }
    }
}
namespace Rupture.Ragdolls.Classes
{
    internal static partial class ShadowRagdollWorld
    {
        internal static IExternalPhysicsRuntime LocalSimulationRuntime;
        internal static int LocalEnvironmentMask = 1;
    }
}
namespace Rupture.Ragdolls.External
{
    public interface IExternalPhysicsRuntime
    {
        bool IsRaidReady { get; }
        ExternalCasingFrame TakeCasingFrame();
        void QueueCasingFrame(ExternalCasingFrame frame);
        bool EnsureEnvironment(uint id, Vector3 p, Vector3 e, out int result, out string failure);
        bool EnsureCasingEnvironment(uint id, Vector3 p, Vector3 e, out string failure);
        void ReleaseEnvironment(uint id);
    }
}
internal sealed class FixtureExplosive : EFT.IExplosiveItem { public bool IsDummy => false; public float MaxExplosionDistance => 5f; public float GetStrength => 10f; }
internal sealed class CasingBackendFixture : IExternalPhysicsRuntime, IExternalEnvironmentPrefetchRuntime
{
    public bool IsRaidReady => true;
    internal bool FailCreation, FailEnvironment;
    internal bool MainThreadObjectInCommands => Array.Exists(typeof(CasingDescriptor).GetFields(), f => typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType));
    internal int LastCreates, LastRemoves, LastImpacts, EnvironmentAcquires, EnvironmentReleases;
    internal int SyncCasingEnvironments, TryAcquires, AreaRequests, AreaCancels;
    internal ExternalEnvironmentPrefetchState AcquireState = ExternalEnvironmentPrefetchState.Ready;
    internal uint LastAreaConsumer;
    internal Vector3 LastAreaCenter, LastAreaSweepEnd;
    internal CasingImpact LastImpact;
    internal CasingDescriptor LastDescriptor;
    private ExternalCasingFrame _queued, _completed;
    public ExternalCasingFrame TakeCasingFrame() { var f = _completed; _completed = null; return f; }
    internal void InjectState(CasingState state)
    {
        if (_completed != null || _queued != null) throw new Exception("Fixture frame must be drained before injection");
        _completed = new ExternalCasingFrame();_completed.States[0]=state;_completed.StateCount=1;
    }
    internal void InjectContacts(params CasingContact[] contacts)
    {
        if (_completed != null || _queued != null) throw new Exception("Fixture frame must be drained before injection");
        _completed = new ExternalCasingFrame();
        for (int i = 0; i < contacts.Length; i++) _completed.Contacts[i] = contacts[i];
        _completed.ContactCount = contacts.Length;
    }
    public void QueueCasingFrame(ExternalCasingFrame frame)
    {
        if (_queued != null) throw new Exception("Overlapping frame ownership");
        _queued = frame; LastCreates = frame.CreateCount; LastRemoves = frame.RemoveCount;
        LastImpacts = frame.ImpactCount;
        if (LastImpacts > 0) LastImpact = frame.Impacts[0];
        if (LastCreates > 0) LastDescriptor = frame.Creates[0];
    }
    internal void Complete()
    {
        _completed = _queued; _queued = null;
        if (_completed == null) return;
        _completed.StateCount = _completed.CreateCount;
        for (int i = 0; i < _completed.CreateCount; i++)
        {
            var d = _completed.Creates[i];
            _completed.States[i] = new CasingState { Id = d.Id, Pose = d.Pose, Velocity = Vector3.zero, Flags = FailCreation ? 2u : 1u };
        }
    }
    public bool EnsureEnvironment(uint id, Vector3 p, Vector3 e, out int result, out string failure) { EnvironmentAcquires++; result = 0; failure = null; return true; }
    public bool EnsureCasingEnvironment(uint id, Vector3 p, Vector3 e, out string failure) { EnvironmentAcquires++; SyncCasingEnvironments++; failure = FailEnvironment ? "unsupported fixture geometry" : null; return !FailEnvironment; }
    public void ReleaseEnvironment(uint id) { EnvironmentReleases++; }
    public ExternalEnvironmentPrefetchState TryAcquireEnvironment(uint id, Vector3 center, Vector3 sweepEnd, bool casing)
    {
        TryAcquires++;
        if (FailEnvironment) return ExternalEnvironmentPrefetchState.Failed;
        if (AcquireState == ExternalEnvironmentPrefetchState.Ready) EnvironmentAcquires++;
        return AcquireState;
    }
    public void RequestEnvironmentPrefetch(uint id, Vector3 center, Vector3 sweepEnd, float ttlSeconds)
    { AreaRequests++; LastAreaConsumer = id; LastAreaCenter = center; LastAreaSweepEnd = sweepEnd; }
    public ExternalEnvironmentPrefetchState GetEnvironmentPrefetchState(uint id) => AcquireState;
    public double PumpEnvironmentPrefetch(double budgetMilliseconds) => 0d;
    public void PromoteEnvironmentPrefetch(uint id, uint consumerId) { }
    public void CancelEnvironmentPrefetch(uint id) { AreaCancels++; }
}
#endif
