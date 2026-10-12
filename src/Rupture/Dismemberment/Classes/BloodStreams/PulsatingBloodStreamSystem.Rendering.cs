using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Ragdolls.Classes;

namespace Rupture.Dismemberment.Classes.BloodStreams;

internal static partial class PulsatingBloodStreamSystem
{
	// The mesh/instancing owner only reads simulated emitter/history data. It
	// cannot end a logical bleed when graphical resources are released.
	private static class StreamRendering
	{
		private static readonly List<Vector3> MeshVertices = new List<Vector3>(16384);
		private static readonly List<Vector3> MeshNormals = new List<Vector3>(16384);
		private static readonly List<Vector2> MeshUvs = new List<Vector2>(16384);
		private static readonly List<Color32> MeshColors = new List<Color32>(16384);
		private static readonly List<int> MeshIndices = new List<int>(49152);
		private static GameObject _meshObject;
		private static readonly BloodParcelRenderer ParcelRenderer = new BloodParcelRenderer();
		private static bool _gpuParcels;
		private static Mesh _mesh;

		internal static void ReleaseParcels()
		{
			ParcelRenderer.Dispose();
			_gpuParcels = false;
		}

		internal static void DestroyMesh()
		{
			MeshVertices.Clear();
			MeshNormals.Clear();
			MeshUvs.Clear();
			MeshColors.Clear();
			MeshIndices.Clear();
			if (_meshObject != null) UnityEngine.Object.Destroy(_meshObject);
			if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
			_meshObject = null;
			_mesh = null;
		}

