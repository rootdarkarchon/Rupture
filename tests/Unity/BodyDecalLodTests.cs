#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.Limbs;

public static class BodyDecalLodTests
{
    [Serializable] private sealed class Report
    {
        public int projections, missing, mismatches, legacyHiddenRejections, destroyedTargetsSkipped;
        public bool passed;
    }
    public static void RunBatch()
    {
        string output = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "DecalVerification");
        Directory.CreateDirectory(output);
        var report = new Report();
        var root = new GameObject("Body");
        var bone = new GameObject("Animated bone"); bone.transform.SetParent(root.transform, false);
        var material = new Material(Shader.Find("Hidden/Rupture_Test_Decal"));
        var skins = new SkinnedMeshRenderer[2];
        var meshes = new Mesh[2];
        var targets = new RenderTexture[2];
        var references = new RenderTexture[2];
        var buffer = new CommandBuffer();
        var pixels = new Texture2D(128, 128, TextureFormat.RGBA32, false);
        for (int lod = 0; lod < 2; lod++)
        {
            var item = new GameObject("Body LOD" + lod); item.transform.SetParent(root.transform, false);
            item.transform.localPosition = new Vector3(0.1f, 0.2f, 0);
            item.transform.localRotation = Quaternion.Euler(0, 0, 17);
            item.transform.localScale = Vector3.one * 0.9f;
            skins[lod] = item.AddComponent<SkinnedMeshRenderer>();
            meshes[lod] = Quad(lod == 1);
            meshes[lod].bindposes = new[] { item.transform.localToWorldMatrix };
            skins[lod].sharedMesh = meshes[lod]; skins[lod].sharedMaterial = material;
            skins[lod].bones = new[] { bone.transform }; skins[lod].rootBone = root.transform;
            skins[lod].updateWhenOffscreen = false;
            targets[lod] = new RenderTexture(128, 128, 0); targets[lod].Create();
            references[lod] = new RenderTexture(128, 128, 0); references[lod].Create();
        }
        try
        {
            // Each LOD stays offscreen throughout, including the first hit. The
            // native visibility predicate would reject every one of these draws.
            for (int pose = 0; pose < 4; pose++)
            {
                root.transform.SetPositionAndRotation(new Vector3(3 + pose, 2, 5), Quaternion.Euler(15 * pose, 23 * pose, 8));
                root.transform.localScale = new Vector3(1.2f, 0.8f, 1.1f);
                bone.transform.localPosition = new Vector3(pose * 0.12f, -0.07f * pose, 0);
                bone.transform.localRotation = Quaternion.Euler(0, 0, pose * 13);
                // Two cumulative hits, with opposite LOD submission orders.
                for (int hit = 0; hit < 2; hit++)
                for (int order = 0; order < 2; order++)
                {
                    int lod = hit == 0 ? order : 1 - order;
                    if (skins[lod].isVisible) throw new Exception("Fixture unexpectedly became visible.");
                    report.legacyHiddenRejections++;
                    if (!BodyDecalRendering.CanPaint(skins[lod])) throw new Exception("Hidden valid skin was rejected.");
                    Vector3 sample = hit == 0 ? new Vector3(0.3f, 0.15f, 0) : new Vector3(-0.3f, -0.15f, 0);
                    Matrix4x4 posed = bone.transform.localToWorldMatrix * meshes[lod].bindposes[0];
                    material.SetVector("_Point", posed.MultiplyPoint3x4(sample));
                    material.SetFloat("_Radius", 0.12f);
                    buffer.Clear(); buffer.SetRenderTarget(targets[lod]);
                    if (hit == 0) buffer.ClearRenderTarget(false, true, Color.clear);
                    BodyDecalRendering.DrawRenderer(buffer, skins[lod], material);
                    Graphics.ExecuteCommandBuffer(buffer);
                    buffer.Clear(); buffer.SetRenderTarget(references[lod]);
                    if (hit == 0) buffer.ClearRenderTarget(false, true, Color.clear);
                    buffer.DrawMesh(meshes[lod], posed, material, 0, -1);
                    Graphics.ExecuteCommandBuffer(buffer);
                    Color32[] actual = Read(targets[lod], pixels), expected = Read(references[lod], pixels);
                    int covered = 0, differences = 0;
                    for (int i = 0; i < actual.Length; i++)
                    {
                        if (actual[i].r > 100) covered++;
                        if (Math.Abs(actual[i].r - expected[i].r) > 2) differences++;
                    }
                    if (covered < 20) report.missing++;
                    if (differences > 2) report.mismatches++;
                    if (differences > 2) Debug.Log($"Projection pose={pose}, hit={hit}, lod={lod}, covered={covered}, differences={differences}");
                    if (pose == 3 && hit == 1)
                    {
                        File.WriteAllBytes(Path.Combine(output, "lod" + lod + "-expected.png"), pixels.EncodeToPNG());
                        Read(targets[lod], pixels);
                        File.WriteAllBytes(Path.Combine(output, "lod" + lod + "-actual.png"), pixels.EncodeToPNG());
                    }
                    report.projections++;
                }
            }
            skins[0].enabled = false;
            if (BodyDecalRendering.CanPaint(skins[0])) throw new Exception("Disabled original renderer was admitted.");
            skins[0].enabled = true; root.SetActive(false);
            if (BodyDecalRendering.CanPaint(skins[0])) throw new Exception("Inactive body was admitted.");
            root.SetActive(true);
            // Model EFT's consumption order for both cached and first-time
            // targets. A skipped draw must return so the next item is consumed.
            for (int cached = 0; cached < 2; cached++)
            {
                var deadObject = new GameObject("queued then destroyed");
                var dead = deadObject.AddComponent<SkinnedMeshRenderer>();
                dead.sharedMesh = meshes[0]; dead.sharedMaterial = material;
                if (cached != 0) { buffer.Clear(); BodyDecalRendering.DrawRenderer(buffer, dead, material); }
                var queue = new System.Collections.Generic.List<Renderer> { dead, skins[0] };
                UnityEngine.Object.DestroyImmediate(deadObject);
                int healthy = 0;
                while (queue.Count > 0)
                {
                    Renderer renderer = queue[0];
                    if (BodyDecalRendering.IsAlive(renderer)) healthy++;
                    else
                    {
                        if (BodyDecalRendering.CanPaint(renderer)) throw new Exception("Destroyed renderer admitted.");
                        buffer.Clear(); BodyDecalRendering.DrawRenderer(buffer, renderer, material);
                        report.destroyedTargetsSkipped++;
                    }
                    queue.RemoveAt(0);
                }
                if (healthy != 1) throw new Exception("Destroyed renderer blocked the next healthy decal.");
            }
            if (skins[0].updateWhenOffscreen || skins[1].updateWhenOffscreen || skins[0].isVisible || skins[1].isVisible)
                throw new Exception("Projection altered skin visibility/update policy.");
            report.passed = report.projections == 16 && report.missing == 0 && report.mismatches == 0 && report.destroyedTargetsSkipped == 2;
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonUtility.ToJson(report, true));
            if (!report.passed) throw new Exception("LOD decal projection failed: " + JsonUtility.ToJson(report));
            Debug.Log("PASS: independent posed LOD decal projections: " + JsonUtility.ToJson(report));
        }
        finally
        {
            // Edit-mode fixture: release the scratch immediately before testing
            // the runtime reset (which normally schedules destruction).
            var scratch = typeof(BodyDecalRendering).GetField("_scratch", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            UnityEngine.Object.DestroyImmediate((Mesh)scratch.GetValue(null));
            BodyDecalRendering.Reset(); buffer.Release(); RenderTexture.active = null;
            if (scratch.GetValue(null) != null) throw new Exception("Scratch mesh survived reset.");
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(pixels);
            foreach (Mesh mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            foreach (RenderTexture target in targets) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            foreach (RenderTexture target in references) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
        }
    }
    private static Color32[] Read(RenderTexture target, Texture2D pixels)
    {
        RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); pixels.Apply();
        return pixels.GetPixels32();
    }
    private static Mesh Quad(bool rotateUv)
    {
        var mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
        mesh.triangles = rotateUv ? new[] {0,1,3,1,2,3} : new[] {0,1,2,0,2,3};
        mesh.uv = rotateUv ? new[] {Vector2.right,Vector2.one,Vector2.up,Vector2.zero} :
            new[] {Vector2.zero,Vector2.right,Vector2.one,Vector2.up};
        mesh.boneWeights = new[] {Weight(),Weight(),Weight(),Weight()}; mesh.bindposes = new[] {Matrix4x4.identity};
        mesh.RecalculateBounds(); return mesh;
    }
    private static BoneWeight Weight() => new BoneWeight { boneIndex0 = 0, weight0 = 1 };
}
#endif
