using System;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public abstract class RigidbodyForcePatch : ModulePatch
{
	private readonly string _methodName;
	private readonly Type[] _parameters;

	protected RigidbodyForcePatch(string methodName, params Type[] parameters)
	{
		_methodName = methodName;
		_parameters = parameters;
	}

	protected override MethodBase GetTargetMethod()
	{
		return typeof(Rigidbody).GetMethod(_methodName, BindingFlags.Instance | BindingFlags.Public, null, _parameters, null);
	}
}

public sealed class RigidbodyAddForcePatch : RigidbodyForcePatch
{
	public RigidbodyAddForcePatch() : base(nameof(Rigidbody.AddForce), typeof(Vector3), typeof(ForceMode))
	{
	}

	[PatchPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Rigidbody __instance, Vector3 force, ForceMode mode)
	{
		ShadowRagdollWorld.RedirectForce(__instance, ShadowForceOperation.AddForce, force, Vector3.zero, 0f, 0f, 0f, mode);
	}
}

public sealed class RigidbodyAddRelativeForcePatch : RigidbodyForcePatch
{
	public RigidbodyAddRelativeForcePatch() : base(nameof(Rigidbody.AddRelativeForce), typeof(Vector3), typeof(ForceMode))
	{
	}

	[PatchPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Rigidbody __instance, Vector3 force, ForceMode mode)
	{
		ShadowRagdollWorld.RedirectForce(__instance, ShadowForceOperation.AddRelativeForce, force, Vector3.zero, 0f, 0f, 0f, mode);
	}
}

public sealed class RigidbodyAddForceAtPositionPatch : RigidbodyForcePatch
{
	public RigidbodyAddForceAtPositionPatch() : base(nameof(Rigidbody.AddForceAtPosition), typeof(Vector3), typeof(Vector3), typeof(ForceMode))
	{
	}

	[PatchPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Rigidbody __instance, Vector3 force, Vector3 position, ForceMode mode)
	{
		Vector3 localPoint = __instance != null ? __instance.transform.InverseTransformPoint(position) : Vector3.zero;
		ShadowRagdollWorld.RedirectForce(__instance, ShadowForceOperation.AddForceAtPosition, force, localPoint, 0f, 0f, 0f, mode);
	}
}

public sealed class RigidbodyAddExplosionForcePatch : RigidbodyForcePatch
{
	public RigidbodyAddExplosionForcePatch() : base(nameof(Rigidbody.AddExplosionForce), typeof(float), typeof(Vector3), typeof(float), typeof(float), typeof(ForceMode))
	{
	}

	[PatchPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Rigidbody __instance, float explosionForce, Vector3 explosionPosition, float explosionRadius, float upwardsModifier, ForceMode mode)
	{
		ShadowRagdollWorld.RedirectForce(__instance, ShadowForceOperation.AddExplosionForce, Vector3.zero, explosionPosition, explosionForce, explosionRadius, upwardsModifier, mode);
	}
}

public sealed class RigidbodyAddTorquePatch : RigidbodyForcePatch
{
	public RigidbodyAddTorquePatch() : base(nameof(Rigidbody.AddTorque), typeof(Vector3), typeof(ForceMode))
	{
	}

	[PatchPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Rigidbody __instance, Vector3 torque, ForceMode mode)
	{
		ShadowRagdollWorld.RedirectForce(__instance, ShadowForceOperation.AddTorque, torque, Vector3.zero, 0f, 0f, 0f, mode);
	}
}

public sealed class RigidbodyAddRelativeTorquePatch : RigidbodyForcePatch
{
	public RigidbodyAddRelativeTorquePatch() : base(nameof(Rigidbody.AddRelativeTorque), typeof(Vector3), typeof(ForceMode))
	{
	}

	[PatchPostfix]
	[HarmonyPriority(Priority.Last)]
	private static void Postfix(Rigidbody __instance, Vector3 torque, ForceMode mode)
	{
		ShadowRagdollWorld.RedirectForce(__instance, ShadowForceOperation.AddRelativeTorque, torque, Vector3.zero, 0f, 0f, 0f, mode);
	}
}
