using System;
using System.Collections.Generic;

namespace Rupture;

// Logical effect lifetimes survive visual-pool eviction and headless rendering.
internal sealed class EffectLifetimes<T>
{
	private readonly Dictionary<uint, T> _active = new Dictionary<uint, T>();
	private readonly List<KeyValuePair<uint, T>> _snapshot = new List<KeyValuePair<uint, T>>();
	internal void Start(uint id, T effect) => _active[id] = effect;
	internal void Remove(uint id) => _active.Remove(id);
	internal bool TryGet(uint id, out T effect) => _active.TryGetValue(id, out effect);
	internal void Clear() { _active.Clear(); _snapshot.Clear(); }
	internal Dictionary<uint, T>.ValueCollection Values => _active.Values;
	internal void StopWhere(Func<T, bool> shouldStop, Action<T> stopped)
	{
		_snapshot.Clear();
		_snapshot.AddRange(_active);
		foreach (KeyValuePair<uint, T> item in _snapshot)
		{
			if (!shouldStop(item.Value) || !_active.Remove(item.Key)) continue;
			// Remove first: reliable stop echoes or repeated updates cannot publish twice.
			stopped(item.Value);
		}
		_snapshot.Clear();
	}
}
