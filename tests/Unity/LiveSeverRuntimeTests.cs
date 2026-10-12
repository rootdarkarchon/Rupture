#if UNITY_EDITOR
// Executes the production living-fall partial in Unity, with fixture EFT actors and
// a local Unity physics scene standing in for the separately tested native backend.
using System;
using System.Collections.Generic;
using System.IO;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Health;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;
using UnityEngine;
using UnityEngine.SceneManagement;

[UnityEditor.InitializeOnLoad]
public static class LiveSeverRuntimeTests
{
    static LiveSeverRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool("LiveSeverTests", false)) return;
            UnityEditor.SessionState.SetBool("LiveSeverTests", false);
            try { Run(); UnityEditor.EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); }
        };
    }
    internal static int Checks;
    internal static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Checks++; Debug.Log("PASS: " + label); }
    public static void RunBatch()
    {
        UnityEditor.SessionState.SetBool("LiveSeverTests", true); UnityEditor.EditorApplication.EnterPlaymode();
    }
    private static void Run()
    {
        ShadowRagdollWorld.VerifyLivingLifecycle();
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json"),
            "{\"passed\":true,\"checks\":" + Checks + "}");
    }
    internal static Player Actor()
    {
        var root = new GameObject("fixture bot"); var player = root.AddComponent<Player>();
        player.Transform = new TransformRef { Original = root.transform };
        player.PlayerBody = root.AddComponent<PlayerBody>();
        var skeleton = new GameObject("skeleton"); skeleton.transform.SetParent(root.transform, false);
        player.PlayerBody.SkeletonRootJoint = skeleton;
        var pool = root.AddComponent<PlayerPoolObject>();
        var spawners = new List<RigidbodySpawner>();
        var hitColliders = new List<BodyPartCollider>();
        var pelvis = new GameObject("pelvis"); pelvis.transform.SetParent(skeleton.transform, false);
        pelvis.transform.localPosition = new Vector3(0, 2, 0);
        pelvis.AddComponent<BoxCollider>().size = Vector3.one * .2f;
        var pelvisSpawner = pelvis.AddComponent<RigidbodySpawner>(); spawners.Add(pelvisSpawner);
        var joints = new List<CharacterJointSpawner>();
        for (int side = 0; side < 2; side++)
        {
            RigidbodySpawner parent = pelvisSpawner;
            foreach (string name in new[] { "thigh", "calf", "foot" })
            {
                var bone = new GameObject((side == 0 ? "l" : "r") + name); bone.transform.SetParent(parent.transform, false);
                bone.transform.localPosition = new Vector3(name == "thigh" ? (side == 0 ? -.3f : .3f) : 0, -.4f, 0);
                bone.AddComponent<BoxCollider>().size = Vector3.one * .2f;
                var spawner = bone.AddComponent<RigidbodySpawner>(); spawners.Add(spawner);
                if (name != "foot") hitColliders.Add(new BodyPartCollider { Collider = bone.GetComponent<Collider>(),
                    BodyPartColliderType = name == "thigh" ? (side == 0 ? EBodyPartColliderType.LeftThigh : EBodyPartColliderType.RightThigh) :
                        (side == 0 ? EBodyPartColliderType.LeftCalf : EBodyPartColliderType.RightCalf) });
                var joint = bone.AddComponent<CharacterJointSpawner>(); joint.Connected = parent; joints.Add(joint);
                if (name == "thigh")
                {
                    var reference = new TransformRef { Original = bone.transform };
                    if (side == 0) player.PlayerBones.LeftThigh1 = reference; else player.PlayerBones.RightThigh1 = reference;
                }
                parent = spawner;
            }
        }
        pool.RigidbodySpawners = spawners.ToArray(); pool.JointSpawners = joints.ToArray();
        player.PlayerBones.Pelvis = new TransformRef { Original = spawners[0].transform };
        player.PlayerBones.BodyPartColliders = hitColliders.ToArray();
        return player;
    }
}

