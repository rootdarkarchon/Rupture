using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class ProceduralLimbCutGeometry
{
	internal sealed class CutBoundaryTrace
	{
		internal bool CaptureGeometry;
		internal readonly List<BoundaryCapture> Sides = new List<BoundaryCapture>();
		internal readonly List<ProceduralLimbWoundGeometry.FoldDiagnostic> Clothing = new List<ProceduralLimbWoundGeometry.FoldDiagnostic>();
	}

	internal sealed class BoundaryCapture
	{
		public string Side;
		public float[][] Positions;
		public int[][] ExteriorTriangles;
		public int[][] PlaneEdges;
		public readonly List<int[]> SelectionEdges = new List<int[]>();
		public readonly List<int[]> ExistingOpenEdges = new List<int[]>();
		public readonly List<int[]> Loops = new List<int[]>();
		public int IgnoredInteriorPlaneEdges;
		public int StitchedTriangleEdges;
		public int RemovedCollapsedTriangles;
	}

	private static bool ContainsGeometricEdge(IReadOnlyList<BoundaryEdge> edges, List<VertexData> vertices,
		Vector3 a, Vector3 b, float tolerance)
	{
		float squared = tolerance * tolerance;
		foreach (BoundaryEdge edge in edges)
		{
			Vector3 ea = vertices[edge.A].Position, eb = vertices[edge.B].Position;
			if ((ea - a).sqrMagnitude <= squared && (eb - b).sqrMagnitude <= squared ||
				(ea - b).sqrMagnitude <= squared && (eb - a).sqrMagnitude <= squared) return true;
		}
		return false;
	}

	// Reconstruct newly exposed edges from the retained exterior, before rim
	// disturbance or adornments. Preserve pre-existing garment openings separately.
	// This also catches a selected/unselected interface away from either plane.
	private static bool TryCompleteCutBoundaries(SourceData source, IndexedBuilder output,
		IReadOnlyList<BindCut> cuts, List<List<BoundaryEdge>> boundaries, int gibCut,
		float worldScale, CutBoundaryTrace trace, out string failure)
	{
		failure = null;
		float tolerance = BoundaryWeldToleranceWorld / worldScale;
		int stitched = SplitRetainedSourceEdges(source, output, tolerance);
		Vector3[] positions = output.Vertices.Select(vertex => vertex.Position).ToArray();
		int[] canonical = BuildCanonicalPositionMap(positions, tolerance);
		// Collapsed faces otherwise count the same edge twice and manufacture a
		// third owner. Remove them from the actual output, not just the audit.
		int removedCollapsed = 0;
		foreach (List<int> triangles in output.Submeshes)
			for (int index = triangles.Count - 3; index >= 0; index -= 3)
			{
				int a = canonical[triangles[index]], b = canonical[triangles[index + 1]], c = canonical[triangles[index + 2]];
				if (a != b && b != c && c != a) continue;
				triangles.RemoveRange(index, 3);
				removedCollapsed++;
			}
		int[] sourceCanonical = source.GetCanonical(tolerance);
		Dictionary<BoundaryEdge, List<int>> sourceOwners = source.GetEdgeOwners(tolerance);
		Dictionary<BoundaryEdge, int> counts = new Dictionary<BoundaryEdge, int>();
		Dictionary<BoundaryEdge, BoundaryEdge> actual = new Dictionary<BoundaryEdge, BoundaryEdge>();
		foreach (List<int> triangles in output.Submeshes)
			for (int index = 0; index + 2 < triangles.Count; index += 3)
				for (int corner = 0; corner < 3; corner++)
				{
					int a = triangles[index + corner], b = triangles[index + (corner + 1) % 3];
					BoundaryEdge key = new BoundaryEdge(canonical[a], canonical[b]);
					if (key.A == key.B) continue;
					counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
					actual[key] = new BoundaryEdge(a, b);
				}
		BoundaryCapture capture = new BoundaryCapture
		{
			Side = gibCut < 0 ? "body" : "gib",
			StitchedTriangleEdges = stitched,
			RemovedCollapsedTriangles = removedCollapsed,
			Positions = trace?.CaptureGeometry == true ? positions.Select(Point).ToArray() : null,
			ExteriorTriangles = trace?.CaptureGeometry == true ? output.Submeshes.Select(indices => indices.ToArray()).ToArray() : null,
			PlaneEdges = trace?.CaptureGeometry == true ? boundaries.SelectMany((edges, cut) => edges.Select(edge => new[] { cut, edge.A, edge.B })).ToArray() : null
		};
		trace?.Sides.Add(capture);
		HashSet<BoundaryEdge> planeKeys = new HashSet<BoundaryEdge>();
		foreach (List<BoundaryEdge> edges in boundaries)
		{
			for (int index = edges.Count - 1; index >= 0; index--)
			{
				BoundaryEdge key = new BoundaryEdge(canonical[edges[index].A], canonical[edges[index].B]);
				if (key.A == key.B || !counts.TryGetValue(key, out int count) || count == 2)
				{
					edges.RemoveAt(index);
					capture.IgnoredInteriorPlaneEdges++;
					continue;
				}
				if (count != 1)
				{
					failure = $"{capture.Side} cut edge has {count} retained exterior owners";
					return false;
				}
				planeKeys.Add(key);
			}
		}
		HashSet<int> originalOpenVertices = new HashSet<int>();
		foreach (KeyValuePair<BoundaryEdge, int> item in counts)
		{
			if (item.Value != 1 || planeKeys.Contains(item.Key)) continue;
			BoundaryEdge edge = actual[item.Key];
			VertexData a = output.Vertices[edge.A], b = output.Vertices[edge.B];
			if (!TrySourceEdge(a, b, sourceCanonical, out int sa, out int sb)) continue;
			if (!sourceOwners.TryGetValue(new BoundaryEdge(sourceCanonical[sa], sourceCanonical[sb]), out List<int> owners)) continue;
			if (owners.Count == 1)
			{
				capture.ExistingOpenEdges.Add(new[] { edge.A, edge.B });
				originalOpenVertices.Add(item.Key.A);
				originalOpenVertices.Add(item.Key.B);
				continue;
			}
			int affected = -1;
			for (int cutIndex = 0; cutIndex < cuts.Count; cutIndex++)
			{
				if (gibCut >= 0 && cutIndex != gibCut) continue;
				if (!owners.Any(owner => cuts[cutIndex].SelectedTriangles.Contains(owner))) continue;
				if (affected >= 0)
				{
					failure = $"{capture.Side} selection interface belongs to multiple cuts";
					return false;
				}
				affected = cutIndex;
			}
			if (affected < 0) continue;
			capture.SelectionEdges.Add(new[] { affected, edge.A, edge.B });
			BindCut cut = cuts[affected];
			Vector3 center = cut.Center + (gibCut >= 0 ? cut.Axis * cut.DistalGap : Vector3.zero);
			float extent = Mathf.Max(Mathf.Abs(Vector3.Dot(a.Position - center, cut.Axis)),
				Mathf.Abs(Vector3.Dot(b.Position - center, cut.Axis)));
			if (extent > cut.CapsuleRadius + tolerance)
			{
				failure = $"{capture.Side} cut {affected} exposes an anatomical selection tear away from the seam " +
					$"(sourceEdge={sourceCanonical[sa]}-{sourceCanonical[sb]}, axialMm={extent * worldScale * 1000f:F2})";
				return false;
			}
			boundaries[gibCut < 0 ? affected : 0].Add(edge);
		}
		// Straight cap-only closures are permissible only at an actual source
		// garment opening. Missing classification edges cannot masquerade as one.
		for (int index = 0; index < boundaries.Count; index++)
		{
			Dictionary<int, HashSet<int>> neighbors = new Dictionary<int, HashSet<int>>();
			foreach (BoundaryEdge edge in boundaries[index])
			{
				int a = canonical[edge.A], b = canonical[edge.B];
				if (a == b) continue;
				if (!neighbors.ContainsKey(a)) neighbors[a] = new HashSet<int>();
				if (!neighbors.ContainsKey(b)) neighbors[b] = new HashSet<int>();
				neighbors[a].Add(b);
				neighbors[b].Add(a);
			}
			foreach (KeyValuePair<int, HashSet<int>> vertex in neighbors)
				if (vertex.Value.Count == 1 && !originalOpenVertices.Contains(vertex.Key))
				{
					failure = $"{capture.Side} cut {index} has an unclosed selection boundary at vertex {vertex.Key}; " +
						"endpoint is not a source garment opening";
					return false;
				}
		}
		return true;
	}

	private static float[] Point(Vector3 value) => new[] { value.x, value.y, value.z };

	// A clipped triangle and an untouched neighbor can share only part of a
	// source edge. Insert the existing crossing into the neighbor too, otherwise
	// edge counting sees a T-junction and a false overlapping boundary. Keep that
	// neighbor's own UVs/weights at duplicated garment seams.
	private static int SplitRetainedSourceEdges(SourceData source, IndexedBuilder output, float tolerance)
	{
		int[] canonical = source.GetCanonical(tolerance);
		Dictionary<BoundaryEdge, List<int>> crossings = new Dictionary<BoundaryEdge, List<int>>();
		for (int index = 0; index < output.Vertices.Count; index++)
		{
			VertexData vertex = output.Vertices[index];
			if (vertex.SourceA == vertex.SourceB) continue;
			BoundaryEdge key = new BoundaryEdge(canonical[vertex.SourceA], canonical[vertex.SourceB]);
			if (!crossings.TryGetValue(key, out List<int> list)) crossings[key] = list = new List<int>();
			if (!list.Any(other => (output.Vertices[other].Position - vertex.Position).sqrMagnitude <= tolerance * tolerance))
				list.Add(index);
		}
		int splits = 0;
		if (crossings.Count == 0) return splits;
		foreach (List<int> triangles in output.Submeshes)
		{
			for (int triangle = 0; triangle + 2 < triangles.Count; triangle += 3)
			{
				bool split;
				do
				{
					split = false;
					for (int corner = 0; corner < 3 && !split; corner++)
					{
						int ia = triangles[triangle + corner], ib = triangles[triangle + (corner + 1) % 3];
						int ic = triangles[triangle + (corner + 2) % 3];
						VertexData a = output.Vertices[ia], b = output.Vertices[ib];
						if (!TrySourceEdge(a, b, canonical, out int sa, out int sb) ||
							!crossings.TryGetValue(new BoundaryEdge(canonical[sa], canonical[sb]), out List<int> candidates)) continue;
						Vector3 delta = b.Position - a.Position;
						float lengthSquared = delta.sqrMagnitude;
						if (lengthSquared <= tolerance * tolerance) continue;
						foreach (int candidate in candidates)
						{
							Vector3 position = output.Vertices[candidate].Position;
							if ((position - a.Position).sqrMagnitude <= tolerance * tolerance ||
								(position - b.Position).sqrMagnitude <= tolerance * tolerance) continue;
							float t = Vector3.Dot(position - a.Position, delta) / lengthSquared;
							if (t <= 0f || t >= 1f || (a.Position + delta * t - position).sqrMagnitude > tolerance * tolerance) continue;
							Vector3 sourceDelta = source.Positions[sb] - source.Positions[sa];
							float sourceT = Vector3.Dot(position - source.Positions[sa], sourceDelta) / sourceDelta.sqrMagnitude;
							int inserted = output.Crossing(sa, sb, sourceT, 100000 + candidate);
							VertexData vertex = output.Vertices[inserted];
							vertex.Position = position;
							output.Vertices[inserted] = vertex;
							triangles[triangle] = ia; triangles[triangle + 1] = inserted; triangles[triangle + 2] = ic;
							triangles.Add(inserted); triangles.Add(ib); triangles.Add(ic);
							splits++;
							split = true;
							break;
						}
					}
				} while (split);
			}
		}
		return splits;
	}

	private static bool TrySourceEdge(VertexData a, VertexData b, int[] canonical, out int sa, out int sb)
	{
		sa = a.SourceA;
		sb = -1;
		return AddSourceSupport(a.SourceB, canonical, sa, ref sb) &&
			AddSourceSupport(b.SourceA, canonical, sa, ref sb) &&
			AddSourceSupport(b.SourceB, canonical, sa, ref sb) && sb >= 0;
	}

	private static bool AddSourceSupport(int index, int[] canonical, int sa, ref int sb)
	{
		if (canonical[index] == canonical[sa]) return true;
		if (sb < 0) { sb = index; return true; }
		return canonical[index] == canonical[sb];
	}
}
