using System;
using System.Reflection;
using DeferredDecals;
using SPT.Reflection.Patching;

namespace Rupture.Dismemberment.Patches;

// GameWorld creates a fresh effects prefab. Configure the static pool capacity
// before EFT allocates its managed meshes. Rupture blood emits only through the
// static path (EmitBloodOnEnvironment/EmitBleeding), so the dynamic impact pool
// keeps its serialized size: its per-decal Update, culling and draw costs scale
// linearly with capacity once the ring fills.
internal sealed class DecalCapacityPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(DeferredDecalRenderer).GetMethod(nameof(DeferredDecalRenderer.Awake));

    [PatchPrefix]
    private static void Prefix(ref int ____maxDecals)
    {
        if (MultiplayerRuntime.Current.IsHeadless || RuptureConfiguration.EnableBloodEffects?.Value != true)
            return;

        // The native emit path uses this capacity as a modulo divisor.
        ____maxDecals = Math.Max(1, RuptureConfiguration.MaxDecals.Value);
    }
}
