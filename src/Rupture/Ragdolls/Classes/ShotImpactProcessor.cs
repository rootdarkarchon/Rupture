using System.Collections.Generic;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;
using Rupture.Combined.Patches;
using Rupture.Dismemberment.Classes.HeadWounds;

namespace Rupture.Ragdolls.Classes;

internal static class ShotImpactProcessor
{
	private const float PendingShotLifetimeSeconds = 30f;

	private struct PendingShot
	{
		internal EFT.Ballistics.Shot Shot;
		internal float ExpiresAt;
		internal float BulletMassGram;
		internal float VelocityMagnitude;
		internal Vector3 Direction;
		internal bool HeadWoundCompleted;
	}

	private static readonly List<PendingShot> PendingShots = new List<PendingShot>(64);

	internal static void Track(EFT.Ballistics.Shot shot)
	{
		if (shot == null)
		{
			return;
		}

		PendingShot pending = new PendingShot
		{
			Shot = shot,
			ExpiresAt = Time.realtimeSinceStartup + PendingShotLifetimeSeconds
		};
		CaptureFlightState(ref pending);
		PendingShots.Add(pending);
	}

	internal static void Update()
	{
		float now = Time.realtimeSinceStartup;
		for (int index = PendingShots.Count - 1; index >= 0; index--)
		{
			PendingShot pending = PendingShots[index];
			if (pending.Shot == null || pending.ExpiresAt <= now)
			{
				PendingShots.RemoveAt(index);
				continue;
			}

			CaptureFlightState(ref pending);
			TryCompleteHeadWound(ref pending);
			if (!pending.Shot.IsShotFinished)
			{
				PendingShots[index] = pending;
				continue;
			}

			PendingShots.RemoveAt(index);
			ProcessCompletedShot(pending);
		}
	}

	internal static void Reset()
	{
		PendingShots.Clear();
	}

	internal static void TryCompleteLethalHeadshotNow()
	{
		for (int index = PendingShots.Count - 1; index >= 0; index--)
		{
			PendingShot pending = PendingShots[index];
			CaptureFlightState(ref pending);
			TryCompleteHeadWound(ref pending);
			PendingShots[index] = pending;
			if (pending.HeadWoundCompleted)
			{
				return;
			}
		}
	}

	private static void CaptureFlightState(ref PendingShot pending)
	{
		EFT.Ballistics.Shot shot = pending.Shot;
		if (shot == null)
		{
			return;
		}
		if (IsFinitePositive(shot.BulletMassGram))
		{
			pending.BulletMassGram = shot.BulletMassGram;
		}
		if (IsFinitePositive(shot.VelocityMagnitude))
		{
			pending.VelocityMagnitude = shot.VelocityMagnitude;
		}
		if (IsFinite(shot.CurrentDirection) && shot.CurrentDirection.sqrMagnitude > 0.0001f)
		{
			pending.Direction = shot.CurrentDirection.normalized;
		}
	}

