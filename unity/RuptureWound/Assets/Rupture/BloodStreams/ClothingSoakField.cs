using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.BloodStreams
{
// Mesh-space transport; UVs are only used when depositing the result. No UV-space
// neighbour lookup can jump between overlapping islands or bridge an open cuff.
public sealed class ClothingSoakTopology
{
    public readonly Vector3[] Rest;
    public readonly int[] Offsets, Neighbours;
    public readonly int[] SeamRepresentative;
    public readonly int[] SeamOffsets, SeamVertices;
    public ClothingSoakTopology(Vector3[] rest, Vector3[] normals, int[] triangles, int[] skinGroups)
    {
        if (rest.Length == 0 || rest.Length > 65536 || normals.Length != rest.Length || skinGroups.Length != rest.Length)
            throw new ArgumentException("Unsupported soaking vertex layout");
        Rest = rest;
        var links = new HashSet<int>[rest.Length];
        SeamRepresentative = new int[rest.Length];
        var seams = new Dictionary<Vector3Int, List<int>>();
        for (int i = 0; i < rest.Length; i++)
        {
            links[i] = new HashSet<int>(); SeamRepresentative[i] = i;
            var key = new Vector3Int(Mathf.RoundToInt(rest[i].x * 10000), Mathf.RoundToInt(rest[i].y * 10000), Mathf.RoundToInt(rest[i].z * 10000));
            if (!seams.TryGetValue(key, out var candidates)) seams.Add(key, candidates = new List<int>());
            foreach (int j in candidates)
                if (skinGroups[i] == skinGroups[j] && Vector3.Dot(normals[i], normals[j]) > 0.9f &&
                    (rest[i] - rest[j]).sqrMagnitude < 0.00000001f)
                { SeamRepresentative[i] = SeamRepresentative[j]; break; }
            candidates.Add(i);
        }
        for (int i = 0; i < triangles.Length; i += 3)
            for (int edge = 0; edge < 3; edge++)
            {
                int a = SeamRepresentative[triangles[i + edge]], b = SeamRepresentative[triangles[i + (edge + 1) % 3]];
                if (a == b) continue;
                links[a].Add(b); links[b].Add(a);
            }
        Offsets = new int[rest.Length + 1];
        for (int i = 0; i < rest.Length; i++) Offsets[i + 1] = Offsets[i] + links[SeamRepresentative[i]].Count;
        Neighbours = new int[Offsets[rest.Length]];
        for (int i = 0; i < rest.Length; i++) links[SeamRepresentative[i]].CopyTo(Neighbours, Offsets[i]);
        SeamOffsets = new int[rest.Length + 1];
        for (int i = 0; i < rest.Length; i++) SeamOffsets[SeamRepresentative[i] + 1]++;
        for (int i = 1; i < SeamOffsets.Length; i++) SeamOffsets[i] += SeamOffsets[i - 1];
        SeamVertices = new int[rest.Length];
        var writes = (int[])SeamOffsets.Clone();
        for (int i = 0; i < rest.Length; i++) SeamVertices[writes[SeamRepresentative[i]]++] = i;
    }
}

public sealed class ClothingSoakField
{
    public readonly ClothingSoakTopology Topology;
    public readonly float[] Saturation;
    private readonly float[] _next;
    private readonly int[] _frontier, _suppliers, _posed;
    private readonly bool[] _inFrontier, _dirty;
    private int _frontierCount, _posedCount;
    public readonly int[] ChangedVertices;
    public int ChangedCount { get; private set; }
    public int FrontierCount => _frontierCount;
    public int PosedCount => _posedCount;
    public int PosedVertex(int slot) => _posed[slot];
    public ClothingSoakField(ClothingSoakTopology topology)
    {
        Topology = topology; int count = topology.Rest.Length;
        Saturation = new float[count]; _next = new float[count];
        _frontier = new int[count]; _suppliers = new int[count]; _posed = new int[count];
        _inFrontier = new bool[count]; _dirty = new bool[count]; ChangedVertices = new int[count];
    }

    private void SetSaturation(int representative, float value)
    {
        for (int slot = Topology.SeamOffsets[representative]; slot < Topology.SeamOffsets[representative + 1]; slot++)
        {
            int i = Topology.SeamVertices[slot];
            if (value <= Saturation[i]) continue;
            Saturation[i] = value;
            if (!_dirty[i]) { _dirty[i] = true; ChangedVertices[ChangedCount++] = i; }
        }
    }

    private void Expand(int supplier)
    {
        for (int edge = Topology.Offsets[supplier]; edge < Topology.Offsets[supplier + 1]; edge++)
        {
            int i = Topology.Neighbours[edge];
            if (Saturation[i] >= 1f || _inFrontier[i]) continue;
            _inFrontier[i] = true; _frontier[_frontierCount++] = i;
        }
    }

    public void ClearChanges()
    {
        for (int i = 0; i < ChangedCount; i++) _dirty[ChangedVertices[i]] = false;
        ChangedCount = 0;
    }

    public void Seed(int vertex)
    {
        int representative = Topology.SeamRepresentative[vertex];
        SetSaturation(representative, 1f);
        Expand(representative);
    }

    // Seed a small connected patch, independent of the garment's vertex density.
    // Never seed by Euclidean proximity alone: that can wet a separate layer.
    public void SeedPatch(int vertex, Vector3[] posed, float radius)
    {
        Seed(vertex);
        var distances = new float[Saturation.Length];
        for (int i = 0; i < distances.Length; i++) distances[i] = float.PositiveInfinity;
        var queue = new Queue<int>();
        int start = Topology.SeamRepresentative[vertex]; distances[start] = 0; queue.Enqueue(start);
        while (queue.Count != 0)
        {
            int i = queue.Dequeue();
            for (int edge = Topology.Offsets[i]; edge < Topology.Offsets[i + 1]; edge++)
            {
                int j = Topology.Neighbours[edge];
                float distance = distances[i] + (posed[i] - posed[j]).magnitude;
                if (distance > radius || distance >= distances[j]) continue;
                distances[j] = distance; queue.Enqueue(j);
            }
        }
        for (int i = 0; i < Saturation.Length; i++)
            if (Topology.SeamRepresentative[i] == i && distances[i] <= radius) Seed(i);
    }

    public void MarkTransportVertices(bool[] used)
    {
        for (int slot = 0; slot < _posedCount; slot++) used[_posed[slot]] = false;
        _posedCount = 0;
        for (int slot = 0; slot < _frontierCount; slot++)
        {
            int i = _frontier[slot];
            if (Saturation[i] >= 1f) continue;
            for (int edge = Topology.Offsets[i]; edge < Topology.Offsets[i + 1]; edge++)
            {
                int j = Topology.Neighbours[edge];
                if (Saturation[j] < 0.999f) continue;
                if (!used[i]) { used[i] = true; _posed[_posedCount++] = i; }
                if (!used[j]) { used[j] = true; _posed[_posedCount++] = j; }
            }
        }
    }

    // Double buffering makes travel independent of triangle/vertex enumeration.
    // A stopped wound never calls Step; its existing deposited pixels are untouched.
    public bool Step(Vector3[] posed, Vector3 gravity, float seconds, float severity)
    {
        if (seconds <= 0 || severity <= 0) return false;
        bool changed = false;
        Vector3 down = gravity.normalized;
        float speed = 0.006f * severity * Mathf.Min(seconds, 0.2f);
        int count = _frontierCount;
        for (int slot = 0; slot < count; slot++)
        {
            int i = _frontier[slot];
            float value = Saturation[i];
            for (int edge = Topology.Offsets[i]; value < 1f && edge < Topology.Offsets[i + 1]; edge++)
            {
                int j = Topology.Neighbours[edge];
                // Saturated neighbours supply the advancing edge; dry islands cannot self-start.
                if (Saturation[j] < 0.999f) continue;
                Vector3 delta = posed[i] - posed[j];
                float length = delta.magnitude;
                float slope = length > 0.0001f ? Vector3.Dot(delta / length, down) : 0;
                float bias = slope >= 0 ? Mathf.Lerp(0.28f, 1f, slope) : Mathf.Lerp(0.28f, 0.06f, -slope);
                value = Mathf.Max(value, Mathf.Min(1f, Saturation[i] + speed * bias / Mathf.Max(0.002f, length)));
            }
            _next[i] = value;
        }
        int suppliers = 0, retained = 0;
        for (int slot = 0; slot < count; slot++)
        {
            int i = _frontier[slot]; float value = _next[i];
            if (value > Saturation[i])
            {
                if (Saturation[i] < 0.999f && value >= 0.999f) _suppliers[suppliers++] = i;
                changed = true; SetSaturation(i, value);
            }
            if (value < 1f) _frontier[retained++] = i;
            else _inFrontier[i] = false;
        }
        _frontierCount = retained;
        // New suppliers cannot influence the tick that saturated them.
        for (int slot = 0; slot < suppliers; slot++) Expand(_suppliers[slot]);
        return changed;
    }
}

// Reverse correspondence is prepared once; color work follows changed vertices.
public sealed class ClothingSoakColors
{
    private readonly Color[] _colors;
    private readonly int[] _offsets, _targets, _previousChanges;
    private int _previousCount;
    private bool _catchUp = true;
    private readonly bool[] _uploadDirty;
    public readonly int[] UploadVertices;
    public int UploadCount { get; private set; }
    public int Visits { get; private set; }
    public ClothingSoakColors(Color[] colors, int[] map, int sourceCount)
    {
        _colors = colors; _offsets = new int[sourceCount + 1];
        _uploadDirty = new bool[colors.Length]; UploadVertices = new int[colors.Length];
        _targets = new int[colors.Length]; _previousChanges = new int[colors.Length];
        for (int i = 0; i < colors.Length; i++) { int source = map == null ? i : map[i]; if (source >= 0) _offsets[source + 1]++; }
        for (int i = 1; i < _offsets.Length; i++) _offsets[i] += _offsets[i - 1];
        var writes = (int[])_offsets.Clone();
        for (int i = 0; i < colors.Length; i++) { int source = map == null ? i : map[i]; if (source >= 0) _targets[writes[source]++] = i; }
    }
    public bool Update(ClothingSoakField field)
    {
        Visits = _previousCount;
        // Retire last tick's delta before introducing this tick's delta. Otherwise
        // an unchanged triangle could repaint old texels during another draw.
        for (int i = 0; i < _previousCount; i++)
        {
            int target = _previousChanges[i]; Color color = _colors[target]; color.g = color.r; _colors[target] = color;
            MarkUpload(target);
        }
        _previousCount = 0;
        int count = _catchUp ? field.Saturation.Length : field.ChangedCount;
        for (int slot = 0; slot < count; slot++)
        {
            int source = _catchUp ? slot : field.ChangedVertices[slot]; float current = field.Saturation[source];
            for (int mapped = _offsets[source]; mapped < _offsets[source + 1]; mapped++)
            {
                Visits++; int target = _targets[mapped]; float previous = _colors[target].r;
                if (current <= previous) continue;
                _colors[target] = new Color(current, previous, 0, 1);
                MarkUpload(target);
                _previousChanges[_previousCount++] = target;
            }
        }
        _catchUp = false;
        return _previousCount != 0;
    }
    public void Invalidate() { _catchUp = true; }
    public void ClearUploads()
    {
        for (int i = 0; i < UploadCount; i++) _uploadDirty[UploadVertices[i]] = false;
        UploadCount = 0;
    }
    private void MarkUpload(int target)
    {
        if (_uploadDirty[target]) return;
        _uploadDirty[target] = true; UploadVertices[UploadCount++] = target;
    }
    public void RollbackPaint()
    {
        for (int i = 0; i < _previousCount; i++)
        {
            int target = _previousChanges[i]; Color color = _colors[target];
            color.r = color.g; _colors[target] = color;
        }
        _previousCount = 0; _catchUp = true;
    }
}
}