namespace Rupture.Ragdolls.Classes
{
    internal static partial class ShadowRagdollWorld
    {
        private static IExternalPhysicsRuntime _externalRuntime;
        private const string WeaponPath = "@weapon";
        private static readonly Dictionary<string, PendingColliderDisable> PendingColliderDisables = new Dictionary<string, PendingColliderDisable>();
        private static readonly HashSet<string> FailedColliderDisableProfiles = new HashSet<string>();
        private sealed class DetachedEffect { }
        private static string GetCorpseName(CorpseRagdoll ragdoll) => "fixture corpse";
        private static bool TryBuildWeaponBox(Rigidbody body, IReadOnlyList<Collider> colliders, string profile, out ExternalBoxShape box, out string failure)
            => throw new NotSupportedException("weapon fitting is outside the living/corpse handoff fixture");
        private static uint _externalGroupSequence;
        private static int _localEnvironmentMask;
        private static bool EnsureScene(out string failure) { failure = null; return _externalRuntime != null; }
        private static int GetSupportCount(Rigidbody body) => PhysicsExtensions.UpdateController.Count(body);
        internal static void VerifyLivingLifecycle()
        {
            var runtime = new LivingFixtureRuntime(); _externalRuntime = runtime;
            Player player = LiveSeverRuntimeTests.Actor();
            var pool = player.GetComponent<PlayerPoolObject>();
            Vector3 bind = pool.RigidbodySpawners[0].transform.localPosition;
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = new Vector3(4, -.1f, 3); floor.transform.localScale = new Vector3(10, .2f, 10);
            _localEnvironmentMask = Physics.DefaultRaycastLayers; Physics.SyncTransforms();
            try
            {
                LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _), "living fall admitted without death");
                LiveSeverRuntimeTests.Check(player.HealthController.IsAlive && !player._characterController.isEnabled &&
                    !player.BodyAnimatorCommon.enabled && !((Player.FirearmController)player.HandsController).Pressed,
                    "living fall freezes animation, movement and active fire");
                LiveSeverRuntimeTests.Check(runtime.Count == 7 && runtime.JointCount == 6 && PhysicsExtensions.UpdateController.Total == 0,
                    "only external bodies retain simulation ownership");
                var data = new BotSeverData(); CaptureLivingFall(player, ref data);
                LiveSeverRuntimeTests.Check(data.Phase == 1 && data.Remaining <= 1 && data.BonePaths.Length == 7,
                    "fall snapshots expose bounded time and bone identities");
                for (int i = 0; i < 10; i++) runtime.Step(.02f);
                PublishLivingPhysics(); PresentLivingFalls();
                LiveSeverRuntimeTests.Check(pool.RigidbodySpawners[0].transform.position.y < 1.95f,
                    "completed external physics poses drive the live skeleton");
                LivingRigs[player].RecoveryBody.Position = new Vector3(4, .25f, 3);
                LivingRigs[player].EndsAt = -1;
                UpdateLivingFalls();
                LiveSeverRuntimeTests.Check(runtime.Count == 0 && runtime.Groups.Count == 0 && player.IsInPronePose &&
                    player.BodyAnimatorCommon.enabled && player._characterController.isEnabled,
                    "fall completion releases physics and restores prone animation");
                LiveSeverRuntimeTests.Check(pool.RigidbodySpawners[0].Rigidbody == null &&
                    pool.RigidbodySpawners[0].transform.localPosition == bind,
                    "native ragdoll resources and bind pose are restored once");
                LiveSeverRuntimeTests.Check(Vector3.Distance(player.Transform.Original.position, new Vector3(4, 0, 3)) < .01f && player.Teleported,
                    "recovery uses final pelvis ground location through native teleport without a navmesh");
                LiveSeverRuntimeTests.Check(Vector3.Distance(ResolveLivingRecoveryRoot(new Vector3(30, .3f, 30), Vector3.zero),
                    new Vector3(30, .15f, 30)) < .01f,
                    "missing ground and navigation retain the settled location rather than the shot position");
                LivingRigs[player].BlendEndsAt = -1; UpdateLivingFalls();
                LiveSeverRuntimeTests.Check(!LivingFallActive(player), "blend completion removes the temporary rig");
                player.MovementContext.IsInPronePose = false;
                BotSeverStateService.IsRestricted = false; runtime.FailAt = 2;
                LiveSeverRuntimeTests.Check(!BeginLivingFall(player, out _) && runtime.Count == 0 && runtime.Groups.Count == 0 &&
                    player.BodyAnimatorCommon.enabled && player._characterController.isEnabled && !player.IsInPronePose,
                    "partial body admission rolls back native and external resources");
                runtime.FailAt = 0; BotSeverStateService.IsRestricted = true;
                LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _), "repeat fall can reacquire cleaned native resources");
                player.HealthController.IsAlive = false; UpdateLivingFalls();
                LiveSeverRuntimeTests.Check(!LivingFallActive(player) && runtime.Count == 0 && PhysicsExtensions.UpdateController.Total == 0,
                    "death during a fall retires all physics resources");
                player.HealthController.IsAlive = true;
                data.Phase = 1; data.Remaining = .7f;
                ReceiveLivingFall(player, data); PresentLivingFalls();
                LiveSeverRuntimeTests.Check(LivingFallActive(player) && runtime.Count == 0 && !player.BodyAnimatorCommon.enabled,
                    "remote fall presents authority poses without local simulation");
                data.Phase = 2; data.Remaining = .2f;
                ReceiveLivingFall(player, data);
                LiveSeverRuntimeTests.Check(player.BodyAnimatorCommon.enabled && player.IsInPronePose,
                    "remote blend restores animation while retaining prone");
                data.Phase = 1; ReceiveLivingFall(player, data);
                LiveSeverRuntimeTests.Check(!player.BodyAnimatorCommon.enabled, "remote second fall freezes a fresh animation lifecycle");
                data.Phase = 0; ReceiveLivingFall(player, data);
                LiveSeverRuntimeTests.Check(!LivingFallActive(player) && player.BodyAnimatorCommon.enabled,
                    "remote prone snapshot cancels temporary presentation");
                VerifyLivingTopology(runtime, player);
                VerifyCorpseHandoff(runtime, player);
                LimbDismembermentSystem.VerifyDeathCancellation(player);
                var gib = new GameObject("pending detached leg"); gib.AddComponent<Rigidbody>();
                int completions = 0;
                LiveSeverRuntimeTests.Check(RegisterLivingGib(gib, Vector3.zero, Vector3.right, Array.Empty<ExternalTransientConvexShape>(),
                    (success, failure) => completions++), "detached leg starts asynchronous admission");
                CancelLivingGib(gib); UpdateLivingFalls();
                LiveSeverRuntimeTests.Check(completions == 0 && LivingGibs.Count == 0 && runtime.Groups.Count == 0,
                    "cancelled cuts cannot receive late gib completion");
                gib = new GameObject("detached leg"); gib.transform.position = new Vector3(8, 3, 0); gib.AddComponent<Rigidbody>();
                RegisterLivingGib(gib, gib.transform.position, Vector3.up, Array.Empty<ExternalTransientConvexShape>(),
                    (success, failure) => { if (success) completions++; });
                UpdateLivingFalls();
                LiveSeverRuntimeTests.Check(completions == 1 && runtime.Count == 1 && LivingGibs[0].Body.Velocity.y == 2,
                    "admitted detached leg receives its launch velocity exactly once");
                LiveSeverRuntimeTests.Check(gib.GetComponent<Rigidbody>().isKinematic && !gib.GetComponent<Rigidbody>().useGravity,
                    "Unity gib shell remains parked after the gravity-enabled snapshot");
                for (int i = 0; i < 50; i++) runtime.Step(.02f);
                PublishLivingPhysics();
                LiveSeverRuntimeTests.Check(LivingGibs[0].Body.Velocity.y < -7f && gib.transform.position.y < 2.5f,
                    "upward launched gib falls under captured gravity instead of flying continuously");
                ResetLivingFalls();
                LiveSeverRuntimeTests.Check(runtime.Count == 0 && runtime.Groups.Count == 0 && LivingGibs.Count == 0,
                    "raid reset releases detached legs and environment consumers");
            }
            finally { ResetLivingFalls(); UnityEngine.Object.DestroyImmediate(player.gameObject); UnityEngine.Object.DestroyImmediate(floor); runtime.Dispose(); _externalRuntime = null; }
        }
        private static void VerifyLivingTopology(LivingFixtureRuntime runtime, Player player)
        {
            var pool = player.GetComponent<PlayerPoolObject>();
            LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _), "intact jointed skeleton starts for the impulse control");
            var intact = LivingRigs[player];
            intact.Bodies.Find(b => b.Bone.name == "lfoot").External.AddForce(Vector3.right * 100, ForceMode.Impulse);
            for (int i = 0; i < 10; i++) runtime.Step(.02f);
            LiveSeverRuntimeTests.Check(intact.RecoveryBody.External.Velocity.x > 1f,
                "fixture joints transmit an intact foot impulse to the pelvis");
            CancelLivingFall(player);
            foreach (EBodyPart part in new[] { EBodyPart.LeftLeg, EBodyPart.RightLeg })
                foreach (LimbSeverLevel level in new[] { LimbSeverLevel.Proximal, LimbSeverLevel.Distal })
                {
                    BotSeverStateService.Clear(player);
                    LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _), "jointed fall starts before cut commit: " + part + " " + level);
                    var rig = LivingRigs[player];
                    IExternalPhysicsBody removed = rig.Bodies.Find(b => b.Bone.name == (part == EBodyPart.LeftLeg ? "lfoot" : "rfoot")).External;
                    int kept = level == LimbSeverLevel.Proximal ? 4 : 5;
                    BotSeverStateService.Commit(player, part, level);
                    SeverLivingLeg(player, part, level);
                    LiveSeverRuntimeTests.Check(runtime.Count == kept && runtime.JointCount == kept - 1 && !removed.IsValid,
                        "cut retires all missing actors and boundary/internal joints: " + part + " " + level);
                    LiveSeverRuntimeTests.Check(runtime.Mass == kept && rig.Bodies.Count == kept &&
                        pool.RigidbodySpawners.Length == 7 && Array.TrueForAll(pool.RigidbodySpawners, b => b.Rigidbody != null && b.Rigidbody.isKinematic) &&
                        PhysicsExtensions.UpdateController.Total == 0,
                        "missing limb has no solver mass while all native shells stay parked: " + part + " " + level);
                    removed.AddForce(Vector3.right * 1000, ForceMode.Impulse);
                    for (int i = 0; i < 5; i++) runtime.Step(.02f);
                    PublishLivingPhysics();
                    LiveSeverRuntimeTests.Check(Mathf.Abs(rig.RecoveryBody.External.Velocity.x) < .01f,
                        "retired leg cannot transmit an impulse to the pelvis: " + part + " " + level);
                    var snapshot = new BotSeverData(); CaptureLivingFall(player, ref snapshot);
                    LiveSeverRuntimeTests.Check(snapshot.BonePaths.Length == kept, "authority snapshot omits severed bodies: " + part + " " + level);
                    CancelLivingFall(player);
                    LiveSeverRuntimeTests.Check(runtime.Count == 0 && runtime.JointCount == 0 && runtime.Groups.Count == 0 &&
                        Array.TrueForAll(pool.RigidbodySpawners, b => b.Rigidbody == null), "cut cleanup preserves reusable skeleton: " + part + " " + level);
                    LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _) && runtime.Count == kept && runtime.JointCount == kept - 1 &&
                        PhysicsExtensions.UpdateController.Total == 0 && Array.TrueForAll(pool.RigidbodySpawners, b => b.Rigidbody.isKinematic),
                        "later falls never recreate missing external segments: " + part + " " + level);
                    EBodyPart other = part == EBodyPart.LeftLeg ? EBodyPart.RightLeg : EBodyPart.LeftLeg;
                    BotSeverStateService.Commit(player, other, LimbSeverLevel.Proximal);
                    SeverLivingLeg(player, other, LimbSeverLevel.Proximal);
                    LiveSeverRuntimeTests.Check(runtime.Count == kept - 3 && runtime.JointCount == kept - 4,
                        "second cut prunes an existing fall before another solver step: " + part + " " + level);
                    SeverLivingLeg(player, other, LimbSeverLevel.Proximal);
                    LiveSeverRuntimeTests.Check(runtime.Count == kept - 3 && runtime.JointCount == kept - 4,
                        "duplicate cut retirement is idempotent: " + part + " " + level);
                    CancelLivingFall(player);
                }
            BotSeverStateService.Clear(player);
            LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _), "full topology can be recreated for an unsevered actor");
            var data = new BotSeverData(); CaptureLivingFall(player, ref data); CancelLivingFall(player);
            ReceiveLivingFall(player, data);
            LiveSeverRuntimeTests.Check(LivingRigs[player].Bodies.Count == 7 && runtime.Count == 0, "remote jointed skeleton has presentation only");
            data.BonePaths = new[] { "pelvis", "pelvis/rthigh", "pelvis/rthigh/rcalf", "pelvis/rthigh/rcalf/rfoot" };
            data.Positions = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            data.Rotations = new[] { Quaternion.identity, Quaternion.identity, Quaternion.identity, Quaternion.identity };
            ReceiveLivingFall(player, data);
            var retiredBone = player.PlayerBones.LeftThigh1.Original;
            LiveSeverRuntimeTests.Check(LivingRigs[player].Bodies.Count == 4 && !LivingRigs[player].Bodies.Exists(b => b.Bone == retiredBone),
                "remote snapshots remove previously cached missing leg poses");
            PresentLivingFalls(); retiredBone.position = new Vector3(40, 40, 40); PresentLivingFalls();
            LiveSeverRuntimeTests.Check(retiredBone.position == new Vector3(40, 40, 40), "remote presentation no longer writes retired bone poses");
            data.BonePaths = new[] { "pelvis" }; data.Positions = new[] { Vector3.up }; data.Rotations = new[] { Quaternion.identity };
            ReceiveLivingFall(player, data);
            LiveSeverRuntimeTests.Check(LivingRigs[player].Bodies.Count == 1, "remote second cut reconciles to the latest authority topology");
            CancelLivingFall(player);
            runtime.FailJointAt = 3;
            LiveSeverRuntimeTests.Check(!BeginLivingFall(player, out _) && runtime.Count == 0 && runtime.JointCount == 0 &&
                PhysicsExtensions.UpdateController.Total == 0 && player.BodyAnimatorCommon.enabled && player._characterController.isEnabled,
                "partial joint admission retires constraints before actors and restores animation");
            runtime.FailJointAt = 0;
        }
        private static void VerifyCorpseHandoff(LivingFixtureRuntime runtime, Player player)
        {
            var pool = player.GetComponent<PlayerPoolObject>();
            var corpse = player.gameObject.AddComponent<Corpse>(); corpse.PlayerProfileID = player.ProfileId;
            foreach (var injury in new[] { (EBodyPart.LeftLeg, LimbSeverLevel.Proximal, false, false),
                (EBodyPart.LeftLeg, LimbSeverLevel.Distal, false, false), (EBodyPart.RightLeg, LimbSeverLevel.Proximal, false, false),
                (EBodyPart.RightLeg, LimbSeverLevel.Distal, false, false), (EBodyPart.LeftLeg, LimbSeverLevel.Proximal, true, false),
                (EBodyPart.RightLeg, LimbSeverLevel.Distal, false, true) })
                {
                    EBodyPart part = injury.Item1; LimbSeverLevel level = injury.Item2; bool both = injury.Item3;
                    EBodyPart other = part == EBodyPart.LeftLeg ? EBodyPart.RightLeg : EBodyPart.LeftLeg;
                    foreach (var spawner in pool.RigidbodySpawners) foreach (Collider collider in spawner.GetComponents<Collider>()) collider.enabled = true;
                    player.HealthController.IsAlive = true; BotSeverStateService.Clear(player);
                    LiveSeverRuntimeTests.Check(BeginLivingFall(player, out _), "live-to-corpse fixture starts: " + part + " " + level);
                    BotSeverStateService.Commit(player, part, level); SeverLivingLeg(player, part, level);
                    foreach (Collider collider in RagdollCoordinator.GetLivingLimbColliders(player, part, level)) collider.enabled = false;
                    if (both)
                    {
                        BotSeverStateService.Commit(player, other, LimbSeverLevel.Distal); SeverLivingLeg(player, other, LimbSeverLevel.Distal);
                        foreach (Collider collider in RagdollCoordinator.GetLivingLimbColliders(player, other, LimbSeverLevel.Distal)) collider.enabled = false;
                    }
                    Rigidbody preservedPelvis = null;
                    Vector3 deathPelvis = Vector3.zero;
                    if (injury.Item4)
                    {
                        preservedPelvis = pool.RigidbodySpawners[0].Rigidbody;
                        var falling = LivingRigs[player];
                        foreach (var body in falling.Bodies) body.Position += Vector3.right * 2f;
                        deathPelvis = falling.RecoveryBody.Position;
                        player.HealthController.IsAlive = false; CancelLivingFall(player, forDeath: true);
                        LiveSeverRuntimeTests.Check(runtime.Count == 0 && !LivingFallActive(player) && preservedPelvis == pool.RigidbodySpawners[0].Rigidbody &&
                            Array.TrueForAll(pool.RigidbodySpawners, b => b.Rigidbody != null && b.Rigidbody.isKinematic) && PhysicsExtensions.UpdateController.Total == 0,
                            "death during a fall releases external ownership while preserving native shells for synchronous corpse reuse");
                        LiveSeverRuntimeTests.Check(Vector3.Distance(preservedPelvis.position, deathPelvis) < .001f,
                            "death handoff preserves the final living physics pose instead of restoring the animated bind pose");
                    }
                    else { CancelLivingFall(player); player.HealthController.IsAlive = false; }
                    PreserveLivingSeverForCorpse(player, part, level);
                    if (both) PreserveLivingSeverForCorpse(player, other, LimbSeverLevel.Distal);
                    var native = new CorpseRagdoll(pool.RigidbodySpawners, pool.JointSpawners, null, Vector3.zero, 10,
                        CollisionDetectionMode.Discrete, corpse, (s, t) => false, player.PlayerBody, true, () => { }, false, false);
                    if (injury.Item4) LiveSeverRuntimeTests.Check(preservedPelvis == pool.RigidbodySpawners[0].Rigidbody && !preservedPelvis.isKinematic,
                        "native corpse startup reuses the existing living shell without a deferred destruction race");
                    var rig = new ShadowRig { Corpse = corpse, NativeRagdoll = native, ExternalGroup = ++_externalGroupSequence,
                        CoreBodyCount = pool.RigidbodySpawners.Length };
                    int retained = (level == LimbSeverLevel.Proximal ? 4 : 5) - (both ? 2 : 0);
                    try
                    {
                        var disabled = RagdollCoordinator.GetLivingLimbColliders(player, part, level);
                        LiveSeverRuntimeTests.Check(disabled.Length > 0 && !disabled[0].enabled &&
                            GetAttachedBodyColliders(disabled[0].GetComponentInParent<Rigidbody>(true)).Contains(disabled[0]),
                            "disabled live hit colliders retain structural body ownership at death: " + part + " " + level);
                        foreach (var spawner in pool.RigidbodySpawners)
                        {
                            if (!TryCreateBody(rig, spawner.Rigidbody, BodyHierarchyPath.RelativeTo(player.PlayerBody.SkeletonRootJoint.transform, spawner.transform), true, out string failure))
                                throw new Exception("corpse body admission: " + failure);
                            rig.CoreBodies.Add(rig.BodiesByNative[spawner.Rigidbody]);
                        }
                        LiveSeverRuntimeTests.Check(rig.CoreBodies.Count == 7 && rig.PermanentlyDisabledBodies.Count == 7 - retained,
                            "corpse preserves all pose indices without requiring missing-limb colliders: " + part + " " + level);
                        LiveSeverRuntimeTests.Check(TryCloneJoints(rig, out _) && runtime.JointCount == retained - 1,
                            "corpse never attaches constraints to missing anatomy: " + part + " " + level);
                        ApplyPendingColliderDisable(rig);
                        bool inert = true;
                        foreach (var body in rig.PermanentlyDisabledBodies)
                            inert &= body.Proxy.isKinematic && runtime.ShapeCount(((ExternalShadowBodyProxy)body.Proxy).ExternalBody) == 0;
                        LiveSeverRuntimeTests.Check(inert && runtime.DynamicCount == retained && !PendingColliderDisables.ContainsKey(player.ProfileId),
                            "corpse missing segments have no shapes or dynamic mass, including the unlisted foot: " + part + " " + level);
                        foreach (var body in rig.Bodies)
                        {
                            PhysicsExtensions.UpdateController.UnsupportRigidbody(body.Native); body.Native.isKinematic = true;
                        }
                        for (int i = 0; i < 10; i++) runtime.Step(.02f);
                        LiveSeverRuntimeTests.Check(PhysicsExtensions.UpdateController.Total == 0 && rig.CenterBody.Proxy.position.y < player.Transform.Original.position.y + 1.95f,
                            "retained corpse simulates with sole external ownership after a living cut: " + part + " " + level);
                        if (!both)
                        {
                            ApplyColliderDisable(rig, CaptureColliderDisable(RagdollCoordinator.GetLivingLimbColliders(player, other, LimbSeverLevel.Distal)));
                            LiveSeverRuntimeTests.Check(runtime.DynamicCount < retained && runtime.JointCount < retained - 1 && rig.CoreBodies.Count == 7,
                                "postmortem cut can still park intact segments and retire their joints: " + part + " " + level);
                        }
                        else LiveSeverRuntimeTests.Check(runtime.DynamicCount == 2 && rig.PermanentlyDisabledBodies.Count == 5,
                            "both committed leg cuts survive death with consistent corpse topology");
                    }
                    finally
                    {
                        foreach (var joint in rig.ExternalJoints) _externalRuntime.DestroyJoint(joint.Joint);
                        foreach (var body in rig.Bodies) _externalRuntime.DestroyBody(((ExternalShadowBodyProxy)body.Proxy).ExternalBody);
                        foreach (var joint in pool.JointSpawners) joint.Remove();
                        foreach (var body in pool.RigidbodySpawners) { PhysicsExtensions.UpdateController.UnsupportRigidbody(body.Rigidbody); body.Remove(); }
                        PendingColliderDisables.Remove(player.ProfileId);
                    }
                }
            BotSeverStateService.Clear(player); player.HealthController.IsAlive = true;
            foreach (var spawner in pool.RigidbodySpawners) foreach (Collider collider in spawner.GetComponents<Collider>()) collider.enabled = true;
            var missing = pool.RigidbodySpawners[3]; missing.Create();
            var intactRig = new ShadowRig { Corpse = corpse };
            Collider intactCollider = missing.GetComponent<Collider>(); UnityEngine.Object.DestroyImmediate(intactCollider);
            LiveSeverRuntimeTests.Check(!TryCreateBody(intactRig, missing.Rigidbody, "intact foot", true, out _),
                "an unknown intact body without colliders still fails admission");
            missing.gameObject.AddComponent<BoxCollider>().size = Vector3.one * .2f; missing.Remove();
        }
    }
}

