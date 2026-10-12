#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Rupture.Dismemberment.Classes.HeadWounds;

public static class HeadWoundCullingTests
{
    [Serializable] private sealed class Report
    {
        public string unityVersion;
        public int cases, oldSkinMissingWithTunnelVisible, fixedSkinMissing, boundsFailures, pixelParityFailures;
        public bool passed;
    }

    public static void RunBatch()
    {
        var report = new Report { unityVersion = Application.unityVersion };
        string output = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "CullingVerification");
        Directory.CreateDirectory(output);
        var root = new GameObject("Corpse skeleton root");
        var head = new GameObject("Head bone"); head.transform.SetParent(root.transform, false);
        var replacement = new GameObject("Replacement head"); replacement.transform.SetParent(head.transform, false);
        var skin = replacement.AddComponent<SkinnedMeshRenderer>();
        skin.bones = new[] { head.transform }; skin.updateWhenOffscreen = false;
        skin.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
        var oldObject = new GameObject("Former replacement head"); oldObject.transform.SetParent(head.transform, false);
        var oldSkin = oldObject.AddComponent<SkinnedMeshRenderer>();
        oldSkin.bones = skin.bones; oldSkin.updateWhenOffscreen = false; oldSkin.rootBone = root.transform;
        oldSkin.sharedMaterial = skin.sharedMaterial;
        var referenceObject = new GameObject("Reference head-local surface"); referenceObject.transform.SetParent(head.transform, false);
        var reference = referenceObject.AddComponent<MeshRenderer>(); reference.sharedMaterial = skin.sharedMaterial;
        var referenceFilter = referenceObject.AddComponent<MeshFilter>();
        var tunnelObject = new GameObject("Separate tunnel renderer"); tunnelObject.transform.SetParent(head.transform, false);
        var tunnel = tunnelObject.AddComponent<MeshRenderer>();
        tunnel.sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.green };
        var tunnelMesh = Ring(0.025f, 0.025f, 0f);
        tunnelObject.AddComponent<MeshFilter>().sharedMesh = tunnelMesh;
        var cameraObject = new GameObject("Scope camera");
        var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.01f; camera.farClipPlane = 250f;
        camera.useOcclusionCulling = false;
        var target = new RenderTexture(256, 256, 24); target.Create(); camera.targetTexture = target;
        var pixels = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        var meshes = new[] { Ring(0.12f, 0.16f, 0.04f), Ring(0.115f, 0.15f, 0.055f) };
        try
        {
            foreach (Mesh mesh in meshes)
            foreach (float pose in new[] { 0f, 65f, 90f })
            foreach (float distance in new[] { 4f, 16f, 48f })
            foreach (float fov in new[] { 40f, 10f, 3f })
            foreach (float yaw in new[] { -0.15f, 0f, 0.15f })
            {
                root.transform.SetPositionAndRotation(new Vector3(10f + pose * 0.01f, 0.3f, 20f), Quaternion.Euler(0f, pose, pose * 0.5f));
                head.transform.localPosition = new Vector3(0f, 1.5f, 0f);
                head.transform.localRotation = Quaternion.Euler(pose * 0.5f, 0f, pose);
                camera.transform.position = head.transform.position - head.transform.forward * distance;
                camera.transform.rotation = Quaternion.LookRotation(head.transform.forward, head.transform.up) * Quaternion.Euler(0f, yaw, 0f);
                camera.fieldOfView = fov;
                referenceFilter.sharedMesh = mesh;
                oldSkin.sharedMesh = mesh; oldSkin.localBounds = mesh.bounds;
                HeadWoundSkinning.SetMesh(skin, mesh, head.transform);
                reference.enabled = true; skin.enabled = false; oldSkin.enabled = false;
                int expected = Render(camera, pixels, out _);
                if (expected == 0) throw new Exception("Reference fixture is too small or outside the camera.");
                reference.enabled = false; oldSkin.enabled = true;
                // Reproduce the former native-root/head-local-bounds assignment.
                int old = Render(camera, pixels, out int oldTunnel);
                if (old == 0 && oldTunnel > 0) report.oldSkinMissingWithTunnelVisible++;
                if (report.cases == 0 || (old == 0 && report.oldSkinMissingWithTunnelVisible == 1))
                    File.WriteAllBytes(Path.Combine(output, "old.png"), pixels.EncodeToPNG());

                // This is the production method used at initial creation AND mesh swaps.
                oldSkin.enabled = false; skin.enabled = true;
                int actual = Render(camera, pixels, out _);
                if (actual == 0 || Math.Abs(actual - expected) > Math.Max(2, expected * 0.02f))
                    Debug.Log($"Culling case pose={pose}, distance={distance}, fov={fov}, yaw={yaw}, expected={expected}, old={old}, actual={actual}, worldBounds={skin.bounds}, head={head.transform.position}");
                if (actual == 0) report.fixedSkinMissing++;
                if (Math.Abs(actual - expected) > Math.Max(2, expected * 0.02f)) report.pixelParityFailures++;
                if (!skin.bounds.Contains(head.transform.TransformPoint(mesh.bounds.center))) report.boundsFailures++;
                if (report.cases == 0 || (old == 0 && report.oldSkinMissingWithTunnelVisible == 1))
                    File.WriteAllBytes(Path.Combine(output, "fixed.png"), pixels.EncodeToPNG());
                report.cases++;
            }
            report.passed = report.oldSkinMissingWithTunnelVisible > 0 && report.fixedSkinMissing == 0 &&
                report.boundsFailures == 0 && report.pixelParityFailures == 0;
            File.WriteAllText(Path.Combine(output, "verification.json"), JsonUtility.ToJson(report, true));
            if (!report.passed) throw new Exception("Head culling regression failed: " + JsonUtility.ToJson(report));
            Debug.Log("PASS: head skin/canal scope culling: " + JsonUtility.ToJson(report));
        }
        finally
        {
            camera.targetTexture = null; RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(skin.sharedMaterial); UnityEngine.Object.DestroyImmediate(tunnel.sharedMaterial);
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraObject);
            foreach (Mesh mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(tunnelMesh); UnityEngine.Object.DestroyImmediate(pixels);
            target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }
    }

    private static int Render(Camera camera, Texture2D pixels, out int green)
    {
        camera.Render(); RenderTexture.active = camera.targetTexture;
        pixels.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); pixels.Apply();
        int red = 0; green = 0;
        foreach (Color32 pixel in pixels.GetPixels32())
        {
            if (pixel.r > 100 && pixel.g < 30) red++;
            if (pixel.g > 100 && pixel.r < 30) green++;
        }
        return red;
    }

    private static Mesh Ring(float x, float y, float hole)
    {
        var vertices = new List<Vector3>(); var triangles = new List<int>();
        void Quad(float x0, float y0, float x1, float y1)
        {
            int first = vertices.Count;
            vertices.Add(new Vector3(x0, y0, 0)); vertices.Add(new Vector3(x1, y0, 0));
            vertices.Add(new Vector3(x1, y1, 0)); vertices.Add(new Vector3(x0, y1, 0));
            triangles.AddRange(new[] { first, first+1, first+2, first, first+2, first+3,
                first+2, first+1, first, first+3, first+2, first });
        }
        Quad(-x, -y, -hole, y); Quad(hole, -y, x, y);
        Quad(-hole, -y, hole, -hole); Quad(-hole, hole, hole, y);
        var mesh = new Mesh(); mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0);
        var weights = new BoneWeight[vertices.Count];
        for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        mesh.boneWeights = weights; mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
    }
}
#endif
