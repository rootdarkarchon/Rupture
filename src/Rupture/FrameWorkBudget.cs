using System;
using System.Diagnostics;

namespace Rupture;

// Reset once by the plugin before any consumer runs. Charge only Rupture stage
// CPU time, not the unrelated gameplay work between the consumers. Limb-cut and
// head-wound stages obey CanRunStage; deferrable work (headgear throws, blood-ring
// decal submissions, warmup steps) additionally yields to any heavy stage that
// already ran this frame, so it never stacks on a ragdoll build or environment
// capture. A stage is heavy only when it alone used at least half the allowance:
// cheap acquisitions and probes are charged but never block deferrable work.
internal static class FrameWorkBudget
{
    internal static double BudgetMilliseconds { get; private set; } = 2d;
    internal static long SpentTicks { get; private set; }
    internal static bool HeavyStageRan { get; private set; }
    private static long _limitTicks = Stopwatch.Frequency / 500;
    internal static bool CanRunStage => SpentTicks < _limitTicks;
    internal static bool CanRunDeferrableStage => CanRunStage && !HeavyStageRan;

    internal static void BeginFrame(float milliseconds)
    {
        BudgetMilliseconds = float.IsNaN(milliseconds) ? 2d : Math.Max(0.5d, Math.Min(8d, milliseconds));
        _limitTicks = Math.Max(1L, (long)(BudgetMilliseconds * Stopwatch.Frequency / 1000d));
        SpentTicks = 0;
        HeavyStageRan = false;
    }

    internal static void RecordStage(long elapsedTicks) => SpentTicks += Math.Max(0L, elapsedTicks);

    /// <summary>
    /// Charges a main-thread stage that may be expensive (ragdoll build, environment acquisition, inventory throw) and
    /// latches the frame as heavy only when this stage alone took at least half the allowance.
    /// </summary>
    internal static void RecordHeavyStage(long elapsedTicks)
    {
        RecordStage(elapsedTicks);
        if (elapsedTicks >= _limitTicks / 2) HeavyStageRan = true;
    }
}
