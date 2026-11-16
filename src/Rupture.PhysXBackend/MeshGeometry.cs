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

	/// <summary>
	/// SHA-256 over the cooking identity. Memoized per geometry (and shared with unscaled clones, which have an
	/// identical identity) because every body shape that references the mesh asks for it.
	/// </summary>
	internal string CacheKey(string eftBuild)
	{
		CacheKeyMemo memo = _keyMemo ??= new CacheKeyMemo();
		if (memo.Key != null && string.Equals(memo.EftBuild, eftBuild, StringComparison.Ordinal)) return memo.Key;
		// A signed-scale bake leaves RawHash unset: hashing the whole mesh is paid here, by whoever asks for the cache key (the owner thread
		// when it creates the actor), never by the main-thread snapshot that baked it. The arrays are immutable once baked.
		string source = $"PhysX=4.1.2;EFT={eftBuild};raw={RawHash ??= HashGeometry(Vertices, Indices)};layout={Layout};convex={Convex};scale={CookScaleIdentity};cooking={CookingOptions}";
		using SHA256 hash = SHA256.Create();
		memo.Key = Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(source)));
		memo.EftBuild = eftBuild;
		return memo.Key;
	}

	private sealed class CacheKeyMemo
	{
		internal string EftBuild;
		internal string Key;
	}

	private CacheKeyMemo _keyMemo;

	internal MeshGeometry BakeNegativeScale(Vector3 scale)
	{
		if (scale.x >= 0f && scale.y >= 0f && scale.z >= 0f)
		{
			MeshGeometry clone = new MeshGeometry
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
			// The identity string omits the native scale, so clones of an unbaked source share one memoized key.
			if (string.Equals(CookScaleIdentity, clone.CookScaleIdentity, StringComparison.Ordinal))
				clone._keyMemo = _keyMemo ??= new CacheKeyMemo();
			return clone;
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
			RawHash = null,
			Convex = Convex,
			CookingOptions = CookingOptions,
			NativeScale = Vector3.one,
			CookScaleIdentity = $"baked:{scale.x:R},{scale.y:R},{scale.z:R}"
		};
	}

	/// <summary>
	/// Digest of the little-endian vertex floats then indices. It is the <c>.pxc</c> cache identity, so the byte stream is
	/// fixed; it is fed through a bounded chunk instead of one buffer sized to the whole mesh.
	/// </summary>
	internal static string HashGeometry(Vector3[] vertices, uint[] indices)
	{
		using SHA256 hash = SHA256.Create();
		byte[] chunk = new byte[HashChunkBytes];
		int offset = 0;
		foreach (Vector3 vertex in vertices)
		{
			if (offset + 12 > chunk.Length)
			{
				hash.TransformBlock(chunk, 0, offset, null, 0);
				offset = 0;
			}
			WriteSingle(chunk, ref offset, vertex.x);
			WriteSingle(chunk, ref offset, vertex.y);
			WriteSingle(chunk, ref offset, vertex.z);
		}
		foreach (uint index in indices)
		{
			if (offset + 4 > chunk.Length)
			{
				hash.TransformBlock(chunk, 0, offset, null, 0);
				offset = 0;
			}
			chunk[offset++] = (byte)index;
			chunk[offset++] = (byte)(index >> 8);
			chunk[offset++] = (byte)(index >> 16);
			chunk[offset++] = (byte)(index >> 24);
		}
		hash.TransformFinalBlock(chunk, 0, offset);
		return Hex(hash.Hash);
	}

	private const int HashChunkBytes = 12 * 1024;
	private static readonly char[] HexDigits = "0123456789abcdef".ToCharArray();

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
		char[] characters = new char[bytes.Length * 2];
		for (int index = 0; index < bytes.Length; index++)
		{
			characters[index * 2] = HexDigits[bytes[index] >> 4];
			characters[index * 2 + 1] = HexDigits[bytes[index] & 15];
		}
		return new string(characters);
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

	/// <summary>
	/// Cache identity of the heightfield. Memoized because the raid-lifetime terrain cache hands the same instance to every
	/// consumer, and the owner asks for the key twice per creation (residency check, then attach).
	/// </summary>
	internal string CacheKey(string eftBuild)
	{
		KeyMemo memo = _keyMemo;
		if (memo != null && string.Equals(memo.EftBuild, eftBuild, StringComparison.Ordinal)) return memo.Key;
		string key = $"PhysX=4.1.2;EFT={eftBuild};heightfield=0x{NativeIdentity:X16};timestamp={Timestamp};rows={Rows};columns={Columns};stride={SampleStride};flags={HeightFieldFlags};threshold={ConvexEdgeThreshold:R}";
		_keyMemo = new KeyMemo(eftBuild, key);
		return key;
	}

	private sealed class KeyMemo
	{
		internal readonly string EftBuild;
		internal readonly string Key;
		internal KeyMemo(string eftBuild, string key) { EftBuild = eftBuild; Key = key; }
	}

	private KeyMemo _keyMemo;
}

internal static class MeshGeometryReader
{
	private const int WeaponRoundedBoxArcSegments = 4;

	internal static MeshGeometry CreateWeaponRoundedBox(Vector3 requestedSize)
	{
		Vector3 size = new Vector3(Mathf.Abs(requestedSize.x), Mathf.Abs(requestedSize.y), Mathf.Abs(requestedSize.z));
		if (!Finite(size) || size.x < 0.02f || size.y < 0.02f || size.z < 0.02f)
			throw new InvalidDataException($"Weapon rounded box has invalid size {requestedSize}.");

		Vector3 half = size * 0.5f;
		// Full half-thickness removes the narrow flat strips: top, bottom and ends
		// meet at a central ridge. Even arc subdivision also includes the 45-degree midpoint.
		float radius = Mathf.Min(half.x, Mathf.Min(half.y, half.z));
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
						float latitudeRadius = latitudeStep == WeaponRoundedBoxArcSegments ? 0f : Mathf.Cos(latitude);
						for (int longitudeStep = 0; longitudeStep <= WeaponRoundedBoxArcSegments; longitudeStep++)
						{
							float longitude = Mathf.PI * 0.5f * longitudeStep / WeaponRoundedBoxArcSegments;
							Vector3 direction = new Vector3(
								latitudeRadius * (longitudeStep == WeaponRoundedBoxArcSegments ? 0f : Mathf.Cos(longitude)) * xSign,
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
			Layout = $"weapon-rounded-box/v2/arc-segments:{WeaponRoundedBoxArcSegments}/full-thickness-ridge",
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
