using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Systems.Effects;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Ragdolls.Classes;
using TraceStage = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadStage;
using TraceWork = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork;

namespace Rupture.Dismemberment.Classes.BloodStreams
{

    internal static class ClothingSoakingSystem
    {
        private const int MaximumWounds = 64;
        private const int MaximumTargets = 8;
        private const int MaximumVertices = 32768;
        private const float CellSize = 0.025f;
        private const float UpdateInterval = 0.5f;
        private const float BatchInterval = 0.05f;
        private const int FieldsPerBatch = 8;
        // Renderer/mesh lifetime owns immutable skin data. Wounds retain their
        // own saturation and paint history, so overlapping stains keep native order.
        private sealed class SkinGeometry
        {
            internal readonly SkinnedMeshRenderer Renderer;
            internal readonly Mesh Original;
            internal readonly Vector3[] Rest, Normals;
            internal readonly Vector2[] Uv;
            internal readonly BoneWeight[] Weights;
            internal readonly Transform[] Bones;
            internal readonly string[] BoneNames;
            internal readonly Matrix4x4[] BindPoses, SharedMatrices;
            internal readonly int[] Triangles, Groups;
            internal readonly Lazy<ClothingSoakTopology> Topology;
            internal uint MatrixEpoch;
            internal int References;
            internal Mesh PaintMesh;
            internal ClothingSoakGpuGeometry Gpu;
            internal SkinGeometry(SkinnedMeshRenderer renderer, Mesh original, Transform[] bones)
            {
                Renderer = renderer; Original = original;
                Rest = original.vertices; Normals = original.normals; Weights = original.boneWeights;
                BindPoses = original.bindposes; Bones = bones; Uv = original.uv; Triangles = original.triangles;
                if (Rest.Length == 0 || Normals.Length != Rest.Length || Weights.Length != Rest.Length ||
                    Uv.Length != Rest.Length || BindPoses.Length != Bones.Length)
                    throw new InvalidOperationException("unsupported garment skin/UV layout");
                BoneNames = new string[Bones.Length];
                for (int i = 0; i < Bones.Length; i++) BoneNames[i] = Bones[i] != null ? Bones[i].name : string.Empty;
                Groups = new int[Rest.Length];
                for (int i = 0; i < Groups.Length; i++) Groups[i] = SkinData.WeightHash(Weights[i]);
                SharedMatrices = new Matrix4x4[Bones.Length];
                Topology = new Lazy<ClothingSoakTopology>(() => new ClothingSoakTopology(Rest, Normals, Triangles, Groups),
                    System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            }
            internal void Capture(uint epoch)
            {
                if (MatrixEpoch == epoch) return;
                for (int i = 0; i < Bones.Length; i++) SharedMatrices[i] = Bones[i] != null
                    ? Bones[i].localToWorldMatrix * BindPoses[i] : Matrix4x4.zero;
                MatrixEpoch = epoch;
            }
        }
        private static readonly Dictionary<SkinnedMeshRenderer, SkinGeometry> Geometry = new Dictionary<SkinnedMeshRenderer, SkinGeometry>();
        private static uint _poseEpoch;
        private static SkinGeometry AcquireGeometry(SkinnedMeshRenderer renderer, Mesh original)
        {
            // A renderer can be rebound without replacing its mesh. This allocation
            // happens only on wound initialization, never in the steady-state tick.
            Transform[] bones = renderer.bones;
            bool matches = Geometry.TryGetValue(renderer, out var geometry) && geometry.Original == original && geometry.Bones.Length == bones.Length;
            if (matches) for (int i = 0; i < bones.Length; i++) if (!ReferenceEquals(bones[i], geometry.Bones[i])) { matches = false; break; }
            if (!matches)
            { geometry = new SkinGeometry(renderer, original, bones); Geometry[renderer] = geometry; }
            else ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGeometryCacheHits, 1);
            geometry.References++; return geometry;
        }
        private static void ReleaseGeometry(SkinGeometry geometry)
        {
            if (geometry == null || --geometry.References != 0) return;
            if (Geometry.TryGetValue(geometry.Renderer, out var current) && ReferenceEquals(current, geometry))
                Geometry.Remove(geometry.Renderer);
            geometry.Gpu?.Dispose(); geometry.Gpu = null;
            if (geometry.PaintMesh != null) UnityEngine.Object.Destroy(geometry.PaintMesh);
        }
        private sealed class SkinData : IDisposable
        {
            internal SkinnedMeshRenderer Renderer;
            private SkinGeometry _geometry;
            internal Mesh Original, PaintMesh;
            internal ClothingSoakTopology Topology;
            internal Vector3[] Rest, Normals, Posed, PosedNormals;
            internal BoneWeight[] Weights;
            internal Transform[] Bones;
            internal string[] BoneNames;
            internal Matrix4x4[] BindPoses, Matrices;
            internal ClothingSoakGpuTarget Gpu;
            internal RenderTexture LastDestination;
            internal int[] Map;
            internal int[] Triangles, Groups;
            internal SkinData(SkinnedMeshRenderer renderer, bool source)
            {
                Renderer = renderer; Original = renderer.sharedMesh;
                if (Original == null || !Original.isReadable || Original.vertexCount > MaximumVertices || Original.blendShapeCount != 0)
                    throw new InvalidOperationException("unreadable, oversized or blend-shaped garment");
                _geometry = AcquireGeometry(renderer, Original);
                try
                {
                    Rest = _geometry.Rest; Normals = _geometry.Normals; Weights = _geometry.Weights; BindPoses = _geometry.BindPoses;
                    Bones = _geometry.Bones; BoneNames = _geometry.BoneNames; Triangles = _geometry.Triangles; Groups = _geometry.Groups;
                    Matrices = new Matrix4x4[Bones.Length];
                    Posed = new Vector3[Rest.Length]; PosedNormals = new Vector3[Rest.Length];
                    if (_geometry.PaintMesh == null)
                        _geometry.PaintMesh = new Mesh { name = "RPT GPU clothing projection", hideFlags = HideFlags.HideAndDontSave,
                            indexFormat = Original.indexFormat, vertices = Rest, uv = _geometry.Uv, triangles = Triangles };
                    PaintMesh = _geometry.PaintMesh;
                }
                catch { Dispose(); throw; }
            }
            internal void CaptureMatrices(Dictionary<string, Transform> anatomicalBones = null, bool shared = false)
            {
                if (shared)
                {
                    _geometry.Capture(_poseEpoch); Matrices = _geometry.SharedMatrices;
                    return;
                }
                for (int i = 0; i < Bones.Length; i++)
                {
                    Transform bone = Bones[i];
                    if (bone != null && anatomicalBones != null) anatomicalBones.TryGetValue(BoneNames[i], out bone);
                    Matrices[i] = bone != null ? bone.localToWorldMatrix * BindPoses[i] : Matrix4x4.zero;
                }
            }
            internal void Pose(bool[] active = null)
            {
                for (int i = 0; i < Rest.Length; i++)
                {
                    if (active != null && !active[i]) continue;
                    BoneWeight w = Weights[i];
                    Posed[i] = Point(Rest[i], w); if (active == null) PosedNormals[i] = Normal(Normals[i], w).normalized;
                }
            }
            internal ClothingSoakTopology SharedTopology => _geometry.Topology.Value;
            internal ClothingSoakGpuGeometry GpuGeometry => _geometry.Gpu ??
                (_geometry.Gpu = new ClothingSoakGpuGeometry(_compute, SharedTopology, Weights, Bones.Length));
            private Vector3 Point(Vector3 p, BoneWeight w) => Matrices[w.boneIndex0].MultiplyPoint3x4(p) * w.weight0 +
                Matrices[w.boneIndex1].MultiplyPoint3x4(p) * w.weight1 + Matrices[w.boneIndex2].MultiplyPoint3x4(p) * w.weight2 + Matrices[w.boneIndex3].MultiplyPoint3x4(p) * w.weight3;
            private Vector3 Normal(Vector3 p, BoneWeight w) => Matrices[w.boneIndex0].MultiplyVector(p) * w.weight0 +
                Matrices[w.boneIndex1].MultiplyVector(p) * w.weight1 + Matrices[w.boneIndex2].MultiplyVector(p) * w.weight2 + Matrices[w.boneIndex3].MultiplyVector(p) * w.weight3;
            internal string DominantBone(int vertex)
            {
                BoneWeight w = Weights[vertex]; int bone = w.boneIndex0; float weight = w.weight0;
                if (w.weight1 > weight) { bone = w.boneIndex1; weight = w.weight1; }
                if (w.weight2 > weight) { bone = w.boneIndex2; weight = w.weight2; }
                if (w.weight3 > weight) bone = w.boneIndex3;
                return BoneNames[bone];
            }
            public void Dispose()
            {
                Gpu?.Dispose(); Gpu = null;
                ReleaseGeometry(_geometry); _geometry = null; PaintMesh = null;
            }
            internal static int WeightHash(BoneWeight w)
            {
                unchecked
                {
                    int h = w.boneIndex0; h = h * 397 ^ w.boneIndex1; h = h * 397 ^ w.boneIndex2; h = h * 397 ^ w.boneIndex3;
                    h = h * 397 ^ Mathf.RoundToInt(w.weight0 * 10000); h = h * 397 ^ Mathf.RoundToInt(w.weight1 * 10000);
                    h = h * 397 ^ Mathf.RoundToInt(w.weight2 * 10000); return h * 397 ^ Mathf.RoundToInt(w.weight3 * 10000);
                }
            }
        }
        private sealed class Wound : IDisposable
        {
            internal BloodStreamData Descriptor;
            internal Player Player;
            internal Transform Anchor;
            internal SkinData Source;
            internal readonly List<SkinData> Targets = new List<SkinData>();
            internal float[] Seed;
            internal ClothingSoakGpuField Gpu;
            internal bool Supplied, Failed;
            internal float NextTick, LastSupply, LastAdvance;
            internal bool Advanced;
            internal Task Preparation;
            internal readonly List<string> Failures = new List<string>();
            internal bool Retired;
            public void Dispose() { foreach (var target in Targets) target.Dispose(); Targets.Clear(); Gpu?.Dispose(); Gpu = null; Source = null; Seed = null; }
        }
        private static readonly Wound[] Wounds = new Wound[MaximumWounds];
        private static readonly List<Wound> Retiring = new List<Wound>();
        private static Material _paint;
        private static ComputeShader _compute;
        private static CommandBuffer _commands;
        private static Material _initializeTexture;
        private static Texture2D _clearStamp;
        private static readonly FieldInfo NativeMergeShader = typeof(TextureDecalsPainter).GetField("_blitShader", BindingFlags.Instance | BindingFlags.Public);
        private static int _warnings, _cursor;
        private static readonly Dictionary<uint, Wound> WoundsById = new Dictionary<uint, Wound>();
        private static readonly List<Wound> PendingSteps = new List<Wound>(FieldsPerBatch);
        private static float _nextBatch;
        private static readonly List<RenderTexture> ScratchTargets = new List<RenderTexture>(4);
        private static bool _paintBatchFailed;
        private static readonly UnityEngine.Profiling.CustomSampler GpuSample = UnityEngine.Profiling.CustomSampler.Create("RPT.GPU.ClothingSoak", true);
        private static readonly int BloodTexture = Shader.PropertyToID("_BloodTex");

