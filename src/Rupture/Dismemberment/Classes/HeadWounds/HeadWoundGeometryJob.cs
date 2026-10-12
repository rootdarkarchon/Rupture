using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
// Main-thread capture transfers ownership of the data to the job. A successful
// source is retained read-only for later wounds; each rebuild gets a private copy.
internal sealed class HeadWoundGeometrySource
{
    internal ProceduralHeadWoundWorkspace Data;
    internal Matrix4x4 ToHead = Matrix4x4.identity;
    internal bool NeedsTransform;
    internal int BoneCount;
}

internal sealed partial class HeadWoundGeometryJob : ManagedGeometryJob
{
    internal HeadWoundGeometrySource[] Sources;
    internal ProceduralWoundDescriptor[] Previous;
    internal Vector3 SuppliedEntry, Direction;
    internal float EntryRadius, ExitRadius, MaximumDepth;
    internal uint Seed;

    internal HeadWoundGeometrySource Source;
    internal ProceduralHeadWoundWorkspace Workspace;
    internal ProceduralWoundDescriptor Descriptor;
    internal ProceduralWoundMetrics Metrics;
    internal int SourceIndex = -1;
    internal Vector3 Entry, Exit;
    internal float ExitDistance;
    internal string EntryResolution;
    internal bool TerminalSurfaceResolved;
    internal Bounds HeadBounds, CavityBounds;
    internal double RayMilliseconds, GenerationMilliseconds, ValidationMilliseconds;

    private static void TransformSource(ProceduralHeadWoundWorkspace data, Matrix4x4 transform)
    {
        if (MeshFrameTransform.TransformVertices(transform, data.SourceVertices, data.SourceNormals, data.SourceTangents))
            for (int submesh = 0; submesh < data.SubMeshCount; submesh++)
            {
                var indices = data.GetSourceSubmesh(submesh);
                // GetSourceSubmesh exposes a read-only view; the captured workspace owns its list.
                var writable = (List<int>)indices;
                for (int i = 0; i < writable.Count; i += 3)
                    (writable[i + 1], writable[i + 2]) = (writable[i + 2], writable[i + 1]);
            }
    }

    internal static Bounds CalculateBounds(IReadOnlyList<Vector3> vertices)
    {
        if (vertices.Count == 0) return default;
        var bounds = new Bounds(vertices[0], Vector3.zero);
        for (int i = 1; i < vertices.Count; i++) bounds.Encapsulate(vertices[i]);
        return bounds;
    }

    private static bool ValidateSource(HeadWoundGeometrySource source, out string failure)
    {
        failure = "the selected LOD0 head has invalid skinning weights";
        if (source.BoneCount == 0 || source.Data.SourceBoneWeights.Count != source.Data.SourceVertices.Count) return false;
        foreach (var weight in source.Data.SourceBoneWeights)
            if (!ValidBoneInfluence(weight.boneIndex0, weight.weight0, source.BoneCount) ||
                !ValidBoneInfluence(weight.boneIndex1, weight.weight1, source.BoneCount) ||
                !ValidBoneInfluence(weight.boneIndex2, weight.weight2, source.BoneCount) ||
                !ValidBoneInfluence(weight.boneIndex3, weight.weight3, source.BoneCount) ||
                weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3 <= 0.0001f) return false;
        failure = null;
        return true;
    }

    private static bool ValidBoneInfluence(int index, float weight, int count) =>
        !float.IsNaN(weight) && !float.IsInfinity(weight) && weight >= 0 && (weight <= 0.0000001f || (uint)index < (uint)count);

	private const float MaximumVisualEntrySnapDistance = 0.015f;
	private const float MaximumBackwardEntryProjectionDistance = 0.045f;
	private const float MaximumForwardEntryProjectionDistance = 0.045f;
	private const float EntryInteriorTraceOffset = 0.00075f;
	private const float MinimumThroughShotDepth = 0.015f;
	private const int MaximumResultVertices = 250000;

