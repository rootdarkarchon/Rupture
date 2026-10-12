using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static class CorpseWeaponCollisionFilter
{
	private static readonly Dictionary<EFT.Interactive.CorpseRagdoll, Rigidbody> PendingWeapons = new Dictionary<EFT.Interactive.CorpseRagdoll, Rigidbody>();
	private static readonly List<Collider> CorpseColliderScratch = new List<Collider>(64);
	private static readonly List<Collider> WeaponColliderScratch = new List<Collider>(16);

	internal static void Reset()
	{
		PendingWeapons.Clear();
		CorpseColliderScratch.Clear();
		WeaponColliderScratch.Clear();
	}

	internal static void Forget(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll != null) PendingWeapons.Remove(ragdoll);
	}

	internal static void TrackAndIgnore(EFT.Interactive.CorpseRagdoll ragdoll, Rigidbody weaponRigidbody)
	{
		if (ragdoll == null || weaponRigidbody == null)
		{
			return;
		}

		PendingWeapons[ragdoll] = weaponRigidbody;
		if (Ignore(ragdoll, weaponRigidbody))
		{
			PendingWeapons.Remove(ragdoll);
		}
	}

	internal static void CompleteAfterRagdollStart(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll == null)
		{
			return;
		}

		if (!PendingWeapons.TryGetValue(ragdoll, out Rigidbody weaponRigidbody))
		{
			Joint weaponJoint = ragdoll._weaponJoint;
			weaponRigidbody = weaponJoint != null ? weaponJoint.GetComponent<Rigidbody>() : null;
		}
		if (Ignore(ragdoll, weaponRigidbody))
		{
			PendingWeapons.Remove(ragdoll);
		}
	}

	internal static void Restore(EFT.Interactive.CorpseRagdoll ragdoll, Rigidbody weaponRigidbody)
	{
		RigidbodySpawner[] spawners = ragdoll?._rigidbodySpawners;
		if (weaponRigidbody == null || spawners == null)
		{
			return;
		}

		WeaponColliderScratch.Clear();
		weaponRigidbody.GetComponentsInChildren(includeInactive: true, result: WeaponColliderScratch);
		foreach (RigidbodySpawner spawner in spawners)
		{
			Rigidbody body = spawner?.Rigidbody;
			if (body == null || body == weaponRigidbody)
			{
				continue;
			}

			CorpseColliderScratch.Clear();
			body.GetComponentsInChildren(includeInactive: true, result: CorpseColliderScratch);
			foreach (Collider corpseCollider in CorpseColliderScratch)
			{
				if (corpseCollider == null || corpseCollider.isTrigger || corpseCollider.attachedRigidbody != body)
				{
					continue;
				}
				foreach (Collider weaponCollider in WeaponColliderScratch)
				{
					if (weaponCollider != null && !weaponCollider.isTrigger && weaponCollider != corpseCollider)
					{
						Physics.IgnoreCollision(corpseCollider, weaponCollider, ignore: false);
					}
				}
			}
		}
		PendingWeapons.Remove(ragdoll);
		CorpseColliderScratch.Clear();
		WeaponColliderScratch.Clear();
	}

	private static bool Ignore(EFT.Interactive.CorpseRagdoll ragdoll, Rigidbody weaponRigidbody)
	{
		RigidbodySpawner[] spawners = ragdoll?._rigidbodySpawners;
		if (weaponRigidbody == null || spawners == null || spawners.Length == 0)
		{
			return false;
		}
		foreach (RigidbodySpawner spawner in spawners)
		{
			if (spawner?.Rigidbody == weaponRigidbody)
			{
				return false;
			}
		}

		WeaponColliderScratch.Clear();
		weaponRigidbody.GetComponentsInChildren(includeInactive: true, result: WeaponColliderScratch);
		if (WeaponColliderScratch.Count == 0)
		{
			return false;
		}

		int ignoredPairs = 0;
		foreach (RigidbodySpawner spawner in spawners)
		{
			Rigidbody body = spawner?.Rigidbody;
			if (body == null || body == weaponRigidbody)
			{
				continue;
			}

			CorpseColliderScratch.Clear();
			body.GetComponentsInChildren(includeInactive: true, result: CorpseColliderScratch);
			foreach (Collider corpseCollider in CorpseColliderScratch)
			{
				if (corpseCollider == null || corpseCollider.isTrigger || corpseCollider.attachedRigidbody != body)
				{
					continue;
				}

				foreach (Collider weaponCollider in WeaponColliderScratch)
				{
					if (weaponCollider != null && !weaponCollider.isTrigger && weaponCollider != corpseCollider)
					{
						Physics.IgnoreCollision(corpseCollider, weaponCollider, ignore: true);
						ignoredPairs++;
					}
				}
			}
		}

		CorpseColliderScratch.Clear();
		WeaponColliderScratch.Clear();
		return ignoredPairs > 0;
	}
}
