using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class ProceduralLimbCutGeometry
{
	// Managed integration hook: real clipping -> exposed-boundary reconstruction
	// -> loop ordering -> rim -> wounds -> retained-triangle blood placement.
	// It never materializes a Unity object or invokes the native profiler.
	internal static string InspectClothingCutForTests(float[] flatPositions, int[] indices,
		int[] selectedTriangles, bool gib, bool isLeg, bool proximal, uint seed, out int[] metrics)
	{
		metrics = null;
		Vector3[] positions = ToVectors(flatPositions);
		SourceData source = new SourceData
		{
			Name = "clothing-fixture", Positions = positions, SubmeshCount = 1,
			Normals = positions.Select(p => new Vector3(p.x, p.y, 0f).normalized).ToArray(),
			Tangents = Array.Empty<Vector4>(), Uv0 = Array.Empty<Vector2>(), Uv1 = Array.Empty<Vector2>(),
			Uv2 = Array.Empty<Vector2>(), Uv3 = Array.Empty<Vector2>(), Colors = Array.Empty<Color>(),
			Weights = positions.Select(p => new BoneWeight { boneIndex0 = 0, weight0 = 1f }).ToArray(),
			Bindposes = new[] { Matrix4x4.identity }, BlendShapes = Array.Empty<BlendShapeFrame>(),
			Triangles = new List<SourceTriangle>()
		};
		for (int i = 0; i < indices.Length; i += 3)
			source.Triangles.Add(new SourceTriangle(indices[i], indices[i + 1], indices[i + 2], 0, i / 3));
		BindCut cut = new BindCut
		{
			Center = new Vector3(0, 0, 0.3f), Axis = Vector3.forward, DistalGap = 0.1f,
			CapsuleRadius = 0.12f, SelectedTriangles = new HashSet<int>(selectedTriangles)
		};
		IndexedBuilder output = new IndexedBuilder(source, 1);
		List<BoundaryEdge> edges = new List<BoundaryEdge>();
		foreach (SourceTriangle triangle in source.Triangles)
		{
			if (!cut.SelectedTriangles.Contains(triangle.Index))
			{
				if (!gib) output.Triangle(output.Original(triangle.A), output.Original(triangle.B), output.Original(triangle.C), 0);
				continue;
			}
			if (!ClipTriangle(source, triangle, cut.Center + (gib ? cut.Axis * cut.DistalGap : Vector3.zero),
				cut.Axis, gib, output, 1, edges, out string clipFailure)) return clipFailure;
		}
		CutBoundaryTrace trace = new CutBoundaryTrace();
		if (!TryCompleteCutBoundaries(source, output, new[] { cut }, new List<List<BoundaryEdge>> { edges },
			gib ? 0 : -1, 1f, trace, out string failure)) return failure;
		Vector3 normal = gib ? -cut.Axis : cut.Axis;
		if (!TryOrderClosedLoops(edges, output.Vertices, normal, BoundaryWeldToleranceWorld, cut.CapsuleRadius * 2f,
			out List<List<int>> loops, out failure)) return failure;
		IndexedBuilder wound = new IndexedBuilder(source, 1, true);
		CutSpec spec = new CutSpec { IsLeg = isLeg, Level = proximal ? LimbSeverLevel.Proximal : LimbSeverLevel.Distal, Seed = seed };
		int sampleCount = 0, boneCount = 0;
		foreach (List<int> loop in loops)
		{
			if (!DisturbBoundaryLoop(output, edges, loop, normal, 1f, spec, gib, out failure)) return failure;
			if (!AddWound(wound, output, loop, edges, normal, 1f, 0, 1, spec, gib, true,
				out ProceduralLimbWoundGeometry.Surface surface, out failure)) return failure;
			for (int index = 0; index < loop.Count; index++)
				if ((surface.Vertices[surface.ExactSeamVertexIndices[index]].Position - output.Vertices[loop[index]].Position).sqrMagnitude > 1e-12f)
					return "nonplanar cap seam moved away from retained exterior";
			foreach (ProceduralLimbWoundGeometry.DecalSample sample in surface.DecalSamples)
			{
				if (!ContainsGeometricEdge(edges, output.Vertices, output.Vertices[loop[sample.BoundaryA]].Position,
					output.Vertices[loop[sample.BoundaryB]].Position, BoundaryWeldToleranceWorld)) return "decal used synthetic closure";
			}
			sampleCount += surface.DecalSamples.Length;
			boneCount += surface.BoneCount;
		}
		metrics = new[] { loops.Count, sampleCount, boneCount, trace.Sides[0].SelectionEdges.Count, trace.Sides[0].StitchedTriangleEdges };
		return null;
	}

	internal static int[] ClassifyCompetingOwnershipForTests(float[] flatPositions, int[] indices,
		float[] targetWeights, float[] otherWeights)
	{
		Vector3[] positions = ToVectors(flatPositions);
		List<SourceTriangle> triangles = new List<SourceTriangle>();
		for (int index = 0; index < indices.Length; index += 3)
			triangles.Add(new SourceTriangle(indices[index], indices[index + 1], indices[index + 2], 0, index / 3));
		Classification classified = ClassifyTriangles(positions, triangles, targetWeights, targetWeights,
			new List<Segment3> { new Segment3(Vector3.zero, new Vector3(0, 0, 1)) }, 0.2f,
			Vector3.zero, Vector3.forward, 0f, BuildTriangleAdjacency(triangles, positions, BoundaryWeldToleranceWorld), otherWeights);
		return classified.Selected.OrderBy(value => value).ToArray();
	}

	internal static string[] FailureCacheForTests()
	{
		ResetFailureCache();
		SourceData source = new SourceData();
		ClassificationWork Work(SourceData data, uint seed, LimbSeverLevel level = LimbSeverLevel.Proximal) => new ClassificationWork(data, Array.Empty<BoneSnapshot>(), new[]
		{
			new CapturedCut { CacheKey = "requested", PaletteKey = "palette", Level = level,
				ProximalIndex = -1, JointIndex = -1, DistalIndex = -1,
				ProximalBones = Array.Empty<int>(), DistalBones = Array.Empty<int>(), Seed = seed }
		});
		string Run(ClassificationWork work)
		{
			TryBuildBatchWithRetries(new[] { new BatchItem { Classification = work, BuildDistal = true } }, false, null, out _, out _);
			return string.Join(";", work.Attempts.Select(a => a.Level + ":" + a.FractionOffset.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + ":" + a.Cached));
		}
		string first = Run(Work(source, 1u));
		string repeat = Run(Work(source, 234u));
		string oppositeRequest = Run(Work(source, 1u, LimbSeverLevel.Distal));
		string changedSource = Run(Work(new SourceData(), 1u));
		ResetFailureCache();
		string reset = Run(Work(source, 1u));
		return new[] { first, repeat, changedSource, reset, oppositeRequest };
	}

	internal static string[] CaptureClothingForTests(string directory)
	{
		SourceData source = new SourceData
		{
			Name = "capture-fixture", Positions = new[] { Vector3.zero, Vector3.right, Vector3.up }, SubmeshCount = 1,
			Normals = Array.Empty<Vector3>(), Tangents = Array.Empty<Vector4>(), Uv0 = Array.Empty<Vector2>(),
			Uv1 = Array.Empty<Vector2>(), Uv2 = Array.Empty<Vector2>(), Uv3 = Array.Empty<Vector2>(), Colors = Array.Empty<Color>(),
			Weights = new[] { new BoneWeight { weight0 = 1f }, new BoneWeight { weight0 = 1f }, new BoneWeight { weight0 = 1f } },
			Bindposes = new[] { Matrix4x4.identity }, BlendShapes = Array.Empty<BlendShapeFrame>(),
			Triangles = new List<SourceTriangle> { new SourceTriangle(0, 1, 2, 0, 0) }
		};
		CapturedCut cut = new CapturedCut { CacheKey = "fixture", StartIndex = 0, EndIndex = 0,
			LimbBones = new[] { 0 }, WorldScale = 1f, Level = LimbSeverLevel.Proximal, Seed = 1u };
		source.BindCutCache[cut.CacheKey] = new BindCut
		{
			Center = Vector3.zero, Axis = Vector3.forward, CapsuleRadius = 0.1f, DistalGap = 0.1f,
			SelectedTriangles = new HashSet<int> { 0 }, Classification = new Classification
			{
				Candidate = new[] { true }, LimbWeight = new[] { 1f }, SeedWeight = new[] { 1f },
				ProtectedByOtherBones = new[] { false }
			}
		};
		var session = new LimbCutCaptureSession(directory);
		var trace = new CutBoundaryTrace();
		trace.Sides.Add(new BoundaryCapture
		{
			Side = "body", Positions = source.Positions.Select(Point).ToArray(),
			ExteriorTriangles = new[] { new[] { 0, 1, 2 } }, PlaneEdges = new[] { new[] { 0, 0, 1 } }
		});
		BoneSnapshot[] bones = { new BoneSnapshot("limb", -1, Vector3.zero) };
		string first = CaptureClothingCase(session, source, bones, new[] { cut }, trace, null, "shirt_LOD0", 0, "LeftArm", 0, "corpse1", "boundary rejected");
		cut.Seed = 234u;
		string repeated = CaptureClothingCase(session, source, bones, new[] { cut }, trace, null, "shirt_LOD0", 0, "LeftArm", 0, "corpse2", "boundary rejected");
		string lod = CaptureClothingCase(session, source, bones, new[] { cut }, trace, null, "shirt_LOD1", 1, "LeftArm", 0, "corpse3", "prepared");
		return new[] { first, repeated, lod };
	}
}
