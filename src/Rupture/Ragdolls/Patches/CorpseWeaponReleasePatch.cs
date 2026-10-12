using System.Reflection;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

internal sealed class CorpseWeaponReleasePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(EFT.Interactive.CorpseRagdoll).GetMethod(nameof(EFT.Interactive.CorpseRagdoll.ClearWeapon));

    // Stop presenting the old world pose before EFT destroys/recycles its body.
    [PatchPrefix]
    private static void Prefix(EFT.Interactive.CorpseRagdoll __instance) => ShadowRagdollWorld.ReleaseWeapon(__instance);
}
