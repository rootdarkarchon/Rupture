using System;
using System.Reflection;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public sealed class RagdollWakePatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(EFT.Interactive.CorpseRagdoll).GetMethod(
			nameof(EFT.Interactive.CorpseRagdoll.WakeUp),
			BindingFlags.Instance | BindingFlags.Public,
			null,
			Type.EmptyTypes,
			null);
	}

	[PatchPostfix]
	private static void Postfix(EFT.Interactive.CorpseRagdoll __instance)
	{
		ShadowRagdollWorld.WakeFromNative(__instance);
	}
}
