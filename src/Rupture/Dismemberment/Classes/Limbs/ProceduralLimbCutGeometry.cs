using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.Limbs;

internal static partial class ProceduralLimbCutGeometry
{
	private readonly struct ProfileScope : IDisposable
	{
		internal ProfileScope(string name) => UnityEngine.Profiling.Profiler.BeginSample(name);
		public void Dispose() => UnityEngine.Profiling.Profiler.EndSample();
	}

	internal const float MissingBandWidthWorld = ProceduralLimbWoundGeometry.MissingBandWorld;
	private const float PlaneEpsilon = 0.000001f;
	private const float BoundaryWeldToleranceWorld = 0.00005f;
	private const float StrongLimbWeight = 0.65f;
	private const float StrongSeedWeight = 0.2f;
	private const float MinimumAnatomicalWeight = 0.01f;
	private const int MaximumWeakBridgeTriangles = 3;
	private static readonly Dictionary<int, SourceData> SourceCache = new Dictionary<int, SourceData>();
	private static readonly HashSet<Mesh> UnreadableSources = new HashSet<Mesh>();

	internal static void ResetCaches() { SourceCache.Clear(); UnreadableSources.Clear(); ResetFailureCache(); }

	// Main-thread source classification. Unreadable native assets are terminal for
	// this cache lifetime: never attempt data reads or positional retries on them.
	internal static void MarkSourceUnreadable(Mesh source) { if (source != null) UnreadableSources.Add(source); }
	internal static bool IsSourceReadable(Mesh source, out string failure)
	{
		failure = null;
		if (source == null) { failure = "source mesh is missing"; return false; }
		if (UnreadableSources.Contains(source)) { failure = "source mesh is unreadable (cached)"; return false; }
		if (source.isReadable) return true;
		UnreadableSources.Add(source);
		failure = "source mesh is unreadable";
		return false;
	}

	internal class CutSpec
	{
		internal Transform ProximalStart;
		internal Transform Joint;
		internal Transform DistalEnd;
		internal Transform LimbRoot;
		internal LimbSeverLevel Level;
		internal bool IsLeg;
		internal uint Seed;
		internal bool IncludeAnatomicalDetails;
		internal float FractionOffset;
		internal LimbClothingProfiles.Core SharedBodyClothCore, SharedGibClothCore;
		internal string[] BoneNames;
		// Optional anatomical frames for bones omitted from this renderer's skinning palette.
		// These are mapped from another mesh's bind pose, never from the animated corpse pose.
		internal Matrix4x4? ProximalBind, JointBind, DistalBind;
	}

	internal sealed class ClassificationWork
	{
		private readonly SourceData _source;
		private readonly BoneSnapshot[] _bones;
		private readonly CapturedCut[] _cuts;
		private readonly string[] _initialCacheKeys;
		private readonly LimbSeverLevel[] _initialLevels;
		internal readonly List<CutAttempt> Attempts = new List<CutAttempt>();
		internal double ClassificationMilliseconds { get; private set; }
		internal CutBoundaryTrace BoundaryTrace { get; private set; }
		internal bool CaptureEnabled;
		internal string Capture(LimbCutCaptureSession session, string renderer, int lod, string bodyPart,
			int newCutIndex, string request, string outcome) =>
			CaptureClothingCase(session, _source, _bones, _cuts, BoundaryTrace, Attempts,
				renderer, lod, bodyPart, newCutIndex, request, outcome);

		internal ClassificationWork(object source, object bones, object cuts)
		{
			_source = (SourceData)source;
			_bones = (BoneSnapshot[])bones;
			_cuts = (CapturedCut[])cuts;
			_initialCacheKeys = _cuts.Select(cut => cut.CacheKey).ToArray();
			_initialLevels = _cuts.Select(cut => cut.Level).ToArray();
		}

		internal LimbSeverLevel RequestedLevel(int cutIndex) => _initialLevels[cutIndex];
		internal string FailureKey(int cutIndex, bool includeSeed) => _source.CacheIdentity + ":" +
			string.Join(";", _cuts.Select((cut, index) => index == cutIndex
				? cut.CacheKey : _initialCacheKeys[index])) + (includeSeed ? ":seed:" + string.Join(",", _cuts.Select(cut => cut.Seed)) : "");

		internal bool SetCandidate(int cutIndex, LimbSeverLevel level, float offset)
		{
			CapturedCut cut = _cuts[cutIndex];
			cut.Level = level;
			cut.StartIndex = level == LimbSeverLevel.Proximal ? cut.ProximalIndex : cut.JointIndex;
			cut.EndIndex = level == LimbSeverLevel.Proximal ? cut.JointIndex : cut.DistalIndex;
			cut.StartBind = level == LimbSeverLevel.Proximal ? cut.ProximalBind : cut.JointBind;
			cut.EndBind = level == LimbSeverLevel.Proximal ? cut.JointBind : cut.DistalBind;
			cut.LimbBones = level == LimbSeverLevel.Proximal ? cut.ProximalBones : cut.DistalBones;
			cut.FractionOffset = offset;
			cut.CacheKey = cut.PaletteKey + ":" + level + ":" + offset.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
			cut.IncludeAnatomicalDetails = false;
			BoundaryTrace = null;
			return cut.StartBind.HasValue && cut.EndBind.HasValue && cut.LimbBones.Length > 0;
		}

		internal void RecordAttempt(int cutIndex, string stage, string failure, bool cached = false)
		{
			CapturedCut cut = _cuts[cutIndex];
			_source.BindCutCache.TryGetValue(cut.CacheKey, out BindCut classified);
			Attempts.Add(new CutAttempt
			{
				Level = cut.Level.ToString(), Cached = cached, FractionOffset = cut.FractionOffset, Stage = stage, Failure = failure,
				Center = classified == null ? null : Point(classified.Center),
				SelectedTriangles = classified?.SelectedTriangles.Count ?? 0,
				Selected = CaptureEnabled ? classified?.SelectedTriangles.OrderBy(i => i).ToArray() : null,
				Boundaries = CaptureEnabled ? BoundaryTrace?.Sides : null
			});
		}

		internal bool TryPrime(out string failure)
		{
			long started = Stopwatch.GetTimestamp();
			bool succeeded = TryPrimeClassificationsCore(_source, _bones, _cuts, out failure);
			ClassificationMilliseconds = ElapsedMilliseconds(started);
			return succeeded;
		}

		internal void SetIncludeAnatomicalDetails(int cutIndex, bool include)
		{
			if (cutIndex >= 0 && cutIndex < _cuts.Length) _cuts[cutIndex].IncludeAnatomicalDetails = include;
		}

		internal void SetClothingCores(int cutIndex, LimbClothingProfiles.Core body, LimbClothingProfiles.Core gib)
		{
			_cuts[cutIndex].SharedBodyClothCore = body;
			_cuts[cutIndex].SharedGibClothCore = gib;
		}

		internal bool TryBuild(int newCutIndex, bool buildDebugOverlay, bool analysisOnly, bool buildDistal,
			out Result result, out string failure)
		{
			result = null;
			failure = null;
			if (newCutIndex < 0 || newCutIndex >= _cuts.Length)
			{
				failure = "the captured new-cut index is invalid";
				return false;
			}
			List<BindCut> cuts = new List<BindCut>(_cuts.Length);
			foreach (CapturedCut captured in _cuts)
			{
				lock (_source.CacheGate)
				{
					if (!_source.BindCutCache.TryGetValue(captured.CacheKey, out BindCut cut))
					{
						failure = "a captured anatomical classification was not primed before geometry work";
						return false;
					}
					cuts.Add(cut);
				}
			}
			BoundaryTrace = new CutBoundaryTrace { CaptureGeometry = CaptureEnabled };
			return TryBuildCore(_source, cuts, _cuts, newCutIndex, buildDebugOverlay, analysisOnly,
				buildDistal, _cuts[0].WorldScale, true, 0d, ClassificationMilliseconds,
				out result, out failure, BoundaryTrace);
		}
	}

	private readonly struct BoneSnapshot
	{
		internal readonly string Name;
		internal readonly int ParentIndex;
		internal readonly Vector3 BindPosition;

		internal BoneSnapshot(string name, int parentIndex, Vector3 bindPosition)
		{
			Name = name ?? string.Empty;
			ParentIndex = parentIndex;
			BindPosition = bindPosition;
		}
	}

	private sealed class CapturedCut : CutSpec
	{
		internal string CacheKey, PaletteKey;
		internal int ProximalIndex, JointIndex, DistalIndex;
		internal int[] ProximalBones, DistalBones;
		internal int StartIndex;
		internal int EndIndex;
		internal Matrix4x4? StartBind, EndBind;
		internal int[] LimbBones;
		internal float WorldScale;
	}

	internal sealed class MappedBlendShapeFrame
	{
		internal string Name;
		internal float Weight;
		internal Vector3[] DeltaVertices;
		internal Vector3[] DeltaNormals;
		internal Vector3[] DeltaTangents;
	}

	internal sealed class MeshBuffers
	{
		internal string Name;
		internal IndexFormat IndexFormat;
		internal Vector3[] Positions;
		internal Vector3[] Normals;
		internal Vector4[] Tangents;
		internal Vector2[] Uv0;
		internal Vector2[] Uv1;
		internal Vector2[] Uv2;
		internal Vector2[] Uv3;
		internal Color[] Colors;
		internal BoneWeight[] Weights;
		internal Matrix4x4[] Bindposes;
		internal int[][] Submeshes;
		internal MappedBlendShapeFrame[] BlendShapes;
		internal bool HasUv0;
		internal bool HasUv1;
		internal bool HasUv2;
		internal bool HasUv3;
		internal bool HasColors;
	}

	internal sealed class Result
	{
		internal Mesh Body;
		internal Mesh BodySocket;
		internal Mesh NewDistal;
		internal Mesh DebugOverlay;
		internal int BodyLoopCount;
		internal float FractionOffset;
		internal LimbSeverLevel EffectiveLevel;
		internal int NewBodyLoopCount;
		internal int GibLoopCount;
		internal int NewSelectedTriangleCount;
		internal int DebugCandidateTriangleCount;
		internal int DebugStrongSelectedTriangleCount;
		internal int DebugConnectedWeakTriangleCount;
		internal int DebugCutBoundaryTriangleCount;
		internal float DebugCapsuleRadiusWorld;
		internal float PrimaryLoopScore;
		internal LimbClothingProfiles.Core BodyClothCore, GibClothCore;
		internal List<ProceduralLimbWoundGeometry.FoldDiagnostic> Clothing;
		internal ProceduralLimbWoundGeometry.DecalSample[] BodyDecalSamples;
		internal ProceduralLimbWoundGeometry.DecalSample[] GibDecalSamples;
		internal ProceduralLimbWoundGeometry.EmissionAnchor BodyStreamAnchor;
		internal ProceduralLimbWoundGeometry.EmissionAnchor GibStreamAnchor;
		internal bool HasBodyStreamAnchor;
		internal bool HasGibStreamAnchor;
		internal int FleshVertexCount;
		internal int BoneCount;
		internal int BoneVertexCount;
		internal int FlapCount;
		internal int CordCount;
		internal float BodyPlateauProtrusionWorld;
		internal float GibPlateauProtrusionWorld;
		internal float MaximumAdornmentProtrusionWorld;
		internal bool DecorationsClosed;
		internal bool SourceCacheHit;
		internal double SourceCaptureMilliseconds;
		internal double ClassificationMilliseconds;
		internal double ClippingMilliseconds;
		internal double LoopAndWoundMilliseconds;
		internal double BufferConstructionMilliseconds;
		private MeshBuffers BodyBuffers;
		private MeshBuffers BodySocketBuffers;
		private MeshBuffers NewDistalBuffers;
		private MeshBuffers DebugOverlayBuffers;

		internal void SetBuffers(MeshBuffers body, MeshBuffers socket, MeshBuffers distal, MeshBuffers debug)
		{
			BodyBuffers = body;
			BodySocketBuffers = socket;
			NewDistalBuffers = distal;
			DebugOverlayBuffers = debug;
		}

		internal bool TryMaterialize(out string failure)
		{
			failure = null;
			try
			{
				using (new ProfileScope("RPT.LimbCut.MeshUploadBlendShapes"))
				{
					Body = Materialize(BodyBuffers);
					BodySocket = Materialize(BodySocketBuffers);
					NewDistal = Materialize(NewDistalBuffers);
					DebugOverlay = Materialize(DebugOverlayBuffers);
				}
				BodyBuffers = BodySocketBuffers = NewDistalBuffers = DebugOverlayBuffers = null;
				return true;
			}
			catch (Exception exception)
			{
				if (Body != null) UnityEngine.Object.Destroy(Body);
				if (BodySocket != null) UnityEngine.Object.Destroy(BodySocket);
				if (NewDistal != null) UnityEngine.Object.Destroy(NewDistal);
				if (DebugOverlay != null) UnityEngine.Object.Destroy(DebugOverlay);
				Body = BodySocket = NewDistal = DebugOverlay = null;
				failure = $"Unity mesh upload failed: {exception.GetType().Name}: {exception.Message}";
				return false;
			}
		}
	}

	private readonly struct SourceTriangle
	{
		internal readonly int A;
		internal readonly int B;
		internal readonly int C;
		internal readonly int Submesh;
		internal readonly int Index;

		internal SourceTriangle(int a, int b, int c, int submesh, int index)
		{
			A = a;
			B = b;
			C = c;
			Submesh = submesh;
			Index = index;
		}
	}

	private readonly struct Segment3
	{
		internal readonly Vector3 A;
		internal readonly Vector3 B;

		internal Segment3(Vector3 a, Vector3 b)
		{
			A = a;
			B = b;
		}
	}

	private readonly struct DebugPoint
	{
		internal readonly Vector3 Position;
		internal readonly BoneWeight Weight;
		internal readonly int SourceVertex;
		internal DebugPoint(Vector3 position, BoneWeight weight, int sourceVertex)
		{
			Position = position;
			Weight = weight;
			SourceVertex = sourceVertex;
		}
	}

	private readonly struct EdgeKey : IEquatable<EdgeKey>
	{
		internal readonly int A;
		internal readonly int B;
		internal readonly int Plane;

		internal EdgeKey(int a, int b, int plane)
		{
			A = Mathf.Min(a, b);
			B = Mathf.Max(a, b);
			Plane = plane;
		}

		public bool Equals(EdgeKey other) => A == other.A && B == other.B && Plane == other.Plane;
		public override bool Equals(object value) => value is EdgeKey other && Equals(other);
		public override int GetHashCode() => ((A * 397) ^ B) * 397 ^ Plane;
	}

	private readonly struct BoundaryEdge : IEquatable<BoundaryEdge>
	{
		internal readonly int A;
		internal readonly int B;

		internal BoundaryEdge(int a, int b)
		{
			A = Mathf.Min(a, b);
			B = Mathf.Max(a, b);
		}

		public bool Equals(BoundaryEdge other) => A == other.A && B == other.B;
		public override bool Equals(object value) => value is BoundaryEdge other && Equals(other);
		public override int GetHashCode() => (A * 397) ^ B;
	}

	private readonly struct PositionKey : IEquatable<PositionKey>
	{
		internal readonly int X;
		internal readonly int Y;
		internal readonly int Z;

		internal PositionKey(Vector3 position, float cellSize)
		{
			X = Mathf.FloorToInt(position.x / cellSize);
			Y = Mathf.FloorToInt(position.y / cellSize);
			Z = Mathf.FloorToInt(position.z / cellSize);
		}

		internal PositionKey(int x, int y, int z) { X = x; Y = y; Z = z; }
		public bool Equals(PositionKey other) => X == other.X && Y == other.Y && Z == other.Z;
		public override bool Equals(object value) => value is PositionKey other && Equals(other);
		public override int GetHashCode() => ((X * 397) ^ Y) * 397 ^ Z;
	}

