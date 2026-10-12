#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Dismemberment.Classes.BloodStreams
{
internal static partial class PulsatingBloodStreamSystem
{
    private static readonly StreamEmitter[] Emitters = new StreamEmitter[64];
    private static readonly StreamRingSample[] StreamSamples = new StreamRingSample[64 * 96];
    private static readonly StreamRingSample[] StreamSampleScratch = new StreamRingSample[96]; // Frozen oracle uses it.
    private static readonly RaycastHit[] CastHits = new RaycastHit[16];
    internal static EmitterScenarioResult RunScenario(bool moving, int fps, bool postmortem)
    {
        var root = new GameObject("emitter fixture");
        var result = new EmitterScenarioResult { moving = moving, fps = fps, postmortem = postmortem };
        for (int i = 0; i < 24; i++) Emitters[i] = new StreamEmitter
        {
            Active = true, Visible = true, Anchor = root.transform,
            ExpiresAt = postmortem ? 3f : 0f,
            Descriptor = new BloodStreamData { TriggerKind = postmortem ? BloodStreamTriggerKind.PostMortemStumpFlow : BloodStreamTriggerKind.HeavyWound, Seed = (uint)i + 1,
                Duration = postmortem ? 3f : 0f, PeakSpeed = 1.5f, Radius = 0.0052f, LocalDirection = Vector3.right },
            BaseFlow = new BaseFlowState { NextDecalAt = 100f }
        };
        ShadowRagdollWorld.ResetWork();
        double total = 0; long bytes = 0;
        try
        {
            for (int frame = 0; frame < fps * 8; frame++)
            {
                float now = frame / (float)fps;
                root.transform.position = new Vector3(moving ? now * 2f : 0f, 1.4f, 0f);
                long before = GC.GetAllocatedBytesForCurrentThread(); long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < 24; i++)
                {
                    var emitter = Emitters[i];
                    if (!postmortem && now >= emitter.NextPulseAt) { TriggerHeartbeat(emitter, now); emitter.NextPulseAt = now + 0.72f; }
                    if (postmortem && now >= emitter.ExpiresAt && !emitter.Draining) BeginDrain(emitter);
                    PruneStreamSamples(i, now);
                    uint previous = emitter.VisualIndex;
                    UpdateStreamFlow(i, emitter, now);
#if BASELINE_EMITTER
                    if (emitter.ImpactArrivalPending && now >= emitter.ImpactArrivalAt) emitter.ImpactArrivalPending = false;
#endif
                    if (emitter.BaseFlow.HasImpact && now >= emitter.BaseFlow.NextDecalAt) emitter.BaseFlow.NextDecalAt = now + 0.75f;
                    if (previous != emitter.VisualIndex)
                    {
                        var sample = GetStreamSample(i, emitter, emitter.SampleCount - 1);
                        result.emissions++;
                        result.trajectorySum += sample.Origin.x + sample.Velocity.x + sample.Velocity.y + sample.Radius;
                        Mix(ref result.trajectoryHash,sample.Origin.x); Mix(ref result.trajectoryHash,sample.Origin.y); Mix(ref result.trajectoryHash,sample.Origin.z);
                        Mix(ref result.trajectoryHash,sample.Velocity.x); Mix(ref result.trajectoryHash,sample.Velocity.y); Mix(ref result.trajectoryHash,sample.Velocity.z);
                        Mix(ref result.trajectoryHash,sample.Radius); Mix(ref result.trajectoryHash,sample.Strength); Mix(ref result.trajectoryHash,sample.StartedAt); Mix(ref result.trajectoryHash,sample.EndAt);
                        Mix(ref result.launchHash,sample.Origin.x); Mix(ref result.launchHash,sample.Origin.y); Mix(ref result.launchHash,sample.Origin.z);
                        Mix(ref result.launchHash,sample.Velocity.x); Mix(ref result.launchHash,sample.Velocity.y); Mix(ref result.launchHash,sample.Velocity.z);
                        Mix(ref result.launchHash,sample.Radius); Mix(ref result.launchHash,sample.Strength); Mix(ref result.launchHash,sample.StartedAt);
                        if (sample.EndAt <= sample.StartedAt || sample.EndAt > now + MaximumFlightSeconds + 0.001f)
                            throw new Exception("Invalid parcel lifetime");
                    }
                }
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (frame >= fps) { total += elapsed; bytes += GC.GetAllocatedBytesForCurrentThread() - before; }
            }
            result.meanFrameMs = total / (fps * 7); result.allocatedBytes = bytes;
            result.raycasts = ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodRaycasts);
            result.predictions = ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions);
            result.skipped = ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodFlowSkipped);
            return result;
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    internal static void VerifyHistory()
    {
        var random = new System.Random(17);
        for (int run = 0; run < 300; run++)
        {
            var e = Emitters[0] = new StreamEmitter { SampleStart = random.Next(96), SampleCount = random.Next(1, 97), NextSampleExpiryAt = 0 };
            var survivors = new List<uint>();
            for (int i = 0; i < e.SampleCount; i++)
            {
                float end = random.Next(10);
                StreamSamples[(e.SampleStart + i) % 96] = new StreamRingSample { Seed = (uint)i + 1, EndAt = end };
                if (end > 4f) survivors.Add((uint)i + 1);
            }
            PruneStreamSamples(0, 4f);
            if (e.SampleCount != survivors.Count) throw new Exception("History lost a survivor");
            for (int i = 0; i < survivors.Count; i++) if (GetStreamSample(0, e, i).Seed != survivors[i]) throw new Exception("Wrapped history reordered");
            ClearStreamSamples(0);
            if (e.SampleCount != 0) throw new Exception("Cleared history remains visible");
        }
    }
    private static void Mix(ref uint hash,float value) { unchecked { hash = (hash ^ (uint)value.GetHashCode()) * 16777619u; } }
}
}
#endif
