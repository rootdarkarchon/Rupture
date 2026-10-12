using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
internal static class HeadWoundSkinning
{
    internal static void SetMesh(SkinnedMeshRenderer renderer, Mesh mesh, Transform headBone)
    {
        // The baked vertices, rebuilt bindposes and bounds are all head-local.
        // Unity anchors skinned culling bounds to rootBone, which need not match
        // the native renderer's transform or its original skeleton root.
        renderer.rootBone = headBone;
        renderer.sharedMesh = mesh;
        renderer.localBounds = mesh.bounds;
    }
}
}
