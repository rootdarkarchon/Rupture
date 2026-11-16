namespace Rupture.Ragdolls.External;

/// <summary>
/// Cumulative per-raid counters of the geometry work a corpse build can trigger. Callers snapshot
/// before and after a build and use <see cref="Since"/> for the delta.
/// </summary>
public readonly struct ExternalBuildCounters
{
	public ExternalBuildCounters(long shapeMapRefreshes, long targetedShapeMappings, long meshExtractions,
		long meshCooks, long meshDiskHits, long meshMemoryHits, long noShapeColliders)
	{
		ShapeMapRefreshes = shapeMapRefreshes;
		TargetedShapeMappings = targetedShapeMappings;
		MeshExtractions = meshExtractions;
		MeshCooks = meshCooks;
		MeshDiskHits = meshDiskHits;
		MeshMemoryHits = meshMemoryHits;
		NoShapeColliders = noShapeColliders;
	}

	/// <summary>Whole-scene Unity shape remaps (bootstrap / full-prebake / late-scene only).</summary>
	public long ShapeMapRefreshes { get; }

	/// <summary>Bounds-limited native overlap mappings for a single unmapped collider.</summary>
	public long TargetedShapeMappings { get; }

	/// <summary>Post-cook Unity PhysX meshes copied into managed memory.</summary>
	public long MeshExtractions { get; }

	/// <summary>Meshes cooked by the backend (no <c>.pxc</c> cache entry existed).</summary>
	public long MeshCooks { get; }

	/// <summary>Cooked meshes loaded from the <c>.pxc</c> disk cache.</summary>
	public long MeshDiskHits { get; }

	/// <summary>Mesh requests served by the in-memory cooked-mesh cache.</summary>
	public long MeshMemoryHits { get; }

	/// <summary>Colliders Unity simulates no native shape for, omitted from external bodies.</summary>
	public long NoShapeColliders { get; }

	/// <summary>Delta against an earlier snapshot; counters reset by a raid or scene change clamp to zero.</summary>
	public ExternalBuildCounters Since(ExternalBuildCounters earlier) => new ExternalBuildCounters(
		Delta(ShapeMapRefreshes, earlier.ShapeMapRefreshes), Delta(TargetedShapeMappings, earlier.TargetedShapeMappings),
		Delta(MeshExtractions, earlier.MeshExtractions), Delta(MeshCooks, earlier.MeshCooks),
		Delta(MeshDiskHits, earlier.MeshDiskHits), Delta(MeshMemoryHits, earlier.MeshMemoryHits),
		Delta(NoShapeColliders, earlier.NoShapeColliders));

	private static long Delta(long current, long earlier) => current > earlier ? current - earlier : 0L;
}

/// <summary>
/// Optional managed extension: older backends keep their runtime ABI and simply report no build counters.
/// </summary>
public interface IExternalBuildDiagnosticsRuntime
{
	ExternalBuildCounters ReadBuildCounters();
}
