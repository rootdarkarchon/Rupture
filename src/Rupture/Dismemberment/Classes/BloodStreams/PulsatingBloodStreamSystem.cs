using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using Systems.Effects;
using UnityEngine;
using Rupture.Dismemberment.Classes.HeadWounds;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Ragdolls.Classes;

namespace Rupture.Dismemberment.Classes.BloodStreams;

internal static partial class PulsatingBloodStreamSystem
{
	internal const int MaximumVisualEmitters = 64;
	internal const int MaximumRenderedEmitters = 24;
	internal const int MaximumStreamSamplesPerEmitter = 96;
	private const int MaximumDecalsPerFrame = 3;
	private const int MaximumDecalsPerSecond = 12;
	private const int MaximumDismembermentDecalsPerFrame = 6;
	private const int MaximumDismembermentDecalsPerSecond = 20;
	private const float SpatialDecalCooldown = 0.65f;
	private const float SpatialDecalCellSize = 0.22f;
	private const float DismembermentSpatialDecalCooldown = 0.24f;
	private const float DismembermentSpatialDecalCellSize = 0.10f;
	private const float HeadResolutionWaitSeconds = 1.4f;
	private const float MaximumFlightSeconds = 1.8f;
	private const float BaseTraceInterval = 0.12f;
	private const float BaseDecalInterval = 0.75f;
	private const float FiniteFadeStart = 0.75f;
	private const float FiniteEndStrength = 0.12f;
	private const float WoundOffshootRecedeSeconds = 0.52f;
	private const float StreamSampleInterval = 1f / 50f;
	private const float WoundParcelDirectionSpread = 0.028f;
	private const float WoundParcelLengthOverlap = 1.18f;
	private const float MaximumWoundParcelLength = 0.12f;
	private const int WoundParcelSides = 6;
	private const int WoundParcelAxialRings = 4;
	private const int TrajectorySegments = 14;

	private struct BaseFlowState
	{
		internal bool Active;
		internal bool TraceValid;
		internal bool HasImpact;
		internal bool HasImpactCell;
		internal int ImpactCellKey;
		internal Vector3 ReferenceOrigin, ReferenceVelocity, ReferenceGravity;
		internal float ReferenceRadius;
		internal uint ShapeSeed;
		internal Vector3 Origin;
		internal Vector3 Direction;
		internal Vector3 Velocity;
		internal Vector3 ImpactPoint;
		internal Vector3 ImpactNormal;
		internal float FlightDuration;
		internal float ImpactAge;
		internal float Radius;
		internal float Strength;
		internal float NextTraceAt;
		internal float NextDecalAt;
	}

	private sealed class StreamEmitter
	{
		internal bool Active;
		internal bool LocalFracture;
		internal bool Visible;
		internal BloodStreamData Descriptor;
		internal Player Player;
		internal Transform Anchor;
		internal float StartedAt;
		internal float StartsAt;
		internal float ExpiresAt;
		internal float NextPulseAt;
		internal bool HeartbeatImpactScheduled;
		internal float WobbleTransitionStartedAt;
		internal Vector2 OriginWobbleFrom;
		internal Vector2 OriginWobbleTarget;
		internal Vector2 DirectionWobbleFrom;
		internal Vector2 DirectionWobbleTarget;
		internal uint VisualIndex;
		internal int SampleStart;
		internal int SampleCount;
		internal float NextSampleExpiryAt;
		internal float NextSampleAt;
		internal bool Draining;
		internal float DrainStartedAt;
		internal float DrainEndsAt;
		internal uint PulseIndex;
		internal float WoundOffshootStartedAt;
		internal BaseFlowState BaseFlow;
		internal float NextDripAt;
	}

	private struct StreamRingSample
	{
		internal uint Seed;
		internal Vector3 Origin;
		internal Vector3 Velocity;
		internal Vector3 Gravity;
		internal float StartedAt;
		internal float EndAt;
		internal float Radius;
		internal float Strength;
	}

	private struct DecalCell
	{
		internal bool Used;
		internal Vector3 Position;
		internal float Time;
	}
	private struct DripImpact
	{
		internal bool Active;
		internal float ArrivesAt;
		internal Vector3 Point, Normal;
		internal BloodStreamTriggerKind Kind;
		internal float Strength;
		internal uint Seed;
		internal int Priority;
		internal bool Burst;
	}
	private static readonly DripImpact[] DripImpacts = new DripImpact[128];

	private struct PendingDescriptor
	{
		internal bool Active;
		internal BloodStreamData Packet;
		internal float ExpiresAt;
	}

	private struct SequenceRecord
	{
		internal bool Active;
		internal string ProfileId;
		internal uint DeathSequence;
		internal uint LastEventSequence;
		internal float UpdatedAt;
	}

	private static readonly StreamEmitter[] Emitters = CreateEmitters();
	private static readonly StreamRingSample[] StreamSamples =
		new StreamRingSample[MaximumVisualEmitters * MaximumStreamSamplesPerEmitter];
	private static readonly int[] VisibleEmitterIndices = new int[MaximumRenderedEmitters];
	private static readonly float[] VisibleEmitterDistances = new float[MaximumRenderedEmitters];
	private static readonly uint[] RetiredStreamIds = new uint[256];
	private static readonly DecalCell[] DecalCells = new DecalCell[48];
	private static readonly PendingDescriptor[] PendingDescriptors = new PendingDescriptor[MaximumVisualEmitters];
	private static readonly SequenceRecord[] SequenceRecords = new SequenceRecord[MaximumVisualEmitters];

	private static int _retiredWriteIndex;
	private static int _decalFrame = -1;
	private static int _decalsThisFrame;
	private static float _decalSecondStarted;
	private static int _decalsThisSecond;
	private static int _decalCellWriteIndex;
	private static bool _warnedMissingEffects;
	private static bool _wasEnabled;
	private static float _nextRestoreAt;
	private static bool _lifecycleEnabled;
	private sealed class LivingStream
	{
		internal BloodStreamData Descriptor;
		internal Player Player;
		internal float ResolveExpiresAt;
	}
	// Gameplay lifetime is independent of the bounded, evictable visual emitter pool.
	private static readonly EffectLifetimes<LivingStream> LivingStreams = new EffectLifetimes<LivingStream>();
	private static readonly Dictionary<(string, EBodyPart, BloodStreamTriggerKind), HashSet<uint>> LivingWounds =
		new Dictionary<(string, EBodyPart, BloodStreamTriggerKind), HashSet<uint>>();
	private static void UnindexLivingStream(LivingStream stream)
	{
		var packet = stream.Descriptor;
		var key = (packet.ProfileId, packet.BodyPart, packet.TriggerKind);
		if (LivingWounds.TryGetValue(key, out var ids) && ids.Remove(packet.StreamId) && ids.Count == 0) LivingWounds.Remove(key);
	}

	internal static void PublishLivingWound(Player player, EBodyPart part, EBodyPartColliderType collider,
		Vector3 point, Vector3 direction, BloodStreamTriggerKind kind, uint generation)
	{
		bool light = kind == BloodStreamTriggerKind.LightWound;
		BloodStreamData descriptor = CreateDescriptor(player, kind, part, collider, point, direction,
			0f, light ? 0.06f : 1.5f, light ? 0.0022f : 0.0052f, 0, generation);
		Publish(descriptor, true);
	}

	internal static void StopLivingWound(Player player, EBodyPart part)
	{
		LivingStreams.StopWhere(stream => stream.Descriptor.BodyPart == part && ProfileMatches(stream.Descriptor.ProfileId, player.ProfileId),
			stream => StopLivingStream(stream, true));
	}

	// Fractures are already synchronized health state. Reuse light-wound visuals
	// locally without publishing packets or changing EFT's bleeding effects.
	internal static uint StartFractureWound(Player player, EBodyPart part, Transform anchor,
		Vector3 point, Vector3 direction, uint seed)
	{
		if (!CanCreateStreams() || MultiplayerRuntime.Current.IsHeadless || anchor == null ||
			player?.HealthController == null || !player.HealthController.IsAlive) return 0;
		var descriptor = CreateDescriptor(player, BloodStreamTriggerKind.LightWound, part, default,
			point, direction, 0f, 0.06f, 0.0022f, 0, seed ^ 0xF4AC7B19u);
		StartOrReplace(descriptor, player, anchor, localFracture: true);
		return UpdateFractureWound(descriptor.StreamId, anchor, point, direction) ? descriptor.StreamId : 0;
	}

	internal static bool UpdateFractureWound(uint id, Transform anchor, Vector3 point, Vector3 direction)
	{
		int index = FindActive(id);
		if (index < 0 || !Emitters[index].LocalFracture || Emitters[index].Draining) return false;
		var emitter = Emitters[index];
		emitter.Anchor = anchor;
		emitter.Descriptor.LocalPoint = point;
		emitter.Descriptor.LocalDirection = direction;
		return true;
	}

	internal static void StopFractureWound(uint id)
	{
		if (id == 0) return;
		ClothingSoakingSystem.Stop(id);
		int index = FindActive(id);
		if (index >= 0 && Emitters[index].LocalFracture) StopById(id);
	}

	internal static bool HasLivingWound(Player player, EBodyPart part, BloodStreamTriggerKind kind)
	{
		return LivingWounds.ContainsKey((player.ProfileId, part, kind));
	}

