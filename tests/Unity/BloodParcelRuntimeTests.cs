#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using Rupture.UnityPreview.BloodStreams;

namespace Rupture.Dismemberment.Classes.BloodStreams
{
    internal static class BloodStreamMaterialProvider
    {
        internal static Material Current;
        internal static Material Get() => Current;
    }

    // Minimal simulation input. Rendering deliberately has no trigger-kind or
    // live-anchor dependency; all production emitter kinds share this path.
    internal static partial class PulsatingBloodStreamSystem
    {
        private sealed class StreamEmitter
        {
            internal bool Active, Visible;
            internal int SampleStart, SampleCount;
        }
        private static readonly StreamEmitter[] Emitters = new StreamEmitter[MaximumVisualEmitters];
        private static readonly StreamRingSample[] StreamSamples = new StreamRingSample[MaximumVisualEmitters * MaximumStreamSamplesPerEmitter];
        private static T RenderField<T>(string name) => (T)typeof(StreamRendering).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

        internal static long VerifyParcelRendering(Action<bool, string> check)
        {
            var fallback = new Material(Shader.Find("Standard"));
            var gpu = new Material(Shader.Find("Rupture/PulsatingBloodStream"));
            for (int i = 0; i < Emitters.Length; i++) Emitters[i] = new StreamEmitter();
            try
            {
                BloodStreamMaterialProvider.Current = gpu;
                StreamRendering.RebuildMesh(1f);
                check(RenderField<Mesh>("_mesh") == null, "Empty GPU path does not create a CPU mesh");
                for (int i = 0; i < MaximumRenderedEmitters; i++)
                {
                    Emitters[i].Active = Emitters[i].Visible = true;
                    Emitters[i].SampleCount = MaximumStreamSamplesPerEmitter;
                    Emitters[i].SampleStart = 93; // Exercise the wrapped history read.
                    for (int j = 0; j < MaximumStreamSamplesPerEmitter; j++)
                        StreamSamples[i * MaximumStreamSamplesPerEmitter + j] = new StreamRingSample
                        {
                            Origin = new Vector3(i, 2f, 0f), Velocity = new Vector3(1f, 0.2f, 0.1f),
                            Gravity = Physics.gravity, StartedAt = 0.5f, EndAt = 2f,
                            Radius = 0.0105f, Strength = 0.65f, Seed = (uint)(i * 96 + j + 1)
                        };
                }
                StreamRendering.RebuildMesh(1f);
                var parcels = RenderField<BloodParcelRenderer>("ParcelRenderer");
                check(RenderField<bool>("_gpuParcels") && parcels.Count == 2304, "All 24 wrapped histories fit the GPU parcel capacity");
                check(parcels.UploadedBytes == 147456 && RenderField<Mesh>("_mesh") == null, "GPU path uploads 64 bytes per parcel without a CPU stream mesh");
                check(!parcels.Add(Vector3.zero, Vector3.one, Physics.gravity, 0, 0.01f, 1, 1), "GPU capacity rejects overflow");
                for (int i = 0; i < 30; i++) StreamRendering.RebuildMesh(1f);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 120; i++) StreamRendering.RebuildMesh(1f);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                check(allocated == 0, "Production GPU gather/upload allocates zero managed bytes after warmup");

                BloodStreamMaterialProvider.Current = fallback;
                StreamRendering.RebuildMesh(1f);
                Mesh mesh = RenderField<Mesh>("_mesh");
                check(!RenderField<bool>("_gpuParcels") && mesh.vertexCount == 2304 * 26, "Unsupported material retains all parcels through CPU fallback");
                int[] triangles = mesh.triangles;
                check(triangles.Length == 2304 * 48 * 3, "CPU fallback preserves closed parcel topology at full capacity");
                bool disconnected = true;
                for (int i = 0; i < triangles.Length; i += 3)
                    disconnected &= triangles[i] / 26 == triangles[i + 1] / 26 && triangles[i] / 26 == triangles[i + 2] / 26;
                check(disconnected, "No triangles connect neighboring parcels");
                Vector3[] original = mesh.vertices;
                StreamRendering.RebuildMesh(1.1f);
                Vector3[] advanced = mesh.vertices;
                Vector3 velocity = StreamSamples[0].Velocity;
                Vector3 displacement = velocity * 0.1f + Physics.gravity * (0.5f * (0.6f * 0.6f - 0.5f * 0.5f));
                // End-cap midpoint is the exact ballistic center independent of stretch/shape.
                Vector3 oldCenter = (original[24] + original[25]) * 0.5f;
                Vector3 newCenter = (advanced[24] + advanced[25]) * 0.5f;
                check((newCenter - oldCenter - displacement).sqrMagnitude < 0.000001f, "CPU parcels advance along their captured ballistic trajectory");
                StreamRendering.RebuildMesh(2f);
                check(!RenderField<GameObject>("_meshObject").activeSelf && mesh.vertexCount == 0, "Expired histories clear and hide CPU geometry");
                for (int i = 1; i < MaximumRenderedEmitters; i++) Emitters[i].Visible = false;
                Emitters[0].SampleCount = 3;
                StreamSamples[93].StartedAt = 1.5f; // Future sample.
                StreamSamples[94].EndAt = 1f; // Expired sample.
                StreamRendering.RebuildMesh(1f);
                check(mesh.vertexCount == 26, "Future, expired and invisible histories do not render");
                Emitters[0].Active = false;
                StreamRendering.RebuildMesh(1f);
                check(mesh.vertexCount == 0, "Inactive emitter history does not render");
                Emitters[0].Active = true;
                BloodStreamMaterialProvider.Current = gpu;
                StreamRendering.RebuildMesh(1f);
                check(parcels.Count == 1 && !RenderField<GameObject>("_meshObject").activeSelf, "Restored GPU rendering leaves fallback geometry hidden");
                StreamRendering.ReleaseParcels();
                check(!parcels.Ready && parcels.Count == 0, "Teardown releases GPU resources and counters");
                return allocated;
            }
            finally
            {
                StreamRendering.ReleaseParcels(); StreamRendering.DestroyMesh();
                UnityEngine.Object.DestroyImmediate(fallback); UnityEngine.Object.DestroyImmediate(gpu);
                BloodStreamMaterialProvider.Current = null;
            }
        }
    }

    public static class BloodParcelRuntimeTests
    {
        [Serializable] private sealed class Report { public bool passed; public int checks; public long managedSteadyStateBytes; public string generatedUtc; }
        public static void RunBatch()
        {
            var report = new Report { generatedUtc = DateTime.UtcNow.ToString("O") };
            report.managedSteadyStateBytes = PulsatingBloodStreamSystem.VerifyParcelRendering((ok, name) =>
            {
                if (!ok) throw new Exception(name);
                report.checks++; Debug.Log("PASS: " + name);
            });
            BloodParcelVisualTests.RunBatch();
            PulsatingBloodStreamEditorTools.Benchmark();
            report.passed = true;
            File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json"), JsonUtility.ToJson(report, true));
        }
    }
}
#endif
