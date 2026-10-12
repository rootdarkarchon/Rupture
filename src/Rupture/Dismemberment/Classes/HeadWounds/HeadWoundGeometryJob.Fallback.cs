using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
internal sealed partial class HeadWoundGeometryJob
{
    internal const int MaximumGenerationAttempts = 6;
    private const int MaximumSources = 8;
    private const float MaximumFallbackSnap = 0.08f;
    internal int GenerationAttempts;
    internal Vector3 ResolvedDirection;
    internal bool UsedFallback;

    private sealed class Surface
    {
        internal HeadWoundGeometrySource Source;
        internal List<int> Triangles;
        internal int Index;
        internal Vector3 Nearest, Inward;
        internal bool HasNearest;
    }
    private sealed class Candidate
    {
        internal Surface Surface;
        internal Vector3 Entry, Exit;
        internal bool Terminal, Crater;
        internal int Priority;
        internal string Mode;
        internal float Snap;
    }

    protected override bool TryExecute(out string failure)
    {
        failure = "no readable LOD0 head resolved the wound trajectory";
        if (!IsFinite(Direction) || Direction.sqrMagnitude < 0.0001f || !IsFinite(SuppliedEntry) ||
            Previous == null || Previous.Length >= HeadWoundPolicy.MaximumWounds ||
            !FinitePositive(EntryRadius) || !FinitePositive(ExitRadius) || !FinitePositive(MaximumDepth)) return false;
        var surfaces = new List<Surface>();
        var candidates = new List<Candidate>();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < Math.Min(Sources.Length, MaximumSources); i++)
        {
            if (Cancelled) return false;
            var data = new ProceduralHeadWoundWorkspace();
            data.CopySource(Sources[i].Data);
            if (Sources[i].NeedsTransform) TransformSource(data, Sources[i].ToHead);
            var source = new HeadWoundGeometrySource { Data = data, BoneCount = Sources[i].BoneCount };
            if (!ValidateSource(source, out failure)) continue;
            var surface = new Surface { Source = source, Index = i, Triangles = new List<int>() };
            for (int submesh = 0; submesh < data.SubMeshCount; submesh++) surface.Triangles.AddRange(data.GetSourceSubmesh(submesh));
            surfaces.Add(surface);
            AddTrajectory(candidates, surface, SuppliedEntry, Direction, null, out failure);
        }
        RayMilliseconds = timer.Elapsed.TotalMilliseconds;
        if (TryCandidates(candidates, 2, false, out string reason)) return true;
        if (reason != null) failure = reason;
        if (Cancelled) return false;

        timer.Restart(); candidates.Clear();
        foreach (var surface in surfaces)
        {
            FindNearest(surface);
            if (!surface.HasNearest) continue;
            // Deterministic inward tilts; no random rotation or unrestricted snap.
            AddTrajectory(candidates, surface, surface.Nearest,
                Vector3.Slerp(Direction.normalized, surface.Inward, 0.35f), "tilted-35", out _);
            AddTrajectory(candidates, surface, surface.Nearest,
                Vector3.Slerp(Direction.normalized, surface.Inward, 0.7f), "tilted-70", out _);
        }
        RayMilliseconds += timer.Elapsed.TotalMilliseconds;
        if (TryCandidates(candidates, 2, true, out reason)) return true;
        if (reason != null) failure = reason;
        if (Cancelled) return false;

        timer.Restart(); candidates.Clear();
        foreach (var surface in surfaces)
            if (surface.HasNearest) AddTrajectory(candidates, surface, surface.Nearest, surface.Inward, "surface-normal", out _);
        RayMilliseconds += timer.Elapsed.TotalMilliseconds;
        if (TryCandidates(candidates, 1, true, out reason)) return true;
        if (reason != null) failure = reason;
        if (Cancelled) return false;

