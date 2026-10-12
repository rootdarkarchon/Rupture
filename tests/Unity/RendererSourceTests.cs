#if UNITY_EDITOR
using System;
using Rupture.Dismemberment.Classes;
using Rupture.Ragdolls.Classes;
using UnityEngine;

internal static class RendererSourceTests
{
	internal static void Run(Action<bool, string> check)
	{
		var root = new GameObject("renderer source tests");
		var sourceHost = new GameObject("source"); sourceHost.transform.SetParent(root.transform);
		var targetHost = new GameObject("target"); targetHost.transform.SetParent(root.transform);
		var source = sourceHost.AddComponent<SkinnedMeshRenderer>();
		var target = targetHost.AddComponent<SkinnedMeshRenderer>();
		var sourceMesh = new Mesh(); var targetMesh = new Mesh();
		var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
		try
		{
			check(BodyHierarchyPath.RelativeTo(root.transform, root.transform) == ".", "body root keeps wire identity dot");
			check(BodyHierarchyPath.RelativeTo(root.transform, source.transform) == "source", "child body uses exact relative name");
			check(BodyHierarchyPath.RelativeTo(source.transform, target.transform) == null, "body outside root is rejected");
			check(BodyHierarchyPath.RelativeTo(null, source.transform) == null, "missing root has no body identity");
			var grandchild = new GameObject("bone"); grandchild.transform.SetParent(source.transform);
			check(BodyHierarchyPath.RelativeTo(root.transform, grandchild.transform) == "source/bone", "nested body path preserves hierarchy order");
			check(!RendererSource.TryGetLodLevel(source, out _), "unknown LOD remains unknown for caller policy");
			check(!RendererSource.TryGetLodLevel(null, out _), "missing renderer has no LOD");
			var group = root.AddComponent<LODGroup>();
			group.SetLODs(new[] { new LOD(.5f, new Renderer[] { target }), new LOD(.1f, new Renderer[] { source }) });
			check(RendererSource.TryGetLodLevel(source, out int level) && level == 1, "LOD group fallback finds actual tier");
			source.name = "shirt_lOd02_variant";
			check(RendererSource.TryGetLodLevel(source, out level) && level == 2, "named LOD takes precedence over group");
			source.name = "shirt_LOD0_LOD3";
			check(RendererSource.TryGetLodLevel(source, out level) && level == 3, "last LOD marker takes precedence");
			source.name = "shirt_LOD999999999999999";
			check(RendererSource.TryGetLodLevel(source, out level) && level == 1, "overflowed named LOD falls back to group");
			source.name = "shirt_LOD_invalid";
			check(RendererSource.TryGetLodLevel(source, out level) && level == 1, "non-numeric LOD falls back to group");

			source.sharedMaterials = target.sharedMaterials = new[] { material, material };
			int property = Shader.PropertyToID("_RefactorProbe");
			var block = new MaterialPropertyBlock();
			block.SetFloat(property, 11); source.SetPropertyBlock(block);
			block.SetFloat(property, 22); source.SetPropertyBlock(block, 0);
			block.SetFloat(property, 33); target.SetPropertyBlock(block, 1);
			check(RendererSource.CopyPropertyBlocks(source, target, 2) == 2, "copy counts global and populated material block");
			target.GetPropertyBlock(block); check(block.GetFloat(property) == 11, "global property block preserved");
			target.GetPropertyBlock(block, 0); check(block.GetFloat(property) == 22, "per-material override preserved");
			target.GetPropertyBlock(block, 1); check(block.GetFloat(property) == 33, "empty source slot does not erase destination");
			source.GetPropertyBlock(block, 0); check(block.GetFloat(property) == 22, "copy leaves borrowed source unchanged");

			sourceMesh.vertices = targetMesh.vertices = new[] { Vector3.zero };
			var delta = new[] { Vector3.up };
			sourceMesh.AddBlendShapeFrame("first", 100, delta, null, null);
			sourceMesh.AddBlendShapeFrame("second", 100, delta, null, null);
			targetMesh.AddBlendShapeFrame("second", 100, delta, null, null);
			targetMesh.AddBlendShapeFrame("missing", 100, delta, null, null);
			targetMesh.AddBlendShapeFrame("first", 100, delta, null, null);
			source.sharedMesh = sourceMesh; target.sharedMesh = targetMesh;
			source.SetBlendShapeWeight(0, 31); source.SetBlendShapeWeight(1, 67); target.SetBlendShapeWeight(1, 19);
			RendererSource.CopyBlendShapeWeights(source, target);
			check(target.GetBlendShapeWeight(0) == 67 && target.GetBlendShapeWeight(2) == 31, "blend shapes copied by name across reordered channels");
			check(target.GetBlendShapeWeight(1) == 19, "missing blend shape preserves destination weight");
		}
		finally
		{
			UnityEngine.Object.DestroyImmediate(root);
			UnityEngine.Object.DestroyImmediate(sourceMesh); UnityEngine.Object.DestroyImmediate(targetMesh);
			UnityEngine.Object.DestroyImmediate(material);
		}
	}
}
#endif
