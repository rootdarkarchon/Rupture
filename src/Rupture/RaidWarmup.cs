using System;
using System.Diagnostics;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.Fractures;
using Rupture.Dismemberment.Classes.HeadWounds;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture;

/// <summary>
/// Moves every one-time wound-effect cost out of the kill window. At the raid boundary it resolves the EFT wound
/// textures from the Effects prefab, then creates the limb-sever, blood-stream and head-wound materials and warms
/// the GPU program variants of the three bundle shaders, compiles the heavy first-use methods and runs the first
/// fracture on a throwaway rig (<see cref="FractureWarmup"/>), one step per frame, so the first kill and the first
/// broken limb find all of it ready.
/// Steps run after the heavy physics stages, only in deferrable frames, and are charged to <see cref="FrameWorkBudget"/>.
/// </summary>
internal static partial class RaidWarmup
{
    private enum Step
    {
        Idle,
        Textures,
        LimbSeverMaterial,
        BloodStreamMaterial,
        HeadWoundMaterial,
        ShaderVariants,
        PreJit,
        FractureFirstUse,
        Done
    }

    private readonly struct VariantSpec
    {
        internal readonly PassType Pass;
        internal readonly string[] Keywords;

        internal VariantSpec(PassType pass, params string[] keywords)
        {
            Pass = pass;
            Keywords = keywords;
        }
    }

    private sealed class VariantTarget
    {
        internal readonly string ShaderName;
        internal readonly VariantSpec[] Specs;
        /// <summary>The shader instance already warmed; survives raids because the bundle stays loaded.</summary>
        internal Shader Warmed;

        internal VariantTarget(string shaderName, VariantSpec[] specs)
        {
            ShaderName = shaderName;
            Specs = specs;
        }
    }

    /// <summary>How long to wait for the Effects singleton before falling back to the texture scan.</summary>
    private const float EffectsWaitSeconds = 30f;
    private const float ScanIntervalSeconds = 10f;
    private const int MaximumScans = 3;

    // EFT renders opaque surfaces deferred, so the surface shaders' Deferred and ShadowCaster passes are the ones a
    // corpse draws. Dynamic characters sample light probes, hence the LIGHTPROBE_SH variant. Entries the compiled
    // bundle does not contain are rejected by Unity and skipped; see WarmShader.
    private static readonly VariantSpec[] SurfaceVariants =
    {
        new VariantSpec(PassType.Deferred, "UNITY_HDR_ON"),
        new VariantSpec(PassType.Deferred, "UNITY_HDR_ON", "LIGHTPROBE_SH"),
        new VariantSpec(PassType.ShadowCaster, "SHADOWS_DEPTH")
    };

    private static readonly VariantSpec[] ParcelVariants =
    {
        new VariantSpec(PassType.Deferred, "UNITY_HDR_ON", "INSTANCING_ON"),
        new VariantSpec(PassType.Deferred, "UNITY_HDR_ON"),
        new VariantSpec(PassType.ShadowCaster, "SHADOWS_DEPTH", "INSTANCING_ON"),
        new VariantSpec(PassType.ShadowCaster, "SHADOWS_DEPTH")
    };

    private static readonly VariantTarget[] VariantTargets =
    {
        new VariantTarget(LimbSeverMaterialProvider.ShaderName, SurfaceVariants),
        new VariantTarget(HeadWoundMaterialProvider.ShaderName, SurfaceVariants),
        new VariantTarget(BloodStreamMaterialProvider.ShaderName, ParcelVariants)
    };

    private static Step _step;
    private static float _beganAt;
    private static float _nextScanAt;
    private static int _scans;
    private static bool _directAttempted;
    private static bool _headless;

