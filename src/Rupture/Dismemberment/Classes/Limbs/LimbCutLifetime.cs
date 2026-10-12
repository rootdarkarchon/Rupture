using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal sealed class LimbCutLifetime : MonoBehaviour
{
	private readonly List<Renderer> _active = new List<Renderer>();
	private readonly List<Renderer> _debug = new List<Renderer>();
	private readonly List<Mesh> _meshes = new List<Mesh>();
	private readonly Dictionary<SkinnedMeshRenderer, Mesh> _originalMeshes = new Dictionary<SkinnedMeshRenderer, Mesh>();
	private readonly Dictionary<LODGroup, LOD[]> _originalLods = new Dictionary<LODGroup, LOD[]>();
	private readonly Dictionary<Renderer, Renderer> _attachmentOwners = new Dictionary<Renderer, Renderer>();

	internal void Initialize(Renderer[] nativeRenderers)
	{
		var native = new HashSet<Renderer>(nativeRenderers ?? Array.Empty<Renderer>());
		foreach (SkinnedMeshRenderer renderer in native.OfType<SkinnedMeshRenderer>())
			if (renderer != null && !_originalMeshes.ContainsKey(renderer))
				_originalMeshes.Add(renderer, renderer.sharedMesh);
	}

	internal void ReplaceLodAttachments(Renderer native, Renderer[] previous, Renderer[] current)
	{
		if (native == null) return;
		HashSet<Renderer> removed = new HashSet<Renderer>((previous ?? Array.Empty<Renderer>()).Where(item => item != null));
		Renderer[] added = (current ?? Array.Empty<Renderer>()).Where(item => item != null).Distinct().ToArray();
		foreach (Renderer renderer in removed) _attachmentOwners.Remove(renderer);
		foreach (Renderer renderer in added) _attachmentOwners[renderer] = native;

		LODGroup group = native.GetComponentInParent<LODGroup>();
		if (group == null) return;
		LOD[] lods = group.GetLODs();
		if (!_originalLods.ContainsKey(group)) _originalLods.Add(group, CloneLods(lods));
		bool changed = false;
		for (int level = 0; level < lods.Length; level++)
		{
			List<Renderer> renderers = (lods[level].renderers ?? Array.Empty<Renderer>())
				.Where(renderer => renderer != null && !removed.Contains(renderer)).ToList();
			int nativeIndex = renderers.IndexOf(native);
			if (nativeIndex < 0)
			{
				if (renderers.Count != (lods[level].renderers?.Length ?? 0))
				{
					lods[level].renderers = renderers.ToArray();
					changed = true;
				}
				continue;
			}
			for (int index = 0; index < added.Length; index++)
				if (!renderers.Contains(added[index])) renderers.Insert(nativeIndex + 1 + index, added[index]);
			lods[level].renderers = renderers.ToArray();
			changed = true;
		}
		if (!changed) return;
		group.SetLODs(lods);
		group.RecalculateBounds();
	}

	private static LOD[] CloneLods(LOD[] source)
	{
		LOD[] clone = new LOD[source?.Length ?? 0];
		for (int index = 0; index < clone.Length; index++)
		{
			clone[index] = new LOD(source[index].screenRelativeTransitionHeight,
				(source[index].renderers ?? Array.Empty<Renderer>()).ToArray())
			{
				fadeTransitionWidth = source[index].fadeTransitionWidth
			};
		}
		return clone;
	}

	internal void AddReplacement(Renderer renderer, Mesh mesh)
	{
		if (renderer != null && !_active.Contains(renderer)) _active.Add(renderer);
		TrackMesh(mesh);
	}

	internal void AddDebug(Renderer renderer, Mesh mesh)
	{
		if (renderer != null && !_debug.Contains(renderer)) _debug.Add(renderer);
		TrackMesh(mesh);
	}

	internal void TrackMesh(Mesh mesh)
	{
		if (mesh != null && !_meshes.Contains(mesh)) _meshes.Add(mesh);
	}

	private void LateUpdate()
	{
		foreach (Renderer renderer in _active)
			if (renderer != null)
			{
				bool visible = !_attachmentOwners.TryGetValue(renderer, out Renderer owner) ||
					(owner != null && owner.enabled && !owner.forceRenderingOff);
				renderer.forceRenderingOff = !visible;
				renderer.enabled = visible;
			}
		bool showDebug = RuptureConfiguration.ProceduralLimbCutDebugOverlay?.Value == true;
		foreach (Renderer renderer in _debug)
			if (renderer != null)
			{
				bool visible = showDebug && (!_attachmentOwners.TryGetValue(renderer, out Renderer owner) ||
					(owner != null && owner.enabled && !owner.forceRenderingOff));
				renderer.forceRenderingOff = !visible;
				renderer.enabled = visible;
			}
	}

	private void OnDestroy()
	{
		foreach (KeyValuePair<LODGroup, LOD[]> item in _originalLods)
			if (item.Key != null) { item.Key.SetLODs(item.Value); item.Key.RecalculateBounds(); }
		foreach (KeyValuePair<SkinnedMeshRenderer, Mesh> item in _originalMeshes)
			if (item.Key != null) item.Key.sharedMesh = item.Value;
		foreach (Mesh mesh in _meshes) if (mesh != null) Destroy(mesh);
	}
}

internal sealed class LimbGibMeshLifetime : MonoBehaviour
{
	private Mesh[] _meshes;
	internal void Initialize(Mesh[] meshes) => _meshes = meshes;
	private void OnDestroy()
	{
		if (_meshes == null) return;
		foreach (Mesh mesh in _meshes) if (mesh != null) Destroy(mesh);
	}
}
