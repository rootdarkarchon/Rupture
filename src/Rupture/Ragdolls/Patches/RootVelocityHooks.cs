using System;
using EFT.Interactive;
using HarmonyLib;
using Rupture.Ragdolls.Classes;
using UnityEngine;

namespace Rupture.Ragdolls.Patches;

internal static class RootVelocityHooks
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.root-velocity");
    internal static bool Available { get; private set; }
    internal static void Install()
    {
        try
        {
            Patches.Patch(AccessTools.Method(typeof(CorpseRagdoll), "Start", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(RootVelocityHooks), nameof(Before)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(RootVelocityHooks), nameof(After)));
            Available = true;
        }
        catch (Exception exception)
        {
            Patches.UnpatchSelf(); Available = false;
            RupturePlugin.Log.InitializationWarning("RPT velocity capture unavailable; retaining stock movement initialization. " + exception);
        }
    }
    internal static void Shutdown() { Patches.UnpatchSelf(); Available = false; }
    private static void Before(CorpseRagdoll __instance, Vector3 ____velocity, out RootVelocityExperiment.Record __state) =>
        __state = RootVelocityExperiment.BeforeStart(__instance, ____velocity);
    private static void After(Vector3 ____velocity, RootVelocityExperiment.Record __state, bool __runOriginal)
    {
        RootVelocityExperiment.AfterStart(__state, ____velocity, __runOriginal);
        AngularDriveExperiment.Capture(__state);
    }
}
