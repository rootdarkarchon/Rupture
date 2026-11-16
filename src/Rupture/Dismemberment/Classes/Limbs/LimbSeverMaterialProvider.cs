using System;
using UnityEngine;
using Rupture.Dismemberment.Classes.HeadWounds;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static class LimbSeverMaterialProvider
{
	internal const string ShaderName = "Rupture/LimbSeverSurface";
	private static readonly int VariationOffsetId = Shader.PropertyToID("_VariationOffset");
	private static Material _material;
	private static Shader _shader;
	private static string _lastFailure;
	// Main-thread scratch; GetPropertyBlock refills it completely on every call.
	private static MaterialPropertyBlock _variationBlock;

	/// <summary>
	/// Returns the shared sever material, creating it on first success. It never scans for textures: they come from
	/// <see cref="EftWoundTextures"/>, which <see cref="RaidWarmup"/> fills at raid start, so a cut never pays for the lookup.
	/// </summary>
	internal static bool TryGet(out Material material, out string failure)
	{
		material = _material;
		failure = _lastFailure;
		if (_material != null) return true;
		_shader = WoundResources.GetShader(ShaderName);
		if (_shader == null)
			return Fail("the LimbSeverSurface shader is missing from rupture-wound.bundle", out material, out failure);
		if (!EftWoundTextures.Ready)
			return Fail(EftWoundTextures.LookupExhausted
				? "required EFT textures (decal_flesh2, decal_flesh2_nrm, blood_splash_d, blood_splash_n) were not found"
				: "required EFT textures are not resolved yet", out material, out failure);

		_material = new Material(_shader)
		{
			name = "RPT Limb Sever Surface (Runtime)",
			hideFlags = HideFlags.HideAndDontSave
		};
		_material.SetTexture("_FleshTex", EftWoundTextures.Flesh);
		_material.SetTexture("_FleshNormal", EftWoundTextures.FleshNormal);
		_material.SetTexture("_BloodTex", EftWoundTextures.Blood);
		_material.SetTexture("_BloodNormal", EftWoundTextures.BloodNormal);
		_material.SetFloat("_NormalStrength", 1.15f);
		_lastFailure = null;
		material = _material;
		failure = null;
		return true;
	}

	internal static void ApplyVariation(Renderer renderer, int materialIndex, uint seed, bool gibSide)
	{
		if (renderer == null || materialIndex < 0) return;
		uint branch = seed ^ (gibSide ? 0xB5297A4Du : 0x68E31DA4u);
		float x = (branch & 0xFFFFu) / 65535f * 17f;
		float y = ((branch >> 16) & 0xFFFFu) / 65535f * 17f;
		MaterialPropertyBlock block = _variationBlock ??= new MaterialPropertyBlock();
		renderer.GetPropertyBlock(block, materialIndex);
		block.SetVector(VariationOffsetId, new Vector4(x, y, gibSide ? 1f : 0f, 0f));
		renderer.SetPropertyBlock(block, materialIndex);
	}

	internal static void Reset()
	{
		// Also covers GameWorld.OnGameStarted, which resets the sever subsystem
		// after pre-existing health effects may already have been discovered.
		Rupture.Dismemberment.Classes.Fractures.FractureSystem.Reset();
		if (_material != null) UnityEngine.Object.Destroy(_material);
		_material = null;
		_shader = null;
		_lastFailure = null;
		RaidWarmup.InvalidateMaterials();
	}

	private static bool Fail(string message, out Material material, out string failure)
	{
		_lastFailure = message;
		material = null;
		failure = message;
		return false;
	}
}
