using System;
using System.Reflection;
using DeferredDecals;
using SPT.Reflection.Patching;

namespace Rupture.Dismemberment.Patches;

// GameWorld creates a fresh effects prefab. Configure its serialized capacities
// before EFT allocates meshes, dynamic decals and their culling-sphere array.
internal sealed class DecalCapacityPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(DeferredDecalRenderer).GetMethod(nameof(DeferredDecalRenderer.Awake));

    [PatchPrefix]
    private static void Prefix(ref int ____maxDecals, ref int ____maxDynamicDecals)
    {
        if (MultiplayerRuntime.Current.IsHeadless || RuptureConfiguration.EnableBloodEffects?.Value != true)
            return;

        // Both native emit paths use their capacity as a modulo divisor.
        int capacity = Math.Max(1, RuptureConfiguration.MaxDecals.Value);
        ____maxDecals = capacity;
        ____maxDynamicDecals = capacity;
    }
}
