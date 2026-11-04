using System;

namespace Rupture.Ragdolls.Classes;

internal enum LethalImpulseSource { Baseline, StockOnly, RuptureOnly }

// No Unity state: select after both originals are held, or after a correlated
// stock descriptor demonstrably failed to arm. Never infer absence from a timeout.
internal sealed class LethalImpulsePolicy
{
    internal const float HoldSeconds = 0.25f;
    internal readonly LethalImpulseSource Source;
    internal readonly bool Physical;
    internal bool StockHeld, RuptureHeld, StockRejected, Admitted, Finished, Fallback;
    internal float HeldAt = float.NaN;
    internal int HeldFrame = -1;

    internal LethalImpulsePolicy(LethalImpulseSource source, bool physical)
    { Source = source; Physical = physical; }

    internal bool CanHold(bool correlated, bool localComparisonAllowed, bool externalAvailable) =>
        !Finished && Source != LethalImpulseSource.Baseline && correlated && localComparisonAllowed && externalAvailable;

    internal void Held(bool stock, float now, int frame = -1)
    {
        if (stock) StockHeld = true; else RuptureHeld = true;
        if (float.IsNaN(HeldAt)) { HeldAt = now; HeldFrame = frame; }
    }

    internal bool Ready => !Finished && Admitted && RuptureHeld &&
        (StockHeld || Source == LethalImpulseSource.RuptureOnly && StockRejected);
    // A cold handoff can consume >250 ms in the same/next main-thread frame.
    // Give both callbacks one complete frame opportunity, without extending the
    // normal timeout or allowing a genuinely stalled transaction to wait forever.
    internal bool Expired(float now, int frame = -1) => !Finished && !float.IsNaN(HeldAt) &&
        now - HeldAt >= HoldSeconds && (HeldFrame < 0 || frame < 0 || frame > HeldFrame + 1);
    internal void Finish(bool fallback) { Finished = true; Fallback = fallback; }
}