namespace EFT
{
    public sealed class TransformRef { public Transform Original; }
    public sealed class FixtureHealth { public bool IsAlive = true; }
    public sealed class FixtureEnabled { public bool enabled = true; }
    public sealed class FixtureController { public bool isEnabled = true; }
    public sealed class FixturePom { public void Off() { } public void On() { } }
    public sealed class FixtureMover { public void MovementPause(float duration, bool force) { } public void MovementResume() { } }
    public sealed class FixtureOwner { public FixtureMover Mover = new FixtureMover(); }
    public sealed class FixtureAI { public FixtureOwner BotOwner = new FixtureOwner(); }
    public sealed class FixtureBones { public TransformRef Pelvis, LeftThigh1, RightThigh1; public BodyPartCollider[] BodyPartColliders; public Collider[] ArmorPlateColliders = Array.Empty<Collider>(); }
    public sealed class BodyPartCollider { public Collider Collider; public EBodyPartColliderType BodyPartColliderType; }
    public sealed class FixtureAnimator { public void SetProneStateForce() { } }
    public sealed class FixtureMovement
    { public bool IsInPronePose; public FixtureAnimator PlayerAnimator = new FixtureAnimator(); public void ReleaseDoorIfInteractingWithOne() { } }
    public sealed class Player : MonoBehaviour
    {
        [Flags] public enum EAnimatorMask { Body = 1, Arms = 2 }
        public sealed class FirearmController { public bool Pressed = true; public void SetTriggerPressed(bool pressed) { Pressed = pressed; } }
        public TransformRef Transform;
        public string ProfileId = Guid.NewGuid().ToString("N");
        public PlayerBody PlayerBody;
        public FixtureHealth HealthController = new FixtureHealth();
        public FixtureEnabled BodyAnimatorCommon = new FixtureEnabled(), ArmsAnimatorCommon = new FixtureEnabled();
        public FixtureController _characterController = new FixtureController();
        public FixturePom POM = new FixturePom();
        public FixtureMovement MovementContext = new FixtureMovement();
        public FixtureAI AIData = new FixtureAI();
        public FixtureBones PlayerBones = new FixtureBones();
        public object HandsController = new FirearmController();
        public EAnimatorMask EnabledAnimators = EAnimatorMask.Body | EAnimatorMask.Arms;
        public Vector3 Velocity;
        public bool UsedSimplifiedSkeleton;
        public bool Teleported;
        public void Teleport(Vector3 position, bool onServerToo) { Transform.Original.position = position; Teleported = true; }
        public bool IsInPronePose => MovementContext.IsInPronePose;
    }
    public sealed class PlayerBody : MonoBehaviour { public GameObject SkeletonRootJoint; public bool IsVisible = true; }
    public sealed class EFTHardSettings { public static EFTHardSettings Instance = new EFTHardSettings(); public float CorpseMaxDepenetrationVelocity = 10; }
}
namespace EFT.AssetsManager
{
    public sealed class PlayerPoolObject : MonoBehaviour
    { public RigidbodySpawner[] RigidbodySpawners; public CharacterJointSpawner[] JointSpawners; public object PlayerRigidbodySleepHierarchy; }
    public sealed class RigidbodySpawner : MonoBehaviour
    {
        public Rigidbody Rigidbody;
        public void Create() { if (Rigidbody == null) Rigidbody = gameObject.AddComponent<Rigidbody>(); }
        public void Remove() { if (Rigidbody != null) UnityEngine.Object.DestroyImmediate(Rigidbody); Rigidbody = null; }
    }
    public sealed class CharacterJointSpawner : MonoBehaviour
    {
        public RigidbodySpawner Connected;
        private CharacterJoint _joint;
        public Joint Create()
        {
            if (_joint == null) { _joint = gameObject.AddComponent<CharacterJoint>(); _joint.connectedBody = Connected.Rigidbody; }
            return _joint;
        }
        public void Remove() { if (_joint != null) UnityEngine.Object.DestroyImmediate(_joint); _joint = null; }
    }
}
namespace EFT.Interactive
{
    public sealed class Corpse : MonoBehaviour { public string PlayerProfileID; }
    public sealed class CorpseRagdoll
    {
        public CharacterJointSpawner[] _jointSpawners;
        public CorpseRagdoll(RigidbodySpawner[] bodies, CharacterJointSpawner[] joints, object hierarchy,
            Vector3 velocity, float depenetration, CollisionDetectionMode mode, MonoBehaviour owner,
            Func<bool, float, bool> sleep, PlayerBody body, bool visible, Action done, bool keep, bool putToSleep)
        { _jointSpawners = joints; foreach (var spawner in bodies) { spawner.Create(); spawner.Rigidbody.isKinematic = false; PhysicsExtensions.UpdateController.Support(spawner.Rigidbody); } foreach (var joint in joints) joint.Create(); }
    }
}
public static class PhysicsExtensions
{
    public static class UpdateController
    {
        private static readonly Dictionary<Rigidbody, int> Bodies = new Dictionary<Rigidbody, int>();
        public static int Total => Bodies.Count;
        public static int Count(Rigidbody body) => Bodies.TryGetValue(body, out int count) ? count : 0;
        public static void Support(Rigidbody body) => Bodies[body] = Count(body) + 1;
        public static void UnsupportRigidbody(Rigidbody body) { if (Count(body) <= 1) Bodies.Remove(body); else Bodies[body]--; }
    }
}
namespace Rupture.Health
{
    internal static partial class BotSeverStateService
    {
        internal sealed class Actor { internal readonly BotSeverPolicy Policy = new BotSeverPolicy(); internal readonly LimbSeverLevel[] Levels = new LimbSeverLevel[2]; }
        private static readonly Dictionary<Player, Actor> Actors = new Dictionary<Player, Actor>();
        internal static bool IsRestricted = true;
        internal static bool Restricted(Player player) => IsRestricted;
        internal static void EnforceProne(Player player) => player.MovementContext.IsInPronePose = true;
        internal static Actor Get(Player player) => Actors.TryGetValue(player, out Actor actor) ? actor : null;
        internal static EBodyPart Part(int leg) => leg == 0 ? EBodyPart.LeftLeg : EBodyPart.RightLeg;
        internal static void Clear(Player player) => Actors.Remove(player);
        internal static void Commit(Player player, EBodyPart part, LimbSeverLevel level)
        {
            if (!Actors.TryGetValue(player, out Actor actor)) Actors[player] = actor = new Actor();
            int leg = part == EBodyPart.LeftLeg ? 0 : 1; actor.Policy.Sever(leg); actor.Levels[leg] = level;
        }
        internal static void Publish(object actor, bool reliable) { }
    }
}
public enum EBodyPart { LeftArm, RightArm, LeftLeg, RightLeg }
public enum EBodyPartColliderType { LeftForearm, LeftUpperArm, RightForearm, RightUpperArm, LeftCalf, LeftThigh, RightCalf, RightThigh }
namespace Rupture.Dismemberment.Classes.Limbs
{
    internal static partial class LimbDismembermentSystem
    {
        private enum SeverCompletionStatus { Committed, Failed, Cancelled }
        private readonly struct SeverCompletion
        { internal SeverCompletion(SeverCompletionStatus status, LimbSeverLevel level, string failure) { } }
        private sealed class Work { internal bool Cancelled, Completed; }
        private sealed class PendingSever
        {
            internal bool Living, Committed;
            internal PlayerTarget Target;
            internal GameObject GibRoot;
            internal Work GeometryJob, ColliderBakeJob;
            internal List<object> Prepared;
            internal string ReservationKey;
            internal LimbSeverLevel Level;
            internal Action<SeverCompletion> Completion;
        }
        private sealed class PlayerTarget { internal Player Player; }
        private sealed class DeferredGibCleanup { internal GameObject Root; internal Work Job; }
        private static PendingSever _active;
        private static readonly Queue<PendingSever> Pending = new Queue<PendingSever>();
        private static readonly HashSet<string> Reservations = new HashSet<string>();
        private static readonly List<DeferredGibCleanup> DeferredGibCleanups = new List<DeferredGibCleanup>();
        private static void DestroyPrepared(List<object> prepared) => throw new NotSupportedException("prepared geometry is outside cancellation fixture");
        internal static void VerifyDeathCancellation(Player player)
        {
            Player other = LiveSeverRuntimeTests.Actor();
            int cancelled = 0;
            var gib = new GameObject("uncommitted living gib"); gib.AddComponent<Rigidbody>();
            ShadowRagdollWorld.RegisterLivingGib(gib, Vector3.zero, Vector3.up, Array.Empty<ExternalTransientConvexShape>(), (s, f) => throw new Exception("late completion after death"));
            var active = new PendingSever { Living = true, Target = new PlayerTarget { Player = player }, GibRoot = gib,
                ReservationKey = "dead-live-active", Completion = _ => cancelled++ };
            var queued = new PendingSever { Living = true, Target = new PlayerTarget { Player = player }, GeometryJob = new Work(),
                ReservationKey = "dead-live-queued", Completion = _ => cancelled++ };
            var otherFirst = new PendingSever { Living = true, Target = new PlayerTarget { Player = other }, ReservationKey = "other-first" };
            var corpse = new PendingSever { Living = false, Target = new PlayerTarget { Player = player }, ReservationKey = "corpse" };
            var otherLast = new PendingSever { Living = true, Target = new PlayerTarget { Player = other }, ReservationKey = "other-last" };
            _active = active; Pending.Enqueue(otherFirst); Pending.Enqueue(queued); Pending.Enqueue(corpse); Pending.Enqueue(otherLast);
            foreach (var item in new[] { active, queued, otherFirst, corpse, otherLast }) Reservations.Add(item.ReservationKey);
            try
            {
                CancelLivingCuts(player);
                LiveSeverRuntimeTests.Check(_active == null && cancelled == 2 && queued.GeometryJob.Cancelled &&
                    !Reservations.Contains(active.ReservationKey) && !Reservations.Contains(queued.ReservationKey),
                    "death immediately cancels active and queued live cuts and releases their reservations");
                LiveSeverRuntimeTests.Check(Pending.Count == 3 && Pending.Dequeue() == otherFirst && Pending.Dequeue() == corpse && Pending.Dequeue() == otherLast,
                    "death cancellation preserves other actors and corpse cut queue order");
                CancelLivingCuts(player);
                LiveSeverRuntimeTests.Check(cancelled == 2, "repeated death cancellation cannot complete a live cut twice");
            }
            finally { _active = null; Pending.Clear(); Reservations.Clear(); UnityEngine.Object.DestroyImmediate(other.gameObject); }
        }
        internal static bool TryGetLimbSegment(Player player, EBodyPart part, LimbSeverLevel level, out Transform start, out Transform end)
        {
            Transform thigh = (part == EBodyPart.LeftLeg ? player.PlayerBones.LeftThigh1 : player.PlayerBones.RightThigh1).Original;
            Transform calf = thigh.GetChild(0); start = level == LimbSeverLevel.Proximal ? thigh : calf;
            end = level == LimbSeverLevel.Proximal ? calf : calf.GetChild(0); return true;
        }
    }
}
namespace Rupture
{
    internal static class RupturePlugin { internal static FixtureLog Log = new FixtureLog(); }
    internal sealed class FixtureLog { internal void LogWarning(string value) => Debug.LogWarning(value); internal void LogError(string value) => Debug.LogError(value); }
}