    /// <summary>Call once at the raid-start boundary.</summary>
    internal static void Begin()
    {
        EftWoundTextures.Reset();
        _beganAt = Time.realtimeSinceStartup;
        _nextScanAt = 0f;
        _scans = 0;
        _directAttempted = false;
        // The headless host never loads the wound bundle or draws wound surfaces.
        _headless = MultiplayerRuntime.Current.IsHeadless;
        // The headless host skips the wound-effect steps but still pre-compiles the shared code paths.
        _step = _headless ? Step.PreJit : Step.Textures;
        BeginPreJit();
    }

    /// <summary>
    /// Call every Update frame while the raid world is active, after the heavy stages. Runs at most one step, and only
    /// when no heavy stage ran this frame; otherwise does nothing.
    /// </summary>
    internal static void Update()
    {
        Step step = _step;
        if (step == Step.Idle || step == Step.Done || !FrameWorkBudget.CanRunDeferrableStage) return;

        long started = Stopwatch.GetTimestamp();
        bool finished;
        try
        {
            finished = RunStep(step);
        }
        catch (Exception exception)
        {
            // Warmup is an optimisation: a failed step is reported once and skipped, never retried per frame.
            RupturePlugin.Log.LogError($"RPT raid warmup step {step} failed and was skipped: {exception}");
            finished = true;
        }
        long ticks = Stopwatch.GetTimestamp() - started;
        FrameWorkBudget.RecordStage(ticks);
        if (!finished) return;

        if (RupturePlugin.Log.IsDebugEnabled)
            RupturePlugin.Log.LogInfo($"RPT raid warmup step {step} done: ms={ticks * 1000d / Stopwatch.Frequency:F3}, textures={EftWoundTextures.Source}.");
        // A provider reset during the step restarts the material steps; keep that restart.
        if (_step == step) _step = step + 1;
    }

    /// <summary>
    /// Called by a material provider after it destroyed its material (raid-start sever reset or raid end). While a raid
    /// is warming or warmed, the material steps run again so the next kill still finds the materials ready.
    /// </summary>
    internal static void InvalidateMaterials()
    {
        Step step = _step;
        if (_headless || step == Step.Idle || step == Step.Textures || step == Step.LimbSeverMaterial) return;
        _step = Step.LimbSeverMaterial;
    }

    /// <summary>Raid end. Must run before the providers' own resets so they do not re-arm a finished raid.</summary>
    internal static void Reset()
    {
        _step = Step.Idle;
        FractureWarmup.Abort();
        EftWoundTextures.Reset();
    }

    private static bool RunStep(Step step)
    {
        switch (step)
        {
        case Step.Textures: return StepTextures();
        case Step.LimbSeverMaterial: return StepMaterial(LimbSeverMaterialProvider.TryGet(out _, out string limbFailure), limbFailure, "limb sever");
        case Step.BloodStreamMaterial: return StepMaterial(BloodStreamMaterialProvider.Get() != null, "the shader or textures are unavailable", "blood stream");
        case Step.HeadWoundMaterial: return StepMaterial(HeadWoundMaterialProvider.TryGet(out _, out string headFailure), headFailure, "head wound");
        case Step.ShaderVariants: return StepShaderVariants();
        case Step.PreJit: return StepPreJit();
        case Step.FractureFirstUse: return StepFractureFirstUse();
        default: return true;
        }
    }

    private static bool StepTextures()
    {
        if (EftWoundTextures.Ready) return true;

        if (!_directAttempted)
        {
            if (EftWoundTextures.TryResolveFromEffects())
            {
                // The prefab is complete once instantiated; a second pass over it cannot find more.
                _directAttempted = true;
                if (EftWoundTextures.Ready) return true;
            }
            else if (Time.realtimeSinceStartup - _beganAt < EffectsWaitSeconds)
            {
                return false;
            }
        }

        // Direct references are incomplete or the Effects singleton never appeared: one rate-limited scan, never in a
        // frame that already ran physics adoption.
        float now = Time.realtimeSinceStartup;
        if (FrameWorkBudget.HeavyStageRan || now < _nextScanAt) return false;
        _scans++;
        _nextScanAt = now + ScanIntervalSeconds;
        EftWoundTextures.ResolveFromScan();
        if (EftWoundTextures.Ready) return true;
        if (_scans < MaximumScans) return false;

        EftWoundTextures.MarkExhausted();
        RupturePlugin.Log.LogWarning("RPT raid warmup: the EFT wound textures (decal_flesh2, decal_flesh2_nrm, blood_splash_d, blood_splash_n) were not found; limb and head wound surfaces stay unavailable.");
        return true;
    }