	private static bool TryResolveEntryExit(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles, Vector3 suppliedEntry, Vector3 direction, float maximumDepth,
		out Vector3 entry, out Vector3 exit, out float distance, out string entryResolution,
		float entryRadius, float exitRadius, out bool terminalSurfaceResolved, out string failure)
	{
		entry = default;
		exit = default;
		distance = 0f;
		entryResolution = null;
		terminalSurfaceResolved = false;
		failure = null;
		if (vertices == null || direction.sqrMagnitude < 0.0001f)
		{
			failure = "the lethal-hit direction was invalid";
			return false;
		}
		direction.Normalize();
		if (vertices == null || triangles == null || triangles.Count < 3)
		{
			failure = "the baked LOD0 mesh had no triangle surface";
			return false;
		}

		bool foundEntry = TryProjectEntryAlongTrajectory(vertices, triangles, suppliedEntry, direction,
			MaximumBackwardEntryProjectionDistance, MaximumForwardEntryProjectionDistance, out entry);
		if (foundEntry)
		{
			entryResolution = "trajectory";
		}
		else
		{
			float nearestSquared = MaximumVisualEntrySnapDistance * MaximumVisualEntrySnapDistance;
			for (int index = 0; index + 2 < triangles.Count; index += 3)
			{
				Vector3 candidate = ClosestPointOnTriangle(suppliedEntry, vertices[triangles[index]],
					vertices[triangles[index + 1]], vertices[triangles[index + 2]]);
				float squared = (candidate - suppliedEntry).sqrMagnitude;
				if (squared <= nearestSquared)
				{
					nearestSquared = squared;
					entry = candidate;
					foundEntry = true;
				}
			}
			if (foundEntry) entryResolution = "nearest";
		}
		if (!foundEntry)
		{
			float extendedProjectionDistance = Mathf.Max(maximumDepth,
				MaximumBackwardEntryProjectionDistance + MaximumForwardEntryProjectionDistance);
			foundEntry = TryProjectEntryAlongTrajectory(vertices, triangles, suppliedEntry, direction,
				extendedProjectionDistance, extendedProjectionDistance, out entry);
			if (foundEntry) entryResolution = "trajectory-extended";
		}
		if (!foundEntry)
		{
			failure = $"no visual surface lay within {MaximumBackwardEntryProjectionDistance * 1000f:F0}mm backward or " +
				$"{MaximumForwardEntryProjectionDistance * 1000f:F0}mm forward along the lethal trajectory " +
				$"or {MaximumVisualEntrySnapDistance * 1000f:F0}mm of the lethal hit, and the bounded " +
				$"{Mathf.Max(maximumDepth, MaximumBackwardEntryProjectionDistance + MaximumForwardEntryProjectionDistance) * 1000f:F0}mm " +
				$"extended trajectory fallback found no LOD0 surface";
			return false;
		}

		Vector3 origin = entry + direction * EntryInteriorTraceOffset;
		float furthestExitDistance = float.MinValue;
		for (int index = 0; index + 2 < triangles.Count; index += 3)
		{
			if (!RayTriangle(origin, direction, vertices[triangles[index]], vertices[triangles[index + 1]],
				vertices[triangles[index + 2]], out float traceDistance))
			{
				continue;
			}
			float candidateDistance = traceDistance + EntryInteriorTraceOffset;
			if (candidateDistance >= MinimumThroughShotDepth && candidateDistance <= maximumDepth &&
				candidateDistance > furthestExitDistance)
			{
				furthestExitDistance = candidateDistance;
			}
		}
		if (furthestExitDistance == float.MinValue)
		{
			if (!TryResolveConeTerminalExtent(vertices, triangles, entry, direction, maximumDepth,
				entryRadius, exitRadius, out furthestExitDistance))
			{
				failure = "no terminal surface or bounded cone-approached LOD0 geometry was found from the visual entry";
				return false;
			}
		}
		else
		{
			terminalSurfaceResolved = true;
		}
		// A head is not a single closed shell. Ears, nose geometry, mouth/eye cavities,
		// and separate surface islands can create multiple crossings with empty space
		// between them. Keep the destructive interval open across every such gap and
		// terminate at either the furthest bounded surface or the conservative cone
		// extent; the clipper then removes all geometry intersecting that full canal.
		distance = furthestExitDistance;
		exit = entry + direction * distance;
		return true;
	}

