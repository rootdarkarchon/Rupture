using System;
using Rupture.Impacts;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Systems.Effects;
using Comfort.Common;
using Diz.Skinning;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.Patches;

namespace Rupture.Dismemberment.Classes.HeadWounds;

internal static partial class HeadWoundSystem
{
	private sealed class PendingLethalHeadshot
	{
		internal ulong Projectile;
		internal Player Player;
		internal CorpseInteractionTarget Target;
		internal string ProfileId;
		internal int PlayerId;
		internal uint DeathSequence;
		internal int FireIndex;
		internal string SourceId;
		internal Collider HitCollider;
		internal Vector3 HitPointWorld;
		internal Vector3 EntryLocal;
		internal Vector3 DirectionLocal;
		internal Vector3 DirectionWorld;
		internal float PenetrationPower;
		internal bool GenerateWound;
		internal float ExpiresAt;
	}

	private sealed class WoundWorkItem
	{
		internal CorpseInteractionTarget Target;
		internal HeadWoundData Packet;
		internal CorpseWounds State;
		internal HeadBuild Build;
		internal int StaleRecaptures;
		internal float QueuedAt;
		internal float ExpiresAt;
	}
	private const int MaximumStaleRecaptures = 2;

	private enum ApplyResult
	{
		Waiting,
		Applied,
		Rejected
	}

	private static readonly List<PendingLethalHeadshot> PendingLethalHeadshots = new List<PendingLethalHeadshot>();
	private static readonly Queue<WoundWorkItem> PendingWounds = new Queue<WoundWorkItem>();
	private sealed class CorpseWounds
	{
		internal readonly HeadWoundPolicy Policy = new HeadWoundPolicy();
		internal readonly HeadWoundOrder Order = new HeadWoundOrder();
		internal readonly List<ProceduralWoundDescriptor> Descriptors = new List<ProceduralWoundDescriptor>(4);
		internal HeadWoundLifetime Lifetime;
		internal HeadWoundGeometrySource Source;
		internal Mesh NativeMesh;
		internal int Revision;
		internal SkinnedMeshRenderer SourceRenderer;
		internal Renderer[] HeadRenderers;
		internal Transform[] Bones;
		internal Matrix4x4[] Bindposes;
	}
	private static readonly Dictionary<string, CorpseWounds> Corpses = new Dictionary<string, CorpseWounds>(StringComparer.Ordinal);
	private static CorpseWounds GetState(CorpseInteractionTarget target)
	{
		string key = CreateKey(target.ProfileId, target.PlayerId, target.DeathSequence);
		if (!Corpses.TryGetValue(key, out CorpseWounds state)) Corpses.Add(key, state = new CorpseWounds());
		return state;
	}
	private static readonly HashSet<string> ReportedUnavailableResources = new HashSet<string>(StringComparer.Ordinal);
	private static readonly List<HeadWoundLifetime> ActiveWounds = new List<HeadWoundLifetime>();
	private static WoundWorkItem _activeWork;
	private const float PendingLifetimeSeconds = 10f;
	private const float ShotMatchLifetimeSeconds = 5f;
	private const float MaximumHitPointDifference = 0.075f;

	internal static bool RegisterLethalHeadshot(Player player, EFT.Ballistics.DamageInfo damageInfo, bool generateWound)
	{
		if (player == null || !TryGetHeadBone(player, out Transform headBone) ||
			!IsFinite(damageInfo.HitPoint) || !IsFinite(damageInfo.Direction) || damageInfo.Direction.sqrMagnitude < 0.0001f ||
			!RagdollCoordinator.TryGetInteractionTarget(player, out CorpseInteractionTarget target))
		{
			RupturePlugin.Log.LogWarning("Procedural head wound was not armed because the lethal hit or head transform was unavailable.");
			return false;
		}

		for (int index = PendingLethalHeadshots.Count - 1; index >= 0; index--)
		{
			PendingLethalHeadshot existing = PendingLethalHeadshots[index];
			if (string.Equals(existing.ProfileId, target.ProfileId, StringComparison.Ordinal) && existing.DeathSequence == target.DeathSequence)
			{
				PendingLethalHeadshots.RemoveAt(index);
			}
		}

		Vector3 worldDirection = damageInfo.Direction.normalized;
		PendingLethalHeadshots.Add(new PendingLethalHeadshot
		{
			Player = player,
			Target = target,
			ProfileId = target.ProfileId,
			PlayerId = target.PlayerId,
			DeathSequence = target.DeathSequence,
			FireIndex = damageInfo.FireIndex,
			SourceId = damageInfo.SourceId,
			HitCollider = damageInfo.HitCollider,
			HitPointWorld = damageInfo.HitPoint,
			EntryLocal = headBone.InverseTransformPoint(damageInfo.HitPoint),
			DirectionLocal = headBone.InverseTransformDirection(worldDirection).normalized,
			DirectionWorld = worldDirection,
			Projectile = ImpactStateService.Current.Projectile,
			PenetrationPower = damageInfo.PenetrationPower,
			GenerateWound = generateWound,
			ExpiresAt = Time.realtimeSinceStartup + ShotMatchLifetimeSeconds
		});
		return true;
	}

