#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    internal enum BloodStreamTriggerKind { HeavyWound = 1, KillEntry, HeadKillPending, DismembermentStump, PostMortemStumpFlow, LightWound, LivingSeverStump }
    internal struct BloodStreamData { internal BloodStreamTriggerKind TriggerKind; internal uint Seed; internal float PeakSpeed, Radius, Duration; internal Vector3 LocalDirection, LocalPoint; }
    internal sealed class EFTHardSettings { internal static readonly EFTHardSettings Instance = new EFTHardSettings(); internal int ENVIRONMENT_HIT_MASK = Physics.DefaultRaycastLayers; }
    [Serializable] public sealed class EmitterScenarioResult { public bool moving, postmortem; public int fps, emissions; public uint trajectoryHash = 2166136261u, launchHash = 2166136261u; public double meanFrameMs, trajectorySum; public long raycasts, predictions, skipped, allocatedBytes; }
    public static class BloodEmitterRuntimeTests
    {
        [Serializable] private sealed class Report { public bool passed; public int postmortemDepositScenarios, referenceChecks; public EmitterScenarioResult[] current, baseline; }
        public static void RunBatch()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(10, -0.1f, 0); floor.transform.localScale = new Vector3(100, 0.2f, 100); Physics.SyncTransforms();
            var current = new List<EmitterScenarioResult>(); var baseline = new List<EmitterScenarioResult>();
            Type old = typeof(BloodEmitterRuntimeTests).Assembly.GetType("Rupture.Dismemberment.Classes.BloodStreams.BaselinePulsatingBloodStreamSystem");
            try
            {
                foreach (int fps in new[] { 60, 120 }) foreach (bool moving in new[] { false, true }) foreach (bool postmortem in new[] { false, true })
                {
                    var result = PulsatingBloodStreamSystem.RunScenario(moving, fps, postmortem); current.Add(result);
                    if (result.allocatedBytes != 0 || result.skipped == 0) throw new Exception("Emitter steady-state allocation/scheduling failed: " + JsonUtility.ToJson(result));
                    if (old != null)
                    {
                        var previous = (EmitterScenarioResult)old.GetMethod("RunScenario", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { moving, fps, postmortem }); baseline.Add(previous);
                        if (!postmortem && (result.emissions != previous.emissions || result.launchHash != previous.launchHash))
                            throw new Exception("Emission cadence/captured trajectories changed");
                        if (result.raycasts != previous.raycasts || result.predictions != previous.predictions || result.trajectoryHash != previous.trajectoryHash)
                            throw new Exception("Closest-hit optimization changed sampling or impact trajectories");
                    }
                }
                PulsatingBloodStreamSystem.VerifyHistory();
                PulsatingBloodStreamSystem.VerifyPostMortemDeposits();
                int referenceChecks = PulsatingBloodStreamSystem.VerifyReferenceParcels();
                BloodTrackingRuntimeTests.Run();
                LivingIndexRuntimeTests.Run();
                PulsatingBloodStreamSystem.VerifyClosestHits();
                File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json"), JsonUtility.ToJson(new Report { passed = true, postmortemDepositScenarios = 12, referenceChecks = referenceChecks, current = current.ToArray(), baseline = baseline.ToArray() }, true));
            }
            finally { UnityEngine.Object.DestroyImmediate(floor); }
        }
    }
}
namespace Systems.Effects
{
    public sealed class Effects
    {
        public readonly List<Vector3> Points = new List<Vector3>();
        public void EmitBleeding(Vector3 point, Vector3 normal) { Points.Add(point); }
        public void EmitBloodOnEnvironment(Vector3 point, Vector3 normal) { Points.Add(point); }
    }
}
#endif
