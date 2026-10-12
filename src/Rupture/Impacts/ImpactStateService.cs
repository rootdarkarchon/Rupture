using System;
using Rupture.Dismemberment.Classes.HeadWounds;
using System.Collections.Generic;
using EFT;
using EFT.HealthSystem;
using EFT.Interactive;
using Rupture.Combined.Patches;
using Rupture.Ragdolls.Classes;
using UnityEngine;

namespace Rupture.Impacts;

internal static class ImpactStateService
{
    internal sealed class VictimState
    {
        internal Player Player;
        internal readonly LimbImpactPolicy[] Limbs = { new LimbImpactPolicy(), new LimbImpactPolicy(), new LimbImpactPolicy(), new LimbImpactPolicy() };
        internal readonly LimbImpactPolicy[] LivingLegs = { new LimbImpactPolicy(), new LimbImpactPolicy() };
        internal bool RetainedCorpse;
        internal bool DeathSent;
        internal bool DeathApplied;
        internal bool AwaitingDeathSnapshot;
        internal bool DeathQueued;
        internal bool TerminalDeathObserved;
        internal EDamageType QueuedDeathCause;
    }
    private static readonly Dictionary<string, VictimState> States = new Dictionary<string, VictimState>();
    private static readonly List<string> Removed = new List<string>();
    private static readonly List<VictimState> PendingDeaths = new List<VictimState>();
    private static float _nextPrune;
    private static int _missingSamples;
    [ThreadStatic] internal static ImpactData Current;
    internal static int Index(EBodyPart part) => part == EBodyPart.LeftArm ? 0 : part == EBodyPart.RightArm ? 1 : part == EBodyPart.LeftLeg ? 2 : part == EBodyPart.RightLeg ? 3 : -1;
    internal static bool CorpsePart(EBodyPart part) => part == EBodyPart.Head || Index(part) >= 0;
    internal static EBodyPart Part(int index) => index == 0 ? EBodyPart.LeftArm : index == 1 ? EBodyPart.RightArm : index == 2 ? EBodyPart.LeftLeg : EBodyPart.RightLeg;

    internal static VictimState Get(Player player)
    {
        if (player == null || string.IsNullOrEmpty(player.ProfileId)) return null;
        if (!States.TryGetValue(player.ProfileId, out VictimState state) ||
            !ReferenceEquals(state.Player, player))
        {
            state = new VictimState { Player = player,
                AwaitingDeathSnapshot = RagdollCoordinator.IsNetworkAuthority && MultiplayerRuntime.Current.IsObservedPlayer(player) && !player.IsAI };
            States[player.ProfileId] = state;
        }
        return state;
    }

    internal static VictimState Get(CorpseInteractionTarget target)
    {
        if (string.IsNullOrEmpty(target.ProfileId)) return null;
        if (States.TryGetValue(target.ProfileId, out VictimState existing)) { existing.RetainedCorpse = true; return existing; }
        VictimState state = new VictimState { Player = target.Player, RetainedCorpse = true,
            AwaitingDeathSnapshot = RagdollCoordinator.IsNetworkAuthority && ImpactNetwork.IsRemoteVictim(target.ProfileId) };
        States.Add(target.ProfileId, state);
        return state;
    }

    internal static bool Eligible(EFT.Ballistics.DamageInfo damage) => damage.DamageType == EDamageType.Bullet &&
        damage.Damage > 0 && !damage.Blunt && !damage.OverDamageFrom.HasValue;

    internal static void RecordDamage(Player player, EFT.Ballistics.DamageInfo damage, EBodyPart part, float appliedDamage, float healthBeforeHit)
    {
        if (player?.HealthController == null || !LimbImpactPolicy.Positive(appliedDamage) ||
            (RagdollCoordinator.IsRemoteClient && !player.IsYourPlayer) || !Eligible(damage)) return;
        VictimState state = Get(player);
        int index = Index(part);
        if (!Current.Valid)
        {
            MissingSample("ballistic damage");
            return;
        }
        if (index >= 0)
        {
            state.Limbs[index].Add(Current.Projectile, Current.Momentum, RuptureConfiguration.DismembermentMomentumThreshold.Value);
            if (index >= 2 && Health.BotSeverStateService.Eligible(player) && RuptureConfiguration.LivingLegSeveringExplosionsOnly?.Value != true)
            {
                bool added = Health.BotSeverPolicy.Accumulate(state.LivingLegs[index - 2], Current.Projectile, Current.Momentum,
                    RuptureConfiguration.LivingLegMomentumThreshold.Value, healthBeforeHit);
                Health.BotSeverStateService.Damaged(player, damage, part, added, healthBeforeHit);
            }
        }
    }

