#if UNITY_EDITOR
// These isolated visual/physics fixtures run with diagnostic tracing disabled.
// Production timing arithmetic is exercised by PerformanceTimingTests; no Unity
// sample calls or allocations should contaminate the existing workload benchmark.
using System;
namespace Rupture.Ragdolls.Classes
{
    internal static partial class ShadowRagdollWorld
    {
        internal enum MainThreadStage
        {
            SoakInitialize, SoakPreparationPublish, SoakMarkVertices, SoakCaptureMatrices, SoakPose, SoakTransport, SoakGpuSkin, SoakGpuTransport, SoakPaint,
            SoakTextureRegistration, SoakColorMapping, SoakMeshUpload, SoakRenderTargets, SoakDrawSubmit, SoakCompositeSubmit, SoakCommandSubmit, SoakRenderTargetRelease,
            CasingReceiveAndRetire, CasingExposureRelease, CasingListenerAndCapacity, CasingAdmission, CasingMovingCoverage,
            CasingImpactSelection, CasingImpactExposure, CasingImpactCoverage, CasingQueueSubmit, CasingPosePublish, CasingAudio,
            CasingAudioSelection, CasingAudioSurface, CasingAudioPlayback, CasingCoverageRefresh,
            BloodDecalEmit, BloodDecalUpload, BloodMeshBuild, BloodMeshUpload, BloodParcelSubmit, BloodHistory, BloodClothingSupply, BloodFlow, BloodImpactPrediction
        }
        internal enum MainThreadWork
        {
            SoakFields, SoakPoseVertexSlots, SoakPaintTargets, SoakColorVertices, SoakChangedTargets,
            CasingPendingEntries, CasingMovingEntries, CasingImpactEvents, CasingImpactCells, CasingImpactCandidates,
            CasingAffectedEntries, CasingPoseStates, CasingContacts,
            SoakFrontierVertices, SoakPosedVertices, SoakChangedVertices, SoakColorVisits,
            CasingAudioAttempts, CasingAudioSources, CasingCoverageRequests, CasingCoverageReused, CasingImpactQueries,
            BloodDecalEmissions, BloodDecalDeferredUploads, BloodDecalUploads, BloodMeshVertices, BloodMeshIndices, SoakCommandExecutions,
            BloodDecalPartialUploads, BloodDecalFullUploads, BloodDecalLayoutFallbacks, BloodDecalUploadRanges, BloodDecalUploadVertices, BloodDecalUploadBytes, BloodParcelSamplesVisited, BloodParcelsInstanced, BloodParcelsCpuFallback, BloodParcelUploadBytes, BloodEmitterVisits, BloodHistoryScans, BloodHistoryCopies, BloodFlowSkipped, BloodAnchorReads, BloodImpactPredictions, BloodRaycasts, BloodTraceTimer, BloodTraceMovement, BloodSamplesEmitted, BloodEffectScans, BloodEffectsVisited, SoakPoseComputedVertices, SoakGeometryCacheHits, SoakColorUploadVertices, SoakColorUploadRanges, SoakColorFullUploads, SoakColorPartialUploads, BloodPostMortemImpactHits, BloodPostMortemImpactMisses, BloodPostMortemDecalBlocked, BloodPostMortemDecals, WeaponsReleased, BloodTraceInitial, BloodReferenceParcels, BloodReusedFlightTimes, BloodImpactsQueued, BloodImpactQueueFull, BloodImpactsExpired, SoakTemporaryTargets, SoakStampDraws, SoakBlendDraws, SoakGpuSkinVertices, SoakGpuBoneUploadBytes, SoakGpuTransportVertices, SoakGpuPaintVertices, SoakGpuDispatches, SoakGpuScratchAllocations, SoakGpuHistoryCopies
        }
        internal readonly struct MainThreadTrace : IDisposable { public void Dispose() { } }
        internal static MainThreadTrace TraceMainThread(MainThreadStage stage, bool active = true) => default;
        private static readonly long[] WorkCounts = new long[Enum.GetValues(typeof(MainThreadWork)).Length];
        internal static void RecordMainThreadWork(MainThreadWork work, long count) { WorkCounts[(int)work] += count; }
        internal static long Work(MainThreadWork work) => WorkCounts[(int)work];
        internal static void ResetWork() => Array.Clear(WorkCounts, 0, WorkCounts.Length);
    }
}
#endif
