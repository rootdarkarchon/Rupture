using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.PhysXBackend;

/// <summary>
/// Raid-lifetime cache of the Unity-cooked geometry read through <see cref="IMeshGeometrySource"/>. Mesh geometry is keyed by
/// mesh asset, convexity and cooking options. A terrain's heightfield is keyed by its <see cref="TerrainCollider"/> instance and
/// is kept until the raid resets or the collider is destroyed, so the roughly 10 ms read happens once per terrain instead of once per
/// frame that needs it.
/// </summary>
/// <remarks>
/// Native shape pointers live for one frame (see <c>UnityPhysXMeshGeometrySource.BeginMappingFrame</c>), but the extracted managed
/// copy does not: only the read itself has to map and extract in the same frame, and <see cref="IMeshGeometrySource.Read(TerrainCollider)"/>
/// already does. A cached heightfield is trusted while all of these hold: the collider still references the same
/// <see cref="TerrainData"/> instance, its <see cref="TerrainData.heightmapResolution"/> is unchanged,
/// <see cref="TerrainCallbacks.heightmapChanged"/> has not reported an edit of that data since the read, and the collider's
/// transform still has the world position and rotation it had at the read (the cached <see cref="HeightFieldGeometry.WorldPose"/>
/// places the shape, so a moved terrain is read again).
/// Limitation: Unity reports no change signal for terrain holes (<c>TerrainData.SetHoles</c>) and none for an edit made through
/// native code; such a change is only picked up after a raid reset. EFT raid terrains are authored data and are not edited at runtime.
/// Entries of destroyed colliders are dropped by <see cref="PruneDestroyedTerrains"/> (terrain capture rescans and every cache miss).
/// Each entry keeps its <see cref="HeightFieldGeometry.Samples"/> next to the copy the native scene creates from it: the owner
/// thread reads the same shared instance whenever a scene does not hold the heightfield yet (the first actor of the raid, and the
/// separately staged scene of a full prebake), so releasing the managed copy after the first native creation would be a cross-thread
/// write that a later scene creation can fault on. A promotion, a scale rebuild or a re-capture of a terrain actor snapshots the collider again and
/// needs the entry too, so it stays after the permanent terrain actor exists (see ExternalPhysXRuntime.TerrainCapture). Only a few terrains exist per map, so the duplicate is accepted.
/// Main thread only.
/// </remarks>
internal static class MeshGeometryRepository
{
	private static readonly Dictionary<MeshKey, MeshGeometry> Cache = new Dictionary<MeshKey, MeshGeometry>();
	private static readonly Dictionary<int, HeightFieldCacheEntry> HeightFields = new Dictionary<int, HeightFieldCacheEntry>();
	/// <summary>Heightmap edit count per <see cref="TerrainData"/> instance id, bumped by <see cref="TerrainCallbacks.heightmapChanged"/>.</summary>
	private static readonly Dictionary<int, int> TerrainDataEdits = new Dictionary<int, int>();
	private static readonly TerrainCallbacks.HeightmapChangedCallback HeightmapChangedHandler = OnHeightmapChanged;
	private static IMeshGeometrySource _source;
	private static bool _subscribed;
	private static readonly List<int> PruneScratch = new List<int>();
	private static long _heightFieldReads, _heightFieldHits, _heightFieldInvalidations, _heightmapEditEvents, _heightFieldBytes, _heightFieldPruned;

