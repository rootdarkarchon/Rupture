using System.Text;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Ragdolls.Classes
{

// Shared by native bindings and external rigs: these strings also identify bodies
// in motion/force descriptors, so root and outside-hierarchy results are a contract.
internal static class BodyHierarchyPath
{
	/// <summary>Main thread only (Transform access): the chain and builder are reused so each path costs its name reads and one result string.</summary>
	private static readonly List<Transform> Chain = new List<Transform>(16);
	private static readonly StringBuilder Builder = new StringBuilder(128);

	internal static string RelativeTo(Transform root, Transform transform)
	{
		if (root == null || transform == null) return null;
		if (transform == root) return ".";
		Chain.Clear();
		Transform current = transform;
		while (current != null && current != root)
		{
			Chain.Add(current);
			current = current.parent;
		}
		if (current != root)
		{
			Chain.Clear();
			return null;
		}
		Builder.Length = 0;
		for (int index = Chain.Count - 1; index >= 0; index--)
		{
			if (index != Chain.Count - 1) Builder.Append('/');
			Builder.Append(Chain[index].name);
		}
		Chain.Clear();
		return Builder.ToString();
	}
}
}
