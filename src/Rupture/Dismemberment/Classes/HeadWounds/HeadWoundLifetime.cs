using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using Systems.Effects;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal sealed class HeadWoundLifetime : MonoBehaviour
{
	private Mesh[] _ownedMeshes;
	private Renderer[] _disabledRenderers;
	private bool[] _originalRendererStates;
	private Renderer[] _replacementRenderers;
	private string _key;
	internal Renderer SkinRenderer => _replacementRenderers?[0];

	internal void QueueBloodDecals(Vector3 entryLocal, Vector3 exitLocal, Vector3 directionLocal, bool paintExit)
	{
		StartCoroutine(PaintBloodDecals(entryLocal, exitLocal, directionLocal, paintExit));
	}

	private IEnumerator PaintBloodDecals(Vector3 entryLocal, Vector3 exitLocal, Vector3 directionLocal, bool paintExit)
	{
		// The new renderer is not visible until Unity has culled it at least once.
		// Retain local coordinates while waiting so moving corpses keep their marks.
		for (int frame = 0; frame < 120; frame++)
		{
			yield return null;
			if (RuptureConfiguration.EnableBloodEffects?.Value != true) yield break;
			Renderer skin = SkinRenderer;
			if (!LimbDecalTextureTransfer.CanAcceptNativeDecal(skin) ||
				!Singleton<Effects>.Instantiated || Singleton<Effects>.Instance?.TexDecals == null ||
				!Singleton<Effects>.Instance.UseDecalPainter) continue;
			var renderers = new List<BodyRenderer>
			{
				new BodyRenderer { DecalType = EDecalTextureType.Blood, Renderers = new[] { skin } }
			};
			Vector3 direction = transform.TransformDirection(directionLocal).normalized;
			// Use EFT's untouched painter, default decal textures and default size.
			Singleton<Effects>.Instance.PlayerMeshesHit(renderers, transform.TransformPoint(entryLocal) - direction * 0.01f, direction);
			if (paintExit)
				Singleton<Effects>.Instance.PlayerMeshesHit(renderers, transform.TransformPoint(exitLocal) + direction * 0.01f, -direction);
			RupturePlugin.Log.LogInfo($"Head wound blood decals submitted: corpse={_key}, count={(paintExit ? 2 : 1)}.");
			yield break;
		}
		RupturePlugin.Log.LogWarning($"Head wound blood decals timed out waiting for a visible EFT decal renderer: corpse={_key}.");
	}

	internal void Initialize(string key, Mesh[] ownedMeshes, Renderer[] disabledRenderers, bool[] originalRendererStates,
		Renderer[] replacementRenderers)
	{
		_key = key;
		_ownedMeshes = ownedMeshes;
		_disabledRenderers = disabledRenderers;
		_originalRendererStates = originalRendererStates;
		_replacementRenderers = replacementRenderers;
	}

	internal void ReplaceMeshes(Mesh head, Mesh cavity)
	{
		var skin = (SkinnedMeshRenderer)_replacementRenderers[0];
		var filter = _replacementRenderers[1].GetComponent<MeshFilter>();
		if (skin == null || filter == null) throw new System.InvalidOperationException("Corpse replacement renderers were destroyed.");
		Mesh[] previous = _ownedMeshes;
		Mesh[] next = new[] { head, cavity };
		Bounds previousBounds = skin.localBounds;
		try
		{
			HeadWoundSkinning.SetMesh(skin, head, transform.parent); filter.sharedMesh = cavity;
		}
		catch
		{
			skin.sharedMesh = previous[0]; skin.localBounds = previousBounds; filter.sharedMesh = previous[1];
			throw;
		}
		_ownedMeshes = next;
		foreach (Mesh mesh in previous) if (mesh != null) Destroy(mesh);
	}

	private void LateUpdate()
	{
		// EFT's skin/LOD lifecycle may re-enable native head renderers after the
		// one-time swap. Keep the validated static replacement authoritative for
		// the lifetime of this corpse, without changing EFT-owned colliders.
		if (_disabledRenderers != null)
		{
			foreach (Renderer renderer in _disabledRenderers)
			{
				if (renderer != null && renderer.enabled)
				{
					renderer.enabled = false;
				}
			}
		}
		if (_replacementRenderers != null)
		{
			foreach (Renderer renderer in _replacementRenderers)
			{
				if (renderer != null)
				{
					renderer.forceRenderingOff = false;
					renderer.enabled = true;
				}
			}
		}
	}

	private void OnDestroy()
	{
		if (_disabledRenderers != null)
		{
			for (int index = 0; index < _disabledRenderers.Length; index++)
			{
				Renderer renderer = _disabledRenderers[index];
				if (renderer != null)
				{
					renderer.enabled = _originalRendererStates != null && index < _originalRendererStates.Length &&
						_originalRendererStates[index];
				}
			}
		}
		if (_ownedMeshes != null)
		{
			foreach (Mesh mesh in _ownedMeshes)
			{
				if (mesh != null)
				{
					Destroy(mesh);
				}
			}
		}
		HeadWoundSystem.NotifyDestroyed(this, _key);
	}
}
