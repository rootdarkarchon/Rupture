#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
internal static partial class BenchmarkRuptureHeadWound
{
	internal static ProceduralWoundDescriptor[] MultipleDescriptors(string kind)
	{
		if (kind == "intersecting" || kind == "high-intersecting")
			return new[] { CreateThroughDescriptor(), CreateObliqueThroughDescriptor(),
				new ProceduralWoundDescriptor(new Vector3(-0.09f, 0, 0), new Vector3(0.09f, 0, 0), 0.006f, 0.024f, 13),
				new ProceduralWoundDescriptor(new Vector3(0, -0.12f, 0), new Vector3(0, 0.12f, 0), 0.006f, 0.024f, 19) };
		if (kind == "reverse" || kind == "separated")
		{
			var first = kind == "separated" ? CreateSeparatedShellDescriptor() : CreateThroughDescriptor();
			return new[] { first, new ProceduralWoundDescriptor(first.Exit, first.Entry, first.EntryRadius, first.ExitRadius, 991) };
		}
		if (kind == "coincident") return new[] { CreateThroughDescriptor(), CreateThroughDescriptor() };
		var result = new ProceduralWoundDescriptor[4];
		for (int i = 0; i < 4; i++)
		{
			float x = kind == "near-coincident" ? i * 0.0002f : (i % 2 == 0 ? -0.032f : 0.032f);
			float y = kind == "near-coincident" ? 0 : (i < 2 ? -0.038f : 0.038f);
			float z = HeadRadii.z * Mathf.Sqrt(1 - x * x / (HeadRadii.x * HeadRadii.x) - y * y / (HeadRadii.y * HeadRadii.y));
			result[i] = new ProceduralWoundDescriptor(new Vector3(x, y, -z), new Vector3(x, y, z), 0.006f, 0.024f, (uint)(100 + i));
		}
		return result;
	}

	private static BenchmarkCase[] RunMultipleCases()
	{
		var names = new[] { "separate", "intersecting", "near-coincident", "coincident", "reverse", "separated", "high-intersecting", "forced-rejection" };
		var results = new BenchmarkCase[names.Length];
		for (int i = 0; i < names.Length; i++) results[i] = RunMultiple(names[i]);
		return results;
	}

