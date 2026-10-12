using System.Reflection;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Ragdolls.Classes;

namespace Rupture.Dismemberment.Patches;

public sealed class HeavyBleedingPatch : ModulePatch
{
	private struct PatchState
	{
		internal bool Candidate;
		internal EFT.Ballistics.DamageInfo DamageInfo;
		internal EBodyPart BodyPart;
		internal EBodyPartColliderType ColliderType;
		internal EFT.HealthSystem.IHealthEffect BeforeLight, BeforeHeavy;
	}

	protected override MethodBase GetTargetMethod()
	{
		return typeof(Player).GetMethod(nameof(Player.ApplyDamageInfo), new[]
		{
			typeof(EFT.Ballistics.DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float)
		});
	}

	[PatchPrefix]
	private static void Prefix(Player __instance, EFT.Ballistics.DamageInfo damageInfo, EBodyPart bodyPartType,
		EBodyPartColliderType colliderType, out PatchState __state)
	{
		__state = default;
		if (__instance?.HealthController == null || !__instance.HealthController.IsAlive ||
			(RagdollCoordinator.IsRemoteClient && !__instance.IsYourPlayer) || !IsFinite(damageInfo.HitPoint) ||
			damageInfo.Direction.sqrMagnitude < 0.0001f)
		{
			return;
		}
		__state.Candidate = true;
		__state.DamageInfo = damageInfo;
		__state.BodyPart = bodyPartType;
		__state.ColliderType = colliderType;
		__state.BeforeLight = __instance.HealthController.FindExistingEffect<EFT.HealthSystem.ILightBleeding>(bodyPartType);
		__state.BeforeHeavy = __instance.HealthController.FindExistingEffect<EFT.HealthSystem.IHeavyBleeding>(bodyPartType);
	}

	[PatchPostfix]
	private static void Postfix(Player __instance, PatchState __state)
	{
		if (!__state.Candidate || __instance?.HealthController == null || !__instance.HealthController.IsAlive)
		{
			return;
		}
		EFT.HealthSystem.IHealthEffect heavy = __instance.HealthController.FindExistingEffect<EFT.HealthSystem.IHeavyBleeding>(__state.BodyPart);
		EFT.HealthSystem.IHealthEffect light = __instance.HealthController.FindExistingEffect<EFT.HealthSystem.ILightBleeding>(__state.BodyPart);
		if ((heavy != null && !ReferenceEquals(heavy, __state.BeforeHeavy)) ||
			(light != null && !ReferenceEquals(light, __state.BeforeLight)))
			BleedingWoundTracker.Capture(__instance, __state.DamageInfo, __state.BodyPart, __state.ColliderType);
	}

	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}
