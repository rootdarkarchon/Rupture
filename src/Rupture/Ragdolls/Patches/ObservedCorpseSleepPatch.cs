using System;
using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public sealed class ObservedCorpseSleepPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(ObservedCorpse).GetMethod(
			nameof(ObservedCorpse.CheckCorpseIsStill),
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
			null,
			new[] { typeof(bool), typeof(float) },
			null);
	}

	[PatchPostfix]
	private static void Postfix(ObservedCorpse __instance, bool sleeping, float timePass, ref bool __result)
	{
		__result = RagdollCoordinator.ShouldStop(__instance, __result, sleeping, timePass);
	}
}
