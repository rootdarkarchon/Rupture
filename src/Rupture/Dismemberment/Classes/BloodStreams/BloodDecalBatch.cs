using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using Rupture.Ragdolls.Classes;
using UnityEngine;
using UnityEngine.Rendering;
using Work = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork;
using MeshData = DeferredDecals.DeferredDecalRenderer.ManagedMesh;

namespace Rupture.Dismemberment.Classes.BloodStreams;

// Keep native projector generation and ring insertion intact; coalesce only the
// upload at the end of our synchronous emission scope. Constructors still upload.
internal static class BloodDecalBatch
{
    private static Harmony _harmony;
    private static int _depth;
    private const int ProjectorVertices = 24, MaximumRanges = 32, MaximumPartialVertices = 768, VertexStride = 88;
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex { internal Vector3 Position, Normal; internal Vector4 Tangent, Uv0, Uv1, Uv2; }
    private struct Range { internal int Start, End; }
    private sealed class Pending
    {
        internal MeshData Data;
        internal readonly Range[] Ranges = new Range[MaximumRanges];
        internal int Count;
        internal bool Full;
        internal void Add(int start)
        {
            if (Full) return;
            if (start < 0 || start % ProjectorVertices != 0 || start > Data.Vertices.Length - ProjectorVertices)
            { Full = true; return; }
            int end = start + ProjectorVertices, first = 0;
            while (first < Count && Ranges[first].End < start) first++;
            int last = first;
            while (last < Count && Ranges[last].Start <= end)
            { start = Math.Min(start, Ranges[last].Start); end = Math.Max(end, Ranges[last].End); last++; }
            int removed = last - first;
            if (removed == 0 && Count == MaximumRanges) { Full = true; return; }
            Array.Copy(Ranges, last, Ranges, first + 1, Count - last);
            Ranges[first] = new Range { Start = start, End = end };
            Count += 1 - removed;
        }
    }
    // Reuse only bounded scratch/range storage, never retain a native mesh between scopes.
    private static readonly List<Pending> PendingMeshes = new List<Pending>(4);
    private static readonly Vertex[] Scratch = new Vertex[MaximumPartialVertices];
    private static readonly List<VertexAttributeDescriptor> Attributes = new List<VertexAttributeDescriptor>(6);
    private static int _pendingCount;
    private static bool _partialFailed;
    internal static void Install()
    {
        _harmony = new Harmony("com.rupture.blood-decal-batch");
        try
        {
            var target = AccessTools.DeclaredMethod(typeof(MeshData), nameof(MeshData.PasteProjectorIntoMiddle));
            if (target == null) throw new MissingMethodException("DeferredDecalMeshDataClass.PasteProjectorIntoMiddle");
            _harmony.Patch(target, transpiler: new HarmonyMethod(typeof(BloodDecalBatch), nameof(Transpile)));
        }
        catch (Exception exception)
        {
            _harmony.UnpatchSelf(); _harmony = null;
            RupturePlugin.Log.LogWarning("Blood decal batching unavailable; native uploads retained: " + exception.Message);
        }
    }
    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo original = AccessTools.DeclaredMethod(typeof(MeshData), nameof(MeshData.UpdateInnerMesh));
        MethodInfo replacement = AccessTools.DeclaredMethod(typeof(BloodDecalBatch), nameof(Upload));
        int matches = 0;
        for (int i = 0; i < code.Count; i++)
            if (code[i].Calls(original))
            {
                // The native receiver is already on the stack. Append startvIndex.
                // Copy metadata through Harmony rather than exposing its Framework Label
                // type to the netstandard build's Reflection.Emit facade.
                var argument = new CodeInstruction(code[i]) { opcode = OpCodes.Ldarg_1, operand = null };
                code[i] = new CodeInstruction(OpCodes.Call, replacement);
                code.Insert(i++, argument);
                matches++;
            }
        if (matches != 1) throw new InvalidOperationException("Expected exactly one native projector mesh upload, found " + matches);
        return code;
    }
    private static void Upload(MeshData data, int startvIndex)
    {
        if (_depth == 0) { data.UpdateInnerMesh(); return; }
        ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalDeferredUploads, 1);
        for (int i = 0; i < _pendingCount; i++)
            if (ReferenceEquals(PendingMeshes[i].Data, data)) { PendingMeshes[i].Add(startvIndex); return; }
        if (_pendingCount == PendingMeshes.Count) PendingMeshes.Add(new Pending());
        Pending pending = PendingMeshes[_pendingCount++];
        pending.Data = data; pending.Count = 0; pending.Full = false; pending.Add(startvIndex);
    }
    private static bool Compatible(MeshData data)
    {
        Mesh mesh = data.Mesh;
        int count = data.Vertices.Length;
        if (!mesh.isReadable || mesh.vertexCount != count || mesh.vertexBufferCount != 1 ||
            data.Normals.Length != count || data.Tangents.Length != count ||
            data.Uvs0.Count != count || data.Uvs1.Count != count || data.Uvs2.Count != count ||
            mesh.GetVertexBufferStride(0) != VertexStride) return false;
        Attributes.Clear(); mesh.GetVertexAttributes(Attributes);
        if (Attributes.Count != 6) return false;
        int offset = 0;
        for (int i = 0; i < Attributes.Count; i++)
        {
            VertexAttribute expected = i == 0 ? VertexAttribute.Position : i == 1 ? VertexAttribute.Normal :
                i == 2 ? VertexAttribute.Tangent : (VertexAttribute)((int)VertexAttribute.TexCoord0 + i - 3);
            var attribute = Attributes[i]; int dimension = i < 2 ? 3 : 4;
            if (attribute.attribute != expected || attribute.format != VertexAttributeFormat.Float32 ||
                attribute.dimension != dimension || attribute.stream != 0 || mesh.GetVertexAttributeOffset(expected) != offset) return false;
            offset += dimension * sizeof(float);
        }
        return true;
    }
    private static void Flush(Pending pending)
    {
        MeshData data = pending.Data;
        int vertices = 0;
        for (int i = 0; i < pending.Count; i++) vertices += pending.Ranges[i].End - pending.Ranges[i].Start;
        bool partial = !pending.Full && !_partialFailed && vertices <= MaximumPartialVertices && vertices <= data.Vertices.Length / 4;
        if (partial)
        {
            try
            {
                partial = Compatible(data);
                if (!partial) ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalLayoutFallbacks, 1);
                else
                {
                    Bounds bounds = data.Mesh.bounds;
                    for (int range = 0; range < pending.Count; range++)
                    {
                        Range dirty = pending.Ranges[range]; int count = dirty.End - dirty.Start;
                        for (int j = 0; j < count; j++)
                        {
                            int index = dirty.Start + j;
                            Scratch[j] = new Vertex { Position = data.Vertices[index], Normal = data.Normals[index],
                                Tangent = data.Tangents[index], Uv0 = data.Uvs0[index], Uv1 = data.Uvs1[index], Uv2 = data.Uvs2[index] };
                            bounds.Encapsulate(data.Vertices[index]);
                        }
                        data.Mesh.SetVertexBufferData(Scratch, 0, dirty.Start, count, 0, MeshUpdateFlags.DontRecalculateBounds);
                        ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalUploadRanges, 1);
                        RecordVolume(count);
                    }
                    data.Mesh.bounds = bounds;
                    ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalPartialUploads, 1);
                }
            }
            catch (Exception exception)
            {
                // Repair any partial writes from authoritative native arrays, and stop retrying
                // an unsupported upload path for this plugin lifetime.
                partial = false; _partialFailed = true;
                RupturePlugin.Log.LogWarning("Partial decal uploads unavailable; native uploads retained: " + exception.Message);
            }
        }
        if (!partial)
        {
            data.UpdateInnerMesh(); RecordVolume(data.Vertices.Length);
            ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalFullUploads, 1);
        }
        ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalUploads, 1);
    }
    private static void RecordVolume(int vertices)
    {
        ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalUploadVertices, vertices);
        ShadowRagdollWorld.RecordMainThreadWork(Work.BloodDecalUploadBytes, (long)vertices * VertexStride);
    }
    internal static Scope Begin() { _depth++; return new Scope(); }
    internal readonly struct Scope : IDisposable
    {
        public void Dispose()
        {
            if (--_depth != 0) return;
            try
            {
                using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.BloodDecalUpload))
                    for (int i = 0; i < _pendingCount; i++) Flush(PendingMeshes[i]);
            }
            finally { ClearPending(); }
        }
    }
    private static void ClearPending()
    {
        for (int i = 0; i < _pendingCount; i++) PendingMeshes[i].Data = null;
        _pendingCount = 0;
    }
    internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; ClearPending(); _depth = 0; _partialFailed = false; }
}
