using System.Reflection;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

public class CorpseWeaponCollisionPatch : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(EFT.Interactive.CorpseRagdoll).GetMethod(
			"AttachWeapon",
			BindingFlags.Instance | BindingFlags.Public,
			null,
			new[]
			{
				typeof(Rigidbody),
				typeof(GameObject),
				typeof(PlayerBones),
				typeof(TransformLinks),
				typeof(bool),
				typeof(Vector3)
			},
			null);
	}

	[PatchPostfix]
	private static void Postfix(EFT.Interactive.CorpseRagdoll __instance, Rigidbody weaponRigidbody)
	{
		CorpseWeaponCollisionFilter.TrackAndIgnore(__instance, weaponRigidbody);
		ShadowRagdollWorld.AttachLateWeapon(__instance, weaponRigidbody);
	}
}