internal sealed class LivingFixtureRuntime : IExternalPhysicsRuntime
{
    private readonly Scene _scene = SceneManager.CreateScene("living external fixture", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
    private readonly List<FixtureBody> _bodies = new List<FixtureBody>();
    private readonly List<FixtureJoint> _joints = new List<FixtureJoint>();
    internal readonly HashSet<uint> Groups = new HashSet<uint>();
    internal int FailAt;
    internal int FailJointAt;
    internal int Count => _bodies.Count;
    internal int JointCount => _joints.Count;
    internal int DynamicCount => _bodies.FindAll(body => !body.Native.isKinematic).Count;
    internal int ShapeCount(IExternalPhysicsBody body) => ((FixtureBody)body).Native.GetComponents<Collider>().Length;
    internal float Mass { get { float mass = 0; foreach (var body in _bodies) mass += body.Native.mass; return mass; } }
    private int _attempt;
    private int _jointAttempt;
    internal void Step(float delta) => _scene.GetPhysicsScene().Simulate(delta);
    private FixtureBody Create(Rigidbody native, int shapes = 1, bool kinematic = false)
    {
        var obj = new GameObject("external fixture body"); SceneManager.MoveGameObjectToScene(obj, _scene);
        obj.transform.SetPositionAndRotation(native.position, native.rotation);
        if (shapes > 0) obj.AddComponent<BoxCollider>().size = Vector3.one * .2f;
        var body = new FixtureBody(obj.AddComponent<Rigidbody>()); body.Native.isKinematic = kinematic;
        _bodies.Add(body); return body;
    }
    private sealed class FixtureBody : IExternalPhysicsBody
    {
        internal readonly Rigidbody Native;
        internal FixtureBody(Rigidbody native) { Native = native; }
        public ulong Handle => (ulong)Native.GetInstanceID();
        public Vector3 Position { get => Native.position; set => Native.position = value; }
        public Quaternion Rotation { get => Native.rotation; set => Native.rotation = value; }
        public Vector3 Velocity { get => Native.velocity; set { if (!Native.isKinematic) Native.velocity = value; } }
        public Vector3 AngularVelocity { get => Native.angularVelocity; set { if (!Native.isKinematic) Native.angularVelocity = value; } }
        public Vector3 CenterOfMass => Native.centerOfMass;
        public Vector3 WorldCenterOfMass => Native.worldCenterOfMass;
        public bool IsKinematic { get => Native.isKinematic; set => Native.isKinematic = value; }
        public bool CollisionEnabled { get => Native.detectCollisions; set => Native.detectCollisions = value; }
        public bool IsSleeping => Native.IsSleeping();
        public bool IsValid => Native != null;
        public void WakeUp() => Native.WakeUp();
        public void AddForce(Vector3 force, ForceMode mode) { if (IsValid) Native.AddForce(force, mode); }
        public void AddForceAtPosition(Vector3 force, Vector3 point, ForceMode mode) => Native.AddForceAtPosition(force, point, mode);
        public void AddTorque(Vector3 torque, ForceMode mode) => Native.AddTorque(torque, mode);
    }
    private sealed class FixtureJoint : IExternalPhysicsJoint
    {
        internal readonly FixtureBody Body, Connected;
        internal readonly CharacterJoint Native;
        internal FixtureJoint(FixtureBody body, FixtureBody connected, CharacterJoint native)
        { Body = body; Connected = connected; Native = native; }
        public ulong Handle => (ulong)Native.GetInstanceID();
        public bool IsValid => Native != null;
        public bool IsBroken => !IsValid;
    }
    private sealed class Creation : IExternalBodyCreation
    {
        private readonly LivingFixtureRuntime _runtime; private readonly Rigidbody _native; private readonly bool _gravity; private bool _cancelled;
        internal Creation(LivingFixtureRuntime runtime, Rigidbody native)
        { _runtime = runtime; _native = native; _gravity = native.useGravity; }
        public double WorkerMilliseconds => 0;
        public ExternalBodyCreationStatus Poll(out IExternalPhysicsBody body, out string failure)
        {
            failure = null;
            var created = _cancelled ? null : _runtime.Create(_native);
            if (created != null) created.Native.useGravity = _gravity;
            body = created; return _cancelled ? ExternalBodyCreationStatus.Cancelled : ExternalBodyCreationStatus.Succeeded;
        }
        public void Cancel() => _cancelled = true;
        public void Dispose() { }
    }
    public bool TryCreateBody(Rigidbody native, IReadOnlyList<Collider> colliders, uint group, bool kinematic,
        ExternalCollisionProfile profile, out IExternalPhysicsBody body, out string failure)
    {
        _attempt++; failure = null;
        if (FailAt != 0 && _attempt == FailAt) { _attempt = 0; body = null; failure = "fixture admission failure"; return false; }
        body = Create(native, colliders.Count, kinematic); return true;
    }
    public bool TryCreateBody(Rigidbody n, IReadOnlyList<Collider> c, uint g, bool k, out IExternalPhysicsBody b, out string f)
        => TryCreateBody(n, c, g, k, ExternalCollisionProfile.EnvironmentOnly, out b, out f);
    public bool EnsureEnvironment(uint group, Vector3 center, Vector3 end, out ExternalEnvironmentAcquireResult result, out string failure)
    { Groups.Add(group); _attempt = 0; result = default; failure = null; return true; }
    public void ReleaseEnvironment(uint group) { Groups.Remove(group); _jointAttempt = 0; }
    public IExternalBodyCreation BeginCreateTransientConvexBody(Rigidbody native, IReadOnlyList<ExternalTransientConvexShape> shapes,
        uint group, bool kinematic, ExternalCollisionProfile profile, out string failure)
    { failure = null; return new Creation(this, native); }
    public void DestroyBody(IExternalPhysicsBody body)
    {
        var fixture = (FixtureBody)body;
        if (_joints.Exists(j => j.Body == fixture || j.Connected == fixture)) throw new Exception("actor retired before its constraints");
        _bodies.Remove(fixture); UnityEngine.Object.DestroyImmediate(fixture.Native.gameObject);
    }
    public void Dispose()
    {
        foreach (var j in _joints.ToArray()) DestroyJoint(j);
        foreach (var b in _bodies.ToArray()) DestroyBody(b);
        SceneManager.UnloadSceneAsync(_scene);
    }
    public bool IsInitialized => true;
    public bool IsRaidReady => true;
    public bool NeedsSimulation => Count != 0;
    public bool IsFullPrebake => false;
    public string Failure => null;
    public string Status => "fixture";
    public IReadOnlyList<Bounds> RecentlyMovedBounds => Array.Empty<Bounds>();
    public void Initialize(ExternalBackendOptions options) { }
    public void BeginRaid() { }
    public void NotifySceneLoaded(Scene scene) { }
    public void CaptureEnvironmentChanges() { }
    public bool EnsureCasingEnvironment(uint g, Vector3 c, Vector3 e, out string f) { f = null; return true; }
    public bool TryCreateRoundedBoxBody(Rigidbody n, IReadOnlyList<Collider> c, ExternalRoundedBoxShape s, uint g, bool k, out IExternalPhysicsBody b, out string f)
        => TryCreateBody(n, c, g, k, out b, out f);
    public bool TryCreateBoxBody(Rigidbody n, IReadOnlyList<Collider> c, ExternalBoxShape s, uint g, bool k, out IExternalPhysicsBody b, out string f)
        => TryCreateBody(n, c, g, k, out b, out f);
    public bool TryCreateJoint(Joint n, IExternalPhysicsBody b, IExternalPhysicsBody c, out IExternalPhysicsJoint j, out string f)
    {
        _jointAttempt++; f = null;
        if (FailJointAt != 0 && _jointAttempt == FailJointAt) { j = null; f = "fixture joint admission failure"; return false; }
        var body = (FixtureBody)b; var connected = (FixtureBody)c;
        var native = body.Native.gameObject.AddComponent<CharacterJoint>(); native.connectedBody = connected.Native;
        var fixture = new FixtureJoint(body, connected, native); _joints.Add(fixture); j = fixture; return true;
    }
    public void DestroyJoint(IExternalPhysicsJoint joint)
    { var fixture = (FixtureJoint)joint; _joints.Remove(fixture); UnityEngine.Object.DestroyImmediate(fixture.Native); }
    public void IgnoreCollision(Collider a, Collider b) { }
    public bool TryApplyBodyStates(IReadOnlyList<ExternalBodyState> s, out string f) { f = null; return true; }
    public bool SubmitStep(float delta, out string f) { f = null; Step(delta); return true; }
    public bool CompleteStep(int t, out ExternalStepResult r, out string f) { r = default; f = null; return true; }
    public void ResetRaid() { }
    public void QueueCasingFrame(ExternalCasingFrame frame) { }
    public ExternalCasingFrame TakeCasingFrame() => null;
}
#endif
