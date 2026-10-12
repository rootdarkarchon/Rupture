using System;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Health;
using Rupture.Ragdolls.External;
using UnityEngine;
using UnityEngine.AI;
using static EFT.Player;

namespace Rupture.Ragdolls.Classes;

internal static partial class ShadowRagdollWorld
{
    private sealed class LivingBody
    {
        internal Rigidbody Native;
        internal Transform Bone;
        internal string Path;
        internal IExternalPhysicsBody External;
        internal Vector3 Position, BlendPosition;
        internal Quaternion Rotation, BlendRotation;
    }
    private sealed class LivingRig
    {
        internal Player Player;
        internal CorpseRagdoll NativeRagdoll;
        internal readonly List<LivingBody> Bodies = new List<LivingBody>();
        internal readonly List<LivingJoint> Joints = new List<LivingJoint>();
        internal LivingBody RecoveryBody;
        internal readonly List<GameObject> ActiveArmor = new List<GameObject>();
        internal EAnimatorMask AnimatorMask;
        internal bool BodyEnabled, ArmsEnabled, ControllerEnabled;
        internal bool Remote;
        internal bool AnimationRestored, WasProne;
        internal uint Group;
        internal float EndsAt, BlendEndsAt;
        internal BotFallPhase Phase;
        internal Vector3 OriginalRoot, PelvisOffset;
        internal Quaternion OriginalRotation;
        internal Vector3[] BindPositions;
        internal Quaternion[] BindRotations;
        internal PlayerPoolObject Pool;
    }
    private sealed class LivingJoint
    {
        internal LivingBody Body, Connected;
        internal IExternalPhysicsJoint External;
    }
    private sealed class LivingGib
    {
        internal GameObject Root;
        internal IExternalBodyCreation Creation;
        internal IExternalPhysicsBody Body;
        internal uint Group;
        internal float ExpiresAt;
        internal Vector3 Velocity;
        internal Action<bool, string> Completion;
    }
    private static readonly Dictionary<Player, LivingRig> LivingRigs = new Dictionary<Player, LivingRig>();
    private static readonly List<Player> LivingRemoved = new List<Player>();
    private static readonly List<LivingGib> LivingGibs = new List<LivingGib>();
    internal static bool LivingFallActive(Player player) => player != null && LivingRigs.ContainsKey(player);
    internal static void PreserveLivingSeverForCorpse(Player player, EBodyPart part, LimbSeverLevel level)
    {
        if (player == null || string.IsNullOrEmpty(player.ProfileId) ||
            !LimbDismembermentSystem.TryGetLimbSegment(player, part, level, out Transform root, out _) || root == null) return;
        if (!PendingColliderDisables.TryGetValue(player.ProfileId, out PendingColliderDisable pending))
            PendingColliderDisables[player.ProfileId] = pending = new PendingColliderDisable();
        MergeColliderDisable(pending, RagdollCoordinator.GetLivingLimbColliders(player, part, level));
        if (!pending.RemovedRoots.Contains(root)) pending.RemovedRoots.Add(root);
    }
    internal static void SeverLivingLeg(Player player, EBodyPart part, LimbSeverLevel level)
    {
        if (player == null || !LivingRigs.TryGetValue(player, out LivingRig rig) ||
            !LimbDismembermentSystem.TryGetLimbSegment(player, part, level, out Transform root, out _) || root == null) return;
        bool Removed(LivingBody body) => body != rig.RecoveryBody && body.Bone != null &&
            (body.Bone == root || body.Bone.IsChildOf(root));
        // Retire boundary and internal constraints before their actors. Native bones
        // remain parked for skinning and for EFT's later corpse construction.
        for (int i = rig.Joints.Count - 1; i >= 0; i--)
            if (Removed(rig.Joints[i].Body) || Removed(rig.Joints[i].Connected))
            { _externalRuntime.DestroyJoint(rig.Joints[i].External); rig.Joints.RemoveAt(i); }
        for (int i = rig.Bodies.Count - 1; i >= 0; i--)
            if (Removed(rig.Bodies[i]))
            {
                if (rig.Bodies[i].External != null) _externalRuntime.DestroyBody(rig.Bodies[i].External);
                rig.Bodies.RemoveAt(i);
            }
    }
    internal static bool BeginLivingFall(Player player, out string failure)
    {
        failure = null;
        if (LivingRigs.ContainsKey(player)) return true;
        if (!EnsureScene(out failure)) return false;
        if (player?.HealthController?.IsAlive != true || player.UsedSimplifiedSkeleton)
        { failure = "a living humanoid skeleton is required"; return false; }
        LivingRig rig = null;
        try
        {
            rig = FreezeLiving(player, false);
            if (rig.Pool.RigidbodySpawners.Length > 32) throw new InvalidOperationException("living skeleton exceeds 32 bodies");
            rig.Group = ++_externalGroupSequence;
            rig.NativeRagdoll = new CorpseRagdoll(rig.Pool.RigidbodySpawners, rig.Pool.JointSpawners,
                rig.Pool.PlayerRigidbodySleepHierarchy, player.Velocity, EFTHardSettings.Instance.CorpseMaxDepenetrationVelocity,
                CollisionDetectionMode.Discrete, player, (sleeping, elapsed) => false, player.PlayerBody,
                player.PlayerBody.IsVisible, () => { }, false, false);
            Transform skeleton = player.PlayerBody.SkeletonRootJoint.transform;
            var byNative = new Dictionary<Rigidbody, LivingBody>();
            foreach (RigidbodySpawner spawner in rig.Pool.RigidbodySpawners)
            {
                Rigidbody native = spawner.Rigidbody;
                if (BotSeverStateService.RemovedLivingBone(player, native.transform)) continue;
                var colliders = new List<Collider>();
                foreach (Collider collider in native.GetComponentsInChildren<Collider>(true))
                    if (collider.attachedRigidbody == native && !collider.isTrigger) colliders.Add(collider);
                if (!_externalRuntime.TryCreateBody(native, colliders, rig.Group, false, ExternalCollisionProfile.EnvironmentOnly,
                    out IExternalPhysicsBody external, out failure)) throw new InvalidOperationException(failure);
                var body = new LivingBody { Native = native, Bone = native.transform, External = external,
                    Path = BodyHierarchyPath.RelativeTo(skeleton, native.transform), Position = native.position, Rotation = native.rotation };
                external.Velocity = player.Velocity;
                rig.Bodies.Add(body); byNative[native] = body;
                if (body.Bone == player.PlayerBones.Pelvis.Original) rig.RecoveryBody = body;
                if (body.Path == null) throw new InvalidOperationException("living body is outside the replicated skeleton");
            }
            foreach (CharacterJointSpawner spawner in rig.Pool.JointSpawners)
            {
                Joint joint = spawner.Create();
                Rigidbody native = joint.GetComponent<Rigidbody>();
                if (joint.connectedBody != null && byNative.TryGetValue(native, out LivingBody body) &&
                    byNative.TryGetValue(joint.connectedBody, out LivingBody connected))
                {
                    if (!_externalRuntime.TryCreateJoint(joint, body.External, connected.External, out IExternalPhysicsJoint external, out failure))
                        throw new InvalidOperationException(failure);
                    rig.Joints.Add(new LivingJoint { Body = body, Connected = connected, External = external });
                }
            }
            if (rig.Bodies.Count == 0 || !_externalRuntime.EnsureEnvironment(rig.Group, player.Transform.Original.position,
                player.Transform.Original.position + player.Velocity, out _, out failure)) throw new InvalidOperationException(failure ?? "empty living rig");
            foreach (RigidbodySpawner spawner in rig.Pool.RigidbodySpawners)
            {
                Rigidbody native = spawner.Rigidbody;
                int count = GetSupportCount(native);
                for (int i = 0; i < count; i++) PhysicsExtensions.UpdateController.UnsupportRigidbody(native);
                native.collisionDetectionMode = CollisionDetectionMode.Discrete;
                native.isKinematic = true;
            }
            rig.RecoveryBody ??= rig.Bodies[0];
            rig.PelvisOffset = rig.RecoveryBody.Position - rig.OriginalRoot;
            // Standing pelvis height is not a valid offset for prone recovery.
            rig.PelvisOffset.y = 0f;
            rig.EndsAt = Time.realtimeSinceStartup + BotSeverPolicy.FallSeconds;
            rig.Phase = BotFallPhase.Falling;
            LivingRigs.Add(player, rig);
            return true;
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            if (rig != null) ReleaseLiving(rig, false);
            return false;
        }
    }
    private static LivingRig FreezeLiving(Player player, bool remote)
    {
        PlayerPoolObject pool = player.GetComponent<PlayerPoolObject>();
        if (pool == null || pool.RigidbodySpawners == null || pool.RigidbodySpawners.Length == 0)
            throw new InvalidOperationException("player pool ragdoll resources are unavailable");
        var rig = new LivingRig { Player = player, Pool = pool, Remote = remote,
            AnimatorMask = player.EnabledAnimators, BodyEnabled = player.BodyAnimatorCommon.enabled,
            ArmsEnabled = player.ArmsAnimatorCommon.enabled, ControllerEnabled = player._characterController.isEnabled,
            OriginalRoot = player.Transform.Original.position, OriginalRotation = player.Transform.Original.rotation, WasProne = player.IsInPronePose,
            BindPositions = new Vector3[pool.RigidbodySpawners.Length], BindRotations = new Quaternion[pool.RigidbodySpawners.Length] };
        for (int i = 0; i < pool.RigidbodySpawners.Length; i++)
        { rig.BindPositions[i] = pool.RigidbodySpawners[i].transform.localPosition; rig.BindRotations[i] = pool.RigidbodySpawners[i].transform.localRotation; }
        foreach (var armor in player.PlayerBones.ArmorPlateColliders)
            if (armor != null && armor.gameObject.activeSelf) rig.ActiveArmor.Add(armor.gameObject);
        try
        {
            player.MovementContext.ReleaseDoorIfInteractingWithOne();
            if (player.HandsController is Player.FirearmController firearm) firearm.SetTriggerPressed(false);
            player.EnabledAnimators = 0; player.BodyAnimatorCommon.enabled = false; player.ArmsAnimatorCommon.enabled = false;
            player._characterController.isEnabled = false; player.POM.Off();
            player.AIData?.BotOwner?.Mover.MovementPause(1.2f, false);
            return rig;
        }
        catch { RestoreLivingAnimation(rig); throw; }
    }
    private static void RestoreLivingAnimation(LivingRig rig, bool preserveNative = false)
    {
        if (rig.AnimationRestored) return;
        rig.AnimationRestored = true;
        Player player = rig.Player;
        if (player == null) return;
        foreach (GameObject armor in rig.ActiveArmor) if (armor != null) armor.SetActive(true);
        if (rig.NativeRagdoll != null)
        {
            if (!preserveNative)
            {
                foreach (CharacterJointSpawner joint in rig.Pool.JointSpawners) joint.Remove();
                foreach (RigidbodySpawner body in rig.Pool.RigidbodySpawners)
                {
                    if (body.Rigidbody != null) { PhysicsExtensions.UpdateController.UnsupportRigidbody(body.Rigidbody); body.Rigidbody.isKinematic = true; }
                    body.Remove();
                }
            }
            rig.NativeRagdoll = null;
        }
        for (int i = 0; !preserveNative && i < rig.Pool.RigidbodySpawners.Length; i++)
        {
            Transform bone = rig.Pool.RigidbodySpawners[i].transform;
            bone.localPosition = rig.BindPositions[i]; bone.localRotation = rig.BindRotations[i];
        }
        player.EnabledAnimators = rig.AnimatorMask;
        player.BodyAnimatorCommon.enabled = rig.BodyEnabled; player.ArmsAnimatorCommon.enabled = rig.ArmsEnabled;
        player._characterController.isEnabled = rig.ControllerEnabled; player.POM.On();
        if (player.HealthController?.IsAlive == true)
        {
            if (BotSeverStateService.Restricted(player))
            { BotSeverStateService.EnforceProne(player); player.MovementContext.PlayerAnimator.SetProneStateForce(); }
            else player.MovementContext.IsInPronePose = rig.WasProne;
            player.AIData?.BotOwner?.Mover.MovementResume();
        }
    }
    private static void ReleaseLiving(LivingRig rig, bool keepBlend, bool preserveNative = false)
    {
        if (_externalRuntime != null)
        {
            foreach (var joint in rig.Joints) _externalRuntime.DestroyJoint(joint.External);
            rig.Joints.Clear();
            foreach (LivingBody body in rig.Bodies) if (body.External != null)
            { _externalRuntime.DestroyBody(body.External); body.External = null; }
            if (rig.Group != 0) _externalRuntime.ReleaseEnvironment(rig.Group);
            rig.Group = 0;
        }
        RestoreLivingAnimation(rig, preserveNative);
        if (!keepBlend && rig.Player != null) LivingRigs.Remove(rig.Player);
    }
    internal static void CancelLivingFall(Player player, bool forDeath = false)
    {
        if (player == null || !LivingRigs.TryGetValue(player, out LivingRig rig)) return;
        bool preserveNative = forDeath && rig.NativeRagdoll != null;
        if (preserveNative)
        {
            // OnDead creates the native corpse synchronously. Destroying these
            // pooled components here would leave deferred Unity destruction
            // racing the replacement spawners in the same frame. EFT can reuse
            // the parked shells and joints at the last completed living pose.
            rig.Bodies.Sort((left, right) => GetHierarchyDepth(left.Bone).CompareTo(GetHierarchyDepth(right.Bone)));
            foreach (LivingBody body in rig.Bodies) if (body.Native != null)
            { body.Native.position = body.Position; body.Native.rotation = body.Rotation; }
        }
        ReleaseLiving(rig, false, preserveNative);
    }
    private static void UpdateLivingFalls()
    {
        LivingRemoved.Clear();
        foreach (var pair in LivingRigs)
        {
            LivingRig rig = pair.Value;
            if (rig.Player == null || rig.Player.HealthController?.IsAlive != true) { LivingRemoved.Add(pair.Key); continue; }
            if (rig.Phase == BotFallPhase.Falling && Time.realtimeSinceStartup >= rig.EndsAt)
            {
                foreach (LivingBody body in rig.Bodies) { body.BlendPosition = body.Position; body.BlendRotation = body.Rotation; }
                if (!rig.Remote)
                {
                    Vector3 root = ResolveLivingRecoveryRoot(rig.RecoveryBody.Position, rig.PelvisOffset);
                    // Use EFT's movement transform and reset its height/flying history.
                    rig.Player.Teleport(root, true);
                }
                ReleaseLiving(rig, true); rig.Phase = BotFallPhase.Blending;
                rig.BlendEndsAt = Time.realtimeSinceStartup + BotSeverPolicy.BlendSeconds;
                var actor = BotSeverStateService.Get(rig.Player);
                if (actor != null) BotSeverStateService.Publish(actor, true);
            }
            else if (rig.Phase == BotFallPhase.Blending && Time.realtimeSinceStartup >= rig.BlendEndsAt)
                LivingRemoved.Add(pair.Key);
        }
        foreach (Player player in LivingRemoved)
            if (LivingRigs.TryGetValue(player, out LivingRig rig)) { ReleaseLiving(rig, false); LivingRigs.Remove(player);
                var actor = BotSeverStateService.Get(player); if (actor != null) BotSeverStateService.Publish(actor, true); }
        for (int i = LivingGibs.Count - 1; i >= 0; i--)
        {
            LivingGib gib = LivingGibs[i];
            if (gib.Root == null || Time.realtimeSinceStartup > gib.ExpiresAt)
            { DestroyLivingGib(gib); LivingGibs.RemoveAt(i); continue; }
            if (gib.Creation != null)
            {
                var status = gib.Creation.Poll(out var body, out string failure);
                if (status == ExternalBodyCreationStatus.Pending) continue;
                gib.Creation.Dispose(); gib.Creation = null;
                if (status != ExternalBodyCreationStatus.Succeeded)
                { gib.Completion?.Invoke(false, failure); gib.Completion = null; DestroyLivingGib(gib); LivingGibs.RemoveAt(i); continue; }
                gib.Body = body; body.Velocity = gib.Velocity;
                gib.Completion?.Invoke(true, null); gib.Completion = null;
            }
            if (gib.Root == null || Time.realtimeSinceStartup > gib.ExpiresAt) { DestroyLivingGib(gib); LivingGibs.RemoveAt(i); }
        }
    }
    internal static Vector3 ResolveLivingRecoveryRoot(Vector3 pelvis, Vector3 horizontalOffset)
    {
        Vector3 root = pelvis - new Vector3(horizontalOffset.x, 0f, horizontalOffset.z);
        if (Physics.Raycast(root + Vector3.up * .75f, Vector3.down, out RaycastHit ground, 2.5f,
            _localEnvironmentMask, QueryTriggerInteraction.Ignore) && ground.normal.y > .25f)
            root.y = ground.point.y;
        else root.y -= .15f;
        if (NavMesh.SamplePosition(root, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) &&
            Mathf.Abs(hit.position.y - root.y) <= .75f && !Physics.CheckCapsule(hit.position + Vector3.up * .3f,
                hit.position + Vector3.up * .6f, .2f, _localEnvironmentMask, QueryTriggerInteraction.Ignore))
            return hit.position;
        // Navmesh gaps or nearby obstructions must not teleport back to the shot.
        // The native prone controller resolves remaining local contact on resume.
        return root;
    }
    private static void PublishLivingPhysics()
    {
        foreach (LivingRig rig in LivingRigs.Values)
            if (!rig.Remote && rig.Phase == BotFallPhase.Falling)
                foreach (LivingBody body in rig.Bodies) if (body.External?.IsValid == true)
                { body.Position = body.External.Position; body.Rotation = body.External.Rotation; }
        foreach (LivingGib gib in LivingGibs) if (gib.Root != null && gib.Body?.IsValid == true)
            gib.Root.transform.SetPositionAndRotation(gib.Body.Position, gib.Body.Rotation);
    }
    private static void PresentLivingFalls()
    {
        foreach (LivingRig rig in LivingRigs.Values)
            foreach (LivingBody body in rig.Bodies)
            {
                if (body.Bone == null) continue;
                if (rig.Phase == BotFallPhase.Falling) body.Bone.SetPositionAndRotation(body.Position, body.Rotation);
                else
                {
                    float blend = Mathf.Clamp01(1f - (rig.BlendEndsAt - Time.realtimeSinceStartup) / BotSeverPolicy.BlendSeconds);
                    body.Bone.SetPositionAndRotation(Vector3.Lerp(body.BlendPosition, body.Bone.position, blend),
                        Quaternion.Slerp(body.BlendRotation, body.Bone.rotation, blend));
                }
            }
    }
    internal static void CaptureLivingFall(Player player, ref BotSeverData data)
    {
        if (!LivingRigs.TryGetValue(player, out LivingRig rig)) return;
        data.Phase = (byte)rig.Phase;
        data.Remaining = Mathf.Clamp((rig.Phase == BotFallPhase.Falling ? rig.EndsAt : rig.BlendEndsAt) - Time.realtimeSinceStartup, 0f, 1.2f);
        int count = rig.Bodies.Count;
        data.BonePaths = new string[count]; data.Positions = new Vector3[count]; data.Rotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        { data.BonePaths[i] = rig.Bodies[i].Path; data.Positions[i] = rig.Bodies[i].Position; data.Rotations[i] = rig.Bodies[i].Rotation; }
    }
    internal static void ReceiveLivingFall(Player player, BotSeverData data)
    {
        if (data.Phase == 0) { CancelLivingFall(player); return; }
        if (LivingRigs.TryGetValue(player, out LivingRig previous) && previous.AnimationRestored && data.Phase == (byte)BotFallPhase.Falling)
            CancelLivingFall(player);
        if (!LivingRigs.TryGetValue(player, out LivingRig rig))
        {
            try { rig = FreezeLiving(player, true); LivingRigs[player] = rig; }
            catch (Exception exception) { RupturePlugin.Log.LogWarning("RPT remote living fall unavailable: " + exception.Message); return; }
        }
        rig.Phase = (BotFallPhase)data.Phase;
        rig.EndsAt = Time.realtimeSinceStartup + data.Remaining;
        Transform skeleton = player.PlayerBody.SkeletonRootJoint.transform;
        // A later cut changes the authority's topology during the same fall.
        // Rebuild presentation membership on every snapshot so retired bones
        // cannot keep receiving an obsolete world pose.
        var previousBodies = new Dictionary<string, LivingBody>();
        foreach (LivingBody body in rig.Bodies) previousBodies[body.Path] = body;
        rig.Bodies.Clear();
        for (int i = 0; i < (data.BonePaths?.Length ?? 0); i++)
        {
            string path = data.BonePaths[i];
            if (!previousBodies.TryGetValue(path, out LivingBody body))
            {
                Transform bone = string.IsNullOrEmpty(path) || path == "." ? skeleton : skeleton.Find(path);
                if (bone == null) continue;
                body = new LivingBody { Bone = bone, Path = path };
            }
            body.Position = data.Positions[i]; body.Rotation = data.Rotations[i];
            rig.Bodies.Add(body);
        }
        if (rig.Phase == BotFallPhase.Blending)
        {
            foreach (LivingBody body in rig.Bodies) { body.BlendPosition = body.Position; body.BlendRotation = body.Rotation; }
            player.Transform.Original.SetPositionAndRotation(data.RootPosition, data.RootRotation);
            RestoreLivingAnimation(rig); rig.BlendEndsAt = Time.realtimeSinceStartup + Mathf.Min(data.Remaining, .2f);
        }
    }
    internal static bool RegisterLivingGib(GameObject root, Vector3 point, Vector3 direction,
        ExternalTransientConvexShape[] shapes, Action<bool, string> completion)
    {
        if (!EnsureScene(out string failure)) { completion?.Invoke(false, failure); return false; }
        if (LivingGibs.Count >= 128)
        { completion?.Invoke(false, "living detached-limb capacity reached"); return false; }
        Rigidbody native = root.GetComponent<Rigidbody>();
        uint group = ++_externalGroupSequence;
        if (native == null || !_externalRuntime.EnsureEnvironment(group, point, point + direction, out _, out failure))
        { _externalRuntime.ReleaseEnvironment(group); completion?.Invoke(false, failure ?? "living gib rigidbody missing"); return false; }
        Vector3 velocity = native.velocity;
        Vector3 position = native.transform.position;
        Quaternion rotation = native.transform.rotation;
        IExternalBodyCreation creation;
        try
        {
            // Capture dynamic gravity/CCD state and the intended transform before
            // parking the Unity presentation shell. The backend snapshots these now.
            native.isKinematic = false;
            native.position = position; native.rotation = rotation; native.useGravity = true;
            native.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            native.maxDepenetrationVelocity = LocalGibMaximumDepenetrationSpeed;
            native.maxAngularVelocity = LocalGibMaximumAngularSpeed;
            creation = _externalRuntime.BeginCreateTransientConvexBody(native, shapes, group, false,
                ExternalCollisionProfile.EnvironmentOnly, out failure);
        }
        catch { _externalRuntime.ReleaseEnvironment(group); throw; }
        finally { ParkLocalDetachedEffectShell(root); }
        if (creation == null) { _externalRuntime.ReleaseEnvironment(group); completion?.Invoke(false, failure); return false; }
        LivingGibs.Add(new LivingGib { Root = root, Creation = creation, Group = group,
            Velocity = Vector3.ClampMagnitude(velocity + Vector3.ClampMagnitude(direction, 1f) * 2f, LocalGibMaximumInitialLaunchSpeed),
            Completion = completion, ExpiresAt = Time.realtimeSinceStartup + 120f });
        return true;
    }
    internal static void CancelLivingGib(GameObject root)
    {
        for (int i = LivingGibs.Count - 1; i >= 0; i--) if (LivingGibs[i].Root == root)
        {
            // A cancelled cut must never receive a late registration completion.
            LivingGibs[i].Completion = null;
            DestroyLivingGib(LivingGibs[i]); LivingGibs.RemoveAt(i);
        }
    }
    private static void DestroyLivingGib(LivingGib gib)
    {
        gib.Creation?.Cancel(); gib.Creation?.Dispose();
        if (gib.Body != null) _externalRuntime?.DestroyBody(gib.Body);
        if (gib.Group != 0) _externalRuntime?.ReleaseEnvironment(gib.Group);
        if (gib.Root != null) UnityEngine.Object.Destroy(gib.Root);
        gib.Completion?.Invoke(false, "living gib retired"); gib.Completion = null;
    }
    internal static void ResetLivingFalls()
    {
        var rigs = new List<LivingRig>(LivingRigs.Values);
        foreach (LivingRig rig in rigs) ReleaseLiving(rig, false);
        LivingRigs.Clear();
        foreach (LivingGib gib in LivingGibs) DestroyLivingGib(gib);
        LivingGibs.Clear();
    }
}
