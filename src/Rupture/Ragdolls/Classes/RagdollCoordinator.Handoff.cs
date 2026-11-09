using System;
using EFT.Interactive;
using Rupture.Integration;
using Rupture.Ragdolls.Classes.Packets;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static partial class RagdollCoordinator
{
	private enum EpochAction
	{
		// Packet is older than what this peer already knows.
		Drop,
		// Managed corpse: handle the packet normally.
		Handle,
		// Held corpse: events are dropped, only the newest complete snapshot/settle is kept.
		Retain
	}

	// The world owns the lease truth. HandoffAdopting lets the adoption hook publish and replay
	// even when the world has not cleared its held flag yet.
	private static bool IsHeld(RagdollState state)
	{
		return state != null && !state.HandoffAdopting && state.Corpse != null &&
			ShadowRagdollWorld.IsHandoffHeld(state.Corpse);
	}

	private static uint NextOwnershipEpoch(uint epoch)
	{
		unchecked { epoch++; }
		return epoch == 0 ? 1u : epoch;
	}

	private static EpochAction ClassifyIncomingEpoch(RagdollState state, uint incomingEpoch)
	{
		bool held = IsHeld(state);
		// A held client never adopts, so the highest epoch seen during the hold is the
		// reference that keeps reordered older packets from being retained as newest.
		uint localEpoch = held && state.HeldHighestEpoch > state.OwnershipEpoch
			? state.HeldHighestEpoch
			: state.OwnershipEpoch;
		switch (CorpseHandoffPolicy.ClassifyEpoch(localEpoch, incomingEpoch, held))
		{
		case OwnershipEpochDecision.Stale:
			return EpochAction.Drop;
		case OwnershipEpochDecision.NewerAdopt:
			state.OwnershipEpoch = incomingEpoch;
			return EpochAction.Handle;
		case OwnershipEpochDecision.NewerRetain:
			state.HeldHighestEpoch = incomingEpoch;
			return EpochAction.Retain;
		default:
			return held ? EpochAction.Retain : EpochAction.Handle;
		}
	}

	private static void NoteAuthorityMotion(RagdollState state)
	{
		state.LastAuthorityMotionAt = Time.realtimeSinceStartup;
	}

	private static void RetainSettle(RagdollState state, RagdollSettleData packet)
	{
		if (state.HasRetainedSettle && !IsNewerSnapshotSequence(state.RetainedSettle.SnapshotSequence, packet.SnapshotSequence))
		{
			return;
		}
		state.RetainedSettle = packet;
		state.HasRetainedSettle = true;
		if (IsNewerSnapshotSequence(state.LastReceivedPoseSnapshotSequence, packet.SnapshotSequence))
		{
			state.LastReceivedPoseSnapshotSequence = packet.SnapshotSequence;
		}
	}

	private static void ClearHandoffRetention(RagdollState state)
	{
		state.HasRetainedSettle = false;
		state.RetainedSettle = default;
		state.RetainedSnapshot = null;
		state.HeldHighestEpoch = 0;
	}

	// Identity change (new death sequence): ownership and everything retained for the old identity end.
	private static void ResetHandoffState(RagdollState state)
	{
		state.OwnershipEpoch = 0;
		state.HandoffAdopting = false;
		state.LastAuthorityMotionAt = 0f;
		state.LocalPredictionStartedAt = 0f;
		ClearHandoffRetention(state);
	}

	// Playback, prediction and queued events belong to a rig that no longer takes part.
	private static void ClearHandoffMotion(RagdollState state)
	{
		ClearAuthorityPlayback(state, advanceBarrier: false);
		PendingSettleStates.Remove(state);
		DiscardPendingPoseSnapshotAssembly(state);
		state.SettlePending = false;
		state.PendingPose = null;
		state.PendingPointImpulses.Clear();
		state.PendingRadialImpulses.Clear();
		state.PendingShadowForces.Clear();
		state.PendingShadowWakes.Clear();
		state.PredictedPointImpulses.Clear();
		state.PredictedRadialImpulses.Clear();
		state.PredictedShadowForces.Clear();
		state.LocalPredictionStartedAt = 0f;
		state.AuthorityMotionEpochActive = false;
		AuthorityMotionStates.Remove(state);
	}

	private static bool TryFindHandoffState(Corpse corpse, out RagdollState state)
	{
		// Destroyed corpses are still valid dictionary keys; cancellation must find them.
		state = null;
		return !ReferenceEquals(corpse, null) && StatesByCorpse.TryGetValue(corpse, out state);
	}

	internal static void OnHandoffReleased(Corpse corpse)
	{
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (state == null)
		{
			return;
		}

		// Retained packets from the Pending window (accepted Release, boundary not reached) stay.
		ClearHandoffMotion(state);
	}

	internal static void OnHandoffAdopted(Corpse corpse, EFT.Interactive.CorpseRagdoll ragdoll)
	{
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (state == null)
		{
			return;
		}

		state.HandoffAdopting = true;
		try
		{
			AdoptHandoffState(state, ragdoll);
		}
		finally
		{
			state.HandoffAdopting = false;
		}
	}

	private static void AdoptHandoffState(RagdollState state, EFT.Interactive.CorpseRagdoll ragdoll)
	{
		ClearHandoffMotion(state);
		if (ragdoll != null)
		{
			state.Ragdoll = ragdoll;
		}
		RegisterBodies(state);

		uint heldHighestEpoch = state.HeldHighestEpoch;
		bool hasRetainedSettle = state.HasRetainedSettle;
		RagdollSettleData retainedSettle = state.RetainedSettle;
		AuthorityPoseFrame retainedSnapshot = state.RetainedSnapshot;
		ClearHandoffRetention(state);

		// Same shape as a normally settled corpse: stopped, but ReactivateState can wake it.
		// The rebuilt rig starts at rest, so neither the lethal velocity nor the lethal
		// impact flags may act on it.
		state.Parked = false;
		state.StopRequested = true;
		state.PoseApplied = true;
		state.InitialVelocityPending = false;
		state.InitialVelocityApplied = true;
		state.InitialProjectileImpactPending = false;
		state.InitialExplosionImpactPending = false;
		ActiveStates.Add(state);

		if (IsRemoteClient)
		{
			// Clients never increment. They take the highest epoch the authority stamped during the hold.
			if (heldHighestEpoch > state.OwnershipEpoch)
			{
				state.OwnershipEpoch = heldHighestEpoch;
			}
			state.SettleReceived = true;
			ReplayRetainedAuthorityState(state, hasRetainedSettle, retainedSettle, retainedSnapshot);
			return;
		}

		state.OwnershipEpoch = NextOwnershipEpoch(state.OwnershipEpoch);
		state.SettleReceived = false;
		// Reset the settle bookkeeping so the dedupe in SendSettle does not swallow the publication.
		state.SettleSent = false;
		state.NextPoseSnapshotAt = 0f;
		if (IsNetworkAuthority)
		{
			// The snapshot goes first: the reliable settle must be the newest sequence so a
			// client's playback ends on the terminal frame.
			SendPoseSnapshot(state, force: true);
			SendSettle(state);
		}
	}

	private static void ReplayRetainedAuthorityState(RagdollState state, bool hasSettle, RagdollSettleData settle,
		AuthorityPoseFrame snapshot)
	{
		bool snapshotIsNewest = snapshot != null &&
			(!hasSettle || IsNewerSnapshotSequence(settle.SnapshotSequence, snapshot.SnapshotSequence));
		if (!snapshotIsNewest && !hasSettle)
		{
			return;
		}

		// Frames were retained after the last local playback barrier; replay must not be filtered by it.
		state.AuthorityPlaybackSequenceBarrier = 0;
		NoteAuthorityMotion(state);
		if (snapshotIsNewest)
		{
			// The authority moved again after its last retained settle. The wake that said so was
			// dropped during the hold, so wake the corpse before playing the snapshot back.
			ReactivateState(state, state.DynamicDuration);
			state.PendingShadowWakes.Add(new PendingShadowWake
			{
				Packet = new RagdollWakeData
				{
					PlayerId = state.PlayerId,
					ProfileId = state.ProfileId,
					DeathSequence = state.DeathSequence,
					ReactivateSettled = 1,
					DynamicDuration = state.DynamicDuration
				},
				ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
			});
			ApplyPendingShadowWakes(state);
			QueueAuthorityFrame(state, snapshot);
			return;
		}

		AcceptSettle(state, settle, replay: true);
	}

	internal static void OnHandoffAdoptionFailed(Corpse corpse)
	{
		AbandonHandoffState(corpse);
	}

	internal static void OnHandoffCancelled(Corpse corpse)
	{
		AbandonHandoffState(corpse);
	}

	// An accepted lease ended before the quiesce boundary: the corpse never stopped following the
	// authority. Whatever was retained during the window replays once through the normal non-held
	// path; events dropped in the window are lost.
	internal static void OnHandoffQueueEnded(Corpse corpse)
	{
		try
		{
			if (!TryFindHandoffState(corpse, out RagdollState state))
			{
				return;
			}

			if (state.HeldHighestEpoch > state.OwnershipEpoch)
			{
				state.OwnershipEpoch = state.HeldHighestEpoch;
			}
			bool hasRetainedSettle = state.HasRetainedSettle;
			RagdollSettleData retainedSettle = state.RetainedSettle;
			AuthorityPoseFrame retainedSnapshot = state.RetainedSnapshot;
			ClearHandoffRetention(state);
			if (IsRemoteClient)
			{
				ReplayRetainedAuthorityState(state, hasRetainedSettle, retainedSettle, retainedSnapshot);
			}
		}
		catch (Exception exception)
		{
			RupturePlugin.Log.LogError($"RPT handoff queue end failed: {exception}");
		}
	}

	// A retired client either never took part in the hold or finished it before the authority published
	// its adoption. Only a newer ownership epoch of the same death revives it; equal or lower stays stale.
	private static bool TryRestoreRetiredForNewerEpoch(string profileId, int playerId, uint deathSequence,
		uint incomingEpoch, out RagdollTombstone tombstone)
	{
		if (TryGetTombstone(profileId, playerId, out tombstone) &&
			RagdollSequencePolicy.Classify(tombstone.DeathSequence, deathSequence) == DeathSequenceRelation.Current &&
			CorpseHandoffPolicy.ClassifyEpoch(tombstone.OwnershipEpoch, incomingEpoch, released: false) ==
				OwnershipEpochDecision.NewerAdopt)
		{
			RemoveTombstone(tombstone);
			return true;
		}
		tombstone = null;
		return false;
	}

	// Same restore the reactivation-wake path performs. The epoch comes back with the tombstone,
	// so the following ClassifyIncomingEpoch sees the publication as NewerAdopt.
	private static void BindRestoredRetiredState(RagdollState state, RagdollTombstone tombstone)
	{
		RestoreMotionSequences(state, tombstone);
		state.StartReceived = true;
		BindStateToRetainedCorpse(state);
	}

	// The corpse stays (or becomes) native. Nothing retained may replay and no binding may point at a dead body.
	private static void AbandonHandoffState(Corpse corpse)
	{
		if (!TryFindHandoffState(corpse, out RagdollState state))
		{
			return;
		}

		if (state.HeldHighestEpoch > state.OwnershipEpoch)
		{
			state.OwnershipEpoch = state.HeldHighestEpoch;
		}
		ClearHandoffRetention(state);
		ClearHandoffMotion(state);
		DetachBodyBindings(state);
	}

	internal static uint GetOwnershipEpoch(Corpse corpse)
	{
		if (ReferenceEquals(corpse, null))
		{
			return 0;
		}
		if (StatesByCorpse.TryGetValue(corpse, out RagdollState state))
		{
			return state.OwnershipEpoch;
		}
		string profileId = corpse.PlayerProfileID;
		if (string.IsNullOrEmpty(profileId))
		{
			return 0;
		}
		if (StatesByProfile.TryGetValue(profileId, out state))
		{
			return state.Corpse != null && !ReferenceEquals(state.Corpse, corpse) ? 0u : state.OwnershipEpoch;
		}
		return TryGetTombstone(profileId, -1, out RagdollTombstone tombstone) ? tombstone.OwnershipEpoch : 0u;
	}

	internal static bool IsClientMotionLive(Corpse corpse)
	{
		if (!IsRemoteClient || ReferenceEquals(corpse, null) || !StatesByCorpse.TryGetValue(corpse, out RagdollState state) ||
			IsHeld(state))
		{
			return false;
		}
		// Settled means the terminal pose was applied; a pending settle or playback is still motion.
		if (state.SettleReceived && !state.SettlePending && !state.AuthorityPlaybackActive)
		{
			return false;
		}

		float now = Time.realtimeSinceStartup;
		// Local prediction is bounded by the configured ragdoll deadline of its last start.
		if (state.LocalPredictionStartedAt > 0f && now < state.LocalPredictionStartedAt + state.DynamicDuration &&
			ShadowRagdollWorld.IsActive(corpse))
		{
			return true;
		}
		return state.LastAuthorityMotionAt > 0f &&
			now - state.LastAuthorityMotionAt <= CorpseHandoffPolicy.ClientMotionSilenceSeconds;
	}
}
