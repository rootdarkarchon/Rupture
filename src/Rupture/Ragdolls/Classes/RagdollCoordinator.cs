using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;
using Rupture.Combined.Patches;
using Rupture.Dismemberment.Classes.HeadWounds;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Ragdolls.Classes.Packets;

namespace Rupture.Ragdolls.Classes;

internal static partial class RagdollCoordinator
{
	private const float PendingLifetimeSeconds = 10f;
	private const float PoseSnapshotIntervalSeconds = 1f / 30f;
	private const int MaximumPoseSnapshotBodies = 64;
	private const int MaximumUnreliablePoseSnapshotChunkBodies = 14;
	private const float PoseSnapshotFailureLogIntervalSeconds = 5f;
	private const int MaximumAuthorityPlaybackFrames = 8;
	private const float MinimumAuthorityPlaybackLagSeconds = PoseSnapshotIntervalSeconds;
	private const float PredictedEventEchoLifetimeSeconds = 2f;

	private sealed class BodyBinding
	{
		internal RagdollState State;
		internal Rigidbody Rigidbody;
		internal string Path;
	}

	private sealed class CachedBodyPartBinding
	{
		internal Collider Collider;
		internal Transform Transform;
		internal EBodyPart BodyPart;
		internal EBodyPartColliderType ColliderType;
	}

	private sealed class PendingPointImpulse
	{
		internal RagdollPointImpulseData Packet;
		internal float ExpiresAt;
	}

	private sealed class PendingRadialImpulse
	{
		internal RagdollRadialImpulseData Packet;
		internal float ExpiresAt;
	}

	private sealed class PendingShadowForce
	{
		internal RagdollForceData Packet;
		internal float ExpiresAt;
	}

	private sealed class PendingShadowWake
	{
		internal RagdollWakeData Packet;
		internal float ExpiresAt;
	}

	private sealed class PendingDismemberment
	{
		internal DismembermentData Packet;
		internal float ExpiresAt;
	}

	private sealed class PendingHeadWound
	{
		internal HeadWoundData Packet;
		internal float ExpiresAt;
	}

	private sealed class AuthorityPoseFrame
	{
		internal uint SnapshotSequence;
		internal float AuthorityTime;
		internal float SimulationDeltaTime;
		internal RagdollPose[] Poses;
		internal bool Terminal;
	}

	private sealed class PredictedPointImpulse
	{
		internal string BodyPath;
		internal Vector3 LocalPoint;
		internal Vector3 Impulse;
		internal float ExpiresAt;
	}

	private sealed class PredictedRadialImpulse
	{
		internal Vector3 Origin;
		internal float Radius;
		internal float Force;
		internal float ExpiresAt;
	}

	private sealed class PredictedShadowForce
	{
		internal string BodyPath;
		internal ShadowForceOperation Operation;
		internal Vector3 Force;
		internal Vector3 Point;
		internal float Scalar;
		internal float Radius;
		internal float UpwardsModifier;
		internal ForceMode Mode;
		internal float ExpiresAt;
	}

	private sealed class RagdollState
	{
		internal object ImpactLifetime = new object();
		internal CorpseLimbContext Limbs;
		internal int PlayerId = -1;
		internal string ProfileId;
		internal uint DeathSequence;
		internal EBodyPart BodyPart;
		internal Vector3 HitDirection;
		internal int DynamicDuration = 5;
		internal Player Player;
		internal CorpseHeadContext Head;
		internal PlayerBody PlayerBody;
		internal Transform VisualRoot;
		internal bool IsYourPlayer;
		internal Corpse Corpse;
		internal EFT.Interactive.CorpseRagdoll Ragdoll;
		internal Transform SkeletonRoot;
		internal bool StartSent;
		internal bool StartReceived;
		internal bool InitialVelocitySent;
		internal bool StopRequested;
		internal bool SettleSent;
		internal bool SettleReceived;
		internal bool SettlePending;
		internal bool ObservedAliveAfterDeath;
		internal bool InitialVelocityPending;
		internal bool InitialVelocityApplied;
		internal Vector3 InitialVelocity;
		internal float DeathStartedAt;
		internal float LifecycleExpiresAt;
		internal float SettleExpiresAt;
		internal RagdollPose[] PendingPose;
		internal bool PoseApplied;
		internal bool Parked;
		internal bool PostMortemReactivated;
		internal bool InitialProjectileImpactPending;
		internal bool InitialExplosionImpactPending;
		internal long InitialExplosionEvent;
		internal float NextPoseSnapshotAt;
		internal bool HasPublishedAuthorityTime;
		internal float LastPublishedAuthorityTime;
		internal float LastPublishedSimulationDeltaTime = PoseSnapshotIntervalSeconds;
		internal uint NextPoseSnapshotSequence;
		internal uint LastReceivedPoseSnapshotSequence;
		internal uint LastDiscardedPoseSnapshotSequence;
		internal PendingPoseSnapshotAssembly PendingPoseSnapshotAssembly;
		internal readonly List<AuthorityPoseFrame> AuthorityPlaybackFrames = new List<AuthorityPoseFrame>();
		internal bool AuthorityPlaybackActive;
		internal float AuthorityPlaybackTime;
		internal float AuthorityPlaybackUpdatedAt;
		internal float AuthorityPlaybackLastAppliedTime = float.MinValue;
		internal uint AuthorityPlaybackSequenceBarrier;
		internal uint LastAppliedAuthoritySnapshotSequence;
		internal int PoseSnapshotsSent;
		internal int PoseSnapshotsReceived;
		internal int PoseSnapshotsApplied;
		internal int PoseSnapshotChunksSent;
		internal int PoseSnapshotChunksReceived;
		internal int PoseSnapshotAssembliesDiscarded;
		internal int PoseSnapshotSendFailures;
		internal float NextPoseSnapshotFailureLogAt;
		internal int PoseSnapshotsAtMotionEpochStart;
		internal bool AuthorityMotionEpochActive;
		internal readonly Dictionary<string, BodyBinding> BodiesByPath = new Dictionary<string, BodyBinding>(StringComparer.Ordinal);
		internal readonly List<CachedBodyPartBinding> BodyParts = new List<CachedBodyPartBinding>();
		internal readonly HashSet<uint> ReceivedEvents = new HashSet<uint>();
		internal readonly List<PendingPointImpulse> PendingPointImpulses = new List<PendingPointImpulse>();
		internal readonly List<PendingRadialImpulse> PendingRadialImpulses = new List<PendingRadialImpulse>();
		internal readonly List<PendingShadowForce> PendingShadowForces = new List<PendingShadowForce>();
		internal readonly List<PendingShadowWake> PendingShadowWakes = new List<PendingShadowWake>();
		internal readonly List<PredictedPointImpulse> PredictedPointImpulses = new List<PredictedPointImpulse>();
		internal readonly List<PredictedRadialImpulse> PredictedRadialImpulses = new List<PredictedRadialImpulse>();
		internal readonly List<PredictedShadowForce> PredictedShadowForces = new List<PredictedShadowForce>();
	}

	private sealed class RagdollTombstone
	{
		internal int PlayerId;
		internal string ProfileId;
		internal uint DeathSequence;
		internal uint NextPoseSnapshotSequence;
		internal uint LastReceivedPoseSnapshotSequence;
		internal uint LastDiscardedPoseSnapshotSequence;
	}

	private static readonly Dictionary<string, RagdollState> StatesByProfile = new Dictionary<string, RagdollState>(StringComparer.Ordinal);
	private static readonly Dictionary<int, RagdollState> StatesByPlayerId = new Dictionary<int, RagdollState>();
	private static readonly Dictionary<Corpse, RagdollState> StatesByCorpse = new Dictionary<Corpse, RagdollState>();
	private static readonly Dictionary<Rigidbody, BodyBinding> BindingsByRigidbody = new Dictionary<Rigidbody, BodyBinding>();
	private static readonly Dictionary<string, RagdollTombstone> TombstonesByProfile = new Dictionary<string, RagdollTombstone>(StringComparer.Ordinal);
	private static readonly Dictionary<int, RagdollTombstone> TombstonesByPlayerId = new Dictionary<int, RagdollTombstone>();
	private static readonly HashSet<RagdollState> ActiveStates = new HashSet<RagdollState>();
	private static readonly HashSet<RagdollState> AuthorityMotionStates = new HashSet<RagdollState>();
	private static readonly HashSet<RagdollState> PendingPoseAssemblyStates = new HashSet<RagdollState>();
	private static readonly HashSet<RagdollState> PendingPoseSnapshotStates = new HashSet<RagdollState>();
	private static readonly HashSet<RagdollState> PendingSettleStates = new HashSet<RagdollState>();
	private static readonly List<PendingDismemberment> PendingDismemberments = new List<PendingDismemberment>();
	private static readonly List<PendingHeadWound> PendingHeadWounds = new List<PendingHeadWound>();
	private static readonly List<RagdollState> StateSnapshot = new List<RagdollState>();
	private static readonly List<RagdollState> AuthorityMotionSnapshot = new List<RagdollState>();
	private static readonly List<RagdollState> PendingPoseAssemblySnapshot = new List<RagdollState>();
	private static readonly List<RagdollState> PendingPoseSnapshotStateSnapshot = new List<RagdollState>();
	private static readonly List<RagdollState> PendingSettleStateSnapshot = new List<RagdollState>();
	private static readonly List<Corpse> ImpactWakeCorpses = new List<Corpse>();

	private static uint _nextDeathSequence = 1;
	private static uint _nextEventSequence = 1;
	private static int _poseSnapshotsSent;
	private static int _poseSnapshotsReceived;
	private static int _poseSnapshotsRejected;
	private static int _poseSnapshotsApplied;
	private static int _poseSnapshotChunksSent;
	private static int _poseSnapshotChunksReceived;
	private static int _poseSnapshotAssembliesDiscarded;
	private static int _poseSnapshotSendFailures;
	private static int _authorityPlaybackBacklogFramesDiscarded;
	private static int _authorityPlaybackTimelineFastForwards;
	private static int _poseAssemblyUpdateCycle;

	internal static bool IsRemoteClient => MultiplayerRuntime.Current.IsRemoteClient;

	internal static bool IsNetworkAuthority => MultiplayerRuntime.Current.IsNetworkAuthority;
	internal static bool HasAuthorityPlayback => IsRemoteClient && PendingPoseSnapshotStates.Count != 0;
	internal static bool IsAuthorityPlaybackActive(Corpse corpse)
	{
		return IsRemoteClient && corpse != null && StatesByCorpse.TryGetValue(corpse, out RagdollState state) &&
			state.AuthorityPlaybackActive;
	}

	internal static bool ImpactReactivationEnabled => RuptureConfiguration.WakeCorpsesOnImpact?.Value == true && ShadowRagdollWorld.Enabled;

	internal static bool IsTrackedBody(Rigidbody rigidbody)
	{
		return rigidbody != null && BindingsByRigidbody.ContainsKey(rigidbody);
	}

	internal static void GetPoseSnapshotDiagnosticCounts(out int sent, out int received, out int rejected, out int applied,
		out int chunksSent, out int chunksReceived, out int incompleteDiscarded, out int sendFailures)
	{
		sent = _poseSnapshotsSent;
		received = _poseSnapshotsReceived;
		rejected = _poseSnapshotsRejected;
		applied = _poseSnapshotsApplied;
		chunksSent = _poseSnapshotChunksSent;
		chunksReceived = _poseSnapshotChunksReceived;
		incompleteDiscarded = _poseSnapshotAssembliesDiscarded;
		sendFailures = _poseSnapshotSendFailures;
	}

	internal static void GetAuthorityPlaybackDiagnosticCounts(out int queuedStates, out int activeStates,
		out int bufferedFrames, out int predictedEchoes, out int backlogFramesDiscarded,
		out int timelineFastForwards, out int waitingForCorpse, out int waitingForLifecycle)
	{
		queuedStates = PendingPoseSnapshotStates.Count;
		activeStates = 0;
		bufferedFrames = 0;
		predictedEchoes = 0;
		backlogFramesDiscarded = _authorityPlaybackBacklogFramesDiscarded;
		timelineFastForwards = _authorityPlaybackTimelineFastForwards;
		waitingForCorpse = 0;
		waitingForLifecycle = 0;
		foreach (RagdollState state in StatesByProfile.Values)
		{
			if (state.AuthorityPlaybackActive) activeStates++;
			bufferedFrames += state.AuthorityPlaybackFrames.Count;
			predictedEchoes += state.PredictedPointImpulses.Count + state.PredictedRadialImpulses.Count + state.PredictedShadowForces.Count;
			if (state.AuthorityPlaybackFrames.Count != 0 && state.Corpse == null) waitingForCorpse++;
			if (state.AuthorityPlaybackFrames.Count != 0 && !state.StartReceived) waitingForLifecycle++;
		}
	}

	internal static void GetDiagnosticCounts(out int activeStates, out int trackedBodies, out int trackedDynamic, out int trackedAwake)
	{
		activeStates = ActiveStates.Count;
		trackedBodies = 0;
		trackedDynamic = 0;
		trackedAwake = 0;
		foreach (Rigidbody rigidbody in BindingsByRigidbody.Keys)
		{
			if (rigidbody == null)
			{
				continue;
			}

			trackedBodies++;
			if (!rigidbody.isKinematic)
			{
				trackedDynamic++;
			}
			if (!rigidbody.IsSleeping())
			{
				trackedAwake++;
			}
		}
	}

	internal static void Reset()
	{
		LimbDismembermentSystem.Reset();
		Rupture.Impacts.ImpactNetwork.ResetRaid();
		CorpseWeaponCollisionFilter.Reset();
		ShadowRagdollWorld.Reset();
		StatesByProfile.Clear();
		StatesByPlayerId.Clear();
		StatesByCorpse.Clear();
		BindingsByRigidbody.Clear();
		TombstonesByProfile.Clear();
		TombstonesByPlayerId.Clear();
		ActiveStates.Clear();
		AuthorityMotionStates.Clear();
		PendingPoseAssemblyStates.Clear();
		PendingPoseSnapshotStates.Clear();
		PendingSettleStates.Clear();
		PendingDismemberments.Clear();
		PendingHeadWounds.Clear();
		StateSnapshot.Clear();
		AuthorityMotionSnapshot.Clear();
		PendingPoseAssemblySnapshot.Clear();
		PendingPoseSnapshotStateSnapshot.Clear();
		PendingSettleStateSnapshot.Clear();
		ImpactWakeCorpses.Clear();
		_nextDeathSequence = 1;
		_nextEventSequence = 1;
		_poseSnapshotsSent = 0;
		_poseSnapshotsReceived = 0;
		_poseSnapshotsRejected = 0;
		_poseSnapshotsApplied = 0;
		_poseSnapshotChunksSent = 0;
		_poseSnapshotChunksReceived = 0;
		_poseSnapshotAssembliesDiscarded = 0;
		_poseSnapshotSendFailures = 0;
		_authorityPlaybackBacklogFramesDiscarded = 0;
		_authorityPlaybackTimelineFastForwards = 0;
		_poseAssemblyUpdateCycle = 0;
	}

