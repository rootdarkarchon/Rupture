using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Rupture.Dismemberment.Classes.Limbs;
using Cutter = Rupture.Dismemberment.Classes.Limbs.ProceduralLimbCutGeometry;

public static class LimbBindFrameTests
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
        Debug.Log("PASS: " + message);
    }

    public static void RunBatch()
    {
        try
        {
            Run();
            File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "bind-frame-verification.json"),
                "{\"passed\":true,\"checks\":" + _checks + "}");
            UnityEditor.EditorApplication.Exit(0);
        }
        catch (Exception error) { Debug.LogException(error); UnityEditor.EditorApplication.Exit(1); }
    }

    private static void Run()
    {
        var space = new GameObject("garment").transform;
        var root = new GameObject("Base HumanLThigh1").transform;
        root.SetParent(space, false);
        var joint = new GameObject("Base HumanLCalf").transform;
        joint.SetParent(root, false); joint.localPosition = Vector3.up * .4f;
        var end = new GameObject("Base HumanLFoot").transform;
        end.SetParent(joint, false); end.localPosition = Vector3.up * .4f;
        Transform[] complete = { root, joint, end }, partial = { joint, end };
        Matrix4x4[] reference = complete.Select(b => b.worldToLocalMatrix * space.localToWorldMatrix).ToArray();
        var meshFrame = Matrix4x4.TRS(new Vector3(.1f, -.2f, .3f), Quaternion.Euler(13, 21, 37), Vector3.one);
        Matrix4x4[] shifted = reference.Select(b => b * meshFrame).ToArray();
        Matrix4x4[] partialBinds = { reference[1], reference[2] };
        Check(Cutter.TryMapBindFrame(root, partial, partialBinds, complete, shifted, out var recovered),
            "missing proximal endpoint maps from another renderer with a different mesh frame");
        Check(Enumerable.Range(0, 16).All(i => Mathf.Abs(recovered[i] - reference[0][i]) < .0001f),
            "mapped endpoint equals the omitted original bind frame");
        root.localRotation = Quaternion.Euler(32, 21, 83); joint.localRotation = Quaternion.Euler(20, -47, 5);
        Check(Cutter.TryMapBindFrame(root, partial, partialBinds, complete, shifted, out var posed) &&
            Enumerable.Range(0, 16).All(i => Mathf.Abs(posed[i] - recovered[i]) < .0001f),
            "ragdoll animation cannot change recovered cut anatomy");
        var inconsistent = (Matrix4x4[])shifted.Clone(); inconsistent[2] = Matrix4x4.Translate(Vector3.right) * inconsistent[2];
        Check(!Cutter.TryMapBindFrame(root, partial, partialBinds, complete, inconsistent, out _),
            "inconsistent shared bind skeleton is rejected");
        Check(!Cutter.TryMapBindFrame(root, partial, partialBinds, new[] { root }, new[] { reference[0] }, out _),
            "a reference with no shared bone cannot invent an endpoint");

        Mesh mesh = Tube(partialBinds);
        var spec = new Cutter.CutSpec { ProximalStart = root, Joint = joint, DistalEnd = end,
            LimbRoot = root, Level = LimbSeverLevel.Proximal, IsLeg = true, Seed = 42 };
        Check(Cutter.TryCaptureClassificationWork(mesh, space, partial, new[] { spec }, out var missing, out var failure), failure);
        Check(!missing.SetCandidate(0, LimbSeverLevel.Proximal, 0), "fixture reproduces the locked proximal eligibility failure");
        spec.ProximalBind = recovered;
        Check(Cutter.TryCaptureClassificationWork(mesh, space, partial, new[] { spec }, out var work, out failure), failure);
        Check(work.SetCandidate(0, LimbSeverLevel.Proximal, 0) && work.TryPrime(out failure),
            "recovered endpoint classifies the original weighted partial palette: " + failure);
        var items = new[] {
            new Cutter.BatchItem { Label = "partial LOD0", Classification = work, BuildDistal = true, LodLevel = 0 },
            new Cutter.BatchItem { Label = "partial LOD1", Classification = Capture(), BuildDistal = false, LodLevel = 1 }
        };
        Check(Cutter.TryBuildBatchWithRetries(items, false, null, out _, out failure, allowLevelFallback: false),
            "both clothing LODs commit at the authoritative proximal level: " + failure);
        Check(items.All(i => i.Classification.RequestedLevel(0) == LimbSeverLevel.Proximal), "locked cut never changes to the alternate level");
        foreach (var item in items) Check(item.Built.TryMaterialize(out failure), "upload cut mesh: " + failure);
        Check(items.All(i => i.Built.Body.bindposes.Length == 2 &&
            i.Built.Body.boneWeights.All(w => w.boneIndex0 < 2 && w.boneIndex1 < 2 && w.boneIndex2 < 2 && w.boneIndex3 < 2)),
            "cut output keeps the real skinning palette without virtual bone indices");
        UnityEngine.Object.DestroyImmediate(mesh);
        UnityEngine.Object.DestroyImmediate(space.gameObject);

        Cutter.ClassificationWork Capture()
        {
            if (!Cutter.TryCaptureClassificationWork(mesh, space, partial, new[] { spec }, out var value, out string reason)) throw new Exception(reason);
            return value;
        }
    }

    private static Mesh Tube(Matrix4x4[] bindposes)
    {
        const int sides = 32, rings = 17;
        var vertices = new Vector3[sides * rings]; var uv = new Vector2[vertices.Length];
        var weights = new BoneWeight[vertices.Length]; var triangles = new int[(rings - 1) * sides * 6];
        for (int ring = 0; ring < rings; ring++) for (int side = 0; side < sides; side++)
        {
            int i = ring * sides + side; float angle = side * Mathf.PI * 2 / sides;
            vertices[i] = new Vector3(Mathf.Cos(angle) * .06f, ring * .05f, Mathf.Sin(angle) * .06f);
            uv[i] = new Vector2((float)side / sides, (float)ring / (rings - 1));
            weights[i] = new BoneWeight { boneIndex0 = ring < 12 ? 0 : 1, weight0 = 1 };
            if (ring == rings - 1) continue;
            int next = ring * sides + (side + 1) % sides, t = (ring * sides + side) * 6;
            triangles[t] = i; triangles[t+1] = i+sides; triangles[t+2] = next;
            triangles[t+3] = next; triangles[t+4] = i+sides; triangles[t+5] = next+sides;
        }
        var mesh = new Mesh { name = "partial palette leg", vertices = vertices, uv = uv, boneWeights = weights, bindposes = bindposes, triangles = triangles };
        mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
    }
}