	private struct VertexData
	{
		internal Vector3 Position;
		internal Vector3 Normal;
		internal Vector4 Tangent;
		internal Vector2 Uv0;
		internal Vector2 Uv1;
		internal Vector2 Uv2;
		internal Vector2 Uv3;
		internal Color Color;
		internal BoneWeight Weight;
		internal int SourceA;
		internal int SourceB;
		internal float SourceT;
	}

	private static long NextSourceIdentity;
	private sealed class SourceData
	{
		internal readonly long CacheIdentity = System.Threading.Interlocked.Increment(ref NextSourceIdentity);
		internal string Name;
		internal int SubmeshCount;
		internal Matrix4x4[] Bindposes;
		internal Vector3[] Positions;
		internal Vector3[] Normals;
		internal Vector4[] Tangents;
		internal Vector2[] Uv0;
		internal Vector2[] Uv1;
		internal Vector2[] Uv2;
		internal Vector2[] Uv3;
		internal Color[] Colors;
		internal BoneWeight[] Weights;
		internal List<SourceTriangle> Triangles;
		internal readonly Dictionary<float, List<int>[]> AdjacencyByTolerance =
			new Dictionary<float, List<int>[]>();
		internal readonly Dictionary<string, BindCut> BindCutCache = new Dictionary<string, BindCut>(StringComparer.Ordinal);
		internal readonly object CacheGate = new object();
		internal BlendShapeFrame[] BlendShapes;
		internal string DiagnosticIdentity;
		internal readonly Dictionary<float, int[]> CanonicalByTolerance = new Dictionary<float, int[]>();
		internal readonly Dictionary<float, Dictionary<BoundaryEdge, List<int>>> EdgeOwnersByTolerance =
			new Dictionary<float, Dictionary<BoundaryEdge, List<int>>>();

		internal int[] GetCanonical(float tolerance)
		{
			lock (CacheGate)
			{
				if (!CanonicalByTolerance.TryGetValue(tolerance, out int[] canonical))
					CanonicalByTolerance[tolerance] = canonical = BuildCanonicalPositionMap(Positions, tolerance);
				return canonical;
			}
		}

		internal Dictionary<BoundaryEdge, List<int>> GetEdgeOwners(float tolerance)
		{
			lock (CacheGate)
			{
				if (EdgeOwnersByTolerance.TryGetValue(tolerance, out Dictionary<BoundaryEdge, List<int>> cached)) return cached;
				int[] canonical = GetCanonical(tolerance);
				cached = new Dictionary<BoundaryEdge, List<int>>();
				foreach (SourceTriangle triangle in Triangles)
				{
					if (canonical[triangle.A] == canonical[triangle.B] || canonical[triangle.B] == canonical[triangle.C] || canonical[triangle.C] == canonical[triangle.A]) continue;
					AddOwner(cached, new BoundaryEdge(canonical[triangle.A], canonical[triangle.B]), triangle.Index);
					AddOwner(cached, new BoundaryEdge(canonical[triangle.B], canonical[triangle.C]), triangle.Index);
					AddOwner(cached, new BoundaryEdge(canonical[triangle.C], canonical[triangle.A]), triangle.Index);
				}
				EdgeOwnersByTolerance[tolerance] = cached;
				return cached;
			}
		}

		internal List<int>[] GetAdjacency(float tolerance)
		{
			lock (CacheGate)
			{
				if (!AdjacencyByTolerance.TryGetValue(tolerance, out List<int>[] adjacency))
				{
					adjacency = BuildTriangleAdjacency(Triangles, Positions, tolerance, GetCanonical(tolerance));
					AdjacencyByTolerance[tolerance] = adjacency;
				}
				return adjacency;
			}
		}

		internal VertexData Vertex(int index)
		{
			return new VertexData
			{
				Position = Positions[index],
				Normal = Normals.Length == Positions.Length ? Normals[index] : Vector3.up,
				Tangent = Tangents.Length == Positions.Length ? Tangents[index] : new Vector4(1f, 0f, 0f, 1f),
				Uv0 = Uv0.Length == Positions.Length ? Uv0[index] : Vector2.zero,
				Uv1 = Uv1.Length == Positions.Length ? Uv1[index] : Vector2.zero,
				Uv2 = Uv2.Length == Positions.Length ? Uv2[index] : Vector2.zero,
				Uv3 = Uv3.Length == Positions.Length ? Uv3[index] : Vector2.zero,
				Color = Colors.Length == Positions.Length ? Colors[index] : Color.white,
				Weight = Weights[index],
				SourceA = index,
				SourceB = index,
				SourceT = 0f
			};
		}
	}

	private sealed class BlendShapeFrame
	{
		internal string Name;
		internal float Weight;
		internal Vector3[] DeltaVertices;
		internal Vector3[] DeltaNormals;
		internal Vector3[] DeltaTangents;
	}

	private sealed class BindCut
	{
		internal float SegmentLength;
		internal Matrix4x4 ToBone, FromBone;
		internal Vector3 Center;
		internal Vector3 Axis;
		internal float DistalGap;
		internal float CapsuleRadius;
		internal HashSet<int> SelectedTriangles;
		internal Classification Classification;
	}

	private sealed class Classification
	{
		internal bool[] Candidate;
		internal float[] Projection;
		internal float[] LimbWeight;
		internal float[] SeedWeight;
		internal bool[] ProtectedByOtherBones;
		internal HashSet<int> Selected;
	}

	private sealed class IndexedBuilder
	{
		private readonly SourceData _source;
		private readonly bool _requiresGeneratedSurfaceAttributes;
		private readonly Dictionary<int, int> _originals = new Dictionary<int, int>();
		private readonly Dictionary<EdgeKey, int> _crossings = new Dictionary<EdgeKey, int>();
		internal readonly List<VertexData> Vertices = new List<VertexData>();
		internal readonly List<List<int>> Submeshes;

		internal IndexedBuilder(SourceData source, int submeshCount,
			bool requiresGeneratedSurfaceAttributes = false)
		{
			_source = source;
			_requiresGeneratedSurfaceAttributes = requiresGeneratedSurfaceAttributes;
			Submeshes = new List<List<int>>(submeshCount);
			for (int index = 0; index < submeshCount; index++) Submeshes.Add(new List<int>());
		}

		internal int Original(int sourceIndex)
		{
			if (_originals.TryGetValue(sourceIndex, out int existing)) return existing;
			int created = Add(_source.Vertex(sourceIndex));
			_originals[sourceIndex] = created;
			return created;
		}

		internal int Crossing(int sourceA, int sourceB, float sourceAToB, int plane)
		{
			EdgeKey key = new EdgeKey(sourceA, sourceB, plane);
			if (_crossings.TryGetValue(key, out int existing)) return existing;
			float normalizedT = sourceA <= sourceB ? sourceAToB : 1f - sourceAToB;
			VertexData crossing = Lerp(_source.Vertex(key.A), _source.Vertex(key.B), normalizedT);
			crossing.SourceA = key.A;
			crossing.SourceB = key.B;
			crossing.SourceT = normalizedT;
			int created = Add(crossing);
			_crossings[key] = created;
			return created;
		}

		internal int Add(VertexData vertex)
		{
			int index = Vertices.Count;
			Vertices.Add(vertex);
			return index;
		}

		internal void Triangle(int a, int b, int c, int submesh)
		{
			Submeshes[submesh].Add(a);
			Submeshes[submesh].Add(b);
			Submeshes[submesh].Add(c);
		}

		internal void Polygon(List<int> polygon, int submesh)
		{
			RemoveAdjacentDuplicates(polygon);
			for (int index = 1; index + 1 < polygon.Count; index++) Triangle(polygon[0], polygon[index], polygon[index + 1], submesh);
		}

		internal MeshBuffers BuildBuffers(string name, Matrix4x4[] bindposes)
		{
			int count = Vertices.Count;
			Vector3[] positions = new Vector3[count];
			Vector3[] normals = new Vector3[count];
			Vector4[] tangents = new Vector4[count];
			Vector2[] uv0 = new Vector2[count];
			Vector2[] uv1 = new Vector2[count];
			Vector2[] uv2 = new Vector2[count];
			Vector2[] uv3 = new Vector2[count];
			Color[] colors = new Color[count];
			BoneWeight[] weights = new BoneWeight[count];
			for (int index = 0; index < count; index++)
			{
				VertexData vertex = Vertices[index];
				positions[index] = vertex.Position;
				normals[index] = vertex.Normal;
				tangents[index] = vertex.Tangent;
				uv0[index] = vertex.Uv0;
				uv1[index] = vertex.Uv1;
				uv2[index] = vertex.Uv2;
				uv3[index] = vertex.Uv3;
				colors[index] = vertex.Color;
				weights[index] = vertex.Weight;
			}
			int[][] submeshes = new int[Submeshes.Count][];
			for (int index = 0; index < Submeshes.Count; index++) submeshes[index] = Submeshes[index].ToArray();
			return new MeshBuffers
			{
				Name = name,
				IndexFormat = count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16,
				Positions = positions,
				Normals = normals,
				Tangents = tangents,
				Uv0 = uv0,
				Uv1 = uv1,
				Uv2 = uv2,
				Uv3 = uv3,
				Colors = colors,
				Weights = weights,
				Bindposes = bindposes,
				Submeshes = submeshes,
				BlendShapes = MapBlendShapes(_source, Vertices),
				HasUv0 = _requiresGeneratedSurfaceAttributes || SourceHasUv(_source.Uv0),
				HasUv1 = SourceHasUv(_source.Uv1),
				HasUv2 = SourceHasUv(_source.Uv2),
				HasUv3 = SourceHasUv(_source.Uv3),
				HasColors = _requiresGeneratedSurfaceAttributes || SourceHasColors
			};
		}

		private bool SourceHasUv(Vector2[] channel) => channel.Length == _source.Positions.Length;
		private bool SourceHasColors => _source.Colors.Length == _source.Positions.Length;
	}

	private static Mesh Materialize(MeshBuffers buffers)
	{
		if (buffers == null) return null;
		Mesh mesh = new Mesh
		{
			name = buffers.Name,
			indexFormat = buffers.IndexFormat
		};
		mesh.vertices = buffers.Positions;
		mesh.normals = buffers.Normals;
		mesh.tangents = buffers.Tangents;
		// EFT's decal shaders select their UV path from the source vertex layout.
		// Do not manufacture absent UV channels filled with zeroes: doing so makes
		// otherwise paintable LOD/body meshes sample the decal texture at (0, 0).
		if (buffers.HasUv0) mesh.uv = buffers.Uv0;
		if (buffers.HasUv1) mesh.uv2 = buffers.Uv1;
		if (buffers.HasUv2) mesh.uv3 = buffers.Uv2;
		if (buffers.HasUv3) mesh.uv4 = buffers.Uv3;
		if (buffers.HasColors) mesh.colors = buffers.Colors;
		mesh.boneWeights = buffers.Weights;
		mesh.bindposes = buffers.Bindposes;
		mesh.subMeshCount = buffers.Submeshes.Length;
		for (int index = 0; index < buffers.Submeshes.Length; index++)
			mesh.SetTriangles(buffers.Submeshes[index], index, calculateBounds: false);
		foreach (MappedBlendShapeFrame frame in buffers.BlendShapes ?? Array.Empty<MappedBlendShapeFrame>())
			mesh.AddBlendShapeFrame(frame.Name, frame.Weight, frame.DeltaVertices,
				frame.DeltaNormals, frame.DeltaTangents);
		mesh.RecalculateBounds();
		return mesh;
	}

	internal static bool TryBuild(Mesh source, Transform rendererTransform, Transform[] bones,
		IReadOnlyList<CutSpec> activeCuts, int newCutIndex, bool buildDebugOverlay,
		out Result result, out string failure)
	{
		return TryBuild(source, rendererTransform, bones, activeCuts, newCutIndex, buildDebugOverlay,
			analysisOnly: false, buildDistal: true, out result, out failure);
	}

	// Captures every Unity-owned value needed by first-use anatomical
	// classification. ClassificationWork contains managed arrays, strings and value
	// types only; the dedicated cut worker must never dereference Mesh, Transform,
	// Renderer, GameObject, or another UnityEngine.Object.
	internal static bool TryCaptureClassificationWork(Mesh source, Transform rendererTransform, Transform[] bones,
		IReadOnlyList<CutSpec> activeCuts, out ClassificationWork work, out string failure)
	{
		work = null;
		failure = null;
		if (source == null || rendererTransform == null || bones == null || activeCuts == null || activeCuts.Count == 0)
		{
			failure = "classification capture input is incomplete";
			return false;
		}
		if (!IsSourceReadable(source, out failure)) return false;
		if (source.vertexCount < 3)
		{
			failure = "the immutable source mesh is unreadable or its skinning data is inconsistent";
			return false;
		}

		SourceData data = GetOrReadSource(source, out _);
		if (data.Weights.Length != data.Positions.Length || data.Bindposes.Length != bones.Length)
		{
			failure = "the immutable managed snapshot has inconsistent skinning data";
			return false;
		}
		Matrix4x4[] bindposes = data.Bindposes;
		Dictionary<Transform, int> palette = new Dictionary<Transform, int>();
		for (int index = 0; index < bones.Length; index++)
			if (bones[index] != null && !palette.ContainsKey(bones[index])) palette.Add(bones[index], index);
		BoneSnapshot[] capturedBones = new BoneSnapshot[bones.Length];
		for (int index = 0; index < bones.Length; index++)
		{
			Transform bone = bones[index];
			Transform parent = bone?.parent;
			while (parent != null && !palette.ContainsKey(parent)) parent = parent.parent;
			int parentIndex = parent != null && palette.TryGetValue(parent, out int mappedParent) ? mappedParent : -1;
			Vector3 bindPosition = bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero);
			if (!IsFinite(bindPosition))
			{
				failure = $"bone {index} has a non-finite bind position";
				return false;
			}
			capturedBones[index] = new BoneSnapshot(bone?.name, parentIndex, bindPosition);
		}

