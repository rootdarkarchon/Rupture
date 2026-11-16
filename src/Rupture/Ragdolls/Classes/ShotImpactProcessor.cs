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
	private const float MaximumShotDeferralSeconds = 1f;

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
		/// <summary>Realtime of the first time this shot waited for a parked body's environment capture; zero while it has not waited.</summary>
		internal float DeferredSince;
	}

	private static readonly List<PendingShot> PendingShots = new List<PendingShot>(64);
	/// <summary>
	/// Finished shots that hit a parked body and wait for the geometry it needs (<see cref="ProcessCompletedShot"/>). They no longer
	/// fly, so they live apart from <see cref="PendingShots"/>: a pooled <see cref="EFT.Ballistics.Shot"/> that EFT reuses for a new
	/// projectile can neither feed its flight into them nor make <see cref="TryGetTrackedContact"/> ambiguous for the new shot.
	/// </summary>
	private static readonly List<PendingShot> DeferredShots = new List<PendingShot>(8);
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
		RetryDeferredShots(now);
		for (int index = PendingShots.Count - 1; index >= 0; index--)
		{
			PendingShot pending = PendingShots[index];
			if (pending.Shot == null || pending.ExpiresAt <= now)
			{
				PendingShots.RemoveAt(index);
				continue;
			}

			CaptureFlightState(ref pending);
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotHeadWound)) TryCompleteHeadWound(ref pending);
			if (!pending.Shot.IsShotFinished)
			{
				PendingShots[index] = pending;
				continue;
			}

			PendingShots.RemoveAt(index);
			// A shot that hit a parked body waits, apart from the flying shots, for the geometry that body needs before it may move.
			if (ProcessCompletedShot(ref pending)) DeferredShots.Add(pending);
		}
	}

	/// <summary>
	/// Retries the finished shots that wait for a parked body. A deferred shot never samples flight, completes a head wound or checks
	/// <c>IsShotFinished</c> again: the <see cref="EFT.Ballistics.Shot"/> may be a pooled object that now describes another projectile,
	/// which is dropped as soon as its birth fingerprint no longer matches.
	/// </summary>
	private static void RetryDeferredShots(float now)
	{
		for (int index = DeferredShots.Count - 1; index >= 0; index--)
		{
			PendingShot pending = DeferredShots[index];
			if (pending.Shot == null || pending.ExpiresAt <= now || !SameLifetime(pending))
			{
				DeferredShots.RemoveAt(index);
				continue;
			}

			if (ProcessCompletedShot(ref pending)) DeferredShots[index] = pending;
			else DeferredShots.RemoveAt(index);
		}
	}

	internal static void Reset()
	{
		PendingShots.Clear();
		DeferredShots.Clear();
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

	/// <summary>
	/// Applies a finished shot to what it hit. True when the shot has to wait: its target is parked (a settled corpse, its weapon or a
	/// detached gib) and the geometry it needs before it may move is still being captured. The shot is kept in <see cref="DeferredShots"/>
	/// and its impulse is applied once the body is awake; the wait is bounded by the world's wake wait.
	/// </summary>
	private static bool ProcessCompletedShot(ref PendingShot pending)
	{
		EFT.Ballistics.Shot shot = pending.Shot;
		Collider hitCollider = shot?.HitCollider;
		if (hitCollider == null || !(shot.Ammo is EFT.InventoryLogic.Ammo ammo))
		{
			return false;
		}
		// Complete the lethal descriptor with retained impact velocity, mass,
		// diameter and final collider before processing post-mortem interactions.
		if (!pending.HeadWoundCompleted)
		{
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotHeadWound))
			{
				pending.HeadWoundCompleted = HeadWoundSystem.TryCompleteLethalHeadshot(shot, pending.BulletMassGram,
					pending.VelocityMagnitude, pending.Direction);
			}
		}
		float projectileImpulseMagnitude = PhysicsMath.CalculateProjectileImpulse(
			pending.BulletMassGram, pending.VelocityMagnitude, RuptureConfiguration.ShotIntensity.Value);
		float now = Time.realtimeSinceStartup;
		string localGibBody = null;
		bool localGibApplied = false;
		bool awaitingGibEnvironment = false;
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotImpulse))
		{
			localGibApplied = projectileImpulseMagnitude > 0f && pending.Direction.sqrMagnitude > 0.0001f &&
				ShadowRagdollWorld.TryApplyLocalDetachedGibPointImpulse(hitCollider,
					pending.Direction * projectileImpulseMagnitude, shot.HitPoint, out localGibBody, out awaitingGibEnvironment);
		}
		if (localGibApplied)
		{
			if (RupturePlugin.Log.IsDebugEnabled)
			{
				using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotLog))
				{
					RupturePlugin.Log.LogInfo($"RPT local gib bullet impulse: applied=True, body={localGibBody ?? "unresolved"}, " +
						$"impulse={projectileImpulseMagnitude:F2}, speed={pending.VelocityMagnitude:F1}, " +
						$"massGrams={pending.BulletMassGram:F2}, caliber={ammo.Caliber ?? "unknown"}.");
				}
			}
			return false;
		}
		if (awaitingGibEnvironment && ShouldDefer(ref pending, now))
		{
			return true;
		}

		Corpse corpse;
		bool independentWeaponHit;
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotResolve))
		{
			corpse = hitCollider.GetComponentInParent<Corpse>();
			if (corpse == null)
			{
				ShadowRagdollWorld.TryResolveHitCorpse(hitCollider, out corpse);
			}
			independentWeaponHit = corpse != null && ShadowRagdollWorld.IsIndependentWeaponHit(corpse, hitCollider);
		}
		if (corpse != null && ShadowRagdollWorld.IsAwaitingImpactWake(corpse, independentWeaponHit) && ShouldDefer(ref pending, now))
		{
			return true;
		}
		CorpseInteractionTarget interactionTarget = default;
		bool postMortemImpact = false;
		if (corpse != null && !independentWeaponHit)
		{
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotPrepare))
			{
				RagdollCoordinator.PrepareCorpseImpact(corpse, explosion: false, awakenedStates: null, out interactionTarget, out postMortemImpact);
			}
		}
		EBodyPart resolvedBodyPart = default;
		Collider canonicalCollider = null;
		bool bodyPartResolved;
		Rigidbody rigidbody;
		using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotResolve))
		{
			bodyPartResolved = !independentWeaponHit && RagdollCoordinator.TryResolveCorpseBodyPart(interactionTarget, hitCollider,
				out resolvedBodyPart, out canonicalCollider);
			Collider physicsCollider = canonicalCollider ?? hitCollider;
			rigidbody = corpse != null && ShadowRagdollWorld.TryResolveHitBody(corpse, physicsCollider, out Rigidbody resolvedBody)
				? resolvedBody
				: physicsCollider.attachedRigidbody;
		}
		Collider physicsHitCollider = canonicalCollider ?? hitCollider;
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
				using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotImpulse))
				{
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
		}
		if (postMortemImpact)
		{
			AngularDriveExperiment.CorpseImpact(corpse, impulseApplied, appliedImpulseMagnitude, resolvedBodyName);
			// Debug diagnostics only: several shots of an automatic burst complete in one frame, and every console and file write lands inside this stage.
			if (RupturePlugin.Log.IsDebugEnabled)
			{
				using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.ShotLog))
				{
					RupturePlugin.Log.LogInfo($"RPT corpse bullet impulse: profile={corpse?.PlayerProfileID ?? "unknown"}, applied={impulseApplied}, bodyPart={(bodyPartResolved ? resolvedBodyPart.ToString() : "unresolved")}, " +
						$"body={resolvedBodyName ?? "unresolved"}, nativeBodyAlive={rigidbody != null}, interactionTarget={interactionTarget.VisualRoot != null}, " +
						$"impulse={appliedImpulseMagnitude:F2}, speed={pending.VelocityMagnitude:F1}, " +
						$"massGrams={pending.BulletMassGram:F2}, caliber={ammo.Caliber ?? "unknown"}.");
				}
			}
		}
		return false;
	}

	/// <summary>Marks the shot as waiting and reports whether it may keep waiting; a shot that waited a second goes on regardless.</summary>
	private static bool ShouldDefer(ref PendingShot pending, float now)
	{
		if (pending.DeferredSince <= 0f) pending.DeferredSince = now;
		return now - pending.DeferredSince < MaximumShotDeferralSeconds;
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
