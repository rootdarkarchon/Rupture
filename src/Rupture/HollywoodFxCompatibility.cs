using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace Rupture;

internal static class HollywoodFxCompatibility
{
    private const string LegacyGuid = "com.servph.VisceralCombat";
    private const string RuptureGuid = "com.rootdarkarchon.rupture";

    internal static void Install()
    {
        if (!Chainloader.PluginInfos.TryGetValue("com.janky.hollywoodfx", out PluginInfo plugin)) return;
        try
        {
            MethodInfo load = AccessTools.Method(plugin.Instance.GetType(), "DelayedLoad");
            Type iterator = load?.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;
            MethodInfo moveNext = iterator == null ? null : AccessTools.Method(iterator, "MoveNext");
            if (moveNext == null) throw new MissingMethodException("HollywoodFX delayed compatibility check is unavailable.");
            new Harmony("com.rootdarkarchon.rupture.hollywoodfx").Patch(moveNext,
                transpiler: new HarmonyMethod(typeof(HollywoodFxCompatibility), nameof(RecognizeRupture)));
            RupturePlugin.Log.Initialization("HollywoodFX compatibility: Rupture now activates HFX's existing VC exclusions for ragdolls, death impulses/gore and shell physics.");
        }
        catch (Exception exception)
        {
            RupturePlugin.Log.InitializationWarning($"HollywoodFX compatibility could not be installed; overlapping death impulses may remain active: {exception.Message}");
        }
    }

    internal static bool HasRagdollOwner(Dictionary<string, PluginInfo> plugins, string guid) =>
        plugins.ContainsKey(guid) || (guid == LegacyGuid && plugins.ContainsKey(RuptureGuid));

    internal static IEnumerable<CodeInstruction> RecognizeRupture(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int matches = 0;
        for (int index = 1; index < code.Count; index++)
        {
            if (code[index - 1].opcode != OpCodes.Ldstr || !Equals(code[index - 1].operand, LegacyGuid) ||
                !(code[index].operand is MethodInfo method) || method.Name != "ContainsKey" ||
                method.DeclaringType != typeof(Dictionary<string, PluginInfo>)) continue;
            code[index].opcode = OpCodes.Call;
            code[index].operand = typeof(HollywoodFxCompatibility).GetMethod(nameof(HasRagdollOwner),
                BindingFlags.Static | BindingFlags.NonPublic);
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException($"Expected one HollywoodFX VC check, found {matches}.");
        return code;
    }
}
