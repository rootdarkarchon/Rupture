using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Dismemberment.Patches;

internal static class BodyDecalLodPatch
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.bodydecals");
    private static MethodInfo Eligibility => typeof(TextureDecalsPainter).GetMethods()
        .Single(m => m.ReturnType == typeof(bool) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Renderer));
    private static readonly MethodInfo NativeDraw = typeof(CommandBuffer).GetMethod(nameof(CommandBuffer.DrawRenderer),
        new[] { typeof(Renderer), typeof(Material) });

    internal static void Install()
    {
        try
        {
            var draw = typeof(TextureDecalsPainter).GetMethod(nameof(TextureDecalsPainter.DrawDecal), new[] { typeof(Renderer) });
            // Resolve admission first, then validate/install the draw hook before
            // admitting hidden LODs. A failure removes both hooks.
            MethodInfo eligibility = Eligibility;
            Patches.Patch(draw, prefix: new HarmonyMethod(typeof(BodyDecalLodPatch), nameof(AdmitDraw)),
                transpiler: new HarmonyMethod(typeof(BodyDecalLodPatch), nameof(Rewrite)));
            Patches.Patch(eligibility, prefix: new HarmonyMethod(typeof(BodyDecalLodPatch), nameof(AdmitHiddenSkin)));
            BodyDecalRendering.ReportFailure = message => RupturePlugin.Log.LogWarning(message);
        }
        catch (Exception exception)
        {
            Patches.UnpatchSelf();
            RupturePlugin.Log.InitializationWarning("Body decal LOD integration unavailable; EFT painter retained: " + exception.Message);
        }
    }

    private static bool AdmitHiddenSkin(Renderer objRenderer, ref bool __result)
    {
        if (objRenderer == null) { __result = false; return false; }
        if (!(objRenderer is SkinnedMeshRenderer) || objRenderer.isVisible) return true;
        __result = BodyDecalRendering.CanPaint(objRenderer);
        return false;
    }

    // Update removes the queued item after DrawDecal returns. Guard before EFT
    // accesses materials, including renderers with no previous decal texture.
    private static bool AdmitDraw(Renderer __0) => BodyDecalRendering.IsAlive(__0);

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var result = instructions.ToList();
        var matches = result.Where(i => i.Calls(NativeDraw)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one native character decal draw call.");
        matches[0].opcode = OpCodes.Call;
        matches[0].operand = typeof(BodyDecalRendering).GetMethod(nameof(BodyDecalRendering.DrawRenderer), BindingFlags.Static | BindingFlags.NonPublic);
        return result;
    }

    internal static void Shutdown()
    {
        Patches.UnpatchSelf();
        BodyDecalRendering.Reset();
        BodyDecalRendering.ReportFailure = null;
    }
}