	internal static void Configure(IMeshGeometrySource source)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		Clear();
		TerrainCallbacks.heightmapChanged += HeightmapChangedHandler;
		_subscribed = true;
	}

	internal static void Clear()
	{
		if (_subscribed)
		{
			TerrainCallbacks.heightmapChanged -= HeightmapChangedHandler;
			_subscribed = false;
		}
		Cache.Clear();
		HeightFields.Clear();
		TerrainDataEdits.Clear();
		_heightFieldReads = _heightFieldHits = _heightFieldInvalidations = _heightmapEditEvents = _heightFieldBytes = _heightFieldPruned = 0L;
	}

	/// <summary>Terrain cache counters for the status line.</summary>
	internal static string Describe() =>
		$"terrainCache=entries:{HeightFields.Count}/reads:{_heightFieldReads}/hits:{_heightFieldHits}/invalidations:{_heightFieldInvalidations}/pruned:{_heightFieldPruned}/heightmapEdits:{_heightmapEditEvents}/bytes:{_heightFieldBytes}";

	/// <summary>Whether reading the collider's mesh needs no native shape: its Unity-cooked geometry is already in memory.</summary>
	internal static bool IsCached(MeshCollider collider)
	{
		Mesh mesh = collider.sharedMesh;
		return mesh != null && Cache.ContainsKey(new MeshKey(mesh.GetInstanceID(), collider.convex, (uint)collider.cookingOptions));
	}

	/// <summary>Whether the terrain's heightfield was read earlier in this raid and is still current, so reading it needs no native shape.</summary>
	internal static bool IsCached(TerrainCollider collider) =>
		HeightFields.TryGetValue(collider.GetInstanceID(), out HeightFieldCacheEntry cached) && IsCurrent(cached, collider);

	internal static MeshGeometry Get(MeshCollider collider)
	{
		if (_source == null) throw new InvalidOperationException("The external mesh geometry source is not configured.");
		Mesh mesh = collider.sharedMesh;
		MeshKey key = new MeshKey(mesh.GetInstanceID(), collider.convex, (uint)collider.cookingOptions);
		if (!Cache.TryGetValue(key, out MeshGeometry geometry))
		{
			geometry = _source.Read(collider);
			Cache.Add(key, geometry);
		}
		return geometry;
	}

	internal static HeightFieldGeometry Get(TerrainCollider collider)
	{
		if (_source == null) throw new InvalidOperationException("The external Unity PhysX geometry source is not configured.");
		int colliderId = collider.GetInstanceID();
		bool replaced = HeightFields.TryGetValue(colliderId, out HeightFieldCacheEntry cached);
		if (replaced && IsCurrent(cached, collider))
		{
			_heightFieldHits++;
			return cached.Geometry;
		}
		HeightFieldGeometry geometry = _source.Read(collider);
		TerrainData data = collider.terrainData;
		int dataId = data.GetInstanceID();
		TerrainDataEdits.TryGetValue(dataId, out int edits);
		if (replaced)
		{
			_heightFieldInvalidations++;
			_heightFieldBytes -= cached.Geometry.Samples.Length;
		}
		collider.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
		HeightFields[colliderId] = new HeightFieldCacheEntry(collider, geometry, dataId, data.heightmapResolution, edits, position, rotation);
		_heightFieldReads++;
		_heightFieldBytes += geometry.Samples.Length;
		PruneDestroyedTerrains();
		return geometry;
	}

	/// <summary>Drops the cached heightfields whose <see cref="TerrainCollider"/> was destroyed, for example by an unloaded late scene.</summary>
	internal static void PruneDestroyedTerrains()
	{
		PruneScratch.Clear();
		foreach (KeyValuePair<int, HeightFieldCacheEntry> pair in HeightFields)
			if (pair.Value.Collider == null) PruneScratch.Add(pair.Key);
		for (int index = 0; index < PruneScratch.Count; index++)
		{
			int colliderId = PruneScratch[index];
			_heightFieldBytes -= HeightFields[colliderId].Geometry.Samples.Length;
			HeightFields.Remove(colliderId);
			_heightFieldPruned++;
		}
		PruneScratch.Clear();
	}

	private static bool IsCurrent(HeightFieldCacheEntry entry, TerrainCollider collider)
	{
		TerrainData data = collider.terrainData;
		if (data == null || data.GetInstanceID() != entry.TerrainDataId || data.heightmapResolution != entry.HeightmapResolution) return false;
		TerrainDataEdits.TryGetValue(entry.TerrainDataId, out int edits);
		if (edits != entry.Edits) return false;
		collider.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
		return position == entry.Position && rotation == entry.Rotation;
	}

	private static void OnHeightmapChanged(Terrain terrain, RectInt heightRegion, bool synched)
	{
		TerrainData data = terrain != null ? terrain.terrainData : null;
		if (data == null) return;
		int dataId = data.GetInstanceID();
		TerrainDataEdits.TryGetValue(dataId, out int edits);
		TerrainDataEdits[dataId] = edits + 1;
		_heightmapEditEvents++;
	}

	private readonly struct HeightFieldCacheEntry
	{
		internal readonly TerrainCollider Collider;
		internal readonly HeightFieldGeometry Geometry;
		internal readonly int TerrainDataId;
		internal readonly int HeightmapResolution;
		internal readonly int Edits;
		internal readonly Vector3 Position;
		internal readonly Quaternion Rotation;

		internal HeightFieldCacheEntry(TerrainCollider collider, HeightFieldGeometry geometry, int terrainDataId, int heightmapResolution, int edits,
			Vector3 position, Quaternion rotation)
		{
			Collider = collider;
			Geometry = geometry;
			TerrainDataId = terrainDataId;
			HeightmapResolution = heightmapResolution;
			Edits = edits;
			Position = position;
			Rotation = rotation;
		}
	}

	/// <summary>Cache identity of a Unity-cooked mesh; the single configured source is implicit because the cache is cleared on Configure.</summary>
	private readonly struct MeshKey : IEquatable<MeshKey>
	{
		private readonly int _meshId;
		private readonly uint _cookingOptions;
		private readonly bool _convex;
		internal MeshKey(int meshId, bool convex, uint cookingOptions) { _meshId = meshId; _convex = convex; _cookingOptions = cookingOptions; }
		public bool Equals(MeshKey other) => _meshId == other._meshId && _convex == other._convex && _cookingOptions == other._cookingOptions;
		public override bool Equals(object value) => value is MeshKey other && Equals(other);
		public override int GetHashCode() => (_meshId * 397 ^ (int)_cookingOptions) * 31 + (_convex ? 1 : 0);
	}
}
