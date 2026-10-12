using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Fractures
{

// Geometry is selected in bind space, never from a posed BakeMesh or a collider.
internal sealed class FractureSurface
{
    internal sealed class Snapshot
    {
        internal readonly Vector3[] Vertices, Normals;
        internal readonly BoneWeight[] Weights;
        internal readonly Matrix4x4[] BindPoses;
        internal readonly int[] Triangles;
        internal Snapshot(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.vertexCount > 100000 || mesh.blendShapeCount != 0)
                throw new InvalidOperationException("unreadable, oversized, or blend-shaped fracture covering");
            Vertices = mesh.vertices; Normals = mesh.normals; Weights = mesh.boneWeights;
            BindPoses = mesh.bindposes; Triangles = mesh.triangles; // Unity concatenates in stable submesh order.
            if (Vertices.Length == 0 || Normals.Length != Vertices.Length || Weights.Length != Vertices.Length ||
                Triangles.Length % 3 != 0) throw new InvalidOperationException("invalid fracture covering skin layout");
            for (int i = 0; i < Vertices.Length; i++)
                if (!Finite(Vertices[i]) || !Finite(Normals[i])) throw new InvalidOperationException("nonfinite fracture covering");
            foreach (int index in Triangles) if (index < 0 || index >= Vertices.Length)
                throw new InvalidOperationException("invalid fracture covering indices");
        }
    }

    internal readonly SkinnedMeshRenderer Renderer;
    internal readonly Mesh Original;
    internal readonly Snapshot Data;
    internal readonly Transform[] Bones;
    internal readonly int RootIndex;
    internal readonly string StableKey;
    internal readonly Matrix4x4 ToBone, FromBone;
    internal readonly Vector3 Origin, Direction, Tangent;
    internal readonly float Scale;
    private readonly Vector3[] _points;
    private readonly bool[] _eligible;

    internal FractureSurface(SkinnedMeshRenderer renderer, Snapshot data, string rootName, string endName,
        FractureAppearance appearance, string stableKey)
    {
        Renderer = renderer; Original = renderer.sharedMesh; Data = data; StableKey = stableKey;
        Bones = renderer.bones;
        if (Bones.Length != data.BindPoses.Length) throw new InvalidOperationException("fracture bone palette mismatch");
        RootIndex = FindBone(Bones, rootName);
        int end = FindBone(Bones, endName);
        if (RootIndex < 0 || end < 0) throw new InvalidOperationException("fracture segment is absent from covering palette");
        ToBone = data.BindPoses[RootIndex]; FromBone = ToBone.inverse;
        Vector3 endpoint = (ToBone * data.BindPoses[end].inverse).MultiplyPoint3x4(Vector3.zero);
        if (!Finite(endpoint) || endpoint.sqrMagnitude < 0.001f || Mathf.Abs(ToBone.determinant) < 0.000001f)
            throw new InvalidOperationException("degenerate fracture bind segment");
        Vector3 axis = endpoint.normalized;
        // A fixed bone-local reference is shared by the world and first-person palettes.
        Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.forward)) < 0.85f ? Vector3.forward : Vector3.right;
        Direction = Quaternion.AngleAxis(appearance.Angle, axis) * Vector3.ProjectOnPlane(reference, axis).normalized;
        Tangent = Vector3.Cross(axis, Direction).normalized;
        Origin = endpoint * appearance.Fraction;
        Vector3 scale = Bones[RootIndex].lossyScale;
        Scale = (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f;
        if (!Finite(scale) || Scale < 0.001f || Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) > Scale * 1.05f)
            throw new InvalidOperationException("unsupported fracture anatomical scale");
        var segmentBones = new bool[Bones.Length];
        for (int i = 0; i < Bones.Length; i++) segmentBones[i] = Bones[i] != null &&
            (Bones[i] == Bones[RootIndex] || Bones[i].IsChildOf(Bones[RootIndex])) &&
            Bones[i] != Bones[end] && !Bones[i].IsChildOf(Bones[end]);
        _points = new Vector3[data.Vertices.Length];
        var weights = new float[_points.Length];
        for (int i = 0; i < _points.Length; i++)
        {
            _points[i] = ToBone.MultiplyPoint3x4(data.Vertices[i]);
            BoneWeight w = data.Weights[i];
            weights[i] = Membership(w.boneIndex0, w.weight0, segmentBones) + Membership(w.boneIndex1, w.weight1, segmentBones) +
                Membership(w.boneIndex2, w.weight2, segmentBones) + Membership(w.boneIndex3, w.weight3, segmentBones);
        }
        _eligible = new bool[data.Triangles.Length / 3];
        for (int i = 0; i < _eligible.Length; i++)
        {
            float a = weights[data.Triangles[i * 3]], b = weights[data.Triangles[i * 3 + 1]], c = weights[data.Triangles[i * 3 + 2]];
            _eligible[i] = Mathf.Min(a, Mathf.Min(b, c)) >= 0.2f && a + b + c >= 1.35f;
        }
    }

    private static float Membership(int index, float weight, bool[] segment)
    {
        if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0f ||
            (weight > 0f && (index < 0 || index >= segment.Length))) throw new InvalidOperationException("invalid fracture skin weight");
        return weight > 0f && segment[index] ? weight : 0f;
    }
    internal static int FindBone(Transform[] bones, string name)
    { for (int i = 0; i < bones.Length; i++) if (bones[i] != null && bones[i].name == name) return i; return -1; }

    internal readonly struct Hit
    {
        internal readonly Vector3 Point, Normal;
        internal readonly BoneWeight Weight;
        internal readonly float Distance;
        internal Hit(Vector3 point, Vector3 normal, BoneWeight weight, float distance)
        { Point = point; Normal = normal; Weight = weight; Distance = distance; }
    }

    internal bool Raycast(Vector3 origin, out Hit hit)
    {
        hit = default; float farthest = -1f; int triangle = -1; Vector3 bary = default;
        for (int i = 0; i < _eligible.Length; i++)
        {
            if (!_eligible[i]) continue;
            int a = Data.Triangles[i * 3], b = Data.Triangles[i * 3 + 1], c = Data.Triangles[i * 3 + 2];
            if (!Intersect(origin, Direction, _points[a], _points[b], _points[c], out float t, out Vector3 candidate) ||
                t * Scale > 0.3f || t <= farthest + 0.00001f) continue;
            farthest = t; triangle = i * 3; bary = candidate;
        }
        if (triangle < 0) return false;
        int ia = Data.Triangles[triangle], ib = Data.Triangles[triangle + 1], ic = Data.Triangles[triangle + 2];
        Vector3 sourceNormal = Data.Normals[ia] * bary.x + Data.Normals[ib] * bary.y + Data.Normals[ic] * bary.z;
        Vector3 normal = FromBone.transpose.MultiplyVector(sourceNormal).normalized;
        if (Vector3.Dot(normal, Direction) < 0f) normal = -normal;
        hit = new Hit(origin + Direction * farthest, normal,
            Interpolate(Data.Weights[ia], Data.Weights[ib], Data.Weights[ic], bary), farthest * Scale);
        return true;
    }

    internal static bool Prefer(float distance, string key, float previous, string previousKey) =>
        distance > previous + 0.0001f || (Mathf.Abs(distance - previous) <= 0.0001f && string.CompareOrdinal(key, previousKey) < 0);

    // Bind-space work is immutable. Precompute each influence once rather than
    // multiplying a world matrix by a bind matrix for every anchor every frame.
    internal sealed class PoseSample
    {
        private readonly Transform[] _bones = new Transform[4];
        private readonly Vector3[] _local = new Vector3[4];
        private readonly float[] _weights;
        internal PoseSample(FractureSurface owner, Hit hit)
        {
            BoneWeight w = hit.Weight;
            _weights = new[] { w.weight0, w.weight1, w.weight2, w.weight3 };
            int[] indices = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
            Vector3 source = owner.FromBone.MultiplyPoint3x4(hit.Point);
            for (int i = 0; i < 4; i++)
            {
                if (_weights[i] <= 0) continue;
                int index = indices[i];
                if ((uint)index >= owner.Bones.Length) continue;
                _bones[i] = owner.Bones[index];
                _local[i] = owner.Data.BindPoses[index].MultiplyPoint3x4(source);
            }
        }
        internal bool TryPose(out Vector3 point)
        {
            point = Vector3.zero;
            for (int i = 0; i < 4; i++)
            {
                if (_weights[i] <= 0) continue;
                if (_bones[i] == null) return false;
                point += _bones[i].TransformPoint(_local[i]) * _weights[i];
            }
            return Finite(point);
        }
    }

    // Follow the collar's attachment point, but never use these blended matrices
    // to deform exposed bone vertices. A child of the anatomical bone owns their
    // orientation; only its translation follows this surface sample.
    internal bool TryPose(Hit hit, out Vector3 point)
    {
        point = Vector3.zero; Vector3 source = FromBone.MultiplyPoint3x4(hit.Point);
        BoneWeight w = hit.Weight;
        return AccumulatePose(w.boneIndex0, w.weight0, source, ref point) &&
            AccumulatePose(w.boneIndex1, w.weight1, source, ref point) &&
            AccumulatePose(w.boneIndex2, w.weight2, source, ref point) &&
            AccumulatePose(w.boneIndex3, w.weight3, source, ref point) && Finite(point);
    }
    private bool AccumulatePose(int index, float weight, Vector3 source, ref Vector3 point)
    {
        if (weight <= 0) return true;
        if (index < 0 || index >= Bones.Length || Bones[index] == null) return false;
        point += (Bones[index].localToWorldMatrix * Data.BindPoses[index]).MultiplyPoint3x4(source) * weight;
        return true;
    }

    internal static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        out float distance, out Vector3 bary)
    {
        distance = 0; bary = default;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(direction, e2);
        float determinant = Vector3.Dot(e1, p);
        if (Mathf.Abs(determinant) < 0.00000001f) return false;
        float inverse = 1f / determinant; Vector3 delta = origin - a;
        float u = Vector3.Dot(delta, p) * inverse;
        Vector3 q = Vector3.Cross(delta, e1); float v = Vector3.Dot(direction, q) * inverse;
        distance = Vector3.Dot(e2, q) * inverse;
        bary = new Vector3(1f - u - v, u, v);
        return distance >= 0f && u >= -0.00001f && v >= -0.00001f && u + v <= 1.00001f;
    }

    internal static BoneWeight Interpolate(BoneWeight a, BoneWeight b, BoneWeight c, Vector3 bary)
    {
        var weights = new Dictionary<int, float>();
        Add(weights, a, Mathf.Max(0, bary.x)); Add(weights, b, Mathf.Max(0, bary.y)); Add(weights, c, Mathf.Max(0, bary.z));
        var sorted = new List<KeyValuePair<int, float>>(weights);
        sorted.Sort((x, y) => { int order = y.Value.CompareTo(x.Value); return order != 0 ? order : x.Key.CompareTo(y.Key); });
        float sum = 0; for (int i = 0; i < Math.Min(4, sorted.Count); i++) sum += sorted[i].Value;
        if (sum <= 0) throw new InvalidOperationException("empty fracture surface skin weight");
        var result = new BoneWeight();
        if (sorted.Count > 0) { result.boneIndex0 = sorted[0].Key; result.weight0 = sorted[0].Value / sum; }
        if (sorted.Count > 1) { result.boneIndex1 = sorted[1].Key; result.weight1 = sorted[1].Value / sum; }
        if (sorted.Count > 2) { result.boneIndex2 = sorted[2].Key; result.weight2 = sorted[2].Value / sum; }
        if (sorted.Count > 3) { result.boneIndex3 = sorted[3].Key; result.weight3 = sorted[3].Value / sum; }
        return result;
    }
    private static void Add(Dictionary<int, float> weights, BoneWeight weight, float factor)
    {
        Accumulate(weights, weight.boneIndex0, weight.weight0 * factor); Accumulate(weights, weight.boneIndex1, weight.weight1 * factor);
        Accumulate(weights, weight.boneIndex2, weight.weight2 * factor); Accumulate(weights, weight.boneIndex3, weight.weight3 * factor);
    }
    private static void Accumulate(Dictionary<int, float> weights, int index, float value)
    { if (value > 0) weights[index] = weights.TryGetValue(index, out float previous) ? previous + value : value; }
    internal static bool Finite(Vector3 point) => !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
        !float.IsNaN(point.y) && !float.IsInfinity(point.y) && !float.IsNaN(point.z) && !float.IsInfinity(point.z);
}
}
