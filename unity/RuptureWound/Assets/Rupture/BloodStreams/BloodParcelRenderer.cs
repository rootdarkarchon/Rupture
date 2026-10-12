using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Rupture.Dismemberment.Classes.BloodStreams
{
// One immutable 26-vertex/48-triangle mesh, with exactly the CPU parcel topology.
// Only captured emission state is uploaded; motion and shape are evaluated by the
// vertex shader. No resampling, dropped parcels, billboards or geometry LODs.
internal sealed class BloodParcelRenderer : IDisposable
{
    internal const int Capacity = 24 * 96;
    internal const int VertexCount = 26;
    internal const int TriangleCount = 48;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Parcel
    {
        internal Vector4 OriginTime;
        internal Vector4 VelocityRadius;
        internal Vector4 GravityStrength;
        internal uint Seed;
        internal uint Padding0, Padding1, Padding2;
    }
    private readonly Parcel[] _parcels = new Parcel[Capacity];
    private ComputeBuffer _buffer;
    private Mesh _mesh;
    private Material _material;
    private Material _source;
    private bool _failed;
    internal int Count { get; private set; }
    internal int UploadedBytes => Count * 64;
    internal bool Ready => !_failed && _buffer != null;
    private static readonly int BufferId = Shader.PropertyToID("_RPTParcels");
    private static readonly int TimeId = Shader.PropertyToID("_RPTParcelTime");

    internal bool Begin(Material material)
    {
        Count = 0;
        if (_failed || material == null || !material.HasProperty(TimeId) || !material.shader.isSupported ||
            !SystemInfo.supportsInstancing || SystemInfo.graphicsShaderLevel < 45) return false;
        try
        {
            if (_buffer == null)
            {
                _buffer = new ComputeBuffer(Capacity, 64, ComputeBufferType.Structured);
                _mesh = CreateTemplate();
            }
            if (_source != material || _material == null)
            {
                if (_material != null) UnityEngine.Object.Destroy(_material);
                _source = material;
                _material = new Material(material) { name = "RPT Instanced Blood Parcels", enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
                _material.SetBuffer(BufferId, _buffer);
            }
            return true;
        }
        catch (Exception exception)
        {
            Dispose(); _failed = true;
            Debug.LogWarning("RPT instanced parcels unavailable; retaining CPU geometry: " + exception.Message);
            return false;
        }
    }

    internal bool Add(Vector3 origin, Vector3 velocity, Vector3 gravity, float startedAt, float radius, float strength, uint seed)
    {
        if (!Ready || Count >= Capacity) return false;
        _parcels[Count++] = new Parcel
        {
            OriginTime = new Vector4(origin.x, origin.y, origin.z, startedAt),
            VelocityRadius = new Vector4(velocity.x, velocity.y, velocity.z, radius),
            GravityStrength = new Vector4(gravity.x, gravity.y, gravity.z, strength), Seed = seed
        };
        return true;
    }

    internal void Upload(float now)
    {
        if (!Ready || Count == 0) return;
        _buffer.SetData(_parcels, 0, 0, Count);
        _material.SetFloat(TimeId, now);
    }

    internal void Submit(float now, Camera camera = null)
    {
        if (!Ready || Count == 0) return;
        Upload(now);
        // Same conservative world-space bound as the CPU stream mesh; Unity still
        // performs normal camera/layer dispatch, and geometry casts no shadows.
        Vector4 origin = _parcels[0].OriginTime;
        Graphics.DrawMeshInstancedProcedural(_mesh, 0, _material,
            new Bounds(new Vector3(origin.x, origin.y, origin.z), Vector3.one * 2000f), Count,
            null, ShadowCastingMode.Off, true, 0, camera, LightProbeUsage.Off);
    }

    internal static Mesh CreateTemplate()
    {
        var vertices = new Vector3[VertexCount];
        var normals = new Vector3[VertexCount];
        var uv = new Vector2[VertexCount];
        var identity = new Vector2[VertexCount];
        var colors = new Color32[VertexCount];
        var triangles = new int[TriangleCount * 3];
        for (int ring = 0; ring < 4; ring++)
        for (int side = 0; side < 6; side++)
        {
            int index = ring * 6 + side;
            float t = (ring + 1f) / 5f, angle = side * Mathf.PI * 2f / 6f;
            vertices[index] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), t);
            normals[index] = Vector3.forward; uv[index] = new Vector2(side / 6f, t);
            identity[index] = new Vector2(index, 0); colors[index] = new Color32(255,255,255,255);
        }
        vertices[24] = Vector3.zero; vertices[25] = Vector3.forward;
        normals[24] = -Vector3.forward; normals[25] = Vector3.forward;
        uv[24] = new Vector2(0.5f,0); uv[25] = new Vector2(0.5f,1);
        identity[24] = new Vector2(0,-1); identity[25] = new Vector2(0,1);
        colors[24] = colors[25] = new Color32(255,255,255,255);
        int write = 0;
        for (int ring = 0; ring < 3; ring++)
        for (int side = 0; side < 6; side++)
        {
            int next = (side + 1) % 6, current = ring * 6, nextRing = current + 6;
            triangles[write++] = current + side; triangles[write++] = nextRing + side; triangles[write++] = current + next;
            triangles[write++] = current + next; triangles[write++] = nextRing + side; triangles[write++] = nextRing + next;
        }
        for (int side = 0; side < 6; side++)
        {
            int next = (side + 1) % 6;
            triangles[write++] = 24; triangles[write++] = next; triangles[write++] = side;
            triangles[write++] = 25; triangles[write++] = 18 + side; triangles[write++] = 18 + next;
        }
        var mesh = new Mesh { name = "RPT Immutable Blood Parcel", hideFlags = HideFlags.HideAndDontSave };
        mesh.vertices = vertices; mesh.normals = normals; mesh.uv = uv; mesh.uv2 = identity; mesh.colors32 = colors; mesh.triangles = triangles;
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 2000f);
        mesh.UploadMeshData(true);
        return mesh;
    }

    public void Dispose()
    {
        Count = 0;
        if (_buffer != null) _buffer.Release();
        if (_mesh != null) UnityEngine.Object.Destroy(_mesh);
        if (_material != null) UnityEngine.Object.Destroy(_material);
        _buffer = null; _mesh = null; _material = null; _source = null; _failed = false;
    }
}
}
