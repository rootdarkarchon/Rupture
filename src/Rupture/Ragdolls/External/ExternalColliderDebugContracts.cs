using UnityEngine;

namespace Rupture.Ragdolls.External;

// Optional diagnostics extension; existing physics body implementations stay compatible.
public interface IExternalColliderDebugBody
{
	// Consecutive pairs are edges in actor space, including each shape's local pose.
	bool TryGetColliderWireframe(out Vector3[] edges, out string failure);
}