	private static bool TryResolveConeTerminalExtent(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles,
		Vector3 entry, Vector3 direction, float maximumDepth, float entryRadius, float exitRadius,
		out float distance)
	{
		const float terminalPadding = 0.002f;
		distance = 0f;
		if (maximumDepth < MinimumThroughShotDepth || entryRadius <= 0f || exitRadius <= 0f)
		{
			return false;
		}
		for (int index = 0; index + 2 < triangles.Count; index += 3)
		{
			Vector3 a = vertices[triangles[index]];
			Vector3 b = vertices[triangles[index + 1]];
			Vector3 c = vertices[triangles[index + 2]];
			float axialA = Vector3.Dot(a - entry, direction);
			float axialB = Vector3.Dot(b - entry, direction);
			float axialC = Vector3.Dot(c - entry, direction);
			float minimumAxial = Mathf.Min(axialA, Mathf.Min(axialB, axialC));
			float maximumAxial = Mathf.Max(axialA, Mathf.Max(axialB, axialC));
			if (maximumAxial < MinimumThroughShotDepth || minimumAxial > maximumDepth)
			{
				continue;
			}

			Vector3 center = (a + b + c) / 3f;
			float centerAxial = Mathf.Clamp(Vector3.Dot(center - entry, direction), 0f, maximumDepth);
			Vector3 axisPoint = entry + direction * centerAxial;
			float triangleRadius = Mathf.Max(Vector3.Distance(center, a),
				Mathf.Max(Vector3.Distance(center, b), Vector3.Distance(center, c)));
			float woundRadius = Mathf.Lerp(entryRadius, exitRadius, centerAxial / maximumDepth);
			float conservativeRadius = triangleRadius + woundRadius;
			if ((center - axisPoint).sqrMagnitude > conservativeRadius * conservativeRadius)
			{
				continue;
			}
			distance = Mathf.Max(distance, Mathf.Min(maximumDepth, maximumAxial + terminalPadding));
		}
		return distance >= MinimumThroughShotDepth;
	}

	private static bool TryProjectEntryAlongTrajectory(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> triangles,
		Vector3 suppliedEntry, Vector3 direction, float backwardDistance, float forwardDistance,
		out Vector3 entry)
	{
		entry = default;
		// EFT's lethal hit can be reported on a helmet or simplified hit collider on
		// either side of the rendered skin. Search the same trajectory in both
		// directions and choose the visual intersection closest to the supplied hit.
		Vector3 origin = suppliedEntry - direction * backwardDistance;
		float targetDistance = backwardDistance;
		float maximumDistance = backwardDistance + forwardDistance;
		float nearestDifference = float.MaxValue;
		bool found = false;
		for (int index = 0; index + 2 < triangles.Count; index += 3)
		{
			if (!RayTriangle(origin, direction, vertices[triangles[index]], vertices[triangles[index + 1]],
				vertices[triangles[index + 2]], out float traceDistance) || traceDistance > maximumDistance)
			{
				continue;
			}
			float difference = Mathf.Abs(traceDistance - targetDistance);
			if (difference >= nearestDifference) continue;
			nearestDifference = difference;
			entry = origin + direction * traceDistance;
			found = true;
		}
		return found;
	}

	private static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
	{
		Vector3 ab = b - a;
		Vector3 ac = c - a;
		Vector3 ap = point - a;
		float d1 = Vector3.Dot(ab, ap);
		float d2 = Vector3.Dot(ac, ap);
		if (d1 <= 0f && d2 <= 0f) return a;

		Vector3 bp = point - b;
		float d3 = Vector3.Dot(ab, bp);
		float d4 = Vector3.Dot(ac, bp);
		if (d3 >= 0f && d4 <= d3) return b;

		float vc = d1 * d4 - d3 * d2;
		if (vc <= 0f && d1 >= 0f && d3 <= 0f)
		{
			float v = d1 / (d1 - d3);
			return a + ab * v;
		}

		Vector3 cp = point - c;
		float d5 = Vector3.Dot(ab, cp);
		float d6 = Vector3.Dot(ac, cp);
		if (d6 >= 0f && d5 <= d6) return c;

		float vb = d5 * d2 - d1 * d6;
		if (vb <= 0f && d2 >= 0f && d6 <= 0f)
		{
			float w = d2 / (d2 - d6);
			return a + ac * w;
		}

		float va = d3 * d6 - d5 * d4;
		if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
		{
			float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
			return b + (c - b) * w;
		}

		float denominator = va + vb + vc;
		if (Mathf.Abs(denominator) < 0.0000001f)
		{
			return a;
		}
		float inverse = 1f / denominator;
		float barycentricV = vb * inverse;
		float barycentricW = vc * inverse;
		return a + ab * barycentricV + ac * barycentricW;
	}

