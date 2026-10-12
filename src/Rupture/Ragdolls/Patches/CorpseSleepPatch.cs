using System;
using System.Reflection;
using EFT.Interactive;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public sealed class CorpseSleepPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(Corpse).GetMethod(
			nameof(Corpse.CheckCorpseIsStill),
			BindingFlags.Instance | BindingFlags.Public,
			null,
			new[] { typeof(bool), typeof(float) },
			null);
	}

	[PatchPostfix]
	private static void Postfix(Corpse __instance, bool sleeping, float timePass, ref bool __result)
	{
		__result = RagdollCoordinator.ShouldStop(__instance, __result, sleeping, timePass);
	}
}
