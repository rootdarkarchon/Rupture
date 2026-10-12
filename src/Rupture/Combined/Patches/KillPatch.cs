using System;
using System.Reflection;
using Comfort.Common;
using Rupture.Impacts;
using EFT;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.HeadWounds;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Ragdolls.Classes;

namespace Rupture.Combined.Patches;

public class KillPatch : ModulePatch
{

	protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.ApplyDamageInfo),
		new[] { typeof(EFT.Ballistics.DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) });

	[PatchPostfix]
	internal static void Postfix(Player __instance, EFT.Ballistics.DamageInfo damageInfo, EBodyPart bodyPartType)
	{
		if (__instance == null || __instance.HealthController == null || __instance.HealthController.IsAlive || damageInfo.OverDamageFrom.HasValue) return;
		ImpactStateService.Death(__instance, damageInfo, bodyPartType);
	}

	internal static void ProcessDeath(Player __instance, EFT.Ballistics.DamageInfo damageInfo, EBodyPart bodyPartType)
	{
		RagdollCoordinator.RegisterDeath(__instance, damageInfo, bodyPartType, out _);
		PulsatingBloodStreamSystem.StopLivingStreams(__instance, publish: true);
		if (!LimbImpactPolicy.Positive(damageInfo.Damage)) return;

		bool explosion = DeathImpactPolicy.HasSpatialExplosion(damageInfo, ImpactStateService.Current);
		if (explosion) RagdollCoordinator.MarkInitialExplosionImpact(__instance, damageInfo.MasterOrigin);
		bool projectile = ImpactStateService.Eligible(damageInfo);
		if (projectile && bodyPartType == EBodyPart.Head)
		{
			PulsatingBloodStreamSystem.PublishKillStream(__instance, damageInfo, bodyPartType,
				RagdollCoordinator.GetDeathSequence(__instance), waitForHeadExit: true);
			bool generateWound = RuptureConfiguration.EnableDismemberment.Value;
			if (ImpactStateService.Current.Valid)
				HeadWoundSystem.CompleteImpact(__instance, damageInfo, generateWound, ImpactStateService.Current);
			else if (HeadWoundSystem.RegisterLethalHeadshot(__instance, damageInfo, generateWound))
				ShotImpactProcessor.TryCompleteLethalHeadshotNow();
			// Whole-head dismemberment is disabled. The procedural projectile canal
			// remains the only current lethal head gore path.
			return;
		}

		bool dismembered = false;
		if (RuptureConfiguration.EnableDismemberment.Value && (explosion || (projectile && ImpactStateService.Current.Valid)) &&
			RagdollCoordinator.TryGetInteractionTarget(__instance, out CorpseInteractionTarget target))
		{
			Action failureFallback = projectile ? () =>
				PulsatingBloodStreamSystem.PublishKillStream(__instance, damageInfo, bodyPartType,
					RagdollCoordinator.GetDeathSequence(__instance), waitForHeadExit: false) : null;
			// EFT retains the blast/fragment launch center in MasterOrigin. HitPoint is on the victim.
			dismembered = DismemberForImpact(target, explosion ? damageInfo.MasterOrigin : damageInfo.HitPoint, damageInfo.Direction,
				bodyPartType, explosion, initialDeathSpawn: true, failureFallback);
		}
		if (projectile && !dismembered)
		{
			PulsatingBloodStreamSystem.PublishKillStream(__instance, damageInfo, bodyPartType,
				RagdollCoordinator.GetDeathSequence(__instance), waitForHeadExit: false);
		}
	}

	internal static void TryDismemberCorpseShot(CorpseInteractionTarget target,
		EBodyPart bodyPart, Vector3 hitPoint, Vector3 direction)
	{
		if (RagdollCoordinator.IsRemoteClient || target.VisualRoot == null ||
			RuptureConfiguration.EnableDismemberment?.Value != true || bodyPart == EBodyPart.Head) return;
		DismemberForImpact(target, hitPoint, direction, bodyPart, explosion: false, initialDeathSpawn: false);
	}

	internal static bool TryDismemberCorpseExplosion(CorpseInteractionTarget target, Vector3 impactPoint, Vector3 direction)
	{
		if (RagdollCoordinator.IsRemoteClient || target.VisualRoot == null ||
			RuptureConfiguration.EnableDismemberment?.Value != true) return false;
		// The owner's lethal snapshot performs the initial roll, even when it arrives
		// after the native corpse callback. Do not pre-roll its arms on the host.
		if (ImpactStateService.Get(target)?.AwaitingDeathSnapshot == true) return false;
		return DismemberForImpact(target, impactPoint, direction, EBodyPart.Common, explosion: true, initialDeathSpawn: false);
	}

	private static bool DismemberForImpact(CorpseInteractionTarget target, Vector3 impactPoint, Vector3 direction,
		EBodyPart bodyPart, bool explosion, bool initialDeathSpawn, Action failureFallback = null)
	{
		bool dismembered = false;
		if (explosion)
		{
			if (!ExplosionSeverPolicy.Finite(impactPoint)) return false;
			dismembered |= TrySeverExplosionLimb(target, EBodyPart.LeftLeg, impactPoint, initialDeathSpawn);
			dismembered |= TrySeverExplosionLimb(target, EBodyPart.RightLeg, impactPoint, initialDeathSpawn);
			dismembered |= TrySeverExplosionLimb(target, EBodyPart.LeftArm, impactPoint, initialDeathSpawn);
			dismembered |= TrySeverExplosionLimb(target, EBodyPart.RightArm, impactPoint, initialDeathSpawn);
			return dismembered;
		}
		if (bodyPart == EBodyPart.Head || !ImpactStateService.BeginSever(target, bodyPart, UnityEngine.Random.value)) return false;
		return TrySeverAuthority(target, bodyPart, impactPoint, direction, initialDeathSpawn, failureFallback);
	}

	private static bool TrySeverExplosionLimb(CorpseInteractionTarget target, EBodyPart bodyPart,
		Vector3 explosionCenter, bool initialDeathSpawn)
	{
		if (!LimbDismembermentSystem.TryGetExplosionDistance(target, bodyPart, explosionCenter,
			out float distance, out Vector3 nearest) || !ExplosionSeverPolicy.Select(distance, UnityEngine.Random.value)) return false;
		LimbSeverLevel level = ExplosionSeverPolicy.Level(UnityEngine.Random.value);
		Vector3 direction = nearest - explosionCenter;
		return TrySeverAuthority(target, bodyPart, explosionCenter,
			direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.up, initialDeathSpawn, forcedLevel: level);
	}

	private static bool TrySeverAuthority(CorpseInteractionTarget target, EBodyPart bodyPart, Vector3 impactPoint,
		Vector3 direction, bool initialDeathSpawn, Action failureFallback = null, LimbSeverLevel? forcedLevel = null)
	{
		uint eventSequence = RagdollCoordinator.NextNetworkEventSequence();
		uint seed = eventSequence * 0x9E3779B9u ^ target.DeathSequence * 0x85EBCA6Bu ^ (uint)bodyPart;
		LimbDismembermentSystem.SeverRequestResult result = LimbDismembermentSystem.RequestSever(target,
			bodyPart, impactPoint, direction, initialDeathSpawn, eventSequence, seed, forcedLevel,
			replicatedObserver: false, completion =>
			{
				ImpactStateService.Complete(target, bodyPart, completion.Status == LimbDismembermentSystem.SeverCompletionStatus.Committed);
				if (completion.Status == LimbDismembermentSystem.SeverCompletionStatus.Committed)
					SendDismemberment(target, bodyPart, completion.Level, direction, eventSequence, seed, initialDeathSpawn);
				else if (completion.Status == LimbDismembermentSystem.SeverCompletionStatus.Failed)
					failureFallback?.Invoke();
			}, out _);
		if (result == LimbDismembermentSystem.SeverRequestResult.Rejected) ImpactStateService.Complete(target, bodyPart, false);
		return result != LimbDismembermentSystem.SeverRequestResult.Rejected;
	}

	private static void SendDismemberment(CorpseInteractionTarget target, EBodyPart bodyPart,
		LimbSeverLevel level, Vector3 direction, uint eventSequence, uint seed, bool initialDeathSpawn)
	{
		if (!RagdollCoordinator.IsNetworkAuthority || target.PlayerId < 0 || target.DeathSequence == 0 ||
			!MultiplayerRuntime.Current.HasServer) return;
		DismembermentData packet = new DismembermentData
		{
			PlayerId = target.PlayerId,
			DeathSequence = target.DeathSequence,
			EventSequence = eventSequence,
			Direction = direction,
			BodyPart = bodyPart,
			SeverLevel = level,
			IrregularitySeed = seed,
			InitialDeathSpawn = initialDeathSpawn
		};
		MultiplayerRuntime.Current.Send(ref packet);
	}

	internal static BodyPartCollider ResolveBodyPartCollider(Player player, Collider hitCollider)
	{
		if (player == null || hitCollider == null) return null;
		BodyPartCollider direct = hitCollider.GetComponent<BodyPartCollider>() ?? hitCollider.GetComponentInParent<BodyPartCollider>();
		if (direct != null) return direct;
		BodyPartCollider[] bodyPartColliders = player.PlayerBones?.BodyPartColliders;
		if (bodyPartColliders == null) return null;
		foreach (BodyPartCollider candidate in bodyPartColliders)
		{
			Collider canonical = candidate?.Collider;
			if (canonical != null && (canonical == hitCollider || hitCollider.transform.IsChildOf(canonical.transform) ||
				canonical.transform.IsChildOf(hitCollider.transform))) return candidate;
		}
		return null;
	}
}
