using System;
using System.Collections.Generic;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.Fractures
{

// One owner and one renderer contain the collar plus the one/two rigid ends.
// Native LOD membership is additive and removal touches only our own renderer.
internal sealed class FractureVisual : IDisposable
{
    internal readonly FractureSurface Owner;
    internal SkinnedMeshRenderer Renderer { get; private set; }
    private Mesh _mesh;
    private LODGroup _group;
    private Transform[] _anchors;
    private FractureSurface.Hit[] _roots;
    private FractureSurface.PoseSample[] _poses;
    private Vector3 _woundWorld;
    internal int BoneCount { get; private set; }

    internal FractureVisual(FractureSurface owner, FractureAppearance appearance, Material material)
    {
        Owner = owner;
        _roots = new FractureSurface.Hit[appearance.BoneCount];
        _poses = new FractureSurface.PoseSample[appearance.BoneCount];
        var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>();
        var colors = new List<Color>(); var weights = new List<BoneWeight>(); var triangles = new List<int>();
        Vector3 direction = owner.Direction, tangent = owner.Tangent, up = Vector3.Cross(direction, tangent).normalized;
        // The surface ray still chooses the covering. Ends rise only 20-30
        // degrees out of the limb axis, with the optional end facing proximally.
        float tilt = appearance.Tilt * Mathf.Deg2Rad;
        Vector2 primary = ProceduralLimbWoundGeometry.ResolveBoneRadiiWorld(appearance.IsLeg, appearance.Level, 0);
        Vector2 secondary = appearance.BoneCount == 2 ? ProceduralLimbWoundGeometry.ResolveBoneRadiiWorld(appearance.IsLeg, appearance.Level, 1) : Vector2.zero;
        float separation = appearance.BoneCount == 2 ? (primary.x + secondary.x) * 1.2f / owner.Scale : 0f;
        float halfWidth = (separation * 0.5f + Mathf.Max(primary.x, secondary.x) * 1.15f / owner.Scale) + 0.006f / owner.Scale;
        float halfHeight = Mathf.Max(primary.y, secondary.y) * 1.15f / owner.Scale + 0.006f / owner.Scale;
        const int sides = 20;
        // Three overlapping concentric rings plus a filled bloody center conceal the intact source.
        AddCollar(owner.Origin, new Vector2(0.5f, 0.5f), 0.003f);
        for (int ring = 0; ring < 3; ring++)
        {
            float radius = ring == 0 ? 0.45f : ring == 1 ? 0.78f : 1f;
            for (int i = 0; i < sides; i++)
            {
                float angle = i * Mathf.PI * 2f / sides;
                float irregular = 1f + Mathf.Sin(angle * 5f + (appearance.ShapeSeed & 255u)) * 0.06f;
                float x = Mathf.Cos(angle) * radius * irregular, y = Mathf.Sin(angle) * radius * irregular;
                AddCollar(owner.Origin + tangent * (x * halfWidth) + up * (y * halfHeight),
                    new Vector2(x, y) * 0.5f + Vector2.one * 0.5f, ring == 1 ? 0.004f : ring == 2 ? 0.0008f : 0.003f);
            }
        }
        for (int i = 0; i < sides; i++)
        {
            int next = (i + 1) % sides;
            Triangle(0, 1 + i, 1 + next);
            for (int ring = 0; ring < 2; ring++)
            {
                int a = 1 + ring * sides + i, b = 1 + ring * sides + next;
                Triangle(a, a + sides, b + sides); Triangle(a, b + sides, b);
            }
        }
        for (int bone = 0; bone < appearance.BoneCount; bone++)
        {
            Vector3 boneDirection = up * ((appearance.Site == 0 ? 1f : -1f) * Mathf.Cos(tilt)) + direction * Mathf.Sin(tilt);
            Vector3 ray = owner.Origin + tangent * (appearance.BoneCount == 1 ? 0 : (bone == 0 ? -0.5f : 0.5f) * separation);
            if (!owner.Raycast(ray, out var root)) throw new InvalidOperationException("fracture bone root misses outer covering");
            _roots[bone] = root;
            _poses[bone] = new FractureSurface.PoseSample(owner, root);
            var surface = ProceduralLimbWoundGeometry.GenerateFractureBone(appearance.IsLeg, appearance.Level, bone,
                appearance.BoneSeed(bone), owner.Scale, root.Point + direction * (0.003f / owner.Scale), boneDirection, tangent, owner.Bones.Length + bone);
            int offset = vertices.Count;
            foreach (var vertex in surface.Vertices)
            {
                vertices.Add(owner.FromBone.MultiplyPoint3x4(vertex.Position));
                normals.Add(owner.ToBone.transpose.MultiplyVector(vertex.Normal).normalized);
                uv.Add(vertex.Uv); colors.Add(vertex.Mask); weights.Add(vertex.Weight);
            }
            foreach (int index in surface.Triangles) triangles.Add(index + offset);
            BoneCount++;
        }
        try
        {
            var bones = new Transform[owner.Bones.Length + appearance.BoneCount];
            var binds = new Matrix4x4[bones.Length];
            Array.Copy(owner.Bones, bones, owner.Bones.Length); Array.Copy(owner.Data.BindPoses, binds, owner.Bones.Length);
            _anchors = new Transform[appearance.BoneCount];
            for (int i = 0; i < _anchors.Length; i++)
            {
                var anchor = new GameObject("RPT rigid fracture end") { hideFlags = HideFlags.HideAndDontSave }.transform;
                _anchors[i] = anchor;
                anchor.SetParent(owner.Bones[owner.RootIndex], false); anchor.localPosition = _roots[i].Point;
                bones[owner.Bones.Length + i] = anchor;
                binds[owner.Bones.Length + i] = Matrix4x4.Translate(-_roots[i].Point) * owner.ToBone;
            }
            _mesh = new Mesh { name = "RPT fracture collar and rigid bone", hideFlags = HideFlags.HideAndDontSave };
            _mesh.SetVertices(vertices); _mesh.SetNormals(normals); _mesh.SetUVs(0, uv); _mesh.SetColors(colors);
            _mesh.boneWeights = weights.ToArray(); _mesh.bindposes = binds; _mesh.SetTriangles(triangles, 0); _mesh.RecalculateBounds();
            var host = new GameObject("RPT fracture") { hideFlags = HideFlags.HideAndDontSave, layer = owner.Renderer.gameObject.layer };
            host.transform.SetParent(owner.Renderer.transform, false);
            Renderer = host.AddComponent<SkinnedMeshRenderer>(); Renderer.enabled = false;
            Renderer.sharedMesh = _mesh; Renderer.sharedMaterial = material; Renderer.bones = bones;
            Renderer.rootBone = owner.Renderer.rootBone; Renderer.quality = owner.Renderer.quality;
            Renderer.updateWhenOffscreen = owner.Renderer.updateWhenOffscreen;
            Bounds bounds = owner.Renderer.localBounds; bounds.Encapsulate(_mesh.bounds); bounds.Expand(0.1f / owner.Scale);
            Renderer.localBounds = bounds;
            LimbSeverMaterialProvider.ApplyVariation(Renderer, 0, appearance.ShapeSeed, false);
            _group = owner.Renderer.GetComponentInParent<LODGroup>();
            ChangeLodMembership(true);
        }
        catch { Dispose(); throw; }

        void AddCollar(Vector3 ray, Vector2 texture, float lift)
        {
            if (!owner.Raycast(ray, out var hit)) throw new InvalidOperationException("fracture collar extends beyond outer covering");
            vertices.Add(owner.FromBone.MultiplyPoint3x4(hit.Point + hit.Normal * (lift / owner.Scale)));
            normals.Add(owner.ToBone.transpose.MultiplyVector(hit.Normal).normalized);
            uv.Add(texture); colors.Add(new Color(0.92f, 0f, 0.7f, 0.85f)); weights.Add(hit.Weight);
        }
        void Triangle(int a, int b, int c)
        {
            Vector3 facing = owner.ToBone.transpose.MultiplyVector(direction);
            if (Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), facing) < 0f)
            { int temporary = b; b = c; c = temporary; }
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }
    }

    internal bool TryGetWound(out Transform anchor, out Vector3 point, out Vector3 direction)
    {
        anchor = Owner.Bones[Owner.RootIndex]; point = direction = default;
        if (Renderer == null || !Renderer.enabled || anchor == null) return false;
        point = anchor.InverseTransformPoint(_woundWorld);
        direction = Owner.Direction;
        return true;
    }

    internal void Show(bool show)
    {
        if (Renderer == null) return;
        var source = Owner.Renderer;
        bool enabled = show && Visible(source) && source.sharedMesh == Owner.Original;
        if (Renderer.enabled != enabled) Renderer.enabled = enabled;
        if (enabled)
            for (int i = 0; i < _anchors.Length; i++)
            {
                if (_anchors[i] == null || !_poses[i].TryPose(out Vector3 point)) { Renderer.enabled = false; break; }
                _anchors[i].position = point;
                if (i == 0) _woundWorld = point;
            }
        if (source == null) return;
        int layer = source.gameObject.layer;
        if (Renderer.gameObject.layer != layer) Renderer.gameObject.layer = layer;
        var shadows = source.shadowCastingMode;
        if (Renderer.shadowCastingMode != shadows) Renderer.shadowCastingMode = shadows;
        bool receive = source.receiveShadows;
        if (Renderer.receiveShadows != receive) Renderer.receiveShadows = receive;
    }
    internal static bool Visible(SkinnedMeshRenderer renderer) => renderer != null && renderer.enabled &&
        !renderer.forceRenderingOff && renderer.gameObject.activeInHierarchy && renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly;

    private void ChangeLodMembership(bool add)
    {
        if (_group == null || Renderer == null) return;
        LOD[] lods = _group.GetLODs(); bool changed = false;
        for (int i = 0; i < lods.Length; i++)
        {
            var renderers = new List<Renderer>(lods[i].renderers ?? Array.Empty<Renderer>());
            bool removed = renderers.Remove(Renderer);
            if (add && renderers.Contains(Owner.Renderer)) { renderers.Add(Renderer); changed = true; }
            if (removed) changed = true;
            lods[i].renderers = renderers.ToArray();
        }
        // Keep EFT's reference point/size and thresholds unchanged.
        if (changed) _group.SetLODs(lods);
    }
    public void Dispose()
    {
        if (Renderer != null) { Renderer.enabled = false; ChangeLodMembership(false); UnityEngine.Object.Destroy(Renderer.gameObject); }
        if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
        if (_anchors != null) foreach (var anchor in _anchors) if (anchor != null) UnityEngine.Object.Destroy(anchor.gameObject);
        _anchors = null;
        Renderer = null; _mesh = null;
    }
}
}
