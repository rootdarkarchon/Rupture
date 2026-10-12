using System;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using Rupture.Ragdolls.Classes;

namespace Rupture.Health;

internal static class BotSeverHooks
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.living-sever");
    private static readonly AccessTools.FieldRef<MovementContext, Player> MovementPlayer = AccessTools.FieldRefAccess<MovementContext, Player>("_player");
    [ThreadStatic] private static ActiveHealthController.MedEffect _surgery;
    private struct MedCompletionState
    {
        internal ActiveHealthController.MedEffect Previous;
        internal ActiveHealthController.Effect Heavy, Light, Fracture;
    }
    internal static void Install()
    {
        Patch(typeof(ActiveHealthController), nameof(ActiveHealthController.RestoreBodyPart), nameof(RestorePrefix), nameof(RestorePostfix));
        Patch(typeof(ActiveHealthController), nameof(ActiveHealthController.FullRestoreBodyPart), nameof(FullRestorePrefix), nameof(FullRestorePostfix));
        Patch(typeof(ActiveHealthController), nameof(ActiveHealthController.DoFracture), nameof(EffectPrefix));
        MethodInfo bleed = typeof(ActiveHealthController).GetMethod(nameof(ActiveHealthController.DoBleed), new[] { typeof(EBodyPart) });
        foreach (Type type in new[] { typeof(ActiveHealthController.LightBleeding), typeof(ActiveHealthController.HeavyBleeding) })
            Patches.Patch(bleed.MakeGenericMethod(type), prefix: Method(nameof(EffectPrefix)));
        Patches.Patch(AccessTools.Method(typeof(ActiveHealthController.MedEffect), nameof(ActiveHealthController.MedEffect.Residue)),
            prefix: Method(nameof(SurgeryPrefix)), postfix: Method(nameof(TreatmentPostfix)), finalizer: Method(nameof(SurgeryFinally)));
        Patches.Patch(AccessTools.PropertySetter(typeof(MovementContext), nameof(MovementContext.IsInPronePose)), prefix: Method(nameof(PronePrefix)));
        Patch(typeof(MovementContext), nameof(MovementContext.SetPoseLevel), nameof(PosePrefix));
        Patch(typeof(MovementContext), nameof(MovementContext.TryVaulting), nameof(VaultPrefix));
        Patches.Patch(AccessTools.PropertyGetter(typeof(MovementContext), nameof(MovementContext.CanJump)), postfix: Method(nameof(JumpPostfix)));
        Patches.Patch(AccessTools.PropertyGetter(typeof(MovementContext), nameof(MovementContext.CanProne)), postfix: Method(nameof(CanPronePostfix)));
        Patch(typeof(Player), nameof(Player.EnableSprint), nameof(SprintPrefix));
        Patch(typeof(Player.FirearmController), "SetTriggerPressed", nameof(TriggerPrefix));
        Patch(typeof(BotSurgicalKit), nameof(BotSurgicalKit.FindDamagedPart), null, nameof(SurgicalTargetPostfix));
    }
    private static HarmonyMethod Method(string name) => name == null ? null : new HarmonyMethod(typeof(BotSeverHooks), name);
    private static void Patch(Type type, string name, string prefix = null, string postfix = null)
    {
        MethodInfo target = AccessTools.Method(type, name);
        if (target == null) throw new MissingMethodException(type.FullName, name);
        Patches.Patch(target, prefix: Method(prefix), postfix: Method(postfix));
    }
    internal static void Shutdown() { Patches.UnpatchSelf(); _surgery = null; }
    private static void SurgeryPrefix(ActiveHealthController.MedEffect __instance, out MedCompletionState __state)
    {
        __state = new MedCompletionState { Previous = _surgery };
        if (!__instance._interrupted) CaptureLegTreatment(__instance, ref __state);
        _surgery = !__instance._interrupted && __instance._healthEffects?.AffectsAny(EDamageEffectType.DestroyedPart) == true
            ? __instance : null;
    }
    private static void CaptureLegTreatment(ActiveHealthController.MedEffect effect, ref MedCompletionState state)
    {
        if (BotSeverStateService.Leg(effect.BodyPart) < 0 || effect._healthEffects == null) return;
        var heavy = effect.HealthController.FindActiveEffect<ActiveHealthController.HeavyBleeding>(effect.BodyPart);
        var light = effect.HealthController.FindActiveEffect<ActiveHealthController.LightBleeding>(effect.BodyPart);
        var fracture = effect.HealthController.FindActiveEffect<ActiveHealthController.Fracture>(effect.BodyPart);
        if (CanTreat(effect, EDamageEffectType.HeavyBleeding, heavy != null)) state.Heavy = heavy;
        if (CanTreat(effect, EDamageEffectType.LightBleeding, light != null)) state.Light = light;
        if (CanTreat(effect, EDamageEffectType.Fracture, fracture != null)) state.Fracture = fracture;
    }
    private static bool CanTreat(ActiveHealthController.MedEffect effect, EDamageEffectType type, bool present) => present &&
        effect._healthEffects.DamageEffects.TryGetValue(type, out var treatment) &&
        (effect._medKit == null || effect._medKit.HpResource >= treatment.Cost);
    private static void TreatmentPostfix(ActiveHealthController.MedEffect __instance, MedCompletionState __state)
    {
        if (!__instance._interrupted && (WasTreated(__state.Heavy) || WasTreated(__state.Light) || WasTreated(__state.Fracture)))
            Impacts.ImpactStateService.Restored(__instance.HealthController, __instance.BodyPart);
    }
    private static bool WasTreated(ActiveHealthController.Effect effect) => effect != null && !effect.Existing;
    private static Exception SurgeryFinally(Exception __exception, MedCompletionState __state)
    { _surgery = __state.Previous; return __exception; }
    private static bool RestorePrefix(ActiveHealthController __instance, EBodyPart bodyPart, ref bool __result)
    {
        if (!BotSeverStateService.Severed(__instance.Player, bodyPart)) return true;
        if (!BotSeverStateService.Untreated(__instance.Player, bodyPart)) { __result = false; return false; }
        if (_surgery == null || !ReferenceEquals(_surgery.HealthController, __instance) || _surgery.BodyPart != bodyPart || _surgery._interrupted)
        { __result = false; return false; }
        // Let the native completion charge the item and publish the normal restored event.
        // Its skill-adjusted calculation cannot exceed one when the input maximum is one.
        __instance.BodyState[bodyPart].Health = new HealthValue(0f, 1f);
        return true;
    }
    private static void RestorePostfix(ActiveHealthController __instance, EBodyPart bodyPart, bool __result)
    {
        if (__result) Impacts.ImpactStateService.Restored(__instance, bodyPart);
        if (__result && _surgery != null && BotSeverStateService.Untreated(__instance.Player, bodyPart))
            BotSeverStateService.Treated(__instance, bodyPart);
    }
    private static void FullRestorePostfix(ActiveHealthController __instance, EBodyPart bodyPart, bool __result)
    { if (__result) Impacts.ImpactStateService.Restored(__instance, bodyPart); }
    private static bool FullRestorePrefix(ActiveHealthController __instance, EBodyPart bodyPart, ref bool __result)
    {
        if (!BotSeverStateService.Severed(__instance.Player, bodyPart)) return true;
        if (BotSeverStateService.Untreated(__instance.Player, bodyPart)) { __result = false; return false; }
        BotSeverStateService.ClampTreated(__instance, bodyPart); return true;
    }
    private static bool EffectPrefix(ActiveHealthController __instance, EBodyPart bodyPart) => !BotSeverStateService.Severed(__instance.Player, bodyPart);
    private static bool PronePrefix(MovementContext __instance, bool value) => value || !BotSeverStateService.Restricted(MovementPlayer(__instance));
    private static bool PosePrefix(MovementContext __instance, float poseLevel) => poseLevel <= 0f || !BotSeverStateService.Restricted(MovementPlayer(__instance));
    private static void JumpPostfix(MovementContext __instance, ref bool __result)
    { if (BotSeverStateService.Restricted(MovementPlayer(__instance))) __result = false; }
    private static bool VaultPrefix(MovementContext __instance, ref bool __result)
    {
        if (!BotSeverStateService.Restricted(MovementPlayer(__instance))) return true;
        __result = false; return false;
    }
    private static void CanPronePostfix(MovementContext __instance, ref bool __result)
    { if (BotSeverStateService.Restricted(MovementPlayer(__instance))) __result = true; }
    private static bool SprintPrefix(Player __instance, bool enable) => !enable || !BotSeverStateService.Restricted(__instance);
    private static bool TriggerPrefix(Player.FirearmController __instance, bool pressed) => !pressed ||
        !BotSeverStateService.FallingHands(__instance);
    private static void SurgicalTargetPostfix(BotSurgicalKit __instance)
    {
        Player player = (__instance._health as ActiveHealthController)?.Player;
        var actor = BotSeverStateService.Get(player);
        if (actor == null || actor.Policy.SeveredMask == 0) return;
        __instance._bodyPartToHeal = null; __instance.Damaged = false;
        for (int leg = 0; leg < 2; leg++) if (actor.Policy.Untreated(leg))
        { __instance._bodyPartToHeal = BotSeverStateService.Part(leg); __instance.Damaged = true; return; }
        foreach (EBodyPart part in __instance.parts)
            if (!BotSeverStateService.Severed(player, part) && __instance._health.IsBodyPartDestroyed(part))
            { __instance._bodyPartToHeal = part; __instance.Damaged = true; return; }
    }
}
