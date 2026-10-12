using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Rupture.Ragdolls.Classes;

internal static partial class ShadowRagdollWorld
{
	internal enum MainThreadStage
	{
		UpdateSetup, ImpactNetwork, ImpactState, ShotImpacts, Headgear, HeadWounds,
		Blood, FracturesUpdate, Coordinator, ShadowUpdate, LimbCuts, PhysicsDiagnostics, UpdateCleanup,
		LatePhysics, VisibleShell, FracturesLate,
		AuthorityPlayback, CasingsPrepare, PoseSync, SleepAndWeaponState, PoseSnapshots,
		BloodTracking, BloodLivingStreams, BloodPreparation, BloodEmitters, ClothingSoak, BloodDecals, BloodMesh,
		DiagnosticLogging, PostAnimationVisibleShell,
		SoakInitialize, SoakPreparationPublish, SoakMarkVertices, SoakCaptureMatrices, SoakPose, SoakTransport, SoakGpuSkin, SoakGpuTransport, SoakPaint,
		SoakTextureRegistration, SoakColorMapping, SoakMeshUpload, SoakRenderTargets, SoakDrawSubmit, SoakCompositeSubmit, SoakCommandSubmit, SoakRenderTargetRelease,
		CasingReceiveAndRetire, CasingExposureRelease, CasingListenerAndCapacity, CasingAdmission, CasingMovingCoverage,
		CasingImpactSelection, CasingImpactExposure, CasingImpactCoverage, CasingQueueSubmit, CasingPosePublish, CasingAudio,
		CasingAudioSelection, CasingAudioSurface, CasingAudioPlayback, CasingCoverageRefresh,
		BloodDecalEmit, BloodDecalUpload, BloodMeshBuild, BloodMeshUpload, BloodParcelSubmit, BloodHistory, BloodClothingSupply, BloodFlow, BloodImpactPrediction,
		Count
	}

	internal enum MainThreadWork
	{
		ExternalIdleSkips, ExternalBodiesPublished, ExternalSleepingBodiesSkipped,
		SoakFields, SoakPoseVertexSlots, SoakPaintTargets, SoakColorVertices, SoakChangedTargets,
		CasingPendingEntries, CasingMovingEntries, CasingImpactEvents, CasingImpactCells, CasingImpactCandidates,
		CasingAffectedEntries, CasingPoseStates, CasingContacts,
		SoakFrontierVertices, SoakPosedVertices, SoakChangedVertices, SoakColorVisits,
		CasingAudioAttempts, CasingAudioSources, CasingCoverageRequests, CasingCoverageReused, CasingImpactQueries,
		BloodDecalEmissions, BloodDecalDeferredUploads, BloodDecalUploads, BloodMeshVertices, BloodMeshIndices, SoakCommandExecutions,
            BloodDecalPartialUploads, BloodDecalFullUploads, BloodDecalLayoutFallbacks, BloodDecalUploadRanges, BloodDecalUploadVertices, BloodDecalUploadBytes, BloodParcelSamplesVisited, BloodParcelsInstanced, BloodParcelsCpuFallback, BloodParcelUploadBytes, BloodEmitterVisits, BloodHistoryScans, BloodHistoryCopies, BloodFlowSkipped, BloodAnchorReads, BloodImpactPredictions, BloodRaycasts, BloodTraceTimer, BloodTraceMovement, BloodSamplesEmitted, BloodEffectScans, BloodEffectsVisited, SoakPoseComputedVertices, SoakGeometryCacheHits, SoakColorUploadVertices, SoakColorUploadRanges, SoakColorFullUploads, SoakColorPartialUploads, BloodPostMortemImpactHits, BloodPostMortemImpactMisses, BloodPostMortemDecalBlocked, BloodPostMortemDecals, WeaponsReleased, BloodTraceInitial, BloodReferenceParcels, BloodReusedFlightTimes, BloodImpactsQueued, BloodImpactQueueFull, BloodImpactsExpired, SoakTemporaryTargets, SoakStampDraws, SoakBlendDraws, SoakGpuSkinVertices, SoakGpuBoneUploadBytes, SoakGpuTransportVertices, SoakGpuPaintVertices, SoakGpuDispatches, SoakGpuScratchAllocations, SoakGpuHistoryCopies,
		Count
	}
	private static readonly long[] MainThreadWorkCounts = new long[(int)MainThreadWork.Count];
	private static readonly string[] MainThreadWorkNames = Enum.GetNames(typeof(MainThreadWork));
	internal static void RecordMainThreadWork(MainThreadWork work, long count)
	{
		if (_performanceLoggingWasEnabled && PerformanceLoggingEnabled) MainThreadWorkCounts[(int)work] += count;
	}

