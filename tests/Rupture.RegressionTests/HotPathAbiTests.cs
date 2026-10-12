using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class HotPathAbiTests
{
    internal static void Run()
    {
        using var game = AssemblyDefinition.ReadAssembly(typeof(EFT.GameWorld).Assembly.Location);
        var data = game.MainModule.GetType("DeferredDecals.DeferredDecalRenderer").NestedTypes.Single(t => t.Name == "ManagedMesh");
        var insert = data.Methods.Single(m => m.Name == "PasteProjectorIntoMiddle");
        var upload = data.Methods.Single(m => m.Name == "UpdateInnerMesh");
        if (!data.IsNestedPublic || !insert.IsPublic || !upload.IsPublic || upload.Parameters.Count != 0 ||
            insert.Parameters.Count == 0 || insert.Parameters[0].ParameterType.FullName != "System.Int32" ||
            insert.Body.Instructions.Count(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                i.Operand is MethodReference m && m.FullName == upload.FullName) != 1)
            throw new Exception("Installed decal batching ABI differs from the scoped upload hook");
        foreach (string field in new[] { "Mesh", "Vertices", "Normals", "Tangents", "Uvs0", "Uvs1", "Uvs2" })
            if (!data.Fields.Any(f => f.Name == field && f.IsPublic && !f.IsStatic))
                throw new Exception("Installed native decal field unavailable: " + field);
        string[] channels = { "set_vertices", "set_normals", "set_tangents", "SetUVs" };
        foreach (string channel in channels)
            if (!upload.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "UnityEngine.Mesh" && m.Name == channel))
                throw new Exception("Installed decal upload no longer writes expected channel: " + channel);
        Console.WriteLine("PASS: installed native decal insertion and complete mesh upload ABI.");
    }
}
