using System.Collections.Generic;
using UnityEngine;
using Rupture.Ragdolls.External;

namespace Rupture.Ragdolls.Classes;

internal static partial class ShadowRagdollWorld
{
	internal readonly struct WeaponColliderDebugShape
	{
		internal readonly Transform Transform;
		internal readonly Vector3[] Edges;
		internal readonly Vector3 Size;
		internal readonly bool Rounded;
		internal WeaponColliderDebugShape(Transform transform, Vector3[] edges, Vector3 size, bool rounded)
		{ Transform = transform; Edges = edges; Size = size; Rounded = rounded; }
	}

	internal static void CollectWeaponColliderDebugShapes(Vector3 cameraPosition, List<WeaponColliderDebugShape> shapes)
	{
		shapes.Clear();
		foreach (ShadowRig rig in Rigs)
		{
			ShadowBody weapon = rig.WeaponBody;
			if (!rig.Committed || weapon?.NativeTransform == null || !weapon.NativeTransform.gameObject.activeInHierarchy ||
				weapon.Proxy is not ExternalShadowBodyProxy proxy || !proxy.ExternalBody.IsValid ||
				(!weapon.UsesCustomBox && !weapon.UsesCustomRoundedBox) ||
				(weapon.NativeTransform.position - cameraPosition).sqrMagnitude > 50f * 50f) continue;
			if (!weapon.DebugColliderAttempted)
			{
				weapon.DebugColliderAttempted = true;
				string failure = "The physics backend does not expose collider wireframes.";
				if (proxy.ExternalBody is not IExternalColliderDebugBody debug ||
					!debug.TryGetColliderWireframe(out weapon.DebugColliderEdges, out failure))
					RupturePlugin.Log.LogWarning($"Weapon collider wireframe unavailable for {weapon.NativeTransform.name}: {failure}");
			}
			if (weapon.DebugColliderEdges?.Length > 0)
				shapes.Add(new WeaponColliderDebugShape(weapon.NativeTransform, weapon.DebugColliderEdges,
					weapon.CustomBoxSize, weapon.UsesCustomRoundedBox));
		}
	}
}