        internal static void BeginFrame()
        {
            for (int i = Retiring.Count - 1; i >= 0; i--) if (Retiring[i].Preparation.IsCompleted) { Retiring[i].Dispose(); Retiring.RemoveAt(i); }
            foreach (var wound in Wounds) if (wound != null) wound.Supplied = false;
        }
        internal static void Supply(BloodStreamData descriptor, Player player, Transform anchor, float now = -1f)
        {
            if (now < 0f) now = Time.realtimeSinceStartup;
            if (WoundsById.TryGetValue(descriptor.StreamId, out var supplied))
            { supplied.Supplied = true; supplied.LastSupply = now; return; }
            int free = -1;
            for (int i = 0; i < Wounds.Length; i++)
            {
                Wound wound = Wounds[i];
                if (wound == null) { if (free < 0) free = i; continue; }
            }
            if (free < 0) return;
            Wounds[free] = new Wound
            {
                Descriptor = descriptor,
                Player = player,
                Anchor = anchor,
                Supplied = true,
                LastSupply = now,
                NextTick = now + (free % 6) * 0.016f
            };
            WoundsById.Add(descriptor.StreamId, Wounds[free]);
        }
        internal static void Stop(uint id)
        { for (int i = 0; i < Wounds.Length; i++) if (Wounds[i]?.Descriptor.StreamId == id) { Retire(Wounds[i]); Wounds[i] = null; } }
        private static void Retire(Wound wound)
        {
            WoundsById.Remove(wound.Descriptor.StreamId);
            wound.Retired = true;
            if (wound.Preparation != null && !wound.Preparation.IsCompleted) Retiring.Add(wound); else wound.Dispose();
        }

