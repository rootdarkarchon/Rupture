using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Ragdolls.Classes;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal static partial class HeadWoundSystem
{
    private static readonly string[] HeadStageMarkers = Enum.GetNames(typeof(HeadStage)).Select(s => "RPT.HeadWound." + s).ToArray();
    private static bool _warmResourcesPending;
    private static long _warmResourceTicks, _warmResourceMaxTicks;
    private static int _warmResourceCalls;

    internal static void PrepareResources()
    {
        _warmResourceTicks = _warmResourceMaxTicks = 0;
        _warmResourceCalls = 0;
        _warmResourcesPending = true;
    }

    private static void WarmHeadResources()
    {
        if (!_warmResourcesPending || MultiplayerRuntime.Current.IsHeadless || !ProceduralCutFrameBudget.CanRunStage) return;
        long started = Stopwatch.GetTimestamp();
        UnityEngine.Profiling.Profiler.BeginSample(HeadStageMarkers[(int)HeadStage.Resources]);
        try
        {
            if (HeadWoundMaterialProvider.TryGet(out _, out _)) _warmResourcesPending = false;
        }
        finally
        {
            long ticks = Stopwatch.GetTimestamp() - started;
            _warmResourceTicks += ticks;
            _warmResourceMaxTicks = Math.Max(_warmResourceMaxTicks, ticks);
            _warmResourceCalls++;
            ProceduralCutFrameBudget.RecordStage(ticks);
            UnityEngine.Profiling.Profiler.EndSample();
            if (RupturePlugin.Log.IsDebugEnabled && !_warmResourcesPending)
                RupturePlugin.Log.LogInfo($"RPT head wound resource warmup: totalMainMs={_warmResourceTicks * 1000d / Stopwatch.Frequency:F3}, maxAtomicMainMs={_warmResourceMaxTicks * 1000d / Stopwatch.Frequency:F3}, calls={_warmResourceCalls}.");
            if (RupturePlugin.Log.IsDebugEnabled && ticks * 1000d / Stopwatch.Frequency > ProceduralCutFrameBudget.BudgetMilliseconds)
                RupturePlugin.Log.LogInfo($"RPT head wound resource warmup overrun: atomicMainMs={ticks * 1000d / Stopwatch.Frequency:F3}, budgetMs={ProceduralCutFrameBudget.BudgetMilliseconds:F3}.");
        }
    }

    private static ApplyResult AdvanceHeadStages(WoundWorkItem work, string key, out string failure)
    {
        failure = null;
        if (!ProceduralCutFrameBudget.CanRunStage) return ApplyResult.Waiting;
        if (work.Build == null) work.Build = new HeadBuild { State = work.State, Revision = work.State.Revision };
        HeadBuild build = work.Build;
        build.YieldFrame = false;
        while (ProceduralCutFrameBudget.CanRunStage)
        {
            HeadStage stage = build.Stage;
            int progress = build.CaptureIndex + build.AcquisitionPart + build.UploadPart;
            // Pending jobs are polled without a stage, allocation or worker wait.
            if (stage == HeadStage.WaitForGeometry && !build.Job.Completed) return ApplyResult.Waiting;
            long started = Stopwatch.GetTimestamp();
            UnityEngine.Profiling.Profiler.BeginSample(HeadStageMarkers[(int)stage]);
            ApplyResult result;
            try { result = TryApply(work, key, out failure); }
            finally
            {
                long ticks = Stopwatch.GetTimestamp() - started;
                ProceduralCutFrameBudget.RecordStage(ticks);
                work.StageTicks[(int)stage] += ticks;
                work.StageMaxTicks[(int)stage] = Math.Max(work.StageMaxTicks[(int)stage], ticks);
                work.StageCalls[(int)stage]++;
                if (stage >= HeadStage.Select && stage <= HeadStage.Enqueue) build.CaptureMilliseconds += ticks * 1000d / Stopwatch.Frequency;
                UnityEngine.Profiling.Profiler.EndSample();
                if (RupturePlugin.Log.IsDebugEnabled && ticks * 1000d / Stopwatch.Frequency > ProceduralCutFrameBudget.BudgetMilliseconds)
                    RupturePlugin.Log.LogInfo($"RPT head wound budget overrun: stage={stage}, atomicMainMs={ticks * 1000d / Stopwatch.Frequency:F3}, budgetMs={ProceduralCutFrameBudget.BudgetMilliseconds:F3}.");
            }
            if (result != ApplyResult.Waiting || work.Build == null || build.YieldFrame ||
                build.Stage == stage && progress == build.CaptureIndex + build.AcquisitionPart + build.UploadPart) return result;
        }
        return ApplyResult.Waiting;
    }

    private static void LogHeadStages(WoundWorkItem work, ApplyResult result, string failure)
    {
        if (!RupturePlugin.Log.IsDebugEnabled) return;
        var text = new StringBuilder($"RPT head wound transaction: profile={work.Packet.ProfileId}, ordinal={work.Packet.WoundOrdinal}, status={result}, stages=totalMs/maxAtomicMs/calls");
        for (int i = 0; i < (int)HeadStage.Count; i++)
            if (work.StageCalls[i] > 0) text.Append($", {(HeadStage)i}={work.StageTicks[i] * 1000d / Stopwatch.Frequency:F3}/{work.StageMaxTicks[i] * 1000d / Stopwatch.Frequency:F3}/{work.StageCalls[i]}");
        text.Append($", geometryWorkerMs={work.Build?.Job?.WorkerMilliseconds ?? 0d:F3}, endToEndMs={(Time.realtimeSinceStartup - work.QueuedAt) * 1000f:F3}, reason={failure ?? "none"}.");
        RupturePlugin.Log.LogInfo(text.ToString());
    }

    private static ApplyResult TryApply(WoundWorkItem work, string key, out string failure)
    {
        failure = null;
        HeadBuild build = work.Build;
        CorpseWounds state = Corpses[key];
        if (build.Stage > HeadStage.Select)
        {
            Transform currentHead = work.Target.Head?.Bone;
            if (currentHead == null)
            {
                var retained = CorpseHeadContext.Capture(work.Target.Player, work.Target.PlayerBody);
                retained?.BindIdentity(work.Packet.ProfileId, work.Packet.PlayerId, work.Packet.DeathSequence);
                currentHead = retained?.Bone;
            }
            if (!ReferenceEquals(build.State, state) || build.Revision != state.Revision ||
                build.Head != currentHead || build.Head == null ||
                build.VisualRoot != work.Target.VisualRoot || state.Order.Next != work.Packet.WoundOrdinal)
            { failure = "head geometry result belongs to a retired corpse or wound revision"; return ApplyResult.Rejected; }
        }
        if (build.Stage > HeadStage.WaitForGeometry)
        {
            if (build.Job.Cancelled || !build.Job.Succeeded)
            { failure = build.Job.Failure ?? "head geometry was cancelled"; return ApplyResult.Rejected; }
            HeadCandidate candidate = build.Candidates[build.Job.SourceIndex];
            if (candidate.Renderer == null || candidate.Renderer.sharedMesh != candidate.NativeMesh ||
                candidate.Bones.Any(bone => bone == null) || !candidate.Renderer.bones.SequenceEqual(candidate.Bones))
            {
                failure = "captured head renderer or skeleton changed before adoption";
                if (work.StaleRecaptures++ >= MaximumStaleRecaptures) return ApplyResult.Rejected;
                ReleaseBuild(work);
                return ApplyResult.Waiting;
            }
            if (state.Lifetime == null && build.Stage >= HeadStage.PrepareCavity &&
                (build.Replacement == null || build.Skin == null ||
                 build.Stage >= HeadStage.TransferDecals && build.Cavity == null))
            { failure = "prepared head renderers were destroyed before adoption"; return ApplyResult.Rejected; }
        }

        switch (build.Stage)
        {
            case HeadStage.Resources:
                if (!HeadWoundMaterialProvider.TryGet(out build.WoundMaterial, out failure)) return ApplyResult.Waiting;
                build.Stage = HeadStage.Select;
                break;
            case HeadStage.Select:
                Transform head = work.Target.Head?.Bone;
                if (head == null)
                {
                    var retained = CorpseHeadContext.Capture(work.Target.Player, work.Target.PlayerBody);
                    retained?.BindIdentity(work.Packet.ProfileId, work.Packet.PlayerId, work.Packet.DeathSequence);
                    head = retained?.Bone;
                }
                if (head == null || !TryGetHeadRenderers(work.Target.PlayerBody, out var renderers))
                { failure = "the retained head skin or bone is unavailable"; return ApplyResult.Waiting; }
                build.Head = head; build.VisualRoot = work.Target.VisualRoot; build.HeadRenderers = renderers;
                SelectHeadSources(build);
                break;
            case HeadStage.Bake:
                BakeHeadCandidate(build);
                break;
            case HeadStage.Acquire:
                AcquireHeadCandidate(build);
                break;
            case HeadStage.Enqueue:
                var sources = build.Sources;
                if (sources.Count == 0)
                { failure = build.LastCaptureFailure ?? "no readable LOD0 head renderer was identified; lower head LODs are intentionally excluded"; return ApplyResult.Rejected; }
                build.Job = ManagedGeometryWorker.Enqueue(new HeadWoundGeometryJob
                {
                    Sources = sources.ToArray(), Previous = state.Descriptors.ToArray(),
                    SuppliedEntry = work.Packet.EntryLocal, Direction = work.Packet.DirectionLocal,
                    EntryRadius = work.Packet.EntryRadius, ExitRadius = work.Packet.ExitRadius,
                    MaximumDepth = work.Packet.ConeDepth, Seed = work.Packet.IrregularitySeed
                });
                build.Stage = HeadStage.WaitForGeometry;
                build.YieldFrame = true;
                break;
            case HeadStage.WaitForGeometry:
                if (!build.Job.Completed) return ApplyResult.Waiting;
                if (!build.Job.Succeeded || build.Job.Cancelled)
                { failure = build.Job.Failure ?? "head geometry was cancelled"; return ApplyResult.Rejected; }
                build.Stage = HeadStage.UploadHead;
                break;
            case HeadStage.UploadHead:
                if (build.HeadMesh == null) build.HeadMesh = new Mesh { name = $"RPT Filtered Head Wound {work.Packet.ProfileId}" };
                build.Job.Workspace.ApplyFilteredIndicesPartOnMainThread(build.HeadMesh, build.UploadPart++, build.Job.HeadBounds);
                if (build.UploadPart == build.Job.Workspace.HeadUploadPartCount)
                { build.Stage = HeadStage.UploadCavity; build.YieldFrame = true; }
                break;
            case HeadStage.UploadCavity:
                build.CavityMesh = build.Job.Workspace.CreateCavityMeshOnMainThread($"RPT Head Wound Cavity {work.Packet.ProfileId}", build.Job.CavityBounds);
                var source = build.Candidates[build.Job.SourceIndex];
                build.SourceMaterials = source.Renderer.sharedMaterials;
                build.ResultMaterials = NormalizeMaterials(build.SourceMaterials, build.Job.Workspace.SubMeshCount);
                if (build.HeadMesh.vertexCount != build.Job.Workspace.SourceVertices.Count ||
                    build.CavityMesh.vertexCount != build.Job.Workspace.CavityVertices.Count ||
                    build.ResultMaterials.Length != build.HeadMesh.subMeshCount)
                { failure = "head mesh upload did not preserve the validated layout"; return ApplyResult.Rejected; }
                build.HeadMesh.bindposes = source.Bindposes;
                build.Stage = state.Lifetime != null ? HeadStage.Commit : HeadStage.PrepareSkin;
                build.YieldFrame = true;
                break;
            case HeadStage.PrepareSkin:
                PrepareHeadSkin(build);
                build.Stage = HeadStage.PrepareCavity;
                break;
            case HeadStage.PrepareCavity:
                PrepareHeadCavity(build);
                build.Stage = HeadStage.TransferDecals;
                build.YieldFrame = true;
                break;
            case HeadStage.TransferDecals:
                var renderer = build.Candidates[build.Job.SourceIndex].Renderer;
                Material before = build.Skin.sharedMaterial;
                LimbDecalTextureTransfer.CopyExisting(renderer, build.Skin, out _);
                if (build.Skin.sharedMaterial != before) build.OwnedDecalMaterial = build.Skin.sharedMaterial;
                build.Stage = HeadStage.Commit;
                build.YieldFrame = true;
                break;
            case HeadStage.Commit:
                CommitHeadWound(work, key);
                return ApplyResult.Applied;
        }
        return ApplyResult.Waiting;
    }

    private static void PrepareHeadSkin(HeadBuild build)
    {
        HeadCandidate source = build.Candidates[build.Job.SourceIndex];
        build.Replacement = new GameObject("RPT Procedural Head Wound");
        // No lifetime component or renderer may become active during preparation.
        build.Replacement.SetActive(false);
        build.Replacement.layer = source.Renderer.gameObject.layer;
        build.Replacement.transform.SetParent(build.Head, worldPositionStays: false);
        build.Replacement.transform.localPosition = Vector3.zero;
        build.Replacement.transform.localRotation = Quaternion.identity;
        build.Replacement.transform.localScale = Vector3.one;
        build.Skin = build.Replacement.AddComponent<SkinnedMeshRenderer>();
        build.Skin.bones = source.Bones;
        HeadWoundSkinning.SetMesh(build.Skin, build.HeadMesh, build.Head);
        build.Skin.sharedMaterials = build.ResultMaterials;
        build.Skin.quality = source.Renderer.quality;
        build.Skin.updateWhenOffscreen = source.Renderer.updateWhenOffscreen;
        CopyRendererSettings(source.Renderer, build.Skin);
        build.CopiedPropertyBlocks = RendererSource.CopyPropertyBlocks(source.Renderer, build.Skin,
            Mathf.Min(build.SourceMaterials?.Length ?? 0, build.ResultMaterials.Length));
        build.Skin.enabled = true;
        build.Skin.forceRenderingOff = false;
    }

    private static void PrepareHeadCavity(HeadBuild build)
    {
        var cavity = new GameObject("RPT Procedural Wound Cavity");
        // Parent before adding renderers; the parent is inactive until commit.
        cavity.transform.SetParent(build.Replacement.transform, worldPositionStays: false);
        cavity.layer = build.Replacement.layer;
        cavity.transform.localPosition = Vector3.zero;
        cavity.transform.localRotation = Quaternion.identity;
        cavity.transform.localScale = Vector3.one;
        cavity.AddComponent<MeshFilter>().sharedMesh = build.CavityMesh;
        build.Cavity = cavity.AddComponent<MeshRenderer>();
        build.Cavity.sharedMaterial = build.WoundMaterial;
        CopyRendererSettings(build.Candidates[build.Job.SourceIndex].Renderer, build.Cavity);
        build.Cavity.enabled = true;
        build.Cavity.forceRenderingOff = false;
    }

    private static void CommitHeadWound(WoundWorkItem work, string key)
    {
        HeadBuild build = work.Build;
        CorpseWounds state = build.State;
        HeadWoundGeometryJob job = build.Job;
        HeadCandidate source = build.Candidates[job.SourceIndex];
        if (state.Lifetime != null) state.Lifetime.ReplaceMeshes(build.HeadMesh, build.CavityMesh);
        else
        {
            bool[] originalRendererStates = build.HeadRenderers.Select(r => r != null && r.enabled).ToArray();
            var lifetime = build.Replacement.AddComponent<HeadWoundLifetime>();
            lifetime.Initialize(key, new[] { build.HeadMesh, build.CavityMesh }, build.HeadRenderers, originalRendererStates,
                new Renderer[] { build.Skin, build.Cavity }, build.OwnedDecalMaterial);
            state.Lifetime = lifetime;
            ActiveWounds.Add(lifetime);
            build.Replacement.SetActive(true);
            foreach (Renderer renderer in build.HeadRenderers) if (renderer != null) renderer.enabled = false;
        }
        build.Committed = true;
        state.Source = job.Source; state.SourceRenderer = source.Renderer; state.NativeMesh = source.NativeMesh;
        state.HeadRenderers = build.HeadRenderers; state.Bones = source.Bones; state.Bindposes = source.Bindposes;
        state.Descriptors.Add(job.Descriptor);
        state.Revision++;
        PulsatingBloodStreamSystem.StartPostMortemHeadWound(work.Target, work.Packet, build.Head, job.Entry, job.Exit,
            job.ResolvedDirection, job.TerminalSurfaceResolved);
        if (work.Packet.IsLethal) PulsatingBloodStreamSystem.ResolveHeadExit(work.Packet, build.Head, job.Exit,
            job.ResolvedDirection, job.TerminalSurfaceResolved, job.Entry);
        PaintVanillaBloodDecals(state.Lifetime, build.Head.TransformPoint(job.Entry), build.Head.TransformPoint(job.Exit),
            build.Head.TransformDirection(job.ResolvedDirection).normalized, job.TerminalSurfaceResolved);
    }
}
