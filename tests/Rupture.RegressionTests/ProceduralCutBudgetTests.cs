using System;
using System.Diagnostics;
using System.Reflection;

internal static class ProceduralCutBudgetTests
{
    internal static void Run(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Type type = assembly.GetType("Rupture.Dismemberment.Classes.ProceduralCutFrameBudget", true);
        void Begin(float ms) => type.GetMethod("BeginFrame", flags).Invoke(null, new object[] { ms });
        void Charge(long ticks) => type.GetMethod("RecordStage", flags).Invoke(null, new object[] { ticks });
        bool Ready() => (bool)type.GetProperty("CanRunStage", flags).GetValue(null);
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
    }
}