	internal static bool TryCompleteLethalHeadshot(EFT.Ballistics.Shot shot, float massGrams, float impactVelocity, Vector3 impactDirection)
	{
		if (shot == null)
		{
			return false;
		}
		PrunePendingLethalHeadshots();
		int matchIndex = FindMatchingHeadshot(shot);
		if (matchIndex < 0)
		{
			return false;
		}

		PendingLethalHeadshot pending = PendingLethalHeadshots[matchIndex];
		PendingLethalHeadshots.RemoveAt(matchIndex);
		pending.Projectile = ImpactData.ProjectileIdentity(shot);
		return CompleteMatched(pending, massGrams, impactVelocity, shot.BulletDiameterMilimeters,
			(shot.Ammo as EFT.InventoryLogic.Ammo)?.Caliber, shot.HitPoint, impactDirection, detachHeadgear: true);
	}

	internal static void CompleteImpact(Player player, EFT.Ballistics.DamageInfo damage, bool generateWound, ImpactData sample)
	{
		if (!RagdollCoordinator.TryGetInteractionTarget(player, out var target) || !GetState(target).Policy.Remember(sample.Projectile)) return;
		uint seed = HeadWoundBallistics.CreateSeed(player.ProfileId, target.DeathSequence, sample.FireIndex);
		LethalHeadgearDetach.Queue(target.Head, sample.Momentum, damage.Direction, seed ^ 0xA511E9B3u);
		if (!generateWound || !RegisterLethalHeadshot(player, damage, generateWound)) return;
		for (int index = PendingLethalHeadshots.Count - 1; index >= 0; index--)
		{
			PendingLethalHeadshot pending = PendingLethalHeadshots[index];
			if (pending.Player != player) continue;
			PendingLethalHeadshots.RemoveAt(index);
			CompleteMatched(pending, sample.MassGrams, sample.Speed, sample.Diameter, sample.Caliber,
				damage.HitPoint, damage.Direction, detachHeadgear: false);
			return;
		}
	}

	private static bool CompleteMatched(PendingLethalHeadshot pending, float massGrams, float impactVelocity,
		float diameter, string caliber, Vector3 completedHitPoint, Vector3 impactDirection, bool detachHeadgear)
	{
		uint seed = HeadWoundBallistics.CreateSeed(pending.ProfileId, pending.DeathSequence, pending.FireIndex);
		if (detachHeadgear && !GetState(pending.Target).Policy.Remember(pending.Projectile)) return true;
		if (detachHeadgear)
			LethalHeadgearDetach.Queue(pending.Target.Head, LimbImpactPolicy.Momentum(massGrams, impactVelocity),
				pending.DirectionWorld, seed ^ 0xA511E9B3u);
		if (!HeadWoundBallistics.TryCalculate(massGrams, impactVelocity, diameter,
			pending.PenetrationPower, caliber, out HeadWoundShape shape))
		{
			RupturePlugin.Log.LogWarning($"Procedural head wound skipped for {pending.ProfileId}: invalid completed-shot ballistics.");
			return true;
		}

		// CurrentDirection is sampled until EFT.Ballistics.Shot completes. A penetrating or
		// deflected projectile can therefore hold a later trajectory by that time.
		// Geometry and headgear must use the direction captured by the lethal damage
		// callback, which is in the same head-local frame as the entry point.
		Vector3 launchDirection = pending.DirectionWorld;
		float completedDirectionAgreement = IsFinite(impactDirection) && impactDirection.sqrMagnitude > 0.0001f
			? Vector3.Dot(launchDirection, impactDirection.normalized)
			: float.NaN;
		float completedHitPointDifference = IsFinite(completedHitPoint)
			? Vector3.Distance(pending.HitPointWorld, completedHitPoint)
			: float.NaN;
		if (!pending.GenerateWound)
		{
			return true;
		}

		int ordinal = GetState(pending.Target).Policy.Reserve(true);
		if (ordinal == 0) return true;
		uint eventSequence = RagdollCoordinator.NextNetworkEventSequence();
		HeadWoundData packet = new HeadWoundData
		{
			PlayerId = pending.PlayerId,
			ProfileId = pending.ProfileId,
			DeathSequence = pending.DeathSequence,
			EventSequence = eventSequence,
			WoundOrdinal = (byte)ordinal,
			IsLethal = true,
			EntryLocal = pending.EntryLocal,
			DirectionLocal = pending.DirectionLocal,
			EntryRadius = shape.EntryRadius,
			ExitRadius = shape.ExitRadius,
			ConeDepth = shape.ConeDepth,
			IrregularitySeed = seed
		};

		if (RagdollCoordinator.IsNetworkAuthority && pending.PlayerId >= 0 && pending.DeathSequence != 0 && MultiplayerRuntime.Current.HasServer)
		{
			MultiplayerRuntime.Current.Send(ref packet);
		}
		if (!MultiplayerRuntime.Current.IsHeadless)
		{
			if (pending.Player != null && RagdollCoordinator.TryGetInteractionTarget(pending.Player, out CorpseInteractionTarget refreshed))
			{
				pending.Target = refreshed;
			}
			Enqueue(pending.Target, packet);
		}

		RupturePlugin.Log.LogInfo($"Procedural head wound queued: profile={pending.ProfileId}, caliber={caliber ?? "unknown"}, " +
			$"speed={impactVelocity:F1}m/s, mass={massGrams:F2}g, momentum={shape.Momentum:F2}, energy={shape.Energy:F0}J, " +
			$"bulletDiameter={diameter:F1}mm, " +
			$"entryRadius={shape.EntryRadius * 1000f:F1}mm, entryDiameter={shape.EntryRadius * 2000f:F1}mm, " +
			$"exitRadius={shape.ExitRadius * 1000f:F1}mm, exitDiameter={shape.ExitRadius * 2000f:F1}mm, " +
			$"lethalHitWorld={FormatVector(pending.HitPointWorld)}, lethalDirectionWorld={FormatVector(launchDirection)}, " +
			$"completedHitDeltaMm={completedHitPointDifference * 1000f:F1}, completedDirectionDot={completedDirectionAgreement:F3}, seed={seed}.");
		return true;
	}

