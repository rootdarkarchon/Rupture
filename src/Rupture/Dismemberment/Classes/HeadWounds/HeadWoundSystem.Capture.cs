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

    private sealed class HeadBuild
    {
        internal CorpseWounds State;
        internal int Revision;
        internal Transform Head, VisualRoot;
        internal Renderer[] HeadRenderers;
        internal HeadCandidate[] Candidates;
        internal HeadWoundGeometryJob Job;
        internal double CaptureMilliseconds, BakeMilliseconds, AcquisitionMilliseconds;
    }

    private static bool TryCaptureHead(WoundWorkItem work, CorpseWounds state, Transform head,
        Renderer[] renderers, out HeadBuild build, out string failure)
    {
        failure = null;
        build = new HeadBuild { State = state, Revision = state.Revision, Head = head,
            VisualRoot = work.Target.VisualRoot, HeadRenderers = renderers };
        long started = Stopwatch.GetTimestamp();
        var candidates = new List<HeadCandidate>();
        var sources = new List<HeadWoundGeometrySource>();
        if (state.Source != null)
        {
            candidates.Add(new HeadCandidate { Renderer = state.SourceRenderer, NativeMesh = state.NativeMesh,
                Bones = state.Bones, Bindposes = state.Bindposes });
            sources.Add(state.Source);
            build.HeadRenderers = state.HeadRenderers;
        }
        else
        {
            // Capture every eligible LOD0 candidate before the worker chooses the
            // same closest trajectory/nearest-surface winner as the former path.
            foreach (var renderer in renderers.OfType<SkinnedMeshRenderer>()
                .Where(r => RendererSource.TryGetLodLevel(r, out int level) && level == 0)
                .OrderByDescending(r => r.enabled))
            {
                Mesh native = renderer.sharedMesh;
                if (native == null || !native.isReadable) continue;
                Mesh baked = new Mesh { name = "RPT Head Capture", indexFormat = native.indexFormat };
                try
                {
                    long bakeStarted = Stopwatch.GetTimestamp();
                    renderer.BakeMesh(baked);
                    build.BakeMilliseconds += ElapsedMilliseconds(bakeStarted);
                    Transform[] bones = renderer.bones;
                    if (!TryBuildCurrentBindposes(bones, head, out var bindposes, out failure)) continue;
                    long acquisitionStarted = Stopwatch.GetTimestamp();
                    var data = new ProceduralHeadWoundWorkspace();
                    if (!data.AcquireFromMeshOnMainThread(baked, out failure)) continue;
                    data.SourceBoneWeights.Clear();
                    data.SourceBoneWeights.AddRange(native.boneWeights);
                    sources.Add(new HeadWoundGeometrySource { Data = data, BoneCount = bones.Length,
                        NeedsTransform = true, ToHead = head.worldToLocalMatrix * renderer.transform.localToWorldMatrix });
                    candidates.Add(new HeadCandidate { Renderer = renderer, NativeMesh = native, Bones = bones, Bindposes = bindposes });
                    build.AcquisitionMilliseconds += ElapsedMilliseconds(acquisitionStarted);
                }
                catch (Exception exception) { failure = $"head capture failed for {renderer.name}: {exception.Message}"; }
                finally { UnityEngine.Object.Destroy(baked); }
            }
        }
        if (sources.Count == 0)
        {
            failure = failure ?? "no readable LOD0 head renderer was identified; lower head LODs are intentionally excluded";
            return false;
        }
        build.Candidates = candidates.ToArray();
        build.CaptureMilliseconds = ElapsedMilliseconds(started);
        build.Job = ManagedGeometryWorker.Enqueue(new HeadWoundGeometryJob
        {
            Sources = sources.ToArray(), Previous = state.Descriptors.ToArray(),
            SuppliedEntry = work.Packet.EntryLocal, Direction = work.Packet.DirectionLocal,
            EntryRadius = work.Packet.EntryRadius, ExitRadius = work.Packet.ExitRadius,
            MaximumDepth = work.Packet.ConeDepth, Seed = work.Packet.IrregularitySeed
        });
        return true;
    }

    private static void ReleaseBuild(WoundWorkItem work)
    {
        if (work?.Build != null) work.Build.Job.Cancelled = true;
        if (work != null) work.Build = null;
        if (ReferenceEquals(_activeWork, work)) _activeWork = null;
    }

    private static void CancelActive(CorpseWounds state = null)
    {
        if (_activeWork != null && (state == null || ReferenceEquals(_activeWork.State, state))) ReleaseBuild(_activeWork);
    }
}
