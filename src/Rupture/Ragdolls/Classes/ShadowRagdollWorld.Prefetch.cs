using System;
using System.Collections.Generic;
using System.Diagnostics;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.Ragdolls.Classes;

/// <summary>
/// Proximity-cache environment prefetch. Bots warm the environment around themselves while they are being shot and in the
/// death frame, so the corpse build, its bounded readiness wait and the rig corridor refresh find their geometry already
/// captured instead of extracting it on the main thread. Everything here is optional: a backend without
/// <see cref="IExternalEnvironmentPrefetchRuntime"/>, full-prebake mode, or a prefetch fault leaves the synchronous
/// <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> path in charge.
/// </summary>
internal static partial class ShadowRagdollWorld
{
	#region Constants
	private const float BotPrefetchTimeToLiveSeconds = 30f;
	private const float BotPrefetchRefreshIntervalSeconds = 0.5f;
	private const float BotPrefetchRefreshDistance = 1f;
	private const float RigPrefetchTimeToLiveSeconds = 2f;
	/// <summary>Longest a built-ready corpse stays on native EFT physics while its prefetch is pending; afterwards it builds through the synchronous path.</summary>
	private const float PrefetchBuildMaximumWaitSeconds = 0.35f;
	private const double PrefetchMinimumPumpMilliseconds = 0.5d;
	private const float PrefetchPruneIntervalSeconds = 1f;
	private const float PrefetchFallbackCenterHeight = 0.9f;
	/// <summary>Longest a settled corpse, a parked weapon or a detached gib waits, parked, for the environment capture its wake needs; afterwards the wake acquires synchronously.</summary>
	private const float WakeEnvironmentMaximumWaitSeconds = 0.25f;
	/// <summary>Prefetch consumer ids with this bit set are bot prefetches: <c>0x8000_0000 | (Player.Id &amp; 0x3FFF_FFFF)</c>. Rig groups are small sequence numbers and never collide.</summary>
	private const uint BotPrefetchConsumerFlag = 0x8000_0000u;
	private const uint BotPrefetchConsumerMask = 0x3FFF_FFFFu;
	/// <summary>Rig corridor prefetches: <c>0xC000_0000 | ShadowRig.ExternalGroup</c>, disjoint from bot ids because <see cref="BotPrefetchConsumerMask"/> clears bit 30.</summary>
	private const uint RigPrefetchConsumerFlag = 0xC000_0000u;
	/// <summary>How far the tracked body center may leave a captured corridor before the rig counts as about to leave captured space: bodies reach about this far from the center.</summary>
	private const float CorridorCoverageDistance = EnvironmentRadius * 0.5f;
	#endregion

	private enum RigPrefetchAction
	{
		/// <summary>Keep the current corridor; a prefetch is in flight or was just requested.</summary>
		Wait,
		/// <summary>Acquire synchronously: no prefetch applies, it failed, or the rig is about to leave captured space.</summary>
		Acquire,
		/// <summary>Hand the finished prefetch to the rig group, then acquire; the acquisition only reuses.</summary>
		PromoteThenAcquire
	}

	private sealed class BotPrefetch
	{
		internal uint ConsumerId;
		internal Player Player;
		/// <summary>The corridor last requested for the bot; a build whose body center is still inside it acquires exactly this corridor, so the acquisition only reuses.</summary>
		internal Vector3 Center;
		internal Vector3 SweepEnd;
		internal float RequestedAt;
	}

	private static readonly Dictionary<string, BotPrefetch> BotPrefetches = new Dictionary<string, BotPrefetch>(StringComparer.Ordinal);
	private static readonly List<string> PrefetchPruneScratch = new List<string>(8);
	private static float _nextPrefetchPruneAt;
	private static bool _prefetchDisabled;
	/// <summary>Acquisitions that bound an already captured corridor without extracting anything on the main thread.</summary>
	private static long _environmentAsyncAcquisitions;
	/// <summary>Synchronous <see cref="IExternalPhysicsRuntime.EnsureEnvironment"/> calls made after the non-blocking route was unavailable, failed or ran out of its bounded wait (builds excluded).</summary>
	private static long _environmentSyncFallbacks;
	/// <summary>Synchronous acquisitions of callers that never try the non-blocking route (an independent weapon wake that must move now); not a fallback of anything.</summary>
	private static long _environmentSyncDirect;

