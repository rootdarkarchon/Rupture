using System;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
internal sealed partial class ProceduralHeadWoundWorkspace
{
	internal ProceduralHeadWoundGeometry.UnionScratch Union;
	internal double LastSinglesMs, LastSkinMs, LastCanalsMs;
	private static void Copy<T>(List<T> target, List<T> source) { target.Clear(); target.AddRange(source); }
	internal void CopySource(ProceduralHeadWoundWorkspace source, bool filtered = false)
	{
		Copy(SourceVertices, source.SourceVertices); Copy(SourceNormals, source.SourceNormals);
		Copy(SourceTangents, source.SourceTangents); Copy(SourceColors, source.SourceColors);
		Copy(SourceBoneWeights, source.SourceBoneWeights);
		for (int i = 0; i < SourceUvs.Length; i++) Copy(SourceUvs[i], source.SourceUvs[i]);
		BaseVertexCount = SourceVertices.Count;
		EnsureSubmeshCapacity(source.SubMeshCount);
		for (int i = 0; i < SubMeshCount; i++) Copy(_sourceSubmeshes[i], filtered ? source._filteredSubmeshes[i] : source._sourceSubmeshes[i]);
	}

	internal void AppendGenerated(ProceduralHeadWoundWorkspace source, int originalCount)
	{
		for (int i = originalCount; i < source.SourceVertices.Count; i++)
		{
			SourceVertices.Add(source.SourceVertices[i]);
			if (source.SourceNormals.Count > i) SourceNormals.Add(source.SourceNormals[i]);
			if (source.SourceTangents.Count > i) SourceTangents.Add(source.SourceTangents[i]);
			if (source.SourceColors.Count > i) SourceColors.Add(source.SourceColors[i]);
			if (source.SourceBoneWeights.Count > i) SourceBoneWeights.Add(source.SourceBoneWeights[i]);
			for (int j = 0; j < SourceUvs.Length; j++) if (source.SourceUvs[j].Count > i) SourceUvs[j].Add(source.SourceUvs[j][i]);
		}
	}

	internal void ImportCavity(ProceduralHeadWoundWorkspace source)
	{
		Copy(SourceVertices, source.CavityVertices); Copy(SourceNormals, source.CavityNormals);
		Copy(SourceTangents, source.CavityTangents); Copy(SourceColors, source.CavityColors);
		SourceBoneWeights.Clear();
		foreach (var uv in SourceUvs) uv.Clear();
		foreach (Vector2 uv in source.CavityUvs) SourceUvs[0].Add(new Vector4(uv.x, uv.y, 0, 0));
		BaseVertexCount = SourceVertices.Count;
		EnsureSubmeshCapacity(1); Copy(_sourceSubmeshes[0], source.CavityTriangles);
	}

	internal void AppendFilteredCavity(ProceduralHeadWoundWorkspace source)
	{
		// Compact only wound data; the original skin's exact vertex prefix is retained.
		_cavityRemap.Clear();
		foreach (int index in source._filteredSubmeshes[0])
		{
			if (!_cavityRemap.TryGetValue(index, out int mapped))
			{
				mapped = CavityVertices.Count; _cavityRemap.Add(index, mapped);
				CavityVertices.Add(source.SourceVertices[index]); CavityNormals.Add(source.SourceNormals[index]);
				CavityTangents.Add(source.SourceTangents[index]); CavityColors.Add(source.SourceColors[index]);
				Vector4 uv = source.SourceUvs[0][index]; CavityUvs.Add(new Vector2(uv.x, uv.y));
			}
			CavityTriangles.Add(mapped);
		}
	}
	private readonly Dictionary<int, int> _cavityRemap = new Dictionary<int, int>();
}

internal static partial class ProceduralHeadWoundGeometry
{
	internal const int MaximumCombinedVertices = 16384;
	internal sealed class UnionScratch
	{
		internal readonly ProceduralHeadWoundWorkspace UnionOriginal = new ProceduralHeadWoundWorkspace();
		internal readonly ProceduralHeadWoundWorkspace UnionCavity = new ProceduralHeadWoundWorkspace();
		internal readonly ProceduralHeadWoundWorkspace[] UnionSingles =
		{
			new ProceduralHeadWoundWorkspace(), new ProceduralHeadWoundWorkspace(),
			new ProceduralHeadWoundWorkspace(), new ProceduralHeadWoundWorkspace()
		};
		internal readonly WoundProfile[] UnionProfiles = new WoundProfile[4];
		internal readonly WoundProfile[] OtherProfiles = new WoundProfile[3];
		internal readonly int[] VertexOffsets = new int[4];
		internal readonly List<int> RefinedCavity = new List<int>();
	}

