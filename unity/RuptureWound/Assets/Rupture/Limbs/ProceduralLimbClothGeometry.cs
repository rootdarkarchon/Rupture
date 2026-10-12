using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Limbs
{
internal static partial class ProceduralLimbWoundGeometry
{
    internal sealed class FoldDiagnostic
    {
        public string Status, Reason;
        public string Face;
        public int CutIndex;
        public float DiameterWorld, ThresholdWorld, MajorWorld, MinorWorld, DepthWorld, ContainmentScale = 1f;
        public float RimMovementScale = 1f;
    }

    // Alpha zero is reserved for cloth on GENERATED wound vertices. All legacy
    // tissue/bone masks have positive wetness; source garment colors are untouched.
    private static Color ClothMask => new Color(0.7f, 0f, 0.85f, 0f);

    internal static bool TryGenerate(Request request, out Surface surface, out string failure)
    {
        if (request?.Clothing == null)
            return TryGenerateTissue(request, out surface, out failure);
        var diagnostic = new FoldDiagnostic { Status = "unchanged" };
        if (TryGenerateCloth(request, diagnostic, out surface))
        { failure = null; surface.Fold = diagnostic; return true; }
        bool success = TryGenerateTissue(request, out surface, out failure);
        if (surface != null)
        {
            surface.Fold = diagnostic;
            if (request.AddAnatomicalDetails)
                surface.ClothCore = new LimbClothingProfiles.Core { KeepPrimaryUnchanged = true };
        }
        return success;
    }

    private static bool FoldFallback(FoldDiagnostic diagnostic, string reason)
    { diagnostic.Status = "fallback"; diagnostic.Reason = reason; return false; }

    private static bool TryGenerateCloth(Request request, FoldDiagnostic diagnostic, out Surface surface)
    {
        surface = null;
        var context = request.Clothing;
        if (request.AddAnatomicalDetails && context.SharedCore?.KeepPrimaryUnchanged == true)
        { diagnostic.Status = "primary-lod-unchanged"; return false; }
        if (request.Boundary == null || request.Boundary.Length < 3 || request.WorldScale <= 0 ||
            !IsFinite(context.BoneCenter) || !IsFinite(context.SegmentLengthWorld) || context.SegmentLengthWorld <= 0 ||
            !IsFinite(request.FacingNormal) || request.FacingNormal.sqrMagnitude < 0.5f)
            return FoldFallback(diagnostic, "invalid cloth cut frame");
        // A lower LOD must not independently classify or resize an already
        // accepted core. Its coarser contour only supplies the outer cloth edge.
        if (context.SharedCore?.Interior != null)
        {
            Vector3 c = context.FromBone.MultiplyPoint3x4(context.SharedCore.Center);
            Vector3 u = context.FromBone.MultiplyVector(context.SharedCore.Major).normalized;
            Vector3 v = context.FromBone.MultiplyVector(context.SharedCore.Minor).normalized;
            if (request.AddAnatomicalDetails || ClothContains(request.Boundary.Select(p =>
                new Vector2(Vector3.Dot(p.Position-c,u),Vector3.Dot(p.Position-c,v))).ToArray(),Vector2.zero))
                return TrySharedInterior(request, diagnostic, out surface);
        }
        if (LimbClothingProfiles.IsBare(context, request.IsLeg, request.Level, request.GibSide))
        { diagnostic.Status = "bare-exception"; return false; }
        if (!LimbClothingProfiles.TryGet(request.IsLeg, request.Level, request.GibSide, context.Offset, out var profile))
            return FoldFallback(diagnostic, "no calibrated profile for this cut position");
        float scale = request.WorldScale;
        float anatomyScale = context.SegmentLengthWorld / profile.BoneLength;
        float diameterWorld = profile.Diameter * anatomyScale;
        diagnostic.ThresholdWorld = diameterWorld * LimbClothingProfiles.LooseThreshold;
        Vector3 normal = request.FacingNormal.normalized;
        Vector3 center = context.BoneCenter;
        BoundaryVertex[] outer = request.Boundary;
        int count = outer.Length;
        Vector3[] radial = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            if (!IsFinite(outer[i].Position)) return FoldFallback(diagnostic, "nonfinite exterior boundary");
            radial[i] = Vector3.ProjectOnPlane(outer[i].Position - center, normal);
        }
        float widestSquared = 0f;
        Vector3 axisU = Vector3.zero;
        for (int i = 0; i < count; i++)
            for (int j = i + 1; j < count; j++)
            {
                Vector3 delta = radial[i] - radial[j];
                if (delta.sqrMagnitude <= widestSquared) continue;
                widestSquared = delta.sqrMagnitude; axisU = delta.normalized;
            }
        diagnostic.DiameterWorld = Mathf.Sqrt(widestSquared) * scale;
        Vector3 axisV = Vector3.Cross(normal, axisU).normalized;
        Vector2[] polygon = radial.Select(p => new Vector2(Vector3.Dot(p, axisU), Vector3.Dot(p, axisV))).ToArray();
        if (!request.AddAnatomicalDetails && !ClothContains(polygon, Vector2.zero))
            return TryDarkClosure(request, diagnostic, out surface);
        if (diagnostic.DiameterWorld <= diagnostic.ThresholdWorld + 0.0000001f)
        { diagnostic.Status = "thin"; return false; }
        if (!ClothContains(polygon, Vector2.zero))
            return FoldFallback(diagnostic, "primary loop does not contain the bone");

        float major = diameterWorld * 0.5f / scale;
        float minor = Mathf.Min(profile.Minor, profile.Diameter) * anatomyScale * 0.5f / scale;
        bool shared = context.SharedCore != null && !context.SharedCore.KeepPrimaryUnchanged;
        if (shared)
        {
            Vector3 majorVector = context.FromBone.MultiplyVector(context.SharedCore.Major);
            Vector3 minorVector = context.FromBone.MultiplyVector(context.SharedCore.Minor);
            center = context.FromBone.MultiplyPoint3x4(context.SharedCore.Center);
            major = majorVector.magnitude; minor = minorVector.magnitude;
            axisU = majorVector.normalized; axisV = minorVector.normalized;
            for (int i = 0; i < count; i++) radial[i] = Vector3.ProjectOnPlane(outer[i].Position - center, normal);
            polygon = radial.Select(p => new Vector2(Vector3.Dot(p, axisU), Vector3.Dot(p, axisV))).ToArray();
        }
        // Fit a bone-centered ellipse inside every boundary edge, not merely
        // its vertices. This also protects small/off-center source meshes.
        float containment = 1f;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = new Vector2(polygon[i].x / major, polygon[i].y / minor);
            Vector2 b = new Vector2(polygon[(i + 1) % count].x / major, polygon[(i + 1) % count].y / minor);
            Vector2 edge = b - a;
            float t = edge.sqrMagnitude > 1e-12f ? Mathf.Clamp01(-Vector2.Dot(a, edge) / edge.sqrMagnitude) : 0;
            containment = Mathf.Min(containment, (a + edge * t).magnitude * 0.97f);
        }
        if (containment < 0.45f) return FoldFallback(diagnostic, "bone-centered ellipse would collapse inside this contour");
        if (shared && containment < .999f) return FoldFallback(diagnostic, "shared anatomical ellipse does not fit this layer");
        major *= containment; minor *= containment;
        diagnostic.ContainmentScale = containment;
        diagnostic.MajorWorld = major * 2f * scale;
        diagnostic.MinorWorld = minor * 2f * scale;
        float clearanceWorld = Mathf.Max(0f, (diagnostic.DiameterWorld - diameterWorld) * 0.5f);
        float depth = Mathf.Min(clearanceWorld * 0.35f, (request.IsLeg ? 0.018f : 0.012f) * anatomyScale) / scale;
        if (depth < 0.0001f) return FoldFallback(diagnostic, "insufficient cloth recess depth");
        diagnostic.DepthWorld = depth * scale;
        var folded = new BoundaryVertex[count];
        var inner = new BoundaryVertex[count];
        var recessed = new Vector3[count];
        float winding = 0f;
        for (int i = 0; i < count; i++)
        {
            Vector2 p = polygon[i], next = polygon[(i + 1) % count];
            float cross = p.x * next.y - p.y * next.x;
            // A radial connection cannot represent a non-star-shaped loop or
            // coincident angular seam vertices safely. Keep the accepted cut.
            if (Mathf.Abs(cross) < 1e-10f) return FoldFallback(diagnostic, "coincident radial seam directions");
            if (winding == 0) winding = Mathf.Sign(cross);
            else if (cross * winding < 0) return FoldFallback(diagnostic, "non-star-shaped cloth boundary");
            float angle = Mathf.Atan2(p.y / minor, p.x / major);
            // Smooth angular irregularity never exceeds the contained ellipse.
            float irregular = 0.985f + 0.012f * Mathf.Sin(angle * 5f + (request.Seed & 255) * 0.031f);
            Vector3 target = axisU * (Mathf.Cos(angle) * major * irregular) + axisV * (Mathf.Sin(angle) * minor * irregular);
            float clearance = Mathf.Max(0, radial[i].magnitude - target.magnitude);
            float movement = Mathf.Min(clearance * 0.10f, (request.IsLeg ? 0.008f : 0.005f) * anatomyScale / scale);
            folded[i] = new BoundaryVertex(outer[i].Position - radial[i].normalized * movement, outer[i].Normal, outer[i].Weight);
            inner[i] = new BoundaryVertex(center + target, normal, outer[i].Weight);
            // Follow the lowest local tear before returning to the nominal plane.
            // This keeps the lip folded inward even at a negative rim disturbance.
            float seamDepth = Vector3.Dot(folded[i].Position - center, normal);
            recessed[i] = center + target - normal * (depth + Mathf.Max(0, -seamDepth));
        }
        if (!request.AddAnatomicalDetails && shared && context.SharedCore.BackRing != null)
            return TrySharedClothLayer(request, diagnostic, folded, center, axisU, axisV, out surface);
        var innerRequest = new Request { Boundary = inner, FacingNormal = request.FacingNormal,
            StableCutAxis = request.StableCutAxis, WorldScale = scale, IsLeg = request.IsLeg,
            Level = request.Level, Seed = request.Seed, GibSide = request.GibSide,
            AddAnatomicalDetails = request.AddAnatomicalDetails, OmitApron = true, AnatomicalCenter = center };
        if (!TryGenerateTissue(innerRequest, out Surface tissue, out string failure))
            return FoldFallback(diagnostic, "inner flesh surface: " + failure);
        var builder = new Builder();
        int[] seam = new int[count], lip = new int[count], tubeStart = new int[count], tubeEnd = new int[count];
        for (int i = 0; i < count; i++)
        {
            Vector3 inward = -radial[i].normalized;
            seam[i] = builder.Add(new GeneratedVertex(folded[i].Position, (normal + inward).normalized,
                new Vector2((float)i / count, 0), ClothMask, folded[i].Weight, i, Feature.ClothLip));
            lip[i] = builder.Add(new GeneratedVertex(recessed[i], (normal + inward).normalized,
                new Vector2((float)i / count, 1), ClothMask, inner[i].Weight, i, Feature.ClothLip));
            tubeStart[i] = builder.Add(new GeneratedVertex(recessed[i], radial[i].normalized,
                new Vector2((float)i / count, 0), FleshMask(.88f, .55f, .55f, request.Seed, i + 5000), inner[i].Weight, i, Feature.LimbTube));
            tubeEnd[i] = builder.Add(new GeneratedVertex(inner[i].Position, radial[i].normalized,
                new Vector2((float)i / count, 1), FleshMask(.83f, .45f, .65f, request.Seed, i + 6000), inner[i].Weight, i, Feature.LimbTube));
        }
        // The lip faces out through the opening; the tube faces radially out.
        ConnectEqualRings(builder, seam, lip, normal);
        ConnectTubeRings(builder, tubeStart, tubeEnd, center - normal * depth, center);
        if (!ValidateFiniteAndNondegenerate(builder, out failure))
            return FoldFallback(diagnostic, "cloth bridge: " + failure);
        int tissueStart = builder.Vertices.Count;
        if (request.AddAnatomicalDetails)
        {
            builder.Vertices.AddRange(tissue.Vertices);
            builder.Triangles.AddRange(tissue.Triangles.Select(i => i + tissueStart));
        }
        else
        {
            // Additional surrounding clothing layers terminate at the same
            // anatomical ellipse; only the primary layer owns the flesh core.
            tissue.FleshVertexCount = 0;
        }
        tissue.Vertices = builder.Vertices.ToArray();
        tissue.Triangles = builder.Triangles.ToArray();
        tissue.ExactSeamVertexIndices = seam;
        tissue.ReplacementBoundary = folded;
        tissue.ClothCore = new LimbClothingProfiles.Core { Center = context.ToBone.MultiplyPoint3x4(center),
            Major = context.ToBone.MultiplyVector(axisU * major), Minor = context.ToBone.MultiplyVector(axisV * minor),
            BackRing = recessed.Select(p => context.ToBone.MultiplyPoint3x4(p)).ToArray() };
        tissue.DecalSamples = request.AddAnatomicalDetails ? BuildDecalSamples(folded, request.IsLeg ? 8 : 6) : Array.Empty<DecalSample>();
        tissue.FleshVertexCount += count * 2;
        diagnostic.Status = request.AddAnatomicalDetails ? "folded" : "cloth-layer";
        if (request.AddAnatomicalDetails)
        {
            // Store only tube/tissue, not the LOD-specific outer cloth strip.
            var interior = tissue.Copy();
            int[] kept = Enumerable.Range(0, tissue.Vertices.Length).Where(i => tissue.Vertices[i].Feature != Feature.ClothLip).ToArray();
            var map = kept.Select((old, index) => new { old, index }).ToDictionary(p => p.old, p => p.index);
            interior.Vertices = kept.Select(i => tissue.Vertices[i]).ToArray();
            var triangles = new List<int>();
            for (int i = 0; i < tissue.Triangles.Length; i += 3)
                if (map.ContainsKey(tissue.Triangles[i]) && map.ContainsKey(tissue.Triangles[i+1]) && map.ContainsKey(tissue.Triangles[i+2]))
                    for (int j = 0; j < 3; j++) triangles.Add(map[tissue.Triangles[i+j]]);
            interior.Triangles = triangles.ToArray();
            interior.ClothCore = null; interior.ReplacementBoundary = null; interior.Fold = null;
            tissue.ClothCore.Interior = interior;
            tissue.ClothCore.InteriorToBone = context.ToBone;
            tissue.ClothCore.BoneNames = context.BoneNames;
        }
        surface = tissue;
        return true;
    }

    private static bool TrySharedInterior(Request request, FoldDiagnostic diagnostic, out Surface surface)
    {
        surface = null;
        var core = request.Clothing.SharedCore;
        var template = core.Interior;
        var context = request.Clothing;
        Matrix4x4 transform = context.FromBone * core.InteriorToBone;
        // Cofactor normal transform stays managed on the geometry worker (Unity's
        // Matrix4x4.inverse invokes a native internal call).
        Vector3 x = transform.MultiplyVector(Vector3.right), y = transform.MultiplyVector(Vector3.up), z = transform.MultiplyVector(Vector3.forward);
        float handedness = Mathf.Sign(Vector3.Dot(x,Vector3.Cross(y,z)));
        Vector3 Normal(Vector3 n) => ((Vector3.Cross(y,z)*n.x + Vector3.Cross(z,x)*n.y + Vector3.Cross(x,y)*n.z)*handedness).normalized;
        Vector3 normal = request.FacingNormal.normalized;
        Vector3 center = context.FromBone.MultiplyPoint3x4(core.Center);
        var outer = request.Boundary;
        Vector3[] ring = core.BackRing.Select(context.FromBone.MultiplyPoint3x4).ToArray();
        // Keep polygon order, including concave sectors. Angular sorting was
        // unsuitable for coarse LOD contours that double back around the bone.
        float Winding(Vector3[] points)
        {
            float area = 0;
            for (int i = 0; i < points.Length; i++) area += Vector3.Dot(Vector3.Cross(points[i]-center, points[(i+1)%points.Length]-center), normal);
            return area;
        }
        if (Winding(ring) * Winding(outer.Select(v => v.Position).ToArray()) < 0) Array.Reverse(ring);
        int start = Enumerable.Range(0, ring.Length).OrderBy(i => (ring[i]-outer[0].Position).sqrMagnitude).First();
        ring = Enumerable.Range(0, ring.Length).Select(i => ring[(i+start)%ring.Length]).ToArray();
        float[] Phases(Vector3[] points)
        {
            float[] phase = new float[points.Length+1];
            for (int i = 0; i < points.Length; i++) phase[i+1] = phase[i] + Vector3.ProjectOnPlane(points[(i+1)%points.Length]-points[i], normal).magnitude;
            float length = phase[points.Length];
            if (length > 1e-8f) for (int i = 1; i < phase.Length; i++) phase[i] /= length;
            return phase;
        }
        int Nearest(Vector3 position)
        {
            int nearest = 0; float distance = float.PositiveInfinity;
            for (int i=0;i<outer.Length;i++)
            {
                float d=(outer[i].Position-position).sqrMagnitude;
                if (d<distance) { nearest=i; distance=d; }
            }
            return nearest;
        }
        bool missingWeight = false;
        BoneWeight Remap(BoneWeight weight)
        {
            int Index(int source, float value)
            {
                if (value <= 0) return 0;
                if (core.BoneNames == null && context.BoneNames == null) return source; // synthetic geometry tests
                int index = core.BoneNames != null && context.BoneNames != null && source < core.BoneNames.Length ?
                    Array.IndexOf(context.BoneNames, core.BoneNames[source]) : -1;
                if (index < 0) { missingWeight = true; return 0; }
                return index;
            }
            return new BoneWeight { boneIndex0=Index(weight.boneIndex0,weight.weight0), weight0=weight.weight0,
                boneIndex1=Index(weight.boneIndex1,weight.weight1), weight1=weight.weight1,
                boneIndex2=Index(weight.boneIndex2,weight.weight2), weight2=weight.weight2,
                boneIndex3=Index(weight.boneIndex3,weight.weight3), weight3=weight.weight3 };
        }
        var builder = new Builder();
        var folded = new BoundaryVertex[outer.Length];
        int[] a = new int[outer.Length], b = new int[ring.Length];
        float radius = context.FromBone.MultiplyVector(core.Major).magnitude;
        for (int i = 0; i < a.Length; i++)
        {
            Vector3 radial = Vector3.ProjectOnPlane(outer[i].Position-center,normal);
            float movement = Mathf.Min(Mathf.Max(0,radial.magnitude-radius)*.1f, radius*.04f);
            folded[i] = new BoundaryVertex(outer[i].Position-radial.normalized*movement,outer[i].Normal,outer[i].Weight);
            a[i] = builder.Add(new GeneratedVertex(folded[i].Position,normal,new Vector2((float)i/a.Length,0),ClothMask,outer[i].Weight,i,Feature.ClothLip));
        }
        for (int i = 0; i < b.Length; i++)
        {
            // The lip's inner edge uses the same mapped skinning as the tube.
            GeneratedVertex original = default; float distance = float.PositiveInfinity;
            foreach (var v in template.Vertices)
            {
                if (v.Feature!=Feature.LimbTube) continue;
                float d=(transform.MultiplyPoint3x4(v.Position)-ring[i]).sqrMagnitude;
                if (d<distance) { original=v; distance=d; }
            }
            b[i] = builder.Add(new GeneratedVertex(ring[i],normal,new Vector2((float)i/b.Length,1),ClothMask,Remap(original.Weight),Nearest(ring[i]),Feature.ClothLip));
        }
        float[] pa = Phases(folded.Select(v => v.Position).ToArray()), pb = Phases(ring);
        int ai = 0, bi = 0;
        while (ai < a.Length || bi < b.Length)
        {
            if (ai < a.Length && (bi >= b.Length || pa[ai+1] <= pb[bi+1]))
            { builder.Triangle(a[ai%a.Length],a[(ai+1)%a.Length],b[bi%b.Length],normal); ai++; }
            else { builder.Triangle(a[ai%a.Length],b[(bi+1)%b.Length],b[bi%b.Length],normal); bi++; }
        }
        int first = builder.Vertices.Count;
        if (request.AddAnatomicalDetails) foreach (var v in template.Vertices)
        {
            Vector3 p = transform.MultiplyPoint3x4(v.Position);
            builder.Add(new GeneratedVertex(p,Normal(v.Normal),v.Uv,v.Mask,Remap(v.Weight),Nearest(p),v.Feature));
        }
        if (request.AddAnatomicalDetails) builder.Triangles.AddRange(template.Triangles.Select(i => i+first));
        if (missingWeight) return FoldFallback(diagnostic,"shared core bone is absent from this renderer palette");
        if (!ValidateFiniteAndNondegenerate(builder,out string failure)) return FoldFallback(diagnostic,"shared core bridge: "+failure);
        if (!request.AddAnatomicalDetails)
        {
            surface=new Surface { Vertices=builder.Vertices.ToArray(),Triangles=builder.Triangles.ToArray(),
                ExactSeamVertexIndices=a,ReplacementBoundary=folded,DecalSamples=Array.Empty<DecalSample>(),
                MoundDebugPoints=Array.Empty<Vector3>(),BoneRootDebugPoints=Array.Empty<Vector3>(),
                BoneRadiiWorld=Array.Empty<Vector2>(),DecorationsClosed=true,ClothCore=core };
            diagnostic.Status="shared-cloth-layer";
            return true;
        }
        surface = template.Copy();
        surface.Vertices=builder.Vertices.ToArray(); surface.Triangles=builder.Triangles.ToArray();
        surface.ExactSeamVertexIndices=a; surface.ReplacementBoundary=folded;
        surface.DecalSamples=BuildDecalSamples(folded,request.IsLeg?8:6);
        surface.MoundDebugPoints=template.MoundDebugPoints.Select(transform.MultiplyPoint3x4).ToArray();
        surface.BoneRootDebugPoints=template.BoneRootDebugPoints.Select(transform.MultiplyPoint3x4).ToArray();
        surface.StreamAnchor=new EmissionAnchor(transform.MultiplyPoint3x4(template.StreamAnchor.Position),
            Normal(template.StreamAnchor.Normal),Remap(template.StreamAnchor.Weight));
        if (missingWeight) { surface=null; return FoldFallback(diagnostic,"shared core anchor bone is absent from this renderer palette"); }
        surface.ClothCore=core;
        diagnostic.Status="shared-core";
        diagnostic.MajorWorld=radius*2*request.WorldScale;
        diagnostic.MinorWorld=context.FromBone.MultiplyVector(core.Minor).magnitude*2*request.WorldScale;
        diagnostic.DepthWorld=ring.Max(p => Mathf.Abs(Vector3.Dot(p-center,normal)))*request.WorldScale;
        return true;
    }

    private static bool TrySharedClothLayer(Request request, FoldDiagnostic diagnostic, BoundaryVertex[] folded,
        Vector3 center, Vector3 axisU, Vector3 axisV, out Surface surface)
    {
        surface = null;
        var builder = new Builder();
        Vector3 normal = request.FacingNormal.normalized;
        Vector3[] ring = request.Clothing.SharedCore.BackRing.Select(p => request.Clothing.FromBone.MultiplyPoint3x4(p)).ToArray();
        float Angle(Vector3 p) => Mathf.Atan2(Vector3.Dot(p-center, axisV), Vector3.Dot(p-center, axisU));
        float start = Angle(folded[0].Position);
        float direction = Mathf.Sign(Vector3.Dot(Vector3.Cross(folded[0].Position-center, folded[1].Position-center), normal));
        float Phase(Vector3 p) => Mathf.Repeat((Angle(p)-start)*direction, Mathf.PI*2f);
        // Sort by geometric angle rather than index density, then zip the two
        // loops in that order. All layers use the exact primary back-ring points.
        ring = ring.OrderBy(Phase).ToArray();
        var a = new int[folded.Length]; var b = new int[ring.Length];
        for (int i = 0; i < a.Length; i++)
            a[i] = builder.Add(new GeneratedVertex(folded[i].Position, normal, new Vector2((float)i/a.Length,0),
                ClothMask, folded[i].Weight, i, Feature.ClothLip));
        for (int i = 0; i < b.Length; i++)
        {
            int nearest = 0; float distance = float.PositiveInfinity;
            for (int j = 0; j < folded.Length; j++)
            {
                float d = Vector3.ProjectOnPlane(folded[j].Position-ring[i], normal).sqrMagnitude;
                if (d < distance) { nearest = j; distance = d; }
            }
            b[i] = builder.Add(new GeneratedVertex(ring[i], normal, new Vector2((float)i/b.Length,1),
                ClothMask, folded[nearest].Weight, nearest, Feature.ClothLip));
        }
        int ai = 0, bi = 0;
        while (ai < a.Length || bi < b.Length)
        {
            float nextA = ai+1 < a.Length ? Phase(folded[ai+1].Position) : Mathf.PI*2f;
            float nextB = bi+1 < b.Length ? Phase(ring[bi+1]) : Mathf.PI*2f;
            if (ai < a.Length && (bi >= b.Length || nextA <= nextB))
            { builder.Triangle(a[ai%a.Length], a[(ai+1)%a.Length], b[bi%b.Length], normal); ai++; }
            else
            { builder.Triangle(a[ai%a.Length], b[(bi+1)%b.Length], b[bi%b.Length], normal); bi++; }
        }
        if (!ValidateFiniteAndNondegenerate(builder, out string failure)) return FoldFallback(diagnostic, "shared cloth lip: "+failure);
        diagnostic.Status = "cloth-layer";
        surface = new Surface { Vertices = builder.Vertices.ToArray(), Triangles = builder.Triangles.ToArray(),
            ExactSeamVertexIndices = a, ReplacementBoundary = folded, DecalSamples = Array.Empty<DecalSample>(),
            MoundDebugPoints = Array.Empty<Vector3>(), BoneRootDebugPoints = Array.Empty<Vector3>(),
            BoneRadiiWorld = Array.Empty<Vector2>(), DecorationsClosed = true, ClothCore = request.Clothing.SharedCore };
        return true;
    }

    private static bool TryDarkClosure(Request request, FoldDiagnostic diagnostic, out Surface surface)
    {
        // An accessory loop outside the bone is not an anatomical cross-section.
        if (request.AddAnatomicalDetails)
        { surface = null; return FoldFallback(diagnostic, "primary loop does not contain the bone"); }
        if (!TryGenerateTissue(request, out surface, out string failure))
            return FoldFallback(diagnostic, "accessory closure: " + failure);
        surface.Vertices = surface.Vertices.Select(v => new GeneratedVertex(v.Position, v.Normal, v.Uv,
            ClothMask, v.Weight, v.BoundarySource, Feature.ClothLip)).ToArray();
        surface.FleshVertexCount = 0;
        diagnostic.Status = "accessory-cloth";
        return true;
    }

    private static bool ClothContains(Vector2[] polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if ((a.y > point.y) != (b.y > point.y) &&
                point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    internal static string ValidateClothingForTests()
    {
        foreach (bool leg in new[] { false, true })
        foreach (LimbSeverLevel level in new[] { LimbSeverLevel.Proximal, LimbSeverLevel.Distal })
        foreach (bool gib in new[] { false, true })
        foreach (float offset in new[] { 0f, -.05f, .05f })
        foreach (float skeletonScale in new[] { .6f, 1f, 1.4f })
        foreach (float rendererScale in new[] { .5f, 1f, 2f })
        foreach (float ratio in new[] { 1f, 1.10f, 1.11f, 1.65f })
        {
            if (!LimbClothingProfiles.TryGet(leg, level, gib, offset, out var profile)) return "missing profile";
            var boundary = new BoundaryVertex[24];
            Vector3 normal = gib ? Vector3.back : Vector3.forward;
            for (int i = 0; i < boundary.Length; i++)
            {
                float a = i * Mathf.PI * 2f / boundary.Length;
                boundary[i] = new BoundaryVertex(new Vector3(Mathf.Cos(a) * profile.Diameter,
                    Mathf.Sin(a) * profile.Minor, 0) * (ratio * skeletonScale * .5f / rendererScale),
                    normal, new BoneWeight { boneIndex0 = i % 2, weight0 = 1f });
            }
            var request = new Request { Boundary = boundary, FacingNormal = normal, StableCutAxis = Vector3.forward,
                WorldScale = rendererScale, IsLeg = leg, Level = level, GibSide = gib, Seed = 123456,
                AddAnatomicalDetails = true, Clothing = new LimbClothingProfiles.Context {
                    BoneCenter = Vector3.zero, SegmentLengthWorld = profile.BoneLength * skeletonScale, Offset = offset } };
            if (!TryGenerate(request, out var first, out string failure)) return "generation: " + failure;
            bool expected = ratio > 1.10f;
            if ((first.Fold.Status == "folded") != expected)
                return $"threshold {leg}/{level}/{gib}/{offset}/{skeletonScale}/{rendererScale}/{ratio}: {first.Fold.Status} {first.Fold.Reason}";
            if (!TryGenerate(request, out var second, out failure) || first.Vertices.Length != second.Vertices.Length ||
                !first.Triangles.SequenceEqual(second.Triangles)) return "nondeterministic topology";
            for (int i = 0; i < first.Vertices.Length; i++)
            {
                var v = first.Vertices[i];
                if ((v.Position - second.Vertices[i].Position).sqrMagnitude > 1e-12f) return "nondeterministic positions";
                if (Mathf.Abs(v.Weight.weight0 + v.Weight.weight1 + v.Weight.weight2 + v.Weight.weight3 - 1f) > .0001f)
                    return "unnormalized skinning";
            }
            if (!expected)
            {
                request.Clothing = null;
                if (!TryGenerate(request, out var legacy, out failure) || legacy.Vertices.Length != first.Vertices.Length ||
                    !legacy.Triangles.SequenceEqual(first.Triangles)) return "thin path changed";
                for (int i = 0; i < first.Vertices.Length; i++)
                    if (first.Vertices[i].Position != legacy.Vertices[i].Position || first.Vertices[i].Mask != legacy.Vertices[i].Mask)
                        return "thin appearance changed";
                continue;
            }
            if (first.ReplacementBoundary.Length != boundary.Length || first.Fold.DepthWorld <= 0 ||
                first.Fold.MajorWorld > profile.Diameter * skeletonScale + .00001f ||
                first.BoneCount != (level == LimbSeverLevel.Proximal ? 1 : 2)) return "invalid anatomy or folded boundary";
            foreach (var v in first.Vertices.Where(v => v.Feature == Feature.LimbTube))
                if (Vector3.Dot(v.Position, normal) > 1e-6f) return "tube extends past original plane";
            foreach (int i in first.ExactSeamVertexIndices)
                if (first.Vertices[i].Mask.a != 0) return "lip lost cloth shader mask";
            if (first.DecalSamples.Length != (leg ? 8 : 6)) return "outer decal samples lost";
            // Non-anatomical layers must never close the entire core with flesh.
            request.AddAnatomicalDetails = false;
            request.Clothing.SharedCore = first.ClothCore;
            if (!TryGenerate(request, out var layer, out failure) || layer.BoneCount != 0 ||
                layer.Vertices.Any(v => v.Feature == Feature.Mound || v.Feature == Feature.LimbTube)) return "duplicate layer flesh core";
        }
        // A coarse or narrower contour must retain the precise LOD0 interior,
        // including its skinning when renderer palettes and transforms differ.
        LimbClothingProfiles.TryGet(true,LimbSeverLevel.Proximal,false,0,out var legProfile);
        var lod0 = new Request { AddAnatomicalDetails=true, IsLeg=true, Level=LimbSeverLevel.Proximal,
            WorldScale=1, FacingNormal=Vector3.forward, StableCutAxis=Vector3.forward, Seed=123456,
            Clothing=new LimbClothingProfiles.Context { SegmentLengthWorld=legProfile.BoneLength,
                BoneNames=new[]{"thigh","calf"} } };
        BoundaryVertex[] Ring(int count,float radius) => Enumerable.Range(0,count).Select(i => {
            float angle=i*Mathf.PI*2/count;
            return new BoundaryVertex(new Vector3(Mathf.Cos(angle)*radius,Mathf.Sin(angle)*radius,0),
                Vector3.forward,new BoneWeight { boneIndex0=0,weight0=.75f,boneIndex1=1,weight1=.25f }); }).ToArray();
        lod0.Boundary=Ring(40,.16f);
        if (!TryGenerate(lod0,out var reference,out var lodFailure) || reference.ClothCore?.Interior==null)
            return "LOD reference: "+lodFailure;
        lod0.Clothing.SharedCore=reference.ClothCore;
        lod0.Clothing.BoneNames=new[]{"calf","thigh"};
        lod0.Clothing.FromBone=Matrix4x4.Translate(new Vector3(.02f,-.01f,0));
        lod0.Boundary=Ring(11,legProfile.Diameter*.5f).Select(v => new BoundaryVertex(
            lod0.Clothing.FromBone.MultiplyPoint3x4(v.Position),v.Normal,v.Weight)).ToArray();
        if (!TryGenerate(lod0,out var coarse,out lodFailure) || coarse.Fold.Status!="shared-core")
            return "LOD core reverted: "+coarse?.Fold?.Reason;
        var originalInterior=reference.ClothCore.Interior;
        var coarseInterior=coarse.Vertices.Where(v => v.Feature!=Feature.ClothLip).ToArray();
        if (coarseInterior.Length!=originalInterior.Vertices.Length) return "LOD interior topology changed";
        for(int i=0;i<coarseInterior.Length;i++)
        {
            var a=originalInterior.Vertices[i]; var b=coarseInterior[i];
            if ((lod0.Clothing.FromBone.MultiplyPoint3x4(a.Position)-b.Position).sqrMagnitude>1e-12f || a.Mask!=b.Mask)
                return "LOD interior shape/material changed";
            if (b.Weight.boneIndex0!=1-a.Weight.boneIndex0 || b.Weight.weight0!=a.Weight.weight0)
                return "LOD bone palette was not remapped";
        }
        // A subsequent rebuild must reuse the immutable reference, not the last LOD.
        if (!ReferenceEquals(coarse.ClothCore,reference.ClothCore)) return "LOD replaced authoritative core";
        foreach (var bare in LimbClothingProfiles.Bare)
        {
            var context = new LimbClothingProfiles.Context { Mesh = bare.Mesh, Source = bare.Source };
            if (!LimbClothingProfiles.IsBare(context, bare.Leg, bare.Level, bare.Gib)) return "bare exception not resolved";
            context.Source += "changed";
            if (LimbClothingProfiles.IsBare(context, bare.Leg, bare.Level, bare.Gib)) return "bare exception ignored source fingerprint";
        }
        return null;
    }
}
}
