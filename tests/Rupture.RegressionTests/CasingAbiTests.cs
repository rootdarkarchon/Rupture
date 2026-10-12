using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using EFT;
using EFT.AssetsManager;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class CasingAbiTests
{
    internal static void Run(Assembly plugin)
    {
        void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); }
        Check(typeof(AmmoPoolObject).GetMethod("SetUsed", new[] { typeof(bool) }) != null &&
            typeof(GameWorld).GetMethod("SpawnShellInTheWorld", new[] { typeof(AmmoPoolObject).MakeByRefType() }) != null &&
            typeof(AmmoPoolObject).GetMethod("StartAutoDestroyCountDown", new[] { typeof(float) }) != null,
            "installed spent-casing lifecycle ABI matches hooks");
        Check(typeof(GameWorld).GetField("_ammoShells", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType == typeof(System.Collections.Generic.List<AmmoPoolObject>) &&
            typeof(Shell).GetField("_caliber", BindingFlags.Public | BindingFlags.Instance)?.FieldType == typeof(ECaliber),
            "installed native cleanup collection and caliber ABI match");
        Check(typeof(EFT.Ballistics.Shot).GetMethod("CheckCollision", new[] { typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3) })?.ReturnType == typeof(bool),
            "installed ballistic trace ABI supplies clipped local segments");
        using var binary = AssemblyDefinition.ReadAssembly(plugin.Location);
        var awake = binary.MainModule.GetType("Rupture.RupturePlugin").Methods.Single(m => m.Name == "Awake");
        var install = awake.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Rupture.Casings.CasingHooks" && m.Name == "Install");
        int index = awake.Body.Instructions.IndexOf(install);
        var guard = awake.Body.Instructions[index - 1];
        Check((guard.OpCode.Code == Code.Brtrue || guard.OpCode.Code == Code.Brtrue_S) && guard.Operand is Instruction skip &&
            awake.Body.Instructions.IndexOf(skip) > index && awake.Body.Instructions[index - 2].Operand is MethodReference role && role.Name == "get_IsHeadless",
            "compiled headless branch skips casing registration before any service construction");
        var casingHooks = binary.MainModule.GetType("Rupture.Casings.CasingHooks");
        Check(!casingHooks.Methods.Any(m => m.Name == ".cctor"), "casing hook type has no eager service allocation");
        var config = plugin.GetType("Rupture.RuptureConfiguration");
        object entry = config.GetProperty("MaximumCasings").GetValue(null);
        Check((int)entry.GetType().GetProperty("Value").GetValue(entry) == 4096, "local casing capacity defaults to 4096");
        var fields = new[] { ("CasingDescriptor", 112), ("CasingImpact", 56), ("CasingState", 56), ("CasingContact", 40) };
        foreach (var (name, expected) in fields)
        {
            Type type = plugin.GetType("Rupture.Ragdolls.External." + name, true);
            Check(Marshal.SizeOf(type) == expected, "casing native layout size: " + name);
            Check(type.GetFields().All(f => !typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType)), "casing wire data contains no Unity object: " + name);
        }
        using var game = AssemblyDefinition.ReadAssembly(typeof(GameWorld).Assembly.Location);
        var findVisuals = game.MainModule.GetType("TransformTools").Methods.Single(m => m.Name == "FindTransformRecursiveContains");
        Check(findVisuals.Body.Instructions.Any(i => i.Operand is MethodReference method &&
            method.DeclaringType.FullName == "System.String" && method.Name == "Contains"),
            "installed ammo visual discovery uses substring names, including shell LOD variants");
        var countdown = game.MainModule.GetType("EFT.AssetsManager.AmmoPoolObject").Methods.Single(m => m.Name == "StartAutoDestroyCountDown");
        Check(countdown.Body.Instructions.Any(i => i.Operand is MethodReference method && method.Name == "set_enabled"),
            "native countdown still uses the guarded component lifecycle");
    }
}
