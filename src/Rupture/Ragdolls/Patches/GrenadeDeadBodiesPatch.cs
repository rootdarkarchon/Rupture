using System.Reflection;
using System;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Ragdolls.Classes;
using Rupture.Impacts;

namespace Rupture.Ragdolls.Patches;

public class GrenadeDeadBodiesPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(Grenade).GetMethod("Explosion", BindingFlags.Static | BindingFlags.Public, null,
			new[] { typeof(Grenade), typeof(IExplosiveItem), typeof(Vector3), typeof(string),
				typeof(EFT.Ballistics.IBallisticsCalculator), typeof(EFT.InventoryLogic.Item), typeof(Vector3), typeof(bool) }, null);
	}

	[PatchPrefix]
	private static void Prefix(Vector3 grenadePosition, Vector3 shift, out ExplosionEventScope __state)
	{
		__state = ExplosionEventScope.Enter(grenadePosition + shift);
	}

	[PatchFinalizer]
	private static Exception Finalizer(Exception __exception, ExplosionEventScope __state)
	{
		ExplosionEventScope.Restore(__state);
		return __exception;
	}

	[PatchPostfix]
	private static void Postfix(IExplosiveItem grenadeItem, Vector3 grenadePosition, Vector3 shift)
	{
		// EFT applies this offset before creating radial damage and fragments.
		GrenadePhysics.ApplyAuthorityRadialImpulse(grenadeItem, grenadePosition + shift);
	}
}