	private static readonly TimingMetric[] MainThreadStageTimings = CreateMainThreadStageTimings();
	private static readonly string[] MainThreadStageNames = Enum.GetNames(typeof(MainThreadStage));
	private static readonly string[] MainThreadMarkerNames = CreateMainThreadMarkerNames();
	private static int _mainThreadTimingGeneration;
	private static int _performanceReportPending;
	private static Exception _performanceReportFailure;
	private sealed class PerformanceReport
	{
		internal readonly TimingMetric[] Stages = CreateMainThreadStageTimings();
		internal readonly TimingMetric[] Callbacks = new TimingMetric[12];
		internal readonly TimingMetric Wait = new TimingMetric(1000);
		internal readonly long[] Work = new long[(int)MainThreadWork.Count];
		internal long Frames;
		internal int Generation;
		internal FormattableString Physics;
		internal RuptureLog Sink;
		internal PerformanceReport() { for (int i = 0; i < Callbacks.Length; i++) Callbacks[i] = new TimingMetric(); }
	}
	private static readonly PerformanceReport Report = new PerformanceReport();

	private static TimingMetric[] CreateMainThreadStageTimings()
	{
		var metrics = new TimingMetric[(int)MainThreadStage.Count];
		for (int index = 0; index < metrics.Length; index++) metrics[index] = new TimingMetric();
		return metrics;
	}

	private static string[] CreateMainThreadMarkerNames()
	{
		var names = new string[(int)MainThreadStage.Count];
		for (int index = 0; index < names.Length; index++) names[index] = "RPT.MainThread." + (MainThreadStage)index;
		return names;
	}

	// Struct scopes avoid delegates/boxing and retain exception-safe profiler pairing.
	// Names and sample buffers are allocated once; disabled tracing does no timing work.
	internal readonly struct MainThreadTrace : IDisposable
	{
		private readonly MainThreadStage _stage;
		private readonly long _started;
		private readonly int _generation;
		internal MainThreadTrace(MainThreadStage stage)
		{
			_stage = stage;
			_generation = _mainThreadTimingGeneration;
			UnityEngine.Profiling.Profiler.BeginSample(MainThreadMarkerNames[(int)stage]);
			_started = Stopwatch.GetTimestamp();
		}
		public void Dispose()
		{
			if (_started == 0L) return;
			try
			{
				if (_generation == _mainThreadTimingGeneration)
					MainThreadStageTimings[(int)_stage].RecordElapsed(_started);
			}
			finally { UnityEngine.Profiling.Profiler.EndSample(); }
		}
	}

	internal static MainThreadTrace TraceMainThread(MainThreadStage stage, bool active = true) =>
		active && _performanceLoggingWasEnabled && PerformanceLoggingEnabled ? new MainThreadTrace(stage) : default;

	private static void ResetMainThreadStageTimings()
	{
		_mainThreadTimingGeneration++;
		foreach (TimingMetric metric in MainThreadStageTimings) metric.Reset();
		Array.Clear(MainThreadWorkCounts, 0, MainThreadWorkCounts.Length);
	}

