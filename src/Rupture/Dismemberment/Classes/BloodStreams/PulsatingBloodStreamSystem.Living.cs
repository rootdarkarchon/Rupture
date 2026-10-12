using System.Collections.Generic;
using EFT;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Ragdolls.Classes;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.BloodStreams;

internal static partial class PulsatingBloodStreamSystem
{
    private sealed class LivingStump
    {
        internal CorpseInteractionTarget Target;
        internal Transform Anchor;
        internal Vector3 Point, Axis;
        internal uint Sequence;
    }
    private static readonly Dictionary<(Player Player, EBodyPart Part), LivingStump> LivingStumps =
        new Dictionary<(Player, EBodyPart), LivingStump>();
    private static readonly List<(Player Player, EBodyPart Part)> RetiredStumps = new List<(Player, EBodyPart)>();
    private static void RegisterLivingStump(CorpseInteractionTarget target, Transform anchor, Vector3 point, Vector3 axis, EBodyPart part, uint sequence)
    {
        if (MultiplayerRuntime.Current.IsHeadless || target.Player == null || anchor == null) return;
        LivingStumps[(target.Player, part)] = new LivingStump { Target = target, Anchor = anchor,
            Point = anchor.InverseTransformPoint(point), Axis = anchor.InverseTransformDirection(axis), Sequence = sequence };
    }
    private static void RestoreLivingStumps()
    {
        RetiredStumps.Clear();
        foreach (var pair in LivingStumps)
        {
            LivingStump stump = pair.Value;
            if (stump.Anchor == null || !Health.BotSeverStateService.Untreated(pair.Key.Player, pair.Key.Part) ||
                pair.Key.Player?.HealthController?.IsAlive != true)
            { RetiredStumps.Add(pair.Key); continue; }
            bool active = false;
            foreach (StreamEmitter emitter in Emitters)
                if (emitter.Active && emitter.Player == pair.Key.Player && emitter.Descriptor.BodyPart == pair.Key.Part &&
                    emitter.Descriptor.TriggerKind == BloodStreamTriggerKind.LivingSeverStump) { active = true; break; }
            if (active) continue;
            StartLocalProceduralStream(stump.Target, stump.Anchor, stump.Anchor.TransformPoint(stump.Point),
                stump.Anchor.TransformDirection(stump.Axis), pair.Key.Part, RagdollCoordinator.NextNetworkEventSequence(),
                BloodStreamTriggerKind.LivingSeverStump, 0f, false, 0xB0D157u);
        }
        foreach (var key in RetiredStumps) LivingStumps.Remove(key);
    }
    internal static void ResetLivingStumps() { LivingStumps.Clear(); RetiredStumps.Clear(); }
}
