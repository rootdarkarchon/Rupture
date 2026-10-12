using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Comfort.Common;
using Systems.Effects;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.Limbs;

internal sealed class LimbDecalAudit
{
	internal string RendererLabel;
	internal int LodLevel;
	internal Vector2[] BodyUvs = Array.Empty<Vector2>();
	internal Vector2[] GibUvs = Array.Empty<Vector2>();
	internal string BodyUvExtraction;
	internal string GibUvExtraction;
	internal LimbDecalTextureSnapshot SourceBefore;
	internal LimbDecalTextureSnapshot GibBefore;
	internal LimbDecalQueueSnapshot QueueBefore;
}

internal sealed class LimbDecalTextureSnapshot
{
	internal bool Available;
	internal string Failure;
	internal int TextureId;
	internal string TextureName;
	internal int SourceWidth;
	internal int SourceHeight;
	internal int ReadbackWidth;
	internal int ReadbackHeight;
	internal ulong Hash;
	internal int NonZeroPixels;
	internal ulong RedTotal;
	internal ulong GreenTotal;
	internal ulong BlueTotal;
	internal ulong AlphaTotal;
	internal int BodySampleCount;
	internal int BodyNonZeroSamples;
	internal ulong BodySamplePeakEnergy;
	internal int GibSampleCount;
	internal int GibNonZeroSamples;
	internal ulong GibSamplePeakEnergy;
	internal string BodySamples;
	internal string GibSamples;

	internal string Describe(string stage)
	{
		if (!Available) return $"{stage}=unavailable({Failure ?? "unknown"})";
		return $"{stage}=rt#{TextureId} '{TextureName}' {SourceWidth}x{SourceHeight}->" +
			$"{ReadbackWidth}x{ReadbackHeight} hash={Hash:X16} nonZero={NonZeroPixels}/" +
			$"{ReadbackWidth * ReadbackHeight} rgbaSum={RedTotal},{GreenTotal},{BlueTotal},{AlphaTotal} " +
			$"bodyRegion={BodyNonZeroSamples}/{BodySampleCount},energy:{BodySamplePeakEnergy} " +
			$"gibRegion={GibNonZeroSamples}/{GibSampleCount},energy:{GibSamplePeakEnergy} " +
			$"bodyUv=[{BodySamples}] gibUv=[{GibSamples}]";
	}
}

internal sealed class LimbDecalQueueSnapshot
{
	internal bool Available;
	internal string Failure;
	internal int Total;
	internal int Observed;
	internal string PerRenderer;

	internal string Describe(string stage) => Available
		? $"{stage}=total:{Total},observed:{Observed},perRenderer:[{PerRenderer}]"
		: $"{stage}=unavailable({Failure ?? "unknown"})";
}

internal sealed class LimbMeshUvLayoutSnapshot
{
	internal int Channel;
	internal bool HasAttribute;
	internal VertexAttributeDescriptor Attribute;
	internal int Count;
	internal int FiniteCount;
	internal int NonZeroXyCount;
	internal int NonZeroZwCount;
	internal ulong Hash;

	internal string Describe() => HasAttribute || Count > 0
		? $"uv{Channel}:{LimbDecalDiagnostics.DescribeAttribute(Attribute)},data:{Count}," +
			$"finite:{FiniteCount},xyNonZero:{NonZeroXyCount},zwNonZero:{NonZeroZwCount},hash:{Hash:X16}"
		: $"uv{Channel}:absent,data:0";
}

internal sealed class LimbMeshLayoutSnapshot
{
	internal bool Available;
	internal string Failure;
	internal string MeshName;
	internal int MeshId;
	internal bool Readable;
	internal int VertexCount;
	internal int SubmeshCount;
	internal IndexFormat IndexFormat;
	internal VertexAttributeDescriptor[] Attributes = Array.Empty<VertexAttributeDescriptor>();
	internal int[] StreamStrides = Array.Empty<int>();
	internal LimbMeshUvLayoutSnapshot[] Uvs = Array.Empty<LimbMeshUvLayoutSnapshot>();
	internal int ColorCount;
	internal int ColorNonWhiteCount;
	internal int ColorNonZeroAlphaCount;
	internal ulong ColorHash;
	internal int LegacyBoneWeightCount;
	internal ulong LegacyBoneWeightHash;
	internal int BonesPerVertexCount;
	internal int TotalBoneInfluences;
	internal int MaximumBoneInfluences;
	internal int VerticesAboveFourInfluences;
	internal int BoneWeightOneCount;
	internal ulong VariableBoneWeightHash;
	internal int BindposeCount;
	internal int BlendShapeCount;

