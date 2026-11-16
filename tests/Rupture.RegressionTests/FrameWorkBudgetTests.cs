using System;
using System.Diagnostics;
using System.Reflection;

internal static class FrameWorkBudgetTests
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Type type = assembly.GetType("Rupture.FrameWorkBudget", true);
        void Begin(float ms) => type.GetMethod("BeginFrame", flags).Invoke(null, new object[] { ms });
        void Charge(long ticks) => type.GetMethod("RecordStage", flags).Invoke(null, new object[] { ticks });
        void ChargeHeavy(long ticks) => type.GetMethod("RecordHeavyStage", flags).Invoke(null, new object[] { ticks });
        bool Ready() => (bool)type.GetProperty("CanRunStage", flags).GetValue(null);
        bool Deferrable() => (bool)type.GetProperty("CanRunDeferrableStage", flags).GetValue(null);
        bool Heavy() => (bool)type.GetProperty("HeavyStageRan", flags).GetValue(null);
        double Budget() => (double)type.GetProperty("BudgetMilliseconds", flags).GetValue(null);
        void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }

        Begin(2f);
        Check(Ready(), "shared procedural allowance starts available");
        Charge(Stopwatch.Frequency / 1000);
        Check(Ready(), "head work leaves unused allowance available to limbs");
        Charge(Stopwatch.Frequency / 1000);
        Check(!Ready(), "head and limb costs exhaust one combined 2 ms allowance");
        Charge(Stopwatch.Frequency / 100);
        Check(!Ready(), "an atomic overrun prevents further stages from either consumer");
        Begin(2f);
        Check(Ready(), "next frame resets an overrun without carrying a backlog");
        Charge(-Stopwatch.Frequency);
        Check((long)type.GetProperty("SpentTicks", flags).GetValue(null) == 0, "negative elapsed time cannot increase the allowance");
        Begin(0f); Check(Budget() == 0.5d, "shared allowance retains the existing minimum");
        Begin(float.PositiveInfinity); Check(Budget() == 8d, "shared allowance retains the existing maximum");
        Begin(float.NaN); Check(Budget() == 2d, "invalid allowance falls back to 2 ms");
        Begin(2f);
        Check(Deferrable() && !Heavy(), "deferrable work may run in a frame with no heavy stage");
        ChargeHeavy(Stopwatch.Frequency / 1000 / 2);
        Check(!Heavy() && Deferrable(), "a cheap stage below half the allowance is charged but never blocks deferrable work");
        Check((long)type.GetProperty("SpentTicks", flags).GetValue(null) == Stopwatch.Frequency / 1000 / 2, "a cheap heavy-API stage is still charged to the shared allowance");
        ChargeHeavy(Stopwatch.Frequency / 1000);
        Check(Heavy() && Ready() && !Deferrable(), "a stage that used half the allowance blocks deferrable work even with allowance left");
        Begin(2f);
        Check(!Heavy() && Deferrable(), "next frame clears the heavy-stage latch");
        ChargeHeavy(Stopwatch.Frequency / 1000 - 1);
        Check(!Heavy(), "the threshold is half of the allowance, exclusive below");
        Begin(4f);
        ChargeHeavy(Stopwatch.Frequency / 1000);
        Check(!Heavy(), "the half-allowance threshold scales with the configured budget");
        ChargeHeavy(Stopwatch.Frequency / 500);
        Check(Heavy(), "a stage of half the configured 4 ms allowance is heavy");
        Begin(2f);
        Charge(Stopwatch.Frequency / 100);
        Check(!Deferrable(), "an exhausted allowance also blocks deferrable work");
    }
}
