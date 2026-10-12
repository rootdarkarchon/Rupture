#if UNITY_EDITOR
using System;
using UnityEngine;
using Comfort.Common;
using Systems.Effects;
namespace Rupture.Dismemberment.Classes.BloodStreams
{
internal static partial class PulsatingBloodStreamSystem
{
    private static readonly DecalCell[] DecalCells = new DecalCell[48];
    private static readonly DripImpact[] DripImpacts = new DripImpact[128];
    private static int _decalFrame, _decalsThisFrame, _decalsThisSecond, _decalCellWriteIndex;
    private static float _decalSecondStarted;
    private static bool _warnedMissingEffects;

    internal static void VerifyPostMortemDeposits()
    {
        var root = new GameObject("postmortem floor deposits");
        var effects = Singleton<Effects>.Instance = new Effects();
        try
        {
            foreach (float height in new[] { 0.01f, 0.12f, 1.4f })
            foreach (float duration in new[] { 1.5f, 3f })
            foreach (bool throttled in new[] { false, true })
            {
                for (int i = 0; i < Emitters.Length; i++) Emitters[i] = new StreamEmitter();
                Array.Clear(DecalCells, 0, DecalCells.Length); Array.Clear(DripImpacts, 0, DripImpacts.Length);
                _decalsThisSecond = _decalCellWriteIndex = 0; _decalSecondStarted = 0f;
                effects.Points.Clear();
                root.transform.position = new Vector3(0, height, 0);
                var e = Emitters[0] = new StreamEmitter { Active = true, Visible = true, Anchor = root.transform, ExpiresAt = duration,
                    Descriptor = new BloodStreamData { TriggerKind = BloodStreamTriggerKind.PostMortemStumpFlow,
                        Seed = 17, Duration = duration, PeakSpeed = 1.35f, Radius = 0.0105f, LocalDirection = Vector3.right } };
                for (int frame = 0; frame < 240; frame++)
                {
                    float now = frame / 60f;
                    if (!e.Draining && now >= duration) BeginDrain(e);
                    PruneStreamSamples(0, now);
                    if (e.Draining && now >= e.DrainEndsAt && e.SampleCount == 0) e.Active = false;
                    if (e.Active) UpdateStreamFlow(0, e, now);
                    _decalFrame = -1; // Editor batch mode has one Unity frame; expose each simulated frame boundary.
                    if (throttled && now < 0.5f) _decalsThisSecond = MaximumDismembermentDecalsPerSecond;
                    UpdateDecals(now);
                }
                if (effects.Points.Count == 0) throw new Exception($"No stump deposit: height={height}, duration={duration}, throttled={throttled}");
                foreach (Vector3 point in effects.Points)
                    if (Mathf.Abs(point.y) > 0.001f || point.x < -0.1f || point.x > 2f)
                        throw new Exception("Stump deposit missed the actual raycast floor");
                if (e.PulseIndex != 0) throw new Exception("Postmortem deposits incorrectly require heartbeat");
                int stopped = effects.Points.Count;
                _decalFrame = -1; UpdateDecals(10f);
                if (effects.Points.Count != stopped) throw new Exception("Retired stump keeps depositing");
            }
        }
        finally { Singleton<Effects>.Instance = null; UnityEngine.Object.DestroyImmediate(root); }
    }
}
}
#endif