        candidates.Clear();
        foreach (var surface in surfaces)
        {
            if (!surface.HasNearest) continue;
            float depth = Mathf.Min(MaximumDepth, Mathf.Clamp(EntryRadius * 1.5f, 0.008f, 0.025f));
            candidates.Add(new Candidate { Surface = surface, Entry = surface.Nearest,
                Exit = surface.Nearest + surface.Inward * depth, Crater = true, Mode = "entry-crater",
                Snap = Vector3.Distance(SuppliedEntry, surface.Nearest) });
        }
        if (TryCandidates(candidates, 1, true, out reason)) return true;
        if (reason != null) failure = reason;
        return false;
    }

    private static bool FinitePositive(float value) => value > 0 && !float.IsInfinity(value) && !float.IsNaN(value);

    private void AddTrajectory(List<Candidate> candidates, Surface surface, Vector3 supplied, Vector3 direction,
        string mode, out string failure)
    {
        if (!TryResolveEntryExit(surface.Source.Data.SourceVertices, surface.Triangles, supplied, direction, MaximumDepth,
            out var entry, out var exit, out _, out string resolution, EntryRadius, ExitRadius, out bool terminal, out failure)) return;
        float snap = Vector3.Distance(SuppliedEntry, entry);
        if (mode != null && snap > MaximumFallbackSnap) return;
        candidates.Add(new Candidate { Surface = surface, Entry = entry, Exit = exit, Terminal = terminal,
            Mode = mode ?? resolution, Snap = snap, Priority = mode != null ? (mode == "tilted-35" ? 0 : 1) :
                resolution == "trajectory" ? 0 : resolution == "nearest" ? 1 : 2 });
    }

    private void FindNearest(Surface surface)
    {
        var vertices = surface.Source.Data.SourceVertices;
        var bounds = CalculateBounds(vertices);
        float limit = Mathf.Min(MaximumFallbackSnap, Mathf.Max(0.03f, bounds.extents.magnitude * 0.5f));
        float best = limit * limit;
        for (int i = 0; i + 2 < surface.Triangles.Count; i += 3)
        {
            var a = vertices[surface.Triangles[i]]; var b = vertices[surface.Triangles[i + 1]]; var c = vertices[surface.Triangles[i + 2]];
            var normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-14f) continue;
            var point = ClosestPointOnTriangle(SuppliedEntry, a, b, c);
            float distance = (point - SuppliedEntry).sqrMagnitude;
            if (distance > best) continue;
            normal.Normalize();
            if (Vector3.Dot(normal, bounds.center - point) < 0) normal = -normal;
            best = distance; surface.Nearest = point; surface.Inward = normal; surface.HasNearest = true;
        }
    }

    private bool TryCandidates(List<Candidate> candidates, int limit, bool fallback, out string failure)
    {
        failure = null;
        candidates.Sort((a, b) => {
            int order = a.Priority.CompareTo(b.Priority);
            if (order == 0) order = a.Snap.CompareTo(b.Snap);
            if (order == 0) order = (b.Exit - b.Entry).sqrMagnitude.CompareTo((a.Exit - a.Entry).sqrMagnitude);
            return order == 0 ? a.Surface.Index.CompareTo(b.Surface.Index) : order;
        });
        for (int i = 0; i < Math.Min(limit, candidates.Count) && GenerationAttempts < MaximumGenerationAttempts; i++)
        {
            if (Cancelled) return false;
            var candidate = candidates[i];
            Source = candidate.Surface.Source; SourceIndex = candidate.Surface.Index;
            Entry = candidate.Entry; Exit = candidate.Exit; ExitDistance = Vector3.Distance(Entry, Exit);
            ResolvedDirection = (Exit - Entry).normalized;
            TerminalSurfaceResolved = candidate.Terminal && !candidate.Crater;
            EntryResolution = candidate.Mode; UsedFallback = fallback;
            var bounds = CalculateBounds(Source.Data.SourceVertices);
            float maximum = Mathf.Clamp(bounds.extents.magnitude * 0.22f,
                EntryRadius * HeadWoundBallistics.MinimumExitMultiplier, HeadWoundBallistics.MaximumAbsoluteExitRadius);
            Descriptor = new ProceduralWoundDescriptor(Entry, Exit, EntryRadius,
                candidate.Crater ? EntryRadius * 0.35f : Mathf.Min(ExitRadius, maximum), Seed,
                throughShot: !candidate.Crater, generateExitLip: TerminalSurfaceResolved);
            var descriptors = new List<ProceduralWoundDescriptor>(Previous) { Descriptor };
            // Every attempt starts from immutable skin and the committed descriptors.
            Workspace = new ProceduralHeadWoundWorkspace(); Workspace.CopySource(Source.Data);
            GenerationAttempts++;
            var timer = Stopwatch.StartNew();
            bool generated = ProceduralHeadWoundGeometry.TryGenerateMultiple(Workspace, descriptors, out Metrics, out failure);
            GenerationMilliseconds += timer.Elapsed.TotalMilliseconds;
            if (!generated) continue;
            if (Cancelled) return false;
            timer.Restart();
            HeadBounds = CalculateBounds(Workspace.SourceVertices); CavityBounds = CalculateBounds(Workspace.CavityVertices);
            bool valid = descriptors.Count == 1
                ? ValidateResult(Workspace, HeadBounds, Source.Data.SourceVertices.Count, Metrics, Entry, ResolvedDirection,
                    ExitDistance, TerminalSurfaceResolved, out failure, Descriptor.ThroughShot)
                : ValidateMultipleResult(Workspace, HeadBounds, Source.Data.SourceVertices.Count, descriptors, out failure);
            ValidationMilliseconds += timer.Elapsed.TotalMilliseconds;
            if (valid) return true;
        }
        return false;
    }
}
}
