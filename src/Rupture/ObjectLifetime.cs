using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture
{

// Callbacks replace by identity key, so rebinding a corpse does not retain old state graphs.
internal sealed class ObjectLifetime : MonoBehaviour
{
	private readonly Dictionary<string, Action> _removed = new Dictionary<string, Action>();
	private bool _destroying;
	internal static void Bind(GameObject owner, string key, Action removed)
	{
		if (owner == null) throw new ArgumentNullException(nameof(owner));
		ObjectLifetime lifetime = owner.GetComponent<ObjectLifetime>() ?? owner.AddComponent<ObjectLifetime>();
		lifetime._removed[key] = removed;
	}

	private void OnDestroy()
	{
		_destroying = true;
		foreach (Action removed in _removed.Values)
			try { removed(); } catch (Exception exception) { RupturePlugin.Log.LogError($"Object cleanup failed: {exception}"); }
		_removed.Clear();
	}

	internal static void Unbind(GameObject owner, string key)
	{
		ObjectLifetime lifetime = owner != null ? owner.GetComponent<ObjectLifetime>() : null;
		if (lifetime != null && !lifetime._destroying) lifetime._removed.Remove(key);
	}
}
}