	internal static uint RegisterDeath(Player player, EFT.Ballistics.DamageInfo damageInfo, EBodyPart bodyPart, out bool isFirstLethalTransition)
	{
		isFirstLethalTransition = true;
		if (player?.Profile == null)
		{
			return 0;
		}

		string profileId = player.Profile.Id;
		if (string.IsNullOrEmpty(profileId))
		{
			return 0;
		}

		StatesByProfile.TryGetValue(profileId, out RagdollState existing);
		if (IsRemoteClient)
		{
			int remotePlayerId = MultiplayerRuntime.Current.PlayerId(player);
			if (existing == null && TryGetTombstone(profileId, remotePlayerId, out RagdollTombstone tombstone))
			{
				isFirstLethalTransition = false;
				return tombstone.DeathSequence;
			}

			RagdollState remoteState = existing ?? GetOrCreateState(profileId, -1, 0);
			CaptureCorpseInteraction(remoteState, player);
			remoteState.PlayerId = remotePlayerId >= 0 ? remotePlayerId : remoteState.PlayerId;
			if (remoteState.PlayerId >= 0)
			{
				StatesByPlayerId[remoteState.PlayerId] = remoteState;
			}
			isFirstLethalTransition = existing == null;
			return remoteState.DeathSequence;
		}

		if (IsDuplicateDeathRegistration(existing, player))
		{
			isFirstLethalTransition = false;
			return existing.DeathSequence;
		}

		object impactLifetime = null;
		if (existing != null)
		{
			impactLifetime = DetachStateForDeathRegistration(existing, player);
		}
		RemoveTombstone(profileId, MultiplayerRuntime.Current.PlayerId(player));

		RagdollState state = new RagdollState();
		if (impactLifetime != null) state.ImpactLifetime = impactLifetime;
		state.ProfileId = profileId;
		CaptureCorpseInteraction(state, player);
		state.BodyPart = bodyPart;
		state.HitDirection = damageInfo.Direction;
		state.DynamicDuration = GetConfiguredDuration();
		state.DeathSequence = NextDeathSequence();
		state.PlayerId = MultiplayerRuntime.Current.PlayerId(player);
		state.DeathStartedAt = Time.realtimeSinceStartup;
		state.LifecycleExpiresAt = state.DeathStartedAt + PendingLifetimeSeconds;
		state.StartSent = false;
		state.StartReceived = false;
		state.InitialVelocitySent = false;
		state.StopRequested = false;
		state.SettleSent = false;
		state.SettleReceived = false;
		state.SettlePending = false;
		state.ObservedAliveAfterDeath = false;
		state.InitialVelocityPending = false;
		state.InitialVelocityApplied = false;
		state.PendingPose = null;
		state.PoseApplied = false;
		state.Parked = false;
		state.PostMortemReactivated = false;
		state.InitialExplosionEvent = Rupture.Impacts.DeathImpactPolicy.HasSpatialExplosion(damageInfo, Rupture.Impacts.ImpactStateService.Current)
			? Rupture.Impacts.ExplosionEventScope.SequenceFor(damageInfo.MasterOrigin) : 0;
		state.InitialExplosionImpactPending = state.InitialExplosionEvent != 0;
		state.InitialProjectileImpactPending = damageInfo.Damage > 0f && IsProjectileDamage(damageInfo.DamageType);
		StatesByProfile[profileId] = state;
		ActiveStates.Add(state);
		if (IsNetworkAuthority)
		{
			state.PoseSnapshotsAtMotionEpochStart = state.PoseSnapshotsSent;
			state.AuthorityMotionEpochActive = true;
			AuthorityMotionStates.Add(state);
		}
		if (state.PlayerId >= 0)
		{
			StatesByPlayerId[state.PlayerId] = state;
		}

		if (IsNetworkAuthority)
		{
			SendStart(state, hasInitialVelocity: false, Vector3.zero);
		}

		return state.DeathSequence;
	}

	internal static uint GetDeathSequence(Player player)
	{
		if (player?.Profile != null && StatesByProfile.TryGetValue(player.Profile.Id, out RagdollState state))
		{
			return state.DeathSequence;
		}
		if (player?.Profile != null && TryGetTombstone(player.Profile.Id, MultiplayerRuntime.Current.PlayerId(player), out RagdollTombstone tombstone))
		{
			return tombstone.DeathSequence;
		}

		return 0;
	}

	internal static bool TryGetInteractionTarget(Player player, out CorpseInteractionTarget target)
	{
		target = default;
		if (player == null || player.Profile == null)
		{
			return false;
		}
		if (StatesByProfile.TryGetValue(player.Profile.Id, out RagdollState state))
		{
			CaptureCorpseInteraction(state, player);
			target = CreateInteractionTarget(state);
			return target.VisualRoot != null && target.PlayerBody != null;
		}

		Transform visualRoot = player.Transform?.Original;
		PlayerBody playerBody = player.PlayerBody;
		target = new CorpseInteractionTarget(
			corpse: null,
			player,
			playerBody,
			visualRoot ?? playerBody?.SkeletonRootJoint?.transform,
			player.Profile.Id,
			MultiplayerRuntime.Current.PlayerId(player),
			GetDeathSequence(player),
			player.IsYourPlayer);
		return target.VisualRoot != null && target.PlayerBody != null;
	}

	internal static bool TryGetInteractionTarget(string profileId, int playerId, out CorpseInteractionTarget target)
	{
		target = default;
		if (string.IsNullOrEmpty(profileId) || !StatesByProfile.TryGetValue(profileId, out RagdollState state) ||
			state.PlayerId != playerId) return false;
		target = CreateInteractionTarget(state);
		return target.Corpse != null && target.VisualRoot != null && target.PlayerBody != null;
	}

	internal static bool TryGetInteractionTarget(Corpse corpse, out CorpseInteractionTarget target)
	{
		target = default;
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (state == null)
		{
			return false;
		}
		target = CreateInteractionTarget(state);
		return target.Corpse != null && target.VisualRoot != null && target.PlayerBody != null;
	}

	// Pending live impacts must not use GetStateForCorpse: its profile fallback
	// can rebind an old corpse or restore a tombstone after this lifetime ended.
	internal static object CaptureCorpseImpactLifetime(Corpse corpse) =>
		corpse != null && StatesByCorpse.TryGetValue(corpse, out RagdollState state) ? state.ImpactLifetime : null;

	internal static bool TryGetBoundInteractionTarget(Corpse corpse, object lifetime, out CorpseInteractionTarget target)
	{
		target = default;
		if (corpse == null || lifetime == null || string.IsNullOrEmpty(corpse.PlayerProfileID) ||
			!StatesByProfile.TryGetValue(corpse.PlayerProfileID, out RagdollState state) ||
			!ReferenceEquals(state.ImpactLifetime, lifetime) || state.ObservedAliveAfterDeath) return false;
		// Start clears provisional bindings before the normal update reattaches
		// the retained corpse. Preserve only this lifetime while it is unbound.
		if (ReferenceEquals(state.Corpse, null)) return true;
		if (!ReferenceEquals(state.Corpse, corpse)) return false;
		target = CreateInteractionTarget(state);
		return true;
	}

	internal static bool TryResolveCorpseBodyPart(CorpseInteractionTarget target, Collider hitCollider,
		out EBodyPart bodyPart, out Collider canonicalCollider)
	{
		bodyPart = EBodyPart.Common;
		canonicalCollider = hitCollider;
		if (hitCollider == null)
		{
			return false;
		}

		BodyPartCollider direct = hitCollider.GetComponent<BodyPartCollider>() ?? hitCollider.GetComponentInParent<BodyPartCollider>();
		if (direct != null)
		{
			bodyPart = direct.BodyPartType;
			canonicalCollider = direct.Collider ?? hitCollider;
			return true;
		}

		if (target.Corpse != null && StatesByCorpse.TryGetValue(target.Corpse, out RagdollState state))
		{
			Transform hitTransform = hitCollider.transform;
			foreach (CachedBodyPartBinding binding in state.BodyParts)
			{
				Transform canonicalTransform = binding.Transform;
				if (canonicalTransform != null && (canonicalTransform == hitTransform ||
					hitTransform.IsChildOf(canonicalTransform) || canonicalTransform.IsChildOf(hitTransform)))
				{
					bodyPart = binding.BodyPart;
					canonicalCollider = binding.Collider != null ? binding.Collider : hitCollider;
					return true;
				}
			}
		}

		if (target.Corpse != null && ShadowRagdollWorld.TryGetHitBodyPath(target.Corpse, hitCollider, out string bodyPath) &&
			TryInferBodyPart(bodyPath, out bodyPart))
		{
			return true;
		}
		return false;
	}

	internal static Collider[] GetLimbColliders(CorpseInteractionTarget target, EBodyPart bodyPart,
		LimbSeverLevel level)
	{
		if (target.Corpse == null && target.Player?.HealthController?.IsAlive == true)
			return GetLivingLimbColliders(target.Player, bodyPart, level);
		RagdollState state = target.Corpse != null ? GetStateForCorpse(target.Corpse, allowRetired: true) : null;
		if (state == null && !string.IsNullOrEmpty(target.ProfileId)) StatesByProfile.TryGetValue(target.ProfileId, out state);
		if (state == null) return Array.Empty<Collider>();
		List<Collider> colliders = new List<Collider>();
		foreach (CachedBodyPartBinding binding in state.BodyParts)
		{
			if (binding?.Collider != null && IsRemovedLimbCollider(binding.ColliderType, bodyPart, level))
				colliders.Add(binding.Collider);
		}
		return colliders.ToArray();
	}

	internal static Collider[] GetLivingLimbColliders(Player player, EBodyPart bodyPart, LimbSeverLevel level)
	{
		var colliders = new List<Collider>();
		foreach (BodyPartCollider binding in player.PlayerBones?.BodyPartColliders ?? Array.Empty<BodyPartCollider>())
			if (binding?.Collider != null && IsRemovedLimbCollider(binding.BodyPartColliderType, bodyPart, level))
				colliders.Add(binding.Collider);
		return colliders.ToArray();
	}

	internal static void DisableLimbColliders(CorpseInteractionTarget target, EBodyPart bodyPart,
		LimbSeverLevel level)
	{
		Collider[] colliders = GetLimbColliders(target, bodyPart, level);
		if (target.Corpse == null && target.Player?.HealthController?.IsAlive == true)
			ShadowRagdollWorld.SeverLivingLeg(target.Player, bodyPart, level);
		else if (ShadowRagdollWorld.DisableCorpseColliders(target.Corpse, target.ProfileId, colliders)) return;
		foreach (Collider collider in colliders)
			if (collider != null) collider.enabled = false;
	}

	private static bool IsRemovedLimbCollider(EBodyPartColliderType colliderType, EBodyPart bodyPart,
		LimbSeverLevel level)
	{
		switch (bodyPart)
		{
		case EBodyPart.LeftArm:
			return colliderType == EBodyPartColliderType.LeftForearm ||
				(level == LimbSeverLevel.Proximal && colliderType == EBodyPartColliderType.LeftUpperArm);
		case EBodyPart.RightArm:
			return colliderType == EBodyPartColliderType.RightForearm ||
				(level == LimbSeverLevel.Proximal && colliderType == EBodyPartColliderType.RightUpperArm);
		case EBodyPart.LeftLeg:
			return colliderType == EBodyPartColliderType.LeftCalf ||
				(level == LimbSeverLevel.Proximal && colliderType == EBodyPartColliderType.LeftThigh);
		case EBodyPart.RightLeg:
			return colliderType == EBodyPartColliderType.RightCalf ||
				(level == LimbSeverLevel.Proximal && colliderType == EBodyPartColliderType.RightThigh);
		default:
			return false;
		}
	}

	internal static void RegisterNativeRagdoll(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll == null)
		{
			return;
		}

		Corpse corpse = ragdoll._owner as Corpse;
		string profileId = corpse?.PlayerProfileID;
		if (string.IsNullOrEmpty(profileId))
		{
			return;
		}
		if (!StatesByProfile.ContainsKey(profileId) && TryGetTombstone(profileId, -1, out _))
		{
			return;
		}

		RagdollState state = GetOrCreateState(profileId, -1, 0);
		state.Corpse = corpse;
		state.Ragdoll = ragdoll;
		state.PlayerBody = ragdoll._playerBody;
		state.SkeletonRoot = ragdoll._playerBody?.SkeletonRootJoint?.transform;
		if (state.VisualRoot == null)
		{
			state.VisualRoot = state.SkeletonRoot;
		}
		StatesByCorpse[corpse] = state;
		ShadowRagdollWorld.BindDragDeathIdentity(corpse, ragdoll, state.DeathSequence);
		BindCorpseLifetime(state, corpse);
		RegisterBodies(state);
		if (FreezeCorpseRagdollsForWoundDebug)
		{
			FreezeNativeCorpseRagdoll(state);
			state.InitialVelocityPending = false;
			state.InitialVelocityApplied = true;
			if (IsNetworkAuthority)
			{
				SendStart(state, hasInitialVelocity: false, Vector3.zero);
				SendSettle(state);
			}
			return;
		}
		ShadowRagdollWorld.Schedule(ragdoll);

		if (IsNetworkAuthority)
		{
			SendStart(state, IsFinite(ragdoll._velocity), ragdoll._velocity);
		}

