using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs
{

/// <summary>
/// Generates only the tissue which closes and adorns an already validated cut
/// boundary. The source cutter owns clipping and topology; this class never
/// moves the exact seam and never contributes geometry to gib colliders.
/// </summary>
internal static partial class ProceduralLimbWoundGeometry
{
	internal const float MissingBandWorld = 0.10f;
	internal const float MaximumAdornmentReachWorld = 0.042f;
	internal const float MinimumArmRimDisturbanceWorld = 0.008f;
	internal const float MaximumArmRimDisturbanceWorld = 0.018f;
	internal const float MinimumLegRimDisturbanceWorld = 0.010f;
	internal const float MaximumLegRimDisturbanceWorld = 0.024f;
	private const float RimDisturbanceRadiusScale = 0.25f;
	private const int RimCurveControlCount = 7;
	private const float ApronDepthWorld = 0.0025f;
	private const float ApronScale = 1.035f;
	private const int TubeSides = 7;

	internal enum Feature : byte
	{
		Apron,
		ExactSeam,
		Mound,
		Bone,
		Flap,
		Cord,
		ClothLip,
		LimbTube
	}

	internal readonly struct BoundaryVertex
	{
		internal readonly Vector3 Position;
		internal readonly Vector3 Normal;
		internal readonly BoneWeight Weight;

		internal BoundaryVertex(Vector3 position, Vector3 normal, BoneWeight weight)
		{
			Position = position;
			Normal = normal;
			Weight = weight;
		}
	}

	internal readonly struct GeneratedVertex
	{
		internal readonly Vector3 Position;
		internal readonly Vector3 Normal;
		internal readonly Vector2 Uv;
		internal readonly Color Mask;
		internal readonly BoneWeight Weight;
		internal readonly int BoundarySource;
		internal readonly Feature Feature;

		internal GeneratedVertex(Vector3 position, Vector3 normal, Vector2 uv, Color mask,
			BoneWeight weight, int boundarySource, Feature feature)
		{
			Position = position;
			Normal = normal;
			Uv = uv;
			Mask = mask;
			Weight = weight;
			BoundarySource = boundarySource;
			Feature = feature;
		}
	}

	internal readonly struct DecalSample
	{
		internal readonly Vector3 Position;
		internal readonly Vector3 Normal;
		internal readonly BoneWeight Weight;
		internal readonly int BoundaryA;
		internal readonly int BoundaryB;
		internal readonly float BoundaryT;
		internal readonly int MeshVertexA;
		internal readonly int MeshVertexB;
		internal readonly int MeshVertexC;
		internal readonly Vector3 MeshBarycentric;

		internal DecalSample(Vector3 position, Vector3 normal, BoneWeight weight,
			int boundaryA, int boundaryB, float boundaryT)
			: this(position, normal, weight, boundaryA, boundaryB, boundaryT,
				-1, -1, -1, new Vector3(1f - boundaryT, boundaryT, 0f))
		{
		}

		private DecalSample(Vector3 position, Vector3 normal, BoneWeight weight,
			int boundaryA, int boundaryB, float boundaryT, int meshVertexA, int meshVertexB,
			int meshVertexC, Vector3 meshBarycentric)
		{
			Position = position;
			Normal = normal;
			Weight = weight;
			BoundaryA = boundaryA;
			BoundaryB = boundaryB;
			BoundaryT = boundaryT;
			MeshVertexA = meshVertexA;
			MeshVertexB = meshVertexB;
			MeshVertexC = meshVertexC;
			MeshBarycentric = meshBarycentric;
		}

		internal DecalSample OnRetainedTriangle(Vector3 position, Vector3 normal, BoneWeight weight,
			int a, int b, int c, Vector3 barycentric) =>
			new DecalSample(position, normal, weight, BoundaryA, BoundaryB, BoundaryT,
				a, b, c, barycentric);
	}

	internal readonly struct EmissionAnchor
	{
		internal readonly Vector3 Position;
		internal readonly Vector3 Normal;
		internal readonly BoneWeight Weight;

		internal EmissionAnchor(Vector3 position, Vector3 normal, BoneWeight weight)
		{
			Position = position;
			Normal = normal;
			Weight = weight;
		}
	}

	internal sealed class Request
	{
		internal BoundaryVertex[] Boundary;
		internal Vector3 FacingNormal;
		internal Vector3 StableCutAxis;
		internal float WorldScale;
		internal bool IsLeg;
		internal LimbSeverLevel Level;
		internal uint Seed;
		internal bool GibSide;
		internal bool AddAnatomicalDetails;
		internal bool OmitApron;
		internal Vector3? AnatomicalCenter;
		internal LimbClothingProfiles.Context Clothing;
	}

	internal sealed class Surface
	{
		internal Surface Copy() => (Surface)MemberwiseClone();
		internal GeneratedVertex[] Vertices;
		internal int[] Triangles;
		internal DecalSample[] DecalSamples;
		internal int[] ExactSeamVertexIndices;
		internal Vector3[] MoundDebugPoints;
		internal Vector3[] BoneRootDebugPoints;
		internal Vector2[] BoneRadiiWorld;
		internal EmissionAnchor StreamAnchor;
		internal int FleshVertexCount;
		internal int BoneCount;
		internal int BoneVertexCount;
		internal int FlapCount;
		internal int CordCount;
		internal float MeanPlateauProtrusionWorld;
		internal float MaximumProtrusionWorld;
		internal float MinimumBoneFractureSpanWorld;
		internal float MinimumAdornmentEmbedWorld;
		internal float MinimumBoneEmbedWorld;
		internal float MinimumBoneExposedReachWorld;
		internal float MaximumBoneRadiusWorld;
		internal bool DecorationsClosed;
		internal BoundaryVertex[] ReplacementBoundary;
		internal FoldDiagnostic Fold;
		internal LimbClothingProfiles.Core ClothCore;
	}

	private readonly struct BoneBuildMetrics
	{
		internal readonly float FractureSpanWorld;
		internal readonly float EmbeddedDepthWorld;
		internal readonly float ExposedReachWorld;
		internal readonly float MaximumRadiusWorld;
		internal readonly Vector2 RadiiWorld;

		internal BoneBuildMetrics(float fractureSpanWorld, float embeddedDepthWorld,
			float exposedReachWorld, Vector2 radiiWorld)
		{
			FractureSpanWorld = fractureSpanWorld;
			EmbeddedDepthWorld = embeddedDepthWorld;
			ExposedReachWorld = exposedReachWorld;
			MaximumRadiusWorld = Mathf.Max(radiiWorld.x, radiiWorld.y);
			RadiiWorld = radiiWorld;
		}
	}

	/// <summary>
	/// Applies the visual tear only after clipping and loop validation. The
	/// cross-section and topology stay unchanged; every supplied rim vertex moves
	/// solely along the cut-plane normal by a smooth deterministic closed curve.
	/// </summary>
	internal static BoundaryVertex[] DisturbBoundary(BoundaryVertex[] source, Vector3 planeNormal,
		float worldScale, bool isLeg, uint seed, bool gibSide)
	{
		if (source == null || source.Length < 3 || worldScale <= 0f ||
			!IsFinite(planeNormal) || planeNormal.sqrMagnitude < 0.5f)
			return source;
		Vector3 normal = planeNormal.normalized;
		Vector3 center = source.Aggregate(Vector3.zero, (sum, item) => sum + item.Position) / source.Length;
		float radius = source.Average(item => Vector3.ProjectOnPlane(item.Position - center, normal).magnitude);
		float minimum = isLeg ? MinimumLegRimDisturbanceWorld : MinimumArmRimDisturbanceWorld;
		float maximum = isLeg ? MaximumLegRimDisturbanceWorld : MaximumArmRimDisturbanceWorld;
		uint branch = seed ^ (gibSide ? 0xD3A2646Cu : 0x7B19E5A1u);
		float amplitudeWorld = Mathf.Clamp(radius * worldScale * RimDisturbanceRadiusScale, minimum, maximum) *
			Mathf.Lerp(0.76f, 1f, Noise01(branch, 0, 113));
		float amplitude = amplitudeWorld / worldScale;

		float[] controls = new float[RimCurveControlCount];
		float controlMean = 0f;
		for (int index = 0; index < controls.Length; index++)
		{
			controls[index] = Noise01(branch, index, 127);
			controlMean += controls[index];
		}
		controlMean /= controls.Length;
		float negative = 0f;
		float positive = 0f;
		for (int index = 0; index < controls.Length; index++)
		{
			controls[index] -= controlMean;
			negative = Mathf.Min(negative, controls[index]);
			positive = Mathf.Max(positive, controls[index]);
		}
		for (int index = 0; index < controls.Length; index++)
			controls[index] = controls[index] < 0f
				? controls[index] / Mathf.Max(0.0001f, -negative)
				: controls[index] / Mathf.Max(0.0001f, positive);

		Vector3 reference = Mathf.Abs(normal.y) < 0.82f ? Vector3.up : Vector3.right;
		Vector3 axisU = Vector3.Cross(normal, reference).normalized;
		Vector3 axisV = Vector3.Cross(normal, axisU).normalized;
		float[] offsets = new float[source.Length];
		float offsetMean = 0f;
		for (int index = 0; index < source.Length; index++)
		{
			Vector3 radial = Vector3.ProjectOnPlane(source[index].Position - center, normal);
			float angle = Mathf.Atan2(Vector3.Dot(radial, axisV), Vector3.Dot(radial, axisU));
			if (angle < 0f) angle += Mathf.PI * 2f;
			float phase = angle / (Mathf.PI * 2f) * controls.Length;
			int lower = Mathf.FloorToInt(phase) % controls.Length;
			int upper = (lower + 1) % controls.Length;
			float t = phase - Mathf.Floor(phase);
			float smooth = 0.5f - Mathf.Cos(t * Mathf.PI) * 0.5f;
			offsets[index] = Mathf.Lerp(controls[lower], controls[upper], smooth);
			offsetMean += offsets[index];
		}
		offsetMean /= offsets.Length;
		negative = 0f;
		positive = 0f;
		for (int index = 0; index < offsets.Length; index++)
		{
			offsets[index] -= offsetMean;
			negative = Mathf.Min(negative, offsets[index]);
			positive = Mathf.Max(positive, offsets[index]);
		}

		BoundaryVertex[] disturbed = new BoundaryVertex[source.Length];
		for (int index = 0; index < source.Length; index++)
		{
			float normalized = offsets[index] < 0f
				? offsets[index] / Mathf.Max(0.0001f, -negative)
				: offsets[index] / Mathf.Max(0.0001f, positive);
			disturbed[index] = new BoundaryVertex(source[index].Position + normal * (normalized * amplitude),
				source[index].Normal, source[index].Weight);
		}
		return disturbed;
	}

	private sealed class Builder
	{
		internal readonly List<GeneratedVertex> Vertices = new List<GeneratedVertex>();
		internal readonly List<int> Triangles = new List<int>();
		internal readonly List<(int StartTriangle, int EndTriangle)> ClosedRanges = new List<(int, int)>();

		internal int Add(GeneratedVertex vertex)
		{
			int index = Vertices.Count;
			Vertices.Add(vertex);
			return index;
		}

		internal void Triangle(int a, int b, int c, Vector3 facing)
		{
			Vector3 pa = Vertices[a].Position;
			Vector3 pb = Vertices[b].Position;
			Vector3 pc = Vertices[c].Position;
			if (Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), facing) >= 0f)
			{
				Triangles.Add(a); Triangles.Add(b); Triangles.Add(c);
			}
			else
			{
				Triangles.Add(a); Triangles.Add(c); Triangles.Add(b);
			}
		}

		internal void TriangleOrdered(int a, int b, int c)
		{
			Triangles.Add(a); Triangles.Add(b); Triangles.Add(c);
		}

		internal void Quad(int a, int b, int c, int d, Vector3 facing)
		{
			Triangle(a, b, c, facing);
			Triangle(a, c, d, facing);
		}
	}

	private struct DeterministicRandom
	{
		private uint _state;
		internal DeterministicRandom(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;
		private uint Next()
		{
			uint value = _state;
			value ^= value << 13;
			value ^= value >> 17;
			value ^= value << 5;
			_state = value;
			return value;
		}
		internal float Unit() => (Next() & 0x00FFFFFFu) / 16777215f;
		internal float Signed() => Unit() * 2f - 1f;
		internal int Range(int minimumInclusive, int maximumExclusive) =>
			minimumInclusive + (int)(Unit() * Mathf.Max(1, maximumExclusive - minimumInclusive)) % Mathf.Max(1, maximumExclusive - minimumInclusive);
	}

	private static bool TryGenerateTissue(Request request, out Surface surface, out string failure)
	{
		surface = null;
		failure = null;
		if (request?.Boundary == null || request.Boundary.Length < 3 || request.WorldScale <= 0f ||
			!IsFinite(request.FacingNormal) || request.FacingNormal.sqrMagnitude < 0.5f ||
			!IsFinite(request.StableCutAxis) || request.StableCutAxis.sqrMagnitude < 0.5f)
		{
			failure = "the wound request does not contain a valid closed boundary and cut frame";
			return false;
		}
		for (int index = 0; index < request.Boundary.Length; index++)
			if (!IsFinite(request.Boundary[index].Position))
			{
				failure = $"boundary vertex {index} is not finite";
				return false;
			}

		Vector3 normal = request.FacingNormal.normalized;
		Vector3 center = request.AnatomicalCenter ?? (request.Boundary.Aggregate(Vector3.zero, (sum, item) => sum + item.Position) /
			request.Boundary.Length);
		BuildFrame(request.Boundary, center, request.StableCutAxis.normalized,
			out Vector3 axisU, out Vector3 axisV, out float radius);
		if (!IsFinite(radius) || radius <= 0.0001f)
		{
			failure = "the exact cut boundary has no usable cross-sectional radius";
			return false;
		}

		uint shapeBranch = request.Seed ^ (request.GibSide ? 0xA511E9B3u : 0x63D83595u);
		DeterministicRandom random = new DeterministicRandom(shapeBranch);
		Builder builder = new Builder();
		BoneWeight averageWeight = AverageWeights(request.Boundary.Select(item => item.Weight));
		float scale = request.WorldScale;
		float maximumReach = MaximumAdornmentReachWorld / scale;
		float minimumHeight = (request.IsLeg ? 0.010f : 0.006f) / scale;
		float maximumHeight = (request.IsLeg ? 0.024f : 0.018f) / scale;
		float moundHeight = Mathf.Clamp(Mathf.Lerp(minimumHeight, maximumHeight, 0.20f + random.Unit() * 0.80f),
			minimumHeight, Mathf.Min(maximumHeight, maximumReach * 0.62f));

		int count = request.Boundary.Length;
		int[] apron = new int[count];
		int[] exact = new int[count];
		int[] shoulder = new int[count];
		int[] crown = new int[count];
		Vector2 shoulderOffset = new Vector2(random.Signed(), random.Signed()).normalized * radius * 0.055f;
		Vector2 crownOffset = new Vector2(random.Signed(), random.Signed()).normalized * radius * 0.105f;
		List<Vector3> moundDebug = new List<Vector3>();
		for (int index = 0; index < count; index++)
		{
			BoundaryVertex source = request.Boundary[index];
			Vector3 radial = Vector3.ProjectOnPlane(source.Position - center, normal);
			float irregular = 1f + 0.035f * Noise01(request.Seed, index, request.GibSide ? 19 : 7);
			// Selection interfaces can contain an axial edge. Flattening both ends
			// into a plane collapses the apron into a zero-area triangle. Follow the
			// actual seam in depth and taper that depth through the inner rings.
			float seamDepth = Vector3.Dot(source.Position - center, normal);
			Vector3 apronPosition = source.Position + radial * (ApronScale - 1f) - normal * (ApronDepthWorld / scale);
			Vector3 shoulderPosition = center + radial * (0.77f * irregular) +
				axisU * shoulderOffset.x + axisV * shoulderOffset.y + normal * (seamDepth * 0.77f + moundHeight * (0.34f + 0.13f * Noise01(request.Seed, index, 31)));
			Vector3 crownPosition = center + radial * (0.45f * irregular) +
				axisU * crownOffset.x + axisV * crownOffset.y + normal * (seamDepth * 0.45f + moundHeight * (0.72f + 0.17f * Noise01(request.Seed, index, 47)));
			Vector3 radialNormal = radial.sqrMagnitude > 0.000001f ? radial.normalized : normal;
			Vector3 shoulderNormal = (normal * 0.76f + radialNormal * 0.52f).normalized;
			Vector3 crownNormal = (normal * 0.88f + radialNormal * 0.31f).normalized;
			apron[index] = AddSurfaceVertex(builder, apronPosition, normal, center, axisU, axisV, radius,
				FleshMask(0.78f, 0.30f, 0.66f, request.Seed, index), source.Weight, index, Feature.Apron, center, maximumReach);
			exact[index] = AddSurfaceVertex(builder, source.Position, normal, center, axisU, axisV, radius,
				FleshMask(0.92f, 0.22f, 0.88f, request.Seed, index + 1000), source.Weight, index, Feature.ExactSeam, center, maximumReach);
			shoulder[index] = AddSurfaceVertex(builder, shoulderPosition, shoulderNormal, center, axisU, axisV, radius,
				FleshMask(0.76f, 0.44f, 0.72f, request.Seed, index + 2000), source.Weight, index, Feature.Mound, center, maximumReach);
			crown[index] = AddSurfaceVertex(builder, crownPosition, crownNormal, center, axisU, axisV, radius,
				FleshMask(0.62f, 0.68f, 0.58f, request.Seed, index + 3000), source.Weight, index, Feature.Mound, center, maximumReach);
			if (index % Mathf.Max(1, count / 12) == 0)
			{
				moundDebug.Add(shoulderPosition);
				moundDebug.Add(crownPosition);
			}
		}
		if (!request.OmitApron) ConnectEqualRings(builder, apron, exact, normal);
		ConnectEqualRings(builder, exact, shoulder, normal);
		ConnectEqualRings(builder, shoulder, crown, normal);

		int plateauCount = Mathf.Clamp(count / 3, 5, 8);
		int[] plateau = new int[plateauCount];
		for (int index = 0; index < plateauCount; index++)
		{
			float angle = Mathf.PI * 2f * index / plateauCount;
			float plateauRadius = radius * (0.16f + random.Unit() * 0.055f);
			float height = moundHeight * (0.91f + random.Signed() * 0.09f);
			Vector3 position = center + axisU * (crownOffset.x + Mathf.Cos(angle) * plateauRadius) +
				axisV * (crownOffset.y + Mathf.Sin(angle) * plateauRadius) + normal * height;
			int boundarySource = Mathf.FloorToInt((float)index * count / plateauCount) % count;
			Vector3 plateauNormal = (normal + axisU * random.Signed() * 0.08f + axisV * random.Signed() * 0.08f).normalized;
			plateau[index] = AddSurfaceVertex(builder, position, plateauNormal, center, axisU, axisV, radius,
				FleshMask(0.55f, 0.77f, 0.50f, request.Seed, index + 4000), averageWeight,
				boundarySource, Feature.Mound, center, maximumReach);
			moundDebug.Add(position);
		}
		ConnectDifferentRings(builder, crown, plateau, normal);
		for (int index = 1; index + 1 < plateau.Length; index++)
			builder.Triangle(plateau[0], plateau[index], plateau[index + 1], normal);

		int fleshVertices = builder.Vertices.Count;
		List<Vector3> boneRoots = new List<Vector3>();
		List<Vector2> boneRadii = new List<Vector2>();
		int boneCount = 0;
		int flapCount = 0;
		int cordCount = 0;
		float minimumBoneFractureSpan = float.PositiveInfinity;
		float minimumAdornmentEmbed = float.PositiveInfinity;
		float minimumBoneEmbed = float.PositiveInfinity;
		float minimumBoneExposedReach = float.PositiveInfinity;
		float maximumBoneRadius = 0f;
		if (request.AddAnatomicalDetails)
		{
			boneCount = AnatomicalBoneCount(request.Level);
			Vector2[] normalizedBoneRoots = BuildSharedBoneRoots(request.Seed, boneCount);
			for (int index = 0; index < boneCount; index++)
			{
				Vector2 root2 = normalizedBoneRoots[index] * radius;
				Vector3 root = center + axisU * root2.x + axisV * root2.y;
				boneRoots.Add(root);
				BoneBuildMetrics bone = AddBone(builder, request, ref random, root, normal, axisU, axisV, radius,
					moundHeight, averageWeight, index, center, maximumReach);
				minimumBoneFractureSpan = Mathf.Min(minimumBoneFractureSpan, bone.FractureSpanWorld);
				minimumBoneEmbed = Mathf.Min(minimumBoneEmbed, bone.EmbeddedDepthWorld);
				minimumBoneExposedReach = Mathf.Min(minimumBoneExposedReach, bone.ExposedReachWorld);
				maximumBoneRadius = Mathf.Max(maximumBoneRadius, bone.MaximumRadiusWorld);
				minimumAdornmentEmbed = Mathf.Min(minimumAdornmentEmbed, bone.EmbeddedDepthWorld);
				boneRadii.Add(bone.RadiiWorld);
			}
			flapCount = random.Range(3, 8);
			for (int index = 0; index < flapCount; index++)
				minimumAdornmentEmbed = Mathf.Min(minimumAdornmentEmbed,
					AddFlap(builder, request, ref random, center, normal, axisU, axisV, radius, moundHeight,
						averageWeight, index, flapCount, maximumReach));

			cordCount = random.Range(2, 5);
			for (int index = 0; index < cordCount; index++)
				minimumAdornmentEmbed = Mathf.Min(minimumAdornmentEmbed,
					AddCord(builder, request, ref random, center, normal, axisU, axisV, radius, moundHeight,
						averageWeight, index, cordCount, maximumReach));
		}

		if (!ValidateFiniteAndNondegenerate(builder, out failure)) return false;
		bool decorationsClosed = ValidateClosedRanges(builder, out string topologyFailure);
		if (!decorationsClosed)
		{
			failure = "a bone, fleshy lobe, or tissue cord is not closed exactly once at every edge: " + topologyFailure;
			return false;
		}

		float plateauMean = 0f;
		Vector3 plateauCenter = Vector3.zero;
		for (int index = 0; index < plateau.Length; index++)
		{
			plateauMean += Vector3.Dot(builder.Vertices[plateau[index]].Position - center, normal);
			plateauCenter += builder.Vertices[plateau[index]].Position;
		}
		plateauMean = plateauMean / plateau.Length * scale;
		plateauCenter /= plateau.Length;
		// Keep the launch point within the closed mound so blood emerges through
		// the actual generated cap instead of beginning on or outside its surface.
		Vector3 streamAnchor = Vector3.Lerp(center, plateauCenter, 0.58f);
		float maxProtrusion = builder.Vertices.Max(item => Vector3.Dot(item.Position - center, normal)) * scale;
		surface = new Surface
		{
			Vertices = builder.Vertices.ToArray(),
			Triangles = builder.Triangles.ToArray(),
			DecalSamples = request.AddAnatomicalDetails ? BuildDecalSamples(request.Boundary, request.IsLeg ? 8 : 6) : Array.Empty<DecalSample>(),
			ExactSeamVertexIndices = exact,
			MoundDebugPoints = moundDebug.ToArray(),
			BoneRootDebugPoints = boneRoots.ToArray(),
			BoneRadiiWorld = boneRadii.ToArray(),
			StreamAnchor = new EmissionAnchor(streamAnchor, normal, averageWeight),
			FleshVertexCount = fleshVertices,
			BoneCount = boneCount,
			BoneVertexCount = builder.Vertices.Count(vertex => vertex.Feature == Feature.Bone),
			FlapCount = flapCount,
			CordCount = cordCount,
			MeanPlateauProtrusionWorld = plateauMean,
			MaximumProtrusionWorld = maxProtrusion,
			MinimumBoneFractureSpanWorld = IsFinite(minimumBoneFractureSpan) ? minimumBoneFractureSpan : 0f,
			MinimumAdornmentEmbedWorld = IsFinite(minimumAdornmentEmbed) ? minimumAdornmentEmbed : 0f,
			MinimumBoneEmbedWorld = IsFinite(minimumBoneEmbed) ? minimumBoneEmbed : 0f,
			MinimumBoneExposedReachWorld = IsFinite(minimumBoneExposedReach) ? minimumBoneExposedReach : 0f,
			MaximumBoneRadiusWorld = maximumBoneRadius,
			DecorationsClosed = decorationsClosed
		};
		return true;
	}

	private static int AddSurfaceVertex(Builder builder, Vector3 position, Vector3 normal, Vector3 center,
		Vector3 axisU, Vector3 axisV, float radius, Color mask, BoneWeight weight, int boundarySource,
		Feature feature, Vector3 reachCenter, float maximumReach)
	{
		Vector3 facing = normal.normalized;
		float reach = Vector3.Dot(position - reachCenter, facing);
		if (feature != Feature.ExactSeam && reach > maximumReach) position -= facing * (reach - maximumReach);
		Vector3 offset = position - center;
		Vector2 uv = new Vector2(Vector3.Dot(offset, axisU), Vector3.Dot(offset, axisV)) /
			Mathf.Max(0.0001f, radius * 2f) + new Vector2(0.5f, 0.5f);
		return builder.Add(new GeneratedVertex(position, normal.normalized, uv, mask, weight,
			boundarySource, feature));
	}

	private static void ConnectEqualRings(Builder builder, int[] inner, int[] outer, Vector3 normal)
	{
		for (int index = 0; index < inner.Length; index++)
		{
			int next = (index + 1) % inner.Length;
			builder.Quad(inner[index], inner[next], outer[next], outer[index], normal);
		}
	}

	private static bool ConnectTubeRings(Builder builder, int[] start, int[] end,
		Vector3 startCenter, Vector3 endCenter, bool? forcedReverse = null)
	{
		Vector3 firstOutward = ((builder.Vertices[start[0]].Position + builder.Vertices[start[1]].Position +
			builder.Vertices[end[0]].Position + builder.Vertices[end[1]].Position) * 0.25f -
			(startCenter + endCenter) * 0.5f).normalized;
		Vector3 firstNormal = Vector3.Cross(builder.Vertices[start[1]].Position - builder.Vertices[start[0]].Position,
			builder.Vertices[end[1]].Position - builder.Vertices[start[0]].Position);
		bool reverse = forcedReverse ?? Vector3.Dot(firstNormal, firstOutward) < 0f;
		for (int index = 0; index < start.Length; index++)
		{
			int next = (index + 1) % start.Length;
			if (!reverse)
			{
				builder.TriangleOrdered(start[index], start[next], end[next]);
				builder.TriangleOrdered(start[index], end[next], end[index]);
			}
			else
			{
				builder.TriangleOrdered(start[index], end[next], start[next]);
				builder.TriangleOrdered(start[index], end[index], end[next]);
			}
		}
		return reverse;
	}

	private static void ConnectDifferentRings(Builder builder, int[] outer, int[] inner, Vector3 normal)
	{
		int outerIndex = 0;
		int innerIndex = 0;
		while (outerIndex < outer.Length || innerIndex < inner.Length)
		{
			float nextOuter = (float)(outerIndex + 1) / outer.Length;
			float nextInner = (float)(innerIndex + 1) / inner.Length;
			int currentOuter = outer[outerIndex % outer.Length];
			int currentInner = inner[innerIndex % inner.Length];
			if (nextOuter <= nextInner && outerIndex < outer.Length)
			{
				builder.Triangle(currentOuter, outer[(outerIndex + 1) % outer.Length], currentInner, normal);
				outerIndex++;
			}
			else
			{
				builder.Triangle(currentOuter, inner[(innerIndex + 1) % inner.Length], currentInner, normal);
				innerIndex++;
			}
		}
	}

	private static BoneBuildMetrics AddBone(Builder builder, Request request, ref DeterministicRandom random,
		Vector3 root, Vector3 normal, Vector3 axisU, Vector3 axisV, float limbRadius, float moundHeight,
		BoneWeight weight, int boneIndex, Vector3 reachCenter, float maximumReach, float exposedLengthScale = 1f)
	{
		int rangeStart = builder.Triangles.Count;
		float scale = request.WorldScale;
		float anatomicalScale = Mathf.Lerp(0.85f, 1.15f, random.Unit());
		Vector2 radiiWorld = ResolveBoneRadiiWorld(request.IsLeg, request.Level, boneIndex) * anatomicalScale;
		float radiusU = radiiWorld.x / scale;
		float radiusV = radiiWorld.y / scale;
		float normalizedRootRadius = Vector3.ProjectOnPlane(root - reachCenter, normal).magnitude /
			Mathf.Max(0.0001f, limbRadius);
		float surfaceHeight = EstimateMoundSurfaceHeight(moundHeight, normalizedRootRadius);
		float requestedExposedReach = Mathf.Lerp(0.015f, 0.035f, random.Unit()) / scale * exposedLengthScale;
		float availableExposedReach = Mathf.Max(0.006f / scale,
			maximumReach - surfaceHeight - 0.003f / scale);
		float exposedReach = Mathf.Min(requestedExposedReach, availableExposedReach);
		float minimumEmbedWorld = request.IsLeg ? 0.008f : 0.006f;
		float maximumEmbedWorld = request.IsLeg ? 0.021f : 0.015f;
		float embeddedDepth = Mathf.Clamp(Mathf.Max(radiiWorld.x, radiiWorld.y) * 1.15f,
			minimumEmbedWorld, maximumEmbedWorld) / scale;
		Vector3 skewDirection = axisU * random.Signed() + axisV * random.Signed();
		Vector3 skew = (skewDirection.sqrMagnitude > 0.000001f ? skewDirection.normalized : axisU) *
			exposedReach * 0.16f;
		int[][] rings = new int[3][];
		Vector3[] ringCenters = new Vector3[3];
		float fractureMinimum = float.PositiveInfinity;
		float fractureMaximum = float.NegativeInfinity;
		for (int ring = 0; ring < rings.Length; ring++)
		{
			rings[ring] = new int[TubeSides];
			float t = ring / 2f;
			ringCenters[ring] = root + normal * (surfaceHeight - embeddedDepth +
				(embeddedDepth + exposedReach) * t) + skew * t;
			float taper = Mathf.Lerp(1f, 0.82f, t);
			for (int side = 0; side < TubeSides; side++)
			{
				float angle = Mathf.PI * 2f * side / TubeSides;
				float fracture = ring == 2 ? (random.Signed() * 0.0018f + (side % 2 == 0 ? 0.001f : -0.001f)) / scale : 0f;
				if (ring == 2)
				{
					fractureMinimum = Mathf.Min(fractureMinimum, fracture);
					fractureMaximum = Mathf.Max(fractureMaximum, fracture);
				}
				Vector3 radial = axisU * (Mathf.Cos(angle) * radiusU * taper) + axisV * (Mathf.Sin(angle) * radiusV * taper);
				Vector3 position = ringCenters[ring] + normal * fracture + radial;
				Color mask = new Color(ring == 0 ? 0.82f : 0.20f + random.Unit() * 0.20f,
					1f, 0.28f + random.Unit() * 0.45f, 0.32f + random.Unit() * 0.36f);
				rings[ring][side] = AddSurfaceVertex(builder, position, radial.normalized, root, axisU, axisV,
					limbRadius, mask, weight, 0, Feature.Bone, reachCenter, maximumReach);
			}
		}
		bool? tubeReverse = null;
		for (int ring = 0; ring + 1 < rings.Length; ring++)
			tubeReverse = ConnectTubeRings(builder, rings[ring], rings[ring + 1], ringCenters[ring], ringCenters[ring + 1], tubeReverse);
		AddRingCap(builder, rings[0], ringCenters[0] - normal * (0.0008f / scale), -normal, root, axisU, axisV,
			limbRadius, new Color(0.72f, 1f, 0.35f, 0.45f), weight, Feature.Bone, reachCenter, maximumReach,
			reverseRingWinding: !tubeReverse.GetValueOrDefault());
		AddRingCap(builder, rings[2], ringCenters[2], normal, root, axisU, axisV, limbRadius,
			new Color(0.38f, 1f, 0.72f, 0.34f), weight, Feature.Bone, reachCenter, maximumReach,
			reverseRingWinding: tubeReverse.GetValueOrDefault());
		builder.ClosedRanges.Add((rangeStart, builder.Triangles.Count));
		return new BoneBuildMetrics((fractureMaximum - fractureMinimum) * scale, embeddedDepth * scale,
			exposedReach * scale, radiiWorld);
	}

	private static float AddFlap(Builder builder, Request request, ref DeterministicRandom random,
		Vector3 center, Vector3 normal, Vector3 axisU, Vector3 axisV, float radius, float moundHeight,
		BoneWeight weight, int flapIndex, int flapCount, float maximumReach)
	{
		int rangeStart = builder.Triangles.Count;
		float angle = Mathf.PI * 2f * (flapIndex + random.Unit() * 0.65f) / flapCount;
		Vector3 radialDirection = axisU * Mathf.Cos(angle) + axisV * Mathf.Sin(angle);
		Vector3 tangent = Vector3.Cross(normal, radialDirection).normalized;
		float rootRadius = radius * Mathf.Lerp(0.60f, 0.88f, random.Unit());
		float width = Mathf.Clamp(radius * Mathf.Lerp(0.08f, 0.16f, random.Unit()), 0.002f / request.WorldScale, radius * 0.22f);
		float surfaceHeight = EstimateMoundSurfaceHeight(moundHeight, rootRadius / Mathf.Max(0.0001f, radius));
		float embeddedDepth = Mathf.Clamp(width * 1.15f, 0.005f / request.WorldScale, 0.012f / request.WorldScale);
		Vector3 surfaceRoot = center + radialDirection * rootRadius + normal * surfaceHeight;
		Vector3 embeddedRoot = surfaceRoot - normal * embeddedDepth;
		float availableReach = Mathf.Max(0.005f / request.WorldScale,
			maximumReach - surfaceHeight - 0.002f / request.WorldScale);
		float length = Mathf.Min(availableReach,
			Mathf.Min(maximumReach * 0.62f, radius * Mathf.Lerp(0.24f, 0.50f, random.Unit())));
		Vector3 tipCenter = surfaceRoot + normal * length + tangent * random.Signed() * width * 0.45f +
			radialDirection * random.Signed() * width * 0.22f;
		int[] rootRing = new int[6];
		int[] tipRing = new int[6];
		for (int side = 0; side < 6; side++)
		{
			float a = Mathf.PI * 2f * side / 6f;
			Vector3 rootRadial = tangent * (Mathf.Cos(a) * width) + radialDirection * (Mathf.Sin(a) * width * 0.55f);
			float tipIrregularity = 0.82f + random.Unit() * 0.28f;
			Vector3 tipRadial = (tangent * (Mathf.Cos(a) * width * 0.42f) +
				radialDirection * (Mathf.Sin(a) * width * 0.23f)) * tipIrregularity;
			rootRing[side] = AddSurfaceVertex(builder, embeddedRoot + rootRadial,
				rootRadial.normalized, center, axisU, axisV, radius,
				FleshMask(0.86f, 0.34f, 0.82f, request.Seed, 5000 + flapIndex * 10 + side), weight, 0,
				Feature.Flap, center, maximumReach);
			tipRing[side] = AddSurfaceVertex(builder, tipCenter + tipRadial,
				tipRadial.normalized, center, axisU, axisV, radius,
				FleshMask(0.70f, 0.58f, 0.62f, request.Seed, 5100 + flapIndex * 10 + side), weight, 0,
				Feature.Flap, center, maximumReach);
		}
		bool tubeReverse = ConnectTubeRings(builder, rootRing, tipRing, embeddedRoot, tipCenter);
		AddRingCap(builder, rootRing, embeddedRoot - normal * (0.0008f / request.WorldScale), -normal,
			center, axisU, axisV, radius,
			new Color(0.84f, 0f, 0.40f, 0.74f), weight, Feature.Flap, center, maximumReach,
			reverseRingWinding: !tubeReverse);
		AddRingCap(builder, tipRing, tipCenter + normal * width * 0.08f, normal, center, axisU, axisV, radius,
			new Color(0.62f, 0f, 0.72f, 0.58f), weight, Feature.Flap, center, maximumReach,
			reverseRingWinding: tubeReverse);
		builder.ClosedRanges.Add((rangeStart, builder.Triangles.Count));
		return embeddedDepth * request.WorldScale;
	}

	private static float AddCord(Builder builder, Request request, ref DeterministicRandom random,
		Vector3 center, Vector3 normal, Vector3 axisU, Vector3 axisV, float radius, float moundHeight,
		BoneWeight weight, int cordIndex, int cordCount, float maximumReach)
	{
		int rangeStart = builder.Triangles.Count;
		float angle = Mathf.PI * 2f * (cordIndex + random.Unit() * 0.75f) / cordCount;
		Vector3 radial = axisU * Mathf.Cos(angle) + axisV * Mathf.Sin(angle);
		Vector3 tangent = Vector3.Cross(normal, radial).normalized;
		float rootRadius = radius * Mathf.Lerp(0.22f, 0.56f, random.Unit());
		float cordRadius = Mathf.Clamp(radius * Mathf.Lerp(0.025f, 0.055f, random.Unit()),
			0.0012f / request.WorldScale, 0.0045f / request.WorldScale);
		float surfaceHeight = EstimateMoundSurfaceHeight(moundHeight, rootRadius / Mathf.Max(0.0001f, radius));
		float embeddedDepth = Mathf.Clamp(cordRadius * 2f, 0.004f / request.WorldScale, 0.010f / request.WorldScale);
		Vector3 surfaceRoot = center + radial * rootRadius + normal * surfaceHeight;
		Vector3 embeddedRoot = surfaceRoot - normal * embeddedDepth;
		float availableReach = Mathf.Max(0.005f / request.WorldScale,
			maximumReach - surfaceHeight - 0.002f / request.WorldScale);
		float length = Mathf.Min(availableReach,
			Mathf.Min(maximumReach * 0.82f, Mathf.Lerp(0.012f, 0.032f, random.Unit()) / request.WorldScale));
		int[][] rings = new int[4][];
		Vector3[] ringCenters = new Vector3[4];
		for (int ring = 0; ring < rings.Length; ring++)
		{
			float t = ring / 3f;
			Vector3 path = Vector3.Lerp(embeddedRoot, surfaceRoot + normal * length, t) +
				tangent * length * 0.24f * Mathf.Sin(t * Mathf.PI) +
				radial * length * 0.12f * t * random.Signed();
			ringCenters[ring] = path;
			float taper = Mathf.Lerp(1f, 0.34f, t);
			rings[ring] = new int[6];
			for (int side = 0; side < 6; side++)
			{
				float a = Mathf.PI * 2f * side / 6f;
				Vector3 ringRadial = axisU * Mathf.Cos(a) * cordRadius * taper + axisV * Mathf.Sin(a) * cordRadius * taper;
				rings[ring][side] = AddSurfaceVertex(builder, path + ringRadial, ringRadial.normalized,
					center, axisU, axisV, radius,
					FleshMask(0.74f, 0.46f, 0.78f, request.Seed, 6000 + cordIndex * 30 + ring * 6 + side),
					weight, 0, Feature.Cord, center, maximumReach);
			}
		}
		bool? tubeReverse = null;
		for (int ring = 0; ring + 1 < rings.Length; ring++)
			tubeReverse = ConnectTubeRings(builder, rings[ring], rings[ring + 1], ringCenters[ring], ringCenters[ring + 1], tubeReverse);
		AddRingCap(builder, rings[0], ringCenters[0] - normal * (0.0006f / request.WorldScale), -normal,
			center, axisU, axisV, radius,
			new Color(0.88f, 0f, 0.46f, 0.86f), weight, Feature.Cord, center, maximumReach,
			reverseRingWinding: !tubeReverse.GetValueOrDefault());
		AddRingCap(builder, rings[3], ringCenters[3], normal, center, axisU, axisV, radius,
			new Color(0.58f, 0f, 0.76f, 0.63f), weight, Feature.Cord, center, maximumReach,
			reverseRingWinding: tubeReverse.GetValueOrDefault());
		builder.ClosedRanges.Add((rangeStart, builder.Triangles.Count));
		return embeddedDepth * request.WorldScale;
	}

	internal static int AnatomicalBoneCount(LimbSeverLevel level) => level == LimbSeverLevel.Proximal ? 1 : 2;

	internal static Vector2 ResolveBoneRadiiWorld(bool isLeg, LimbSeverLevel level, int boneIndex)
	{
		if (level == LimbSeverLevel.Proximal)
		{
			// Fixed anatomical semi-axes: femur 29 x 26 mm, humerus 20 x 18 mm.
			return isLeg ? new Vector2(0.0145f, 0.0130f) : new Vector2(0.0100f, 0.0090f);
		}
		if (isLeg)
		{
			// Tibia 27 x 20 mm, fibula 14 x 14 mm.
			return boneIndex == 0 ? new Vector2(0.0135f, 0.0100f) : new Vector2(0.0070f, 0.0070f);
		}
		// Ulna 16 x 11 mm, radius 15 x 11 mm.
		return boneIndex == 0 ? new Vector2(0.0080f, 0.0055f) : new Vector2(0.0075f, 0.0055f);
	}

	private static float EstimateMoundSurfaceHeight(float moundHeight, float normalizedRadius)
	{
		float radius = Mathf.Clamp01(normalizedRadius);
		if (radius <= 0.22f) return moundHeight * 0.91f;
		if (radius <= 0.45f) return moundHeight * Mathf.Lerp(0.91f, 0.72f, (radius - 0.22f) / 0.23f);
		if (radius <= 0.77f) return moundHeight * Mathf.Lerp(0.72f, 0.34f, (radius - 0.45f) / 0.32f);
		return moundHeight * Mathf.Lerp(0.34f, 0.04f, (radius - 0.77f) / 0.23f);
	}

	private static void AddRingCap(Builder builder, int[] ring, Vector3 centerPosition, Vector3 facing,
		Vector3 uvCenter, Vector3 axisU, Vector3 axisV, float radius, Color color, BoneWeight weight,
		Feature feature, Vector3 reachCenter, float maximumReach, bool reverseRingWinding)
	{
		int center = AddSurfaceVertex(builder, centerPosition, facing, uvCenter, axisU, axisV, radius,
			color, weight, 0, feature, reachCenter, maximumReach);
		for (int side = 0; side < ring.Length; side++)
		{
			int next = (side + 1) % ring.Length;
			if (!reverseRingWinding) builder.TriangleOrdered(ring[side], ring[next], center);
			else builder.TriangleOrdered(ring[side], center, ring[next]);
		}
	}

	private static DecalSample[] BuildDecalSamples(BoundaryVertex[] boundary, int requestedCount)
	{
		int count = Mathf.Clamp(requestedCount, 6, 10);
		float[] cumulative = new float[boundary.Length + 1];
		for (int index = 0; index < boundary.Length; index++)
			cumulative[index + 1] = cumulative[index] + Vector3.Distance(boundary[index].Position,
				boundary[(index + 1) % boundary.Length].Position);
		if (cumulative[boundary.Length] <= 0.0001f) return Array.Empty<DecalSample>();
		DecalSample[] samples = new DecalSample[count];
		for (int sample = 0; sample < count; sample++)
		{
			float target = cumulative[boundary.Length] * (sample + 0.5f) / count;
			int edge = 0;
			while (edge + 1 < cumulative.Length && cumulative[edge + 1] < target) edge++;
			int next = (edge + 1) % boundary.Length;
			float length = cumulative[edge + 1] - cumulative[edge];
			float t = length > 0.000001f ? (target - cumulative[edge]) / length : 0f;
			samples[sample] = new DecalSample(Vector3.Lerp(boundary[edge].Position, boundary[next].Position, t),
				Vector3.Lerp(boundary[edge].Normal, boundary[next].Normal, t).normalized,
				LerpWeights(boundary[edge].Weight, boundary[next].Weight, t), edge, next, t);
		}
		return samples;
	}

	private static Vector2[] BuildSharedBoneRoots(uint seed, int count)
	{
		DeterministicRandom shared = new DeterministicRandom(seed ^ 0xD1B54A35u);
		if (count == 1) return new[] { new Vector2(shared.Signed() * 0.075f, shared.Signed() * 0.075f) };
		float rotation = shared.Signed() * 0.55f;
		Vector2 direction = new Vector2(Mathf.Cos(rotation), Mathf.Sin(rotation));
		Vector2 perpendicular = new Vector2(-direction.y, direction.x);
		return new[]
		{
			direction * -0.19f + perpendicular * shared.Signed() * 0.035f,
			direction * 0.19f + perpendicular * shared.Signed() * 0.035f
		};
	}

	private static Color FleshMask(float blood, float clot, float wetness, uint seed, int salt)
	{
		float noise = Noise01(seed, salt, 97);
		return new Color(Mathf.Clamp01(blood + noise * 0.12f), 0f,
			Mathf.Clamp01(clot + noise * 0.18f), Mathf.Clamp01(wetness + noise * 0.12f));
	}

	private static float Noise01(uint seed, int index, int branch)
	{
		uint value = seed ^ ((uint)index * 747796405u) ^ ((uint)branch * 2891336453u);
		value ^= value >> 16;
		value *= 2246822519u;
		value ^= value >> 13;
		return ((value & 0x00FFFFFFu) / 8388607.5f) - 1f;
	}

	private static void BuildFrame(BoundaryVertex[] boundary, Vector3 center, Vector3 normal,
		out Vector3 axisU, out Vector3 axisV, out float radius)
	{
		axisU = Vector3.zero;
		float best = 0f;
		for (int index = 0; index < boundary.Length; index++)
		{
			Vector3 candidate = Vector3.ProjectOnPlane(boundary[index].Position - center, normal);
			if (candidate.sqrMagnitude <= best) continue;
			best = candidate.sqrMagnitude;
			axisU = candidate.normalized;
		}
		if (axisU.sqrMagnitude < 0.5f) axisU = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.8f ? Vector3.up : Vector3.right).normalized;
		axisV = Vector3.Cross(normal, axisU).normalized;
		radius = 0f;
		for (int index = 0; index < boundary.Length; index++)
			radius += Vector3.ProjectOnPlane(boundary[index].Position - center, normal).magnitude;
		radius /= boundary.Length;
	}

	private static bool ValidateFiniteAndNondegenerate(Builder builder, out string failure)
	{
		failure = null;
		for (int index = 0; index < builder.Vertices.Count; index++)
			if (!IsFinite(builder.Vertices[index].Position) || !IsFinite(builder.Vertices[index].Normal))
			{
				failure = $"generated wound vertex {index} is not finite";
				return false;
			}
		for (int index = 0; index + 2 < builder.Triangles.Count; index += 3)
		{
			Vector3 a = builder.Vertices[builder.Triangles[index]].Position;
			Vector3 b = builder.Vertices[builder.Triangles[index + 1]].Position;
			Vector3 c = builder.Vertices[builder.Triangles[index + 2]].Position;
			// Decorative cords intentionally taper below a millimetre. Reject only
			// effectively zero-area float geometry, not legitimate tiny end facets.
			if (Vector3.Cross(b - a, c - a).sqrMagnitude > 0.00000000000000000001f) continue;
			failure = $"generated wound triangle {index / 3} is degenerate " +
				$"({builder.Triangles[index]},{builder.Triangles[index + 1]},{builder.Triangles[index + 2]}; " +
				$"features={builder.Vertices[builder.Triangles[index]].Feature}/" +
				$"{builder.Vertices[builder.Triangles[index + 1]].Feature}/" +
				$"{builder.Vertices[builder.Triangles[index + 2]].Feature})";
			return false;
		}
		return true;
	}

	private static bool ValidateClosedRanges(Builder builder, out string failure)
	{
		failure = null;
		for (int rangeIndex = 0; rangeIndex < builder.ClosedRanges.Count; rangeIndex++)
		{
			(int start, int end) = builder.ClosedRanges[rangeIndex];
			Dictionary<(int, int), int> edges = new Dictionary<(int, int), int>();
			Dictionary<(int, int), int> winding = new Dictionary<(int, int), int>();
			for (int index = start; index + 2 < end; index += 3)
			{
				AddEdge(edges, winding, builder.Triangles[index], builder.Triangles[index + 1]);
				AddEdge(edges, winding, builder.Triangles[index + 1], builder.Triangles[index + 2]);
				AddEdge(edges, winding, builder.Triangles[index + 2], builder.Triangles[index]);
			}
			foreach (KeyValuePair<(int, int), int> edge in edges)
			{
				int balance = winding.TryGetValue(edge.Key, out int value) ? value : int.MinValue;
				if (edge.Value == 2 && balance == 0) continue;
				Feature feature = builder.Vertices[edge.Key.Item1].Feature;
				List<string> incident = new List<string>();
				for (int triangle = start; triangle + 2 < end; triangle += 3)
				{
					int a = builder.Triangles[triangle];
					int b = builder.Triangles[triangle + 1];
					int c = builder.Triangles[triangle + 2];
					if ((a == edge.Key.Item1 || b == edge.Key.Item1 || c == edge.Key.Item1) &&
						(a == edge.Key.Item2 || b == edge.Key.Item2 || c == edge.Key.Item2))
						incident.Add($"({a},{b},{c})");
				}
				failure = $"range={rangeIndex}, feature={feature}, edge=({edge.Key.Item1},{edge.Key.Item2}), count={edge.Value}, winding={balance}";
				failure += $", triangles={string.Join("/", incident)}";
				return false;
			}
		}
		return true;
	}

	private static void AddEdge(Dictionary<(int, int), int> edges,
		Dictionary<(int, int), int> winding, int a, int b)
	{
		var key = a < b ? (a, b) : (b, a);
		edges[key] = edges.TryGetValue(key, out int count) ? count + 1 : 1;
		int direction = a < b ? 1 : -1;
		winding[key] = winding.TryGetValue(key, out int balance) ? balance + direction : direction;
	}

	private static BoneWeight AverageWeights(IEnumerable<BoneWeight> values)
	{
		Dictionary<int, float> weights = new Dictionary<int, float>();
		int count = 0;
		foreach (BoneWeight value in values)
		{
			count++;
			AddWeight(weights, value.boneIndex0, value.weight0);
			AddWeight(weights, value.boneIndex1, value.weight1);
			AddWeight(weights, value.boneIndex2, value.weight2);
			AddWeight(weights, value.boneIndex3, value.weight3);
		}
		if (count > 1)
			foreach (int bone in weights.Keys.ToArray()) weights[bone] /= count;
		return CreateWeight(weights);
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

	private static void AddWeight(Dictionary<int, float> weights, int bone, float value)
	{
		if (value <= 0f) return;
		weights[bone] = weights.TryGetValue(bone, out float existing) ? existing + value : value;
	}

	private static BoneWeight CreateWeight(Dictionary<int, float> values)
	{
		KeyValuePair<int, float>[] ordered = values.OrderByDescending(item => item.Value).Take(4).ToArray();
		float total = ordered.Sum(item => item.Value);
		if (total <= 0.000001f) return default;
		BoneWeight result = new BoneWeight();
		if (ordered.Length > 0) { result.boneIndex0 = ordered[0].Key; result.weight0 = ordered[0].Value / total; }
		if (ordered.Length > 1) { result.boneIndex1 = ordered[1].Key; result.weight1 = ordered[1].Value / total; }
		if (ordered.Length > 2) { result.boneIndex2 = ordered[2].Key; result.weight2 = ordered[2].Value / total; }
		if (ordered.Length > 3) { result.boneIndex3 = ordered[3].Key; result.weight3 = ordered[3].Value / total; }
		return result;
	}

	private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
	private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	// Pure hooks used by compiled regressions and the Unity preview harness.
	internal static float[] SeamOffsetsForTests() => new[] { 0f, MissingBandWorld };
	// A fracture reuses precisely the severed bone primitive, with a rigid weight.
	// Coordinates are in its anatomical bone frame; the caller owns the collar.
	internal static Surface GenerateFractureBone(bool isLeg, LimbSeverLevel level, int boneIndex,
		uint seed, float worldScale, Vector3 root, Vector3 normal, Vector3 tangent, int skinBoneIndex)
	{
		if (boneIndex < 0 || boneIndex >= AnatomicalBoneCount(level) || worldScale <= 0f)
			throw new ArgumentOutOfRangeException(nameof(boneIndex));
		var request = new Request { IsLeg = isLeg, Level = level, WorldScale = worldScale };
		var builder = new Builder();
		var random = new DeterministicRandom(seed);
		normal.Normalize();
		tangent = Vector3.ProjectOnPlane(tangent, normal).normalized;
		var metrics = AddBone(builder, request, ref random, root, normal, tangent,
			Vector3.Cross(normal, tangent).normalized, 0.05f / worldScale, 0f,
			new BoneWeight { boneIndex0 = skinBoneIndex, weight0 = 1f }, boneIndex,
			root, 0.060f / worldScale, exposedLengthScale: 1.5f);
		if (!ValidateFiniteAndNondegenerate(builder, out string failure) || !ValidateClosedRanges(builder, out failure))
			throw new InvalidOperationException("fracture bone: " + failure);
		return new Surface { Vertices = builder.Vertices.ToArray(), Triangles = builder.Triangles.ToArray(),
			BoneCount = 1, BoneVertexCount = builder.Vertices.Count, DecorationsClosed = true,
			BoneRadiiWorld = new[] { metrics.RadiiWorld }, MinimumBoneEmbedWorld = metrics.EmbeddedDepthWorld,
			MinimumBoneExposedReachWorld = metrics.ExposedReachWorld, MinimumBoneFractureSpanWorld = metrics.FractureSpanWorld };
	}

	internal static int BoneCountForTests(bool isLeg, LimbSeverLevel level) => AnatomicalBoneCount(level);
	internal static int DecalCountForTests(bool isLeg) => isLeg ? 8 : 6;
	internal static float MaximumAdornmentReachForTests() => MaximumAdornmentReachWorld;
	internal static float[] BoneRootsForTests(uint seed, int count)
	{
		Vector2[] roots = BuildSharedBoneRoots(seed, count);
		float[] result = new float[roots.Length * 2];
		for (int index = 0; index < roots.Length; index++)
		{
			result[index * 2] = roots[index].x;
			result[index * 2 + 1] = roots[index].y;
		}
		return result;
	}

	internal static float[] BoneDiameterBaselineForTests(bool isLeg, LimbSeverLevel level, int boneIndex)
	{
		Vector2 radii = ResolveBoneRadiiWorld(isLeg, level, boneIndex);
		return new[] { radii.x * 2f, radii.y * 2f };
	}

	internal static float[] GenerateMetricsForTests(bool isLeg, LimbSeverLevel level, uint seed,
		bool gibSide, bool primary)
	{
		const int count = 18;
		BoundaryVertex[] boundary = new BoundaryVertex[count];
		for (int index = 0; index < count; index++)
		{
			float angle = Mathf.PI * 2f * index / count;
			BoneWeight weight = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
			boundary[index] = new BoundaryVertex(new Vector3(Mathf.Cos(angle) * (isLeg ? 0.075f : 0.052f),
				Mathf.Sin(angle) * (isLeg ? 0.061f : 0.043f), 0f), Vector3.forward, weight);
		}
		if (!TryGenerate(new Request
		{
			Boundary = boundary,
			FacingNormal = Vector3.forward,
			StableCutAxis = Vector3.forward,
			WorldScale = 1f,
			IsLeg = isLeg,
			Level = level,
			Seed = seed,
			GibSide = gibSide,
			AddAnatomicalDetails = primary
		}, out Surface generated, out _)) return Array.Empty<float>();
		int exactMatches = 0;
		foreach (GeneratedVertex vertex in generated.Vertices)
		{
			if (vertex.Feature != Feature.ExactSeam || vertex.BoundarySource < 0 ||
				vertex.BoundarySource >= boundary.Length) continue;
			if (vertex.Position == boundary[vertex.BoundarySource].Position) exactMatches++;
		}
		Vector2 firstBone = generated.BoneRadiiWorld.Length > 0 ? generated.BoneRadiiWorld[0] : Vector2.zero;
		Vector2 secondBone = generated.BoneRadiiWorld.Length > 1 ? generated.BoneRadiiWorld[1] : Vector2.zero;
		return new[]
		{
			(float)generated.Vertices.Length,
			generated.Triangles.Length / 3f,
			generated.BoneCount,
			generated.BoneVertexCount,
			generated.FlapCount,
			generated.CordCount,
			generated.MeanPlateauProtrusionWorld,
			generated.MaximumProtrusionWorld,
			generated.DecorationsClosed ? 1f : 0f,
			generated.DecalSamples.Length,
			exactMatches,
			generated.MinimumBoneFractureSpanWorld,
			Vector3.Dot(generated.StreamAnchor.Position, Vector3.forward),
			generated.MinimumAdornmentEmbedWorld,
			generated.MinimumBoneEmbedWorld,
			generated.MinimumBoneExposedReachWorld,
			generated.MaximumBoneRadiusWorld,
			firstBone.x * 2f,
			firstBone.y * 2f,
			secondBone.x * 2f,
			secondBone.y * 2f
		};
	}

	internal static string GenerationFailureForTests(bool isLeg, LimbSeverLevel level, uint seed,
		bool gibSide, bool primary)
	{
		const int count = 18;
		BoundaryVertex[] boundary = new BoundaryVertex[count];
		for (int index = 0; index < count; index++)
		{
			float angle = Mathf.PI * 2f * index / count;
			boundary[index] = new BoundaryVertex(new Vector3(Mathf.Cos(angle) * (isLeg ? 0.075f : 0.052f),
				Mathf.Sin(angle) * (isLeg ? 0.061f : 0.043f), 0f), Vector3.forward,
				new BoneWeight { boneIndex0 = 0, weight0 = 1f });
		}
		return TryGenerate(new Request
		{
			Boundary = boundary, FacingNormal = Vector3.forward, StableCutAxis = Vector3.forward,
			WorldScale = 1f, IsLeg = isLeg,
			Level = level, Seed = seed, GibSide = gibSide, AddAnatomicalDetails = primary
		}, out _, out string failure) ? string.Empty : failure;
	}
}
}