	private static void LogMainThreadStageTimings()
	{
		Exception failure = Interlocked.Exchange(ref _performanceReportFailure, null);
		if (failure != null) RupturePlugin.Log?.LogWarning("Performance report failed: " + failure.Message);
		// Exactly one reusable snapshot can be in flight. A slow disk must never
		// queue unbounded reports or make the game wait for diagnostic formatting.
		if (Interlocked.CompareExchange(ref _performanceReportPending, 1, 0) != 0) return;
		try
		{
			Report.Generation = _mainThreadTimingGeneration; Report.Frames = PluginUpdateTiming.Samples;
			Report.Sink = RupturePlugin.Log;
			for (int i = 0; i < MainThreadStageTimings.Length; i++) MainThreadStageTimings[i].CopyTo(Report.Stages[i]);
			Array.Copy(MainThreadWorkCounts, Report.Work, MainThreadWorkCounts.Length);
			Report.Physics = CapturePerformanceStatus(Report.Callbacks, Report.Wait);
			if (!ThreadPool.QueueUserWorkItem(WritePerformanceReport)) Interlocked.Exchange(ref _performanceReportPending, 0);
		}
		catch { Interlocked.Exchange(ref _performanceReportPending, 0); throw; }
	}
	private static void WritePerformanceReport(object unused)
	{
		try
		{
		WriteReportLine(Report.Physics.ToString(CultureInfo.InvariantCulture));
		LogStageRange("Update", MainThreadStage.UpdateSetup, MainThreadStage.UpdateCleanup);
		LogStageRange("LateUpdate", MainThreadStage.LatePhysics, MainThreadStage.FracturesLate);
		LogStageRange("LatePhysics children", MainThreadStage.AuthorityPlayback, MainThreadStage.PoseSnapshots);
		LogStageRange("Blood children", MainThreadStage.BloodTracking, MainThreadStage.BloodMesh);
		LogStageRange("ClothingSoak children", MainThreadStage.SoakInitialize, MainThreadStage.SoakPaint);
		LogStageRange("Clothing painting detail", MainThreadStage.SoakTextureRegistration, MainThreadStage.SoakRenderTargetRelease);
		LogStageRange("CasingsPrepare children", MainThreadStage.CasingReceiveAndRetire, MainThreadStage.CasingAudio);
		LogStageRange("Casing detail", MainThreadStage.CasingAudioSelection, MainThreadStage.CasingCoverageRefresh);
		LogStageRange("Blood rendering detail", MainThreadStage.BloodDecalEmit, MainThreadStage.BloodParcelSubmit);
		LogStageRange("Blood emitter detail", MainThreadStage.BloodHistory, MainThreadStage.BloodImpactPrediction);
		var work = new StringBuilder("RPT main thread work: traceSchema=12; optimization=coarse-soaking-sleep-aware-physics-4hz-roots; diagnosticFormatting=worker; countsSinceReset; updateFrames=");
		work.Append(Report.Frames);
		for (int index = 0; index < Report.Work.Length; index++)
			work.Append(", ").Append(MainThreadWorkNames[index]).Append('=').Append(Report.Work[index]);
		WriteReportLine(work.ToString());
		// Logging ends after this report; its newest sample appears in the next report.
		LogStageRange("outside callback totals", MainThreadStage.DiagnosticLogging, MainThreadStage.PostAnimationVisibleShell);
		}
		catch (Exception exception) { Interlocked.Exchange(ref _performanceReportFailure, exception); }
		finally { Report.Physics = null; Report.Sink = null; Interlocked.Exchange(ref _performanceReportPending, 0); }
	}
	private static void WriteReportLine(string line)
	{
		if (Report.Generation == Volatile.Read(ref _mainThreadTimingGeneration)) Report.Sink?.LogInfo(line);
	}

	private static void LogStageRange(string group, MainThreadStage first, MainThreadStage last)
	{
		var text = new StringBuilder("RPT main thread stages: ");
		text.Append(group).Append("; inclusive ms/call=last/avgSinceReset/avgRecent512/p99Recent512/maxSinceReset; callsSinceReset; extra=totalMsSinceReset,avgMsPerUpdateFrame; updateFrames=")
			.Append(Report.Frames).Append("; ");
		for (int index = (int)first; index <= (int)last; index++)
		{
			TimingMetric metric = Report.Stages[index];
			if (index != (int)first) text.Append(", ");
			text.Append(MainThreadStageNames[index]).Append(':')
				.Append(metric.LastMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append('/')
				.Append(metric.AverageMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append('/')
				.Append(metric.RecentAverageMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append('/')
				.Append(metric.P99Milliseconds().ToString("F3", CultureInfo.InvariantCulture)).Append('/')
				.Append(metric.MaximumMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append("ms/").Append(metric.Samples)
				.Append("[totalMs=").Append(metric.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
				.Append(",perUpdateFrame=").Append(metric.AveragePerFrame(Report.Frames).ToString("F6", CultureInfo.InvariantCulture)).Append(']');
		}
		WriteReportLine(text.ToString());
	}
}
