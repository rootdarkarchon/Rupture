namespace Rupture.Dismemberment.Classes.BloodStreams;

internal enum BleedEmissionChange { None, Stop, Start }

internal static class BleedEmissionPolicy
{
    // Null activeEffect includes treatment, residual, paused, dead and disabled
    // states. The caller keeps the anatomical anchor independently of this decision.
    internal static BleedEmissionChange Evaluate(object activeEffect, object suppliedEffect,
        byte activeKind, byte suppliedKind, bool acknowledged, bool retryDue)
    {
        if (activeEffect == null) return suppliedKind == 0 ? BleedEmissionChange.None : BleedEmissionChange.Stop;
        if (ReferenceEquals(activeEffect, suppliedEffect) && activeKind == suppliedKind && (acknowledged || !retryDue))
            return BleedEmissionChange.None;
        return BleedEmissionChange.Start;
    }
}
