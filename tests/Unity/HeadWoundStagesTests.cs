#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using Rupture.Ragdolls.Classes;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
internal static partial class BenchmarkRuptureHeadWound
{
    private static IEnumerator StagedPipelineChecks()
    {
        ResourceWarmupChecks();
        foreach (string stage in new[] { "Acquire", "WaitForGeometry", "UploadHead", "UploadCavity", "PrepareSkin", "PrepareCavity", "TransferDecals", "Commit" })
        foreach (string action in new[] { "reset", "remove" })
        {
            var checks = StagedScenario(stage, action);
            while (checks.MoveNext()) yield return checks.Current;
        }
        foreach (string action in new[] { "revision", "expiry", "renderer", "bones", "head", "prepared", "apply", "decalFailure" })
        {
            var checks = StagedScenario(action == "decalFailure" ? "TransferDecals" : "Commit", action);
            while (checks.MoveNext()) yield return checks.Current;
        }
    }

    private static void ResourceWarmupChecks()
    {
        HeadWoundSystem.Reset();
        var material = new Material(Shader.Find("Standard"));
        HeadWoundMaterialProvider.Material = null; HeadWoundMaterialProvider.Calls = 0;
        RupturePlugin.Log.IsDebugEnabled = true; RupturePlugin.Log.Infos.Clear();
        try
        {
            HeadWoundSystem.PrepareResources();
            ProceduralCutFrameBudget.BeginFrame(2f);
            ProceduralCutFrameBudget.RecordStage(long.MaxValue / 2);
            HeadWoundSystem.Update();
            Require(HeadWoundMaterialProvider.Calls == 0, "resource preparation obeys an exhausted shared allowance");
            AdvanceHeadFrame();
            Require(HeadWoundMaterialProvider.Calls == 1, "missing raid resources retain the prewarm request");
            HeadWoundMaterialProvider.Material = material;
            AdvanceHeadFrame(); AdvanceHeadFrame();
            Require(HeadWoundMaterialProvider.Calls == 2, "ready resources end prewarm polling before any shot");
            Require(RupturePlugin.Log.Infos.Any(s => s.Contains("resource warmup:") && s.Contains("totalMainMs=") && s.Contains("maxAtomicMainMs=")),
                "resource warmup reports total and maximum atomic CPU time separately");
        }
        finally
        {
            RupturePlugin.Log.IsDebugEnabled = false;
            HeadWoundSystem.Reset(); UnityEngine.Object.DestroyImmediate(material);
        }
    }

