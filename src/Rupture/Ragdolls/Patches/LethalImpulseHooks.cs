using System;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using Rupture.Ragdolls.Classes;
using UnityEngine;

namespace Rupture.Ragdolls.Patches;

internal static class LethalImpulseHooks
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.lethal-impulse");
    [ThreadStatic] private static int _stepDepth;
    private struct StepState { internal bool Observed, Ran; }
    internal static bool Available { get; private set; }
    internal static void Install()
    {
        try
        {
            Patches.Patch(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
                new[] { typeof(Collider), typeof(Vector3), typeof(Vector3), typeof(float) }),
                prefix: Method(nameof(QueuePrefix)), postfix: Method(nameof(Queued)));
            Patches.Patch(AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
                new[] { typeof(Rigidbody), typeof(Vector3), typeof(Vector3), typeof(float) }),
                prefix: Method(nameof(StockPrefix)), postfix: Method(nameof(StockPostfix)));
            // Observe real default-scene simulate wrappers, not frame count or EFT's
            // update gate (which can be enabled without executing a simulation).
            Patches.Patch(AccessTools.Method(typeof(Physics), nameof(Physics.Simulate), new[] { typeof(float) }),
                prefix: Method(nameof(NativePrefix)), postfix: Method(nameof(NativePostfix)), finalizer: Method(nameof(NativeFinally)));
            Patches.Patch(AccessTools.Method(typeof(PhysicsScene), nameof(PhysicsScene.Simulate), new[] { typeof(float) }),
                prefix: Method(nameof(ScenePrefix)), postfix: Method(nameof(NativePostfix)), finalizer: Method(nameof(NativeFinally)));
            Available = true;
        }
        catch (Exception exception)
        {
            Patches.UnpatchSelf(); Available = false;
            RupturePlugin.Log.InitializationWarning("RPT lethal impulse correction unavailable; existing impulses unchanged. " + exception);
        }
    }
    internal static void Shutdown() { Patches.UnpatchSelf(); Available = false; }
    private static HarmonyMethod Method(string name) => new HarmonyMethod(typeof(LethalImpulseHooks), name);
    private static void QueuePrefix(CorpseRagdoll __instance, out LethalImpulseExperiment.QueueState __state) =>
        __state = LethalImpulseExperiment.BeforeStockQueue(__instance);
    private static void Queued(CorpseRagdoll __instance, Collider collider, Vector3 direction, Vector3 point, float thrust,
        LethalImpulseExperiment.QueueState __state, bool __runOriginal) =>
        LethalImpulseExperiment.StockQueued(__instance, collider, direction, point, thrust, __state, __runOriginal);
    private static bool StockPrefix(CorpseRagdoll __instance, Rigidbody rigidbody, Vector3 direction, Vector3 point, float thrust,
        out LethalImpulseExperiment.Record __state) =>
        LethalImpulseExperiment.BeforeStock(__instance, rigidbody, direction, point, thrust, out __state);
    private static void StockPostfix(LethalImpulseExperiment.Record __state, bool __runOriginal) =>
        LethalImpulseExperiment.AfterStock(__state, __runOriginal);
    private static void NativePrefix(float step, out StepState __state) =>
        Begin(LethalImpulseExperiment.Enabled && step > 0f && Physics.simulationMode == SimulationMode.Script, out __state);
    private static void ScenePrefix(PhysicsScene __instance, float step, out StepState __state) =>
        Begin(LethalImpulseExperiment.Enabled && step > 0f && __instance == Physics.defaultPhysicsScene && Physics.simulationMode == SimulationMode.Script, out __state);
    private static void Begin(bool eligible, out StepState state)
    {
        state = new StepState { Observed = eligible };
        if (eligible && _stepDepth++ == 0) LethalImpulseExperiment.BeforeNativeStep();
    }
    private static void NativePostfix(ref StepState __state, bool __runOriginal) { __state.Ran = __runOriginal; }
    private static Exception NativeFinally(Exception __exception, StepState __state)
    {
        if (__state.Observed && --_stepDepth == 0) LethalImpulseExperiment.AfterNativeStep(__exception == null && __state.Ran);
        return __exception;
    }
}
