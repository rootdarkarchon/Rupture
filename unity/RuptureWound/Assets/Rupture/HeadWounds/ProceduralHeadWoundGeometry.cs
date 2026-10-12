using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.HeadWounds
{

internal readonly struct ProceduralWoundDescriptor
{
	internal readonly Vector3 Entry;
	internal readonly Vector3 Exit;
	internal readonly float EntryRadius;
	internal readonly float ExitRadius;
	internal readonly uint Seed;
	internal readonly bool ThroughShot;
	internal readonly bool ExpandAdjacency;
	internal readonly bool GenerateExitLip;

	internal ProceduralWoundDescriptor(Vector3 entry, Vector3 exit, float entryRadius, float exitRadius,
		uint seed, bool throughShot = true, bool expandAdjacency = false, bool generateExitLip = true)
	{
		Entry = entry;
		Exit = exit;
		EntryRadius = entryRadius;
		ExitRadius = exitRadius;
		Seed = seed == 0 ? 1u : seed;
		ThroughShot = throughShot;
		ExpandAdjacency = expandAdjacency;
		GenerateExitLip = throughShot && generateExitLip;
	}
}

internal readonly struct ProceduralWoundMetrics
{
	internal readonly int SourceTriangleCount;
	internal readonly int CandidateTriangleCount;
	internal readonly int IntersectedSourceTriangleCount;
	internal readonly int ClippedTriangleCount;
	internal readonly int RemovedTriangleCount;
	internal readonly int ResultHeadTriangleCount;
	internal readonly int GeneratedHeadVertexCount;
	internal readonly float EntryOpeningCoverageRadius;
	internal readonly float ExitOpeningCoverageRadius;
	internal readonly float EntryRimOuterRadius;
	internal readonly float ExitRimOuterRadius;
	internal readonly float MinimumRimCoverageMargin;
	internal readonly int RimVertexCount;
	internal readonly int RimTriangleCount;
	internal readonly bool EntryLipStitched;
	internal readonly bool ExitLipStitched;
	internal readonly int CavityVertexCount;
	internal readonly int CavityTriangleCount;
	internal readonly float MinimumEntryRimFacingDot;
	internal readonly float MinimumExitRimFacingDot;
	internal readonly int EntryRimSurfaceHitCount;
	internal readonly int ExitRimSurfaceHitCount;
	internal readonly int BoundaryComponentCount;
	internal readonly int OpenBoundaryComponentCount;
	internal readonly int SolidCanalSpanCount;
	internal readonly int EntryLipCount;
	internal readonly int IntermediateLipCount;
	internal readonly int ExitFlapCount;
	internal readonly double TriangleClassificationMilliseconds;
	internal readonly double CavityGenerationMilliseconds;

	internal ProceduralWoundMetrics(int sourceTriangleCount, int candidateTriangleCount,
		int intersectedSourceTriangleCount, int clippedTriangleCount, int removedTriangleCount,
		int resultHeadTriangleCount, int generatedHeadVertexCount,
		float entryOpeningCoverageRadius, float exitOpeningCoverageRadius,
		float entryRimOuterRadius, float exitRimOuterRadius, float minimumRimCoverageMargin,
		int rimVertexCount, int rimTriangleCount, bool entryLipStitched, bool exitLipStitched,
		int cavityVertexCount, int cavityTriangleCount,
		float minimumEntryRimFacingDot, float minimumExitRimFacingDot,
		int entryRimSurfaceHitCount, int exitRimSurfaceHitCount,
		int boundaryComponentCount, int openBoundaryComponentCount, int solidCanalSpanCount,
		int entryLipCount, int intermediateLipCount, int exitFlapCount,
		double triangleClassificationMilliseconds, double cavityGenerationMilliseconds)
	{
		SourceTriangleCount = sourceTriangleCount;
		CandidateTriangleCount = candidateTriangleCount;
		IntersectedSourceTriangleCount = intersectedSourceTriangleCount;
		ClippedTriangleCount = clippedTriangleCount;
		RemovedTriangleCount = removedTriangleCount;
		ResultHeadTriangleCount = resultHeadTriangleCount;
		GeneratedHeadVertexCount = generatedHeadVertexCount;
		EntryOpeningCoverageRadius = entryOpeningCoverageRadius;
		ExitOpeningCoverageRadius = exitOpeningCoverageRadius;
		EntryRimOuterRadius = entryRimOuterRadius;
		ExitRimOuterRadius = exitRimOuterRadius;
		MinimumRimCoverageMargin = minimumRimCoverageMargin;
		RimVertexCount = rimVertexCount;
		RimTriangleCount = rimTriangleCount;
		EntryLipStitched = entryLipStitched;
		ExitLipStitched = exitLipStitched;
		CavityVertexCount = cavityVertexCount;
		CavityTriangleCount = cavityTriangleCount;
		MinimumEntryRimFacingDot = minimumEntryRimFacingDot;
		MinimumExitRimFacingDot = minimumExitRimFacingDot;
		EntryRimSurfaceHitCount = entryRimSurfaceHitCount;
		ExitRimSurfaceHitCount = exitRimSurfaceHitCount;
		BoundaryComponentCount = boundaryComponentCount;
		OpenBoundaryComponentCount = openBoundaryComponentCount;
		SolidCanalSpanCount = solidCanalSpanCount;
		EntryLipCount = entryLipCount;
		IntermediateLipCount = intermediateLipCount;
		ExitFlapCount = exitFlapCount;
		TriangleClassificationMilliseconds = triangleClassificationMilliseconds;
		CavityGenerationMilliseconds = cavityGenerationMilliseconds;
	}
}

internal readonly struct CutBoundaryEdge
{
	internal readonly int A;
	internal readonly int B;
	internal readonly int Submesh;

	internal CutBoundaryEdge(int a, int b, int submesh)
	{
		A = a;
		B = b;
		Submesh = submesh;
	}
}

internal readonly struct BoundaryVertexKey : IEquatable<BoundaryVertexKey>
{
	internal readonly int X;
	internal readonly int Y;
	internal readonly int Z;

	internal BoundaryVertexKey(int x, int y, int z)
	{
		X = x;
		Y = y;
		Z = z;
	}

	public bool Equals(BoundaryVertexKey other) => X == other.X && Y == other.Y && Z == other.Z;
	public override bool Equals(object obj) => obj is BoundaryVertexKey other && Equals(other);
	public override int GetHashCode()
	{
		unchecked
		{
			int hash = X;
			hash = hash * 397 ^ Y;
			return hash * 397 ^ Z;
		}
	}
}

internal sealed class WoundBoundaryComponent
{
	internal readonly List<int> Vertices = new List<int>(64);
	internal readonly List<int> EdgeSubmeshes = new List<int>(64);
	internal readonly List<int> AngularVertices = new List<int>(64);
	internal bool Closed;
	internal float MinimumAxial;
	internal float MaximumAxial;
	internal float MeanAxial;
	internal float MeanFacing;
	internal float MaximumRadius;
	internal int PrimarySubmesh;

	internal void Clear()
	{
		Vertices.Clear();
		EdgeSubmeshes.Clear();
		AngularVertices.Clear();
		Closed = false;
		MinimumAxial = float.MaxValue;
		MaximumAxial = float.MinValue;
		MeanAxial = 0f;
		MeanFacing = 0f;
		MaximumRadius = 0f;
		PrimarySubmesh = 0;
	}
}

internal readonly struct WoundCanalBoundaryAttachment
{
	internal readonly WoundBoundaryComponent Component;
	internal readonly int RingStart;
	internal readonly int SegmentCount;

	internal WoundCanalBoundaryAttachment(WoundBoundaryComponent component, int ringStart, int segmentCount)
	{
		Component = component;
		RingStart = ringStart;
		SegmentCount = segmentCount;
	}
}

internal readonly struct RimSurfaceCandidate
{
	internal readonly int A;
	internal readonly int B;
	internal readonly int C;
	internal readonly float MinimumU;
	internal readonly float MaximumU;
	internal readonly float MinimumV;
	internal readonly float MaximumV;

	internal RimSurfaceCandidate(int a, int b, int c, float minimumU, float maximumU,
		float minimumV, float maximumV)
	{
		A = a;
		B = b;
		C = c;
		MinimumU = minimumU;
		MaximumU = maximumU;
		MinimumV = minimumV;
		MaximumV = maximumV;
	}
}

internal sealed partial class ProceduralHeadWoundWorkspace
{
	private List<int>[] _sourceSubmeshes = Array.Empty<List<int>>();
	private List<int>[] _filteredSubmeshes = Array.Empty<List<int>>();
	private List<byte>[] _removedTriangles = Array.Empty<List<byte>>();
	private int[] _vertexMarks = Array.Empty<int>();
	private int _markGeneration = 1;
	private readonly Dictionary<ulong, int> _midpointVertices = new Dictionary<ulong, int>(2048);
	internal readonly Dictionary<int, bool> InsideVertices = new Dictionary<int, bool>();
	internal void ResetClipCaches() { InsideVertices.Clear(); _intersectionVertices.Clear(); }
	internal bool TryGetIntersectionVertex(int a, int b, out int index) => _intersectionVertices.TryGetValue(((ulong)(uint)Math.Min(a, b) << 32) | (uint)Math.Max(a, b), out index);
	private readonly Dictionary<ulong, int> _intersectionVertices = new Dictionary<ulong, int>(1024);
	private readonly Dictionary<ulong, int> _cutBoundaryEdgeLookup = new Dictionary<ulong, int>(1024);
	private readonly Dictionary<int, List<int>> _cutBoundaryAdjacency = new Dictionary<int, List<int>>(1024);
	private readonly List<List<int>> _cutBoundaryAdjacencyPool = new List<List<int>>(1024);
	private readonly HashSet<int> _visitedCutBoundaryEdges = new HashSet<int>();
	private readonly Dictionary<BoundaryVertexKey, int> _canonicalBoundaryVertices =
		new Dictionary<BoundaryVertexKey, int>(1024);
	private readonly HashSet<ulong> _canonicalCutBoundaryEdges = new HashSet<ulong>();
	private readonly List<WoundBoundaryComponent> _boundaryComponentPool = new List<WoundBoundaryComponent>(16);
	internal readonly HashSet<int> CutBoundaryVertices = new HashSet<int>();
	internal readonly List<CutBoundaryEdge> CutBoundaryEdges = new List<CutBoundaryEdge>(1024);
	internal readonly List<WoundBoundaryComponent> BoundaryComponents = new List<WoundBoundaryComponent>(16);
	internal readonly List<WoundCanalBoundaryAttachment> CanalBoundaryAttachments =
		new List<WoundCanalBoundaryAttachment>(16);
	internal readonly List<int> BoundaryTraversalEdges = new List<int>(1024);
	internal readonly HashSet<int> BoundaryTraversalVertices = new HashSet<int>();
	internal readonly List<int> BoundaryOpenEndpoints = new List<int>(128);
	internal Dictionary<int, List<int>> CutBoundaryAdjacency => _cutBoundaryAdjacency;
	internal readonly HashSet<int> RetainedCutBoundaryVertices = new HashSet<int>();
	internal readonly List<int> EntryCutBoundaryVertices = new List<int>(128);
	internal readonly List<int> ExitCutBoundaryVertices = new List<int>(128);

	internal readonly List<Vector3> SourceVertices = new List<Vector3>(2048);
	internal readonly List<Vector3> SourceNormals = new List<Vector3>(2048);
	internal readonly List<Vector4> SourceTangents = new List<Vector4>(2048);
	internal readonly List<Color> SourceColors = new List<Color>(2048);
	internal readonly List<BoneWeight> SourceBoneWeights = new List<BoneWeight>(2048);
	private readonly int[] _blendBoneIndices = new int[12];
	private readonly float[] _blendBoneWeights = new float[12];
	private int _blendBoneCount;
	internal readonly List<Vector4>[] SourceUvs =
	{
		new List<Vector4>(2048), new List<Vector4>(2048), new List<Vector4>(2048), new List<Vector4>(2048),
		new List<Vector4>(2048), new List<Vector4>(2048), new List<Vector4>(2048), new List<Vector4>(2048)
	};
	internal readonly List<Vector3> CavityVertices = new List<Vector3>(256);
	internal readonly List<Vector3> CavityNormals = new List<Vector3>(256);
	internal readonly List<Vector4> CavityTangents = new List<Vector4>(256);
	internal readonly List<Vector2> CavityUvs = new List<Vector2>(256);
	internal readonly List<Color> CavityColors = new List<Color>(256);
	internal readonly List<int> CavityTriangles = new List<int>(768);
	internal readonly float[] RingPhases = new float[ProceduralHeadWoundGeometry.AxialRingCount];
	internal readonly float[] RingMinimum = new float[ProceduralHeadWoundGeometry.AxialRingCount];
	internal readonly float[] RingMaximum = new float[ProceduralHeadWoundGeometry.AxialRingCount];
	internal readonly float[] RingRadii = new float[ProceduralHeadWoundGeometry.AxialRingCount * ProceduralHeadWoundGeometry.AngularSegmentCount];
	internal readonly List<RimSurfaceCandidate> EntryRimSurfaceCandidates = new List<RimSurfaceCandidate>(1024);
	internal readonly List<RimSurfaceCandidate> ExitRimSurfaceCandidates = new List<RimSurfaceCandidate>(1024);
	internal readonly float[] EntryRimSurfaceOffsets = new float[ProceduralHeadWoundGeometry.RimSurfaceSampleCount];
	internal readonly float[] ExitRimSurfaceOffsets = new float[ProceduralHeadWoundGeometry.RimSurfaceSampleCount];
	internal readonly Vector3[] EntryRimSurfaceNormals = new Vector3[ProceduralHeadWoundGeometry.RimSurfaceSampleCount];
	internal readonly Vector3[] ExitRimSurfaceNormals = new Vector3[ProceduralHeadWoundGeometry.RimSurfaceSampleCount];
	internal readonly byte[] EntryRimSurfaceHits = new byte[ProceduralHeadWoundGeometry.RimSurfaceSampleCount];
	internal readonly byte[] ExitRimSurfaceHits = new byte[ProceduralHeadWoundGeometry.RimSurfaceSampleCount];
	internal readonly float[] EntryOpeningCoverageRadii = new float[ProceduralHeadWoundGeometry.AngularSegmentCount];
	internal readonly float[] ExitOpeningCoverageRadii = new float[ProceduralHeadWoundGeometry.AngularSegmentCount];
	internal readonly float[] EntryRimContactRadii = new float[ProceduralHeadWoundGeometry.AngularSegmentCount];
	internal readonly float[] ExitRimContactRadii = new float[ProceduralHeadWoundGeometry.AngularSegmentCount];
	internal readonly Vector3[] EntryBoundaryPositions = new Vector3[ProceduralHeadWoundGeometry.EntryAngularSegmentCount];
	internal readonly Vector3[] EntryBoundaryNormals = new Vector3[ProceduralHeadWoundGeometry.EntryAngularSegmentCount];
	internal readonly Vector3[] EntryLipOuterPositions = new Vector3[ProceduralHeadWoundGeometry.EntryAngularSegmentCount];
	internal readonly Vector3[] EntryLipOuterNormals = new Vector3[ProceduralHeadWoundGeometry.EntryAngularSegmentCount];
	internal readonly Vector3[] ExitBoundaryPositions = new Vector3[ProceduralHeadWoundGeometry.ExitAngularSegmentCount];
	internal readonly Vector3[] ExitBoundaryNormals = new Vector3[ProceduralHeadWoundGeometry.ExitAngularSegmentCount];
	internal readonly Vector3[] ExitLipOuterPositions = new Vector3[ProceduralHeadWoundGeometry.ExitAngularSegmentCount];
	internal readonly Vector3[] ExitLipOuterNormals = new Vector3[ProceduralHeadWoundGeometry.ExitAngularSegmentCount];
	internal readonly int[] ExitCollarRing = new int[ProceduralHeadWoundGeometry.ExitAngularSegmentCount + 1];
	internal readonly int[] ExitFlapInnerVertices = new int[ProceduralHeadWoundGeometry.ExitFlapVertexCapacity];
	internal readonly int[] ExitFlapOuterVertices = new int[ProceduralHeadWoundGeometry.ExitFlapVertexCapacity];
	internal int EntryRimSurfaceHitCount;
	internal int ExitRimSurfaceHitCount;
	internal float EntryOpeningCoverageRadius;
	internal float ExitOpeningCoverageRadius;
	internal float EntryMinimumRimCoverageMargin = float.MaxValue;
	internal float ExitMinimumRimCoverageMargin = float.MaxValue;
	internal float MinimumEntryRimFacingDot = 1f;
	internal float MinimumExitRimFacingDot = 1f;
	internal int WorstEntryRimTriangle = -1;
	internal int WorstExitRimTriangle = -1;
	internal float WorstEntryRimAreaSquared;
	internal float WorstExitRimAreaSquared;
	internal Vector3 WorstEntryActualNormal;
	internal Vector3 WorstEntryDesiredNormal;
	internal Vector3 WorstExitActualNormal;
	internal Vector3 WorstExitDesiredNormal;
	internal int BaseVertexCount;
	internal readonly List<List<int>> FragmentStarts = new List<List<int>>();
	internal int GeneratedVertexLimit = ProceduralHeadWoundGeometry.MaximumGeneratedHeadVertices;

	internal int SubMeshCount => _sourceSubmeshes.Length;
	internal IReadOnlyList<int> GetSourceSubmesh(int submesh) => _sourceSubmeshes[submesh];
	internal IReadOnlyList<int> GetFilteredSubmesh(int submesh) => _filteredSubmeshes[submesh];
	internal List<int> GetMutableSourceSubmesh(int submesh) => _sourceSubmeshes[submesh];
	internal List<int> GetMutableFilteredSubmesh(int submesh) => _filteredSubmeshes[submesh];
	internal List<byte> GetRemovedTriangles(int submesh) => _removedTriangles[submesh];

	internal bool AcquireFromMeshOnMainThread(Mesh mesh, out string failure)
	{
		failure = null;
		if (mesh == null || !mesh.isReadable || mesh.vertexCount < 3 || mesh.subMeshCount < 1)
		{
			failure = "source head mesh is missing, unreadable, or empty";
			return false;
		}

		EnsureSubmeshCapacity(mesh.subMeshCount);
		SourceVertices.Clear();
		mesh.GetVertices(SourceVertices);
		if (SourceVertices.Count != mesh.vertexCount)
		{
			failure = "source head vertex acquisition was incomplete";
			return false;
		}
		BaseVertexCount = SourceVertices.Count;
		SourceNormals.Clear();
		mesh.GetNormals(SourceNormals);
		if (SourceNormals.Count != BaseVertexCount) SourceNormals.Clear();
		SourceTangents.Clear();
		mesh.GetTangents(SourceTangents);
		if (SourceTangents.Count != BaseVertexCount) SourceTangents.Clear();
		SourceColors.Clear();
		mesh.GetColors(SourceColors);
		if (SourceColors.Count != BaseVertexCount) SourceColors.Clear();
		SourceBoneWeights.Clear();
		BoneWeight[] boneWeights = mesh.boneWeights;
		if (boneWeights != null && boneWeights.Length == BaseVertexCount) SourceBoneWeights.AddRange(boneWeights);
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			SourceUvs[channel].Clear();
			mesh.GetUVs(channel, SourceUvs[channel]);
			if (SourceUvs[channel].Count != BaseVertexCount) SourceUvs[channel].Clear();
		}

		for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
		{
			if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
			{
				failure = $"source head submesh {submesh} is not triangle topology";
				return false;
			}
			List<int> source = _sourceSubmeshes[submesh];
			source.Clear();
			mesh.GetTriangles(source, submesh, applyBaseVertex: true);
			if (source.Count == 0 || source.Count % 3 != 0)
			{
				failure = $"source head submesh {submesh} has invalid triangle indices";
				return false;
			}
		}

		if (_vertexMarks.Length < SourceVertices.Count)
		{
			_vertexMarks = new int[SourceVertices.Count];
			_markGeneration = 1;
		}
		return true;
	}

	internal void BeginClassification()
	{
		if (SourceVertices.Count > BaseVertexCount) SourceVertices.RemoveRange(BaseVertexCount, SourceVertices.Count - BaseVertexCount);
		if (SourceNormals.Count > BaseVertexCount) SourceNormals.RemoveRange(BaseVertexCount, SourceNormals.Count - BaseVertexCount);
		if (SourceTangents.Count > BaseVertexCount) SourceTangents.RemoveRange(BaseVertexCount, SourceTangents.Count - BaseVertexCount);
		if (SourceColors.Count > BaseVertexCount) SourceColors.RemoveRange(BaseVertexCount, SourceColors.Count - BaseVertexCount);
		if (SourceBoneWeights.Count > BaseVertexCount) SourceBoneWeights.RemoveRange(BaseVertexCount, SourceBoneWeights.Count - BaseVertexCount);
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			if (SourceUvs[channel].Count > BaseVertexCount) SourceUvs[channel].RemoveRange(BaseVertexCount, SourceUvs[channel].Count - BaseVertexCount);
		}
		_midpointVertices.Clear();
		ResetClipCaches();
		_cutBoundaryEdgeLookup.Clear();
		for (int index = 0; index < _cutBoundaryAdjacencyPool.Count; index++) _cutBoundaryAdjacencyPool[index].Clear();
		_cutBoundaryAdjacency.Clear();
		_visitedCutBoundaryEdges.Clear();
		_canonicalBoundaryVertices.Clear();
		_canonicalCutBoundaryEdges.Clear();
		for (int index = 0; index < _boundaryComponentPool.Count; index++) _boundaryComponentPool[index].Clear();
		BoundaryComponents.Clear();
		CanalBoundaryAttachments.Clear();
		BoundaryTraversalEdges.Clear();
		BoundaryTraversalVertices.Clear();
		BoundaryOpenEndpoints.Clear();
		CutBoundaryVertices.Clear();
		CutBoundaryEdges.Clear();
		RetainedCutBoundaryVertices.Clear();
		EntryCutBoundaryVertices.Clear();
		ExitCutBoundaryVertices.Clear();
		_markGeneration++;
		if (_markGeneration == int.MaxValue)
		{
			Array.Clear(_vertexMarks, 0, _vertexMarks.Length);
			_markGeneration = 1;
		}
		for (int submesh = 0; submesh < _sourceSubmeshes.Length; submesh++)
		{
			_filteredSubmeshes[submesh].Clear();
			_removedTriangles[submesh].Clear();
		}
		CavityVertices.Clear();
		CavityNormals.Clear();
		CavityTangents.Clear();
		CavityUvs.Clear();
		CavityColors.Clear();
		CavityTriangles.Clear();
		EntryRimSurfaceCandidates.Clear();
		ExitRimSurfaceCandidates.Clear();
		Array.Clear(EntryRimSurfaceHits, 0, EntryRimSurfaceHits.Length);
		Array.Clear(ExitRimSurfaceHits, 0, ExitRimSurfaceHits.Length);
		Array.Clear(EntryOpeningCoverageRadii, 0, EntryOpeningCoverageRadii.Length);
		Array.Clear(ExitOpeningCoverageRadii, 0, ExitOpeningCoverageRadii.Length);
		Array.Clear(EntryRimContactRadii, 0, EntryRimContactRadii.Length);
		Array.Clear(ExitRimContactRadii, 0, ExitRimContactRadii.Length);
		Array.Clear(EntryBoundaryPositions, 0, EntryBoundaryPositions.Length);
		Array.Clear(EntryBoundaryNormals, 0, EntryBoundaryNormals.Length);
		Array.Clear(EntryLipOuterPositions, 0, EntryLipOuterPositions.Length);
		Array.Clear(EntryLipOuterNormals, 0, EntryLipOuterNormals.Length);
		Array.Clear(ExitBoundaryPositions, 0, ExitBoundaryPositions.Length);
		Array.Clear(ExitBoundaryNormals, 0, ExitBoundaryNormals.Length);
		Array.Clear(ExitLipOuterPositions, 0, ExitLipOuterPositions.Length);
		Array.Clear(ExitLipOuterNormals, 0, ExitLipOuterNormals.Length);
		Array.Clear(ExitCollarRing, 0, ExitCollarRing.Length);
		Array.Clear(ExitFlapInnerVertices, 0, ExitFlapInnerVertices.Length);
		Array.Clear(ExitFlapOuterVertices, 0, ExitFlapOuterVertices.Length);
		EntryRimSurfaceHitCount = 0;
		ExitRimSurfaceHitCount = 0;
		EntryOpeningCoverageRadius = 0f;
		ExitOpeningCoverageRadius = 0f;
		EntryMinimumRimCoverageMargin = float.MaxValue;
		ExitMinimumRimCoverageMargin = float.MaxValue;
		MinimumEntryRimFacingDot = 1f;
		MinimumExitRimFacingDot = 1f;
		WorstEntryRimTriangle = -1;
		WorstExitRimTriangle = -1;
		WorstEntryRimAreaSquared = 0f;
		WorstExitRimAreaSquared = 0f;
		WorstEntryActualNormal = Vector3.zero;
		WorstEntryDesiredNormal = Vector3.zero;
		WorstExitActualNormal = Vector3.zero;
		WorstExitDesiredNormal = Vector3.zero;
	}

	internal void AddCutBoundaryEdge(int a, int b, int submesh)
	{
		if (a < 0 || b < 0 || a == b) return;
		uint low = (uint)Math.Min(a, b);
		uint high = (uint)Math.Max(a, b);
		ulong key = ((ulong)low << 32) | high;
		if (_cutBoundaryEdgeLookup.ContainsKey(key)) return;
		_cutBoundaryEdgeLookup.Add(key, CutBoundaryEdges.Count);
		CutBoundaryEdges.Add(new CutBoundaryEdge(a, b, submesh));
	}

	internal List<int> GetCutBoundaryAdjacency(int vertex)
	{
		if (_cutBoundaryAdjacency.TryGetValue(vertex, out List<int> adjacency)) return adjacency;
		int poolIndex = _cutBoundaryAdjacency.Count;
		if (poolIndex == _cutBoundaryAdjacencyPool.Count)
		{
			_cutBoundaryAdjacencyPool.Add(new List<int>(4));
		}
		adjacency = _cutBoundaryAdjacencyPool[poolIndex];
		_cutBoundaryAdjacency.Add(vertex, adjacency);
		return adjacency;
	}

	internal bool TryGetCutBoundaryAdjacency(int vertex, out List<int> adjacency)
	{
		return _cutBoundaryAdjacency.TryGetValue(vertex, out adjacency);
	}

	internal bool VisitCutBoundaryEdge(int edgeIndex)
	{
		return _visitedCutBoundaryEdges.Add(edgeIndex);
	}

	internal bool IsCutBoundaryEdgeVisited(int edgeIndex)
	{
		return _visitedCutBoundaryEdges.Contains(edgeIndex);
	}

	internal int GetCanonicalBoundaryVertex(int vertex)
	{
		const float quantization = 100000f;
		const float weldDistanceSquared = 0.0000000001f;
		Vector3 position = SourceVertices[vertex];
		int x = Mathf.RoundToInt(position.x * quantization);
		int y = Mathf.RoundToInt(position.y * quantization);
		int z = Mathf.RoundToInt(position.z * quantization);
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dz = -1; dz <= 1; dz++)
				{
					BoundaryVertexKey neighbor = new BoundaryVertexKey(x + dx, y + dy, z + dz);
					if (_canonicalBoundaryVertices.TryGetValue(neighbor, out int candidate) &&
						(SourceVertices[candidate] - position).sqrMagnitude <= weldDistanceSquared)
					{
						return candidate;
					}
				}
			}
		}
		_canonicalBoundaryVertices[new BoundaryVertexKey(x, y, z)] = vertex;
		return vertex;
	}

	internal bool AddCanonicalCutBoundaryEdge(int a, int b)
	{
		if (a == b) return false;
		uint low = (uint)Math.Min(a, b);
		uint high = (uint)Math.Max(a, b);
		return _canonicalCutBoundaryEdges.Add(((ulong)low << 32) | high);
	}

	internal WoundBoundaryComponent RentBoundaryComponent()
	{
		int index = BoundaryComponents.Count;
		if (index == _boundaryComponentPool.Count)
		{
			_boundaryComponentPool.Add(new WoundBoundaryComponent());
		}
		WoundBoundaryComponent component = _boundaryComponentPool[index];
		component.Clear();
		BoundaryComponents.Add(component);
		return component;
	}

	internal void MarkRemovedVertex(int vertexIndex)
	{
		if ((uint)vertexIndex < (uint)_vertexMarks.Length)
		{
			_vertexMarks[vertexIndex] = _markGeneration;
		}
	}

	internal bool IsMarkedRemovedVertex(int vertexIndex)
	{
		return (uint)vertexIndex < (uint)_vertexMarks.Length && _vertexMarks[vertexIndex] == _markGeneration;
	}

	internal void ApplyFilteredIndicesOnMainThread(Mesh mesh, Bounds? bounds = null)
	{
		mesh.Clear(keepVertexLayout: false);
		mesh.indexFormat = SourceVertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
		mesh.SetVertices(SourceVertices);
		if (SourceNormals.Count == SourceVertices.Count) mesh.SetNormals(SourceNormals);
		if (SourceTangents.Count == SourceVertices.Count) mesh.SetTangents(SourceTangents);
		if (SourceColors.Count == SourceVertices.Count) mesh.SetColors(SourceColors);
		if (SourceBoneWeights.Count == SourceVertices.Count) mesh.boneWeights = SourceBoneWeights.ToArray();
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			if (SourceUvs[channel].Count == SourceVertices.Count) mesh.SetUVs(channel, SourceUvs[channel]);
		}
		mesh.subMeshCount = _filteredSubmeshes.Length;
		for (int submesh = 0; submesh < _filteredSubmeshes.Length; submesh++)
		{
			mesh.SetTriangles(_filteredSubmeshes[submesh], submesh, calculateBounds: false);
		}
		if (bounds.HasValue) mesh.bounds = bounds.Value;
		else mesh.RecalculateBounds();
	}

	internal int GetOrCreateMidpointVertex(int a, int b, int maximumGeneratedVertices)
	{
		uint low = (uint)Math.Min(a, b);
		uint high = (uint)Math.Max(a, b);
		ulong key = ((ulong)low << 32) | high;
		if (_midpointVertices.TryGetValue(key, out int existing)) return existing;
		if (SourceVertices.Count - BaseVertexCount >= maximumGeneratedVertices) return -1;

		int index = SourceVertices.Count;
		bool hasNormals = SourceNormals.Count == index;
		bool hasTangents = SourceTangents.Count == index;
		bool hasColors = SourceColors.Count == index;
		bool hasBoneWeights = SourceBoneWeights.Count == index;
		SourceVertices.Add((SourceVertices[a] + SourceVertices[b]) * 0.5f);
		if (hasNormals)
		{
			Vector3 normal = SourceNormals[a] + SourceNormals[b];
			SourceNormals.Add(normal.sqrMagnitude > 0.0000001f ? normal.normalized : SourceNormals[a]);
		}
		if (hasTangents)
		{
			Vector4 ta = SourceTangents[a];
			Vector4 tb = SourceTangents[b];
			Vector3 tangent = new Vector3(ta.x + tb.x, ta.y + tb.y, ta.z + tb.z);
			if (tangent.sqrMagnitude > 0.0000001f) tangent.Normalize();
			else tangent = new Vector3(ta.x, ta.y, ta.z);
			SourceTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, ta.w));
		}
		if (hasColors) SourceColors.Add((SourceColors[a] + SourceColors[b]) * 0.5f);
		if (hasBoneWeights) SourceBoneWeights.Add(BlendBoneWeights(a, 0.5f, b, 0.5f));
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			List<Vector4> uvs = SourceUvs[channel];
			if (uvs.Count == index) uvs.Add((uvs[a] + uvs[b]) * 0.5f);
		}
		_midpointVertices.Add(key, index);
		return index;
	}

	internal int GetOrCreateIntersectionVertex(int a, int b, float t, int maximumGeneratedVertices)
	{
		uint low = (uint)Math.Min(a, b);
		uint high = (uint)Math.Max(a, b);
		ulong key = ((ulong)low << 32) | high;
		if (_intersectionVertices.TryGetValue(key, out int existing))
		{
			CutBoundaryVertices.Add(existing);
			return existing;
		}
		int first = a <= b ? a : b;
		int second = a <= b ? b : a;
		float canonicalT = a <= b ? t : 1f - t;
		int index = AddInterpolatedVertex(first, second, canonicalT, maximumGeneratedVertices);
		if (index >= 0)
		{
			_intersectionVertices.Add(key, index);
			CutBoundaryVertices.Add(index);
		}
		return index;
	}

	internal int AddCentroidVertex(int a, int b, int c, int maximumGeneratedVertices)
	{
		if (SourceVertices.Count - BaseVertexCount >= maximumGeneratedVertices) return -1;
		int index = SourceVertices.Count;
		bool hasNormals = SourceNormals.Count == index;
		bool hasTangents = SourceTangents.Count == index;
		bool hasColors = SourceColors.Count == index;
		bool hasBoneWeights = SourceBoneWeights.Count == index;
		SourceVertices.Add((SourceVertices[a] + SourceVertices[b] + SourceVertices[c]) / 3f);
		if (hasNormals)
		{
			Vector3 normal = SourceNormals[a] + SourceNormals[b] + SourceNormals[c];
			SourceNormals.Add(normal.sqrMagnitude > 0.0000001f ? normal.normalized : SourceNormals[a]);
		}
		if (hasTangents)
		{
			Vector4 ta = SourceTangents[a];
			Vector4 tb = SourceTangents[b];
			Vector4 tc = SourceTangents[c];
			Vector3 tangent = new Vector3(ta.x + tb.x + tc.x, ta.y + tb.y + tc.y, ta.z + tb.z + tc.z);
			if (tangent.sqrMagnitude > 0.0000001f) tangent.Normalize();
			else tangent = new Vector3(ta.x, ta.y, ta.z);
			SourceTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, ta.w));
		}
		if (hasColors) SourceColors.Add((SourceColors[a] + SourceColors[b] + SourceColors[c]) / 3f);
		if (hasBoneWeights) SourceBoneWeights.Add(BlendBoneWeights(a, 1f / 3f, b, 1f / 3f, c, 1f / 3f));
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			List<Vector4> uvs = SourceUvs[channel];
			if (uvs.Count == index) uvs.Add((uvs[a] + uvs[b] + uvs[c]) / 3f);
		}
		return index;
	}

	internal int AddDisplacedInterpolatedVertex(int a, int b, float t, Vector3 position,
		Vector3 normal, int maximumGeneratedVertices)
	{
		int index = AddInterpolatedVertex(a, b, t, maximumGeneratedVertices);
		if (index < 0) return -1;
		SourceVertices[index] = position;
		if (SourceNormals.Count == SourceVertices.Count)
		{
			SourceNormals[index] = normal.sqrMagnitude > 0.0000001f ? normal.normalized : SourceNormals[index];
		}
		return index;
	}

	internal int AddDisplacedBarycentricVertex(int a, int b, int c, Vector3 barycentric,
		Vector3 position, Vector3 normal, int maximumGeneratedVertices)
	{
		if (SourceVertices.Count - BaseVertexCount >= maximumGeneratedVertices) return -1;
		int index = SourceVertices.Count;
		bool hasNormals = SourceNormals.Count == index;
		bool hasTangents = SourceTangents.Count == index;
		bool hasColors = SourceColors.Count == index;
		bool hasBoneWeights = SourceBoneWeights.Count == index;
		SourceVertices.Add(position);
		if (hasNormals)
		{
			SourceNormals.Add(normal.sqrMagnitude > 0.0000001f ? normal.normalized : SourceNormals[a]);
		}
		if (hasTangents)
		{
			Vector4 ta = SourceTangents[a];
			Vector4 tb = SourceTangents[b];
			Vector4 tc = SourceTangents[c];
			Vector3 tangent = new Vector3(ta.x, ta.y, ta.z) * barycentric.x +
				new Vector3(tb.x, tb.y, tb.z) * barycentric.y +
				new Vector3(tc.x, tc.y, tc.z) * barycentric.z;
			if (tangent.sqrMagnitude > 0.0000001f) tangent.Normalize();
			else tangent = new Vector3(ta.x, ta.y, ta.z);
			SourceTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, ta.w));
		}
		if (hasColors)
		{
			SourceColors.Add(SourceColors[a] * barycentric.x + SourceColors[b] * barycentric.y +
				SourceColors[c] * barycentric.z);
		}
		if (hasBoneWeights)
		{
			SourceBoneWeights.Add(BlendBoneWeights(a, barycentric.x, b, barycentric.y, c, barycentric.z));
		}
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			List<Vector4> uvs = SourceUvs[channel];
			if (uvs.Count == index)
			{
				uvs.Add(uvs[a] * barycentric.x + uvs[b] * barycentric.y + uvs[c] * barycentric.z);
			}
		}
		return index;
	}

	internal bool AddGeneratedHeadTriangle(int submesh, int a, int b, int c, Vector3 desiredNormal)
	{
		if ((uint)submesh >= (uint)_filteredSubmeshes.Length || a < 0 || b < 0 || c < 0 ||
			a == b || b == c || c == a) return false;
		Vector3 actual = Vector3.Cross(SourceVertices[b] - SourceVertices[a], SourceVertices[c] - SourceVertices[a]);
		if (actual.sqrMagnitude <= 0.0000000000000001f) return false;
		if (desiredNormal.sqrMagnitude > 0.0000001f && Vector3.Dot(actual, desiredNormal) < 0f)
		{
			int swap = b;
			b = c;
			c = swap;
		}
		_filteredSubmeshes[submesh].Add(a);
		_filteredSubmeshes[submesh].Add(b);
		_filteredSubmeshes[submesh].Add(c);
		return true;
	}

	private int AddInterpolatedVertex(int a, int b, float t, int maximumGeneratedVertices)
	{
		if (SourceVertices.Count - BaseVertexCount >= maximumGeneratedVertices) return -1;
		int index = SourceVertices.Count;
		bool hasNormals = SourceNormals.Count == index;
		bool hasTangents = SourceTangents.Count == index;
		bool hasColors = SourceColors.Count == index;
		bool hasBoneWeights = SourceBoneWeights.Count == index;
		SourceVertices.Add(Vector3.LerpUnclamped(SourceVertices[a], SourceVertices[b], t));
		if (hasNormals)
		{
			Vector3 normal = Vector3.LerpUnclamped(SourceNormals[a], SourceNormals[b], t);
			SourceNormals.Add(normal.sqrMagnitude > 0.0000001f ? normal.normalized : SourceNormals[a]);
		}
		if (hasTangents)
		{
			Vector4 ta = SourceTangents[a];
			Vector4 tb = SourceTangents[b];
			Vector3 tangent = Vector3.LerpUnclamped(new Vector3(ta.x, ta.y, ta.z), new Vector3(tb.x, tb.y, tb.z), t);
			if (tangent.sqrMagnitude > 0.0000001f) tangent.Normalize();
			else tangent = new Vector3(ta.x, ta.y, ta.z);
			SourceTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, Mathf.LerpUnclamped(ta.w, tb.w, t)));
		}
		if (hasColors) SourceColors.Add(Color.LerpUnclamped(SourceColors[a], SourceColors[b], t));
		if (hasBoneWeights) SourceBoneWeights.Add(BlendBoneWeights(a, 1f - t, b, t));
		for (int channel = 0; channel < SourceUvs.Length; channel++)
		{
			List<Vector4> uvs = SourceUvs[channel];
			if (uvs.Count == index) uvs.Add(Vector4.LerpUnclamped(uvs[a], uvs[b], t));
		}
		return index;
	}

	private BoneWeight BlendBoneWeights(int a, float factorA, int b, float factorB)
	{
		_blendBoneCount = 0;
		AccumulateBoneWeight(SourceBoneWeights[a], factorA);
		AccumulateBoneWeight(SourceBoneWeights[b], factorB);
		return BuildBoneWeight();
	}

	private BoneWeight BlendBoneWeights(int a, float factorA, int b, float factorB, int c, float factorC)
	{
		_blendBoneCount = 0;
		AccumulateBoneWeight(SourceBoneWeights[a], factorA);
		AccumulateBoneWeight(SourceBoneWeights[b], factorB);
		AccumulateBoneWeight(SourceBoneWeights[c], factorC);
		return BuildBoneWeight();
	}

	private void AccumulateBoneWeight(BoneWeight source, float factor)
	{
		AddBoneInfluence(source.boneIndex0, source.weight0 * factor);
		AddBoneInfluence(source.boneIndex1, source.weight1 * factor);
		AddBoneInfluence(source.boneIndex2, source.weight2 * factor);
		AddBoneInfluence(source.boneIndex3, source.weight3 * factor);
	}

	private void AddBoneInfluence(int boneIndex, float weight)
	{
		if (weight <= 0.0000001f) return;
		for (int index = 0; index < _blendBoneCount; index++)
		{
			if (_blendBoneIndices[index] != boneIndex) continue;
			_blendBoneWeights[index] += weight;
			return;
		}
		if (_blendBoneCount >= _blendBoneIndices.Length) return;
		_blendBoneIndices[_blendBoneCount] = boneIndex;
		_blendBoneWeights[_blendBoneCount] = weight;
		_blendBoneCount++;
	}

	private BoneWeight BuildBoneWeight()
	{
		BoneWeight result = default;
		float total = 0f;
		for (int slot = 0; slot < 4; slot++)
		{
			int best = -1;
			float bestWeight = 0f;
			for (int index = 0; index < _blendBoneCount; index++)
			{
				if (_blendBoneWeights[index] <= bestWeight) continue;
				best = index;
				bestWeight = _blendBoneWeights[index];
			}
			if (best < 0) break;
			SetBoneInfluence(ref result, slot, _blendBoneIndices[best], bestWeight);
			total += bestWeight;
			_blendBoneWeights[best] = 0f;
		}
		if (total > 0.0000001f)
		{
			result.weight0 /= total;
			result.weight1 /= total;
			result.weight2 /= total;
			result.weight3 /= total;
		}
		return result;
	}

	private static void SetBoneInfluence(ref BoneWeight target, int slot, int boneIndex, float weight)
	{
		switch (slot)
		{
			case 0:
				target.boneIndex0 = boneIndex;
				target.weight0 = weight;
				break;
			case 1:
				target.boneIndex1 = boneIndex;
				target.weight1 = weight;
				break;
			case 2:
				target.boneIndex2 = boneIndex;
				target.weight2 = weight;
				break;
			default:
				target.boneIndex3 = boneIndex;
				target.weight3 = weight;
				break;
		}
	}

	internal Mesh CreateCavityMeshOnMainThread(string name, Bounds? bounds = null)
	{
		Mesh mesh = new Mesh
		{
			name = name,
			indexFormat = CavityVertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
		};
		mesh.SetVertices(CavityVertices);
		mesh.SetNormals(CavityNormals);
		mesh.SetTangents(CavityTangents);
		mesh.SetUVs(0, CavityUvs);
		mesh.SetColors(CavityColors);
		mesh.SetTriangles(CavityTriangles, 0, calculateBounds: !bounds.HasValue);
		if (bounds.HasValue) mesh.bounds = bounds.Value;
		return mesh;
	}

	private void EnsureSubmeshCapacity(int count)
	{
		if (_sourceSubmeshes.Length == count)
		{
			return;
		}
		_sourceSubmeshes = new List<int>[count];
		_filteredSubmeshes = new List<int>[count];
		_removedTriangles = new List<byte>[count];
		for (int index = 0; index < count; index++)
		{
			_sourceSubmeshes[index] = new List<int>(4096);
			_filteredSubmeshes[index] = new List<int>(4096);
			_removedTriangles[index] = new List<byte>(1365);
		}
	}
}

