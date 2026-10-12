using UnityEngine;

namespace Rupture.Ragdolls.Classes
{

internal readonly struct ColliderContactState
{
	private readonly Collider _collider;
	private readonly int _layer;
	private readonly LayerMask _include, _exclude;
	private readonly bool _enabled, _trigger;
	internal ColliderContactState(Collider collider)
	{
		_collider = collider;
		_layer = collider.gameObject.layer;
		_include = collider.includeLayers;
		_exclude = collider.excludeLayers;
		_enabled = collider.enabled;
		_trigger = collider.isTrigger;
	}
	internal void Restore()
	{
		if (_collider == null) return;
		_collider.gameObject.layer = _layer;
		_collider.includeLayers = _include;
		_collider.excludeLayers = _exclude;
		_collider.isTrigger = _trigger;
		_collider.enabled = _enabled;
	}
}
}
