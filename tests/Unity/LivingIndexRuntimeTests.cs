#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;
using Rupture;
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    // Methods and LivingStream are extracted verbatim from production by the runner.
    internal static partial class LivingIndexRuntimeTests
    {
        internal struct BloodStreamData { internal uint StreamId; internal string ProfileId; internal EBodyPart BodyPart; internal BloodStreamTriggerKind TriggerKind; }
        private static readonly EffectLifetimes<LivingStream> LivingStreams = new EffectLifetimes<LivingStream>();
        private static readonly Dictionary<(string, EBodyPart, BloodStreamTriggerKind), HashSet<uint>> LivingWounds = new Dictionary<(string, EBodyPart, BloodStreamTriggerKind), HashSet<uint>>();
        internal static void Run()
        {
            var player = new Player { ProfileId = "actor" };
            var packet = new BloodStreamData { StreamId = 1, ProfileId = "actor", BodyPart = EBodyPart.Chest, TriggerKind = BloodStreamTriggerKind.HeavyWound };
            TrackLivingStream(packet, null); TrackLivingStream(packet, player);
            if (!HasLivingWound(player, packet.BodyPart, packet.TriggerKind) || LivingWounds.Count != 1) throw new Exception("unresolved/repeated living start lost index");
            packet.StreamId = 2; TrackLivingStream(packet, player);
            LivingStreams.TryGet(1, out var first); UnindexLivingStream(first); LivingStreams.Remove(1); UnindexLivingStream(first);
            if (!HasLivingWound(player, packet.BodyPart, packet.TriggerKind)) throw new Exception("one stop removed a sibling stream");
            packet.BodyPart = EBodyPart.LeftArm; TrackLivingStream(packet, player);
            if (HasLivingWound(player, EBodyPart.Chest, packet.TriggerKind)) throw new Exception("replacement leaked old index");
            LivingStreams.StopWhere(s => true, UnindexLivingStream);
            if (LivingWounds.Count != 0) throw new Exception("remove-before-callback lifetime leaked index");
            LivingStreams.Clear(); LivingWounds.Clear();
            Debug.Log("PASS: living index replacement, duplicates, pending player, sibling stop and lifecycle removal");
        }
    }
}
#endif