	/// <summary>The backend's prefetch extension for this raid, or null when prefetch does not apply (no extension, full prebake, disabled after a fault).</summary>
	private static IExternalEnvironmentPrefetchRuntime PrefetchRuntime
	{
		get
		{
			IExternalPhysicsRuntime runtime = _externalRuntime;
			return _prefetchDisabled || runtime == null || runtime.IsFullPrebake ? null : runtime as IExternalEnvironmentPrefetchRuntime;
		}
	}

	#region Triggers
	/// <summary>
	/// Per-hit trigger: a bot that takes damage may die next, so warm the environment around it. Refreshes at most every
	/// <see cref="BotPrefetchRefreshIntervalSeconds"/> and only after the bot moved <see cref="BotPrefetchRefreshDistance"/> or half the lifetime passed.
	/// </summary>
	internal static void PrefetchForDamagedBot(Player player)
	{
		if (player == null || !player.IsAI) return;
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		string profileId = player.ProfileId;
		if (runtime == null || string.IsNullOrEmpty(profileId)) return;
		if (!TryGetPrefetchCenter(player, out Vector3 center)) return;
		float now = Time.realtimeSinceStartup;
		if (BotPrefetches.TryGetValue(profileId, out BotPrefetch existing))
		{
			float age = now - existing.RequestedAt;
			bool moved = (center - existing.Center).sqrMagnitude > BotPrefetchRefreshDistance * BotPrefetchRefreshDistance;
			if (age < BotPrefetchRefreshIntervalSeconds || (!moved && age < BotPrefetchTimeToLiveSeconds * 0.5f)) return;
		}
		RequestBotPrefetch(runtime, player, profileId, center, PredictEnvironmentSweepEnd(center, player.Velocity), now);
	}

	/// <summary>
	/// Death-frame trigger: requests the corridor the corpse build will acquire and replaces the region the bot's damage
	/// prefetch held (the backend returns the state to pending for a changed corridor). The corridor comes from the pending
	/// ragdoll's native center body with its own velocity, the exact inputs of <see cref="TryBuild"/>, so the build finds it
	/// captured; a bot without a pending ragdoll falls back to its pelvis and walking velocity.
	/// Bots only: the consumer id is shared with the local player's casing area prefetch, so a human player's death never requests one.
	/// </summary>
	internal static void RequestDeathPrefetch(Player player)
	{
		if (player == null || !player.IsAI) return;
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		string profileId = player.ProfileId;
		if (runtime == null || string.IsNullOrEmpty(profileId)) return;
		float now = Time.realtimeSinceStartup;
		if (TryFindPendingBuild(profileId, out PendingBuild pending) && TryGetBuildCorridor(pending, out Vector3 buildCenter, out Vector3 buildSweepEnd))
		{
			RequestBotPrefetch(runtime, player, profileId, buildCenter, buildSweepEnd, now);
			return;
		}
		if (!TryGetPrefetchCenter(player, out Vector3 center)) return;
		RequestBotPrefetch(runtime, player, profileId, center, PredictEnvironmentSweepEnd(center, player.Velocity), now);
	}

	private static void RequestBotPrefetch(IExternalEnvironmentPrefetchRuntime runtime, Player player, string profileId,
		Vector3 center, Vector3 sweepEnd, float now)
	{
		if (!BotPrefetches.TryGetValue(profileId, out BotPrefetch prefetch))
		{
			prefetch = new BotPrefetch { ConsumerId = BotPrefetchConsumerFlag | ((uint)player.Id & BotPrefetchConsumerMask) };
			BotPrefetches.Add(profileId, prefetch);
		}
		prefetch.Player = player;
		prefetch.Center = center;
		prefetch.SweepEnd = sweepEnd;
		prefetch.RequestedAt = now;
		RequestPrefetch(runtime, prefetch.ConsumerId, center, sweepEnd, BotPrefetchTimeToLiveSeconds);
	}

	/// <summary>The pelvis is what the corpse build tracks; the bot's root plus a standing offset stands in when the skeleton is unavailable. False when the bot has no transform yet.</summary>
	private static bool TryGetPrefetchCenter(Player player, out Vector3 center)
	{
		Transform pelvis = player.PlayerBones?.Pelvis?.Original;
		Transform root = player.Transform?.Original;
		center = pelvis != null ? pelvis.position : root != null ? root.position + Vector3.up * PrefetchFallbackCenterHeight : Vector3.zero;
		return pelvis != null || root != null;
	}

