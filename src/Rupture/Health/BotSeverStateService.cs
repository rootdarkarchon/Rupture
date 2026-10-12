using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Impacts;
using Rupture.Ragdolls.Classes;
using UnityEngine;

namespace Rupture.Health;

internal static class BotSeverStateService
{
    internal sealed class Actor
    {
        internal Player Player;
        internal IHealthController Health;
        internal readonly BotSeverPolicy Policy = new BotSeverPolicy();
        internal readonly uint[] Events = new uint[2], Seeds = new uint[2];
        internal readonly uint[] PendingHealingRevisions = new uint[2];
        internal readonly LimbSeverLevel[] Levels = new LimbSeverLevel[2];
        internal readonly Vector3[] Directions = new Vector3[2];
        internal readonly float[] BleedTime = new float[2], EnergyTime = new float[2];
        internal byte Pending, Visuals;
        internal byte PendingExplosions;
        internal readonly Dictionary<long, byte> ExplosionRolls = new Dictionary<long, byte>();
        internal uint PoseSequence, LastPose;
        internal float NextSnapshot;
    }
    private static readonly Dictionary<Player, Actor> Actors = new Dictionary<Player, Actor>();
    private static readonly List<Player> Removed = new List<Player>();
    private static readonly Dictionary<string, (BotSeverData Data, float Deadline)> Incoming = new Dictionary<string, (BotSeverData, float)>();
    private static readonly List<string> Expired = new List<string>();
    internal static bool Owns => !MultiplayerRuntime.Current.IsRemoteClient;
    internal static int Leg(EBodyPart part) => part == EBodyPart.LeftLeg ? 0 : part == EBodyPart.RightLeg ? 1 : -1;
    internal static EBodyPart Part(int leg) => leg == 0 ? EBodyPart.LeftLeg : EBodyPart.RightLeg;
    internal static bool Eligible(Player player) => Owns && player != null && player.IsAI && !player.IsYourPlayer &&
        player.AIData?.BotOwner != null && !player.UsedSimplifiedSkeleton && player.HealthController?.IsAlive == true &&
        !MultiplayerRuntime.Current.IsDowned(player) && player.PlayerBody != null &&
        BotSeverRolePolicy.Allowed(player.Profile?.Info?.Settings?.Role, RuptureConfiguration.LivingLegSeveringSpecialAI?.Value == true);
    internal static Actor Get(Player player) => player != null && Actors.TryGetValue(player, out Actor actor) &&
        ReferenceEquals(actor.Health, player.HealthController) ? actor : null;
    private static Actor Add(Player player)
    {
        Actor actor = Get(player);
        if (actor != null) return actor;
        actor = new Actor { Player = player, Health = player.HealthController };
        Actors[player] = actor; return actor;
    }
    internal static bool Restricted(Player player) => player?.HealthController?.IsAlive == true && Get(player)?.Policy.SeveredMask > 0;
    internal static bool FallingHands(object hands)
    {
        foreach (Actor actor in Actors.Values) if (actor.Player != null && ReferenceEquals(actor.Player.HandsController, hands))
            return ShadowRagdollWorld.LivingFallActive(actor.Player);
        return false;
    }
    internal static bool Severed(Player player, EBodyPart part) => Get(player)?.Policy.Severed(Leg(part)) == true;
    internal static bool Untreated(Player player, EBodyPart part) => Get(player)?.Policy.Untreated(Leg(part)) == true;
    internal static bool IsLivingCut(CorpseInteractionTarget target) => target.Corpse == null && target.Player?.HealthController?.IsAlive == true && Get(target.Player) != null;
    internal static void Damaged(Player player, DamageInfo damage, EBodyPart part, bool newProjectile, float healthBeforeHit)
    {
        int leg = Leg(part);
        if (leg < 0 || !newProjectile || RuptureConfiguration.LivingLegSeveringExplosionsOnly?.Value == true ||
            !BotSeverPolicy.WasBlacked(healthBeforeHit) || !Eligible(player) || RuptureConfiguration.LivingBotLegSevering?.Value != true ||
            RuptureConfiguration.EnableDismemberment?.Value != true) return;
        Actor actor = Add(player);
        if (actor.Policy.Severed(leg) || (actor.Pending & (1 << leg)) != 0) return;
        LimbImpactPolicy impact = ImpactStateService.Get(player).LivingLegs[leg];
        if (!impact.Begin(true, UnityEngine.Random.value, RuptureConfiguration.LivingLegMomentumThreshold.Value)) return;
        QueueSever(player, damage, part, impact, false, damage.HitPoint, damage.Direction);
    }
    internal static void ExplosionDamaged(Player player, DamageInfo damage, EBodyPart part, float healthDamage)
    {
        int leg = Leg(part);
        if (leg < 0 || !Eligible(player) || RuptureConfiguration.LivingBotLegSevering?.Value != true ||
            RuptureConfiguration.EnableDismemberment?.Value != true || !DeathImpactPolicy.IsExplosion(damage.DamageType) ||
            damage.OverDamageFrom.HasValue || !LimbImpactPolicy.Positive(healthDamage)) return;
        Actor actor = Add(player);
        if (actor.Policy.Severed(leg) || (actor.Pending & (1 << leg)) != 0 ||
            !LivingExplosionEvents.Resolve(damage, ImpactStateService.Current, out long sequence, out Vector3 center, out float radius) ||
            !LimbDismembermentSystem.TryGetExplosionDistance(Target(player), part, center, out float distance, out Vector3 nearest)) return;
        if (!LivingExplosionSeverPolicy.Remember(actor.ExplosionRolls, sequence, leg) ||
            !LivingExplosionSeverPolicy.Select(distance, radius, healthDamage,
                player.HealthController.GetBodyPartHealth(part, false).Maximum, UnityEngine.Random.value)) return;
        Vector3 direction = nearest - center;
        QueueSever(player, damage, part, ImpactStateService.Get(player).LivingLegs[leg], true, nearest,
            direction.sqrMagnitude > .000001f ? direction.normalized : Vector3.up);
    }
    private static void QueueSever(Player player, DamageInfo damage, EBodyPart part, LimbImpactPolicy impact,
        bool explosion, Vector3 point, Vector3 direction)
    {
        int leg = Leg(part); Actor actor = Get(player);
        actor.Pending |= (byte)(1 << leg);
        if (explosion) actor.PendingExplosions |= (byte)(1 << leg);
        else actor.PendingExplosions &= (byte)~(1 << leg);
        actor.PendingHealingRevisions[leg] = impact.HealingRevision;
        uint sequence = RagdollCoordinator.NextNetworkEventSequence();
        uint seed = sequence * 0x9E3779B9u ^ (uint)part;
        actor.Events[leg] = sequence; actor.Seeds[leg] = seed; actor.Directions[leg] = direction;
        CorpseInteractionTarget target = Target(player);
        var result = LimbDismembermentSystem.RequestSever(target, part, point, direction,
            false, sequence, seed, null, false, completion =>
            {
                actor.Pending &= (byte)~(1 << leg);
                actor.PendingExplosions &= (byte)~(1 << leg);
                bool committed = completion.Status == LimbDismembermentSystem.SeverCompletionStatus.Committed;
                impact.Complete(committed);
                if (!committed) { if (actor.Policy.SeveredMask == 0) ShadowRagdollWorld.CancelLivingFall(player); return; }
                // The anatomy is also gone for subsequent corpse dismemberment.
                ImpactStateService.Get(player).Limbs[leg + 2].Complete(true);
                actor.Levels[leg] = completion.Level;
                actor.Visuals |= (byte)(1 << leg);
                if (!Eligible(player) || !ReferenceEquals(Get(player), actor)) return;
                actor.Policy.Sever(leg);
                ActiveHealthController health = player.ActiveHealthController;
                health.BodyState[part].Health.Current = 0f;
                health.DestroyBodyPart(part, damage.DamageType);
                health.NetworkSyncBodyHealth(part);
                RemoveReplacedEffects(health, part);
                EnforceProne(player);
                Publish(actor, true);
            }, out _);
        if (result != LimbDismembermentSystem.SeverRequestResult.Accepted)
        { actor.Pending &= (byte)~(1 << leg); actor.PendingExplosions &= (byte)~(1 << leg); impact.Complete(false); }
    }
    internal static CorpseInteractionTarget Target(Player player) => new CorpseInteractionTarget(null, player,
        player.PlayerBody, player.Transform.Original, player.ProfileId, MultiplayerRuntime.Current.PlayerId(player), 0, false);
    internal static bool BeforeCutCommit(CorpseInteractionTarget target, EBodyPart part)
    {
        if (!IsLivingCut(target)) return false;
        if (!Owns) return target.Player.HealthController.IsAlive;
        if (!Eligible(target.Player) || RuptureConfiguration.LivingBotLegSevering?.Value != true || RuptureConfiguration.EnableDismemberment?.Value != true) return false;
        int leg = Leg(part);
        Actor actor = Get(target.Player);
        if (leg < 0 || (actor.Pending & (1 << leg)) == 0 || !BotSeverPolicy.CanCommit((actor.PendingExplosions & (1 << leg)) != 0,
            RuptureConfiguration.LivingLegSeveringExplosionsOnly?.Value == true, target.Player.HealthController.GetBodyPartHealth(part, false).Current,
            actor.PendingHealingRevisions[leg], ImpactStateService.Get(target.Player).LivingLegs[leg].HealingRevision)) return false;
        bool success = ShadowRagdollWorld.BeginLivingFall(target.Player, out string failure);
        if (!success) RupturePlugin.Log.LogWarning("RPT living sever rejected: " + failure);
        return success;
    }
    internal static void RemoveReplacedEffects(ActiveHealthController health, EBodyPart part)
    {
        // HeavyBleeding.Removed creates Wound. Remove that last, including residues.
        health.RemoveEffect<ActiveHealthController.HeavyBleeding>(part);
        health.RemoveEffect<ActiveHealthController.LightBleeding>(part);
        health.RemoveEffect<ActiveHealthController.Fracture>(part);
        health.RemoveEffect<ActiveHealthController.Wound>(part);
    }
    internal static bool RemovedLivingBone(Player player, Transform bone)
    {
        Actor actor = Get(player);
        if (actor == null || bone == null) return false;
        for (int leg = 0; leg < 2; leg++) if (actor.Policy.Severed(leg))
            if (LimbDismembermentSystem.TryGetLimbSegment(player, Part(leg), actor.Levels[leg], out Transform root, out _) &&
                root != null && (bone == root || bone.IsChildOf(root))) return true;
        return false;
    }
    internal static void EnforceProne(Player player)
    {
        if (player?.HealthController?.IsAlive != true) return;
        player.MovementContext.IsInPronePose = true;
        if (Owns && player.AIData?.BotOwner != null)
        {
            player.AIData.BotOwner.BotRun.StopRunAnyway();
            player.AIData.BotOwner.Mover.SetPose(0f);
        }
    }
    internal static void Treated(ActiveHealthController health, EBodyPart part)
    {
        Actor actor = Get(health.Player);
        if (actor == null || !actor.Policy.Treat(Leg(part))) return;
        ClampTreated(health, part);
        PulsatingBloodStreamSystem.StopLivingSever(health.Player, part);
        Publish(actor, true);
    }
    internal static void ClampTreated(ActiveHealthController health, EBodyPart part)
    {
        Actor actor = Get(health.Player); int leg = Leg(part);
        if (actor == null || !actor.Policy.Severed(leg) || actor.Policy.Untreated(leg)) return;
        var state = health.BodyState[part];
        if (state.Health.Maximum != 1f || state.Health.Current > 1f)
        { state.Health = new HealthValue(Mathf.Clamp(state.Health.Current, 0f, 1f), 1f); health.NetworkSyncBodyHealth(part); }
    }
    internal static void Death(Player player)
    {
        // Cancel incomplete live transactions before the lethal hit can reserve
        // a corpse cut on the same shared anatomy.
        LimbDismembermentSystem.CancelLivingCuts(player);
        ShadowRagdollWorld.CancelLivingFall(player, forDeath: true);
        if (Get(player) is Actor actor)
        {
            actor.Pending = 0;
            actor.PendingExplosions = 0;
            for (int leg = 0; leg < 2; leg++) if (actor.Policy.Severed(leg))
                ShadowRagdollWorld.PreserveLivingSeverForCorpse(player, Part(leg), actor.Levels[leg]);
        }
    }
    internal static void Update(bool simulationActive)
    {
        if (!simulationActive) return;
        DrainIncoming();
        Removed.Clear();
        foreach (var pair in Actors)
        {
            Actor actor = pair.Value; Player player = actor.Player;
            if (player == null || !ReferenceEquals(player.HealthController, actor.Health)) { Removed.Add(pair.Key); continue; }
            if (!actor.Health.IsAlive) { ShadowRagdollWorld.CancelLivingFall(player); continue; }
            if (actor.Policy.SeveredMask != 0) EnforceProne(player);
            if (!Owns) continue;
            ActiveHealthController health = player.ActiveHealthController;
            for (int leg = 0; leg < 2; leg++)
            {
                if (!actor.Policy.Severed(leg)) continue;
                EBodyPart part = Part(leg);
                RemoveReplacedEffects(health, part);
                if (!actor.Policy.Untreated(leg)) { ClampTreated(health, part); continue; }
                var settings = Singleton<GlobalConfiguration>.Instance.Health.Effects.HeavyBleeding;
                bool dehydrated = health.FindActiveEffect<ActiveHealthController.Dehydration>() != null;
                float interval = dehydrated ? settings.HealthLoopTimeDehydrated : settings.HealthLoopTime;
                if (BotSeverPolicy.Tick(Time.deltaTime, interval, ref actor.BleedTime[leg]))
                {
                    float damage = BotSeverPolicy.Damage(dehydrated ? settings.DamageHealthDehydrated : settings.DamageHealth);
                    foreach (EBodyPart bodyPart in HealthHelper.RealBodyParts)
                    {
                        if (!health.IsAlive) break;
                        health.ApplyDamage(bodyPart, damage, DamageHelper.HeavyBleedingDamage);
                    }
                }
                if (health.IsAlive && BotSeverPolicy.Tick(Time.deltaTime, settings.EnergyLoopTime, ref actor.EnergyTime[leg]))
                    health.ChangeEnergy(-settings.DamageEnergy);
            }
            if (actor.Policy.SeveredMask != 0 && Time.realtimeSinceStartup >= actor.NextSnapshot)
            {
                bool falling = ShadowRagdollWorld.LivingFallActive(player);
                actor.NextSnapshot = Time.realtimeSinceStartup + (falling ? .05f : 1f);
                Publish(actor, !falling);
            }
        }
        foreach (Player player in Removed) { ShadowRagdollWorld.CancelLivingFall(player); Actors.Remove(player); }
    }
    internal static void Publish(Actor actor, bool reliable)
    {
        if (!Owns || !MultiplayerRuntime.Current.HasServer || actor.Policy.SeveredMask == 0) return;
        var data = new BotSeverData { ProfileId = actor.Player.ProfileId, PlayerId = MultiplayerRuntime.Current.PlayerId(actor.Player),
            Revision = actor.Policy.Revision, PoseSequence = ++actor.PoseSequence, SeveredMask = actor.Policy.SeveredMask,
            TreatedMask = actor.Policy.TreatedMask, RootPosition = actor.Player.Transform.Original.position,
            RootRotation = actor.Player.Transform.Original.rotation, LeftEvent = actor.Events[0], RightEvent = actor.Events[1],
            LeftSeed = actor.Seeds[0], RightSeed = actor.Seeds[1], LeftLevel = actor.Levels[0], RightLevel = actor.Levels[1],
            LeftDirection = actor.Directions[0], RightDirection = actor.Directions[1] };
        ShadowRagdollWorld.CaptureLivingFall(actor.Player, ref data);
        MultiplayerRuntime.Current.Send(ref data, !reliable);
    }
    internal static void Receive(BotSeverData data)
    {
        if (Owns || !Valid(data)) return;
        if (Incoming.TryGetValue(data.ProfileId, out var pending) &&
            (data.Revision < pending.Data.Revision || data.Revision == pending.Data.Revision && data.PoseSequence <= pending.Data.PoseSequence)) return;
        if (Incoming.Count >= 256 && !Incoming.ContainsKey(data.ProfileId)) return;
        Incoming[data.ProfileId] = (data, Time.realtimeSinceStartup + 5f);
    }
    internal static bool Valid(BotSeverData data)
    {
        if (string.IsNullOrEmpty(data.ProfileId) || data.ProfileId.Length > 128 || data.PlayerId < 0 || data.Revision == 0 ||
            data.PoseSequence == 0 || !BotSeverPolicy.Valid(data.SeveredMask, data.TreatedMask) || data.Phase > 2 ||
            !ExplosionSeverPolicy.Finite(data.RootPosition) || !Finite(data.RootRotation) ||
            float.IsNaN(data.Remaining) || data.Remaining < 0 || data.Remaining > 1.2f) return false;
        int count = data.BonePaths?.Length ?? 0;
        if (count > 32 || (data.Positions?.Length ?? 0) != count || (data.Rotations?.Length ?? 0) != count) return false;
        if (!ExplosionSeverPolicy.Finite(data.LeftDirection) || !ExplosionSeverPolicy.Finite(data.RightDirection)) return false;
        for (int i = 0; i < count; i++) if (data.BonePaths[i] == null || data.BonePaths[i].Length > 256 ||
            !ExplosionSeverPolicy.Finite(data.Positions[i]) || !Finite(data.Rotations[i]) ||
            (data.Positions[i] - data.RootPosition).sqrMagnitude > 100f) return false;
        return ((data.SeveredMask & 1) == 0 || data.LeftEvent != 0 && ValidLevel(data.LeftLevel)) &&
            ((data.SeveredMask & 2) == 0 || data.RightEvent != 0 && ValidLevel(data.RightLevel));
    }
    private static bool ValidLevel(LimbSeverLevel level) => level == LimbSeverLevel.Proximal || level == LimbSeverLevel.Distal;
    private static bool Finite(Quaternion value) => !float.IsNaN(value.x + value.y + value.z + value.w) &&
        !float.IsInfinity(value.x + value.y + value.z + value.w) && value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w > .5f &&
        value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w < 1.5f;
    private static void DrainIncoming()
    {
        if (!Singleton<GameWorld>.Instantiated) return;
        Expired.Clear();
        foreach (var pair in Incoming)
        {
            BotSeverData data = pair.Value.Data; Player player = null;
            foreach (Player candidate in Singleton<GameWorld>.Instance.AllAlivePlayersList)
                if (candidate != null && candidate.ProfileId == data.ProfileId && MultiplayerRuntime.Current.PlayerId(candidate) == data.PlayerId) { player = candidate; break; }
            if (player == null) { if (Time.realtimeSinceStartup > pair.Value.Deadline) Expired.Add(pair.Key); continue; }
            Expired.Add(pair.Key);
            if (player.IsYourPlayer || !MultiplayerRuntime.Current.IsObservedPlayer(player) || !player.HealthController.IsAlive) continue;
            Actor actor = Add(player);
            if (data.PoseSequence <= actor.LastPose || !actor.Policy.Import(data.SeveredMask, data.TreatedMask, data.Revision)) continue;
            actor.LastPose = data.PoseSequence;
            actor.Events[0] = data.LeftEvent; actor.Events[1] = data.RightEvent;
            actor.Seeds[0] = data.LeftSeed; actor.Seeds[1] = data.RightSeed;
            actor.Levels[0] = data.LeftLevel; actor.Levels[1] = data.RightLevel;
            actor.Directions[0] = data.LeftDirection; actor.Directions[1] = data.RightDirection;
            for (int leg = 0; leg < 2; leg++)
            {
                if (!actor.Policy.Severed(leg) || (actor.Visuals & (1 << leg)) != 0 || (actor.Pending & (1 << leg)) != 0) continue;
                int capturedLeg = leg; actor.Pending |= (byte)(1 << leg);
                var result = LimbDismembermentSystem.RequestSever(Target(player), Part(leg), player.Transform.Original.position,
                    actor.Directions[leg], false, actor.Events[leg], actor.Seeds[leg], actor.Levels[leg], true,
                    completion => { actor.Pending &= (byte)~(1 << capturedLeg); if (completion.Status == LimbDismembermentSystem.SeverCompletionStatus.Committed)
                        actor.Visuals |= (byte)(1 << capturedLeg); }, out _);
                if (result == LimbDismembermentSystem.SeverRequestResult.Rejected || result == LimbDismembermentSystem.SeverRequestResult.WaitingForGeometry)
                    actor.Pending &= (byte)~(1 << leg);
            }
            ShadowRagdollWorld.ReceiveLivingFall(player, data);
            EnforceProne(player);
        }
        foreach (string id in Expired) Incoming.Remove(id);
    }
    internal static void Reset()
    {
        PulsatingBloodStreamSystem.ResetLivingStumps();
        ShadowRagdollWorld.ResetLivingFalls(); Actors.Clear(); Removed.Clear(); Incoming.Clear(); Expired.Clear();
        LivingExplosionEvents.Reset();
    }
}
