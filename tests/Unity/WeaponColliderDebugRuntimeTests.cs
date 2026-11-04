#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Rupture;
using Rupture.Ragdolls.Classes;
using UnityEngine;

// Isolates the production renderer; native polygon extraction has separate native tests.
namespace Rupture
{
    internal static class RuptureConfiguration
    {
        internal sealed class Toggle { internal bool Value; }
        internal static readonly Toggle WeaponColliderWireframe = new Toggle();
    }
    internal static class RupturePlugin
    {
        internal sealed class Logger { internal void LogWarning(string message) { Debug.LogWarning(message); } }
        internal static readonly Logger Log = new Logger();
    }
}
namespace Rupture.Ragdolls.Classes
{
    internal static class ShadowRagdollWorld
    {
        internal struct WeaponColliderDebugShape
        {
            internal Transform Transform;
            internal Vector3[] Edges;
            internal Vector3 Size;
            internal bool Rounded;
        }
        internal static WeaponColliderDebugShape Fixture;
        internal static void CollectWeaponColliderDebugShapes(Vector3 position, List<WeaponColliderDebugShape> shapes)
        { shapes.Clear(); shapes.Add(Fixture); }
    }
}

[UnityEditor.InitializeOnLoad]
public static class WeaponColliderDebugRuntimeTests
{
    private const string Pending = "WeaponColliderDebugRuntimeTests";
    private static int _cases;
    static WeaponColliderDebugRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool(Pending, false)) return;
            UnityEditor.SessionState.SetBool(Pending, false);
            try { Execute(); UnityEditor.EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); }
        };
    }
    public static void RunBatch()
    { UnityEditor.SessionState.SetBool(Pending, true); UnityEditor.EditorApplication.EnterPlaymode(); }
    private static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); _cases++; Debug.Log("PASS: " + name); }
    private static void Execute()
    {
        var camera = new GameObject("debug camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0, 0, -5);
        camera.orthographic = true; camera.orthographicSize = 2;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        var target = new RenderTexture(256, 256, 24); target.Create(); camera.targetTexture = target;
        var weapon = new GameObject("fixture weapon");
        // Scale must not be applied twice to already baked actor-space edges.
        weapon.transform.localScale = new Vector3(3, 2, 4);
        weapon.transform.rotation = Quaternion.Euler(0, 0, 20);
        var edges = new[] { new Vector3(-.7f, -.3f, 0), new Vector3(.9f, -.3f, 0),
            new Vector3(.9f, -.3f, 0), new Vector3(.9f, .4f, 0),
            new Vector3(.9f, .4f, 0), new Vector3(-.7f, .4f, 0),
            new Vector3(-.7f, .4f, 0), new Vector3(-.7f, -.3f, 0) };
        ShadowRagdollWorld.Fixture = new ShadowRagdollWorld.WeaponColliderDebugShape
        { Transform = weapon.transform, Edges = edges, Size = new Vector3(1.6f, .7f, .1f), Rounded = true };
        var view = new GameObject("production wireframe renderer").AddComponent<WeaponColliderDebugView>();
        var readback = new Texture2D(256, 256, TextureFormat.RGB24, false);
        Func<bool, int> render = cyan =>
        {
            view.SendMessage("LateUpdate"); camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); readback.Apply(); RenderTexture.active = null;
            int count = 0;
            foreach (Color32 pixel in readback.GetPixels32())
                if (cyan ? pixel.g > 150 && pixel.b > 150 && pixel.r < 50 : pixel.r > 150 && pixel.g > 150 && pixel.b < 50) count++;
            return count;
        };
        Check(render(true) == 0, "wireframe is invisible by default");
        RuptureConfiguration.WeaponColliderWireframe.Value = true;
        Check(render(true) > 100, "production GL renderer draws cyan hull edges in a game camera");
        Vector3 corner = camera.WorldToScreenPoint(weapon.transform.position + weapon.transform.rotation * edges[0]);
        bool atCorner = false;
        for (int x = (int)corner.x - 2; x <= corner.x + 2; x++)
        for (int y = (int)corner.y - 2; y <= corner.y + 2; y++)
        { Color pixel = readback.GetPixel(x, y); atCorner |= pixel.g > .5f && pixel.b > .5f && pixel.r < .2f; }
        Check(atCorner, "wireframe matches the visible actor pose without applying scale twice");
        File.WriteAllBytes(Path.Combine(Application.dataPath, "../wireframe.png"), readback.EncodeToPNG());
        var shape = ShadowRagdollWorld.Fixture; shape.Rounded = false; ShadowRagdollWorld.Fixture = shape;
        Check(render(false) > 100, "box fallback uses yellow edges");
        RuptureConfiguration.WeaponColliderWireframe.Value = false;
        Check(render(false) == 0, "live toggle hides existing wireframes immediately");
        RuptureConfiguration.WeaponColliderWireframe.Value = true;
        Check(render(false) > 100, "live toggle restores existing settled geometry");
        UnityEngine.Object.DestroyImmediate(weapon);
        Check(render(false) == 0, "released weapon transforms do not leave stale wireframes");
        File.WriteAllText(Path.Combine(Application.dataPath, "../verification.json"), "{\"passed\":true,\"cases\":" + _cases + "}");
        UnityEngine.Object.DestroyImmediate(view.gameObject);
        UnityEngine.Object.DestroyImmediate(camera.gameObject);
        UnityEngine.Object.DestroyImmediate(readback); UnityEngine.Object.DestroyImmediate(target);
    }
}
#endif