	private static bool TryFindPendingBuild(string profileId, out PendingBuild pending)
	{
		for (int index = 0; index < PendingBuilds.Count; index++)
		{
			pending = PendingBuilds[index];
			if (pending.Ragdoll?._owner is Corpse corpse && string.Equals(corpse.PlayerProfileID, profileId, StringComparison.Ordinal)) return true;
		}
		pending = null;
		return false;
	}

	/// <summary>
	/// The corridor <see cref="TryBuild"/> acquires for a ragdoll that is still native. The build tracks the weapon body while the
	/// corpse holds one (<see cref="GetEnvironmentTrackingBody"/>: the rig is not active yet), else its center body (the first
	/// pelvis-path core body, else the first core body, as <see cref="RegisterSpecialBody"/> picks it); the corridor starts at that
	/// body's center of mass and is swept with the body's own velocity through <see cref="PredictEnvironmentSweepEnd"/>. False when
	/// the native bodies are gone.
	/// </summary>
	private static bool TryGetBuildCorridor(PendingBuild pending, out Vector3 center, out Vector3 sweepEnd)
	{
		center = sweepEnd = Vector3.zero;
		Rigidbody body = pending.Ragdoll?.WeaponRigidbody;
		if (body == null || body.isKinematic)
		{
			if (pending.NativeCenterBody == null) pending.NativeCenterBody = ResolveNativeCenterBody(pending.Ragdoll);
			body = pending.NativeCenterBody;
		}
		if (body == null) return false;
		center = body.worldCenterOfMass;
		Vector3 velocity = body.velocity;
		if (!IsFinite(center) || !IsFinite(velocity)) return false;
		sweepEnd = PredictEnvironmentSweepEnd(center, velocity);
		return true;
	}

	private static Rigidbody ResolveNativeCenterBody(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		RigidbodySpawner[] spawners = ragdoll?._rigidbodySpawners;
		if (spawners == null) return null;
		Transform skeletonRoot = ragdoll._playerBody?.SkeletonRootJoint?.transform;
		Rigidbody center = null;
		bool centerIsPelvis = false;
		foreach (RigidbodySpawner spawner in spawners)
		{
			Rigidbody body = spawner?.Rigidbody;
			if (body == null || body.isKinematic) continue;
			string path = BodyHierarchyPath.RelativeTo(skeletonRoot, body.transform) ?? body.name;
			bool pelvis = path.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0;
			if (center != null && !(pelvis && !centerIsPelvis)) continue;
			center = body;
			centerIsPelvis = pelvis;
		}
		return center;
	}
	#endregion

