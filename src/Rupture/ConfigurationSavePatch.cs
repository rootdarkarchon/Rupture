using System;
using System.Reflection;
using BepInEx.Configuration;
using SPT.Reflection.Patching;

namespace Rupture;

internal sealed class ConfigurationSavePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => typeof(ConfigFile).GetMethod(nameof(ConfigFile.Save));
    [PatchPrefix]
    private static void Prefix(ConfigFile __instance, out bool __state) => __state = RuptureConfiguration.BeforeSave(__instance);
    [PatchFinalizer]
    private static Exception Finalizer(Exception __exception, bool __state)
    {
        RuptureConfiguration.AfterSave(__state);
        return __exception;
    }
}
