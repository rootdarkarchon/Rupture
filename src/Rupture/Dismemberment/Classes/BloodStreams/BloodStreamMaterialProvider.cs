using System;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.BloodStreams;

internal static class BloodStreamMaterialProvider
{
	private const string BundleName = "rupture-wound";
	private const string ShaderName = "Rupture/PulsatingBloodStream";
	private static Material _material;
	private static Texture2D _fallbackAlbedo;
	private static Texture2D _fallbackNormal;
	private static bool _warnedShaderFallback;

	internal static Material Get()
	{
		if (_material != null) return _material;

		if (WoundResources.IsLoading) return null;
		Shader shader = WoundResources.GetShader(ShaderName);
		if (shader == null)
		{
			shader = Shader.Find("Standard");
			if (!_warnedShaderFallback)
			{
				_warnedShaderFallback = true;
				RupturePlugin.Log.LogWarning("PulsatingBloodStream shader is unavailable; using a generated dark-red Standard material until the wound bundle is rebuilt.");
			}
		}
		if (shader == null) return null;

		Texture2D bloodAlbedo = null;
		Texture2D bloodNormal = null;
		foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
		{
			if (texture == null) continue;
			if (bloodAlbedo == null && texture.name == "blood_splash_d") bloodAlbedo = texture;
			else if (bloodNormal == null && texture.name == "blood_splash_n") bloodNormal = texture;
			if (bloodAlbedo != null && bloodNormal != null) break;
		}

		if (bloodAlbedo == null)
		{
			_fallbackAlbedo = MakeTexture("RPT Generated Blood Albedo", new Color32(70, 1, 2, 255));
			bloodAlbedo = _fallbackAlbedo;
		}
		if (bloodNormal == null)
		{
			_fallbackNormal = MakeTexture("RPT Generated Blood Normal", new Color32(128, 128, 255, 255));
			bloodNormal = _fallbackNormal;
		}

		_material = new Material(shader)
		{
			name = "RPT Pulsating Blood Stream (Runtime)",
			hideFlags = HideFlags.HideAndDontSave
		};
		if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", bloodAlbedo);
		if (_material.HasProperty("_BumpMap")) _material.SetTexture("_BumpMap", bloodNormal);
		if (_material.HasProperty("_Color")) _material.SetColor("_Color", new Color(0.82f, 0.18f, 0.16f, 1f));
		if (_material.HasProperty("_Glossiness")) _material.SetFloat("_Glossiness", 0.94f);
		if (_material.HasProperty("_Smoothness")) _material.SetFloat("_Smoothness", 0.94f);
		if (_material.HasProperty("_NormalStrength")) _material.SetFloat("_NormalStrength", 1.1f);
		return _material;
	}

	internal static void Reset()
	{
		if (_material != null) UnityEngine.Object.Destroy(_material);
		if (_fallbackAlbedo != null) UnityEngine.Object.Destroy(_fallbackAlbedo);
		if (_fallbackNormal != null) UnityEngine.Object.Destroy(_fallbackNormal);
		_material = null;
		_fallbackAlbedo = null;
		_fallbackNormal = null;
		_warnedShaderFallback = false;
	}

	private static Texture2D MakeTexture(string name, Color32 color)
	{
		Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false)
		{
			name = name,
			hideFlags = HideFlags.HideAndDontSave,
			wrapMode = TextureWrapMode.Repeat,
			filterMode = FilterMode.Bilinear
		};
		texture.SetPixels32(new[] { color, color, color, color });
		texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		return texture;
	}
}