		internal static void RebuildMesh(float now)
		{
			Material material = BloodStreamMaterialProvider.Get();
			if (material == null) return;
			_gpuParcels = ParcelRenderer.Begin(material);
			int samplesVisited = 0;
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodMeshBuild))
			{
				MeshVertices.Clear();
				MeshNormals.Clear();
				MeshUvs.Clear();
				MeshColors.Clear();
				MeshIndices.Clear();
				for (int index = 0; index < Emitters.Length; index++)
				{
					StreamEmitter emitter = Emitters[index];
					if (!emitter.Active || !emitter.Visible) continue;
					samplesVisited += emitter.SampleCount;
					AddWoundParcels(index, emitter, now);
				}
			}
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodParcelSamplesVisited, samplesVisited);
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodParcelsInstanced, ParcelRenderer.Count);
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodParcelsCpuFallback, MeshVertices.Count / BloodParcelRenderer.VertexCount);
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodParcelUploadBytes, _gpuParcels ? ParcelRenderer.UploadedBytes : 0);
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodMeshVertices, MeshVertices.Count);
			ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.BloodMeshIndices, MeshIndices.Count);
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodParcelSubmit))
				if (_gpuParcels) ParcelRenderer.Submit(now);
			using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodMeshUpload))
			{
				if (MeshVertices.Count == 0)
				{
					if (_meshObject != null && _meshObject.activeSelf) { _mesh.Clear(); _meshObject.SetActive(false); }
					return;
				}
				EnsureMeshObject();
				if (_mesh == null) return;
				_mesh.Clear(keepVertexLayout: true);
				if (!_meshObject.activeSelf) _meshObject.SetActive(true);
				_mesh.SetVertices(MeshVertices);
				_mesh.SetNormals(MeshNormals);
				_mesh.SetUVs(0, MeshUvs);
				_mesh.SetColors(MeshColors);
				_mesh.SetTriangles(MeshIndices, 0, calculateBounds: false);
				_mesh.bounds = new Bounds(MeshVertices[0], Vector3.one * 2000f);
			}
		}

		private static void AddWoundParcels(int emitterIndex, StreamEmitter emitter, float now)
		{
			for (int logicalIndex = 0; logicalIndex < emitter.SampleCount; logicalIndex++)
			{
				StreamRingSample sample = GetStreamSample(emitterIndex, emitter, logicalIndex);
				if (sample.StartedAt <= now && sample.EndAt > now) AddWoundParcel(sample, now);
			}
		}

		private static void AddWoundParcel(StreamRingSample sample, float now)
		{
			if (_gpuParcels && ParcelRenderer.Add(sample.Origin, sample.Velocity, sample.Gravity, sample.StartedAt, sample.Radius, sample.Strength, sample.Seed)) return;
			float age = Mathf.Max(0f, now - sample.StartedAt);
			Vector3 center = PositionAt(sample.Origin, sample.Velocity, sample.Gravity, age);
			Vector3 instantaneousVelocity = sample.Velocity + sample.Gravity * age;
			float launchSpeed = Mathf.Max(0.01f, sample.Velocity.magnitude);
			float instantaneousSpeed = Mathf.Max(0.01f, instantaneousVelocity.magnitude);
			Vector3 forward = instantaneousVelocity / instantaneousSpeed;
			float referenceLength = Mathf.Max(sample.Radius * 2.4f,
				launchSpeed * StreamSampleInterval * WoundParcelLengthOverlap);
			float length = Mathf.Clamp(Mathf.Max(referenceLength,
				instantaneousSpeed * StreamSampleInterval * WoundParcelLengthOverlap),
				sample.Radius * 2.4f, MaximumWoundParcelLength);
			float thinning = Mathf.Clamp(Mathf.Sqrt(referenceLength / Mathf.Max(referenceLength, length)), 0.46f, 1.05f);
			float radius = sample.Radius * thinning;
			float halfLength = length * 0.5f;
			Vector3 tangent = Vector3.Cross(forward,
				Mathf.Abs(forward.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
			Vector3 bitangent = Vector3.Cross(forward, tangent).normalized;
			uint seed = sample.Seed ^ 0x27D4EB2Du;
			byte shade = (byte)Mathf.RoundToInt(Mathf.Lerp(188f, 242f, sample.Strength));
			Color32 color = new Color32(shade, shade, shade, 255);
			int firstRing = MeshVertices.Count;
			for (int ring = 0; ring < WoundParcelAxialRings; ring++)
			{
				float t = (ring + 1f) / (WoundParcelAxialRings + 1f);
				float axial = (t - 0.5f) * length;
				float profile = Mathf.Sin(t * Mathf.PI);
				for (int side = 0; side < WoundParcelSides; side++)
				{
					float angle = side * Mathf.PI * 2f / WoundParcelSides;
					float irregular = Mathf.Lerp(0.92f, 1.08f, Next01(ref seed));
					Vector3 radial = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
					Vector3 normal = (radial * (profile / Mathf.Max(radius, 0.00001f)) +
						forward * ((t * 2f - 1f) / Mathf.Max(halfLength, 0.00001f))).normalized;
					MeshVertices.Add(center + forward * axial + radial * radius * profile * irregular);
					MeshNormals.Add(normal);
					MeshUvs.Add(new Vector2(side / (float)WoundParcelSides, t));
					MeshColors.Add(color);
				}
			}
			for (int ring = 0; ring < WoundParcelAxialRings - 1; ring++)
			{
				int current = firstRing + ring * WoundParcelSides;
				int nextRing = current + WoundParcelSides;
				for (int side = 0; side < WoundParcelSides; side++)
				{
					int nextSide = (side + 1) % WoundParcelSides;
					MeshIndices.Add(current + side); MeshIndices.Add(nextRing + side); MeshIndices.Add(current + nextSide);
					MeshIndices.Add(current + nextSide); MeshIndices.Add(nextRing + side); MeshIndices.Add(nextRing + nextSide);
				}
			}
			int tail = MeshVertices.Count;
			MeshVertices.Add(center - forward * halfLength); MeshNormals.Add(-forward);
			MeshUvs.Add(new Vector2(0.5f, 0f)); MeshColors.Add(color);
			int head = MeshVertices.Count;
			MeshVertices.Add(center + forward * halfLength); MeshNormals.Add(forward);
			MeshUvs.Add(new Vector2(0.5f, 1f)); MeshColors.Add(color);
			int lastRing = firstRing + (WoundParcelAxialRings - 1) * WoundParcelSides;
			for (int side = 0; side < WoundParcelSides; side++)
			{
				int next = (side + 1) % WoundParcelSides;
				MeshIndices.Add(tail); MeshIndices.Add(firstRing + next); MeshIndices.Add(firstRing + side);
				MeshIndices.Add(head); MeshIndices.Add(lastRing + side); MeshIndices.Add(lastRing + next);
			}
		}

		private static void EnsureMeshObject()
		{
			if (_meshObject != null) return;
			Material material = BloodStreamMaterialProvider.Get();
			if (material == null) return;
			_meshObject = new GameObject("RPT Pulsating Blood Streams") { hideFlags = HideFlags.HideAndDontSave };
			_meshObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
			_meshObject.transform.localScale = Vector3.one;
			_mesh = new Mesh
			{
				name = "RPT Pulsating Blood Stream World Mesh",
				indexFormat = IndexFormat.UInt32,
				hideFlags = HideFlags.HideAndDontSave
			};
			_mesh.MarkDynamic();
			_meshObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
			MeshRenderer renderer = _meshObject.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = material;
			renderer.shadowCastingMode = ShadowCastingMode.Off;
			renderer.receiveShadows = true;
			_meshObject.SetActive(false);
		}

	}
}
