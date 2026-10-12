using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Mono.Cecil;

internal static class LimbFailureRetentionTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string description)
    {
        if (!value) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }

    internal static void Run(Assembly assembly)
    {
        using var binary = AssemblyDefinition.ReadAssembly(assembly.Location);
        var limbs = binary.MainModule.GetType("Rupture.Dismemberment.Classes.Limbs.LimbDismembermentSystem");
        var failure = limbs.Methods.Single(m => m.Name == "ObserverVisualFailure");
        Check(!failure.Body.Instructions.Any(i => i.Operand is MethodReference m && (m.Name == "DisableLimbColliders" || m.Name == "Add")),
            "failed replicated visual neither removes colliders nor records a completed sever");
        var context = binary.MainModule.GetType("Rupture.Ragdolls.Classes.CorpseLimbContext");
        Check(!context.Fields.Any(f => f.FieldType.FullName == "EFT.Player" || f.FieldType.Name == "PlayerBones"),
            "retained corpse anatomy has no disposable player owner");
    }
}