	internal static bool IsAwaitingLethalHeadshot(CorpseInteractionTarget target)
	{
		foreach (var pending in PendingLethalHeadshots)
			if (pending.DeathSequence == target.DeathSequence && string.Equals(pending.ProfileId, target.ProfileId, StringComparison.Ordinal)) return true;
		return false;
	}

	internal static void CompleteCorpseImpact(CorpseInteractionTarget target, ImpactData sample,
		Vector3 entryLocal, Vector3 directionLocal, float penetration)
	{
		if (RagdollCoordinator.IsRemoteClient || target.Head?.Bone == null || !sample.Valid ||
			!IsFinite(entryLocal) || !IsFinite(directionLocal) || directionLocal.sqrMagnitude < 0.0001f) return;
		CorpseWounds state = GetState(target);
		if (!state.Policy.Remember(sample.Projectile)) return;
		uint seed = HeadWoundBallistics.CreateSeed(target.ProfileId, target.DeathSequence, sample.FireIndex) ^ (uint)sample.Projectile;
		LethalHeadgearDetach.Queue(target.Head, sample.Momentum,
			target.Head.Bone.TransformDirection(directionLocal), seed ^ 0xA511E9B3u);
		if (RuptureConfiguration.EnableDismemberment?.Value != true ||
			!HeadWoundBallistics.TryCalculate(sample.MassGrams, sample.Speed, sample.Diameter, penetration, sample.Caliber, out var shape)) return;
		int ordinal = state.Policy.Reserve(true);
		if (ordinal == 0) return;
		HeadWoundData packet = new HeadWoundData
		{
			PlayerId = target.PlayerId, ProfileId = target.ProfileId, DeathSequence = target.DeathSequence,
			EventSequence = RagdollCoordinator.NextNetworkEventSequence(), WoundOrdinal = (byte)ordinal,
			IsLethal = false, EntryLocal = entryLocal, DirectionLocal = directionLocal.normalized,
			EntryRadius = shape.EntryRadius, ExitRadius = shape.ExitRadius, ConeDepth = shape.ConeDepth, IrregularitySeed = seed
		};
		if (RagdollCoordinator.IsNetworkAuthority && target.PlayerId >= 0 && target.DeathSequence != 0 && MultiplayerRuntime.Current.HasServer)
			MultiplayerRuntime.Current.Send(ref packet);
		Enqueue(target, packet);
	}

	internal static void Enqueue(CorpseInteractionTarget target, HeadWoundData packet)
	{
		if (MultiplayerRuntime.Current.IsHeadless || target.PlayerBody == null || target.VisualRoot == null ||
			packet.WoundOrdinal < 1 || packet.WoundOrdinal > HeadWoundPolicy.MaximumAttempts) return;
		CorpseWounds state = GetState(target);
		if (state.Descriptors.Count >= HeadWoundPolicy.MaximumWounds) return;
		if (!state.Order.TryQueue(packet.WoundOrdinal)) return;
		var work = new WoundWorkItem { Target = target, Packet = packet, State = state,
			QueuedAt = Time.realtimeSinceStartup, ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds };
		PendingWounds.Enqueue(work);
	}

	internal static void Update()
	{
		PrunePendingLethalHeadshots();
		if ((_activeWork == null && PendingWounds.Count == 0) || MultiplayerRuntime.Current.IsHeadless) return;
		WoundWorkItem work = _activeWork ?? PendingWounds.Dequeue();
		string key = CreateKey(work.Packet.ProfileId, work.Packet.PlayerId, work.Packet.DeathSequence);
		if (!Corpses.TryGetValue(key, out CorpseWounds state) || !ReferenceEquals(state, work.State) || !state.Order.Contains(work.Packet.WoundOrdinal))
		{ ReleaseBuild(work); return; }
		if (RagdollCoordinator.TryGetInteractionTarget(work.Packet.ProfileId, work.Packet.PlayerId, out var refreshed) &&
			refreshed.DeathSequence == work.Packet.DeathSequence) work.Target = refreshed;
		if (work.Target.VisualRoot == null) { Remove(work.Packet.ProfileId, work.Packet.PlayerId, work.Packet.DeathSequence); return; }
		if (state.Descriptors.Count >= HeadWoundPolicy.MaximumWounds)
		{ ReleaseBuild(work); state.Order.Discard(work.Packet.WoundOrdinal); return; }
		if (work.Packet.WoundOrdinal != state.Order.Next)
		{
			// Missing earlier events never allow later cuts to overtake their order.
			if (state.Order.Contains(state.Order.Next) || Time.realtimeSinceStartup < work.ExpiresAt) PendingWounds.Enqueue(work);
			else { state.Order.Discard(work.Packet.WoundOrdinal); RupturePlugin.Log.LogWarning($"Head wound {work.Packet.WoundOrdinal} expired waiting for ordinal {state.Order.Next}."); }
			return;
		}
		ApplyResult result;
		string failure;
		try
		{
			if (Time.realtimeSinceStartup >= work.ExpiresAt) { result = ApplyResult.Rejected; failure = "head wound expired before adoption"; }
			else result = TryApply(work, key, out failure);
		}
		catch (Exception exception) { result = ApplyResult.Rejected; failure = $"head acquisition threw {exception.GetType().Name}: {exception.Message}"; }
		if (result == ApplyResult.Waiting && Time.realtimeSinceStartup < work.ExpiresAt)
		{
			if (work.Build != null) _activeWork = work;
			else PendingWounds.Enqueue(work);
			return;
		}
		if (result == ApplyResult.Applied && work.Build?.Job != null && work.Build.Job.UsedFallback)
			RupturePlugin.Log.LogWarning($"Head wound recovered: profile={work.Packet.ProfileId}, ordinal={work.Packet.WoundOrdinal}, " +
				$"mode={work.Build.Job.EntryResolution}, builds={work.Build.Job.GenerationAttempts}, " +
				$"entrySnapMm={Vector3.Distance(work.Packet.EntryLocal, work.Build.Job.Entry) * 1000f:F1}, " +
				$"angleDeg={Vector3.Angle(work.Packet.DirectionLocal, work.Build.Job.ResolvedDirection):F1}.");
		ReleaseBuild(work);
		state.Order.Complete(work.Packet.WoundOrdinal);
		if (result != ApplyResult.Applied)
		{
			if (work.Packet.IsLethal) PulsatingBloodStreamSystem.RejectHeadExit(work.Packet.ProfileId, work.Packet.DeathSequence);
			string diagnostic = failure ?? "generation failed";
			if (result == ApplyResult.Rejected || ReportedUnavailableResources.Add(diagnostic))
				RupturePlugin.Log.LogWarning($"Procedural head wound skipped for {work.Packet.ProfileId}, ordinal={work.Packet.WoundOrdinal}: {diagnostic}.");
		}
	}

