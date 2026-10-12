using System.Reflection;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public class RagdollClassPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(EFT.Interactive.CorpseRagdoll).GetMethod(
			"Start",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			null,
			System.Type.EmptyTypes,
			null);
	}

	[PatchPostfix]
	private static void Postfix(EFT.Interactive.CorpseRagdoll __instance)
	{
		RagdollCoordinator.RegisterNativeRagdoll(__instance);
		CorpseWeaponCollisionFilter.CompleteAfterRagdollStart(__instance);
	}
}
