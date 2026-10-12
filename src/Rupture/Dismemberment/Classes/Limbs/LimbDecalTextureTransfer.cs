using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using Systems.Effects;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal enum LimbDecalTransferResult
{
	Unavailable,
	NoSourceTexture,
	AlreadyRegistered,
	Copied
}

/// <summary>
/// EFT's character painter owns one accumulated decal render texture per Renderer.
/// A detached gib is a new Renderer, so it must receive an independent copy of the
/// source texture before EFT paints the sever ring onto it. The dictionary field is
/// resolved by its exact closed generic type instead of by its obfuscated name.
/// </summary>
internal static class LimbDecalTextureTransfer
{
	private static readonly FieldInfo RendererTexturesField = ResolveRendererTexturesField();
	private static readonly int DecalsTextureId = Shader.PropertyToID("_DecalsTex");
	private static readonly int DecalPowerId = Shader.PropertyToID("_DecalPower");

	internal static LimbDecalTransferResult CopyExisting(Renderer source, Renderer destination,
		out string failure)
	{
		failure = null;
		if (source == null || destination == null)
		{
			failure = "source or destination renderer is missing";
			return LimbDecalTransferResult.Unavailable;
		}
		if (!TryGetRendererTextures(out Dictionary<Renderer, RenderTexture> textures, out failure))
			return LimbDecalTransferResult.Unavailable;
		if (textures.TryGetValue(destination, out RenderTexture existing))
		{
			if (existing != null) return LimbDecalTransferResult.AlreadyRegistered;
			textures.Remove(destination);
		}
		if (!textures.TryGetValue(source, out RenderTexture sourceTexture) || sourceTexture == null)
			return LimbDecalTransferResult.NoSourceTexture;
		if (!sourceTexture.IsCreated())
		{
			failure = "source renderer decal texture is not created";
			return LimbDecalTransferResult.Unavailable;
		}

		RenderTexture copy = null;
		try
		{
			copy = new RenderTexture(sourceTexture.descriptor)
			{
				name = $"RPT inherited limb decals {destination.GetInstanceID()}",
				filterMode = sourceTexture.filterMode,
				wrapMode = sourceTexture.wrapMode,
				anisoLevel = sourceTexture.anisoLevel
			};
			if (!copy.Create()) throw new InvalidOperationException("copied decal render texture could not be created");
			Graphics.Blit(sourceTexture, copy);
			Material material = destination.material;
			if (material == null)
				throw new InvalidOperationException("destination renderer has no material instance");
			material.SetTexture(DecalsTextureId, copy);
			material.SetFloat(DecalPowerId, 1f);
			textures.Add(destination, copy);
			return LimbDecalTransferResult.Copied;
		}
		catch (Exception exception)
		{
			if (copy != null) UnityEngine.Object.Destroy(copy);
			failure = $"{exception.GetType().Name}: {exception.Message}";
			return LimbDecalTransferResult.Unavailable;
		}
	}

	internal static bool RebindExisting(Renderer renderer, out string failure)
	{
		failure = null;
		if (renderer == null)
		{
			failure = "renderer is missing";
			return false;
		}
		if (!TryGetRendererTextures(out Dictionary<Renderer, RenderTexture> textures, out failure)) return false;
		if (!textures.TryGetValue(renderer, out RenderTexture texture) || texture == null) return true;
		try
		{
			Material material = renderer.material;
			if (material == null) throw new InvalidOperationException("renderer has no material instance");
			material.SetTexture(DecalsTextureId, texture);
			material.SetFloat(DecalPowerId, 1f);
			return true;
		}
		catch (Exception exception)
		{
			failure = $"{exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	internal static bool CanAcceptNativeDecal(Renderer renderer)
	{
		return BodyDecalRendering.CanPaint(renderer);
	}

	internal static bool TryGetRegisteredTexture(Renderer renderer, out RenderTexture texture,
		out string failure)
	{
		texture = null;
		if (renderer == null)
		{
			failure = "renderer is missing";
			return false;
		}
		if (!TryGetRendererTextures(out Dictionary<Renderer, RenderTexture> textures, out failure))
			return false;
		if (!textures.TryGetValue(renderer, out texture) || texture == null)
		{
			failure = "renderer has no registered EFT decal texture";
			texture = null;
			return false;
		}
		failure = null;
		return true;
	}

	// The painter dictionary owns the texture after registration, including normal
	// corpse cleanup and the existing severed-limb copy path. The soak system never
	// releases or replaces a registered accumulation texture on treatment.
	internal static bool EnsureRegisteredTexture(Renderer renderer, out RenderTexture texture, out string failure, out bool createdTexture)
	{
		createdTexture = false;
		texture = null;
		if (!TryGetRendererTextures(out var textures, out failure)) return false;
		if (textures.TryGetValue(renderer, out texture) && texture != null && texture.IsCreated()) return true;
		RenderTexture created = null;
		try
		{
			created = Singleton<Effects>.Instance.TexDecals.CreateDecalTexture();
			RenderTexture previous = RenderTexture.active;
			try { RenderTexture.active = created; GL.Clear(false, true, Color.clear); }
			finally { RenderTexture.active = previous; }
			Material material = renderer.material;
			material.SetTexture(DecalsTextureId, created); material.SetFloat(DecalPowerId, 1f);
			textures[renderer] = created; texture = created; createdTexture = true; return true;
		}
		catch (Exception exception)
		{
			if (created != null) UnityEngine.Object.Destroy(created);
			failure = exception.Message; return false;
		}
	}

	private static bool TryGetRendererTextures(out Dictionary<Renderer, RenderTexture> textures,
		out string failure)
	{
		textures = null;
		failure = null;
		if (RendererTexturesField == null)
		{
			failure = "TextureDecalsPainter renderer-texture dictionary ABI was not found";
			return false;
		}
		if (!Singleton<Effects>.Instantiated || Singleton<Effects>.Instance?.TexDecals == null)
		{
			failure = "EFT texture decal painter is unavailable";
			return false;
		}
		try
		{
			textures = RendererTexturesField.GetValue(Singleton<Effects>.Instance.TexDecals) as
				Dictionary<Renderer, RenderTexture>;
			if (textures != null) return true;
			failure = "EFT texture decal painter returned no renderer-texture dictionary";
			return false;
		}
		catch (Exception exception)
		{
			failure = $"{exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	private static FieldInfo ResolveRendererTexturesField()
	{
		Type expected = typeof(Dictionary<Renderer, RenderTexture>);
		foreach (FieldInfo field in typeof(TextureDecalsPainter).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
			if (field.FieldType == expected) return field;
		return null;
	}
}
