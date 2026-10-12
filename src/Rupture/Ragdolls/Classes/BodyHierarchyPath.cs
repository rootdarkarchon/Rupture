using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Ragdolls.Classes
{

// Shared by native bindings and external rigs: these strings also identify bodies
// in motion/force descriptors, so root and outside-hierarchy results are a contract.
internal static class BodyHierarchyPath
{
	internal static string RelativeTo(Transform root, Transform transform)
	{
		if (root == null || transform == null) return null;
		if (transform == root) return ".";
		Stack<string> names = new Stack<string>();
		Transform current = transform;
		while (current != null && current != root)
		{
			names.Push(current.name);
			current = current.parent;
		}
		return current == root ? string.Join("/", names.ToArray()) : null;
	}
}
}
