using System;
using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic.Operations;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

internal sealed class HeadgearOperationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(AbstractOperation).GetMethod(nameof(AbstractOperation.Execute), new[] { typeof(Callback) });
    [PatchPrefix]
    private static void Prefix(AbstractOperation __instance, ref Callback callback)
    {
        try { HeadgearDiagnostics.ObserveOperation(__instance, ref callback); }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
    [PatchFinalizer]
    private static void Finalizer(AbstractOperation __instance, Exception __exception)
    {
        if (__exception == null) return;
        try { HeadgearDiagnostics.OperationException(__instance, __exception); }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
}
