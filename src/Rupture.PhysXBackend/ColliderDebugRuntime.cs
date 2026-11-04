using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.PhysXBackend;

public sealed partial class ExternalPhysXRuntime
{
	internal bool TryGetColliderWireframe(ExternalBody body, out Vector3[] edges, out string failure)
	{
		edges = null;
		failure = null;
		if (!IsRaidReady || !body.IsValid || !_bodies.TryGetValue(body.Handle, out ExternalBody owned) || owned != body)
		{
			failure = "The collider body is no longer owned by this scene.";
			return false;
		}
		try
		{
			var points = new List<Vector3>();
			// Shape handles repeat for compound colliders bound to one fitted shape.
			var shapes = new HashSet<ulong>(body.ShapeHandlesByCollider.Values);
			InvokeOwner(() =>
			{
				foreach (ulong shape in shapes) points.AddRange(_scene.GetShapeWireframe(shape));
			});
			edges = points.ToArray();
			return edges.Length > 0;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			return false;
		}
	}
}
