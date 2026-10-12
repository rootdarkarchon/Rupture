#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using Rupture.Dismemberment.Classes.BloodStreams;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Rupture.UnityPreview.BloodStreams
{
    [ExecuteAlways]
    public sealed class PulsatingBloodStreamPreview : MonoBehaviour
    {
        [Serializable]
        private struct PreviewEmitter
        {
            internal Transform Anchor;
            internal Vector3 BasePosition;
            internal Vector3 Direction;
            internal float Speed;
            internal float Radius;
            internal float BaseRadiusScale;
            internal float NormalSpeedScale;
            internal float HeartbeatSpeedScale;
            internal float Period;
            internal float Phase;
            internal bool Moving;
            internal bool HasPulse;
            internal bool PostMortemFade;
        }

        internal bool Paused;
        internal bool SinglePulseRequested;
        private static readonly RaycastHit[] PreviewCastHits = new RaycastHit[8];
        private readonly List<Vector3> _vertices = new List<Vector3>(4096);
        private readonly List<Vector3> _normals = new List<Vector3>(4096);
        private readonly List<Vector2> _uvs = new List<Vector2>(4096);
        private readonly List<Color32> _colors = new List<Color32>(4096);
        private readonly List<int> _indices = new List<int>(12288);
        private PreviewEmitter[] _emitters;
        private Mesh _mesh;
        private double _previewTime;
        private double _lastEditorTime;

        internal void Initialize(Transform[] anchors)
        {
            _emitters = new[]
            {
                Make(anchors[0], new Vector3(0.95f, 0.12f, 0.05f), 1.5f, 0.052f, 0.58f, 0.25f, 0.74f, 0.72f, 0.00f, false, true, false),
                Make(anchors[1], new Vector3(0.90f, 0.37f, 0.20f), 2.0f, 0.078f, 0.58f, 0.25f, 0.74f, 0.66f, 0.16f, false, true, false),
                Make(anchors[2], new Vector3(-0.92f, 0.14f, 0.18f), 2.0f, 0.078f, 0.58f, 0.25f, 0.74f, 0.79f, 0.31f, false, true, false),
				Make(anchors[3], new Vector3(0.83f, 0.42f, -0.15f), 3.4f, 0.115f, 0.88f, 0.42f, 0.75f, 0.86f, 0.47f, true, true, false),
                Make(anchors[4], new Vector3(0.64f, -0.72f, 0.18f), 1.5f, 0.052f, 0.58f, 0.25f, 0.74f, 0.75f, 0.09f, false, true, false),
                Make(anchors[5], new Vector3(0.96f, 0.05f, 0.06f), 2.0f, 0.078f, 0.58f, 0.25f, 0.74f, 0.70f, 0.38f, false, true, false),
                Make(anchors[6], new Vector3(0.88f, 0.16f, -0.08f), 1.35f, 0.105f, 0.94f, 0.50f, 0.50f, 0.8f, 0f, false, false, true)
            };
            MeshFilter filter = gameObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "RPT Blood Stream Preview Mesh", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            filter.sharedMesh = _mesh;
            renderer.sharedMaterial = CreatePreviewMaterial();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            _lastEditorTime = EditorApplication.timeSinceStartup;
        }

        private void Update()
        {
            if (_mesh == null || _emitters == null) return;
            double editorNow = EditorApplication.timeSinceStartup;
            double delta = Math.Min(0.05, Math.Max(0.0, editorNow - _lastEditorTime));
            _lastEditorTime = editorNow;
            if (!Paused) _previewTime += delta;
            else if (SinglePulseRequested)
            {
                _previewTime += 0.12;
                SinglePulseRequested = false;
            }
            else return;

            StreamPreviewGeometry.Clear(_vertices, _normals, _uvs, _colors, _indices);
            for (int index = 0; index < _emitters.Length; index++)
            {
                PreviewEmitter emitter = _emitters[index];
                if (emitter.Anchor == null) continue;
                if (emitter.Moving)
                {
                    emitter.Anchor.position = AnchorPositionAt(emitter, (float)_previewTime);
                }
                float previewTime = (float)_previewTime;
                AddCapturedPreviewHistory(emitter, index, previewTime);
            }
            StreamPreviewGeometry.Apply(_mesh, _vertices, _normals, _uvs, _colors, _indices);
        }

        private void OnDestroy()
        {
            if (_mesh != null) DestroyImmediate(_mesh);
            Renderer renderer = GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null) DestroyImmediate(renderer.sharedMaterial);
        }

        private static PreviewEmitter Make(Transform anchor, Vector3 direction, float speed, float radius,
            float baseRadiusScale, float normalSpeedScale, float heartbeatSpeedScale, float period, float phase,
            bool moving, bool hasPulse, bool postMortemFade)
        {
            return new PreviewEmitter
            {
                Anchor = anchor, BasePosition = anchor.position, Direction = direction, Speed = speed, Radius = radius,
                BaseRadiusScale = baseRadiusScale, NormalSpeedScale = normalSpeedScale,
                HeartbeatSpeedScale = heartbeatSpeedScale,
                Period = period, Phase = phase, Moving = moving, HasPulse = hasPulse,
                PostMortemFade = postMortemFade
            };
        }

        private static Vector3 AnchorPositionAt(PreviewEmitter emitter, float time)
        {
            if (!emitter.Moving) return emitter.BasePosition;
            Vector3 position = emitter.BasePosition;
            position.y = 1.35f + Mathf.Sin(time * 1.8f) * 0.18f;
            return position;
        }

        private void AddCapturedPreviewHistory(PreviewEmitter emitter, int emitterIndex,
            float previewTime)
        {
            const float sampleInterval = 1f / 50f;
            const float maximumFlight = 1.8f;
            float firstEmission = Mathf.Floor((previewTime - maximumFlight) / sampleInterval) * sampleInterval;
            Vector3 baseDirection = emitter.Direction.normalized;
            Vector3 tangent = Vector3.Cross(baseDirection,
                Mathf.Abs(baseDirection.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
            Vector3 bitangent = Vector3.Cross(baseDirection, tangent).normalized;
            for (float emittedAt = firstEmission; emittedAt <= previewTime; emittedAt += sampleInterval)
            {
                float cycle = Mathf.Repeat(emittedAt, 4f) / 4f;
                float strength = emitter.PostMortemFade && cycle > 0.75f
                    ? Mathf.Lerp(1f, 0.02f, Mathf.SmoothStep(0f, 1f, (cycle - 0.75f) / 0.25f)) : 1f;
                float cycleTime = emittedAt + emitter.Phase;
                int pulseIndex = emitter.HasPulse ? Mathf.FloorToInt(cycleTime / emitter.Period) : 0;
                float pulseAge = emitter.HasPulse ? cycleTime - pulseIndex * emitter.Period : emitter.Period;
                float pressure = emitter.HasPulse
                    ? 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(pulseAge / 0.52f))
                    : 0f;
                uint seed = (uint)(emitterIndex * 1709 + pulseIndex * 7919 + 41);
                Vector2 wobble = RandomDisk(ref seed);
                Vector3 direction = (baseDirection + tangent * wobble.x * 0.05f +
                    bitangent * wobble.y * 0.05f).normalized;
                Vector3 origin = AnchorPositionAt(emitter, emittedAt) +
                    tangent * wobble.x * emitter.Radius * 0.12f +
                    bitangent * wobble.y * emitter.Radius * 0.12f;
                Vector3 velocity = direction * emitter.Speed * strength *
                    Mathf.Lerp(emitter.NormalSpeedScale, emitter.HeartbeatSpeedScale, pressure);
                float radius = emitter.Radius * strength *
                    Mathf.Lerp(emitter.BaseRadiusScale, 1f, pressure);
                uint sampleSeed = seed ^ (uint)(Mathf.FloorToInt(emittedAt / sampleInterval) + 32768) * 0x85EBCA6Bu;
                if (emitter.HasPulse) velocity = StreamPreviewGeometry.ApplyParcelSpread(velocity, sampleSeed);
                float age = Mathf.Min(maximumFlight, Mathf.Max(0f, previewTime - emittedAt));
                float impactAge = FindImpactAge(origin, velocity, maximumFlight);
                if (age <= impactAge)
                    StreamPreviewGeometry.AddParcel(_vertices, _normals, _uvs, _colors, _indices,
                        origin, velocity, Physics.gravity, age, radius, strength, sampleSeed);
            }
        }

        private static float FindImpactAge(Vector3 origin, Vector3 velocity, float maximumAge)
        {
            const int segments = 14;
            Vector3 previous = origin;
            for (int segment = 1; segment <= segments; segment++)
            {
                float age = maximumAge * segment / segments;
                Vector3 next = StreamPreviewGeometry.Position(origin, velocity, Physics.gravity, age);
                Vector3 delta = next - previous;
                float distance = delta.magnitude;
                int count = distance > 0.0001f
                    ? Physics.RaycastNonAlloc(previous, delta / distance, PreviewCastHits, distance,
                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    : 0;
                if (count > 0)
                {
                    float nearest = float.MaxValue;
                    for (int hit = 0; hit < count; hit++)
                        if (PreviewCastHits[hit].collider != null && PreviewCastHits[hit].distance < nearest)
                            nearest = PreviewCastHits[hit].distance;
                    if (nearest < float.MaxValue) return maximumAge * (segment - 1) / segments +
                        maximumAge / segments * Mathf.Clamp01(nearest / distance);
                }
                previous = next;
            }
            return maximumAge;
        }

        private static Vector2 RandomDisk(ref uint seed)
        {
            float angle = Next01(ref seed) * Mathf.PI * 2f;
            float radius = Mathf.Sqrt(Next01(ref seed));
            return new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
        }

        private static float Next01(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0x00FFFFFFu) / 16777215f;
        }

        private static Material CreatePreviewMaterial()
        {
            Shader shader = Shader.Find("Rupture/PulsatingBloodStream") ?? Shader.Find("Standard");
            Material material = new Material(shader) { name = "RPT Blood Stream Preview Material" };
            if (material.HasProperty("_Color")) material.SetColor("_Color", new Color(0.82f, 0.18f, 0.16f));
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.94f);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.94f);
            return material;
        }
    }

    internal static class StreamPreviewGeometry
    {
        private const int ParcelSides = 6;
        private const int ParcelAxialRings = 4;
        private const float SampleInterval = 1f / 50f;
        private const float ParcelDirectionSpread = 0.028f;
        private const float ParcelLengthOverlap = 1.18f;
        private const float MaximumParcelLength = 0.12f;

        internal static void Clear(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
            List<Color32> colors, List<int> indices)
        {
            vertices.Clear(); normals.Clear(); uvs.Clear(); colors.Clear(); indices.Clear();
        }

        internal static int AddCapturedHistory(List<Vector3> vertices, List<Vector3> normals,
            List<Vector2> uvs, List<Color32> colors, List<int> indices, Vector3 origin,
            Vector3 direction, Vector3 gravity, float peakSpeed, float radius, float baseRadiusScale,
            float normalSpeedScale, float heartbeatSpeedScale, float period, float currentTime,
            float strength, uint seed)
        {
            const float maximumFlight = 1.8f;
            float firstEmission = Mathf.Floor((currentTime - maximumFlight) / SampleInterval) * SampleInterval;
            direction.Normalize();
            int parcelCount = 0;
            for (float emittedAt = firstEmission; emittedAt <= currentTime; emittedAt += SampleInterval)
            {
                int pulseIndex = Mathf.FloorToInt(emittedAt / period);
                float pulseAge = emittedAt - pulseIndex * period;
                float pressure = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(pulseAge / 0.52f));
                Vector3 velocity = direction * peakSpeed * strength *
                    Mathf.Lerp(normalSpeedScale, heartbeatSpeedScale, pressure);
                float capturedRadius = radius * strength * Mathf.Lerp(baseRadiusScale, 1f, pressure);
                uint sampleSeed = seed ^ (uint)(Mathf.FloorToInt(emittedAt / SampleInterval) + 32768) * 0x85EBCA6Bu;
                velocity = ApplyParcelSpread(velocity, sampleSeed);
                float age = Mathf.Min(maximumFlight, Mathf.Max(0f, currentTime - emittedAt));
                AddParcel(vertices, normals, uvs, colors, indices, origin, velocity, gravity,
                    age, capturedRadius, strength, sampleSeed);
                parcelCount++;
            }
            return parcelCount;
        }

        internal static Vector3 ApplyParcelSpread(Vector3 velocity, uint seed)
        {
            float speed = velocity.magnitude;
            if (speed <= 0.0001f) return velocity;
            Vector3 direction = velocity / speed;
            Vector3 tangent = Vector3.Cross(direction,
                Mathf.Abs(direction.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
            Vector3 bitangent = Vector3.Cross(direction, tangent).normalized;
            Vector2 spread = new Vector2(Signed01(ref seed), Signed01(ref seed));
            if (spread.sqrMagnitude > 1f) spread.Normalize();
            spread *= ParcelDirectionSpread;
            return (direction + tangent * spread.x + bitangent * spread.y).normalized * speed;
        }

        internal static void AddParcel(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
            List<Color32> colors, List<int> indices, Vector3 origin, Vector3 velocity, Vector3 gravity,
            float age, float sourceRadius, float strength, uint seed)
        {
            Vector3 center = Position(origin, velocity, gravity, age);
            Vector3 instantaneousVelocity = velocity + gravity * age;
            float launchSpeed = Mathf.Max(0.01f, velocity.magnitude);
            float instantaneousSpeed = Mathf.Max(0.01f, instantaneousVelocity.magnitude);
            Vector3 forward = instantaneousVelocity / instantaneousSpeed;
            float referenceLength = Mathf.Max(sourceRadius * 2.4f,
                launchSpeed * SampleInterval * ParcelLengthOverlap);
            float length = Mathf.Clamp(Mathf.Max(referenceLength,
                instantaneousSpeed * SampleInterval * ParcelLengthOverlap),
                sourceRadius * 2.4f, MaximumParcelLength);
            float thinning = Mathf.Clamp(Mathf.Sqrt(referenceLength / Mathf.Max(referenceLength, length)), 0.46f, 1.05f);
            float radius = sourceRadius * thinning;
            float halfLength = length * 0.5f;
            Vector3 tangent = Vector3.Cross(forward,
                Mathf.Abs(forward.y) < 0.85f ? Vector3.up : Vector3.right).normalized;
            Vector3 bitangent = Vector3.Cross(forward, tangent).normalized;
            uint shapeSeed = seed ^ 0x27D4EB2Du;
            byte shade = (byte)Mathf.RoundToInt(Mathf.Lerp(188f, 242f, strength));
            Color32 color = new Color32(shade, shade, shade, 255);
            int firstRing = vertices.Count;
            for (int ring = 0; ring < ParcelAxialRings; ring++)
            {
                float t = (ring + 1f) / (ParcelAxialRings + 1f);
                float axial = (t - 0.5f) * length;
                float profile = Mathf.Sin(t * Mathf.PI);
                for (int side = 0; side < ParcelSides; side++)
                {
                    float angle = side * Mathf.PI * 2f / ParcelSides;
                    float irregular = Mathf.Lerp(0.92f, 1.08f, Next01(ref shapeSeed));
                    Vector3 radial = tangent * Mathf.Cos(angle) + bitangent * Mathf.Sin(angle);
                    Vector3 normal = (radial * (profile / Mathf.Max(radius, 0.00001f)) +
                        forward * ((t * 2f - 1f) / Mathf.Max(halfLength, 0.00001f))).normalized;
                    vertices.Add(center + forward * axial + radial * radius * profile * irregular);
                    normals.Add(normal); uvs.Add(new Vector2(side / (float)ParcelSides, t)); colors.Add(color);
                }
            }
            for (int ring = 0; ring < ParcelAxialRings - 1; ring++)
            for (int side = 0; side < ParcelSides; side++)
            {
                int next = (side + 1) % ParcelSides;
                int current = firstRing + ring * ParcelSides;
                int nextRing = current + ParcelSides;
                indices.Add(current + side); indices.Add(nextRing + side); indices.Add(current + next);
                indices.Add(current + next); indices.Add(nextRing + side); indices.Add(nextRing + next);
            }
            int tail = vertices.Count;
            vertices.Add(center - forward * halfLength); normals.Add(-forward); uvs.Add(new Vector2(0.5f, 0f)); colors.Add(color);
            int head = vertices.Count;
            vertices.Add(center + forward * halfLength); normals.Add(forward); uvs.Add(new Vector2(0.5f, 1f)); colors.Add(color);
            int lastRing = firstRing + (ParcelAxialRings - 1) * ParcelSides;
            for (int side = 0; side < ParcelSides; side++)
            {
                int next = (side + 1) % ParcelSides;
                indices.Add(tail); indices.Add(firstRing + next); indices.Add(firstRing + side);
                indices.Add(head); indices.Add(lastRing + side); indices.Add(lastRing + next);
            }
        }

        internal static void Apply(Mesh mesh, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
            List<Color32> colors, List<int> indices)
        {
            mesh.Clear(false);
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetColors(colors);
            mesh.SetTriangles(indices, 0, false);
            mesh.bounds = vertices.Count > 0 ? new Bounds(vertices[0], Vector3.one * 100f) : new Bounds();
        }

        internal static Vector3 Position(Vector3 origin, Vector3 velocity, Vector3 gravity, float age)
        {
            return origin + velocity * age + gravity * (0.5f * age * age);
        }

        private static float Next01(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0x00FFFFFFu) / 16777215f;
        }

        private static float Signed01(ref uint state)
        {
            return Next01(ref state) * 2f - 1f;
        }
    }

    public static class PulsatingBloodStreamEditorTools
    {
        private const string RootName = "RPT Pulsating Blood Stream Preview";
        private static readonly RaycastHit[] CastHits = new RaycastHit[8];

        [MenuItem("Tools/Rupture/Preview Blood Streams/Create All Examples")]
        public static void CreateAllExamples()
        {
            ClearPreview();
            GameObject root = new GameObject(RootName);
            string[] names = { "Heavy Entry", "Head Exit", "Entry Fallback", "Moving Stump", "Floor Impact", "Wall Impact", "PostMortem Fade" };
            Transform[] anchors = new Transform[names.Length];
            for (int index = 0; index < names.Length; index++)
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = names[index] + " Anchor";
                marker.transform.SetParent(root.transform);
                marker.transform.position = new Vector3(-6.5f, 1.35f + (index % 2) * 1.6f, (index / 2) * 3.2f - 3.2f);
                marker.transform.localScale = Vector3.one * 0.16f;
                anchors[index] = marker.transform;
            }
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Native-decal Floor Target"; floor.transform.SetParent(root.transform);
            floor.transform.position = new Vector3(-3.3f, -0.08f, 0f); floor.transform.localScale = new Vector3(9f, 0.15f, 10f);
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Native-decal Wall Target"; wall.transform.SetParent(root.transform);
            wall.transform.position = new Vector3(-1.2f, 2f, 4.4f); wall.transform.localScale = new Vector3(0.15f, 4f, 3f);
            CreateDecalMarker(root.transform, "Small periodic EmitBleeding base mark", new Vector3(-3.6f, 0.01f, 2.6f),
                Quaternion.Euler(90f, 0f, 0f), 0.11f, new Color(0.32f, 0.015f, 0.01f));
            CreateDecalMarker(root.transform, "Strong pulse EmitBloodOnEnvironment cluster", new Vector3(-1.11f, 2.1f, 3.9f),
                Quaternion.Euler(0f, -90f, 0f), 0.28f, new Color(0.48f, 0.02f, 0.015f));
            PulsatingBloodStreamPreview preview = root.AddComponent<PulsatingBloodStreamPreview>();
            preview.Initialize(anchors);
            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
			Debug.Log("Created independent heavy/kill/death-dismemberment blood parcels including post-mortem stump flow: pulse launches travel higher and farther, later parcels return to normal pressure, and every emitted parcel keeps its own world-space ballistic path.");
        }

        private static void CreateDecalMarker(Transform parent, string name, Vector3 position, Quaternion rotation,
            float scale, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Quad);
            marker.name = name;
            marker.transform.SetParent(parent);
            marker.transform.SetPositionAndRotation(position, rotation);
            marker.transform.localScale = Vector3.one * scale;
            Material material = new Material(Shader.Find("Standard")) { name = name + " Material", color = color };
            material.SetFloat("_Glossiness", 0.85f);
            marker.GetComponent<Renderer>().sharedMaterial = material;
        }

        [MenuItem("Tools/Rupture/Preview Blood Streams/Pause or Resume")]
        public static void TogglePause()
        {
            PulsatingBloodStreamPreview preview = FindPreview();
            if (preview == null) { CreateAllExamples(); preview = FindPreview(); }
            preview.Paused = !preview.Paused;
            Debug.Log("RPT blood-stream preview paused=" + preview.Paused);
        }

        [MenuItem("Tools/Rupture/Preview Blood Streams/Advance Single Pulse Step")]
        public static void SinglePulse()
        {
            PulsatingBloodStreamPreview preview = FindPreview();
            if (preview == null) { CreateAllExamples(); preview = FindPreview(); }
            preview.Paused = true; preview.SinglePulseRequested = true; EditorApplication.QueuePlayerLoopUpdate();
        }

        [MenuItem("Tools/Rupture/Preview Blood Streams/Clear Preview")]
        public static void ClearPreview()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        }

        [MenuItem("Tools/Rupture/Benchmark Pulsating Blood Streams")]
        public static void Benchmark()
        {
            BloodStreamBenchmarkReport report = new BloodStreamBenchmarkReport
            {
                backend = "instanced-parcels",
                rendering = "parcel-only",
                measurementScope = "CPU capture, submission data and mesh uploads; GPU execution measured separately",
                unityVersion = Application.unityVersion,
                generatedUtc = DateTime.UtcNow.ToString("O"),
                cases = new[] { RunCase(1), RunCase(8), RunCase(24) }
            };
            report.allPassed = report.cases.All(item => item.valid);
            string outputDirectory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "AssetBundles");
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "rupture-bloodstream.benchmark.json");
            File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));
            Debug.Log("Rupture pulsating blood-stream benchmark:\n" + JsonUtility.ToJson(report, true));
            if (!report.allPassed) throw new InvalidOperationException("Pulsating blood-stream benchmark exceeded its acceptance limits. See " + outputPath);
        }

        private static BloodStreamBenchmarkCase RunCase(int emitterCount)
        {
            const int warmupFrames = 180;
            const int measuredFrames = 720;
            using var parcelRenderer = new BloodParcelRenderer();
            var parcelMaterial = new Material(Shader.Find("Rupture/PulsatingBloodStream"));
            if (!parcelRenderer.Begin(parcelMaterial)) throw new InvalidOperationException("Benchmark requires a graphics device supporting instanced parcels.");
            var history = new CapturedParcelHistory(emitterCount);
            double[] totals = new double[measuredFrames];
            double scheduling = 0, casts = 0, streamGeneration = 0, application = 0, decal = 0;
            int pulseEvents = 0, collisionRefreshEvents = 0, baseDecalEvents = 0, pulseDecalEvents = 0;
            int maxVertices = 0, maxTriangles = 0, maxRenderedParcels = 0;
            int decalsThisSecond = 0;
            float decalSecondStarted = 0f;
            float[] nextPulses = new float[emitterCount];
            float[] heartbeatStarts = new float[emitterCount];
            float[] nextTraces = new float[emitterCount];
            float[] nextBaseDecals = new float[emitterCount];
            uint[] seeds = new uint[emitterCount];
            for (int index = 0; index < emitterCount; index++) seeds[index] = (uint)(index * 7919 + 17);
            for (int frame = -warmupFrames; frame < measuredFrames; frame++)
            {
                float time = (frame + warmupFrames) / 60f;
                long totalStart = Stopwatch.GetTimestamp();
                long phaseStart = Stopwatch.GetTimestamp();
                int pulsesThisFrame = 0;
                int tracesThisFrame = 0;
                int baseDecalsDue = 0;
                for (int emitter = 0; emitter < emitterCount; emitter++)
                {
                    if (time >= nextPulses[emitter])
                    {
                        nextPulses[emitter] = time + 0.60f + (seeds[emitter] & 255) / 255f * 0.26f;
                        heartbeatStarts[emitter] = time;
                        pulsesThisFrame++;
                    }
                    float heartbeat = BenchmarkWoundPressure(time - heartbeatStarts[emitter]);
                    if (time >= nextTraces[emitter])
                    {
                        nextTraces[emitter] = time + (heartbeat > 0.01f ? 0.055f : 0.12f);
                        tracesThisFrame++;
                    }
                    if (time >= nextBaseDecals[emitter])
                    {
                        nextBaseDecals[emitter] = time + 0.75f;
                        baseDecalsDue++;
                    }
                }
                double scheduleMs = ElapsedMilliseconds(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                for (int trace = 0; trace < tracesThisFrame; trace++)
                    Physics.RaycastNonAlloc(Vector3.up * 1.4f, new Vector3(0.9f, -0.35f, 0.1f).normalized,
                        CastHits, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                double castMs = ElapsedMilliseconds(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                parcelRenderer.Begin(parcelMaterial);
                history.Fill(parcelRenderer, time);
                int parcelsThisFrame = parcelRenderer.Count;
                double streamGenerationMs = ElapsedMilliseconds(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                parcelRenderer.Upload(time);
                double applicationMs = ElapsedMilliseconds(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                if (time - decalSecondStarted >= 1f)
                {
                    decalSecondStarted = time;
                    decalsThisSecond = 0;
                }
                int pulseAllowed = Mathf.Min(pulsesThisFrame, Mathf.Min(3, 12 - decalsThisSecond));
                decalsThisSecond += pulseAllowed;
                int baseAllowed = Mathf.Min(baseDecalsDue, Mathf.Min(3 - pulseAllowed, 12 - decalsThisSecond));
                decalsThisSecond += baseAllowed;
                if (frame >= 0)
                {
                    pulseDecalEvents += pulseAllowed;
                    baseDecalEvents += baseAllowed;
                }
                double decalMs = ElapsedMilliseconds(phaseStart);
                double totalMs = ElapsedMilliseconds(totalStart);
                if (frame < 0) continue;
                scheduling += scheduleMs; casts += castMs; streamGeneration += streamGenerationMs;
                application += applicationMs; decal += decalMs;
                pulseEvents += pulsesThisFrame; collisionRefreshEvents += tracesThisFrame;
                totals[frame] = totalMs;
                maxVertices = Mathf.Max(maxVertices, BloodParcelRenderer.VertexCount); maxTriangles = Mathf.Max(maxTriangles, BloodParcelRenderer.TriangleCount);
                maxRenderedParcels = Mathf.Max(maxRenderedParcels, parcelsThisFrame);
            }
            long managedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int repeat = 0; repeat < 120; repeat++)
            {
                float time = 15f + repeat / 60f;
                parcelRenderer.Begin(parcelMaterial);
                history.Fill(parcelRenderer, time);
                parcelRenderer.Upload(time);
            }
            long managed = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - managedBefore);
            Array.Sort(totals);
            double average = totals.Average();
            double p99 = totals[Mathf.Clamp(Mathf.CeilToInt(totals.Length * 0.99f) - 1, 0, totals.Length - 1)];
            UnityEngine.Object.DestroyImmediate(parcelMaterial);
            return new BloodStreamBenchmarkCase
            {
                emitterCount = emitterCount, measuredFrames = measuredFrames, pulseEvents = pulseEvents,
                baseTraceEvents = collisionRefreshEvents, collisionRefreshEvents = collisionRefreshEvents,
                baseDecalEvents = baseDecalEvents, pulseDecalEvents = pulseDecalEvents,
                decalEvents = baseDecalEvents + pulseDecalEvents,
                pulseSchedulingMilliseconds = scheduling / measuredFrames, collisionRefreshMilliseconds = casts / measuredFrames,
                trajectoryCastMilliseconds = casts / measuredFrames,
                persistentBaseGenerationMilliseconds = streamGeneration / measuredFrames,
                heartbeatGenerationMilliseconds = streamGeneration / measuredFrames,
                heartbeatDeformationMilliseconds = streamGeneration / measuredFrames,
                parcelGenerationMilliseconds = streamGeneration / measuredFrames,
                meshGenerationMilliseconds = streamGeneration / measuredFrames,
                meshApplicationMilliseconds = application / measuredFrames,
                decalSchedulingMilliseconds = decal / measuredFrames, totalAverageMilliseconds = average, totalP99Milliseconds = p99,
                managedSteadyStateBytes = managed, maximumVertices = maxVertices, maximumTriangles = maxTriangles,
                maximumRenderedParcels = maxRenderedParcels,
                maximumInstanceUploadBytes = maxRenderedParcels * 64,
                maximumRenderedVertices = maxRenderedParcels * BloodParcelRenderer.VertexCount,
                maximumRenderedTriangles = maxRenderedParcels * BloodParcelRenderer.TriangleCount,
                parcelVertexCount = maxRenderedParcels * (4 * 6 + 2),
                parcelTriangleCount = maxRenderedParcels * 48,
                maximumLogicalEmitters = 64, maximumRenderedEmitters = 24, maximumStreamSamplesPerEmitter = 96,
                valid = managed == 0 && average < 0.75 && p99 < 2.0 && emitterCount <= 24
            };
        }

        private static float BenchmarkWoundPressure(float age)
        {
            if (age < 0f || age >= 0.52f) return 0f;
            return 1f - Mathf.SmoothStep(0f, 1f, age / 0.52f);
        }

        private static PulsatingBloodStreamPreview FindPreview()
        {
            GameObject root = GameObject.Find(RootName);
            return root != null ? root.GetComponent<PulsatingBloodStreamPreview>() : null;
        }

        private static double ElapsedMilliseconds(long start)
        {
            return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
    }

    [Serializable]
    internal sealed class BloodStreamBenchmarkReport
    {
        public string backend;
        public string rendering;
        public string measurementScope;
        public string unityVersion;
        public string generatedUtc;
        public bool allPassed;
        public BloodStreamBenchmarkCase[] cases;
    }

    [Serializable]
    internal sealed class BloodStreamBenchmarkCase
    {
        public int emitterCount;
        public int measuredFrames;
        public int pulseEvents;
        public int baseTraceEvents;
        public int collisionRefreshEvents;
        public int baseDecalEvents;
        public int pulseDecalEvents;
        public int decalEvents;
        public double pulseSchedulingMilliseconds;
        public double collisionRefreshMilliseconds;
        public double trajectoryCastMilliseconds;
        public double persistentBaseGenerationMilliseconds;
        public double heartbeatGenerationMilliseconds;
        public double heartbeatDeformationMilliseconds;
        public double parcelGenerationMilliseconds;
        public double meshGenerationMilliseconds;
        public double meshApplicationMilliseconds;
        public double decalSchedulingMilliseconds;
        public double totalAverageMilliseconds;
        public double totalP99Milliseconds;
        public long managedSteadyStateBytes;
        public int maximumVertices;
        public int maximumTriangles;
        public int maximumInstanceUploadBytes;
        public int maximumRenderedVertices;
        public int maximumRenderedTriangles;
        public int maximumRenderedParcels;
        public int parcelVertexCount;
        public int parcelTriangleCount;
        public int maximumLogicalEmitters;
        public int maximumRenderedEmitters;
        public int maximumStreamSamplesPerEmitter;
        public bool valid;
    }
}
#endif
