using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class ProceduralLimbCutGeometry
{
    private static void EnsureSourceIdentity(SourceData source)
    {
        if (source.DiagnosticIdentity != null) return;
        using (MemoryStream bytes = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(bytes))
        {
            writer.Write(source.Name ?? "");
            foreach (Vector3 p in source.Positions) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
            foreach (SourceTriangle t in source.Triangles)
            { writer.Write(t.A); writer.Write(t.B); writer.Write(t.C); writer.Write(t.Submesh); }
            foreach (BoneWeight w in source.Weights)
            {
                writer.Write(w.boneIndex0); writer.Write(w.weight0); writer.Write(w.boneIndex1); writer.Write(w.weight1);
                writer.Write(w.boneIndex2); writer.Write(w.weight2); writer.Write(w.boneIndex3); writer.Write(w.weight3);
            }
            foreach (Matrix4x4 pose in source.Bindposes) for (int i = 0; i < 16; i++) writer.Write(pose[i]);
            source.DiagnosticIdentity = LimbCutCaptureSession.Hash(bytes.ToArray());
        }
    }

    private static LimbClothingProfiles.Context CreateClothingContext(SourceData source, BindCut cut,
        CutSpec spec, float worldScale, bool gib)
    {
        if (cut.SegmentLength <= 0) return null; // synthetic legacy topology hooks
        if (LimbClothingProfiles.NeedsIdentity(source.Name)) EnsureSourceIdentity(source);
        return new LimbClothingProfiles.Context {
            BoneCenter = cut.Center + (gib ? cut.Axis * cut.DistalGap : Vector3.zero),
            SegmentLengthWorld = cut.SegmentLength * worldScale, Offset = spec.FractionOffset,
            Mesh = source.Name, Source = source.DiagnosticIdentity, ToBone = cut.ToBone, FromBone = cut.FromBone,
            BoneNames = spec.BoneNames,
            SharedCore = gib ? spec.SharedGibClothCore : spec.SharedBodyClothCore };
    }

    // Stage every duplicate seam displacement and validate affected retained
    // triangles before touching the builder. A rejected fold leaves the accepted
    // axial tear and source UVs/skinning exactly as they were.
    private static bool TryApplyClothFold(IndexedBuilder builder, IReadOnlyList<BoundaryEdge> edges,
        List<int> loop, ProceduralLimbWoundGeometry.BoundaryVertex[] original,
        ProceduralLimbWoundGeometry.BoundaryVertex[] replacement, float worldScale, out float movementScale, out string failure)
    {
        failure = null;
        movementScale = 1f;
        var candidates = new HashSet<int>(loop);
        foreach (BoundaryEdge edge in edges) { candidates.Add(edge.A); candidates.Add(edge.B); }
        var moves = new Dictionary<int, Vector3>();
        float toleranceSquared = BoundaryWeldToleranceWorld / worldScale;
        toleranceSquared *= toleranceSquared;
        foreach (int index in candidates)
        {
            Vector3 p = builder.Vertices[index].Position;
            int closest = -1; float distance = float.PositiveInfinity;
            for (int i = 0; i < original.Length; i++)
            {
                float d = (p - original[i].Position).sqrMagnitude;
                if (d > toleranceSquared || d >= distance) continue;
                closest = i; distance = d;
            }
            if (closest >= 0) moves[index] = p + replacement[closest].Position - original[closest].Position;
        }
        // Determine one safe scale for the complete seam. This is bounded
        // decoration fitting, not another classification/cut-position retry.
        bool safe = false;
        for (int attempt = 0; attempt < 12 && !safe; attempt++)
        {
            movementScale = attempt == 11 ? 0f : Mathf.Pow(.5f, attempt);
            safe = true;
            foreach (List<int> submesh in builder.Submeshes)
            for (int i = 0; i < submesh.Count && safe; i += 3)
            {
                int ia = submesh[i], ib = submesh[i+1], ic = submesh[i+2];
                if (!moves.ContainsKey(ia) && !moves.ContainsKey(ib) && !moves.ContainsKey(ic)) continue;
                Vector3 a = builder.Vertices[ia].Position, b = builder.Vertices[ib].Position, c = builder.Vertices[ic].Position;
                Vector3 na = moves.TryGetValue(ia, out var pa) ? Vector3.Lerp(a,pa,movementScale) : a;
                Vector3 nb = moves.TryGetValue(ib, out var pb) ? Vector3.Lerp(b,pb,movementScale) : b;
                Vector3 nc = moves.TryGetValue(ic, out var pc) ? Vector3.Lerp(c,pc,movementScale) : c;
                Vector3 before = Vector3.Cross(b-a,c-a), after = Vector3.Cross(nb-na,nc-na);
                if (after.sqrMagnitude < 1e-16f || Vector3.Dot(before, after) <= 0)
                    safe = false;
            }
        }
        if (!safe) { failure = "retained seam has an existing collapsed triangle"; return false; }
        for (int i = 0; i < replacement.Length; i++)
            replacement[i] = new ProceduralLimbWoundGeometry.BoundaryVertex(
                Vector3.Lerp(original[i].Position,replacement[i].Position,movementScale),replacement[i].Normal,replacement[i].Weight);
        foreach (var move in moves)
        {
            VertexData vertex = builder.Vertices[move.Key]; vertex.Position = Vector3.Lerp(vertex.Position,move.Value,movementScale);
            builder.Vertices[move.Key] = vertex;
        }
        return true;
    }
}
