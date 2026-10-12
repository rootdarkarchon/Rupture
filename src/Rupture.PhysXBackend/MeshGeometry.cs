using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Rupture.PhysXBackend;

internal sealed class MeshGeometry
{
	internal Vector3[] Vertices;
	internal uint[] Indices;
	internal string Layout;
	internal string RawHash;
	internal bool Convex;
	internal uint CookingOptions;
	internal Vector3 NativeScale = Vector3.one;
	internal string CookScaleIdentity = "runtime-mesh-scale";
	internal Bounds LocalBounds;

	internal string CacheKey(string eftBuild)
	{
		string source = $"PhysX=4.1.2;EFT={eftBuild};raw={RawHash};layout={Layout};convex={Convex};scale={CookScaleIdentity};cooking={CookingOptions}";
		using SHA256 hash = SHA256.Create();
		return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(source)));
	}

	internal MeshGeometry BakeNegativeScale(Vector3 scale)
	{
		if (scale.x >= 0f && scale.y >= 0f && scale.z >= 0f)
		{
			return new MeshGeometry
			{
				Vertices = Vertices,
				Indices = Indices,
				Layout = Layout,
				RawHash = RawHash,
				Convex = Convex,
				CookingOptions = CookingOptions,
				NativeScale = scale,
				CookScaleIdentity = "runtime-mesh-scale"
			};
		}

		Vector3[] vertices = new Vector3[Vertices.Length];
		for (int index = 0; index < vertices.Length; index++)
		{
			Vector3 source = Vertices[index];
			vertices[index] = new Vector3(source.x * scale.x, source.y * scale.y, source.z * scale.z);
		}
		uint[] indices = (uint[])Indices.Clone();
		if (scale.x * scale.y * scale.z < 0f)
		{
			for (int index = 0; index + 2 < indices.Length; index += 3)
			{
				uint temporary = indices[index + 1];
				indices[index + 1] = indices[index + 2];
				indices[index + 2] = temporary;
			}
		}
		return new MeshGeometry
		{
			Vertices = vertices,
			Indices = indices,
			Layout = Layout + "/baked-signed-scale",
			RawHash = HashGeometry(vertices, indices),
			Convex = Convex,
			CookingOptions = CookingOptions,
			NativeScale = Vector3.one,
			CookScaleIdentity = $"baked:{scale.x:R},{scale.y:R},{scale.z:R}"
		};
	}

	internal static string HashGeometry(Vector3[] vertices, uint[] indices)
	{
		using SHA256 hash = SHA256.Create();
		byte[] bytes = new byte[vertices.Length * 12 + indices.Length * 4];
		int offset = 0;
		foreach (Vector3 vertex in vertices)
		{
			WriteSingle(bytes, ref offset, vertex.x);
			WriteSingle(bytes, ref offset, vertex.y);
			WriteSingle(bytes, ref offset, vertex.z);
		}
		foreach (uint index in indices)
		{
			bytes[offset++] = (byte)index;
			bytes[offset++] = (byte)(index >> 8);
			bytes[offset++] = (byte)(index >> 16);
			bytes[offset++] = (byte)(index >> 24);
		}
		return Hex(hash.ComputeHash(bytes));
	}

	private static void WriteSingle(byte[] bytes, ref int offset, float value)
	{
		uint bits = new SingleBits { Value = value }.Bits;
		bytes[offset++] = (byte)bits;
		bytes[offset++] = (byte)(bits >> 8);
		bytes[offset++] = (byte)(bits >> 16);
		bytes[offset++] = (byte)(bits >> 24);
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct SingleBits
	{
		[FieldOffset(0)] internal float Value;
		[FieldOffset(0)] internal uint Bits;
	}

	private static string Hex(byte[] bytes)
	{
		StringBuilder builder = new StringBuilder(bytes.Length * 2);
		foreach (byte value in bytes) builder.Append(value.ToString("x2"));
		return builder.ToString();
	}
}