    internal static void Healed(ActiveHealthController health, EBodyPart part, float delta, float healthBeforeHealing)
    {
        int index = Index(part);
        if (index < 0 || delta <= 0 || !health.IsAlive) return;
        foreach (VictimState state in States.Values)
        {
            if (state.Player == null || !ReferenceEquals(state.Player.HealthController, health)) continue;
            var value = health.GetBodyPartHealth(part, false);
            state.Limbs[index].Heal(true, delta, value.Current, value.Maximum);
            if (index >= 2) Health.BotSeverPolicy.Healed(state.LivingLegs[index - 2], true, value.Current - healthBeforeHealing);
            break;
        }
    }

    internal static void Death(Player player, EFT.Ballistics.DamageInfo damage, EBodyPart part)
    {
        VictimState state = Get(player);
        if (!TryBeginDeath(state, MultiplayerRuntime.Current.IsDowned(player))) return;
        bool scopedOrigin = ExplosionEventScope.TryGetOrigin(out Vector3 origin);
        ImpactData sample = DeathImpactPolicy.PrepareSample(damage, Current, scopedOrigin, origin);
        if (DeathImpactPolicy.IsExplosion(damage.DamageType) && sample.HasMasterOrigin) damage.MasterOrigin = sample.MasterOrigin;
        if (RagdollCoordinator.IsRemoteClient)
        {
            if (player.IsYourPlayer) ImpactNetwork.SendDeath(player, state, damage, part, sample);
            return;
        }
        ApplyDeath(player, state, damage, part, sample);
    }

    internal static bool TryBeginDeath(VictimState state, bool downed)
    {
        // Fika uses IsAlive=false for recoverable downing. OnDead is the terminal
        // boundary, including bleedout where Fika can leave Downed set to true.
        if (state == null || state.DeathSent || state.DeathApplied || state.AwaitingDeathSnapshot ||
            (downed && !state.TerminalDeathObserved)) return false;
        state.DeathSent = true;
        return true;
    }

    internal static bool IsTerminalDeath(Player player) => player != null &&
        !string.IsNullOrEmpty(player.ProfileId) && States.TryGetValue(player.ProfileId, out VictimState state) &&
        ReferenceEquals(state.Player, player) && (state.TerminalDeathObserved || state.DeathApplied);

    // OnDead runs inside health damage, before the damage postfix records the
    // lethal momentum. Let that path finish before supplying a lifecycle-only death.
    internal static void QueueDeath(Player player, EDamageType cause)
    {
        if (player == null) return;
        VictimState state = Get(player);
        if (state == null) return;
        state.TerminalDeathObserved = true;
        if ((RagdollCoordinator.IsRemoteClient && !player.IsYourPlayer) ||
            state.DeathQueued || state.DeathSent || state.DeathApplied || state.AwaitingDeathSnapshot) return;
        state.DeathQueued = true;
        state.QueuedDeathCause = cause;
        PendingDeaths.Add(state);
    }

    internal static bool ConsumeQueuedDeath(VictimState state, out EFT.Ballistics.DamageInfo damage)
    {
        damage = new EFT.Ballistics.DamageInfo { DamageType = state.QueuedDeathCause };
        bool needed = state.DeathQueued && !state.DeathSent && !state.DeathApplied && !state.AwaitingDeathSnapshot;
        state.DeathQueued = false;
        return needed;
    }

    private static void ProcessPendingDeaths()
    {
        for (int i = PendingDeaths.Count - 1; i >= 0; i--)
        {
            VictimState state = PendingDeaths[i];
            PendingDeaths.RemoveAt(i);
            if (!ConsumeQueuedDeath(state, out EFT.Ballistics.DamageInfo damage)) continue;
            Player player = state.Player;
            if (player == null || player.HealthController == null || player.HealthController.IsAlive ||
                !States.TryGetValue(player.ProfileId, out VictimState current) || !ReferenceEquals(current, state)) continue;
            ImpactData previous = Current;
            Current = default;
            try
            {
                Death(player, damage, EBodyPart.Common);
                RupturePlugin.Log.LogDebug($"RPT lifecycle-only death: profile={player.ProfileId}, cause={damage.DamageType}.");
            }
            finally { Current = previous; }
        }
    }
    internal static void Restored(ActiveHealthController health, EBodyPart part)
    {
        int leg = Health.BotSeverStateService.Leg(part);
        if (leg < 0 || !health.IsAlive) return;
        foreach (VictimState state in States.Values)
            if (state.Player != null && ReferenceEquals(state.Player.HealthController, health))
            { state.LivingLegs[leg].ResetBuildup(); break; }
    }

