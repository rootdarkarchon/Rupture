using System;
using UnityEngine;

namespace Rupture.Dismemberment.Classes
{

// Reads borrowed renderer state. Callers retain their own adoption, visibility,
// material, and culling policies; this type owns no Unity resources.
internal static class RendererSource
{
	internal static bool TryGetLodLevel(SkinnedMeshRenderer renderer, out int level)
	{
		level = -1;
		if (renderer == null) return false;
		if (TryParseLodName(renderer.name, out level)) return true;
		LODGroup group = renderer.GetComponentInParent<LODGroup>();
		if (group == null) return false;
		LOD[] lods = group.GetLODs();
		for (int index = 0; index < lods.Length; index++)
			foreach (Renderer candidate in lods[index].renderers)
				if (candidate == renderer) { level = index; return true; }
		return false;
	}

	private static bool TryParseLodName(string name, out int level)
	{
		level = -1;
		if (string.IsNullOrEmpty(name)) return false;
		int marker = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
		if (marker < 0) return false;
		int start = marker + 4;
		int end = start;
		while (end < name.Length && char.IsDigit(name[end])) end++;
		return end != start && int.TryParse(name.Substring(start, end - start), out level);
	}

	internal static int CopyPropertyBlocks(Renderer source, Renderer destination, int materialCount)
	{
		if (source == null || destination == null) return 0;
		int copied = 0;
		MaterialPropertyBlock properties = new MaterialPropertyBlock();
		source.GetPropertyBlock(properties);
		if (!properties.isEmpty)
		{
			destination.SetPropertyBlock(properties);
			copied++;
		}
		for (int materialIndex = 0; materialIndex < materialCount; materialIndex++)
		{
			properties.Clear();
			source.GetPropertyBlock(properties, materialIndex);
			if (properties.isEmpty) continue;
			destination.SetPropertyBlock(properties, materialIndex);
			copied++;
		}
		return copied;
	}

	internal static void CopyBlendShapeWeights(SkinnedMeshRenderer source, SkinnedMeshRenderer destination)
	{
		Mesh sourceMesh = source?.sharedMesh;
		Mesh destinationMesh = destination?.sharedMesh;
		if (sourceMesh == null || destinationMesh == null) return;
		for (int index = 0; index < destinationMesh.blendShapeCount; index++)
		{
			int sourceIndex = sourceMesh.GetBlendShapeIndex(destinationMesh.GetBlendShapeName(index));
			if (sourceIndex >= 0) destination.SetBlendShapeWeight(index, source.GetBlendShapeWeight(sourceIndex));
		}
	}
}
}