	private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
	{
		distance = 0f;
		Vector3 edge1 = b - a;
		Vector3 edge2 = c - a;
		Vector3 p = Vector3.Cross(direction, edge2);
		float determinant = Vector3.Dot(edge1, p);
		if (Mathf.Abs(determinant) < 0.0000001f)
		{
			return false;
		}
		float inverse = 1f / determinant;
		Vector3 t = origin - a;
		float u = Vector3.Dot(t, p) * inverse;
		if (u < 0f || u > 1f)
		{
			return false;
		}
		Vector3 q = Vector3.Cross(t, edge1);
		float v = Vector3.Dot(direction, q) * inverse;
		if (v < 0f || u + v > 1f)
		{
			return false;
		}
		distance = Vector3.Dot(edge2, q) * inverse;
		return distance >= 0f;
	}

	private static bool ValidateResult(ProceduralHeadWoundWorkspace workspace, Bounds bounds,
		int sourceVertexCount, ProceduralWoundMetrics metrics, Vector3 entry,
		Vector3 direction, float exitDistance, bool generateExitLip, out string failure, bool throughShot = true)
	{
		failure = null;
		if (workspace.SourceVertices.Count < sourceVertexCount ||
			workspace.SourceVertices.Count != sourceVertexCount + metrics.GeneratedHeadVertexCount || workspace.SourceVertices.Count < 3 ||
			workspace.SourceVertices.Count > MaximumResultVertices || workspace.SubMeshCount == 0)
		{
			failure = "locally clipped head violated its bounded source-plus-generated vertex contract";
			return false;
		}
		var resultBoneWeights = workspace.SourceBoneWeights;
		if (resultBoneWeights == null || resultBoneWeights.Count != workspace.SourceVertices.Count)
		{
			failure = "locally clipped head did not preserve one skinning weight record per result vertex";
			return false;
		}
		if (!IsFinite(bounds.center) || !IsFinite(bounds.extents) || bounds.extents.sqrMagnitude < 0.000001f ||
			bounds.extents.x > 1f || bounds.extents.y > 1f || bounds.extents.z > 1f)
		{
			failure = "filtered head produced invalid or implausible mesh bounds";
			return false;
		}
		foreach (Vector3 vertex in workspace.SourceVertices)
		{
			if (!IsFinite(vertex))
			{
				failure = "filtered head retained a non-finite vertex";
				return false;
			}
		}
		int retainedIndices = 0;
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> indices = workspace.GetFilteredSubmesh(submesh);
			if (indices.Count % 3 != 0)
			{
				failure = $"filtered head produced a malformed index count in submesh {submesh}";
				return false;
			}
			retainedIndices += indices.Count;
			for (int index = 0; index < indices.Count; index++)
			{
				if ((uint)indices[index] >= (uint)workspace.SourceVertices.Count)
				{
					failure = $"filtered head produced an out-of-range index in submesh {submesh}";
					return false;
				}
			}
		}
		if (retainedIndices == 0 || metrics.RemovedTriangleCount <= 0 ||
			retainedIndices / 3 != metrics.ResultHeadTriangleCount)
		{
			failure = "filtered head did not retain skin or remove wound triangles";
			return false;
		}

		if (workspace.CavityVertices.Count != metrics.CavityVertexCount + metrics.RimVertexCount ||
			workspace.CavityVertices.Count > ProceduralHeadWoundGeometry.MaximumCavityVertices ||
			workspace.CavityTriangles.Count != (metrics.CavityTriangleCount + metrics.RimTriangleCount) * 3)
		{
			failure = "procedural cavity violated its bounded mesh contract";
			return false;
		}
		if (metrics.EntryOpeningCoverageRadius <= 0f ||
			(generateExitLip && metrics.ExitOpeningCoverageRadius <= 0f) ||
			metrics.MinimumRimCoverageMargin < ProceduralHeadWoundGeometry.MinimumRimCoverageMargin ||
			metrics.RimVertexCount <= 0 || metrics.RimTriangleCount <= 0 ||
			metrics.BoundaryComponentCount <= 0 || metrics.SolidCanalSpanCount <= 0 ||
			metrics.EntryLipCount != 1 ||
			(generateExitLip && metrics.ExitFlapCount != ProceduralHeadWoundGeometry.ExitFlapCount) ||
			metrics.MinimumEntryRimFacingDot < 0f ||
			(generateExitLip && metrics.MinimumExitRimFacingDot < 0f) ||
			metrics.EntryRimSurfaceHitCount <= 0 ||
			(generateExitLip && metrics.ExitRimSurfaceHitCount <= 0))
		{
			failure = "component-driven canal, independent lips, or the eight thick terminal exit flaps are missing or unbounded";
			return false;
		}
		for (int index = 0; index < workspace.CavityVertices.Count; index++)
		{
			if (!IsFinite(workspace.CavityVertices[index]) || !IsFinite(workspace.CavityNormals[index]))
			{
				failure = "procedural cavity contains non-finite vertex data";
				return false;
			}
		}
		foreach (int index in workspace.CavityTriangles)
		{
			if ((uint)index >= (uint)workspace.CavityVertices.Count)
			{
				failure = "procedural cavity contains an out-of-range index";
				return false;
			}
		}

