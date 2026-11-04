using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal static partial class HeadWoundSystem
{
    // Unity references never enter HeadWoundGeometryJob. Candidate indices join
    // its captured values back to the original renderer and exact captured pose.
    private sealed class HeadCandidate
    {
        internal SkinnedMeshRenderer Renderer;
        internal Mesh NativeMesh;
        internal Transform[] Bones;
        internal Matrix4x4[] Bindposes;
    }

    private enum HeadStage
    {
        Resources, Select, Bake, Acquire, Enqueue, WaitForGeometry,
        UploadHead, UploadCavity, PrepareSkin, PrepareCavity, TransferDecals, Commit, Count
    }

    private sealed class HeadBuild
    {
        internal CorpseWounds State;
        internal int Revision;
        internal Transform Head, VisualRoot;
        internal Renderer[] HeadRenderers;
        internal readonly List<HeadCandidate> Candidates = new List<HeadCandidate>();
        internal readonly List<HeadWoundGeometrySource> Sources = new List<HeadWoundGeometrySource>();
        internal SkinnedMeshRenderer[] Renderers;
        internal int CaptureIndex, AcquisitionPart, UploadPart;
        internal ProceduralHeadWoundWorkspace CapturedData;
        internal HeadStage Stage;
        internal HeadWoundGeometryJob Job;
        internal Mesh CapturedMesh, HeadMesh, CavityMesh;
        internal HeadCandidate CapturedCandidate;
        internal BoneWeight[] CapturedWeights;
        internal Matrix4x4 CapturedToHead;
        internal Material WoundMaterial, OwnedDecalMaterial;
        internal Material[] SourceMaterials, ResultMaterials;
        internal GameObject Replacement;
        internal SkinnedMeshRenderer Skin;
        internal MeshRenderer Cavity;
        internal bool Committed, YieldFrame;
        internal int CopiedPropertyBlocks;
        internal string LastCaptureFailure;
        internal double CaptureMilliseconds, BakeMilliseconds, AcquisitionMilliseconds;
    }

    private static void SelectHeadSources(HeadBuild build)
    {
        var state = build.State;
        if (state.Source != null)
        {
            build.Candidates.Add(new HeadCandidate { Renderer = state.SourceRenderer, NativeMesh = state.NativeMesh,
                Bones = state.Bones, Bindposes = state.Bindposes });
            build.Sources.Add(state.Source);
            build.HeadRenderers = state.HeadRenderers;
            build.Stage = HeadStage.Enqueue;
            return;
        }
        build.Renderers = build.HeadRenderers.OfType<SkinnedMeshRenderer>()
            .Where(r => RendererSource.TryGetLodLevel(r, out int level) && level == 0)
            .OrderByDescending(r => r.enabled).ToArray();
        build.Stage = HeadStage.Bake;
    }

    private static void BakeHeadCandidate(HeadBuild build)
    {
        if (build.CaptureIndex >= build.Renderers.Length) { build.Stage = HeadStage.Enqueue; return; }
        var renderer = build.Renderers[build.CaptureIndex++];
        if (renderer == null) return;
        Mesh native = renderer.sharedMesh;
        if (native == null || !native.isReadable) return;
        Mesh baked = new Mesh { name = "RPT Head Capture", indexFormat = native.indexFormat };
        long started = Stopwatch.GetTimestamp();
        try
        {
            renderer.BakeMesh(baked);
            Transform[] bones = renderer.bones;
            if (!TryBuildCurrentBindposes(bones, build.Head, out var bindposes, out build.LastCaptureFailure)) return;
            // Freeze the complete pose together with BakeMesh. Readback may run
            // in another frame; it must never sample the then-current skeleton.
            build.CapturedToHead = build.Head.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            build.CapturedWeights = native.boneWeights;
            build.CapturedCandidate = new HeadCandidate { Renderer = renderer, NativeMesh = native, Bones = bones, Bindposes = bindposes };
            build.CapturedMesh = baked;
            build.Stage = HeadStage.Acquire;
        }
        catch (Exception exception) { build.LastCaptureFailure = $"head capture failed for {renderer.name}: {exception.Message}"; }
        finally
        {
            build.BakeMilliseconds += ElapsedMilliseconds(started);
            if (build.CapturedMesh != baked) UnityEngine.Object.Destroy(baked);
            build.YieldFrame = true;
        }
    }

    private static void AcquireHeadCandidate(HeadBuild build)
    {
        long started = Stopwatch.GetTimestamp();
        bool release = false;
        try
        {
            if (build.CapturedData == null) build.CapturedData = new ProceduralHeadWoundWorkspace();
            var data = build.CapturedData;
            if (!data.AcquireFromMeshPartOnMainThread(build.CapturedMesh, build.AcquisitionPart++, out build.LastCaptureFailure))
            { release = true; return; }
            if (build.AcquisitionPart < data.SourceAcquisitionPartCount) return;
            release = true;
            data.SourceBoneWeights.Clear();
            data.SourceBoneWeights.AddRange(build.CapturedWeights);
            build.Sources.Add(new HeadWoundGeometrySource { Data = data, BoneCount = build.CapturedCandidate.Bones.Length,
                NeedsTransform = true, ToHead = build.CapturedToHead });
            build.Candidates.Add(build.CapturedCandidate);
        }
        catch (Exception exception) { release = true; build.LastCaptureFailure = $"head readback failed: {exception.Message}"; }
        finally
        {
            build.AcquisitionMilliseconds += ElapsedMilliseconds(started);
            if (release)
            {
                UnityEngine.Object.Destroy(build.CapturedMesh);
                build.CapturedMesh = null; build.CapturedCandidate = null; build.CapturedWeights = null; build.CapturedData = null;
                build.AcquisitionPart = 0;
                build.Stage = HeadStage.Bake;
            }
        }
    }

    private static void ReleaseBuild(WoundWorkItem work)
    {
        HeadBuild build = work?.Build;
        if (build != null)
        {
            if (build.Job != null) build.Job.Cancelled = true;
            if (build.CapturedMesh != null) UnityEngine.Object.Destroy(build.CapturedMesh);
            if (!build.Committed)
            {
                if (!ReferenceEquals(build.Skin, null)) Limbs.LimbDecalTextureTransfer.ReleasePreparedTexture(build.Skin);
                if (build.OwnedDecalMaterial != null) UnityEngine.Object.Destroy(build.OwnedDecalMaterial);
                if (build.Replacement != null) UnityEngine.Object.Destroy(build.Replacement);
                if (build.HeadMesh != null) UnityEngine.Object.Destroy(build.HeadMesh);
                if (build.CavityMesh != null) UnityEngine.Object.Destroy(build.CavityMesh);
            }
        }
        if (work != null) work.Build = null;
        if (ReferenceEquals(_activeWork, work)) _activeWork = null;
    }

    private static void CancelActive(CorpseWounds state = null)
    {
        if (_activeWork != null && (state == null || ReferenceEquals(_activeWork.State, state)))
        {
            LogHeadStages(_activeWork, ApplyResult.Rejected, "cancelled by corpse removal or reset");
            ReleaseBuild(_activeWork);
        }
    }
}
