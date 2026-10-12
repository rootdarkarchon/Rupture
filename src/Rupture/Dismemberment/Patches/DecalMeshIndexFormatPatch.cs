using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DeferredDecals;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Patches;

// Choose the format before native triangle assignment: 4096 projectors need
// 98304 vertices. Changing the format after assignment clears the index buffer.
internal sealed class DecalMeshIndexFormatPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        typeof(DeferredDecals.DeferredDecalRenderer.ManagedMesh).GetConstructor(new[] { typeof(int) });

    [PatchTranspiler]
    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        ConstructorInfo constructor = typeof(Mesh).GetConstructor(Type.EmptyTypes);
        MethodInfo factory = AccessTools.DeclaredMethod(typeof(DecalMeshIndexFormatPatch), nameof(CreateMesh));
        int matches = 0;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Newobj || !Equals(code[i].operand, constructor)) continue;
            code[i] = new CodeInstruction(code[i]) { opcode = OpCodes.Ldarg_1, operand = null };
            code.Insert(++i, new CodeInstruction(OpCodes.Call, factory));
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException("Expected one native decal mesh allocation, found " + matches);
        return code;
    }

    private static Mesh CreateMesh(int nDecals) => new Mesh
    {
        indexFormat = nDecals > ushort.MaxValue / 24 ? IndexFormat.UInt32 : IndexFormat.UInt16
    };
}
