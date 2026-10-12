using EFT.Interactive;
using HarmonyLib;
using Rupture.Ragdolls.Classes;

namespace Rupture.Ragdolls.Patches;

internal static class HeadgearPhysicsLifetimePatch
{
    internal static void Install()
    {
        var harmony = new Harmony("com.rootdarkarchon.rupture.headgear-physics");
        var done = new HarmonyMethod(typeof(HeadgearPhysicsLifetimePatch), nameof(Done));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(LootItem), nameof(LootItem.IsRigidbodyDone)), postfix: done);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ObservedLootItem), nameof(LootItem.IsRigidbodyDone)), postfix: done);
        harmony.Patch(AccessTools.Method(typeof(LootItem), nameof(LootItem.StopPhysics)),
            prefix: new HarmonyMethod(typeof(HeadgearPhysicsLifetimePatch), nameof(Stopping)));
    }

    private static void Done(LootItem __instance, ref bool __result) =>
        __result = HeadgearPhysicsLifetime.IsDone(__instance, __result, UnityEngine.Time.time);

    private static void Stopping(LootItem __instance) => HeadgearPhysicsLifetime.BeforeStop(__instance);
    internal static void Shutdown() => new Harmony("com.rootdarkarchon.rupture.headgear-physics").UnpatchSelf();
}