		float minimumAxial = float.MaxValue;
		float maximumAxial = float.MinValue;
		Vector3 normalizedDirection = direction.normalized;
		foreach (Vector3 vertex in workspace.CavityVertices)
		{
			float axial = Vector3.Dot(vertex - entry, normalizedDirection);
			minimumAxial = Mathf.Min(minimumAxial, axial);
			maximumAxial = Mathf.Max(maximumAxial, axial);
		}
		if (minimumAxial > 0.01f || maximumAxial < exitDistance - 0.01f)
		{
			failure = "procedural cavity does not reach both wound openings";
			return false;
		}
		Vector3 rayOrigin = entry - direction.normalized * 0.003f;
		int remainingHeadIntersections = CountRayIntersections(workspace.SourceVertices,
			workspace, rayOrigin, direction.normalized, throughShot ? exitDistance + 0.006f : exitDistance * 0.65f);
		if (remainingHeadIntersections != 0)
		{
			failure = $"the central entry/exit channel is not open after triangle filtering " +
				$"({remainingHeadIntersections} retained intersections)";
			return false;
		}
		if (!throughShot)
		{
			bool cap = false;
			for (int i = 0; i + 2 < workspace.CavityTriangles.Count; i += 3)
				if (RayTriangle(rayOrigin, normalizedDirection,
					workspace.CavityVertices[workspace.CavityTriangles[i]],
					workspace.CavityVertices[workspace.CavityTriangles[i + 1]],
					workspace.CavityVertices[workspace.CavityTriangles[i + 2]], out float distance) &&
					distance > exitDistance * 0.65f && distance <= exitDistance + 0.01f) cap = true;
			if (!cap) { failure = "entry crater has no closed terminal surface"; return false; }
		}
		return true;
	}

	private static int CountRayIntersections(IReadOnlyList<Vector3> vertices, ProceduralHeadWoundWorkspace workspace,
		Vector3 origin, Vector3 direction, float maximumDistance)
	{
		int count = 0;
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> triangles = workspace.GetFilteredSubmesh(submesh);
			for (int index = 0; index + 2 < triangles.Count; index += 3)
			{
				if (RayTriangle(origin, direction, vertices[triangles[index]], vertices[triangles[index + 1]],
					vertices[triangles[index + 2]], out float distance) && distance <= maximumDistance)
				{
					count++;
				}
			}
		}
		return count;
	}

	private static bool ValidateMultipleResult(ProceduralHeadWoundWorkspace workspace, Bounds bounds, int sourceCount,
		IReadOnlyList<ProceduralWoundDescriptor> descriptors, out string failure)
	{
		failure = null;
		if (workspace.SourceVertices.Count > MaximumResultVertices ||
			workspace.SourceBoneWeights.Count != workspace.SourceVertices.Count ||
			!IsFinite(bounds.center) || !IsFinite(bounds.extents) || bounds.extents.sqrMagnitude < 0.000001f ||
			bounds.extents.x > 1f || bounds.extents.y > 1f || bounds.extents.z > 1f)
		{ failure = "combined head has invalid skinning or empty skin"; return false; }
		int indices = 0;
		for (int i = 0; i < workspace.SubMeshCount; i++) indices += workspace.GetFilteredSubmesh(i).Count;
		if (indices == 0) { failure = "combined head retained no skin"; return false; }
		return ProceduralHeadWoundGeometry.ValidateCombined(workspace, descriptors, sourceCount, out failure);
	}
	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
			!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}
}