    private static IEnumerator StagedScenario(string stopStage, string action)
    {
        HeadWoundSystem.Reset();
        LimbDecalTextureTransfer.Prepared.Clear();
        RupturePlugin.Log.IsDebugEnabled = true; RupturePlugin.Log.Infos.Clear();
        var root = new GameObject("staged corpse");
        var head = new GameObject("head").transform; head.SetParent(root.transform, false);
        var jaw = new GameObject("jaw").transform; jaw.SetParent(head, false);
        var skinObject = new GameObject("head_LOD0"); skinObject.transform.SetParent(head, false);
        var skin = skinObject.AddComponent<SkinnedMeshRenderer>();
        Mesh mesh = BuildEllipsoid(32, 16, HeadRadii); AddSkinningFixture(mesh);
        mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
        var material = new Material(Shader.Find("Standard"));
        skin.sharedMesh = mesh; skin.bones = new[] { head, jaw }; skin.rootBone = head; skin.sharedMaterial = material;
        var body = new EFT.PlayerBody(); body.BodySkins.Add(EBodyModelPart.Head, new EFT.FixtureSkin { Renderers = new Renderer[] { skin } });
        var target = new CorpseInteractionTarget { ProfileId = "staged", PlayerId = 1, DeathSequence = 1,
            PlayerBody = body, VisualRoot = root.transform, Head = new CorpseHeadContext { Bone = head } };
        HeadWoundMaterialProvider.Material = material;
        Mesh pendingHead = null, pendingCavity = null, captured = null;
        GameObject prepared = null;
        Material decalMaterial = null;
        try
        {
            HeadWoundSystem.Enqueue(target, Packet(target, MultipleDescriptors("intersecting")[0], 1));
            ProceduralCutFrameBudget.BeginFrame(0.5f);
            ProceduralCutFrameBudget.RecordStage(long.MaxValue / 2);
            HeadWoundSystem.Update();
            Require(typeof(HeadWoundSystem).GetField("_activeWork", Hidden).GetValue(null) == null && skin.enabled,
                "limb-exhausted shared allowance defers head capture");
            AdvanceHeadFrame();
            object work = typeof(HeadWoundSystem).GetField("_activeWork", Hidden).GetValue(null);
            // A cold resource lookup may consume the frame before baking.
            for (int i = 0; i < 20 && Field(Field(work, "Build"), "Stage").ToString() != "Acquire"; i++) AdvanceHeadFrame();
            object build = Field(work, "Build");
            Require(Field(build, "Stage").ToString() == "Acquire", "baking yields before readback");
            captured = (Mesh)Field(build, "CapturedMesh");
            Require(captured != null, "frozen baked mesh is retained across the frame boundary");
            root.transform.SetPositionAndRotation(new Vector3(3, 2, -1), Quaternion.Euler(10, 60, 0));
            jaw.localRotation = Quaternion.Euler(0, 0, 3);
            for (int i = 0; i < 200 && Field(build, "Stage").ToString() != stopStage; i++)
            {
                var job = Field(build, "Job") as HeadWoundGeometryJob;
                if (job != null && !job.Completed) Wait(job);
                object[] args = { work, "staged:1", null };
                var result = typeof(HeadWoundSystem).GetMethod("TryApply", Hidden).Invoke(null, args);
                Require(result.ToString() == "Waiting", "preparation does not commit early: " + args[2]);
                Require(skin.enabled && root.GetComponentInChildren<HeadWoundLifetime>(true) == null,
                    "native visibility and lifetime remain unchanged while preparing");
                var hidden = Field(build, "Replacement") as GameObject;
                Require(hidden == null || !hidden.activeSelf, "prepared renderers remain inactive");
            }
            Require(Field(build, "Stage").ToString() == stopStage, "reached stage " + stopStage);
            int[] beforeCalls = (int[])((int[])Field(work, "StageCalls")).Clone();
            ProceduralCutFrameBudget.BeginFrame(0.5f);
            ProceduralCutFrameBudget.RecordStage(long.MaxValue / 2);
            HeadWoundSystem.Update();
            Require(Field(build, "Stage").ToString() == stopStage && ((int[])Field(work, "StageCalls")).SequenceEqual(beforeCalls),
                "exhausted shared allowance starts no head step at " + stopStage);
            pendingHead = Field(build, "HeadMesh") as Mesh; pendingCavity = Field(build, "CavityMesh") as Mesh;
            prepared = Field(build, "Replacement") as GameObject; decalMaterial = Field(build, "OwnedDecalMaterial") as Material;
            object state = Field(work, "State");
            if (action == "reset") HeadWoundSystem.Reset();
            else if (action == "remove") HeadWoundSystem.Remove("staged", 1, 1);
            else
            {
                if (action == "revision") state.GetType().GetField("Revision", Hidden).SetValue(state, (int)Field(state, "Revision") + 1);
                if (action == "expiry") work.GetType().GetField("ExpiresAt", Hidden).SetValue(work, -1f);
                if (action == "renderer") skin.sharedMesh = null;
                if (action == "bones") skin.bones = new[] { head, head };
                if (action == "head") target.Head.Bone = jaw;
                if (action == "prepared") UnityEngine.Object.DestroyImmediate(prepared);
                if (action == "decalFailure") LimbDecalTextureTransfer.FailCopy = true;
                AdvanceHeadFrame();
                if (action == "apply")
                {
                    var lifetime = root.GetComponentInChildren<HeadWoundLifetime>();
                    Require(lifetime != null && !skin.enabled && prepared.activeSelf, "only commit activates the prepared replacement");
                    Require(RupturePlugin.Log.Infos.Any(s => s.Contains("RPT head wound transaction:") && s.Contains("Commit=") &&
                        s.Contains("stages=totalMs/maxAtomicMs/calls") && s.Contains("geometryWorkerMs=") && s.Contains("endToEndMs=")),
                        "debug diagnostics distinguish per-stage CPU, atomic maxima, worker and end-to-end time");
                    Require(lifetime.SkinRenderer.sharedMaterial == decalMaterial, "prepared decal material is adopted");
                    var expected = new Mesh(); var actual = new Mesh();
                    skin.BakeMesh(expected); ((SkinnedMeshRenderer)lifetime.SkinRenderer).BakeMesh(actual);
                    Require(expected.vertices.Select((v, i) => Vector3.Distance(v, actual.vertices[i])).All(d => d < 0.0001f),
                        "pose frozen at bake still follows movement between bake and readback");
                    UnityEngine.Object.DestroyImmediate(expected); UnityEngine.Object.DestroyImmediate(actual);
                    UnityEngine.Object.DestroyImmediate(lifetime.gameObject);
                }
                else Require(skin.enabled && root.GetComponentInChildren<HeadWoundLifetime>() == null,
                    "late " + action + " preserves the native head");
            }
            yield return null;
            Require(captured == null && pendingHead == null && pendingCavity == null && prepared == null && decalMaterial == null,
                "captured and prepared resources are released at " + stopStage + "/" + action);
            Require(LimbDecalTextureTransfer.Prepared.Count == 0 || action == "apply", "abandoned decal registration is removed");
        }
        finally
        {
            RupturePlugin.Log.IsDebugEnabled = false;
            LimbDecalTextureTransfer.FailCopy = false;
            LimbDecalTextureTransfer.Prepared.Clear();
            HeadWoundSystem.Reset();
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material);
        }
    }
}
}
#endif
