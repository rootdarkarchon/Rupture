using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.Limbs
{
internal static class BodyDecalRendering
{
    private static Mesh _scratch;
    private static int _failures;
    private static readonly Dictionary<int, bool> DecalShaders = new Dictionary<int, bool>();
    internal static Action<string> ReportFailure;

    internal static bool IsAlive(Renderer renderer) => renderer != null;

    internal static bool CanPaint(Renderer renderer)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;
        if (renderer is SkinnedMeshRenderer skin)
        {
            if (skin.sharedMesh == null) return false;
        }
        else if (!renderer.isVisible) return false;
        Material material = renderer.sharedMaterial;
        return material?.shader != null && IsDecalShader(material.shader);
    }

    // Invoked only from EFT's character texture painter. A culled skin may not
    // have a current GPU skinning buffer. Bake its actual pose and UVs for this
    // projection without changing its visibility, LOD, materials or bones.
    internal static void DrawRenderer(CommandBuffer buffer, Renderer renderer, Material material)
    {
        if (!IsAlive(renderer)) return;
        if (renderer is SkinnedMeshRenderer skin && !skin.isVisible && skin.sharedMesh != null)
        {
            try
            {
                if (_scratch == null)
                {
                    _scratch = new Mesh { name = "RPT offscreen body decal projection", hideFlags = HideFlags.HideAndDontSave };
                    _scratch.MarkDynamic();
                }
                skin.BakeMesh(_scratch, false);
                // BakeMesh includes the skeleton/renderer scale. Applying the
                // full localToWorld matrix here would scale it a second time.
                buffer.DrawMesh(_scratch, Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one), material, 0, -1);
                return;
            }
            catch (Exception exception)
            {
                if (_failures++ < 5) ReportFailure?.Invoke("Offscreen body decal projection fell back to EFT: " + exception.Message);
            }
        }
        buffer.DrawRenderer(renderer, material);
    }

    internal static void Reset()
    {
        if (_scratch != null) UnityEngine.Object.Destroy(_scratch);
        _scratch = null; _failures = 0;
        DecalShaders.Clear();
    }

    /// <summary>Whether the shader is an EFT decal shader; <c>Shader.name</c> allocates, so the answer is cached per instance.</summary>
    private static bool IsDecalShader(Shader shader)
    {
        int id = shader.GetInstanceID();
        if (!DecalShaders.TryGetValue(id, out bool isDecal))
        {
            isDecal = shader.name.IndexOf("_Decal", StringComparison.Ordinal) >= 0;
            DecalShaders.Add(id, isDecal);
        }
        return isDecal;
    }
}
}
