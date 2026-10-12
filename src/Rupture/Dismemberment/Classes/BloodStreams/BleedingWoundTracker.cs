using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using UnityEngine;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Ragdolls.Classes;

namespace Rupture.Dismemberment.Classes.BloodStreams;

// Tracks actual health effects independently of the bounded rendering pool. Slots
// retain wound anchors through pauses/treatment so reopening does not move a wound.
internal static class BleedingWoundTracker
{
    private sealed class Wound
    {
        internal Transform Anchor;
        internal EBodyPartColliderType Collider;
        internal Vector3 Point, Direction;
        internal BloodStreamTriggerKind Sent;
        internal EFT.HealthSystem.IHealthEffect Effect;
        internal float RetryAt;
    }
    private sealed class Actor
    {
        internal Player Player;
        internal IHealthController Health;
        internal readonly Wound[] Wounds = new Wound[7];
        internal readonly EFT.HealthSystem.IHealthEffect[] Heavy = new EFT.HealthSystem.IHealthEffect[7], Light = new EFT.HealthSystem.IHealthEffect[7];
        private readonly List<(EFT.HealthSystem.IHealthEffect Effect, int Part, bool Heavy)> _bleeds = new List<(EFT.HealthSystem.IHealthEffect, int, bool)>();
        private bool _dirty = true;
        private int _effectCount = -1;
        private float _nextReconcile;
        internal int EffectsFrame = -1;
        internal bool Downed;
        internal void InvalidateEffects() { _dirty = true; EffectsFrame = -1; }
        internal bool CaptureEffects()
        {
            EffectsFrame = -1;
            IReadOnlyList<EFT.HealthSystem.IHealthEffect> effects = Rupture.Health.NativeHealthEffects.TryGet(Health);
            if (effects == null) return false;
            // Cache membership, not Active: native pause/resume can change state
            // without an event. Recheck every bleeding candidate each frame.
            float now = Time.realtimeSinceStartup;
            if (_dirty || effects.Count != _effectCount || now >= _nextReconcile)
            {
                _bleeds.Clear(); _effectCount = effects.Count; _dirty = false; _nextReconcile = now + 0.5f;
                ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodEffectScans, 1);
                ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodEffectsVisited, effects.Count);
                for (int i = 0; i < effects.Count; i++)
                {
                    EFT.HealthSystem.IHealthEffect effect = effects[i]; int part = (int)effect.BodyPart;
                    if ((uint)part >= 7) continue;
                    if (effect is EFT.HealthSystem.IHeavyBleeding) _bleeds.Add((effect, part, true));
                    if (effect is EFT.HealthSystem.ILightBleeding) _bleeds.Add((effect, part, false));
                }
            }
            System.Array.Clear(Heavy, 0, Heavy.Length); System.Array.Clear(Light, 0, Light.Length);
            for (int i = 0; i < _bleeds.Count; i++)
            {
                var bleed = _bleeds[i]; var target = bleed.Heavy ? Heavy : Light;
                if (target[bleed.Part] == null && bleed.Effect.Active) target[bleed.Part] = bleed.Effect;
            }
            EffectsFrame = Time.frameCount;
            return true;
        }
        internal Actor(Player player)
        {
            Player = player; Health = player.HealthController;
            for (int i = 0; i < Wounds.Length; i++) Wounds[i] = new Wound();
            Health.EffectResidualEvent += StopChanged;
            Health.EffectRemovedEvent += StopChanged;
            Health.EffectUpdatedEvent += StopChanged;
            Health.EffectAddedEvent += StopChanged;
            Health.EffectStartedEvent += StopChanged;
        }
        private void StopChanged(EFT.HealthSystem.IHealthEffect effect)
        {
            if (!(effect is EFT.HealthSystem.IBleeding)) return;
            EffectsFrame = -1; _dirty = true;
            if (!effect.Active && (uint)effect.BodyPart < 7)
            {
                Wound wound = Wounds[(int)effect.BodyPart];
                if (!ReferenceEquals(wound.Effect, effect)) return;
                Stop(Player, effect.BodyPart, wound);
            }
        }
        internal void Dispose()
        {
            Health.EffectResidualEvent -= StopChanged; Health.EffectRemovedEvent -= StopChanged; Health.EffectUpdatedEvent -= StopChanged;
            Health.EffectAddedEvent -= StopChanged; Health.EffectStartedEvent -= StopChanged;
        }
    }
    private static readonly List<Actor> Actors = new List<Actor>();
    private static readonly Dictionary<Player, Actor> ActorsByPlayer = new Dictionary<Player, Actor>();
    private static float _nextScan;
    private static uint _generation;
    private static bool Owns(Player player) => !RagdollCoordinator.IsRemoteClient || player.IsYourPlayer;
    internal static bool IsLiving(BloodStreamTriggerKind kind) => kind == BloodStreamTriggerKind.HeavyWound || kind == BloodStreamTriggerKind.LightWound;
    internal static EFT.HealthSystem.IHealthEffect ActiveEffect(Player player, EBodyPart part, BloodStreamTriggerKind kind)
    {
        IHealthController health = player?.HealthController;
        // Native FindActiveEffect builds LINQ iterators on every call. The matched
        // controller families expose their existing list covariantly without copies.
        IReadOnlyList<EFT.HealthSystem.IHealthEffect> effects = Rupture.Health.NativeHealthEffects.TryGet(health);
        if (effects == null) return kind == BloodStreamTriggerKind.LightWound ? health?.FindActiveEffect<EFT.HealthSystem.ILightBleeding>(part) : health?.FindActiveEffect<EFT.HealthSystem.IHeavyBleeding>(part);
        for (int i = 0; i < effects.Count; i++)
        {
            EFT.HealthSystem.IHealthEffect effect = effects[i];
            if (effect.BodyPart == part && effect.Active && (kind == BloodStreamTriggerKind.LightWound ? effect is EFT.HealthSystem.ILightBleeding : effect is EFT.HealthSystem.IHeavyBleeding)) return effect;
        }
        return null;
    }
    internal static EFT.HealthSystem.IHealthEffect TrackedActiveEffect(Player player, EBodyPart part, BloodStreamTriggerKind kind)
    {
        if ((uint)part < 7 && player != null && ActorsByPlayer.TryGetValue(player, out Actor actor) && actor.Health == player.HealthController)
        {
            if (actor.EffectsFrame != Time.frameCount && !actor.CaptureEffects()) return ActiveEffect(player, part, kind);
            EFT.HealthSystem.IHealthEffect effect = kind == BloodStreamTriggerKind.LightWound ? actor.Light[(int)part] : actor.Heavy[(int)part];
            // Stop/updated native events invalidate the snapshot. Also read
            // Active again so a paused cached effect cannot prolong a stream.
            if (effect == null || effect.Active) return effect;
        }
        return ActiveEffect(player, part, kind);
    }
    private static Actor Get(Player player)
    {
        if (ActorsByPlayer.TryGetValue(player, out Actor actor)) return actor;
        actor = new Actor(player); Actors.Add(actor); ActorsByPlayer.Add(player, actor); return actor;
    }
    internal static void Capture(Player player, EFT.Ballistics.DamageInfo damage, EBodyPart part, EBodyPartColliderType collider)
    {
        if ((int)part >= 7 || !Owns(player)) return;
        Wound wound = Get(player).Wounds[(int)part];
        // Severity replacement is a new wound at the new impact, but doesn't erase old paint.
        Stop(player, part, wound);
        wound.Anchor = PulsatingBloodStreamSystem.ResolveDamageAnchor(player, damage.HitCollider, collider, part, out wound.Collider);
        if (wound.Anchor == null) return;
        wound.Point = wound.Anchor.InverseTransformPoint(damage.HitPoint);
        wound.Direction = wound.Anchor.InverseTransformDirection(PulsatingBloodStreamSystem.ResolveEntryDirection(damage));
    }
    internal static void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (Singleton<GameWorld>.Instantiated && now >= _nextScan)
        {
            _nextScan = now + 0.5f;
            var players = Singleton<GameWorld>.Instance.AllAlivePlayersList;
            for (int i = 0; i < players.Count; i++) if (players[i]?.HealthController != null && Owns(players[i])) Get(players[i]);
        }
        for (int i = Actors.Count - 1; i >= 0; i--)
        {
            Actor actor = Actors[i]; Player player = actor.Player;
            bool downed = player != null && MultiplayerRuntime.Current.IsDowned(player) && !Impacts.ImpactStateService.IsTerminalDeath(player);
            if (player == null || player.HealthController != actor.Health || (!actor.Health.IsAlive && !downed) || !Singleton<GameWorld>.Instantiated)
            { if (player != null) PulsatingBloodStreamSystem.StopLivingStreams(player, true); actor.Dispose(); ActorsByPlayer.Remove(actor.Player); Actors.RemoveAt(i); continue; }
            if (downed)
            {
                if (!actor.Downed)
                {
                    for (int part = 0; part < 7; part++) Stop(player, (EBodyPart)part, actor.Wounds[part]);
                    actor.Downed = true;
                }
                continue;
            }
            if (actor.Downed) { actor.Downed = false; actor.InvalidateEffects(); }
            bool captured = actor.CaptureEffects();
            bool enabled = RuptureConfiguration.EnableBloodEffects?.Value == true;
            for (int part = 0; part < 7; part++)
            {
                Wound wound = actor.Wounds[part]; var bodyPart = (EBodyPart)part;
                EFT.HealthSystem.IHealthEffect effect = captured ? actor.Heavy[part] : ActiveEffect(player, bodyPart, BloodStreamTriggerKind.HeavyWound);
                BloodStreamTriggerKind kind = BloodStreamTriggerKind.HeavyWound;
                if (effect == null) { effect = captured ? actor.Light[part] : ActiveEffect(player, bodyPart, BloodStreamTriggerKind.LightWound); kind = BloodStreamTriggerKind.LightWound; }
                if (!enabled) effect = null;
                var change = BleedEmissionPolicy.Evaluate(effect, wound.Effect, (byte)kind, (byte)wound.Sent,
                    effect != null && ReferenceEquals(effect, wound.Effect) && kind == wound.Sent &&
                    now >= wound.RetryAt && PulsatingBloodStreamSystem.HasLivingWound(player, bodyPart, kind), now >= wound.RetryAt);
                if (change == BleedEmissionChange.None) continue;
                Stop(player, bodyPart, wound);
                if (change == BleedEmissionChange.Stop) continue;
                if (wound.Anchor == null)
                {
                    wound.Collider = (EBodyPartColliderType)255;
                    wound.Anchor = PulsatingBloodStreamSystem.ResolveDamageAnchor(player, null, wound.Collider, bodyPart, out wound.Collider);
                    if (wound.Anchor == null) continue;
                    Vector3 center = wound.Anchor.position;
                    var colliders = player.PlayerBones?.BodyPartColliders;
                    if (colliders != null) foreach (var candidate in colliders)
                        if (candidate != null && candidate.BodyPartType == bodyPart && candidate.Collider != null)
                        {
                            wound.Collider = candidate.BodyPartColliderType; wound.Anchor = candidate.Collider.transform;
                            center = candidate.Collider.ClosestPoint(candidate.Collider.bounds.center + player.Transform.Original.forward * 2f); break;
                        }
                    wound.Point = wound.Anchor.InverseTransformPoint(center);
                    wound.Direction = wound.Anchor.InverseTransformDirection(player.Transform.Original.forward);
                    RupturePlugin.Log.LogInfo($"Bleed anchor fallback: player={player.Id}, part={bodyPart}.");
                }
                wound.Effect = effect; wound.Sent = kind;
                wound.RetryAt = now + 1f;
                PulsatingBloodStreamSystem.PublishLivingWound(player, bodyPart, wound.Collider, wound.Point, wound.Direction, kind, ++_generation);
            }
        }
    }
    private static void Stop(Player player, EBodyPart part, Wound wound)
    {
        if (wound.Sent != 0) PulsatingBloodStreamSystem.StopLivingWound(player, part);
        wound.Sent = 0; wound.Effect = null;
    }
    internal static void Reset()
    { foreach (var actor in Actors) actor.Dispose(); Actors.Clear(); ActorsByPlayer.Clear(); _nextScan = 0; _generation = 0; }
}
