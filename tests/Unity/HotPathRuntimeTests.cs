#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Rupture.Casings;
using Rupture.PhysXBackend;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Ragdolls.External;
using UnityEngine;
using MeshData = DeferredDecals.DeferredDecalRenderer.ManagedMesh;

public static class HotPathRuntimeTests
{
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; Debug.Log("PASS: " + message); }
    public static void RunBatch()
    {
        VerifyHierarchy(); VerifyTransformProbe(); VerifyQueries(); VerifyDecalCapacity(); VerifyDecalBatch();
        _checks += PhysicsIdleRuntimeTests.Run();
        _checks += HeadgearPhysicsRuntimeTests.Run();
        Rupture.Ragdolls.Classes.ShadowRagdollWorld.VerifyWeaponRelease();
        Check(true, "native loot, destroyed/replaced bodies, pooled transforms and repeated release");
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json"),
            "{\"passed\":true,\"checks\":" + _checks + "}");
    }
    private static void VerifyTransformProbe()
    {
        var parent = new GameObject("environment parent"); var child = new GameObject("environment root");
        child.transform.SetParent(parent.transform, false);
        var probe = new EnvironmentTransformProbe();
        try
        {
            Check(probe.Changed(child.transform) && !probe.Changed(child.transform), "stable environment root skips decomposition");
            parent.transform.position = Vector3.one;
            child.transform.hasChanged = false;
            Check(probe.Changed(child.transform), "ancestor motion survives another owner's hasChanged reset");
            parent.transform.localScale = new Vector3(2, 3, 4);
            Check(probe.Changed(child.transform), "ancestor scale is detected");
            child.transform.localRotation = Quaternion.Euler(10, 20, 30);
            Check(probe.Changed(child.transform) && child.transform.hasChanged, "rotation is detected without consuming shared change flag");
            const int count = 121, frames = 3000;
            var roots = new Transform[count]; var probes = new EnvironmentTransformProbe[count];
            for (int i = 0; i < count; i++) { var go = new GameObject("mirrored root"); go.transform.SetParent(parent.transform, false); roots[i] = go.transform; probes[i].Changed(roots[i]); }
            double baseline = 0, optimized = 0; float sink = 0;
            // Alternate trials; include the same destruction check in both paths.
            for (int trial = 0; trial < 6; trial++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < frames; f++) for (int i = 0; i < count; i++)
                    if (roots[i] != null) { var p = roots[i].position; var r = roots[i].rotation; var s = roots[i].lossyScale; sink += p.x + r.w + s.y; }
                baseline += timer.Elapsed.TotalMilliseconds / frames;
                timer.Restart();
                for (int f = 0; f < frames; f++) for (int i = 0; i < count; i++)
                    if (roots[i] != null && probes[i].Changed(roots[i])) { roots[i].GetPositionAndRotation(out var p, out var r); sink += p.x + r.w + roots[i].lossyScale.y; }
                optimized += timer.Elapsed.TotalMilliseconds / frames;
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 100; f++) for (int i = 0; i < count; i++) probes[i].Changed(roots[i]);
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "steady environment probe allocates nothing");
            File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "environment-benchmark.json"),
                "{\"actors\":" + count + ",\"baselineMs\":" + (baseline/6).ToString("R",System.Globalization.CultureInfo.InvariantCulture) +
                ",\"optimizedMs\":" + (optimized/6).ToString("R",System.Globalization.CultureInfo.InvariantCulture) + "}");
        }
        finally { UnityEngine.Object.DestroyImmediate(parent); }
    }
    private static void VerifyHierarchy()
    {
        var filter = new EnvironmentHierarchyFilter();
        var root = new GameObject("root"); var a = new GameObject("a"); var b = new GameObject("b");
        a.transform.parent = root.transform; b.transform.parent = root.transform;
        var excluded = new GameObject("player"); excluded.AddComponent<EFT.Player>();
        try
        {
            filter.BeginQuery(); Check(filter.Allows(a.transform) && filter.Allows(b.transform) && filter.Hits > 0, "shared ancestry is classified once per query"); filter.EndQuery();
            a.transform.parent = excluded.transform;
            filter.BeginQuery(); Check(!filter.Allows(a.transform) && filter.Allows(b.transform), "reparented player hierarchy is excluded on next query"); filter.EndQuery();
            var corpse = root.AddComponent<EFT.Interactive.Corpse>();
            filter.BeginQuery(); Check(!filter.Allows(b.transform), "new corpse component invalidates next query"); filter.EndQuery();
            UnityEngine.Object.DestroyImmediate(corpse);
            filter.BeginQuery(); Check(filter.Allows(b.transform), "removed exclusion is observed on next query"); filter.EndQuery();
            var particle = root.AddComponent<ParticleSystem>();
            filter.BeginQuery(); Check(!filter.Allows(b.transform), "particle hierarchy remains excluded"); filter.EndQuery();
            UnityEngine.Object.DestroyImmediate(particle);
            filter.BeginQuery(); filter.Allows(b.transform); filter.EndQuery();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) { filter.BeginQuery(); filter.Allows(b.transform); filter.EndQuery(); }
            Check(GC.GetAllocatedBytesForCurrentThread() == before, "warmed hierarchy classification allocates no component arrays");
        }
        finally { filter.EndQuery(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(excluded); }
    }
    private static void VerifyQueries()
    {
        var random = new System.Random(7143);
        Vector3 Point() => new Vector3((float)random.NextDouble() * 16 - 8, (float)random.NextDouble() * 16 - 8, (float)random.NextDouble() * 16 - 8);
        int bounded = 0;
        for (int sample = 0; sample < 200; sample++)
        {
            var impact = new CasingImpact { Start = Point(), End = Point(), Kind = (uint)(sample % 3), Radius = (float)random.NextDouble() * 3 };
            if (sample % 7 == 0) impact.End = impact.Start;
            var query = new CasingImpactQuery(impact);
            bool box = query.TryCellBounds(4096, out Vector3Int min, out Vector3Int max); if (box) bounded++;
            for (int x = -12; x <= 12; x++) for (int y = -12; y <= 12; y++) for (int z = -12; z <= 12; z++)
            {
                Vector3 position = new Vector3(x + .5f, y + .5f, z + .5f);
                Vector3 segment = impact.End - impact.Start;
                float t = segment.sqrMagnitude < 1e-10f ? 0 : Mathf.Clamp01(Vector3.Dot(position - impact.Start, segment) / segment.sqrMagnitude);
                bool expected = impact.Kind == 0 ? (position - (impact.Start + segment * t)).sqrMagnitude < 1f :
                    (position - impact.Start).sqrMagnitude <= (impact.Radius + 1) * (impact.Radius + 1);
                if (expected != query.MayReach(position, 1)) throw new Exception("Impact reach changed");
                if (expected && box && (x < min.x || x > max.x || y < min.y || y > max.y || z < min.z || z > max.z))
                    throw new Exception("Spatial query dropped an occupied cell");
            }
        }
        Check(bounded > 0, "bounded impact lookup preserves exhaustive segment and sphere candidate sets");
        Check(!new CasingImpactQuery(new CasingImpact { Kind = 1, Radius = 200 }).TryCellBounds(4096, out _, out _), "large explosions retain bounded occupied-cell scan");
        Check(!new CasingImpactQuery(new CasingImpact { Start = Vector3.one * 1e30f }).TryCellBounds(4096, out _, out _), "extreme coordinates avoid integer traversal overflow");
    }
    private static void VerifyDecalCapacity()
    {
        new Rupture.Dismemberment.Patches.DecalMeshIndexFormatPatch().Enable();
        foreach (int capacity in new[] { 128, 2048, 2730, 2731, 4096 })
        using (var data = new MeshData(capacity))
        {
            int[] indices = data.Mesh.triangles;
            Check(data.Mesh.indexFormat == (capacity > 2730 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16), "index format matches capacity " + capacity);
            for (int i = 0; i < indices.Length; i++)
            {
                int face = i / 6, v = face * 4;
                int[] corner = { 3, 0, 1, 3, 1, 2 };
                if (indices[i] != v + corner[i % 6]) throw new Exception("Native decal indices wrapped at capacity " + capacity);
            }
            BloodDecalBatch.Install();
            try
            {
                using (BloodDecalBatch.Begin())
                {
                    data.PasteProjectorIntoMiddle((capacity - 1) * 24, 50);
                    data.PasteProjectorIntoMiddle(0, 10);
                }
                Check(Exact(data) && data.Mesh.triangles[indices.Length - 1] == capacity * 24 - 2,
                    "partial last-slot and wrapped first-slot updates preserve complete projector geometry " + capacity);
            }
            finally { BloodDecalBatch.Shutdown(); }
        }
    }
    private static void VerifyDecalBatch()
    {
        BloodDecalBatch.Install();
        var data = new MeshData(); var second = new MeshData(); MeshData constructed = null;
        try
        {
            data.PasteProjectorIntoMiddle(0, 1);
            Check(data.Uploads == 2 && data.Mesh.vertices[0].x == 1, "native emissions outside the batch upload immediately");
            using (BloodDecalBatch.Begin())
            {
                data.PasteProjectorIntoMiddle(0, 2); data.PasteProjectorIntoMiddle(24, 3);
                using (BloodDecalBatch.Begin()) { second.PasteProjectorIntoMiddle(0, 4); }
                constructed = new MeshData();
                Check(constructed.Uploads == 1, "native mesh initialization is never deferred");
                Check(data.Uploads == 2 && second.Uploads == 1, "nested batch defers only projector uploads");
            }
            Check(data.Uploads == 2 && second.Uploads == 1 && Exact(data) && Exact(second),
                "partial uploads retain all channels without invoking the full native setters");
            try { using (BloodDecalBatch.Begin()) { data.PasteProjectorIntoMiddle(48, 5); throw new InvalidOperationException("fixture"); } }
            catch (InvalidOperationException) { }
            Check(data.Uploads == 2 && Exact(data) && data.Mesh.vertices[48].x == 5, "exception exit flushes pending partial data");
            data.PasteProjectorIntoMiddle(72, 6);
            Check(data.Uploads == 3, "exception exit restores immediate uploads");
            using (BloodDecalBatch.Begin())
            {
                data.PasteProjectorIntoMiddle(data.Vertices.Length - 24, 8);
                data.PasteProjectorIntoMiddle(0, 9); data.PasteProjectorIntoMiddle(0, 10);
                data.PasteProjectorIntoMiddle(96, 11);
            }
            Check(data.Uploads == 3 && Exact(data) && data.Mesh.vertices[0].x == 10, "wrapped/disjoint ranges preserve untouched data and repeated-slot last-write order");
            data.Mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            using (BloodDecalBatch.Begin()) data.PasteProjectorIntoMiddle(120, 25000);
            Check(data.Mesh.bounds.Contains(data.Vertices[120]) && data.Mesh.bounds.Contains(Vector3.zero), "partial writes expand bounds for newly distant decals");
            using (BloodDecalBatch.Begin()) for (int i = 0; i < 40; i++) data.PasteProjectorIntoMiddle(i * 24, i + 20);
            Check(data.Uploads == 4 && Exact(data), "large dirty coverage falls back to native full upload");
            using (BloodDecalBatch.Begin()) for (int i = 0; i < 33; i++) data.PasteProjectorIntoMiddle(i * 48, i + 30);
            Check(data.Uploads == 5 && Exact(data), "fragmented dirty ranges stay bounded and fall back safely");
            data.Mesh.colors = new Color[data.Vertices.Length];
            using (BloodDecalBatch.Begin()) data.PasteProjectorIntoMiddle(0, 12);
            Check(data.Uploads == 6 && Exact(data), "unexpected extra vertex channel triggers native layout fallback");
            using (BloodDecalBatch.Begin()) { data.PasteProjectorIntoMiddle(24, 13); data.Mesh.Clear(false); }
            Check(data.Uploads == 7 && Exact(data), "externally cleared mesh is repaired with the complete native data");
            for (int i = 0; i < 5; i++) using (BloodDecalBatch.Begin()) data.PasteProjectorIntoMiddle(0, i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) using (BloodDecalBatch.Begin()) data.PasteProjectorIntoMiddle((i % 128) * 24, i);
            Check(GC.GetAllocatedBytesForCurrentThread() == before && data.Uploads == 7 && Exact(data), "warmed partial mesh updates allocate no managed memory and revalidate restored layouts");
            BloodDecalBatch.Shutdown(); data.PasteProjectorIntoMiddle(0, 7);
            Check(data.Uploads == 8, "shutdown restores native upload call");
        }
        finally { BloodDecalBatch.Shutdown(); data.Dispose(); second.Dispose(); constructed?.Dispose(); }
    }
    private static bool Exact(MeshData data)
    {
        var p = data.Mesh.vertices; var n = data.Mesh.normals; var t = data.Mesh.tangents;
        var u = new List<Vector4>(); var v = new List<Vector4>(); var w = new List<Vector4>();
        data.Mesh.GetUVs(0, u); data.Mesh.GetUVs(1, v); data.Mesh.GetUVs(2, w);
        if (p.Length != data.Vertices.Length || n.Length != p.Length || t.Length != p.Length || u.Count != p.Length || v.Count != p.Length || w.Count != p.Length) return false;
        for (int i = 0; i < p.Length; i++)
            if (p[i] != data.Vertices[i] || n[i] != data.Normals[i] || t[i] != data.Tangents[i] || u[i] != data.Uvs0[i] || v[i] != data.Uvs1[i] || w[i] != data.Uvs2[i]) return false;
        return true;
    }
}
namespace Rupture { internal static class RupturePlugin { internal static readonly FixtureLog Log = new FixtureLog(); } internal sealed class FixtureLog { internal void LogWarning(string text) { Debug.LogWarning(text); } } }
namespace EFT { public sealed class Player : MonoBehaviour { } }
namespace EFT.Interactive { public sealed class Corpse : MonoBehaviour { } }
// Boundary fixture for the exact native insertion->upload call. Uses a real Unity
// mesh and real installed Harmony; the installed target is checked separately by Cecil.
namespace DeferredDecals
{
    public class DeferredDecalRenderer
    {
        public sealed class ManagedMesh : IDisposable
        {
            public readonly Mesh Mesh = new Mesh(); public readonly Vector3[] Vertices, Normals;
            public readonly Vector4[] Tangents;
            public readonly List<Vector4> Uvs0, Uvs1, Uvs2;
            public int Uploads;
            [MethodImpl(MethodImplOptions.NoInlining)]
            public ManagedMesh(int nDecals = 128) {
                Vertices = new Vector3[nDecals * 24]; Normals = new Vector3[nDecals * 24]; Tangents = new Vector4[nDecals * 24];
                Uvs0 = new List<Vector4>(new Vector4[nDecals * 24]); Uvs1 = new List<Vector4>(new Vector4[nDecals * 24]); Uvs2 = new List<Vector4>(new Vector4[nDecals * 24]);
                Mesh.MarkDynamic(); UpdateInnerMesh();
                var indices = new int[nDecals * 36];
                for (int i = 0; i < nDecals; i++) for (int j = 0; j < 6; j++) {
                    int o = i * 36 + j * 6, v = i * 24 + j * 4;
                    indices[o] = v + 3; indices[o+1] = v; indices[o+2] = v+1;
                    indices[o+3] = v+3; indices[o+4] = v+1; indices[o+5] = v+2;
                }
                Mesh.triangles = indices; Mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000);
            }
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void PasteProjectorIntoMiddle(int index, float value)
            {
                for (int i = index; i < index + 24; i++) {
                    Vertices[i] = new Vector3(value, i, 1); Normals[i] = new Vector3(0, value, i);
                    Tangents[i] = new Vector4(value, 2, 3, i); Uvs0[i] = new Vector4(i, value, 5, 6);
                    Uvs1[i] = new Vector4(7, i, value, 8); Uvs2[i] = new Vector4(9, 10, i, value);
                }
                UpdateInnerMesh();
            }
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void UpdateInnerMesh() { Mesh.vertices = Vertices; Mesh.normals = Normals; Mesh.tangents = Tangents; Mesh.SetUVs(0, Uvs0); Mesh.SetUVs(1, Uvs1); Mesh.SetUVs(2, Uvs2); Uploads++; }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(Mesh); }
        }
    }
}
#endif
