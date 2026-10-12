using System;
using System.Linq;
using EFT;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

// Retain the corpse anatomy, not the disposable Player/PlayerBones owner.
internal sealed class CorpseLimbContext
{
    private readonly Transform[] _segments;

    private CorpseLimbContext(Transform[] segments)
    {
        _segments = segments;
    }

    internal static CorpseLimbContext Capture(Player player, PlayerBody body = null)
    {
        body = body != null ? body : player?.PlayerBody;
        Transform root = body?.SkeletonRootJoint?.transform;
        if (root == null) return null;
        PlayerBones bones = body.PlayerBones ?? player?.PlayerBones;
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        Transform Find(string name) => all.FirstOrDefault(t => string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase));
        Transform Arm(Transform[] values, int side, string fallback) =>
            values != null && values.Length > side && values[side] != null ? values[side] : Find(fallback);
        return new CorpseLimbContext(new[]
            {
                Arm(bones?.Upperarms, 0, "Base HumanLUpperarm"), Arm(bones?.Forearms, 0, "Base HumanLForearm1"), bones?.LeftPalm ?? Find("Base HumanLPalm"),
                Arm(bones?.Upperarms, 1, "Base HumanRUpperarm"), Arm(bones?.Forearms, 1, "Base HumanRForearm1"), bones?.RightPalm ?? Find("Base HumanRPalm"),
                bones?.LeftThigh1?.Original ?? Find("Base HumanLThigh1"), Find("Base HumanLCalf"), Find("Base HumanLFoot"),
                bones?.RightThigh1?.Original ?? Find("Base HumanRThigh1"), Find("Base HumanRCalf"), Find("Base HumanRFoot")
            });
    }

    internal bool TryGet(EBodyPart part, out Transform start, out Transform joint, out Transform end)
    {
        int index = part == EBodyPart.LeftArm ? 0 : part == EBodyPart.RightArm ? 3 :
            part == EBodyPart.LeftLeg ? 6 : part == EBodyPart.RightLeg ? 9 : -1;
        start = index < 0 ? null : _segments[index];
        joint = index < 0 ? null : _segments[index + 1];
        end = index < 0 ? null : _segments[index + 2];
        return start != null && joint != null && end != null;
    }

    internal string Describe(EBodyPart part)
    {
        TryGet(part, out Transform start, out Transform joint, out Transform end);
        return $"retained bones: start={start != null}, joint={joint != null}, end={end != null}";
    }
}
