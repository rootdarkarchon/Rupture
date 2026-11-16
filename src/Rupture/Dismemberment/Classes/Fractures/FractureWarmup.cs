using System;
using System.Collections.Generic;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Fractures
{

/// <summary>
/// Runs the first fracture of the process on a throwaway skinned rig far below the world, one stage per call, so the
/// player's first broken limb finds everything compiled and initialised. Pre-JIT only compiles the types it lists; the
/// work that remained inside <c>FractureSystem.Update</c> was the generic instantiations over value types
/// (<c>Dictionary&lt;ulong,…&gt;</c>, <c>HashSet&lt;ulong&gt;</c>, the LINQ in the bone weight builder), the first
/// skinned renderer, mesh, LOD group and property block built for a fracture, and the first bone geometry.
/// The rig is never enabled across a frame boundary, so nothing is rendered; it is destroyed after the last stage or
/// when a stage throws.
/// </summary>
internal static class FractureWarmup
{
    private enum Stage
    {
        Variants,
        Failures,
        Rig,
        Engine,
        Surface,
        Geometry,
        Visual,
        Present,
        Done
    }

    private const string RootName = "segment";
    private const string EndName = "end";
    private const int GridSize = 12;
    private const int SeedAttempts = 64;
    private const int RimProbes = 20;
    /// <summary>The collar rim is a unit ring scaled by at most 1 + 0.06 of irregularity.</summary>
    private const float MaximumRimScale = 1.06f;

    private static Stage _stage;
    private static bool _failed;
    private static GameObject _host;
    private static SkinnedMeshRenderer _renderer;
    private static LODGroup _group;
    private static Mesh _mesh;
    private static FractureSurface.Snapshot _snapshot;
    private static FractureSurface _surface;
    private static FractureAppearance _appearance;
    private static FractureVisual _visual;

    /// <summary>Drops a half-built rig (the raid ended between stages); the sequence restarts at the next attempt.</summary>
    internal static void Abort()
    {
        if (_stage == Stage.Done || _failed) return;
        Release();
        _stage = Stage.Variants;
    }

    /// <summary>True once every stage ran; compiled code and engine state stay for the process, so it is never repeated.</summary>
    internal static bool Warmed => _stage == Stage.Done;

    /// <summary>True after a stage threw; the failure is deterministic, so the warmup is not attempted again this process.</summary>
    internal static bool Failed => _failed;

    /// <summary>
    /// Runs the next stage with the live sever material. Returns true when the warmup is complete, or when an earlier
    /// attempt already failed. A stage that throws releases the rig, marks the warmup failed for the process and
    /// rethrows; it is never attempted again.
    /// </summary>
    internal static bool Step(Material material)
    {
        if (_stage == Stage.Done || _failed) return true;
        try
        {
            switch (_stage)
            {
            case Stage.Variants: WarmVariants(); break;
            case Stage.Failures: WarmFailures(); break;
            case Stage.Rig: BuildRig(material); break;
            case Stage.Engine: WarmEngine(); break;
            case Stage.Surface: BuildSurface(); break;
            case Stage.Geometry: BuildGeometry(); break;
            case Stage.Visual: BuildVisual(material); break;
            default: PresentVisual(); break;
            }
        }
        catch
        {
            // The rig is fixed, so a stage that fails once fails identically in every raid: mark it for the process.
            Release();
            _failed = true;
            throw;
        }
        _stage++;
        if (_stage == Stage.Done) Release();
        return _stage == Stage.Done;
    }

    /// <summary>The per-representation variant table of <c>FractureSystem</c>, closed over the same value-type key.</summary>
    private static void WarmVariants()
    {
        var variants = new Dictionary<ulong, FractureVisual>();
        ulong mask = 1UL << 3;
        variants.TryGetValue(mask, out FractureVisual _);
        variants.ContainsKey(mask);
        variants.Add(mask, null);
        foreach (KeyValuePair<ulong, FractureVisual> entry in variants) mask ^= entry.Key;
        foreach (FractureVisual visual in variants.Values) mask ^= visual == null ? 1UL : 2UL;
        variants.Remove(mask);
        variants.Clear();
    }

    /// <summary>The failed-variant set and the hierarchy key of the first candidate a slot builds.</summary>
    private static void WarmFailures()
    {
        var failed = new HashSet<ulong>();
        failed.Contains(8UL);
        failed.Add(8UL);
        failed.Clear();
        var anchor = new GameObject("RPT fracture warmup key") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            FractureSystem.HierarchyKey(anchor.transform);
        }
        finally
        {
            UnityEngine.Object.Destroy(anchor);
        }
    }

    private static void BuildRig(Material material)
    {
        _host = new GameObject("RPT fracture warmup") { hideFlags = HideFlags.HideAndDontSave };
        // Far below any map so no camera, shadow cascade or probe ever sees it.
        _host.transform.position = new Vector3(0f, -10000f, 0f);
        Transform root = Child(_host.transform, RootName);
        Transform end = Child(root, EndName);
        end.localPosition = new Vector3(0f, 0.4f, 0f);
        Transform twist = Child(root, "twist");
        GameObject surface = new GameObject("RPT fracture warmup surface") { hideFlags = HideFlags.HideAndDontSave };
        surface.transform.SetParent(_host.transform, false);
        _renderer = surface.AddComponent<SkinnedMeshRenderer>();
        _renderer.enabled = false;
        Transform[] bones = { root, end, twist };
        _renderer.bones = bones;
        _renderer.rootBone = root;
        _mesh = BuildCovering(surface.transform, bones);
        _renderer.sharedMesh = _mesh;
        _renderer.sharedMaterial = material;
        _renderer.localBounds = new Bounds(new Vector3(0f, 0.2f, 0f), Vector3.one);
        _group = _host.AddComponent<LODGroup>();
        _group.SetLODs(new[] { new LOD(0.01f, new Renderer[] { _renderer }) });
    }

    /// <summary>
    /// First use of the mesh, property block and LOD calls a visual makes, on a throwaway mesh and the rig, so the visual
    /// stage only pays its own geometry. Mirrors <c>FractureVisual</c>'s mesh upload and LOD membership change.
    /// </summary>
    private static void WarmEngine()
    {
        var vertices = new List<Vector3> { Vector3.zero, Vector3.right, Vector3.up };
        var normals = new List<Vector3> { Vector3.forward, Vector3.forward, Vector3.forward };
        var uv = new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up };
        var colors = new List<Color> { Color.white, Color.white, Color.white };
        var weights = new List<BoneWeight> { default, default, default };
        var triangles = new List<int> { 0, 1, 2 };
        var probe = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            probe.SetVertices(vertices);
            probe.SetNormals(normals);
            probe.SetUVs(0, uv);
            probe.SetColors(colors);
            probe.boneWeights = weights.ToArray();
            probe.bindposes = new[] { Matrix4x4.identity };
            probe.SetTriangles(triangles, 0);
            probe.RecalculateBounds();
        }
        finally
        {
            UnityEngine.Object.Destroy(probe);
        }
        LimbSeverMaterialProvider.ApplyVariation(_renderer, 0, 1u, false);
        LOD[] lods = _group.GetLODs();
        var renderers = new List<Renderer>(lods[0].renderers);
        renderers.Remove(_renderer);
        renderers.Contains(_renderer);
        renderers.Add(_renderer);
        lods[0].renderers = renderers.ToArray();
        _group.SetLODs(lods);
    }

    private static void BuildSurface()
    {
        _snapshot = new FractureSurface.Snapshot(_mesh);
        _surface = SelectSurface();
    }

    /// <summary>
    /// First seed whose appearance has two sites and two bones and whose collar rim and bone roots all land on the
    /// covering. <c>FractureVisual</c> throws when a collar ray misses, and the rig is fixed, so a seed that misses
    /// would fail identically in every process.
    /// </summary>
    private static FractureSurface SelectSurface()
    {
        for (int seed = 0; seed < SeedAttempts; seed++)
        {
            var candidate = new FractureAppearance("rupture-warmup-" + seed, 3);
            if (candidate.BoneCount != 2 || candidate.SiteCount != 2) continue;
            candidate = candidate.ForSite(0);
            var surface = new FractureSurface(_renderer, _snapshot, RootName, EndName, candidate, "warmup");
            if (!CollarFits(surface, candidate)) continue;
            _appearance = candidate;
            return surface;
        }
        throw new InvalidOperationException($"no fracture warmup seed of {SeedAttempts} fits the covering");
    }

    /// <summary>
    /// Mirrors the extents <c>FractureVisual</c> casts its collar and bone-root rays from: the centre, the two bone
    /// roots and the widest the irregular outer rim can reach.
    /// </summary>
    private static bool CollarFits(FractureSurface surface, FractureAppearance appearance)
    {
        Vector2 primary = ProceduralLimbWoundGeometry.ResolveBoneRadiiWorld(appearance.IsLeg, appearance.Level, 0);
        Vector2 secondary = ProceduralLimbWoundGeometry.ResolveBoneRadiiWorld(appearance.IsLeg, appearance.Level, 1);
        float separation = (primary.x + secondary.x) * 1.2f / surface.Scale;
        float halfWidth = (separation * 0.5f + Mathf.Max(primary.x, secondary.x) * 1.15f / surface.Scale) + 0.006f / surface.Scale;
        float halfHeight = Mathf.Max(primary.y, secondary.y) * 1.15f / surface.Scale + 0.006f / surface.Scale;
        Vector3 up = Vector3.Cross(surface.Direction, surface.Tangent).normalized;
        if (!surface.Raycast(surface.Origin, out _)) return false;
        if (!surface.Raycast(surface.Origin - surface.Tangent * (0.5f * separation), out _)) return false;
        if (!surface.Raycast(surface.Origin + surface.Tangent * (0.5f * separation), out _)) return false;
        for (int i = 0; i < RimProbes; i++)
        {
            float angle = i * Mathf.PI * 2f / RimProbes;
            Vector3 ray = surface.Origin + surface.Tangent * (Mathf.Cos(angle) * halfWidth * MaximumRimScale)
                + up * (Mathf.Sin(angle) * halfHeight * MaximumRimScale);
            if (!surface.Raycast(ray, out _)) return false;
        }
        return true;
    }

    /// <summary>The bone primitive alone, so its compilation and LINQ instantiations do not share a frame with the visual.</summary>
    private static void BuildGeometry()
    {
        for (int bone = 0; bone < _appearance.BoneCount; bone++)
            ProceduralLimbWoundGeometry.GenerateFractureBone(_appearance.IsLeg, _appearance.Level, bone,
                _appearance.BoneSeed(bone), 1f, Vector3.zero, Vector3.forward, Vector3.right, 3 + bone);
    }

    private static void BuildVisual(Material material)
    {
        _visual = new FractureVisual(_surface, _appearance, material);
    }

    /// <summary>
    /// The renderer is enabled only inside this call, and the rig is destroyed with it: no frame draws the rig.
    /// </summary>
    private static void PresentVisual()
    {
        _renderer.enabled = true;
        _visual.Show(true);
        _visual.TryGetWound(out _, out _, out _);
        _visual.Show(false);
    }

    /// <summary>A flat sleeve-like grid in front of the segment bone, the covering the fixture regressions fracture.</summary>
    private static Mesh BuildCovering(Transform surface, Transform[] bones)
    {
        const int count = (GridSize + 1) * (GridSize + 1);
        var vertices = new Vector3[count];
        var normals = new Vector3[count];
        var weights = new BoneWeight[count];
        var triangles = new int[GridSize * GridSize * 6];
        int triangle = 0;
        for (int y = 0; y <= GridSize; y++)
        {
            for (int x = 0; x <= GridSize; x++)
            {
                int index = y * (GridSize + 1) + x;
                vertices[index] = new Vector3((x / (float)GridSize - 0.5f) * 0.35f, y / (float)GridSize * 0.55f - 0.05f, 0.06f);
                normals[index] = Vector3.forward;
                weights[index] = new BoneWeight { boneIndex0 = 0, weight0 = 0.65f, boneIndex1 = 2, weight1 = 0.35f };
                if (x == GridSize || y == GridSize) continue;
                triangles[triangle++] = index;
                triangles[triangle++] = index + 1;
                triangles[triangle++] = index + GridSize + 2;
                triangles[triangle++] = index;
                triangles[triangle++] = index + GridSize + 2;
                triangles[triangle++] = index + GridSize + 1;
            }
        }
        var binds = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++) binds[i] = bones[i].worldToLocalMatrix * surface.localToWorldMatrix;
        return new Mesh
        {
            name = "RPT fracture warmup covering",
            hideFlags = HideFlags.HideAndDontSave,
            vertices = vertices,
            normals = normals,
            boneWeights = weights,
            bindposes = binds,
            triangles = triangles
        };
    }

    private static Transform Child(Transform parent, string name)
    {
        Transform child = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave }.transform;
        child.SetParent(parent, false);
        return child;
    }

    private static void Release()
    {
        _visual?.Dispose();
        if (_host != null) UnityEngine.Object.Destroy(_host);
        if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
        _visual = null;
        _host = null;
        _renderer = null;
        _group = null;
        _mesh = null;
        _snapshot = null;
        _surface = null;
    }
}
}