internal static partial class ProceduralHeadWoundGeometry
{
	internal const int AxialRingCount = 8;
	internal const int EntryAngularSegmentCount = 16;
	internal const int ExitAngularSegmentCount = 24;
	internal const int AngularSegmentCount = ExitAngularSegmentCount;
	internal const int CavityVerticesPerRing = AngularSegmentCount + 1;
	internal const int MaximumCavityVertices = 4096;
	internal const int MaximumGeneratedHeadVertices = 4096;
	internal const int RimAddedRingCount = 4;
	internal const int RimSurfaceSampleCount = RimAddedRingCount * AngularSegmentCount;
	internal const float MinimumRimCoverageMargin = 0.0008f;
	internal const float MaximumRimOuterRadius = 0.06f;
	internal const int ExpectedThroughShotRimVertexCount =
		RimAddedRingCount * (EntryAngularSegmentCount + ExitAngularSegmentCount);
	internal const int ExpectedEntryOnlyRimVertexCount = RimAddedRingCount * EntryAngularSegmentCount;
	internal const int ExpectedThroughShotLipSurfaceTriangleCount =
		(RimAddedRingCount - 1) * (EntryAngularSegmentCount + ExitAngularSegmentCount) * 2;
	internal const int ExpectedEntryLipStitchTriangleCount = EntryAngularSegmentCount + AngularSegmentCount;
	internal const int ExpectedExitLipStitchTriangleCount = ExitAngularSegmentCount + AngularSegmentCount;
	internal const int ExpectedThroughShotRimTriangleCount =
		ExpectedThroughShotLipSurfaceTriangleCount +
		ExpectedEntryLipStitchTriangleCount + ExpectedExitLipStitchTriangleCount;
	internal const int ExpectedEntryOnlyRimTriangleCount =
		(RimAddedRingCount - 1) * EntryAngularSegmentCount * 2 +
		ExpectedEntryLipStitchTriangleCount;
	private const float TargetRimCoverageMargin = 0.0015f;
	private const float TwoPi = Mathf.PI * 2f;
	private const float SurfaceOverlap = 0.006f;
	private const float ClassificationPadding = 0.0005f;
	private const float CavityEndInsetScale = 0.88f;
	private const float CavityMiddleInsetScale = 0.97f;
	private const float CavityAxialInset = 0.0025f;
	private const float RimContactInset = 0.0003f;
	private const float RimSurfaceClearance = 0.0002f;
	private const float RimSurfaceSampleOutside = 0.01f;
	private const float RimMinimumCrestHeight = 0.00035f;
	private const float RimMaximumCrestHeight = 0.0016f;
	private const float RimOuterIrregularity = 0.04f;
	private const float MaximumRimSurfaceSampleDepth = 0.025f;
	private const float RimSurfaceRadialSearchStep = 0.0005f;
	private const float MaximumRimSurfaceRadialSearch = 0.012f;
	private const float MinimumDepth = 0.015f;
	private const float MinimumRefinementEdgeLength = 0.0015f;
	private const float MaximumRefinementEdgeLength = 0.0065f;
	private const int MaximumRefinementDepth = 6;
	private const float BoundaryOverlap = 0.00075f;
	private const float BoundaryContactExpansion = 0.0006f;
	private const float BoundarySampleOutside = 0.015f;
	private const float LipSurfaceLift = 0.0003f;
	private const float LipCanalInset = 0.0015f;
	private const float LipThroatRadialInsetScale = 1.6f;
	private const float LipMinimumOverlapWidth = 0.00125f;
	private const float LipMaximumOverlapWidth = 0.00375f;
	private const float LipOverlapRadiusScale = 0.18f;
	private const int EdgeRootIterations = 14;
	private const int CanalSpanRingCount = 6;
	private const int CanalAngularSegmentCount = ExitAngularSegmentCount;
	private const int IndependentLipRingCount = 3;
	private const float CanalRoughnessScale = 0.18f;
	private const float CanalAxialRoughness = 0.0015f;
	internal const int ExitFlapCount = 8;
	internal const int ExitFlapAngularSegments = ExitAngularSegmentCount / ExitFlapCount;
	internal const int ExitFlapAngularSamples = ExitFlapAngularSegments + 1;
	internal const int ExitFlapRowCount = 4;
	internal const int ExitFlapVertexCapacity = ExitFlapCount * ExitFlapRowCount * ExitFlapAngularSamples;
	private const float ExitCollarMinimumOverlap = 0.0025f;
	private const float ExitCollarMaximumOverlap = 0.0045f;
	private const float ExitCollarSurfaceInset = 0.0006f;
	private const float ExitFlapMinimumLength = 0.010f;
	private const float ExitFlapMaximumLength = 0.026f;
	private const float ExitFlapMinimumRadialSpread = 0.004f;
	private const float ExitFlapMaximumRadialSpread = 0.012f;
	private const float ExitFlapMinimumThickness = 0.002f;
	private const float ExitFlapMaximumThickness = 0.005f;
	private const float ExitFlapMinimumTipWidth = 0.38f;
	private const float ExitFlapMaximumTipWidth = 0.74f;
	private const float ExitFlapMaximumTangentialWarp = 0.003f;
	private const float BoundaryGraphBridgeMaximum = 0.0075f;