	internal static void LogStatus()
	{
		if (!RupturePlugin.Log.IsDebugEnabled) return;
		bool materialReady = HeadWoundMaterialProvider.TryGet(out Material material, out string materialFailure);
		RupturePlugin.Log.LogInfo($"RPT procedural head wounds: dismembermentEnabled={RuptureConfiguration.EnableDismemberment?.Value == true}, " +
			$"helmetThrowEnabled={RuptureConfiguration.AutomaticHeadgearDetachment?.Value != false}, headless={MultiplayerRuntime.Current.IsHeadless}, " +
			$"authority={RagdollCoordinator.IsNetworkAuthority}, remoteClient={RagdollCoordinator.IsRemoteClient}, " +
			$"materialReady={materialReady}, material={(material != null ? material.name : "none")}, " +
			$"resourceStatus={(materialReady ? "ready" : materialFailure ?? "retry pending")}, " +
			$"maximumWoundsPerHead={HeadWoundPolicy.MaximumWounds}, trackedHeads={Corpses.Count}, pendingShotMatches={PendingLethalHeadshots.Count}, queuedWounds={PendingWounds.Count}, building={_activeWork != null}, activeWounds={ActiveWounds.Count}.");
	}

	internal static void Reset()
	{
		CancelActive();
		PendingLethalHeadshots.Clear();
		PendingWounds.Clear();
		Corpses.Clear();
		ReportedUnavailableResources.Clear();
		HeadWoundLifetime[] active = ActiveWounds.ToArray();
		ActiveWounds.Clear();
		foreach (HeadWoundLifetime wound in active)
		{
			if (wound != null)
			{
				UnityEngine.Object.Destroy(wound.gameObject);
			}
		}
		HeadWoundMaterialProvider.Reset();
	}

	internal static void NotifyDestroyed(HeadWoundLifetime lifetime, string key)
	{
		ActiveWounds.Remove(lifetime);
		if (!string.IsNullOrEmpty(key))
		{
			if (Corpses.TryGetValue(key, out var state) && ReferenceEquals(state.Lifetime, lifetime))
			{
				state.Revision++;
				state.Descriptors.Clear();
				state.Source = null;
				state.Lifetime = null;
				// Admission belongs to the corpse skeleton, never to graphical success.
			}
		}
	}

	internal static void Remove(string profile, int playerId, uint sequence)
	{
		string key = CreateKey(profile, playerId, sequence);
		if (!Corpses.TryGetValue(key, out var state)) return;
		Corpses.Remove(key);
		CancelActive(state);
		if (state.Lifetime != null) UnityEngine.Object.Destroy(state.Lifetime.gameObject);
	}

	internal static List<BodyRenderer> CaptureDecalRenderers(Player player)
	{
		if (player?.PlayerBody == null) return null;
		try
		{
			var renderers = new List<BodyRenderer>();
			player.PlayerBody.GetBodyRenderersNonAlloc(renderers);
			return renderers;
		}
		catch { return null; }
	}

