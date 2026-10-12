using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;

internal static class ClothingLoaderAbiTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    internal static void Run(Assembly rupture)
    {
        var original = typeof(Diz.Resources.EasyBundle).GetMethod("LoadingCoroutine", Type.EmptyTypes);
        var state = original.GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType;
        using var game = AssemblyDefinition.ReadAssembly(typeof(Diz.Resources.EasyBundle).Assembly.Location);
        var body = game.MainModule.Types.SelectMany(t => t.NestedTypes).Single(t => t.FullName.Replace('/', '+') == state.FullName)
            .Methods.Single(m => m.Name == "MoveNext");
        var calls = body.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand).ToArray();
        Check(calls.Count(m => m.DeclaringType.FullName == "UnityEngine.AssetBundle" && m.Name == "LoadFromFileAsync" && m.Parameters.Count == 1) == 1,
            "installed EFT async loader has exactly the supported file-load call");
        Check(calls.Any(m => m.Name == "Invoke" && m.DeclaringType.FullName.StartsWith("System.Func`2")), "native bundle validation callback is retained in the state machine");
        Check(typeof(EFT.CustomizationSolver).GetProperty("ClothingTemplates") != null && typeof(EFT.Customization.CustomizationClothing).GetField("BodyPart") != null &&
              typeof(EFT.Customization.CustomizationClothing).GetField("Prefab") != null, "runtime clothing catalog ABI includes part and prefab references");
        using var binary = AssemblyDefinition.ReadAssembly(rupture.Location);
        var loader = binary.MainModule.GetType("Rupture.Clothing.ClothingBundleLoader");
        Check(loader != null, "clothing loader is present");
        var schedule = typeof(Diz.DependencyManager.DependencyGraph<Diz.Resources.IEasyBundle>).GetMethod("ProcessPendingLoads");
        Check(schedule != null && schedule.IsStatic && schedule.GetCustomAttribute<AsyncStateMachineAttribute>() == null &&
            schedule.GetParameters().Single().ParameterType == typeof(System.Collections.Generic.List<Diz.DependencyManager.Node<Diz.Resources.IEasyBundle>>),
            "pending bundle scheduler has the supported synchronous ABI");
        Check(!loader.Fields.Any(f => f.Name == "_plan"), "runtime no longer creates a global clothing discovery task");
        Check(!loader.Methods.Any(m => m.Name == "NativeLoad" || m.Name == "BeforeLoad" || m.Name == "ReplaceFileLoad"),
            "clothing recovery leaves EFT async kickoff and MoveNext unpatched");
        Check(loader.Methods.Single(m => m.Name == "Register").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_bundleCheck" && i.OpCode.Code == Mono.Cecil.Cil.Code.Stfld),
            "clothing preparation uses native awaited validation callback");
        Check(binary.MainModule.GetType("Rupture.Dismemberment.Classes.Limbs.UnreadableLimbMeshes") == null, "mesh blacklist removed");
        var clothing = binary.MainModule.Types.Where(t => t.Namespace == "Rupture.Clothing").SelectMany(t => new[] { t }.Concat(t.NestedTypes));
        Check(!clothing.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i =>
            i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IO.File" &&
            (m.Name.StartsWith("Write") || m.Name == "Create" || m.Name == "Delete")), "runtime clothing recovery does not write bundle files");
    }
}
