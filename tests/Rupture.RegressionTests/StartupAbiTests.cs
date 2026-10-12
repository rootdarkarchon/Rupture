using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StartupAbiTests
{
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static IEnumerable<TypeDefinition> Types(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(Types));

    internal static void Run(Assembly plugin)
    {
        using var rupture = AssemblyDefinition.ReadAssembly(plugin.Location);
        using var game = AssemblyDefinition.ReadAssembly(typeof(EFT.Ballistics.Shot).Assembly.Location);
        var corpseCollisionMode = game.MainModule.GetType("EFT.Interactive.Corpse")
            .Methods.Single(m => m.Name == "get_CollisionDetectionMode");
        Check(corpseCollisionMode.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_0),
            "installed EFT authority corpse uses discrete collision detection before external handoff");
        var runtimeTypes = rupture.MainModule.Types.SelectMany(Types).ToArray();
        var instructions = runtimeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).ToArray();
        Check(!instructions.Any(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference field &&
            field.DeclaringType.FullName == "EFT.Ballistics.BallisticsCalculatorConstants" && field.Name == "HitMask"),
            "Rupture leaves the native projectile hit mask unchanged");
        Check(!instructions.Any(i => i.Operand is MethodReference method &&
            method.DeclaringType.FullName == "DeferredDecals.DeferredDecalRenderer" &&
            new[] { "Clear", "SetMaxStaticDecals", "SetMaxDynamicDecals" }.Contains(method.Name)),
            "Rupture never clears or resizes an allocated native decal renderer");

        var nativeMask = game.MainModule.GetType("EFT.Ballistics.BallisticsCalculatorConstants")
            .Methods.Single(m => m.Name == ".cctor");
        string[] collisionLayers = { "Water", "Terrain", "HighPolyCollider", "TransparentCollider", "Deadbody", "HitCollider" };
        Check(nativeMask.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr)
            .Take(6).Select(i => (string)i.Operand).SequenceEqual(collisionLayers),
            "installed EFT already declares every layer from the removed override");

        var nativeRenderer = game.MainModule.GetType("DeferredDecals.DeferredDecalRenderer");
        var awake = nativeRenderer.Methods.Single(m => m.Name == "Awake");
        var patch = rupture.MainModule.GetType("Rupture.Dismemberment.Patches.DecalCapacityPatch");
        var target = patch.Methods.Single(m => m.Name == "GetTargetMethod");
        Check(target.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldtoken &&
            i.Operand is TypeReference type && type.FullName == nativeRenderer.FullName) &&
            target.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Awake"),
            "decal capacity patch targets native allocation entry");
        var prefix = patch.Methods.Single(m => m.Name == "Prefix");
        Check(prefix.ReturnType.FullName == "System.Void" && prefix.CustomAttributes.Any(a => a.AttributeType.Name == "PatchPrefixAttribute"),
            "decal capacity prefix always permits EFT initialization");
        foreach (var parameter in prefix.Parameters)
        {
            string fieldName = parameter.Name.Substring(3);
            var field = nativeRenderer.Fields.SingleOrDefault(f => f.Name == fieldName);
            Check(parameter.Name.StartsWith("___") && parameter.ParameterType.FullName == "System.Int32&" &&
                field != null && !field.IsStatic && field.FieldType.FullName == "System.Int32" &&
                awake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Name == fieldName),
                "Harmony capacity field exists and is consumed by native Awake: " + fieldName);
        }
        Check(awake.Body.Instructions.Any(i => i.OpCode == OpCodes.Newarr &&
            i.Operand is TypeReference type && type.FullName == "UnityEngine.BoundingSphere"),
            "native Awake allocates dynamic decal culling storage");

        var nativeMesh = nativeRenderer.NestedTypes.Single(t => t.Name == "ManagedMesh");
        var meshConstructor = nativeMesh.Methods.Single(m => m.IsConstructor && !m.IsStatic);
        Check(meshConstructor.Parameters.Count == 1 && meshConstructor.Parameters[0].ParameterType.FullName == "System.Int32" &&
            meshConstructor.Body.Instructions.Count(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference method &&
                method.DeclaringType.FullName == "UnityEngine.Mesh") == 1,
            "installed decal constructor matches the wide-index allocation hook");
        var indexPatch = rupture.MainModule.GetType("Rupture.Dismemberment.Patches.DecalMeshIndexFormatPatch");
        Check(indexPatch.Methods.Single(m => m.Name == "CreateMesh").Body.Instructions.Any(i =>
            i.Operand is MethodReference method && method.FullName == "System.Void UnityEngine.Mesh::set_indexFormat(UnityEngine.Rendering.IndexFormat)"),
            "decal factory selects index width before native triangle assignment");
        var nativeRagdoll = game.MainModule.GetType("EFT.Interactive.CorpseRagdoll");
        Check(nativeRagdoll.Methods.Any(m => m.Name == "ClearWeapon" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Void") &&
            nativeRagdoll.Methods.Any(m => m.Name == "get_WeaponRigidbody" && m.ReturnType.FullName == "UnityEngine.Rigidbody"),
            "installed weapon-clear hook and defensive ownership lookup are available");
        var releasePatch = rupture.MainModule.GetType("Rupture.Ragdolls.Patches.CorpseWeaponReleasePatch");
        Check(releasePatch.Methods.Single(m => m.Name == "Prefix").CustomAttributes.Any(a => a.AttributeType.Name == "PatchPrefixAttribute"),
            "weapon binding is released before native destruction and pooling");

        var startup = rupture.MainModule.GetType("Rupture.RupturePlugin").Methods.Single(m => m.Name == "Awake");
        var startupCalls = startup.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        int bindCapacity = Array.FindIndex(startupCalls, m => m.Name == "Initialize" && m.DeclaringType.FullName == "Rupture.RuptureConfiguration");
        int installCapacity = Array.FindIndex(startupCalls, m => m.Name == ".ctor" && m.DeclaringType.FullName == patch.FullName);
        Check(bindCapacity >= 0 && installCapacity > bindCapacity, "decal configuration is bound before the prefix is installed");
        Check(bindCapacity < Array.FindIndex(startupCalls, m => m.Name == "Install" && m.DeclaringType.Name == "ImpactHooks"),
            "configuration is initialized before impact hooks can consume it");
        var configType = rupture.MainModule.GetType("Rupture.RuptureConfiguration");
        Check(configType.IsAbstract && configType.IsSealed && configType.Properties.Count == 31 &&
            configType.Properties.All(p => p.GetMethod.IsStatic && p.PropertyType.Name.StartsWith("ConfigEntry")) &&
            !rupture.MainModule.GetType("Rupture.RupturePlugin").Properties.Any(p => p.PropertyType.Name.StartsWith("ConfigEntry")),
            "all thirty-one configuration entries belong to the static configuration class");
        Check(rupture.Name.Version == new Version(1, 2, 1, 0), "SDK-generated assembly version preserves package identity");
    }
}