	internal string Describe()
	{
		if (!Available) return $"unavailable({Failure ?? "unknown"})";
		string attributes = string.Join(";", Attributes.Select(LimbDecalDiagnostics.DescribeAttribute));
		string strides = string.Join(";", StreamStrides.Select((stride, stream) => $"s{stream}:{stride}"));
		string uvs = string.Join(";", Uvs.Select(item => item.Describe()));
		return $"mesh:'{MeshName}'#{MeshId},readable:{Readable},vertices:{VertexCount},submeshes:{SubmeshCount}," +
			$"index:{IndexFormat},vertexBuffers:{StreamStrides.Length},attrs:[{attributes}],strides:[{strides}]," +
			$"uvs:[{uvs}],colors:count:{ColorCount},nonWhite:{ColorNonWhiteCount}," +
			$"nonZeroAlpha:{ColorNonZeroAlphaCount},hash:{ColorHash:X16}," +
			$"skin:legacy:{LegacyBoneWeightCount},legacyHash:{LegacyBoneWeightHash:X16}," +
			$"bonesPerVertex:{BonesPerVertexCount},influences:{TotalBoneInfluences},max:{MaximumBoneInfluences}," +
			$"above4:{VerticesAboveFourInfluences},boneWeight1:{BoneWeightOneCount}," +
			$"variableHash:{VariableBoneWeightHash:X16},bindposes:{BindposeCount},blendShapes:{BlendShapeCount}";
	}
}

/// <summary>
/// Opt-in diagnostics for EFT's accumulated per-renderer decal textures. The audit
/// deliberately reads back GPU state only when the procedural cut overlay was enabled
/// before the cut; normal dismemberment never pays the synchronous readback cost.
/// </summary>
internal static class LimbDecalDiagnostics
{
	private const int ReadbackSize = 256;
	private const int MaximumLoggedSamplesPerGroup = 8;
	private static readonly int DecalsTextureId = Shader.PropertyToID("_DecalsTex");
	private static readonly int DecalPowerId = Shader.PropertyToID("_DecalPower");
	private static readonly FieldInfo PendingQueueField = ResolvePendingQueueField();

	internal static LimbDecalAudit CaptureBeforeCommit(SkinnedMeshRenderer source, Mesh bodyMesh,
		Renderer gibRenderer,
		ProceduralLimbWoundGeometry.DecalSample[] bodySamples,
		ProceduralLimbWoundGeometry.DecalSample[] gibSamples, int lodLevel)
	{
		LimbDecalAudit audit = new LimbDecalAudit
		{
			RendererLabel = RendererLabel(source),
			LodLevel = lodLevel,
			QueueBefore = CaptureQueue(source, gibRenderer)
		};
		audit.BodyUvs = ExtractUvs(bodyMesh, bodySamples, out audit.BodyUvExtraction);
		Mesh gibMesh = GetMesh(gibRenderer);
		audit.GibUvs = ExtractUvs(gibMesh, gibSamples, out audit.GibUvExtraction);
		audit.SourceBefore = CaptureTexture(source, audit.BodyUvs, audit.GibUvs);
		audit.GibBefore = CaptureTexture(gibRenderer, Array.Empty<Vector2>(), audit.GibUvs);
		return audit;
	}

