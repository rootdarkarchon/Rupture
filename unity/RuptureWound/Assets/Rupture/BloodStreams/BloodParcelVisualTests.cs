#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.BloodStreams;

namespace Rupture.UnityPreview.BloodStreams
{
public static class BloodParcelVisualTests
{
    [Serializable] private sealed class Report { public string generatedUtc; public string bundleSha256; public int cases; public int minimumCoveredPixels; public double maximumMeanChannelError; public bool passed; }
    public static void RunBatch()
    {
        string[] args = Environment.GetCommandLineArgs();
        int bundleArgument = Array.IndexOf(args,"-rptBundlePath");
        if (bundleArgument < 0 || bundleArgument + 1 >= args.Length) throw new Exception("Pass the built bundle with -rptBundlePath.");
        string bundlePath = args[bundleArgument + 1];
        var bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle == null) throw new Exception("Built bundle could not be opened.");
        var material = new Material(bundle.LoadAllAssets<Shader>().Single(shader => shader.name == "Rupture/PulsatingBloodStream"));
        RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.4f,0.4f,0.4f);
        var cameraObject = new GameObject("RPT Parcel Verification Camera");
        var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        camera.orthographic = true; camera.orthographicSize = 0.10f; camera.nearClipPlane = 0.01f; camera.farClipPlane = 10;
        camera.renderingPath = RenderingPath.Forward;
        var target = new RenderTexture(256,256,24,RenderTextureFormat.ARGB32); target.Create(); camera.targetTexture = target;
        var pixels = new Texture2D(256,256,TextureFormat.RGBA32,false);
        var lightObject = new GameObject("RPT Parcel Verification Light");
        var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1;
        light.transform.rotation = Quaternion.Euler(35,-25,0);
        var body = new GameObject("RPT CPU Reference Parcel");
        var mesh = new Mesh(); body.AddComponent<MeshFilter>().sharedMesh = mesh;
        var meshRenderer = body.AddComponent<MeshRenderer>(); meshRenderer.sharedMaterial = material; meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>();
        var colors = new List<Color32>(); var triangles = new List<int>();
        var report = new Report { generatedUtc = DateTime.UtcNow.ToString("O"), minimumCoveredPixels = int.MaxValue, passed = true };
        string output = Path.Combine(Directory.GetParent(Application.dataPath).FullName,"ParcelVerification"); Directory.CreateDirectory(output);
        using (var hash = SHA256.Create())
        using (var input = File.OpenRead(bundlePath)) report.bundleSha256 = BitConverter.ToString(hash.ComputeHash(input)).Replace("-","");
        using var renderer = new BloodParcelRenderer();
        try
        {
            foreach (float age in new[] {0f,0.15f,0.5f,1.3f})
            foreach (uint seed in new[] {17u,123456u,0x27D4EB2Du})
            {
                Vector3 origin = Vector3.zero, velocity = new Vector3(1.1f,0.2f,0.05f), gravity = Physics.gravity;
                Vector3 center = origin + velocity * age + gravity * (0.5f * age * age);
                camera.transform.position = center + new Vector3(0.2f,0.08f,-0.5f); camera.transform.LookAt(center);
                StreamPreviewGeometry.Clear(vertices,normals,uv,colors,triangles);
                StreamPreviewGeometry.AddParcel(vertices,normals,uv,colors,triangles,origin,velocity,gravity,age,0.012f,0.8f,seed);
                StreamPreviewGeometry.Apply(mesh,vertices,normals,uv,colors,triangles);
                body.SetActive(true); camera.Render(); Color32[] cpu = Read(target,pixels);
                body.SetActive(false);
                if (!renderer.Begin(material) || !renderer.Add(origin,velocity,gravity,0,0.012f,0.8f,seed)) throw new Exception("Instanced renderer unavailable.");
                renderer.Submit(age,camera); camera.Render(); Color32[] gpu = Read(target,pixels);
                long difference = 0; int covered = 0;
                for (int i = 0; i < cpu.Length; i++)
                {
                    if (cpu[i].r + cpu[i].g + cpu[i].b > 12 || gpu[i].r + gpu[i].g + gpu[i].b > 12)
                    {
                        covered++; difference += Math.Abs(cpu[i].r-gpu[i].r) + Math.Abs(cpu[i].g-gpu[i].g) + Math.Abs(cpu[i].b-gpu[i].b);
                    }
                }
                double mean = covered == 0 ? 255 : difference / (double)(covered * 3);
                report.cases++; report.minimumCoveredPixels = Math.Min(report.minimumCoveredPixels,covered);
                report.maximumMeanChannelError = Math.Max(report.maximumMeanChannelError,mean);
                report.passed &= covered > 100 && mean < 2.0;
                if (seed == 123456u)
                {
                    pixels.SetPixels32(cpu); pixels.Apply(); File.WriteAllBytes(Path.Combine(output,"cpu-"+age.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+".png"),pixels.EncodeToPNG());
                    pixels.SetPixels32(gpu); pixels.Apply(); File.WriteAllBytes(Path.Combine(output,"gpu-"+age.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+".png"),pixels.EncodeToPNG());
                }
            }
            File.WriteAllText(Path.Combine(output,"verification.json"),JsonUtility.ToJson(report,true));
            if (!report.passed) throw new Exception("GPU/CPU parcel image parity failed: " + JsonUtility.ToJson(report));
            Debug.Log("PASS: GPU/CPU parcel image parity: " + JsonUtility.ToJson(report));
        }
        finally
        {
            bundle.Unload(false);
            camera.targetTexture = null; RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(body); UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(cameraObject); UnityEngine.Object.DestroyImmediate(lightObject);
            UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(pixels); target.Release(); UnityEngine.Object.DestroyImmediate(target);
        }
    }
    private static Color32[] Read(RenderTexture target, Texture2D pixels)
    {
        RenderTexture.active = target; pixels.ReadPixels(new Rect(0,0,256,256),0,0); pixels.Apply(); return pixels.GetPixels32();
    }
}
}
#endif
