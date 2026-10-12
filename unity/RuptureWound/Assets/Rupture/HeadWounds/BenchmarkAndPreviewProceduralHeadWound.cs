#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.HeadWounds
{

internal static partial class BenchmarkRuptureHeadWound
{
	[Serializable]
	private sealed class BenchmarkReport
	{
		public string unityVersion;
		public string generatedUtc;
		public bool allPassed;
		public bool index32Passed;
		public BenchmarkCase[] cases;
		public BenchmarkCase[] multipleCases;
	}

	[Serializable]
	private sealed class BenchmarkCase
	{
		public string name;
		public double singlesMs, skinMs, canalsMs;
		public int sourceVertexCount;
		public int sourceTriangleCount;
		public int candidateTriangleCount;
		public int intersectedSourceTriangleCount;
		public int clippedTriangleCount;
		public int removedTriangleCount;
		public int generatedHeadVertexCount;
		public double entryOpeningCoverageRadiusMillimeters;
		public double exitOpeningCoverageRadiusMillimeters;
		public double entryRimOuterRadiusMillimeters;
		public double exitRimOuterRadiusMillimeters;
		public double minimumRimCoverageMarginMillimeters;
		public int rimVertexCount;
		public int rimTriangleCount;
		public bool entryLipStitched;
		public bool exitLipStitched;
		public double minimumEntryRimFacingDot;
		public double minimumExitRimFacingDot;
		public int entryRimSurfaceHitCount;
		public int exitRimSurfaceHitCount;
		public int boundaryComponentCount;
		public int openBoundaryComponentCount;
		public int solidCanalSpanCount;
		public int entryLipCount;
		public int intermediateLipCount;
		public int exitFlapCount;
		public int resultHeadVertexCount;
		public int resultHeadTriangleCount;
		public int cavityVertexCount;
		public int cavityTriangleCount;
		public int generatedWoundVertexCount;
		public int generatedWoundTriangleCount;
		public double bakeOrAcquisitionMilliseconds;
		public double triangleClassificationMilliseconds;
		public double cavityGenerationMilliseconds;
		public double unityMeshApplicationMilliseconds;
		public double totalMilliseconds;
		public long managedDeltaBytes;
		public double maximumGeometryMilliseconds;
		public double maximumTotalMilliseconds;
		public long maximumManagedBytes;
		public bool valid;
		public string failure;
	}

	private static readonly Vector3 HeadRadii = new Vector3(0.09f, 0.12f, 0.105f);
	private const uint BenchmarkSeed = 0xC001CAFEu;

	[MenuItem("Tools/Rupture/Benchmark Head Wound Cutter")]
	public static void RunFromMenu()
	{
		RunAndWriteReport(throwOnFailure: true);
	}

	public static void RunBatch()
	{
		RunAndWriteReport(throwOnFailure: true);
	}

	private static void RunAndWriteReport(bool throwOnFailure)
	{
		BenchmarkReport report = new BenchmarkReport
		{
			unityVersion = Application.unityVersion,
			generatedUtc = DateTime.UtcNow.ToString("O"),
			index32Passed = ValidateThirtyTwoBitFiltering(),
			multipleCases = RunMultipleCases(),
			cases = new[]
			{
				RunCase("common-head", 64, 32, CreateThroughDescriptor(), 15d, 25d, 512L * 1024L),
				RunCase("high-vertex-head-oblique", 192, 96, CreateObliqueThroughDescriptor(), 35d, 50d, 1024L * 1024L),
				RunCase("separated-shell-crossings", 48, 24, CreateSeparatedShellDescriptor(), 20d, 30d,
					768L * 1024L, separatedShells: true)
			}
		};
		report.allPassed = report.index32Passed && report.multipleCases.All(result => result.valid && result.totalMilliseconds <= result.maximumTotalMilliseconds && result.triangleClassificationMilliseconds <= result.maximumGeometryMilliseconds && result.managedDeltaBytes <= result.maximumManagedBytes) && report.cases.All(result => result.valid &&
			result.triangleClassificationMilliseconds + result.cavityGenerationMilliseconds <= result.maximumGeometryMilliseconds &&
			result.totalMilliseconds <= result.maximumTotalMilliseconds && result.managedDeltaBytes <= result.maximumManagedBytes);

		string outputDirectory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "AssetBundles");
		Directory.CreateDirectory(outputDirectory);
		string reportPath = Path.Combine(outputDirectory, "rupture-wound.benchmark.json");
		File.WriteAllText(reportPath, JsonUtility.ToJson(report, prettyPrint: true));
		UnityEngine.Debug.Log("Rupture specialized head-wound benchmark:\n" + JsonUtility.ToJson(report, prettyPrint: true));
		AssetDatabase.Refresh();

		if (!report.allPassed && throwOnFailure)
		{
			throw new InvalidOperationException("Specialized head-wound cutter failed validation, allocation, or timing gates. See " + reportPath);
		}
	}

	private static BenchmarkCase RunCase(string name, int longitudeSegments, int latitudeSegments,
		ProceduralWoundDescriptor descriptor,
		double maximumGeometryMilliseconds, double maximumTotalMilliseconds, long maximumManagedBytes,
		bool separatedShells = false)
	{
		Mesh source = null;
		Mesh resultHead = null;
		Mesh cavity = null;
		BenchmarkCase result = new BenchmarkCase
		{
			name = name,
			maximumGeometryMilliseconds = maximumGeometryMilliseconds,
			maximumTotalMilliseconds = maximumTotalMilliseconds,
			maximumManagedBytes = maximumManagedBytes
		};
		try
		{
			source = separatedShells
				? BuildSeparatedShells(longitudeSegments, latitudeSegments)
				: BuildEllipsoid(longitudeSegments, latitudeSegments, HeadRadii);
			resultHead = UnityEngine.Object.Instantiate(source);
			resultHead.name = "Benchmark Filtered " + name;
			ProceduralHeadWoundWorkspace workspace = new ProceduralHeadWoundWorkspace();

			// Size and warm all reusable buffers before measuring the steady-state operation.
			if (!workspace.AcquireFromMeshOnMainThread(source, out string warmFailure) ||
				!ProceduralHeadWoundGeometry.TryGenerate(workspace, descriptor, out _, out warmFailure))
			{
				throw new InvalidDataException("warm-up failed: " + warmFailure);
			}

			result.sourceVertexCount = source.vertexCount;
			result.sourceTriangleCount = CountTriangles(source);
			GC.Collect();
			long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			Stopwatch total = Stopwatch.StartNew();

			Stopwatch acquisition = Stopwatch.StartNew();
			if (!workspace.AcquireFromMeshOnMainThread(resultHead, out string failure))
			{
				throw new InvalidDataException(failure);
			}
			acquisition.Stop();
			result.bakeOrAcquisitionMilliseconds = acquisition.Elapsed.TotalMilliseconds;

			if (!ProceduralHeadWoundGeometry.TryGenerate(workspace, descriptor, out ProceduralWoundMetrics metrics, out failure))
			{
				throw new InvalidDataException(failure);
			}
			result.triangleClassificationMilliseconds = metrics.TriangleClassificationMilliseconds;
			result.cavityGenerationMilliseconds = metrics.CavityGenerationMilliseconds;

			Stopwatch application = Stopwatch.StartNew();
			workspace.ApplyFilteredIndicesOnMainThread(resultHead);
			cavity = workspace.CreateCavityMeshOnMainThread("Benchmark Cavity " + name);
			application.Stop();
			result.unityMeshApplicationMilliseconds = application.Elapsed.TotalMilliseconds;
			total.Stop();

			result.totalMilliseconds = total.Elapsed.TotalMilliseconds;
			result.managedDeltaBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
			result.candidateTriangleCount = metrics.CandidateTriangleCount;
			result.intersectedSourceTriangleCount = metrics.IntersectedSourceTriangleCount;
			result.clippedTriangleCount = metrics.ClippedTriangleCount;
			result.removedTriangleCount = metrics.RemovedTriangleCount;
			result.generatedHeadVertexCount = metrics.GeneratedHeadVertexCount;
			result.entryOpeningCoverageRadiusMillimeters = metrics.EntryOpeningCoverageRadius * 1000.0;
			result.exitOpeningCoverageRadiusMillimeters = metrics.ExitOpeningCoverageRadius * 1000.0;
			result.entryRimOuterRadiusMillimeters = metrics.EntryRimOuterRadius * 1000.0;
			result.exitRimOuterRadiusMillimeters = metrics.ExitRimOuterRadius * 1000.0;
			result.minimumRimCoverageMarginMillimeters = metrics.MinimumRimCoverageMargin * 1000.0;
			result.rimVertexCount = metrics.RimVertexCount;
			result.rimTriangleCount = metrics.RimTriangleCount;
			result.entryLipStitched = metrics.EntryLipStitched;
			result.exitLipStitched = metrics.ExitLipStitched;
			result.minimumEntryRimFacingDot = metrics.MinimumEntryRimFacingDot;
			result.minimumExitRimFacingDot = metrics.MinimumExitRimFacingDot;
			result.entryRimSurfaceHitCount = metrics.EntryRimSurfaceHitCount;
			result.exitRimSurfaceHitCount = metrics.ExitRimSurfaceHitCount;
			result.boundaryComponentCount = metrics.BoundaryComponentCount;
			result.openBoundaryComponentCount = metrics.OpenBoundaryComponentCount;
			result.solidCanalSpanCount = metrics.SolidCanalSpanCount;
			result.entryLipCount = metrics.EntryLipCount;
			result.intermediateLipCount = metrics.IntermediateLipCount;
			result.exitFlapCount = metrics.ExitFlapCount;
			result.resultHeadVertexCount = resultHead.vertexCount;
			result.resultHeadTriangleCount = CountTriangles(resultHead);
			result.cavityVertexCount = metrics.CavityVertexCount;
			result.cavityTriangleCount = metrics.CavityTriangleCount;
			result.generatedWoundVertexCount = cavity.vertexCount;
			result.generatedWoundTriangleCount = CountTriangles(cavity);
			result.valid = ValidateResult(source, resultHead, cavity, metrics, descriptor, out failure);
			if (result.valid && separatedShells &&
				(metrics.BoundaryComponentCount < 4 || metrics.SolidCanalSpanCount < 2 ||
				 metrics.IntermediateLipCount < 2))
			{
				result.valid = false;
				failure = "separated shells were bridged instead of producing independent solid canal spans";
			}
			result.failure = failure;
		}
		catch (Exception exception)
		{
			result.valid = false;
			result.failure = exception.GetType().Name + ": " + exception.Message;
		}
		finally
		{
			if (source != null) UnityEngine.Object.DestroyImmediate(source);
			if (resultHead != null) UnityEngine.Object.DestroyImmediate(resultHead);
			if (cavity != null) UnityEngine.Object.DestroyImmediate(cavity);
		}
		return result;
	}

	private static bool ValidateThirtyTwoBitFiltering()
	{
		Mesh source = null;
		Mesh result = null;
		try
		{
			source = BuildEllipsoid(384, 192, HeadRadii);
			result = UnityEngine.Object.Instantiate(source);
			ProceduralHeadWoundWorkspace workspace = new ProceduralHeadWoundWorkspace();
			if (!workspace.AcquireFromMeshOnMainThread(result, out string acquisitionFailure))
			{
				UnityEngine.Debug.LogError("32-bit wound smoke acquisition failed: " + acquisitionFailure);
				return false;
			}
			if (!ProceduralHeadWoundGeometry.TryGenerate(workspace, CreateIndex32Descriptor(),
				out _, out string generationFailure))
			{
				UnityEngine.Debug.LogError("32-bit wound smoke generation failed: " + generationFailure);
				return false;
			}
			workspace.ApplyFilteredIndicesOnMainThread(result);
			return source.vertexCount > ushort.MaxValue && result.vertexCount > source.vertexCount &&
				source.indexFormat == IndexFormat.UInt32 && result.indexFormat == IndexFormat.UInt32;
		}
		finally
		{
			if (source != null) UnityEngine.Object.DestroyImmediate(source);
			if (result != null) UnityEngine.Object.DestroyImmediate(result);
		}
	}

	private static bool ValidateResult(Mesh source, Mesh resultHead, Mesh cavity, ProceduralWoundMetrics metrics,
		ProceduralWoundDescriptor descriptor, out string failure)
	{
		failure = null;
		if (resultHead == null || cavity == null ||
			resultHead.vertexCount != source.vertexCount + metrics.GeneratedHeadVertexCount ||
			metrics.GeneratedHeadVertexCount <= 0 ||
			metrics.GeneratedHeadVertexCount > ProceduralHeadWoundGeometry.MaximumGeneratedHeadVertices)
		{
			failure = "locally clipped head violated its bounded source-plus-generated vertex contract";
			return false;
		}
		if (!SameSourceVertexPrefix(source, resultHead))
		{
			failure = "local clipping changed pre-existing source positions, normals, tangents, or UVs";
			return false;
		}
		if (metrics.SourceTriangleCount != CountTriangles(source) || metrics.CandidateTriangleCount <= 0 ||
			metrics.IntersectedSourceTriangleCount <= 0 || metrics.ClippedTriangleCount <= 0 ||
			metrics.RemovedTriangleCount <= 0 || CountTriangles(resultHead) != metrics.ResultHeadTriangleCount)
		{
			failure = "triangle classification counts are inconsistent";
			return false;
		}
		if (metrics.EntryOpeningCoverageRadius <= 0f || metrics.ExitOpeningCoverageRadius <= 0f ||
			metrics.MinimumRimCoverageMargin < ProceduralHeadWoundGeometry.MinimumRimCoverageMargin ||
			metrics.RimVertexCount <= 0 || metrics.RimTriangleCount <= 0 ||
			metrics.BoundaryComponentCount < 2 || metrics.SolidCanalSpanCount <= 0 ||
			metrics.EntryLipCount != 1 ||
			metrics.ExitFlapCount != ProceduralHeadWoundGeometry.ExitFlapCount ||
			metrics.MinimumEntryRimFacingDot < 0f || metrics.MinimumExitRimFacingDot < 0f ||
			metrics.EntryRimSurfaceHitCount <= 0 || metrics.ExitRimSurfaceHitCount <= 0)
		{
			failure = "component-driven canal, independent entry lip, or thick terminal exit collar/flaps are missing or unbounded";
			return false;
		}
		if (cavity.vertexCount != metrics.CavityVertexCount + metrics.RimVertexCount ||
			CountTriangles(cavity) != metrics.CavityTriangleCount + metrics.RimTriangleCount ||
			cavity.vertexCount > ProceduralHeadWoundGeometry.MaximumCavityVertices)
		{
			failure = "cavity geometry is missing or unbounded";
			return false;
		}
		foreach (Vector3 vertex in cavity.vertices)
		{
			if (!Finite(vertex))
			{
				failure = "cavity contains a non-finite vertex";
				return false;
			}
		}
		Vector3 direction = (descriptor.Exit - descriptor.Entry).normalized;
		Vector3 origin = descriptor.Entry - direction * 0.003f;
		float distance = Vector3.Distance(descriptor.Entry, descriptor.Exit) + 0.006f;
		int headIntersections = CountRayIntersections(resultHead, origin, direction, distance);
		int woundIntersections = CountRayIntersections(cavity, origin, direction, distance);
		if (headIntersections != 0 || woundIntersections != 0)
		{
			failure = $"through-shot centerline remains blocked (head={headIntersections}, wound={woundIntersections}; " +
				DescribeFirstRayIntersection(cavity, origin, direction, distance) + ")";
			return false;
		}
		return true;
	}

	internal static ProceduralWoundDescriptor CreateThroughDescriptor()
	{
		return new ProceduralWoundDescriptor(new Vector3(0f, 0f, -HeadRadii.z),
			new Vector3(0f, 0f, HeadRadii.z), 0.006f, 0.024f, BenchmarkSeed, throughShot: true);
	}

	private static ProceduralWoundDescriptor CreateIndex32Descriptor()
	{
		return new ProceduralWoundDescriptor(new Vector3(-HeadRadii.x, 0f, 0f),
			new Vector3(0.025f, 0f, 0f), 0.006f, 0.018f,
			BenchmarkSeed ^ 0x6A09E667u, throughShot: false, generateExitLip: false);
	}

	internal static ProceduralWoundDescriptor CreateNonThroughDescriptor()
	{
		return new ProceduralWoundDescriptor(new Vector3(0f, 0f, -HeadRadii.z),
			new Vector3(0f, 0f, 0.025f), 0.006f, 0.018f, BenchmarkSeed ^ 0x82A91F3Du, throughShot: false);
	}

	internal static ProceduralWoundDescriptor CreateObliqueThroughDescriptor()
	{
		Vector3 direction = new Vector3(0.12f, 0.72f, 0.68f).normalized;
		float inverseRadiusSquared = direction.x * direction.x / (HeadRadii.x * HeadRadii.x) +
			direction.y * direction.y / (HeadRadii.y * HeadRadii.y) +
			direction.z * direction.z / (HeadRadii.z * HeadRadii.z);
		float distance = 1f / Mathf.Sqrt(inverseRadiusSquared);
		return new ProceduralWoundDescriptor(-direction * distance, direction * distance,
			0.006f, 0.024f, BenchmarkSeed ^ 0x4D31A68Bu, throughShot: true);
	}

	internal static ProceduralWoundDescriptor CreateSeparatedShellDescriptor()
	{
		return new ProceduralWoundDescriptor(new Vector3(0f, 0f, -0.115f),
			new Vector3(0f, 0f, 0.115f), 0.006f, 0.018f,
			BenchmarkSeed ^ 0x6A09E667u, throughShot: true);
	}

	internal static Mesh BuildSeparatedShells(int longitudeSegments, int latitudeSegments)
	{
		Mesh first = BuildEllipsoid(longitudeSegments, latitudeSegments, new Vector3(0.07f, 0.09f, 0.045f));
		Mesh second = BuildEllipsoid(longitudeSegments, latitudeSegments, new Vector3(0.075f, 0.095f, 0.045f));
		try
		{
			Mesh combined = new Mesh { name = "Separated Head-Surface Crossings" };
			combined.indexFormat = first.vertexCount + second.vertexCount > ushort.MaxValue
				? IndexFormat.UInt32
				: IndexFormat.UInt16;
			CombineInstance[] instances =
			{
				new CombineInstance { mesh = first, transform = Matrix4x4.Translate(new Vector3(0f, 0f, -0.07f)) },
				new CombineInstance { mesh = second, transform = Matrix4x4.Translate(new Vector3(0f, 0f, 0.07f)) }
			};
			combined.CombineMeshes(instances, mergeSubMeshes: true, useMatrices: true, hasLightmapData: false);
			return combined;
		}
		finally
		{
			UnityEngine.Object.DestroyImmediate(first);
			UnityEngine.Object.DestroyImmediate(second);
		}
	}

	internal static Mesh BuildEllipsoid(int longitudeSegments, int latitudeSegments, Vector3 radii)
	{
		List<Vector3> vertices = new List<Vector3>(2 + (latitudeSegments - 1) * longitudeSegments);
		List<Vector3> normals = new List<Vector3>(vertices.Capacity);
		List<Vector2> uvs = new List<Vector2>(vertices.Capacity);
		List<int> triangles = new List<int>(longitudeSegments * (latitudeSegments - 1) * 6);
		vertices.Add(new Vector3(0f, radii.y, 0f));
		normals.Add(Vector3.up);
		uvs.Add(new Vector2(0.5f, 1f));
		for (int latitude = 1; latitude < latitudeSegments; latitude++)
		{
			float v = latitude / (float)latitudeSegments;
			float polar = v * Mathf.PI;
			float y = Mathf.Cos(polar);
			float ring = Mathf.Sin(polar);
			for (int longitude = 0; longitude < longitudeSegments; longitude++)
			{
				float u = longitude / (float)longitudeSegments;
				float azimuth = u * Mathf.PI * 2f;
				Vector3 unit = new Vector3(ring * Mathf.Cos(azimuth), y, ring * Mathf.Sin(azimuth));
				vertices.Add(Vector3.Scale(unit, radii));
				normals.Add(new Vector3(unit.x / radii.x, unit.y / radii.y, unit.z / radii.z).normalized);
				uvs.Add(new Vector2(u, 1f - v));
			}
		}
		int bottomIndex = vertices.Count;
		vertices.Add(new Vector3(0f, -radii.y, 0f));
		normals.Add(Vector3.down);
		uvs.Add(new Vector2(0.5f, 0f));

		for (int longitude = 0; longitude < longitudeSegments; longitude++)
		{
			int next = (longitude + 1) % longitudeSegments;
			triangles.Add(0); triangles.Add(1 + next); triangles.Add(1 + longitude);
		}
		for (int latitude = 0; latitude < latitudeSegments - 2; latitude++)
		{
			int currentRing = 1 + latitude * longitudeSegments;
			int nextRing = currentRing + longitudeSegments;
			for (int longitude = 0; longitude < longitudeSegments; longitude++)
			{
				int next = (longitude + 1) % longitudeSegments;
				triangles.Add(currentRing + longitude); triangles.Add(currentRing + next); triangles.Add(nextRing + longitude);
				triangles.Add(currentRing + next); triangles.Add(nextRing + next); triangles.Add(nextRing + longitude);
			}
		}
		int lastRing = 1 + (latitudeSegments - 2) * longitudeSegments;
		for (int longitude = 0; longitude < longitudeSegments; longitude++)
		{
			int next = (longitude + 1) % longitudeSegments;
			triangles.Add(lastRing + longitude); triangles.Add(lastRing + next); triangles.Add(bottomIndex);
		}

		Mesh mesh = new Mesh
		{
			name = "RPT Synthetic Head",
			indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
		};
		mesh.SetVertices(vertices);
		mesh.SetNormals(normals);
		mesh.SetUVs(0, uvs);
		mesh.SetTriangles(triangles, 0, calculateBounds: true);
		mesh.RecalculateTangents();
		return mesh;
	}

	internal static int CountTriangles(Mesh mesh)
	{
		int count = 0;
		for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
		{
			count += (int)mesh.GetIndexCount(submesh) / 3;
		}
		return count;
	}

	private static bool SameSourceVertexPrefix(Mesh source, Mesh result)
	{
		return SamePrefix(source.vertices, result.vertices) && SamePrefix(source.normals, result.normals) &&
			SamePrefix(source.tangents, result.tangents) && SamePrefix(source.uv, result.uv);
	}

	private static bool SamePrefix(Vector3[] left, Vector3[] right)
	{
		if (left.Length > right.Length) return false;
		for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
		return true;
	}

	private static bool SamePrefix(Vector4[] left, Vector4[] right)
	{
		if (left.Length > right.Length) return false;
		for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
		return true;
	}

	private static bool SamePrefix(Vector2[] left, Vector2[] right)
	{
		if (left.Length > right.Length) return false;
		for (int index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
		return true;
	}

	private static int CountRayIntersections(Mesh mesh, Vector3 origin, Vector3 direction, float maximumDistance)
	{
		int count = 0;
		Vector3[] vertices = mesh.vertices;
		int[] triangles = mesh.triangles;
		for (int index = 0; index + 2 < triangles.Length; index += 3)
		{
			if (RayTriangle(origin, direction, vertices[triangles[index]], vertices[triangles[index + 1]],
				vertices[triangles[index + 2]], out float distance) && distance <= maximumDistance)
			{
				count++;
			}
		}
		return count;
	}

	private static string DescribeFirstRayIntersection(Mesh mesh, Vector3 origin, Vector3 direction,
		float maximumDistance)
	{
		Vector3[] vertices = mesh.vertices;
		int[] triangles = mesh.triangles;
		for (int index = 0; index + 2 < triangles.Length; index += 3)
		{
			Vector3 a = vertices[triangles[index]];
			Vector3 b = vertices[triangles[index + 1]];
			Vector3 c = vertices[triangles[index + 2]];
			if (RayTriangle(origin, direction, a, b, c, out float distance) && distance <= maximumDistance)
			{
				return $"woundTriangle={index / 3}, hitDistance={distance:F5}, a={a}, b={b}, c={c}";
			}
		}
		return "no wound triangle detail";
	}

	private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
	{
		distance = 0f;
		Vector3 edge1 = b - a;
		Vector3 edge2 = c - a;
		Vector3 p = Vector3.Cross(direction, edge2);
		float determinant = Vector3.Dot(edge1, p);
		if (Mathf.Abs(determinant) < 0.0000001f) return false;
		float inverse = 1f / determinant;
		Vector3 t = origin - a;
		float u = Vector3.Dot(t, p) * inverse;
		if (u < 0f || u > 1f) return false;
		Vector3 q = Vector3.Cross(t, edge1);
		float v = Vector3.Dot(direction, q) * inverse;
		if (v < 0f || u + v > 1f) return false;
		distance = Vector3.Dot(edge2, q) * inverse;
		return distance >= 0f;
	}

	private static bool Finite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
			!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}

internal static partial class PreviewRuptureHeadWound
{
	private const string PreviewRootName = "RPT Procedural Head Wound Preview";

	[MenuItem("Tools/Rupture/Preview Head Wound/Common Through-Shot")]
	private static void PreviewCommonThrough()
	{
		CreatePreview(64, 32, BenchmarkRuptureHeadWound.CreateThroughDescriptor(), "Common Through-Shot");
	}

	[MenuItem("Tools/Rupture/Preview Head Wound/Common Non-Through")]
	private static void PreviewCommonNonThrough()
	{
		CreatePreview(64, 32, BenchmarkRuptureHeadWound.CreateNonThroughDescriptor(), "Common Non-Through");
	}

	[MenuItem("Tools/Rupture/Preview Head Wound/High-Vertex Oblique Through-Shot")]
	private static void PreviewHighThrough()
	{
		CreatePreview(192, 96, BenchmarkRuptureHeadWound.CreateObliqueThroughDescriptor(),
			"High-Vertex Oblique Through-Shot");
	}

	[MenuItem("Tools/Rupture/Preview Head Wound/Separated Surface Crossings")]
	private static void PreviewSeparatedCrossings()
	{
		CreatePreview(48, 24, BenchmarkRuptureHeadWound.CreateSeparatedShellDescriptor(),
			"Separated Surface Crossings", separatedShells: true);
	}

	[MenuItem("Tools/Rupture/Preview Head Wound/Clear Preview")]
	private static void ClearPreview()
	{
		GameObject existing = GameObject.Find(PreviewRootName);
		if (existing == null) return;
		HashSet<Mesh> meshes = new HashSet<Mesh>();
		HashSet<Material> materials = new HashSet<Material>();
		HashSet<Texture> textures = new HashSet<Texture>();
		foreach (MeshFilter filter in existing.GetComponentsInChildren<MeshFilter>(includeInactive: true))
		{
			if (filter.sharedMesh != null && !AssetDatabase.Contains(filter.sharedMesh)) meshes.Add(filter.sharedMesh);
		}
		foreach (Renderer renderer in existing.GetComponentsInChildren<Renderer>(includeInactive: true))
		{
			foreach (Material material in renderer.sharedMaterials)
			{
				if (material == null || AssetDatabase.Contains(material)) continue;
				materials.Add(material);
				foreach (string property in new[] { "_FleshTex", "_FleshNormal", "_BloodTex", "_BloodNormal" })
				{
					if (material.HasProperty(property) && material.GetTexture(property) is Texture texture && !AssetDatabase.Contains(texture)) textures.Add(texture);
				}
			}
		}
		UnityEngine.Object.DestroyImmediate(existing);
		foreach (Mesh mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
		foreach (Material material in materials) UnityEngine.Object.DestroyImmediate(material);
		foreach (Texture texture in textures) UnityEngine.Object.DestroyImmediate(texture);
	}

	private static void CreatePreview(int longitudeSegments, int latitudeSegments,
		ProceduralWoundDescriptor descriptor, string label, bool separatedShells = false)
	{
		ClearPreview();
		Mesh intact = separatedShells
			? BenchmarkRuptureHeadWound.BuildSeparatedShells(longitudeSegments, latitudeSegments)
			: BenchmarkRuptureHeadWound.BuildEllipsoid(longitudeSegments, latitudeSegments,
				new Vector3(0.09f, 0.12f, 0.105f));
		Mesh wounded = UnityEngine.Object.Instantiate(intact);
		wounded.name = "Filtered Preview Head";
		ProceduralHeadWoundWorkspace workspace = new ProceduralHeadWoundWorkspace();
		if (!workspace.AcquireFromMeshOnMainThread(wounded, out string failure) ||
			!ProceduralHeadWoundGeometry.TryGenerate(workspace, descriptor, out ProceduralWoundMetrics metrics, out failure))
		{
			UnityEngine.Object.DestroyImmediate(intact);
			UnityEngine.Object.DestroyImmediate(wounded);
			throw new InvalidOperationException("Preview generation failed: " + failure);
		}
		workspace.ApplyFilteredIndicesOnMainThread(wounded);
		Mesh cavity = workspace.CreateCavityMeshOnMainThread("Procedural Wound Cavity Preview");
		Mesh cavityOnly = UnityEngine.Object.Instantiate(cavity);
		Mesh lipOnly = CreateLipOnlyMesh(workspace, metrics);

		GameObject root = new GameObject(PreviewRootName + " - " + label);
		root.name = PreviewRootName;
		Undo.RegisterCreatedObjectUndo(root, "Create procedural head wound preview");
		Material skin = CreateSkinMaterial();
		Material wound = CreateWoundMaterial();
		Material lipDebug = CreateLipDebugMaterial();
		CreateMeshObject("1 - Intact Reference", root.transform, new Vector3(-0.24f, 0f, 0f), intact, skin);
		GameObject woundedObject = CreateMeshObject("2 - Filtered Head", root.transform, Vector3.zero, wounded, skin);
		CreateMeshObject("Wound Cavity", woundedObject.transform, Vector3.zero, cavity, wound);
		CreateMeshObject("3 - Cavity Only", root.transform, new Vector3(0.24f, 0f, 0f), cavityOnly, wound);
		CreateMeshObject("4 - Exterior Lip Only (Debug)", root.transform, new Vector3(0.48f, 0f, 0f), lipOnly, lipDebug);

		Selection.activeGameObject = root;
		SceneView scene = SceneView.lastActiveSceneView;
		if (scene != null)
		{
			scene.LookAt(new Vector3(0.12f, 0f, 0f), Quaternion.Euler(8f, 180f, 0f), 0.68f);
			scene.Repaint();
		}
		UnityEngine.Debug.Log($"Created {label} preview: sourceTriangles={metrics.SourceTriangleCount}, " +
			$"candidateTriangles={metrics.CandidateTriangleCount}, intersectedSourceTriangles={metrics.IntersectedSourceTriangleCount}, " +
			$"clippedTriangles={metrics.ClippedTriangleCount}, removedFragments={metrics.RemovedTriangleCount}, " +
			$"generatedHeadVertices={metrics.GeneratedHeadVertexCount}, resultHeadTriangles={metrics.ResultHeadTriangleCount}, " +
			$"entryCoverageMm={metrics.EntryOpeningCoverageRadius * 1000f:F3}, exitCoverageMm={metrics.ExitOpeningCoverageRadius * 1000f:F3}, " +
			$"entryLipRadiusMm={metrics.EntryRimOuterRadius * 1000f:F3}, exitBurstRadiusMm={metrics.ExitRimOuterRadius * 1000f:F3}, " +
			$"minimumOverlapMm={metrics.MinimumRimCoverageMargin * 1000f:F3}, rimVertices={metrics.RimVertexCount}, rimTriangles={metrics.RimTriangleCount}, " +
			$"boundaryComponents={metrics.BoundaryComponentCount}, openBoundaryComponents={metrics.OpenBoundaryComponentCount}, " +
			$"solidCanalSpans={metrics.SolidCanalSpanCount}, entryLips={metrics.EntryLipCount}, " +
			$"intermediateLips={metrics.IntermediateLipCount}, exitFlaps={metrics.ExitFlapCount}, " +
			$"entryRimFacing={metrics.MinimumEntryRimFacingDot:F3}, exitRimFacing={metrics.MinimumExitRimFacingDot:F3}, " +
			$"entrySurfaceSamples={metrics.EntryRimSurfaceHitCount}, exitSurfaceSamples={metrics.ExitRimSurfaceHitCount}, " +
			$"cavityVertices={metrics.CavityVertexCount}, cavityTriangles={metrics.CavityTriangleCount}, " +
			$"generatedWoundVertices={cavity.vertexCount}, generatedWoundTriangles={cavity.triangles.Length / 3}.");
	}

	private static Mesh CreateLipOnlyMesh(ProceduralHeadWoundWorkspace workspace, ProceduralWoundMetrics metrics)
	{
		Mesh mesh = new Mesh
		{
			name = "Procedural Exterior Lip Debug",
			indexFormat = workspace.CavityVertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
		};
		mesh.SetVertices(workspace.CavityVertices);
		mesh.SetNormals(workspace.CavityNormals);
		mesh.SetTangents(workspace.CavityTangents);
		mesh.SetUVs(0, workspace.CavityUvs);
		mesh.SetColors(workspace.CavityColors);
		int firstRimIndex = metrics.CavityTriangleCount * 3;
		List<int> triangles = workspace.CavityTriangles.GetRange(firstRimIndex,
			workspace.CavityTriangles.Count - firstRimIndex);
		mesh.SetTriangles(triangles, 0, calculateBounds: true);
		return mesh;
	}

	private static GameObject CreateMeshObject(string name, Transform parent, Vector3 localPosition, Mesh mesh, Material material)
	{
		GameObject result = new GameObject(name);
		result.transform.SetParent(parent, worldPositionStays: false);
		result.transform.localPosition = localPosition;
		result.AddComponent<MeshFilter>().sharedMesh = mesh;
		result.AddComponent<MeshRenderer>().sharedMaterial = material;
		return result;
	}

	private static Material CreateSkinMaterial()
	{
		Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
		Material material = new Material(shader) { name = "RPT Preview Skin" };
		material.color = new Color(0.72f, 0.47f, 0.38f, 1f);
		material.SetFloat("_Glossiness", 0.18f);
		return material;
	}

	private static Material CreateWoundMaterial()
	{
		Shader shader = Shader.Find("Rupture/HeadWoundInterior") ?? Shader.Find("Standard") ?? Shader.Find("Diffuse");
		Material material = new Material(shader) { name = "RPT Preview Wet Flesh" };
		if (shader.name == "Rupture/HeadWoundInterior")
		{
			material.SetTexture("_FleshTex", CreateColorTexture("Preview Flesh", new Color(0.38f, 0.08f, 0.06f), new Color(0.62f, 0.18f, 0.12f)));
			material.SetTexture("_BloodTex", CreateColorTexture("Preview Blood", new Color(0.18f, 0.005f, 0.008f), new Color(0.52f, 0.015f, 0.02f)));
			Texture2D normal = CreateFlatNormalTexture();
			material.SetTexture("_FleshNormal", normal);
			material.SetTexture("_BloodNormal", normal);
			material.SetFloat("_BloodAmount", 0.78f);
			material.SetFloat("_WetSmoothness", 0.9f);
		}
		else
		{
			material.color = new Color(0.36f, 0.015f, 0.02f, 1f);
			if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.85f);
		}
		return material;
	}

	private static Material CreateLipDebugMaterial()
	{
		Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
		Material material = new Material(shader) { name = "RPT Exterior Lip Debug" };
		material.color = new Color(0.95f, 0.03f, 0.015f, 1f);
		if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.72f);
		return material;
	}

	private static Texture2D CreateColorTexture(string name, Color first, Color second)
	{
		Texture2D texture = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: true) { name = name };
		Color[] colors = new Color[16];
		for (int index = 0; index < colors.Length; index++) colors[index] = ((index + index / 4) & 1) == 0 ? first : second;
		texture.SetPixels(colors);
		texture.Apply(updateMipmaps: true, makeNoLongerReadable: false);
		return texture;
	}

	private static Texture2D CreateFlatNormalTexture()
	{
		Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false) { name = "Preview Flat Normal" };
		texture.SetPixels(new[]
		{
			new Color(0.5f, 0.5f, 1f, 1f), new Color(0.55f, 0.45f, 1f, 1f),
			new Color(0.45f, 0.55f, 1f, 1f), new Color(0.5f, 0.5f, 1f, 1f)
		});
		texture.Apply();
		return texture;
	}
}
}
#endif
