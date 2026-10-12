using System;
using System.Threading;
using UnityEngine;

namespace Rupture.Impacts;

// Scope only the synchronous native explosion and its corpse postfix. Delayed
// fragments retain MasterOrigin but must not suppress a future explosion event.
internal readonly struct ExplosionEventScope
{
    private static long _nextSequence;
    [ThreadStatic] private static ExplosionEventScope _current;
    private readonly long _sequence;
    private readonly Vector3 _center;
    private readonly float _radius;

    private ExplosionEventScope(long sequence, Vector3 center, float radius = 0f) { _sequence = sequence; _center = center; _radius = radius; }

    internal static ExplosionEventScope Enter(Vector3 center)
    {
        ExplosionEventScope previous = _current;
        _current = new ExplosionEventScope(Interlocked.Increment(ref _nextSequence), center);
        return previous;
    }

    internal static void Restore(ExplosionEventScope previous) => _current = previous;

    internal static ExplosionEventScope EnterBlast(Vector3 center, float radius)
    {
        ExplosionEventScope previous = _current;
        // Enrich the grenade wrapper's scope instead of changing its identity:
        // the outer corpse postfix must still recognize its lethal blast.
        long sequence = previous._sequence != 0 && previous._radius == 0f &&
            (center - previous._center).sqrMagnitude <= .000001f ? previous._sequence : Interlocked.Increment(ref _nextSequence);
        _current = new ExplosionEventScope(sequence, center, radius);
        return previous;
    }
    internal static bool TryGetBlast(out long sequence, out Vector3 center, out float radius)
    {
        sequence = _current._sequence; center = _current._center; radius = _current._radius;
        return sequence != 0 && ExplosionSeverPolicy.Finite(center) && LimbImpactPolicy.Positive(radius);
    }

    internal static bool TryGetOrigin(out Vector3 origin)
    {
        origin = _current._center;
        return _current._sequence != 0 && ExplosionSeverPolicy.Finite(origin);
    }

    internal static long SequenceFor(Vector3 center) =>
        ExplosionSeverPolicy.Finite(center) && (center - _current._center).sqrMagnitude <= 0.000001f
            ? _current._sequence : 0;

    internal static bool IsCurrent(long sequence) => sequence != 0 && sequence == _current._sequence;
}
