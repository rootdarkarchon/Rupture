using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.PhysXBackend;

// Query-local memoization avoids stale classification after reparenting or
// component changes. Unity eligibility/geometry checks remain at the caller.
internal sealed class EnvironmentHierarchyFilter
{
    private readonly HashSet<Transform> _allowed = new HashSet<Transform>();
    private readonly HashSet<Transform> _excluded = new HashSet<Transform>();
    private readonly List<Transform> _path = new List<Transform>(16);
    private readonly List<Component> _components = new List<Component>(16);
    private bool _query;
    internal long Visits { get; private set; }
    internal long Hits { get; private set; }
    internal void BeginQuery() { Clear(); _query = true; }
    internal void EndQuery() { Clear(); _query = false; }
    internal void Reset() { EndQuery(); Visits = Hits = 0; }
    private void Clear() { _allowed.Clear(); _excluded.Clear(); _path.Clear(); _components.Clear(); }
    internal bool Allows(Transform transform)
    {
        _path.Clear(); bool eligible = true;
        for (Transform current = transform; current != null; current = current.parent)
        {
            if (_query)
            {
                if (_allowed.Contains(current)) { Hits++; break; }
                if (_excluded.Contains(current)) { Hits++; eligible = false; break; }
                Visits++; _path.Add(current);
            }
            current.GetComponents(_components);
            foreach (Component component in _components)
            {
                string type = component?.GetType().FullName;
                if (component is ParticleSystem || type == "EFT.Player" || type == "EFT.Interactive.Corpse" || type?.EndsWith(".ObservedCorpse", StringComparison.Ordinal) == true)
                { eligible = false; break; }
            }
            if (!eligible) break;
        }
        if (_query) foreach (Transform visited in _path) (eligible ? _allowed : _excluded).Add(visited);
        _path.Clear(); _components.Clear(); return eligible;
    }
}