	internal static LimbMeshLayoutSnapshot CaptureMeshLayout(Mesh mesh)
	{
		LimbMeshLayoutSnapshot snapshot = new LimbMeshLayoutSnapshot();
		if (mesh == null)
		{
			snapshot.Failure = "mesh-missing";
			return snapshot;
		}
		try
		{
			snapshot.MeshName = mesh.name;
			snapshot.MeshId = mesh.GetInstanceID();
			snapshot.Readable = mesh.isReadable;
			snapshot.VertexCount = mesh.vertexCount;
			snapshot.SubmeshCount = mesh.subMeshCount;
			snapshot.IndexFormat = mesh.indexFormat;
			snapshot.Attributes = mesh.GetVertexAttributes() ?? Array.Empty<VertexAttributeDescriptor>();
			int streamCount = snapshot.Attributes.Length == 0
				? mesh.vertexBufferCount
				: Math.Max(mesh.vertexBufferCount, snapshot.Attributes.Max(item => item.stream) + 1);
			snapshot.StreamStrides = new int[Math.Max(0, streamCount)];
			for (int stream = 0; stream < snapshot.StreamStrides.Length; stream++)
				snapshot.StreamStrides[stream] = mesh.GetVertexBufferStride(stream);

			snapshot.Uvs = new LimbMeshUvLayoutSnapshot[8];
			for (int channel = 0; channel < snapshot.Uvs.Length; channel++)
				snapshot.Uvs[channel] = CaptureUvLayout(mesh, snapshot.Attributes, channel);

			Color32[] colors = mesh.colors32 ?? Array.Empty<Color32>();
			snapshot.ColorCount = colors.Length;
			ulong colorHash = 14695981039346656037UL;
			foreach (Color32 color in colors)
			{
				if (color.r != byte.MaxValue || color.g != byte.MaxValue ||
					color.b != byte.MaxValue || color.a != byte.MaxValue) snapshot.ColorNonWhiteCount++;
				if (color.a != 0) snapshot.ColorNonZeroAlphaCount++;
				colorHash = HashByte(colorHash, color.r);
				colorHash = HashByte(colorHash, color.g);
				colorHash = HashByte(colorHash, color.b);
				colorHash = HashByte(colorHash, color.a);
			}
			snapshot.ColorHash = colorHash;

			BoneWeight[] legacyWeights = mesh.boneWeights ?? Array.Empty<BoneWeight>();
			snapshot.LegacyBoneWeightCount = legacyWeights.Length;
			ulong legacyHash = 14695981039346656037UL;
			foreach (BoneWeight weight in legacyWeights)
			{
				legacyHash = HashInt32(legacyHash, weight.boneIndex0);
				legacyHash = HashSingle(legacyHash, weight.weight0);
				legacyHash = HashInt32(legacyHash, weight.boneIndex1);
				legacyHash = HashSingle(legacyHash, weight.weight1);
				legacyHash = HashInt32(legacyHash, weight.boneIndex2);
				legacyHash = HashSingle(legacyHash, weight.weight2);
				legacyHash = HashInt32(legacyHash, weight.boneIndex3);
				legacyHash = HashSingle(legacyHash, weight.weight3);
			}
			snapshot.LegacyBoneWeightHash = legacyHash;

			var bonesPerVertex = mesh.GetBonesPerVertex();
			var allBoneWeights = mesh.GetAllBoneWeights();
			snapshot.BonesPerVertexCount = bonesPerVertex.Length;
			snapshot.BoneWeightOneCount = allBoneWeights.Length;
			ulong variableHash = 14695981039346656037UL;
			for (int index = 0; index < bonesPerVertex.Length; index++)
			{
				int count = bonesPerVertex[index];
				snapshot.TotalBoneInfluences += count;
				snapshot.MaximumBoneInfluences = Math.Max(snapshot.MaximumBoneInfluences, count);
				if (count > 4) snapshot.VerticesAboveFourInfluences++;
				variableHash = HashByte(variableHash, bonesPerVertex[index]);
			}
			for (int index = 0; index < allBoneWeights.Length; index++)
			{
				BoneWeight1 weight = allBoneWeights[index];
				variableHash = HashInt32(variableHash, weight.boneIndex);
				variableHash = HashSingle(variableHash, weight.weight);
			}
			snapshot.VariableBoneWeightHash = variableHash;
			snapshot.BindposeCount = mesh.bindposes?.Length ?? 0;
			snapshot.BlendShapeCount = mesh.blendShapeCount;
			snapshot.Available = true;
		}
		catch (Exception exception)
		{
			snapshot.Failure = $"{exception.GetType().Name}: {exception.Message}";
		}
		return snapshot;
	}

	internal static string CompareMeshLayouts(string label, LimbMeshLayoutSnapshot expected,
		LimbMeshLayoutSnapshot actual)
	{
		if (expected?.Available != true || actual?.Available != true)
			return $"{label}=not-comparable(expected:{expected?.Failure ?? "missing"}," +
				$"actual:{actual?.Failure ?? "missing"})";
		Dictionary<VertexAttribute, VertexAttributeDescriptor> expectedAttributes = expected.Attributes
			.ToDictionary(item => item.attribute, item => item);
		Dictionary<VertexAttribute, VertexAttributeDescriptor> actualAttributes = actual.Attributes
			.ToDictionary(item => item.attribute, item => item);
		List<string> missing = expectedAttributes.Keys.Except(actualAttributes.Keys)
			.OrderBy(item => (int)item).Select(item => item.ToString()).ToList();
		List<string> added = actualAttributes.Keys.Except(expectedAttributes.Keys)
			.OrderBy(item => (int)item).Select(item => item.ToString()).ToList();
		List<string> changed = new List<string>();
		foreach (VertexAttribute semantic in expectedAttributes.Keys.Intersect(actualAttributes.Keys)
			.OrderBy(item => (int)item))
		{
			VertexAttributeDescriptor before = expectedAttributes[semantic];
			VertexAttributeDescriptor after = actualAttributes[semantic];
			if (SameAttribute(before, after)) continue;
			changed.Add($"{semantic}:{DescribeAttribute(before)}->{DescribeAttribute(after)}");
		}
		List<string> uvDataChanges = new List<string>();
		for (int channel = 0; channel < Math.Min(expected.Uvs.Length, actual.Uvs.Length); channel++)
		{
			LimbMeshUvLayoutSnapshot before = expected.Uvs[channel];
			LimbMeshUvLayoutSnapshot after = actual.Uvs[channel];
			if (before.HasAttribute == after.HasAttribute && before.Count == after.Count &&
				before.Attribute.dimension == after.Attribute.dimension &&
				before.Attribute.format == after.Attribute.format) continue;
			uvDataChanges.Add($"uv{channel}:attr:{before.HasAttribute}->{after.HasAttribute}," +
				$"format:{before.Attribute.format}x{before.Attribute.dimension}->" +
				$"{after.Attribute.format}x{after.Attribute.dimension},data:{before.Count}->{after.Count}");
		}
		bool attributesExact = missing.Count == 0 && added.Count == 0 && changed.Count == 0;
		bool stridesExact = expected.StreamStrides.SequenceEqual(actual.StreamStrides);
		return $"{label}=attributesExact:{attributesExact},stridesExact:{stridesExact}," +
			$"missing:[{string.Join(",", missing)}],added:[{string.Join(",", added)}]," +
			$"changed:[{string.Join(";", changed)}]," +
			$"strides:[{string.Join(",", expected.StreamStrides)}]->[{string.Join(",", actual.StreamStrides)}]," +
			$"uvLayoutOrCount:[{string.Join(";", uvDataChanges)}]," +
			$"colors:{expected.ColorCount}->{actual.ColorCount}," +
			$"legacyWeights:{expected.LegacyBoneWeightCount}->{actual.LegacyBoneWeightCount}," +
			$"bonesPerVertex:{expected.BonesPerVertexCount}->{actual.BonesPerVertexCount}," +
			$"influences:{expected.TotalBoneInfluences}->{actual.TotalBoneInfluences}," +
			$"maxInfluences:{expected.MaximumBoneInfluences}->{actual.MaximumBoneInfluences}," +
			$"bindposes:{expected.BindposeCount}->{actual.BindposeCount}," +
			$"blendShapes:{expected.BlendShapeCount}->{actual.BlendShapeCount}";
	}

