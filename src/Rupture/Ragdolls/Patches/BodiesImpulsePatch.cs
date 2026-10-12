using System;
using System.Reflection;
using EFT.Ballistics;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public class BodiesImpulsePatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(BallisticsCalculator).GetMethod("Shoot", BindingFlags.Instance | BindingFlags.Public, null, new Type[1] { typeof(EFT.Ballistics.Shot) }, null);
	}

	[PatchPostfix]
	private static void Postfix(EFT.Ballistics.Shot shot)
	{
		ShotImpactProcessor.Track(shot);
	}
}