    internal static void ApplyDeath(Player player, VictimState state, EFT.Ballistics.DamageInfo damage, EBodyPart part, ImpactData sample)
    {
        if (state.DeathApplied) return;
        state.DeathApplied = true;
        state.AwaitingDeathSnapshot = false;
        ImpactData previous = Current;
        Current = sample;
        damage.MasterOrigin = ImpactNetwork.DeathMasterOrigin(damage, sample);
        try { KillPatch.ProcessDeath(player, damage, part); }
        finally { Current = previous; }
    }

    internal static bool BeginSever(CorpseInteractionTarget target, EBodyPart part, float roll)
    {
        int index = Index(part);
        VictimState state = Get(target);
        return index >= 0 && state != null && !state.AwaitingDeathSnapshot && state.Limbs[index].Begin(true, roll, RuptureConfiguration.DismembermentMomentumThreshold.Value);
    }

    internal static void Complete(CorpseInteractionTarget target, EBodyPart part, bool committed)
    {
        int index = Index(part);
        if (index >= 0 && States.TryGetValue(target.ProfileId, out VictimState state)) state.Limbs[index].Complete(committed);
    }

    internal static void CorpseImpact(EFT.Ballistics.Shot shot, ImpactData sample)
    {
        if (shot == null || shot.HitCollider == null || shot.BlockedBy != null || shot.DeflectedBy != null || shot.Damage <= 0 || shot.AvoidAdditionalDamage || !shot.IsForwardHit) return;
        Corpse corpse = shot.HitCollider.GetComponentInParent<Corpse>();
        if (corpse == null) ShadowRagdollWorld.TryResolveHitCorpse(shot.HitCollider, out corpse);
        if (corpse == null || ShadowRagdollWorld.IsIndependentWeaponHit(corpse, shot.HitCollider) ||
            !RagdollCoordinator.TryGetInteractionTarget(corpse, out CorpseInteractionTarget target) ||
            !RagdollCoordinator.TryResolveCorpseBodyPart(target, shot.HitCollider, out EBodyPart part, out _) || !CorpsePart(part)) return;
        if (!sample.Valid) { MissingSample("corpse collision"); return; }
        Vector3 point = shot.HitPoint, direction = shot.CurrentDirection;
        if (part == EBodyPart.Head)
        {
            if (target.Head?.Bone == null) return;
            point = target.Head.Bone.InverseTransformPoint(point);
            direction = target.Head.Bone.InverseTransformDirection(direction).normalized;
        }
        if (RagdollCoordinator.IsRemoteClient) ImpactNetwork.SendCorpse(target, part, sample, point, direction, shot.PenetrationPower);
        else if (!ApplyCorpse(target, part, sample, point, direction, shot.PenetrationPower))
            ImpactNetwork.DeferCorpse(target, part, sample, point, direction, shot.PenetrationPower);
    }

    // False means the owner's death snapshot has not arrived; caller retains this impact.
    internal static bool ApplyCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration = 0f)
    {
        int index = Index(part);
        VictimState state = Get(target);
        if (!CorpsePart(part) || state == null || !sample.Valid) return true;
        if (state.AwaitingDeathSnapshot) return false;
        if (part == EBodyPart.Head && HeadWoundSystem.IsAwaitingLethalHeadshot(target)) return false;
        if (part == EBodyPart.Head)
        {
            HeadWoundSystem.CompleteCorpseImpact(target, sample, point, direction, penetration);
            return true;
        }
        if (state.Limbs[index].Add(sample.Projectile, sample.Momentum, RuptureConfiguration.DismembermentMomentumThreshold.Value)) KillPatch.TryDismemberCorpseShot(target, part, point, direction);
        return true;
    }

    internal static void Update()
    {
        ProcessPendingDeaths();
        if (Time.realtimeSinceStartup < _nextPrune) return;
        _nextPrune = Time.realtimeSinceStartup + 1f;
        Removed.Clear();
        foreach (var pair in States) if (pair.Value.Player == null && !pair.Value.RetainedCorpse && !pair.Value.DeathSent && !pair.Value.DeathApplied && !pair.Value.AwaitingDeathSnapshot) Removed.Add(pair.Key);
        foreach (string profile in Removed) States.Remove(profile);
    }
    private static void MissingSample(string context)
    {
        if (_missingSamples++ < 5) RupturePlugin.Log.LogWarning("RPT " + context + " has no valid impact sample; no momentum was invented.");
    }
    internal static void Remove(string profile) { if (profile != null) States.Remove(profile); }
    internal static void Reset() { States.Clear(); Removed.Clear(); PendingDeaths.Clear(); Current = default; _missingSamples = 0; _nextPrune = 0; }
}
