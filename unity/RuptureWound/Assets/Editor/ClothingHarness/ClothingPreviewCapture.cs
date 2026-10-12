using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Rupture.ClothingHarness
{
    internal static class ClothingPreviewCapture
    {
        internal static string Save(CutCaseReport report)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("Preview capture needs a graphics device; omit -nographics");
            if (report.View == null) throw new InvalidOperationException("This case has no scene clone");
            bool wasActive = report.View.gameObject.activeSelf;
            report.View.gameObject.SetActive(true);
            Transform[] transforms = report.View.GetComponentsInChildren<Transform>(true);
            int[] layers = transforms.Select(t => t.gameObject.layer).ToArray();
            GameObject host = new GameObject("Clothing preview camera") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture target = null;
            Texture2D image = null;
            RenderTexture previous = RenderTexture.active;
            bool previousWireframe = GL.wireframe;
            var results = report.View.Results.Where(o => o != null).ToArray();
            var active = results.Select(o => o.activeSelf).ToArray();
            int previousLod = report.View.PreviewLod;
            try
            {
                foreach (Transform t in transforms) t.gameObject.layer = 31;
                Renderer[] renderers = report.View.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) throw new InvalidOperationException("Case has no visible geometry");
                Bounds bounds = renderers[0].bounds;
                foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);
                Camera camera = host.AddComponent<Camera>();
                camera.enabled = false; camera.cullingMask = 1 << 31;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.07f, 0.08f, 0.1f);
                camera.orthographic = true; camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x) * 1.35f;
                camera.nearClipPlane = 0.01f; camera.farClipPlane = 50f;
                host.transform.position = bounds.center + new Vector3(0.3f, 0.15f, 3f).normalized * 5f;
                host.transform.LookAt(bounds.center);
                Light light = host.AddComponent<Light>(); light.type = LightType.Directional;
                light.intensity = 1.5f; light.cullingMask = 1 << 31;
                target = new RenderTexture(1024, 1024, 24); camera.targetTexture = target;
                camera.Render(); RenderTexture.active = target;
                image = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); image.Apply();
                string path = Path.Combine(report.EvidenceDirectory, "preview.png");
                File.WriteAllBytes(path, image.EncodeToPNG());
                foreach (var focus in report.View.CutViews)
                {
                    report.View.PreviewLod = focus.LodLevel;
                    report.View.ApplyVisibility();
                    for (int i = 0; i < results.Length; i++)
                        results[i].SetActive(active[i] && (focus.Face == "body" ? results[i].name != "Detached limb" :
                            results[i].name != "Retained body" && results[i].name != "Body wound"));
                    Vector3 tangent = Vector3.Cross(focus.Normal, Mathf.Abs(focus.Normal.y) < .8f ? Vector3.up : Vector3.right).normalized;
                    camera.orthographicSize = focus.Radius;
                    foreach (string angle in new[] { "front", "oblique", "wire" })
                    {
                        Vector3 direction = (focus.Normal + tangent * (angle == "front" ? 0 : .85f)).normalized;
                        host.transform.position = focus.Center + direction * .65f;
                        host.transform.LookAt(focus.Center);
                        GL.wireframe = angle == "wire";
                        camera.Render();
                        image.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); image.Apply();
                        File.WriteAllBytes(Path.Combine(report.EvidenceDirectory, "lod" + focus.LodLevel + "-" + focus.Face + "-" + angle + ".png"), image.EncodeToPNG());
                    }
                }
                return path;
            }
            finally
            {
                RenderTexture.active = previous;
                GL.wireframe = previousWireframe;
                report.View.PreviewLod = previousLod;
                report.View.ApplyVisibility();
                for (int i = 0; i < results.Length; i++) if (results[i] != null) results[i].SetActive(active[i]);
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                UnityEngine.Object.DestroyImmediate(host);
                for (int i = 0; i < transforms.Length; i++) if (transforms[i] != null) transforms[i].gameObject.layer = layers[i];
                report.View.gameObject.SetActive(wasActive);
            }
        }
    }
}
