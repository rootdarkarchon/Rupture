using System;
using EFT;
using EFT.AssetsManager;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.Casings;

internal static class CasingGeometry
{
    internal static bool TryFit(AmmoPoolObject source, out CasingDescriptor descriptor, out int meshKey)
    {
        descriptor = default; meshKey = 0;
        bool found = false;
        Bounds bounds = default;
        foreach (MeshFilter filter in source.GetComponentsInChildren<MeshFilter>(false))
        {
            if (filter.sharedMesh == null || !IsShell(filter.transform, source.transform)) continue;
            Renderer renderer = filter.GetComponent<Renderer>();
            if (renderer == null || !renderer.enabled) continue;
            meshKey = unchecked(meshKey * 397 ^ filter.sharedMesh.GetInstanceID());
            Bounds local = filter.sharedMesh.bounds;
            Matrix4x4 matrix = source.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                Vector3 point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                point = Vector3.Scale(point, source.transform.lossyScale);
                if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; }
                else bounds.Encapsulate(point);
            }
        }
        if (!found) return false;
        return TryFitBounds(bounds, out descriptor);
    }

    internal static bool TryFitBounds(Bounds bounds, out CasingDescriptor descriptor)
    {
        descriptor = default;
        Vector3 size = bounds.size;
        int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
        float length = size[axis];
        float radius = Mathf.Max(size[(axis + 1) % 3], size[(axis + 2) % 3]) * .5f;
        if (!Finite(bounds.center) || !Finite(size) || radius < .001f || radius > .1f || length < .003f || length > .5f) return false;
        descriptor.Radius = radius;
        descriptor.HalfHeight = Mathf.Max(0, length * .5f - radius);
        // The matched 4,096-casing pile settles with a box; capsules continued
        // rolling/contact-solving. Keep the single fitted primitive cheap.
        descriptor.Box = 1;
        descriptor.ShapePose = new CasingPose(bounds.center, Quaternion.FromToRotation(Vector3.right,
            axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward));
        // Thin brass shell approximation, bounded for mixed ammunition assets.
        descriptor.Mass = Mathf.Clamp(8500f * Mathf.PI * (2f * radius * length * .0003f + radius * radius * .0006f), .002f, .04f);
        return true;
    }

    internal static bool IsShell(Transform transform, Transform root)
    {
        // Match AmmoPoolObject.SetUsed's TransformTools.FindTransformRecursiveContains rule.
        // Stock assets use names such as patron_556x45_m855_shell_LOD0.
        for (Transform current = transform; current != null; current = current.parent)
        {
            if (current.name.IndexOf("shell", StringComparison.Ordinal) >= 0) return true;
            if (current == root) break;
        }
        return false;
    }
    internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
