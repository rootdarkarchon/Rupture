using Comfort.Common;
using UnityEngine;

namespace Rupture;

/// <summary>
/// Single owner of the four EFT textures the wound surfaces sample (flesh and blood albedo/normal).
/// They are read from the instantiated <c>Systems.Effects.Effects</c> prefab, never found by scanning
/// every loaded <see cref="Texture2D"/>: <c>decal_flesh2</c>/<c>decal_flesh2_nrm</c> are the
/// <c>_MainTex</c>/<c>_BumpMap</c> of the <c>DecalSystem</c> "Decal Body" child material, and
/// <c>blood_splash_d</c>/<c>blood_splash_n</c> are those of <c>DeferredDecalRenderer._bleedingDecal.DecalMaterial</c>.
/// Resolution runs from <see cref="RaidWarmup"/> at raid start. Providers only read the cache.
/// </summary>
internal static class EftWoundTextures
{
    private const string FleshName = "decal_flesh2";
    private const string FleshNormalName = "decal_flesh2_nrm";
    private const string BloodName = "blood_splash_d";
    private const string BloodNormalName = "blood_splash_n";

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");

    internal static Texture2D Flesh { get; private set; }
    internal static Texture2D FleshNormal { get; private set; }
    internal static Texture2D Blood { get; private set; }
    internal static Texture2D BloodNormal { get; private set; }

    /// <summary>True once the warmup gave up on missing textures; providers then report unavailable or use generated fallbacks.</summary>
    internal static bool LookupExhausted { get; private set; }

    /// <summary>How the cache was filled, for the one warmup diagnostics line.</summary>
    internal static string Source { get; private set; } = "none";

    internal static bool BloodReady => Blood != null && BloodNormal != null;
    internal static bool Ready => BloodReady && Flesh != null && FleshNormal != null;

    /// <summary>
    /// Reads whatever the Effects prefab references. Returns false only when the Effects singleton does not exist yet,
    /// so the caller can wait; a partial result is read through <see cref="Ready"/>.
    /// </summary>
    internal static bool TryResolveFromEffects()
    {
        if (!Singleton<Systems.Effects.Effects>.Instantiated) return false;
        Systems.Effects.Effects effects = Singleton<Systems.Effects.Effects>.Instance;
        if (effects == null) return false;

        if (!BloodReady)
        {
            DeferredDecals.DeferredDecalRenderer decals = effects.DeferredDecals;
            DeferredDecals.DeferredDecalRenderer.SingleDecal bleeding = decals != null ? decals._bleedingDecal : null;
            Material material = bleeding != null ? bleeding.DecalMaterial : null;
            if (material != null)
            {
                if (Blood == null) Blood = ReadTexture(material, MainTexId);
                if (BloodNormal == null) BloodNormal = ReadTexture(material, BumpMapId);
            }
        }

        if (Flesh == null || FleshNormal == null)
        {
            global::DecalSystem body = effects.GetComponentInChildren<global::DecalSystem>(true);
            Renderer renderer = body != null ? body.GetComponent<Renderer>() : null;
            Material material = renderer != null ? renderer.sharedMaterial : null;
            if (material != null)
            {
                if (Flesh == null) Flesh = ReadTexture(material, MainTexId);
                if (FleshNormal == null) FleshNormal = ReadTexture(material, BumpMapId);
            }
        }

        if (Ready) Source = "effects-prefab";
        return true;
    }

    /// <summary>
    /// Last resort when the Effects references are incomplete: one pass over every loaded texture. Allocates a
    /// name string per visited texture and takes tens of milliseconds, so only <see cref="RaidWarmup"/> may call it,
    /// rate-limited and never on a kill or first-use path.
    /// </summary>
    internal static void ResolveFromScan()
    {
        foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
        {
            if (Ready) break;
            if (texture == null) continue;
            switch (texture.name)
            {
            case FleshName: if (Flesh == null) Flesh = texture; break;
            case FleshNormalName: if (FleshNormal == null) FleshNormal = texture; break;
            case BloodName: if (Blood == null) Blood = texture; break;
            case BloodNormalName: if (BloodNormal == null) BloodNormal = texture; break;
            }
        }
        if (Ready) Source = "scan";
    }

    internal static void MarkExhausted() => LookupExhausted = true;

    /// <summary>Per-raid reset: the Effects prefab, and so every texture it owns, is replaced with the next game world.</summary>
    internal static void Reset()
    {
        Flesh = FleshNormal = Blood = BloodNormal = null;
        LookupExhausted = false;
        Source = "none";
    }

    private static Texture2D ReadTexture(Material material, int propertyId) =>
        material.HasProperty(propertyId) ? material.GetTexture(propertyId) as Texture2D : null;
}
