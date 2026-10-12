using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;

internal static class HollywoodFxCompatibilityTests
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Type type = assembly.GetType("Rupture.HollywoodFxCompatibility", true);
        MethodInfo detect = type.GetMethod("HasRagdollOwner", flags);
        var plugins = new Dictionary<string, PluginInfo>();
        bool Has(string guid) => (bool)detect.Invoke(null, new object[] { plugins, guid });
        const string legacy = "com.servph.VisceralCombat";
        if (Has(legacy)) throw new Exception("Empty plugin set enables HFX compatibility.");
        plugins.Add("com.rootdarkarchon.rupture", null);
        if (!Has(legacy) || Has("unrelated")) throw new Exception("Rupture compatibility changes unrelated plugin detection.");
        plugins.Clear(); plugins.Add(legacy, null);
        if (!Has(legacy)) throw new Exception("Legacy VC detection was lost.");
        MethodInfo contains = typeof(Dictionary<string, PluginInfo>).GetMethod("ContainsKey");
        var input = new[] { new CodeInstruction(OpCodes.Ldstr, "unrelated"), new CodeInstruction(OpCodes.Callvirt, contains),
            new CodeInstruction(OpCodes.Ldstr, legacy), new CodeInstruction(OpCodes.Callvirt, contains) };
        var rewritten = ((IEnumerable<CodeInstruction>)type.GetMethod("RecognizeRupture", flags)
            .Invoke(null, new object[] { input })).ToArray();
        if (rewritten[1].opcode != OpCodes.Callvirt || rewritten[3].opcode != OpCodes.Call ||
            !Equals(rewritten[3].operand, detect)) throw new Exception("HFX transpiler did not isolate the legacy detection call.");
        try
        {
            type.GetMethod("RecognizeRupture", flags).Invoke(null, new object[] { Array.Empty<CodeInstruction>() });
            throw new Exception("HFX missing detection was silently accepted.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { }
        Console.WriteLine("PASS: HFX recognizes Rupture and legacy VC, preserves unrelated checks, and rejects incompatible detection IL");
    }
}
