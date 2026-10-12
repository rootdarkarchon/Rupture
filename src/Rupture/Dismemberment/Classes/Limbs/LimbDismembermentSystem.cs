using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;
using Rupture.Impacts;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class LimbDismembermentSystem
{
	private readonly struct ProfileScope : IDisposable
	{
		internal ProfileScope(string name) => UnityEngine.Profiling.Profiler.BeginSample(name);
		public void Dispose() => UnityEngine.Profiling.Profiler.EndSample();
	}

	internal enum SeverRequestResult
	{
		Rejected,
		AlreadyPendingOrSevered,
		Accepted,
		WaitingForGeometry,
	}

	internal enum SeverCompletionStatus
	{
		Committed,
		Failed,
		Cancelled
	}

	internal readonly struct SeverCompletion
	{
		internal readonly SeverCompletionStatus Status;
		internal readonly LimbSeverLevel Level;
		internal readonly string Failure;

		internal SeverCompletion(SeverCompletionStatus status, LimbSeverLevel level, string failure)
		{
			Status = status;
			Level = level;
			Failure = failure;
		}
	}

	private enum SeverStage
	{
		Prepare,
		Materialize,
		BakeAndColliders,
		WaitForUnityColliderBake,
		RegisterAndCommit
	}

	private sealed class PendingSever
	{
		internal string ReservationKey;
		internal CorpseInteractionTarget Target;
		internal EBodyPart BodyPart;
		internal Vector3 Direction;
		internal bool InitialDeathSpawn;
		internal uint EventSequence;
		internal uint IrregularitySeed;
		internal bool ReplicatedObserver;
		internal bool Living;
		internal LimbSeverLevel Level;
		internal CutFrame Frame;
		internal SeverStage Stage;
		internal Action<SeverCompletion> Completion;
		internal long EnqueuedAt;
		internal long PrepareTicks;
		internal long GibTicks;
		internal long RegisterTicks;
		internal long QueueWaitTicks;
		internal CutState State;
		internal SeverRecord Sever;
		internal Material WoundMaterial;
		internal List<PreparedRenderer> Prepared;
		internal Collider[] SourceColliders;
		internal GameObject GibRoot;
		internal bool RegistrationStarted;
		internal bool RegistrationCompleted;
		internal string RegistrationFailure;
		internal bool Committed;
		internal SeverCompletionStatus FinalStatus;
		internal GeometryJob GeometryJob;
		internal SourceRendererState[] GeometrySources;
		internal int MaterializeIndex;
		internal double GeometryWorkerMilliseconds;
		internal ColliderBakeJob ColliderBakeJob;
		internal MeshCollider[] PendingGibColliders;
		internal Mesh[] PendingGibColliderMeshes;
		internal Bounds PendingGibColliderBounds;
		internal ExternalTransientConvexShape[] PendingExternalShapes;
		internal double ColliderBakeWorkerMilliseconds;
	}

	private sealed class DeferredGibCleanup
	{
		internal GameObject Root;
		internal ColliderBakeJob Job;
	}

	private sealed class CutFrame
	{
		internal Transform ProximalStart;
		internal Transform Joint;
		internal Transform DistalEnd;
		internal Transform ProximalRoot;
		internal Transform DistalRoot;
		internal Vector3 ProximalPoint;
		internal Vector3 DistalPoint;
		internal Vector3 ProximalAxis;
		internal Vector3 DistalAxis;
	}

	private sealed class CutState
	{
		internal string Key;
		internal GameObject Host;
		internal LimbCutLifetime Lifetime;
		internal readonly List<SourceRendererState> Sources = new List<SourceRendererState>();
		internal readonly List<SeverRecord> Cuts = new List<SeverRecord>();
		internal readonly List<Renderer> NativeRenderers = new List<Renderer>();
		internal readonly HashSet<EBodyPart> Severed = new HashSet<EBodyPart>();
	}

	private sealed class SourceRendererState
	{
		internal SkinnedMeshRenderer Native;
		internal Mesh ImmutableMesh;
		internal SkinnedMeshRenderer SocketRenderer;
		internal SkinnedMeshRenderer DebugRenderer;
		internal Mesh BodyMesh;
		internal Mesh SocketMesh;
		internal Mesh DebugMesh;
		internal int LodLevel;
	}

	private sealed class SeverRecord
	{
		internal EBodyPart BodyPart;
		internal LimbSeverLevel Level;
		internal CutFrame Frame;
		internal Transform LimbRoot;
		internal uint Seed;
		internal float FractionOffset;
		internal readonly HashSet<SourceRendererState> PrimarySources = new HashSet<SourceRendererState>();
		internal LimbClothingProfiles.Core BodyClothCore, GibClothCore;

		internal ProceduralLimbCutGeometry.CutSpec ToSpec(SourceRendererState source) => new ProceduralLimbCutGeometry.CutSpec
		{
			ProximalStart = Frame.ProximalStart,
			Joint = Frame.Joint,
			DistalEnd = Frame.DistalEnd,
			LimbRoot = LimbRoot,
			Level = Level,
			IsLeg = BodyPart == EBodyPart.LeftLeg || BodyPart == EBodyPart.RightLeg,
			Seed = Seed,
			FractionOffset = FractionOffset,
			SharedBodyClothCore = BodyClothCore,
			SharedGibClothCore = GibClothCore,
			IncludeAnatomicalDetails = PrimarySources.Contains(source)
		};
	}

	private readonly struct LocalDecalSample
	{
		internal readonly Vector3 Point;
		internal readonly Vector3 Normal;
		internal LocalDecalSample(Vector3 point, Vector3 normal) { Point = point; Normal = normal; }
	}

	private sealed class PreparedRenderer
	{
		internal SourceRendererState Source;
		internal Mesh BodyMesh;
		internal Mesh SocketMesh;
		internal Mesh DistalMesh;
		internal Mesh GibMesh;
		internal Mesh DebugMesh;
		internal SkinnedMeshRenderer BodyRenderer;
		internal SkinnedMeshRenderer SocketRenderer;
		internal SkinnedMeshRenderer DistalRenderer;
		internal MeshRenderer GibRenderer;
		internal SkinnedMeshRenderer DebugRenderer;
		internal int NewBodyLoopCount;
		internal int GibLoopCount;
		internal int NewSelectedTriangleCount;
		internal int DebugCandidateTriangleCount;
		internal int DebugStrongSelectedTriangleCount;
		internal int DebugConnectedWeakTriangleCount;
		internal int DebugCutBoundaryTriangleCount;
		internal float DebugCapsuleRadiusWorld;
		internal float PrimaryLoopScore;
		internal ProceduralLimbWoundGeometry.DecalSample[] BodyDecalSamples;
		internal ProceduralLimbWoundGeometry.DecalSample[] GibDecalSamples;
		internal ProceduralLimbWoundGeometry.EmissionAnchor BodyStreamAnchor;
		internal ProceduralLimbWoundGeometry.EmissionAnchor GibStreamAnchor;
		internal bool HasBodyStreamAnchor;
		internal bool HasGibStreamAnchor;
		internal readonly List<LocalDecalSample> GibLocalDecalSamples = new List<LocalDecalSample>();
		internal int FleshVertexCount;
		internal int BoneCount;
		internal int BoneVertexCount;
		internal int FlapCount;
		internal int CordCount;
		internal float BodyPlateauProtrusionWorld;
		internal float GibPlateauProtrusionWorld;
		internal float MaximumAdornmentProtrusionWorld;
		internal bool DecorationsClosed;
		internal int OriginalSubmeshCount;
		internal uint WoundSeed;
		internal bool IncludeInGib;
		internal LimbDecalTransferResult GibDecalTransfer;
		internal LimbDecalAudit DecalAudit;
		internal bool SourceCacheHit;
		internal double SourceCaptureMilliseconds;
		internal double ClassificationMilliseconds;
		internal double ClippingMilliseconds;
		internal double LoopAndWoundMilliseconds;
		internal double BufferConstructionMilliseconds;
	}

	private sealed class BakedGibData
	{
		internal PreparedRenderer Item;
		internal Mesh Mesh;
		internal Vector3[] Vertices;
		internal Vector3[] Normals;
		internal Vector4[] Tangents;
		internal int[][] ExteriorTriangles;
	}

	private static readonly Dictionary<string, CutState> States = new Dictionary<string, CutState>(StringComparer.Ordinal);
	private static readonly Queue<PendingSever> Pending = new Queue<PendingSever>();
	private static readonly HashSet<int> LivingBodies = new HashSet<int>();
	private static readonly HashSet<string> Reservations = new HashSet<string>(StringComparer.Ordinal);
	private static readonly List<DeferredGibCleanup> DeferredGibCleanups = new List<DeferredGibCleanup>();
	private static PendingSever _active;
	private static int _generation;
	private static int _acceptedRequests;
	private static int _duplicateRequests;
	private static int _rejectedRequests;
	private static int _committedCuts;
	private static int _failedCuts;
	private static double _maximumAtomicMainMilliseconds;
	private static double _cumulativeCutMainMilliseconds;
	private static double _maximumGeometryWorkerMilliseconds;
	private static double _cumulativeGeometryWorkerMilliseconds;
	private static double _maximumColliderBakeWorkerMilliseconds;
	private static double _cumulativeColliderBakeWorkerMilliseconds;
	private static Material[] _debugMaterials;
	private static LimbCutCaptureSession _clothingCaptureSession;
	private static readonly HashSet<string> ReportedClothingSkips = new HashSet<string>();

	internal static void Reset()
	{
		_generation++;
		_clothingCaptureSession = null;
		LimbCutCaptureQueue.Reset();
		ReportedClothingSkips.Clear();
		ProcessDeferredWorkerCleanup();
		CancelPending(_active);
		_active = null;
		while (Pending.Count > 0) CancelPending(Pending.Dequeue());
		Reservations.Clear();
		_acceptedRequests = _duplicateRequests = _rejectedRequests = _committedCuts = _failedCuts = 0;
		_maximumAtomicMainMilliseconds = _cumulativeCutMainMilliseconds = 0d;
		_maximumGeometryWorkerMilliseconds = _cumulativeGeometryWorkerMilliseconds = 0d;
		_maximumColliderBakeWorkerMilliseconds = _cumulativeColliderBakeWorkerMilliseconds = 0d;
		foreach (CutState state in States.Values)
			if (state?.Host != null) UnityEngine.Object.Destroy(state.Host);
		States.Clear();
		LivingBodies.Clear();
		ProceduralLimbCutGeometry.ResetCaches();
		DestroyDebugMaterials();
		LimbSeverMaterialProvider.Reset();
	}

	internal static SeverRequestResult RequestSever(CorpseInteractionTarget target, EBodyPart bodyPart, Vector3 impactPoint,
		Vector3 direction, bool initialDeathSpawn, uint eventSequence, uint irregularitySeed,
		LimbSeverLevel? forcedLevel, bool replicatedObserver, Action<SeverCompletion> completion,
		out LimbSeverLevel selectedLevel)
	{
		long requestStarted = PerformanceLoggingEnabled ? Stopwatch.GetTimestamp() : 0L;
		selectedLevel = forcedLevel ?? default;
		if (!IsLimb(bodyPart) || target.VisualRoot == null || target.PlayerBody == null || eventSequence == 0)
		{
			_rejectedRequests++;
			return SeverRequestResult.Rejected;
		}
		if (Health.BotSeverStateService.IsLivingCut(target))
		{
			int bodyId = target.PlayerBody.GetInstanceID();
			if (LivingBodies.Add(bodyId)) ObjectLifetime.Bind(target.PlayerBody.gameObject, "living-limb-key", () => LivingBodies.Remove(bodyId));
		}
		string key = CreateKey(target);
		string reservationKey = key + ":" + bodyPart;
		if (Reservations.Contains(reservationKey) ||
			States.TryGetValue(key, out CutState existing) && existing.Severed.Contains(bodyPart))
		{
			_duplicateRequests++;
			return SeverRequestResult.AlreadyPendingOrSevered;
		}
		if (!TryResolveCutFrame(target, bodyPart, out CutFrame frame, out string frameFailure))
		{
			if (replicatedObserver) return SeverRequestResult.WaitingForGeometry;
			RupturePlugin.Log.LogWarning($"Procedural limb cut rejected for {bodyPart}: {frameFailure}");
			_rejectedRequests++;
			return SeverRequestResult.Rejected;
		}
		selectedLevel = forcedLevel ?? LimbSeverMath.Closest(impactPoint, frame.ProximalPoint, frame.DistalPoint);
		if (MultiplayerRuntime.Current.IsHeadless)
		{
			if (Health.BotSeverStateService.IsLivingCut(target) && !Health.BotSeverStateService.BeforeCutCommit(target, bodyPart))
				return SeverRequestResult.Rejected;
			CutState headlessState = GetOrCreateState(target, key, createVisualHost: false);
			headlessState.Severed.Add(bodyPart);
			RagdollCoordinator.DisableLimbColliders(target, bodyPart, selectedLevel);
			completion?.Invoke(new SeverCompletion(SeverCompletionStatus.Committed, selectedLevel, null));
			_acceptedRequests++;
			_committedCuts++;
			return SeverRequestResult.Accepted;
		}

		Reservations.Add(reservationKey);
		_acceptedRequests++;
		Pending.Enqueue(new PendingSever
		{
			ReservationKey = reservationKey,
			Target = target,
			BodyPart = bodyPart,
			Direction = direction,
			InitialDeathSpawn = initialDeathSpawn,
			EventSequence = eventSequence,
			IrregularitySeed = irregularitySeed,
			ReplicatedObserver = replicatedObserver,
			Living = Health.BotSeverStateService.IsLivingCut(target),
			Level = selectedLevel,
			Frame = frame,
			Stage = SeverStage.Prepare,
			Completion = completion,
			EnqueuedAt = Stopwatch.GetTimestamp()
		});
		if (PerformanceLoggingEnabled)
			RupturePlugin.Log.LogInfo($"RPT limb cut request: bodyPart={bodyPart}, level={selectedLevel}, enqueueMs={ElapsedMilliseconds(requestStarted):F3}, queueDepth={Pending.Count + (_active != null ? 1 : 0)}.");
		return SeverRequestResult.Accepted;
	}

	internal static void Update()
	{
		while (LimbCutCaptureQueue.TryDequeueReport(out string captureReport))
			RupturePlugin.Log.LogInfo("RPT limb clothing case: " + captureReport);
		ProcessDeferredWorkerCleanup();
		if (_active == null && Pending.Count > 0)
		{
			_active = Pending.Dequeue();
			_active.QueueWaitTicks = Stopwatch.GetTimestamp() - _active.EnqueuedAt;
		}
		if (_active == null) return;
		PendingSever request = _active;
		if (request.Living && request.Target.Player?.HealthController?.IsAlive != true)
		{
			CancelPending(request);
			_active = null;
			return;
		}
		if (request.Target.PlayerBody == null || request.Target.VisualRoot == null)
		{
			CancelPending(request);
			_active = null;
			return;
		}
		float budget = Mathf.Clamp(RuptureConfiguration.ProceduralLimbCutMainThreadBudget?.Value ?? 2f, 0.5f, 8f);
		long frameStarted = Stopwatch.GetTimestamp();
		while (ReferenceEquals(_active, request))
		{
			long started = Stopwatch.GetTimestamp();
			SeverStage executedStage = request.Stage;
			int materializeIndex = request.MaterializeIndex;
			UnityEngine.Profiling.Profiler.BeginSample("RPT.LimbCut." + executedStage);
			try
			{
				switch (executedStage)
				{
					case SeverStage.Prepare:
						ProcessPrepare(request);
						request.PrepareTicks += Stopwatch.GetTimestamp() - started;
						break;
					case SeverStage.Materialize:
						ProcessMaterialize(request);
						request.PrepareTicks += Stopwatch.GetTimestamp() - started;
						break;
					case SeverStage.BakeAndColliders:
						ProcessBakeAndColliders(request);
						request.GibTicks += Stopwatch.GetTimestamp() - started;
						break;
					case SeverStage.WaitForUnityColliderBake:
						ProcessWaitForUnityColliderBake(request);
						request.GibTicks += Stopwatch.GetTimestamp() - started;
						break;
					case SeverStage.RegisterAndCommit:
						ProcessRegisterAndCommit(request);
						request.RegisterTicks += Stopwatch.GetTimestamp() - started;
						break;
				}
			}
			catch (Exception exception)
			{
				if (request.Committed)
				{
					RupturePlugin.Log.LogError($"Limb sever committed, but follow-up work failed: {exception}");
					Complete(request, SeverCompletionStatus.Committed, null);
				}
				else Fail(request, $"{executedStage} threw {exception.GetType().Name}: {exception.Message}");
			}
			finally
			{
				UnityEngine.Profiling.Profiler.EndSample();
			}

			double atomicMilliseconds = ElapsedMilliseconds(started);
			_cumulativeCutMainMilliseconds += atomicMilliseconds;
			_maximumAtomicMainMilliseconds = Math.Max(_maximumAtomicMainMilliseconds, atomicMilliseconds);
			if (PerformanceLoggingEnabled && atomicMilliseconds > budget)
				RupturePlugin.Log.LogInfo($"RPT limb cut budget overrun: stage={executedStage}, bodyPart={request.BodyPart}, atomicMainMs={atomicMilliseconds:F3}, budgetMs={budget:F3}, queueDepth={Pending.Count + (_active != null ? 1 : 0)}. No additional cut stage runs this frame.");
			if (!ReferenceEquals(_active, request)) break;
			bool madeProgress = request.Stage != executedStage ||
				executedStage == SeverStage.Materialize && request.MaterializeIndex != materializeIndex;
			if (!madeProgress || ElapsedMilliseconds(frameStarted) >= budget) break;
		}
		if (PerformanceLoggingEnabled && !ReferenceEquals(_active, request))
			RupturePlugin.Log.LogInfo($"RPT limb cut transaction: bodyPart={request.BodyPart}, status={request.FinalStatus}, queueWaitMs={TicksToMilliseconds(request.QueueWaitTicks):F3}, " +
				$"prepareMainMs={TicksToMilliseconds(request.PrepareTicks):F3}, geometryWorkerMs={request.GeometryWorkerMilliseconds:F3}, " +
				$"gibMainMs={TicksToMilliseconds(request.GibTicks):F3}, colliderBakeWorkerMs={request.ColliderBakeWorkerMilliseconds:F3}, " +
				$"registerCommitMainMs={TicksToMilliseconds(request.RegisterTicks):F3}, " +
				$"endToEndMs={ElapsedMilliseconds(request.EnqueuedAt):F3}, queueDepth={Pending.Count}.");
	}

	internal static void CancelLivingCuts(Player player)
	{
		bool Matches(PendingSever request) => request != null && request.Living && ReferenceEquals(request.Target.Player, player);
		if (Matches(_active)) { CancelPending(_active); _active = null; }
		int count = Pending.Count;
		for (int i = 0; i < count; i++)
		{
			PendingSever request = Pending.Dequeue();
			if (Matches(request)) CancelPending(request); else Pending.Enqueue(request);
		}
	}

	private static void ProcessPrepare(PendingSever request)
	{
		if (request.GeometryJob == null)
		{
			if (!LimbSeverMaterialProvider.TryGet(out Material capturedMaterial, out string materialFailure))
			{
				Fail(request, "bloody cap material is unavailable: " + materialFailure);
				return;
			}
			CutState capturedState = GetOrCreateState(request.Target, CreateKey(request.Target), createVisualHost: true);
			Transform distalRoot = request.Level == LimbSeverLevel.Proximal ? request.Frame.ProximalRoot : request.Frame.DistalRoot;
			SeverRecord capturedSever = new SeverRecord
			{
				BodyPart = request.BodyPart,
				Level = request.Level,
				Frame = request.Frame,
				LimbRoot = distalRoot,
				Seed = request.IrregularitySeed
			};
			if (!TryCaptureGeometryWork(capturedState, capturedSever,
				out GeometryBuildWork[] geometryWork, out SourceRendererState[] geometrySources,
				out string captureFailure))
			{
				Fail(request, captureFailure, capturedState);
				return;
			}
			request.State = capturedState;
			request.Sever = capturedSever;
			request.WoundMaterial = capturedMaterial;
			request.GeometrySources = geometrySources;
			foreach (GeometryBuildWork item in geometryWork)
				item.RequestIdentity = request.ReservationKey + ":" + request.IrregularitySeed;
			request.GeometryJob = LimbGeometryWorker.Enqueue(geometryWork, DebugOverlayEnabled);
			return;
		}
		if (!request.GeometryJob.Completed) return;
		foreach (GeometryBuildWork item in request.GeometryJob.Work)
			if (item.Diagnostic != null)
			{
				RupturePlugin.Log.LogInfo("RPT limb clothing case: " + item.Diagnostic);
				item.Diagnostic = null;
			}
		request.GeometryWorkerMilliseconds = request.GeometryJob.WorkerMilliseconds;
		_maximumGeometryWorkerMilliseconds = Math.Max(_maximumGeometryWorkerMilliseconds,
			request.GeometryWorkerMilliseconds);
		_cumulativeGeometryWorkerMilliseconds += request.GeometryWorkerMilliseconds;
		if (!request.GeometryJob.Succeeded)
		{
			Fail(request, "managed limb geometry failed: " + request.GeometryJob.Failure,
				request.State);
			return;
		}
		request.Sever.FractionOffset = request.GeometryJob.WinningOffset;
		request.Level = request.Sever.Level = request.GeometryJob.Work[0].Built.EffectiveLevel;
		request.Sever.LimbRoot = request.Level == LimbSeverLevel.Proximal ? request.Frame.ProximalRoot : request.Frame.DistalRoot;
		request.Prepared = new List<PreparedRenderer>(request.GeometryJob.Work.Length);
		foreach (var item in request.GeometryJob.Work.OrderBy(item => item.LodLevel).Where(item => item.IsPrimary))
		{
			request.Sever.BodyClothCore ??= item.Built.BodyClothCore;
			request.Sever.GibClothCore ??= item.Built.GibClothCore;
		}
		for (int index = 0; index < request.GeometryJob.Work.Length; index++)
			if (request.GeometryJob.Work[index].IsPrimary)
				request.Sever.PrimarySources.Add(request.GeometrySources[index]);
		request.MaterializeIndex = 0;
		request.Stage = SeverStage.Materialize;
	}

	private static void ProcessMaterialize(PendingSever request)
	{
		if (request.MaterializeIndex >= request.GeometryJob.Work.Length)
		{
			request.SourceColliders = RagdollCoordinator.GetLimbColliders(request.Target,
				request.BodyPart, request.Level);
			request.Stage = SeverStage.BakeAndColliders;
			return;
		}
		int index = request.MaterializeIndex;
		GeometryBuildWork work = request.GeometryJob.Work[index];
		SourceRendererState sourceState = request.GeometrySources[index];
		if (work?.Built == null || sourceState?.Native == null)
		{
			Fail(request, $"managed geometry output {index} lost its source renderer", request.State);
			return;
		}
		if (!work.Built.TryMaterialize(out string uploadFailure))
		{
			Fail(request, $"LOD{sourceState.LodLevel} renderer {sourceState.Native.name} {uploadFailure}",
				request.State);
			return;
		}
		PreparedRenderer prepared = new PreparedRenderer
		{
			Source = sourceState,
			WoundSeed = request.Sever.Seed,
			IncludeInGib = work.BuildDistal
		};
		PopulatePreparedGeometry(prepared, work.Built);
		request.Prepared.Add(prepared);
		if (!TryCreatePreparedRenderers(request.State, request.Sever, request.WoundMaterial,
			prepared, out string rendererFailure))
		{
			Fail(request, rendererFailure, request.State);
			return;
		}
		LogGeometryPhases(prepared, work, request.GeometryJob);
		request.MaterializeIndex++;
		if (request.MaterializeIndex >= request.GeometryJob.Work.Length)
		{
			request.SourceColliders = RagdollCoordinator.GetLimbColliders(request.Target,
				request.BodyPart, request.Level);
			request.Stage = SeverStage.BakeAndColliders;
		}
	}

	private static void ProcessBakeAndColliders(PendingSever request)
	{
		CurrentCutPose(request.Frame, request.Level, out Vector3 cutPoint, out Vector3 axis, request.Sever.FractionOffset);
		GameObject gibRoot;
		string failure;
		bool gibCreated;
		UnityEngine.Profiling.Profiler.BeginSample("RPT.LimbCut.PosedGibBakeAndCollider");
		try
		{
			gibCreated = TryCreateGib(request.SourceColliders, request.Prepared, request.Frame, request.BodyPart,
				request.Level, cutPoint, axis, request.WoundMaterial, out gibRoot,
				out MeshCollider[] pendingColliders, out Mesh[] colliderMeshes, out Bounds colliderBounds,
				out ExternalTransientConvexShape[] externalShapes, out failure);
			if (gibCreated)
			{
				request.PendingGibColliders = pendingColliders;
				request.PendingGibColliderMeshes = colliderMeshes;
				request.PendingGibColliderBounds = colliderBounds;
				request.PendingExternalShapes = externalShapes;
			}
		}
		finally
		{
			UnityEngine.Profiling.Profiler.EndSample();
		}
		if (!gibCreated)
		{
			Fail(request, failure, request.State);
			return;
		}
		request.GibRoot = gibRoot;
		request.ColliderBakeJob = LimbGeometryWorker.EnqueueColliderBake(request.PendingGibColliderMeshes);
		request.Stage = SeverStage.WaitForUnityColliderBake;
	}

	private static void ProcessWaitForUnityColliderBake(PendingSever request)
	{
		if (request.ColliderBakeJob == null || !request.ColliderBakeJob.Completed) return;
		request.ColliderBakeWorkerMilliseconds = request.ColliderBakeJob.WorkerMilliseconds;
		_maximumColliderBakeWorkerMilliseconds = Math.Max(_maximumColliderBakeWorkerMilliseconds,
			request.ColliderBakeWorkerMilliseconds);
		_cumulativeColliderBakeWorkerMilliseconds += request.ColliderBakeWorkerMilliseconds;
		if (!request.ColliderBakeJob.Succeeded)
		{
			Fail(request, "Unity convex proxy bake failed on the cut worker: " + request.ColliderBakeJob.Failure,
				request.State);
			return;
		}
		if (!TryAttachBakedGibColliders(request.GibRoot, request.PendingGibColliders,
			request.PendingGibColliderMeshes, request.PendingGibColliderBounds, out string failure))
		{
			Fail(request, failure, request.State);
			return;
		}
		CurrentCutPose(request.Frame, request.Level, out Vector3 cutPoint, out _, request.Sever.FractionOffset);
		request.Stage = SeverStage.RegisterAndCommit;
		BeginExternalRegistration(request, cutPoint);
	}

	private static void ProcessRegisterAndCommit(PendingSever request)
	{
		CurrentCutPose(request.Frame, request.Level, out Vector3 cutPoint, out Vector3 axis, request.Sever.FractionOffset);
		if (!request.RegistrationStarted) BeginExternalRegistration(request, cutPoint);
		if (!request.RegistrationCompleted) return;
		if (!string.IsNullOrEmpty(request.RegistrationFailure))
		{
			Fail(request, request.RegistrationFailure, request.State);
			return;
		}
		Transform fallbackBodyAnchor = request.Level == LimbSeverLevel.Proximal ? request.Frame.ProximalStart : request.Frame.Joint;
		ResolveProceduralStreamAnchors(request.Prepared, fallbackBodyAnchor, cutPoint, axis, request.GibRoot.transform,
			out Transform bodyStreamAnchor, out Vector3 bodyStreamPointWorld, out Vector3 gibStreamPointLocal);
		if (DebugOverlayEnabled) CaptureLimbDecalAudits(request.Prepared, request.BodyPart, request.Level);
		UnityEngine.Profiling.Profiler.BeginSample("RPT.LimbCut.RendererLodCommitDecalsCollidersBlood");
		try
		{
			if (request.Living && !Health.BotSeverStateService.BeforeCutCommit(request.Target, request.BodyPart))
			{
				Fail(request, "living ragdoll admission failed", request.State);
				return;
			}
			CommitPrepared(request);
			request.State.Severed.Add(request.BodyPart);
			RagdollCoordinator.DisableLimbColliders(request.Target, request.BodyPart, request.Level);
			request.State.Lifetime.StartCoroutine(PaintNativeBloodDeferred(request.Target.PlayerBody, request.Prepared));
			if (request.Living) PulsatingBloodStreamSystem.StartLivingSever(request.Target, bodyStreamAnchor,
				bodyStreamPointWorld, axis, request.BodyPart, request.EventSequence);
			else PulsatingBloodStreamSystem.StartProceduralDismembermentStreams(request.Target,
				bodyStreamAnchor, bodyStreamPointWorld, request.GibRoot.transform,
				request.GibRoot.transform.TransformPoint(gibStreamPointLocal), axis, request.BodyPart,
				request.EventSequence, request.InitialDeathSpawn);
		}
		finally
		{
			UnityEngine.Profiling.Profiler.EndSample();
		}
		LogCommitted(request, cutPoint);
		Complete(request, SeverCompletionStatus.Committed, null);
	}

	private static void BeginExternalRegistration(PendingSever request, Vector3 cutPoint)
	{
		if (request.RegistrationStarted) return;
		request.RegistrationStarted = true;
		if (request.Living)
		{
			if (!ShadowRagdollWorld.RegisterLivingGib(request.GibRoot, cutPoint, request.Direction, request.PendingExternalShapes,
				(succeeded, failure) => { request.RegistrationFailure = succeeded ? null : failure; request.RegistrationCompleted = true; }))
			{ request.RegistrationFailure ??= "living gib registration rejected"; request.RegistrationCompleted = true; }
			return;
		}
		if (!ShadowRagdollWorld.RegisterLocalDetachedGib(request.Target.ProfileId, request.GibRoot, cutPoint,
			request.Direction, request.InitialDeathSpawn, request.EventSequence, request.PendingExternalShapes,
			(succeeded, failure) =>
			{
				request.RegistrationFailure = succeeded ? null :
					"the generated gib could not be registered in ShadowRagdollWorld: " + failure;
				request.RegistrationCompleted = true;
			}))
		{
			request.RegistrationFailure = "the generated gib could not begin registration in ShadowRagdollWorld";
			request.RegistrationCompleted = true;
		}
	}

	private static void CurrentCutPose(CutFrame frame, LimbSeverLevel level, out Vector3 cutPoint, out Vector3 axis, float fractionOffset = 0f)
	{
		Transform start = level == LimbSeverLevel.Proximal ? frame.ProximalStart : frame.Joint;
		Transform end = level == LimbSeverLevel.Proximal ? frame.Joint : frame.DistalEnd;
		cutPoint = LimbSeverMath.Candidate(start.position, end.position, level, fractionOffset);
		axis = (end.position - start.position).normalized;
	}

	private static void Fail(PendingSever request, string failure, CutState state = null)
	{
		if (request.Committed) throw new InvalidOperationException("A committed sever cannot be rejected.");
		if (request.Prepared != null) DestroyPrepared(request.Prepared);
		if (request.Living) ShadowRagdollWorld.CancelLivingGib(request.GibRoot);
		if (request.GibRoot != null) UnityEngine.Object.Destroy(request.GibRoot);
		if (request.ReplicatedObserver)
			ObserverVisualFailure(request.Target, request.BodyPart, failure);
		else RupturePlugin.Log.LogWarning($"Procedural limb cut transaction rejected for {request.BodyPart}: {failure}");
		Complete(request, SeverCompletionStatus.Failed, failure);
	}

	private static void Complete(PendingSever request, SeverCompletionStatus status, string failure)
	{
		request.FinalStatus = status;
		if (request.GeometryJob != null)
			RupturePlugin.Log.LogInfo($"RPT limb clothing completion: request='{request.ReservationKey}:{request.IrregularitySeed}', " +
				$"part={request.BodyPart}, level={request.Level}, status={status}, reason='{failure ?? "none"}'");
		if (status == SeverCompletionStatus.Committed) _committedCuts++;
		else if (status == SeverCompletionStatus.Failed) _failedCuts++;
		Reservations.Remove(request.ReservationKey);
		try { request.Completion?.Invoke(new SeverCompletion(status, request.Level, failure)); }
		catch (Exception exception) { RupturePlugin.Log.LogError($"Limb sever completion callback failed: {exception}"); }
		if (ReferenceEquals(_active, request)) _active = null;
	}

	private static void CancelPending(PendingSever request)
	{
		if (request == null) return;
		if (request.Committed)
		{
			Reservations.Remove(request.ReservationKey);
			return;
		}
		if (request.GeometryJob != null) request.GeometryJob.Cancelled = true;
		if (request.ColliderBakeJob != null) request.ColliderBakeJob.Cancelled = true;
		if (request.Living) ShadowRagdollWorld.CancelLivingGib(request.GibRoot);
		if (request.Prepared != null) DestroyPrepared(request.Prepared);
		if (request.GibRoot != null && request.ColliderBakeJob != null && !request.ColliderBakeJob.Completed)
		{
			// Physics.BakeMesh receives only instance IDs and may already be executing on the
			// dedicated worker. Keep the owning GameObject and meshes alive until that call
			// returns; Unity object destruction remains a main-thread operation.
			DeferredGibCleanups.Add(new DeferredGibCleanup
			{
				Root = request.GibRoot,
				Job = request.ColliderBakeJob
			});
			request.GibRoot = null;
		}
		if (request.GibRoot != null) UnityEngine.Object.Destroy(request.GibRoot);
		Reservations.Remove(request.ReservationKey);
		try { request.Completion?.Invoke(new SeverCompletion(SeverCompletionStatus.Cancelled, request.Level, "raid reset")); }
		catch { }
	}

	private static void ProcessDeferredWorkerCleanup()
	{
		for (int index = DeferredGibCleanups.Count - 1; index >= 0; index--)
		{
			DeferredGibCleanup cleanup = DeferredGibCleanups[index];
			if (cleanup?.Job != null && !cleanup.Job.Completed) continue;
			if (cleanup?.Root != null) UnityEngine.Object.Destroy(cleanup.Root);
			DeferredGibCleanups.RemoveAt(index);
		}
	}

	private static double ElapsedMilliseconds(long started) => TicksToMilliseconds(Stopwatch.GetTimestamp() - started);
	private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
	private static bool PerformanceLoggingEnabled => ShadowRagdollWorld.PerformanceLoggingEnabled;
	internal static int QueueDepth => Pending.Count + (_active != null ? 1 : 0);
	internal static string PerformanceSnapshot =>
		$"requests={_acceptedRequests + _duplicateRequests + _rejectedRequests}, accepted={_acceptedRequests}, deduplicated={_duplicateRequests}, rejected={_rejectedRequests}, failed={_failedCuts}, committed={_committedCuts}, queueDepth={QueueDepth}, " +
		$"maxAtomicMainMs={_maximumAtomicMainMilliseconds:F3}, cumulativeCutMainMs={_cumulativeCutMainMilliseconds:F3}, " +
		$"maxGeometryWorkerMs={_maximumGeometryWorkerMilliseconds:F3}, cumulativeGeometryWorkerMs={_cumulativeGeometryWorkerMilliseconds:F3}, " +
		$"maxColliderBakeWorkerMs={_maximumColliderBakeWorkerMilliseconds:F3}, cumulativeColliderBakeWorkerMs={_cumulativeColliderBakeWorkerMilliseconds:F3}";

	private static void LogCommitted(PendingSever request, Vector3 cutPoint)
	{
		if (!RupturePlugin.Log.IsDebugEnabled) return;
		List<PreparedRenderer> prepared = request.Prepared;
		Collider[] sourceColliders = request.SourceColliders ?? Array.Empty<Collider>();
		GameObject gibRoot = request.GibRoot;
		EBodyPart bodyPart = request.BodyPart;
		LimbSeverLevel selectedLevel = request.Level;
		Vector3 axis = selectedLevel == LimbSeverLevel.Proximal ? request.Frame.ProximalAxis : request.Frame.DistalAxis;
		RupturePlugin.Log.LogInfo($"Procedural limb sever committed: bodyPart={bodyPart}, level={selectedLevel}, cut={cutPoint}, " +
			$"renderers={prepared.Count}, lodTiers={prepared.Select(item => item.Source.LodLevel).Distinct().Count()}, " +
			$"sourceTriangles={prepared.Sum(item => item.NewSelectedTriangleCount)}, " +
			$"bodyLoops={prepared.Sum(item => item.NewBodyLoopCount)}, gibLoops={prepared.Sum(item => item.GibLoopCount)}, " +
			$"fleshVertices={prepared.Sum(item => item.FleshVertexCount)}, bones={prepared.Sum(item => item.BoneCount)}, " +
			$"boneVertices={prepared.Sum(item => item.BoneVertexCount)}, flaps={prepared.Sum(item => item.FlapCount)}, " +
			$"cords={prepared.Sum(item => item.CordCount)}, maxAdornment={prepared.Max(item => item.MaximumAdornmentProtrusionWorld):F4}m, " +
			$"gibCenter={gibRoot.transform.position}, removedColliders={sourceColliders.Length}, localGib=environment-only.");
		if (DebugOverlayEnabled)
		{
			RupturePlugin.Log.LogInfo($"RPT limb cut debug: bodyPart={bodyPart}, level={selectedLevel}, " +
				$"cutWorld={cutPoint}, axisWorld={axis}, missingBand={ProceduralLimbCutGeometry.MissingBandWidthWorld:F4}m, " +
				$"candidates={prepared.Sum(item => item.DebugCandidateTriangleCount)}, " +
				$"selectedStrongGreen={prepared.Sum(item => item.DebugStrongSelectedTriangleCount)}, " +
				$"selectedConnectivityRed={prepared.Sum(item => item.DebugConnectedWeakTriangleCount)}, " +
				$"cutPlaneCyan={prepared.Sum(item => item.DebugCutBoundaryTriangleCount)}, " +
				$"capsuleRadiusMax={prepared.Max(item => item.DebugCapsuleRadiusWorld):F4}m. " +
				"Legend: green=strong target-limb weights, red=connectivity-only selection, " +
				"yellow=rejected capsule candidate, cyan=cut-plane intersection, magenta=exact seam, " +
				"white=global primary loop, purple=mound rings, blue=bone roots, orange=decal samples.");
		}
	}

	private static void ObserverVisualFailure(CorpseInteractionTarget target, EBodyPart bodyPart, string failure)
	{
		// Failed visuals are not a completed local sever. Keep the intact limb hittable.
		RupturePlugin.Log.LogWarning($"Replicated procedural limb visuals failed: profile={target.ProfileId}, sequence={target.DeathSequence}, part={bodyPart}; visible limb and colliders preserved. {failure}");
	}

	internal static void ExpireObserverSever(CorpseInteractionTarget target, EBodyPart bodyPart, LimbSeverLevel level)
	{
		if (target.PlayerBody != null)
			ObserverVisualFailure(target, bodyPart, "authoritative sever geometry was unavailable before the retry deadline; " +
				(target.Limbs?.Describe(bodyPart) ?? "no retained limb context"));
	}

	private static bool TryCaptureGeometryWork(CutState state, SeverRecord newCut,
		out GeometryBuildWork[] captured, out SourceRendererState[] capturedSources, out string failure)
	{
		captured = null;
		capturedSources = null;
		failure = null;
		List<SeverRecord> allCuts = new List<SeverRecord>(state.Cuts.Count + 1);
		allCuts.AddRange(state.Cuts);
		allCuts.Add(newCut);
		List<GeometryBuildWork> work = new List<GeometryBuildWork>();
		List<SourceRendererState> sources = new List<SourceRendererState>();
		bool captureClothing = RuptureConfiguration.ProceduralLimbClothingCapture?.Value == true;
		if (captureClothing && _clothingCaptureSession == null)
			_clothingCaptureSession = new LimbCutCaptureSession(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath,
				"diagnostics", "Rupture", "LimbCuts", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
		foreach (SourceRendererState sourceState in state.Sources)
		{
			SkinnedMeshRenderer source = sourceState?.Native;
			if (source == null) continue;
			Mesh immutable = sourceState.ImmutableMesh != null ? sourceState.ImmutableMesh : source.sharedMesh;
			if (!RendererSupportsEitherCut(source, newCut))
			{
				if (captureClothing && RendererHasWeightedBone(source, newCut.LimbRoot))
				{
					string identity = $"{source.name}:{source.sharedMesh?.name}:{sourceState.LodLevel}:{newCut.BodyPart}:{newCut.Level}";
					if (ReportedClothingSkips.Add(identity))
						RupturePlugin.Log.LogWarning($"RPT limb clothing skipped: {identity}; target limb has weights but selected segment bones are missing. " +
							$"palette=[{string.Join(",", source.bones.Select(bone => bone?.name ?? "null"))}]");
				}
				continue;
			}
			if (!ProceduralLimbCutGeometry.IsSourceReadable(immutable, out string readableFailure))
			{
				string identity = $"unreadable:{immutable?.GetInstanceID()}";
				if (ReportedClothingSkips.Add(identity))
					RupturePlugin.Log.LogWarning($"RPT limb clothing classified Unreadable: request='{state.Key}', part={newCut.BodyPart}, renderer='{source.name}', mesh='{immutable?.name}', meshId={immutable?.GetInstanceID()}, lod={sourceState.LodLevel}, runtimeReadable={immutable != null && immutable.isReadable}, retainedSource={sourceState.ImmutableMesh != null}; {readableFailure}. Cutting and retries skipped.");
				continue;
			}
			if (!RendererHasWeightedBone(source, newCut.Frame.ProximalRoot)) continue;
			if (!TryEnsureImmutableSource(state, sourceState, out string immutableFailure))
			{
				failure = $"immutable source for affected renderer {source.name} is unavailable: {immutableFailure}";
				return false;
			}
			List<SeverRecord> relevantCuts = allCuts.Where(cut => cut == newCut || RendererSupportsEitherCut(source, cut) &&
				RendererHasWeightedBone(source, cut.LimbRoot, sourceState.ImmutableMesh)).ToList();
			int newCutIndex = relevantCuts.IndexOf(newCut);
			if (newCutIndex < 0) continue;
			List<ProceduralLimbCutGeometry.CutSpec> specs = relevantCuts
				.Select(cut => cut.ToSpec(sourceState)).ToList();
			foreach (var spec in specs) SupplyMissingBindFrames(state, sourceState, spec);
			long captureStarted = Stopwatch.GetTimestamp();
			if (!ProceduralLimbCutGeometry.TryCaptureClassificationWork(sourceState.ImmutableMesh,
				source.transform, source.bones, specs,
				out ProceduralLimbCutGeometry.ClassificationWork capturedWork, out string captureFailure))
			{
				failure = $"renderer {source.name} classification capture failed: {captureFailure}";
				return false;
			}
			capturedWork.CaptureEnabled = captureClothing;
			work.Add(new GeometryBuildWork
			{
				RendererName = source.name,
				BodyPart = newCut.BodyPart.ToString(),
				RequestIdentity = $"{sourceState.Native.GetInstanceID()}:{newCut.BodyPart}:{newCut.Seed}",
				CaptureSession = captureClothing ? _clothingCaptureSession : null,
				Classification = capturedWork,
				LodLevel = sourceState.LodLevel,
				NewCutIndex = newCutIndex,
				BuildDistal = sourceState.LodLevel == 0,
				CaptureMainMilliseconds = ElapsedMilliseconds(captureStarted)
			});
			sources.Add(sourceState);
		}
		if (work.Count == 0)
		{
			failure = "no affected readable body LOD renderer was found";
			return false;
		}
		captured = work.ToArray();
		capturedSources = sources.ToArray();
		return true;
	}

	private static bool TryCreatePreparedRenderers(CutState state, SeverRecord newCut, Material woundMaterial,
		PreparedRenderer item, out string failure)
	{
		failure = null;
		SkinnedMeshRenderer source = item?.Source?.Native;
		if (state?.Host == null || source == null || item.BodyMesh == null)
		{
			failure = "the prepared renderer transaction is incomplete";
			return false;
		}
		try
		{
			if (item.SocketMesh != null)
			{
				item.SocketRenderer = CreateSkinnedRenderer(state.Host.transform, source, item.SocketMesh,
					new[] { woundMaterial }, "_RPT_BloodySocket");
				LimbSeverMaterialProvider.ApplyVariation(item.SocketRenderer, 0, newCut.Seed, gibSide: false);
			}
			if (item.IncludeInGib)
			{
				item.DistalRenderer = CreateSkinnedRenderer(state.Host.transform, source, item.DistalMesh,
					AppendMaterial(source.sharedMaterials, woundMaterial), "_RPT_DistalBakeSource");
				LimbSeverMaterialProvider.ApplyVariation(item.DistalRenderer, source.sharedMaterials.Length,
					newCut.Seed, gibSide: true);
			}
			if (item.DebugMesh != null)
			{
				if (TryGetDebugMaterials(out Material[] debugMaterials, out string debugFailure))
					item.DebugRenderer = CreateDebugRenderer(state.Host.transform, source, item.DebugMesh, debugMaterials);
				else RupturePlugin.Log.LogWarning($"RPT limb cut debug overlay material unavailable: {debugFailure}");
			}
			if (DebugOverlayEnabled)
				RupturePlugin.Log.LogInfo($"RPT limb cut debug renderer: renderer={source.name}, mesh={item.Source.ImmutableMesh.name}, " +
					$"vertices={item.Source.ImmutableMesh.vertexCount}, sourceTriangles={CountTriangles(item.Source.ImmutableMesh)}, " +
					$"candidates={item.DebugCandidateTriangleCount}, selectedStrongGreen={item.DebugStrongSelectedTriangleCount}, " +
					$"selectedConnectivityRed={item.DebugConnectedWeakTriangleCount}, cutPlaneCyan={item.DebugCutBoundaryTriangleCount}, " +
					$"lod={item.Source.LodLevel}, primary={newCut.PrimarySources.Contains(item.Source)}, " +
					$"gibSource={item.IncludeInGib}, capsuleRadius={item.DebugCapsuleRadiusWorld:F4}m.");
			return true;
		}
		catch (Exception exception)
		{
			failure = $"renderer {source.name} transaction creation failed: {exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	private static void LogGeometryPhases(PreparedRenderer item, GeometryBuildWork work, GeometryJob job)
	{
		if (!PerformanceLoggingEnabled) return;
		RupturePlugin.Log.LogInfo($"RPT limb geometry phases: renderer={item.Source.Native.name}, lod={item.Source.LodLevel}, cacheHit={item.SourceCacheHit}, " +
			$"captureMainMs={work.CaptureMainMilliseconds:F3}, classificationWorkerMs={item.ClassificationMilliseconds:F3}, " +
			$"classificationWorkerTotalMs={job.ClassificationMilliseconds:F3}, " +
			$"analysisWorkerTotalMs={job.AnalysisMilliseconds:F3}, clippingMs={item.ClippingMilliseconds:F3}, " +
			$"loopsWoundsMs={item.LoopAndWoundMilliseconds:F3}, buffersBlendShapesMs={item.BufferConstructionMilliseconds:F3}, " +
			$"sourceVertices={item.Source.ImmutableMesh.vertexCount}, sourceTriangles={CountTriangles(item.Source.ImmutableMesh)}, " +
			$"sourceSubmeshes={item.Source.ImmutableMesh.subMeshCount}, blendShapes={item.Source.ImmutableMesh.blendShapeCount}, " +
			$"bodyVertices={item.BodyMesh?.vertexCount ?? 0}, bodyTriangles={CountTriangles(item.BodyMesh)}, " +
			$"socketVertices={item.SocketMesh?.vertexCount ?? 0}, distalBuilt={item.DistalMesh != null}, distalVertices={item.DistalMesh?.vertexCount ?? 0}.");
	}

	private static bool TryEnsureImmutableSource(CutState state, SourceRendererState source, out string failure)
	{
		failure = null;
		if (source?.ImmutableMesh != null)
		{
			if (source.ImmutableMesh.isReadable) return true;
			failure = "the cached immutable mesh is no longer readable";
			return false;
		}
		Mesh nativeMesh = source?.Native?.sharedMesh;
		if (nativeMesh == null || !nativeMesh.isReadable)
		{
			failure = nativeMesh == null ? "the native renderer has no mesh" : "the native mesh is not readable";
			return false;
		}
		// Retain the original shared asset as the immutable identity. Runtime commits
		// replace only renderer.sharedMesh; the raid-lifetime managed snapshot owns all
		// copied channels and therefore needs no cloned Unity Mesh.
		source.ImmutableMesh = nativeMesh;
		return true;
	}

	private static void PopulatePreparedGeometry(PreparedRenderer item, ProceduralLimbCutGeometry.Result result)
	{
		item.BodyMesh = result.Body;
		item.SocketMesh = result.BodySocket;
		item.DistalMesh = result.NewDistal;
		item.DebugMesh = result.DebugOverlay;
		item.NewBodyLoopCount = result.NewBodyLoopCount;
		item.GibLoopCount = result.GibLoopCount;
		item.NewSelectedTriangleCount = result.NewSelectedTriangleCount;
		item.DebugCandidateTriangleCount = result.DebugCandidateTriangleCount;
		item.DebugStrongSelectedTriangleCount = result.DebugStrongSelectedTriangleCount;
		item.DebugConnectedWeakTriangleCount = result.DebugConnectedWeakTriangleCount;
		item.DebugCutBoundaryTriangleCount = result.DebugCutBoundaryTriangleCount;
		item.DebugCapsuleRadiusWorld = result.DebugCapsuleRadiusWorld;
		item.PrimaryLoopScore = result.PrimaryLoopScore;
		item.BodyDecalSamples = result.BodyDecalSamples ?? Array.Empty<ProceduralLimbWoundGeometry.DecalSample>();
		item.GibDecalSamples = result.GibDecalSamples ?? Array.Empty<ProceduralLimbWoundGeometry.DecalSample>();
		item.BodyStreamAnchor = result.BodyStreamAnchor;
		item.GibStreamAnchor = result.GibStreamAnchor;
		item.HasBodyStreamAnchor = result.HasBodyStreamAnchor;
		item.HasGibStreamAnchor = result.HasGibStreamAnchor;
		item.FleshVertexCount = result.FleshVertexCount;
		item.BoneCount = result.BoneCount;
		item.BoneVertexCount = result.BoneVertexCount;
		item.FlapCount = result.FlapCount;
		item.CordCount = result.CordCount;
		item.BodyPlateauProtrusionWorld = result.BodyPlateauProtrusionWorld;
		item.GibPlateauProtrusionWorld = result.GibPlateauProtrusionWorld;
		item.MaximumAdornmentProtrusionWorld = result.MaximumAdornmentProtrusionWorld;
		item.DecorationsClosed = result.DecorationsClosed;
		item.OriginalSubmeshCount = item.Source.ImmutableMesh.subMeshCount;
		item.SourceCacheHit = result.SourceCacheHit;
		item.SourceCaptureMilliseconds = result.SourceCaptureMilliseconds;
		item.ClassificationMilliseconds = result.ClassificationMilliseconds;
		item.ClippingMilliseconds = result.ClippingMilliseconds;
		item.LoopAndWoundMilliseconds = result.LoopAndWoundMilliseconds;
		item.BufferConstructionMilliseconds = result.BufferConstructionMilliseconds;
	}

	private static SkinnedMeshRenderer CreateSkinnedRenderer(Transform host, SkinnedMeshRenderer source,
		Mesh mesh, Material[] materials, string suffix)
	{
		GameObject replacement = new GameObject(source.name + suffix);
		replacement.layer = source.gameObject.layer;
		replacement.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
		replacement.transform.localScale = source.transform.lossyScale;
		replacement.transform.SetParent(host, worldPositionStays: true);
		SkinnedMeshRenderer renderer = replacement.AddComponent<SkinnedMeshRenderer>();
		renderer.sharedMesh = mesh;
		renderer.bones = source.bones;
		renderer.rootBone = source.rootBone;
		renderer.localBounds = source.localBounds;
		renderer.quality = source.quality;
		renderer.updateWhenOffscreen = source.updateWhenOffscreen;
		renderer.skinnedMotionVectors = source.skinnedMotionVectors;
		renderer.sharedMaterials = materials;
		CopyRendererSettings(source, renderer);
		RendererSource.CopyPropertyBlocks(source, renderer, Mathf.Min(source.sharedMaterials.Length, materials.Length));
		RendererSource.CopyBlendShapeWeights(source, renderer);
		renderer.enabled = false;
		return renderer;
	}

	private static SkinnedMeshRenderer CreateDebugRenderer(Transform host, SkinnedMeshRenderer source,
		Mesh mesh, Material[] materials)
	{
		GameObject overlay = new GameObject(source.name + "_RPT_LimbCutDebugOverlay");
		overlay.layer = source.gameObject.layer;
		overlay.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
		overlay.transform.localScale = source.transform.lossyScale;
		overlay.transform.SetParent(host, worldPositionStays: true);
		SkinnedMeshRenderer renderer = overlay.AddComponent<SkinnedMeshRenderer>();
		renderer.sharedMesh = mesh;
		renderer.bones = source.bones;
		renderer.rootBone = source.rootBone;
		renderer.localBounds = source.localBounds;
		renderer.quality = source.quality;
		renderer.updateWhenOffscreen = true;
		renderer.skinnedMotionVectors = false;
		renderer.shadowCastingMode = ShadowCastingMode.Off;
		renderer.receiveShadows = false;
		renderer.lightProbeUsage = LightProbeUsage.Off;
		renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
		renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
		renderer.allowOcclusionWhenDynamic = false;
		renderer.sortingOrder = short.MaxValue;
		renderer.sharedMaterials = materials;
		RendererSource.CopyBlendShapeWeights(source, renderer);
		renderer.enabled = false;
		return renderer;
	}

	private static bool TryGetDebugMaterials(out Material[] materials, out string failure)
	{
		if (_debugMaterials != null && _debugMaterials.All(material => material != null))
		{
			materials = _debugMaterials;
			failure = null;
			return true;
		}
		Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
		if (shader == null)
		{
			materials = null;
			failure = "Hidden/Internal-Colored, Unlit/Color and Standard shaders are all unavailable";
			return false;
		}
		Color[] colors =
		{
			new Color(0.10f, 1.00f, 0.20f, 0.38f),
			new Color(1.00f, 0.05f, 0.05f, 0.62f),
			new Color(1.00f, 0.80f, 0.05f, 0.22f),
			new Color(0.05f, 0.90f, 1.00f, 0.78f),
			new Color(1.00f, 0.05f, 0.85f, 0.95f), // exact seam
			new Color(1.00f, 1.00f, 1.00f, 0.98f), // global primary loop
			new Color(0.72f, 0.18f, 1.00f, 0.90f), // mound rings/plateau
			new Color(0.18f, 0.45f, 1.00f, 0.98f), // bone roots
			new Color(1.00f, 0.46f, 0.02f, 0.98f)  // decal samples
		};
		_debugMaterials = new Material[colors.Length];
		for (int index = 0; index < colors.Length; index++)
		{
			Material material = new Material(shader)
			{
				name = $"RPT Limb Cut Debug {index}",
				hideFlags = HideFlags.HideAndDontSave,
				color = colors[index],
				renderQueue = 5000
			};
			material.SetColor("_Color", colors[index]);
			material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
			material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
			material.SetInt("_Cull", (int)CullMode.Off);
			material.SetInt("_ZWrite", 0);
			material.SetInt("_ZTest", (int)CompareFunction.Always);
			material.EnableKeyword("_ALPHABLEND_ON");
			_debugMaterials[index] = material;
		}
		materials = _debugMaterials;
		failure = null;
		return true;
	}

	private static void DestroyDebugMaterials()
	{
		if (_debugMaterials == null) return;
		foreach (Material material in _debugMaterials)
			if (material != null) UnityEngine.Object.Destroy(material);
		_debugMaterials = null;
	}

	private static bool TryCreateGib(Collider[] sourceColliders, List<PreparedRenderer> prepared,
		CutFrame frame, EBodyPart bodyPart, LimbSeverLevel level, Vector3 cutPoint, Vector3 axis,
		Material woundMaterial, out GameObject root, out MeshCollider[] pendingColliders,
		out Mesh[] colliderMeshes, out Bounds colliderBounds,
		out ExternalTransientConvexShape[] externalShapes, out string failure)
	{
		long gibStarted = Stopwatch.GetTimestamp();
		double bakeMilliseconds = 0d;
		double extractionMilliseconds = 0d;
		root = null;
		pendingColliders = Array.Empty<MeshCollider>();
		colliderMeshes = Array.Empty<Mesh>();
		colliderBounds = default;
		externalShapes = Array.Empty<ExternalTransientConvexShape>();
		failure = null;
		if (!IsFinite(cutPoint) || !IsFinite(axis) || axis.sqrMagnitude <= 0.0001f)
		{
			failure = "the generated gib pose is invalid";
			return false;
		}
		List<Mesh> ownedMeshes = new List<Mesh>();
		List<BakedGibData> bakedMeshes = new List<BakedGibData>();
		Bounds combinedBounds = default;
		bool hasBounds = false;
		try
		{
			long bakeStarted = Stopwatch.GetTimestamp();
			// The temporary distal renderer uses the corpse's current bones and blend-shape
			// values. This is the only BakeMesh call in the detached-gib transaction.
			using (new ProfileScope("RPT.LimbCut.PosedGibBake"))
			{
			foreach (PreparedRenderer item in prepared)
			{
				if (item?.DistalRenderer == null) continue;
				Mesh baked = new Mesh
				{
					name = item.Source.ImmutableMesh.name + "_RPT_GibBakedOnce",
					indexFormat = item.DistalMesh.indexFormat
				};
				item.DistalRenderer.BakeMesh(baked);
				Vector3[] vertices = baked.vertices;
				Vector3[] normals = baked.normals;
				Vector4[] tangents = baked.tangents;
				if (vertices.Length == 0) throw new InvalidOperationException($"renderer {item.Source.Native.name} baked no distal vertices");
				foreach (Vector3 vertex in vertices)
				{
					Vector3 world = item.DistalRenderer.transform.TransformPoint(vertex);
					if (!IsFinite(world)) throw new InvalidOperationException("the baked distal mesh contains a non-finite vertex");
					if (!hasBounds) { combinedBounds = new Bounds(world, Vector3.zero); hasBounds = true; }
					else combinedBounds.Encapsulate(world);
				}
				int exteriorSubmeshCount = Mathf.Min(item.OriginalSubmeshCount, baked.subMeshCount);
				int[][] exteriorTriangles = new int[exteriorSubmeshCount][];
				for (int submesh = 0; submesh < exteriorSubmeshCount; submesh++)
					exteriorTriangles[submesh] = baked.GetTriangles(submesh);
				item.GibMesh = baked;
				bakedMeshes.Add(new BakedGibData
				{
					Item = item,
					Mesh = baked,
					Vertices = vertices,
					Normals = normals,
					Tangents = tangents,
					ExteriorTriangles = exteriorTriangles
				});
			}
			}
			if (!hasBounds || !IsFinite(combinedBounds.center)) throw new InvalidOperationException("the baked distal meshes have no finite bounds");
			bakeMilliseconds = ElapsedMilliseconds(bakeStarted);

			long extractionStarted = Stopwatch.GetTimestamp();
			using (new ProfileScope("RPT.LimbCut.GibArrayExtractionAndRenderer"))
			{
			root = new GameObject("RPT_ProceduralLimbGib");
			root.transform.SetPositionAndRotation(combinedBounds.center, Quaternion.identity);
			PreparedRenderer firstRenderer = prepared.FirstOrDefault(item => item?.Source?.Native != null);
			if (firstRenderer != null) root.layer = firstRenderer.Source.Native.gameObject.layer;
			foreach (BakedGibData bakedData in bakedMeshes)
			{
				PreparedRenderer item = bakedData.Item;
				if (item?.GibMesh == null || item.DistalRenderer == null) continue;
				MoveBakedMeshIntoGibFrame(bakedData, item.DistalRenderer.transform, root.transform);
				GameObject meshObject = new GameObject(item.Source.Native.name + "_RPT_GibMesh");
				meshObject.layer = item.Source.Native.gameObject.layer;
				meshObject.transform.SetParent(root.transform, false);
				MeshFilter filter = meshObject.AddComponent<MeshFilter>();
				filter.sharedMesh = item.GibMesh;
				MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();
				renderer.sharedMaterials = AppendMaterial(item.Source.Native.sharedMaterials, woundMaterial);
				CopyRendererSettings(item.Source.Native, renderer);
				// Geometry and external admission are asynchronous. Reveal the gib
				// only at the same ownership boundary that removes it from the body.
				renderer.enabled = false;
				RendererSource.CopyPropertyBlocks(item.Source.Native, renderer, item.Source.Native.sharedMaterials.Length);
				LimbSeverMaterialProvider.ApplyVariation(renderer, item.Source.Native.sharedMaterials.Length,
					item.WoundSeed, gibSide: true);
				item.GibRenderer = renderer;
				item.GibDecalTransfer = LimbDecalTextureTransfer.CopyExisting(item.Source.Native, renderer,
					out string decalTransferFailure);
				if (item.GibDecalTransfer == LimbDecalTransferResult.Unavailable)
					RupturePlugin.Log.LogWarning($"RPT could not preserve existing limb decals on gib renderer {renderer.name}: {decalTransferFailure}");
				item.GibLocalDecalSamples.Clear();
				foreach (ProceduralLimbWoundGeometry.DecalSample sample in item.GibDecalSamples ??
					Array.Empty<ProceduralLimbWoundGeometry.DecalSample>())
				{
					if (!TryTransformBakedSample(bakedData.Vertices, bakedData.Normals, root.transform, sample,
						out Vector3 worldPoint, out Vector3 worldNormal)) continue;
					item.GibLocalDecalSamples.Add(new LocalDecalSample(
						renderer.transform.InverseTransformPoint(worldPoint),
						renderer.transform.InverseTransformDirection(worldNormal).normalized));
				}
				ownedMeshes.Add(item.GibMesh);
			}
			}
			extractionMilliseconds = ElapsedMilliseconds(extractionStarted);
		}
		catch (Exception exception)
		{
			failure = $"the posed distal mesh could not be baked exactly once: {exception.GetType().Name}: {exception.Message}";
			if (root != null) UnityEngine.Object.Destroy(root);
			foreach (PreparedRenderer item in prepared)
			{
				if (item?.GibMesh != null) UnityEngine.Object.Destroy(item.GibMesh);
				if (item != null) item.GibMesh = null;
			}
			root = null;
			return false;
		}

		Rigidbody rigidbody = root.AddComponent<Rigidbody>();
		rigidbody.useGravity = true;
		rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
		float mass = 0f;
		HashSet<Rigidbody> massSources = new HashSet<Rigidbody>();
		foreach (Collider source in sourceColliders ?? Array.Empty<Collider>())
		{
			if (source == null) continue;
			if (source.attachedRigidbody != null && massSources.Add(source.attachedRigidbody)) mass += source.attachedRigidbody.mass;
		}
		long colliderStarted = Stopwatch.GetTimestamp();
		MeshCollider[] generatedColliders;
		Mesh[] generatedColliderMeshes;
		ExternalTransientConvexShape[] generatedExternalShapes;
		Bounds generatedBounds;
		string colliderFailure;
		bool collidersCreated;
		using (new ProfileScope("RPT.LimbCut.ColliderProxyConstruction"))
		{
			collidersCreated = TryCreateGeneratedLimbMeshColliders(root.transform, bakedMeshes, frame, level,
				cutPoint, out generatedColliders, out generatedColliderMeshes, out generatedBounds,
				out generatedExternalShapes, out colliderFailure);
		}
		if (!collidersCreated)
		{
			failure = "generated limb compound could not create the gib collider: " + colliderFailure;
			UnityEngine.Object.Destroy(root);
			root = null;
			return false;
		}
		double colliderMilliseconds = ElapsedMilliseconds(colliderStarted);
		ownedMeshes.AddRange(generatedColliderMeshes);
		rigidbody.mass = Mathf.Clamp(mass > 0f ? mass : 2f, 0.25f, 20f);
		rigidbody.drag = 0.05f;
		rigidbody.angularDrag = 0.8f;
		rigidbody.velocity = Vector3.zero;
		rigidbody.angularVelocity = Vector3.zero;
		LimbGibMeshLifetime lifetime = root.AddComponent<LimbGibMeshLifetime>();
		lifetime.Initialize(ownedMeshes.ToArray());
		foreach (PreparedRenderer item in prepared) item.GibMesh = null;
		pendingColliders = generatedColliders;
		colliderMeshes = generatedColliderMeshes;
		colliderBounds = generatedBounds;
		externalShapes = generatedExternalShapes;
		if (PerformanceLoggingEnabled)
			RupturePlugin.Log.LogInfo($"RPT limb gib phases: bodyPart={bodyPart}, level={level}, bakeAndBoundsMs={bakeMilliseconds:F3}, " +
				$"arrayTransformRendererMs={extractionMilliseconds:F3}, proxyConstructionMainMs={colliderMilliseconds:F3}, " +
				$"totalMainMs={ElapsedMilliseconds(gibStarted):F3}, bakedVertices={prepared.Sum(item => item.GibMesh?.vertexCount ?? item.GibRenderer?.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount ?? 0)}, " +
				$"queryColliders={generatedColliders.Length}, colliderVertices={generatedColliderMeshes.Sum(mesh => mesh?.vertexCount ?? 0)}, colliderTriangles={generatedColliderMeshes.Sum(mesh => (int)CountTriangles(mesh))}.");
		return true;
	}

	private static void MoveBakedMeshIntoGibFrame(BakedGibData baked, Transform rendererTransform, Transform gibFrame)
	{
		MeshFrameTransform.Apply(baked.Mesh, gibFrame.worldToLocalMatrix * rendererTransform.localToWorldMatrix,
			baked.Vertices, baked.Normals, baked.Tangents);
	}

	private static void ResolveProceduralStreamAnchors(List<PreparedRenderer> prepared,
		Transform fallbackBodyAnchor, Vector3 cutPoint, Vector3 distalAxis, Transform gibFrame,
		out Transform bodyAnchor, out Vector3 bodyPointWorld, out Vector3 gibPointLocal)
	{
		Vector3 axis = distalAxis.sqrMagnitude > 0.0001f ? distalAxis.normalized : fallbackBodyAnchor.forward;
		PreparedRenderer primary = prepared.Where(item => item != null && item.IncludeInGib)
			.OrderBy(item => item.Source.LodLevel).FirstOrDefault(item => item.HasBodyStreamAnchor || item.HasGibStreamAnchor);
		float bodyInset = Mathf.Clamp((primary?.BodyPlateauProtrusionWorld ?? 0.008f) * 0.58f, 0.0025f, 0.016f);
		float gibInset = Mathf.Clamp((primary?.GibPlateauProtrusionWorld ?? 0.008f) * 0.58f, 0.0025f, 0.016f);
		bodyAnchor = fallbackBodyAnchor;
		bodyPointWorld = cutPoint + axis * bodyInset;
		Vector3 gibPointWorld = cutPoint + axis * ProceduralLimbCutGeometry.MissingBandWidthWorld - axis * gibInset;
		bool exactBody = false;
		bool exactGib = false;

		if (primary?.HasBodyStreamAnchor == true && primary.SocketRenderer != null &&
			TrySkinWoundAnchor(primary.SocketRenderer, primary.BodyStreamAnchor,
				out Vector3 generatedBodyPoint, out _))
		{
			bodyPointWorld = generatedBodyPoint;
			bodyAnchor = ResolveWeightedAnchor(primary.SocketRenderer, primary.BodyStreamAnchor.Weight,
				fallbackBodyAnchor);
			exactBody = true;
		}
		if (primary?.HasGibStreamAnchor == true && primary.DistalRenderer != null &&
			TrySkinWoundAnchor(primary.DistalRenderer, primary.GibStreamAnchor,
				out Vector3 generatedGibPoint, out _))
		{
			gibPointWorld = generatedGibPoint;
			exactGib = true;
		}
		gibPointLocal = gibFrame.InverseTransformPoint(gibPointWorld);
		RupturePlugin.Log.LogInfo($"Resolved procedural stump origins inside caps: body={bodyPointWorld}, gib={gibPointWorld}, " +
			$"bodyFromGeneratedCap={exactBody}, gibFromGeneratedCap={exactGib}, bodyAnchor={bodyAnchor.name}.");
	}

	private static Transform ResolveWeightedAnchor(SkinnedMeshRenderer renderer, BoneWeight weight, Transform fallback)
	{
		Transform[] bones = renderer?.bones;
		return bones != null && weight.weight0 > 0f && weight.boneIndex0 >= 0 && weight.boneIndex0 < bones.Length &&
			bones[weight.boneIndex0] != null ? bones[weight.boneIndex0] : fallback;
	}

	private static bool TryCreateGeneratedLimbMeshColliders(Transform root, List<BakedGibData> bakedMeshes,
		CutFrame frame, LimbSeverLevel level, Vector3 cutPoint, out MeshCollider[] colliders,
		out Mesh[] colliderMeshes, out Bounds combinedBounds,
		out ExternalTransientConvexShape[] externalShapes, out string failure)
	{
		colliders = Array.Empty<MeshCollider>();
		colliderMeshes = Array.Empty<Mesh>();
		externalShapes = Array.Empty<ExternalTransientConvexShape>();
		combinedBounds = default;
		failure = null;
		if (root == null || bakedMeshes == null || bakedMeshes.Count == 0 || frame?.Joint == null ||
			frame.DistalEnd == null || !IsFinite(cutPoint))
		{
			failure = "the mesh-derived gib collider frame is unavailable";
			return false;
		}

		List<(Vector3 Start, Vector3 End, string Name)> segments = new List<(Vector3, Vector3, string)>();
		if (level == LimbSeverLevel.Proximal)
		{
			segments.Add((root.InverseTransformPoint(cutPoint), root.InverseTransformPoint(frame.Joint.position), "Proximal"));
			segments.Add((root.InverseTransformPoint(frame.Joint.position), root.InverseTransformPoint(frame.DistalEnd.position), "Distal"));
		}
		else
		{
			segments.Add((root.InverseTransformPoint(cutPoint), root.InverseTransformPoint(frame.DistalEnd.position), "Distal"));
		}

		List<Vector3>[] segmentPoints = new List<Vector3>[segments.Count];
		for (int index = 0; index < segments.Count; index++)
			segmentPoints[index] = new List<Vector3>();
		foreach (BakedGibData baked in bakedMeshes)
		{
			if (baked?.Mesh == null || baked.Vertices == null) continue;
			Vector3[] vertices = baked.Vertices;
			// The final submesh contains flesh, bone, flaps, and cords. Collider
			// cooking is restricted to immutable-source exterior submeshes.
			for (int submesh = 0; submesh < baked.ExteriorTriangles.Length; submesh++)
			{
				int[] indices = baked.ExteriorTriangles[submesh];
				HashSet<int> referenced = new HashSet<int>(indices);
				foreach (int vertexIndex in referenced)
				{
					if (vertexIndex < 0 || vertexIndex >= vertices.Length || !IsFinite(vertices[vertexIndex])) continue;
					Vector3 point = vertices[vertexIndex];
					int bestSegment = 0;
					float bestDistance = float.PositiveInfinity;
					for (int segment = 0; segment < segments.Count; segment++)
					{
						float distance = DistanceToSegmentSquared(point, segments[segment].Start, segments[segment].End);
						if (distance >= bestDistance) continue;
						bestDistance = distance;
						bestSegment = segment;
					}
					segmentPoints[bestSegment].Add(point);
				}
			}
		}

		List<MeshCollider> generated = new List<MeshCollider>();
		List<Mesh> generatedMeshes = new List<Mesh>();
		List<ExternalTransientConvexShape> generatedShapes = new List<ExternalTransientConvexShape>();
		bool hasBounds = false;
		for (int segment = 0; segment < segments.Count; segment++)
		{
			if (!TryBuildSegmentProxy(segments[segment].Start, segments[segment].End,
				segmentPoints[segment], out Vector3[] proxyVertices, out int[] proxyIndices,
				out string proxyFailure))
			{
				if (PerformanceLoggingEnabled)
					RupturePlugin.Log.LogWarning($"RPT gib collider proxy skipped {segments[segment].Name}: {proxyFailure}");
				continue;
			}
			Mesh colliderMesh = new Mesh
			{
				name = "RPT_GeneratedLimb" + segments[segment].Name + "CollisionMesh",
				indexFormat = IndexFormat.UInt16
			};
			colliderMesh.vertices = proxyVertices;
			colliderMesh.SetTriangles(proxyIndices, 0, calculateBounds: true);
			const MeshColliderCookingOptions cookingOptions = MeshColliderCookingOptions.None;
			GameObject child = new GameObject("RPT_GeneratedLimb" + segments[segment].Name + "MeshCollider");
			child.layer = root.gameObject.layer;
			child.transform.SetParent(root, worldPositionStays: false);
			MeshCollider meshCollider = child.AddComponent<MeshCollider>();
			meshCollider.cookingOptions = cookingOptions;
			meshCollider.convex = true;
			// Keep the collider detached until the dedicated worker has pre-baked this
			// exact mesh/convex/options tuple. Assignment on the main thread then reuses
			// Unity's cached cooked data instead of blocking on the cook.
			meshCollider.sharedMesh = null;
			meshCollider.isTrigger = false;
			meshCollider.enabled = false;
			if (!IsFinite(colliderMesh.bounds.center) || !IsFinite(colliderMesh.bounds.size) ||
				colliderMesh.bounds.size.sqrMagnitude <= 0.000001f)
			{
				UnityEngine.Object.Destroy(child);
				UnityEngine.Object.Destroy(colliderMesh);
				continue;
			}
			generated.Add(meshCollider);
			generatedMeshes.Add(colliderMesh);
			uint[] externalIndices = new uint[proxyIndices.Length];
			for (int index = 0; index < proxyIndices.Length; index++) externalIndices[index] = (uint)proxyIndices[index];
			generatedShapes.Add(new ExternalTransientConvexShape
			{
				QueryColliderId = meshCollider.GetInstanceID(),
				Vertices = proxyVertices,
				Indices = externalIndices,
				LocalPosition = Vector3.zero,
				LocalRotation = Quaternion.identity,
				Material = meshCollider.sharedMaterial,
				ContactOffset = meshCollider.contactOffset
			});
			if (!hasBounds)
			{
				combinedBounds = colliderMesh.bounds;
				hasBounds = true;
			}
			else combinedBounds.Encapsulate(colliderMesh.bounds);
			if (PerformanceLoggingEnabled)
				RupturePlugin.Log.LogInfo($"RPT gib collider proxy: segment={segments[segment].Name}, sourcePoints={segmentPoints[segment].Count}, proxyVertices={proxyVertices.Length}, proxyTriangles={proxyIndices.Length / 3}, cookingOptions={cookingOptions}.");
		}
		if (generated.Count == 0)
		{
			failure = "no convex mesh collider proxy could be constructed from the isolated gib triangles";
			return false;
		}
		if (!IsFinite(combinedBounds.center) || !IsFinite(combinedBounds.size) ||
			combinedBounds.size.x > 1.5f || combinedBounds.size.y > 1.75f || combinedBounds.size.z > 1.5f)
		{
			foreach (Mesh mesh in generatedMeshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
			failure = $"mesh-derived gib compound bounds are implausible (size={combinedBounds.size}, center={combinedBounds.center})";
			return false;
		}
		colliders = generated.ToArray();
		colliderMeshes = generatedMeshes.ToArray();
		externalShapes = generatedShapes.ToArray();
		return true;
	}

	private static bool TryAttachBakedGibColliders(GameObject root, MeshCollider[] colliders,
		Mesh[] colliderMeshes, Bounds localProxyBounds, out string failure)
	{
		failure = null;
		if (root == null || colliders == null || colliderMeshes == null || colliders.Length == 0 ||
			colliders.Length != colliderMeshes.Length || !IsFinite(localProxyBounds.center) ||
			!IsFinite(localProxyBounds.size) || localProxyBounds.size.sqrMagnitude <= 0.000001f)
		{
			failure = "the pre-baked gib collider proxy set is invalid";
			return false;
		}

		try
		{
			Bounds combinedWorldBounds = default;
			bool hasWorldBounds = false;
			using (new ProfileScope("RPT.LimbCut.UnityColliderAttach"))
			{
				for (int index = 0; index < colliders.Length; index++)
				{
					MeshCollider collider = colliders[index];
					Mesh mesh = colliderMeshes[index];
					if (collider == null || mesh == null)
					{
						failure = "a pre-baked gib collider or mesh was destroyed before attachment";
						return false;
					}
					collider.cookingOptions = MeshColliderCookingOptions.None;
					collider.convex = true;
					collider.sharedMesh = mesh;
					collider.isTrigger = false;
					collider.enabled = true;
					Bounds bounds = collider.bounds;
					if (!IsFinite(bounds.center) || !IsFinite(bounds.size) ||
						bounds.size.sqrMagnitude <= 0.000001f)
					{
						failure = "a pre-baked gib collider produced invalid world bounds";
						return false;
					}
					if (!hasWorldBounds)
					{
						combinedWorldBounds = bounds;
						hasWorldBounds = true;
					}
					else combinedWorldBounds.Encapsulate(bounds);
				}
			}

			Rigidbody rigidbody = root.GetComponent<Rigidbody>();
			if (rigidbody == null || !hasWorldBounds)
			{
				failure = "the generated gib rigidbody or collider bounds are unavailable";
				return false;
			}
			rigidbody.ResetCenterOfMass();
			rigidbody.ResetInertiaTensor();
			combinedWorldBounds.Expand(0.1f);
			if (!IsFinite(rigidbody.worldCenterOfMass) || !combinedWorldBounds.Contains(rigidbody.worldCenterOfMass))
			{
				failure = "the generated gib center of mass is invalid after collider attachment";
				return false;
			}
			return true;
		}
		catch (Exception exception)
		{
			failure = $"the pre-baked gib colliders could not be attached: {exception.GetType().Name}: {exception.Message}";
			return false;
		}
	}

	private static bool TryBuildSegmentProxy(Vector3 start, Vector3 end, List<Vector3> points,
		out Vector3[] vertices, out int[] indices, out string failure)
	{
		const int Stations = 5;
		const int Directions = 8;
		vertices = null;
		indices = null;
		failure = null;
		Vector3 segment = end - start;
		float length = segment.magnitude;
		if (points == null || points.Count < 4 || !IsFinite(start) || !IsFinite(end) || length < 0.025f)
		{
			failure = "segment or baked exterior point cloud is degenerate";
			return false;
		}
		Vector3 axis = segment / length;
		Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.8f ? Vector3.up : Vector3.right).normalized;
		Vector3 v = Vector3.Cross(axis, u).normalized;
		vertices = new Vector3[Stations * Directions + 2];
		for (int station = 0; station < Stations; station++)
		{
			float t = station / (float)(Stations - 1);
			Vector3 axial = start + segment * t;
			Vector3 radialCenter = Vector3.zero;
			int localCount = 0;
			float window = length * 0.22f;
			foreach (Vector3 point in points)
			{
				float along = Vector3.Dot(point - start, axis);
				if (Mathf.Abs(along - length * t) > window) continue;
				radialCenter += point - (start + axis * along);
				localCount++;
			}
			if (localCount > 0) radialCenter /= localCount;
			radialCenter -= axis * Vector3.Dot(radialCenter, axis);
			for (int directionIndex = 0; directionIndex < Directions; directionIndex++)
			{
				float angle = directionIndex * Mathf.PI * 2f / Directions;
				Vector3 radialDirection = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
				float support = 0f;
				foreach (Vector3 point in points)
				{
					float along = Vector3.Dot(point - start, axis);
					if (Mathf.Abs(along - length * t) > window) continue;
					support = Mathf.Max(support, Vector3.Dot(point - axial - radialCenter, radialDirection));
				}
				support = Mathf.Clamp(support, 0.012f, 0.30f);
				vertices[station * Directions + directionIndex] = axial + radialCenter + radialDirection * support;
			}
		}
		vertices[Stations * Directions] = start;
		vertices[Stations * Directions + 1] = end;
		List<int> triangles = new List<int>(80 * 3);
		for (int station = 0; station < Stations - 1; station++)
			for (int directionIndex = 0; directionIndex < Directions; directionIndex++)
			{
				int next = (directionIndex + 1) % Directions;
				int a = station * Directions + directionIndex;
				int b = station * Directions + next;
				int c = (station + 1) * Directions + directionIndex;
				int d = (station + 1) * Directions + next;
				triangles.Add(a); triangles.Add(b); triangles.Add(c);
				triangles.Add(b); triangles.Add(d); triangles.Add(c);
			}
		int startCenter = Stations * Directions;
		int endCenter = startCenter + 1;
		for (int directionIndex = 0; directionIndex < Directions; directionIndex++)
		{
			int next = (directionIndex + 1) % Directions;
			triangles.Add(startCenter); triangles.Add(next); triangles.Add(directionIndex);
			int last = (Stations - 1) * Directions;
			triangles.Add(endCenter); triangles.Add(last + directionIndex); triangles.Add(last + next);
		}
		indices = triangles.ToArray();
		if (vertices.Length > 42 || indices.Length / 3 > 80 || !ValidateClosedProxy(vertices, indices, out failure))
		{
			vertices = null;
			indices = null;
			return false;
		}
		return true;
	}

	private static bool ValidateClosedProxy(Vector3[] vertices, int[] indices, out string failure)
	{
		failure = null;
		Dictionary<(int, int), int> owners = new Dictionary<(int, int), int>();
		for (int index = 0; index + 2 < indices.Length; index += 3)
		{
			int a = indices[index]; int b = indices[index + 1]; int c = indices[index + 2];
			if (a == b || b == c || c == a || !IsFinite(vertices[a]) || !IsFinite(vertices[b]) ||
				!IsFinite(vertices[c]) || Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).sqrMagnitude < 0.0000000001f)
			{
				failure = "proxy contains a non-finite or degenerate triangle";
				return false;
			}
			AddProxyEdge(owners, a, b); AddProxyEdge(owners, b, c); AddProxyEdge(owners, c, a);
		}
		if (owners.Values.Any(count => count != 2))
		{
			failure = "proxy edge ownership is not closed-manifold";
			return false;
		}
		return true;
	}

	private static void AddProxyEdge(Dictionary<(int, int), int> owners, int a, int b)
	{
		var edge = a < b ? (a, b) : (b, a);
		owners[edge] = owners.TryGetValue(edge, out int count) ? count + 1 : 1;
	}

	private static float DistanceToSegmentSquared(Vector3 point, Vector3 start, Vector3 end)
	{
		Vector3 segment = end - start;
		float denominator = segment.sqrMagnitude;
		if (denominator <= 0.000001f) return (point - start).sqrMagnitude;
		float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / denominator);
		return (point - (start + segment * t)).sqrMagnitude;
	}

	private static void CommitPrepared(PendingSever request)
	{
		CutState state = request.State;
		List<PreparedRenderer> prepared = request.Prepared;
		CommitTransaction transaction = new CommitTransaction();
		foreach (PreparedRenderer item in prepared)
		{
			SourceRendererState source = item.Source;
			Mesh previousMesh = source.Native.sharedMesh;
			Renderer[] previous = { source.SocketRenderer, source.DebugRenderer };
			Renderer[] next = { item.SocketRenderer, item.DebugRenderer };
			transaction.Add(() => source.Native.sharedMesh = item.BodyMesh,
				() => { if (source.Native != null) source.Native.sharedMesh = previousMesh; });
			transaction.Add(() => state.Lifetime.ReplaceLodAttachments(source.Native, previous, next),
				() => state.Lifetime.ReplaceLodAttachments(source.Native, next, previous));
		}
		transaction.Commit();
		// No fallible cosmetics before this ownership boundary. Rollback never destroys
		// the previous meshes; once committed, only the lifetime owns the new meshes.
		request.Committed = true;
		state.Cuts.Add(request.Sever);
		state.Severed.Add(request.BodyPart);
		foreach (PreparedRenderer item in prepared)
		{
			state.Lifetime.TrackMesh(item.BodyMesh);
			state.Lifetime.AddReplacement(item.SocketRenderer, item.SocketMesh);
			state.Lifetime.AddDebug(item.DebugRenderer, item.DebugMesh);
		}
		foreach (PreparedRenderer item in prepared)
		{
			SourceRendererState source = item.Source;
			// Keep the native renderer identity and its LOD slot. EFT's later hit/decal
			// paths continue to address that renderer, while the immutable source lets
			// subsequent cuts rebuild rather than recursively clip this mesh.
			Mesh previousBodyMesh = source.BodyMesh;
			SkinnedMeshRenderer previousSocket = source.SocketRenderer;
			SkinnedMeshRenderer previousDebug = source.DebugRenderer;
			Mesh previousSocketMesh = source.SocketMesh;
			Mesh previousDebugMesh = source.DebugMesh;
			item.BodyRenderer = source.Native;
			source.SocketRenderer = item.SocketRenderer;
			source.DebugRenderer = item.DebugRenderer;
			source.BodyMesh = item.BodyMesh;
			source.SocketMesh = item.SocketMesh;
			source.DebugMesh = item.DebugMesh;
			try
			{
				if (previousBodyMesh != null) UnityEngine.Object.Destroy(previousBodyMesh);
				DestroyReplacement(previousSocket, previousSocketMesh);
				DestroyReplacement(previousDebug, previousDebugMesh);
				if (item.SocketRenderer != null) item.SocketRenderer.enabled = true;
				if (item.GibRenderer != null) item.GibRenderer.enabled = true;
				if (item.DebugRenderer != null) item.DebugRenderer.enabled = DebugOverlayEnabled;
				DestroyTemporaryDistal(item);
			}
			catch (Exception exception) { RupturePlugin.Log.LogWarning($"RPT committed limb renderer cleanup failed: {exception.Message}"); }
		}
		foreach (PreparedRenderer item in prepared)
			try
			{
				if (!LimbDecalTextureTransfer.RebindExisting(item.Source.Native, out string failure))
					RupturePlugin.Log.LogWarning($"RPT could not rebind limb decals: {failure}");
			}
			catch (Exception exception) { RupturePlugin.Log.LogWarning($"RPT limb decal rebind failed: {exception.Message}"); }
	}

	private static void DestroyReplacement(Renderer renderer, Mesh mesh)
	{
		if (renderer != null) UnityEngine.Object.Destroy(renderer.gameObject);
		if (mesh != null) UnityEngine.Object.Destroy(mesh);
	}

	private static void DestroyTemporaryDistal(PreparedRenderer item)
	{
		if (item?.DistalRenderer != null) UnityEngine.Object.Destroy(item.DistalRenderer.gameObject);
		if (item?.DistalMesh != null) UnityEngine.Object.Destroy(item.DistalMesh);
		if (item != null) { item.DistalRenderer = null; item.DistalMesh = null; }
	}

	private static CutState GetOrCreateState(CorpseInteractionTarget target, string key, bool createVisualHost)
	{
		if (States.TryGetValue(key, out CutState state))
		{
			if (createVisualHost && state.Host == null) InitializeVisualState(state, target);
			return state;
		}
		state = new CutState { Key = key };
		if (target.PlayerBody?.BodySkins != null)
		{
			foreach (var skin in target.PlayerBody.BodySkins.Values)
			{
				if (skin == null) continue;
				foreach (Renderer renderer in skin.GetRenderers() ?? Array.Empty<Renderer>())
				{
					if (renderer != null && !state.NativeRenderers.Contains(renderer)) state.NativeRenderers.Add(renderer);
				}
			}
		}
		if (createVisualHost) InitializeVisualState(state, target);
		States[key] = state;
		CutState ownedState = state;
		int bodyId = target.PlayerBody.GetInstanceID();
		ObjectLifetime.Bind(target.PlayerBody.gameObject, "limbs", () =>
		{
			if (!States.TryGetValue(key, out CutState current) || !ReferenceEquals(current, ownedState)) return;
			States.Remove(key);
			LivingBodies.Remove(bodyId);
			if (ownedState.Host != null) UnityEngine.Object.Destroy(ownedState.Host);
		});
		return state;
	}

	private static void InitializeVisualState(CutState state, CorpseInteractionTarget target)
	{
		state.Host = new GameObject("RPT_ProceduralLimbCuts_" + state.Key);
		state.Host.transform.SetParent(target.VisualRoot, false);
		state.Lifetime = state.Host.AddComponent<LimbCutLifetime>();
		state.Lifetime.Initialize(state.NativeRenderers.ToArray());
		foreach (SkinnedMeshRenderer renderer in state.NativeRenderers.OfType<SkinnedMeshRenderer>())
		{
			state.Sources.Add(new SourceRendererState
			{
				Native = renderer,
				LodLevel = RendererSource.TryGetLodLevel(renderer, out int level) ? Mathf.Max(0, level) : 0
			});
		}
	}

	internal static bool TryGetExplosionDistance(CorpseInteractionTarget target, EBodyPart bodyPart,
		Vector3 explosionCenter, out float distance, out Vector3 nearest)
	{
		distance = float.PositiveInfinity;
		nearest = default;
		if (!TryResolveCutFrame(target, bodyPart, out CutFrame frame, out string failure))
		{
			RupturePlugin.Log.LogWarning($"Explosion limb selection rejected for {bodyPart}: {failure}");
			return false;
		}
		distance = ExplosionSeverPolicy.Distance(explosionCenter, frame.ProximalStart.position,
			frame.Joint.position, frame.DistalEnd.position, out nearest);
		return !float.IsInfinity(distance);
	}

	private static bool TryResolveCutFrame(CorpseInteractionTarget target, EBodyPart bodyPart,
		out CutFrame frame, out string failure)
	{
		CorpseLimbContext limbs = target.Limbs ?? CorpseLimbContext.Capture(target.Player, target.PlayerBody);
		if (limbs != null && limbs.TryGet(bodyPart, out Transform start, out Transform joint, out Transform end))
			return BuildFrame(start, joint, end, start, joint, out frame, out failure);
		// A packet can precede local skeleton construction. Retry against the retained body,
		// while never depending on the retired Player object to become available again.
		limbs = CorpseLimbContext.Capture(null, target.PlayerBody);
		if (limbs != null && limbs.TryGet(bodyPart, out start, out joint, out end))
			return BuildFrame(start, joint, end, start, joint, out frame, out failure);
		frame = null;
		failure = "retained corpse limb bones are unavailable";
		return false;
	}

	internal static bool TryGetLimbSegment(Player player, EBodyPart part, LimbSeverLevel level,
		out Transform start, out Transform end)
	{
		start = end = null;
		if (!TryResolveCutFrame(player, part, out CutFrame frame, out _)) return false;
		start = level == LimbSeverLevel.Proximal ? frame.ProximalStart : frame.Joint;
		end = level == LimbSeverLevel.Proximal ? frame.Joint : frame.DistalEnd;
		return true;
	}

	private static bool TryResolveCutFrame(Player player, EBodyPart bodyPart,
		out CutFrame frame, out string failure)
	{
		frame = null;
		failure = null;
		PlayerBones bones = player?.PlayerBones;
		if (bones == null)
		{
			failure = "player bones are unavailable";
			return false;
		}
		bool left = bodyPart == EBodyPart.LeftArm || bodyPart == EBodyPart.LeftLeg;
		if (bodyPart == EBodyPart.LeftArm || bodyPart == EBodyPart.RightArm)
		{
			int side = left ? 0 : 1;
			if (bones.Upperarms == null || bones.Forearms == null || bones.Upperarms.Length <= side || bones.Forearms.Length <= side)
			{
				failure = "arm segment bones are unavailable";
				return false;
			}
			Transform shoulder = bones.Upperarms[side];
			Transform elbow = bones.Forearms[side];
			Transform wrist = left ? bones.LeftPalm : bones.RightPalm;
			return BuildFrame(shoulder, elbow, wrist, shoulder, elbow, out frame, out failure);
		}
		Transform thigh = (left ? bones.LeftThigh1 : bones.RightThigh1)?.Original;
		Transform knee = FindDescendant(thigh, left ? "lcalf" : "rcalf", "calf");
		Transform ankle = FindDescendant(knee ?? thigh, left ? "lfoot" : "rfoot", "foot");
		return BuildFrame(thigh, knee, ankle, thigh, knee, out frame, out failure);
	}

	private static bool BuildFrame(Transform proximal, Transform joint, Transform distal,
		Transform proximalRoot, Transform distalRoot, out CutFrame frame, out string failure)
	{
		frame = null;
		failure = null;
		if (proximal == null || joint == null || distal == null ||
			(joint.position - proximal.position).sqrMagnitude < 0.001f || (distal.position - joint.position).sqrMagnitude < 0.001f)
		{
			failure = "anatomical limb segments are missing or degenerate";
			return false;
		}
		frame = new CutFrame
		{
			ProximalStart = proximal,
			Joint = joint,
			DistalEnd = distal,
			ProximalRoot = proximalRoot,
			DistalRoot = distalRoot,
			ProximalPoint = LimbSeverMath.Candidate(proximal.position, joint.position, LimbSeverLevel.Proximal),
			DistalPoint = LimbSeverMath.Candidate(joint.position, distal.position, LimbSeverLevel.Distal),
			ProximalAxis = (joint.position - proximal.position).normalized,
			DistalAxis = (distal.position - joint.position).normalized
		};
		return true;
	}

	private static Transform FindDescendant(Transform root, params string[] tokens)
	{
		if (root == null) return null;
		foreach (Transform current in root.GetComponentsInChildren<Transform>(includeInactive: true))
		{
			if (current == root) continue;
			foreach (string token in tokens)
				if (current.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return current;
		}
		return null;
	}

	private static bool RendererSupportsEitherCut(SkinnedMeshRenderer renderer, SeverRecord cut)
	{
		Transform[] bones = renderer?.bones;
		CutFrame frame = cut?.Frame;
		return bones != null && frame != null && Array.IndexOf(bones, frame.Joint) >= 0 &&
			(Array.IndexOf(bones, frame.ProximalStart) >= 0 || Array.IndexOf(bones, frame.DistalEnd) >= 0);
	}

	private static void SupplyMissingBindFrames(CutState state, SourceRendererState source,
		ProceduralLimbCutGeometry.CutSpec spec)
	{
		Transform[] bones = source.Native.bones;
		if (Array.IndexOf(bones, spec.ProximalStart) >= 0 && Array.IndexOf(bones, spec.Joint) >= 0 &&
			Array.IndexOf(bones, spec.DistalEnd) >= 0) return;
		Matrix4x4[] bindposes = source.ImmutableMesh.bindposes;
		spec.ProximalBind = Resolve(spec.ProximalStart);
		spec.JointBind = Resolve(spec.Joint);
		spec.DistalBind = Resolve(spec.DistalEnd);
		Matrix4x4? Resolve(Transform target)
		{
			if (Array.IndexOf(bones, target) >= 0) return null;
			foreach (SourceRendererState reference in state.Sources)
			{
				if (reference == source || reference?.Native == null) continue;
				Mesh mesh = reference.ImmutableMesh != null ? reference.ImmutableMesh : reference.Native.sharedMesh;
				if (mesh == null || !mesh.isReadable) continue;
				if (ProceduralLimbCutGeometry.TryMapBindFrame(target, bones, bindposes,
					reference.Native.bones, mesh.bindposes, out Matrix4x4 frame)) return frame;
			}
			return null;
		}
	}

	private static bool RendererHasWeightedBone(SkinnedMeshRenderer renderer, Transform root, Mesh immutable = null)
	{
		Transform[] bones = renderer?.bones;
		Mesh mesh = immutable != null ? immutable : renderer?.sharedMesh;
		if (bones == null || mesh == null || !mesh.isReadable) return false;
		HashSet<int> limbBones = new HashSet<int>();
		for (int index = 0; index < bones.Length; index++)
			if (bones[index] == root || (bones[index] != null && bones[index].IsChildOf(root))) limbBones.Add(index);
		foreach (BoneWeight weight in mesh.boneWeights)
		{
			float sum = 0f;
			if (limbBones.Contains(weight.boneIndex0)) sum += weight.weight0;
			if (limbBones.Contains(weight.boneIndex1)) sum += weight.weight1;
			if (limbBones.Contains(weight.boneIndex2)) sum += weight.weight2;
			if (limbBones.Contains(weight.boneIndex3)) sum += weight.weight3;
			if (sum > 0.01f) return true;
		}
		return false;
	}

	private static Material[] AppendMaterial(Material[] source, Material appended)
	{
		Material[] materials = new Material[(source?.Length ?? 0) + 1];
		if (source != null) Array.Copy(source, materials, source.Length);
		materials[materials.Length - 1] = appended;
		return materials;
	}

	private static long CountTriangles(Mesh mesh)
	{
		if (mesh == null) return 0;
		long count = 0;
		for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
			count += (long)mesh.GetIndexCount(submesh) / 3L;
		return count;
	}

	private static void CopyRendererSettings(Renderer source, Renderer destination)
	{
		destination.shadowCastingMode = source.shadowCastingMode;
		destination.receiveShadows = source.receiveShadows;
		destination.lightProbeUsage = source.lightProbeUsage;
		destination.reflectionProbeUsage = source.reflectionProbeUsage;
		destination.probeAnchor = source.probeAnchor;
		destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
		destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
		destination.sortingLayerID = source.sortingLayerID;
		destination.sortingOrder = source.sortingOrder;
	}

	private static bool TrySkinSample(SkinnedMeshRenderer renderer,
		ProceduralLimbWoundGeometry.DecalSample sample, out Vector3 point, out Vector3 normal)
	{
		return TrySkinPoint(renderer, sample.Position, sample.Normal, sample.Weight, out point, out normal);
	}

	private static bool TrySkinWoundAnchor(SkinnedMeshRenderer renderer,
		ProceduralLimbWoundGeometry.EmissionAnchor anchor, out Vector3 point, out Vector3 normal)
	{
		return TrySkinPoint(renderer, anchor.Position, anchor.Normal, anchor.Weight, out point, out normal);
	}

	private static bool TrySkinPoint(SkinnedMeshRenderer renderer, Vector3 sourcePoint,
		Vector3 sourceNormal, BoneWeight weight, out Vector3 point, out Vector3 normal)
	{
		point = Vector3.zero;
		normal = Vector3.zero;
		Mesh mesh = renderer?.sharedMesh;
		Transform[] bones = renderer?.bones;
		Matrix4x4[] bindposes = mesh?.bindposes;
		if (renderer == null || bones == null || bindposes == null || bindposes.Length != bones.Length) return false;
		float total = 0f;
		AccumulateSkin(weight.boneIndex0, weight.weight0, sourcePoint, sourceNormal, bones, bindposes,
			ref point, ref normal, ref total);
		AccumulateSkin(weight.boneIndex1, weight.weight1, sourcePoint, sourceNormal, bones, bindposes,
			ref point, ref normal, ref total);
		AccumulateSkin(weight.boneIndex2, weight.weight2, sourcePoint, sourceNormal, bones, bindposes,
			ref point, ref normal, ref total);
		AccumulateSkin(weight.boneIndex3, weight.weight3, sourcePoint, sourceNormal, bones, bindposes,
			ref point, ref normal, ref total);
		if (total <= 0.0001f)
		{
			point = renderer.transform.TransformPoint(sourcePoint);
			normal = renderer.transform.TransformDirection(sourceNormal).normalized;
		}
		else
		{
			point /= total;
			normal = normal.sqrMagnitude > 0.000001f ? normal.normalized : renderer.transform.forward;
		}
		return IsFinite(point) && IsFinite(normal);
	}

	private static bool TryTransformBakedSample(Vector3[] vertices, Vector3[] normals, Transform gibFrame,
		ProceduralLimbWoundGeometry.DecalSample sample, out Vector3 point, out Vector3 normal)
	{
		point = default;
		normal = default;
		if (vertices == null || gibFrame == null || sample.MeshVertexA < 0 || sample.MeshVertexB < 0 ||
			sample.MeshVertexC < 0) return false;
		if (sample.MeshVertexA >= vertices.Length || sample.MeshVertexB >= vertices.Length ||
			sample.MeshVertexC >= vertices.Length) return false;
		Vector3 barycentric = sample.MeshBarycentric;
		Vector3 localPoint = vertices[sample.MeshVertexA] * barycentric.x +
			vertices[sample.MeshVertexB] * barycentric.y + vertices[sample.MeshVertexC] * barycentric.z;
		Vector3 localNormal = normals.Length == vertices.Length
			? (normals[sample.MeshVertexA] * barycentric.x + normals[sample.MeshVertexB] * barycentric.y +
				normals[sample.MeshVertexC] * barycentric.z).normalized
			: sample.Normal;
		point = gibFrame.TransformPoint(localPoint);
		normal = gibFrame.TransformDirection(localNormal).normalized;
		return IsFinite(point) && IsFinite(normal);
	}

	private static void AccumulateSkin(int boneIndex, float weight, Vector3 position, Vector3 sourceNormal,
		Transform[] bones, Matrix4x4[] bindposes, ref Vector3 point, ref Vector3 normal, ref float total)
	{
		if (weight <= 0f || boneIndex < 0 || boneIndex >= bones.Length || bones[boneIndex] == null) return;
		Matrix4x4 matrix = bones[boneIndex].localToWorldMatrix * bindposes[boneIndex];
		point += matrix.MultiplyPoint3x4(position) * weight;
		normal += matrix.MultiplyVector(sourceNormal) * weight;
		total += weight;
	}

	private static void DestroyPrepared(List<PreparedRenderer> prepared)
	{
		if (prepared == null) return;
		foreach (PreparedRenderer item in prepared)
		{
			if (item?.BodyRenderer != null && item.BodyRenderer != item.Source?.Native)
				UnityEngine.Object.Destroy(item.BodyRenderer.gameObject);
			if (item?.SocketRenderer != null) UnityEngine.Object.Destroy(item.SocketRenderer.gameObject);
			if (item?.DistalRenderer != null) UnityEngine.Object.Destroy(item.DistalRenderer.gameObject);
			if (item?.DebugRenderer != null) UnityEngine.Object.Destroy(item.DebugRenderer.gameObject);
			if (item?.BodyMesh != null) UnityEngine.Object.Destroy(item.BodyMesh);
			if (item?.SocketMesh != null) UnityEngine.Object.Destroy(item.SocketMesh);
			if (item?.DistalMesh != null) UnityEngine.Object.Destroy(item.DistalMesh);
			if (item?.DebugMesh != null) UnityEngine.Object.Destroy(item.DebugMesh);
			if (item?.GibMesh != null) UnityEngine.Object.Destroy(item.GibMesh);
		}
		prepared.Clear();
	}

	private static string CreateKey(CorpseInteractionTarget target) =>
		(target.PlayerBody != null && LivingBodies.Contains(target.PlayerBody.GetInstanceID()))
			? $"{target.ProfileId ?? target.PlayerId.ToString()}:living:{target.PlayerBody.GetInstanceID()}"
			: $"{target.ProfileId ?? target.PlayerId.ToString()}:{target.DeathSequence}";

	private static bool DebugOverlayEnabled =>
		RuptureConfiguration.ProceduralLimbCutDebugOverlay?.Value == true;

	private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

	private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	internal static bool IsLimb(EBodyPart part) => part == EBodyPart.LeftArm || part == EBodyPart.RightArm ||
		part == EBodyPart.LeftLeg || part == EBodyPart.RightLeg;
}