	internal static void PublishKillStream(Player player, EFT.Ballistics.DamageInfo damageInfo, EBodyPart bodyPart,
		uint deathSequence, bool waitForHeadExit)
	{
		if (!CanCreateStreams() || player == null || !IsFinite(damageInfo.HitPoint)) return;
		Transform anchor = ResolveDamageAnchor(player, damageInfo.HitCollider, damageInfo.BodyPartColliderType,
			bodyPart, out EBodyPartColliderType colliderType);
		if (anchor == null) return;
		// Keep the pending descriptor pointed back out of the entry wound. An exact
		// procedural exit replaces both this point and direction before the first pulse.
		Vector3 outward = ResolveEntryDirection(damageInfo);
		float duration = Mathf.Clamp(RuptureConfiguration.KillshotStreamDuration.Value, 0.25f, 30f);
		BloodStreamData descriptor = CreateDescriptor(player,
			waitForHeadExit ? BloodStreamTriggerKind.HeadKillPending : BloodStreamTriggerKind.KillEntry,
			bodyPart, colliderType, anchor.InverseTransformPoint(damageInfo.HitPoint),
			anchor.InverseTransformDirection(outward), duration, 2.0f, 0.0078f, deathSequence,
			(uint)(damageInfo.FireIndex * 486187739) ^ deathSequence);
		Publish(descriptor, clientRequestAllowed: false);
	}

	internal static void StartProceduralDismembermentStreams(CorpseInteractionTarget target, Transform bodyAnchor,
		Vector3 bodyPointWorld, Transform gibAnchor, Vector3 gibPointWorld, Vector3 distalAxisWorld,
		EBodyPart bodyPart, uint eventSequence, bool initialDeathSpawn)
	{
		if (!CanCreateStreams() || MultiplayerRuntime.Current.IsHeadless || bodyAnchor == null || gibAnchor == null) return;
		float bodyDuration = Mathf.Clamp(RuptureConfiguration.DismembermentStreamDuration.Value, 0.25f, 30f);
		bool postMortem = !initialDeathSpawn;
		if (postMortem) bodyDuration = Mathf.Min(bodyDuration, 3f);
		float gibDuration = bodyDuration * 0.5f;
		Vector3 axis = distalAxisWorld.sqrMagnitude > 0.0001f ? distalAxisWorld.normalized : bodyAnchor.up;
		BloodStreamTriggerKind kind = postMortem
			? BloodStreamTriggerKind.PostMortemStumpFlow
			: BloodStreamTriggerKind.DismembermentStump;
		StartLocalProceduralStream(target, bodyAnchor, bodyPointWorld, axis, bodyPart, eventSequence,
			kind, bodyDuration, postMortem, 0xB0D157u);
		StartLocalProceduralStream(target, gibAnchor, gibPointWorld, -axis, bodyPart, eventSequence,
			kind, gibDuration, postMortem, 0x61B157u);
	}
	internal static void StartLivingSever(CorpseInteractionTarget target, Transform anchor, Vector3 point,
		Vector3 axis, EBodyPart part, uint sequence)
	{
		RegisterLivingStump(target, anchor, point, axis, part, sequence);
	}
	internal static void StopLivingSever(Player player, EBodyPart part)
	{
		LivingStumps.Remove((player, part));
		for (int i = 0; i < Emitters.Length; i++)
			if (Emitters[i].Active && Emitters[i].Player == player && Emitters[i].Descriptor.BodyPart == part &&
				Emitters[i].Descriptor.TriggerKind == BloodStreamTriggerKind.LivingSeverStump)
				StopById(Emitters[i].Descriptor.StreamId);
	}

	internal static void StartPostMortemHeadWound(CorpseInteractionTarget target, HeadWoundData packet,
		Transform head, Vector3 entry, Vector3 exit, Vector3 direction, bool hasExit)
	{
		if (packet.IsLethal || !CanCreateStreams() || MultiplayerRuntime.Current.IsHeadless || head == null) return;
		try
		{
			float duration = Mathf.Min(Mathf.Clamp(RuptureConfiguration.DismembermentStreamDuration.Value, 0.25f, 30f), 3f);
			// The shared postmortem profile also covers head entry/exit wounds;
			// StreamRendering renders every profile through independent parcels.
			Vector3 axis = head.TransformDirection(direction).normalized;
			StartLocalProceduralStream(target, head, head.TransformPoint(entry), -axis, EBodyPart.Head,
				packet.WoundOrdinal, BloodStreamTriggerKind.PostMortemStumpFlow, duration, true, 0x48454144u);
			if (hasExit) StartLocalProceduralStream(target, head, head.TransformPoint(exit), axis, EBodyPart.Head,
				packet.WoundOrdinal, BloodStreamTriggerKind.PostMortemStumpFlow, duration, true, 0x48455849u);
		}
		catch (Exception exception)
		{
			// Effect allocation must not reject geometry that has already committed.
			RupturePlugin.Log.LogWarning($"Post-mortem head trickle could not start: ordinal={packet.WoundOrdinal}, {exception.Message}");
		}
	}

	private static void StartLocalProceduralStream(CorpseInteractionTarget target, Transform anchor,
		Vector3 pointWorld, Vector3 directionWorld, EBodyPart bodyPart, uint eventSequence,
		BloodStreamTriggerKind kind, float duration, bool postMortem, uint salt)
	{
		uint streamId = Hash(target.ProfileId, target.DeathSequence, eventSequence, (uint)bodyPart ^ salt);
		BloodStreamData descriptor = new BloodStreamData
		{
			Operation = BloodStreamOperation.Start,
			TriggerKind = kind,
			PlayerId = target.PlayerId,
			ProfileId = target.ProfileId,
			DeathSequence = target.DeathSequence,
			EventSequence = eventSequence,
			StreamId = streamId,
			BodyPart = bodyPart,
			ColliderType = default,
			AnchorBone = anchor.name,
			LocalPoint = anchor.InverseTransformPoint(pointWorld),
			LocalDirection = anchor.InverseTransformDirection(directionWorld),
			Duration = duration,
			PeakSpeed = postMortem ? 1.35f : 3.4f,
			Radius = postMortem ? 0.0105f : 0.0115f,
			PulsePeriod = postMortem ? 0.15f : PulsePeriod(streamId),
			Seed = streamId ^ 0x6C8E9CF5u
		};
		StartOrReplace(descriptor, target.Player, anchor);
	}

	internal static void ResolveHeadExit(HeadWoundData packet, Transform headBone, Vector3 exitLocal,
		Vector3 directionLocal, bool terminalSurfaceResolved, Vector3 entryLocal)
	{
		if (headBone == null) return;
		// A recovered crater has no exit. Resolve the delayed stream at its actual
		// entrance, pointing outward, rather than at the original missed hit point.
		Vector3 point = terminalSurfaceResolved ? exitLocal : entryLocal;
		Vector3 direction = terminalSurfaceResolved ? directionLocal : -directionLocal;
		for (int index = 0; index < Emitters.Length; index++)
		{
			StreamEmitter emitter = Emitters[index];
			if (!emitter.Active || emitter.Descriptor.TriggerKind != BloodStreamTriggerKind.HeadKillPending ||
				emitter.Descriptor.DeathSequence != packet.DeathSequence ||
				!ProfileMatches(emitter.Descriptor.ProfileId, packet.ProfileId)) continue;
			emitter.Anchor = headBone;
			emitter.Descriptor.LocalPoint = point;
			emitter.Descriptor.LocalDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
			emitter.StartsAt = Time.realtimeSinceStartup;
			emitter.StartedAt = emitter.StartsAt;
			emitter.ExpiresAt = emitter.StartsAt + emitter.Descriptor.Duration;
			emitter.NextPulseAt = emitter.StartsAt;
			ClearStreamSamples(index);
			ResetHeartbeatState(emitter, emitter.StartsAt);
			ResetBaseFlow(emitter);
			return;
		}
		for (int index = 0; index < PendingDescriptors.Length; index++)
		{
			PendingDescriptor pending = PendingDescriptors[index];
			if (!pending.Active || pending.Packet.TriggerKind != BloodStreamTriggerKind.HeadKillPending ||
				pending.Packet.DeathSequence != packet.DeathSequence ||
				!ProfileMatches(pending.Packet.ProfileId, packet.ProfileId)) continue;
			BloodStreamData descriptor = pending.Packet;
			descriptor.TriggerKind = BloodStreamTriggerKind.KillEntry;
			descriptor.LocalPoint = point;
			descriptor.LocalDirection = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
			Player player = ResolvePlayer(descriptor.ProfileId);
			if (player != null)
			{
				StartOrReplace(descriptor, player, headBone);
				PendingDescriptors[index] = default;
			}
			else
			{
				pending.Packet = descriptor;
				PendingDescriptors[index] = pending;
			}
			return;
		}
	}

	internal static void RejectHeadExit(string profileId, uint deathSequence)
	{
		for (int index = 0; index < Emitters.Length; index++)
		{
			StreamEmitter emitter = Emitters[index];
			if (!emitter.Active || emitter.Descriptor.TriggerKind != BloodStreamTriggerKind.HeadKillPending ||
				emitter.Descriptor.DeathSequence != deathSequence || !ProfileMatches(emitter.Descriptor.ProfileId, profileId)) continue;
			emitter.StartsAt = Time.realtimeSinceStartup;
			emitter.StartedAt = emitter.StartsAt;
			emitter.ExpiresAt = emitter.StartsAt + emitter.Descriptor.Duration;
			emitter.NextPulseAt = emitter.StartsAt;
			ClearStreamSamples(index);
			ResetHeartbeatState(emitter, emitter.StartsAt);
			ResetBaseFlow(emitter);
			return;
		}
		for (int index = 0; index < PendingDescriptors.Length; index++)
		{
			PendingDescriptor pending = PendingDescriptors[index];
			if (!pending.Active || pending.Packet.TriggerKind != BloodStreamTriggerKind.HeadKillPending ||
				pending.Packet.DeathSequence != deathSequence || !ProfileMatches(pending.Packet.ProfileId, profileId)) continue;
			pending.Packet.TriggerKind = BloodStreamTriggerKind.KillEntry;
			PendingDescriptors[index] = pending;
			return;
		}
	}

