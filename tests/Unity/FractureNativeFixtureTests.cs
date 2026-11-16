#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Rupture.ClothingHarness;
using Rupture.Dismemberment.Classes.Fractures;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;
using Debug = UnityEngine.Debug;

internal static class FractureNativeFixtureTests
{
    internal static void Run()
    {
        string directory = Environment.GetEnvironmentVariable("RPT_FRACTURE_FIXTURES");
        string output = Directory.GetParent(Application.dataPath).FullName;
        if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, "catalog.json")))
        {
            File.WriteAllText(Path.Combine(output, "native-fixtures.json"), "{\"skipped\":true,\"reason\":\"No optional native clothing fixtures supplied.\"}");
            Debug.Log("SKIP: optional native clothing sample; run Setup-ClothingHarness -ExtractFixtures -AllLods to provide fixtures.");
            return;
        }
        var catalog = JsonConvert.DeserializeObject<ClothingCatalog>(File.ReadAllText(Path.Combine(directory, "catalog.json")));
        var results = new List<object>(); int accepted = 0, rejected = 0, unreadable = 0;
        // Per accepted case: snapshot (charged to the first case of its mesh), surface, visual construction, Show(true).
        var timings = new List<double[]>();
        // Small source-backed experiment: eight garments of each anatomy, both sides,
        // both segment choices, all their source LODs. Keep rejected cases explicit.
        foreach (var garment in catalog.Garments.Where(g => string.IsNullOrEmpty(g.Error)).GroupBy(g => g.Part).SelectMany(g => g.Take(8)))
        {
            bool leg = garment.Part == "Feet";
            var fixture = FixtureIO.Read(directory, garment);
            foreach (var rendererFixture in fixture.Renderers)
            {
                if (!rendererFixture.Readable) { unreadable++; continue; }
                var host = new GameObject("native fracture fixture");
                Mesh mesh = rendererFixture.Mesh.Create();
                try
                {
                    var paths = new Dictionary<string, Transform>();
                    foreach (string path in rendererFixture.BonePaths.OrderBy(p => p.Length))
                    {
                        string current = ""; Transform parent = host.transform;
                        foreach (string part in path.Split('/'))
                        {
                            current = current.Length == 0 ? part : current + "/" + part;
                            if (!paths.TryGetValue(current, out Transform transform))
                            { transform = new GameObject(part).transform; transform.SetParent(parent, false); paths.Add(current, transform); }
                            parent = transform;
                        }
                    }
                    Transform[] bones = rendererFixture.BonePaths.Select(p => paths[p]).ToArray();
                    Matrix4x4[] binds = mesh.bindposes;
                    foreach (int index in Enumerable.Range(0, bones.Length).OrderBy(i => rendererFixture.BonePaths[i].Length))
                    {
                        Matrix4x4 inverse = binds[index].inverse;
                        bones[index].SetPositionAndRotation(inverse.MultiplyPoint3x4(Vector3.zero), inverse.rotation);
                    }
                    var renderer = host.AddComponent<SkinnedMeshRenderer>(); renderer.sharedMesh = mesh; renderer.bones = bones;
                    renderer.sharedMaterial = LimbSeverMaterialProvider.Material;
                    long mark = Stopwatch.GetTimestamp();
                    var snapshot = new FractureSurface.Snapshot(mesh);
                    double pendingSnapshotMs = Lap(ref mark);
                    foreach (int bodyPart in leg ? new[] { 5, 6 } : new[] { 3, 4 })
                    foreach (LimbSeverLevel level in new[] { LimbSeverLevel.Proximal, LimbSeverLevel.Distal })
                    {
                        int seed = 0; FractureAppearance logical;
                        do { logical = new FractureAppearance("native-fracture-" + seed++, bodyPart); } while (logical.Level != level || logical.SiteCount != 2);
                        string side = bodyPart == 3 || bodyPart == 5 ? "L" : "R";
                        string root = "Base Human" + side + (leg ? level == LimbSeverLevel.Proximal ? "Thigh1" : "Calf" : level == LimbSeverLevel.Proximal ? "Upperarm" : "Forearm1");
                        string end = "Base Human" + side + (leg ? level == LimbSeverLevel.Proximal ? "Calf" : "Foot" : level == LimbSeverLevel.Proximal ? "Forearm1" : "Palm");
                        for (int site = 0; site < logical.SiteCount; site++)
                        {
                        var appearance = logical.ForSite(site);
                        string failure = null;
                        double surfaceMs = 0, visualMs = 0, showMs = 0;
                        try
                        {
                            mark = Stopwatch.GetTimestamp();
                            var source = new FractureSurface(renderer, snapshot, root, end, appearance, garment.Id);
                            surfaceMs = Lap(ref mark);
                            using (var visual = new FractureVisual(source, appearance, LimbSeverMaterialProvider.Material))
                            {
                                visualMs = Lap(ref mark);
                                if (visual.BoneCount != appearance.BoneCount) throw new Exception("wrong native fixture bone count");
                                visual.Show(true);
                                showMs = Lap(ref mark);
                            }
                            accepted++;
                        }
                        catch (Exception exception) { rejected++; failure = exception.Message; }
                        double snapshotMs = failure == null ? pendingSnapshotMs : 0;
                        if (failure == null) pendingSnapshotMs = 0;
                        results.Add(new { garment.Id, rendererFixture.Renderer, rendererFixture.LodLevel, bodyPart, site, level = level.ToString(), failure,
                            vertices = mesh.vertexCount, snapshotMs, surfaceMs, visualMs, showMs });
                        if (failure == null) timings.Add(new[] { snapshotMs, surfaceMs, visualMs, showMs });
                        }
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }
        // The first accepted case pays every one-time cost; the median of the rest is the steady per-fracture cost.
        double[] first = timings.Count > 0 ? timings[0] : new double[4];
        var later = new double[4];
        for (int step = 0; step < 4; step++)
        {
            var column = timings.Skip(1).Select(t => t[step]).Where(v => step != 0 || v > 0).OrderBy(v => v).ToArray();
            later[step] = column.Length > 0 ? column[column.Length / 2] : 0;
        }
        var summary = new { firstSnapshotMs = first[0], firstSurfaceMs = first[1], firstVisualMs = first[2], firstShowMs = first[3],
            medianSnapshotMs = later[0], medianSurfaceMs = later[1], medianVisualMs = later[2], medianShowMs = later[3] };
        File.WriteAllText(Path.Combine(output, "native-fixtures.json"), JsonConvert.SerializeObject(new { accepted, rejected, unreadable, summary, results }, Formatting.Indented));
        Debug.Log($"Native fracture fixture sample: accepted={accepted}, rejected={rejected}, unreadable renderers={unreadable}");
        Debug.Log("Fracture first-use timing (ms) " + JsonConvert.SerializeObject(summary));
        if (accepted == 0) throw new Exception("No native clothing fracture fixture succeeded.");
        // With the production warmup the first fracture must cost what any later one does (cold: about 8 ms more).
        double firstTotal = first.Sum(), steadyTotal = later.Sum();
        bool warmed = (Environment.GetEnvironmentVariable("RPT_FRACTURE_FIRST_USE") ?? "warm") == "warm";
        if (warmed && firstTotal > steadyTotal + 4d)
            throw new Exception($"First fracture after warmup cost {firstTotal:F2} ms against a steady {steadyTotal:F2} ms.");
    }

    private static double Lap(ref long mark)
    {
        long now = Stopwatch.GetTimestamp();
        double milliseconds = (now - mark) * 1000d / Stopwatch.Frequency;
        mark = now;
        return milliseconds;
    }
}
#endif