	internal readonly struct WoundProfile
	{
		internal readonly WoundProfile[] Members;
		internal readonly int MemberCount;
		internal WoundProfile(WoundProfile basis, WoundProfile[] members, int count)
		{
			this = basis; Members = members; MemberCount = count;
		}
		internal readonly Vector3 Entry;
		internal readonly Vector3 Exit;
		internal readonly Vector3 Axis;
		internal readonly Vector3 BasisU;
		internal readonly Vector3 BasisV;
		internal readonly float Length;
		internal readonly float EntryRadius;
		internal readonly float ExitRadius;
		internal readonly float MaximumRadius;
		internal readonly float[] RingPhases;
		internal readonly float[] RingMinimum;
		internal readonly float[] RingMaximum;
		internal readonly float[] RingRadii;

		internal WoundProfile(Vector3 entry, Vector3 exit, Vector3 axis, Vector3 basisU, Vector3 basisV,
			float length, float entryRadius, float exitRadius, float maximumRadius,
			float[] ringPhases, float[] ringRadii, float[] ringMinimum, float[] ringMaximum)
		{
			Members = null; MemberCount = 0;
			Entry = entry;
			Exit = exit;
			Axis = axis;
			BasisU = basisU;
			BasisV = basisV;
			Length = length;
			EntryRadius = entryRadius;
			ExitRadius = exitRadius;
			MaximumRadius = maximumRadius;
			RingMinimum = ringMinimum; RingMaximum = ringMaximum;
			RingPhases = ringPhases;
			RingRadii = ringRadii;
		}
	}

