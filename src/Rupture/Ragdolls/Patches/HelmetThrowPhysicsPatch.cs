using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public sealed class HelmetThrowPhysicsPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(GameWorld).GetMethod(
			"ThrowItem",
			BindingFlags.Instance | BindingFlags.Public,
			null,
			new[]
			{
				typeof(Item), typeof(IPlayer), typeof(Vector3), typeof(Quaternion), typeof(Vector3),
				typeof(Vector3), typeof(bool), typeof(bool), typeof(float)
			},
			null);
	}

	[PatchPrefix]
	private static void Prefix(Item item, ref Vector3 velocity, ref Vector3 angularVelocity, out bool __state)
	{
		if (LethalHeadgearDetach.TryConsume(item, out Vector3 lethalVelocity, out Vector3 lethalAngularVelocity))
		{
			velocity = lethalVelocity;
			angularVelocity = lethalAngularVelocity;
			__state = true;
			return;
		}
		__state = false;
	}

	[PatchPostfix]
	private static void Postfix(Item item, IPlayer player, LootItem __result, bool __state)
	{
		try { HeadgearDiagnostics.WorldCreated(item, __result); }
		catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
		bool replicated = LethalHeadgearDetach.ConsumeReplicatedThrow(item);
		if (__state || replicated) HeadgearPhysicsLifetime.Track(__result);
		if (!__state || __result == null || __result.RigidBody == null)
		{
			return;
		}
		Rigidbody helmetBody = __result.RigidBody;
		helmetBody.useGravity = true;
		helmetBody.isKinematic = false;
		helmetBody.WakeUp();

		// EFT's StartPhysics owns both support registration and its stop worker.
		// Do not add a standalone registration without the matching lifecycle.

		if (player is Player concretePlayer)
		{
			Collider[] helmetColliders = __result.GetComponentsInChildren<Collider>(includeInactive: true);
			Collider[] corpseColliders = concretePlayer.GetComponentsInChildren<Collider>(includeInactive: true);
			foreach (Collider helmetCollider in helmetColliders)
			{
				if (helmetCollider == null || helmetCollider.isTrigger)
				{
					continue;
				}
				foreach (Collider corpseCollider in corpseColliders)
				{
					if (corpseCollider != null && !corpseCollider.isTrigger && corpseCollider != helmetCollider)
					{
						Physics.IgnoreCollision(helmetCollider, corpseCollider, ignore: true);
					}
				}
			}
		}
	}
}