	private static ApplyResult TryApply(WoundWorkItem work, string key, out string failure)
	{
		failure = null;
		if (work.Build != null && !work.Build.Job.Completed) return ApplyResult.Waiting;
		long totalStarted = Stopwatch.GetTimestamp();
		long managedBytesBefore = RupturePlugin.Log.IsDebugEnabled ? GC.GetTotalMemory(forceFullCollection: false) : 0L;
		long materialStarted = Stopwatch.GetTimestamp();
		if (!HeadWoundMaterialProvider.TryGet(out Material woundMaterial, out failure))
		{
			return ApplyResult.Waiting;
		}
		double materialMilliseconds = ElapsedMilliseconds(materialStarted);
		CorpseWounds state = Corpses[key];
		Transform headBone = work.Target.Head?.Bone;
		if (headBone == null)
		{
			var retained = CorpseHeadContext.Capture(work.Target.Player, work.Target.PlayerBody);
			retained?.BindIdentity(work.Packet.ProfileId, work.Packet.PlayerId, work.Packet.DeathSequence);
			headBone = retained?.Bone;
		}
		if (headBone == null)
		{
			failure = "the retained head bone is unavailable";
			return ApplyResult.Waiting;
		}
		if (!TryGetHeadRenderers(work.Target.PlayerBody, out Renderer[] headRenderers))
		{
			failure = "the retained head skin renderers are unavailable";
			return ApplyResult.Waiting;
		}

		if (work.Build == null)
		{
			if (!TryCaptureHead(work, state, headBone, headRenderers, out var captured, out failure)) return ApplyResult.Rejected;
			work.Build = captured;
			return ApplyResult.Waiting;
		}
		HeadBuild build = work.Build;
		HeadWoundGeometryJob job = build.Job;
		if (!job.Completed) return ApplyResult.Waiting;
		if (!job.Succeeded || job.Cancelled) { failure = job.Failure ?? "head geometry was cancelled"; return ApplyResult.Rejected; }
		if (!ReferenceEquals(build.State, state) || build.Revision != state.Revision ||
			build.Head != headBone || build.VisualRoot != work.Target.VisualRoot || state.Order.Next != work.Packet.WoundOrdinal)
		{ failure = "head geometry result belongs to a retired corpse or wound revision"; return ApplyResult.Rejected; }
		HeadCandidate candidate = build.Candidates[job.SourceIndex];
		SkinnedMeshRenderer sourceRenderer = candidate.Renderer;
		if (sourceRenderer == null || sourceRenderer.sharedMesh != candidate.NativeMesh || candidate.Bones.Any(bone => bone == null) ||
			!sourceRenderer.bones.SequenceEqual(candidate.Bones))
		{
			failure = "captured head renderer or skeleton changed before adoption";
			// Corpse handoff or equipment changes can swap the skin while the worker
			// runs. Rebuild from the current renderer within the same wound lifetime.
			if (work.StaleRecaptures++ >= MaximumStaleRecaptures) return ApplyResult.Rejected;
			ReleaseBuild(work);
			return ApplyResult.Waiting;
		}
		headRenderers = build.HeadRenderers;
		Transform[] sourceBones = candidate.Bones;
		Vector3 entry = job.Entry, exit = job.Exit;
		float exitDistance = job.ExitDistance;
		string entryResolution = job.EntryResolution;
		bool terminalSurfaceResolved = job.TerminalSurfaceResolved;
		double bakeMilliseconds = build.BakeMilliseconds, rayMilliseconds = job.RayMilliseconds;
		double acquisitionMilliseconds = build.AcquisitionMilliseconds;
		int sourceVertexCount = job.Source.Data.SourceVertices.Count;
		Material[] sourceMaterials = sourceRenderer.sharedMaterials;
		Material[] resultMaterials = NormalizeMaterials(sourceMaterials, job.Workspace.SubMeshCount);
		Mesh bakedHead = null, cavityMesh = null;
		GameObject replacement = null;
		bool lifetimeOwnsMeshes = false;
		double meshApplicationMilliseconds = 0d;
		ProceduralWoundMetrics geometryMetrics = job.Metrics;
		ProceduralWoundDescriptor descriptor = job.Descriptor;
		try
		{
			long meshApplicationStarted = Stopwatch.GetTimestamp();
			bakedHead = new Mesh { name = $"RPT Filtered Head Wound {work.Packet.ProfileId}" };
			job.Workspace.ApplyFilteredIndicesOnMainThread(bakedHead, job.HeadBounds);
			cavityMesh = job.Workspace.CreateCavityMeshOnMainThread($"RPT Head Wound Cavity {work.Packet.ProfileId}", job.CavityBounds);
			meshApplicationMilliseconds = ElapsedMilliseconds(meshApplicationStarted);
			// Pure-data validation completed before any Unity mesh was created. Keep a
			// cheap upload/layout gate before changing the visible corpse.
			if (bakedHead.vertexCount != job.Workspace.SourceVertices.Count ||
				cavityMesh.vertexCount != job.Workspace.CavityVertices.Count || resultMaterials.Length != bakedHead.subMeshCount)
			{ failure = "head mesh upload did not preserve the validated layout"; return ApplyResult.Rejected; }
			state.Source = job.Source; state.SourceRenderer = sourceRenderer; state.NativeMesh = candidate.NativeMesh;
			state.HeadRenderers = headRenderers; state.Bones = sourceBones; state.Bindposes = candidate.Bindposes;

			bakedHead.bindposes = state.Bindposes;
			if (state.Lifetime != null)
			{
				state.Lifetime.ReplaceMeshes(bakedHead, cavityMesh);
				lifetimeOwnsMeshes = true;
				state.Descriptors.Add(descriptor);
				state.Revision++;
				PulsatingBloodStreamSystem.StartPostMortemHeadWound(work.Target, work.Packet, headBone, entry, exit,
					job.ResolvedDirection, terminalSurfaceResolved);
				int decals = PaintVanillaBloodDecals(state.Lifetime, headBone.TransformPoint(entry), headBone.TransformPoint(exit),
					headBone.TransformDirection(job.ResolvedDirection).normalized, terminalSurfaceResolved);
				if (work.Packet.IsLethal) PulsatingBloodStreamSystem.ResolveHeadExit(work.Packet, headBone, exit,
					job.ResolvedDirection, terminalSurfaceResolved, entry);
				if (RupturePlugin.Log.IsDebugEnabled) RupturePlugin.Log.LogInfo($"Procedural head wound applied: profile={work.Packet.ProfileId}, ordinal={work.Packet.WoundOrdinal}, " +
					$"wounds={state.Descriptors.Count}, headVertices={bakedHead.vertexCount}, cavityVertices={cavityMesh.vertexCount}, " +
					$"queuedBloodDecals={decals}, captureMainMs={build.CaptureMilliseconds:F2}, geometryWorkerMs={job.WorkerMilliseconds:F2}, applyMainMs={ElapsedMilliseconds(totalStarted):F2}, endToEndMs={(Time.realtimeSinceStartup - work.QueuedAt) * 1000:F2}.");
				return ApplyResult.Applied;
			}

			bool[] originalRendererStates = new bool[headRenderers.Length];
			for (int index = 0; index < headRenderers.Length; index++)
			{
				originalRendererStates[index] = headRenderers[index] != null && headRenderers[index].enabled;
			}

			replacement = new GameObject("RPT Procedural Head Wound");
			replacement.layer = sourceRenderer.gameObject.layer;
			replacement.transform.SetParent(headBone, worldPositionStays: false);
			replacement.transform.localPosition = Vector3.zero;
			replacement.transform.localRotation = Quaternion.identity;
			replacement.transform.localScale = Vector3.one;
			bakedHead.bindposes = state.Bindposes;
			SkinnedMeshRenderer renderer = replacement.AddComponent<SkinnedMeshRenderer>();
			renderer.bones = sourceBones;
			HeadWoundSkinning.SetMesh(renderer, bakedHead, headBone);
			renderer.sharedMaterials = resultMaterials;
			renderer.quality = sourceRenderer.quality;
			renderer.updateWhenOffscreen = sourceRenderer.updateWhenOffscreen;
			CopyRendererSettings(sourceRenderer, renderer);
			int copiedPropertyBlocks = RendererSource.CopyPropertyBlocks(sourceRenderer, renderer,
				Mathf.Min(sourceMaterials?.Length ?? 0, resultMaterials.Length));
			renderer.enabled = true;
			renderer.forceRenderingOff = false;
			Rupture.Dismemberment.Classes.Limbs.LimbDecalTextureTransfer.CopyExisting(sourceRenderer, renderer, out _);

			GameObject cavityObject = new GameObject("RPT Procedural Wound Cavity");
			cavityObject.layer = sourceRenderer.gameObject.layer;
			cavityObject.transform.SetParent(replacement.transform, worldPositionStays: false);
			cavityObject.transform.localPosition = Vector3.zero;
			cavityObject.transform.localRotation = Quaternion.identity;
			cavityObject.transform.localScale = Vector3.one;
			cavityObject.AddComponent<MeshFilter>().sharedMesh = cavityMesh;
			MeshRenderer cavityRenderer = cavityObject.AddComponent<MeshRenderer>();
			cavityRenderer.sharedMaterial = woundMaterial;
			CopyRendererSettings(sourceRenderer, cavityRenderer);
			cavityRenderer.enabled = true;
			cavityRenderer.forceRenderingOff = false;

			HeadWoundLifetime lifetime = replacement.AddComponent<HeadWoundLifetime>();
			lifetime.Initialize(key, new[] { bakedHead, cavityMesh }, headRenderers, originalRendererStates,
				new Renderer[] { renderer, cavityRenderer });
			lifetimeOwnsMeshes = true;
			ActiveWounds.Add(lifetime);
			state.Lifetime = lifetime;
			state.Descriptors.Add(descriptor);
			state.Revision++;
			PulsatingBloodStreamSystem.StartPostMortemHeadWound(work.Target, work.Packet, headBone, entry, exit,
				job.ResolvedDirection, terminalSurfaceResolved);
			Vector3 resolvedEntryWorld = headBone.TransformPoint(entry);
			Vector3 resolvedExitWorld = headBone.TransformPoint(exit);
			Vector3 decalDirectionWorld = headBone.TransformDirection(job.ResolvedDirection).normalized;
			if (work.Packet.IsLethal) PulsatingBloodStreamSystem.ResolveHeadExit(work.Packet, headBone, exit,
				job.ResolvedDirection, terminalSurfaceResolved, entry);
			int vanillaBloodDecalCount = PaintVanillaBloodDecals(state.Lifetime, resolvedEntryWorld,
				resolvedExitWorld, decalDirectionWorld, terminalSurfaceResolved);
			for (int index = 0; index < headRenderers.Length; index++)
			{
				Renderer headRenderer = headRenderers[index];
				if (headRenderer != null)
				{
					headRenderer.enabled = false;
				}
			}

			if (RupturePlugin.Log.IsDebugEnabled)
			{
				double totalMilliseconds = ElapsedMilliseconds(totalStarted);
				long managedAllocationDelta = Math.Max(0L, GC.GetTotalMemory(forceFullCollection: false) - managedBytesBefore);
				double queueMilliseconds = Mathf.Max(0f, Time.realtimeSinceStartup - work.QueuedAt) * 1000.0;
				Vector3 suppliedEntryWorld = headBone.TransformPoint(work.Packet.EntryLocal);
				float entrySnapDistance = Vector3.Distance(work.Packet.EntryLocal, entry);
				RupturePlugin.Log.LogInfo($"Procedural head wound applied: profile={work.Packet.ProfileId}, renderer={sourceRenderer.name}, " +
					$"suppliedEntryWorld={FormatVector(suppliedEntryWorld)}, resolvedEntryWorld={FormatVector(resolvedEntryWorld)}, " +
					$"resolvedExitWorld={FormatVector(resolvedExitWorld)}, entrySnapMm={entrySnapDistance * 1000f:F2}, " +
					$"entryResolution={entryResolution}, terminalResolution={(terminalSurfaceResolved ? "surface" : "cone-extent")}, " +
					$"directionWorld={FormatVector(decalDirectionWorld)}, queuedBloodDecals={vanillaBloodDecalCount}, " +
					$"replacementEnabled={renderer.enabled && cavityRenderer.enabled}, skinningBones={sourceBones.Length}, " +
					$"sourceBoundsRoot={sourceRenderer.rootBone?.name ?? "none"}, replacementBoundsRoot={headBone.name}, " +
					$"rendererPropertyBlocks={copiedPropertyBlocks}, sourceVertices={sourceVertexCount}, " +
					$"resultHeadVertices={bakedHead.vertexCount}, sourceTriangles={geometryMetrics.SourceTriangleCount}, " +
					$"candidateTriangles={geometryMetrics.CandidateTriangleCount}, intersectedSourceTriangles={geometryMetrics.IntersectedSourceTriangleCount}, " +
					$"clippedTriangles={geometryMetrics.ClippedTriangleCount}, removedFragments={geometryMetrics.RemovedTriangleCount}, " +
					$"entryCoverageMm={geometryMetrics.EntryOpeningCoverageRadius * 1000f:F3}, exitCoverageMm={geometryMetrics.ExitOpeningCoverageRadius * 1000f:F3}, " +
					$"entryLipRadiusMm={geometryMetrics.EntryRimOuterRadius * 1000f:F3}, exitBurstRadiusMm={geometryMetrics.ExitRimOuterRadius * 1000f:F3}, " +
					$"minimumOverlapMm={geometryMetrics.MinimumRimCoverageMargin * 1000f:F3}, rimVertices={geometryMetrics.RimVertexCount}, rimTriangles={geometryMetrics.RimTriangleCount}, " +
					$"boundaryComponents={geometryMetrics.BoundaryComponentCount}, openBoundaryComponents={geometryMetrics.OpenBoundaryComponentCount}, " +
					$"solidCanalSpans={geometryMetrics.SolidCanalSpanCount}, entryLips={geometryMetrics.EntryLipCount}, " +
					$"intermediateLips={geometryMetrics.IntermediateLipCount}, exitFlaps={geometryMetrics.ExitFlapCount}, " +
					$"entryRimFacing={geometryMetrics.MinimumEntryRimFacingDot:F3}, exitRimFacing={geometryMetrics.MinimumExitRimFacingDot:F3}, " +
					$"entrySurfaceSamples={geometryMetrics.EntryRimSurfaceHitCount}, exitSurfaceSamples={geometryMetrics.ExitRimSurfaceHitCount}, " +
					$"cavityVertices={geometryMetrics.CavityVertexCount}, cavityTriangles={geometryMetrics.CavityTriangleCount}, " +
					$"entryExitDistance={exitDistance:F3}m, queueMs={queueMilliseconds:F2}, materialMs={materialMilliseconds:F2}, " +
					$"bakeMs={bakeMilliseconds:F2}, rayMs={rayMilliseconds:F2}, acquisitionMs={acquisitionMilliseconds:F2}, " +
					$"classificationMs={geometryMetrics.TriangleClassificationMilliseconds:F2}, cavityGenerationMs={geometryMetrics.CavityGenerationMilliseconds:F2}, " +
					$"meshApplicationMs={meshApplicationMilliseconds:F2}, captureMainMs={build.CaptureMilliseconds:F2}, geometryWorkerMs={job.WorkerMilliseconds:F2}, validationWorkerMs={job.ValidationMilliseconds:F2}, applyMainMs={totalMilliseconds:F2}, managedDeltaBytes={managedAllocationDelta}.");
			}
			return ApplyResult.Applied;
		}
		catch (Exception exception)
		{
			if (lifetimeOwnsMeshes)
			{
				RupturePlugin.Log.LogWarning($"Head wound committed, but follow-up effects failed: {exception.Message}");
				return ApplyResult.Applied;
			}
			if (replacement != null)
			{
				UnityEngine.Object.Destroy(replacement);
			}
			failure = $"specialized head-wound generation threw {exception.GetType().Name}: {exception.Message}";
			return ApplyResult.Rejected;
		}
		finally
		{
			if (!lifetimeOwnsMeshes)
			{
				if (bakedHead != null) UnityEngine.Object.Destroy(bakedHead);
				if (cavityMesh != null) UnityEngine.Object.Destroy(cavityMesh);
			}
		}
	}