		ApplyInitialVelocity(state);
		ApplyPendingImpulses(state);
		ApplyPendingShadowForces(state);
		ApplyPendingShadowWakes(state);
	}

	internal static bool ShouldStop(Corpse corpse, bool nativeResult, bool nativeSleeping, float nativeTimePass)
	{
		if (ShadowRagdollWorld.IsDragHeld(corpse)) return false;
		if (corpse == null)
		{
			return nativeResult;
		}

		if (!StatesByCorpse.TryGetValue(corpse, out RagdollState state) && corpse.Ragdoll != null)
		{
			RegisterNativeRagdoll(corpse.Ragdoll);
			StatesByCorpse.TryGetValue(corpse, out state);
		}

		if (IsRemoteClient && (state == null || state.DeathSequence == 0 || !state.StartReceived))
		{
			return nativeResult;
		}
		if (FreezeCorpseRagdollsForWoundDebug)
		{
			FreezeNativeCorpseRagdoll(state);
			return false;
		}

		int duration = state?.DynamicDuration ?? GetConfiguredDuration();
		bool timedOut = state != null && state.DeathStartedAt > 0f
			? Time.realtimeSinceStartup >= state.DeathStartedAt + duration
			: CorpseSleepPolicy.ShouldForceSleep(nativeTimePass, duration);
		bool shadowActive = ShadowRagdollWorld.IsActive(corpse);
		bool shadowSettled = ShadowRagdollWorld.IsSettled(corpse);
		// The standalone PhysX actor is not visible to EFT's Unity-scene ballistic
		// raycasts.  When post-mortem interaction is selected, retain EFT's native
		// kinematic corpse hierarchy as a query-only shell even after RPT has settled
		// the authoritative external actor.  Returning true here lets EFT perform
		// its terminal ragdoll cleanup, after which later bullets can no longer find
		// the corpse to wake, push, or dismember it.
		bool retainNativeQueryShell = ImpactReactivationEnabled && (shadowActive || shadowSettled);
		bool shouldStop;
		if (IsRemoteClient)
		{
			// Predicting clients never choose the terminal state from their own timer or
			// sleep result. The reliable authority settle packet owns final positions.
			shouldStop = state.SettleReceived;
		}
		else if (shadowActive || shadowSettled)
		{
			shouldStop = shadowSettled || timedOut;
		}
		else
		{
			shouldStop = nativeResult || nativeSleeping || timedOut;
		}

		if (shouldStop && state != null && !state.StopRequested)
		{
			ShadowRagdollWorld.ForceSettle(corpse);
			state.StopRequested = true;
			if (IsNetworkAuthority)
			{
				SendSettle(state);
			}
		}

		return retainNativeQueryShell ? false : shouldStop;
	}

	internal static bool TryApplyPointImpulse(Rigidbody rigidbody, Vector3 impulse, Vector3 worldPoint)
	{
		if (rigidbody == null || !IsFinite(impulse) || !IsFinite(worldPoint) || impulse.sqrMagnitude <= 0f)
		{
			return false;
		}

		if (!BindingsByRigidbody.TryGetValue(rigidbody, out BodyBinding binding))
		{
			Corpse corpse = rigidbody.GetComponentInParent<Corpse>();
			if (corpse?.Ragdoll != null)
			{
				RegisterNativeRagdoll(corpse.Ragdoll);
				BindingsByRigidbody.TryGetValue(rigidbody, out binding);
			}
		}

		if (binding?.State == null || (!IsRemoteClient && IsStopped(binding.State)))
		{
			return false;
		}

		if (IsRemoteClient)
		{
			bool independentWeapon = ShadowRagdollWorld.IsIndependentWeaponPath(binding.Path);
			if (!BeginLocalPrediction(binding.State, independentWeapon)) return false;
		}

		if (!ShadowRagdollWorld.TryApplySimulationPointImpulse(rigidbody, impulse, worldPoint))
		{
			return false;
		}
		if (IsRemoteClient)
		{
			RecordPredictedPointImpulse(binding.State, binding.Path,
				rigidbody.transform.InverseTransformPoint(worldPoint), impulse);
		}
		if (IsNetworkAuthority && binding.State.PlayerId >= 0)
		{
			RagdollPointImpulseData packet = new RagdollPointImpulseData
			{
				PlayerId = binding.State.PlayerId,
				ProfileId = binding.State.ProfileId,
				DeathSequence = binding.State.DeathSequence,
				EventSequence = NextEventSequence(),
				BodyPath = binding.Path,
				LocalPoint = rigidbody.transform.InverseTransformPoint(worldPoint),
				Impulse = impulse
			};
			MultiplayerRuntime.Current.Send(ref packet);
		}

		return true;
	}

	internal static bool TryApplyCorpsePointImpulse(Corpse corpse, Collider hitCollider, Vector3 impulse, Vector3 worldPoint, out string bodyName)
	{
		bodyName = null;
		if (corpse == null || hitCollider == null || !IsFinite(impulse) || !IsFinite(worldPoint) || impulse.sqrMagnitude <= 0f)
		{
			return false;
		}
		RagdollState state = GetStateForCorpse(corpse, allowRetired: IsRemoteClient);
		if (state == null || (!IsRemoteClient && IsStopped(state)))
		{
			return false;
		}
		if (IsRemoteClient)
		{
			if (!BeginLocalPrediction(state, independentWeaponOnly: false)) return false;
		}
		if (!ShadowRagdollWorld.TryApplyPointImpulse(corpse, hitCollider, impulse, worldPoint,
			out string bodyPath, out bodyName, out Vector3 localPoint))
		{
			return false;
		}
		if (IsRemoteClient) RecordPredictedPointImpulse(state, bodyPath, localPoint, impulse);
		if (IsNetworkAuthority && state.PlayerId >= 0)
		{
			RagdollPointImpulseData packet = new RagdollPointImpulseData
			{
				PlayerId = state.PlayerId,
				ProfileId = state.ProfileId,
				DeathSequence = state.DeathSequence,
				EventSequence = NextEventSequence(),
				BodyPath = bodyPath,
				LocalPoint = localPoint,
				Impulse = impulse
			};
			MultiplayerRuntime.Current.Send(ref packet);
		}
		return true;
	}

	internal static bool TryApplyIndependentWeaponPointImpulse(Corpse corpse, Collider hitCollider, Vector3 impulse, Vector3 worldPoint, out string bodyName)
	{
		bodyName = null;
		if (corpse == null || hitCollider == null || !IsFinite(impulse) || !IsFinite(worldPoint) || impulse.sqrMagnitude <= 0f)
		{
			return false;
		}
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (IsRemoteClient)
		{
			if (state == null || !BeginLocalPrediction(state, independentWeaponOnly: true)) return false;
		}
		if (!ShadowRagdollWorld.TryApplyIndependentWeaponPointImpulse(corpse, hitCollider, impulse, worldPoint,
			out string bodyPath, out bodyName, out Vector3 localPoint))
		{
			return false;
		}
		if (IsRemoteClient) RecordPredictedPointImpulse(state, bodyPath, localPoint, impulse);
		if (state != null && IsNetworkAuthority && state.PlayerId >= 0)
		{
			RagdollPointImpulseData packet = new RagdollPointImpulseData
			{
				PlayerId = state.PlayerId,
				ProfileId = state.ProfileId,
				DeathSequence = state.DeathSequence,
				EventSequence = NextEventSequence(),
				BodyPath = bodyPath,
				LocalPoint = localPoint,
				Impulse = impulse
			};
			MultiplayerRuntime.Current.Send(ref packet);
		}
		return true;
	}

	private static bool BeginLocalPrediction(RagdollState state, bool independentWeaponOnly)
	{
		if (!IsRemoteClient || state?.Corpse == null) return false;
		if (!independentWeaponOnly && IsStopped(state))
		{
			ReactivateState(state, state.DynamicDuration);
		}
		if (!independentWeaponOnly)
		{
			state.SettlePending = false;
			PendingSettleStates.Remove(state);
			state.PendingPose = null;
			state.PoseApplied = false;
			state.StopRequested = false;
			state.SettleReceived = false;
			state.Parked = false;
			ActiveStates.Add(state);
		}
		state.AuthorityPlaybackSequenceBarrier = state.LastReceivedPoseSnapshotSequence;
		ClearAuthorityPlayback(state, advanceBarrier: false);
		return ShadowRagdollWorld.BeginLocalPrediction(state.Corpse, independentWeaponOnly);
	}

	private static void RecordPredictedPointImpulse(RagdollState state, string bodyPath, Vector3 localPoint, Vector3 impulse)
	{
		if (state == null) return;
		PrunePredictedEventEchoes(state, Time.realtimeSinceStartup);
		state.PredictedPointImpulses.Add(new PredictedPointImpulse
		{
			BodyPath = bodyPath ?? string.Empty,
			LocalPoint = localPoint,
			Impulse = impulse,
			ExpiresAt = Time.realtimeSinceStartup + PredictedEventEchoLifetimeSeconds
		});
	}

	private static bool ConsumePredictedPointImpulse(RagdollState state, RagdollPointImpulseData packet)
	{
		if (state == null) return false;
		float now = Time.realtimeSinceStartup;
		for (int index = state.PredictedPointImpulses.Count - 1; index >= 0; index--)
		{
			PredictedPointImpulse predicted = state.PredictedPointImpulses[index];
			if (now > predicted.ExpiresAt)
			{
				state.PredictedPointImpulses.RemoveAt(index);
				continue;
			}
			if (!string.Equals(predicted.BodyPath, packet.BodyPath ?? string.Empty, StringComparison.Ordinal) ||
				(predicted.LocalPoint - packet.LocalPoint).sqrMagnitude > 0.0625f ||
				(predicted.Impulse - packet.Impulse).sqrMagnitude > Mathf.Max(0.0001f, packet.Impulse.sqrMagnitude * 0.01f)) continue;
			state.PredictedPointImpulses.RemoveAt(index);
			return true;
		}
		return false;
	}

	internal static bool BeginLocalForcePrediction(Corpse corpse, string bodyPath)
	{
		if (!IsRemoteClient) return true;
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		return state != null && BeginLocalPrediction(state, ShadowRagdollWorld.IsIndependentWeaponPath(bodyPath));
	}

	internal static void RecordLocalForcePrediction(Corpse corpse, string bodyPath, ShadowForceOperation operation,
		Vector3 force, Vector3 point, float scalar, float radius, float upwardsModifier, ForceMode mode)
	{
		if (!IsRemoteClient) return;
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (state == null) return;
		PrunePredictedEventEchoes(state, Time.realtimeSinceStartup);
		state.PredictedShadowForces.Add(new PredictedShadowForce
		{
			BodyPath = bodyPath ?? string.Empty,
			Operation = operation,
			Force = force,
			Point = point,
			Scalar = scalar,
			Radius = radius,
			UpwardsModifier = upwardsModifier,
			Mode = mode,
			ExpiresAt = Time.realtimeSinceStartup + PredictedEventEchoLifetimeSeconds
		});
	}

	private static bool ConsumePredictedShadowForce(RagdollState state, RagdollForceData packet)
	{
		float now = Time.realtimeSinceStartup;
		for (int index = state.PredictedShadowForces.Count - 1; index >= 0; index--)
		{
			PredictedShadowForce predicted = state.PredictedShadowForces[index];
			if (now > predicted.ExpiresAt)
			{
				state.PredictedShadowForces.RemoveAt(index);
				continue;
			}
			if (!string.Equals(predicted.BodyPath, packet.BodyPath ?? string.Empty, StringComparison.Ordinal) ||
				predicted.Operation != (ShadowForceOperation)packet.Operation || predicted.Mode != (ForceMode)packet.ForceMode ||
				(predicted.Force - packet.Force).sqrMagnitude > Mathf.Max(0.0001f, packet.Force.sqrMagnitude * 0.01f) ||
				(predicted.Point - packet.Point).sqrMagnitude > 0.0625f ||
				Mathf.Abs(predicted.Scalar - packet.Scalar) > 0.001f || Mathf.Abs(predicted.Radius - packet.Radius) > 0.001f ||
				Mathf.Abs(predicted.UpwardsModifier - packet.UpwardsModifier) > 0.001f) continue;
			state.PredictedShadowForces.RemoveAt(index);
			return true;
		}
		return false;
	}

	internal static bool PrepareCorpseImpact(Corpse corpse, bool explosion, HashSet<object> awakenedStates, out CorpseInteractionTarget target, out bool postMortemImpact)
	{
		target = default;
		postMortemImpact = false;
		if (corpse == null || IsRemoteClient)
		{
			return false;
		}

		// A retained shadow rig can outlive its heavyweight coordinator state. When
		// impact reactivation is enabled, restore that state from its tombstone so
		// old corpses remain just as wakeable as newly settled ones.
		RagdollState directState = GetStateForCorpse(corpse, allowRetired: ImpactReactivationEnabled);
		if (directState != null)
		{
			target = CreateInteractionTarget(directState);
		}
		bool initialImpact = directState != null && (explosion
			? ConsumeInitialExplosionImpact(directState)
			: ConsumeInitialProjectileImpact(directState));
		if (initialImpact || !ImpactReactivationEnabled)
		{
			if (directState != null && !IsStopped(directState))
			{
				awakenedStates?.Add(directState);
			}
			return directState != null;
		}

		postMortemImpact = true;
		if (!ShadowRagdollWorld.WakeFromImpact(corpse, ImpactWakeCorpses))
		{
			return false;
		}

		int duration = GetConfiguredDuration();
		bool directPrepared = false;
		foreach (Corpse awakenedCorpse in ImpactWakeCorpses)
		{
			RagdollState state = GetStateForCorpse(awakenedCorpse, allowRetired: true);
			if (state == null)
			{
				ShadowRagdollWorld.ForceSettle(awakenedCorpse);
				continue;
			}
			ReactivateState(state, duration);
			awakenedStates?.Add(state);
			SendReactivationWake(state);
			if (ReferenceEquals(awakenedCorpse, corpse))
			{
				directPrepared = true;
				target = CreateInteractionTarget(state);
			}
		}
		ImpactWakeCorpses.Clear();
		return directPrepared;
	}

	internal static bool ReactivateFromMovingEnvironment(Corpse corpse)
	{
		if (corpse == null || IsRemoteClient || !ImpactReactivationEnabled)
		{
			return false;
		}
		ImpactWakeCorpses.Clear();
		if (!ShadowRagdollWorld.WakeFromImpact(corpse, ImpactWakeCorpses))
		{
			return false;
		}
		int duration = GetConfiguredDuration();
		bool directPrepared = false;
		foreach (Corpse awakenedCorpse in ImpactWakeCorpses)
		{
			RagdollState state = GetStateForCorpse(awakenedCorpse, allowRetired: true);
			if (state == null)
			{
				ShadowRagdollWorld.ForceSettle(awakenedCorpse);
				continue;
			}
			ReactivateState(state, duration);
			SendReactivationWake(state);
			directPrepared |= ReferenceEquals(awakenedCorpse, corpse);
		}
		ImpactWakeCorpses.Clear();
		return directPrepared;
	}

	internal static void SendShadowForce(Corpse corpse, string bodyPath, ShadowForceOperation operation, Vector3 force, Vector3 point, float scalar, float radius, float upwardsModifier, ForceMode mode)
	{
		if (!IsNetworkAuthority || corpse == null || string.IsNullOrEmpty(bodyPath) || !StatesByCorpse.TryGetValue(corpse, out RagdollState state) || IsStopped(state) || state.PlayerId < 0)
		{
			return;
		}
		RagdollForceData packet = new RagdollForceData
		{
			PlayerId = state.PlayerId,
			ProfileId = state.ProfileId,
			DeathSequence = state.DeathSequence,
			EventSequence = NextEventSequence(),
			BodyPath = bodyPath,
			Operation = (byte)operation,
			Force = force,
			Point = point,
			Scalar = scalar,
			Radius = radius,
			UpwardsModifier = upwardsModifier,
			ForceMode = (byte)mode
		};
		MultiplayerRuntime.Current.Send(ref packet);
	}

	internal static void SendShadowWake(Corpse corpse)
	{
		if (!IsNetworkAuthority || corpse == null || !StatesByCorpse.TryGetValue(corpse, out RagdollState state) || IsStopped(state) || state.PlayerId < 0)
		{
			return;
		}
		RagdollWakeData packet = new RagdollWakeData
		{
			PlayerId = state.PlayerId,
			ProfileId = state.ProfileId,
			DeathSequence = state.DeathSequence,
			EventSequence = NextEventSequence(),
			ReactivateSettled = 0,
			DynamicDuration = state.DynamicDuration
		};
		MultiplayerRuntime.Current.Send(ref packet);
	}

	private static void SendReactivationWake(RagdollState state)
	{
		if (!IsNetworkAuthority || state == null || state.PlayerId < 0 || state.DeathSequence == 0)
		{
			return;
		}
		RagdollWakeData packet = new RagdollWakeData
		{
			PlayerId = state.PlayerId,
			ProfileId = state.ProfileId,
			DeathSequence = state.DeathSequence,
			EventSequence = NextEventSequence(),
			ReactivateSettled = 1,
			DynamicDuration = state.DynamicDuration
		};
		MultiplayerRuntime.Current.Send(ref packet);
	}

	internal static void SendPoseSnapshot(Corpse corpse, bool force = false)
	{
		if (!IsNetworkAuthority || corpse == null || !StatesByCorpse.TryGetValue(corpse, out RagdollState state) ||
			state.PlayerId < 0 || state.DeathSequence == 0 || !MultiplayerRuntime.Current.HasServer)
		{
			return;
		}
		SendPoseSnapshot(state, force);
	}

	private static void SendPoseSnapshot(RagdollState state, bool force = false)
	{
		if (!IsNetworkAuthority || state?.Corpse == null || state.PlayerId < 0 || state.DeathSequence == 0 ||
			!MultiplayerRuntime.Current.HasServer)
		{
			return;
		}
		float now = Time.realtimeSinceStartup;
		if (!force && now < state.NextPoseSnapshotAt)
		{
			return;
		}
		ScheduleNextPoseSnapshot(state, now, force);
		RagdollPose[] poses = CapturePose(state);
		if (!TryBuildLiveSnapshotChunks(poses, out List<RagdollPose[]> chunks, out string failure))
		{
			RecordPoseSnapshotSendFailure(state, 0, -1, 0, poses?.Length ?? 0, failure);
			return;
		}
		state.NextPoseSnapshotSequence++;
		if (state.NextPoseSnapshotSequence == 0)
		{
			state.NextPoseSnapshotSequence = 1;
		}
		uint snapshotSequence = state.NextPoseSnapshotSequence;
		ResolveOutgoingAuthorityTiming(state, out float authorityTime, out float simulationDeltaTime);
		for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
		{
			RagdollPoseSnapshotData packet = new RagdollPoseSnapshotData
			{
				PlayerId = state.PlayerId,
				ProfileId = state.ProfileId,
				DeathSequence = state.DeathSequence,
				SnapshotSequence = snapshotSequence,
				AuthorityTime = authorityTime,
				SimulationDeltaTime = simulationDeltaTime,
				ChunkIndex = (byte)chunkIndex,
				ChunkCount = (byte)chunks.Count,
				TotalPoseCount = (byte)poses.Length,
				Poses = chunks[chunkIndex]
			};
			try
			{
				// Chunk ordering is explicit and independent per corpse. Ordinary unreliable
				// delivery avoids LiteNetLib's shared sequenced channel dropping corpse A
				// merely because a newer packet for corpse B arrived first.
				MultiplayerRuntime.Current.Send(ref packet, unreliable: true);
				_poseSnapshotChunksSent++;
				state.PoseSnapshotChunksSent++;
			}
			catch (Exception exception)
			{
				RecordPoseSnapshotSendFailure(state, snapshotSequence, chunkIndex, chunks.Count,
					packet.Poses.Length, $"{exception.GetType().Name}: {exception.Message}");
				return;
			}
		}
		state.HasPublishedAuthorityTime = true;
		state.LastPublishedAuthorityTime = authorityTime;
		state.LastPublishedSimulationDeltaTime = simulationDeltaTime;
		_poseSnapshotsSent++;
		state.PoseSnapshotsSent++;
	}

	private static void ScheduleNextPoseSnapshot(RagdollState state, float now, bool force)
	{
		if (force || state.NextPoseSnapshotAt <= 0f)
		{
			state.NextPoseSnapshotAt = now + PoseSnapshotIntervalSeconds;
			return;
		}
		float intervals = Mathf.Floor((now - state.NextPoseSnapshotAt) / PoseSnapshotIntervalSeconds) + 1f;
		state.NextPoseSnapshotAt += Mathf.Max(1f, intervals) * PoseSnapshotIntervalSeconds;
	}

	private static void ResolveOutgoingAuthorityTiming(RagdollState state, out float authorityTime, out float simulationDeltaTime)
	{
		ShadowRagdollWorld.GetAuthoritySimulationTimeline(out authorityTime, out float completedStepDeltaTime);
		if (!IsFinite(completedStepDeltaTime) || completedStepDeltaTime <= 0f)
		{
			completedStepDeltaTime = PoseSnapshotIntervalSeconds;
		}
		simulationDeltaTime = state.HasPublishedAuthorityTime && authorityTime > state.LastPublishedAuthorityTime
			? authorityTime - state.LastPublishedAuthorityTime
			: completedStepDeltaTime;
		if (!IsFinite(simulationDeltaTime) || simulationDeltaTime <= 0f)
		{
			simulationDeltaTime = PoseSnapshotIntervalSeconds;
		}
		if (!IsFinite(authorityTime))
		{
			authorityTime = state.HasPublishedAuthorityTime
				? state.LastPublishedAuthorityTime + simulationDeltaTime
				: 0f;
		}
		else if (state.HasPublishedAuthorityTime && authorityTime <= state.LastPublishedAuthorityTime)
		{
			authorityTime = state.LastPublishedAuthorityTime + simulationDeltaTime;
		}
	}

	private static bool TryBuildLiveSnapshotChunks(RagdollPose[] poses, out List<RagdollPose[]> chunks, out string failure)
	{
		chunks = new List<RagdollPose[]>();
		failure = null;
		if (poses == null || poses.Length == 0 || poses.Length > MaximumPoseSnapshotBodies)
		{
			failure = $"captured pose count {poses?.Length ?? 0} is outside 1..{MaximumPoseSnapshotBodies}";
			return false;
		}

		List<RagdollPose> core = new List<RagdollPose>();
		List<RagdollPose> independent = new List<RagdollPose>();
		foreach (RagdollPose pose in poses)
		{
			if (pose.Kind == RagdollMotionKind.CoreBody)
			{
				core.Add(pose);
			}
			else
			{
				independent.Add(pose);
			}
		}
		if (core.Count == 0 || core.Count > MaximumUnreliablePoseSnapshotChunkBodies)
		{
			failure = $"core pose count {core.Count} does not fit one {MaximumUnreliablePoseSnapshotChunkBodies}-pose datagram";
			return false;
		}

		// Chunk zero is always the complete constrained ragdoll island. Weapons and
		// detached gore follow in one or more independent-body chunks. Every chunk
		// remains comfortably below LiteNetLib's 1024-byte initial MTU (14 * 64-byte
		// poses plus packet and protocol headers).
		chunks.Add(core.ToArray());
		for (int offset = 0; offset < independent.Count; offset += MaximumUnreliablePoseSnapshotChunkBodies)
		{
			int count = Math.Min(MaximumUnreliablePoseSnapshotChunkBodies, independent.Count - offset);
			RagdollPose[] chunk = new RagdollPose[count];
			independent.CopyTo(offset, chunk, 0, count);
			chunks.Add(chunk);
		}
		if (chunks.Count > byte.MaxValue)
		{
			failure = $"chunk count {chunks.Count} exceeds the packet field";
			chunks.Clear();
			return false;
		}
		return true;
	}

	private static void RecordPoseSnapshotSendFailure(RagdollState state, uint snapshotSequence, int chunkIndex,
		int chunkCount, int poseCount, string failure)
	{
		_poseSnapshotSendFailures++;
		if (state == null)
		{
			return;
		}
		state.PoseSnapshotSendFailures++;
		float now = Time.realtimeSinceStartup;
		if (now < state.NextPoseSnapshotFailureLogAt) return;
		state.NextPoseSnapshotFailureLogAt = now + PoseSnapshotFailureLogIntervalSeconds;
		string chunk = chunkIndex >= 0 ? $"{chunkIndex + 1}/{chunkCount}" : "not-built";
		RupturePlugin.Log.LogWarning($"RPT ragdoll pose snapshot send failed: profile={state.ProfileId}, deathSequence={state.DeathSequence}, " +
			$"snapshotSequence={snapshotSequence}, chunk={chunk}, poses={poseCount}, " +
			$"profileFailures={state.PoseSnapshotSendFailures}, totalFailures={_poseSnapshotSendFailures}, failure={failure}.");
	}

	internal static bool TryGetState(Corpse corpse, out object stateToken)
	{
		stateToken = null;
		if (corpse == null)
		{
			return false;
		}

		if (!StatesByCorpse.TryGetValue(corpse, out RagdollState state) && corpse.Ragdoll != null)
		{
			RegisterNativeRagdoll(corpse.Ragdoll);
			StatesByCorpse.TryGetValue(corpse, out state);
		}

		stateToken = state;
		return state != null;
	}

	internal static bool IsDynamicState(object stateToken)
	{
		return stateToken is RagdollState state && !IsStopped(state) && HasDynamicBody(state);
	}

	internal static int ApplyAuthorityRadialImpulse(object stateToken, Vector3 origin, float radius, float force)
	{
		if (!(stateToken is RagdollState state) || (!IsRemoteClient && IsStopped(state)))
		{
			return 0;
		}
		if (IsRemoteClient && !BeginLocalPrediction(state, independentWeaponOnly: false)) return 0;

		int affectedBodies = ApplyRadialImpulse(state, origin, radius, force);
		if (IsRemoteClient && affectedBodies > 0)
		{
			PrunePredictedEventEchoes(state, Time.realtimeSinceStartup);
			state.PredictedRadialImpulses.Add(new PredictedRadialImpulse
			{
				Origin = origin,
				Radius = radius,
				Force = force,
				ExpiresAt = Time.realtimeSinceStartup + PredictedEventEchoLifetimeSeconds
			});
		}
		if (IsNetworkAuthority && state.PlayerId >= 0)
		{
			RagdollRadialImpulseData packet = new RagdollRadialImpulseData
			{
				PlayerId = state.PlayerId,
				ProfileId = state.ProfileId,
				DeathSequence = state.DeathSequence,
				EventSequence = NextEventSequence(),
				Origin = origin,
				Radius = radius,
				Force = force
			};
			MultiplayerRuntime.Current.Send(ref packet);
		}
		return affectedBodies;
	}

	internal static bool TryPrepareLocalRadialPrediction(Corpse corpse, out object stateToken)
	{
		stateToken = null;
		if (!IsRemoteClient) return false;
		RagdollState state = GetStateForCorpse(corpse, allowRetired: true);
		if (state == null || !BeginLocalPrediction(state, independentWeaponOnly: false)) return false;
		stateToken = state;
		return true;
	}

	private static bool ConsumePredictedRadialImpulse(RagdollState state, RagdollRadialImpulseData packet)
	{
		float now = Time.realtimeSinceStartup;
		for (int index = state.PredictedRadialImpulses.Count - 1; index >= 0; index--)
		{
			PredictedRadialImpulse predicted = state.PredictedRadialImpulses[index];
			if (now > predicted.ExpiresAt)
			{
				state.PredictedRadialImpulses.RemoveAt(index);
				continue;
			}
			if ((predicted.Origin - packet.Origin).sqrMagnitude > 0.0025f ||
				Mathf.Abs(predicted.Radius - packet.Radius) > 0.01f || Mathf.Abs(predicted.Force - packet.Force) > 0.01f) continue;
			state.PredictedRadialImpulses.RemoveAt(index);
			return true;
		}
		return false;
	}

	internal static void Receive(RagdollStartData packet)
	{
		if (!IsRemoteClient || string.IsNullOrEmpty(packet.ProfileId) || packet.DeathSequence == 0)
		{
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}

		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		if (state.DeathSequence > packet.DeathSequence)
		{
			return;
		}

		state.DeathSequence = packet.DeathSequence;
		state.PlayerId = packet.PlayerId;
		state.BodyPart = packet.BodyPart;
		state.HitDirection = packet.HitDirection;
		state.DynamicDuration = CorpseSleepPolicy.ClampDuration(packet.DynamicDuration);
		state.StartReceived = true;
		state.DeathStartedAt = state.DeathStartedAt > 0f ? state.DeathStartedAt : Time.realtimeSinceStartup;
		state.LifecycleExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds;
		state.InitialVelocityPending = packet.HasInitialVelocity && IsFinite(packet.InitialVelocity);
		if (state.InitialVelocityPending)
		{
			state.InitialVelocityApplied = false;
		}
		state.InitialVelocity = packet.InitialVelocity;
		StatesByPlayerId[packet.PlayerId] = state;
		ActivatePendingSettle(state);
		ApplyInitialVelocity(state);
		ApplyPendingImpulses(state);
		ApplyPendingShadowForces(state);
		ApplyPendingShadowWakes(state);
	}

	internal static void Receive(RagdollPointImpulseData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.EventSequence == 0 || !IsFinite(packet.LocalPoint) || !IsFinite(packet.Impulse))
		{
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}

		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		if (!RagdollSequencePolicy.TryAcceptEvent(state.DeathSequence, packet.DeathSequence, packet.EventSequence, state.ReceivedEvents))
		{
			return;
		}
		if (ConsumePredictedPointImpulse(state, packet)) return;

		PendingPointImpulse pending = new PendingPointImpulse
		{
			Packet = packet,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		};
		state.PendingPointImpulses.Add(pending);
		ApplyPendingImpulses(state);
	}

	internal static void Receive(RagdollRadialImpulseData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.EventSequence == 0 || !IsFinite(packet.Origin) || !IsFinite(packet.Radius) || !IsFinite(packet.Force) || packet.Radius <= 0f || packet.Force <= 0f)
		{
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}

		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		if (!RagdollSequencePolicy.TryAcceptEvent(state.DeathSequence, packet.DeathSequence, packet.EventSequence, state.ReceivedEvents))
		{
			return;
		}
		if (ConsumePredictedRadialImpulse(state, packet)) return;

		PendingRadialImpulse pending = new PendingRadialImpulse
		{
			Packet = packet,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		};
		state.PendingRadialImpulses.Add(pending);
		ApplyPendingImpulses(state);
	}

	internal static void Receive(RagdollForceData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.EventSequence == 0 ||
			!Enum.IsDefined(typeof(ShadowForceOperation), packet.Operation) || !Enum.IsDefined(typeof(ForceMode), (int)packet.ForceMode) ||
			!IsFinite(packet.Force) || !IsFinite(packet.Point) || !IsFinite(packet.Scalar) || !IsFinite(packet.Radius) || !IsFinite(packet.UpwardsModifier))
		{
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}

		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		if (!RagdollSequencePolicy.TryAcceptEvent(state.DeathSequence, packet.DeathSequence, packet.EventSequence, state.ReceivedEvents))
		{
			return;
		}
		if (ConsumePredictedShadowForce(state, packet)) return;
		state.PendingShadowForces.Add(new PendingShadowForce
		{
			Packet = packet,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		});
		ApplyPendingShadowForces(state);
	}

	internal static void Receive(RagdollWakeData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.EventSequence == 0)
		{
			return;
		}
		bool reactivation = packet.ReactivateSettled != 0;
		RagdollTombstone restoredMotion = null;
		if (!reactivation && RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}
		if (reactivation && TryGetTombstone(packet.ProfileId, packet.PlayerId, out RagdollTombstone tombstone))
		{
			DeathSequenceRelation relation = RagdollSequencePolicy.Classify(tombstone.DeathSequence, packet.DeathSequence);
			if (relation == DeathSequenceRelation.Stale || relation == DeathSequenceRelation.Invalid)
			{
				return;
			}
			restoredMotion = tombstone;
			RemoveTombstone(tombstone);
		}
		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		RestoreMotionSequences(state, restoredMotion);
		if (!RagdollSequencePolicy.TryAcceptEvent(state.DeathSequence, packet.DeathSequence, packet.EventSequence, state.ReceivedEvents))
		{
			return;
		}
		if (reactivation)
		{
			state.StartReceived = true;
			BindStateToRetainedCorpse(state);
			ReactivateState(state, CorpseSleepPolicy.ClampDuration(packet.DynamicDuration));
		}
		state.PendingShadowWakes.Add(new PendingShadowWake
		{
			Packet = packet,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		});
		ApplyPendingShadowWakes(state);
	}

	internal static void Receive(RagdollSettleData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.SnapshotSequence == 0 ||
			!IsFinite(packet.AuthorityTime) || !IsFinite(packet.SimulationDeltaTime) || packet.SimulationDeltaTime <= 0f)
		{
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}

		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		if (state.DeathSequence != packet.DeathSequence)
		{
			return;
		}

		state.PendingPose = packet.Poses ?? Array.Empty<RagdollPose>();
		for (int index = 0; index < state.PendingPose.Length; index++)
		{
			RagdollPose pose = state.PendingPose[index];
			if (pose.IsValid != 0 && (!IsFinite(pose.Position) || !IsFinite(pose.Rotation) ||
				!IsFinite(pose.Velocity) || !IsFinite(pose.AngularVelocity)))
			{
				pose.IsValid = 0;
				state.PendingPose[index] = pose;
			}
		}
		state.SettlePending = true;
		state.SettleExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds;
		RupturePlugin.Log.LogInfo($"RPT ragdoll settle received: profile={state.ProfileId}, sequence={state.DeathSequence}, " +
			$"snapshotsReceived={state.PoseSnapshotsReceived}, snapshotsApplied={state.PoseSnapshotsApplied}, " +
			$"snapshotChunksReceived={state.PoseSnapshotChunksReceived}, incompleteSnapshotsDiscarded={state.PoseSnapshotAssembliesDiscarded}, " +
			$"lastSnapshot={state.LastReceivedPoseSnapshotSequence}, terminalBodies={state.PendingPose.Length}.");
		if (ShadowRagdollWorld.RequiresAuthorityStateBoundary)
		{
			if (IsNewerSnapshotSequence(state.LastReceivedPoseSnapshotSequence, packet.SnapshotSequence) &&
				IsNewerSnapshotSequence(state.AuthorityPlaybackSequenceBarrier, packet.SnapshotSequence))
			{
				state.LastReceivedPoseSnapshotSequence = packet.SnapshotSequence;
				QueueAuthorityFrame(state, new AuthorityPoseFrame
				{
					SnapshotSequence = packet.SnapshotSequence,
					AuthorityTime = packet.AuthorityTime,
					SimulationDeltaTime = packet.SimulationDeltaTime,
					Poses = state.PendingPose,
					Terminal = true
				});
			}
			return;
		}
		ActivatePendingSettle(state);
	}

	internal static void Receive(RagdollPoseSnapshotData packet)
	{
		if (!IsRemoteClient)
		{
			return;
		}
		if (packet.DeathSequence == 0 || packet.SnapshotSequence == 0 || !IsFinite(packet.AuthorityTime) ||
			!IsFinite(packet.SimulationDeltaTime) || packet.SimulationDeltaTime <= 0f || packet.ChunkCount == 0 ||
			packet.ChunkIndex >= packet.ChunkCount || packet.TotalPoseCount == 0 ||
			packet.TotalPoseCount > MaximumPoseSnapshotBodies || packet.Poses == null ||
			packet.Poses.Length == 0 || packet.Poses.Length > MaximumUnreliablePoseSnapshotChunkBodies)
		{
			_poseSnapshotsRejected++;
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			_poseSnapshotsRejected++;
			return;
		}

		RagdollState state = GetOrCreateState(packet.ProfileId, packet.PlayerId, packet.DeathSequence);
		if (state.DeathSequence != packet.DeathSequence)
		{
			_poseSnapshotsRejected++;
			return;
		}
		for (int index = 0; index < packet.Poses.Length; index++)
		{
			RagdollPose pose = packet.Poses[index];
			if (pose.IsValid != 0 && (!IsFinite(pose.Position) || !IsFinite(pose.Rotation) ||
				!IsFinite(pose.Velocity) || !IsFinite(pose.AngularVelocity) ||
				pose.Kind > RagdollMotionKind.DetachedGib ||
				(packet.ChunkIndex == 0 && pose.Kind != RagdollMotionKind.CoreBody) ||
				(packet.ChunkIndex != 0 && pose.Kind == RagdollMotionKind.CoreBody)))
			{
				_poseSnapshotsRejected++;
				return;
			}
		}

		PendingPoseSnapshotAssembly assembly = state.PendingPoseSnapshotAssembly;
		if (assembly != null && assembly.SnapshotSequence != packet.SnapshotSequence)
		{
			if (!IsNewerSnapshotSequence(assembly.SnapshotSequence, packet.SnapshotSequence))
			{
				_poseSnapshotsRejected++;
				return;
			}
			DiscardPendingPoseSnapshotAssembly(state);
			assembly = null;
		}
		if (assembly == null)
		{
			if (!IsNewerSnapshotSequence(state.LastReceivedPoseSnapshotSequence, packet.SnapshotSequence) ||
				!IsNewerSnapshotSequence(state.LastDiscardedPoseSnapshotSequence, packet.SnapshotSequence))
			{
				_poseSnapshotsRejected++;
				return;
			}
			assembly = new PendingPoseSnapshotAssembly(packet.SnapshotSequence, packet.ChunkCount,
				packet.TotalPoseCount, packet.AuthorityTime, packet.SimulationDeltaTime, _poseAssemblyUpdateCycle);
			state.PendingPoseSnapshotAssembly = assembly;
			PendingPoseAssemblyStates.Add(state);
		}
		else if (!assembly.Matches(packet))
		{
			DiscardPendingPoseSnapshotAssembly(state);
			_poseSnapshotsRejected++;
			return;
		}
		if (!assembly.TryAddChunk(packet.ChunkIndex, packet.Poses))
		{
			_poseSnapshotsRejected++;
			return;
		}

		_poseSnapshotChunksReceived++;
		state.PoseSnapshotChunksReceived++;
		if (!assembly.IsComplete)
		{
			return;
		}

		if (!assembly.TryBuild(MaximumPoseSnapshotBodies, out RagdollPose[] completePoses))
		{
			DiscardPendingPoseSnapshotAssembly(state);
			_poseSnapshotsRejected++;
			return;
		}

		PendingPoseAssemblyStates.Remove(state);
		state.PendingPoseSnapshotAssembly = null;
		_poseSnapshotsReceived++;
		state.PoseSnapshotsReceived++;
		state.LastReceivedPoseSnapshotSequence = packet.SnapshotSequence;
		QueueAuthorityFrame(state, new AuthorityPoseFrame
		{
			SnapshotSequence = packet.SnapshotSequence,
			AuthorityTime = assembly.AuthorityTime,
			SimulationDeltaTime = assembly.SimulationDeltaTime,
			Poses = completePoses,
			Terminal = false
		});
	}

	internal static void Receive(DismembermentData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.EventSequence == 0)
		{
			return;
		}
		if (packet.PlayerId < 0 || packet.BodyPart == EBodyPart.Head ||
			(packet.SeverLevel != LimbSeverLevel.Proximal && packet.SeverLevel != LimbSeverLevel.Distal) ||
			RejectRetiredSequence(null, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}
		PendingDismemberments.Add(new PendingDismemberment
		{
			Packet = packet,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		});
		ProcessPendingDismemberments();
	}

	internal static void Receive(HeadWoundData packet)
	{
		if (!IsRemoteClient || packet.DeathSequence == 0 || packet.EventSequence == 0 ||
			packet.PlayerId < 0 || packet.WoundOrdinal < 1 || packet.WoundOrdinal > HeadWoundPolicy.MaximumAttempts || !IsFinite(packet.EntryLocal) || !IsFinite(packet.DirectionLocal) ||
			packet.DirectionLocal.sqrMagnitude < 0.0001f || !IsFinitePositive(packet.EntryRadius) ||
			!IsFinitePositive(packet.ExitRadius) || !IsFinitePositive(packet.ConeDepth) || packet.EntryLocal.sqrMagnitude > 4f ||
			packet.EntryRadius > HeadWoundBallistics.MaximumEntryRadius ||
			packet.ExitRadius > HeadWoundBallistics.MaximumAbsoluteExitRadius || packet.ConeDepth > 0.35f)
		{
			return;
		}
		if (RejectRetiredSequence(packet.ProfileId, packet.PlayerId, packet.DeathSequence))
		{
			return;
		}
		PendingHeadWounds.Add(new PendingHeadWound
		{
			Packet = packet,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		});
		ProcessPendingHeadWounds();
	}

	internal static void Update()
	{
		_poseAssemblyUpdateCycle++;
		if (_poseAssemblyUpdateCycle <= 0) _poseAssemblyUpdateCycle = 1;
		float now = Time.realtimeSinceStartup;
		StateSnapshot.Clear();
		StateSnapshot.AddRange(ActiveStates);
		foreach (RagdollState state in StateSnapshot)
		{
			PrunePredictedEventEchoes(state, now);
			if (state.Player?.HealthController != null && state.Player.HealthController.IsAlive)
			{
				state.ObservedAliveAfterDeath = true;
			}
			if (FreezeCorpseRagdollsForWoundDebug && state.Ragdoll != null)
			{
				FreezeNativeCorpseRagdoll(state);
				state.InitialVelocityPending = false;
				state.InitialVelocityApplied = true;
				if (IsNetworkAuthority) SendSettle(state);
				continue;
			}

			if (IsRemoteClient && state.StartReceived && state.Ragdoll == null && RagdollSequencePolicy.IsExpired(now, state.LifecycleExpiresAt))
			{
				RetireState(state);
				continue;
			}

			if (!IsRemoteClient && !ShadowRagdollWorld.IsDragHeld(state.Corpse) && !state.StopRequested && state.DeathStartedAt > 0f && now >= state.DeathStartedAt + state.DynamicDuration)
			{
				ShadowRagdollWorld.ForceSettle(state.Corpse);
				state.StopRequested = true;
				if (IsNetworkAuthority)
				{
					SendSettle(state);
				}
			}

			// Missing identity or transport must not consume the terminal send.
			if (IsNetworkAuthority && state.StopRequested && !state.SettleSent) SendSettle(state);

			// A predicting client must not end a reanimation from its own wall clock.
			// Only the reliable authority settle packet owns the terminal transition.

			if (state.SettlePending && RagdollSequencePolicy.IsExpired(now, state.SettleExpiresAt))
			{
				state.SettlePending = false;
				PendingSettleStates.Remove(state);
				state.PendingPose = null;
			}

			ActivatePendingSettle(state);
			ApplyPendingImpulses(state);
			ApplyPendingShadowForces(state);
			ApplyPendingShadowWakes(state);
			if (CanRetire(state))
			{
				if (ShouldParkState(state))
				{
					ParkState(state);
				}
				else
				{
					RetireState(state);
				}
			}
		}

		// Publishing authority motion has a narrower lifetime than the retained
		// coordinator state. Keep it in an explicit collection so retirement,
		// query-shell retention, or repeated settle/reactivate cycles cannot silently
		// end an active publication epoch. Poses are emitted only after a completed
		// external step so their timestamp and represented simulation delta match.
		AuthorityMotionSnapshot.Clear();
		AuthorityMotionSnapshot.AddRange(AuthorityMotionStates);
		foreach (RagdollState state in AuthorityMotionSnapshot)
		{
			if (state == null || !state.AuthorityMotionEpochActive || state.Corpse == null)
			{
				AuthorityMotionStates.Remove(state);
			}
		}

		ProcessPendingDismemberments();
		ProcessPendingHeadWounds();
		DiscardExpiredPoseSnapshotAssemblies();
	}

	private static void PrunePredictedEventEchoes(RagdollState state, float now)
	{
		if (state == null) return;
		for (int index = state.PredictedPointImpulses.Count - 1; index >= 0; index--)
			if (now > state.PredictedPointImpulses[index].ExpiresAt) state.PredictedPointImpulses.RemoveAt(index);
		for (int index = state.PredictedRadialImpulses.Count - 1; index >= 0; index--)
			if (now > state.PredictedRadialImpulses[index].ExpiresAt) state.PredictedRadialImpulses.RemoveAt(index);
		for (int index = state.PredictedShadowForces.Count - 1; index >= 0; index--)
			if (now > state.PredictedShadowForces[index].ExpiresAt) state.PredictedShadowForces.RemoveAt(index);
	}

	private static void DiscardExpiredPoseSnapshotAssemblies()
	{
		if (PendingPoseAssemblyStates.Count == 0) return;
		PendingPoseAssemblySnapshot.Clear();
		PendingPoseAssemblySnapshot.AddRange(PendingPoseAssemblyStates);
		foreach (RagdollState state in PendingPoseAssemblySnapshot)
		{
			PendingPoseSnapshotAssembly assembly = state?.PendingPoseSnapshotAssembly;
			if (assembly == null)
			{
				PendingPoseAssemblyStates.Remove(state);
				continue;
			}
			// Chunks may arrive in any order, but they must all arrive between two
			// coordinator updates. Never blend a partial body/weapon/gib snapshot.
			if (assembly.UpdateCycle < _poseAssemblyUpdateCycle)
			{
				DiscardPendingPoseSnapshotAssembly(state);
			}
		}
	}

	private static void DiscardPendingPoseSnapshotAssembly(RagdollState state)
	{
		PendingPoseSnapshotAssembly assembly = state?.PendingPoseSnapshotAssembly;
		if (assembly == null) return;
		state.LastDiscardedPoseSnapshotSequence = assembly.SnapshotSequence;
		state.PendingPoseSnapshotAssembly = null;
		PendingPoseAssemblyStates.Remove(state);
		_poseSnapshotAssembliesDiscarded++;
		state.PoseSnapshotAssembliesDiscarded++;
	}

	private static void SendStart(RagdollState state, bool hasInitialVelocity, Vector3 initialVelocity)
	{
		if (state == null || state.PlayerId < 0 || state.DeathSequence == 0 || !MultiplayerRuntime.Current.HasServer)
		{
			return;
		}
		if (state.SettleSent && state.StartSent)
		{
			return;
		}
		if (state.StartSent && (!hasInitialVelocity || state.InitialVelocitySent))
		{
			return;
		}

		RagdollStartData packet = new RagdollStartData
		{
			PlayerId = state.PlayerId,
			ProfileId = state.ProfileId,
			DeathSequence = state.DeathSequence,
			BodyPart = state.BodyPart,
			HitDirection = state.HitDirection,
			DynamicDuration = state.DynamicDuration,
			HasInitialVelocity = hasInitialVelocity,
			InitialVelocity = hasInitialVelocity ? initialVelocity : Vector3.zero
		};
		MultiplayerRuntime.Current.Send(ref packet);
		state.StartSent = true;
		state.InitialVelocitySent |= hasInitialVelocity;
	}

	private static void SendSettle(RagdollState state)
	{
		if (state == null || state.SettleSent)
		{
			return;
		}

		if (state.PlayerId < 0 || state.DeathSequence == 0 || !MultiplayerRuntime.Current.HasServer)
		{
			return;
		}

		state.NextPoseSnapshotSequence++;
		if (state.NextPoseSnapshotSequence == 0) state.NextPoseSnapshotSequence = 1;
		ResolveOutgoingAuthorityTiming(state, out float authorityTime, out float simulationDeltaTime);
		RagdollSettleData packet = new RagdollSettleData
		{
			PlayerId = state.PlayerId,
			ProfileId = state.ProfileId,
			DeathSequence = state.DeathSequence,
			SnapshotSequence = state.NextPoseSnapshotSequence,
			AuthorityTime = authorityTime,
			SimulationDeltaTime = simulationDeltaTime,
			Poses = CapturePose(state)
		};
		MultiplayerRuntime.Current.Send(ref packet);
		state.SettleSent = true;
		state.AuthorityMotionEpochActive = false;
		AuthorityMotionStates.Remove(state);
		state.HasPublishedAuthorityTime = true;
		state.LastPublishedAuthorityTime = authorityTime;
		state.LastPublishedSimulationDeltaTime = simulationDeltaTime;
		RupturePlugin.Log.LogInfo($"RPT ragdoll settle sent: profile={state.ProfileId}, sequence={state.DeathSequence}, " +
			$"snapshotsSent={state.PoseSnapshotsSent}, motionEpochSnapshots={state.PoseSnapshotsSent - state.PoseSnapshotsAtMotionEpochStart}, " +
			$"snapshotChunksSent={state.PoseSnapshotChunksSent}, snapshotSendFailures={state.PoseSnapshotSendFailures}, " +
			$"terminalBodies={packet.Poses.Length}.");
	}



	private static RagdollState GetOrCreateState(string profileId, int playerId, uint deathSequence)
	{
		if (string.IsNullOrEmpty(profileId))
		{
			if (playerId >= 0 && StatesByPlayerId.TryGetValue(playerId, out RagdollState byPlayerId))
			{
				return byPlayerId;
			}
			profileId = "#fika:" + playerId;
		}

		if (!StatesByProfile.TryGetValue(profileId, out RagdollState state))
		{
			state = new RagdollState
			{
				ProfileId = profileId,
				PlayerId = playerId,
				DeathSequence = deathSequence,
				DynamicDuration = GetConfiguredDuration()
			};
			StatesByProfile[profileId] = state;
			ActiveStates.Add(state);
		}
		else if (deathSequence > state.DeathSequence)
		{
			ResetStateForSequence(state, deathSequence);
			ActiveStates.Add(state);
		}

		if (playerId >= 0)
		{
			state.PlayerId = playerId;
			StatesByPlayerId[playerId] = state;
		}

		return state;
	}

	private static void CaptureCorpseInteraction(RagdollState state, Player player)
	{
		if (state == null || player == null)
		{
			return;
		}
		state.Player = player;
		if (state.Limbs == null)
			state.Limbs = CorpseLimbContext.Capture(player);
		state.PlayerBody = player.PlayerBody;
		if (state.Head?.Bone == null) state.Head = CorpseHeadContext.Capture(player, state.PlayerBody);
		state.VisualRoot = player.Transform?.Original ?? state.PlayerBody?.SkeletonRootJoint?.transform;
		state.IsYourPlayer = player.IsYourPlayer;
		state.BodyParts.Clear();
		BodyPartCollider[] bodyPartColliders = player.PlayerBones?.BodyPartColliders;
		if (bodyPartColliders == null)
		{
			return;
		}
		foreach (BodyPartCollider bodyPartCollider in bodyPartColliders)
		{
			Collider collider = bodyPartCollider?.Collider;
			if (collider == null)
			{
				continue;
			}
			state.BodyParts.Add(new CachedBodyPartBinding
			{
				Collider = collider,
				Transform = collider.transform,
				BodyPart = bodyPartCollider.BodyPartType,
				ColliderType = bodyPartCollider.BodyPartColliderType
			});
		}
	}

	private static CorpseInteractionTarget CreateInteractionTarget(RagdollState state)
	{
		if (state == null)
		{
			return default;
		}
		PlayerBody playerBody = state.PlayerBody != null ? state.PlayerBody : state.Ragdoll?._playerBody;
		Transform visualRoot = state.VisualRoot != null ? state.VisualRoot : state.SkeletonRoot;
		return new CorpseInteractionTarget(
			state.Corpse,
			state.Player,
			playerBody,
			visualRoot ?? playerBody?.SkeletonRootJoint?.transform,
			state.ProfileId,
			state.PlayerId,
			state.DeathSequence,
			state.IsYourPlayer, state.Head, state.Limbs);
	}

	private static bool TryInferBodyPart(string bodyPath, out EBodyPart bodyPart)
	{
		bodyPart = EBodyPart.Common;
		if (string.IsNullOrEmpty(bodyPath))
		{
			return false;
		}
		string normalized = bodyPath.ToLowerInvariant();
		if (normalized.Contains("head"))
		{
			bodyPart = EBodyPart.Head;
		}
		else if (normalized.Contains("lupperarm") || normalized.Contains("lforearm") || normalized.Contains("leftarm"))
		{
			bodyPart = EBodyPart.LeftArm;
		}
		else if (normalized.Contains("rupperarm") || normalized.Contains("rforearm") || normalized.Contains("rightarm"))
		{
			bodyPart = EBodyPart.RightArm;
		}
		else if (normalized.Contains("lthigh") || normalized.Contains("lcalf") || normalized.Contains("leftleg"))
		{
			bodyPart = EBodyPart.LeftLeg;
		}
		else if (normalized.Contains("rthigh") || normalized.Contains("rcalf") || normalized.Contains("rightleg"))
		{
			bodyPart = EBodyPart.RightLeg;
		}
		else if (normalized.Contains("spine3"))
		{
			bodyPart = EBodyPart.Chest;
		}
		else if (normalized.Contains("spine2") || normalized.Contains("pelvis"))
		{
			bodyPart = EBodyPart.Stomach;
		}
		else
		{
			return false;
		}
		return true;
	}

	private static void RegisterBodies(RagdollState state)
	{
		foreach (BodyBinding oldBinding in state.BodiesByPath.Values)
		{
			if (oldBinding.Rigidbody != null)
			{
				BindingsByRigidbody.Remove(oldBinding.Rigidbody);
			}
		}
		state.BodiesByPath.Clear();

		RigidbodySpawner[] spawners = state.Ragdoll?._rigidbodySpawners;
		if (spawners == null)
		{
			return;
		}

		foreach (RigidbodySpawner spawner in spawners)
		{
			Rigidbody rigidbody = spawner?.Rigidbody;
			if (rigidbody == null)
			{
				continue;
			}

			string path = BodyHierarchyPath.RelativeTo(state.SkeletonRoot, rigidbody.transform);
			if (string.IsNullOrEmpty(path))
			{
				path = rigidbody.name;
			}
			BodyBinding binding = new BodyBinding
			{
				State = state,
				Rigidbody = rigidbody,
				Path = path
			};
			state.BodiesByPath[path] = binding;
			BindingsByRigidbody[rigidbody] = binding;
		}
	}

	internal static bool FreezeCorpseRagdollsForWoundDebug =>
		RuptureConfiguration.FreezeCorpseRagdollsForWoundDebug?.Value == true;

	private static void FreezeNativeCorpseRagdoll(RagdollState state)
	{
		if (state == null) return;
		foreach (BodyBinding binding in state.BodiesByPath.Values)
		{
			Rigidbody body = binding.Rigidbody;
			if (body == null) continue;
			if (!body.isKinematic)
			{
				body.velocity = Vector3.zero;
				body.angularVelocity = Vector3.zero;
				body.Sleep();
				body.collisionDetectionMode = CollisionDetectionMode.Discrete;
				body.isKinematic = true;
			}
		}
	}

	private static void ApplyInitialVelocity(RagdollState state)
	{
		if (state == null || !state.InitialVelocityPending || state.InitialVelocityApplied || state.Ragdoll == null)
		{
			return;
		}

		foreach (BodyBinding binding in state.BodiesByPath.Values)
		{
			ShadowRagdollWorld.TrySetSimulationVelocity(binding.Rigidbody, state.InitialVelocity);
		}
		state.InitialVelocityApplied = true;
	}

	private static void ApplyPendingImpulses(RagdollState state)
	{
		if (state == null)
		{
			return;
		}
		if (IsRemoteClient && !state.StartReceived)
		{
			return;
		}
		float now = Time.realtimeSinceStartup;
		for (int index = state.PendingPointImpulses.Count - 1; index >= 0; index--)
		{
			PendingPointImpulse pending = state.PendingPointImpulses[index];
			bool independentWeapon = ShadowRagdollWorld.IsIndependentWeaponPath(pending.Packet.BodyPath);
			if (RagdollSequencePolicy.IsExpired(now, pending.ExpiresAt) || state.DeathSequence != pending.Packet.DeathSequence || (IsStopped(state) && !independentWeapon))
			{
				state.PendingPointImpulses.RemoveAt(index);
				continue;
			}

			if (state.BodiesByPath.TryGetValue(pending.Packet.BodyPath ?? string.Empty, out BodyBinding binding) && binding.Rigidbody != null)
			{
				if (IsRemoteClient && !BeginLocalPrediction(state,
					ShadowRagdollWorld.IsIndependentWeaponPath(pending.Packet.BodyPath))) continue;
				Vector3 worldPoint = binding.Rigidbody.transform.TransformPoint(pending.Packet.LocalPoint);
				if (ShadowRagdollWorld.TryApplySimulationPointImpulse(binding.Rigidbody, pending.Packet.Impulse, worldPoint))
				{
					state.PendingPointImpulses.RemoveAt(index);
				}
			}
		}

		for (int index = state.PendingRadialImpulses.Count - 1; index >= 0; index--)
		{
			PendingRadialImpulse pending = state.PendingRadialImpulses[index];
			if (RagdollSequencePolicy.IsExpired(now, pending.ExpiresAt) || state.DeathSequence != pending.Packet.DeathSequence || IsStopped(state))
			{
				state.PendingRadialImpulses.RemoveAt(index);
				continue;
			}

			if (state.Ragdoll != null)
			{
				if (IsRemoteClient && !BeginLocalPrediction(state, independentWeaponOnly: false)) continue;
				ApplyRadialImpulse(state, pending.Packet.Origin, pending.Packet.Radius, pending.Packet.Force);
				state.PendingRadialImpulses.RemoveAt(index);
			}
		}
	}

	private static void ApplyPendingShadowForces(RagdollState state)
	{
		if (state == null || (IsRemoteClient && !state.StartReceived))
		{
			return;
		}
		float now = Time.realtimeSinceStartup;
		for (int index = state.PendingShadowForces.Count - 1; index >= 0; index--)
		{
			PendingShadowForce pending = state.PendingShadowForces[index];
			if (RagdollSequencePolicy.IsExpired(now, pending.ExpiresAt) || state.DeathSequence != pending.Packet.DeathSequence || IsStopped(state))
			{
				state.PendingShadowForces.RemoveAt(index);
				continue;
			}
			if (state.Ragdoll == null)
			{
				continue;
			}
			if (IsRemoteClient && !BeginLocalPrediction(state,
				ShadowRagdollWorld.IsIndependentWeaponPath(pending.Packet.BodyPath))) continue;
			if (ShadowRagdollWorld.ApplyReceivedForce(
				state.Ragdoll,
				pending.Packet.BodyPath,
				(ShadowForceOperation)pending.Packet.Operation,
				pending.Packet.Force,
				pending.Packet.Point,
				pending.Packet.Scalar,
				pending.Packet.Radius,
				pending.Packet.UpwardsModifier,
				(ForceMode)pending.Packet.ForceMode))
			{
				state.PendingShadowForces.RemoveAt(index);
			}
		}
	}

	private static void ApplyPendingShadowWakes(RagdollState state)
	{
		if (state == null || (IsRemoteClient && !state.StartReceived))
		{
			return;
		}
		float now = Time.realtimeSinceStartup;
		for (int index = state.PendingShadowWakes.Count - 1; index >= 0; index--)
		{
			PendingShadowWake pending = state.PendingShadowWakes[index];
			if (RagdollSequencePolicy.IsExpired(now, pending.ExpiresAt) || state.DeathSequence != pending.Packet.DeathSequence || IsStopped(state))
			{
				state.PendingShadowWakes.RemoveAt(index);
				continue;
			}
			bool woke = pending.Packet.ReactivateSettled != 0
				? state.Corpse != null && ShadowRagdollWorld.WakeFromNetwork(state.Corpse)
				: state.Corpse != null && ShadowRagdollWorld.Wake(state.Corpse);
			if (woke)
			{
				state.PendingShadowWakes.RemoveAt(index);
			}
		}
	}

	internal static void ApplyPendingAuthorityStatesBeforeSimulation()
	{
		if (!IsRemoteClient) return;
		if (PendingPoseSnapshotStates.Count != 0)
		{
			PendingPoseSnapshotStateSnapshot.Clear();
			PendingPoseSnapshotStateSnapshot.AddRange(PendingPoseSnapshotStates);
			foreach (RagdollState state in PendingPoseSnapshotStateSnapshot)
			{
				ApplyAuthorityPlayback(state);
			}
		}
		if (PendingSettleStates.Count != 0)
		{
			PendingSettleStateSnapshot.Clear();
			PendingSettleStateSnapshot.AddRange(PendingSettleStates);
			foreach (RagdollState state in PendingSettleStateSnapshot)
			{
				ApplyPendingSettleNow(state);
			}
		}
	}

	private static void QueueAuthorityFrame(RagdollState state, AuthorityPoseFrame frame)
	{
		if (state == null || frame?.Poses == null || frame.Poses.Length == 0 ||
			!IsNewerSnapshotSequence(state.AuthorityPlaybackSequenceBarrier, frame.SnapshotSequence))
		{
			return;
		}
		if (state.AuthorityPlaybackFrames.Count != 0)
		{
			AuthorityPoseFrame last = state.AuthorityPlaybackFrames[state.AuthorityPlaybackFrames.Count - 1];
			if (!IsNewerSnapshotSequence(last.SnapshotSequence, frame.SnapshotSequence)) return;
			if (frame.AuthorityTime <= last.AuthorityTime)
			{
				frame.AuthorityTime = last.AuthorityTime + Mathf.Max(MinimumAuthorityPlaybackLagSeconds, frame.SimulationDeltaTime);
			}
		}
		state.AuthorityPlaybackFrames.Add(frame);
		while (state.AuthorityPlaybackFrames.Count > MaximumAuthorityPlaybackFrames)
		{
			state.AuthorityPlaybackFrames.RemoveAt(0);
			_authorityPlaybackBacklogFramesDiscarded++;
		}
		if (state.AuthorityPlaybackActive)
		{
			AuthorityPoseFrame latest = state.AuthorityPlaybackFrames[state.AuthorityPlaybackFrames.Count - 1];
			float minimumPlaybackTime = latest.AuthorityTime - GetAuthorityPlaybackLagSeconds(state);
			if (state.AuthorityPlaybackTime < minimumPlaybackTime - 0.001f)
			{
				state.AuthorityPlaybackTime = minimumPlaybackTime;
				state.AuthorityPlaybackLastAppliedTime = float.MinValue;
				_authorityPlaybackTimelineFastForwards++;
			}
		}
		PendingPoseSnapshotStates.Add(state);
	}

	private static float GetAuthorityPlaybackLagSeconds(RagdollState state)
	{
		if (state?.AuthorityPlaybackFrames == null || state.AuthorityPlaybackFrames.Count == 0)
		{
			return MinimumAuthorityPlaybackLagSeconds;
		}
		int latestIndex = state.AuthorityPlaybackFrames.Count - 1;
		AuthorityPoseFrame latest = state.AuthorityPlaybackFrames[latestIndex];
		float observedInterval = latest.SimulationDeltaTime;
		if (latestIndex > 0)
		{
			float timestampInterval = latest.AuthorityTime - state.AuthorityPlaybackFrames[latestIndex - 1].AuthorityTime;
			if (IsFinite(timestampInterval) && timestampInterval > 0f)
			{
				observedInterval = timestampInterval;
			}
		}
		if (!IsFinite(observedInterval) || observedInterval <= 0f)
		{
			observedInterval = MinimumAuthorityPlaybackLagSeconds;
		}
		return Mathf.Max(MinimumAuthorityPlaybackLagSeconds, observedInterval);
	}

	private static void ApplyAuthorityPlayback(RagdollState state)
	{
		if (state == null || !IsRemoteClient || state.Corpse == null ||
			state.AuthorityPlaybackFrames.Count == 0) return;

		float now = Time.realtimeSinceStartup;
		bool justActivated = false;
		if (!state.AuthorityPlaybackActive)
		{
			if (!ShadowRagdollWorld.TryCaptureMotionSnapshot(state.Corpse, out RagdollPose[] predicted) || predicted.Length == 0) return;
			int latestIndex = state.AuthorityPlaybackFrames.Count - 1;
			AuthorityPoseFrame first = state.AuthorityPlaybackFrames[latestIndex];
			if (latestIndex != 0)
			{
				state.AuthorityPlaybackFrames.RemoveRange(0, latestIndex);
				_authorityPlaybackBacklogFramesDiscarded += latestIndex;
			}
			state.AuthorityPlaybackFrames.Insert(0, new AuthorityPoseFrame
			{
				SnapshotSequence = 0,
				AuthorityTime = first.AuthorityTime - Mathf.Max(MinimumAuthorityPlaybackLagSeconds, first.SimulationDeltaTime),
				SimulationDeltaTime = Mathf.Max(MinimumAuthorityPlaybackLagSeconds, first.SimulationDeltaTime),
				Poses = SeedPredictionAnchorWithAuthorityMotion(predicted, first.Poses),
				Terminal = false
			});
			state.AuthorityPlaybackActive = true;
			state.AuthorityPlaybackTime = state.AuthorityPlaybackFrames[0].AuthorityTime;
			state.AuthorityPlaybackUpdatedAt = now;
			state.AuthorityPlaybackLastAppliedTime = float.MinValue;
			justActivated = true;
		}
		else
		{
			float elapsed = Mathf.Clamp(now - state.AuthorityPlaybackUpdatedAt, 0f, 0.1f);
			state.AuthorityPlaybackUpdatedAt = now;
			state.AuthorityPlaybackTime += elapsed;
		}

		while (state.AuthorityPlaybackFrames.Count >= 3 &&
			state.AuthorityPlaybackTime >= state.AuthorityPlaybackFrames[1].AuthorityTime)
		{
			state.AuthorityPlaybackFrames.RemoveAt(0);
		}
		if (state.AuthorityPlaybackFrames.Count < 2) return;

		AuthorityPoseFrame older = state.AuthorityPlaybackFrames[0];
		AuthorityPoseFrame newer = state.AuthorityPlaybackFrames[1];
		float duration = Mathf.Max(0.001f, newer.AuthorityTime - older.AuthorityTime);
		float progress = Mathf.Clamp01((state.AuthorityPlaybackTime - older.AuthorityTime) / duration);
		state.AuthorityPlaybackTime = Mathf.Min(state.AuthorityPlaybackTime, newer.AuthorityTime);
		if (!justActivated && Mathf.Abs(state.AuthorityPlaybackTime - state.AuthorityPlaybackLastAppliedTime) <= 0.00001f) return;

		RagdollPose[] interpolated = InterpolateAuthorityPoses(older.Poses, newer.Poses, progress);
		if (!ShadowRagdollWorld.ApplyAuthorityPose(state.Corpse, interpolated, terminal: false)) return;
		state.AuthorityPlaybackLastAppliedTime = state.AuthorityPlaybackTime;
		if (newer.SnapshotSequence != 0 && state.LastAppliedAuthoritySnapshotSequence != newer.SnapshotSequence)
		{
			state.LastAppliedAuthoritySnapshotSequence = newer.SnapshotSequence;
			_poseSnapshotsApplied++;
			state.PoseSnapshotsApplied++;
		}

		if (!newer.Terminal || progress < 1f) return;
		if (!ShadowRagdollWorld.ApplyAuthorityPose(state.Corpse, newer.Poses, terminal: true)) return;
		CompleteAuthoritySettle(state);
	}

	private static RagdollPose[] SeedPredictionAnchorWithAuthorityMotion(RagdollPose[] predicted, RagdollPose[] authority)
	{
		RagdollPose[] anchor = (RagdollPose[])predicted.Clone();
		if (authority == null)
		{
			return anchor;
		}

		// The synthetic first frame exists only to avoid an initial pose warp. Its
		// momentum must come from the real authority frame: carrying local predicted
		// velocity into kinematic playback feeds the previous shot back into the next
		// local prediction bootstrap.
		for (int anchorIndex = 0; anchorIndex < anchor.Length; anchorIndex++)
		{
			if (anchor[anchorIndex].IsValid == 0)
			{
				continue;
			}
			for (int authorityIndex = 0; authorityIndex < authority.Length; authorityIndex++)
			{
				RagdollPose authorityPose = authority[authorityIndex];
				if (!SameMotionBody(anchor[anchorIndex], authorityPose))
				{
					continue;
				}
				anchor[anchorIndex].Velocity = authorityPose.Velocity;
				anchor[anchorIndex].AngularVelocity = authorityPose.AngularVelocity;
				anchor[anchorIndex].IsSleeping = authorityPose.IsSleeping;
				break;
			}
		}
		return anchor;
	}

	private static RagdollPose[] InterpolateAuthorityPoses(RagdollPose[] older, RagdollPose[] newer, float progress)
	{
		RagdollPose[] result = new RagdollPose[newer.Length];
		for (int index = 0; index < newer.Length; index++)
		{
			RagdollPose target = newer[index];
			result[index] = target;
			if (target.IsValid == 0) continue;
			for (int oldIndex = 0; oldIndex < older.Length; oldIndex++)
			{
				RagdollPose source = older[oldIndex];
				if (!SameMotionBody(source, target)) continue;
				result[index].Position = Vector3.LerpUnclamped(source.Position, target.Position, progress);
				result[index].Rotation = Quaternion.SlerpUnclamped(source.Rotation, target.Rotation, progress);
				result[index].Velocity = Vector3.LerpUnclamped(source.Velocity, target.Velocity, progress);
				result[index].AngularVelocity = Vector3.LerpUnclamped(source.AngularVelocity, target.AngularVelocity, progress);
				break;
			}
		}
		return result;
	}

	private static bool SameMotionBody(RagdollPose left, RagdollPose right)
	{
		return left.IsValid != 0 && left.Kind == right.Kind && left.BodyIndex == right.BodyIndex &&
			left.EffectIndex == right.EffectIndex && left.EventSequence == right.EventSequence;
	}

	private static void CompleteAuthoritySettle(RagdollState state)
	{
		state.SettlePending = false;
		PendingSettleStates.Remove(state);
		state.SettleReceived = true;
		state.StopRequested = true;
		DiscardPendingPoseSnapshotAssembly(state);
		state.PoseApplied = true;
		state.PendingPose = null;
		ClearAuthorityPlayback(state, advanceBarrier: true);
	}

	private static void ClearPendingPoseSnapshot(RagdollState state)
	{
		ClearAuthorityPlayback(state, advanceBarrier: false);
	}

	private static void ClearAuthorityPlayback(RagdollState state, bool advanceBarrier)
	{
		if (state == null) return;
		if (advanceBarrier) state.AuthorityPlaybackSequenceBarrier = state.LastReceivedPoseSnapshotSequence;
		state.AuthorityPlaybackFrames.Clear();
		state.AuthorityPlaybackActive = false;
		state.AuthorityPlaybackTime = 0f;
		state.AuthorityPlaybackUpdatedAt = 0f;
		state.AuthorityPlaybackLastAppliedTime = float.MinValue;
		PendingPoseSnapshotStates.Remove(state);
	}

	private static int ApplyRadialImpulse(RagdollState state, Vector3 origin, float radius, float force)
	{
		if (state == null || !IsFinite(origin) || !IsFinite(radius) || !IsFinite(force) || radius <= 0f || force <= 0f)
		{
			return 0;
		}

		if (state.Corpse != null && ShadowRagdollWorld.TryApplyRadialImpulse(state.Corpse, origin, radius, force, out int shadowBodies))
		{
			return shadowBodies;
		}

		int affectedBodies = 0;
		foreach (BodyBinding binding in state.BodiesByPath.Values)
		{
			Rigidbody rigidbody = ShadowRagdollWorld.GetSimulationBody(binding.Rigidbody);
			if (rigidbody != null && !rigidbody.isKinematic)
			{
				Vector3 offset = rigidbody.worldCenterOfMass - origin;
				float distance = offset.magnitude;
				if (distance > radius)
				{
					continue;
				}
				float falloff = Mathf.Max(0.2f, 1f - distance / radius);
				Vector3 direction = distance > 0.001f ? offset / distance : Vector3.up;
				direction = (direction + Vector3.up * 0.15f).normalized;
				rigidbody.AddForce(direction * (force * falloff), ForceMode.Impulse);
				affectedBodies++;
			}
		}
		return affectedBodies;
	}

	private static RagdollPose[] CapturePose(RagdollState state)
	{
		if (state?.Corpse != null && ShadowRagdollWorld.TryCaptureMotionSnapshot(state.Corpse, out RagdollPose[] shadowPoses))
		{
			return shadowPoses;
		}
		RigidbodySpawner[] spawners = state?.Ragdoll?._rigidbodySpawners;
		if (spawners == null || spawners.Length == 0)
		{
			return Array.Empty<RagdollPose>();
		}

		Rigidbody weapon = state.Ragdoll.WeaponRigidbody;
		RagdollPose[] poses = new RagdollPose[spawners.Length + (weapon != null ? 1 : 0)];
		for (int index = 0; index < spawners.Length; index++)
		{
			Transform transform = spawners[index]?.transform;
			if (transform != null)
			{
				Rigidbody body = spawners[index]?.Rigidbody;
				poses[index].IsValid = 1;
				poses[index].Kind = RagdollMotionKind.CoreBody;
				poses[index].BodyIndex = checked((byte)index);
				poses[index].Position = transform.position;
				poses[index].Rotation = transform.rotation;
				poses[index].Velocity = body != null ? body.velocity : Vector3.zero;
				poses[index].AngularVelocity = body != null ? body.angularVelocity : Vector3.zero;
				poses[index].IsSleeping = body != null && body.IsSleeping() ? (byte)1 : (byte)0;
			}
		}
		if (weapon != null)
		{
			int weaponIndex = spawners.Length;
			poses[weaponIndex].IsValid = 1;
			poses[weaponIndex].Kind = RagdollMotionKind.Weapon;
			poses[weaponIndex].Position = weapon.position;
			poses[weaponIndex].Rotation = weapon.rotation;
			poses[weaponIndex].Velocity = weapon.velocity;
			poses[weaponIndex].AngularVelocity = weapon.angularVelocity;
			poses[weaponIndex].IsSleeping = weapon.IsSleeping() ? (byte)1 : (byte)0;
		}
		return poses;
	}

	private static void ProcessPendingDismemberments()
	{
		if (!IsRemoteClient)
		{
			return;
		}

		float now = Time.realtimeSinceStartup;
		for (int index = PendingDismemberments.Count - 1; index >= 0; index--)
		{
			PendingDismemberment pending = PendingDismemberments[index];
			if (RagdollSequencePolicy.IsExpired(now, pending.ExpiresAt))
			{
				PendingDismemberments.RemoveAt(index);
				if (StatesByPlayerId.TryGetValue(pending.Packet.PlayerId, out RagdollState expiredState) &&
					expiredState.DeathSequence == pending.Packet.DeathSequence && expiredState.StartReceived &&
					!expiredState.ReceivedEvents.Contains(pending.Packet.EventSequence))
				{
					LimbDismembermentSystem.ExpireObserverSever(CreateInteractionTarget(expiredState), pending.Packet.BodyPart, pending.Packet.SeverLevel);
					expiredState.ReceivedEvents.Add(pending.Packet.EventSequence);
				}
				RupturePlugin.Log.LogWarning($"Dismemberment packet expired for Fika player {pending.Packet.PlayerId}.");
				continue;
			}

			if (!StatesByPlayerId.TryGetValue(pending.Packet.PlayerId, out RagdollState state) || state.DeathSequence != pending.Packet.DeathSequence || !state.StartReceived)
			{
				continue;
			}

			CorpseInteractionTarget target = CreateInteractionTarget(state);
			if (target.VisualRoot == null || target.PlayerBody == null)
			{
				continue;
			}
			if (state.ReceivedEvents.Contains(pending.Packet.EventSequence))
			{
				PendingDismemberments.RemoveAt(index);
				continue;
			}

			if (!EventAdmission.TryApply(state.ReceivedEvents, pending.Packet.EventSequence, () =>
			{
				LimbDismembermentSystem.SeverRequestResult result = LimbDismembermentSystem.RequestSever(target, pending.Packet.BodyPart, Vector3.zero,
					pending.Packet.Direction, pending.Packet.InitialDeathSpawn, pending.Packet.EventSequence,
					pending.Packet.IrregularitySeed, pending.Packet.SeverLevel, replicatedObserver: true,
					completion: null, out _);
				if (result == LimbDismembermentSystem.SeverRequestResult.WaitingForGeometry) return false;
				if (result == LimbDismembermentSystem.SeverRequestResult.Rejected)
					LimbDismembermentSystem.ExpireObserverSever(target, pending.Packet.BodyPart, pending.Packet.SeverLevel);
				return true;
			})) continue;
			PendingDismemberments.RemoveAt(index);
		}
	}

	private static void ProcessPendingHeadWounds()
	{
		if (!IsRemoteClient)
		{
			return;
		}

		float now = Time.realtimeSinceStartup;
		PendingHeadWounds.Sort((a, b) => a.Packet.WoundOrdinal.CompareTo(b.Packet.WoundOrdinal));
		for (int index = 0; index < PendingHeadWounds.Count; index++)
		{
			PendingHeadWound pending = PendingHeadWounds[index];
			if (RagdollSequencePolicy.IsExpired(now, pending.ExpiresAt))
			{
				PendingHeadWounds.RemoveAt(index--);
				RupturePlugin.Log.LogWarning($"Head-wound packet expired for Fika player {pending.Packet.PlayerId}.");
				continue;
			}

			if (!StatesByPlayerId.TryGetValue(pending.Packet.PlayerId, out RagdollState state) ||
				state.DeathSequence != pending.Packet.DeathSequence || !state.StartReceived)
			{
				continue;
			}
			CorpseInteractionTarget target = CreateInteractionTarget(state);
			if (target.VisualRoot == null || target.PlayerBody == null)
			{
				continue;
			}
			if (!RagdollSequencePolicy.TryAcceptEvent(state.DeathSequence, pending.Packet.DeathSequence,
				pending.Packet.EventSequence, state.ReceivedEvents))
			{
				PendingHeadWounds.RemoveAt(index--);
				continue;
			}

			HeadWoundSystem.Enqueue(target, pending.Packet);
			PendingHeadWounds.RemoveAt(index--);
		}
	}

	private static bool HasDynamicBody(RagdollState state)
	{
		foreach (BodyBinding binding in state.BodiesByPath.Values)
		{
			if (ShadowRagdollWorld.IsSimulationBodyDynamic(binding.Rigidbody))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsStopped(RagdollState state)
	{
		return state == null || (!ShadowRagdollWorld.IsDragHeld(state.Corpse) &&
			(state.StopRequested || state.SettleReceived || (!state.PostMortemReactivated && state.Ragdoll?._isPhysicsDone == true)));
	}

	private static bool CanRetire(RagdollState state)
	{
		if (state == null || !IsStopped(state) || state.SettlePending || state.PendingPointImpulses.Count != 0 || state.PendingRadialImpulses.Count != 0 || state.PendingShadowForces.Count != 0 || state.PendingShadowWakes.Count != 0)
		{
			return false;
		}
		if (state.PendingPose != null && !state.PoseApplied)
		{
			return false;
		}
		if (IsRemoteClient && state.Ragdoll == null)
		{
			return false;
		}
		if (IsRemoteClient && !state.SettleReceived && !RagdollSequencePolicy.IsExpired(Time.realtimeSinceStartup, state.LifecycleExpiresAt))
		{
			return false;
		}
		if (state.Ragdoll != null && !state.Ragdoll._isPhysicsDone)
		{
			return false;
		}
		if (IsNetworkAuthority && !state.SettleSent)
		{
			return false;
		}

		foreach (PendingDismemberment pending in PendingDismemberments)
		{
			if (pending.Packet.PlayerId == state.PlayerId && pending.Packet.DeathSequence == state.DeathSequence)
			{
				return false;
			}
		}
		foreach (PendingHeadWound pending in PendingHeadWounds)
		{
			if (pending.Packet.PlayerId == state.PlayerId && pending.Packet.DeathSequence == state.DeathSequence)
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsFinitePositive(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
	}

	private static bool ShouldParkState(RagdollState state)
	{
		return state?.Corpse != null && ShadowRagdollWorld.IsSettled(state.Corpse) &&
			(ImpactReactivationEnabled || state.PostMortemReactivated || IsNetworkAuthority || IsRemoteClient);
	}

	private static void ParkState(RagdollState state)
	{
		if (state == null)
		{
			return;
		}
		state.Parked = true;
		ActiveStates.Remove(state);
		state.PendingPointImpulses.Clear();
		state.PendingRadialImpulses.Clear();
		state.PendingShadowForces.Clear();
		state.PendingShadowWakes.Clear();
		DiscardPendingPoseSnapshotAssembly(state);
		ClearPendingPoseSnapshot(state);
	}

	private static RagdollState GetStateForCorpse(Corpse corpse, bool allowRetired)
	{
		if (corpse == null)
		{
			return null;
		}
		if (StatesByCorpse.TryGetValue(corpse, out RagdollState state))
		{
			return state;
		}

		string profileId = corpse.PlayerProfileID;
		if (!string.IsNullOrEmpty(profileId) && StatesByProfile.TryGetValue(profileId, out state))
		{
			BindStateToCorpse(state, corpse);
			return state;
		}
		if (!allowRetired || corpse.Ragdoll == null || !TryGetTombstone(profileId, -1, out RagdollTombstone tombstone))
		{
			return null;
		}

		RemoveTombstone(tombstone);
		state = new RagdollState
		{
			PlayerId = tombstone.PlayerId,
			ProfileId = tombstone.ProfileId,
			DeathSequence = tombstone.DeathSequence,
			DynamicDuration = GetConfiguredDuration(),
			Player = corpse.GetComponentInParent<Player>(),
			StartReceived = IsRemoteClient,
			StartSent = !IsRemoteClient
		};
		RestoreMotionSequences(state, tombstone);
		StatesByProfile[state.ProfileId] = state;
		if (state.PlayerId >= 0)
		{
			StatesByPlayerId[state.PlayerId] = state;
		}
		BindStateToCorpse(state, corpse);
		return state;
	}

	private static void BindStateToRetainedCorpse(RagdollState state)
	{
		if (state == null || state.Corpse != null || !ShadowRagdollWorld.TryGetCorpse(state.ProfileId, out Corpse corpse))
		{
			return;
		}
		BindStateToCorpse(state, corpse);
	}

	private static void BindCorpseLifetime(RagdollState state, Corpse corpse)
	{
		ObjectLifetime.Bind(corpse.gameObject, "ragdoll", () =>
		{
			if (StatesByCorpse.TryGetValue(corpse, out RagdollState current) && ReferenceEquals(current, state))
				RetireState(state);
		});
	}

	private static void BindStateToCorpse(RagdollState state, Corpse corpse)
	{
		if (state == null || corpse?.Ragdoll == null)
		{
			return;
		}
		state.Corpse = corpse;
		state.Ragdoll = corpse.Ragdoll;
		state.PlayerBody = corpse.Ragdoll._playerBody;
		if (state.Head?.Bone == null) state.Head = CorpseHeadContext.Capture(state.Player, state.PlayerBody);
		state.SkeletonRoot = state.PlayerBody?.SkeletonRootJoint?.transform;
		if (state.VisualRoot == null)
		{
			state.VisualRoot = state.SkeletonRoot;
		}
		state.Player ??= corpse.GetComponentInParent<Player>();
		StatesByCorpse[corpse] = state;
		ShadowRagdollWorld.BindDragDeathIdentity(corpse, corpse.Ragdoll, state.DeathSequence);
		BindCorpseLifetime(state, corpse);
		RegisterBodies(state);
	}

	private static void ReactivateState(RagdollState state, int duration)
	{
		if (state == null)
		{
			return;
		}
		state.DynamicDuration = CorpseSleepPolicy.ClampDuration(duration);
		state.DeathStartedAt = Time.realtimeSinceStartup;
		state.LifecycleExpiresAt = state.DeathStartedAt + PendingLifetimeSeconds;
		state.StopRequested = false;
		state.SettleSent = false;
		state.SettleReceived = false;
		state.SettlePending = false;
		PendingSettleStates.Remove(state);
		state.PendingPose = null;
		state.PoseApplied = false;
		state.NextPoseSnapshotAt = 0f;
		state.HasPublishedAuthorityTime = false;
		state.LastPublishedAuthorityTime = 0f;
		state.LastPublishedSimulationDeltaTime = PoseSnapshotIntervalSeconds;
		DiscardPendingPoseSnapshotAssembly(state);
		ClearPendingPoseSnapshot(state);
		state.Parked = false;
		state.PostMortemReactivated = true;
		state.InitialProjectileImpactPending = false;
		state.InitialExplosionImpactPending = false;
		ActiveStates.Add(state);
		if (IsNetworkAuthority)
		{
			if (!state.AuthorityMotionEpochActive)
			{
				state.PoseSnapshotsAtMotionEpochStart = state.PoseSnapshotsSent;
				state.AuthorityMotionEpochActive = true;
			}
			AuthorityMotionStates.Add(state);
		}
	}

	private static bool ConsumeInitialProjectileImpact(RagdollState state)
	{
		if (state?.InitialProjectileImpactPending != true)
		{
			return false;
		}
		state.InitialProjectileImpactPending = false;
		return true;
	}

	internal static void MarkInitialExplosionImpact(Player player, Vector3 center)
	{
		// A native corpse can register before the lethal damage postfix.
		if (player == null || !StatesByProfile.TryGetValue(player.ProfileId, out RagdollState state)) return;
		state.InitialExplosionEvent = Rupture.Impacts.ExplosionEventScope.SequenceFor(center);
		state.InitialExplosionImpactPending = state.InitialExplosionEvent != 0;
	}

	private static bool ConsumeInitialExplosionImpact(RagdollState state)
	{
		if (state?.InitialExplosionImpactPending != true ||
			!Rupture.Impacts.ExplosionEventScope.IsCurrent(state.InitialExplosionEvent))
		{
			return false;
		}
		state.InitialExplosionImpactPending = false;
		return true;
	}

	private static bool IsExplosionDamage(EDamageType damageType)
	{
		return damageType == EDamageType.Landmine || damageType == EDamageType.Explosion || damageType == EDamageType.GrenadeFragment;
	}

	private static bool IsProjectileDamage(EDamageType damageType)
	{
		return !IsExplosionDamage(damageType) && damageType != EDamageType.Barbed && damageType != EDamageType.Flame && damageType != EDamageType.Blunt;
	}

	internal static uint NextNetworkEventSequence()
	{
		return NextEventSequence();
	}

	private static int GetConfiguredDuration()
	{
		return CorpseSleepPolicy.ClampDuration(RuptureConfiguration.RagdollSleepTime?.Value ?? 5);
	}

	private static uint NextDeathSequence()
	{
		if (_nextDeathSequence == 0)
		{
			_nextDeathSequence = 1;
		}
		return _nextDeathSequence++;
	}

	private static uint NextEventSequence()
	{
		if (_nextEventSequence == 0)
		{
			_nextEventSequence = 1;
		}
		return _nextEventSequence++;
	}

	private static bool IsNewerSnapshotSequence(uint current, uint incoming)
	{
		return incoming != 0 && (current == 0 || unchecked((int)(incoming - current)) > 0);
	}

	private static void ActivatePendingSettle(RagdollState state)
	{
		if (state == null || !state.SettlePending || (IsRemoteClient && !state.StartReceived))
		{
			return;
		}
		if (IsRemoteClient && ShadowRagdollWorld.RequiresAuthorityStateBoundary)
		{
			PendingSettleStates.Add(state);
			return;
		}
		ApplyPendingSettleNow(state);
	}

	private static void ApplyPendingSettleNow(RagdollState state)
	{
		if (state == null || !state.SettlePending || (IsRemoteClient && !state.StartReceived)) return;

		bool settled = state.PendingPose == null || state.PendingPose.Length == 0
			? ShadowRagdollWorld.ForceSettle(state.Corpse)
			: ShadowRagdollWorld.ApplyAuthorityPose(state.Corpse, state.PendingPose, terminal: true);
		if (!settled)
		{
			return;
		}

		state.SettlePending = false;
		PendingSettleStates.Remove(state);
		state.SettleReceived = true;
		state.StopRequested = true;
		DiscardPendingPoseSnapshotAssembly(state);
		ClearPendingPoseSnapshot(state);
		state.PoseApplied = true;
		state.PendingPose = null;
	}

	private static void ResetStateForSequence(RagdollState state, uint deathSequence)
	{
		if (state.DeathSequence != 0) state.ImpactLifetime = new object();
		PendingSettleStates.Remove(state);
		DiscardPendingPoseSnapshotAssembly(state);
		DetachBodyBindings(state);
		if (!ReferenceEquals(state.Corpse, null) && StatesByCorpse.TryGetValue(state.Corpse, out RagdollState corpseState) && ReferenceEquals(corpseState, state))
		{
			StatesByCorpse.Remove(state.Corpse);
		}
		state.DeathSequence = deathSequence;
		state.Corpse = null;
		state.Ragdoll = null;
		state.PlayerBody = state.Player?.PlayerBody;
		state.VisualRoot = state.Player != null ? state.Player.Transform?.Original : null;
		state.SkeletonRoot = null;
		state.BodyParts.Clear();
		state.StartReceived = false;
		state.StartSent = false;
		state.InitialVelocitySent = false;
		state.StopRequested = false;
		state.SettleReceived = false;
		state.SettlePending = false;
		state.SettleSent = false;
		state.PendingPose = null;
		ClearPendingPoseSnapshot(state);
		state.PoseApplied = false;
		state.NextPoseSnapshotAt = 0f;
		state.NextPoseSnapshotSequence = 0;
		state.HasPublishedAuthorityTime = false;
		state.LastPublishedAuthorityTime = 0f;
		state.LastPublishedSimulationDeltaTime = PoseSnapshotIntervalSeconds;
		state.LastReceivedPoseSnapshotSequence = 0;
		state.LastDiscardedPoseSnapshotSequence = 0;
		state.AuthorityPlaybackSequenceBarrier = 0;
		state.LastAppliedAuthoritySnapshotSequence = 0;
		state.PoseSnapshotsSent = 0;
		state.PoseSnapshotsReceived = 0;
		state.PoseSnapshotsApplied = 0;
		state.PoseSnapshotChunksSent = 0;
		state.PoseSnapshotChunksReceived = 0;
		state.PoseSnapshotAssembliesDiscarded = 0;
		state.PoseSnapshotSendFailures = 0;
		state.PoseSnapshotsAtMotionEpochStart = 0;
		state.NextPoseSnapshotFailureLogAt = 0f;
		state.InitialVelocityPending = false;
		state.InitialVelocityApplied = false;
		state.ObservedAliveAfterDeath = false;
		state.Parked = false;
		state.PostMortemReactivated = false;
		state.AuthorityMotionEpochActive = false;
		state.InitialProjectileImpactPending = false;
		state.InitialExplosionImpactPending = false;
		state.ReceivedEvents.Clear();
		state.PendingPointImpulses.Clear();
		state.PendingRadialImpulses.Clear();
		state.PendingShadowForces.Clear();
		state.PendingShadowWakes.Clear();
		state.PredictedPointImpulses.Clear();
		state.PredictedRadialImpulses.Clear();
		state.PredictedShadowForces.Clear();
		state.DeathStartedAt = Time.realtimeSinceStartup;
		state.LifecycleExpiresAt = state.DeathStartedAt + PendingLifetimeSeconds;
	}

	private static bool RejectRetiredSequence(string profileId, int playerId, uint incomingDeathSequence)
	{
		if (!TryGetTombstone(profileId, playerId, out RagdollTombstone tombstone))
		{
			return false;
		}

		DeathSequenceRelation relation = RagdollSequencePolicy.Classify(tombstone.DeathSequence, incomingDeathSequence);
		if (relation == DeathSequenceRelation.Newer)
		{
			RemoveTombstone(tombstone);
			return false;
		}
		return true;
	}

	private static bool TryGetTombstone(string profileId, int playerId, out RagdollTombstone tombstone)
	{
		if (!string.IsNullOrEmpty(profileId) && TombstonesByProfile.TryGetValue(profileId, out tombstone))
		{
			return true;
		}
		if (playerId >= 0 && TombstonesByPlayerId.TryGetValue(playerId, out tombstone))
		{
			return true;
		}
		tombstone = null;
		return false;
	}

	private static void RetireState(RagdollState state)
	{
		if (state == null)
		{
			return;
		}

		RagdollTombstone tombstone = new RagdollTombstone
		{
			PlayerId = state.PlayerId,
			ProfileId = state.ProfileId,
			DeathSequence = state.DeathSequence,
			NextPoseSnapshotSequence = state.NextPoseSnapshotSequence,
			LastReceivedPoseSnapshotSequence = state.LastReceivedPoseSnapshotSequence,
			LastDiscardedPoseSnapshotSequence = state.LastDiscardedPoseSnapshotSequence
		};
		DetachState(state);
		if (!string.IsNullOrEmpty(tombstone.ProfileId))
		{
			TombstonesByProfile[tombstone.ProfileId] = tombstone;
		}
		if (tombstone.PlayerId >= 0)
		{
			TombstonesByPlayerId[tombstone.PlayerId] = tombstone;
		}

		state.Player = null;
		state.Head = null;
		state.Limbs = null;
		state.PlayerBody = null;
		state.VisualRoot = null;
		state.Corpse = null;
		state.Ragdoll = null;
		state.SkeletonRoot = null;
		state.PendingPose = null;
		state.BodyParts.Clear();
		ClearPendingPoseSnapshot(state);
		state.ReceivedEvents.Clear();
		state.PendingPointImpulses.Clear();
		state.PendingRadialImpulses.Clear();
		state.PendingShadowForces.Clear();
		state.PendingShadowWakes.Clear();
		state.PredictedPointImpulses.Clear();
		state.PredictedRadialImpulses.Clear();
		state.PredictedShadowForces.Clear();
	}

	private static void RemoveTombstone(string profileId, int playerId)
	{
		if (TryGetTombstone(profileId, playerId, out RagdollTombstone tombstone))
		{
			RemoveTombstone(tombstone);
		}
	}

	private static void RemoveTombstone(RagdollTombstone tombstone)
	{
		if (tombstone == null)
		{
			return;
		}
		if (!string.IsNullOrEmpty(tombstone.ProfileId) && TombstonesByProfile.TryGetValue(tombstone.ProfileId, out RagdollTombstone profileTombstone) && ReferenceEquals(profileTombstone, tombstone))
		{
			TombstonesByProfile.Remove(tombstone.ProfileId);
		}
		if (tombstone.PlayerId >= 0 && TombstonesByPlayerId.TryGetValue(tombstone.PlayerId, out RagdollTombstone playerTombstone) && ReferenceEquals(playerTombstone, tombstone))
		{
			TombstonesByPlayerId.Remove(tombstone.PlayerId);
		}
	}

	private static bool IsDuplicateDeathRegistration(RagdollState state, Player player)
	{
		// Fika's owner snapshot resolves the interaction target before ApplyDeath,
		// binding Player on a native-corpse registration whose sequence is still zero.
		// Only a completed death registration can suppress allocation and SendStart.
		return state != null && state.DeathSequence != 0 &&
			ReferenceEquals(state.Player, player) && !state.ObservedAliveAfterDeath;
	}

	private static object DetachStateForDeathRegistration(RagdollState state, Player player)
	{
		// Native corpse creation can precede RecordDamage and RegisterDeath. Replacing
		// that provisional registration is not victim teardown: retain the lethal
		// momentum (or imported owner snapshot), projectile deduplication and death flags.
		bool provisional = state.DeathSequence == 0 && !state.ObservedAliveAfterDeath &&
			(ReferenceEquals(state.Player, null) || ReferenceEquals(state.Player, player));
		DetachState(state, removeImpactState: !provisional);
		return provisional ? state.ImpactLifetime : null;
	}

	private static void DetachState(RagdollState state, bool removeImpactState = true)
	{
		if (state == null)
		{
			return;
		}
		DiscardPendingPoseSnapshotAssembly(state);
		ClearPendingPoseSnapshot(state);
		PendingSettleStates.Remove(state);
		ActiveStates.Remove(state);
		state.AuthorityMotionEpochActive = false;
		AuthorityMotionStates.Remove(state);
		DetachBodyBindings(state);
		if (!string.IsNullOrEmpty(state.ProfileId) && StatesByProfile.TryGetValue(state.ProfileId, out RagdollState profileState) && ReferenceEquals(profileState, state))
		{
			StatesByProfile.Remove(state.ProfileId);
			if (removeImpactState) Rupture.Impacts.ImpactStateService.Remove(state.ProfileId);
		}
		if (state.PlayerId >= 0 && StatesByPlayerId.TryGetValue(state.PlayerId, out RagdollState playerState) && ReferenceEquals(playerState, state))
		{
			StatesByPlayerId.Remove(state.PlayerId);
		}
		if (state.Corpse != null && StatesByCorpse.TryGetValue(state.Corpse, out RagdollState corpseState) && ReferenceEquals(corpseState, state))
		{
			StatesByCorpse.Remove(state.Corpse);
		}
	}

	private static void DetachBodyBindings(RagdollState state)
	{
		foreach (BodyBinding binding in state.BodiesByPath.Values)
		{
			if (!ReferenceEquals(binding.Rigidbody, null) && BindingsByRigidbody.TryGetValue(binding.Rigidbody, out BodyBinding current) && ReferenceEquals(current, binding))
			{
				BindingsByRigidbody.Remove(binding.Rigidbody);
			}
		}
		state.BodiesByPath.Clear();
	}

	private static bool IsFinite(Vector3 vector)
	{
		return IsFinite(vector.x) && IsFinite(vector.y) && IsFinite(vector.z);
	}

	private static bool IsFinite(Quaternion quaternion)
	{
		float squareMagnitude = quaternion.x * quaternion.x + quaternion.y * quaternion.y + quaternion.z * quaternion.z + quaternion.w * quaternion.w;
		return IsFinite(quaternion.x)
			&& IsFinite(quaternion.y)
			&& IsFinite(quaternion.z)
			&& IsFinite(quaternion.w)
			&& squareMagnitude > 0.0001f;
	}

	private static bool IsFinite(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value);
	}
}
