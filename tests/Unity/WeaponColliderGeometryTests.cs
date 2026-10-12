#if UNITY_EDITOR
using System;
using Rupture.Ragdolls.Classes;
using UnityEngine;

internal static class WeaponColliderGeometryTests
{
    internal static void Run(Action<bool, string> check)
    {
        var root = new GameObject("weapon");
        var body = root.AddComponent<Rigidbody>(); body.isKinematic = true;
        var barrel = new GameObject("barrel"); barrel.transform.SetParent(root.transform, false);
        barrel.transform.localPosition = new Vector3(.02f, .1f, -.15f);
        barrel.transform.localRotation = Quaternion.Euler(3, 0, 7);
        var box = barrel.AddComponent<BoxCollider>(); box.size = new Vector3(.05f, .16f, .7f);
        var renderer = barrel.AddComponent<MeshRenderer>();
        var sphere = root.AddComponent<SphereCollider>(); sphere.radius = .03f;
        var capsule = root.AddComponent<CapsuleCollider>(); capsule.direction = 2; capsule.radius = .025f; capsule.height = .6f;
        var mesh = root.AddComponent<MeshCollider>();
        var shape = new Mesh { vertices = new[] { new Vector3(-.01f,-.02f,-.2f), new Vector3(.01f,-.02f,-.2f), new Vector3(0,.02f,.2f) }, triangles = new[] {0,1,2} };
        shape.RecalculateBounds(); mesh.sharedMesh = shape;
        var trigger = root.AddComponent<BoxCollider>(); trigger.isTrigger = true; trigger.size = Vector3.one * 100;
        var other = new GameObject("separate body"); other.transform.SetParent(root.transform, false);
        other.AddComponent<Rigidbody>().isKinematic = true;
        var foreign = other.AddComponent<BoxCollider>(); foreign.size = Vector3.one * 100;
        Collider[] sources = { box, sphere, capsule, mesh, trigger, foreign };
        try
        {
            foreach (Vector3 scale in new[] { Vector3.one, new Vector3(1.2f, .8f, 1.1f) })
            {
                root.transform.localScale = scale;
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                check(WeaponColliderGeometry.TryFit(body, sources, out Bounds expected), "native weapon collider fit succeeds");
                check(expected.size.magnitude < 2f, "trigger and foreign rigidbody excluded");
                foreach (Quaternion rotation in new[] { Quaternion.Euler(27, 63, 18), Quaternion.Euler(90, -43, 112) })
                foreach (bool visible in new[] { true, false })
                {
                    root.transform.SetPositionAndRotation(new Vector3(124, -3, 212), rotation);
                    renderer.enabled = visible; barrel.SetActive(visible);
                    check(WeaponColliderGeometry.TryFit(body, sources, out Bounds actual), "hidden weapon fit succeeds");
                    check(Vector3.Distance(expected.center, actual.center) < .0001f && Vector3.Distance(expected.size, actual.size) < .0001f,
                        $"weapon geometry is invariant under world rotation, translation and render state: enabled={visible}, expected={expected}, actual={actual}, owner={box.attachedRigidbody}");
                }
                barrel.SetActive(true);
            }
            check(WeaponColliderGeometry.TryFit(body, new Collider[] { box }, out Bounds onlyBox) && onlyBox.size.magnitude < 1f,
                "primitive box uses authored geometry");
            var magazine = new GameObject("native magazine asset root"); magazine.transform.SetParent(root.transform, false);
            var magazineChild = new GameObject("magazine collider"); magazineChild.transform.SetParent(magazine.transform, false);
            var magazineBox = magazineChild.AddComponent<BoxCollider>(); magazineBox.size = new Vector3(.04f, .2f, .1f);
            var withMagazine = new Collider[] { box, magazineBox };
            foreach (Vector3 position in new[] { new Vector3(0, -.15f, 0), new Vector3(3, -10, 4) })
            {
                magazine.transform.localPosition = position;
                foreach (bool active in new[] { true, false })
                {
                    magazine.SetActive(active);
                    check(WeaponColliderGeometry.TryFit(body, withMagazine, out Bounds fitted, new[] { magazine.transform }) &&
                        Vector3.Distance(fitted.center, onlyBox.center) < .0001f && Vector3.Distance(fitted.size, onlyBox.size) < .0001f,
                        "attached or animated magazine cannot inflate weapon box, including inactive magazine");
                }
            }
            magazine.SetActive(true); magazine.transform.SetParent(null, true);
            try
            {
                check(WeaponColliderGeometry.TryFit(body, withMagazine, out Bounds detached) && Vector3.Distance(detached.size, onlyBox.size) < .0001f,
                    "detached magazine is excluded by rigidbody ownership");
            }
            finally { UnityEngine.Object.DestroyImmediate(magazine); }
            var cartridge = new GameObject("pooled cartridge root"); cartridge.transform.SetParent(root.transform, false);
            var cartridgeBox = cartridge.AddComponent<BoxCollider>();
            var shell = new GameObject("ejected shell"); shell.transform.SetParent(root.transform, false); shell.layer = 22;
            var shellBox = shell.AddComponent<BoxCollider>();
            foreach (bool active in new[] { true, false })
            {
                cartridge.transform.localPosition = new Vector3(20, -10, 8);
                shell.transform.localPosition = new Vector3(-9, 20, -10);
                cartridge.SetActive(active); shell.SetActive(active);
                Collider[] ammunition = { box, cartridgeBox, shellBox };
                check(WeaponColliderGeometry.TryFit(body, ammunition, out Bounds filtered, new[] { cartridge.transform }, 22) &&
                    Vector3.Distance(filtered.center, onlyBox.center) < .0001f && Vector3.Distance(filtered.size, onlyBox.size) < .0001f,
                    "pooled cartridges and shell-layer ejection offsets cannot enlarge weapon fit, active=" + active);
                check(WeaponColliderGeometry.TryFit(body, ammunition, out Bounds contaminated) && contaminated.size.magnitude > 20,
                    "fixture reproduces enlarged weapon fit when ammunition is included");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(shape); }
    }
}
#endif