	private static BenchmarkCase RunMultiple(string kind)
	{
		bool high = kind == "high-intersecting";
		var result = new BenchmarkCase { name = kind, maximumGeometryMilliseconds = high ? 140 : 60,
			maximumTotalMilliseconds = high ? 200 : 100, maximumManagedBytes = (high ? 4096 : 2048) * 1024L };
		Mesh source = null, head = null, cavity = null;
		try
		{
			source = kind == "separated" ? BuildSeparatedShells(48, 24) : BuildEllipsoid(high ? 192 : 64, high ? 96 : 32, HeadRadii);
			if (kind == "forced-rejection") AddSkinningFixture(source);
			var descriptors = MultipleDescriptors(kind);
			var workspace = new ProceduralHeadWoundWorkspace();
			if (!workspace.AcquireFromMeshOnMainThread(source, out string failure) ||
				!ProceduralHeadWoundGeometry.TryGenerateMultiple(workspace, descriptors, out _, out failure)) throw new InvalidDataException("warm-up: " + failure);
			head = UnityEngine.Object.Instantiate(source);
			if (kind == "forced-rejection")
			{
				workspace.ApplyFilteredIndicesOnMainThread(head);
				Vector3[] before = head.vertices; int[] indices = head.triangles;
				workspace.AcquireFromMeshOnMainThread(source, out _);
				bool rejected = !ProceduralHeadWoundGeometry.TryGenerateMultiple(workspace, new ProceduralWoundDescriptor[5], out _, out _);
				result.valid = rejected && Exact(before, head.vertices) && Exact(indices, head.triangles) &&
					Prefix(source.boneWeights, head.boneWeights) && Prefix(source.colors, head.colors) && Prefix(source.tangents, head.tangents) &&
					head.boneWeights.Length == head.vertexCount && head.subMeshCount == source.subMeshCount;
				foreach (BoneWeight weight in head.boneWeights)
					result.valid &= Mathf.Abs(weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3 - 1f) < 0.0001f;
				for (int channel = 0; channel < 8; channel++)
				{
					var expected = new System.Collections.Generic.List<Vector4>(); var actual = new System.Collections.Generic.List<Vector4>();
					source.GetUVs(channel, expected); head.GetUVs(channel, actual);
					result.valid &= Prefix(expected.ToArray(), actual.ToArray());
				}
				return result;
			}
			GC.Collect(); long allocated = GC.GetAllocatedBytesForCurrentThread(); var total = Stopwatch.StartNew();
			if (!workspace.AcquireFromMeshOnMainThread(source, out failure)) throw new InvalidDataException(failure);
			var geometry = Stopwatch.StartNew();
			if (!ProceduralHeadWoundGeometry.TryGenerateMultiple(workspace, descriptors, out _, out failure)) throw new InvalidDataException(failure);
			result.singlesMs = workspace.LastSinglesMs; result.skinMs = workspace.LastSkinMs; result.canalsMs = workspace.LastCanalsMs;
			geometry.Stop(); result.triangleClassificationMilliseconds = geometry.Elapsed.TotalMilliseconds;
			workspace.ApplyFilteredIndicesOnMainThread(head);
			cavity = workspace.CreateCavityMeshOnMainThread("Multiple canals");
			total.Stop(); result.totalMilliseconds = total.Elapsed.TotalMilliseconds;
			result.managedDeltaBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
			result.sourceVertexCount = source.vertexCount; result.resultHeadVertexCount = head.vertexCount;
			result.generatedHeadVertexCount = head.vertexCount - source.vertexCount; result.generatedWoundVertexCount = cavity.vertexCount;
			result.valid = ProceduralHeadWoundGeometry.ValidateCombined(workspace, descriptors, source.vertexCount, out failure) &&
				Prefix(source.vertices, head.vertices) && Prefix(source.normals, head.normals) && Prefix(source.uv, head.uv);
			if (kind == "separated" && result.valid)
			{
				// Neither canal may put geometry in the empty gap between the two shells.
				var vertices = cavity.vertices; var triangles = cavity.triangles;
				for (int i = 0; i < triangles.Length; i += 3)
				{
					Vector3 center = (vertices[triangles[i]] + vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3;
					if (Mathf.Abs(center.z) < 0.012f) { result.valid = false; failure = "canal bridged the disconnected shell gap"; break; }
				}
			}
			result.failure = failure;
		}
		catch (Exception exception) { result.valid = false; result.failure = exception.Message; }
		finally
		{
			if (source != null) UnityEngine.Object.DestroyImmediate(source);
			if (head != null) UnityEngine.Object.DestroyImmediate(head);
			if (cavity != null) UnityEngine.Object.DestroyImmediate(cavity);
		}
		return result;
	}
	private static void AddSkinningFixture(Mesh mesh)
	{
		int count = mesh.vertexCount; var weights = new BoneWeight[count]; var colors = new Color[count]; var tangents = new Vector4[count];
		for (int i = 0; i < count; i++)
		{
			float blend = (i % 17) / 16f;
			weights[i] = new BoneWeight { boneIndex0 = 0, boneIndex1 = 1, weight0 = blend, weight1 = 1 - blend };
			colors[i] = new Color(blend, 1 - blend, 0.5f, 1); tangents[i] = new Vector4(1, 0, 0, 1);
		}
		mesh.boneWeights = weights; mesh.colors = colors; mesh.tangents = tangents;
		for (int channel = 1; channel < 8; channel++)
		{
			var uv = new System.Collections.Generic.List<Vector4>(count);
			for (int i = 0; i < count; i++) uv.Add(new Vector4(i * 0.001f, channel, 0.25f, 1));
			mesh.SetUVs(channel, uv);
		}
		int[] indices = mesh.triangles; int split = (indices.Length / 6) * 3;
		var first = new int[split]; var second = new int[indices.Length - split];
		Array.Copy(indices, first, first.Length); Array.Copy(indices, split, second, 0, second.Length);
		mesh.subMeshCount = 2; mesh.SetTriangles(first, 0); mesh.SetTriangles(second, 1);
	}

	private static bool Prefix<T>(T[] source, T[] result)
	{
		if (result.Length < source.Length) return false;
		for (int i = 0; i < source.Length; i++) if (!source[i].Equals(result[i])) return false;
		return true;
	}
	private static bool Exact<T>(T[] a, T[] b) => a.Length == b.Length && Prefix(a, b);
}

internal static partial class PreviewRuptureHeadWound
{
	[MenuItem("Tools/Rupture/Preview Head Wound/Four Separate Canals")]
	private static void PreviewFour() => PreviewMultiple("separate");
	[MenuItem("Tools/Rupture/Preview Head Wound/Intersecting Canals")]
	private static void PreviewCrossing() => PreviewMultiple("intersecting");
	[MenuItem("Tools/Rupture/Preview Head Wound/Nearly Coincident Canals")]
	private static void PreviewNear() => PreviewMultiple("near-coincident");
	[MenuItem("Tools/Rupture/Preview Head Wound/Reverse Canals")]
	private static void PreviewReverse() => PreviewMultiple("reverse");
	[MenuItem("Tools/Rupture/Preview Head Wound/Disconnected Multiple Canals")]
	private static void PreviewDisconnected() => PreviewMultiple("separated");

