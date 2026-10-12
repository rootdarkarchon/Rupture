#if UNITY_EDITOR
using System;
using System.Diagnostics;
using UnityEngine;
using Rupture.Dismemberment.Classes.BloodStreams;

public static class ClothingSoakSparseTests
{
    // Frozen pre-optimization transport, used as an independent numerical oracle.
    private static bool DenseStep(ClothingSoakTopology topology, float[] saturation, float[] next,
        Vector3[] posed, Vector3 gravity, float seconds, float severity)
    {
        if (seconds <= 0 || severity <= 0) return false;
        bool changed = false; Vector3 down = gravity.normalized;
        float speed = 0.006f * severity * Mathf.Min(seconds, 0.2f);
        for (int i = 0; i < saturation.Length; i++)
        {
            float value = saturation[i];
            for (int edge = topology.Offsets[i]; value < 1f && edge < topology.Offsets[i + 1]; edge++)
            {
                int j = topology.Neighbours[edge]; if (saturation[j] < 0.999f) continue;
                Vector3 delta = posed[i] - posed[j]; float length = delta.magnitude;
                float slope = length > 0.0001f ? Vector3.Dot(delta / length, down) : 0;
                float bias = slope >= 0 ? Mathf.Lerp(0.28f, 1f, slope) : Mathf.Lerp(0.28f, 0.06f, -slope);
                value = Mathf.Max(value, Mathf.Min(1f, saturation[i] + speed * bias / Mathf.Max(0.002f, length)));
            }
            next[i] = value;
        }
        for (int i = 0; i < saturation.Length; i++)
        {
            float value = next[topology.SeamRepresentative[i]];
            changed |= value > saturation[i]; saturation[i] = Mathf.Max(saturation[i], value);
        }
        return changed;
    }

    public static void Verify(ClothingSoakTopology topology)
    {
        var sparse = new ClothingSoakField(topology);
        var pose = (Vector3[])topology.Rest.Clone();
        sparse.SeedPatch(0, pose, .018f);
        var dense = (float[])sparse.Saturation.Clone(); var next = new float[dense.Length];
        var active = new bool[dense.Length]; var captured = new Vector3[dense.Length];
        var map = new int[dense.Length + 7];
        for (int i = 0; i < map.Length; i++) map[i] = i % 11 == 0 ? -1 : (i * 7) % dense.Length;
        var colors = new Color[map.Length]; var expected = new Color[map.Length]; var uploaded = new Color[map.Length];
        var paint = new ClothingSoakColors(colors, map, dense.Length);
        for (int tick = 0; tick < 1800; tick++)
        {
            if (tick == 200 || tick == 700)
            {
                int seed = tick % dense.Length; sparse.Seed(seed);
                for (int i = 0; i < dense.Length; i++)
                    if (topology.SeamRepresentative[i] == topology.SeamRepresentative[seed]) dense[i] = 1f;
            }
            Quaternion rotation = Quaternion.Euler(tick % 173, tick % 97, tick % 251);
            for (int i = 0; i < pose.Length; i++) pose[i] = rotation * topology.Rest[i];
            sparse.MarkTransportVertices(active);
            for (int i = 0; i < pose.Length; i++) captured[i] = active[i] ? pose[i] : new Vector3(float.NaN, 0, 0);
            float seconds = tick % 13 == 0 ? 0f : tick % 17 == 0 ? .3f : .1f;
            float severity = tick % 19 == 0 ? 0f : tick % 2 == 0 ? 3f : 1f;
            bool a = sparse.Step(captured, Vector3.down, seconds, severity);
            bool b = DenseStep(topology, dense, next, pose, Vector3.down, seconds, severity);
            if (a != b) throw new Exception("Sparse changed result differs at tick " + tick);
            for (int i = 0; i < dense.Length; i++)
                if (dense[i] != sparse.Saturation[i]) throw new Exception("Sparse saturation differs at " + tick + "/" + i);
            // Simulate a target that misses registrations, then catches up.
            if (tick % 31 == 0) paint.Invalidate();
            else
            {
                bool changed = false;
                for (int i = 0; i < map.Length; i++)
                {
                    float current = map[i] < 0 ? 0f : dense[map[i]], previous = expected[i].r;
                    changed |= current > previous; expected[i] = new Color(current, previous, 0, 1);
                }
                if (changed != paint.Update(sparse)) throw new Exception("Sparse paint change differs");
                if (changed)
                {
                    for (int i = 0; i < paint.UploadCount; i++) uploaded[paint.UploadVertices[i]] = colors[paint.UploadVertices[i]];
                    paint.ClearUploads();
                    for (int i = 0; i < colors.Length; i++)
                        if (uploaded[i].r != colors[i].r || uploaded[i].g != colors[i].g)
                            throw new Exception("Partial uploads lost a retired/new delta after skipped ticks");
                }
                for (int i = 0; i < colors.Length; i++)
                    if (colors[i].r != expected[i].r || colors[i].g != expected[i].g)
                        throw new Exception("Sparse paint history differs at " + tick + "/" + i);
            }
            sparse.ClearChanges();
        }
    }

    [Serializable] public sealed class Benchmark
    {
        public double denseMillisecondsPerTick, sparseMillisecondsPerTick;
        public int vertices, ticks, finalFrontier;
        public long sparseAllocatedBytes;
    }
    public static Benchmark Compare(ClothingSoakTopology topology)
    {
        const int ticks = 600;
        var field = new ClothingSoakField(topology); field.Seed(topology.Rest.Length / 2);
        var dense = (float[])field.Saturation.Clone(); var next = new float[dense.Length];
        // Warm both implementations before the paired workload.
        field.Step(topology.Rest, Vector3.down, .1f, 1);
        DenseStep(topology, dense, next, topology.Rest, Vector3.down, .1f, 1);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < ticks; i++) DenseStep(topology, dense, next, topology.Rest, Vector3.down, .1f, 1);
        watch.Stop(); double reference = watch.Elapsed.TotalMilliseconds;
        long before = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
        for (int i = 0; i < ticks; i++) { field.Step(topology.Rest, Vector3.down, .1f, 1); field.ClearChanges(); }
        watch.Stop(); long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        for (int i = 0; i < dense.Length; i++) if (dense[i] != field.Saturation[i]) throw new Exception("Benchmark transport differs");
        if (allocated != 0) throw new Exception("Sparse transport allocated");
        return new Benchmark { vertices = dense.Length, ticks = ticks, finalFrontier = field.FrontierCount,
            denseMillisecondsPerTick = reference / ticks, sparseMillisecondsPerTick = watch.Elapsed.TotalMilliseconds / ticks,
            sparseAllocatedBytes = allocated };
    }
}
#endif
