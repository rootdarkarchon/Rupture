using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Ragdolls.Classes
{
    // Fit in the body's local frame from authored collision geometry. Renderer
    // visibility, LOD selection and world AABBs must never define physics shapes.
    internal static class WeaponColliderGeometry
    {
        internal static bool TryFit(Rigidbody body, IReadOnlyList<Collider> colliders, out Bounds bounds, IReadOnlyList<Transform> excludedRoots = null, int excludedLayer = -1)
        {
            bounds = default;
            bool found = false;
            if (body == null || colliders == null) return false;
            foreach (Collider collider in colliders)
            {
                if (collider == null || collider.isTrigger || collider.GetComponentInParent<Rigidbody>(true) != body) continue;
                if (collider.gameObject.layer == excludedLayer || IsExcluded(collider.transform, excludedRoots)) continue;
                if (!TryBounds(body.transform, collider, out Bounds local)) return false;
                if (!found) bounds = local; else bounds.Encapsulate(local);
                found = true;
            }
            return found;
        }

        internal static bool IsExcluded(Transform transform, IReadOnlyList<Transform> roots)
        {
            if (roots != null)
                foreach (Transform root in roots)
                    if (root != null && transform.IsChildOf(root)) return true;
            return false;
        }

        internal static bool TryBounds(Transform root, Collider collider, out Bounds bounds)
        {
            bounds = default;
            Matrix4x4 relative = root.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            if (collider is BoxCollider box)
                bounds = TransformBounds(new Bounds(box.center, box.size), relative);
            else if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                bounds = TransformBounds(mesh.sharedMesh.bounds, relative);
            else if (collider is SphereCollider sphere)
            {
                Vector3 scale = Abs(collider.transform.lossyScale);
                float radius = sphere.radius * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
                bounds = new Bounds(relative.MultiplyPoint3x4(sphere.center), Divide(Vector3.one * (2f * radius), Abs(root.lossyScale)));
            }
            else if (collider is CapsuleCollider capsule)
            {
                Vector3 scale = Abs(collider.transform.lossyScale);
                int axis = capsule.direction;
                float radius = capsule.radius * Mathf.Max(scale[(axis + 1) % 3], scale[(axis + 2) % 3]);
                float halfSegment = Mathf.Max(0f, capsule.height * scale[axis] * .5f - radius);
                Vector3 direction = Vector3.zero; direction[axis] = 1f;
                direction = Quaternion.Inverse(root.rotation) * (collider.transform.rotation * direction);
                Vector3 extents = Abs(direction) * halfSegment + Vector3.one * radius;
                bounds = new Bounds(relative.MultiplyPoint3x4(capsule.center), Divide(extents * 2f, Abs(root.lossyScale)));
            }
            else return false;
            return Finite(bounds.center) && Finite(bounds.size);
        }

        private static Bounds TransformBounds(Bounds source, Matrix4x4 matrix)
        {
            Vector3 extents = source.extents;
            Vector3 transformed = Abs(matrix.MultiplyVector(Vector3.right * extents.x)) +
                Abs(matrix.MultiplyVector(Vector3.up * extents.y)) + Abs(matrix.MultiplyVector(Vector3.forward * extents.z));
            return new Bounds(matrix.MultiplyPoint3x4(source.center), transformed * 2f);
        }
        private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        private static Vector3 Divide(Vector3 value, Vector3 scale) => new Vector3(value.x / scale.x, value.y / scale.y, value.z / scale.z);
        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