    private static bool StepMaterial(bool ready, string failure, string label)
    {
        if (ready) return true;
        // The bundle may still be loading; anything else is permanent for this raid.
        if (WoundResources.IsLoading) return false;
        RupturePlugin.Log.LogWarning($"RPT raid warmup: the {label} material is unavailable: {failure}");
        return true;
    }

    /// <summary>
    /// Runs the first fracture's one-time work on a throwaway rig, one stage per call. Done once per process: compiled
    /// code and engine state outlive the raid, and a failure is deterministic, so it is reported with one warning and
    /// never attempted again. Skipped where no fracture is ever built (headless host, blood effects off).
    /// </summary>
    private static bool StepFractureFirstUse()
    {
        if (_headless || FractureWarmup.Warmed || FractureWarmup.Failed || RuptureConfiguration.EnableBloodEffects?.Value != true) return true;
        if (WoundResources.IsLoading) return false;
        if (!LimbSeverMaterialProvider.TryGet(out Material material, out string failure))
        {
            RupturePlugin.Log.LogWarning($"RPT raid warmup: the fracture first-use warmup is skipped, the limb sever material is unavailable: {failure}");
            return true;
        }
        try
        {
            return FractureWarmup.Step(material);
        }
        catch (Exception exception)
        {
            RupturePlugin.Log.LogWarning($"RPT raid warmup: the fracture first-use warmup failed and is skipped for this session: {exception}");
            return true;
        }
    }

    private static bool StepShaderVariants()
    {
        if (WoundResources.IsLoading) return false;
        for (int i = 0; i < VariantTargets.Length; i++)
        {
            VariantTarget target = VariantTargets[i];
            Shader shader = WoundResources.GetShader(target.ShaderName);
            if (shader == null || ReferenceEquals(target.Warmed, shader)) continue;
            WarmShader(shader, target);
            target.Warmed = shader;
            // One shader per frame: the next one continues on the next step call.
            return false;
        }
        return true;
    }

    /// <summary>
    /// Loads the GPU programs for the passes and keyword sets a corpse is drawn with. Unity throws
    /// <see cref="ArgumentException"/> from the variant constructor when the shader lacks a pass or keyword, which is an
    /// expected miss for a variant the bundle build did not include; those entries are counted and skipped.
    /// </summary>
    private static void WarmShader(Shader shader, VariantTarget target)
    {
        ShaderVariantCollection collection = new ShaderVariantCollection { hideFlags = HideFlags.HideAndDontSave };
        int added = 0;
        int rejected = 0;
        bool warmed = false;
        try
        {
            for (int i = 0; i < target.Specs.Length; i++)
            {
                VariantSpec spec = target.Specs[i];
                try
                {
                    if (collection.Add(new ShaderVariantCollection.ShaderVariant(shader, spec.Pass, spec.Keywords))) added++;
                    else rejected++;
                }
                catch (ArgumentException)
                {
                    rejected++;
                }
            }
            if (added > 0)
            {
                collection.WarmUp();
                warmed = collection.isWarmedUp;
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(collection);
        }
        if (RupturePlugin.Log.IsDebugEnabled)
            RupturePlugin.Log.LogInfo($"RPT raid warmup shader variants: shader={target.ShaderName}, added={added}, rejected={rejected}, warmed={warmed}.");
    }
}