	private static void ProcessCompletedShot(PendingShot pending)
	{
		EFT.Ballistics.Shot shot = pending.Shot;
		Collider hitCollider = shot?.HitCollider;
		if (hitCollider == null || !(shot.Ammo is EFT.InventoryLogic.Ammo ammo))
		{
			return;
		}
		// Complete the lethal descriptor with retained impact velocity, mass,
		// diameter and final collider before processing post-mortem interactions.
		if (!pending.HeadWoundCompleted)
		{
			HeadWoundSystem.TryCompleteLethalHeadshot(shot, pending.BulletMassGram,
				pending.VelocityMagnitude, pending.Direction);
		}
		float projectileImpulseMagnitude = PhysicsMath.CalculateProjectileImpulse(
			pending.BulletMassGram, pending.VelocityMagnitude, RuptureConfiguration.ShotIntensity.Value);
		if (projectileImpulseMagnitude > 0f && pending.Direction.sqrMagnitude > 0.0001f &&
			ShadowRagdollWorld.TryApplyLocalDetachedGibPointImpulse(hitCollider,
				pending.Direction * projectileImpulseMagnitude, shot.HitPoint, out string localGibBody))
		{
			RupturePlugin.Log.LogInfo($"RPT local gib bullet impulse: applied=True, body={localGibBody ?? "unresolved"}, " +
				$"impulse={projectileImpulseMagnitude:F2}, speed={pending.VelocityMagnitude:F1}, " +
				$"massGrams={pending.BulletMassGram:F2}, caliber={ammo.Caliber ?? "unknown"}.");
			return;
		}

		Corpse corpse = hitCollider.GetComponentInParent<Corpse>();
		if (corpse == null)
		{
			ShadowRagdollWorld.TryResolveHitCorpse(hitCollider, out corpse);
		}
		bool independentWeaponHit = corpse != null && ShadowRagdollWorld.IsIndependentWeaponHit(corpse, hitCollider);
		CorpseInteractionTarget interactionTarget = default;
		bool postMortemImpact = false;
		if (corpse != null && !independentWeaponHit)
		{
			RagdollCoordinator.PrepareCorpseImpact(corpse, explosion: false, awakenedStates: null, out interactionTarget, out postMortemImpact);
		}
		EBodyPart resolvedBodyPart = default;
		Collider canonicalCollider = null;
		bool bodyPartResolved = !independentWeaponHit && RagdollCoordinator.TryResolveCorpseBodyPart(interactionTarget, hitCollider,
			out resolvedBodyPart, out canonicalCollider);
		Collider physicsHitCollider = canonicalCollider ?? hitCollider;
		Rigidbody rigidbody = corpse != null && ShadowRagdollWorld.TryResolveHitBody(corpse, physicsHitCollider, out Rigidbody resolvedBody)
			? resolvedBody
			: physicsHitCollider.attachedRigidbody;
		string resolvedBodyName = rigidbody?.name;
		float appliedImpulseMagnitude = 0f;
		bool impulseApplied = false;
		// Corpse derives from LootItem in EFT. Preserve the general loot exclusion,
		// but allow a body resolved from an explicit corpse hit through to the
		// coordinator so it can redirect the impulse into the shadow rig.
		bool eligibleNativeBody = rigidbody != null && rigidbody.GetComponentInParent<LootItem>() == null &&
			(RagdollCoordinator.IsTrackedBody(rigidbody) || !rigidbody.isKinematic);
		if (corpse != null || eligibleNativeBody)
		{
			float impulseMagnitude = projectileImpulseMagnitude;
			if (impulseMagnitude > 0f && pending.Direction.sqrMagnitude > 0.0001f)
			{
				Vector3 impulse = pending.Direction * impulseMagnitude;
				appliedImpulseMagnitude = impulseMagnitude;
				impulseApplied = independentWeaponHit
					? RagdollCoordinator.TryApplyIndependentWeaponPointImpulse(corpse, hitCollider, impulse, shot.HitPoint, out resolvedBodyName)
					: corpse != null && RagdollCoordinator.TryApplyCorpsePointImpulse(
						corpse, physicsHitCollider, impulse, shot.HitPoint, out resolvedBodyName);
				if (!impulseApplied && rigidbody != null)
				{
					impulseApplied = RagdollCoordinator.TryApplyPointImpulse(rigidbody, impulse, shot.HitPoint);
				}
			}
		}
		if (postMortemImpact)
		{
			RupturePlugin.Log.LogInfo($"RPT corpse bullet impulse: profile={corpse?.PlayerProfileID ?? "unknown"}, applied={impulseApplied}, bodyPart={(bodyPartResolved ? resolvedBodyPart.ToString() : "unresolved")}, " +
				$"body={resolvedBodyName ?? "unresolved"}, nativeBodyAlive={rigidbody != null}, interactionTarget={interactionTarget.VisualRoot != null}, " +
				$"impulse={appliedImpulseMagnitude:F2}, speed={pending.VelocityMagnitude:F1}, " +
				$"massGrams={pending.BulletMassGram:F2}, caliber={ammo.Caliber ?? "unknown"}.");
		}

	}

	private static void TryCompleteHeadWound(ref PendingShot pending)
	{
		if (pending.HeadWoundCompleted || pending.Shot?.HitCollider == null ||
			!IsFinitePositive(pending.BulletMassGram) || !IsFinitePositive(pending.VelocityMagnitude))
		{
			return;
		}
		pending.HeadWoundCompleted = HeadWoundSystem.TryCompleteLethalHeadshot(pending.Shot,
			pending.BulletMassGram, pending.VelocityMagnitude, pending.Direction);
	}

	private static bool IsFinitePositive(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
	}

	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
			!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}