	internal static string DescribeAttribute(VertexAttributeDescriptor attribute) =>
		$"{attribute.attribute}:{attribute.format}x{attribute.dimension}@s{attribute.stream}";

	private static LimbMeshUvLayoutSnapshot CaptureUvLayout(Mesh mesh,
		VertexAttributeDescriptor[] attributes, int channel)
	{
		VertexAttribute semantic = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
		LimbMeshUvLayoutSnapshot snapshot = new LimbMeshUvLayoutSnapshot { Channel = channel };
		foreach (VertexAttributeDescriptor attribute in attributes)
			if (attribute.attribute == semantic)
			{
				snapshot.HasAttribute = true;
				snapshot.Attribute = attribute;
				break;
			}
		List<Vector4> values = new List<Vector4>();
		mesh.GetUVs(channel, values);
		snapshot.Count = values.Count;
		ulong hash = 14695981039346656037UL;
		foreach (Vector4 value in values)
		{
			if (IsFinite(value)) snapshot.FiniteCount++;
			if (!Mathf.Approximately(value.x, 0f) || !Mathf.Approximately(value.y, 0f)) snapshot.NonZeroXyCount++;
			if (!Mathf.Approximately(value.z, 0f) || !Mathf.Approximately(value.w, 0f)) snapshot.NonZeroZwCount++;
			hash = HashSingle(hash, value.x);
			hash = HashSingle(hash, value.y);
			hash = HashSingle(hash, value.z);
			hash = HashSingle(hash, value.w);
		}
		snapshot.Hash = hash;
		return snapshot;
	}

	private static bool SameAttribute(VertexAttributeDescriptor left, VertexAttributeDescriptor right) =>
		left.attribute == right.attribute && left.format == right.format &&
		left.dimension == right.dimension && left.stream == right.stream;

