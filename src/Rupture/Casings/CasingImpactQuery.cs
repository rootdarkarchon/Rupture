using System;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.Casings;

internal readonly struct CasingImpactQuery
{
    private readonly CasingImpact _impact;
    private readonly Vector3 _segment;
    private readonly float _lengthSquared;
    internal CasingImpactQuery(CasingImpact impact)
    {
        _impact = impact; _segment = impact.End - impact.Start; _lengthSquared = _segment.sqrMagnitude;
    }
    internal bool MayReach(Vector3 position, float padding = .2f)
    {
        if (_impact.Kind != 0) return (position - _impact.Start).sqrMagnitude <= (_impact.Radius + padding) * (_impact.Radius + padding);
        float t = _lengthSquared < 1e-10f ? 0 : Mathf.Clamp01(Vector3.Dot(position - _impact.Start, _segment) / _lengthSquared);
        return (position - (_impact.Start + _segment * t)).sqrMagnitude < padding * padding;
    }
    // Query the existing spatial dictionary only when its conservative cell box
    // costs less than scanning occupied cells (including very large explosions).
    internal bool TryCellBounds(int occupied, out Vector3Int min, out Vector3Int max)
    {
        Vector3 low = _impact.Kind == 0 ? Vector3.Min(_impact.Start, _impact.End) : _impact.Start;
        Vector3 high = _impact.Kind == 0 ? Vector3.Max(_impact.Start, _impact.End) : _impact.Start;
        float padding = _impact.Kind == 0 ? 1f : _impact.Radius + 1f;
        low -= Vector3.one * padding; high += Vector3.one * padding;
        min = max = default;
        for (int axis = 0; axis < 3; axis++)
        {
            if (float.IsNaN(low[axis]) || float.IsNaN(high[axis]) || low[axis] < -2147483000d || high[axis] > 2147483000d) return false;
            min[axis] = (int)Math.Floor(low[axis]); max[axis] = (int)Math.Floor(high[axis]);
        }
        double volume = ((double)max.x - min.x + 1) * ((double)max.y - min.y + 1) * ((double)max.z - min.z + 1);
        return volume < occupied;
    }
}
