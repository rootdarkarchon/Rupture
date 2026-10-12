#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs
{
	internal static class BenchmarkAndPreviewProceduralLimbWounds
	{
		private const string PreviewRootName = "RPT Limb Wound Preview";

		[Serializable]
		private sealed class Report
		{
			public string unityVersion;
			public string generatedUtc;
			public bool allPassed;
			public Case[] cases;
		}

		[Serializable]
		private sealed class Case
		{
			public string name;
			public uint seed;
			public bool leg;
			public string level;
			public int bodyVertices;
			public int gibVertices;
			public int bodyTriangles;
			public int gibTriangles;
			public int bonesPerSide;
			public int bodyFlaps;
			public int gibFlaps;
			public int bodyCords;
			public int gibCords;
			public int bodyDecals;
			public int gibDecals;
			public float bodyPlateauMillimeters;
			public float gibPlateauMillimeters;
			public float maximumReachMillimeters;
			public float minimumFractureSpanMillimeters;
			public float minimumAdornmentEmbedMillimeters;
			public float minimumBoneEmbedMillimeters;
			public float minimumBoneExposedMillimeters;
			public bool anatomicalBoneDimensionsValid;
			public double milliseconds;
			public bool valid;
			public string failure;
		}

		[MenuItem("Tools/Rupture/Preview Limb Sever Wounds/Create Matrix")]
		internal static void CreatePreview()
		{
			ClearPreview();
			GameObject root = new GameObject(PreviewRootName);
			Undo.RegisterCreatedObjectUndo(root, "Create limb wound preview");
			Material shaded = CreatePreviewMaterial();
			Material wire = CreateWireMaterial();
			(bool Leg, LimbSeverLevel Level, string Name)[] cuts =
			{
				(false, LimbSeverLevel.Proximal, "Upper Arm"),
				(false, LimbSeverLevel.Distal, "Lower Arm"),
				(true, LimbSeverLevel.Proximal, "Upper Leg"),
				(true, LimbSeverLevel.Distal, "Lower Leg")
			};
			uint[] seeds = { 0x10203040u, 0x55667788u, 0xC0FFEE11u };
			for (int row = 0; row < cuts.Length; row++)
				for (int column = 0; column < seeds.Length; column++)
				{
					Vector3 origin = new Vector3(column * 0.62f, -row * 0.38f, 0f);
					GameObject example = new GameObject($"{cuts[row].Name} seed {seeds[column]:X8}");
					example.transform.SetParent(root.transform, false);
					example.transform.localPosition = origin;
					CreatePair(example.transform, cuts[row].Leg, cuts[row].Level, seeds[column], shaded, wire);
				}
			Selection.activeGameObject = root;
			SceneView.lastActiveSceneView?.FrameSelected();
		}

		[MenuItem("Tools/Rupture/Preview Limb Sever Wounds/Clear")]
		internal static void ClearPreview()
		{
			GameObject existing = GameObject.Find(PreviewRootName);
			if (existing != null) Undo.DestroyObjectImmediate(existing);
		}

		[MenuItem("Tools/Rupture/Benchmark Limb Sever Wounds")]
		public static void RunBenchmark()
		{
			List<Case> cases = new List<Case>();
			(bool Leg, LimbSeverLevel Level, string Name)[] cuts =
			{
				(false, LimbSeverLevel.Proximal, "upper-arm"),
				(false, LimbSeverLevel.Distal, "lower-arm"),
				(true, LimbSeverLevel.Proximal, "upper-leg"),
				(true, LimbSeverLevel.Distal, "lower-leg")
			};
			uint[] seeds = { 0x10203040u, 0x55667788u, 0xC0FFEE11u };
			foreach (var cut in cuts)
				foreach (uint seed in seeds) cases.Add(RunCase(cut.Name, cut.Leg, cut.Level, seed));
			Report report = new Report
			{
				unityVersion = Application.unityVersion,
				generatedUtc = DateTime.UtcNow.ToString("O"),
				allPassed = cases.All(item => item.valid),
				cases = cases.ToArray()
			};
			string outputDirectory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "AssetBundles");
			Directory.CreateDirectory(outputDirectory);
			string path = Path.Combine(outputDirectory, "rupture-limb-wound.benchmark.json");
			File.WriteAllText(path, JsonUtility.ToJson(report, true));
			AssetDatabase.Refresh();
			UnityEngine.Debug.Log("Rupture limb-wound benchmark:\n" + JsonUtility.ToJson(report, true));
			if (!report.allPassed) throw new InvalidOperationException("Procedural limb-wound benchmark failed. See " + path);
		}

		private static Case RunCase(string name, bool leg, LimbSeverLevel level, uint seed)
		{
			Case result = new Case { name = name, seed = seed, leg = leg, level = level.ToString() };
			try
			{
				Stopwatch timer = Stopwatch.StartNew();
				if (!TryPair(leg, level, seed, false, out ProceduralLimbWoundGeometry.Surface body,
					out ProceduralLimbWoundGeometry.Surface gib, out string failure)) throw new InvalidDataException(failure);
				// An overlapping shell exercises closure without duplicating primary details.
				if (!TryPair(leg, level, seed ^ 0x8A5CD789u, true, out _, out _, out failure))
					throw new InvalidDataException("overlapping shell: " + failure);
				timer.Stop();
				result.bodyVertices = body.Vertices.Length;
				result.gibVertices = gib.Vertices.Length;
				result.bodyTriangles = body.Triangles.Length / 3;
				result.gibTriangles = gib.Triangles.Length / 3;
				result.bonesPerSide = body.BoneCount;
				result.bodyFlaps = body.FlapCount;
				result.gibFlaps = gib.FlapCount;
				result.bodyCords = body.CordCount;
				result.gibCords = gib.CordCount;
				result.bodyDecals = body.DecalSamples.Length;
				result.gibDecals = gib.DecalSamples.Length;
				result.bodyPlateauMillimeters = body.MeanPlateauProtrusionWorld * 1000f;
				result.gibPlateauMillimeters = gib.MeanPlateauProtrusionWorld * 1000f;
				result.maximumReachMillimeters = Mathf.Max(body.MaximumProtrusionWorld, gib.MaximumProtrusionWorld) * 1000f;
				result.minimumFractureSpanMillimeters = Mathf.Min(body.MinimumBoneFractureSpanWorld,
					gib.MinimumBoneFractureSpanWorld) * 1000f;
				result.minimumAdornmentEmbedMillimeters = Mathf.Min(body.MinimumAdornmentEmbedWorld,
					gib.MinimumAdornmentEmbedWorld) * 1000f;
				result.minimumBoneEmbedMillimeters = Mathf.Min(body.MinimumBoneEmbedWorld,
					gib.MinimumBoneEmbedWorld) * 1000f;
				result.minimumBoneExposedMillimeters = Mathf.Min(body.MinimumBoneExposedReachWorld,
					gib.MinimumBoneExposedReachWorld) * 1000f;
				result.anatomicalBoneDimensionsValid = BoneDimensionsValid(body, leg, level) &&
					BoneDimensionsValid(gib, leg, level);
				result.milliseconds = timer.Elapsed.TotalMilliseconds;
				int expectedBones = level == LimbSeverLevel.Proximal ? 1 : 2;
				int expectedDecals = leg ? 8 : 6;
				result.valid = result.bonesPerSide == expectedBones && gib.BoneCount == expectedBones &&
					body.DecorationsClosed && gib.DecorationsClosed && result.bodyDecals == expectedDecals &&
					result.gibDecals == expectedDecals && result.bodyPlateauMillimeters > 0f &&
					result.gibPlateauMillimeters > 0f && Mathf.Abs(result.bodyPlateauMillimeters - result.gibPlateauMillimeters) > 0.01f &&
					result.maximumReachMillimeters <= ProceduralLimbWoundGeometry.MaximumAdornmentReachWorld * 1000f + 0.01f &&
					result.minimumFractureSpanMillimeters > 0.5f && result.minimumAdornmentEmbedMillimeters >= 3.99f &&
					result.minimumBoneEmbedMillimeters >= (leg ? 7.99f : 5.99f) &&
					result.minimumBoneExposedMillimeters >= 5.99f && result.anatomicalBoneDimensionsValid &&
					result.milliseconds < 25d;
			}
			catch (Exception exception)
			{
				result.failure = exception.GetType().Name + ": " + exception.Message;
				result.valid = false;
			}
			return result;
		}

		private static bool BoneDimensionsValid(ProceduralLimbWoundGeometry.Surface surface, bool leg,
			LimbSeverLevel level)
		{
			int expected = level == LimbSeverLevel.Proximal ? 1 : 2;
			if (surface?.BoneRadiiWorld == null || surface.BoneRadiiWorld.Length != expected) return false;
			for (int index = 0; index < expected; index++)
			{
				float[] baseline = ProceduralLimbWoundGeometry.BoneDiameterBaselineForTests(leg, level, index);
				Vector2 actual = surface.BoneRadiiWorld[index] * 2f;
				if (actual.x < baseline[0] * 0.8499f || actual.x > baseline[0] * 1.1501f ||
					actual.y < baseline[1] * 0.8499f || actual.y > baseline[1] * 1.1501f) return false;
			}
			return true;
		}

		private static void CreatePair(Transform parent, bool leg, LimbSeverLevel level, uint seed,
			Material shaded, Material wire)
		{
			if (!TryPair(leg, level, seed, false, out ProceduralLimbWoundGeometry.Surface body,
				out ProceduralLimbWoundGeometry.Surface gib, out string failure))
				throw new InvalidDataException(failure);
			CreateSurface(parent, "Body wound shaded", body, shaded, Vector3.zero, false);
			CreateSurface(parent, "Gib wound shaded", gib, shaded, Vector3.zero, false);
			CreateSurface(parent, "Body wound wire", body, wire, new Vector3(0f, 0f, 0.17f), true);
			CreateSurface(parent, "Gib wound wire", gib, wire, new Vector3(0f, 0f, 0.17f), true);
			if (TryPair(leg, level, seed ^ 0x8A5CD789u, true, out ProceduralLimbWoundGeometry.Surface bodyShell,
				out ProceduralLimbWoundGeometry.Surface gibShell, out _))
			{
				CreateSurface(parent, "Overlapping body clothing shell", bodyShell, shaded, Vector3.zero, false);
				CreateSurface(parent, "Overlapping gib clothing shell", gibShell, shaded, Vector3.zero, false);
			}
		}

		private static bool TryPair(bool leg, LimbSeverLevel level, uint seed, bool shell,
			out ProceduralLimbWoundGeometry.Surface body, out ProceduralLimbWoundGeometry.Surface gib,
			out string failure)
		{
			float radiusY = (leg ? 0.075f : 0.052f) * (shell ? 1.09f : 1f);
			float radiusZ = (leg ? 0.061f : 0.043f) * (shell ? 1.09f : 1f);
			var bodyBoundary = ProceduralLimbWoundGeometry.DisturbBoundary(
				BuildBoundary(0f, radiusY, radiusZ, Vector3.left), Vector3.right, 1f, leg, seed, gibSide: false);
			var gibBoundary = ProceduralLimbWoundGeometry.DisturbBoundary(
				BuildBoundary(ProceduralLimbWoundGeometry.MissingBandWorld, radiusY, radiusZ, Vector3.right),
				Vector3.left, 1f, leg, seed, gibSide: true);
			bool primary = !shell;
			if (!ProceduralLimbWoundGeometry.TryGenerate(new ProceduralLimbWoundGeometry.Request
			{
				Boundary = bodyBoundary, FacingNormal = Vector3.right, StableCutAxis = Vector3.right,
				WorldScale = 1f, IsLeg = leg, Level = level, Seed = seed,
				GibSide = false, AddAnatomicalDetails = primary
			}, out body, out failure)) { gib = null; return false; }
			if (!ProceduralLimbWoundGeometry.TryGenerate(new ProceduralLimbWoundGeometry.Request
			{
				Boundary = gibBoundary, FacingNormal = Vector3.left, StableCutAxis = Vector3.right,
				WorldScale = 1f, IsLeg = leg, Level = level, Seed = seed,
				GibSide = true, AddAnatomicalDetails = primary
			}, out gib, out failure)) return false;
			return true;
		}

		private static ProceduralLimbWoundGeometry.BoundaryVertex[] BuildBoundary(float x, float radiusY,
			float radiusZ, Vector3 retainedNormal)
		{
			const int count = 28;
			var boundary = new ProceduralLimbWoundGeometry.BoundaryVertex[count];
			for (int index = 0; index < count; index++)
			{
				float angle = Mathf.PI * 2f * index / count;
				boundary[index] = new ProceduralLimbWoundGeometry.BoundaryVertex(
					new Vector3(x, Mathf.Cos(angle) * radiusY, Mathf.Sin(angle) * radiusZ), retainedNormal,
					new BoneWeight { boneIndex0 = 0, weight0 = 1f });
			}
			return boundary;
		}

		private static void CreateSurface(Transform parent, string name,
			ProceduralLimbWoundGeometry.Surface surface, Material material, Vector3 offset, bool wire)
		{
			Mesh mesh = new Mesh { name = name };
			mesh.SetVertices(surface.Vertices.Select(item => item.Position + offset).ToList());
			mesh.SetNormals(surface.Vertices.Select(item => item.Normal).ToList());
			mesh.SetUVs(0, surface.Vertices.Select(item => item.Uv).ToList());
			mesh.SetColors(surface.Vertices.Select(item => item.Mask).ToList());
			if (wire)
			{
				HashSet<(int, int)> edges = new HashSet<(int, int)>();
				for (int index = 0; index + 2 < surface.Triangles.Length; index += 3)
				{
					AddEdge(edges, surface.Triangles[index], surface.Triangles[index + 1]);
					AddEdge(edges, surface.Triangles[index + 1], surface.Triangles[index + 2]);
					AddEdge(edges, surface.Triangles[index + 2], surface.Triangles[index]);
				}
				mesh.SetIndices(edges.SelectMany(edge => new[] { edge.Item1, edge.Item2 }).ToArray(), MeshTopology.Lines, 0);
			}
			else mesh.SetTriangles(surface.Triangles, 0);
			mesh.RecalculateBounds();
			GameObject gameObject = new GameObject(name);
			gameObject.transform.SetParent(parent, false);
			gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
			gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
		}

		private static void AddEdge(HashSet<(int, int)> edges, int a, int b) => edges.Add(a < b ? (a, b) : (b, a));

		private static Material CreatePreviewMaterial()
		{
			Shader shader = Shader.Find("Rupture/LimbSeverSurface") ?? Shader.Find("Standard");
			Material material = new Material(shader) { name = "RPT Limb Wound Preview Material" };
			Texture2D flesh = SolidTexture(new Color(0.42f, 0.10f, 0.08f));
			Texture2D blood = SolidTexture(new Color(0.18f, 0.005f, 0.003f));
			material.SetTexture("_FleshTex", flesh);
			material.SetTexture("_BloodTex", blood);
			return material;
		}

		private static Material CreateWireMaterial()
		{
			Material material = new Material(Shader.Find("Hidden/Internal-Colored"));
			material.SetColor("_Color", new Color(1f, 0.65f, 0.1f, 1f));
			return material;
		}

		private static Texture2D SolidTexture(Color color)
		{
			Texture2D texture = new Texture2D(2, 2);
			texture.SetPixels(new[] { color, color * 0.75f, color * 1.12f, color * 0.9f });
			texture.Apply();
			return texture;
		}
	}
}
#endif