	internal static Vector2[] ExtractUvs(Mesh mesh,
		ProceduralLimbWoundGeometry.DecalSample[] samples, out string description)
	{
		if (mesh == null)
		{
			description = "mesh-missing";
			return Array.Empty<Vector2>();
		}
		Vector2[] uv = mesh.uv;
		if (uv == null || uv.Length != mesh.vertexCount)
		{
			description = $"uv0-count:{uv?.Length ?? 0}/vertices:{mesh.vertexCount}";
			return Array.Empty<Vector2>();
		}
		List<Vector2> result = new List<Vector2>();
		int invalid = 0;
		Dictionary<ulong, int> triangleSubmeshes = BuildTriangleSubmeshMap(mesh);
		Dictionary<int, int> sampleSubmeshes = new Dictionary<int, int>();
		int missingSampleTriangles = 0;
		foreach (ProceduralLimbWoundGeometry.DecalSample sample in samples ??
			Array.Empty<ProceduralLimbWoundGeometry.DecalSample>())
		{
			if (sample.MeshVertexA < 0 || sample.MeshVertexB < 0 || sample.MeshVertexC < 0 ||
				sample.MeshVertexA >= uv.Length || sample.MeshVertexB >= uv.Length || sample.MeshVertexC >= uv.Length)
			{
				invalid++;
				continue;
			}
			Vector3 barycentric = sample.MeshBarycentric;
			Vector2 value = uv[sample.MeshVertexA] * barycentric.x +
				uv[sample.MeshVertexB] * barycentric.y + uv[sample.MeshVertexC] * barycentric.z;
			if (!IsFinite(value))
			{
				invalid++;
				continue;
			}
			result.Add(value);
			ulong triangleKey = TriangleKey(sample.MeshVertexA, sample.MeshVertexB, sample.MeshVertexC);
			if (triangleSubmeshes.TryGetValue(triangleKey, out int submesh))
				sampleSubmeshes[submesh] = sampleSubmeshes.TryGetValue(submesh, out int count) ? count + 1 : 1;
			else missingSampleTriangles++;
		}
		description = $"mesh:'{mesh.name}',vertices:{mesh.vertexCount},submeshes:{mesh.subMeshCount}," +
			$"uv0:{uv.Length},samples:{samples?.Length ?? 0},valid:{result.Count},invalid:{invalid}," +
			$"range:{UvRange(result)},sampleSubmeshes:[{string.Join(",", sampleSubmeshes.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"))}]," +
			$"sampleTrianglesMissing:{missingSampleTriangles},eftDrawRendererDefaultSubmesh:0";
		return result.ToArray();
	}

	internal static LimbDecalTextureSnapshot CaptureTexture(Renderer renderer, Vector2[] bodyUvs,
		Vector2[] gibUvs)
	{
		LimbDecalTextureSnapshot snapshot = new LimbDecalTextureSnapshot();
		if (!LimbDecalTextureTransfer.TryGetRegisteredTexture(renderer, out RenderTexture source,
			out string lookupFailure))
		{
			snapshot.Failure = lookupFailure;
			return snapshot;
		}
		if (!source.IsCreated())
		{
			snapshot.Failure = "registered render texture is not created";
			return snapshot;
		}

		RenderTexture temporary = null;
		Texture2D cpuTexture = null;
		RenderTexture previousActive = RenderTexture.active;
		try
		{
			temporary = RenderTexture.GetTemporary(ReadbackSize, ReadbackSize, 0,
				RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
			temporary.filterMode = FilterMode.Bilinear;
			Graphics.Blit(source, temporary);
			RenderTexture.active = temporary;
			cpuTexture = new Texture2D(ReadbackSize, ReadbackSize, TextureFormat.RGBA32, false, true);
			cpuTexture.ReadPixels(new Rect(0f, 0f, ReadbackSize, ReadbackSize), 0, 0, false);
			cpuTexture.Apply(false, false);
			Color32[] pixels = cpuTexture.GetPixels32();
			ulong hash = 14695981039346656037UL;
			int nonZero = 0;
			ulong red = 0;
			ulong green = 0;
			ulong blue = 0;
			ulong alpha = 0;
			foreach (Color32 pixel in pixels)
			{
				if (pixel.r != 0 || pixel.g != 0 || pixel.b != 0 || pixel.a != 0) nonZero++;
				red += pixel.r;
				green += pixel.g;
				blue += pixel.b;
				alpha += pixel.a;
				hash = HashByte(hash, pixel.r);
				hash = HashByte(hash, pixel.g);
				hash = HashByte(hash, pixel.b);
				hash = HashByte(hash, pixel.a);
			}
			snapshot.Available = true;
			snapshot.TextureId = source.GetInstanceID();
			snapshot.TextureName = source.name;
			snapshot.SourceWidth = source.width;
			snapshot.SourceHeight = source.height;
			snapshot.ReadbackWidth = ReadbackSize;
			snapshot.ReadbackHeight = ReadbackSize;
			snapshot.Hash = hash;
			snapshot.NonZeroPixels = nonZero;
			snapshot.RedTotal = red;
			snapshot.GreenTotal = green;
			snapshot.BlueTotal = blue;
			snapshot.AlphaTotal = alpha;
			snapshot.BodySamples = DescribeSamples(pixels, ReadbackSize, ReadbackSize, bodyUvs,
				out snapshot.BodySampleCount, out snapshot.BodyNonZeroSamples, out snapshot.BodySamplePeakEnergy);
			snapshot.GibSamples = DescribeSamples(pixels, ReadbackSize, ReadbackSize, gibUvs,
				out snapshot.GibSampleCount, out snapshot.GibNonZeroSamples, out snapshot.GibSamplePeakEnergy);
	}
		catch (Exception exception)
		{
			snapshot.Available = false;
			snapshot.Failure = $"{exception.GetType().Name}: {exception.Message}";
		}
		finally
		{
			RenderTexture.active = previousActive;
			if (cpuTexture != null) UnityEngine.Object.Destroy(cpuTexture);
			if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
		}
		return snapshot;
	}

	internal static string DescribeBinding(Renderer renderer)
	{
		if (renderer == null) return "renderer-missing";
		StringBuilder builder = new StringBuilder();
		Mesh mesh = GetMesh(renderer);
		builder.Append(RendererLabel(renderer));
		builder.Append($",enabled:{renderer.enabled},visible:{renderer.isVisible},active:{renderer.gameObject.activeSelf},");
		builder.Append($"mesh:'{mesh?.name ?? "null"}',vertices:{mesh?.vertexCount ?? 0},submeshes:{mesh?.subMeshCount ?? 0},");
		if (LimbDecalTextureTransfer.TryGetRegisteredTexture(renderer, out RenderTexture registered,
			out string textureFailure))
			builder.Append($"dictionaryRt:#{registered.GetInstanceID()}('{registered.name}',{registered.width}x{registered.height},created:{registered.IsCreated()}),");
		else builder.Append($"dictionaryRt:none({textureFailure}),");

		Material[] materials = renderer.sharedMaterials ?? Array.Empty<Material>();
		builder.Append($"materials:{materials.Length}[");
		for (int index = 0; index < materials.Length; index++)
		{
			if (index > 0) builder.Append(';');
			Material material = materials[index];
			if (material == null)
			{
				builder.Append($"{index}:null");
				continue;
			}
			bool hasTexture = material.HasProperty(DecalsTextureId);
			bool hasPower = material.HasProperty(DecalPowerId);
			Texture texture = hasTexture ? material.GetTexture(DecalsTextureId) : null;
			float power = hasPower ? material.GetFloat(DecalPowerId) : float.NaN;
			builder.Append($"{index}:'{material.name}' shader:'{material.shader?.name ?? "null"}' " +
				$"decalProp:{hasTexture} bound:{TextureLabel(texture)} power:{FormatFloat(power)}");
		}
		builder.Append(']');
		MaterialPropertyBlock block = new MaterialPropertyBlock();
		renderer.GetPropertyBlock(block);
		builder.Append($",globalBlock:empty:{block.isEmpty},tex:{TextureLabel(block.GetTexture(DecalsTextureId))}," +
			$"power:{FormatFloat(block.GetFloat(DecalPowerId))}");
		for (int index = 0; index < materials.Length; index++)
		{
			block.Clear();
			renderer.GetPropertyBlock(block, index);
			if (!block.isEmpty)
				builder.Append($",slot{index}Block:tex:{TextureLabel(block.GetTexture(DecalsTextureId))}," +
					$"power:{FormatFloat(block.GetFloat(DecalPowerId))}");
		}
		return builder.ToString();
	}

	internal static LimbDecalQueueSnapshot CaptureQueue(params Renderer[] observedRenderers)
	{
		LimbDecalQueueSnapshot snapshot = new LimbDecalQueueSnapshot();
		if (PendingQueueField == null)
		{
			snapshot.Failure = "exact List<TextureDecalsPainter.DecalDescription> queue field was not found";
			return snapshot;
		}
		if (!Singleton<Effects>.Instantiated || Singleton<Effects>.Instance?.TexDecals == null)
		{
			snapshot.Failure = "EFT texture decal painter is unavailable";
			return snapshot;
		}
		try
		{
			List<TextureDecalsPainter.DecalDescription> queue = PendingQueueField.GetValue(
				Singleton<Effects>.Instance.TexDecals) as List<TextureDecalsPainter.DecalDescription>;
			if (queue == null)
			{
				snapshot.Failure = "queue field returned null or an unexpected value";
				return snapshot;
			}
			Renderer[] observed = (observedRenderers ?? Array.Empty<Renderer>()).Where(item => item != null)
				.Distinct().ToArray();
			List<string> counts = new List<string>();
			int observedTotal = 0;
			foreach (Renderer renderer in observed)
			{
				int count = queue.Count(item => item.Renderer == renderer);
				observedTotal += count;
				counts.Add($"{RendererLabel(renderer)}:{count}");
			}
			snapshot.Available = true;
			snapshot.Total = queue.Count;
			snapshot.Observed = observedTotal;
			snapshot.PerRenderer = string.Join(",", counts);
		}
		catch (Exception exception)
		{
			snapshot.Failure = $"{exception.GetType().Name}: {exception.Message}";
		}
		return snapshot;
	}

	internal static string Compare(string label, LimbDecalTextureSnapshot before,
		LimbDecalTextureSnapshot after)
	{
		if (before?.Available != true || after?.Available != true)
			return $"{label}=not-comparable(before:{before?.Failure ?? "missing"},after:{after?.Failure ?? "missing"})";
		return $"{label}=sameRt:{before.TextureId == after.TextureId},sameHash:{before.Hash == after.Hash}," +
			$"nonZeroDelta:{after.NonZeroPixels - before.NonZeroPixels}," +
			$"rgbaDelta:{SignedDelta(after.RedTotal, before.RedTotal)}," +
			$"{SignedDelta(after.GreenTotal, before.GreenTotal)}," +
			$"{SignedDelta(after.BlueTotal, before.BlueTotal)}," +
			$"{SignedDelta(after.AlphaTotal, before.AlphaTotal)}";
	}

	internal static string CompareRegion(string label, LimbDecalTextureSnapshot before, bool beforeGibRegion,
		LimbDecalTextureSnapshot after, bool afterGibRegion)
	{
		if (before?.Available != true || after?.Available != true)
			return $"{label}=not-comparable(before:{before?.Failure ?? "missing"},after:{after?.Failure ?? "missing"})";
		int beforeCount = beforeGibRegion ? before.GibSampleCount : before.BodySampleCount;
		int beforeNonZero = beforeGibRegion ? before.GibNonZeroSamples : before.BodyNonZeroSamples;
		ulong beforeEnergy = beforeGibRegion ? before.GibSamplePeakEnergy : before.BodySamplePeakEnergy;
		int afterCount = afterGibRegion ? after.GibSampleCount : after.BodySampleCount;
		int afterNonZero = afterGibRegion ? after.GibNonZeroSamples : after.BodyNonZeroSamples;
		ulong afterEnergy = afterGibRegion ? after.GibSamplePeakEnergy : after.BodySamplePeakEnergy;
		return $"{label}=samples:{beforeNonZero}/{beforeCount}->{afterNonZero}/{afterCount}," +
			$"peakEnergy:{beforeEnergy}->{afterEnergy},delta:{SignedDelta(afterEnergy, beforeEnergy)}";
	}

	internal static string ClassifySide(string side, LimbDecalTextureSnapshot before, bool beforeGibRegion,
		LimbDecalTextureSnapshot after, bool afterGibRegion, int queuedEntryDelta)
	{
		if (queuedEntryDelta < 0)
			return $"{side}=EFT-queue-observation-unavailable";
		if (queuedEntryDelta <= 0)
			return $"{side}=no-new-EFT-queue-entry";
		if (after?.Available != true)
			return $"{side}=queued-but-no-registered-output-texture({after?.Failure ?? "missing"})";
		int afterNonZero = afterGibRegion ? after.GibNonZeroSamples : after.BodyNonZeroSamples;
		ulong afterEnergy = afterGibRegion ? after.GibSamplePeakEnergy : after.BodySamplePeakEnergy;
		if (before?.Available != true)
			return afterEnergy > 0
				? $"{side}=new-texture-has-seam-data; if visually clean inspect material binding and sample submesh"
				: $"{side}=queue-drained-but-new-texture-has-no-seam-UV-data";
		int beforeNonZero = beforeGibRegion ? before.GibNonZeroSamples : before.BodyNonZeroSamples;
		ulong beforeEnergy = beforeGibRegion ? before.GibSamplePeakEnergy : before.BodySamplePeakEnergy;
		if (before.Hash == after.Hash)
			return $"{side}=queue-drained-but-GPU-texture-unchanged";
		if (afterEnergy == 0)
			return $"{side}=GPU-texture-changed-globally-but-seam-UV-region-remained-empty";
		if (afterEnergy <= beforeEnergy && afterNonZero <= beforeNonZero)
			return $"{side}=GPU-texture-changed-without-seam-region-gain";
		return $"{side}=GPU-texture-and-seam-region-changed; if visually clean inspect binding and sample submesh";
	}

	private static FieldInfo ResolvePendingQueueField()
	{
		Type expected = typeof(List<TextureDecalsPainter.DecalDescription>);
		foreach (FieldInfo field in typeof(TextureDecalsPainter).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
			if (field.FieldType == expected) return field;
		return null;
	}

	private static Mesh GetMesh(Renderer renderer)
	{
		if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
		return renderer?.GetComponent<MeshFilter>()?.sharedMesh;
	}

	private static string RendererLabel(Renderer renderer) => renderer == null
		? "null"
		: $"'{renderer.name}'#{renderer.GetInstanceID()}({renderer.GetType().Name})";

	private static string TextureLabel(Texture texture) => texture == null
		? "none"
		: $"#{texture.GetInstanceID()}('{texture.name}',{texture.width}x{texture.height})";

	private static string FormatFloat(float value) => float.IsNaN(value)
		? "n/a"
		: value.ToString("0.###", CultureInfo.InvariantCulture);

	private static string UvRange(List<Vector2> uvs)
	{
		if (uvs == null || uvs.Count == 0) return "empty";
		float minX = uvs.Min(item => item.x);
		float minY = uvs.Min(item => item.y);
		float maxX = uvs.Max(item => item.x);
		float maxY = uvs.Max(item => item.y);
		return $"({FormatFloat(minX)},{FormatFloat(minY)})-({FormatFloat(maxX)},{FormatFloat(maxY)})";
	}

	private static string DescribeSamples(Color32[] pixels, int width, int height, Vector2[] uvs,
		out int loggedCount, out int nonZeroSamples, out ulong peakEnergyTotal)
	{
		loggedCount = 0;
		nonZeroSamples = 0;
		peakEnergyTotal = 0;
		if (uvs == null || uvs.Length == 0) return "none";
		StringBuilder builder = new StringBuilder();
		int count = Math.Min(MaximumLoggedSamplesPerGroup, uvs.Length);
		loggedCount = count;
		for (int index = 0; index < count; index++)
		{
			if (index > 0) builder.Append(';');
			Vector2 uv = uvs[index];
			int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(uv.x) * (width - 1)), 0, width - 1);
			int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(uv.y) * (height - 1)), 0, height - 1);
			Color32 direct = pixels[y * width + x];
			Color32 flipped = pixels[(height - 1 - y) * width + x];
			Color32 directPeak = PeakAround(pixels, width, height, x, y);
			Color32 flippedPeak = PeakAround(pixels, width, height, x, height - 1 - y);
			int peakEnergy = Math.Max(ColorEnergy(directPeak), ColorEnergy(flippedPeak));
			if (peakEnergy > 0) nonZeroSamples++;
			peakEnergyTotal += (ulong)peakEnergy;
			builder.Append($"{index}@({FormatFloat(uv.x)},{FormatFloat(uv.y)})=" +
				$"D{ColorLabel(direct)}/Dn{ColorLabel(directPeak)}/F{ColorLabel(flipped)}/Fn{ColorLabel(flippedPeak)}");
		}
		if (uvs.Length > count) builder.Append($";+{uvs.Length - count}");
		return builder.ToString();
	}

	private static Color32 PeakAround(Color32[] pixels, int width, int height, int centerX, int centerY)
	{
		Color32 best = default;
		int bestEnergy = -1;
		for (int y = Math.Max(0, centerY - 2); y <= Math.Min(height - 1, centerY + 2); y++)
			for (int x = Math.Max(0, centerX - 2); x <= Math.Min(width - 1, centerX + 2); x++)
			{
				Color32 candidate = pixels[y * width + x];
				int energy = candidate.r + candidate.g + candidate.b + candidate.a;
				if (energy <= bestEnergy) continue;
				bestEnergy = energy;
				best = candidate;
			}
		return best;
	}

	private static int ColorEnergy(Color32 color) => color.r + color.g + color.b + color.a;

	private static Dictionary<ulong, int> BuildTriangleSubmeshMap(Mesh mesh)
	{
		Dictionary<ulong, int> result = new Dictionary<ulong, int>();
		for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
		{
			int[] triangles = mesh.GetTriangles(submesh);
			for (int index = 0; index + 2 < triangles.Length; index += 3)
			{
				ulong key = TriangleKey(triangles[index], triangles[index + 1], triangles[index + 2]);
				if (!result.ContainsKey(key)) result.Add(key, submesh);
			}
		}
		return result;
	}

	private static ulong TriangleKey(int a, int b, int c)
	{
		if (a > b) (a, b) = (b, a);
		if (b > c) (b, c) = (c, b);
		if (a > b) (a, b) = (b, a);
		return ((ulong)(uint)a << 42) | ((ulong)(uint)b << 21) | (uint)c;
	}

	private static string ColorLabel(Color32 color) =>
		$"[{color.r},{color.g},{color.b},{color.a}]";

	private static ulong HashByte(ulong hash, byte value) => (hash ^ value) * 1099511628211UL;

	private static ulong HashInt32(ulong hash, int value)
	{
		unchecked
		{
			hash = HashByte(hash, (byte)value);
			hash = HashByte(hash, (byte)(value >> 8));
			hash = HashByte(hash, (byte)(value >> 16));
			return HashByte(hash, (byte)(value >> 24));
		}
	}

	private static ulong HashSingle(ulong hash, float value) =>
		HashInt32(hash, BitConverter.SingleToInt32Bits(value));

	private static string SignedDelta(ulong after, ulong before)
	{
		if (after >= before) return "+" + (after - before).ToString(CultureInfo.InvariantCulture);
		return "-" + (before - after).ToString(CultureInfo.InvariantCulture);
	}

	private static bool IsFinite(Vector2 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
		!float.IsNaN(value.y) && !float.IsInfinity(value.y);

	private static bool IsFinite(Vector4 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
		!float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
		!float.IsNaN(value.z) && !float.IsInfinity(value.z) &&
		!float.IsNaN(value.w) && !float.IsInfinity(value.w);
}