	private static int FindMatchingHeadshot(EFT.Ballistics.Shot shot)
	{
		for (int index = PendingLethalHeadshots.Count - 1; index >= 0; index--)
		{
			PendingLethalHeadshot pending = PendingLethalHeadshots[index];
			if (pending.FireIndex != shot.FireIndex || !HitColliderMatches(pending.HitCollider, shot.HitCollider))
			{
				continue;
			}
			if (!string.IsNullOrEmpty(pending.SourceId) && shot.Ammo is Item shotAmmo &&
				!string.Equals(pending.SourceId, shotAmmo.TemplateId, StringComparison.Ordinal))
			{
				continue;
			}
			Player hitPlayer = shot.HitCollider?.GetComponentInParent<Player>();
			if (hitPlayer != null && pending.Player != null && hitPlayer != pending.Player)
			{
				continue;
			}
			if (IsFinite(shot.HitPoint) && Vector3.Distance(pending.HitPointWorld, shot.HitPoint) > MaximumHitPointDifference)
			{
				continue;
			}
			return index;
		}
		return -1;
	}

	private static bool HitColliderMatches(Collider expected, Collider actual)
	{
		if (expected == actual)
		{
			return true;
		}
		if (expected == null || actual == null)
		{
			return false;
		}
		return actual.transform.IsChildOf(expected.transform) || expected.transform.IsChildOf(actual.transform);
	}

