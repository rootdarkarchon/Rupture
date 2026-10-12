using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Dismemberment.Classes
{

internal static class MeshFrameTransform
{
	internal static void Apply(Mesh mesh, Matrix4x4 transform, Vector3[] vertices, Vector3[] normals, Vector4[] tangents)
	{
		bool mirrored = TransformVertices(transform, vertices, normals, tangents);
		bool hasNormals = normals != null && normals.Length == vertices.Length;
		bool hasTangents = tangents != null && tangents.Length == vertices.Length;
		mesh.vertices = vertices;
		if (hasNormals) mesh.normals = normals;
		if (hasTangents) mesh.tangents = tangents;
		if (mirrored)
			for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
			{
				if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
				int[] indices = mesh.GetTriangles(submesh);
				for (int index = 0; index + 2 < indices.Length; index += 3)
					(indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
				mesh.SetTriangles(indices, submesh, calculateBounds: false);
			}
		mesh.RecalculateBounds();
	}
	// Value-only frame conversion shared by main-thread materialization and geometry jobs.
	internal static bool TransformVertices(Matrix4x4 transform, IList<Vector3> vertices,
		IList<Vector3> normals, IList<Vector4> tangents)
	{
		float determinant = transform.determinant;
		if (float.IsNaN(determinant) || float.IsInfinity(determinant) || Math.Abs(determinant) < 1e-10f)
			throw new InvalidOperationException("Cannot materialize a mesh in a singular or non-finite frame.");
		Matrix4x4 normalTransform = transform.inverse.transpose;
		bool hasNormals = normals != null && normals.Count == vertices.Count;
		bool hasTangents = tangents != null && tangents.Count == vertices.Count;
		float handedness = determinant < 0f ? -1f : 1f;
		for (int index = 0; index < vertices.Count; index++)
		{
			vertices[index] = transform.MultiplyPoint3x4(vertices[index]);
			if (hasNormals) normals[index] = normalTransform.MultiplyVector(normals[index]).normalized;
			if (!hasTangents) continue;
			Vector4 source = tangents[index];
			Vector3 tangent = transform.MultiplyVector(new Vector3(source.x, source.y, source.z));
			if (hasNormals) tangent -= normals[index] * Vector3.Dot(normals[index], tangent);
			tangent.Normalize();
			tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, source.w * handedness);
		}
		return handedness < 0f;
	}
}
}
