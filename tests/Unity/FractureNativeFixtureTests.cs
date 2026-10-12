#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Rupture.ClothingHarness;
using Rupture.Dismemberment.Classes.Fractures;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;

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
                    var snapshot = new FractureSurface.Snapshot(mesh);
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
                        try
                        {
                            var source = new FractureSurface(renderer, snapshot, root, end, appearance, garment.Id);
                            using (var visual = new FractureVisual(source, appearance, LimbSeverMaterialProvider.Material))
                                if (visual.BoneCount != appearance.BoneCount) throw new Exception("wrong native fixture bone count");
                            accepted++;
                        }
                        catch (Exception exception) { rejected++; failure = exception.Message; }
                        results.Add(new { garment.Id, rendererFixture.Renderer, rendererFixture.LodLevel, bodyPart, site, level = level.ToString(), failure });
                        }
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(mesh); }
            }
        }
        File.WriteAllText(Path.Combine(output, "native-fixtures.json"), JsonConvert.SerializeObject(new { accepted, rejected, unreadable, results }, Formatting.Indented));
        Debug.Log($"Native fracture fixture sample: accepted={accepted}, rejected={rejected}, unreadable renderers={unreadable}");
        if (accepted == 0) throw new Exception("No native clothing fracture fixture succeeded.");
    }
}
#endif
