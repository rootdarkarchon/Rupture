using System;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal static class HeadWoundMaterialProvider
{
	internal const string BundleName = "rupture-wound";
	internal const string ShaderName = "Rupture/HeadWoundInterior";

	private static Material _material;
	private static Shader _shader;
	private static string _lastFailure;

	/// <summary>
	/// Returns the shared head-wound interior material, creating it on first success. It never scans for textures:
	/// they come from <see cref="EftWoundTextures"/>, which <see cref="RaidWarmup"/> fills at raid start.
	/// </summary>
	internal static bool TryGet(out Material material, out string failure)
	{
		material = _material;
		failure = _lastFailure;
		if (_material != null)
		{
			return true;
		}

		_shader = WoundResources.GetShader(ShaderName);
		if (_shader == null)
		{
			return Fail("the HeadWoundInterior shader is missing from rupture-wound.bundle", out material, out failure);
		}
		if (!EftWoundTextures.Ready)
		{
			return Fail(EftWoundTextures.LookupExhausted
				? "required EFT textures (decal_flesh2, decal_flesh2_nrm, blood_splash_d, blood_splash_n) were not found"
				: "required EFT textures are not resolved yet", out material, out failure);
		}

		_material = new Material(_shader)
		{
			name = "RPT Head Wound Interior (Runtime)",
			hideFlags = HideFlags.HideAndDontSave
		};
		_material.SetTexture("_FleshTex", EftWoundTextures.Flesh);
		_material.SetTexture("_FleshNormal", EftWoundTextures.FleshNormal);
		_material.SetTexture("_BloodTex", EftWoundTextures.Blood);
		_material.SetTexture("_BloodNormal", EftWoundTextures.BloodNormal);
		_material.SetFloat("_BloodAmount", 0.78f);
		_material.SetFloat("_FleshSmoothness", 0.22f);
		_material.SetFloat("_WetSmoothness", 0.9f);
		_material.SetFloat("_NormalStrength", 1f);
		_lastFailure = null;
		material = _material;
		failure = null;
		return true;
	}

	internal static void Reset()
	{
		if (_material != null)
		{
			UnityEngine.Object.Destroy(_material);
		}
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
