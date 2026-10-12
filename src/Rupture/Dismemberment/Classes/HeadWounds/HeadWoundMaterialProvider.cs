using System;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal static class HeadWoundMaterialProvider
{
	internal const string BundleName = "rupture-wound";
	private const string ShaderName = "Rupture/HeadWoundInterior";
	private const float RetryIntervalSeconds = 0.5f;

	private static Material _material;
	private static Shader _shader;
	private static float _nextAttemptAt;
	private static string _lastFailure;

	internal static bool TryGet(out Material material, out string failure)
	{
		material = _material;
		failure = _lastFailure;
		if (_material != null)
		{
			return true;
		}
		if (Time.realtimeSinceStartup < _nextAttemptAt)
		{
			return false;
		}
		_nextAttemptAt = Time.realtimeSinceStartup + RetryIntervalSeconds;

		_shader = WoundResources.GetShader(ShaderName);
		if (_shader == null)
		{
			return Fail("the HeadWoundInterior shader is missing from rupture-wound.bundle", out material, out failure);
		}

		Texture2D flesh = null;
		Texture2D fleshNormal = null;
		Texture2D blood = null;
		Texture2D bloodNormal = null;
		foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
		{
			if (texture == null)
			{
				continue;
			}
			switch (texture.name)
			{
			case "decal_flesh2":
				flesh ??= texture;
				break;
			case "decal_flesh2_nrm":
				fleshNormal ??= texture;
				break;
			case "blood_splash_d":
				blood ??= texture;
				break;
			case "blood_splash_n":
				bloodNormal ??= texture;
				break;
			}
		}
		if (flesh == null || fleshNormal == null || blood == null || bloodNormal == null)
		{
			return Fail($"required EFT textures are not loaded (flesh={flesh != null}, fleshNormal={fleshNormal != null}, blood={blood != null}, bloodNormal={bloodNormal != null})", out material, out failure);
		}

		_material = new Material(_shader)
		{
			name = "RPT Head Wound Interior (Runtime)",
			hideFlags = HideFlags.HideAndDontSave
		};
		_material.SetTexture("_FleshTex", flesh);
		_material.SetTexture("_FleshNormal", fleshNormal);
		_material.SetTexture("_BloodTex", blood);
		_material.SetTexture("_BloodNormal", bloodNormal);
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
		_nextAttemptAt = 0f;
		_lastFailure = null;
	}

	private static bool Fail(string message, out Material material, out string failure)
	{
		_lastFailure = message;
		material = null;
		failure = message;
		return false;
	}
}
