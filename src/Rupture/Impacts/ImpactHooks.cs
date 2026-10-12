using System;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using UnityEngine;

namespace Rupture.Impacts;

internal static class ImpactHooks
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.impacts");
    internal static void Install()
    {
        foreach (Type type in new[] { typeof(GameWorld), typeof(ClientGameWorld) })
        {
            MethodInfo method = type.GetMethod(nameof(GameWorld.ShotDelegate), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, new[] { typeof(EFT.Ballistics.Shot) }, null);
            if (method == null) throw new MissingMethodException(type.FullName, nameof(GameWorld.ShotDelegate));
            Patches.Patch(method, prefix: Method(nameof(ShotPrefix)), finalizer: Method(nameof(ShotFinally)));
        }
        var healthArgs = new[] { typeof(EBodyPart), typeof(float), typeof(EFT.Ballistics.DamageInfo) };
        Patches.Patch(AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ChangeHealth), healthArgs),
            prefix: Method(nameof(HealPrefix)), postfix: Method(nameof(HealPostfix)));
        Patches.Patch(AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage), healthArgs),
            prefix: Method(nameof(DamagePrefix)), postfix: Method(nameof(DamagePostfix)));
        Patches.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDead), new[] { typeof(EDamageType) }), prefix: Method(nameof(DeathPrefix)));
        Patches.Patch(AccessTools.Method(typeof(ExplosionSharedMethods), nameof(ExplosionSharedMethods.Explosion)),
            prefix: Method(nameof(BlastPrefix)), finalizer: Method(nameof(BlastFinally)));
    }
    internal static void Shutdown() => Patches.UnpatchSelf();
    private static HarmonyMethod Method(string name) => new HarmonyMethod(typeof(ImpactHooks), name);
    internal static void ShotPrefix(EFT.Ballistics.Shot shotResult, out ImpactData __state)
    {
        __state = ImpactStateService.Current;
        ImpactStateService.Current = ImpactData.Capture(shotResult);
        ImpactStateService.CorpseImpact(shotResult, ImpactStateService.Current);
    }
    internal static Exception ShotFinally(Exception __exception, ImpactData __state)
    {
        ImpactStateService.Current = __state;
        return __exception;
    }
    private static void DamagePrefix(ActiveHealthController __instance, EBodyPart bodyPart, out float __state) =>
        __state = Health.BotSeverStateService.Leg(bodyPart) >= 0 ? __instance.GetBodyPartHealth(bodyPart, false).Current : float.NaN;
    private static void DamagePostfix(ActiveHealthController __instance, EBodyPart bodyPart, EFT.Ballistics.DamageInfo damageInfo, float __result, float __state)
    {
        ImpactStateService.RecordDamage(__instance.Player, damageInfo, bodyPart, __result, __state);
        Health.BotSeverStateService.ExplosionDamaged(__instance.Player, damageInfo, bodyPart, __result);
    }
    private static void BlastPrefix(IExplosiveItem grenadeItem, Vector3 explosionPosition, EFT.InventoryLogic.Item originalWeaponItem,
        out ExplosionEventScope __state)
    {
        __state = ExplosionEventScope.EnterBlast(explosionPosition, grenadeItem?.IsDummy == false ? grenadeItem.MaxExplosionDistance : 0f);
        LivingExplosionEvents.Register(originalWeaponItem?.Id);
    }
    private static Exception BlastFinally(Exception __exception, ExplosionEventScope __state)
    { ExplosionEventScope.Restore(__state); return __exception; }
    private static void HealPrefix(ActiveHealthController __instance, EBodyPart bodyPart, out float __state) =>
        __state = Health.BotSeverStateService.Leg(bodyPart) >= 0 ? __instance.GetBodyPartHealth(bodyPart, false).Current : float.NaN;
    private static void HealPostfix(ActiveHealthController __instance, EBodyPart bodyPart, float value, float __state) =>
        ImpactStateService.Healed(__instance, bodyPart, value, __state);
    private static void DeathPrefix(Player __instance, EDamageType damageType)
    {
        Health.BotSeverStateService.Death(__instance);
        ImpactStateService.QueueDeath(__instance, damageType);
    }
}