		float worldScale = AverageScale(rendererTransform.lossyScale);
		int paletteSignature = BonePaletteSignature(bones);
		CapturedCut[] capturedCuts = new CapturedCut[activeCuts.Count];
		string[] boneNames = capturedBones.Select(b => b.Name).ToArray();
		for (int cutIndex = 0; cutIndex < activeCuts.Count; cutIndex++)
		{
			CutSpec spec = activeCuts[cutIndex];
			if (spec == null || spec.ProximalStart == null || spec.Joint == null || spec.DistalEnd == null || spec.LimbRoot == null)
			{
				failure = $"anatomical transforms for cut {cutIndex} are missing";
				return false;
			}
			Transform start = spec.Level == LimbSeverLevel.Proximal ? spec.ProximalStart : spec.Joint;
			Transform end = spec.Level == LimbSeverLevel.Proximal ? spec.Joint : spec.DistalEnd;
			int startIndex = Array.IndexOf(bones, start);
			int endIndex = Array.IndexOf(bones, end);
			int rootIndex = Array.IndexOf(bones, spec.LimbRoot);
			Matrix4x4? proximalBind = BindFrame(spec.ProximalStart, spec.ProximalBind);
			Matrix4x4? jointBind = BindFrame(spec.Joint, spec.JointBind);
			Matrix4x4? distalBind = BindFrame(spec.DistalEnd, spec.DistalBind);
			Matrix4x4? BindFrame(Transform target, Matrix4x4? fallback)
			{
				int index = Array.IndexOf(bones, target);
				return index >= 0 ? bindposes[index] : fallback;
			}
			if (!jointBind.HasValue || (!proximalBind.HasValue && !distalBind.HasValue))
			{
				failure = $"the renderer bone palette does not contain the complete limb chain for cut {cutIndex}";
				return false;
			}
			List<int> limbBones = new List<int>();
			for (int index = 0; index < bones.Length; index++)
				if (bones[index] == spec.LimbRoot || bones[index] != null && bones[index].IsChildOf(spec.LimbRoot))
					limbBones.Add(index);
			capturedCuts[cutIndex] = new CapturedCut
			{
				PaletteKey = $"{paletteSignature}:{Array.IndexOf(bones, spec.ProximalStart)}:{Array.IndexOf(bones, spec.Joint)}:{Array.IndexOf(bones, spec.DistalEnd)}:{spec.IsLeg}:{worldScale:R}" +
					BindFrameKey(proximalBind) + BindFrameKey(jointBind) + BindFrameKey(distalBind),
				ProximalBind = proximalBind, JointBind = jointBind, DistalBind = distalBind,
				StartBind = spec.Level == LimbSeverLevel.Proximal ? proximalBind : jointBind,
				EndBind = spec.Level == LimbSeverLevel.Proximal ? jointBind : distalBind,
				ProximalIndex = Array.IndexOf(bones, spec.ProximalStart), JointIndex = Array.IndexOf(bones, spec.Joint), DistalIndex = Array.IndexOf(bones, spec.DistalEnd),
				ProximalBones = Enumerable.Range(0, bones.Length).Where(i => bones[i] != null && (bones[i] == spec.ProximalStart || bones[i].IsChildOf(spec.ProximalStart))).ToArray(),
				DistalBones = Enumerable.Range(0, bones.Length).Where(i => bones[i] != null && (bones[i] == spec.Joint || bones[i].IsChildOf(spec.Joint))).ToArray(),
				CacheKey = $"{paletteSignature}:{startIndex}:{endIndex}:{rootIndex}:{spec.Level}:{spec.IsLeg}:{worldScale:R}:{spec.FractionOffset:R}",
				StartIndex = startIndex,
				EndIndex = endIndex,
				LimbBones = limbBones.ToArray(),
				Level = spec.Level,
				IsLeg = spec.IsLeg,
				Seed = spec.Seed,
				IncludeAnatomicalDetails = spec.IncludeAnatomicalDetails,
				SharedBodyClothCore = spec.SharedBodyClothCore,
				SharedGibClothCore = spec.SharedGibClothCore,
				BoneNames = boneNames,
				FractionOffset = spec.FractionOffset,
				WorldScale = worldScale
			};
			capturedCuts[cutIndex].CacheKey += BindFrameKey(proximalBind) + BindFrameKey(jointBind) + BindFrameKey(distalBind);
		}
		work = new ClassificationWork(data, capturedBones, capturedCuts);
		return true;
	}

	private static string BindFrameKey(Matrix4x4? frame) => frame.HasValue
		? ":bind:" + string.Join(",", Enumerable.Range(0, 16).Select(i => frame.Value[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
		: ":missing";

	internal static bool TryMapBindFrame(Transform target, Transform[] sourceBones, Matrix4x4[] sourceBindposes,
		Transform[] referenceBones, Matrix4x4[] referenceBindposes, out Matrix4x4 frame)
	{
		frame = default;
		if (target == null || sourceBones == null || referenceBones == null || sourceBindposes == null ||
			referenceBindposes == null || sourceBones.Length != sourceBindposes.Length ||
			referenceBones.Length != referenceBindposes.Length) return false;
		int targetIndex = Array.IndexOf(referenceBones, target);
		if (targetIndex < 0) return false;
		bool found = false;
		for (int i = 0; i < sourceBones.Length; i++)
		{
			if (sourceBones[i] == null) continue;
			int shared = Array.IndexOf(referenceBones, sourceBones[i]);
			if (shared < 0) continue;
			Matrix4x4 candidate = referenceBindposes[targetIndex] * referenceBindposes[shared].inverse * sourceBindposes[i];
			// All shared bones must agree on the mesh-to-mesh frame. A different bind skeleton
			// cannot supply a trustworthy anatomical endpoint even if its names happen to match.
			for (int component = 0; component < 16; component++)
				if (float.IsNaN(candidate[component]) || float.IsInfinity(candidate[component]) ||
					(found && Mathf.Abs(candidate[component] - frame[component]) > 0.001f)) return false;
			if (Mathf.Abs(candidate.determinant) < 0.000001f) return false;
			frame = candidate;
			found = true;
		}
		return found;
	}

	private static bool TryPrimeClassificationsCore(SourceData source, BoneSnapshot[] bones, CapturedCut[] cuts,
		out string failure)
	{
		failure = null;
		for (int cutIndex = 0; cutIndex < cuts.Length; cutIndex++)
		{
			CapturedCut captured = cuts[cutIndex];
			lock (source.CacheGate)
				if (source.BindCutCache.ContainsKey(captured.CacheKey)) continue;
			if (!TryCreateCapturedBindCut(source, bones, captured, out BindCut cut, out string cutFailure))
			{
				failure = $"captured bind-space anatomy for cut {cutIndex} is invalid: {cutFailure}";
				return false;
			}
			lock (source.CacheGate)
				if (!source.BindCutCache.ContainsKey(captured.CacheKey))
					source.BindCutCache.Add(captured.CacheKey, cut);
		}
		return true;
	}

	internal static bool TryBuild(Mesh source, Transform rendererTransform, Transform[] bones,
		IReadOnlyList<CutSpec> activeCuts, int newCutIndex, bool buildDebugOverlay,
		bool analysisOnly, bool buildDistal, out Result result, out string failure)
	{
		result = null;
		failure = null;
		if (source == null || rendererTransform == null || bones == null || activeCuts == null ||
			newCutIndex < 0 || newCutIndex >= activeCuts.Count)
		{
			failure = "limb cut input is incomplete";
			return false;
		}
		if (!IsSourceReadable(source, out failure)) return false;
		if (source.vertexCount < 3)
		{
			failure = "the immutable source mesh is unreadable or its skinning data is inconsistent";
			return false;
		}

		long sourceStarted = Stopwatch.GetTimestamp();
		SourceData data;
		bool sourceCacheHit;
		using (new ProfileScope("RPT.LimbCut.ImmutableMeshCapture"))
			data = GetOrReadSource(source, out sourceCacheHit);
		if (data.Weights.Length != data.Positions.Length || data.Bindposes.Length != bones.Length)
		{
			failure = "the immutable managed snapshot has inconsistent skinning data";
			return false;
		}
		double sourceCaptureMilliseconds = ElapsedMilliseconds(sourceStarted);
		float worldScale = AverageScale(rendererTransform.lossyScale);
		long classificationStarted = Stopwatch.GetTimestamp();
		List<BindCut> cuts = new List<BindCut>(activeCuts.Count);
		using (new ProfileScope("RPT.LimbCut.ClassificationConnectivity"))
		{
			for (int index = 0; index < activeCuts.Count; index++)
			{
				if (!TryCreateBindCut(data, bones, data.Bindposes, activeCuts[index], worldScale,
					out BindCut cut, out string cutFailure))
				{
					failure = $"bind-space anatomy for cut {index} is invalid: {cutFailure}";
					return false;
				}
				cuts.Add(cut);
			}
		}
		double classificationMilliseconds = ElapsedMilliseconds(classificationStarted);
		if (!TryBuildCore(data, cuts, activeCuts, newCutIndex, buildDebugOverlay, analysisOnly,
			buildDistal, worldScale, sourceCacheHit, sourceCaptureMilliseconds,
			classificationMilliseconds, out result, out failure)) return false;
		return analysisOnly || result.TryMaterialize(out failure);
	}

	private static bool TryBuildCore(SourceData data, IReadOnlyList<BindCut> cuts,
		IReadOnlyList<CutSpec> activeCuts, int newCutIndex, bool buildDebugOverlay,
		bool analysisOnly, bool buildDistal, float worldScale, bool sourceCacheHit,
		double sourceCaptureMilliseconds, double classificationMilliseconds,
		out Result result, out string failure, CutBoundaryTrace trace = null)
	{
		result = null;
		failure = null;
		IndexedBuilder body = new IndexedBuilder(data, data.SubmeshCount);
		// Descriptor-only LOD analysis needs the distal side only when that tier can
		// actually contribute to the LOD0 gib. Lower-detail body LODs still validate
		// and build their proximal seam, but never spend CPU constructing geometry
		// which the transaction must discard.
		bool needDistal = buildDistal;
		IndexedBuilder distal = needDistal ? new IndexedBuilder(data, data.SubmeshCount + 1,
			requiresGeneratedSurfaceAttributes: true) : null;
		List<List<BoundaryEdge>> bodySegments = new List<List<BoundaryEdge>>(cuts.Count);
		for (int index = 0; index < cuts.Count; index++) bodySegments.Add(new List<BoundaryEdge>());
		List<BoundaryEdge> gibSegments = new List<BoundaryEdge>();
		int selectedNew = 0;

		long clippingStarted = Stopwatch.GetTimestamp();
		using (new ProfileScope("RPT.LimbCut.IndexedClipping"))
		{
			foreach (SourceTriangle triangle in data.Triangles)
			{
				int selectedCut = -1;
				for (int cutIndex = 0; cutIndex < cuts.Count; cutIndex++)
				{
					if (!cuts[cutIndex].SelectedTriangles.Contains(triangle.Index)) continue;
					if (selectedCut >= 0)
					{
						failure = $"source triangle {triangle.Index} belongs to more than one anatomical sever region";
						return false;
					}
					selectedCut = cutIndex;
				}

				if (selectedCut < 0)
				{
					body.Triangle(body.Original(triangle.A), body.Original(triangle.B), body.Original(triangle.C), triangle.Submesh);
					continue;
				}

				BindCut selected = cuts[selectedCut];
				// The retained body seam is the anatomical cut itself. Only the gib
				// plane moves distally, so the missing section is a real 10 cm band of
				// original exterior rather than two shorter symmetric offsets.
				Vector3 bodyPlane = selected.Center;
				if (!ClipTriangle(data, triangle, bodyPlane, selected.Axis, keepPositive: false,
					body, selectedCut * 2 + 1, bodySegments[selectedCut], out string bodyFailure))
				{
					failure = $"body plane rejected source triangle {triangle.Index}: {bodyFailure}";
					return false;
				}

				if (selectedCut != newCutIndex) continue;
				selectedNew++;
				if (!needDistal) continue;
				Vector3 gibPlane = selected.Center + selected.Axis * selected.DistalGap;
				if (!ClipTriangle(data, triangle, gibPlane, selected.Axis, keepPositive: true,
					distal, selectedCut * 2 + 2, gibSegments, out string gibFailure))
				{
					failure = $"gib plane rejected source triangle {triangle.Index}: {gibFailure}";
					return false;
				}
			}
		}
		double clippingMilliseconds = ElapsedMilliseconds(clippingStarted);
		// Validate the actual retained exterior, including anatomical-selection
		// interfaces. Plane segments alone cannot describe holes made by selection.
		if (!TryCompleteCutBoundaries(data, body, cuts, bodySegments, -1, worldScale, trace, out failure)) return false;
		if (needDistal && !TryCompleteCutBoundaries(data, distal, cuts,
			new List<List<BoundaryEdge>> { gibSegments }, newCutIndex, worldScale, trace, out failure)) return false;

		IndexedBuilder socket = new IndexedBuilder(data, 1, requiresGeneratedSurfaceAttributes: true);
		int bodyLoopCount = 0;
		int newBodyLoopCount = 0;
		float primaryLoopScore = float.NegativeInfinity;
		List<ProceduralLimbWoundGeometry.DecalSample> bodyDecals = new List<ProceduralLimbWoundGeometry.DecalSample>();
		List<ProceduralLimbWoundGeometry.DecalSample> gibDecals = new List<ProceduralLimbWoundGeometry.DecalSample>();
		ProceduralLimbWoundGeometry.EmissionAnchor bodyStreamAnchor = default;
		LimbClothingProfiles.Core bodyClothCore = null, gibClothCore = null;
		ProceduralLimbWoundGeometry.EmissionAnchor gibStreamAnchor = default;
		bool hasBodyStreamAnchor = false;
		bool hasGibStreamAnchor = false;
		int fleshVertices = 0;
		int boneCount = 0;
		int boneVertices = 0;
		int flapCount = 0;
		int cordCount = 0;
		float bodyPlateau = 0f;
		float gibPlateau = 0f;
		float maximumProtrusion = 0f;
		bool decorationsClosed = true;
		List<DebugPoint> debugSeam = new List<DebugPoint>();
		List<DebugPoint> debugPrimary = new List<DebugPoint>();
		List<DebugPoint> debugMound = new List<DebugPoint>();
		List<DebugPoint> debugBones = new List<DebugPoint>();
		List<DebugPoint> debugDecals = new List<DebugPoint>();
		List<List<int>> gibLoops = new List<List<int>>();
		long woundStarted = Stopwatch.GetTimestamp();
		using (new ProfileScope("RPT.LimbCut.LoopValidationAndWounds"))
		{
		for (int cutIndex = 0; cutIndex < cuts.Count; cutIndex++)
		{
			if (!TryOrderClosedLoops(bodySegments[cutIndex], body.Vertices, cuts[cutIndex].Axis,
				BoundaryWeldToleranceWorld / worldScale, cuts[cutIndex].CapsuleRadius * 2f,
				out List<List<int>> loops, out string loopFailure))
			{
				failure = $"body cut {cutIndex} has an invalid boundary: {loopFailure}";
				return false;
			}
			if (trace != null)
				foreach (List<int> loop in loops) trace.Sides[0].Loops.Add(new[] { cutIndex }.Concat(loop).ToArray());
			bodyLoopCount += loops.Count;
			if (cutIndex == newCutIndex) newBodyLoopCount = loops.Count;
			int localPrimary = FindPrimaryLoop(loops, body.Vertices, cuts[cutIndex].Center, cuts[cutIndex].Axis,
				out float localScore);
			if (cutIndex == newCutIndex) primaryLoopScore = localScore;
			for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
			{
				bool primary = activeCuts[cutIndex].IncludeAnatomicalDetails && loopIndex == localPrimary;
				if (analysisOnly) continue;
				if (!DisturbBoundaryLoop(body, bodySegments[cutIndex], loops[loopIndex], cuts[cutIndex].Axis,
					worldScale, activeCuts[cutIndex], gibSide: false, out string disturbanceFailure))
				{
					failure = $"body cut {cutIndex} rim disturbance failed: {disturbanceFailure}";
					return false;
				}
				if (!AddWound(socket, body, loops[loopIndex], bodySegments[cutIndex], cuts[cutIndex].Axis, worldScale, 0,
					data.SubmeshCount, activeCuts[cutIndex], gibSide: false, primary,
					out ProceduralLimbWoundGeometry.Surface wound, out string woundFailure,
					CreateClothingContext(data, cuts[cutIndex], activeCuts[cutIndex], worldScale, false)))
				{
					failure = $"body cut {cutIndex} wound generation failed: {woundFailure}";
					return false;
				}
				if (wound.Fold != null) { wound.Fold.Face = "body"; wound.Fold.CutIndex = cutIndex; trace?.Clothing.Add(wound.Fold); }
				if (primary && cutIndex == newCutIndex) bodyClothCore = wound.ClothCore;
				fleshVertices += wound.FleshVertexCount;
				boneCount += wound.BoneCount;
				boneVertices += wound.BoneVertexCount;
				flapCount += wound.FlapCount;
				cordCount += wound.CordCount;
				maximumProtrusion = Mathf.Max(maximumProtrusion, wound.MaximumProtrusionWorld);
				decorationsClosed &= wound.DecorationsClosed;
				if (cutIndex == newCutIndex)
				{
					int markerStep = Mathf.Max(1, loops[loopIndex].Count / 24);
					for (int marker = 0; marker < loops[loopIndex].Count; marker += markerStep)
					{
						int vertexIndex = loops[loopIndex][marker];
						VertexData vertex = body.Vertices[vertexIndex];
						DebugPoint point = new DebugPoint(vertex.Position, vertex.Weight, vertex.SourceA);
						debugSeam.Add(point);
						if (primary) debugPrimary.Add(point);
					}
					foreach (Vector3 point in wound.MoundDebugPoints)
					{
						VertexData template = body.Vertices[loops[loopIndex][0]];
						debugMound.Add(new DebugPoint(point, template.Weight, template.SourceA));
					}
					if (primary)
					{
						VertexData template = body.Vertices[loops[loopIndex][0]];
						foreach (Vector3 point in wound.BoneRootDebugPoints)
							debugBones.Add(new DebugPoint(point, template.Weight, template.SourceA));
						foreach (ProceduralLimbWoundGeometry.DecalSample sample in wound.DecalSamples)
							debugDecals.Add(new DebugPoint(sample.Position, sample.Weight, template.SourceA));
					}
				}
				if (cutIndex == newCutIndex && primary)
				{
					bodyDecals.AddRange(wound.DecalSamples);
					bodyPlateau = wound.MeanPlateauProtrusionWorld;
					bodyStreamAnchor = wound.StreamAnchor;
					hasBodyStreamAnchor = true;
				}
			}
		}

		if (needDistal && !TryOrderClosedLoops(gibSegments, distal.Vertices, -cuts[newCutIndex].Axis,
			BoundaryWeldToleranceWorld / worldScale, cuts[newCutIndex].CapsuleRadius * 2f,
			out gibLoops, out string gibLoopFailure))
		{
			failure = "gib cut has an invalid boundary: " + gibLoopFailure;
			return false;
		}
		if (needDistal && trace != null)
			foreach (List<int> loop in gibLoops) trace.Sides[1].Loops.Add(new[] { newCutIndex }.Concat(loop).ToArray());
		int gibPrimary = needDistal ? FindPrimaryLoop(gibLoops, distal.Vertices,
			cuts[newCutIndex].Center + cuts[newCutIndex].Axis * cuts[newCutIndex].DistalGap,
			-cuts[newCutIndex].Axis, out _) : -1;
		for (int loopIndex = 0; loopIndex < gibLoops.Count && !analysisOnly; loopIndex++)
		{
			bool primary = activeCuts[newCutIndex].IncludeAnatomicalDetails && loopIndex == gibPrimary;
			if (!DisturbBoundaryLoop(distal, gibSegments, gibLoops[loopIndex], -cuts[newCutIndex].Axis,
				worldScale, activeCuts[newCutIndex], gibSide: true, out string disturbanceFailure))
			{
				failure = $"gib rim disturbance failed: {disturbanceFailure}";
				return false;
			}
			if (!AddWound(distal, distal, gibLoops[loopIndex], gibSegments, -cuts[newCutIndex].Axis, worldScale,
				data.SubmeshCount, data.SubmeshCount, activeCuts[newCutIndex], gibSide: true, primary,
				out ProceduralLimbWoundGeometry.Surface wound, out string woundFailure,
				CreateClothingContext(data, cuts[newCutIndex], activeCuts[newCutIndex], worldScale, true)))
			{
				failure = "gib wound generation failed: " + woundFailure;
				return false;
			}
			if (wound.Fold != null) { wound.Fold.Face = "gib"; wound.Fold.CutIndex = newCutIndex; trace?.Clothing.Add(wound.Fold); }
			if (primary) gibClothCore = wound.ClothCore;
			fleshVertices += wound.FleshVertexCount;
			boneCount += wound.BoneCount;
			boneVertices += wound.BoneVertexCount;
			flapCount += wound.FlapCount;
			cordCount += wound.CordCount;
			maximumProtrusion = Mathf.Max(maximumProtrusion, wound.MaximumProtrusionWorld);
			decorationsClosed &= wound.DecorationsClosed;
			if (primary)
			{
				gibDecals.AddRange(wound.DecalSamples);
				gibPlateau = wound.MeanPlateauProtrusionWorld;
				gibStreamAnchor = wound.StreamAnchor;
				hasGibStreamAnchor = true;
			}
		}
		}
		double loopAndWoundMilliseconds = ElapsedMilliseconds(woundStarted);

		if (selectedNew == 0 || needDistal && distal.Submeshes.Sum(indices => indices.Count) == 0)
		{
			failure = "the new anatomical sever region produced no distal geometry";
			return false;
		}

		BindCut debugCut = cuts[newCutIndex];
		MeshBuffers debugOverlay = null;
		int debugCandidates = 0;
		int debugStrong = 0;
		int debugWeak = 0;
		int debugBoundary = 0;
		if (buildDebugOverlay && !analysisOnly)
			debugOverlay = BuildDebugOverlay(data, debugCut, out debugCandidates,
				out debugStrong, out debugWeak, out debugBoundary, debugSeam, debugPrimary,
				debugMound, debugBones, debugDecals);

		long bufferStarted = Stopwatch.GetTimestamp();
		MeshBuffers bodyBuffers;
		MeshBuffers socketBuffers;
		MeshBuffers distalBuffers;
		using (new ProfileScope("RPT.LimbCut.ManagedMeshBuffersAndBlendShapes"))
		{
			bodyBuffers = analysisOnly ? null : body.BuildBuffers(data.Name + "_RPT_BodyFromImmutable", data.Bindposes);
			socketBuffers = !analysisOnly && socket.Vertices.Count > 0
				? socket.BuildBuffers(data.Name + "_RPT_BodySocket", data.Bindposes) : null;
			distalBuffers = !analysisOnly && buildDistal
				? distal.BuildBuffers(data.Name + "_RPT_DistalSkinned", data.Bindposes) : null;
		}
		double bufferConstructionMilliseconds = ElapsedMilliseconds(bufferStarted);
		result = new Result
		{
			FractionOffset = activeCuts[newCutIndex].FractionOffset,
			EffectiveLevel = activeCuts[newCutIndex].Level,
			BodyLoopCount = bodyLoopCount,
			NewBodyLoopCount = newBodyLoopCount,
			GibLoopCount = gibLoops.Count,
			NewSelectedTriangleCount = selectedNew,
			DebugCandidateTriangleCount = debugCandidates,
			DebugStrongSelectedTriangleCount = debugStrong,
			DebugConnectedWeakTriangleCount = debugWeak,
			DebugCutBoundaryTriangleCount = debugBoundary,
			DebugCapsuleRadiusWorld = debugCut.CapsuleRadius * worldScale,
			PrimaryLoopScore = primaryLoopScore,
			BodyClothCore = bodyClothCore, GibClothCore = gibClothCore, Clothing = trace?.Clothing,
			BodyDecalSamples = bodyDecals.ToArray(),
			GibDecalSamples = gibDecals.ToArray(),
			BodyStreamAnchor = bodyStreamAnchor,
			GibStreamAnchor = gibStreamAnchor,
			HasBodyStreamAnchor = hasBodyStreamAnchor,
			HasGibStreamAnchor = hasGibStreamAnchor,
			FleshVertexCount = fleshVertices,
			BoneCount = boneCount,
			BoneVertexCount = boneVertices,
			FlapCount = flapCount,
			CordCount = cordCount,
			BodyPlateauProtrusionWorld = bodyPlateau,
			GibPlateauProtrusionWorld = gibPlateau,
			MaximumAdornmentProtrusionWorld = maximumProtrusion,
			DecorationsClosed = decorationsClosed,
			SourceCacheHit = sourceCacheHit,
			SourceCaptureMilliseconds = sourceCaptureMilliseconds,
			ClassificationMilliseconds = classificationMilliseconds,
			ClippingMilliseconds = clippingMilliseconds,
			LoopAndWoundMilliseconds = loopAndWoundMilliseconds,
			BufferConstructionMilliseconds = bufferConstructionMilliseconds
		};
		result.SetBuffers(bodyBuffers, socketBuffers, distalBuffers, debugOverlay);
		return true;
	}

	private static SourceData GetOrReadSource(Mesh source, out bool cacheHit)
	{
		int identity = source.GetInstanceID();
		cacheHit = SourceCache.TryGetValue(identity, out SourceData data);
		if (!cacheHit)
		{
			data = ReadSource(source);
			SourceCache[identity] = data;
		}
		return data;
	}

	private static SourceData ReadSource(Mesh source)
	{
		List<SourceTriangle> triangles = new List<SourceTriangle>();
		for (int submesh = 0; submesh < source.subMeshCount; submesh++)
		{
			int[] indices = source.GetTriangles(submesh);
			for (int index = 0; index + 2 < indices.Length; index += 3)
				triangles.Add(new SourceTriangle(indices[index], indices[index + 1], indices[index + 2], submesh, triangles.Count));
		}
		List<BlendShapeFrame> blendShapes = new List<BlendShapeFrame>();
		Vector3[] deltaVertices = new Vector3[source.vertexCount];
		Vector3[] deltaNormals = new Vector3[source.vertexCount];
		Vector3[] deltaTangents = new Vector3[source.vertexCount];
		for (int shape = 0; shape < source.blendShapeCount; shape++)
		{
			string shapeName = source.GetBlendShapeName(shape);
			for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
			{
				source.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
				blendShapes.Add(new BlendShapeFrame
				{
					Name = shapeName,
					Weight = source.GetBlendShapeFrameWeight(shape, frame),
					DeltaVertices = (Vector3[])deltaVertices.Clone(),
					DeltaNormals = (Vector3[])deltaNormals.Clone(),
					DeltaTangents = (Vector3[])deltaTangents.Clone()
				});
			}
		}
		return new SourceData
		{
			Name = source.name,
			SubmeshCount = source.subMeshCount,
			Bindposes = source.bindposes,
			Positions = source.vertices,
			Normals = source.normals,
			Tangents = source.tangents,
			Uv0 = source.uv,
			Uv1 = source.uv2,
			Uv2 = source.uv3,
			Uv3 = source.uv4,
			Colors = source.colors,
			Weights = source.boneWeights,
			Triangles = triangles,
			BlendShapes = blendShapes.ToArray()
		};
	}

	private static bool TryCreateBindCut(SourceData data, Transform[] bones, Matrix4x4[] bindposes,
		CutSpec spec, float worldScale, out BindCut cut, out string failure)
	{
		cut = null;
		failure = null;
		if (spec == null || spec.ProximalStart == null || spec.Joint == null || spec.DistalEnd == null || spec.LimbRoot == null)
		{
			failure = "anatomical transforms are missing";
			return false;
		}
		Transform startTransform = spec.Level == LimbSeverLevel.Proximal ? spec.ProximalStart : spec.Joint;
		Transform endTransform = spec.Level == LimbSeverLevel.Proximal ? spec.Joint : spec.DistalEnd;
		int startIndex = Array.IndexOf(bones, startTransform);
		int endIndex = Array.IndexOf(bones, endTransform);
		int limbRootIndex = Array.IndexOf(bones, spec.LimbRoot);
		string cacheKey = $"{BonePaletteSignature(bones)}:{startIndex}:{endIndex}:{limbRootIndex}:{spec.Level}:{spec.IsLeg}:{worldScale:R}:{spec.FractionOffset:R}";
		lock (data.CacheGate)
			if (data.BindCutCache.TryGetValue(cacheKey, out BindCut cached))
			{
				cut = cached;
				return true;
			}
		if (!TryGetBindPosition(startTransform, bones, bindposes, out Vector3 start) ||
			!TryGetBindPosition(endTransform, bones, bindposes, out Vector3 end))
		{
			failure = "the renderer bone palette does not contain both bones of the selected limb segment";
			return false;
		}
		Vector3 axis = end - start;
		if (axis.sqrMagnitude < 0.000001f)
		{
			failure = "the bind-space cut segment is degenerate";
			return false;
		}
		axis.Normalize();
		Vector3 center = LimbSeverMath.Candidate(start, end, spec.Level, spec.FractionOffset);

		HashSet<int> limbBones = new HashSet<int>();
		for (int index = 0; index < bones.Length; index++)
			if (bones[index] == spec.LimbRoot || (bones[index] != null && bones[index].IsChildOf(spec.LimbRoot))) limbBones.Add(index);
		if (limbBones.Count == 0)
		{
			failure = "the selected limb root has no bones in this renderer";
			return false;
		}

		HashSet<int> seedBones = FindSeedBones(bones, bindposes, limbBones, center, axis);
		float[] limbWeights = new float[data.Positions.Length];
		float[] seedWeights = new float[data.Positions.Length];
		float[] otherWeights = new float[data.Positions.Length];
		for (int index = 0; index < data.Positions.Length; index++)
		{
			limbWeights[index] = SumWeights(data.Weights[index], limbBones);
			seedWeights[index] = SumWeights(data.Weights[index], seedBones);
			otherWeights[index] = TotalWeight(data.Weights[index]) - limbWeights[index];
		}

		List<Segment3> capsule = BuildCapsuleSegments(bones, bindposes, limbBones, center, axis, end);
		if (capsule.Count == 0)
		{
			failure = "the finite bind-space limb capsule has no segments";
			return false;
		}
		float distalGap = MissingBandWidthWorld / worldScale;
		float selectionMargin = BoundaryWeldToleranceWorld / worldScale;
		float radius = CalculateCapsuleRadius(data.Positions, limbWeights, capsule, center, axis, selectionMargin,
			spec.IsLeg ? 0.28f / worldScale : 0.20f / worldScale);
		Classification classification = ClassifyTriangles(data.Positions, data.Triangles, limbWeights, seedWeights,
			capsule, radius, center, axis, selectionMargin,
			data.GetAdjacency(BoundaryWeldToleranceWorld / worldScale), otherWeights);
		if (classification.Selected.Count == 0)
		{
			failure = "no connected distal triangles reached the bind-space cut capsule";
			return false;
		}
		cut = new BindCut
		{
			SegmentLength = (end - start).magnitude,
			ToBone = data.Bindposes[startIndex], FromBone = data.Bindposes[startIndex].inverse,
			Center = center,
			Axis = axis,
			DistalGap = distalGap,
			CapsuleRadius = radius,
			SelectedTriangles = classification.Selected,
			Classification = classification
		};
		lock (data.CacheGate) data.BindCutCache[cacheKey] = cut;
		return true;
	}

	private static bool TryCreateCapturedBindCut(SourceData data, BoneSnapshot[] bones, CapturedCut spec,
		out BindCut cut, out string failure)
	{
		cut = null;
		failure = null;
		if (!spec.StartBind.HasValue || !spec.EndBind.HasValue || spec.LimbBones == null || spec.LimbBones.Length == 0)
		{
			failure = "captured bone indices are invalid";
			return false;
		}
		Vector3 start = spec.StartBind.Value.inverse.MultiplyPoint3x4(Vector3.zero);
		Vector3 end = spec.EndBind.Value.inverse.MultiplyPoint3x4(Vector3.zero);
		Vector3 axis = end - start;
		if (axis.sqrMagnitude < 0.000001f)
		{
			failure = "the bind-space cut segment is degenerate";
			return false;
		}
		axis.Normalize();
		Vector3 center = LimbSeverMath.Candidate(start, end, spec.Level, spec.FractionOffset);
		HashSet<int> limbBones = new HashSet<int>(spec.LimbBones);
		HashSet<int> seedBones = FindCapturedSeedBones(bones, limbBones, center, axis);
		float[] limbWeights = new float[data.Positions.Length];
		float[] seedWeights = new float[data.Positions.Length];
		float[] otherWeights = new float[data.Positions.Length];
		for (int index = 0; index < data.Positions.Length; index++)
		{
			limbWeights[index] = SumWeights(data.Weights[index], limbBones);
			seedWeights[index] = SumWeights(data.Weights[index], seedBones);
			otherWeights[index] = TotalWeight(data.Weights[index]) - limbWeights[index];
		}
		List<Segment3> capsule = BuildCapturedCapsuleSegments(bones, limbBones, center, axis, end);
		if (capsule.Count == 0)
		{
			failure = "the finite bind-space limb capsule has no segments";
			return false;
		}
		float distalGap = MissingBandWidthWorld / spec.WorldScale;
		float selectionMargin = BoundaryWeldToleranceWorld / spec.WorldScale;
		float radius = CalculateCapsuleRadius(data.Positions, limbWeights, capsule, center, axis,
			selectionMargin, spec.IsLeg ? 0.28f / spec.WorldScale : 0.20f / spec.WorldScale);
		Classification classification = ClassifyTriangles(data.Positions, data.Triangles, limbWeights,
			seedWeights, capsule, radius, center, axis, selectionMargin,
			data.GetAdjacency(BoundaryWeldToleranceWorld / spec.WorldScale), otherWeights);
		if (classification.Selected.Count == 0)
		{
			failure = "no connected distal triangles reached the bind-space cut capsule";
			return false;
		}
		cut = new BindCut
		{
			SegmentLength = (end - start).magnitude,
			ToBone = spec.StartBind.Value, FromBone = spec.StartBind.Value.inverse,
			Center = center,
			Axis = axis,
			DistalGap = distalGap,
			CapsuleRadius = radius,
			SelectedTriangles = classification.Selected,
			Classification = classification
		};
		return true;
	}

	private static HashSet<int> FindCapturedSeedBones(BoneSnapshot[] bones, HashSet<int> limbBones,
		Vector3 cutCenter, Vector3 axis)
	{
		HashSet<int> named = new HashSet<int>();
		foreach (int index in limbBones)
		{
			string name = bones[index].Name.ToLowerInvariant();
			if (name.Contains("hand") || name.Contains("palm") || name.Contains("finger") ||
				name.Contains("foot") || name.Contains("toe")) named.Add(index);
		}
		if (named.Count > 0) return named;
		float farthest = float.NegativeInfinity;
		foreach (int index in limbBones)
			farthest = Mathf.Max(farthest, Vector3.Dot(bones[index].BindPosition - cutCenter, axis));
		HashSet<int> terminal = new HashSet<int>();
		foreach (int index in limbBones)
			if (Vector3.Dot(bones[index].BindPosition - cutCenter, axis) >= farthest - 0.04f)
				terminal.Add(index);
		return terminal;
	}

	private static List<Segment3> BuildCapturedCapsuleSegments(BoneSnapshot[] bones, HashSet<int> limbBones,
		Vector3 cutCenter, Vector3 axis, Vector3 distalFallback)
	{
		List<Segment3> segments = new List<Segment3>();
		foreach (int index in limbBones)
		{
			int parent = bones[index].ParentIndex;
			while (parent >= 0 && !limbBones.Contains(parent)) parent = bones[parent].ParentIndex;
			if (parent < 0) continue;
			Vector3 a = bones[parent].BindPosition;
			Vector3 b = bones[index].BindPosition;
			float da = Vector3.Dot(a - cutCenter, axis);
			float db = Vector3.Dot(b - cutCenter, axis);
			if (da < 0f && db < 0f) continue;
			if (da < 0f && db >= 0f) a = Vector3.Lerp(a, b, da / (da - db));
			else if (db < 0f && da >= 0f) b = Vector3.Lerp(b, a, db / (db - da));
			if ((b - a).sqrMagnitude > 0.000001f) segments.Add(new Segment3(a, b));
		}
		if (segments.Count == 0 && (distalFallback - cutCenter).sqrMagnitude > 0.000001f)
			segments.Add(new Segment3(cutCenter, distalFallback));
		return segments;
	}

	private static int BonePaletteSignature(Transform[] bones)
	{
		unchecked
		{
			int hash = bones.Length;
			for (int index = 0; index < bones.Length; index++)
			{
				Transform bone = bones[index];
				hash = hash * 397 ^ (bone?.name?.GetHashCode() ?? 0);
				hash = hash * 397 ^ (bone?.parent?.name?.GetHashCode() ?? 0);
			}
			return hash;
		}
	}

	private static Classification ClassifyTriangles(Vector3[] positions, List<SourceTriangle> triangles,
		float[] limbWeights, float[] seedWeights, List<Segment3> capsule, float capsuleRadius,
		Vector3 cutCenter, Vector3 cutAxis, float halfBand, List<int>[] adjacency, float[] otherWeights = null)
	{
		// Triangle corners share vertices. Evaluate the identical finite-capsule
		// distance at most once per vertex, and only if spatial gating is needed.
		var distanceKnown = new bool[positions.Length];
		var insideCapsule = new bool[positions.Length];
		bool InsideCapsule(int vertex)
		{
			if (!distanceKnown[vertex])
			{
				insideCapsule[vertex] = DistanceToCapsule(positions[vertex], capsule) <= capsuleRadius;
				distanceKnown[vertex] = true;
			}
			return insideCapsule[vertex];
		}
		bool[] candidate = new bool[triangles.Count];
		float[] projection = new float[triangles.Count];
		float[] triangleLimb = new float[triangles.Count];
		float[] triangleSeed = new float[triangles.Count];
		bool[] protectedByOtherBones = new bool[triangles.Count];
		for (int index = 0; index < triangles.Count; index++)
		{
			SourceTriangle triangle = triangles[index];
			Vector3 a = positions[triangle.A];
			Vector3 b = positions[triangle.B];
			Vector3 c = positions[triangle.C];
			float pa = Vector3.Dot(a - cutCenter, cutAxis);
			float pb = Vector3.Dot(b - cutCenter, cutAxis);
			float pc = Vector3.Dot(c - cutCenter, cutAxis);
			projection[index] = Mathf.Max(pa, Mathf.Max(pb, pc));
			triangleLimb[index] = Mathf.Max(limbWeights[triangle.A], Mathf.Max(limbWeights[triangle.B], limbWeights[triangle.C]));
			triangleSeed[index] = Mathf.Max(seedWeights[triangle.A], Mathf.Max(seedWeights[triangle.B], seedWeights[triangle.C]));
			// Strong ownership at every corner is better evidence than a capsule
			// fitted to the underlying limb: loose sleeves can lie outside it.
			// Mixed/weak triangles still need the spatial gate; other-limb protection
			// and the connected distal-seed flood below remain mandatory.
			bool stronglyOwned = limbWeights[triangle.A] >= StrongLimbWeight &&
				limbWeights[triangle.B] >= StrongLimbWeight && limbWeights[triangle.C] >= StrongLimbWeight;
			candidate[index] = projection[index] >= -halfBand - capsuleRadius * 0.15f &&
				(stronglyOwned || InsideCapsule(triangle.A) || InsideCapsule(triangle.B) || InsideCapsule(triangle.C) ||
				 DistanceToCapsule((a + b + c) / 3f, capsule) <= capsuleRadius);
			// Mixed ownership triangles at the seam remain eligible. A triangle
			// dominated by non-target bones at EVERY corner is not limb tissue,
			// even if a small stray limb influence or a flood path reaches it.
			protectedByOtherBones[index] = otherWeights != null &&
				otherWeights[triangle.A] > limbWeights[triangle.A] &&
				otherWeights[triangle.B] > limbWeights[triangle.B] &&
				otherWeights[triangle.C] > limbWeights[triangle.C];
		}

		bool[] eligible = new bool[candidate.Length];
		for (int index = 0; index < eligible.Length; index++)
			eligible[index] = candidate[index] && !protectedByOtherBones[index];
		bool[] anatomical = BuildAnatomicalMask(eligible, triangleLimb, triangleSeed, adjacency);
		bool[] visited = new bool[triangles.Count];
		HashSet<int> selected = new HashSet<int>();
		for (int start = 0; start < triangles.Count; start++)
		{
			if (!anatomical[start] || visited[start]) continue;
			List<int> component = new List<int>();
			Queue<int> queue = new Queue<int>();
			queue.Enqueue(start);
			visited[start] = true;
			float maximumProjection = float.NegativeInfinity;
			float minimumProjection = float.PositiveInfinity;
			float maximumSeed = 0f;
			float maximumLimb = 0f;
			while (queue.Count > 0)
			{
				int triangle = queue.Dequeue();
				component.Add(triangle);
				maximumProjection = Mathf.Max(maximumProjection, projection[triangle]);
				minimumProjection = Mathf.Min(minimumProjection, projection[triangle]);
				maximumSeed = Mathf.Max(maximumSeed, triangleSeed[triangle]);
				maximumLimb = Mathf.Max(maximumLimb, triangleLimb[triangle]);
				foreach (int neighbor in adjacency[triangle])
				{
					if (!anatomical[neighbor] || visited[neighbor]) continue;
					visited[neighbor] = true;
					queue.Enqueue(neighbor);
				}
			}

			if (maximumProjection <= halfBand || (maximumSeed < StrongSeedWeight && maximumLimb < StrongLimbWeight)) continue;
			float distalSeedWindow = Mathf.Max(0.04f, (maximumProjection - minimumProjection) * 0.15f);
			Queue<int> flood = new Queue<int>();
			HashSet<int> componentSet = new HashSet<int>(component);
			HashSet<int> reached = new HashSet<int>();
			foreach (int triangle in component)
			{
				if (triangleSeed[triangle] < StrongSeedWeight &&
					(projection[triangle] < maximumProjection - distalSeedWindow || triangleLimb[triangle] < StrongLimbWeight)) continue;
				reached.Add(triangle);
				flood.Enqueue(triangle);
			}
			while (flood.Count > 0)
			{
				int triangle = flood.Dequeue();
				foreach (int neighbor in adjacency[triangle])
				{
					if (!componentSet.Contains(neighbor) || !reached.Add(neighbor)) continue;
					flood.Enqueue(neighbor);
				}
			}
			selected.UnionWith(reached);
		}
		return new Classification
		{
			Candidate = candidate,
			Projection = projection,
			LimbWeight = triangleLimb,
			SeedWeight = triangleSeed,
			ProtectedByOtherBones = protectedByOtherBones,
			Selected = selected
		};
	}

	private static bool[] BuildAnatomicalMask(bool[] candidate, float[] triangleLimb,
		float[] triangleSeed, List<int>[] adjacency)
	{
		bool[] anatomical = new bool[candidate.Length];
		bool[] weakVisited = new bool[candidate.Length];
		for (int index = 0; index < candidate.Length; index++)
			anatomical[index] = candidate[index] &&
				(triangleLimb[index] >= MinimumAnatomicalWeight || triangleSeed[index] >= MinimumAnatomicalWeight);

		// A few all-zero triangles can occur at a garment seam. Admit them only
		// when they form a tiny bridge between two target-weighted regions. A
		// terminal weak branch (the red crotch/torso spill seen in the overlay)
		// has only one weighted side and is deliberately excluded.
		for (int start = 0; start < candidate.Length; start++)
		{
			if (!candidate[start] || anatomical[start] || weakVisited[start]) continue;
			List<int> weakComponent = new List<int>();
			HashSet<int> weightedNeighbors = new HashSet<int>();
			Queue<int> queue = new Queue<int>();
			queue.Enqueue(start);
			weakVisited[start] = true;
			while (queue.Count > 0)
			{
				int triangle = queue.Dequeue();
				weakComponent.Add(triangle);
				foreach (int neighbor in adjacency[triangle])
				{
					if (!candidate[neighbor]) continue;
					if (anatomical[neighbor]) weightedNeighbors.Add(neighbor);
					else if (!weakVisited[neighbor])
					{
						weakVisited[neighbor] = true;
						queue.Enqueue(neighbor);
					}
				}
			}
			if (weakComponent.Count > MaximumWeakBridgeTriangles || weightedNeighbors.Count < 2) continue;
			foreach (int triangle in weakComponent) anatomical[triangle] = true;
		}
		return anatomical;
	}

	private static MeshBuffers BuildDebugOverlay(SourceData data, BindCut cut,
		out int candidateCount, out int strongSelectedCount, out int connectedWeakCount,
		out int cutBoundaryCount, List<DebugPoint> seam, List<DebugPoint> primary,
		List<DebugPoint> mound, List<DebugPoint> bones, List<DebugPoint> decals)
	{
		candidateCount = 0;
		strongSelectedCount = 0;
		connectedWeakCount = 0;
		cutBoundaryCount = 0;
		List<int>[] categories =
		{
			new List<int>(), // Strong target-limb/seed-weighted triangles selected for removal.
			new List<int>(), // Selected only through component connectivity; primary bleed diagnostic.
			new List<int>(), // Spatial candidates excluded from the selected distal component.
			new List<int>()  // Selected source triangles intersected by either wide-band plane.
		};
		Classification classification = cut.Classification;
		foreach (SourceTriangle triangle in data.Triangles)
		{
			bool candidate = classification.Candidate[triangle.Index];
			bool selected = classification.Selected.Contains(triangle.Index);
			if (candidate) candidateCount++;
			if (!selected)
			{
				if (candidate) AddTriangle(categories[2], triangle);
				continue;
			}

			bool stronglyWeighted = classification.SeedWeight[triangle.Index] >= StrongSeedWeight ||
				classification.LimbWeight[triangle.Index] >= StrongLimbWeight;
			if (stronglyWeighted)
			{
				strongSelectedCount++;
				AddTriangle(categories[0], triangle);
			}
			else
			{
				connectedWeakCount++;
				AddTriangle(categories[1], triangle);
			}

			float a = Vector3.Dot(data.Positions[triangle.A] - cut.Center, cut.Axis);
			float b = Vector3.Dot(data.Positions[triangle.B] - cut.Center, cut.Axis);
			float c = Vector3.Dot(data.Positions[triangle.C] - cut.Center, cut.Axis);
			float minimum = Mathf.Min(a, Mathf.Min(b, c));
			float maximum = Mathf.Max(a, Mathf.Max(b, c));
			bool crossesBodyPlane = minimum <= 0f && maximum >= 0f;
			bool crossesGibPlane = minimum <= cut.DistalGap && maximum >= cut.DistalGap;
			if (!crossesBodyPlane && !crossesGibPlane) continue;
			cutBoundaryCount++;
			AddTriangle(categories[3], triangle);
		}

		IndexedBuilder overlay = new IndexedBuilder(data, 9, requiresGeneratedSurfaceAttributes: true);
		for (int category = 0; category < categories.Length; category++)
			for (int index = 0; index + 2 < categories[category].Count; index += 3)
				overlay.Triangle(overlay.Original(categories[category][index]),
					overlay.Original(categories[category][index + 1]),
					overlay.Original(categories[category][index + 2]), category);
		float markerRadius = Mathf.Clamp(cut.CapsuleRadius * 0.025f, 0.001f, 0.004f);
		foreach (DebugPoint point in seam) AddDebugMarker(overlay, data, point, cut.Axis, markerRadius, 4);
		foreach (DebugPoint point in primary) AddDebugMarker(overlay, data, point, cut.Axis, markerRadius * 1.55f, 5);
		foreach (DebugPoint point in mound) AddDebugMarker(overlay, data, point, cut.Axis, markerRadius * 1.25f, 6);
		foreach (DebugPoint point in bones) AddDebugMarker(overlay, data, point, cut.Axis, markerRadius * 2f, 7);
		foreach (DebugPoint point in decals) AddDebugMarker(overlay, data, point, cut.Axis, markerRadius * 1.8f, 8);
		return overlay.BuildBuffers(data.Name + "_RPT_LimbClassificationDebug", data.Bindposes);
	}

	private static void AddDebugMarker(IndexedBuilder builder, SourceData data, DebugPoint point,
		Vector3 normal, float radius, int submesh)
	{
		Vector3 axisU = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.8f ? Vector3.up : Vector3.right).normalized;
		Vector3 axisV = Vector3.Cross(normal, axisU).normalized;
		VertexData template = data.Vertex(Mathf.Clamp(point.SourceVertex, 0, data.Positions.Length - 1));
		template.Weight = point.Weight;
		template.Normal = normal;
		VertexData a = template;
		VertexData b = template;
		VertexData c = template;
		a.Position = point.Position + axisV * radius;
		b.Position = point.Position - axisU * radius - axisV * radius;
		c.Position = point.Position + axisU * radius - axisV * radius;
		int ia = builder.Add(a);
		int ib = builder.Add(b);
		int ic = builder.Add(c);
		Vector3 cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
		if (Vector3.Dot(cross, normal) >= 0f) builder.Triangle(ia, ib, ic, submesh);
		else builder.Triangle(ia, ic, ib, submesh);
	}

	private static void AddTriangle(List<int> destination, SourceTriangle triangle)
	{
		destination.Add(triangle.A);
		destination.Add(triangle.B);
		destination.Add(triangle.C);
	}

	private static List<int>[] BuildTriangleAdjacency(List<SourceTriangle> triangles, Vector3[] positions,
		float connectivityTolerance, int[] canonical = null)
	{
		List<int>[] adjacency = new List<int>[triangles.Count];
		for (int index = 0; index < adjacency.Length; index++) adjacency[index] = new List<int>();
		canonical ??= BuildCanonicalPositionMap(positions, connectivityTolerance);
		Dictionary<BoundaryEdge, List<int>> owners = new Dictionary<BoundaryEdge, List<int>>();
		for (int index = 0; index < triangles.Count; index++)
		{
			SourceTriangle triangle = triangles[index];
			AddOwner(owners, new BoundaryEdge(canonical[triangle.A], canonical[triangle.B]), index);
			AddOwner(owners, new BoundaryEdge(canonical[triangle.B], canonical[triangle.C]), index);
			AddOwner(owners, new BoundaryEdge(canonical[triangle.C], canonical[triangle.A]), index);
		}
		foreach (List<int> edgeOwners in owners.Values)
		{
			for (int left = 0; left < edgeOwners.Count; left++)
				for (int right = left + 1; right < edgeOwners.Count; right++)
				{
					adjacency[edgeOwners[left]].Add(edgeOwners[right]);
					adjacency[edgeOwners[right]].Add(edgeOwners[left]);
				}
		}
		return adjacency;
	}

	private static int[] BuildCanonicalPositionMap(Vector3[] positions, float tolerance)
	{
		float cellSize = Mathf.Max(0.0000001f, tolerance);
		float toleranceSquared = cellSize * cellSize;
		Dictionary<PositionKey, List<int>> buckets = new Dictionary<PositionKey, List<int>>();
		int[] canonical = new int[positions.Length];
		for (int vertex = 0; vertex < positions.Length; vertex++)
		{
			Vector3 position = positions[vertex];
			PositionKey key = new PositionKey(position, cellSize);
			int representative = -1;
			for (int x = -1; x <= 1 && representative < 0; x++)
				for (int y = -1; y <= 1 && representative < 0; y++)
					for (int z = -1; z <= 1 && representative < 0; z++)
					{
						PositionKey neighbor = new PositionKey(key.X + x, key.Y + y, key.Z + z);
						if (!buckets.TryGetValue(neighbor, out List<int> candidates)) continue;
						foreach (int candidate in candidates)
						{
							if ((positions[candidate] - position).sqrMagnitude > toleranceSquared) continue;
							representative = candidate;
							break;
						}
					}
			if (representative < 0)
			{
				representative = vertex;
				if (!buckets.TryGetValue(key, out List<int> bucket)) buckets[key] = bucket = new List<int>();
				bucket.Add(vertex);
			}
			canonical[vertex] = representative;
		}
		return canonical;
	}

	private static void AddOwner(Dictionary<BoundaryEdge, List<int>> owners, BoundaryEdge edge, int triangle)
	{
		if (edge.A == edge.B) return;
		if (!owners.TryGetValue(edge, out List<int> list)) owners[edge] = list = new List<int>(2);
		list.Add(triangle);
	}

	private static bool ClipTriangle(SourceData data, SourceTriangle triangle, Vector3 planePoint, Vector3 planeNormal,
		bool keepPositive, IndexedBuilder builder, int planeId, List<BoundaryEdge> boundary, out string failure)
	{
		failure = null;
		int[] sources = { triangle.A, triangle.B, triangle.C };
		float[] distances = new float[3];
		for (int index = 0; index < 3; index++)
		{
			distances[index] = Vector3.Dot(data.Positions[sources[index]] - planePoint, planeNormal);
			if (Mathf.Abs(distances[index]) <= PlaneEpsilon) distances[index] = 0f;
		}
		if (distances[0] == 0f && distances[1] == 0f && distances[2] == 0f)
		{
			failure = "a source triangle is coplanar with the cut";
			return false;
		}

		List<int> polygon = new List<int>(4);
		List<int> planeVertices = new List<int>(2);
		for (int index = 0; index < 3; index++)
		{
			int next = (index + 1) % 3;
			bool currentInside = keepPositive ? distances[index] >= 0f : distances[index] <= 0f;
			bool nextInside = keepPositive ? distances[next] >= 0f : distances[next] <= 0f;
			if (currentInside)
			{
				int original = builder.Original(sources[index]);
				polygon.Add(original);
				if (distances[index] == 0f) AddUnique(planeVertices, original);
			}
			if (currentInside == nextInside) continue;
			float t = distances[index] / (distances[index] - distances[next]);
			// Snapped endpoints must reuse their original vertex, not create a
			// distinct crossing at t=0/1 and a zero-area triangle beside the seam.
			int crossing = distances[index] == 0f ? builder.Original(sources[index]) :
				distances[next] == 0f ? builder.Original(sources[next]) : builder.Crossing(sources[index], sources[next], t, planeId);
			polygon.Add(crossing);
			AddUnique(planeVertices, crossing);
		}
		builder.Polygon(polygon, triangle.Submesh);
		if (planeVertices.Count == 2) boundary.Add(new BoundaryEdge(planeVertices[0], planeVertices[1]));
		// A triangle touching the plane at one vertex contributes no seam edge.
		else if (planeVertices.Count > 2)
		{
			failure = $"clipping produced {planeVertices.Count} boundary vertices instead of two";
			return false;
		}
		return true;
	}

	private static bool TryOrderClosedLoops(List<BoundaryEdge> segments, List<VertexData> vertices,
		Vector3 expectedNormal, float weldTolerance, float maximumOpenClosure,
		out List<List<int>> loops, out string failure)
	{
		loops = new List<List<int>>();
		failure = null;
		if (segments == null || segments.Count == 0) return true;
		if (!TryWeldBoundarySegments(segments, vertices, weldTolerance, out HashSet<BoundaryEdge> unique,
			out string weldFailure))
		{
			failure = weldFailure;
			return false;
		}
		Dictionary<int, List<int>> adjacency = new Dictionary<int, List<int>>();
		foreach (BoundaryEdge edge in unique)
		{
			if (edge.A == edge.B)
			{
				failure = "the boundary contains a zero-length edge";
				return false;
			}
			AddNeighbor(adjacency, edge.A, edge.B);
			AddNeighbor(adjacency, edge.B, edge.A);
		}
		List<KeyValuePair<int, List<int>>> invalidDegrees = adjacency
			.Where(item => item.Value.Count < 1 || item.Value.Count > 2).ToList();
		if (invalidDegrees.Count > 0)
		{
			KeyValuePair<int, List<int>> first = invalidDegrees[0];
			Vector3 position = vertices[first.Key].Position;
			float nearest = float.PositiveInfinity;
			for (int index = 1; index < invalidDegrees.Count; index++)
				nearest = Mathf.Min(nearest, Vector3.Distance(position, vertices[invalidDegrees[index].Key].Position));
			string nearestText = IsFinite(nearest) ? $", nearestInvalid={nearest:F6}" : string.Empty;
			failure = $"boundary vertex {first.Key} at {position} has degree {first.Value.Count}, expected one or two " +
				$"(invalidVertices={invalidDegrees.Count}, rawSegments={segments.Count}, weldedSegments={unique.Count}{nearestText})";
			return false;
		}

		HashSet<int> remaining = new HashSet<int>(adjacency.Keys);
		while (remaining.Count > 0)
		{
			int componentSeed = remaining.Min();
			HashSet<int> component = new HashSet<int>();
			Stack<int> pending = new Stack<int>();
			pending.Push(componentSeed);
			while (pending.Count > 0)
			{
				int vertex = pending.Pop();
				if (!component.Add(vertex)) continue;
				foreach (int neighbor in adjacency[vertex]) pending.Push(neighbor);
			}
			remaining.ExceptWith(component);

			List<int> endpoints = component.Where(vertex => adjacency[vertex].Count == 1).OrderBy(vertex => vertex).ToList();
			if (endpoints.Count != 0 && endpoints.Count != 2)
			{
				failure = $"a boundary component has {endpoints.Count} open endpoints, expected zero or two";
				return false;
			}
			bool closeOpenShell = endpoints.Count == 2;
			if (closeOpenShell)
			{
				float gap = Vector3.Distance(vertices[endpoints[0]].Position, vertices[endpoints[1]].Position);
				if (!IsFinite(gap) || gap > maximumOpenClosure + weldTolerance)
				{
					failure = $"an open garment boundary gap of {gap:F6} exceeds the finite limb capsule closure limit " +
						$"of {maximumOpenClosure:F6}";
					return false;
				}
			}

			int start = closeOpenShell ? endpoints[0] : component.Min();
			List<int> loop = new List<int>(component.Count);
			HashSet<int> visited = new HashSet<int>();
			int previous = -1;
			int current = start;
			for (int guard = 0; guard <= component.Count; guard++)
			{
				if (!visited.Add(current))
				{
					if (!closeOpenShell && current == start) break;
					failure = "the boundary branches back into an already visited vertex";
					return false;
				}
				loop.Add(current);
				List<int> choices = adjacency[current].Where(neighbor => neighbor != previous).ToList();
				if (choices.Count == 0)
				{
					if (closeOpenShell && current == endpoints[1]) break;
					failure = "the boundary traversal ended before reaching its expected endpoint";
					return false;
				}
				int next = previous < 0 ? choices.Min() : choices[0];
				previous = current;
				current = next;
			}
			if (loop.Count != component.Count || loop.Count < 3 || (!closeOpenShell && current != start))
			{
				failure = "the boundary component could not be reconstructed as one closed loop";
				return false;
			}
			Vector3 polygonNormal = Vector3.zero;
			for (int index = 0; index < loop.Count; index++)
			{
				Vector3 a = vertices[loop[index]].Position;
				Vector3 b = vertices[loop[(index + 1) % loop.Count]].Position;
				polygonNormal += Vector3.Cross(a, b);
			}
			if (polygonNormal.sqrMagnitude <= 0.0000000001f)
			{
				failure = "the ordered boundary has zero projected area";
				return false;
			}
			if (Vector3.Dot(polygonNormal, expectedNormal) < 0f) loop.Reverse();
			loops.Add(loop);
		}
		return true;
	}

	private static bool TryWeldBoundarySegments(List<BoundaryEdge> segments, List<VertexData> vertices,
		float tolerance, out HashSet<BoundaryEdge> welded, out string failure)
	{
		welded = new HashSet<BoundaryEdge>();
		failure = null;
		float cellSize = Mathf.Max(0.0000001f, tolerance);
		float toleranceSquared = cellSize * cellSize;
		Dictionary<PositionKey, List<int>> buckets = new Dictionary<PositionKey, List<int>>();
		Dictionary<int, int> canonical = new Dictionary<int, int>();
		foreach (BoundaryEdge segment in segments)
		{
			int a = FindOrCreateCanonicalBoundaryVertex(segment.A, vertices, cellSize, toleranceSquared, buckets, canonical);
			int b = FindOrCreateCanonicalBoundaryVertex(segment.B, vertices, cellSize, toleranceSquared, buckets, canonical);
			if (a == b) continue;
			welded.Add(new BoundaryEdge(a, b));
		}
		if (welded.Count == 0)
		{
			failure = $"all boundary segments were shorter than the {BoundaryWeldToleranceWorld * 1000f:F2} mm seam tolerance";
			return false;
		}
		return true;
	}

	private static int FindOrCreateCanonicalBoundaryVertex(int vertex, List<VertexData> vertices,
		float cellSize, float toleranceSquared, Dictionary<PositionKey, List<int>> buckets,
		Dictionary<int, int> canonical)
	{
		if (canonical.TryGetValue(vertex, out int existing)) return existing;
		Vector3 position = vertices[vertex].Position;
		PositionKey key = new PositionKey(position, cellSize);
		for (int x = -1; x <= 1; x++)
			for (int y = -1; y <= 1; y++)
				for (int z = -1; z <= 1; z++)
				{
					PositionKey neighbor = new PositionKey(key.X + x, key.Y + y, key.Z + z);
					if (!buckets.TryGetValue(neighbor, out List<int> candidates)) continue;
					foreach (int candidate in candidates)
					{
						if ((vertices[candidate].Position - position).sqrMagnitude > toleranceSquared) continue;
						canonical[vertex] = candidate;
						return candidate;
					}
				}
		if (!buckets.TryGetValue(key, out List<int> bucket)) buckets[key] = bucket = new List<int>();
		bucket.Add(vertex);
		canonical[vertex] = vertex;
		return vertex;
	}

	private static void AddNeighbor(Dictionary<int, List<int>> adjacency, int vertex, int neighbor)
	{
		if (!adjacency.TryGetValue(vertex, out List<int> list)) adjacency[vertex] = list = new List<int>(2);
		if (!list.Contains(neighbor)) list.Add(neighbor);
	}

	private static bool AddWound(IndexedBuilder destination, IndexedBuilder boundarySource, List<int> loop,
		IReadOnlyList<BoundaryEdge> retainedEdges,
		Vector3 facingNormal, float worldScale, int submesh, int retainedSubmeshCount,
		CutSpec spec, bool gibSide,
		bool primary, out ProceduralLimbWoundGeometry.Surface wound, out string failure,
		LimbClothingProfiles.Context clothing = null)
	{
		ProceduralLimbWoundGeometry.BoundaryVertex[] boundary = new ProceduralLimbWoundGeometry.BoundaryVertex[loop.Count];
		for (int index = 0; index < loop.Count; index++)
		{
			VertexData source = boundarySource.Vertices[loop[index]];
			boundary[index] = new ProceduralLimbWoundGeometry.BoundaryVertex(source.Position, source.Normal, source.Weight);
		}
		var request = new ProceduralLimbWoundGeometry.Request
		{
			Boundary = boundary,
			FacingNormal = facingNormal,
			StableCutAxis = gibSide ? -facingNormal : facingNormal,
			WorldScale = worldScale,
			IsLeg = spec.IsLeg,
			Level = spec.Level,
			Seed = spec.Seed,
			GibSide = gibSide,
			AddAnatomicalDetails = primary, Clothing = clothing
		};
		if (!ProceduralLimbWoundGeometry.TryGenerate(request, out wound, out failure)) return false;
		float movementScale = 1f;
		if (wound.ReplacementBoundary != null && !TryApplyClothFold(boundarySource, retainedEdges, loop,
			boundary, wound.ReplacementBoundary, worldScale, out movementScale, out string foldFailure))
		{
			var diagnostic = wound.Fold;
			diagnostic.Status = "fallback"; diagnostic.Reason = foldFailure;
			request.Clothing = null;
			if (!ProceduralLimbWoundGeometry.TryGenerate(request, out wound, out failure)) return false;
			wound.Fold = diagnostic;
			if (primary) wound.ClothCore = new LimbClothingProfiles.Core { KeepPrimaryUnchanged = true };
		}
		if (wound.ReplacementBoundary != null)
		{
			wound.Fold.RimMovementScale = movementScale;
			for (int i = 0; i < wound.ExactSeamVertexIndices.Length; i++)
			{
				int index = wound.ExactSeamVertexIndices[i];
				var vertex = wound.Vertices[index];
				wound.Vertices[index] = new ProceduralLimbWoundGeometry.GeneratedVertex(wound.ReplacementBoundary[i].Position,
					vertex.Normal, vertex.Uv, vertex.Mask, vertex.Weight, vertex.BoundarySource, vertex.Feature);
			}
		}
		if (primary && !TryMoveDecalsOntoRetainedTriangles(wound, boundarySource, loop,
			retainedEdges, retainedSubmeshCount, worldScale, out failure)) return false;

		int baseVertex = destination.Vertices.Count;
		foreach (ProceduralLimbWoundGeometry.GeneratedVertex generated in wound.Vertices)
		{
			int sourceIndex = Mathf.Clamp(generated.BoundarySource, 0, loop.Count - 1);
			VertexData vertex = boundarySource.Vertices[loop[sourceIndex]];
			vertex.Position = generated.Position;
			vertex.Normal = generated.Normal;
			Vector3 tangent = Vector3.ProjectOnPlane(
				Vector3.Cross(facingNormal, Mathf.Abs(facingNormal.y) < 0.8f ? Vector3.up : Vector3.right),
				generated.Normal).normalized;
			if (tangent.sqrMagnitude < 0.5f)
				tangent = Vector3.Cross(generated.Normal,
					Mathf.Abs(generated.Normal.z) < 0.8f ? Vector3.forward : Vector3.right).normalized;
			vertex.Tangent = new Vector4(tangent.x, tangent.y, tangent.z, 1f);
			vertex.Uv0 = generated.Uv;
			vertex.Color = generated.Mask;
			vertex.Weight = generated.Weight;
			destination.Add(vertex);
		}
		for (int index = 0; index + 2 < wound.Triangles.Length; index += 3)
			destination.Triangle(baseVertex + wound.Triangles[index], baseVertex + wound.Triangles[index + 1],
				baseVertex + wound.Triangles[index + 2], submesh);
		return true;
	}

	private static bool DisturbBoundaryLoop(IndexedBuilder builder, IReadOnlyList<BoundaryEdge> segments,
		List<int> loop, Vector3 planeNormal, float worldScale, CutSpec spec, bool gibSide, out string failure)
	{
		failure = null;
		if (builder == null || segments == null || loop == null || loop.Count < 3 || spec == null ||
			worldScale <= 0f || !IsFinite(planeNormal) || planeNormal.sqrMagnitude < 0.5f)
		{
			failure = "the validated cut loop or disturbance frame is unavailable";
			return false;
		}
		ProceduralLimbWoundGeometry.BoundaryVertex[] original = new ProceduralLimbWoundGeometry.BoundaryVertex[loop.Count];
		for (int index = 0; index < loop.Count; index++)
		{
			VertexData vertex = builder.Vertices[loop[index]];
			original[index] = new ProceduralLimbWoundGeometry.BoundaryVertex(vertex.Position, vertex.Normal, vertex.Weight);
		}
		ProceduralLimbWoundGeometry.BoundaryVertex[] disturbed = ProceduralLimbWoundGeometry.DisturbBoundary(
			original, planeNormal, worldScale, spec.IsLeg, spec.Seed, gibSide);
		if (disturbed == null || disturbed.Length != loop.Count)
		{
			failure = "the deterministic rim curve returned an invalid boundary";
			return false;
		}

		HashSet<int> candidates = new HashSet<int>(loop);
		foreach (BoundaryEdge segment in segments)
		{
			candidates.Add(segment.A);
			candidates.Add(segment.B);
		}
		float tolerance = BoundaryWeldToleranceWorld / worldScale;
		float toleranceSquared = tolerance * tolerance;
		foreach (int vertexIndex in candidates)
		{
			if (vertexIndex < 0 || vertexIndex >= builder.Vertices.Count) continue;
			Vector3 position = builder.Vertices[vertexIndex].Position;
			int nearest = -1;
			float nearestSquared = float.PositiveInfinity;
			for (int loopIndex = 0; loopIndex < original.Length; loopIndex++)
			{
				float squared = (position - original[loopIndex].Position).sqrMagnitude;
				if (squared > toleranceSquared || squared >= nearestSquared) continue;
				nearest = loopIndex;
				nearestSquared = squared;
			}
			if (nearest < 0) continue;
			VertexData moved = builder.Vertices[vertexIndex];
			moved.Position += disturbed[nearest].Position - original[nearest].Position;
			builder.Vertices[vertexIndex] = moved;
		}
		for (int index = 0; index < loop.Count; index++)
			if ((builder.Vertices[loop[index]].Position - disturbed[index].Position).sqrMagnitude > 0.0000000001f)
			{
				failure = $"rim vertex {index} did not receive the shared post-cut displacement";
				return false;
			}
		return true;
	}

	private static bool TryMoveDecalsOntoRetainedTriangles(ProceduralLimbWoundGeometry.Surface wound,
		IndexedBuilder retained, List<int> loop, IReadOnlyList<BoundaryEdge> retainedEdges,
		int retainedSubmeshCount, float worldScale, out string failure)
	{
		failure = null;
		float tolerance = BoundaryWeldToleranceWorld / worldScale;
		List<int> eligible = new List<int>();
		List<int[]> triangles = new List<int[]>();
		List<float> lengths = new List<float>();
		float circumference = 0f;
		for (int edge = 0; edge < loop.Count; edge++)
		{
			int next = (edge + 1) % loop.Count;
			Vector3 pa = retained.Vertices[loop[edge]].Position;
			Vector3 pb = retained.Vertices[loop[next]].Position;
			// A synthetic garment closure belongs to the cap only, not to skin.
			// Resample across real retained edges instead of inventing a triangle.
			if (!ContainsGeometricEdge(retainedEdges, retained.Vertices, pa, pb, tolerance)) continue;
			if (!TryFindRetainedTriangle(retained, retainedSubmeshCount, loop[edge], loop[next],
				tolerance, out int ta, out int tb, out int tc))
			{
				failure = $"real boundary edge {edge}-{next} lost its retained triangle";
				return false;
			}
			float length = Vector3.Distance(pa, pb);
			if (length <= tolerance) continue;
			eligible.Add(edge);
			triangles.Add(new[] { ta, tb, tc });
			lengths.Add(length);
			circumference += length;
		}
		if (wound.DecalSamples.Length > 0 && circumference <= tolerance)
		{
			failure = "the cap has no real retained circumference for blood samples";
			return false;
		}
		for (int sampleIndex = 0; sampleIndex < wound.DecalSamples.Length; sampleIndex++)
		{
			float distance = circumference * (sampleIndex + 0.5f) / wound.DecalSamples.Length;
			int selected = 0;
			while (selected + 1 < lengths.Count && distance > lengths[selected]) distance -= lengths[selected++];
			int edge = eligible[selected];
			int a = triangles[selected][0], b = triangles[selected][1], c = triangles[selected][2];
			float t = Mathf.Clamp01(distance / lengths[selected]);
			ProceduralLimbWoundGeometry.DecalSample sample = new ProceduralLimbWoundGeometry.DecalSample(
				Vector3.zero, Vector3.up, default, edge, (edge + 1) % loop.Count, t);
			const float interiorWeight = 0.22f;
			Vector3 barycentric = new Vector3((1f - sample.BoundaryT) * (1f - interiorWeight),
				sample.BoundaryT * (1f - interiorWeight), interiorWeight);
			VertexData va = retained.Vertices[a];
			VertexData vb = retained.Vertices[b];
			VertexData vc = retained.Vertices[c];
			Vector3 position = va.Position * barycentric.x + vb.Position * barycentric.y + vc.Position * barycentric.z;
			Vector3 normal = (va.Normal * barycentric.x + vb.Normal * barycentric.y + vc.Normal * barycentric.z).normalized;
			BoneWeight weight = BlendWeights(va.Weight, vb.Weight, vc.Weight, barycentric);
			wound.DecalSamples[sampleIndex] = sample.OnRetainedTriangle(position, normal, weight,
				a, b, c, barycentric);
		}
		return true;
	}

	private static bool TryFindRetainedTriangle(IndexedBuilder retained, int retainedSubmeshCount,
		int boundaryA, int boundaryB, float tolerance, out int a, out int b, out int c)
	{
		a = b = c = -1;
		Vector3 positionA = retained.Vertices[boundaryA].Position;
		Vector3 positionB = retained.Vertices[boundaryB].Position;
		float toleranceSquared = tolerance * tolerance;
		int count = Mathf.Min(retainedSubmeshCount, retained.Submeshes.Count);
		for (int submesh = 0; submesh < count; submesh++)
		{
			List<int> indices = retained.Submeshes[submesh];
			for (int triangle = 0; triangle + 2 < indices.Count; triangle += 3)
			{
				int[] corners = { indices[triangle], indices[triangle + 1], indices[triangle + 2] };
				int foundA = -1;
				int foundB = -1;
				for (int corner = 0; corner < 3; corner++)
				{
					Vector3 position = retained.Vertices[corners[corner]].Position;
					if (foundA < 0 && (corners[corner] == boundaryA || (position - positionA).sqrMagnitude <= toleranceSquared))
						foundA = corner;
					if (foundB < 0 && (corners[corner] == boundaryB || (position - positionB).sqrMagnitude <= toleranceSquared))
						foundB = corner;
				}
				if (foundA < 0 || foundB < 0 || foundA == foundB) continue;
				int foundC = 3 - foundA - foundB;
				a = corners[foundA];
				b = corners[foundB];
				c = corners[foundC];
				return true;
			}
		}
		return false;
	}

	private static BoneWeight BlendWeights(BoneWeight a, BoneWeight b, BoneWeight c, Vector3 barycentric)
	{
		Dictionary<int, float> weights = new Dictionary<int, float>();
		AddWeight(weights, a.boneIndex0, a.weight0 * barycentric.x);
		AddWeight(weights, a.boneIndex1, a.weight1 * barycentric.x);
		AddWeight(weights, a.boneIndex2, a.weight2 * barycentric.x);
		AddWeight(weights, a.boneIndex3, a.weight3 * barycentric.x);
		AddWeight(weights, b.boneIndex0, b.weight0 * barycentric.y);
		AddWeight(weights, b.boneIndex1, b.weight1 * barycentric.y);
		AddWeight(weights, b.boneIndex2, b.weight2 * barycentric.y);
		AddWeight(weights, b.boneIndex3, b.weight3 * barycentric.y);
		AddWeight(weights, c.boneIndex0, c.weight0 * barycentric.z);
		AddWeight(weights, c.boneIndex1, c.weight1 * barycentric.z);
		AddWeight(weights, c.boneIndex2, c.weight2 * barycentric.z);
		AddWeight(weights, c.boneIndex3, c.weight3 * barycentric.z);
		return CreateWeight(weights);
	}

	private static int FindPrimaryLoop(List<List<int>> loops, List<VertexData> vertices,
		Vector3 projectedAxisPoint, Vector3 normal, out float bestScore)
	{
		bestScore = float.NegativeInfinity;
		int bestIndex = -1;
		Vector3 axisU = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.8f ? Vector3.up : Vector3.right).normalized;
		Vector3 axisV = Vector3.Cross(normal, axisU).normalized;
		for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
		{
			List<int> loop = loops[loopIndex];
			Vector2[] polygon = new Vector2[loop.Count];
			Vector2 center = Vector2.zero;
			float areaTwice = 0f;
			for (int index = 0; index < loop.Count; index++)
			{
				Vector3 offset = vertices[loop[index]].Position - projectedAxisPoint;
				polygon[index] = new Vector2(Vector3.Dot(offset, axisU), Vector3.Dot(offset, axisV));
				center += polygon[index];
			}
			center /= loop.Count;
			for (int index = 0; index < loop.Count; index++)
			{
				Vector2 a = polygon[index];
				Vector2 b = polygon[(index + 1) % loop.Count];
				areaTwice += a.x * b.y - b.x * a.y;
			}
			float area = Mathf.Abs(areaTwice) * 0.5f;
			bool containsAxis = ContainsPoint(polygon, Vector2.zero);
			// Axis containment dominates. Otherwise prefer a substantial loop close
			// to the anatomical axis over tiny garment trim or floating shells.
			float score = (containsAxis ? 1000000f : 0f) + Mathf.Sqrt(area) * 100f - center.magnitude * 10f;
			if (score <= bestScore) continue;
			bestScore = score;
			bestIndex = loopIndex;
		}
		return bestIndex;
	}

	private static bool ContainsPoint(Vector2[] polygon, Vector2 point)
	{
		bool inside = false;
		for (int current = 0, previous = polygon.Length - 1; current < polygon.Length; previous = current++)
		{
			Vector2 a = polygon[current];
			Vector2 b = polygon[previous];
			if ((a.y > point.y) == (b.y > point.y)) continue;
			float crossing = (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x;
			if (point.x < crossing) inside = !inside;
		}
		return inside;
	}

	private static Vector3[] CopyExactOuterLoop(IReadOnlyList<Vector3> source)
	{
		Vector3[] copy = new Vector3[source.Count];
		for (int index = 0; index < source.Count; index++) copy[index] = source[index];
		return copy;
	}

	private static bool TryGetBindPosition(Transform target, Transform[] bones, Matrix4x4[] bindposes, out Vector3 position)
	{
		for (int index = 0; index < bones.Length; index++)
		{
			if (bones[index] != target) continue;
			position = bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero);
			return IsFinite(position);
		}
		position = default;
		return false;
	}

	private static HashSet<int> FindSeedBones(Transform[] bones, Matrix4x4[] bindposes, HashSet<int> limbBones,
		Vector3 cutCenter, Vector3 axis)
	{
		HashSet<int> named = new HashSet<int>();
		foreach (int index in limbBones)
		{
			string name = bones[index]?.name?.ToLowerInvariant() ?? string.Empty;
			if (name.Contains("hand") || name.Contains("palm") || name.Contains("finger") ||
				name.Contains("foot") || name.Contains("toe")) named.Add(index);
		}
		if (named.Count > 0) return named;
		float farthest = float.NegativeInfinity;
		foreach (int index in limbBones)
		{
			Vector3 position = bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero);
			farthest = Mathf.Max(farthest, Vector3.Dot(position - cutCenter, axis));
		}
		HashSet<int> terminal = new HashSet<int>();
		foreach (int index in limbBones)
		{
			Vector3 position = bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero);
			if (Vector3.Dot(position - cutCenter, axis) >= farthest - 0.04f) terminal.Add(index);
		}
		return terminal;
	}

	private static List<Segment3> BuildCapsuleSegments(Transform[] bones, Matrix4x4[] bindposes,
		HashSet<int> limbBones, Vector3 cutCenter, Vector3 axis, Vector3 distalFallback)
	{
		Dictionary<Transform, int> indices = new Dictionary<Transform, int>();
		foreach (int index in limbBones) if (bones[index] != null) indices[bones[index]] = index;
		List<Segment3> segments = new List<Segment3>();
		foreach (int index in limbBones)
		{
			Transform parent = bones[index]?.parent;
			while (parent != null && !indices.ContainsKey(parent)) parent = parent.parent;
			if (parent == null) continue;
			Vector3 a = bindposes[indices[parent]].inverse.MultiplyPoint3x4(Vector3.zero);
			Vector3 b = bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero);
			float da = Vector3.Dot(a - cutCenter, axis);
			float db = Vector3.Dot(b - cutCenter, axis);
			if (da < 0f && db < 0f) continue;
			if (da < 0f && db >= 0f) a = Vector3.Lerp(a, b, da / (da - db));
			else if (db < 0f && da >= 0f) b = Vector3.Lerp(b, a, db / (db - da));
			if ((b - a).sqrMagnitude > 0.000001f) segments.Add(new Segment3(a, b));
		}
		if (segments.Count == 0 && (distalFallback - cutCenter).sqrMagnitude > 0.000001f)
			segments.Add(new Segment3(cutCenter, distalFallback));
		return segments;
	}

	private static float CalculateCapsuleRadius(Vector3[] positions, float[] limbWeights, List<Segment3> segments,
		Vector3 cutCenter, Vector3 axis, float halfBand, float maximum)
	{
		List<float> distances = new List<float>();
		for (int index = 0; index < positions.Length; index++)
		{
			if (limbWeights[index] < StrongLimbWeight || Vector3.Dot(positions[index] - cutCenter, axis) < -halfBand) continue;
			distances.Add(DistanceToCapsule(positions[index], segments));
		}
		if (distances.Count == 0) return Mathf.Min(maximum, 0.12f);
		distances.Sort();
		float percentile = distances[Mathf.Clamp(Mathf.RoundToInt((distances.Count - 1) * 0.9f), 0, distances.Count - 1)];
		return Mathf.Clamp(percentile * 1.35f + 0.01f, 0.035f, maximum);
	}

	private static float DistanceToCapsule(Vector3 point, List<Segment3> segments)
	{
		float best = float.PositiveInfinity;
		foreach (Segment3 segment in segments)
		{
			Vector3 delta = segment.B - segment.A;
			float denominator = delta.sqrMagnitude;
			float t = denominator > 0.000001f ? Mathf.Clamp01(Vector3.Dot(point - segment.A, delta) / denominator) : 0f;
			best = Mathf.Min(best, Vector3.Distance(point, segment.A + delta * t));
		}
		return best;
	}

	private static float SumWeights(BoneWeight weight, HashSet<int> selected)
	{
		float sum = 0f;
		if (selected.Contains(weight.boneIndex0)) sum += weight.weight0;
		if (selected.Contains(weight.boneIndex1)) sum += weight.weight1;
		if (selected.Contains(weight.boneIndex2)) sum += weight.weight2;
		if (selected.Contains(weight.boneIndex3)) sum += weight.weight3;
		return sum;
	}

	private static float TotalWeight(BoneWeight weight) =>
		weight.weight0 + weight.weight1 + weight.weight2 + weight.weight3;

	private static VertexData Lerp(VertexData a, VertexData b, float t)
	{
		return new VertexData
		{
			Position = Vector3.Lerp(a.Position, b.Position, t),
			Normal = Vector3.Lerp(a.Normal, b.Normal, t).normalized,
			Tangent = Vector4.Lerp(a.Tangent, b.Tangent, t),
			Uv0 = Vector2.Lerp(a.Uv0, b.Uv0, t),
			Uv1 = Vector2.Lerp(a.Uv1, b.Uv1, t),
			Uv2 = Vector2.Lerp(a.Uv2, b.Uv2, t),
			Uv3 = Vector2.Lerp(a.Uv3, b.Uv3, t),
			Color = Color.Lerp(a.Color, b.Color, t),
			Weight = LerpWeights(a.Weight, b.Weight, t)
		};
	}

	private static BoneWeight LerpWeights(BoneWeight a, BoneWeight b, float t)
	{
		Dictionary<int, float> weights = new Dictionary<int, float>();
		AddWeight(weights, a.boneIndex0, a.weight0 * (1f - t));
		AddWeight(weights, a.boneIndex1, a.weight1 * (1f - t));
		AddWeight(weights, a.boneIndex2, a.weight2 * (1f - t));
		AddWeight(weights, a.boneIndex3, a.weight3 * (1f - t));
		AddWeight(weights, b.boneIndex0, b.weight0 * t);
		AddWeight(weights, b.boneIndex1, b.weight1 * t);
		AddWeight(weights, b.boneIndex2, b.weight2 * t);
		AddWeight(weights, b.boneIndex3, b.weight3 * t);
		return CreateWeight(weights);
	}

	private static BoneWeight AverageWeights(IEnumerable<BoneWeight> source)
	{
		Dictionary<int, float> weights = new Dictionary<int, float>();
		int count = 0;
		foreach (BoneWeight weight in source)
		{
			count++;
			AddWeight(weights, weight.boneIndex0, weight.weight0);
			AddWeight(weights, weight.boneIndex1, weight.weight1);
			AddWeight(weights, weight.boneIndex2, weight.weight2);
			AddWeight(weights, weight.boneIndex3, weight.weight3);
		}
		if (count > 1)
		{
			int[] keys = weights.Keys.ToArray();
			foreach (int key in keys) weights[key] /= count;
		}
		return CreateWeight(weights);
	}

	private static void AddWeight(Dictionary<int, float> weights, int bone, float value)
	{
		if (value <= 0f) return;
		weights[bone] = weights.TryGetValue(bone, out float existing) ? existing + value : value;
	}

	private static BoneWeight CreateWeight(Dictionary<int, float> source)
	{
		KeyValuePair<int, float>[] ordered = source.OrderByDescending(item => item.Value).Take(4).ToArray();
		float total = ordered.Sum(item => item.Value);
		if (ordered.Length == 0 || total <= 0.000001f) return default;
		BoneWeight result = new BoneWeight();
		if (ordered.Length > 0) { result.boneIndex0 = ordered[0].Key; result.weight0 = ordered[0].Value / total; }
		if (ordered.Length > 1) { result.boneIndex1 = ordered[1].Key; result.weight1 = ordered[1].Value / total; }
		if (ordered.Length > 2) { result.boneIndex2 = ordered[2].Key; result.weight2 = ordered[2].Value / total; }
		if (ordered.Length > 3) { result.boneIndex3 = ordered[3].Key; result.weight3 = ordered[3].Value / total; }
		return result;
	}

	private static MappedBlendShapeFrame[] MapBlendShapes(SourceData source, List<VertexData> vertices)
	{
		if (source.BlendShapes == null || source.BlendShapes.Length == 0)
			return Array.Empty<MappedBlendShapeFrame>();
		MappedBlendShapeFrame[] mapped = new MappedBlendShapeFrame[source.BlendShapes.Length];
		int frameIndex = 0;
		foreach (BlendShapeFrame frame in source.BlendShapes)
		{
			Vector3[] deltaVertices = new Vector3[vertices.Count];
			Vector3[] deltaNormals = new Vector3[vertices.Count];
			Vector3[] deltaTangents = new Vector3[vertices.Count];
			for (int index = 0; index < vertices.Count; index++)
			{
				VertexData vertex = vertices[index];
				deltaVertices[index] = Vector3.Lerp(frame.DeltaVertices[vertex.SourceA], frame.DeltaVertices[vertex.SourceB], vertex.SourceT);
				deltaNormals[index] = Vector3.Lerp(frame.DeltaNormals[vertex.SourceA], frame.DeltaNormals[vertex.SourceB], vertex.SourceT);
				deltaTangents[index] = Vector3.Lerp(frame.DeltaTangents[vertex.SourceA], frame.DeltaTangents[vertex.SourceB], vertex.SourceT);
			}
			mapped[frameIndex++] = new MappedBlendShapeFrame
			{
				Name = frame.Name,
				Weight = frame.Weight,
				DeltaVertices = deltaVertices,
				DeltaNormals = deltaNormals,
				DeltaTangents = deltaTangents
			};
		}
		return mapped;
	}

	private static void RemoveAdjacentDuplicates(List<int> polygon)
	{
		for (int index = polygon.Count - 1; index > 0; index--)
			if (polygon[index] == polygon[index - 1]) polygon.RemoveAt(index);
		if (polygon.Count > 1 && polygon[0] == polygon[polygon.Count - 1]) polygon.RemoveAt(polygon.Count - 1);
	}

	private static void AddUnique(List<int> values, int value)
	{
		if (!values.Contains(value)) values.Add(value);
	}

	private static float AverageScale(Vector3 scale)
	{
		return Mathf.Max(0.0001f, (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f);
	}

	private static bool IsFinite(Vector3 value)
	{
		return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) &&
			!float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
	}

	private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
	private static double ElapsedMilliseconds(long started) =>
		(Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

	// Pure regression hooks. Classification accepts bind-space positions only;
	// a corpse pose is deliberately absent from the contract.
	internal static int[] ClassifyBindSpaceForTests(float[] bindPositions, float[] posedPositions, int[] triangleIndices,
		float[] limbWeights, float[] seedWeights, float[] capsulePoints, float capsuleRadius,
		float[] cutCenter, float[] cutAxis, float halfBand)
	{
		// Deliberately validate but never consume posed positions. The regression supplies
		// unfolded and torso-crossing poses to protect the production bind-space contract.
		if (posedPositions == null || posedPositions.Length != bindPositions.Length)
			throw new ArgumentException("posed test vertices must match the bind-space vertex count", nameof(posedPositions));
		Vector3[] positions = ToVectors(bindPositions);
		List<SourceTriangle> triangles = new List<SourceTriangle>();
		for (int index = 0; index + 2 < triangleIndices.Length; index += 3)
			triangles.Add(new SourceTriangle(triangleIndices[index], triangleIndices[index + 1], triangleIndices[index + 2], 0, triangles.Count));
		List<Segment3> capsule = new List<Segment3>();
		Vector3[] points = ToVectors(capsulePoints);
		for (int index = 0; index + 1 < points.Length; index++) capsule.Add(new Segment3(points[index], points[index + 1]));
		Classification classification = ClassifyTriangles(positions, triangles, limbWeights, seedWeights, capsule,
			capsuleRadius, ToVector(cutCenter), ToVector(cutAxis).normalized, halfBand,
			BuildTriangleAdjacency(triangles, positions, BoundaryWeldToleranceWorld));
		return classification.Selected.OrderBy(index => index).ToArray();
	}

	internal static int[] ClassifyDebugCountsForTests(float[] bindPositions, int[] triangleIndices,
		float[] limbWeights, float[] seedWeights, float[] capsulePoints, float capsuleRadius,
		float[] cutCenter, float[] cutAxis, float halfBand)
	{
		Vector3[] positions = ToVectors(bindPositions);
		List<SourceTriangle> triangles = new List<SourceTriangle>();
		for (int index = 0; index + 2 < triangleIndices.Length; index += 3)
			triangles.Add(new SourceTriangle(triangleIndices[index], triangleIndices[index + 1], triangleIndices[index + 2], 0, triangles.Count));
		Vector3[] points = ToVectors(capsulePoints);
		List<Segment3> capsule = new List<Segment3>();
		for (int index = 0; index + 1 < points.Length; index++) capsule.Add(new Segment3(points[index], points[index + 1]));
		Classification classification = ClassifyTriangles(positions, triangles, limbWeights, seedWeights, capsule,
			capsuleRadius, ToVector(cutCenter), ToVector(cutAxis).normalized, halfBand,
			BuildTriangleAdjacency(triangles, positions, BoundaryWeldToleranceWorld));
		int candidates = classification.Candidate.Count(value => value);
		int strong = classification.Selected.Count(index => classification.SeedWeight[index] >= StrongSeedWeight ||
			classification.LimbWeight[index] >= StrongLimbWeight);
		return new[] { candidates, strong, classification.Selected.Count - strong };
	}

	internal static int[][] OrderClosedLoopsForTests(int[] flatSegments, float[] flatPositions,
		float[] expectedNormal, float maximumOpenClosure)
	{
		List<BoundaryEdge> segments = new List<BoundaryEdge>();
		for (int index = 0; index + 1 < flatSegments.Length; index += 2)
			segments.Add(new BoundaryEdge(flatSegments[index], flatSegments[index + 1]));
		List<VertexData> vertices = ToVectors(flatPositions).Select(position => new VertexData { Position = position }).ToList();
		return TryOrderClosedLoops(segments, vertices, ToVector(expectedNormal).normalized,
			BoundaryWeldToleranceWorld, maximumOpenClosure,
			out List<List<int>> loops, out _) ? loops.Select(loop => loop.ToArray()).ToArray() : null;
	}

	internal static float[] CopyExactOuterLoopForTests(float[] flatPositions)
	{
		Vector3[] copy = CopyExactOuterLoop(ToVectors(flatPositions));
		float[] result = new float[copy.Length * 3];
		for (int index = 0; index < copy.Length; index++)
		{
			result[index * 3] = copy[index].x;
			result[index * 3 + 1] = copy[index].y;
			result[index * 3 + 2] = copy[index].z;
		}
		return result;
	}

	internal static float[] DisturbOuterRimForTests(float[] flatPositions, float[] planeNormal,
		bool isLeg, uint seed, bool gibSide)
	{
		Vector3[] positions = ToVectors(flatPositions);
		ProceduralLimbWoundGeometry.BoundaryVertex[] boundary = positions.Select(position =>
			new ProceduralLimbWoundGeometry.BoundaryVertex(position, ToVector(planeNormal),
				new BoneWeight { boneIndex0 = 0, weight0 = 1f })).ToArray();
		ProceduralLimbWoundGeometry.BoundaryVertex[] disturbed = ProceduralLimbWoundGeometry.DisturbBoundary(
			boundary, ToVector(planeNormal), 1f, isLeg, seed, gibSide);
		float[] result = new float[disturbed.Length * 3];
		for (int index = 0; index < disturbed.Length; index++)
		{
			result[index * 3] = disturbed[index].Position.x;
			result[index * 3 + 1] = disturbed[index].Position.y;
			result[index * 3 + 2] = disturbed[index].Position.z;
		}
		return result;
	}

	private static Vector3[] ToVectors(float[] flat)
	{
		Vector3[] result = new Vector3[flat.Length / 3];
		for (int index = 0; index < result.Length; index++)
			result[index] = new Vector3(flat[index * 3], flat[index * 3 + 1], flat[index * 3 + 2]);
		return result;
	}

	private static Vector3 ToVector(float[] value) => new Vector3(value[0], value[1], value[2]);
}