	internal static void StopLivingStreams(Player player, bool publish)
	{
		if (player == null) return;
		LivingStreams.StopWhere(stream => ProfileMatches(stream.Descriptor.ProfileId, player.ProfileId),
			stream => StopLivingStream(stream, publish));
		for (int index = Emitters.Length - 1; index >= 0; index--)
		{
			StreamEmitter emitter = Emitters[index];
			if (!emitter.Active || !BleedingWoundTracker.IsLiving(emitter.Descriptor.TriggerKind) ||
				!ProfileMatches(emitter.Descriptor.ProfileId, player.ProfileId)) continue;
			StopById(emitter.Descriptor.StreamId);
		}
	}

	private static void StopLivingStream(LivingStream stream, bool publish)
	{
		UnindexLivingStream(stream);
		LivingStreams.Remove(stream.Descriptor.StreamId);
		StopById(stream.Descriptor.StreamId);
		if (!publish) return;
		BloodStreamData stop = stream.Descriptor;
		stop.Operation = BloodStreamOperation.Stop;
		Publish(stop, clientRequestAllowed: true);
	}

	private static void TrackLivingStream(BloodStreamData packet, Player player)
	{
		if (BleedingWoundTracker.IsLiving(packet.TriggerKind))
		{
			if (LivingStreams.TryGet(packet.StreamId, out var previous)) UnindexLivingStream(previous);
			LivingStreams.Start(packet.StreamId, new LivingStream
			{
				Descriptor = packet, Player = player, ResolveExpiresAt = Time.realtimeSinceStartup + 4f
			});
			var key = (packet.ProfileId, packet.BodyPart, packet.TriggerKind);
			if (!LivingWounds.TryGetValue(key, out var ids)) LivingWounds.Add(key, ids = new HashSet<uint>());
			ids.Add(packet.StreamId);
		}
	}

	private static void UpdateLivingStreams(bool enabled)
	{
		_lifecycleEnabled = enabled;
		LivingStreams.StopWhere(ShouldStopLivingStream, StopAndPublishLivingStream);
	}
	private static void StopAndPublishLivingStream(LivingStream stream) => StopLivingStream(stream, true);
	private static bool ShouldStopLivingStream(LivingStream stream)
	{
			if (_lifecycleEnabled && ReferenceEquals(stream.Player, null))
			{
				stream.Player = ResolvePlayer(stream.Descriptor.ProfileId);
				if (stream.Player == null && Time.realtimeSinceStartup < stream.ResolveExpiresAt) return false;
			}
			Player player = stream.Player;
			if (_lifecycleEnabled && RagdollCoordinator.IsRemoteClient && player != null && !player.IsYourPlayer) return false;
			return !_lifecycleEnabled || player == null || player.HealthController == null || !player.HealthController.IsAlive ||
				BleedingWoundTracker.TrackedActiveEffect(player, stream.Descriptor.BodyPart, stream.Descriptor.TriggerKind) == null;
	}

	internal static void Receive(BloodStreamData packet)
	{
		if (!Validate(packet, out string failure))
		{
			RupturePlugin.Log.LogWarning($"Rejected blood-stream descriptor {packet.StreamId}: {failure}.");
			return;
		}
		if (packet.EventSequence == 0 || !AcceptSequence(packet)) return;
		if (packet.Operation == BloodStreamOperation.Stop)
		{
			ClothingSoakingSystem.Stop(packet.StreamId);
			if (LivingStreams.TryGet(packet.StreamId, out var stopped)) UnindexLivingStream(stopped);
			LivingStreams.Remove(packet.StreamId);
			RemovePending(packet.StreamId);
			StopById(packet.StreamId);
			return;
		}
		Player player = ResolvePlayer(packet.ProfileId);
		TrackLivingStream(packet, player);
		if (MultiplayerRuntime.Current.IsHeadless || !CanCreateStreams()) return;
		if (FindActive(packet.StreamId) >= 0 || IsRetired(packet.StreamId)) return;
		Transform anchor = player != null ? ResolvePacketAnchor(player, packet.ColliderType, packet.BodyPart, packet.AnchorBone) : null;
		if (player == null || anchor == null)
		{
			QueuePending(packet);
			return;
		}
		StartOrReplace(packet, player, anchor);
	}

	internal static void ReceiveRequest(BloodStreamRequestData request, Player requestingPlayer)
	{
		if (!RagdollCoordinator.IsNetworkAuthority || requestingPlayer == null) return;
		BloodStreamData packet = request.Descriptor;
		if (!ProfileMatches(requestingPlayer.ProfileId, packet.ProfileId) ||
			!BleedingWoundTracker.IsLiving(packet.TriggerKind) || !Validate(packet, out _)) return;
		Player player = ResolvePlayer(packet.ProfileId);
		if (player == null || player.HealthController == null) return;
		if (packet.Operation == BloodStreamOperation.Start &&
			(BleedingWoundTracker.ActiveEffect(player, packet.BodyPart, packet.TriggerKind) == null ||
			 !string.IsNullOrEmpty(packet.AnchorBone))) return;
		if (packet.Operation == BloodStreamOperation.Start && HasLivingWound(player, packet.BodyPart, packet.TriggerKind)) return;
		if (packet.Operation == BloodStreamOperation.Stop)
		{
			bool matches = false;
			foreach (LivingStream stream in LivingStreams.Values)
				if (stream.Descriptor.StreamId == packet.StreamId && ProfileMatches(stream.Descriptor.ProfileId, packet.ProfileId)) { matches = true; break; }
			if (!matches) return;
		}
		packet.EventSequence = RagdollCoordinator.NextNetworkEventSequence();
		packet.PlayerId = requestingPlayer.Id;
		if (packet.Operation == BloodStreamOperation.Start)
		{
			packet.DeathSequence = 0;
			packet.StreamId = Hash(packet.ProfileId, 0, (uint)packet.BodyPart, packet.EventSequence ^ 0xB10D5EEDu);
			packet.LocalDirection = packet.LocalDirection.normalized;
			packet.Duration = 0f;
			packet.PeakSpeed = packet.TriggerKind == BloodStreamTriggerKind.LightWound ? 0.06f : 1.5f;
			packet.Radius = packet.TriggerKind == BloodStreamTriggerKind.LightWound ? 0.0022f : 0.0052f;
			packet.PulsePeriod = PulsePeriod(packet.StreamId);
			packet.Seed = packet.StreamId ^ 0x9E3779B9u;
		}
		BroadcastAndReceive(packet);
	}

