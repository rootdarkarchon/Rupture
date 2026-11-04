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
		internal ulong Projectile;
		internal uint Instance;
		internal Item BirthAmmo;
		internal int BirthFireIndex, BirthFragmentIndex, BirthSeed;
		internal bool HeadWoundCompleted;
	}

	private static readonly List<PendingShot> PendingShots = new List<PendingShot>(64);
	private static uint _nextInstance;

	internal static void Track(EFT.Ballistics.Shot shot)
	{
		if (shot == null)
		{
			return;
		}

		PendingShot pending = new PendingShot
		{
			Shot = shot,
			Instance = NextInstance(),
			BirthAmmo = shot.Ammo, BirthFireIndex = shot.FireIndex, BirthFragmentIndex = shot.FragmentIndex, BirthSeed = shot.RandomSeed,
			ExpiresAt = Time.realtimeSinceStartup + PendingShotLifetimeSeconds
		};
		CaptureFlightState(ref pending);
		PendingShots.Add(pending);
	}

	private static uint NextInstance() { if (++_nextInstance == 0) ++_nextInstance; return _nextInstance; }
	private static bool SameLifetime(PendingShot pending) => ReferenceEquals(pending.BirthAmmo, pending.Shot.Ammo) &&
		pending.BirthFireIndex == pending.Shot.FireIndex && pending.BirthFragmentIndex == pending.Shot.FragmentIndex && pending.BirthSeed == pending.Shot.RandomSeed;

	// The physical projectile identity deliberately groups penetration segments.
	// A tracked instance also ties the contact to this particular completion and
	// prevents a pooled Shot object or a sibling fragment from borrowing it.
	internal static bool TryGetTrackedContact(EFT.Ballistics.Shot contact, ulong projectile,
		out EFT.Ballistics.Shot tracked, out uint instance)
	{
		tracked = null; instance = 0;
		for (int depth = 0; contact != null && depth < 32; depth++, contact = contact.Parent)
		{
			int match = -1;
			for (int i = 0; i < PendingShots.Count; i++)
			{
				if (!ReferenceEquals(PendingShots[i].Shot, contact)) continue;
				if (match >= 0) return false; // ambiguous duplicate/pool lifetime
				match = i;
			}
			if (match >= 0)
			{
				PendingShot pending = PendingShots[match];
				if (!SameLifetime(pending)) return false;
				// Binding must not take an extra flight sample: baseline tracing
				// must retain the original processor's sampling cadence and force.
				if (pending.Projectile == 0) pending.Projectile = Rupture.Impacts.ImpactData.ProjectileIdentity(contact);
				PendingShots[match] = pending;
				if (projectile == 0 || pending.Projectile != projectile) return false;
				tracked = contact; instance = pending.Instance; return true;
			}
			if (contact.Parent?.BulletState == EFT.Ballistics.Shot.EBulletState.FragmentationHit) return false;
		}
		return false;
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
		// Retain identity while the shot/parent chain is still live, just as we
		// retain its last valid flight state before EFT retires the projectile.
		if (LethalImpulseExperiment.Enabled && pending.Projectile == 0)
			pending.Projectile = Rupture.Impacts.ImpactData.ProjectileIdentity(shot);
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
				bool handled = false;
				LethalImpulseExperiment.Record lethal = independentWeaponHit ? null : LethalImpulseExperiment.CompletedShot(
					shot, LethalImpulseExperiment.Enabled && SameLifetime(pending) ? pending.Instance : 0, corpse, hitCollider, rigidbody, pending.Projectile,
					impulse, shot.HitPoint, pending.BulletMassGram, pending.VelocityMagnitude, RuptureConfiguration.ShotIntensity.Value, out handled);
				if (!handled)
				{
					impulseApplied = independentWeaponHit
						? RagdollCoordinator.TryApplyIndependentWeaponPointImpulse(corpse, hitCollider, impulse, shot.HitPoint, out resolvedBodyName)
						: corpse != null && RagdollCoordinator.TryApplyCorpsePointImpulse(
							corpse, physicsHitCollider, impulse, shot.HitPoint, out resolvedBodyName);
					if (!impulseApplied && rigidbody != null)
					{
						impulseApplied = RagdollCoordinator.TryApplyPointImpulse(rigidbody, impulse, shot.HitPoint);
					}
					LethalImpulseExperiment.AfterCompleted(lethal, impulseApplied);
				}
			}
		}
		if (postMortemImpact)
		{
			AngularDriveExperiment.CorpseImpact(corpse, impulseApplied, appliedImpulseMagnitude, resolvedBodyName);
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
