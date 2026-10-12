using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

internal static class BodyDecalLodTests
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        Type patch = assembly.GetType("Rupture.Dismemberment.Patches.BodyDecalLodPatch", true);
        var eligibility = (MethodInfo)patch.GetProperty("Eligibility", flags).GetValue(null);
        var prefix = patch.GetMethod("AdmitHiddenSkin", flags);
        if (eligibility.ReturnType != typeof(bool) || eligibility.GetParameters().Single().Name != prefix.GetParameters()[0].Name)
            throw new Exception("Body decal eligibility prefix does not match the installed EFT ABI.");
        using var game = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(TextureDecalsPainter).Assembly.Location);
        var draw = game.MainModule.GetType("TextureDecalsPainter").Methods.Single(m => m.Name == "DrawDecal" && m.Parameters.Count == 1);
        var admission = patch.GetMethod("AdmitDraw", flags);
        if (admission?.ReturnType != typeof(bool) || admission.GetParameters().Single().Name != "__0" ||
            admission.GetParameters()[0].ParameterType.FullName != draw.Parameters[0].ParameterType.FullName)
            throw new Exception("Destroyed-renderer guard must run at the native DrawDecal entrance.");
        using var plugin = Mono.Cecil.AssemblyDefinition.ReadAssembly(assembly.Location);
        var patchType = plugin.MainModule.GetType(patch.FullName);
        if (!patchType.Methods.Single(m => m.Name == "Install").Body.Instructions.Any(i => Equals(i.Operand, "AdmitDraw")) ||
            !patchType.Methods.Single(m => m.Name == "AdmitDraw").Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference m && m.Name == "IsAlive"))
            throw new Exception("Native decal consumption is missing the renderer lifetime prefix.");
        var calls = draw.Body.Instructions.Select(i => i.Operand).OfType<Mono.Cecil.MethodReference>().ToArray();
        var native = typeof(CommandBuffer).GetMethod("DrawRenderer", new[] { typeof(Renderer), typeof(Material) });
        var nativeCalls = calls.Where(m => m.DeclaringType.FullName == typeof(CommandBuffer).FullName && m.Name == native.Name).ToArray();
        if (nativeCalls.Length != 1 || !nativeCalls[0].Parameters.Select(p => p.ParameterType.FullName)
            .SequenceEqual(native.GetParameters().Select(p => p.ParameterType.FullName)) ||
            Array.FindIndex(calls, m => m.Name == "ExecuteCommandBuffer") <= Array.IndexOf(calls, nativeCalls[0]))
            throw new Exception("Native decal draw ABI or immediate command execution changed: " + string.Join("; ", calls.Select(m => m.FullName)));
        var original = new[] { new CodeInstruction(OpCodes.Nop), new CodeInstruction(OpCodes.Callvirt, native), new CodeInstruction(OpCodes.Ret) };
        var snapshot = original.Select(i => (i.opcode, i.operand)).ToArray();
        var rewrite = patch.GetMethod("Rewrite", flags);
        var rewritten = ((IEnumerable<CodeInstruction>)rewrite.Invoke(null, new object[] { original })).ToArray();
        var changed = rewritten.Select((i, n) => (i, n)).Where(x => x.i.opcode != snapshot[x.n].opcode ||
            !Equals(x.i.operand, snapshot[x.n].operand)).ToArray();
        if (changed.Length != 1 || changed[0].i.opcode != OpCodes.Call ||
            !(changed[0].i.operand is MethodInfo helper) || helper.Name != "DrawRenderer" ||
            !helper.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(CommandBuffer), typeof(Renderer), typeof(Material) }))
            throw new Exception("Body decal rewrite changed native texture accumulation or has an incompatible stack signature.");
        foreach (int count in new[] { 0, 2 })
        {
            try
            {
                rewrite.Invoke(null, new object[] { Enumerable.Range(0, count).Select(_ => new CodeInstruction(OpCodes.Callvirt, native)) });
                throw new Exception("Ambiguous native decal draw was accepted.");
            }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
        }
        Console.WriteLine("PASS: body decal EFT ABI, isolated draw replacement, immediate scratch consumption, and incompatible-IL rejection");
    }
}
