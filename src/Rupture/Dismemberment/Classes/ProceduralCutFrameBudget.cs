using System;
using System.Diagnostics;

namespace Rupture.Dismemberment.Classes;

// Reset once by the plugin before either pipeline runs. Charge only procedural
// stage CPU time, not the unrelated gameplay work between the two consumers.
internal static class ProceduralCutFrameBudget
{
    internal static double BudgetMilliseconds { get; private set; } = 2d;
    internal static long SpentTicks { get; private set; }
    private static long _limitTicks = Stopwatch.Frequency / 500;
    internal static bool CanRunStage => SpentTicks < _limitTicks;

    internal static void BeginFrame(float milliseconds)
    {
        BudgetMilliseconds = float.IsNaN(milliseconds) ? 2d : Math.Max(0.5d, Math.Min(8d, milliseconds));
        _limitTicks = Math.Max(1L, (long)(BudgetMilliseconds * Stopwatch.Frequency / 1000d));
        SpentTicks = 0;
    }

    internal static void RecordStage(long elapsedTicks) => SpentTicks += Math.Max(0L, elapsedTicks);
}