internal sealed class HeightFieldGeometry
{
	internal byte[] Samples;
	internal NativeApi.MaterialDesc[] Materials;
	internal uint Rows, Columns, SampleStride;
	internal ulong NativeIdentity;
	internal uint Timestamp;
	internal NativeApi.Pose WorldPose;
	internal float HeightScale, RowScale, ColumnScale, ConvexEdgeThreshold;
	internal uint HeightFieldFlags, GeometryFlags;

	internal string CacheKey(string eftBuild)
	{
		return $"PhysX=4.1.2;EFT={eftBuild};heightfield=0x{NativeIdentity:X16};timestamp={Timestamp};rows={Rows};columns={Columns};stride={SampleStride};flags={HeightFieldFlags};threshold={ConvexEdgeThreshold:R}";
	}
}

internal static class MeshGeometryReader
{
	private const int WeaponRoundedBoxArcSegments = 3;
	private const float WeaponRoundedBoxRadiusFractionOfHalfThickness = 0.8f;

	internal static MeshGeometry CreateWeaponRoundedBox(Vector3 requestedSize)
	{
		Vector3 size = new Vector3(Mathf.Abs(requestedSize.x), Mathf.Abs(requestedSize.y), Mathf.Abs(requestedSize.z));
		if (!Finite(size) || size.x < 0.02f || size.y < 0.02f || size.z < 0.02f)
			throw new InvalidDataException($"Weapon rounded box has invalid size {requestedSize}.");

		Vector3 half = size * 0.5f;
		float radius = Mathf.Min(half.x, Mathf.Min(half.y, half.z)) * WeaponRoundedBoxRadiusFractionOfHalfThickness;
		Vector3 inner = half - Vector3.one * radius;
		List<Vector3> vertices = new List<Vector3>();
		HashSet<Vector3> unique = new HashSet<Vector3>();
		for (int xSign = -1; xSign <= 1; xSign += 2)
		{
			for (int ySign = -1; ySign <= 1; ySign += 2)
			{
				for (int zSign = -1; zSign <= 1; zSign += 2)
				{
					Vector3 corner = new Vector3(inner.x * xSign, inner.y * ySign, inner.z * zSign);
					for (int latitudeStep = 0; latitudeStep <= WeaponRoundedBoxArcSegments; latitudeStep++)
					{
						float latitude = Mathf.PI * 0.5f * latitudeStep / WeaponRoundedBoxArcSegments;
						float latitudeRadius = Mathf.Cos(latitude);
						for (int longitudeStep = 0; longitudeStep <= WeaponRoundedBoxArcSegments; longitudeStep++)
						{
							float longitude = Mathf.PI * 0.5f * longitudeStep / WeaponRoundedBoxArcSegments;
							Vector3 direction = new Vector3(
								latitudeRadius * Mathf.Cos(longitude) * xSign,
								Mathf.Sin(latitude) * ySign,
								latitudeRadius * Mathf.Sin(longitude) * zSign);
							Vector3 vertex = corner + direction * radius;
							if (unique.Add(vertex)) vertices.Add(vertex);
						}
					}
				}
			}
		}

		Vector3[] vertexArray = vertices.ToArray();
		uint[] indices = Array.Empty<uint>();
		return new MeshGeometry
		{
			Vertices = vertexArray,
			Indices = indices,
			Layout = $"weapon-rounded-box/v1/arc-segments:{WeaponRoundedBoxArcSegments}/radius-fraction:{WeaponRoundedBoxRadiusFractionOfHalfThickness:R}",
			RawHash = MeshGeometry.HashGeometry(vertexArray, indices),
			Convex = true,
			CookingOptions = 0,
			NativeScale = Vector3.one,
			CookScaleIdentity = "procedural-actor-local",
			LocalBounds = new Bounds(Vector3.zero, size)
		};
	}

	private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
}