	private static void PrunePendingLethalHeadshots()
	{
		float now = Time.realtimeSinceStartup;
		for (int index = PendingLethalHeadshots.Count - 1; index >= 0; index--)
		{
			if (PendingLethalHeadshots[index].ExpiresAt <= now || PendingLethalHeadshots[index].Player == null)
			{
				PendingLethalHeadshot expired = PendingLethalHeadshots[index];
				RupturePlugin.Log.LogWarning($"Procedural head wound skipped for {expired.ProfileId ?? expired.PlayerId.ToString()}: no completed EFT.Ballistics.Shot matched " +
					$"fireIndex={expired.FireIndex}, sourceId={expired.SourceId ?? "unknown"} before expiry.");
				PendingLethalHeadshots.RemoveAt(index);
			}
		}
	}

	private static int PaintVanillaBloodDecals(HeadWoundLifetime lifetime, Vector3 entryWorld, Vector3 exitWorld,
		Vector3 directionWorld, bool paintExit)
	{
		if (MultiplayerRuntime.Current.IsHeadless || RuptureConfiguration.EnableBloodEffects?.Value != true ||
			lifetime == null || directionWorld.sqrMagnitude < 0.0001f) return 0;
		Transform anchor = lifetime.transform;
		lifetime.QueueBloodDecals(anchor.InverseTransformPoint(entryWorld), anchor.InverseTransformPoint(exitWorld),
			anchor.InverseTransformDirection(directionWorld), paintExit);
		return paintExit ? 2 : 1;
	}

