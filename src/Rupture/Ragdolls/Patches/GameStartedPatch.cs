using System.Reflection;
using EFT;
using SPT.Reflection.Patching;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public class GameStartedPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(GameWorld).GetMethod("OnGameStarted");
	}

	[PatchPostfix]
	private static void Postfix(GameWorld __instance)
	{
		if (RupturePlugin.Instance == null)
		{
			return;
		}
		RagdollCoordinator.Reset();
		ShotImpactProcessor.Reset();
		ShadowRagdollWorld.OnGameStarted();
	}
}
