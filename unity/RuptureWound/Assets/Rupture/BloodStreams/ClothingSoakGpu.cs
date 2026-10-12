using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.BloodStreams
{
    // GPU ownership is independent of the native accumulated decal texture.
    // No runtime readbacks, CPU saturation steps, or per-tick vertex uploads.
    internal static class ClothingSoakGpu
    {
        internal static readonly int Count = Shader.PropertyToID("_Count"), Skin = Shader.PropertyToID("_Skin"),
            Bones = Shader.PropertyToID("_Bones"), Posed = Shader.PropertyToID("_Posed"),
            Offsets = Shader.PropertyToID("_Offsets"), Neighbours = Shader.PropertyToID("_Neighbours"),
            Representatives = Shader.PropertyToID("_Representatives"), Current = Shader.PropertyToID("_Current"),
            Next = Shader.PropertyToID("_Next"), GravitySpeed = Shader.PropertyToID("_GravitySpeed"),
            PaintCurrent = Shader.PropertyToID("_SoakCurrent"), PaintMap = Shader.PropertyToID("_SoakMap"),
            PaintPrevious = Shader.PropertyToID("_SoakPrevious");
        internal static ComputeBuffer Buffer<T>(T[] data, int stride) where T : struct
        {
            var buffer = new ComputeBuffer(Math.Max(1, data.Length), stride);
            try { if (data.Length != 0) buffer.SetData(data); return buffer; }
            catch { buffer.Release(); throw; }
        }
        internal static GraphicsBuffer FieldBuffer(float[] data, GraphicsBuffer.Target copyTarget)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured | copyTarget, data.Length, 4);
            try { buffer.SetData(data); return buffer; }
            catch { buffer.Release(); throw; }
        }
        internal static void ReleaseShader(ComputeShader shader)
        {
            if (shader == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(shader);
            else UnityEngine.Object.DestroyImmediate(shader);
        }
    }

    internal sealed class ClothingSoakGpuGeometry : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct Vertex { public Vector4 Rest, Bones, Weights; }
        internal readonly ComputeShader Shader;
        internal readonly int Count, BoneCount, SkinKernel, TransportKernel;
        internal ComputeBuffer Skin, Bones, Posed, Offsets, Neighbours, Representatives;
        private uint _epoch;
        internal ClothingSoakGpuGeometry(ComputeShader shader, ClothingSoakTopology topology, BoneWeight[] weights, int boneCount)
        {
            Count = topology.Rest.Length; BoneCount = boneCount;
            SkinKernel = shader.FindKernel("Skin"); TransportKernel = shader.FindKernel("Transport");
            if (Count != weights.Length || BoneCount == 0) throw new ArgumentException("Unsupported GPU garment skin");
            try
            {
                // Each geometry owns immutable bindings. Sharing the asset would
                // let the last recorded garment overwrite earlier dispatch state.
                Shader = UnityEngine.Object.Instantiate(shader);
                var vertices = new Vertex[Count];
                for (int i = 0; i < Count; i++)
                {
                    BoneWeight w = weights[i];
                    if (w.boneIndex0 < 0 || w.boneIndex0 >= boneCount || w.boneIndex1 < 0 || w.boneIndex1 >= boneCount ||
                        w.boneIndex2 < 0 || w.boneIndex2 >= boneCount || w.boneIndex3 < 0 || w.boneIndex3 >= boneCount)
                        throw new ArgumentException("Garment bone index outside GPU palette");
                    vertices[i] = new Vertex { Rest = topology.Rest[i], Bones = new Vector4(w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3),
                        Weights = new Vector4(w.weight0,w.weight1,w.weight2,w.weight3) };
                }
                Skin = ClothingSoakGpu.Buffer(vertices, 48); Bones = new ComputeBuffer(boneCount, 64); Posed = new ComputeBuffer(Count, 16);
                Offsets = ClothingSoakGpu.Buffer(topology.Offsets, 4); Neighbours = ClothingSoakGpu.Buffer(topology.Neighbours, 4);
                Representatives = ClothingSoakGpu.Buffer(topology.SeamRepresentative, 4);
                Shader.SetInt(ClothingSoakGpu.Count, Count);
                Shader.SetBuffer(SkinKernel, ClothingSoakGpu.Skin, Skin);
                Shader.SetBuffer(SkinKernel, ClothingSoakGpu.Bones, Bones);
                Shader.SetBuffer(SkinKernel, ClothingSoakGpu.Posed, Posed);
            }
            catch { Dispose(); throw; }
        }
        internal bool RecordSkin(CommandBuffer commands, Matrix4x4[] matrices, uint epoch)
        {
            if (_epoch == epoch) return false;
            commands.SetBufferData(Bones, matrices);
            commands.DispatchCompute(Shader, SkinKernel, (Count + 63) / 64, 1, 1);
            _epoch = epoch; return true;
        }
        public void Dispose()
        {
            Skin?.Release(); Bones?.Release(); Posed?.Release(); Offsets?.Release(); Neighbours?.Release(); Representatives?.Release();
            Skin = Bones = Posed = Offsets = Neighbours = Representatives = null;
            ClothingSoakGpu.ReleaseShader(Shader);
        }
    }

    internal sealed class ClothingSoakGpuField : IDisposable
    {
        internal readonly ClothingSoakGpuGeometry Geometry;
        internal GraphicsBuffer Current, Next;
        internal GraphicsBuffer Pending { get; private set; }
        private readonly GraphicsBuffer _first;
        private ComputeShader _stepA, _stepB;
        internal ClothingSoakGpuField(ClothingSoakGpuGeometry geometry, float[] seed)
        {
            Geometry = geometry;
            try
            {
                Current = ClothingSoakGpu.FieldBuffer(seed, GraphicsBuffer.Target.CopySource); Next = ClothingSoakGpu.FieldBuffer(seed, GraphicsBuffer.Target.CopySource);
                _first = Current; _stepA = CreateStep(Current, Next); _stepB = CreateStep(Next, Current);
            }
            catch { Dispose(); throw; }
        }
        private ComputeShader CreateStep(GraphicsBuffer current, GraphicsBuffer next)
        {
            var g = Geometry; var shader = UnityEngine.Object.Instantiate(g.Shader); int kernel = g.TransportKernel;
            try
            {
                shader.SetInt(ClothingSoakGpu.Count, g.Count);
                shader.SetBuffer(kernel,ClothingSoakGpu.Posed,g.Posed);
                shader.SetBuffer(kernel,ClothingSoakGpu.Offsets,g.Offsets);
                shader.SetBuffer(kernel,ClothingSoakGpu.Neighbours,g.Neighbours);
                shader.SetBuffer(kernel,ClothingSoakGpu.Representatives,g.Representatives);
                shader.SetBuffer(kernel,ClothingSoakGpu.Current,current);
                shader.SetBuffer(kernel,ClothingSoakGpu.Next,next);
                return shader;
            }
            catch { ClothingSoakGpu.ReleaseShader(shader); throw; }
        }
        internal void RecordStep(CommandBuffer commands, Vector3 gravity, float seconds, float severity)
            => RecordSteps(commands, gravity, seconds, severity, 1);
        internal void RecordSteps(CommandBuffer commands, Vector3 gravity, float seconds, float severity, int steps)
        {
            if (steps < 1 || steps > 8) throw new ArgumentOutOfRangeException(nameof(steps));
            var g = Geometry; int kernel = g.TransportKernel;
            Vector3 down = gravity.normalized;
            var speed = new Vector4(down.x,down.y,down.z,0.006f * Math.Max(0,severity) * Mathf.Clamp(seconds / steps,0,0.2f));
            var input = Current;
            // Reuse one sampled pose for several small transport steps, then paint
            // once. A single large step would stall propagation at each mesh edge.
            for (int i = 0; i < steps; i++)
            {
                var shader = ReferenceEquals(input, _first) ? _stepA : _stepB;
                commands.SetComputeVectorParam(shader, ClothingSoakGpu.GravitySpeed, speed);
                commands.DispatchCompute(shader,kernel,(g.Count+63)/64,1,1);
                input = ReferenceEquals(input, Current) ? Next : Current;
            }
            Pending = input;
        }
        internal void CommitStep()
        {
            if (ReferenceEquals(Pending, Next)) { var old = Current; Current = Next; Next = old; }
            Pending = null;
        }
        public void Dispose()
        {
            Current?.Release(); Next?.Release(); Current = Next = null;
            ClothingSoakGpu.ReleaseShader(_stepA); ClothingSoakGpu.ReleaseShader(_stepB); _stepA = _stepB = null;
        }
    }

    internal sealed class ClothingSoakGpuTarget : IDisposable
    {
        internal readonly int Count;
        internal ComputeBuffer Map;
        internal GraphicsBuffer Previous;
        private readonly ClothingSoakGpuField _field;
        private readonly GraphicsBuffer _a;
        private readonly MaterialPropertyBlock _paintA = new MaterialPropertyBlock(), _paintB = new MaterialPropertyBlock();
        internal MaterialPropertyBlock PaintProperties => ReferenceEquals(_field.Pending ?? _field.Next,_a) ? _paintA : _paintB;
        internal ClothingSoakGpuTarget(ClothingSoakGpuField field, int[] map, int vertexCount)
        {
            _field = field; Count = vertexCount;
            if (map == null) { map = new int[vertexCount]; for (int i=0;i<map.Length;i++) map[i]=i; }
            try
            {
                Map = ClothingSoakGpu.Buffer(map,4);
                Previous = ClothingSoakGpu.FieldBuffer(new float[field.Geometry.Count], GraphicsBuffer.Target.CopyDestination);
                _a = field.Current;
                Bind(_paintA,field.Current); Bind(_paintB,field.Next);
            }
            catch { Dispose(); throw; }
        }
        private void Bind(MaterialPropertyBlock properties, GraphicsBuffer current)
        { properties.SetBuffer(ClothingSoakGpu.PaintCurrent,current); properties.SetBuffer(ClothingSoakGpu.PaintMap,Map); properties.SetBuffer(ClothingSoakGpu.PaintPrevious,Previous); }
        internal void ResetHistory() => Previous.SetData(new float[_field.Geometry.Count]); // Only when EFT replaces a paint destination.
        internal void RecordCommit(CommandBuffer commands)
        {
            // Keep history in source space. The vertex shader applies the same
            // map to both fields, so a GPU buffer copy replaces a compute dispatch
            // and its parameter bindings without changing per-target ownership.
            commands.CopyBuffer(_field.Pending ?? _field.Next, Previous);
        }
        public void Dispose() { Map?.Release(); Previous?.Release(); Map = null; Previous = null; }
    }
}