	internal static bool TryGenerate(ProceduralHeadWoundWorkspace workspace, ProceduralWoundDescriptor descriptor,
		out ProceduralWoundMetrics metrics, out string failure)
	{
		metrics = default;
		failure = null;
		if (workspace == null || workspace.SourceVertices.Count < 3 || workspace.SubMeshCount < 1)
		{
			failure = "head geometry was not acquired before generation";
			return false;
		}
		if (!Finite(descriptor.Entry) || !Finite(descriptor.Exit) || !FinitePositive(descriptor.EntryRadius) ||
			!FinitePositive(descriptor.ExitRadius))
		{
			failure = "wound descriptor contains invalid positions or radii";
			return false;
		}

		Vector3 delta = descriptor.Exit - descriptor.Entry;
		float length = delta.magnitude;
		if (!FinitePositive(length) || length < (descriptor.ThroughShot ? MinimumDepth : 0.008f))
		{
			failure = "wound descriptor is too shallow";
			return false;
		}

		while (workspace.FragmentStarts.Count < workspace.SubMeshCount) workspace.FragmentStarts.Add(new List<int>());
		foreach (var starts in workspace.FragmentStarts) starts.Clear();
		workspace.GeneratedVertexLimit = MaximumGeneratedHeadVertices;
		workspace.BeginClassification();
		WoundProfile profile = BuildProfile(workspace, descriptor, delta / length, length);
		long classificationStarted = Stopwatch.GetTimestamp();
		int sourceTriangles = 0;
		int candidateTriangles = 0;
		int intersectedSourceTriangles = 0;
		int clippedTriangles = 0;
		int removedTriangles = 0;
		int entryRemovedTriangles = 0;
		int exitRemovedTriangles = 0;
		float entryArc = TwoPi * descriptor.EntryRadius / EntryAngularSegmentCount;
		float exitArc = TwoPi * descriptor.ExitRadius / ExitAngularSegmentCount;
		float entryRefinementEdge = Mathf.Clamp(entryArc * 0.95f,
			MinimumRefinementEdgeLength, MaximumRefinementEdgeLength);
		float exitRefinementEdge = Mathf.Clamp(exitArc * 0.95f,
			MinimumRefinementEdgeLength, MaximumRefinementEdgeLength);
		float entryRefinementEdgeSquared = entryRefinementEdge * entryRefinementEdge;
		float exitRefinementEdgeSquared = exitRefinementEdge * exitRefinementEdge;
		Vector3 boundsRadius = Vector3.one * (profile.MaximumRadius + SurfaceOverlap + ClassificationPadding);
		Vector3 woundBoundsMin = Vector3.Min(profile.Entry, profile.Exit) - boundsRadius;
		Vector3 woundBoundsMax = Vector3.Max(profile.Entry, profile.Exit) + boundsRadius;
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> source = workspace.GetSourceSubmesh(submesh);
			List<int> filtered = workspace.GetMutableFilteredSubmesh(submesh);
			for (int index = 0; index + 2 < source.Count; index += 3)
			{
				workspace.FragmentStarts[submesh].Add(filtered.Count);
				sourceTriangles++;
				int ia = source[index];
				int ib = source[index + 1];
				int ic = source[index + 2];
				if (!ValidVertexIndex(workspace, ia) || !ValidVertexIndex(workspace, ib) || !ValidVertexIndex(workspace, ic))
				{
					failure = $"source submesh {submesh} contains an out-of-range vertex index";
					return false;
				}
				Vector3 a = workspace.SourceVertices[ia];
				Vector3 b = workspace.SourceVertices[ib];
				Vector3 c = workspace.SourceVertices[ic];
				bool candidate = TriangleBoundsOverlap(a, b, c, woundBoundsMin, woundBoundsMax) &&
					TriangleApproachesWoundAxis(profile, a, b, c);
				if (!candidate)
				{
					filtered.Add(ia);
					filtered.Add(ib);
					filtered.Add(ic);
					continue;
				}
				candidateTriangles++;
				if (!TriangleIntersectsVolume(profile, a, b, c))
				{
					filtered.Add(ia);
					filtered.Add(ib);
					filtered.Add(ic);
					continue;
				}
				intersectedSourceTriangles++;
				if (!ClipIntersectedTriangle(workspace, profile, filtered, submesh, ia, ib, ic, 0,
					entryRefinementEdgeSquared, exitRefinementEdgeSquared,
					ref clippedTriangles, ref removedTriangles, ref entryRemovedTriangles,
					ref exitRemovedTriangles, out failure))
				{
					return false;
				}
			}
		}

		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++) workspace.FragmentStarts[submesh].Add(workspace.GetFilteredSubmesh(submesh).Count);
		bool generateExitLip = descriptor.ThroughShot && descriptor.GenerateExitLip;
		if (intersectedSourceTriangles == 0 || removedTriangles == 0 || entryRemovedTriangles == 0 ||
			(generateExitLip && exitRemovedTriangles == 0))
		{
			failure = "wound volume did not remove triangles at every required opening";
			return false;
		}

		if (intersectedSourceTriangles > Math.Max(96, (int)(sourceTriangles * 0.30f)))
		{
			failure = $"wound classification intersected an implausible fraction of the head ({intersectedSourceTriangles}/{sourceTriangles})";
			return false;
		}
		if (!BuildCutBoundaryComponents(workspace, profile, out failure))
		{
			return false;
		}
		double classificationMilliseconds = ElapsedMilliseconds(classificationStarted);

		long cavityStarted = Stopwatch.GetTimestamp();
		if (!BuildComponentDrivenWound(workspace, profile, descriptor.ThroughShot, generateExitLip,
			descriptor.Seed ^ 0x7A4D39B1u,
			out int cavityVertexCount, out int cavityTriangleCount, out int rimVertexCount,
			out int rimTriangleCount, out bool entryLipStitched, out bool exitLipStitched,
			out float entryRimOuterRadius, out float exitRimOuterRadius,
			out int openBoundaryComponentCount, out int solidCanalSpanCount,
			out int entryLipCount, out int intermediateLipCount, out int exitFlapCount,
			out int exitBurstRemovedTriangles,
			out failure))
		{
			return false;
		}
		removedTriangles += exitBurstRemovedTriangles;
		double cavityMilliseconds = ElapsedMilliseconds(cavityStarted);
		if (cavityVertexCount <= 0 ||
			workspace.CavityVertices.Count > MaximumCavityVertices || workspace.CavityTriangles.Count == 0 ||
			workspace.CavityTriangles.Count % 3 != 0)
		{
			failure = "procedural cavity exceeded its bounded geometry contract";
			return false;
		}

		float entryOpeningRadius = workspace.EntryOpeningCoverageRadius;
		float exitOpeningRadius = generateExitLip ? workspace.ExitOpeningCoverageRadius : 0f;
		float minimumCoverageMargin = generateExitLip
			? Mathf.Min(workspace.EntryMinimumRimCoverageMargin, workspace.ExitMinimumRimCoverageMargin)
			: workspace.EntryMinimumRimCoverageMargin;
		if (!FinitePositive(minimumCoverageMargin) || minimumCoverageMargin < MinimumRimCoverageMargin)
		{
			failure = "procedural rim does not overlap the removed-triangle opening footprint";
			return false;
		}
		if (workspace.MinimumEntryRimFacingDot < 0f ||
			(generateExitLip && workspace.MinimumExitRimFacingDot < 0f))
		{
			failure = $"procedural rim contains degenerate or inward-facing opening triangles " +
				$"(entryFacing={workspace.MinimumEntryRimFacingDot:F6}, entryTriangle={workspace.WorstEntryRimTriangle}, " +
				$"entryAreaSq={workspace.WorstEntryRimAreaSquared:E6}, entryActual={workspace.WorstEntryActualNormal}, " +
				$"entryDesired={workspace.WorstEntryDesiredNormal}, exitFacing={workspace.MinimumExitRimFacingDot:F6}, " +
				$"exitTriangle={workspace.WorstExitRimTriangle}, exitAreaSq={workspace.WorstExitRimAreaSquared:E6})";
			return false;
		}

		int resultHeadTriangles = 0;
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			resultHeadTriangles += workspace.GetFilteredSubmesh(submesh).Count / 3;
		}
		metrics = new ProceduralWoundMetrics(sourceTriangles, candidateTriangles, intersectedSourceTriangles,
			clippedTriangles, removedTriangles, resultHeadTriangles,
			workspace.SourceVertices.Count - workspace.BaseVertexCount,
			entryOpeningRadius, exitOpeningRadius,
			entryRimOuterRadius, generateExitLip ? exitRimOuterRadius : 0f, minimumCoverageMargin,
			rimVertexCount, rimTriangleCount, entryLipStitched, exitLipStitched,
			cavityVertexCount, cavityTriangleCount,
			workspace.MinimumEntryRimFacingDot, workspace.MinimumExitRimFacingDot,
			workspace.EntryRimSurfaceHitCount, workspace.ExitRimSurfaceHitCount,
			workspace.BoundaryComponents.Count, openBoundaryComponentCount, solidCanalSpanCount,
			entryLipCount, intermediateLipCount, exitFlapCount,
			classificationMilliseconds, cavityMilliseconds);
		return true;
	}

	private static bool ContainsVertex(ProceduralHeadWoundWorkspace workspace, in WoundProfile profile, int index)
	{
		if (workspace.InsideVertices.TryGetValue(index, out bool value)) return value;
		value = Contains(profile, workspace.SourceVertices[index]);
		workspace.InsideVertices.Add(index, value);
		return value;
	}

	private static bool ClipIntersectedTriangle(ProceduralHeadWoundWorkspace workspace, in WoundProfile profile,
		List<int> filtered, int submesh, int ia, int ib, int ic, int depth,
		float entryTargetEdgeSquared, float exitTargetEdgeSquared,
		ref int clippedTriangles, ref int removedTriangles, ref int entryRemovedTriangles, ref int exitRemovedTriangles,
		out string failure)
	{
		failure = null;
		Vector3 a = workspace.SourceVertices[ia];
		Vector3 b = workspace.SourceVertices[ib];
		Vector3 c = workspace.SourceVertices[ic];
		bool insideA = ContainsVertex(workspace, profile, ia);
		bool insideB = ContainsVertex(workspace, profile, ib);
		bool insideC = ContainsVertex(workspace, profile, ic);
		if (!insideA && !insideB && !insideC && !TriangleIntersectsVolume(profile, a, b, c))
		{
			filtered.Add(ia);
			filtered.Add(ib);
			filtered.Add(ic);
			return true;
		}

		bool fullyInside = insideA && insideB && insideC;
		Vector3 centroid = (a + b + c) / 3f;
		float normalizedAxial = Mathf.Clamp01(Vector3.Dot(centroid - profile.Entry, profile.Axis) / profile.Length);
		float targetEdgeSquared = Mathf.Lerp(entryTargetEdgeSquared, exitTargetEdgeSquared, normalizedAxial);
		float maximumEdgeSquared = Mathf.Max((b - a).sqrMagnitude,
			Mathf.Max((c - b).sqrMagnitude, (a - c).sqrMagnitude));
		if (fullyInside)
		{
			removedTriangles++;
			CountOpeningFragment(profile, centroid, ref entryRemovedTriangles, ref exitRemovedTriangles);
			return true;
		}
		bool anyInside = insideA || insideB || insideC;
		if (depth >= MaximumRefinementDepth || maximumEdgeSquared <= targetEdgeSquared)
		{
			if (!anyInside)
			{
				if (Contains(profile, centroid))
				{
					int center = workspace.AddCentroidVertex(ia, ib, ic, workspace.GeneratedVertexLimit);
					if (center < 0)
					{
						failure = $"local wound clipping exceeded {workspace.GeneratedVertexLimit} generated head vertices";
						return false;
					}
					return ClipLeafTriangle(workspace, profile, filtered, submesh, ia, ib, center, ref clippedTriangles,
						ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure) &&
						ClipLeafTriangle(workspace, profile, filtered, submesh, ib, ic, center, ref clippedTriangles,
							ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure) &&
						ClipLeafTriangle(workspace, profile, filtered, submesh, ic, ia, center, ref clippedTriangles,
							ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure);
				}
				removedTriangles++;
				CountOpeningFragment(profile, centroid, ref entryRemovedTriangles, ref exitRemovedTriangles);
				return true;
			}
			return ClipLeafTriangle(workspace, profile, filtered, submesh, ia, ib, ic, ref clippedTriangles,
				ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure);
		}

		int iab = workspace.GetOrCreateMidpointVertex(ia, ib, workspace.GeneratedVertexLimit);
		int ibc = workspace.GetOrCreateMidpointVertex(ib, ic, workspace.GeneratedVertexLimit);
		int ica = workspace.GetOrCreateMidpointVertex(ic, ia, workspace.GeneratedVertexLimit);
		if (iab < 0 || ibc < 0 || ica < 0)
		{
			failure = $"local wound refinement exceeded {workspace.GeneratedVertexLimit} generated head vertices";
			return false;
		}

		int nextDepth = depth + 1;
		return ClipIntersectedTriangle(workspace, profile, filtered, submesh, ia, iab, ica, nextDepth,
				entryTargetEdgeSquared, exitTargetEdgeSquared,
				ref clippedTriangles, ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure) &&
			ClipIntersectedTriangle(workspace, profile, filtered, submesh, iab, ib, ibc, nextDepth,
				entryTargetEdgeSquared, exitTargetEdgeSquared,
				ref clippedTriangles, ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure) &&
			ClipIntersectedTriangle(workspace, profile, filtered, submesh, ica, ibc, ic, nextDepth,
				entryTargetEdgeSquared, exitTargetEdgeSquared,
				ref clippedTriangles, ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure) &&
			ClipIntersectedTriangle(workspace, profile, filtered, submesh, iab, ibc, ica, nextDepth,
				entryTargetEdgeSquared, exitTargetEdgeSquared,
				ref clippedTriangles, ref removedTriangles, ref entryRemovedTriangles, ref exitRemovedTriangles, out failure);
	}

	private static bool ClipLeafTriangle(ProceduralHeadWoundWorkspace workspace, in WoundProfile profile,
		List<int> filtered, int submesh, int ia, int ib, int ic, ref int clippedTriangles, ref int removedTriangles,
		ref int entryRemovedTriangles, ref int exitRemovedTriangles, out string failure)
	{
		failure = null;
		bool insideA = ContainsVertex(workspace, profile, ia);
		bool insideB = ContainsVertex(workspace, profile, ib);
		bool insideC = ContainsVertex(workspace, profile, ic);
		int mask = (insideA ? 1 : 0) | (insideB ? 2 : 0) | (insideC ? 4 : 0);
		if (mask == 0)
		{
			AddHeadTriangle(workspace, filtered, ia, ib, ic);
			return true;
		}
		Vector3 centroid = (workspace.SourceVertices[ia] + workspace.SourceVertices[ib] + workspace.SourceVertices[ic]) / 3f;
		removedTriangles++;
		CountOpeningFragment(profile, centroid, ref entryRemovedTriangles, ref exitRemovedTriangles);
		if (mask == 7) return true;

		int iab = -1;
		int ibc = -1;
		int ica = -1;
		int boundaryA = -1;
		int boundaryB = -1;
		if (insideA != insideB && !TryCreateBoundaryVertex(workspace, profile, ia, ib, insideA, out iab))
		{
			failure = $"local wound clipping exceeded {workspace.GeneratedVertexLimit} generated head vertices";
			return false;
		}
		if (insideB != insideC && !TryCreateBoundaryVertex(workspace, profile, ib, ic, insideB, out ibc))
		{
			failure = $"local wound clipping exceeded {workspace.GeneratedVertexLimit} generated head vertices";
			return false;
		}
		if (insideC != insideA && !TryCreateBoundaryVertex(workspace, profile, ic, ia, insideC, out ica))
		{
			failure = $"local wound clipping exceeded {workspace.GeneratedVertexLimit} generated head vertices";
			return false;
		}

		switch (mask)
		{
			case 1:
				boundaryA = iab;
				boundaryB = ica;
				AddHeadTriangle(workspace, filtered, iab, ib, ic);
				AddHeadTriangle(workspace, filtered, iab, ic, ica);
				break;
			case 2:
				boundaryA = iab;
				boundaryB = ibc;
				AddHeadTriangle(workspace, filtered, ia, iab, ibc);
				AddHeadTriangle(workspace, filtered, ia, ibc, ic);
				break;
			case 3:
				boundaryA = ibc;
				boundaryB = ica;
				AddHeadTriangle(workspace, filtered, ibc, ic, ica);
				break;
			case 4:
				boundaryA = ica;
				boundaryB = ibc;
				AddHeadTriangle(workspace, filtered, ica, ia, ib);
				AddHeadTriangle(workspace, filtered, ica, ib, ibc);
				break;
			case 5:
				boundaryA = iab;
				boundaryB = ibc;
				AddHeadTriangle(workspace, filtered, iab, ib, ibc);
				break;
			case 6:
				boundaryA = ica;
				boundaryB = iab;
				AddHeadTriangle(workspace, filtered, ica, ia, iab);
				break;
		}
		workspace.AddCutBoundaryEdge(boundaryA, boundaryB, submesh);
		clippedTriangles++;
		return true;
	}

	private static bool TryCreateBoundaryVertex(ProceduralHeadWoundWorkspace workspace, in WoundProfile profile,
		int a, int b, bool aInside, out int result)
	{
		if (workspace.TryGetIntersectionVertex(a, b, out result)) return true;
		Vector3 start = workspace.SourceVertices[a];
		Vector3 end = workspace.SourceVertices[b];
		float low = 0f;
		float high = 1f;
		for (int iteration = 0; iteration < EdgeRootIterations; iteration++)
		{
			float middle = (low + high) * 0.5f;
			bool middleInside = Contains(profile, Vector3.LerpUnclamped(start, end, middle));
			if (middleInside == aInside) low = middle;
			else high = middle;
		}
		result = workspace.GetOrCreateIntersectionVertex(a, b, (low + high) * 0.5f,
			workspace.GeneratedVertexLimit);
		return result >= 0;
	}

	private static void AddHeadTriangle(ProceduralHeadWoundWorkspace workspace, List<int> filtered,
		int a, int b, int c)
	{
		if (a < 0 || b < 0 || c < 0 || a == b || b == c || c == a) return;
		Vector3 va = workspace.SourceVertices[a];
		Vector3 vb = workspace.SourceVertices[b];
		Vector3 vc = workspace.SourceVertices[c];
		if (Vector3.Cross(vb - va, vc - va).sqrMagnitude <= 0.0000000000000001f) return;
		filtered.Add(a);
		filtered.Add(b);
		filtered.Add(c);
	}

	private static void CountOpeningFragment(in WoundProfile profile, Vector3 centroid,
		ref int entryRemovedTriangles, ref int exitRemovedTriangles)
	{
		float axial = Vector3.Dot(centroid - profile.Entry, profile.Axis);
		if (axial <= profile.Length * 0.35f) entryRemovedTriangles++;
		if (axial >= profile.Length * 0.65f) exitRemovedTriangles++;
	}

	private static void AccumulateOpeningCoverage(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		Vector3 a, Vector3 b, Vector3 c)
	{
		Vector3 centroid = (a + b + c) / 3f;
		bool entry = Vector3.Dot(centroid - profile.Entry, profile.Axis) <= profile.Length * 0.5f;
		Vector3 opening = entry ? profile.Entry : profile.Exit;
		AccumulateOpeningVertex(workspace, profile, opening, entry, a);
		AccumulateOpeningVertex(workspace, profile, opening, entry, b);
		AccumulateOpeningVertex(workspace, profile, opening, entry, c);
		AccumulateOpeningSectorCoverage(workspace, profile, opening, entry, a, b, c);
	}

	private static void AccumulateOpeningVertex(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		Vector3 opening, bool entry, Vector3 position)
	{
		Vector3 relative = position - opening;
		float axial = Vector3.Dot(relative, profile.Axis);
		float radialDistance = (relative - profile.Axis * axial).magnitude;
		if (entry)
		{
			workspace.EntryOpeningCoverageRadius = Mathf.Max(workspace.EntryOpeningCoverageRadius, radialDistance);
		}
		else
		{
			workspace.ExitOpeningCoverageRadius = Mathf.Max(workspace.ExitOpeningCoverageRadius, radialDistance);
		}
	}

	private static void AccumulateOpeningSectorCoverage(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		Vector3 opening, bool entry, Vector3 a, Vector3 b, Vector3 c)
	{
		int profileRing = entry ? 0 : AxialRingCount - 1;
		float[] coverage = entry ? workspace.EntryOpeningCoverageRadii : workspace.ExitOpeningCoverageRadii;
		Vector2 pa = ProjectToOpeningPlane(a - opening, profile);
		Vector2 pb = ProjectToOpeningPlane(b - opening, profile);
		Vector2 pc = ProjectToOpeningPlane(c - opening, profile);
		for (int segment = 0; segment < AngularSegmentCount; segment++)
		{
			float angle = segment / (float)AngularSegmentCount * TwoPi + profile.RingPhases[profileRing];
			Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
			float extent = ProjectedTriangleRayExtent(direction, pa, pb, pc);
			if (extent > coverage[segment]) coverage[segment] = extent;
		}
	}

	private static Vector2 ProjectToOpeningPlane(Vector3 relative, WoundProfile profile)
	{
		return new Vector2(Vector3.Dot(relative, profile.BasisU), Vector3.Dot(relative, profile.BasisV));
	}

	private static float ProjectedTriangleRayExtent(Vector2 direction, Vector2 a, Vector2 b, Vector2 c)
	{
		float maximum = 0f;
		AccumulateRayEdgeExtent(direction, a, b, ref maximum);
		AccumulateRayEdgeExtent(direction, b, c, ref maximum);
		AccumulateRayEdgeExtent(direction, c, a, ref maximum);
		return maximum;
	}

	private static void AccumulateRayEdgeExtent(Vector2 direction, Vector2 start, Vector2 end, ref float maximum)
	{
		Vector2 edge = end - start;
		float denominator = Cross2(direction, edge);
		if (Mathf.Abs(denominator) <= 0.0000001f) return;
		float rayDistance = Cross2(start, edge) / denominator;
		float edgePosition = Cross2(start, direction) / denominator;
		if (rayDistance >= 0f && edgePosition >= 0f && edgePosition <= 1f)
		{
			maximum = Mathf.Max(maximum, rayDistance);
		}
	}

	private static float Cross2(Vector2 a, Vector2 b)
	{
		return a.x * b.y - a.y * b.x;
	}

	private static void BuildRetainedRimSurfaceCandidates(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, bool throughShot)
	{
		workspace.EntryRimSurfaceCandidates.Clear();
		workspace.ExitRimSurfaceCandidates.Clear();
		float sampleRadius = profile.MaximumRadius + LipMaximumOverlapWidth +
			BoundaryContactExpansion + ClassificationPadding;
		float sampleDepth = profile.Length + BoundarySampleOutside;
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> retained = workspace.GetFilteredSubmesh(submesh);
			for (int index = 0; index + 2 < retained.Count; index += 3)
			{
				int ia = retained[index];
				int ib = retained[index + 1];
				int ic = retained[index + 2];
				Vector3 a = workspace.SourceVertices[ia];
				Vector3 b = workspace.SourceVertices[ib];
				Vector3 c = workspace.SourceVertices[ic];
				if (TryCreateRimSurfaceCandidate(ia, ib, ic, a, b, c, profile.Entry, -profile.Axis,
					profile.BasisU, profile.BasisV, sampleRadius, sampleDepth,
					out RimSurfaceCandidate entryCandidate))
				{
					workspace.EntryRimSurfaceCandidates.Add(entryCandidate);
				}
				if (throughShot && TryCreateRimSurfaceCandidate(ia, ib, ic, a, b, c,
					profile.Exit, profile.Axis, profile.BasisU, profile.BasisV, sampleRadius,
					sampleDepth, out RimSurfaceCandidate exitCandidate))
				{
					workspace.ExitRimSurfaceCandidates.Add(exitCandidate);
				}
			}
		}
	}


	private static void CollectClippedBoundaryVertices(ProceduralHeadWoundWorkspace workspace, WoundProfile profile)
	{
		workspace.RetainedCutBoundaryVertices.Clear();
		workspace.EntryCutBoundaryVertices.Clear();
		workspace.ExitCutBoundaryVertices.Clear();
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> retained = workspace.GetFilteredSubmesh(submesh);
			for (int index = 0; index < retained.Count; index++)
			{
				int vertex = retained[index];
				if (workspace.CutBoundaryVertices.Contains(vertex))
				{
					workspace.RetainedCutBoundaryVertices.Add(vertex);
				}
			}
		}

		bool hasNormals = workspace.SourceNormals.Count == workspace.SourceVertices.Count;
		foreach (int index in workspace.RetainedCutBoundaryVertices)
		{
			if (!ValidVertexIndex(workspace, index)) continue;
			Vector3 position = workspace.SourceVertices[index];
			float facing = hasNormals ? Vector3.Dot(workspace.SourceNormals[index], profile.Axis) : 0f;
			if (facing < -0.05f)
			{
				workspace.EntryCutBoundaryVertices.Add(index);
			}
			else if (facing > 0.05f)
			{
				workspace.ExitCutBoundaryVertices.Add(index);
			}
			else if ((position - profile.Entry).sqrMagnitude <= (position - profile.Exit).sqrMagnitude)
			{
				workspace.EntryCutBoundaryVertices.Add(index);
			}
			else
			{
				workspace.ExitCutBoundaryVertices.Add(index);
			}
		}
		SortBoundaryVertices(workspace, profile, workspace.EntryCutBoundaryVertices);
		SortBoundaryVertices(workspace, profile, workspace.ExitCutBoundaryVertices);
	}

	private static void SortBoundaryVertices(ProceduralHeadWoundWorkspace workspace, WoundProfile profile, List<int> vertices)
	{
		vertices.Sort((left, right) =>
		{
			Vector3 leftRelative = workspace.SourceVertices[left] - profile.Entry;
			Vector3 rightRelative = workspace.SourceVertices[right] - profile.Entry;
			float leftAngle = BoundaryAngle(profile, leftRelative);
			float rightAngle = BoundaryAngle(profile, rightRelative);
			int comparison = leftAngle.CompareTo(rightAngle);
			if (comparison != 0) return comparison;
			comparison = leftRelative.x.CompareTo(rightRelative.x);
			if (comparison != 0) return comparison;
			comparison = leftRelative.y.CompareTo(rightRelative.y);
			return comparison != 0 ? comparison : leftRelative.z.CompareTo(rightRelative.z);
		});
	}

	private static float BoundaryAngle(WoundProfile profile, Vector3 relative)
	{
		float angle = Mathf.Atan2(Vector3.Dot(relative, profile.BasisV), Vector3.Dot(relative, profile.BasisU));
		return angle < 0f ? angle + TwoPi : angle;
	}

	private static bool TrySampleClippedBoundaryLoop(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool entry, int segmentCount, List<RimSurfaceCandidate> candidates, uint shapeSeed, out string failure)
	{
		failure = null;
		List<int> cutVertices = entry ? workspace.EntryCutBoundaryVertices : workspace.ExitCutBoundaryVertices;
		if (cutVertices.Count < 3)
		{
			failure = $"{(entry ? "entry" : "exit")} clipped boundary contained only {cutVertices.Count} usable vertices";
			return false;
		}
		Vector3[] positions = entry ? workspace.EntryBoundaryPositions : workspace.ExitBoundaryPositions;
		Vector3[] normals = entry ? workspace.EntryBoundaryNormals : workspace.ExitBoundaryNormals;
		Vector3[] outerPositions = entry ? workspace.EntryLipOuterPositions : workspace.ExitLipOuterPositions;
		Vector3[] outerNormals = entry ? workspace.EntryLipOuterNormals : workspace.ExitLipOuterNormals;
		Vector3 opening = entry ? profile.Entry : profile.Exit;
		Vector3 outwardAxis = entry ? -profile.Axis : profile.Axis;
		float maximumBoundaryRadius = 0f;
		float minimumCoverageMargin = float.MaxValue;
		for (int segment = 0; segment < segmentCount; segment++)
		{
			float angle = segment / (float)segmentCount * TwoPi;
			SampleSortedBoundaryVertex(workspace, profile, cutVertices, angle, outwardAxis,
				out Vector3 position, out Vector3 normal);
			Vector3 relative = position - opening;
			float axial = Vector3.Dot(relative, profile.Axis);
			Vector3 radial = relative - profile.Axis * axial;
			float boundaryRadius = radial.magnitude;
			if (boundaryRadius <= 0.000001f)
			{
				radial = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
				boundaryRadius = 0f;
			}
			Vector3 radialDirection = radial.normalized;
			float openingRadius = SampleRadius(profile, entry ? 0f : 1f, angle);
			float overlapVariation = Mathf.Lerp(0.78f, 1.22f,
				SmoothRimVariation01(shapeSeed, segment, segmentCount, 0x9E3779B9u));
			float overlapWidth = Mathf.Clamp(openingRadius * LipOverlapRadiusScale * overlapVariation,
				LipMinimumOverlapWidth, LipMaximumOverlapWidth);
			if (!TryFindRetainedOuterContact(workspace, profile, candidates, opening, position, normal, radialDirection,
				boundaryRadius, overlapWidth, outwardAxis, out Vector3 outerPosition, out Vector3 outerNormal))
			{
				Vector3 tangentOut = Vector3.ProjectOnPlane(radialDirection, normal);
				if (tangentOut.sqrMagnitude <= 0.000001f) tangentOut = radialDirection;
				outerPosition = position + tangentOut.normalized * overlapWidth + normal * RimSurfaceClearance;
				outerNormal = normal;
			}
			Vector3 outerRelative = outerPosition - opening;
			float outerAxial = Vector3.Dot(outerRelative, profile.Axis);
			float outerRadius = (outerRelative - profile.Axis * outerAxial).magnitude;
			float coverageMargin = outerRadius - boundaryRadius;
			if (coverageMargin < TargetRimCoverageMargin)
			{
				outerPosition += radialDirection * (TargetRimCoverageMargin - coverageMargin);
				outerRelative = outerPosition - opening;
				outerAxial = Vector3.Dot(outerRelative, profile.Axis);
				outerRadius = (outerRelative - profile.Axis * outerAxial).magnitude;
				coverageMargin = outerRadius - boundaryRadius;
			}
			positions[segment] = position;
			normals[segment] = normal;
			outerPositions[segment] = outerPosition;
			outerNormals[segment] = outerNormal;
			maximumBoundaryRadius = Mathf.Max(maximumBoundaryRadius, boundaryRadius);
			minimumCoverageMargin = Mathf.Min(minimumCoverageMargin, coverageMargin);
		}
		if (entry)
		{
			workspace.EntryRimSurfaceHitCount = segmentCount;
			workspace.EntryOpeningCoverageRadius = maximumBoundaryRadius;
			workspace.EntryMinimumRimCoverageMargin = minimumCoverageMargin;
		}
		else
		{
			workspace.ExitRimSurfaceHitCount = segmentCount;
			workspace.ExitOpeningCoverageRadius = maximumBoundaryRadius;
			workspace.ExitMinimumRimCoverageMargin = minimumCoverageMargin;
		}
		return true;
	}

	private static void SampleSortedBoundaryVertex(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		List<int> vertices, float targetAngle, Vector3 outwardAxis, out Vector3 position, out Vector3 normal)
	{
		int upper = 0;
		while (upper < vertices.Count && BoundaryAngle(profile, workspace.SourceVertices[vertices[upper]] - profile.Entry) < targetAngle)
		{
			upper++;
		}
		int lower = upper == 0 ? vertices.Count - 1 : upper - 1;
		int wrappedUpper = upper == vertices.Count ? 0 : upper;
		float lowerAngle = BoundaryAngle(profile, workspace.SourceVertices[vertices[lower]] - profile.Entry);
		float upperAngle = BoundaryAngle(profile, workspace.SourceVertices[vertices[wrappedUpper]] - profile.Entry);
		if (upper == 0) lowerAngle -= TwoPi;
		if (upper == vertices.Count) upperAngle += TwoPi;
		float adjustedTarget = targetAngle;
		if (upper == vertices.Count) adjustedTarget += TwoPi;
		float denominator = upperAngle - lowerAngle;
		float interpolation = denominator > 0.000001f ? Mathf.Clamp01((adjustedTarget - lowerAngle) / denominator) : 0f;
		int lowerIndex = vertices[lower];
		int upperIndex = vertices[wrappedUpper];
		position = Vector3.LerpUnclamped(workspace.SourceVertices[lowerIndex], workspace.SourceVertices[upperIndex], interpolation);
		if (workspace.SourceNormals.Count == workspace.SourceVertices.Count)
		{
			normal = Vector3.LerpUnclamped(workspace.SourceNormals[lowerIndex], workspace.SourceNormals[upperIndex], interpolation);
		}
		else
		{
			normal = outwardAxis;
		}
		if (normal.sqrMagnitude <= 0.000001f) normal = outwardAxis;
		normal.Normalize();
		if (Vector3.Dot(normal, outwardAxis) < 0f) normal = -normal;
	}

	private static bool TryFindRetainedOuterContact(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		List<RimSurfaceCandidate> candidates, Vector3 opening, Vector3 boundary, Vector3 boundaryNormal,
		Vector3 radialDirection, float boundaryRadius,
		float overlapWidth, Vector3 outwardAxis, out Vector3 position, out Vector3 normal)
	{
		position = default;
		normal = default;
		Vector3 tangentOut = Vector3.ProjectOnPlane(radialDirection, boundaryNormal);
		if (tangentOut.sqrMagnitude <= 0.000001f) tangentOut = radialDirection;
		Vector3 target = boundary + tangentOut.normalized * overlapWidth;
		float maximumDistance = overlapWidth + 0.006f;
		float bestSquared = maximumDistance * maximumDistance;
		Vector3 targetRelative = target - opening;
		float targetU = Vector3.Dot(targetRelative, profile.BasisU);
		float targetV = Vector3.Dot(targetRelative, profile.BasisV);
		bool found = false;
		for (int index = 0; index < candidates.Count; index++)
		{
			RimSurfaceCandidate surface = candidates[index];
			if (targetU < surface.MinimumU - maximumDistance || targetU > surface.MaximumU + maximumDistance ||
				targetV < surface.MinimumV - maximumDistance || targetV > surface.MaximumV + maximumDistance)
			{
				continue;
			}
			Vector3 a = workspace.SourceVertices[surface.A];
			Vector3 b = workspace.SourceVertices[surface.B];
			Vector3 c = workspace.SourceVertices[surface.C];
			Vector3 candidate = ClosestPointOnTriangle(target, a, b, c);
			Vector3 relative = candidate - opening;
			float axial = Vector3.Dot(relative, profile.Axis);
			float candidateRadius = (relative - profile.Axis * axial).magnitude;
			float squared = (candidate - target).sqrMagnitude;
			if (candidateRadius < boundaryRadius + MinimumRimCoverageMargin || squared >= bestSquared) continue;
			Vector3 candidateNormal = Vector3.Cross(b - a, c - a);
			if (candidateNormal.sqrMagnitude <= 0.000000001f) continue;
			candidateNormal.Normalize();
			if (Vector3.Dot(candidateNormal, outwardAxis) < 0f) candidateNormal = -candidateNormal;
			bestSquared = squared;
			position = candidate + candidateNormal * RimSurfaceClearance;
			normal = candidateNormal;
			found = true;
		}
		return found;
	}

	private static bool TrySampleBoundaryLoop(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool entry, int segmentCount, List<RimSurfaceCandidate> candidates, uint shapeSeed, out string failure)
	{
		failure = null;
		if (candidates.Count == 0)
		{
			failure = $"{(entry ? "entry" : "exit")} opening has no nearby LOD0 surface candidates";
			return false;
		}
		Vector3[] positions = entry ? workspace.EntryBoundaryPositions : workspace.ExitBoundaryPositions;
		Vector3[] normals = entry ? workspace.EntryBoundaryNormals : workspace.ExitBoundaryNormals;
		Vector3[] outerPositions = entry ? workspace.EntryLipOuterPositions : workspace.ExitLipOuterPositions;
		Vector3[] outerNormals = entry ? workspace.EntryLipOuterNormals : workspace.ExitLipOuterNormals;
		Vector3 opening = entry ? profile.Entry : profile.Exit;
		Vector3 outwardAxis = entry ? -profile.Axis : profile.Axis;
		float outsideAxial = entry ? -BoundarySampleOutside : profile.Length + BoundarySampleOutside;
		// Follow the mathematical cone generator across the complete available
		// entry-to-exit depth. Oblique openings can meet the skull much deeper on
		// one sector than another; a fixed half-depth limit incorrectly rejected
		// those valid projected cutouts.
		float insideAxial = entry
			? profile.Length + BoundarySampleOutside
			: -BoundarySampleOutside;
		int hitCount = 0;
		float maximumBoundaryRadius = 0f;
		float minimumCoverageMargin = float.MaxValue;
		for (int segment = 0; segment < segmentCount; segment++)
		{
			float angle = segment / (float)segmentCount * TwoPi;
			float openingRadius = SampleRadius(profile, entry ? 0f : 1f, angle);
			float overlapVariation = Mathf.Lerp(0.78f, 1.22f,
				SmoothRimVariation01(shapeSeed, segment, segmentCount, 0x9E3779B9u));
			float overlapWidth = Mathf.Clamp(openingRadius * LipOverlapRadiusScale * overlapVariation,
				LipMinimumOverlapWidth, LipMaximumOverlapWidth);
			Vector3 outside = BoundaryGeneratorPoint(profile, outsideAxial, angle, BoundaryContactExpansion);
			Vector3 inside = BoundaryGeneratorPoint(profile, insideAxial, angle, BoundaryContactExpansion);
			if (!TryIntersectSourceSurface(workspace, candidates, outside, inside, outwardAxis,
				opening, profile.BasisU, profile.BasisV,
				out Vector3 position, out Vector3 normal))
			{
				failure = $"{(entry ? "entry" : "exit")} boundary generator could not intersect the LOD0 skull surface at segment {segment}";
				return false;
			}
			Vector3 outerOutside = BoundaryGeneratorPoint(profile, outsideAxial, angle,
				BoundaryContactExpansion + overlapWidth);
			Vector3 outerInside = BoundaryGeneratorPoint(profile, insideAxial, angle,
				BoundaryContactExpansion + overlapWidth);
			if (!TryIntersectSourceSurface(workspace, candidates, outerOutside, outerInside, outwardAxis,
				opening, profile.BasisU, profile.BasisV,
				out Vector3 outerPosition, out Vector3 outerNormal))
			{
				failure = $"{(entry ? "entry" : "exit")} exterior lip overlap could not sample retained LOD0 surface at segment {segment}";
				return false;
			}
			Vector3 boundaryRelative = position - opening;
			float boundaryAxial = Vector3.Dot(boundaryRelative, profile.Axis);
			float boundaryRadius = (boundaryRelative - profile.Axis * boundaryAxial).magnitude;
			Vector3 outerRelative = outerPosition - opening;
			float outerAxial = Vector3.Dot(outerRelative, profile.Axis);
			float outerRadius = (outerRelative - profile.Axis * outerAxial).magnitude;
			float coverageMargin = outerRadius - boundaryRadius;
			if (!FinitePositive(boundaryRadius) || coverageMargin < MinimumRimCoverageMargin)
			{
				failure = $"{(entry ? "entry" : "exit")} exterior lip does not overlap retained surface at segment {segment} " +
					$"(boundary={boundaryRadius * 1000f:F2}mm, outer={outerRadius * 1000f:F2}mm)";
				return false;
			}
			positions[segment] = position;
			normals[segment] = normal;
			outerPositions[segment] = outerPosition;
			outerNormals[segment] = outerNormal;
			maximumBoundaryRadius = Mathf.Max(maximumBoundaryRadius, boundaryRadius);
			minimumCoverageMargin = Mathf.Min(minimumCoverageMargin, coverageMargin);
			hitCount++;
		}
		if (entry)
		{
			workspace.EntryRimSurfaceHitCount = hitCount;
			workspace.EntryOpeningCoverageRadius = maximumBoundaryRadius;
			workspace.EntryMinimumRimCoverageMargin = minimumCoverageMargin;
		}
		else
		{
			workspace.ExitRimSurfaceHitCount = hitCount;
			workspace.ExitOpeningCoverageRadius = maximumBoundaryRadius;
			workspace.ExitMinimumRimCoverageMargin = minimumCoverageMargin;
		}
		return true;
	}

	private static Vector3 BoundaryGeneratorPoint(WoundProfile profile, float axial, float angle,
		float radialExpansion)
	{
		float normalized = Mathf.Clamp01(axial / profile.Length);
		float radius = SampleRadius(profile, normalized, angle) + radialExpansion;
		Vector3 radial = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
		return profile.Entry + profile.Axis * axial + radial * radius;
	}

	private static bool TryIntersectSourceSurface(ProceduralHeadWoundWorkspace workspace,
		List<RimSurfaceCandidate> candidates, Vector3 start, Vector3 end, Vector3 outwardAxis,
		Vector3 opening, Vector3 basisU, Vector3 basisV,
		out Vector3 position, out Vector3 normal)
	{
		position = default;
		normal = default;
		Vector3 delta = end - start;
		float length = delta.magnitude;
		if (length <= 0.000001f) return false;
		Vector3 direction = delta / length;
		Vector3 startRelative = start - opening;
		Vector3 endRelative = end - opening;
		float startU = Vector3.Dot(startRelative, basisU);
		float endU = Vector3.Dot(endRelative, basisU);
		float startV = Vector3.Dot(startRelative, basisV);
		float endV = Vector3.Dot(endRelative, basisV);
		float minimumU = Mathf.Min(startU, endU) - ClassificationPadding;
		float maximumU = Mathf.Max(startU, endU) + ClassificationPadding;
		float minimumV = Mathf.Min(startV, endV) - ClassificationPadding;
		float maximumV = Mathf.Max(startV, endV) + ClassificationPadding;
		float nearest = length + 1f;
		bool found = false;
		for (int index = 0; index < candidates.Count; index++)
		{
			RimSurfaceCandidate candidate = candidates[index];
			if (candidate.MaximumU < minimumU || candidate.MinimumU > maximumU ||
				candidate.MaximumV < minimumV || candidate.MinimumV > maximumV)
			{
				continue;
			}
			int ia = candidate.A;
			int ib = candidate.B;
			int ic = candidate.C;
			Vector3 a = workspace.SourceVertices[ia];
			Vector3 b = workspace.SourceVertices[ib];
			Vector3 c = workspace.SourceVertices[ic];
			if (!RayTriangleBarycentric(start, direction, a, b, c,
				out float distance, out float barycentricB, out float barycentricC) ||
				distance > length || distance >= nearest)
			{
				continue;
			}
			Vector3 candidateNormal;
			if (workspace.SourceNormals.Count == workspace.SourceVertices.Count)
			{
				float barycentricA = 1f - barycentricB - barycentricC;
				candidateNormal = workspace.SourceNormals[ia] * barycentricA +
					workspace.SourceNormals[ib] * barycentricB + workspace.SourceNormals[ic] * barycentricC;
			}
			else
			{
				candidateNormal = Vector3.Cross(b - a, c - a);
			}
			if (candidateNormal.sqrMagnitude <= 0.0000000001f) continue;
			candidateNormal.Normalize();
			if (Vector3.Dot(candidateNormal, outwardAxis) < 0f) candidateNormal = -candidateNormal;
			nearest = distance;
			position = start + direction * distance;
			normal = candidateNormal;
			found = true;
		}
		return found;
	}

	private static bool RayTriangleBarycentric(Vector3 origin, Vector3 direction,
		Vector3 a, Vector3 b, Vector3 c, out float distance, out float barycentricB, out float barycentricC)
	{
		distance = 0f;
		barycentricB = 0f;
		barycentricC = 0f;
		Vector3 edge1 = b - a;
		Vector3 edge2 = c - a;
		Vector3 p = Vector3.Cross(direction, edge2);
		float determinant = Vector3.Dot(edge1, p);
		if (Mathf.Abs(determinant) < 0.0000001f) return false;
		float inverse = 1f / determinant;
		Vector3 t = origin - a;
		barycentricB = Vector3.Dot(t, p) * inverse;
		if (barycentricB < 0f || barycentricB > 1f) return false;
		Vector3 q = Vector3.Cross(t, edge1);
		barycentricC = Vector3.Dot(direction, q) * inverse;
		if (barycentricC < 0f || barycentricB + barycentricC > 1f) return false;
		distance = Vector3.Dot(edge2, q) * inverse;
		return distance >= 0f;
	}

	private static bool TrySampleRimSurfaces(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool throughShot, uint rimSeed, out string failure)
	{
		if (!TrySampleOpeningSurface(workspace, profile, entry: true,
			workspace.EntryOpeningCoverageRadius, rimSeed ^ 0x2E7C8A61u, out failure))
		{
			return false;
		}
		if (throughShot && !TrySampleOpeningSurface(workspace, profile, entry: false,
			workspace.ExitOpeningCoverageRadius, rimSeed ^ 0xB59472D3u, out failure))
		{
			return false;
		}
		return true;
	}

	private static bool TrySampleOpeningSurface(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool entry, float openingCoverageRadius, uint shapeSeed, out string failure)
	{
		failure = null;
		int profileRing = entry ? 0 : AxialRingCount - 1;
		Vector3 opening = entry ? profile.Entry : profile.Exit;
		Vector3 outwardAxis = entry ? -profile.Axis : profile.Axis;
		float profileOuterRadius = MaximumRingRadius(profile, profileRing) + TargetRimCoverageMargin;
		float maximumSampleRadius = Mathf.Min(MaximumRimOuterRadius,
			Mathf.Max(openingCoverageRadius + TargetRimCoverageMargin, profileOuterRadius) *
			(1f + RimOuterIrregularity) + MaximumRimSurfaceRadialSearch + ClassificationPadding);
		List<RimSurfaceCandidate> candidates = entry ? workspace.EntryRimSurfaceCandidates : workspace.ExitRimSurfaceCandidates;
		float[] offsets = entry ? workspace.EntryRimSurfaceOffsets : workspace.ExitRimSurfaceOffsets;
		Vector3[] normals = entry ? workspace.EntryRimSurfaceNormals : workspace.ExitRimSurfaceNormals;
		byte[] hits = entry ? workspace.EntryRimSurfaceHits : workspace.ExitRimSurfaceHits;
		float[] openingCoverageRadii = entry ? workspace.EntryOpeningCoverageRadii : workspace.ExitOpeningCoverageRadii;
		float[] contactRadii = entry ? workspace.EntryRimContactRadii : workspace.ExitRimContactRadii;

		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> filtered = workspace.GetFilteredSubmesh(submesh);
			for (int index = 0; index + 2 < filtered.Count; index += 3)
			{
				int ia = filtered[index];
				int ib = filtered[index + 1];
				int ic = filtered[index + 2];
				if (!TryCreateRimSurfaceCandidate(ia, ib, ic, workspace.SourceVertices[ia], workspace.SourceVertices[ib],
					workspace.SourceVertices[ic], opening, outwardAxis, profile.BasisU, profile.BasisV,
					maximumSampleRadius, MaximumRimSurfaceSampleDepth, out RimSurfaceCandidate candidate))
				{
					continue;
				}
				candidates.Add(candidate);
			}
		}

		if (candidates.Count == 0)
		{
			failure = $"{(entry ? "entry" : "exit")} rim has no retained skull surface candidates";
			return false;
		}

		int hitCount = 0;
		float minimumCoverageMargin = float.MaxValue;
		for (int segment = 0; segment < AngularSegmentCount; segment++)
		{
			float angle = segment / (float)AngularSegmentCount * TwoPi + profile.RingPhases[profileRing];
			Vector3 radialDirection = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
			float profileRadius = profile.RingRadii[profileRing * AngularSegmentCount + segment];
			float localCoverage = openingCoverageRadii[segment];
			float minimumContactRadius = Mathf.Max(localCoverage + MinimumRimCoverageMargin, profileRadius + 0.00025f);
			float desiredBaseRadius = Mathf.Max(localCoverage + TargetRimCoverageMargin, profileRadius + TargetRimCoverageMargin);
			float desiredRadius = RimOuterRadiusForSegment(desiredBaseRadius, shapeSeed, segment);
			if (!FinitePositive(desiredRadius) || desiredRadius > MaximumRimOuterRadius)
			{
				failure = $"{(entry ? "entry" : "exit")} rim sector {segment} exceeds the bounded contact radius";
				return false;
			}
			if (!TryFindRimContactSurface(workspace, candidates, opening, outwardAxis, profile,
				radialDirection, desiredRadius, minimumContactRadius,
				out float contactRadius, out float distance, out Vector3 normal))
			{
				failure = $"{(entry ? "entry" : "exit")} rim contact could not sample retained skull surface at segment {segment} " +
					$"(localOpening={localCoverage * 1000f:F2}mm, desired={desiredRadius * 1000f:F2}mm, " +
					$"minimum={minimumContactRadius * 1000f:F2}mm)";
				return false;
			}
			contactRadii[segment] = contactRadius;
			minimumCoverageMargin = Mathf.Min(minimumCoverageMargin, contactRadius - localCoverage);
			offsets[segment] = RimSurfaceSampleOutside - distance;
			if (Vector3.Dot(normal, outwardAxis) < 0f) normal = -normal;
			normals[segment] = normal;
			hits[segment] = 1;
			hitCount++;
		}

		for (int ring = 1; ring < RimAddedRingCount; ring++)
		{
			float t = ring / (float)RimAddedRingCount;
			float smooth = t * t * (3f - 2f * t);
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				float angle = segment / (float)AngularSegmentCount * TwoPi + profile.RingPhases[profileRing];
				Vector3 radialDirection = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
				float innerRadius = profile.RingRadii[profileRing * AngularSegmentCount + segment] * CavityEndInsetScale;
				float radius = Mathf.Lerp(contactRadii[segment], innerRadius, smooth);
				if (!TrySampleRetainedSurfaceAtRadius(workspace, candidates, opening, outwardAxis, profile,
					radialDirection, radius, out float distance, out Vector3 normal))
				{
					continue;
				}
				int sample = ring * AngularSegmentCount + segment;
				offsets[sample] = RimSurfaceSampleOutside - distance;
				if (Vector3.Dot(normal, outwardAxis) < 0f) normal = -normal;
				normals[sample] = normal;
				hits[sample] = 1;
				hitCount++;
			}
		}

		if (entry)
		{
			workspace.EntryRimSurfaceHitCount = hitCount;
			workspace.EntryMinimumRimCoverageMargin = minimumCoverageMargin;
		}
		else
		{
			workspace.ExitRimSurfaceHitCount = hitCount;
			workspace.ExitMinimumRimCoverageMargin = minimumCoverageMargin;
		}
		return true;
	}

	private static bool TryFindRimContactSurface(ProceduralHeadWoundWorkspace workspace,
		List<RimSurfaceCandidate> candidates, Vector3 opening, Vector3 outwardAxis, WoundProfile profile,
		Vector3 radialDirection, float desiredRadius, float minimumRadius,
		out float contactRadius, out float distance, out Vector3 normal)
	{
		contactRadius = desiredRadius;
		if (TrySampleRetainedSurfaceAtRadius(workspace, candidates, opening, outwardAxis, profile,
			radialDirection, contactRadius, out distance, out normal))
		{
			return true;
		}

		int steps = Mathf.CeilToInt(MaximumRimSurfaceRadialSearch / RimSurfaceRadialSearchStep);
		for (int step = 1; step <= steps; step++)
		{
			float delta = step * RimSurfaceRadialSearchStep;
			float inwardRadius = desiredRadius - delta;
			if (inwardRadius >= minimumRadius && TrySampleRetainedSurfaceAtRadius(workspace, candidates,
				opening, outwardAxis, profile, radialDirection, inwardRadius, out distance, out normal))
			{
				contactRadius = inwardRadius;
				return true;
			}
			float outwardRadius = desiredRadius + delta;
			if (outwardRadius <= MaximumRimOuterRadius && TrySampleRetainedSurfaceAtRadius(workspace, candidates,
				opening, outwardAxis, profile, radialDirection, outwardRadius, out distance, out normal))
			{
				contactRadius = outwardRadius;
				return true;
			}
		}
		distance = 0f;
		normal = Vector3.zero;
		return false;
	}

	private static bool TrySampleRetainedSurfaceAtRadius(ProceduralHeadWoundWorkspace workspace,
		List<RimSurfaceCandidate> candidates, Vector3 opening, Vector3 outwardAxis, WoundProfile profile,
		Vector3 radialDirection, float radius, out float distance, out Vector3 normal)
	{
		Vector3 radialOffset = radialDirection * radius;
		Vector3 rayOrigin = opening + radialOffset + outwardAxis * RimSurfaceSampleOutside;
		float sampleU = Vector3.Dot(radialOffset, profile.BasisU);
		float sampleV = Vector3.Dot(radialOffset, profile.BasisV);
		return TryRaycastRetainedSurface(workspace, candidates, rayOrigin, -outwardAxis, sampleU, sampleV,
			out distance, out normal);
	}

	private static bool TryCreateRimSurfaceCandidate(int ia, int ib, int ic, Vector3 a, Vector3 b, Vector3 c,
		Vector3 opening, Vector3 outwardAxis, Vector3 basisU, Vector3 basisV, float maximumRadius,
		float maximumSurfaceDepth,
		out RimSurfaceCandidate candidate)
	{
		candidate = default;
		Vector3 ra = a - opening;
		Vector3 rb = b - opening;
		Vector3 rc = c - opening;
		float axialA = Vector3.Dot(ra, outwardAxis);
		float axialB = Vector3.Dot(rb, outwardAxis);
		float axialC = Vector3.Dot(rc, outwardAxis);
		float minimumAxial = Mathf.Min(axialA, Mathf.Min(axialB, axialC));
		float maximumAxial = Mathf.Max(axialA, Mathf.Max(axialB, axialC));
		if (maximumAxial < -maximumSurfaceDepth || minimumAxial > RimSurfaceSampleOutside)
		{
			return false;
		}

		float ua = Vector3.Dot(ra, basisU);
		float ub = Vector3.Dot(rb, basisU);
		float uc = Vector3.Dot(rc, basisU);
		float va = Vector3.Dot(ra, basisV);
		float vb = Vector3.Dot(rb, basisV);
		float vc = Vector3.Dot(rc, basisV);
		float minimumU = Mathf.Min(ua, Mathf.Min(ub, uc));
		float maximumU = Mathf.Max(ua, Mathf.Max(ub, uc));
		float minimumV = Mathf.Min(va, Mathf.Min(vb, vc));
		float maximumV = Mathf.Max(va, Mathf.Max(vb, vc));
		if (maximumU < -maximumRadius || minimumU > maximumRadius ||
			maximumV < -maximumRadius || minimumV > maximumRadius)
		{
			return false;
		}
		candidate = new RimSurfaceCandidate(ia, ib, ic, minimumU, maximumU, minimumV, maximumV);
		return true;
	}

	private static bool TryRaycastRetainedSurface(ProceduralHeadWoundWorkspace workspace,
		List<RimSurfaceCandidate> candidates, Vector3 origin, Vector3 direction, float sampleU, float sampleV,
		out float nearestDistance, out Vector3 nearestNormal)
	{
		nearestDistance = RimSurfaceSampleOutside + MaximumRimSurfaceSampleDepth;
		nearestNormal = Vector3.zero;
		bool found = false;
		for (int index = 0; index < candidates.Count; index++)
		{
			RimSurfaceCandidate candidate = candidates[index];
			if (sampleU < candidate.MinimumU || sampleU > candidate.MaximumU ||
				sampleV < candidate.MinimumV || sampleV > candidate.MaximumV)
			{
				continue;
			}
			Vector3 a = workspace.SourceVertices[candidate.A];
			Vector3 b = workspace.SourceVertices[candidate.B];
			Vector3 c = workspace.SourceVertices[candidate.C];
			if (!RayTriangle(origin, direction, a, b, c, out float distance) ||
				distance > nearestDistance)
			{
				continue;
			}
			Vector3 unscaledNormal = Vector3.Cross(b - a, c - a);
			float normalSquared = unscaledNormal.sqrMagnitude;
			if (normalSquared <= 0.000000000001f) continue;
			nearestDistance = distance;
			nearestNormal = unscaledNormal / Mathf.Sqrt(normalSquared);
			found = true;
		}
		return found;
	}


	private static WoundProfile BuildProfile(ProceduralHeadWoundWorkspace workspace,
		ProceduralWoundDescriptor descriptor, Vector3 axis, float length)
	{
		Vector3 reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
		Vector3 basisU = Vector3.Cross(axis, reference).normalized;
		Vector3 basisV = Vector3.Cross(axis, basisU).normalized;
		uint state = descriptor.Seed;
		float basePhase = (Next01(ref state) * 2f - 1f) * 0.12f;
		float axialTwist = (Next01(ref state) * 2f - 1f) * 0.16f;
		float phase3Entry = Next01(ref state) * TwoPi;
		float phase3Exit = phase3Entry + (Next01(ref state) * 2f - 1f) * 0.75f;
		float phase5Entry = Next01(ref state) * TwoPi;
		float phase5Exit = phase5Entry + (Next01(ref state) * 2f - 1f) * 0.75f;
		float maximumRadius = 0f;
		for (int ring = 0; ring < AxialRingCount; ring++)
		{
			workspace.RingMinimum[ring] = float.MaxValue; workspace.RingMaximum[ring] = 0;
			float axial = ring / (float)(AxialRingCount - 1);
			float baseRadius = Mathf.Lerp(descriptor.EntryRadius, descriptor.ExitRadius, axial);
			workspace.RingPhases[ring] = basePhase + axialTwist * (axial - 0.5f);
			float phase3 = Mathf.Lerp(phase3Entry, phase3Exit, axial);
			float phase5 = Mathf.Lerp(phase5Entry, phase5Exit, axial);
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				float angle = segment / (float)AngularSegmentCount * TwoPi + workspace.RingPhases[ring];
				float variation = Mathf.Sin(angle * 3f + phase3) * 0.10f + Mathf.Sin(angle * 5f + phase5) * 0.05f;
				float radius = baseRadius * (1f + variation);
				workspace.RingRadii[ring * AngularSegmentCount + segment] = radius;
				workspace.RingMinimum[ring] = Mathf.Min(workspace.RingMinimum[ring], radius);
				workspace.RingMaximum[ring] = Mathf.Max(workspace.RingMaximum[ring], radius);
				maximumRadius = Mathf.Max(maximumRadius, radius);
			}
		}
		return new WoundProfile(descriptor.Entry, descriptor.Exit, axis, basisU, basisV, length,
			descriptor.EntryRadius, descriptor.ExitRadius, maximumRadius,
			workspace.RingPhases, workspace.RingRadii, workspace.RingMinimum, workspace.RingMaximum);
	}

	private static bool TriangleIntersectsVolume(in WoundProfile profile, Vector3 a, Vector3 b, Vector3 c)
	{
		if (profile.Members != null)
		{
			for (int i = 0; i < profile.MemberCount; i++) if (TriangleApproachesWoundAxis(profile.Members[i], a, b, c) && TriangleIntersectsVolume(profile.Members[i], a, b, c)) return true;
			return false;
		}
		if (!TriangleApproachesWoundAxis(profile, a, b, c)) return false;
		Vector3 centroid = (a + b + c) / 3f;
		if (Contains(profile, a) || Contains(profile, b) || Contains(profile, c) || Contains(profile, centroid) ||
			Contains(profile, (a + b) * 0.5f) || Contains(profile, (b + c) * 0.5f) || Contains(profile, (c + a) * 0.5f))
		{
			return true;
		}

		Vector3 rayOrigin = profile.Entry - profile.Axis * SurfaceOverlap;
		if (RayTriangle(rayOrigin, profile.Axis, a, b, c, out float rayDistance) &&
			rayDistance <= profile.Length + SurfaceOverlap * 2f)
		{
			return true;
		}

		Vector3 axisStart = profile.Entry - profile.Axis * SurfaceOverlap;
		Vector3 axisEnd = profile.Exit + profile.Axis * SurfaceOverlap;
		if (EdgeApproachesVolume(profile, a, b, axisStart, axisEnd) ||
			EdgeApproachesVolume(profile, b, c, axisStart, axisEnd) ||
			EdgeApproachesVolume(profile, c, a, axisStart, axisEnd))
		{
			return true;
		}

		for (int ring = 0; ring < AxialRingCount; ring++)
		{
			float axial = ring / (float)(AxialRingCount - 1);
			Vector3 center = Vector3.Lerp(profile.Entry, profile.Exit, axial);
			Vector3 closest = ClosestPointOnTriangle(center, a, b, c);
			if (Contains(profile, closest))
			{
				return true;
			}
		}
		return false;
	}

	private static bool Contains(in WoundProfile profile, Vector3 point)
	{
		if (profile.Members != null)
		{
			for (int i = 0; i < profile.MemberCount; i++) if (Contains(profile.Members[i], point)) return true;
			return false;
		}
		Vector3 relative = point - profile.Entry;
		float axial = Vector3.Dot(relative, profile.Axis);
		if (axial < -SurfaceOverlap || axial > profile.Length + SurfaceOverlap)
		{
			return false;
		}
		float normalizedAxial = Mathf.Clamp01(axial / profile.Length);
		Vector3 radial = relative - profile.Axis * axial;
		float radialSquared = radial.sqrMagnitude;
		if (radialSquared > (profile.MaximumRadius + ClassificationPadding) * (profile.MaximumRadius + ClassificationPadding))
		{
			return false;
		}
		float ringPosition = normalizedAxial * (AxialRingCount - 1);
		int ring0 = Math.Min(AxialRingCount - 1, (int)ringPosition), ring1 = Math.Min(AxialRingCount - 1, ring0 + 1);
		float blend = ringPosition - ring0;
		float minimum = profile.RingMinimum[ring0] + (profile.RingMinimum[ring1] - profile.RingMinimum[ring0]) * blend + ClassificationPadding;
		if (radialSquared < minimum * minimum) return true;
		float maximum = profile.RingMaximum[ring0] + (profile.RingMaximum[ring1] - profile.RingMaximum[ring0]) * blend + ClassificationPadding;
		if (radialSquared > maximum * maximum) return false;
		float angle = Mathf.Atan2(Vector3.Dot(radial, profile.BasisV), Vector3.Dot(radial, profile.BasisU));
		if (angle < 0f) angle += TwoPi;
		float radius = SampleRadius(profile, normalizedAxial, angle) + ClassificationPadding;
		return radialSquared <= radius * radius;
	}

	private static float SampleRadius(in WoundProfile profile, float normalizedAxial, float worldAngle)
	{
		float ringPosition = Mathf.Clamp01(normalizedAxial) * (AxialRingCount - 1);
		int ring0 = Mathf.Min(AxialRingCount - 1, Mathf.FloorToInt(ringPosition));
		int ring1 = Mathf.Min(AxialRingCount - 1, ring0 + 1);
		float ringBlend = ringPosition - ring0;
		float radius0 = SampleRingRadius(profile, ring0, worldAngle);
		float radius1 = SampleRingRadius(profile, ring1, worldAngle);
		return Mathf.Lerp(radius0, radius1, ringBlend);
	}

	private static float SampleRingRadius(in WoundProfile profile, int ring, float worldAngle)
	{
		float localAngle = Mathf.Repeat(worldAngle - profile.RingPhases[ring], TwoPi);
		float segmentPosition = localAngle / TwoPi * AngularSegmentCount;
		int segment0 = Mathf.FloorToInt(segmentPosition) % AngularSegmentCount;
		int segment1 = (segment0 + 1) % AngularSegmentCount;
		float blend = segmentPosition - Mathf.Floor(segmentPosition);
		int offset = ring * AngularSegmentCount;
		return Mathf.Lerp(profile.RingRadii[offset + segment0], profile.RingRadii[offset + segment1], blend);
	}

	private static bool EdgeApproachesVolume(in WoundProfile profile, Vector3 edgeStart, Vector3 edgeEnd,
		Vector3 axisStart, Vector3 axisEnd)
	{
		ClosestSegmentPoints(edgeStart, edgeEnd, axisStart, axisEnd, out Vector3 edgePoint, out _);
		return Contains(profile, edgePoint);
	}

	private static bool BuildCutBoundaryComponents(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, out string failure)
	{
		failure = null;
		workspace.RetainedCutBoundaryVertices.Clear();
		for (int submesh = 0; submesh < workspace.SubMeshCount; submesh++)
		{
			IReadOnlyList<int> retained = workspace.GetFilteredSubmesh(submesh);
			for (int index = 0; index < retained.Count; index++)
			{
				int vertex = retained[index];
				if (workspace.CutBoundaryVertices.Contains(vertex))
				{
					workspace.RetainedCutBoundaryVertices.Add(vertex);
				}
			}
		}

		for (int edgeIndex = 0; edgeIndex < workspace.CutBoundaryEdges.Count; edgeIndex++)
		{
			CutBoundaryEdge edge = workspace.CutBoundaryEdges[edgeIndex];
			if (!workspace.RetainedCutBoundaryVertices.Contains(edge.A) ||
				!workspace.RetainedCutBoundaryVertices.Contains(edge.B))
			{
				workspace.VisitCutBoundaryEdge(edgeIndex);
				continue;
			}
			int canonicalA = workspace.GetCanonicalBoundaryVertex(edge.A);
			int canonicalB = workspace.GetCanonicalBoundaryVertex(edge.B);
			if (!workspace.AddCanonicalCutBoundaryEdge(canonicalA, canonicalB))
			{
				workspace.VisitCutBoundaryEdge(edgeIndex);
				continue;
			}
			workspace.CutBoundaryEdges[edgeIndex] = new CutBoundaryEdge(canonicalA, canonicalB, edge.Submesh);
			workspace.GetCutBoundaryAdjacency(canonicalA).Add(edgeIndex);
			workspace.GetCutBoundaryAdjacency(canonicalB).Add(edgeIndex);
		}
		BridgeBoundaryGraphGaps(workspace, profile);

		for (int edgeIndex = 0; edgeIndex < workspace.CutBoundaryEdges.Count; edgeIndex++)
		{
			if (workspace.IsCutBoundaryEdgeVisited(edgeIndex)) continue;
			CutBoundaryEdge seedEdge = workspace.CutBoundaryEdges[edgeIndex];
			if (!workspace.TryGetCutBoundaryAdjacency(seedEdge.A, out _) ||
				!workspace.TryGetCutBoundaryAdjacency(seedEdge.B, out _))
			{
				continue;
			}

			WoundBoundaryComponent component = workspace.RentBoundaryComponent();
			workspace.BoundaryTraversalEdges.Clear();
			workspace.BoundaryTraversalVertices.Clear();
			workspace.BoundaryTraversalEdges.Add(edgeIndex);
			while (workspace.BoundaryTraversalEdges.Count != 0)
			{
				int last = workspace.BoundaryTraversalEdges.Count - 1;
				int connectedEdge = workspace.BoundaryTraversalEdges[last];
				workspace.BoundaryTraversalEdges.RemoveAt(last);
				if (!workspace.VisitCutBoundaryEdge(connectedEdge)) continue;
				CutBoundaryEdge edge = workspace.CutBoundaryEdges[connectedEdge];
				if (workspace.BoundaryTraversalVertices.Add(edge.A)) component.Vertices.Add(edge.A);
				if (workspace.BoundaryTraversalVertices.Add(edge.B)) component.Vertices.Add(edge.B);
				component.EdgeSubmeshes.Add(edge.Submesh);
				if (workspace.TryGetCutBoundaryAdjacency(edge.A, out List<int> adjacencyA))
				{
					for (int adjacent = 0; adjacent < adjacencyA.Count; adjacent++)
					{
						if (!workspace.IsCutBoundaryEdgeVisited(adjacencyA[adjacent]))
							workspace.BoundaryTraversalEdges.Add(adjacencyA[adjacent]);
					}
				}
				if (workspace.TryGetCutBoundaryAdjacency(edge.B, out List<int> adjacencyB))
				{
					for (int adjacent = 0; adjacent < adjacencyB.Count; adjacent++)
					{
						if (!workspace.IsCutBoundaryEdgeVisited(adjacencyB[adjacent]))
							workspace.BoundaryTraversalEdges.Add(adjacencyB[adjacent]);
					}
				}
			}

			int openEnds = 0;
			for (int vertex = 0; vertex < component.Vertices.Count; vertex++)
			{
				if (workspace.TryGetCutBoundaryAdjacency(component.Vertices[vertex], out List<int> adjacency) &&
					adjacency.Count == 1)
				{
					openEnds++;
				}
			}
			component.Closed = openEnds == 0 && component.Vertices.Count >= 3;
			if (component.Vertices.Count < 2)
			{
				workspace.BoundaryComponents.RemoveAt(workspace.BoundaryComponents.Count - 1);
				continue;
			}
			FinalizeBoundaryComponent(workspace, profile, component);
		}

		workspace.BoundaryComponents.Sort((left, right) =>
		{
			int comparison = left.MeanAxial.CompareTo(right.MeanAxial);
			return comparison != 0 ? comparison : left.MinimumAxial.CompareTo(right.MinimumAxial);
		});
		if (workspace.BoundaryComponents.Count == 0)
		{
			failure = "local wound clipping produced no connected cut-boundary components";
			return false;
		}
		return true;
	}

	private static void BridgeBoundaryGraphGaps(ProceduralHeadWoundWorkspace workspace, WoundProfile profile)
	{
		workspace.BoundaryOpenEndpoints.Clear();
		foreach (KeyValuePair<int, List<int>> pair in workspace.CutBoundaryAdjacency)
		{
			if (pair.Value.Count == 1) workspace.BoundaryOpenEndpoints.Add(pair.Key);
		}
		workspace.BoundaryTraversalVertices.Clear();
		float maximumSquared = BoundaryGraphBridgeMaximum * BoundaryGraphBridgeMaximum;
		for (int index = 0; index < workspace.BoundaryOpenEndpoints.Count; index++)
		{
			int a = workspace.BoundaryOpenEndpoints[index];
			if (workspace.BoundaryTraversalVertices.Contains(a)) continue;
			Vector3 positionA = workspace.SourceVertices[a];
			float axialA = Vector3.Dot(positionA - profile.Entry, profile.Axis);
			int best = -1;
			float bestSquared = maximumSquared;
			for (int other = index + 1; other < workspace.BoundaryOpenEndpoints.Count; other++)
			{
				int b = workspace.BoundaryOpenEndpoints[other];
				if (workspace.BoundaryTraversalVertices.Contains(b)) continue;
				Vector3 positionB = workspace.SourceVertices[b];
				float axialB = Vector3.Dot(positionB - profile.Entry, profile.Axis);
				if (Mathf.Abs(axialA - axialB) > BoundaryGraphBridgeMaximum) continue;
				float squared = (positionB - positionA).sqrMagnitude;
				if (squared >= bestSquared) continue;
				bestSquared = squared;
				best = b;
			}
			if (best < 0 || !workspace.AddCanonicalCutBoundaryEdge(a, best)) continue;
			int sourceEdge = workspace.CutBoundaryAdjacency[a][0];
			int submesh = workspace.CutBoundaryEdges[sourceEdge].Submesh;
			int bridgeIndex = workspace.CutBoundaryEdges.Count;
			workspace.CutBoundaryEdges.Add(new CutBoundaryEdge(a, best, submesh));
			workspace.GetCutBoundaryAdjacency(a).Add(bridgeIndex);
			workspace.GetCutBoundaryAdjacency(best).Add(bridgeIndex);
			workspace.BoundaryTraversalVertices.Add(a);
			workspace.BoundaryTraversalVertices.Add(best);
		}
	}

	private static void FinalizeBoundaryComponent(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, WoundBoundaryComponent component)
	{
		bool hasNormals = workspace.SourceNormals.Count == workspace.SourceVertices.Count;
		float axialSum = 0f;
		float facingSum = 0f;
		component.Vertices.Sort((left, right) =>
			BoundaryAngle(profile, workspace.SourceVertices[left] - profile.Entry).CompareTo(
				BoundaryAngle(profile, workspace.SourceVertices[right] - profile.Entry)));
		if (!component.Closed && component.Vertices.Count > 2)
		{
			int startAfterGap = 0;
			float largestGap = float.MinValue;
			for (int index = 0; index < component.Vertices.Count; index++)
			{
				float currentAngle = BoundaryAngle(profile,
					workspace.SourceVertices[component.Vertices[index]] - profile.Entry);
				int next = (index + 1) % component.Vertices.Count;
				float nextAngle = BoundaryAngle(profile,
					workspace.SourceVertices[component.Vertices[next]] - profile.Entry);
				if (next == 0) nextAngle += TwoPi;
				float gap = nextAngle - currentAngle;
				if (gap > largestGap)
				{
					largestGap = gap;
					startAfterGap = next;
				}
			}
			if (startAfterGap > 0)
			{
				component.Vertices.Reverse(0, startAfterGap);
				component.Vertices.Reverse(startAfterGap, component.Vertices.Count - startAfterGap);
				component.Vertices.Reverse();
			}
		}
		for (int index = 0; index < component.Vertices.Count; index++)
		{
			int vertex = component.Vertices[index];
			Vector3 relative = workspace.SourceVertices[vertex] - profile.Entry;
			float axial = Vector3.Dot(relative, profile.Axis);
			Vector3 radial = relative - profile.Axis * axial;
			component.MinimumAxial = Mathf.Min(component.MinimumAxial, axial);
			component.MaximumAxial = Mathf.Max(component.MaximumAxial, axial);
			component.MaximumRadius = Mathf.Max(component.MaximumRadius, radial.magnitude);
			axialSum += axial;
			if (hasNormals) facingSum += Vector3.Dot(workspace.SourceNormals[vertex], profile.Axis);
		}
		component.MeanAxial = axialSum / component.Vertices.Count;
		component.MeanFacing = hasNormals ? facingSum / component.Vertices.Count : 0f;
		component.AngularVertices.AddRange(component.Vertices);
		int bestSubmeshCount = 0;
		for (int index = 0; index < component.EdgeSubmeshes.Count; index++)
		{
			int candidate = component.EdgeSubmeshes[index];
			int count = 0;
			for (int other = 0; other < component.EdgeSubmeshes.Count; other++)
			{
				if (component.EdgeSubmeshes[other] == candidate) count++;
			}
			if (count <= bestSubmeshCount) continue;
			bestSubmeshCount = count;
			component.PrimarySubmesh = candidate;
		}
	}

	private static bool BuildComponentDrivenWound(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, bool throughShot, bool generateExitBreakthrough, uint colorSeed,
		out int cavityVertexCount, out int cavityTriangleCount,
		out int rimVertexCount, out int rimTriangleCount, out bool entryLipStitched,
		out bool exitLipStitched, out float entryRimOuterRadius,
		out float exitRimOuterRadius, out int openBoundaryComponentCount,
		out int solidCanalSpanCount, out int entryLipCount,
		out int intermediateLipCount, out int exitFlapCount,
		out int exitBurstRemovedTriangles, out string failure)
	{
		cavityVertexCount = 0;
		cavityTriangleCount = 0;
		rimVertexCount = 0;
		rimTriangleCount = 0;
		entryLipStitched = false;
		exitLipStitched = false;
		entryRimOuterRadius = 0f;
		exitRimOuterRadius = 0f;
		openBoundaryComponentCount = 0;
		solidCanalSpanCount = 0;
		entryLipCount = 0;
		intermediateLipCount = 0;
		exitFlapCount = 0;
		exitBurstRemovedTriangles = 0;
		failure = null;
		uint shapeSeed = colorSeed;

		for (int index = 0; index < workspace.BoundaryComponents.Count; index++)
		{
			if (!workspace.BoundaryComponents[index].Closed) openBoundaryComponentCount++;
		}

		WoundBoundaryComponent pending = null;
		for (int index = 0; index < workspace.BoundaryComponents.Count; index++)
		{
			WoundBoundaryComponent component = workspace.BoundaryComponents[index];
			if (!component.Closed) continue;
			if (pending == null)
			{
				pending = component;
				continue;
			}
			if (!BuildCanalSpan(workspace, profile, pending, component, false,
				shapeSeed ^ (uint)(solidCanalSpanCount * 0x45D9F3Bu), ref colorSeed, out failure))
			{
				return false;
			}
			solidCanalSpanCount++;
			pending = null;
		}

		if (solidCanalSpanCount == 0)
		{
			WoundBoundaryComponent start = workspace.BoundaryComponents[0];
			WoundBoundaryComponent end = workspace.BoundaryComponents.Count > 1
				? workspace.BoundaryComponents[workspace.BoundaryComponents.Count - 1]
				: null;
			bool capEnd = !throughShot;
			if (!BuildCanalSpan(workspace, profile, start, end, capEnd,
				shapeSeed ^ 0x45D9F3Bu, ref colorSeed, out failure))
			{
				return false;
			}
			solidCanalSpanCount = 1;
		}
		else if (pending != null && (!throughShot || !generateExitBreakthrough))
		{
			if (!BuildCanalSpan(workspace, profile, pending, null, !throughShot,
				shapeSeed ^ (uint)(solidCanalSpanCount * 0x45D9F3Bu), ref colorSeed, out failure))
			{
				return false;
			}
			solidCanalSpanCount++;
		}

		cavityVertexCount = workspace.CavityVertices.Count;
		cavityTriangleCount = workspace.CavityTriangles.Count / 3;
		int verticesBeforeAdornments = cavityVertexCount;
		int trianglesBeforeAdornments = cavityTriangleCount;
		int terminalComponentIndex = -1;
		if (generateExitBreakthrough)
		{
			for (int index = workspace.BoundaryComponents.Count - 1; index >= 0; index--)
			{
				if (!TryGetCanalBoundaryAttachment(workspace, workspace.BoundaryComponents[index], out _)) continue;
				terminalComponentIndex = index;
				break;
			}
		}
		float minimumCoverage = float.MaxValue;
		float minimumEntryFacing = 1f;
		float minimumExitFacing = 1f;
		int entrySurfaceSamples = 0;
		int exitSurfaceSamples = 0;
		for (int index = 0; index < workspace.BoundaryComponents.Count; index++)
		{
			WoundBoundaryComponent component = workspace.BoundaryComponents[index];
			bool terminal = generateExitBreakthrough && index == terminalComponentIndex;
			if (terminal)
			{
				if (!BuildExitBurstCollarAndFlaps(workspace, profile, component,
					shapeSeed ^ (uint)(0xB59472D3u + index * 0x9E3779B9u), ref colorSeed,
					out int flapCount, out int removedSurfaceTriangles,
					out float burstOuterRadius, out float burstFacing, out failure))
				{
					return false;
				}
				exitFlapCount += flapCount;
				exitBurstRemovedTriangles += removedSurfaceTriangles;
				exitRimOuterRadius = Mathf.Max(exitRimOuterRadius, burstOuterRadius);
				minimumExitFacing = Mathf.Min(minimumExitFacing, burstFacing);
				exitSurfaceSamples += ExitAngularSegmentCount;
				workspace.ExitOpeningCoverageRadius = Mathf.Max(workspace.ExitOpeningCoverageRadius,
					component.MaximumRadius);
				continue;
			}

			if (!BuildIndependentBoundaryLip(workspace, profile, component,
				shapeSeed ^ (uint)(0x2E7C8A61u + index * 0x85EBCA6Bu), ref colorSeed,
				out float outerRadius, out float coverage, out float lipFacing, out int sampleCount,
				out failure))
			{
				return false;
			}
			minimumCoverage = Mathf.Min(minimumCoverage, coverage);
			if (entryLipCount == 0)
			{
				entryLipCount = 1;
				entryRimOuterRadius = outerRadius;
				minimumEntryFacing = lipFacing;
				entrySurfaceSamples = sampleCount;
				workspace.EntryOpeningCoverageRadius = component.MaximumRadius;
			}
			else
			{
				intermediateLipCount++;
				minimumEntryFacing = Mathf.Min(minimumEntryFacing, lipFacing);
			}
		}

		if (entryLipCount == 0 || !FinitePositive(minimumCoverage))
		{
			failure = "component-driven wound produced no independent entry lip";
			return false;
		}
		if (generateExitBreakthrough && exitFlapCount == 0)
		{
			failure = "component-driven wound produced no terminal burst collar flaps";
			return false;
		}
		workspace.EntryMinimumRimCoverageMargin = minimumCoverage;
		workspace.ExitMinimumRimCoverageMargin = minimumCoverage;
		workspace.MinimumEntryRimFacingDot = minimumEntryFacing;
		workspace.MinimumExitRimFacingDot = minimumExitFacing;
		workspace.EntryRimSurfaceHitCount = entrySurfaceSamples;
		workspace.ExitRimSurfaceHitCount = exitSurfaceSamples;
		rimVertexCount = workspace.CavityVertices.Count - verticesBeforeAdornments;
		rimTriangleCount = workspace.CavityTriangles.Count / 3 - trianglesBeforeAdornments;
		return true;
	}

	private static bool BuildCanalSpan(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		WoundBoundaryComponent start, WoundBoundaryComponent end, bool capEnd,
		uint shapeSeed, ref uint colorSeed, out string failure)
	{
		failure = null;
		bool closed = start.Closed && (end == null || end.Closed);
		int segmentCount = closed ? CanalAngularSegmentCount : Mathf.Clamp(
			Mathf.Max(start.Vertices.Count, end != null ? end.Vertices.Count : start.Vertices.Count) - 1,
			4, CanalAngularSegmentCount);
		int stride = segmentCount + 1;
		int firstVertex = workspace.CavityVertices.Count;
		for (int ring = 0; ring < CanalSpanRingCount; ring++)
		{
			float t = ring / (float)(CanalSpanRingCount - 1);
			float roughWeight = Mathf.Sin(t * Mathf.PI);
			for (int segment = 0; segment <= segmentCount; segment++)
			{
				float u = segment / (float)segmentCount;
				SampleBoundaryComponent(workspace, profile, start, u, out Vector3 startPosition, out _);
				Vector3 endPosition;
				if (end != null)
				{
					SampleBoundaryComponent(workspace, profile, end, u, out endPosition, out _);
				}
				else
				{
					float angle = u * TwoPi;
					Vector3 radialDirection = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
					float radius = SampleRadius(profile, 1f, angle) * (capEnd ? 0.36f : 0.92f);
					endPosition = profile.Exit - profile.Axis * LipCanalInset + radialDirection * radius;
				}
				Vector3 position = Vector3.LerpUnclamped(startPosition, endPosition, t);
				float axial = Vector3.Dot(position - profile.Entry, profile.Axis);
				Vector3 axisPoint = profile.Entry + profile.Axis * axial;
				Vector3 radial = position - axisPoint;
				float noise = SmoothRimVariation01(shapeSeed, segment + ring * 31,
					segmentCount + CanalSpanRingCount, 0x68E31DA4u) - 0.5f;
				if (radial.sqrMagnitude > 0.0000001f)
				{
					position = axisPoint + radial * (1f + noise * CanalRoughnessScale * 2f * roughWeight) +
						profile.Axis * noise * CanalAxialRoughness * roughWeight;
				}
				Vector3 inwardNormal = axisPoint - position;
				if (inwardNormal.sqrMagnitude <= 0.0000001f) inwardNormal = -profile.BasisU;
				Vector3 tangent = Vector3.Cross(profile.Axis, inwardNormal).normalized;
				AddCavityVertex(workspace, position, inwardNormal, tangent,
					new Vector2(u * 2f, t * 2f), ref colorSeed);
			}
		}
		workspace.CanalBoundaryAttachments.Add(
			new WoundCanalBoundaryAttachment(start, firstVertex, segmentCount));
		if (end != null)
		{
			workspace.CanalBoundaryAttachments.Add(new WoundCanalBoundaryAttachment(end,
				firstVertex + (CanalSpanRingCount - 1) * stride, segmentCount));
		}

		for (int ring = 0; ring < CanalSpanRingCount - 1; ring++)
		{
			int current = firstVertex + ring * stride;
			int next = current + stride;
			for (int segment = 0; segment < segmentCount; segment++)
			{
				AddOrientedTriangle(workspace, current + segment, next + segment, current + segment + 1,
					profile, Vector3.zero, useAxisNormal: false);
				AddOrientedTriangle(workspace, current + segment + 1, next + segment, next + segment + 1,
					profile, Vector3.zero, useAxisNormal: false);
			}
		}

		if (capEnd)
		{
			int center = workspace.CavityVertices.Count;
			AddCavityVertex(workspace, profile.Exit - profile.Axis * LipCanalInset, -profile.Axis,
				profile.BasisU, new Vector2(1f, 2.12f), ref colorSeed);
			int finalRing = firstVertex + (CanalSpanRingCount - 1) * stride;
			for (int segment = 0; segment < segmentCount; segment++)
			{
				AddOrientedTriangle(workspace, center, finalRing + segment, finalRing + segment + 1,
					profile, -profile.Axis, useAxisNormal: true);
			}
		}
		if (workspace.CavityVertices.Count > MaximumCavityVertices)
		{
			failure = $"segmented canal exceeded {MaximumCavityVertices} wound vertices";
			return false;
		}
		return true;
	}

	private static void SampleBoundaryComponent(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, WoundBoundaryComponent component, float normalized,
		out Vector3 position, out Vector3 normal)
	{
		SampleBoundarySource(component, normalized, out int firstVertex, out int secondVertex,
			out float blend);
		position = Vector3.LerpUnclamped(workspace.SourceVertices[firstVertex],
			workspace.SourceVertices[secondVertex], blend);
		Vector3 fallback = component.MeanFacing >= 0f ? profile.Axis : -profile.Axis;
		Vector3 firstNormal = SourceNormal(workspace, firstVertex, fallback);
		Vector3 secondNormal = SourceNormal(workspace, secondVertex, fallback);
		normal = Vector3.LerpUnclamped(firstNormal, secondNormal, blend);
		if (normal.sqrMagnitude <= 0.0000001f) normal = fallback;
		normal.Normalize();
	}

	private static void SampleBoundarySource(WoundBoundaryComponent component, float normalized,
		out int firstVertex, out int secondVertex, out float blend)
	{
		IReadOnlyList<int> vertices = component.Closed ? component.AngularVertices : component.Vertices;
		int count = vertices.Count;
		float sample = component.Closed
			? Mathf.Repeat(normalized, 1f) * count
			: Mathf.Clamp01(normalized) * (count - 1);
		int first = Mathf.Min(count - 1, Mathf.FloorToInt(sample));
		int second = component.Closed ? (first + 1) % count : Mathf.Min(count - 1, first + 1);
		blend = sample - Mathf.Floor(sample);
		firstVertex = vertices[first];
		secondVertex = vertices[second];
	}

	private static bool BuildIndependentBoundaryLip(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, WoundBoundaryComponent component, uint shapeSeed, ref uint colorSeed,
		out float outerRadius, out float minimumCoverage, out float minimumFacing,
		out int sampleCount, out string failure)
	{
		failure = null;
		outerRadius = 0f;
		minimumCoverage = float.MaxValue;
		minimumFacing = 1f;
		sampleCount = component.Closed
			? (component.MeanAxial <= profile.Length * 0.25f ? EntryAngularSegmentCount : 20)
			: Mathf.Clamp(component.Vertices.Count, 4, 20);
		int connectionCount = component.Closed ? sampleCount : sampleCount - 1;
		int addedStart = workspace.CavityVertices.Count;
		for (int ring = 0; ring < IndependentLipRingCount; ring++)
		{
			for (int segment = 0; segment < sampleCount; segment++)
			{
				float u = component.Closed ? segment / (float)sampleCount : segment / (float)(sampleCount - 1);
				SampleBoundaryComponent(workspace, profile, component, u, out Vector3 surface, out Vector3 surfaceNormal);
				float axial = Vector3.Dot(surface - profile.Entry, profile.Axis);
				Vector3 axisPoint = profile.Entry + profile.Axis * axial;
				Vector3 radial = surface - axisPoint;
				if (radial.sqrMagnitude <= 0.0000001f) radial = profile.BasisU;
				Vector3 tangentOut = Vector3.ProjectOnPlane(radial.normalized, surfaceNormal);
				if (tangentOut.sqrMagnitude <= 0.0000001f) tangentOut = radial.normalized;
				tangentOut.Normalize();
				float openingRadius = radial.magnitude;
				float variation = Mathf.Lerp(0.72f, 1.28f,
					SmoothRimVariation01(shapeSeed, segment, sampleCount, 0x9E3779B9u));
				float width = Mathf.Clamp(openingRadius * LipOverlapRadiusScale * variation,
					LipMinimumOverlapWidth, LipMaximumOverlapWidth);
				float crest = Mathf.Lerp(RimMinimumCrestHeight, RimMaximumCrestHeight,
					SmoothRimVariation01(shapeSeed, segment, sampleCount, 0xA24BAED5u));
				Vector3 position;
				Vector3 normal;
				if (ring == 0)
				{
					position = surface + tangentOut * width + surfaceNormal * LipSurfaceLift;
					normal = surfaceNormal;
					outerRadius = Mathf.Max(outerRadius, openingRadius + width);
					minimumCoverage = Mathf.Min(minimumCoverage, width);
				}
				else if (ring == 1)
				{
					position = surface + tangentOut * width * 0.48f + surfaceNormal * crest;
					normal = (surfaceNormal - tangentOut * 0.30f).normalized;
				}
				else
				{
					position = surface - tangentOut * BoundaryOverlap * 0.35f - surfaceNormal * 0.00015f;
					normal = (-tangentOut + surfaceNormal * 0.18f).normalized;
				}
				Vector3 tangent = Vector3.Cross(surfaceNormal, tangentOut).normalized;
				AddCavityVertex(workspace, position, normal, tangent,
					new Vector2(u * 2f, ring / (float)(IndependentLipRingCount - 1) * 0.32f), ref colorSeed);
			}
		}

		for (int ring = 0; ring < IndependentLipRingCount - 1; ring++)
		{
			int outer = addedStart + ring * sampleCount;
			int inner = outer + sampleCount;
			for (int segment = 0; segment < connectionCount; segment++)
			{
				int next = component.Closed ? (segment + 1) % sampleCount : segment + 1;
				AddRimTriangleIfUsable(workspace, outer + segment, outer + next, inner + segment,
					ref minimumFacing);
				AddRimTriangleIfUsable(workspace, outer + next, inner + next, inner + segment,
					ref minimumFacing);
			}
		}
		if (!FinitePositive(outerRadius) || minimumFacing < 0f)
		{
			failure = "independent boundary lip produced invalid or inward-facing geometry";
			return false;
		}
		return true;
	}

	private static bool BuildExitBurstCollarAndFlaps(ProceduralHeadWoundWorkspace workspace,
		WoundProfile profile, WoundBoundaryComponent component, uint shapeSeed, ref uint colorSeed,
		out int flapCount, out int removedSurfaceTriangles, out float outerRadius,
		out float minimumFacing, out string failure)
	{
		failure = null;
		flapCount = 0;
		removedSurfaceTriangles = 0;
		outerRadius = component.MaximumRadius;
		minimumFacing = 1f;
		if (!TryGetCanalBoundaryAttachment(workspace, component, out WoundCanalBoundaryAttachment attachment) ||
			attachment.SegmentCount != ExitAngularSegmentCount)
		{
			failure = "terminal exit has no compatible generated canal ring for its burst collar";
			return false;
		}

		int triangleStart = workspace.CavityTriangles.Count / 3;
		for (int segment = 0; segment <= ExitAngularSegmentCount; segment++)
		{
			int wrapped = segment % ExitAngularSegmentCount;
			float normalized = wrapped / (float)ExitAngularSegmentCount;
			Vector3 root = workspace.CavityVertices[attachment.RingStart + segment];
			Vector3 radial = Vector3.ProjectOnPlane(root - profile.Exit, profile.Axis);
			Vector3 radialDirection = radial.sqrMagnitude > 0.0000001f
				? radial.normalized
				: profile.BasisU * Mathf.Cos(normalized * TwoPi) + profile.BasisV * Mathf.Sin(normalized * TwoPi);
			Vector3 tangent = Vector3.Cross(profile.Axis, radialDirection).normalized;
			float overlap = Mathf.Lerp(ExitCollarMinimumOverlap, ExitCollarMaximumOverlap,
				SmoothRimVariation01(shapeSeed, wrapped, ExitAngularSegmentCount, 0x9E3779B9u));
			float tangentRoughness = (SmoothRimVariation01(shapeSeed ^ 0xA24BAED5u, wrapped,
				ExitAngularSegmentCount, 0x85EBCA6Bu) - 0.5f) * 0.0012f;
			Vector3 collarPosition = root + radialDirection * overlap - profile.Axis * ExitCollarSurfaceInset +
				tangent * tangentRoughness;
			Vector3 collarNormal = (-radialDirection + profile.Axis * 0.12f).normalized;
			workspace.ExitCollarRing[segment] = workspace.CavityVertices.Count;
			AddCavityVertex(workspace, collarPosition, collarNormal, tangent,
				new Vector2(segment / (float)ExitAngularSegmentCount * 2f, 2.18f), ref colorSeed);
			outerRadius = Mathf.Max(outerRadius,
				Vector3.ProjectOnPlane(collarPosition - profile.Exit, profile.Axis).magnitude);
		}

		for (int segment = 0; segment < ExitAngularSegmentCount; segment++)
		{
			int rootA = attachment.RingStart + segment;
			int rootB = rootA + 1;
			int collarA = workspace.ExitCollarRing[segment];
			int collarB = workspace.ExitCollarRing[segment + 1];
			AddExitFlapTriangle(workspace, rootA, collarA, rootB, ref minimumFacing);
			AddExitFlapTriangle(workspace, rootB, collarA, collarB, ref minimumFacing);
		}

		for (int flap = 0; flap < ExitFlapCount; flap++)
		{
			int firstSegment = flap * ExitFlapAngularSegments;
			Vector3 flapBaseCenter = Vector3.zero;
			for (int sample = 0; sample < ExitFlapAngularSamples; sample++)
			{
				flapBaseCenter += workspace.CavityVertices[workspace.ExitCollarRing[firstSegment + sample]];
			}
			flapBaseCenter /= ExitFlapAngularSamples;

			float lengthVariation = SmoothRimVariation01(shapeSeed, flap, ExitFlapCount, 0xD1B54A35u);
			float baseLength = Mathf.Clamp(profile.ExitRadius * 0.82f,
				ExitFlapMinimumLength, ExitFlapMaximumLength);
			float flapLength = Mathf.Clamp(baseLength * Mathf.Lerp(0.72f, 1.32f, lengthVariation),
				ExitFlapMinimumLength, ExitFlapMaximumLength);
			float radialSpread = Mathf.Lerp(ExitFlapMinimumRadialSpread, ExitFlapMaximumRadialSpread,
				SmoothRimVariation01(shapeSeed ^ 0x94D049BBu, flap, ExitFlapCount, 0x27D4EB2Fu));
			float baseThickness = Mathf.Lerp(ExitFlapMinimumThickness, ExitFlapMaximumThickness,
				SmoothRimVariation01(shapeSeed ^ 0x165667B1u, flap, ExitFlapCount, 0x85EBCA6Bu));
			float tipWidth = Mathf.Lerp(ExitFlapMinimumTipWidth, ExitFlapMaximumTipWidth,
				SmoothRimVariation01(shapeSeed ^ 0xC2B2AE35u, flap, ExitFlapCount, 0x9E3779B9u));
			float flapWarp = (SmoothRimVariation01(shapeSeed ^ 0x7F4A7C15u, flap,
				ExitFlapCount, 0xA24BAED5u) - 0.5f) * 2f * ExitFlapMaximumTangentialWarp;

			for (int row = 0; row < ExitFlapRowCount; row++)
			{
				float t = row / (float)(ExitFlapRowCount - 1);
				float widthScale = Mathf.Lerp(1f, tipWidth, t);
				for (int sample = 0; sample < ExitFlapAngularSamples; sample++)
				{
					int globalSegment = firstSegment + sample;
					int wrapped = globalSegment % ExitAngularSegmentCount;
					Vector3 basePosition = workspace.CavityVertices[workspace.ExitCollarRing[globalSegment]];
					Vector3 narrowedBase = flapBaseCenter + (basePosition - flapBaseCenter) * widthScale;
					Vector3 radial = Vector3.ProjectOnPlane(narrowedBase - profile.Exit, profile.Axis);
					float angle = wrapped / (float)ExitAngularSegmentCount * TwoPi;
					Vector3 radialDirection = radial.sqrMagnitude > 0.0000001f
						? radial.normalized
						: profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
					Vector3 tangent = Vector3.Cross(profile.Axis, radialDirection).normalized;
					float tipJaggedness = Mathf.Lerp(0.82f, 1.18f,
						SmoothRimVariation01(shapeSeed ^ (uint)(flap * 0x9E3779B9u), sample,
							ExitFlapAngularSamples, 0x27D4EB2Fu));
					float axialDistance = flapLength * t * Mathf.Lerp(1f, tipJaggedness, t);
					float flare = radialSpread * t * Mathf.Lerp(0.42f, 1f, t);
					float sampleWarp = flapWarp * Mathf.Sin(t * Mathf.PI) *
						Mathf.Lerp(0.72f, 1.28f, SmoothRimVariation01(shapeSeed, wrapped,
							ExitAngularSegmentCount, 0x94D049BBu));
					Vector3 center = narrowedBase + profile.Axis * axialDistance + radialDirection * flare +
						tangent * sampleWarp;
					float thickness = baseThickness * Mathf.Lerp(1f, 0.58f, t) *
						Mathf.Lerp(0.86f, 1.14f, SmoothRimVariation01(shapeSeed ^ 0xD6E8FEB8u,
							wrapped + row * ExitAngularSegmentCount, ExitAngularSegmentCount * ExitFlapRowCount,
							0x165667B1u));
					Vector3 innerPosition = center - radialDirection * thickness * 0.5f;
					Vector3 outerPosition = center + radialDirection * thickness * 0.5f;
					Vector3 innerNormal = (-radialDirection + profile.Axis * 0.10f).normalized;
					Vector3 outerNormal = (radialDirection + profile.Axis * 0.16f).normalized;
					int vertexIndex = ExitFlapVertexIndex(flap, row, sample);
					workspace.ExitFlapInnerVertices[vertexIndex] = workspace.CavityVertices.Count;
					AddCavityVertex(workspace, innerPosition, innerNormal, tangent,
						new Vector2((globalSegment / (float)ExitAngularSegmentCount) * 2f, 2.30f + t * 0.72f),
						ref colorSeed);
					workspace.ExitFlapOuterVertices[vertexIndex] = workspace.CavityVertices.Count;
					AddCavityVertex(workspace, outerPosition, outerNormal, tangent,
						new Vector2((globalSegment / (float)ExitAngularSegmentCount) * 2f, 3.18f + t * 0.72f),
						ref colorSeed);
					outerRadius = Mathf.Max(outerRadius,
						Vector3.ProjectOnPlane(outerPosition - profile.Exit, profile.Axis).magnitude);
				}
			}

			for (int row = 0; row < ExitFlapRowCount - 1; row++)
			{
				for (int sample = 0; sample < ExitFlapAngularSegments; sample++)
				{
					int innerA = ExitFlapInner(workspace, flap, row, sample);
					int innerB = ExitFlapInner(workspace, flap, row, sample + 1);
					int innerC = ExitFlapInner(workspace, flap, row + 1, sample + 1);
					int innerD = ExitFlapInner(workspace, flap, row + 1, sample);
					int outerA = ExitFlapOuter(workspace, flap, row, sample);
					int outerB = ExitFlapOuter(workspace, flap, row, sample + 1);
					int outerC = ExitFlapOuter(workspace, flap, row + 1, sample + 1);
					int outerD = ExitFlapOuter(workspace, flap, row + 1, sample);
					AddExitFlapQuad(workspace, innerA, innerB, innerC, innerD, ref minimumFacing);
					AddExitFlapQuad(workspace, outerA, outerD, outerC, outerB, ref minimumFacing);
				}
				AddExitFlapQuad(workspace,
					ExitFlapInner(workspace, flap, row, 0), ExitFlapInner(workspace, flap, row + 1, 0),
					ExitFlapOuter(workspace, flap, row + 1, 0), ExitFlapOuter(workspace, flap, row, 0),
					ref minimumFacing);
				int last = ExitFlapAngularSegments;
				AddExitFlapQuad(workspace,
					ExitFlapInner(workspace, flap, row, last), ExitFlapOuter(workspace, flap, row, last),
					ExitFlapOuter(workspace, flap, row + 1, last), ExitFlapInner(workspace, flap, row + 1, last),
					ref minimumFacing);
			}

			int tipRow = ExitFlapRowCount - 1;
			for (int sample = 0; sample < ExitFlapAngularSegments; sample++)
			{
				AddExitFlapQuad(workspace,
					ExitFlapInner(workspace, flap, tipRow, sample),
					ExitFlapInner(workspace, flap, tipRow, sample + 1),
					ExitFlapOuter(workspace, flap, tipRow, sample + 1),
					ExitFlapOuter(workspace, flap, tipRow, sample), ref minimumFacing);

				int globalSegment = firstSegment + sample;
				int collarA = workspace.ExitCollarRing[globalSegment];
				int collarB = workspace.ExitCollarRing[globalSegment + 1];
				AddExitFlapTriangle(workspace, collarA, ExitFlapOuter(workspace, flap, 0, sample),
					collarB, ref minimumFacing);
				AddExitFlapTriangle(workspace, collarB, ExitFlapOuter(workspace, flap, 0, sample),
					ExitFlapOuter(workspace, flap, 0, sample + 1), ref minimumFacing);
				AddExitFlapTriangle(workspace, collarA, collarB,
					ExitFlapInner(workspace, flap, 0, sample), ref minimumFacing);
				AddExitFlapTriangle(workspace, collarB, ExitFlapInner(workspace, flap, 0, sample + 1),
					ExitFlapInner(workspace, flap, 0, sample), ref minimumFacing);
			}
		}

		flapCount = ExitFlapCount;
		int addedTriangles = workspace.CavityTriangles.Count / 3 - triangleStart;
		if (workspace.CavityVertices.Count > MaximumCavityVertices || addedTriangles < 400 || minimumFacing < 0f)
		{
			failure = "terminal burst collar or thick flap shells violated their bounded closed-geometry contract";
			return false;
		}
		return true;
	}

	private static int ExitFlapVertexIndex(int flap, int row, int sample) =>
		(flap * ExitFlapRowCount + row) * ExitFlapAngularSamples + sample;

	private static int ExitFlapInner(ProceduralHeadWoundWorkspace workspace, int flap, int row, int sample) =>
		workspace.ExitFlapInnerVertices[ExitFlapVertexIndex(flap, row, sample)];

	private static int ExitFlapOuter(ProceduralHeadWoundWorkspace workspace, int flap, int row, int sample) =>
		workspace.ExitFlapOuterVertices[ExitFlapVertexIndex(flap, row, sample)];

	private static void AddExitFlapQuad(ProceduralHeadWoundWorkspace workspace,
		int a, int b, int c, int d, ref float minimumFacing)
	{
		AddExitFlapTriangle(workspace, a, b, c, ref minimumFacing);
		AddExitFlapTriangle(workspace, a, c, d, ref minimumFacing);
	}

	private static void AddExitFlapTriangle(ProceduralHeadWoundWorkspace workspace,
		int a, int b, int c, ref float minimumFacing)
	{
		float facing = AddRimTriangle(workspace, a, b, c, out _, out _, out _);
		if (facing >= 0f) minimumFacing = Mathf.Min(minimumFacing, facing);
	}


	private static bool TryGetCanalBoundaryAttachment(ProceduralHeadWoundWorkspace workspace,
		WoundBoundaryComponent component, out WoundCanalBoundaryAttachment attachment)
	{
		for (int index = 0; index < workspace.CanalBoundaryAttachments.Count; index++)
		{
			WoundCanalBoundaryAttachment candidate = workspace.CanalBoundaryAttachments[index];
			if (!ReferenceEquals(candidate.Component, component)) continue;
			attachment = candidate;
			return true;
		}
		attachment = default;
		return false;
	}

	private static Vector3 SourceNormal(ProceduralHeadWoundWorkspace workspace, int vertex, Vector3 fallback)
	{
		if (workspace.SourceNormals.Count == workspace.SourceVertices.Count &&
			(uint)vertex < (uint)workspace.SourceNormals.Count)
		{
			Vector3 normal = workspace.SourceNormals[vertex];
			if (normal.sqrMagnitude > 0.0000001f) return normal.normalized;
		}
		return fallback.sqrMagnitude > 0.0000001f ? fallback.normalized : Vector3.up;
	}

	private static bool BuildBoundaryDrivenCavity(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool throughShot, bool generateExitLip, uint colorSeed,
		out int cavityVertexCount, out int cavityTriangleCount,
		out int rimVertexCount, out int rimTriangleCount, out bool entryLipStitched,
		out bool exitLipStitched, out float entryRimOuterRadius,
		out float exitRimOuterRadius, out string failure)
	{
		cavityVertexCount = 0;
		cavityTriangleCount = 0;
		rimVertexCount = 0;
		rimTriangleCount = 0;
		entryLipStitched = false;
		exitLipStitched = false;
		entryRimOuterRadius = 0f;
		exitRimOuterRadius = 0f;
		failure = null;
		uint shapeSeed = colorSeed;

		for (int ring = 0; ring < AxialRingCount; ring++)
		{
			float normalized = ring / (float)(AxialRingCount - 1);
			float bulge = Mathf.Sin(normalized * Mathf.PI);
			for (int segment = 0; segment <= AngularSegmentCount; segment++)
			{
				int wrapped = segment % AngularSegmentCount;
				float angle = wrapped / (float)AngularSegmentCount * TwoPi;
				Vector3 radial = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
				SampleBoundaryLoop(workspace.EntryBoundaryPositions, workspace.EntryBoundaryNormals,
					EntryAngularSegmentCount, wrapped / (float)AngularSegmentCount,
					out Vector3 entrySurface, out _);
				Vector3 start = entrySurface + profile.Axis * LipCanalInset - radial * BoundaryOverlap * 0.7f;
				Vector3 end;
				if (throughShot && generateExitLip)
				{
					SampleBoundaryLoop(workspace.ExitBoundaryPositions, workspace.ExitBoundaryNormals,
						ExitAngularSegmentCount, wrapped / (float)AngularSegmentCount,
						out Vector3 exitSurface, out _);
					end = exitSurface - profile.Axis * LipCanalInset - radial * BoundaryOverlap * 0.7f;
				}
				else if (throughShot)
				{
					float terminalRadius = SampleRadius(profile, 1f, angle);
					end = profile.Exit - profile.Axis * LipCanalInset + radial * terminalRadius;
				}
				else
				{
					float terminalRadius = SampleRadius(profile, 1f, angle) * 0.36f;
					end = profile.Exit - profile.Axis * LipCanalInset + radial * terminalRadius;
				}
				float roughness = (SmoothRimVariation01(shapeSeed, wrapped + ring * 7, 0x68E31DA4u) - 0.5f) *
					Mathf.Lerp(profile.EntryRadius, profile.ExitRadius, normalized) * 0.10f * bulge;
				Vector3 position = Vector3.LerpUnclamped(start, end, normalized) - radial * roughness;
				Vector3 tangent = (-profile.BasisU * Mathf.Sin(angle) + profile.BasisV * Mathf.Cos(angle)).normalized;
				AddCavityVertex(workspace, position, -radial, tangent,
					new Vector2(segment / (float)AngularSegmentCount * 2f, normalized * 2f), ref colorSeed);
			}
		}

		for (int ring = 0; ring < AxialRingCount - 1; ring++)
		{
			int current = ring * CavityVerticesPerRing;
			int next = (ring + 1) * CavityVerticesPerRing;
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				AddOrientedTriangle(workspace, current + segment, next + segment, current + segment + 1,
					profile, Vector3.zero, useAxisNormal: false);
				AddOrientedTriangle(workspace, current + segment + 1, next + segment, next + segment + 1,
					profile, Vector3.zero, useAxisNormal: false);
			}
		}

		if (!throughShot)
		{
			int centerIndex = workspace.CavityVertices.Count;
			AddCavityVertex(workspace, profile.Exit - profile.Axis * LipCanalInset, -profile.Axis,
				profile.BasisU, new Vector2(1f, 2.12f), ref colorSeed);
			int finalRing = (AxialRingCount - 1) * CavityVerticesPerRing;
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				AddOrientedTriangle(workspace, centerIndex, finalRing + segment, finalRing + segment + 1,
					profile, -profile.Axis, useAxisNormal: true);
			}
		}

		cavityVertexCount = workspace.CavityVertices.Count;
		cavityTriangleCount = workspace.CavityTriangles.Count / 3;
		int verticesBeforeRims = cavityVertexCount;
		int trianglesBeforeRims = cavityTriangleCount;
		if (!BuildBoundaryLip(workspace, profile, true, 0,
			EntryAngularSegmentCount, shapeSeed ^ 0x2E7C8A61u, ref colorSeed,
			out entryRimOuterRadius, out float entryFacing, out entryLipStitched, out failure))
		{
			return false;
		}
		workspace.MinimumEntryRimFacingDot = entryFacing;
		if (generateExitLip)
		{
			if (!BuildBoundaryLip(workspace, profile, false,
				(AxialRingCount - 1) * CavityVerticesPerRing,
				ExitAngularSegmentCount, shapeSeed ^ 0xB59472D3u, ref colorSeed,
				out exitRimOuterRadius, out float exitFacing, out exitLipStitched, out failure))
			{
				return false;
			}
			workspace.MinimumExitRimFacingDot = exitFacing;
		}
		rimVertexCount = workspace.CavityVertices.Count - verticesBeforeRims;
		rimTriangleCount = workspace.CavityTriangles.Count / 3 - trianglesBeforeRims;
		return true;
	}

	private static bool BuildBoundaryLip(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool entry, int cavityRingStart, int segmentCount, uint shapeSeed, ref uint colorSeed,
		out float outerRadius, out float minimumFacing, out bool stitched, out string failure)
	{
		failure = null;
		outerRadius = 0f;
		minimumFacing = 1f;
		stitched = false;
		Vector3[] surfaces = entry ? workspace.EntryBoundaryPositions : workspace.ExitBoundaryPositions;
		Vector3[] surfaceNormals = entry ? workspace.EntryBoundaryNormals : workspace.ExitBoundaryNormals;
		Vector3[] outerSurfaces = entry ? workspace.EntryLipOuterPositions : workspace.ExitLipOuterPositions;
		Vector3[] outerSurfaceNormals = entry ? workspace.EntryLipOuterNormals : workspace.ExitLipOuterNormals;
		Vector3 opening = entry ? profile.Entry : profile.Exit;
		Vector3 inwardAxis = entry ? profile.Axis : -profile.Axis;
		int addedStart = workspace.CavityVertices.Count;
		for (int ring = 0; ring < RimAddedRingCount; ring++)
		{
			for (int segment = 0; segment < segmentCount; segment++)
			{
				float angle = segment / (float)segmentCount * TwoPi;
				Vector3 radial = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
				Vector3 surface = surfaces[segment];
				Vector3 surfaceNormal = surfaceNormals[segment];
				Vector3 outerSurface = outerSurfaces[segment];
				Vector3 outerSurfaceNormal = outerSurfaceNormals[segment];
				float crest = Mathf.Lerp(RimMinimumCrestHeight, RimMaximumCrestHeight,
					SmoothRimVariation01(shapeSeed, segment, segmentCount, 0xA24BAED5u));
				Vector3 position;
				Vector3 normal;
				if (ring == 0)
				{
					// This is the visible exterior overlap: it is deliberately outside the cut and
					// lifted just above retained skin, rather than buried at the opening boundary.
					position = outerSurface + outerSurfaceNormal * LipSurfaceLift;
					normal = outerSurfaceNormal;
				}
				else if (ring == 1)
				{
					Vector3 crestSurface = Vector3.LerpUnclamped(outerSurface, surface, 0.58f);
					Vector3 crestNormal = Vector3.LerpUnclamped(outerSurfaceNormal, surfaceNormal, 0.58f).normalized;
					position = crestSurface + crestNormal * crest;
					normal = (crestNormal - radial * 0.28f).normalized;
				}
				else if (ring == 2)
				{
					position = surface + surfaceNormal * Mathf.Max(LipSurfaceLift, crest * 0.42f);
					normal = (surfaceNormal - radial * 0.68f).normalized;
				}
				else
				{
					// Keep the throat visibly folded under the retained skin while leaving enough
					// separation from the independently sampled canal ring to form stable triangles.
					position = surface + inwardAxis * LipCanalInset -
						radial * BoundaryOverlap * LipThroatRadialInsetScale;
					normal = -radial;
				}
				Vector3 tangent = (-profile.BasisU * Mathf.Sin(angle) + profile.BasisV * Mathf.Cos(angle)).normalized;
				float rimV = entry
					? Mathf.Lerp(-0.24f, 0f, ring / (float)(RimAddedRingCount - 1))
					: Mathf.Lerp(2.24f, 2f, ring / (float)(RimAddedRingCount - 1));
				AddCavityVertex(workspace, position, normal, tangent,
					new Vector2(segment / (float)segmentCount * 2f, rimV), ref colorSeed);
				Vector3 relative = outerSurface - opening;
				float axial = Vector3.Dot(relative, profile.Axis);
				outerRadius = Mathf.Max(outerRadius, (relative - profile.Axis * axial).magnitude);
			}
		}

		for (int ring = 0; ring < RimAddedRingCount - 1; ring++)
		{
			int outerStart = addedStart + ring * segmentCount;
			int innerStart = outerStart + segmentCount;
			for (int segment = 0; segment < segmentCount; segment++)
			{
				int next = (segment + 1) % segmentCount;
				AddRimTriangleIfUsable(workspace,
					outerStart + segment, outerStart + next, innerStart + segment, ref minimumFacing);
				AddRimTriangleIfUsable(workspace,
					outerStart + next, innerStart + next, innerStart + segment, ref minimumFacing);
			}
		}

		int throatStart = addedStart + (RimAddedRingCount - 1) * segmentCount;
		int trianglesBeforeStitch = workspace.CavityTriangles.Count;
		float lipFacing = minimumFacing;
		float stitchFacing = minimumFacing;
		stitched = StitchClosedRings(workspace, throatStart, segmentCount, cavityRingStart,
			AngularSegmentCount, ref stitchFacing) && stitchFacing >= 0f;
		if (!stitched)
		{
			// A valid exterior lip and destructive canal must not be discarded merely
			// because the two independently sampled loops form degenerate bridge facets.
			// Roll back only the attempted bridge and leave this opening visibly open.
			workspace.CavityTriangles.RemoveRange(trianglesBeforeStitch,
				workspace.CavityTriangles.Count - trianglesBeforeStitch);
			minimumFacing = lipFacing;
		}
		else
		{
			minimumFacing = stitchFacing;
		}
		if (!FinitePositive(outerRadius))
		{
			failure = $"{(entry ? "entry" : "exit")} lip produced an invalid exterior radius";
			return false;
		}
		return true;
	}

	private static bool StitchClosedRings(ProceduralHeadWoundWorkspace workspace,
		int firstStart, int firstCount, int secondStart, int secondCount, ref float minimumFacing)
	{
		int first = 0;
		int second = 0;
		int addedTriangles = 0;
		while (first < firstCount || second < secondCount)
		{
			float nextFirst = first < firstCount ? (first + 1f) / firstCount : float.PositiveInfinity;
			float nextSecond = second < secondCount ? (second + 1f) / secondCount : float.PositiveInfinity;
			int a = firstStart + (first % firstCount);
			int b = secondStart + (second % secondCount);
			if (Mathf.Abs(nextFirst - nextSecond) <= 0.000001f)
			{
				int nextA = firstStart + ((first + 1) % firstCount);
				int nextB = secondStart + ((second + 1) % secondCount);
				float firstDiagonalMinimum = Mathf.Min(
					TriangleAreaSquared(workspace, a, nextA, b),
					TriangleAreaSquared(workspace, nextA, nextB, b));
				float secondDiagonalMinimum = Mathf.Min(
					TriangleAreaSquared(workspace, a, nextA, nextB),
					TriangleAreaSquared(workspace, a, nextB, b));
				if (secondDiagonalMinimum > firstDiagonalMinimum)
				{
					AddStitchTriangleIfUsable(workspace, a, nextA, nextB, ref minimumFacing, ref addedTriangles);
					AddStitchTriangleIfUsable(workspace, a, nextB, b, ref minimumFacing, ref addedTriangles);
				}
				else
				{
					AddStitchTriangleIfUsable(workspace, a, nextA, b, ref minimumFacing, ref addedTriangles);
					AddStitchTriangleIfUsable(workspace, nextA, nextB, b, ref minimumFacing, ref addedTriangles);
				}
				first++;
				second++;
			}
			else if (nextFirst < nextSecond)
			{
				int nextA = firstStart + ((first + 1) % firstCount);
				AddStitchTriangleIfUsable(workspace, a, nextA, b, ref minimumFacing, ref addedTriangles);
				first++;
			}
			else
			{
				int nextB = secondStart + ((second + 1) % secondCount);
				AddStitchTriangleIfUsable(workspace, a, nextB, b, ref minimumFacing, ref addedTriangles);
				second++;
			}
		}
		// A zero-area triangle contributes no visible coverage and is safe to omit.
		// Still require at least one complete ring's worth of usable bridge geometry.
		return minimumFacing >= 0f && addedTriangles >= Mathf.Max(firstCount, secondCount);
	}

	private static void AddStitchTriangleIfUsable(ProceduralHeadWoundWorkspace workspace,
		int a, int b, int c, ref float minimumFacing, ref int addedTriangles)
	{
		float facing = AddRimTriangle(workspace, a, b, c, out _, out _, out _);
		if (facing < 0f)
		{
			return;
		}
		minimumFacing = Mathf.Min(minimumFacing, facing);
		addedTriangles++;
	}

	private static void AddRimTriangleIfUsable(ProceduralHeadWoundWorkspace workspace,
		int a, int b, int c, ref float minimumFacing)
	{
		float facing = AddRimTriangle(workspace, a, b, c, out _, out _, out _);
		if (facing >= 0f)
		{
			minimumFacing = Mathf.Min(minimumFacing, facing);
		}
	}

	private static float TriangleAreaSquared(ProceduralHeadWoundWorkspace workspace, int a, int b, int c)
	{
		Vector3 va = workspace.CavityVertices[a];
		Vector3 vb = workspace.CavityVertices[b];
		Vector3 vc = workspace.CavityVertices[c];
		return Vector3.Cross(vb - va, vc - va).sqrMagnitude;
	}

	private static void SampleBoundaryLoop(Vector3[] positions, Vector3[] normals, int count,
		float normalizedAngle, out Vector3 position, out Vector3 normal)
	{
		float sample = Mathf.Repeat(normalizedAngle, 1f) * count;
		int first = Mathf.FloorToInt(sample) % count;
		int second = (first + 1) % count;
		float blend = sample - Mathf.Floor(sample);
		position = Vector3.LerpUnclamped(positions[first], positions[second], blend);
		normal = Vector3.LerpUnclamped(normals[first], normals[second], blend);
		if (normal.sqrMagnitude > 0.0000001f) normal.Normalize();
	}

	private static void AddCavityVertex(ProceduralHeadWoundWorkspace workspace, Vector3 position,
		Vector3 normal, Vector3 tangent, Vector2 uv, ref uint colorSeed)
	{
		workspace.CavityVertices.Add(position);
		workspace.CavityNormals.Add(normal.sqrMagnitude > 0.0000001f ? normal.normalized : Vector3.up);
		workspace.CavityTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
		workspace.CavityUvs.Add(uv);
		workspace.CavityColors.Add(new Color(Mathf.Lerp(0.55f, 1f, Next01(ref colorSeed)), 0f, 0f, 1f));
	}

	// Retained failed spike below: whole-leaf omission plus independently sampled oversized overlap rims.
	// The active runtime path is BuildBoundaryDrivenCavity and local implicit-volume clipping above.
	private static bool BuildCavity(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		bool throughShot, uint colorSeed, out int cavityVertexCount, out int cavityTriangleCount,
		out int rimVertexCount, out int rimTriangleCount, out float entryRimOuterRadius,
		out float exitRimOuterRadius, out string failure)
	{
		cavityVertexCount = 0;
		cavityTriangleCount = 0;
		rimVertexCount = 0;
		rimTriangleCount = 0;
		entryRimOuterRadius = 0f;
		exitRimOuterRadius = 0f;
		failure = null;
		uint rimSeed = colorSeed;
		float inset = Mathf.Min(CavityAxialInset, profile.Length * 0.08f);
		for (int ring = 0; ring < AxialRingCount; ring++)
		{
			float normalized = ring / (float)(AxialRingCount - 1);
			float axial = Mathf.Lerp(inset, profile.Length - inset, normalized);
			Vector3 center = profile.Entry + profile.Axis * axial;
			float endWeight = Mathf.Abs(normalized * 2f - 1f);
			float overlapScale = Mathf.Lerp(CavityMiddleInsetScale, CavityEndInsetScale, endWeight * endWeight);
			for (int segment = 0; segment <= AngularSegmentCount; segment++)
			{
				int wrapped = segment % AngularSegmentCount;
				float angle = wrapped / (float)AngularSegmentCount * TwoPi + profile.RingPhases[ring];
				Vector3 radialDirection = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
				float radius = profile.RingRadii[ring * AngularSegmentCount + wrapped] * overlapScale;
				workspace.CavityVertices.Add(center + radialDirection * radius);
				workspace.CavityNormals.Add(-radialDirection);
				Vector3 tangent = (-profile.BasisU * Mathf.Sin(angle) + profile.BasisV * Mathf.Cos(angle)).normalized;
				workspace.CavityTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
				workspace.CavityUvs.Add(new Vector2(segment / (float)AngularSegmentCount * 2f, normalized * 2f));
				float wetness = Mathf.Clamp01(0.48f + normalized * 0.28f + (Next01(ref colorSeed) - 0.5f) * 0.32f);
				workspace.CavityColors.Add(new Color(wetness, 0f, 0f, 1f));
			}
		}

		for (int ring = 0; ring < AxialRingCount - 1; ring++)
		{
			int current = ring * CavityVerticesPerRing;
			int next = (ring + 1) * CavityVerticesPerRing;
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				AddOrientedTriangle(workspace, current + segment, next + segment, current + segment + 1, profile, Vector3.zero, useAxisNormal: false);
				AddOrientedTriangle(workspace, current + segment + 1, next + segment, next + segment + 1, profile, Vector3.zero, useAxisNormal: false);
			}
		}

		if (!throughShot)
		{
			int centerIndex = workspace.CavityVertices.Count;
			Vector3 capCenter = profile.Exit;
			workspace.CavityVertices.Add(capCenter);
			workspace.CavityNormals.Add(-profile.Axis);
			workspace.CavityTangents.Add(new Vector4(profile.BasisU.x, profile.BasisU.y, profile.BasisU.z, 1f));
			workspace.CavityUvs.Add(new Vector2(1f, 2.12f));
			workspace.CavityColors.Add(new Color(0.9f, 0f, 0f, 1f));
			int finalRing = (AxialRingCount - 1) * CavityVerticesPerRing;
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				AddOrientedTriangle(workspace, centerIndex, finalRing + segment, finalRing + segment + 1,
					profile, -profile.Axis, useAxisNormal: true);
			}
		}

		cavityVertexCount = workspace.CavityVertices.Count;
		cavityTriangleCount = workspace.CavityTriangles.Count / 3;
		int verticesBeforeRims = cavityVertexCount;
		int trianglesBeforeRims = cavityTriangleCount;
		if (!BuildReturningHalfToroidRim(workspace, profile, cavityRingStart: 0, entry: true,
			rimSeed ^ 0x2E7C8A61u, ref colorSeed,
			out entryRimOuterRadius, out failure))
		{
			return false;
		}
		if (throughShot)
		{
			if (!BuildReturningHalfToroidRim(workspace, profile,
				(AxialRingCount - 1) * CavityVerticesPerRing, entry: false,
				rimSeed ^ 0xB59472D3u, ref colorSeed,
				out exitRimOuterRadius, out failure))
			{
				return false;
			}
		}
		rimVertexCount = workspace.CavityVertices.Count - verticesBeforeRims;
		rimTriangleCount = workspace.CavityTriangles.Count / 3 - trianglesBeforeRims;
		return true;
	}

	private static bool BuildReturningHalfToroidRim(ProceduralHeadWoundWorkspace workspace, WoundProfile profile,
		int cavityRingStart, bool entry, uint shapeSeed, ref uint colorSeed,
		out float outerRadius, out string failure)
	{
		failure = null;
		int profileRing = entry ? 0 : AxialRingCount - 1;
		Vector3 opening = entry ? profile.Entry : profile.Exit;
		Vector3 outwardAxis = entry ? -profile.Axis : profile.Axis;
		float[] surfaceOffsets = entry ? workspace.EntryRimSurfaceOffsets : workspace.ExitRimSurfaceOffsets;
		Vector3[] surfaceNormals = entry ? workspace.EntryRimSurfaceNormals : workspace.ExitRimSurfaceNormals;
		byte[] surfaceHits = entry ? workspace.EntryRimSurfaceHits : workspace.ExitRimSurfaceHits;
		float[] contactRadii = entry ? workspace.EntryRimContactRadii : workspace.ExitRimContactRadii;
		outerRadius = 0f;
		for (int segment = 0; segment < AngularSegmentCount; segment++)
		{
			outerRadius = Mathf.Max(outerRadius, contactRadii[segment]);
		}
		if (!FinitePositive(outerRadius) || outerRadius > MaximumRimOuterRadius)
		{
			failure = $"{(entry ? "entry" : "exit")} sampled contact exceeds the bounded rim radius";
			return false;
		}
		float inset = Mathf.Min(CavityAxialInset, profile.Length * 0.08f);

		int addedStart = workspace.CavityVertices.Count;
		for (int ring = 0; ring < RimAddedRingCount; ring++)
		{
			float t = ring / (float)RimAddedRingCount;
			float smooth = t * t * (3f - 2f * t);
			float smoothDerivative = 6f * t * (1f - t);
			for (int segment = 0; segment <= AngularSegmentCount; segment++)
			{
				int wrapped = segment % AngularSegmentCount;
				float angle = wrapped / (float)AngularSegmentCount * TwoPi + profile.RingPhases[profileRing];
				Vector3 radialDirection = profile.BasisU * Mathf.Cos(angle) + profile.BasisV * Mathf.Sin(angle);
				float innerRadius = profile.RingRadii[profileRing * AngularSegmentCount + wrapped] * CavityEndInsetScale;
				float irregularOuterRadius = contactRadii[wrapped];
				float crestHeight = Mathf.Lerp(RimMinimumCrestHeight, RimMaximumCrestHeight,
					SmoothRimVariation01(shapeSeed, wrapped, 0xA24BAED5u));
				int contactSample = wrapped;
				if (surfaceHits[contactSample] == 0)
				{
					failure = $"{(entry ? "entry" : "exit")} rim contact lost its retained skull sample at segment {wrapped}";
					return false;
				}
				float contactOffset = surfaceOffsets[contactSample] - RimContactInset;
				float radius = Mathf.Lerp(irregularOuterRadius, innerRadius, smooth);
				float axialOffset = Mathf.Lerp(contactOffset, -inset, smooth) +
					crestHeight * Mathf.Sin(t * Mathf.PI);
				int sample = ring * AngularSegmentCount + wrapped;
				if (surfaceHits[sample] != 0)
				{
					float surfaceTarget = ring == 0
						? surfaceOffsets[sample] - RimContactInset
						: surfaceOffsets[sample] + RimSurfaceClearance +
							crestHeight * Mathf.Sin(t * Mathf.PI) * 0.7f;
					axialOffset = Mathf.Max(axialOffset, surfaceTarget);
				}
				float radialDerivative = (innerRadius - irregularOuterRadius) * smoothDerivative;
				float axialDerivative = (-inset - contactOffset) * smoothDerivative +
					crestHeight * Mathf.PI * Mathf.Cos(t * Mathf.PI);
				Vector3 unscaledNormal = outwardAxis * -radialDerivative + radialDirection * axialDerivative;
				float normalSquared = unscaledNormal.sqrMagnitude;
				Vector3 analyticNormal = normalSquared > 0.0000000001f
					? unscaledNormal / Mathf.Sqrt(normalSquared)
					: -radialDirection;
				Vector3 normal = analyticNormal;
				if (surfaceHits[sample] != 0)
				{
					Vector3 blended = Vector3.Lerp(analyticNormal, surfaceNormals[sample], 1f - t);
					float blendedSquared = blended.sqrMagnitude;
					if (blendedSquared > 0.0000000001f) normal = blended / Mathf.Sqrt(blendedSquared);
				}
				Vector3 tangent = (-profile.BasisU * Mathf.Sin(angle) + profile.BasisV * Mathf.Cos(angle)).normalized;
				workspace.CavityVertices.Add(opening + outwardAxis * axialOffset + radialDirection * radius);
				workspace.CavityNormals.Add(normal);
				workspace.CavityTangents.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
				float rimV = entry ? Mathf.Lerp(-0.24f, 0f, t) : Mathf.Lerp(2.24f, 2f, t);
				workspace.CavityUvs.Add(new Vector2(segment / (float)AngularSegmentCount * 2f, rimV));
				workspace.CavityColors.Add(new Color(Mathf.Lerp(0.72f, 1f, Next01(ref colorSeed)), 0f, 0f, 1f));
			}
		}

		float minimumFacingDot = 1f;
		int worstTriangle = -1;
		float worstAreaSquared = 0f;
		Vector3 worstActualNormal = Vector3.zero;
		Vector3 worstDesiredNormal = Vector3.zero;
		for (int ring = 0; ring < RimAddedRingCount; ring++)
		{
			int outerStart = addedStart + ring * CavityVerticesPerRing;
			int innerStart = ring + 1 < RimAddedRingCount
				? outerStart + CavityVerticesPerRing
				: cavityRingStart;
			for (int segment = 0; segment < AngularSegmentCount; segment++)
			{
				float facing = AddRimTriangle(workspace, outerStart + segment, outerStart + segment + 1,
					innerStart + segment, out float areaSquared, out Vector3 actualNormal, out Vector3 desiredNormal);
				if (facing < minimumFacingDot)
				{
					minimumFacingDot = facing;
					worstTriangle = (ring * AngularSegmentCount + segment) * 2;
					worstAreaSquared = areaSquared;
					worstActualNormal = actualNormal;
					worstDesiredNormal = desiredNormal;
				}
				facing = AddRimTriangle(workspace, outerStart + segment + 1, innerStart + segment + 1,
					innerStart + segment, out areaSquared, out actualNormal, out desiredNormal);
				if (facing < minimumFacingDot)
				{
					minimumFacingDot = facing;
					worstTriangle = (ring * AngularSegmentCount + segment) * 2 + 1;
					worstAreaSquared = areaSquared;
					worstActualNormal = actualNormal;
					worstDesiredNormal = desiredNormal;
				}
			}
		}
		if (entry)
		{
			workspace.MinimumEntryRimFacingDot = minimumFacingDot;
			workspace.WorstEntryRimTriangle = worstTriangle;
			workspace.WorstEntryRimAreaSquared = worstAreaSquared;
			workspace.WorstEntryActualNormal = worstActualNormal;
			workspace.WorstEntryDesiredNormal = worstDesiredNormal;
		}
		else
		{
			workspace.MinimumExitRimFacingDot = minimumFacingDot;
			workspace.WorstExitRimTriangle = worstTriangle;
			workspace.WorstExitRimAreaSquared = worstAreaSquared;
			workspace.WorstExitActualNormal = worstActualNormal;
			workspace.WorstExitDesiredNormal = worstDesiredNormal;
		}
		return true;
	}

	private static float MaximumRingRadius(WoundProfile profile, int ring)
	{
		float maximum = 0f;
		int offset = ring * AngularSegmentCount;
		for (int segment = 0; segment < AngularSegmentCount; segment++)
		{
			maximum = Mathf.Max(maximum, profile.RingRadii[offset + segment]);
		}
		return maximum;
	}

	private static float RimOuterRadiusForSegment(float baseRadius, uint seed, int segment)
	{
		return baseRadius * (1f + SmoothRimVariation01(seed, segment, 0x9E3779B9u) * RimOuterIrregularity);
	}

	private static float SmoothRimVariation01(uint seed, int segment, uint salt)
	{
		return SmoothRimVariation01(seed, segment, AngularSegmentCount, salt);
	}

	private static float SmoothRimVariation01(uint seed, int segment, int segmentCount, uint salt)
	{
		uint state = seed ^ salt;
		float phaseA = Next01(ref state) * TwoPi;
		float phaseB = Next01(ref state) * TwoPi;
		float angle = segment / (float)segmentCount * TwoPi;
		return Mathf.Clamp01(0.5f + Mathf.Sin(angle * 3f + phaseA) * 0.32f +
			Mathf.Sin(angle * 5f + phaseB) * 0.18f);
	}

	private static float AddRimTriangle(ProceduralHeadWoundWorkspace workspace, int a, int b, int c,
		out float areaSquared, out Vector3 actualNormal, out Vector3 desiredNormal)
	{
		Vector3 va = workspace.CavityVertices[a];
		Vector3 vb = workspace.CavityVertices[b];
		Vector3 vc = workspace.CavityVertices[c];
		Vector3 desired = workspace.CavityNormals[a] + workspace.CavityNormals[b] + workspace.CavityNormals[c];
		Vector3 actual = Vector3.Cross(vb - va, vc - va);
		areaSquared = actual.sqrMagnitude;
		float desiredSquared = desired.sqrMagnitude;
		if (areaSquared <= 0.0000000000000001f || desiredSquared <= 0.0000001f)
		{
			actualNormal = Vector3.zero;
			desiredNormal = Vector3.zero;
			return -1f;
		}
		// Unity's Vector3.normalized returns zero below an internal magnitude epsilon.
		// A valid millimetre-scale triangle's cross product is measured in square metres
		// and can fall below that epsilon, so normalize explicitly after the area gate.
		actualNormal = actual / Mathf.Sqrt(areaSquared);
		desiredNormal = desired / Mathf.Sqrt(desiredSquared);
		if (Vector3.Dot(actual, desired) < 0f)
		{
			int swap = b;
			b = c;
			c = swap;
		}
		workspace.CavityTriangles.Add(a);
		workspace.CavityTriangles.Add(b);
		workspace.CavityTriangles.Add(c);
		return Mathf.Clamp01(Mathf.Abs(Vector3.Dot(actualNormal, desiredNormal)));
	}

	private static void AddOrientedTriangle(ProceduralHeadWoundWorkspace workspace, int a, int b, int c,
		WoundProfile profile, Vector3 requestedNormal, bool useAxisNormal)
	{
		Vector3 va = workspace.CavityVertices[a];
		Vector3 vb = workspace.CavityVertices[b];
		Vector3 vc = workspace.CavityVertices[c];
		Vector3 actual = Vector3.Cross(vb - va, vc - va);
		Vector3 desired;
		if (useAxisNormal)
		{
			desired = requestedNormal;
		}
		else
		{
			Vector3 centroid = (va + vb + vc) / 3f;
			float axial = Vector3.Dot(centroid - profile.Entry, profile.Axis);
			Vector3 axisPoint = profile.Entry + profile.Axis * axial;
			desired = axisPoint - centroid;
		}
		if (Vector3.Dot(actual, desired) < 0f)
		{
			int swap = b;
			b = c;
			c = swap;
		}
		workspace.CavityTriangles.Add(a);
		workspace.CavityTriangles.Add(b);
		workspace.CavityTriangles.Add(c);
	}

	private static bool TriangleBoundsOverlap(Vector3 a, Vector3 b, Vector3 c, Vector3 boundsMin, Vector3 boundsMax)
	{
		Vector3 minimum = Vector3.Min(a, Vector3.Min(b, c));
		Vector3 maximum = Vector3.Max(a, Vector3.Max(b, c));
		return maximum.x >= boundsMin.x && minimum.x <= boundsMax.x &&
			maximum.y >= boundsMin.y && minimum.y <= boundsMax.y &&
			maximum.z >= boundsMin.z && minimum.z <= boundsMax.z;
	}

	private static bool TriangleApproachesWoundAxis(in WoundProfile profile, Vector3 a, Vector3 b, Vector3 c)
	{
		if (profile.Members != null)
		{
			for (int i = 0; i < profile.MemberCount; i++) if (TriangleApproachesWoundAxis(profile.Members[i], a, b, c)) return true;
			return false;
		}
		// The world-axis AABB around an oblique shot encloses a large diagonal slab. A
		// triangle bounding sphere gives a conservative oriented broad phase: reject only
		// when the complete sphere lies beyond the wound's capped maximum-radius tube.
		Vector3 centroid = (a + b + c) / 3f;
		float radiusSquared = Mathf.Max((a - centroid).sqrMagnitude,
			Mathf.Max((b - centroid).sqrMagnitude, (c - centroid).sqrMagnitude));
		float triangleRadius = Mathf.Sqrt(radiusSquared);
		float axial = Vector3.Dot(centroid - profile.Entry, profile.Axis);
		if (axial + triangleRadius < -SurfaceOverlap ||
			axial - triangleRadius > profile.Length + SurfaceOverlap)
		{
			return false;
		}

		float clampedAxial = Mathf.Clamp(axial, 0f, profile.Length);
		Vector3 axisPoint = profile.Entry + profile.Axis * clampedAxial;
		// Every sampled radius is baseRadius * (1 + 0.10*sin + 0.05*sin).
		// Bound only the axial interval this triangle can reach, not the largest exit.
		float lowerRadius = Mathf.Lerp(profile.EntryRadius, profile.ExitRadius, Mathf.Clamp01((axial - triangleRadius) / profile.Length));
		float upperRadius = Mathf.Lerp(profile.EntryRadius, profile.ExitRadius, Mathf.Clamp01((axial + triangleRadius) / profile.Length));
		float boundedRadius = Mathf.Min(profile.MaximumRadius, Mathf.Max(lowerRadius, upperRadius) * 1.150001f);
		float maximumDistance = boundedRadius + SurfaceOverlap + ClassificationPadding + triangleRadius;
		return (centroid - axisPoint).sqrMagnitude <= maximumDistance * maximumDistance;
	}

	private static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
	{
		Vector3 ab = b - a;
		Vector3 ac = c - a;
		Vector3 ap = point - a;
		float d1 = Vector3.Dot(ab, ap);
		float d2 = Vector3.Dot(ac, ap);
		if (d1 <= 0f && d2 <= 0f) return a;

		Vector3 bp = point - b;
		float d3 = Vector3.Dot(ab, bp);
		float d4 = Vector3.Dot(ac, bp);
		if (d3 >= 0f && d4 <= d3) return b;

		float vc = d1 * d4 - d3 * d2;
		if (vc <= 0f && d1 >= 0f && d3 <= 0f)
		{
			float v = d1 / (d1 - d3);
			return a + ab * v;
		}

		Vector3 cp = point - c;
		float d5 = Vector3.Dot(ab, cp);
		float d6 = Vector3.Dot(ac, cp);
		if (d6 >= 0f && d5 <= d6) return c;

		float vb = d5 * d2 - d1 * d6;
		if (vb <= 0f && d2 >= 0f && d6 <= 0f)
		{
			float w = d2 / (d2 - d6);
			return a + ac * w;
		}

		float va = d3 * d6 - d5 * d4;
		if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
		{
			float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
			return b + (c - b) * w;
		}

		float denominator = 1f / (va + vb + vc);
		float barycentricV = vb * denominator;
		float barycentricW = vc * denominator;
		return a + ab * barycentricV + ac * barycentricW;
	}

	private static void CalculateBarycentric(Vector3 point, Vector3 a, Vector3 b, Vector3 c,
		out Vector3 weights)
	{
		Vector3 v0 = b - a;
		Vector3 v1 = c - a;
		Vector3 v2 = point - a;
		float d00 = Vector3.Dot(v0, v0);
		float d01 = Vector3.Dot(v0, v1);
		float d11 = Vector3.Dot(v1, v1);
		float d20 = Vector3.Dot(v2, v0);
		float d21 = Vector3.Dot(v2, v1);
		float denominator = d00 * d11 - d01 * d01;
		if (Mathf.Abs(denominator) <= 0.000000000001f)
		{
			weights = new Vector3(1f, 0f, 0f);
			return;
		}
		float v = (d11 * d20 - d01 * d21) / denominator;
		float w = (d00 * d21 - d01 * d20) / denominator;
		float u = 1f - v - w;
		u = Mathf.Clamp01(u);
		v = Mathf.Clamp01(v);
		w = Mathf.Clamp01(w);
		float total = u + v + w;
		weights = total > 0.0000001f ? new Vector3(u / total, v / total, w / total) : new Vector3(1f, 0f, 0f);
	}

	private static void ClosestSegmentPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
		out Vector3 point1, out Vector3 point2)
	{
		Vector3 d1 = q1 - p1;
		Vector3 d2 = q2 - p2;
		Vector3 r = p1 - p2;
		float a = Vector3.Dot(d1, d1);
		float e = Vector3.Dot(d2, d2);
		float f = Vector3.Dot(d2, r);
		float s;
		float t;
		if (a <= 0.0000001f && e <= 0.0000001f)
		{
			point1 = p1;
			point2 = p2;
			return;
		}
		if (a <= 0.0000001f)
		{
			s = 0f;
			t = Mathf.Clamp01(f / e);
		}
		else
		{
			float c = Vector3.Dot(d1, r);
			if (e <= 0.0000001f)
			{
				t = 0f;
				s = Mathf.Clamp01(-c / a);
			}
			else
			{
				float b = Vector3.Dot(d1, d2);
				float denominator = a * e - b * b;
				s = denominator != 0f ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
				t = (b * s + f) / e;
				if (t < 0f)
				{
					t = 0f;
					s = Mathf.Clamp01(-c / a);
				}
				else if (t > 1f)
				{
					t = 1f;
					s = Mathf.Clamp01((b - c) / a);
				}
			}
		}
		point1 = p1 + d1 * s;
		point2 = p2 + d2 * t;
	}

	private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
	{
		distance = 0f;
		Vector3 edge1 = b - a;
		Vector3 edge2 = c - a;
		Vector3 p = Vector3.Cross(direction, edge2);
		float determinant = Vector3.Dot(edge1, p);
		if (Mathf.Abs(determinant) < 0.0000001f) return false;
		float inverse = 1f / determinant;
		Vector3 t = origin - a;
		float u = Vector3.Dot(t, p) * inverse;
		if (u < 0f || u > 1f) return false;
		Vector3 q = Vector3.Cross(t, edge1);
		float v = Vector3.Dot(direction, q) * inverse;
		if (v < 0f || u + v > 1f) return false;
		distance = Vector3.Dot(edge2, q) * inverse;
		return distance >= 0f;
	}

	private static bool ValidVertexIndex(ProceduralHeadWoundWorkspace workspace, int index)
	{
		return (uint)index < (uint)workspace.SourceVertices.Count;
	}

	private static uint NextState(uint state)
	{
		state ^= state << 13;
		state ^= state >> 17;
		state ^= state << 5;
		return state;
	}

	private static float Next01(ref uint state)
	{
		state = NextState(state);
		return (state & 0x00FFFFFFu) / 16777215f;
	}

	private static double ElapsedMilliseconds(long started)
	{
		return (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
	}

	private static bool FinitePositive(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
	}

	private static bool Finite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
			!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
			!float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}
}
}