        internal static void Update(float now)
        {
            if (WoundsById.Count == 0 || now < _nextBatch) return;
            _nextBatch = now + BatchInterval;
            bool initialized = false; int advanced = 0;
            if (++_poseEpoch == 0) { _poseEpoch = 1; foreach (var geometry in Geometry.Values) geometry.MatrixEpoch = 0; }
            _commands?.Clear(); _paintBatchFailed = false;
            try
            {
            for (int offset = 0; offset < Wounds.Length; offset++)
            {
                int i = (_cursor + offset) % Wounds.Length; Wound wound = Wounds[i]; if (wound == null) continue;
                if (wound.Player == null || wound.Anchor == null || !wound.Player.HealthController.IsAlive || now - wound.LastSupply > 5f)
                { Retire(wound); Wounds[i] = null; continue; }
                if (!wound.Supplied || wound.Failed || now < wound.NextTick) continue;
                if (!EnsureMaterials()) return;
                try
                {
                    if (wound.Source == null)
                    {
                        if (initialized) continue; initialized = true;
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakInitialize)) Initialize(wound);
                        if (wound.Source == null) { wound.Failed = true; continue; }
                    }
                    if (wound.Preparation != null)
                    {
                        if (!wound.Preparation.IsCompleted) continue;
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakPreparationPublish))
                        {
                            wound.Preparation.GetAwaiter().GetResult(); wound.Preparation = null;
                            foreach (string failure in wound.Failures) Warn(failure); wound.Failures.Clear();
                            wound.Gpu = new ClothingSoakGpuField(wound.Source.GpuGeometry, wound.Seed);
                            foreach (var target in wound.Targets) target.Gpu = new ClothingSoakGpuTarget(wound.Gpu, target.Map, target.Rest.Length);
                            wound.Seed = null;
                        }
                    }
                    // Half-second visible updates, batched at most 20 times/sec.
                    // Bound overdue work after culling; never replay a long backlog.
                    if (advanced >= FieldsPerBatch) continue; advanced++;
                    if (wound.Source.Renderer == null || wound.Source.Renderer.sharedMesh != wound.Source.Original)
                    { wound.Dispose(); wound.Failed = true; continue; }
                    UnityEngine.Profiling.Profiler.BeginSample("RPT.Bleeding.ClothingSoak");
                    try
                    {
                        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakFields, 1);
                        if (PendingSteps.Count == 0) _commands.BeginSample(GpuSample);
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakCaptureMatrices)) wound.Source.CaptureMatrices(shared: true);
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakGpuSkin))
                            if (wound.Gpu.Geometry.RecordSkin(_commands, wound.Source.Matrices, _poseEpoch))
                            {
                                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuSkinVertices, wound.Source.Rest.Length);
                                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuBoneUploadBytes, wound.Source.Matrices.Length * 64);
                                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuDispatches, 1);
                            }
                        float elapsed = wound.Advanced ? Mathf.Clamp(now - wound.LastAdvance, 0.1f, 0.8f) : 0.1f;
                        int steps = Mathf.Clamp(Mathf.FloorToInt((elapsed + 0.0001f) / 0.1f), 1, 8);
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakGpuTransport))
                            wound.Gpu.RecordSteps(_commands, Physics.gravity, steps * 0.1f,
                                wound.Descriptor.TriggerKind == BloodStreamTriggerKind.HeavyWound ? 3f : 1f, steps);
                        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuTransportVertices, wound.Source.Rest.Length * steps);
                        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuDispatches, steps);
                        PendingSteps.Add(wound);
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakPaint))
                            foreach (var target in wound.Targets) Paint(target);
                        // Carry only the substep remainder; discard long culled
                        // intervals instead of growing a catch-up queue.
                        wound.LastAdvance = now - Mathf.Max(0f, elapsed - steps * 0.1f);
                    }
                    finally { UnityEngine.Profiling.Profiler.EndSample(); }
                    wound.Advanced = true;
                    wound.NextTick = now + UpdateInterval;
                }
                catch (Exception exception) { _paintBatchFailed = true; Warn(exception.Message); wound.Failed = true; }
            }
            }
            finally
            {
                FlushPaint();
                _cursor = (_cursor + FieldsPerBatch) % Wounds.Length;
            }
        }
        private static bool EnsureMaterials()
        {
            if (_paint != null) return true;
            if (!SystemInfo.supportsComputeShaders) { Warn("GPU clothing soaking is unsupported; existing stains are retained"); return false; }
            if (WoundResources.IsLoading || !Singleton<Effects>.Instantiated || Singleton<Effects>.Instance?.TexDecals == null) return false;
            Shader shader = WoundResources.GetShader("Rupture/ClothingSoak");
            if (shader == null || !shader.isSupported || shader.passCount < 2) { Warn("two-pass clothing accumulation shader is unavailable"); return false; }
            _compute = WoundResources.GetComputeShader("ClothingSoak");
            if (_compute == null) { Warn("GPU clothing transport shader is unavailable"); return false; }
            var native = NativeMergeShader?.GetValue(Singleton<Effects>.Instance.TexDecals) as Shader;
            if (native == null || !native.isSupported) { Warn("native clothing texture initialization shader is unavailable"); return false; }
            _clearStamp = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            _clearStamp.SetPixel(0, 0, Color.clear); _clearStamp.Apply(false, true);
            _initializeTexture = new Material(native);
            _initializeTexture.SetTexture("_ObjectDecalTexture", _clearStamp);
            _paint = new Material(shader); _paint.EnableKeyword("RPT_SOAK_GPU"); _commands = new CommandBuffer { name = "RPT clothing blood accumulation" };
            // Native composition consumes a straight-alpha stamp, just as the ordinary
            // painter does. Do not multiply blood RGB by opacity twice.
            _paint.SetInt("_SrcBlend", (int)BlendMode.One); _paint.SetInt("_DstBlend", (int)BlendMode.Zero);
            _paint.SetTexture(BloodTexture, Singleton<Effects>.Instance.TexDecals.GetTextureByType(EDecalTextureType.Blood)); return true;
        }
        private static void Initialize(Wound wound)
        {
            if (wound.Player.PlayerBody?.BodySkins == null) { Warn("player clothing is not ready"); return; }
            EBodyPart part = wound.Descriptor.BodyPart;
            if (part == EBodyPart.Head) return; // No fabric renderer is implied by a bare head wound.
            EBodyModelPart model = part == EBodyPart.LeftLeg || part == EBodyPart.RightLeg ? EBodyModelPart.Feet : EBodyModelPart.Body;
            if (!wound.Player.PlayerBody.BodySkins.TryGetValue(model, out var garment) || garment == null) { Warn("missing garment for " + model); return; }
            var renderers = new List<SkinnedMeshRenderer>();
            foreach (Renderer candidate in garment.GetRenderers()) if (candidate is SkinnedMeshRenderer skin && Compatible(skin)) renderers.Add(skin);
            SkinnedMeshRenderer source = null;
            foreach (var skin in renderers) if (skin.sharedMesh.isReadable && skin.sharedMesh.vertexCount <= MaximumVertices &&
                (source == null || skin.sharedMesh.vertexCount > source.sharedMesh.vertexCount)) source = skin;
            if (source == null) { Warn("no readable decal-compatible garment for " + model); return; }
            wound.Source = new SkinData(source, true); wound.Targets.Add(wound.Source); wound.Source.CaptureMatrices();
            Vector3 point = wound.Anchor.TransformPoint(wound.Descriptor.LocalPoint);
            var bones = new Dictionary<string, Transform>(); foreach (Transform bone in wound.Source.Bones) if (bone != null) bones[bone.name] = bone;
            if (wound.Player.IsYourPlayer && (part == EBodyPart.LeftArm || part == EBodyPart.RightArm) &&
                wound.Player.PlayerBody.BodySkins.TryGetValue(EBodyModelPart.Hands, out var hands) && hands != null)
                foreach (Renderer candidate in hands.GetRenderers()) if (candidate is SkinnedMeshRenderer skin && Compatible(skin) && !renderers.Contains(skin)) renderers.Add(skin);
            foreach (var renderer in renderers)
            {
                if (renderer == source) continue;
                if (wound.Targets.Count >= MaximumTargets) { Warn("garment LOD target budget reached"); break; }
                SkinData target = null;
                try
                {
                    target = new SkinData(renderer, false); target.CaptureMatrices(bones); target.Map = new int[target.Rest.Length];
                    wound.Targets.Add(target); target = null;
                }
                catch (Exception exception) { target?.Dispose(); Warn(exception.Message); }
            }
            // All Unity mesh/bone reads above happen on the main thread. The worker
            // owns plain arrays only; stop/despawn defers their disposal until it exits.
            wound.Preparation = Task.Run(() => Prepare(wound, point));
        }
        private static void Prepare(Wound wound, Vector3 point)
        {
            wound.Source.Pose();
            wound.Source.Topology = wound.Source.SharedTopology;
            float nearest = 0.18f * 0.18f; int seed = -1;
            for (int i = 0; i < wound.Source.Posed.Length; i++) { float distance = (wound.Source.Posed[i] - point).sqrMagnitude; if (distance < nearest) { nearest = distance; seed = i; } }
            if (seed < 0) throw new InvalidOperationException("wound does not reach the selected garment surface");
            var field = new ClothingSoakField(wound.Source.Topology); field.SeedPatch(seed, wound.Source.Posed, 0.018f); wound.Seed = field.Saturation;
            var cells = new Dictionary<(string, Vector3Int), List<int>>();
            for (int i = 0; i < wound.Source.Posed.Length; i++)
            { var key = (wound.Source.DominantBone(i), Cell(wound.Source.Posed[i])); if (!cells.TryGetValue(key, out var vertices)) cells.Add(key, vertices = new List<int>()); vertices.Add(i); }
            foreach (var target in wound.Targets)
            {
                if (target == wound.Source) continue;
                target.Pose(); int mapped = 0;
                for (int v = 0; v < target.Map.Length; v++)
                {
                    int match = -1; float distance = CellSize * CellSize; Vector3Int cell = Cell(target.Posed[v]); string bone = target.DominantBone(v);
                    for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                    {
                        if (!cells.TryGetValue((bone, cell + new Vector3Int(x, y, z)), out var candidates)) continue;
                        foreach (int candidate in candidates)
                        {
                            float d = (target.Posed[v] - wound.Source.Posed[candidate]).sqrMagnitude;
                            if (d < distance && Vector3.Dot(target.PosedNormals[v], wound.Source.PosedNormals[candidate]) > 0.5f) { distance = d; match = candidate; }
                        }
                    }
                    target.Map[v] = match; if (match >= 0) mapped++;
                }
                if (mapped < target.Map.Length * 0.9f) wound.Failures.Add("partial anatomical garment correspondence: " + mapped + "/" + target.Map.Length);
            }
        }
        private static bool Compatible(SkinnedMeshRenderer skin) => skin.sharedMesh != null && skin.sharedMaterial?.shader != null &&
            skin.sharedMaterial.shader.name.IndexOf("_Decal", StringComparison.Ordinal) >= 0;
        private static Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
        private static RenderTexture Scratch(RenderTexture destination)
        {
            foreach (var texture in ScratchTargets)
                if (texture.width == destination.width && texture.height == destination.height && texture.graphicsFormat == destination.graphicsFormat)
                    return texture;
            var descriptor = destination.descriptor;
            descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1; descriptor.useMipMap = false; descriptor.autoGenerateMips = false;
            var created = new RenderTexture(descriptor) { name = "RPT reusable clothing stamp", hideFlags = HideFlags.HideAndDontSave };
            if (!created.Create()) { UnityEngine.Object.Destroy(created); throw new InvalidOperationException("cannot allocate GPU clothing stamp"); }
            ScratchTargets.Add(created); ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuScratchAllocations, 1);
            return created;
        }
        private static void Paint(SkinData target)
        {
            if (target.Renderer == null || target.Renderer.sharedMesh != target.Original) return;
            ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakPaintTargets, 1);
            RenderTexture destination;
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakTextureRegistration))
            {
                if (!LimbDecalTextureTransfer.EnsureRegisteredTexture(target.Renderer, out destination, out string failure, out bool created))
                { Warn(failure); return; } // GPU paint history is not committed; next success catches up.
                if (created)
                {
                    var previous = RenderTexture.active;
                    try { Graphics.Blit(_clearStamp, destination, _initializeTexture); }
                    finally { RenderTexture.active = previous; }
                }
            }
            if (!ReferenceEquals(target.LastDestination, null) && !ReferenceEquals(target.LastDestination, destination)) target.Gpu.ResetHistory();
            target.LastDestination = destination;
            RenderTexture stamp;
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakRenderTargets)) stamp = Scratch(destination);
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakDrawSubmit))
            {
                _commands.SetRenderTarget(stamp); _commands.ClearRenderTarget(false, true, Color.clear);
                _commands.DrawMesh(target.PaintMesh, Matrix4x4.identity, _paint, 0, 0, target.Gpu.PaintProperties);
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakStampDraws, 1);
            }
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakCompositeSubmit))
            {
                _commands.SetRenderTarget(destination, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                _commands.Blit(stamp, destination, _paint, 1);
                target.Gpu.RecordCommit(_commands);
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakBlendDraws, 1);
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuHistoryCopies, 1);
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakGpuPaintVertices, target.Rest.Length);
            }
        }
        private static void FlushPaint()
        {
            try
            {
                if (PendingSteps.Count == 0) return;
                if (_paintBatchFailed)
                {
                    // Pending fields did not advance. Allow their elapsed-time
                    // accumulation to retry at the next bounded update.
                    foreach (var wound in PendingSteps) { wound.Advanced = false; wound.NextTick = 0f; }
                    return;
                }
                _commands.EndSample(GpuSample);
                using (ShadowRagdollWorld.TraceMainThread(TraceStage.SoakCommandSubmit))
                {
                    Graphics.ExecuteCommandBuffer(_commands);
                    ShadowRagdollWorld.RecordMainThreadWork(TraceWork.SoakCommandExecutions, 1);
                }
                foreach (var wound in PendingSteps) wound.Gpu.CommitStep();
            }
            catch (Exception exception)
            {
                // A partially executed GPU batch cannot be rolled back by the CPU.
                // Stop these fields explicitly rather than replaying uncertain paint.
                foreach (var wound in PendingSteps) wound.Failed = true;
                Warn("GPU clothing submission failed: " + exception.Message);
            }
            finally { PendingSteps.Clear(); _commands?.Clear(); }
        }
        private static void Warn(string reason) { if (_warnings++ < 12) RupturePlugin.Log.LogWarning("Clothing soaking: " + reason); }
        internal static void Reset()
        {
            for (int i = 0; i < Wounds.Length; i++) { if (Wounds[i] != null) Retire(Wounds[i]); Wounds[i] = null; }
            foreach (var wound in Retiring) { try { wound.Preparation.GetAwaiter().GetResult(); } catch (Exception) { } wound.Dispose(); }
            Retiring.Clear(); WoundsById.Clear(); Geometry.Clear();
            if (_paint != null) UnityEngine.Object.Destroy(_paint);
            if (_initializeTexture != null) UnityEngine.Object.Destroy(_initializeTexture);
            if (_clearStamp != null) UnityEngine.Object.Destroy(_clearStamp);
            _initializeTexture = null; _clearStamp = null; _compute = null;
            foreach (var texture in ScratchTargets) { texture.Release(); UnityEngine.Object.Destroy(texture); }
            ScratchTargets.Clear(); PendingSteps.Clear();
            _paint = null; _commands?.Release(); _commands = null; _warnings = 0; _cursor = 0; _nextBatch = 0f;
        }
    }
}