	#region Frame pump
	/// <summary>Runs the backend's main-thread prefetch capture within what the frame budget has left, and charges it to the budget.</summary>
	private static void PumpEnvironmentPrefetch()
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime == null || !_externalRuntime.IsRaidReady) return;
		float now = Time.realtimeSinceStartup;
		if (now >= _nextPrefetchPruneAt) PrunePrefetches(runtime, now);
		double remaining = FrameWorkBudget.BudgetMilliseconds - FrameWorkBudget.SpentTicks * 1000d / Stopwatch.Frequency;
		double milliseconds;
		try { milliseconds = runtime.PumpEnvironmentPrefetch(Math.Max(PrefetchMinimumPumpMilliseconds, remaining)); }
		catch (Exception exception)
		{
			DisablePrefetch("pump", exception);
			return;
		}
		if (milliseconds > 0d) FrameWorkBudget.RecordStage((long)(milliseconds * Stopwatch.Frequency / 1000d));
	}

	/// <summary>Drops prefetches of destroyed (despawned) bots and of bots that were never refreshed or consumed within their lifetime.</summary>
	private static void PrunePrefetches(IExternalEnvironmentPrefetchRuntime runtime, float now)
	{
		_nextPrefetchPruneAt = now + PrefetchPruneIntervalSeconds;
		PrefetchPruneScratch.Clear();
		foreach (KeyValuePair<string, BotPrefetch> pair in BotPrefetches)
		{
			if (pair.Value.Player == null || now - pair.Value.RequestedAt > BotPrefetchTimeToLiveSeconds + PrefetchPruneIntervalSeconds)
				PrefetchPruneScratch.Add(pair.Key);
		}
		for (int index = 0; index < PrefetchPruneScratch.Count; index++)
		{
			string profileId = PrefetchPruneScratch[index];
			if (!BotPrefetches.TryGetValue(profileId, out BotPrefetch prefetch)) continue;
			BotPrefetches.Remove(profileId);
			CancelPrefetch(runtime, prefetch.ConsumerId);
		}
		PrefetchPruneScratch.Clear();
	}

	/// <summary>Raid reset: cancels every outstanding bot prefetch. Rig prefetches are cancelled with their rigs.</summary>
	private static void CancelAllPrefetches()
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime != null)
		{
			foreach (BotPrefetch prefetch in BotPrefetches.Values) CancelPrefetch(runtime, prefetch.ConsumerId);
		}
		BotPrefetches.Clear();
		PrefetchPruneScratch.Clear();
		_nextPrefetchPruneAt = 0f;
	}
	#endregion

	#region Corpse build
	/// <summary>
	/// True while the pending corpse should stay on native physics because its prefetch is still pending and the bounded wait has not run out.
	/// A corpse something is waiting on never waits: a held lethal impulse replays into native physics after
	/// <see cref="LethalImpulsePolicy.HoldSeconds"/> (shorter than the wait), and a drag or handoff lease sits in its preparing
	/// state until the rig exists. Those corpses build at once and acquire their environment synchronously.
	/// </summary>
	private static bool ShouldWaitForPrefetch(PendingBuild pending, float now)
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime == null || !TryGetCorpsePrefetch(pending.Ragdoll, out BotPrefetch prefetch)) return false;
		ExternalEnvironmentPrefetchState state = GetPrefetchState(runtime, prefetch.ConsumerId);
		if (state != ExternalEnvironmentPrefetchState.Pending && state != ExternalEnvironmentPrefetchState.Ready) return false;
		if (LethalImpulseExperiment.IsHoldingForAdmission(pending.Ragdoll) ||
			pending.Ragdoll._owner is Corpse corpse && (IsDragHeld(corpse) || IsHandoffHeld(corpse))) return false;
		// A ragdoll that tumbled out of the requested corridor would build from a corridor nobody captured: request the one the
		// build would acquire now and wait for it, within the same bound.
		if (ReplaceUncoveredBuildCorridor(runtime, pending, prefetch, now)) state = ExternalEnvironmentPrefetchState.Pending;
		if (state != ExternalEnvironmentPrefetchState.Pending) return false;
		if (pending.PrefetchWaitStartedAt <= 0f) pending.PrefetchWaitStartedAt = now;
		return now - pending.PrefetchWaitStartedAt < PrefetchBuildMaximumWaitSeconds;
	}

	/// <summary>Re-requests the bot's prefetch from the build's current corridor when the center body left the requested one. Returns true when it did, which puts the prefetch back to pending.</summary>
	private static bool ReplaceUncoveredBuildCorridor(IExternalEnvironmentPrefetchRuntime runtime, PendingBuild pending, BotPrefetch prefetch, float now)
	{
		if (!TryGetBuildCorridor(pending, out Vector3 center, out Vector3 sweepEnd) || CorridorCovers(center, prefetch.Center, prefetch.SweepEnd)) return false;
		prefetch.Center = center;
		prefetch.SweepEnd = sweepEnd;
		prefetch.RequestedAt = now;
		RequestPrefetch(runtime, prefetch.ConsumerId, center, sweepEnd, BotPrefetchTimeToLiveSeconds);
		return true;
	}

	/// <summary>
	/// Consumes the corpse's bot prefetch for the build: a ready one is handed to the rig group so the following acquisition only
	/// reuses; a pending or failed one is cancelled and the build acquires synchronously. Returns the state found and the corridor
	/// that was requested, which the build acquires when it is ready and still covers the body.
	/// </summary>
	private static ExternalEnvironmentPrefetchState PromoteCorpsePrefetch(Corpse corpse, uint group, out Vector3 center, out Vector3 sweepEnd)
	{
		center = sweepEnd = Vector3.zero;
		string profileId = corpse?.PlayerProfileID;
		if (string.IsNullOrEmpty(profileId) || !BotPrefetches.TryGetValue(profileId, out BotPrefetch prefetch)) return ExternalEnvironmentPrefetchState.None;
		BotPrefetches.Remove(profileId);
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime == null) return ExternalEnvironmentPrefetchState.None;
		center = prefetch.Center;
		sweepEnd = prefetch.SweepEnd;
		ExternalEnvironmentPrefetchState state = GetPrefetchState(runtime, prefetch.ConsumerId);
		if (state == ExternalEnvironmentPrefetchState.Ready) PromotePrefetch(runtime, prefetch.ConsumerId, group);
		else if (state != ExternalEnvironmentPrefetchState.None) CancelPrefetch(runtime, prefetch.ConsumerId);
		return state;
	}

	/// <summary>Releases the corpse's bot prefetch when its build was dropped or finished without consuming it; a no-op once promoted.</summary>
	private static void CancelCorpsePrefetch(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (!TryGetCorpsePrefetch(ragdoll, out BotPrefetch prefetch)) return;
		BotPrefetches.Remove(((Corpse)ragdoll._owner).PlayerProfileID);
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime != null) CancelPrefetch(runtime, prefetch.ConsumerId);
	}

	private static bool TryGetCorpsePrefetch(EFT.Interactive.CorpseRagdoll ragdoll, out BotPrefetch prefetch)
	{
		prefetch = null;
		string profileId = (ragdoll?._owner as Corpse)?.PlayerProfileID;
		return !string.IsNullOrEmpty(profileId) && BotPrefetches.TryGetValue(profileId, out prefetch);
	}

	/// <summary>True while the corpse's bot prefetch is still pending, i.e. its build may stay on native physics for the bounded wait.</summary>
	private static bool IsCorpsePrefetchPending(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		return runtime != null && TryGetCorpsePrefetch(ragdoll, out BotPrefetch prefetch) &&
			GetPrefetchState(runtime, prefetch.ConsumerId) == ExternalEnvironmentPrefetchState.Pending;
	}
	#endregion

	#region Rig corridor refresh
	/// <summary>
	/// Decides how a rig whose corridor needs extending gets its environment. A moved rig requests a prefetch and keeps its
	/// current corridor until it is ready; it only falls back to synchronous acquisition when the prefetch failed or the rig is
	/// about to leave the space already captured.
	/// </summary>
	private static RigPrefetchAction EvaluateRigPrefetch(ShadowRig rig, Vector3 center, Vector3 sweepEnd)
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime == null || !rig.EnvironmentAcquired) return RigPrefetchAction.Acquire;
		uint consumerId = RigPrefetchConsumerFlag | rig.ExternalGroup;
		ExternalEnvironmentPrefetchState state = rig.PrefetchRequested ? GetPrefetchState(runtime, consumerId) : ExternalEnvironmentPrefetchState.None;
		bool prefetchCovers = rig.PrefetchRequested && CorridorCovers(center, rig.PrefetchCenter, rig.PrefetchSweepEnd);
		if (state == ExternalEnvironmentPrefetchState.Ready && prefetchCovers) return RigPrefetchAction.PromoteThenAcquire;
		if (!CorridorCovers(center, rig.EnvironmentCenter, rig.EnvironmentSweepEnd) || state == ExternalEnvironmentPrefetchState.Failed)
		{
			CancelRigPrefetch(rig);
			return RigPrefetchAction.Acquire;
		}
		if (state == ExternalEnvironmentPrefetchState.Pending && prefetchCovers) return RigPrefetchAction.Wait;
		RequestPrefetch(runtime, consumerId, center, sweepEnd, RigPrefetchTimeToLiveSeconds);
		rig.PrefetchRequested = true;
		rig.PrefetchCenter = center;
		rig.PrefetchSweepEnd = sweepEnd;
		return RigPrefetchAction.Wait;
	}

	private static void PromoteRigPrefetch(ShadowRig rig)
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		rig.PrefetchRequested = false;
		if (runtime != null) PromotePrefetch(runtime, RigPrefetchConsumerFlag | rig.ExternalGroup, rig.ExternalGroup);
	}

	private static void CancelRigPrefetch(ShadowRig rig)
	{
		if (!rig.PrefetchRequested) return;
		rig.PrefetchRequested = false;
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime != null) CancelPrefetch(runtime, RigPrefetchConsumerFlag | rig.ExternalGroup);
	}

	/// <summary>True while the body center is within <see cref="CorridorCoverageDistance"/> of the corridor from <paramref name="start"/> to <paramref name="end"/>.</summary>
	private static bool CorridorCovers(Vector3 center, Vector3 start, Vector3 end)
	{
		Vector3 segment = end - start;
		float lengthSquared = segment.sqrMagnitude;
		float along = lengthSquared > 0.0001f ? Mathf.Clamp01(Vector3.Dot(center - start, segment) / lengthSquared) : 0f;
		return (start + segment * along - center).sqrMagnitude <= CorridorCoverageDistance * CorridorCoverageDistance;
	}
	#endregion

	#region Non-blocking acquisition
	private enum EnvironmentWait
	{
		/// <summary>The corridor is captured and bound to the consumer.</summary>
		Ready,
		/// <summary>The capture is in flight; the body stays where it is and the caller polls again.</summary>
		Pending,
		/// <summary>No non-blocking route, it failed, or the bounded wait ran out: the caller acquires synchronously.</summary>
		Synchronous
	}

	private enum EnvironmentAcquisition
	{
		Acquired,
		Pending,
		Failed
	}

	/// <summary>One bounded wait for an environment capture. While it runs the same corridor is polled, because the backend restarts a capture whose corridor moved.</summary>
	private struct EnvironmentWaitState
	{
		/// <summary>Realtime of the first pending result; zero while nothing is waiting.</summary>
		internal float StartedAt;
		internal Vector3 Center;
		internal Vector3 SweepEnd;
		/// <summary>Frame of the last pending result; polls of the same corridor in that frame reuse it instead of classifying again.</summary>
		internal int PendingFrame;
	}

	/// <summary>
	/// Asks for the corridor of a body that must not move before it has geometry (a settled corpse, a parked weapon, a detached
	/// gib) and reports what the caller does next. A wait that already started keeps polling its own corridor while the body
	/// center is still inside it; the bound is <see cref="WakeEnvironmentMaximumWaitSeconds"/> from the first pending result.
	/// <paramref name="boundCenter"/> and <paramref name="boundSweepEnd"/> are the corridor a Ready result bound.
	/// </summary>
	private static EnvironmentWait PollEnvironment(uint consumerId, ref EnvironmentWaitState wait, Vector3 center, Vector3 sweepEnd,
		float now, out Vector3 boundCenter, out Vector3 boundSweepEnd)
	{
		// A wait nobody polled for a while was abandoned (the shot that started it never retried): start over instead of timing out at once.
		if (wait.StartedAt > 0f && now - wait.StartedAt > WakeEnvironmentMaximumWaitSeconds * 4f) wait.StartedAt = 0f;
		if (wait.StartedAt > 0f && CorridorCovers(center, wait.Center, wait.SweepEnd))
		{
			center = wait.Center;
			sweepEnd = wait.SweepEnd;
			// Every shot waiting on one parked corpse polls the same consumer in a frame: the first pending result stands for the rest, so the backend classifies once.
			if (wait.PendingFrame == Time.frameCount)
			{
				boundCenter = center;
				boundSweepEnd = sweepEnd;
				return EnvironmentWait.Pending;
			}
		}
		boundCenter = center;
		boundSweepEnd = sweepEnd;
		switch (TryAcquireEnvironmentAsync(consumerId, center, sweepEnd))
		{
		case ExternalEnvironmentPrefetchState.Ready:
			wait.StartedAt = 0f;
			_environmentAsyncAcquisitions++;
			return EnvironmentWait.Ready;
		case ExternalEnvironmentPrefetchState.Pending:
			if (wait.StartedAt <= 0f) wait.StartedAt = now;
			wait.PendingFrame = Time.frameCount;
			wait.Center = center;
			wait.SweepEnd = sweepEnd;
			if (now - wait.StartedAt < WakeEnvironmentMaximumWaitSeconds) return EnvironmentWait.Pending;
			break;
		}
		wait.StartedAt = 0f;
		return EnvironmentWait.Synchronous;
	}

	/// <summary>True while the rig already holds a corridor that covers <paramref name="center"/>, so a wake needs no new capture.</summary>
	private static bool IsCorridorCaptured(ShadowRig rig, Vector3 center) =>
		rig.EnvironmentAcquired && CorridorCovers(center, rig.EnvironmentCenter, rig.EnvironmentSweepEnd);

	/// <summary>
	/// Environment for a rig body that is about to move. With <paramref name="allowPending"/> the non-blocking route runs first and
	/// the result can be <see cref="EnvironmentAcquisition.Pending"/>; every other outcome has acquired the corridor, synchronously
	/// when the non-blocking route was unavailable or out of time (counted as a sync fallback) or not allowed (counted as sync direct).
	/// </summary>
	private static EnvironmentAcquisition AcquireRigEnvironment(ShadowRig rig, ref EnvironmentWaitState wait, Vector3 center, Vector3 sweepEnd,
		bool allowPending, out string failure)
	{
		failure = null;
		if (allowPending)
		{
			switch (PollEnvironment(rig.ExternalGroup, ref wait, center, sweepEnd, Time.realtimeSinceStartup, out Vector3 boundCenter, out Vector3 boundSweepEnd))
			{
			case EnvironmentWait.Ready:
				rig.EnvironmentCenter = boundCenter;
				rig.EnvironmentSweepEnd = boundSweepEnd;
				rig.EnvironmentAcquired = true;
				return EnvironmentAcquisition.Acquired;
			case EnvironmentWait.Pending:
				return EnvironmentAcquisition.Pending;
			}
		}
		if (allowPending) _environmentSyncFallbacks++;
		else _environmentSyncDirect++;
		return TryAcquireEnvironment(rig, center, sweepEnd, out failure, out _, out _) ? EnvironmentAcquisition.Acquired : EnvironmentAcquisition.Failed;
	}
	#endregion

	#region Backend calls
	/// <summary>
	/// The backend extension is an optional optimization behind an isolation boundary: a fault disables prefetch for the raid,
	/// logs once, and leaves every caller on the synchronous environment path instead of failing the raid.
	/// </summary>
	private static void DisablePrefetch(string operation, Exception exception)
	{
		if (_prefetchDisabled) return;
		_prefetchDisabled = true;
		RupturePlugin.Log.LogWarning($"Environment prefetch {operation} failed and prefetch is disabled for this raid; corpse builds use synchronous environment acquisition. {exception.GetType().Name}: {exception.Message}");
	}

	private static ExternalEnvironmentPrefetchState GetPrefetchState(IExternalEnvironmentPrefetchRuntime runtime, uint consumerId)
	{
		try { return runtime.GetEnvironmentPrefetchState(consumerId); }
		catch (Exception exception)
		{
			DisablePrefetch("state query", exception);
			return ExternalEnvironmentPrefetchState.None;
		}
	}

	private static void RequestPrefetch(IExternalEnvironmentPrefetchRuntime runtime, uint consumerId, Vector3 center, Vector3 sweepEnd, float timeToLiveSeconds)
	{
		try { runtime.RequestEnvironmentPrefetch(consumerId, center, sweepEnd, timeToLiveSeconds); }
		catch (Exception exception) { DisablePrefetch("request", exception); }
	}

	private static void PromotePrefetch(IExternalEnvironmentPrefetchRuntime runtime, uint prefetchConsumerId, uint consumerId)
	{
		try { runtime.PromoteEnvironmentPrefetch(prefetchConsumerId, consumerId); }
		catch (Exception exception) { DisablePrefetch("promotion", exception); }
	}

	private static void CancelPrefetch(IExternalEnvironmentPrefetchRuntime runtime, uint consumerId)
	{
		try { runtime.CancelEnvironmentPrefetch(consumerId); }
		catch (Exception exception) { DisablePrefetch("cancellation", exception); }
	}

	/// <summary>
	/// Non-blocking acquisition of a corridor for a consumer. Ready: the corridor is captured and bound to the consumer. Pending:
	/// the capture is in flight. Failed: it failed. None: this backend or raid has no such route (or it faulted), the caller
	/// acquires synchronously.
	/// </summary>
	private static ExternalEnvironmentPrefetchState TryAcquireEnvironmentAsync(uint consumerId, Vector3 center, Vector3 sweepEnd)
	{
		IExternalEnvironmentPrefetchRuntime runtime = PrefetchRuntime;
		if (runtime == null) return ExternalEnvironmentPrefetchState.None;
		try { return runtime.TryAcquireEnvironment(consumerId, center, sweepEnd, casing: false); }
		catch (Exception exception)
		{
			DisablePrefetch("acquisition", exception);
			return ExternalEnvironmentPrefetchState.None;
		}
	}
	#endregion
}
