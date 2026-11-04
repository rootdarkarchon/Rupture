#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;
using Rupture.PhysXBackend;
using UnityEngine;

namespace Rupture.PhysXBackend
{
    internal struct MaterialKey { internal MaterialKey(PhysicMaterial ignored) { } }
    internal sealed class ShapeSnapshot
    {
        internal Collider Collider;
        internal int ColliderId;
        internal uint GeometryKind, LayerBits, CollidesWithBits, OwnerGroup;
        internal NativeApi.Pose LocalPose;
        internal Vector3 Dimensions;
        internal MeshGeometry Mesh;
        internal MaterialKey Material;
        internal float ContactOffset;
        internal bool Enabled;
    }
    internal sealed partial class ActorSnapshot
    {
        internal Transform Root;
        internal bool Movable, UpdateMassPropertiesFromShapes;
        internal NativeApi.BodyDesc Body;
        internal readonly List<ShapeSnapshot> Shapes = new List<ShapeSnapshot>();
        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}

[UnityEditor.InitializeOnLoad]
public static class WeaponColliderShapeRuntimeTests
{
    [Serializable] private sealed class Fixtures { public Prefab[] fixtures; }
    [Serializable] private sealed class Prefab { public string name, root; public Node[] nodes; public Box[] boxes; }
    [Serializable] private sealed class Node { public string id, parent, name; public Vector3 position, scale; public Quaternion rotation; public int layer; }
    [Serializable] private sealed class Box { public string node, path; public Vector3 center, size; public bool trigger; }
    private static int _cases, _prefabs;
    static WeaponColliderShapeRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool("WeaponGeometry", false)) return;
            UnityEditor.SessionState.SetBool("WeaponGeometry", false);
            try { Execute(); UnityEditor.EditorApplication.Exit(0); }
            catch (Exception exception) { Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); }
        };
    }
    public static void RunBatch() { UnityEditor.SessionState.SetBool("WeaponGeometry", true); UnityEditor.EditorApplication.EnterPlaymode(); }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); _cases++; Debug.Log("PASS: " + name); }
    private static float Volume(Vector3 size) => size.x * size.y * size.z;
    private static void Execute()
    {
        WeaponColliderGeometryTests.Run(Check);
        TestOriented();
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-weaponFixtures");
        if (index >= 0) foreach (Prefab prefab in JsonUtility.FromJson<Fixtures>(File.ReadAllText(args[index + 1])).fixtures) TestPrefab(prefab);
        using (var api = new NativeApi(Path.Combine(Application.dataPath, "Plugins/x86_64"), false)) TestCooked(api);
        File.WriteAllText(Path.Combine(Application.dataPath, "../verification.json"),
            "{\"passed\":true,\"cases\":" + _cases + ",\"prefabs\":" + _prefabs + "}");
    }
    private static void TestOriented()
    {
        var root = new GameObject("tilted weapon container"); var body = root.AddComponent<Rigidbody>(); body.isKinematic = true;
        var source = new GameObject("authored barrel frame"); source.transform.SetParent(root.transform, false);
        source.transform.localPosition = new Vector3(.1f, -.2f, .3f); source.transform.localRotation = Quaternion.Euler(27, 0, 0);
        var box = source.AddComponent<BoxCollider>(); box.size = new Vector3(.1f, .2f, .9f);
        var ammo = new GameObject("excluded ammo"); ammo.transform.SetParent(root.transform, false);
        ammo.transform.localPosition = Vector3.one * 100; ammo.AddComponent<BoxCollider>();
        var trigger = root.AddComponent<BoxCollider>(); trigger.isTrigger = true; trigger.size = Vector3.one * 100;
        var foreign = new GameObject("foreign rigidbody"); foreign.transform.SetParent(root.transform, false);
        foreign.AddComponent<Rigidbody>().isKinematic = true; foreign.AddComponent<BoxCollider>().size = Vector3.one * 100;
        var shell = new GameObject("excluded shell layer"); shell.transform.SetParent(root.transform, false);
        shell.layer = 22; shell.AddComponent<BoxCollider>().size = Vector3.one * 100;
        foreach (Vector3 scale in new[] { Vector3.one, new Vector3(1.2f, .8f, 1.1f), new Vector3(-1.2f, .8f, 1.1f) })
        {
            root.transform.localScale = scale;
            Check(WeaponColliderGeometry.TryFit(body, new[] { box }, out Bounds old), "axis locked comparison fit");
            var all = root.GetComponentsInChildren<Collider>(true);
            Check(WeaponColliderGeometry.TryFitOriented(body, all, out var fit, new[] { ammo.transform }, 22), "oriented fit excludes ammunition, shells, triggers and foreign rigidbodies");
            Check(Volume(fit.Size) <= Volume(Vector3.Scale(old.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)))) * 1.001f,
                "oriented fit never inflates the original actor frame bounds");
            CheckContained(body, new[] { box }, fit);
            root.transform.SetPositionAndRotation(new Vector3(120, -3, 200), Quaternion.Euler(27, 61, 43));
            Physics.SyncTransforms();
            Check(WeaponColliderGeometry.TryFitOriented(body, all, out var moved, new[] { ammo.transform }, 22) &&
                Vector3.Distance(moved.Center, fit.Center) < .0003f && Vector3.Distance(moved.Size, fit.Size) < .0003f && Quaternion.Angle(moved.Rotation, fit.Rotation) < .1f,
                "oriented fit survives world pose changes and nonuniform or negative hierarchy scale");
            box.enabled = false; source.SetActive(false);
            Check(WeaponColliderGeometry.TryFitOriented(body, all, out var hidden, new[] { ammo.transform }, 22) &&
                Vector3.Distance(hidden.Size, moved.Size) < .0001f && Vector3.Distance(hidden.Center, moved.Center) < .0001f,
                "oriented fit remains independent of collider enable and object visibility state");
            source.SetActive(true); box.enabled = true;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Physics.SyncTransforms();
        }
        UnityEngine.Object.DestroyImmediate(root);
    }
    private static void CheckContained(Rigidbody body, IEnumerable<BoxCollider> boxes, WeaponColliderGeometry.OrientedFit fit)
    {
        bool contained = true;
        foreach (BoxCollider box in boxes)
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = box.center + Vector3.Scale(box.size * .5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            Vector3 actor = Quaternion.Inverse(body.rotation) * (box.transform.TransformPoint(corner) - body.position);
            Vector3 local = Quaternion.Inverse(fit.Rotation) * (actor - fit.Center);
            for (int axis = 0; axis < 3; axis++) contained &= Mathf.Abs(local[axis]) <= fit.Size[axis] * .5f + .0002f;
        }
        Check(contained, "oriented fit encloses every included authored box corner");
    }
    private static void TestPrefab(Prefab prefab)
    {
        var objects = prefab.nodes.ToDictionary(n => n.id, n => new GameObject(n.name));
        foreach (Node node in prefab.nodes)
        {
            Transform t = objects[node.id].transform;
            if (objects.TryGetValue(node.parent, out GameObject parent)) t.SetParent(parent.transform, false);
            t.localPosition = node.position; t.localRotation = node.rotation; t.localScale = node.scale;
        }
        var body = objects[prefab.root].AddComponent<Rigidbody>(); body.isKinematic = true;
        var boxes = new List<BoxCollider>();
        foreach (Box data in prefab.boxes)
        {
            var box = objects[data.node].AddComponent<BoxCollider>(); box.center = data.center; box.size = data.size;
            box.isTrigger = data.trigger; boxes.Add(box);
        }
        try
        {
            Check(WeaponColliderGeometry.TryFit(body, boxes.Cast<Collider>().ToArray(), out Bounds old) &&
                WeaponColliderGeometry.TryFitOriented(body, boxes.Cast<Collider>().ToArray(), out _), "installed prefab fit: " + prefab.name);
            WeaponColliderGeometry.TryFitOriented(body, boxes.Cast<Collider>().ToArray(), out var fit);
            CheckContained(body, boxes, fit);
            Debug.Log($"WEAPON_FIT {prefab.name}: original={old.size:F4} fitted={fit.Size:F4} rotation={fit.Rotation.eulerAngles:F2} volumeRatio={Volume(fit.Size) / Volume(old.size):F4}");
            Check(Volume(fit.Size) <= Volume(old.size) * 1.001f, "installed prefab volume does not grow: " + prefab.name);
            // Reproduce a changed internal weapon pose while the rigidbody container
            // remains fixed. These are controlled poses, not a captured live death.
            foreach (Node node in prefab.nodes.Where(n => n.name == "Weapon_root_anim"))
                objects[node.id].transform.localRotation *= Quaternion.Euler(23, 0, 0);
            Physics.SyncTransforms();
            Check(WeaponColliderGeometry.TryFit(body, boxes.Cast<Collider>().ToArray(), out Bounds tiltedOld) &&
                WeaponColliderGeometry.TryFitOriented(body, boxes.Cast<Collider>().ToArray(), out _), "posed prefab fit: " + prefab.name);
            WeaponColliderGeometry.TryFitOriented(body, boxes.Cast<Collider>().ToArray(), out var posed);
            CheckContained(body, boxes, posed);
            Debug.Log($"WEAPON_POSED_FIT {prefab.name}: original={tiltedOld.size:F4} fitted={posed.Size:F4} rotation={posed.Rotation.eulerAngles:F2} volumeRatio={Volume(posed.Size) / Volume(tiltedOld.size):F4}");
            Check(Volume(posed.Size) < Volume(tiltedOld.size) * .85f, "authored frame removes diagonal inflation from posed prefab: " + prefab.name);
            _prefabs++;
        }
        finally { UnityEngine.Object.DestroyImmediate(body.gameObject); }
    }
    private static void TestCooked(NativeApi api)
    {
        var desc = NativeApi.SceneDesc.Create(0, NativeApi.UnitySceneSettings.Create());
        api.Check(api.CreateScene(ref desc, out IntPtr scene), "create scene");
        var root = new GameObject("scaled snapshot weapon"); var body = root.AddComponent<Rigidbody>();
        root.transform.localScale = new Vector3(2, .5f, -3);
        Vector3 center = new Vector3(.1f, .2f, -.3f), size = new Vector3(.1f, .45f, .86f);
        Quaternion rotation = Quaternion.Euler(22, 7, -3);
        var rounded = ActorSnapshot.CaptureRoundedBoxBody(body, new ExternalRoundedBoxShape(center, size, rotation, null, .01f), 1, false, 0, 1);
        var fallback = ActorSnapshot.CaptureBoxBody(body, new ExternalBoxShape(center, size, rotation, null, .01f), 1, false, 0, 1);
        Check(Vector3.Distance(rounded.Shapes[0].LocalPose.Position.ToUnity(), center) < 1e-6f &&
            Quaternion.Angle(rounded.Shapes[0].LocalPose.Rotation.ToUnity(), rotation) < .01f &&
            Vector3.Distance(fallback.Shapes[0].Dimensions, size * .5f) < 1e-6f &&
            Quaternion.Angle(fallback.Shapes[0].LocalPose.Rotation.ToUnity(), rotation) < .01f,
            "rounded and fallback snapshots preserve actor-space fit orientation, offset and scale exactly once");
        var legacy = ActorSnapshot.CaptureBoxBody(body, new ExternalBoxShape(center, size, null, .01f), 1, false, 0, 1);
        Check(Vector3.Distance(legacy.Shapes[0].Dimensions, Vector3.Scale(size, new Vector3(2, .5f, 3)) * .5f) < 1e-6f,
            "legacy shape constructor retains body-local scaling behavior");
        try
        {
            var md = new NativeApi.MaterialDesc { StructSize = (uint)Marshal.SizeOf<NativeApi.MaterialDesc>(), StaticFriction = .6f, DynamicFriction = .6f };
            api.Check(api.CreateMaterial(scene, ref md, out ulong material), "material");
            var generated = rounded.Shapes[0].Mesh;
            var vertices = Array.ConvertAll(generated.Vertices, v => new NativeApi.Vec3(v));
            var pin = GCHandle.Alloc(vertices, GCHandleType.Pinned);
            ulong mesh;
            try
            {
                var meshDesc = new NativeApi.MeshDesc { StructSize = (uint)Marshal.SizeOf<NativeApi.MeshDesc>(), Convex = 1,
                    Vertices = pin.AddrOfPinnedObject(), VertexCount = (uint)vertices.Length };
                Check(api.CookMesh(scene, ref meshDesc, IntPtr.Zero, 0, out uint required) == NativeApi.BufferTooSmall, "rounded ridge hull cooks in production PhysX");
                byte[] cooked = new byte[required]; var bytes = GCHandle.Alloc(cooked, GCHandleType.Pinned);
                try { api.Check(api.CopyCookedMesh(scene, IntPtr.Zero, bytes.AddrOfPinnedObject(), required, out _), "copy cooked"); }
                finally { bytes.Free(); }
                api.Check(api.CreateMeshFromCooked(scene, 1, cooked, required, out mesh), "load hull");
            }
            finally { pin.Free(); }
            NativeApi.BodyDesc actorDesc = rounded.Body;
            api.Check(api.CreateActor(scene, ref actorDesc, out ulong actor), "actor");
            var shape = new NativeApi.ShapeDesc { StructSize = (uint)Marshal.SizeOf<NativeApi.ShapeDesc>(), GeometryKind = 5,
                MeshHandle = mesh, MaterialHandle = material, MeshScale = new NativeApi.Vec3(Vector3.one),
                LocalPose = rounded.Shapes[0].LocalPose, LayerBits = 1, CollidesWithBits = 1, ContactOffset = .01f, Enabled = 1 };
            api.Check(api.AttachShape(scene, actor, ref shape, out ulong handle), "attach hull");
            Check(api.GetShapeWireframe(scene, handle, null, 0, out uint count) == NativeApi.BufferTooSmall, "read cooked ridge edge count");
            var edges = new NativeApi.Vec3[count]; api.Check(api.GetShapeWireframe(scene, handle, edges, count, out _), "read ridge edges");
            Vector3[] local = Array.ConvertAll(edges, v => Quaternion.Inverse(rotation) * (v.ToUnity() - center));
            bool ridge = true;
            for (int axis = 1; axis <= 2; axis++)
                foreach (Vector3 point in local.Where(v => Mathf.Abs(v[axis]) >= size[axis] * .5f - .0001f)) ridge &= Mathf.Abs(point.x) < .0002f;
            Check(ridge && local.Any(v => Mathf.Abs(v.y - size.y * .5f) < .0001f) && local.Any(v => Mathf.Abs(v.z - size.z * .5f) < .0001f),
                "actual cooked narrow top/bottom/end supports meet at middle ridge with no flat thickness strip");
            Vector3 midpoint = new Vector3(Mathf.Sqrt(.5f) * .05f, size.y * .5f - .05f + Mathf.Sqrt(.5f) * .05f, size.z * .5f - .05f);
            Check(local.Any(v => Vector3.Distance(v, midpoint) < .0002f), "45-degree arc midpoint survives native convex cooking");
        }
        finally { api.DestroyScene(scene); UnityEngine.Object.DestroyImmediate(root); }
    }
}
#endif