	internal static void Update()
	{
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodTracking)) BleedingWoundTracker.Update();
		bool enabled = CanCreateStreams();
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodLivingStreams)) UpdateLivingStreams(enabled);
		if (!enabled)
		{
			if (_wasEnabled) ClearGraphics();
			_wasEnabled = false;
			return;
		}
		_wasEnabled = true;
		if (MultiplayerRuntime.Current.IsHeadless) return;

		float now = Time.realtimeSinceStartup;
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodPreparation))
		{
			ProcessPending(now);
			RestoreLivingStumps();
			RestoreLivingVisuals(now);
			SelectNearestVisibleEmitters();
			ClothingSoakingSystem.BeginFrame();
		}
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodEmitters))
		{
			for (int index = Emitters.Length - 1; index >= 0; index--)
			{
				StreamEmitter emitter = Emitters[index];
				if (!emitter.Active) continue;
				if (emitter.Anchor == null || (emitter.Player == null &&
					emitter.Descriptor.TriggerKind != BloodStreamTriggerKind.PostMortemStumpFlow))
				{
					Retire(index);
					continue;
				}
				ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodEmitterVisits, 1);
				using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodHistory)) PruneStreamSamples(index, now);
				if (emitter.ExpiresAt > 0f && now >= emitter.ExpiresAt)
				{
					if (!emitter.Draining) BeginDrain(emitter);
					if (now >= emitter.DrainEndsAt && emitter.SampleCount == 0)
					{
						Retire(index);
						continue;
					}
				}
				if (now < emitter.StartsAt) continue;
				BloodStreamTriggerKind kind = emitter.Descriptor.TriggerKind;
				if (kind == BloodStreamTriggerKind.LivingSeverStump &&
					(emitter.Player?.HealthController?.IsAlive != true || !Health.BotSeverStateService.Untreated(emitter.Player, emitter.Descriptor.BodyPart)))
				{ StopById(emitter.Descriptor.StreamId); continue; }
				if ((BleedingWoundTracker.IsLiving(kind) || kind == BloodStreamTriggerKind.LivingSeverStump) && !emitter.Draining && emitter.Visible)
					using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodClothingSupply))
						ClothingSoakingSystem.Supply(emitter.Descriptor, emitter.Player, emitter.Anchor, now);
				if (kind == BloodStreamTriggerKind.LightWound)
				{
					if (!emitter.Draining && emitter.Visible && now >= emitter.NextDripAt) LaunchLightDrip(index, emitter, now);
					continue;
				}
				if (emitter.Draining && BleedingWoundTracker.IsLiving(kind)) continue;
				if (!emitter.Draining && HasHeartbeatPulse(kind) && now >= emitter.NextPulseAt)
				{
					float strength = StreamStrength(emitter, now);
					if (strength > 0.08f) TriggerHeartbeat(emitter, now);
					emitter.NextPulseAt = now + emitter.Descriptor.PulsePeriod * Mathf.Lerp(1f, 1.45f, 1f - strength);
				}
				if (HasPersistentBase(kind) && emitter.Visible)
				{
					using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodFlow)) UpdateStreamFlow(index, emitter, now);
				}
				else
				{
					emitter.BaseFlow.Active = false;
					ClearStreamSamples(index);
				}
			}

		}
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ClothingSoak)) ClothingSoakingSystem.Update(now);
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodDecals))
		using (BloodDecalBatch.Begin()) UpdateDecals(now);
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodMesh)) StreamRendering.RebuildMesh(now);
	}

	internal static void Reset()
	{
		BleedingWoundTracker.Reset();
		ClothingSoakingSystem.Reset();
		LivingStreams.Clear();
		LivingWounds.Clear();
		ClearGraphics();
	}

	private static void ClearGraphics()
	{
		StreamRendering.ReleaseParcels();
		for (int index = 0; index < Emitters.Length; index++) ClearEmitter(Emitters[index]);
		Array.Clear(StreamSamples, 0, StreamSamples.Length);
		Array.Clear(RetiredStreamIds, 0, RetiredStreamIds.Length);
		Array.Clear(DecalCells, 0, DecalCells.Length);
		Array.Clear(DripImpacts, 0, DripImpacts.Length);
		Array.Clear(PendingDescriptors, 0, PendingDescriptors.Length);
		Array.Clear(SequenceRecords, 0, SequenceRecords.Length);
		_retiredWriteIndex = 0;
		_decalCellWriteIndex = 0;
		_decalFrame = -1;
		_decalsThisFrame = 0;
		_decalSecondStarted = 0f;
		_decalsThisSecond = 0;
		_warnedMissingEffects = false;
		StreamRendering.DestroyMesh();
		BloodStreamMaterialProvider.Reset();
	}

	private static void RestoreLivingVisuals(float now)
	{
		if (now < _nextRestoreAt) return;
		_nextRestoreAt = now + 0.5f;
		int restored = 0;
		foreach (LivingStream stream in LivingStreams.Values)
		{
			if (stream.Player == null || FindActive(stream.Descriptor.StreamId) >= 0) continue;
			// Recover evicted visuals only into free slots: never churn a full pool.
			bool free = false; foreach (StreamEmitter emitter in Emitters) if (!emitter.Active) { free = true; break; }
			if (!free || restored >= 4) break;
			Transform anchor = ResolvePacketAnchor(stream.Player, stream.Descriptor.ColliderType, stream.Descriptor.BodyPart, stream.Descriptor.AnchorBone);
			if (anchor == null) continue;
			StartOrReplace(stream.Descriptor, stream.Player, anchor); restored++;
		}
	}

	private static void Publish(BloodStreamData descriptor, bool clientRequestAllowed)
	{
		if (RagdollCoordinator.IsRemoteClient)
		{
			if (!clientRequestAllowed || !MultiplayerRuntime.Current.HasClient) return;
			BloodStreamRequestData request = new BloodStreamRequestData { Descriptor = descriptor };
			MultiplayerRuntime.Current.RequestBloodStream(request);
			return;
		}
		if (descriptor.EventSequence == 0 || descriptor.Operation == BloodStreamOperation.Stop)
			descriptor.EventSequence = RagdollCoordinator.NextNetworkEventSequence();
		BroadcastAndReceive(descriptor);
	}

	private static void BroadcastAndReceive(BloodStreamData descriptor)
	{
		if (RagdollCoordinator.IsNetworkAuthority && MultiplayerRuntime.Current.HasServer)
		{
			MultiplayerRuntime.Current.Send(ref descriptor);
		}
		Receive(descriptor);
	}

	private static BloodStreamData CreateDescriptor(Player player, BloodStreamTriggerKind kind,
		EBodyPart bodyPart, EBodyPartColliderType colliderType, Vector3 localPoint, Vector3 localDirection,
		float duration, float peakSpeed, float radius, uint deathSequence, uint seedSalt)
	{
		uint streamId = Hash(player.ProfileId, deathSequence, (uint)bodyPart, seedSalt ^ (uint)kind);
		return new BloodStreamData
		{
			Operation = BloodStreamOperation.Start,
			TriggerKind = kind,
			PlayerId = player.Id,
			ProfileId = player.ProfileId,
			DeathSequence = deathSequence,
			StreamId = streamId,
			BodyPart = bodyPart,
			ColliderType = colliderType,
			LocalPoint = localPoint,
			LocalDirection = localDirection.sqrMagnitude > 0.0001f ? localDirection.normalized : Vector3.forward,
			Duration = duration,
			PeakSpeed = peakSpeed,
			Radius = radius,
			PulsePeriod = PulsePeriod(streamId),
			Seed = streamId ^ seedSalt ^ 0x9E3779B9u
		};
	}

	private static void StartOrReplace(BloodStreamData descriptor, Player player, Transform anchor, bool localFracture = false)
	{
		int existing = FindActive(descriptor.StreamId);
		if (existing >= 0)
		{
			if (!localFracture && descriptor.EventSequence <= Emitters[existing].Descriptor.EventSequence) return;
			Retire(existing);
		}
		if (!localFracture && BleedingWoundTracker.IsLiving(descriptor.TriggerKind))
		{
			for (int index = 0; index < Emitters.Length; index++)
			{
				StreamEmitter other = Emitters[index];
				if (other.Active && !other.LocalFracture && !other.Draining && BleedingWoundTracker.IsLiving(other.Descriptor.TriggerKind) &&
					other.Descriptor.BodyPart == descriptor.BodyPart && ProfileMatches(other.Descriptor.ProfileId, descriptor.ProfileId))
				{
					StopById(other.Descriptor.StreamId);
					break;
				}
			}
		}
		int slot = FindEmitterSlot(descriptor.TriggerKind, player);
		if (slot < 0) return;
		float now = Time.realtimeSinceStartup;
		StreamEmitter emitter = Emitters[slot];
		emitter.Active = true;
		emitter.LocalFracture = localFracture;
		emitter.Visible = false;
		emitter.Descriptor = descriptor;
		emitter.Player = player;
		emitter.Anchor = anchor;
		emitter.StartedAt = now;
		emitter.StartsAt = descriptor.TriggerKind == BloodStreamTriggerKind.HeadKillPending ? now + HeadResolutionWaitSeconds : now;
		emitter.ExpiresAt = descriptor.Duration > 0f ? emitter.StartsAt + descriptor.Duration : 0f;
		emitter.NextPulseAt = emitter.StartsAt;
		uint dripSeed = descriptor.Seed;
		emitter.NextDripAt = emitter.StartsAt + 1f + Next01(ref dripSeed) * 2f;
		ResetHeartbeatState(emitter, emitter.StartsAt);
		emitter.Draining = false;
		emitter.DrainStartedAt = 0f;
		emitter.DrainEndsAt = 0f;
		emitter.PulseIndex = 0;
		ClearStreamSamples(slot);
		ResetBaseFlow(emitter);
		RupturePlugin.Log.LogInfo($"Pulsating blood stream started: kind={descriptor.TriggerKind}, bodyPart={descriptor.BodyPart}, " +
			$"speed={descriptor.PeakSpeed:F2}m/s, diameter={descriptor.Radius * 2000f:F1}mm, " +
			$"duration={descriptor.Duration:F2}s, directionLocal={descriptor.LocalDirection}, source={(localFracture ? "fracture" : "event")}.");
	}

	private static bool HasPersistentBase(BloodStreamTriggerKind kind)
	{
		return kind == BloodStreamTriggerKind.HeavyWound ||
			kind == BloodStreamTriggerKind.KillEntry ||
			kind == BloodStreamTriggerKind.HeadKillPending ||
			kind == BloodStreamTriggerKind.DismembermentStump ||
			kind == BloodStreamTriggerKind.LivingSeverStump ||
			kind == BloodStreamTriggerKind.PostMortemStumpFlow;
	}

	private static bool HasHeartbeatPulse(BloodStreamTriggerKind kind)
	{
		return kind != BloodStreamTriggerKind.PostMortemStumpFlow && kind != BloodStreamTriggerKind.LightWound;
	}

	private static bool UsesWoundOffshootHandoff(BloodStreamTriggerKind kind)
	{
		return kind == BloodStreamTriggerKind.HeavyWound ||
			kind == BloodStreamTriggerKind.KillEntry ||
			kind == BloodStreamTriggerKind.HeadKillPending ||
			kind == BloodStreamTriggerKind.DismembermentStump;
	}

	private static void BeginDrain(StreamEmitter emitter)
	{
		emitter.Draining = true;
		emitter.DrainStartedAt = emitter.ExpiresAt;
		emitter.DrainEndsAt = emitter.ExpiresAt + DrainDuration(emitter.Descriptor.TriggerKind);
	}

	private static float DrainDuration(BloodStreamTriggerKind kind)
	{
		return kind == BloodStreamTriggerKind.HeavyWound ? 0.55f :
			kind == BloodStreamTriggerKind.KillEntry || kind == BloodStreamTriggerKind.HeadKillPending ? 0.65f :
			kind == BloodStreamTriggerKind.PostMortemStumpFlow ? 0.65f : 0.75f;
	}

	private static void TriggerHeartbeat(StreamEmitter emitter, float now)
	{
		if (UsesWoundOffshootHandoff(emitter.Descriptor.TriggerKind))
		{
			emitter.NextSampleAt = now;
		}
		uint seed = emitter.Descriptor.Seed ^ (++emitter.PulseIndex * 0x85EBCA6Bu);
		float transition = WobbleTransition(emitter, now);
		emitter.OriginWobbleFrom = Vector2.Lerp(emitter.OriginWobbleFrom, emitter.OriginWobbleTarget, transition);
		emitter.DirectionWobbleFrom = Vector2.Lerp(emitter.DirectionWobbleFrom, emitter.DirectionWobbleTarget, transition);
		emitter.OriginWobbleTarget = RandomInsideUnitCircle(ref seed);
		emitter.DirectionWobbleTarget = RandomInsideUnitCircle(ref seed);
		emitter.WobbleTransitionStartedAt = now;
		emitter.WoundOffshootStartedAt = now;
		emitter.HeartbeatImpactScheduled = false;
	}

	private static float WoundOffshootPressure(StreamEmitter emitter, float now)
	{
		if (emitter.PulseIndex == 0 || now < emitter.WoundOffshootStartedAt) return 0f;
		float age = now - emitter.WoundOffshootStartedAt;
		return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(age / WoundOffshootRecedeSeconds));
	}

	private static float WobbleTransition(StreamEmitter emitter, float now)
	{
		return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((now - emitter.WobbleTransitionStartedAt) / 0.18f));
	}

	private static Vector2 RandomInsideUnitCircle(ref uint seed)
	{
		float angle = Next01(ref seed) * Mathf.PI * 2f;
		float radius = Mathf.Sqrt(Next01(ref seed));
		return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
	}

	private static void ResolveWobbledFrame(StreamEmitter emitter, float now, bool snapToTarget,
		out Vector3 origin, out Vector3 direction)
	{
		ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodAnchorReads, 1);
		Vector3 baseDirection = emitter.Anchor.TransformDirection(emitter.Descriptor.LocalDirection).normalized;
		if (baseDirection.sqrMagnitude < 0.0001f) baseDirection = Vector3.up;
		Vector3 tangent = Vector3.Cross(baseDirection,
			Mathf.Abs(baseDirection.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
		Vector3 bitangent = Vector3.Cross(baseDirection, tangent).normalized;
		float blend = snapToTarget ? 1f : WobbleTransition(emitter, now);
		Vector2 originWobble = Vector2.Lerp(emitter.OriginWobbleFrom, emitter.OriginWobbleTarget, blend);
		Vector2 directionWobble = Vector2.Lerp(emitter.DirectionWobbleFrom, emitter.DirectionWobbleTarget, blend);
		float originLimit = Mathf.Min(0.0045f, emitter.Descriptor.Radius * 0.35f);
		origin = emitter.Anchor.TransformPoint(emitter.Descriptor.LocalPoint) +
			tangent * (originWobble.x * originLimit) + bitangent * (originWobble.y * originLimit);
		direction = (baseDirection + tangent * (directionWobble.x * 0.05f) +
			bitangent * (directionWobble.y * 0.05f)).normalized;
	}

	private static float BaseRadiusScale(BloodStreamTriggerKind kind)
	{
		if (kind == BloodStreamTriggerKind.DismembermentStump) return 0.88f;
		if (kind == BloodStreamTriggerKind.PostMortemStumpFlow) return 0.94f;
		return 0.58f;
	}

	private static float NormalSpeedScale(BloodStreamTriggerKind kind)
	{
		if (kind == BloodStreamTriggerKind.DismembermentStump) return 0.42f;
		if (kind == BloodStreamTriggerKind.PostMortemStumpFlow) return 0.50f;
		return 0.25f;
	}

	private static float HeartbeatSpeedScale(BloodStreamTriggerKind kind)
	{
		if (kind == BloodStreamTriggerKind.DismembermentStump) return 0.75f;
		if (kind == BloodStreamTriggerKind.PostMortemStumpFlow) return NormalSpeedScale(kind);
		return 0.74f;
	}

	private static float StreamMaximumFlight(BloodStreamTriggerKind kind)
	{
		return kind == BloodStreamTriggerKind.PostMortemStumpFlow ? 1.6f : MaximumFlightSeconds;
	}

	private static void UpdateStreamFlow(int emitterIndex, StreamEmitter emitter, float now)
	{
		float strength = StreamStrength(emitter, now);
		if (strength <= 0.002f) { emitter.BaseFlow.Active = false; return; }
		UpdateWoundOffshootFlow(emitterIndex, emitter, now, strength);
	}

	private static void UpdateWoundOffshootFlow(int emitterIndex, StreamEmitter emitter, float now, float strength)
	{
		// Each launch owns its trajectory and expiry. Only a scheduled reference
		// launch queries the world; later wound movement never invalidates it.
		if (now < emitter.NextSampleAt)
		{
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodFlowSkipped, 1);
			return;
		}
		BaseFlowState state = emitter.BaseFlow;
		BloodStreamTriggerKind kind = emitter.Descriptor.TriggerKind;
		float pressure = UsesWoundOffshootHandoff(kind) ? WoundOffshootPressure(emitter, now) : 0f;
		ResolveWobbledFrame(emitter, now, snapToTarget: UsesWoundOffshootHandoff(kind), out Vector3 origin, out Vector3 direction);
		state.Active = true;
		state.ShapeSeed = emitter.Descriptor.Seed ^ emitter.PulseIndex * 0x9E3779B9u;
		state.Origin = origin;
		state.Direction = direction;
		state.Velocity = direction * (emitter.Descriptor.PeakSpeed * strength *
			Mathf.Lerp(NormalSpeedScale(kind), HeartbeatSpeedScale(kind), pressure));
		state.Radius = emitter.Descriptor.Radius * strength * Mathf.Lerp(BaseRadiusScale(kind), 1f, pressure);
		state.Strength = strength;
		state.FlightDuration = StreamMaximumFlight(kind);
		bool reference = !state.TraceValid || now >= state.NextTraceAt;
		if (reference)
		{
			uint seed = state.ShapeSeed ^ ((emitter.VisualIndex + 1) * 0x85EBCA6Bu);
			// Keep the existing stationary postmortem cache. Inspect it only at the
			// reference deadline, so motion never introduces extra physics queries.
			bool cachedPostmortem = kind == BloodStreamTriggerKind.PostMortemStumpFlow && state.TraceValid &&
				(origin - state.ReferenceOrigin).sqrMagnitude < 0.0001f &&
				Vector3.Dot(direction, state.ReferenceVelocity.normalized) >= 0.9985f &&
				Mathf.Abs(state.Velocity.magnitude - state.ReferenceVelocity.magnitude) <= Mathf.Max(0.05f, state.Velocity.magnitude * 0.10f) &&
				Mathf.Abs(state.Radius - state.ReferenceRadius) <= Mathf.Max(0.0002f, state.Radius * 0.10f) &&
				Physics.gravity == state.ReferenceGravity;
			if (!cachedPostmortem)
			{
				ShadowRagdollWorld.RecordMainThreadWork(state.TraceValid ? ShadowRagdollWorld.MainThreadWork.BloodTraceTimer :
					ShadowRagdollWorld.MainThreadWork.BloodTraceInitial, 1);
				PredictImpact(origin, ParcelVelocity(kind, state.Velocity, seed), Physics.gravity, state.FlightDuration,
					out state.HasImpact, out state.ImpactAge, out state.ImpactPoint, out state.ImpactNormal);
				state.ReferenceOrigin = origin; state.ReferenceVelocity = state.Velocity;
				state.ReferenceGravity = Physics.gravity; state.ReferenceRadius = state.Radius;
				if (kind == BloodStreamTriggerKind.PostMortemStumpFlow)
					ShadowRagdollWorld.RecordMainThreadWork(state.HasImpact ? ShadowRagdollWorld.MainThreadWork.BloodPostMortemImpactHits :
						ShadowRagdollWorld.MainThreadWork.BloodPostMortemImpactMisses, 1);
			}
			state.TraceValid = true;
			// Keep the existing timer intervals. Select the first emitted parcel due
			// after the deadline rather than inventing an extra visual launch.
			state.NextTraceAt = now + (pressure > 0.01f ? 0.055f : BaseTraceInterval);
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodReferenceParcels, 1);
			if (state.HasImpact)
			{
				int cell = DecalCellKey(state.ImpactPoint);
				if (!state.HasImpactCell || cell != state.ImpactCellKey) state.NextDecalAt = now;
				state.HasImpactCell = true; state.ImpactCellKey = cell;
			}
			else state.HasImpactCell = false;
			if (state.HasImpact && state.Strength >= 0.12f)
			{
				bool burst = pressure >= 0.55f && !emitter.HeartbeatImpactScheduled;
				if ((burst || now >= state.NextDecalAt) && QueueImpact(now + Mathf.Max(0.02f, state.ImpactAge),
					state.ImpactPoint, state.ImpactNormal, kind, state.Strength, seed,
					StreamPriority(kind, emitter.Player), burst))
				{
					if (burst) emitter.HeartbeatImpactScheduled = true;
					state.NextDecalAt = now + BaseDepositInterval(kind, state.Strength);
				}
			}
		}
		else ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodReusedFlightTimes, 1);
		emitter.BaseFlow = state;
		AppendStreamSample(emitterIndex, emitter, state, now);
		emitter.NextSampleAt = now + StreamSampleInterval;
	}

	private static Vector3 ParcelVelocity(BloodStreamTriggerKind kind, Vector3 velocity, uint sampleSeed)
	{
		if (!UsesWoundOffshootHandoff(kind) || velocity.sqrMagnitude <= 0.0001f) return velocity;
		float speed = velocity.magnitude;
		Vector3 direction = velocity / speed;
		Vector3 tangent = Vector3.Cross(direction, Mathf.Abs(direction.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
		Vector3 bitangent = Vector3.Cross(direction, tangent).normalized;
		uint spreadSeed = sampleSeed ^ 0xC2B2AE35u;
		Vector2 spread = RandomInsideUnitCircle(ref spreadSeed) * WoundParcelDirectionSpread;
		return (direction + tangent * spread.x + bitangent * spread.y).normalized * speed;
	}

	private static float BaseDepositInterval(BloodStreamTriggerKind kind, float strength)
	{
		return kind == BloodStreamTriggerKind.DismembermentStump ? 0.32f * Mathf.Lerp(1.5f, 1f, strength) :
			kind == BloodStreamTriggerKind.PostMortemStumpFlow ? 0.42f * Mathf.Lerp(1.6f, 1f, strength) :
			BaseDecalInterval * Mathf.Lerp(2.2f, 1f, strength);
	}

	private static bool QueueImpact(float arrivesAt, Vector3 point, Vector3 normal, BloodStreamTriggerKind kind,
		float strength, uint seed, int priority, bool burst)
	{
		for (int i = 0; i < DripImpacts.Length; i++)
		{
			if (DripImpacts[i].Active) continue;
			DripImpacts[i] = new DripImpact { Active = true, ArrivesAt = arrivesAt, Point = point, Normal = normal,
				Kind = kind, Strength = strength, Seed = seed, Priority = priority, Burst = burst };
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodImpactsQueued, 1);
			return true;
		}
		ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodImpactQueueFull, 1);
		return false;
	}

	private static void AppendStreamSample(int emitterIndex, StreamEmitter emitter, BaseFlowState state, float now)
	{
		if (!state.Active || state.Radius <= 0.00001f) return;
		int baseIndex = emitterIndex * MaximumStreamSamplesPerEmitter;
		if (emitter.SampleCount >= MaximumStreamSamplesPerEmitter)
		{
			emitter.SampleStart = (emitter.SampleStart + 1) % MaximumStreamSamplesPerEmitter;
			emitter.SampleCount--;
		}
		int localIndex = (emitter.SampleStart + emitter.SampleCount) % MaximumStreamSamplesPerEmitter;
		float flight = state.HasImpact ? Mathf.Min(state.FlightDuration, state.ImpactAge) : state.FlightDuration;
		uint sampleSeed = state.ShapeSeed ^ (++emitter.VisualIndex * 0x85EBCA6Bu);
		Vector3 velocity = ParcelVelocity(emitter.Descriptor.TriggerKind, state.Velocity, sampleSeed);
		StreamSamples[baseIndex + localIndex] = new StreamRingSample
		{
			Seed = sampleSeed,
			Origin = state.Origin,
			Velocity = velocity,
			Gravity = Physics.gravity,
			StartedAt = now,
			EndAt = now + Mathf.Max(0.02f, flight),
			Radius = state.Radius,
			Strength = state.Strength
		};
		emitter.NextSampleExpiryAt = emitter.SampleCount == 0 ? StreamSamples[baseIndex + localIndex].EndAt
			: Mathf.Min(emitter.NextSampleExpiryAt, StreamSamples[baseIndex + localIndex].EndAt);
		emitter.SampleCount++;
		ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodSamplesEmitted, 1);
	}

	private static void PruneStreamSamples(int emitterIndex, float now)
	{
		StreamEmitter emitter = Emitters[emitterIndex];
		if (emitter.SampleCount == 0 || now < emitter.NextSampleExpiryAt) return;
		int baseIndex = emitterIndex * MaximumStreamSamplesPerEmitter;
		int survivors = 0;
		float nextExpiry = float.PositiveInfinity;
		ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodHistoryScans, emitter.SampleCount);
		// Expiry order can differ from emission order. Compact the wrapped ring in
		// logical order; destination slots have already been read, never future slots.
		for (int logicalIndex = 0; logicalIndex < emitter.SampleCount; logicalIndex++)
		{
			int localIndex = (emitter.SampleStart + logicalIndex) % MaximumStreamSamplesPerEmitter;
			StreamRingSample sample = StreamSamples[baseIndex + localIndex];
			if (sample.EndAt <= now) continue;
			if (survivors != logicalIndex)
			{
				StreamSamples[baseIndex + (emitter.SampleStart + survivors) % MaximumStreamSamplesPerEmitter] = sample;
				ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodHistoryCopies, 1);
			}
			survivors++;
			nextExpiry = Mathf.Min(nextExpiry, sample.EndAt);
		}
		emitter.SampleCount = survivors;
		emitter.NextSampleExpiryAt = nextExpiry;
	}

	private static void ClearStreamSamples(int emitterIndex)
	{
		if (emitterIndex < 0 || emitterIndex >= Emitters.Length) return;
		StreamEmitter emitter = Emitters[emitterIndex];
		// Samples contain only values; SampleCount owns visibility. No 96-slot clear.
		emitter.NextSampleExpiryAt = float.PositiveInfinity;
		emitter.SampleStart = 0;
		emitter.SampleCount = 0;
		emitter.NextSampleAt = emitter.StartsAt;
	}

	private static StreamRingSample GetStreamSample(int emitterIndex, StreamEmitter emitter, int logicalIndex)
	{
		int localIndex = (emitter.SampleStart + logicalIndex) % MaximumStreamSamplesPerEmitter;
		return StreamSamples[emitterIndex * MaximumStreamSamplesPerEmitter + localIndex];
	}

	private static int DecalCellKey(Vector3 point)
	{
		int x = Mathf.FloorToInt(point.x / SpatialDecalCellSize);
		int y = Mathf.FloorToInt(point.y / SpatialDecalCellSize);
		int z = Mathf.FloorToInt(point.z / SpatialDecalCellSize);
		unchecked { return ((x * 397) ^ y) * 397 ^ z; }
	}

	private static void PredictImpact(Vector3 origin, Vector3 velocity, Vector3 gravity, float flightDuration,
		out bool hasImpact, out float impactAge, out Vector3 impactPoint, out Vector3 impactNormal)
	{
		using var impactTrace = ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodImpactPrediction);
		ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions, 1);
		hasImpact = false;
		impactAge = 0f;
		impactPoint = Vector3.zero;
		impactNormal = Vector3.up;
		Vector3 previous = origin;
		for (int segment = 1; segment <= TrajectorySegments; segment++)
		{
			float age = flightDuration * segment / TrajectorySegments;
			Vector3 next = PositionAt(origin, velocity, gravity, age);
			Vector3 delta = next - previous;
			float distance = delta.magnitude;
			if (distance > 0.0001f)
			{
				ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodRaycasts, 1);
				if (Physics.Raycast(previous, delta / distance, out RaycastHit hit, distance,
					EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK, QueryTriggerInteraction.Ignore))
				{
					float segmentStartAge = flightDuration * (segment - 1) / TrajectorySegments;
					hasImpact = true;
					impactAge = Mathf.Lerp(segmentStartAge, age, hit.distance / distance);
					impactPoint = hit.point;
					impactNormal = hit.normal.sqrMagnitude > 0.0001f
						? hit.normal.normalized : -delta.normalized;
					return;
				}
			}
			previous = next;
		}
	}

	private static void SelectNearestVisibleEmitters()
	{
		for (int index = 0; index < Emitters.Length; index++) Emitters[index].Visible = false;
		for (int index = 0; index < VisibleEmitterIndices.Length; index++)
		{
			VisibleEmitterIndices[index] = -1;
			VisibleEmitterDistances[index] = float.MaxValue;
		}
		Vector3 observer = Vector3.zero;
		if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null)
			observer = Singleton<GameWorld>.Instance.MainPlayer.Position;
		int selected = 0;
		for (int emitterIndex = 0; emitterIndex < Emitters.Length; emitterIndex++)
		{
			StreamEmitter emitter = Emitters[emitterIndex];
			if (!emitter.Active || emitter.Anchor == null) continue;
			float distance = (emitter.Anchor.position - observer).sqrMagnitude;
			if (emitter.Player != null && emitter.Player.IsYourPlayer) distance -= 2000000f;
			else if (BleedingWoundTracker.IsLiving(emitter.Descriptor.TriggerKind) &&
				emitter.Player?.HealthController?.IsAlive == true) distance -= 1000000f;
			int insert = Mathf.Min(selected, MaximumRenderedEmitters - 1);
			while (insert > 0 && distance < VisibleEmitterDistances[insert - 1]) insert--;
			if (insert >= MaximumRenderedEmitters) continue;
			int last = Mathf.Min(selected, MaximumRenderedEmitters - 1);
			for (int move = last; move > insert; move--)
			{
				VisibleEmitterIndices[move] = VisibleEmitterIndices[move - 1];
				VisibleEmitterDistances[move] = VisibleEmitterDistances[move - 1];
			}
			VisibleEmitterIndices[insert] = emitterIndex;
			VisibleEmitterDistances[insert] = distance;
			if (selected < MaximumRenderedEmitters) selected++;
		}
		for (int index = 0; index < selected; index++)
		{
			if (VisibleEmitterIndices[index] >= 0) Emitters[VisibleEmitterIndices[index]].Visible = true;
		}
	}

	private static void UpdateDecals(float now)
	{
		if (_decalFrame != Time.frameCount) { _decalFrame = Time.frameCount; _decalsThisFrame = 0; }
		if (now - _decalSecondStarted >= 1f) { _decalSecondStarted = now; _decalsThisSecond = 0; }
		// Immutable launch records also survive a wound stopping while blood is
		// airborne. They never read a newer reference hit or a live wound anchor.
		for (int priority = 2; priority >= 0; priority--)
		for (int i = 0; i < DripImpacts.Length; i++)
		{
			DripImpact impact = DripImpacts[i];
			if (!impact.Active || impact.Priority != priority || now < impact.ArrivesAt) continue;
			if (now > impact.ArrivesAt + 0.5f)
			{
				DripImpacts[i] = default;
				ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodImpactsExpired, 1);
				continue;
			}
			if (!Singleton<Effects>.Instantiated || Singleton<Effects>.Instance == null)
			{
				if (!_warnedMissingEffects)
				{
					_warnedMissingEffects = true;
					RupturePlugin.Log.LogWarning("EFT deferred blood effects are unavailable; queued blood impacts cannot paint.");
				}
				continue;
			}
			int frameLimit = DecalFrameLimit(impact.Kind), secondLimit = DecalSecondLimit(impact.Kind);
			if (_decalsThisFrame >= frameLimit || _decalsThisSecond >= secondLimit || !ReserveDecalCell(impact.Point, now, impact.Kind))
			{
				if (impact.Kind == BloodStreamTriggerKind.PostMortemStumpFlow)
					ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodPostMortemDecalBlocked, 1);
				continue;
			}
			int requested = !impact.Burst ? 1 : impact.Kind == BloodStreamTriggerKind.DismembermentStump ? 6 :
				impact.Strength > 0.68f ? 3 : 2;
			int available = Mathf.Min(requested, Mathf.Min(frameLimit - _decalsThisFrame, secondLimit - _decalsThisSecond));
			Vector3 tangent = Vector3.Cross(impact.Normal, Mathf.Abs(impact.Normal.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
			Vector3 bitangent = Vector3.Cross(impact.Normal, tangent);
			uint seed = impact.Seed;
			float poolRadius = impact.Kind == BloodStreamTriggerKind.DismembermentStump ? 0.12f : 0.065f;
			for (int deposit = 0; deposit < available; deposit++)
			{
				float radius = deposit == 0 ? 0f : Mathf.Lerp(0.018f, poolRadius, Next01(ref seed));
				float angle = Next01(ref seed) * Mathf.PI * 2f;
				Vector3 point = impact.Point + tangent * (Mathf.Cos(angle) * radius) + bitangent * (Mathf.Sin(angle) * radius);
				using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodDecalEmit))
				{
					if (impact.Burst) Singleton<Effects>.Instance.EmitBloodOnEnvironment(point, impact.Normal);
					else Singleton<Effects>.Instance.EmitBleeding(point, impact.Normal);
				}
				ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodDecalEmissions, 1);
				if (impact.Kind == BloodStreamTriggerKind.PostMortemStumpFlow)
					ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodPostMortemDecals, 1);
				_decalsThisFrame++; _decalsThisSecond++;
			}
			DripImpacts[i] = default;
		}
	}

	internal static Transform ResolveDamageAnchor(Player player, Collider hitCollider,
		EBodyPartColliderType colliderType, EBodyPart bodyPart, out EBodyPartColliderType resolvedCollider)
	{
		BodyPartCollider direct = Rupture.Combined.Patches.KillPatch.ResolveBodyPartCollider(player, hitCollider);
		if (direct != null)
		{
			resolvedCollider = direct.BodyPartColliderType;
			return direct.Collider != null ? direct.Collider.transform : direct.transform;
		}
		resolvedCollider = colliderType;
		return ResolvePacketAnchor(player, colliderType, bodyPart);
	}

	private static Transform ResolvePacketAnchor(Player player, EBodyPartColliderType colliderType, EBodyPart bodyPart)
	{
		return ResolvePacketAnchor(player, colliderType, bodyPart, null);
	}

	private static Transform ResolvePacketAnchor(Player player, EBodyPartColliderType colliderType, EBodyPart bodyPart,
		string anchorBone)
	{
		if (!string.IsNullOrEmpty(anchorBone))
		{
			Transform bone = FindDescendant(player?.PlayerBody?.transform ?? player?.Transform?.Original, anchorBone);
			if (bone != null) return bone;
		}
		Dictionary<EBodyPartColliderType, BodyPartCollider> dictionary = player?.PlayerBones?.BodyPartCollidersDictionary;
		if (dictionary != null && dictionary.TryGetValue(colliderType, out BodyPartCollider exact) && exact != null)
			return exact.Collider != null ? exact.Collider.transform : exact.transform;
		BodyPartCollider[] colliders = player?.PlayerBones?.BodyPartColliders;
		if (colliders != null)
		{
			for (int index = 0; index < colliders.Length; index++)
			{
				BodyPartCollider candidate = colliders[index];
				if (candidate != null && candidate.BodyPartType == bodyPart)
					return candidate.Collider != null ? candidate.Collider.transform : candidate.transform;
			}
		}
		return player?.Transform?.Original;
	}

	private static Transform FindDescendant(Transform root, string name)
	{
		if (root == null) return null;
		if (string.Equals(root.name, name, StringComparison.Ordinal)) return root;
		for (int index = 0; index < root.childCount; index++)
		{
			Transform result = FindDescendant(root.GetChild(index), name);
			if (result != null) return result;
		}
		return null;
	}

	private static Player ResolvePlayer(string profileId)
	{
		if (!Singleton<GameWorld>.Instantiated || string.IsNullOrEmpty(profileId)) return null;
		GameWorld world = Singleton<GameWorld>.Instance;
		return world.GetAlivePlayerByProfileID(profileId) ?? world.GetEverExistedPlayerByID(profileId);
	}

	internal static Vector3 ResolveEntryDirection(EFT.Ballistics.DamageInfo damageInfo)
	{
		Vector3 normal = damageInfo.HitNormal.sqrMagnitude > 0.0001f ? damageInfo.HitNormal.normalized : Vector3.zero;
		Vector3 reverseProjectile = damageInfo.Direction.sqrMagnitude > 0.0001f ? -damageInfo.Direction.normalized : Vector3.zero;
		Vector3 result = normal * 0.62f + reverseProjectile * 0.38f;
		return result.sqrMagnitude > 0.0001f ? result.normalized : Vector3.up;
	}

	private static bool Validate(BloodStreamData packet, out string failure)
	{
		failure = null;
		if (packet.Operation != BloodStreamOperation.Start && packet.Operation != BloodStreamOperation.Stop)
			failure = "operation is invalid";
		else if (packet.TriggerKind < BloodStreamTriggerKind.HeavyWound || packet.TriggerKind > BloodStreamTriggerKind.LivingSeverStump)
			failure = "trigger kind is invalid";
		else if (packet.StreamId == 0 || string.IsNullOrEmpty(packet.ProfileId)) failure = "identity is missing";
		else if (packet.AnchorBone != null && packet.AnchorBone.Length > 128) failure = "bone anchor is malformed";
		else if (!IsFinite(packet.LocalPoint) || !IsFinite(packet.LocalDirection) || packet.LocalPoint.sqrMagnitude > 16f ||
			packet.LocalDirection.sqrMagnitude < 0.25f || packet.LocalDirection.sqrMagnitude > 2.25f) failure = "anchor data is malformed";
		else if (!IsFinite(packet.Duration) || !IsFinite(packet.PeakSpeed) || !IsFinite(packet.Radius) || !IsFinite(packet.PulsePeriod) ||
			packet.Duration < 0f || packet.Duration > 30f || packet.PeakSpeed < (packet.TriggerKind == BloodStreamTriggerKind.LightWound ? 0.01f : 0.5f) || packet.PeakSpeed > 14f ||
			packet.Radius < 0.0005f || packet.Radius > 0.012f ||
			packet.PulsePeriod < (packet.TriggerKind == BloodStreamTriggerKind.PostMortemStumpFlow ? 0.12f : 0.55f) ||
			packet.PulsePeriod > 1.1f)
			failure = "intensity profile is outside bounds";
		return failure == null;
	}

	private static float StreamStrength(StreamEmitter emitter, float now)
	{
		if (emitter.Descriptor.Duration <= 0f) return 1f;
		if (emitter.Draining)
		{
			float drainLength = Mathf.Max(0.01f, emitter.DrainEndsAt - emitter.DrainStartedAt);
			float drain = Mathf.Clamp01((now - emitter.DrainStartedAt) / drainLength);
			return FiniteEndStrength * (1f - Mathf.SmoothStep(0f, 1f, drain));
		}
		float normalized = Mathf.Clamp01((now - emitter.StartsAt) / emitter.Descriptor.Duration);
		if (normalized <= FiniteFadeStart) return 1f;
		float fade = Mathf.InverseLerp(FiniteFadeStart, 1f, normalized);
		return Mathf.Lerp(1f, FiniteEndStrength, Mathf.SmoothStep(0f, 1f, fade));
	}

	private static bool IsDismembermentFlow(BloodStreamTriggerKind kind) =>
		kind == BloodStreamTriggerKind.DismembermentStump || kind == BloodStreamTriggerKind.PostMortemStumpFlow || kind == BloodStreamTriggerKind.LivingSeverStump;

	private static int DecalFrameLimit(BloodStreamTriggerKind kind) =>
		IsDismembermentFlow(kind) ? MaximumDismembermentDecalsPerFrame : MaximumDecalsPerFrame;

	private static int DecalSecondLimit(BloodStreamTriggerKind kind) =>
		IsDismembermentFlow(kind) ? MaximumDismembermentDecalsPerSecond : MaximumDecalsPerSecond;

	private static bool ReserveDecalCell(Vector3 point, float now, BloodStreamTriggerKind kind)
	{
		bool dismemberment = IsDismembermentFlow(kind);
		float cooldown = dismemberment ? DismembermentSpatialDecalCooldown : SpatialDecalCooldown;
		float cellSize = dismemberment ? DismembermentSpatialDecalCellSize : SpatialDecalCellSize;
		float maximumSquared = cellSize * cellSize;
		for (int index = 0; index < DecalCells.Length; index++)
		{
			DecalCell cell = DecalCells[index];
			if (cell.Used && now - cell.Time < cooldown && (cell.Position - point).sqrMagnitude < maximumSquared)
				return false;
		}
		DecalCells[_decalCellWriteIndex] = new DecalCell { Used = true, Position = point, Time = now };
		_decalCellWriteIndex = (_decalCellWriteIndex + 1) % DecalCells.Length;
		return true;
	}

	private static int FindEmitterSlot(BloodStreamTriggerKind incomingKind, Player incomingPlayer)
	{
		for (int index = 0; index < Emitters.Length; index++) if (!Emitters[index].Active) return index;
		int victim = -1;
		int victimPriority = int.MaxValue;
		float oldest = float.MaxValue;
		int incomingPriority = StreamPriority(incomingKind, incomingPlayer);
		for (int index = 0; index < Emitters.Length; index++)
		{
			StreamEmitter candidate = Emitters[index];
			int candidatePriority = StreamPriority(candidate.Descriptor.TriggerKind, candidate.Player);
			if (candidatePriority > incomingPriority) continue;
			if (candidatePriority < victimPriority || (candidatePriority == victimPriority && candidate.StartedAt < oldest))
			{
				victimPriority = candidatePriority;
				oldest = candidate.StartedAt;
				victim = index;
			}
		}
		if (victim >= 0) Retire(victim);
		return victim;
	}

	private static int StreamPriority(BloodStreamTriggerKind kind, Player player)
	{
		if (player != null && player.IsYourPlayer) return 2;
		if ((BleedingWoundTracker.IsLiving(kind) || kind == BloodStreamTriggerKind.LivingSeverStump) && player?.HealthController?.IsAlive == true) return 1;
		return 0;
	}

	private static int FindActive(uint streamId)
	{
		for (int index = 0; index < Emitters.Length; index++)
			if (Emitters[index].Active && Emitters[index].Descriptor.StreamId == streamId) return index;
		return -1;
	}

	private static void StopById(uint streamId)
	{
		ClothingSoakingSystem.Stop(streamId);
		int index = FindActive(streamId);
		if (index >= 0 && BleedingWoundTracker.IsLiving(Emitters[index].Descriptor.TriggerKind))
		{
			StreamEmitter emitter = Emitters[index];
			if (emitter.Draining) return;
			emitter.Draining = true; emitter.ExpiresAt = Time.realtimeSinceStartup;
			emitter.DrainStartedAt = emitter.ExpiresAt; emitter.DrainEndsAt = emitter.ExpiresAt + MaximumFlightSeconds;
			emitter.BaseFlow.Active = false;
			RememberRetired(streamId);
		}
		else if (index >= 0) Retire(index);
		else RememberRetired(streamId);
	}

	private static void LaunchLightDrip(int index, StreamEmitter emitter, float now)
	{
		uint seed = emitter.Descriptor.Seed ^ (++emitter.VisualIndex * 0x9E3779B9u);
		emitter.NextDripAt = now + 1f + Next01(ref seed) * 2f;
		Vector3 direction = emitter.Anchor.TransformDirection(emitter.Descriptor.LocalDirection).normalized;
		Vector3 origin = emitter.Anchor.TransformPoint(emitter.Descriptor.LocalPoint) + direction * 0.004f;
		Vector3 velocity = direction * emitter.Descriptor.PeakSpeed;
		PredictImpact(origin, velocity, Physics.gravity, MaximumFlightSeconds, out bool hit,
			out float age, out Vector3 point, out Vector3 normal);
		emitter.BaseFlow = new BaseFlowState { Active = true, ShapeSeed = seed, Origin = origin, Velocity = velocity, Radius = emitter.Descriptor.Radius,
			Strength = 1f, FlightDuration = MaximumFlightSeconds, HasImpact = hit, ImpactAge = age };
		ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodReferenceParcels, 1);
		AppendStreamSample(index, emitter, emitter.BaseFlow, now);
		emitter.BaseFlow.Active = false;
		if (!hit) return;
		QueueImpact(now + Mathf.Max(0.02f, age), point, normal, BloodStreamTriggerKind.LightWound,
			1f, seed, StreamPriority(BloodStreamTriggerKind.LightWound, emitter.Player), false);
	}

	private static void QueuePending(BloodStreamData packet)
	{
		int slot = -1;
		float oldest = float.MaxValue;
		for (int index = 0; index < PendingDescriptors.Length; index++)
		{
			PendingDescriptor pending = PendingDescriptors[index];
			if (pending.Active && pending.Packet.StreamId == packet.StreamId)
			{
				if (packet.EventSequence > pending.Packet.EventSequence)
					PendingDescriptors[index] = new PendingDescriptor { Active = true, Packet = packet, ExpiresAt = Time.realtimeSinceStartup + 4f };
				return;
			}
			if (!pending.Active)
			{
				slot = index;
				break;
			}
			if (pending.ExpiresAt < oldest)
			{
				oldest = pending.ExpiresAt;
				slot = index;
			}
		}
		if (slot >= 0)
			PendingDescriptors[slot] = new PendingDescriptor { Active = true, Packet = packet, ExpiresAt = Time.realtimeSinceStartup + 4f };
	}

	private static void ProcessPending(float now)
	{
		for (int index = 0; index < PendingDescriptors.Length; index++)
		{
			PendingDescriptor pending = PendingDescriptors[index];
			if (!pending.Active) continue;
			if (pending.ExpiresAt <= now || IsRetired(pending.Packet.StreamId))
			{
				PendingDescriptors[index] = default;
				continue;
			}
			Player player = ResolvePlayer(pending.Packet.ProfileId);
			Transform anchor = player != null ? ResolvePacketAnchor(player, pending.Packet.ColliderType,
				pending.Packet.BodyPart, pending.Packet.AnchorBone) : null;
			if (player == null || anchor == null) continue;
			TrackLivingStream(pending.Packet, player);
			StartOrReplace(pending.Packet, player, anchor);
			PendingDescriptors[index] = default;
		}
	}

	private static void RemovePending(uint streamId)
	{
		for (int index = 0; index < PendingDescriptors.Length; index++)
			if (PendingDescriptors[index].Active && PendingDescriptors[index].Packet.StreamId == streamId)
				PendingDescriptors[index] = default;
	}

	private static bool AcceptSequence(BloodStreamData packet)
	{
		int free = -1;
		int oldest = 0;
		float oldestTime = float.MaxValue;
		for (int index = 0; index < SequenceRecords.Length; index++)
		{
			SequenceRecord record = SequenceRecords[index];
			if (!record.Active)
			{
				if (free < 0) free = index;
				continue;
			}
			if (record.UpdatedAt < oldestTime)
			{
				oldestTime = record.UpdatedAt;
				oldest = index;
			}
			if (!ProfileMatches(record.ProfileId, packet.ProfileId) || record.DeathSequence != packet.DeathSequence) continue;
			if (packet.EventSequence <= record.LastEventSequence) return false;
			record.LastEventSequence = packet.EventSequence;
			record.UpdatedAt = Time.realtimeSinceStartup;
			SequenceRecords[index] = record;
			return true;
		}
		int slot = free >= 0 ? free : oldest;
		SequenceRecords[slot] = new SequenceRecord
		{
			Active = true,
			ProfileId = packet.ProfileId,
			DeathSequence = packet.DeathSequence,
			LastEventSequence = packet.EventSequence,
			UpdatedAt = Time.realtimeSinceStartup
		};
		return true;
	}

	private static void Retire(int index)
	{
		uint id = Emitters[index].Descriptor.StreamId;
		ClearStreamSamples(index);
		ClearEmitter(Emitters[index]);
		RememberRetired(id);
	}

	private static void ClearEmitter(StreamEmitter emitter)
	{
		emitter.Active = false;
		emitter.LocalFracture = false;
		emitter.Visible = false;
		emitter.Descriptor = default;
		emitter.Player = null;
		emitter.Anchor = null;
		emitter.StartedAt = 0f;
		emitter.StartsAt = 0f;
		emitter.ExpiresAt = 0f;
		emitter.NextPulseAt = 0f;
		emitter.HeartbeatImpactScheduled = false;
		emitter.WobbleTransitionStartedAt = 0f;
		emitter.OriginWobbleFrom = Vector2.zero;
		emitter.OriginWobbleTarget = Vector2.zero;
		emitter.DirectionWobbleFrom = Vector2.zero;
		emitter.DirectionWobbleTarget = Vector2.zero;
		emitter.VisualIndex = 0;
		emitter.SampleStart = 0;
		emitter.SampleCount = 0;
		emitter.NextSampleAt = 0f;
		emitter.Draining = false;
		emitter.DrainStartedAt = 0f;
		emitter.DrainEndsAt = 0f;
		emitter.PulseIndex = 0;
		emitter.WoundOffshootStartedAt = 0f;
		emitter.BaseFlow = default;
	}

	private static void ResetBaseFlow(StreamEmitter emitter)
	{
		emitter.BaseFlow = new BaseFlowState
		{
			ShapeSeed = emitter.Descriptor.Seed ^ 0x27D4EB2Du,
			NextTraceAt = emitter.StartsAt,
			NextDecalAt = emitter.StartsAt
		};
	}

	private static void ResetHeartbeatState(StreamEmitter emitter, float now)
	{
		emitter.HeartbeatImpactScheduled = false;
		emitter.WobbleTransitionStartedAt = now;
		emitter.OriginWobbleFrom = Vector2.zero;
		emitter.OriginWobbleTarget = Vector2.zero;
		emitter.DirectionWobbleFrom = Vector2.zero;
		emitter.DirectionWobbleTarget = Vector2.zero;
		emitter.VisualIndex = 0;
		emitter.SampleStart = 0;
		emitter.SampleCount = 0;
		emitter.NextSampleAt = now;
		emitter.WoundOffshootStartedAt = now;
	}

	private static void RememberRetired(uint streamId)
	{
		if (streamId == 0 || IsRetired(streamId)) return;
		RetiredStreamIds[_retiredWriteIndex] = streamId;
		_retiredWriteIndex = (_retiredWriteIndex + 1) % RetiredStreamIds.Length;
	}

	private static bool IsRetired(uint streamId)
	{
		for (int index = 0; index < RetiredStreamIds.Length; index++) if (RetiredStreamIds[index] == streamId) return true;
		return false;
	}

	private static StreamEmitter[] CreateEmitters()
	{
		StreamEmitter[] result = new StreamEmitter[MaximumVisualEmitters];
		for (int index = 0; index < result.Length; index++) result[index] = new StreamEmitter();
		return result;
	}

	private static Vector3 PositionAt(Vector3 origin, Vector3 velocity, Vector3 gravity, float age)
	{
		age = Mathf.Clamp(age, 0f, MaximumFlightSeconds);
		return origin + velocity * age + gravity * (0.5f * age * age);
	}

	private static float PulsePeriod(uint seed)
	{
		uint state = seed;
		float bpm = Mathf.Lerp(70f, 100f, Next01(ref state));
		return 60f / bpm;
	}

	private static uint Hash(string profileId, uint a, uint b, uint c)
	{
		uint hash = 2166136261u;
		if (!string.IsNullOrEmpty(profileId))
		{
			for (int index = 0; index < profileId.Length; index++) hash = (hash ^ profileId[index]) * 16777619u;
		}
		hash = (hash ^ a) * 16777619u;
		hash = (hash ^ b) * 16777619u;
		hash = (hash ^ c) * 16777619u;
		return hash == 0 ? 1u : hash;
	}

	private static float Next01(ref uint state)
	{
		state ^= state << 13;
		state ^= state >> 17;
		state ^= state << 5;
		return (state & 0x00FFFFFFu) / 16777215f;
	}

	private static float Signed01(ref uint state) => Next01(ref state) * 2f - 1f;

	private static bool CanCreateStreams()
	{
		return RuptureConfiguration.EnableBloodEffects?.Value == true;
	}
	private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	private static bool ProfileMatches(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}
