using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using UnityEngine;

namespace Rupture.Casings;

// Installed only after the graphical-peer gate in RupturePlugin.Awake.
internal static class CasingHooks
{
    private static Harmony _harmony;
    internal static CasingWorld World { get; private set; }

    internal static void Install()
    {
        if (MultiplayerRuntime.Current.IsHeadless) throw new InvalidOperationException("Casing hooks cannot run on headless.");
        _harmony = new Harmony("com.rupture.local-casings");
        try
        {
            // Resolve every exact target before installing any hook.
            var targets = new[]
            {
                Target(typeof(AmmoPoolObject), "SetUsed", typeof(bool)),
                Target(typeof(GameWorld), "SpawnShellInTheWorld", typeof(AmmoPoolObject).MakeByRefType()),
                Target(typeof(Shell), "ActivatePhysics", typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Vector3)),
                Target(typeof(AmmoPoolObject), "StartAutoDestroyCountDown", typeof(float)),
                Target(typeof(AmmoPoolObject), "ReturnToPool"),
                Target(typeof(AssetPoolObject), "ReturnToPool", typeof(AssetPoolObject), typeof(bool)),
                Target(typeof(EFT.Ballistics.Shot), "CheckCollision", typeof(Vector3), typeof(Vector3)),
            };
            World = new CasingWorld();
            Post(targets[0], nameof(Used)); Post(targets[1], nameof(Spawned)); Post(targets[2], nameof(Activated));
            Pre(targets[3], nameof(StartingCountdown)); Post(targets[3], nameof(Countdown)); Pre(targets[4], nameof(ReturnInstance)); Pre(targets[5], nameof(ReturnStatic));
            Post(targets[6], nameof(Trace));
            // Shot identity is reset through the shared calculator's Shoot boundary below.
            Post(Target(typeof(EFT.Ballistics.BallisticsCalculator), "Shoot", typeof(EFT.Ballistics.Shot)), nameof(Shot));
            Post(Target(typeof(EFT.Ballistics.BallisticsCalculator), "Shoot", typeof(EFT.Ballistics.Shot), typeof(Transform), typeof(bool)), nameof(Shot));
            Post(Target(typeof(Grenade), "Explosion", typeof(Grenade), typeof(IExplosiveItem), typeof(Vector3), typeof(string),
                typeof(EFT.Ballistics.IBallisticsCalculator), typeof(EFT.InventoryLogic.Item), typeof(Vector3), typeof(bool)), nameof(Explosion));
            Pre(Target(typeof(GameWorld), "Dispose"), nameof(Disposing));
            Rupture.Ragdolls.Classes.ShadowRagdollWorld.RegisterLocalSimulation(World.HasWork, World.BeforeStep, World.Reset, World.BackendFailed, World.ReceiveCompleted);
        }
        catch (Exception exception)
        {
            _harmony.UnpatchSelf(); World = null;
            RupturePlugin.Log.LogWarning("Persistent casings unavailable; native casing behavior retained: " + exception.Message);
        }
    }

    private static MethodInfo Target(Type type, string name, params Type[] args) =>
        AccessTools.DeclaredMethod(type, name, args) ?? throw new MissingMethodException(type.FullName, name);
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(CasingHooks), name);
    private static void Post(MethodInfo target, string name) => _harmony.Patch(target, postfix: Hook(name));
    private static void Pre(MethodInfo target, string name) => _harmony.Patch(target, prefix: Hook(name));
    private static void Used(AmmoPoolObject __instance, bool __0) => World.SetUsed(__instance, __0);
    private static void Spawned(GameWorld __instance, AmmoPoolObject __result) => World.Spawn(__instance, __result);
    private static void Activated(Shell __instance, Vector3 __0, Vector3 __1, Vector3 __2, Vector3 __3) => World.Activated(__instance, __0, __1, __2, __3);
    private static bool StartingCountdown(AmmoPoolObject __instance) => !World.IsOwned(__instance);
    private static void Countdown(AmmoPoolObject __instance, float __0) => World.Admit(__instance, __0);
    private static bool ReturnInstance(AmmoPoolObject __instance) => World.AllowReturn(__instance);
    private static bool ReturnStatic(AssetPoolObject __0) => World.AllowReturn(__0 as AmmoPoolObject);
    private static void Trace(EFT.Ballistics.Shot __instance, Vector3 __0, Vector3 __1, bool __result) => World.Trace(__instance, __0, __1, __result);
    private static void Shot(EFT.Ballistics.Shot __0) => World.NewShot(__0);
    private static void Explosion(IExplosiveItem grenadeItem, Vector3 grenadePosition, Vector3 shift) => World.Explosion(grenadeItem, grenadePosition + shift);
    private static void Disposing() => World.Reset();
    internal static void Shutdown() { World?.Reset(); _harmony?.UnpatchSelf(); World = null; }
}