	// Exclusively owned workspace and pure-data scratch, usable on a worker. Independent surface boundaries are
	// resolved from the immutable skin; only subtraction runs on progressively cut data.
	internal static bool TryGenerateMultiple(ProceduralHeadWoundWorkspace workspace,
		IReadOnlyList<ProceduralWoundDescriptor> descriptors, out ProceduralWoundMetrics metrics, out string failure)
	{
		metrics = default; failure = null;
		workspace.LastSinglesMs = workspace.LastSkinMs = workspace.LastCanalsMs = 0;
		if (descriptors == null || descriptors.Count < 1 || descriptors.Count > 4)
		{ failure = "head requires one to four ordered wound descriptors"; return false; }
		if (descriptors.Count == 1) return TryGenerate(workspace, descriptors[0], out metrics, out failure);
		var scratch = workspace.Union ?? (workspace.Union = new UnionScratch());
		var timer = Stopwatch.StartNew();
		scratch.UnionOriginal.CopySource(workspace);
		int originalCount = workspace.SourceVertices.Count;
		for (int i = 0; i < descriptors.Count; i++)
		{
			var single = scratch.UnionSingles[i]; single.CopySource(scratch.UnionOriginal);
			if (!TryGenerate(single, descriptors[i], out metrics, out failure)) { failure = $"wound {i + 1}: {failure}"; return false; }
			Vector3 delta = descriptors[i].Exit - descriptors[i].Entry;
			scratch.UnionProfiles[i] = BuildProfile(single, descriptors[i], delta.normalized, delta.magnitude);
		}

		workspace.LastSinglesMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
		workspace.CopySource(scratch.UnionOriginal);
		for (int i = 0; i < descriptors.Count; i++)
		{
			scratch.VertexOffsets[i] = workspace.SourceVertices.Count - originalCount;
			workspace.AppendGenerated(scratch.UnionSingles[i], originalCount);
		}
		workspace.BaseVertexCount = workspace.SourceVertices.Count;
		workspace.BeginClassification();
		workspace.GeneratedVertexLimit = MaximumCombinedVertices - (workspace.SourceVertices.Count - originalCount);
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			var source = scratch.UnionOriginal.GetSourceSubmesh(submesh);
			var filtered = workspace.GetMutableFilteredSubmesh(submesh);
			for (int triangle = 0; triangle < source.Count / 3; triangle++)
			{
				int selected = -1, otherCount = 0;
				for (int i = 0; i < descriptors.Count; i++)
				{
					var single = scratch.UnionSingles[i]; var starts = single.FragmentStarts[submesh];
					int start = starts[triangle], end = starts[triangle + 1]; var fragments = single.GetFilteredSubmesh(submesh);
					bool unchanged = end - start == 3 && fragments[start] == source[triangle * 3] &&
						fragments[start + 1] == source[triangle * 3 + 1] && fragments[start + 2] == source[triangle * 3 + 2];
					if (unchanged) continue;
					if (selected < 0) selected = i;
					else if (!SameProfile(descriptors[selected], descriptors[i])) scratch.OtherProfiles[otherCount++] = scratch.UnionProfiles[i];
				}
				if (selected < 0) { filtered.Add(source[triangle * 3]); filtered.Add(source[triangle * 3 + 1]); filtered.Add(source[triangle * 3 + 2]); continue; }
				workspace.ResetClipCaches();
				var chosen = scratch.UnionSingles[selected]; var ranges = chosen.FragmentStarts[submesh]; var indices = chosen.GetFilteredSubmesh(submesh);
				for (int k = ranges[triangle]; k < ranges[triangle + 1]; k += 3)
				{
					int a = Remap(indices[k], selected, originalCount, scratch), b = Remap(indices[k + 1], selected, originalCount, scratch), c = Remap(indices[k + 2], selected, originalCount, scratch);
					if (otherCount == 0) { filtered.Add(a); filtered.Add(b); filtered.Add(c); continue; }
					var profile = new WoundProfile(scratch.OtherProfiles[0], scratch.OtherProfiles, otherCount);
					int clipped = 0, removed = 0, entryRemoved = 0, exitRemoved = 0;
					if (!ClipIntersectedTriangle(workspace, profile, filtered, submesh, a, b, c, 0,
						0.000001f, 0.000009f, ref clipped, ref removed, ref entryRemoved, ref exitRemoved, out failure))
					{ failure = "overlapping skin fragments: " + failure; return false; }
				}
			}
		}
		if (workspace.SourceVertices.Count - originalCount > MaximumCombinedVertices)
		{ failure = "combined skin exceeded 16384 generated vertices"; return false; }
		workspace.LastSkinMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
		// Subtract resets scratch cavity data. Append after all skin operations finish.
		for (int i = 0; i < descriptors.Count; i++)
		{
			bool duplicate = false;
			for (int earlier = 0; earlier < i; earlier++) if (SameProfile(descriptors[earlier], descriptors[i])) duplicate = true;
			if (duplicate) continue;
			scratch.UnionCavity.ImportCavity(scratch.UnionSingles[i]);
			scratch.UnionCavity.BeginClassification();
			scratch.UnionCavity.GetMutableFilteredSubmesh(0).AddRange(scratch.UnionCavity.GetSourceSubmesh(0));
			int otherCount = 0;
			for (int j = 0; j < descriptors.Count; j++)
				if (j != i && !SameProfile(descriptors[i], descriptors[j])) scratch.OtherProfiles[otherCount++] = scratch.UnionProfiles[j];
			if (otherCount > 0 && !Subtract(scratch.UnionCavity, new WoundProfile(scratch.OtherProfiles[0], scratch.OtherProfiles, otherCount), out failure, cavity: true))
			{ failure = $"canal {i + 1} subtraction: {failure}"; return false; }
			if (!RefineObstructions(scratch.UnionCavity, descriptors, scratch, out failure)) return false;
			workspace.AppendFilteredCavity(scratch.UnionCavity);
			if (workspace.CavityVertices.Count > MaximumCombinedVertices)
			{ failure = "combined canals exceeded 16384 vertices after intersection trimming"; return false; }
		}
		workspace.LastCanalsMs = timer.Elapsed.TotalMilliseconds;
		return ValidateCombined(workspace, descriptors, originalCount, out failure);
	}

	private static int Remap(int index, int selected, int originalCount, UnionScratch scratch) => index < originalCount ? index : index + scratch.VertexOffsets[selected];

	private static bool RefineObstructions(ProceduralHeadWoundWorkspace workspace, IReadOnlyList<ProceduralWoundDescriptor> descriptors, UnionScratch scratch, out string failure)
	{
		failure = null;
		var filtered = workspace.GetMutableFilteredSubmesh(0);
		scratch.RefinedCavity.Clear();
		for (int i = 0; i < filtered.Count; i += 3)
		{
			int a = filtered[i], b = filtered[i + 1], c = filtered[i + 2], obstructing = -1;
			for (int j = 0; j < descriptors.Count; j++)
			{
				Vector3 delta = descriptors[j].Exit - descriptors[j].Entry;
				if (RayTriangle(descriptors[j].Entry - delta.normalized * 0.001f, delta.normalized,
					workspace.SourceVertices[a], workspace.SourceVertices[b], workspace.SourceVertices[c], out float distance) && distance < ClearanceLength(descriptors[j]))
				{ obstructing = j; break; }
			}
			if (obstructing < 0) { scratch.RefinedCavity.Add(a); scratch.RefinedCavity.Add(b); scratch.RefinedCavity.Add(c); continue; }
			workspace.ResetClipCaches();
			int clipped = 0, removed = 0, entryRemoved = 0, exitRemoved = 0;
			if (!ClipIntersectedTriangle(workspace, scratch.UnionProfiles[obstructing], scratch.RefinedCavity, 0, a, b, c, MaximumRefinementDepth - 3,
				0.000001f, 0.000009f, ref clipped, ref removed, ref entryRemoved, ref exitRemoved, out failure)) return false;
		}
		filtered.Clear(); filtered.AddRange(scratch.RefinedCavity);
		return true;
	}

	private static bool SameProfile(ProceduralWoundDescriptor a, ProceduralWoundDescriptor b) =>
		a.Entry.Equals(b.Entry) && a.Exit.Equals(b.Exit) && a.EntryRadius == b.EntryRadius &&
		a.ExitRadius == b.ExitRadius && a.Seed == b.Seed && a.ThroughShot == b.ThroughShot && a.GenerateExitLip == b.GenerateExitLip;

	// Entry craters intentionally have an interior cap. Do not "repair" it into a
	// through-hole when combining it with other wounds.
	private static float ClearanceLength(ProceduralWoundDescriptor descriptor) =>
		Vector3.Distance(descriptor.Entry, descriptor.Exit) * (descriptor.ThroughShot ? 1f : 0.65f) +
		(descriptor.ThroughShot ? 0.002f : 0f);

	private static bool Subtract(ProceduralHeadWoundWorkspace workspace, WoundProfile profile, out string failure, bool cavity = false)
	{
		failure = null; workspace.GeneratedVertexLimit = MaximumCombinedVertices; workspace.BeginClassification();
		float entry = Mathf.Clamp(TwoPi * profile.EntryRadius / EntryAngularSegmentCount * 0.95f,
			MinimumRefinementEdgeLength, MaximumRefinementEdgeLength);
		float exit = Mathf.Clamp(TwoPi * profile.ExitRadius / ExitAngularSegmentCount * 0.95f,
			MinimumRefinementEdgeLength, MaximumRefinementEdgeLength);
		if (cavity) { entry = Mathf.Max(entry, 0.003f); exit = Mathf.Max(exit, 0.003f); }
		int clipped = 0, removed = 0, entryRemoved = 0, exitRemoved = 0;
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			var source = workspace.GetSourceSubmesh(submesh);
			var filtered = workspace.GetMutableFilteredSubmesh(submesh);
			for (int i = 0; i < source.Count; i += 3)
			{
				int a = source[i], b = source[i + 1], c = source[i + 2];
				if (!TriangleApproachesWoundAxis(profile, workspace.SourceVertices[a], workspace.SourceVertices[b], workspace.SourceVertices[c]))
				{ filtered.Add(a); filtered.Add(b); filtered.Add(c); continue; }
				if (!ClipIntersectedTriangle(workspace, profile, filtered, submesh, a, b, c, cavity ? MaximumRefinementDepth : 0, entry * entry, exit * exit,
					ref clipped, ref removed, ref entryRemoved, ref exitRemoved, out failure)) return false;
			}
		}
		return true;
	}

	internal static bool ValidateCombined(ProceduralHeadWoundWorkspace workspace,
		IReadOnlyList<ProceduralWoundDescriptor> descriptors, int originalCount, out string failure)
	{
		failure = null;
		if (workspace.SourceVertices.Count < originalCount || workspace.SourceVertices.Count - originalCount > MaximumCombinedVertices ||
			workspace.CavityVertices.Count > MaximumCombinedVertices || workspace.CavityTriangles.Count == 0)
		{ failure = "combined head geometry is empty or exceeds its budget"; return false; }
		foreach (Vector3 v in workspace.SourceVertices) if (!Finite(v)) { failure = "nonfinite combined skin"; return false; }
		foreach (Vector3 v in workspace.CavityNormals) if (!Finite(v)) { failure = "nonfinite combined canal normal"; return false; }
		foreach (Vector3 v in workspace.CavityVertices) if (!Finite(v)) { failure = "nonfinite combined canal"; return false; }
		for (int i = 0; i < workspace.SubMeshCount; i++)
			foreach (int index in workspace.GetFilteredSubmesh(i))
				if (index < 0 || index >= workspace.SourceVertices.Count) { failure = "invalid combined skin index"; return false; }
		foreach (int index in workspace.CavityTriangles)
			if (index < 0 || index >= workspace.CavityVertices.Count) { failure = "invalid combined canal index"; return false; }
		foreach (var descriptor in descriptors)
		{
			Vector3 delta = descriptor.Exit - descriptor.Entry;
			Vector3 direction = delta.normalized;
			Vector3 origin = descriptor.Entry - direction * 0.001f;
			for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
				if (Obstructed(workspace.SourceVertices, workspace.GetFilteredSubmesh(submesh), origin, direction, ClearanceLength(descriptor)))
				{ failure = "combined skin obstructs a retained trajectory"; return false; }
			if (Obstructed(workspace.CavityVertices, workspace.CavityTriangles, origin, direction, ClearanceLength(descriptor)))
			{ failure = "combined canal obstructs a retained trajectory"; return false; }
		}
		return true;
	}

	private static bool Obstructed(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, Vector3 origin, Vector3 direction, float length)
	{
		for (int i = 0; i < indices.Count; i += 3)
		{
			Vector3 a = vertices[indices[i]], e1 = vertices[indices[i + 1]] - a, e2 = vertices[indices[i + 2]] - a;
			Vector3 p = Vector3.Cross(direction, e2); float determinant = Vector3.Dot(e1, p);
			if (Mathf.Abs(determinant) < 1e-10f) continue;
			float inverse = 1f / determinant; Vector3 t = origin - a;
			float u = Vector3.Dot(t, p) * inverse;
			if (u < 0 || u > 1) continue;
			Vector3 q = Vector3.Cross(t, e1); float v = Vector3.Dot(direction, q) * inverse;
			if (v < 0 || u + v > 1) continue;
			float distance = Vector3.Dot(e2, q) * inverse;
			if (distance > 0.00001f && distance < length) return true;
		}
		return false;
	}
}
}