	public static void RenderMultiplePreviews()
	{
		string directory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "MultipleHeadWoundPreviews");
		Directory.CreateDirectory(directory);
		RenderSettings.ambientLight = new Color(0.65f, 0.65f, 0.65f);
		var lightObject = new GameObject("Wound verification light");
		var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f;
		lightObject.transform.rotation = Quaternion.Euler(35, -35, 0);
		var cameraObject = new GameObject("Wound verification camera"); var camera = cameraObject.AddComponent<Camera>();
		camera.orthographic = true; camera.orthographicSize = 0.19f;
		camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
		camera.nearClipPlane = 0.01f; camera.farClipPlane = 5;
		var target = new RenderTexture(1600, 900, 24) { antiAliasing = 4 }; target.Create(); camera.targetTexture = target;
		try
		{
			foreach (string kind in new[] { "separate", "intersecting", "near-coincident", "separated" })
			{
				PreviewMultiple(kind);
				for (int side = 0; side < 2; side++)
				{
					camera.transform.position = new Vector3(0.06f, 0.12f, side == 0 ? -0.9f : 0.9f);
					camera.transform.LookAt(Vector3.zero); camera.Render();
					RenderTexture previous = RenderTexture.active; RenderTexture.active = target;
					var image = new Texture2D(1600, 900, TextureFormat.RGB24, false);
					image.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); image.Apply(); RenderTexture.active = previous;
					File.WriteAllBytes(Path.Combine(directory, kind + (side == 0 ? "-entry.png" : "-exit.png")), image.EncodeToPNG());
					UnityEngine.Object.DestroyImmediate(image);
				}
				ClearPreview();
			}
		}
		finally
		{
			camera.targetTexture = null; target.Release(); UnityEngine.Object.DestroyImmediate(target);
			UnityEngine.Object.DestroyImmediate(cameraObject); UnityEngine.Object.DestroyImmediate(lightObject); ClearPreview();
		}
	}

	private static void PreviewMultiple(string kind)
	{
		ClearPreview();
		Mesh source = kind == "separated" ? BenchmarkRuptureHeadWound.BuildSeparatedShells(48, 24) :
			BenchmarkRuptureHeadWound.BuildEllipsoid(64, 32, new Vector3(0.09f, 0.12f, 0.105f));
		Mesh head = UnityEngine.Object.Instantiate(source); var workspace = new ProceduralHeadWoundWorkspace();
		if (!workspace.AcquireFromMeshOnMainThread(source, out string failure) ||
			!ProceduralHeadWoundGeometry.TryGenerateMultiple(workspace, BenchmarkRuptureHeadWound.MultipleDescriptors(kind), out _, out failure))
		{ UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(head); throw new InvalidOperationException(failure); }
		workspace.ApplyFilteredIndicesOnMainThread(head); Mesh cavity = workspace.CreateCavityMeshOnMainThread(kind);
		var root = new GameObject(PreviewRootName); Undo.RegisterCreatedObjectUndo(root, "Multiple head wounds");
		var skin = CreateSkinMaterial(); var wound = CreateWoundMaterial();
		CreateMeshObject("Intact", root.transform, Vector3.left * 0.24f, source, skin);
		var cut = CreateMeshObject("Cumulative Head", root.transform, Vector3.zero, head, skin);
		CreateMeshObject("Canals", cut.transform, Vector3.zero, cavity, wound);
		CreateMeshObject("Canals Isolated", root.transform, Vector3.right * 0.24f, UnityEngine.Object.Instantiate(cavity), wound);
		Selection.activeGameObject = root;
	}
}
}
#endif