	private static bool TryGetHeadBone(Player player, out Transform headBone)
	{
		headBone = player?.PlayerBones?.Head?.Original;
		return headBone != null;
	}

	private static bool TryGetHeadRenderers(PlayerBody playerBody, out Renderer[] renderers)
	{
		renderers = null;
		if (playerBody?.BodySkins == null || !playerBody.BodySkins.TryGetValue(EBodyModelPart.Head, out var headSkin) || headSkin == null)
		{
			return false;
		}
		renderers = headSkin.GetRenderers()?.Where(renderer => renderer != null).ToArray();
		return renderers != null && renderers.Length != 0;
	}

	private static bool TryBuildCurrentBindposes(Transform[] bones, Transform replacementTransform,
		out Matrix4x4[] bindposes, out string failure)
	{
		bindposes = null;
		failure = null;
		if (bones == null || bones.Length == 0 || replacementTransform == null)
		{
			failure = "the current corpse skinning pose is unavailable";
			return false;
		}
		Matrix4x4 replacementLocalToWorld = replacementTransform.localToWorldMatrix;
		bindposes = new Matrix4x4[bones.Length];
		for (int index = 0; index < bones.Length; index++)
		{
			if (bones[index] == null)
			{
				failure = $"the current corpse skinning pose lost bone {index}";
				bindposes = null;
				return false;
			}
			bindposes[index] = bones[index].worldToLocalMatrix * replacementLocalToWorld;
		}
		return true;
	}

	private static Material[] NormalizeMaterials(Material[] source, int subMeshCount)
	{
		Material[] result = new Material[Mathf.Max(1, subMeshCount)];
		for (int index = 0; index < result.Length; index++)
		{
			result[index] = source != null && source.Length != 0 ? source[Mathf.Min(index, source.Length - 1)] : null;
		}
		return result;
	}

	private static void CopyRendererSettings(Renderer source, Renderer destination)
	{
		destination.shadowCastingMode = source.shadowCastingMode;
		destination.receiveShadows = source.receiveShadows;
		destination.lightProbeUsage = source.lightProbeUsage;
		destination.reflectionProbeUsage = source.reflectionProbeUsage;
		destination.probeAnchor = source.probeAnchor;
		destination.lightProbeProxyVolumeOverride = source.lightProbeProxyVolumeOverride;
		destination.motionVectorGenerationMode = source.motionVectorGenerationMode;
		destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
		destination.renderingLayerMask = source.renderingLayerMask;
		destination.rendererPriority = source.rendererPriority;
		destination.lightmapIndex = source.lightmapIndex;
		destination.realtimeLightmapIndex = source.realtimeLightmapIndex;
		destination.lightmapScaleOffset = source.lightmapScaleOffset;
		destination.realtimeLightmapScaleOffset = source.realtimeLightmapScaleOffset;
		destination.sortingLayerID = source.sortingLayerID;
		destination.sortingOrder = source.sortingOrder;
	}

	private static string CreateKey(string profileId, int playerId, uint deathSequence)
	{
		return $"{profileId ?? playerId.ToString()}:{deathSequence}";
	}

	private static double ElapsedMilliseconds(long started)
	{
		return (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
	}

	private static string FormatVector(Vector3 value)
	{
		return $"({value.x:F3},{value.y:F3},{value.z:F3})";
	}

	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
			!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}
