using EFT.Interactive;

namespace Rupture.Ragdolls.Classes;

internal static partial class RagdollCoordinator
{
	// Inspect must not rebind a profile, restore a tombstone, or replay death work.
	internal static bool TryGetDragIdentity(Corpse corpse, out uint deathSequence)
	{
		deathSequence = 0;
		if (corpse == null) return false;
		if (StatesByCorpse.TryGetValue(corpse, out RagdollState state))
		{
			if (!ReferenceEquals(state.Corpse, corpse) || !ReferenceEquals(state.Ragdoll, corpse.Ragdoll)) return false;
			deathSequence = state.DeathSequence;
			return true;
		}
		if (!string.IsNullOrEmpty(corpse.PlayerProfileID) && StatesByProfile.TryGetValue(corpse.PlayerProfileID, out state))
		{
			if (state.Corpse != null && !ReferenceEquals(state.Corpse, corpse)) return false;
			if (state.Ragdoll != null && !ReferenceEquals(state.Ragdoll, corpse.Ragdoll)) return false;
			deathSequence = state.DeathSequence;
			return true;
		}
		if (TryGetTombstone(corpse.PlayerProfileID, -1, out RagdollTombstone tombstone))
		{
			deathSequence = tombstone.DeathSequence;
			return true;
		}
		return false;
	}

	internal static uint DragDeathSequence(Corpse corpse) => TryGetDragIdentity(corpse, out uint sequence) ? sequence : 0;

	internal static bool ActivateDragMotion(Corpse corpse, uint deathSequence)
	{
		if (!IsNetworkAuthority) return false;
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (state == null || !ReferenceEquals(state.Corpse, corpse) || state.DeathSequence != deathSequence) return false;
		ReactivateState(state, GetConfiguredDuration());
		SendReactivationWake(state);
		SendPoseSnapshot(corpse, force: true);
		return true;
	}

	internal static void ReleaseDragMotion(Corpse corpse, CorpseRagdoll ragdoll, uint deathSequence)
	{
		if (corpse != null && ReferenceEquals(corpse.Ragdoll, ragdoll) && StatesByCorpse.TryGetValue(corpse, out RagdollState state) &&
			ReferenceEquals(state.Ragdoll, ragdoll) && state.DeathSequence == deathSequence)
			ReactivateState(state, GetConfiguredDuration());
	}

	private static void RestoreMotionSequences(RagdollState state, RagdollTombstone tombstone)
	{
		if (state == null || tombstone == null || state.DeathSequence != tombstone.DeathSequence) return;
		state.NextPoseSnapshotSequence = tombstone.NextPoseSnapshotSequence;
		state.LastReceivedPoseSnapshotSequence = tombstone.LastReceivedPoseSnapshotSequence;
		state.LastDiscardedPoseSnapshotSequence = tombstone.LastDiscardedPoseSnapshotSequence;
		state.AuthorityPlaybackSequenceBarrier = tombstone.LastReceivedPoseSnapshotSequence;
	}
}
