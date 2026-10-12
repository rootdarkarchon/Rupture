#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Rupture.Ragdolls.Classes;
using Rupture.Dismemberment.Classes.Packets;

namespace Rupture.Dismemberment.Classes.HeadWounds
{
[UnityEditor.InitializeOnLoad]
internal static partial class BenchmarkRuptureHeadWound
{
    [Serializable] private sealed class WorkerReport
    {
        public bool passed;
        public int checks;
        public double synchronousMs, workerMs, enqueueMainMs, uploadMainMs, captureMainMs, maximumPendingUpdateMs;
        public double warmWorkerMedianMs, warmEnqueueMedianMs, warmUploadMedianMs;
        public double fallbackWorkerMs, craterWorkerMs;
        public int fallbackBuilds, craterBuilds;
    }
    private static WorkerReport _workerReport;
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    private const string PendingWorkerRun = "RuptureHeadWorkerTests";
    static BenchmarkRuptureHeadWound()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool(PendingWorkerRun, false)) return;
            UnityEditor.SessionState.SetBool(PendingWorkerRun, false);
            new GameObject("Head worker test runner").AddComponent<WorkerRunner>().StartCoroutine(ExecuteWorkerChecks());
        };
    }
    public sealed class WorkerRunner : MonoBehaviour { }
    public static void RunThreadedBatch()
    {
        RunBatch();
        UnityEditor.SessionState.SetBool(PendingWorkerRun, true);
        UnityEditor.EditorApplication.EnterPlaymode();
    }
    private static IEnumerator ExecuteWorkerChecks()
    {
        IEnumerator tests = RunWorkerChecks();
        while (true)
        {
            object next;
            try { if (!tests.MoveNext()) break; next = tests.Current; }
            catch (Exception exception) { UnityEngine.Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); yield break; }
            yield return next;
        }
        UnityEditor.EditorApplication.Exit(0);
    }
    private static IEnumerator RunWorkerChecks()
    {
        _workerReport = new WorkerReport();
        Mesh source = BuildEllipsoid(192, 96, HeadRadii);
        AddSkinningFixture(source);
        var snapshot = new ProceduralHeadWoundWorkspace();
        Require(snapshot.AcquireFromMeshOnMainThread(source, out _), "source capture");
        var input = new HeadWoundGeometrySource { Data = snapshot, BoneCount = 2 };
        try
        {
            var expected = MakeJob(input, "high-intersecting");
            expected.Execute(); Require(expected.Succeeded, "synchronous reference: " + expected.Failure);
            _workerReport.synchronousMs = expected.WorkerMilliseconds;
            var job = MakeJob(input, "high-intersecting");
            var watch = Stopwatch.StartNew(); ManagedGeometryWorker.Enqueue(job); watch.Stop();
            _workerReport.enqueueMainMs = watch.Elapsed.TotalMilliseconds;
            Wait(job); Require(job.Succeeded, "background calculation: " + job.Failure);
            _workerReport.workerMs = job.WorkerMilliseconds;
            EqualWorkspace(expected.Workspace, job.Workspace);
            var oracle = new ProceduralHeadWoundWorkspace();
            Require(oracle.AcquireFromMeshOnMainThread(source, out _), "oracle capture");
            var descriptors = job.Previous.Concat(new[] { job.Descriptor }).ToArray();
            Require(ProceduralHeadWoundGeometry.TryGenerateMultiple(oracle, descriptors, out _, out _), "existing generator oracle");
            EqualWorkspace(oracle, job.Workspace);
            Require(Equal(snapshot.SourceVertices, source.vertices), "input source remains immutable");
            var mesh = new Mesh(); Mesh cavity = null;
            try
            {
                watch.Restart();
                job.Workspace.ApplyFilteredIndicesOnMainThread(mesh, job.HeadBounds);
                cavity = job.Workspace.CreateCavityMeshOnMainThread("worker cavity", job.CavityBounds);
                watch.Stop(); _workerReport.uploadMainMs = watch.Elapsed.TotalMilliseconds;
                Require(Equal(mesh.vertices, expected.Workspace.SourceVertices) && mesh.bounds == job.HeadBounds && cavity.bounds == job.CavityBounds,
                    "main-thread upload preserves worker vertices and bounds");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); if (cavity != null) UnityEngine.Object.DestroyImmediate(cavity); }

            var workerTimes = new List<double>(); var enqueueTimes = new List<double>(); var uploadTimes = new List<double>();
            for (int sample = 0; sample < 5; sample++)
            {
                var measured = MakeJob(input, "high-intersecting");
                watch.Restart(); ManagedGeometryWorker.Enqueue(measured); watch.Stop(); enqueueTimes.Add(watch.Elapsed.TotalMilliseconds);
                Wait(measured); Require(measured.Succeeded, "warm worker sample"); workerTimes.Add(measured.WorkerMilliseconds);
                var measuredMesh = new Mesh(); Mesh measuredCavity = null;
                try
                {
                    watch.Restart(); measured.Workspace.ApplyFilteredIndicesOnMainThread(measuredMesh, measured.HeadBounds);
                    measuredCavity = measured.Workspace.CreateCavityMeshOnMainThread("measured cavity", measured.CavityBounds);
                    watch.Stop(); uploadTimes.Add(watch.Elapsed.TotalMilliseconds);
                }
                finally { UnityEngine.Object.DestroyImmediate(measuredMesh); if (measuredCavity != null) UnityEngine.Object.DestroyImmediate(measuredCavity); }
            }
            _workerReport.warmWorkerMedianMs = workerTimes.OrderBy(t => t).ElementAt(2);
            _workerReport.warmEnqueueMedianMs = enqueueTimes.OrderBy(t => t).ElementAt(2);
            _workerReport.warmUploadMedianMs = uploadTimes.OrderBy(t => t).ElementAt(2);

            // Independent jobs deliberately overlap outside the serial scheduler:
            // this detects accidental static scratch use in union generation.
            var left = MakeJob(input, "intersecting"); var right = MakeJob(input, "near-coincident");
            var leftThread = new Thread(left.Execute); var rightThread = new Thread(right.Execute);
            leftThread.Start(); rightThread.Start(); Wait(left); Wait(right);
            Require(left.Succeeded && right.Succeeded, "concurrent workspaces succeed");
            var rightExpected = MakeJob(input, "near-coincident"); rightExpected.Execute();
            EqualWorkspace(rightExpected.Workspace, right.Workspace);
            EqualWorkspace(expected.Workspace, left.Workspace);

            var cancelled = MakeJob(input, "intersecting"); cancelled.Cancelled = true;
            ManagedGeometryWorker.Enqueue(cancelled); Wait(cancelled);
            Require(!cancelled.Succeeded && cancelled.Workspace == null, "cancelled queued job does no geometry work");
            var inFlight = ManagedGeometryWorker.Enqueue(new BarrierJob());
            Require(inFlight.Entered.Wait(TimeSpan.FromSeconds(5)), "cancellation fixture started");
            inFlight.Cancelled = true; inFlight.Release.Set(); Wait(inFlight);
            Require(!inFlight.Succeeded && inFlight.Failure == "cancelled", "in-flight cancellation cannot publish success");
            var exception = MakeJob(input, "intersecting"); exception.Sources = null;
            ManagedGeometryWorker.Enqueue(exception); Wait(exception);
            Require(!exception.Succeeded && exception.Failure.Contains("NullReferenceException"), "job exceptions are contained");
            var invalid = MakeJob(input, "intersecting"); invalid.Direction = Vector3.zero;
            ManagedGeometryWorker.Enqueue(invalid); Wait(invalid);
            Require(!invalid.Succeeded, "invalid trajectory rejects without publication");
            var recovery = MakeJob(input, "coincident"); ManagedGeometryWorker.Enqueue(recovery); Wait(recovery);
            Require(recovery.Succeeded, "worker survives rejected geometry");
            FallbackChecks(input);
            var lifecycle = LifecycleChecks();
            while (lifecycle.MoveNext()) yield return lifecycle.Current;
            _workerReport.passed = true;
            string output = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "worker-verification.json");
            File.WriteAllText(output, JsonUtility.ToJson(_workerReport, true));
            UnityEngine.Debug.Log("PASS: head worker and production lifecycle: " + JsonUtility.ToJson(_workerReport));
        }
        finally { UnityEngine.Object.DestroyImmediate(source); }
    }

    private static HeadWoundGeometryJob MakeJob(HeadWoundGeometrySource source, string kind)
    {
        var descriptors = MultipleDescriptors(kind); var last = descriptors[descriptors.Length - 1];
        return new HeadWoundGeometryJob { Sources = new[] { source }, Previous = descriptors.Take(descriptors.Length - 1).ToArray(),
            SuppliedEntry = last.Entry, Direction = (last.Exit - last.Entry).normalized, MaximumDepth = 0.3f,
            EntryRadius = last.EntryRadius, ExitRadius = last.ExitRadius, Seed = last.Seed };
    }

    private static void FallbackChecks(HeadWoundGeometrySource input)
    {
        HeadWoundGeometryJob Job(Vector3 entry, Vector3 direction) => new HeadWoundGeometryJob {
            Sources = new[] { input }, Previous = Array.Empty<ProceduralWoundDescriptor>(),
            SuppliedEntry = entry, Direction = direction, MaximumDepth = 0.3f,
            EntryRadius = 0.01f, ExitRadius = 0.025f, Seed = 0x12345678 };
        var entry = new Vector3(HeadRadii.x + 0.025f, 0, 0);
        var missed = Job(entry, Vector3.forward); missed.Execute();
        Require(missed.Succeeded && missed.UsedFallback && missed.GenerationAttempts <= HeadWoundGeometryJob.MaximumGenerationAttempts,
            "off-surface tangential trajectory recovers with bounded geometry: " + missed.Failure);
        Require(Vector3.Dot(missed.ResolvedDirection, Vector3.left) > 0 && missed.EntryResolution != "trajectory",
            "fallback resolves inward geometry rather than preserving a missed bullet line");
        _workerReport.fallbackWorkerMs = missed.WorkerMilliseconds; _workerReport.fallbackBuilds = missed.GenerationAttempts;
        var repeat = Job(entry, Vector3.forward); repeat.Execute();
        Require(repeat.Succeeded && repeat.Descriptor.Entry == missed.Descriptor.Entry && repeat.Descriptor.Exit == missed.Descriptor.Exit,
            "fallback candidates are deterministic");
        EqualWorkspace(missed.Workspace, repeat.Workspace);
        var distant = Job(Vector3.one * 2, Vector3.forward); distant.Execute();
        Require(!distant.Succeeded && distant.GenerationAttempts == 0, "fallback never snaps distant hits onto the head");
        var excessive = Job(entry, Vector3.forward); excessive.Previous = new ProceduralWoundDescriptor[4]; excessive.Execute();
        Require(!excessive.Succeeded && excessive.GenerationAttempts == 0, "worker refuses a fifth visible wound");
        var craterJob = Job(entry, Vector3.forward); craterJob.MaximumDepth = 0.012f; craterJob.Execute();
        Require(craterJob.Succeeded && craterJob.EntryResolution == "entry-crater" && !craterJob.Descriptor.ThroughShot &&
            !craterJob.TerminalSurfaceResolved && craterJob.GenerationAttempts <= HeadWoundGeometryJob.MaximumGenerationAttempts,
            "short-depth hit recovers through the actual closed-crater fallback: " + craterJob.Failure);
        _workerReport.craterWorkerMs = craterJob.WorkerMilliseconds; _workerReport.craterBuilds = craterJob.GenerationAttempts;

        // Exercise the last fallback directly as well as the complete selection path.
        var crater = new ProceduralWoundDescriptor(new Vector3(HeadRadii.x, 0, 0),
            new Vector3(HeadRadii.x - 0.015f, 0, 0), 0.01f, 0.0035f, 123, throughShot: false);
        var data = new ProceduralHeadWoundWorkspace(); data.CopySource(input.Data);
        Require(ProceduralHeadWoundGeometry.TryGenerateMultiple(data, new[] { crater }, out var metrics, out string failure),
            "closed entry crater generation: " + failure);
        Require(metrics.ExitFlapCount == 0 && metrics.EntryLipCount == 1, "entry crater has one entrance and no exit burst");
        var through = MultipleDescriptors("intersecting")[0];
        var mixed = new ProceduralHeadWoundWorkspace(); mixed.CopySource(input.Data);
        Require(ProceduralHeadWoundGeometry.TryGenerateMultiple(mixed, new[] { crater, through }, out _, out failure),
            "closed crater combines with a through wound: " + failure);
        Require(ProceduralHeadWoundGeometry.ValidateCombined(mixed, new[] { crater, through }, input.Data.SourceVertices.Count, out failure),
            "combined validation permits a closed crater end: " + failure);
        var capRay = typeof(ProceduralHeadWoundGeometry).GetMethod("Obstructed", Hidden);
        foreach (var workspace in new[] { data, mixed })
        {
            var direction = (crater.Exit - crater.Entry).normalized;
            Require((bool)capRay.Invoke(null, new object[] { workspace.CavityVertices, workspace.CavityTriangles,
                crater.Entry - direction * 0.001f, direction, 0.025f }),
                "crater retains a closed terminal surface before and after adding a separate through wound");
        }
    }

    private static void Wait(ManagedGeometryJob job)
    {
        var timer = Stopwatch.StartNew();
        while (!job.Completed && timer.Elapsed.TotalSeconds < 15) Thread.Sleep(1);
        Require(job.Completed, "worker completed without deadlock");
    }

    private sealed class BarrierJob : ManagedGeometryJob
    {
        internal readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
        internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
        protected override bool TryExecute(out string failure)
        {
            Entered.Set(); bool ok = Release.Wait(TimeSpan.FromSeconds(15)); failure = ok ? null : "barrier timed out"; return ok;
        }
    }

    private static IEnumerator LifecycleChecks()
    {
        foreach (string mode in new[] { "ordered", "fallback", "rejected", "destroyed", "reset", "remove", "expired", "revision", "renderer", "headless" })
        {
            HeadWoundSystem.Reset();
            var root = new GameObject("worker corpse"); var head = new GameObject("head").transform; head.SetParent(root.transform, false);
            var secondary = new GameObject("jaw").transform; secondary.SetParent(head, false);
            var skinObject = new GameObject("head_LOD0"); skinObject.transform.SetParent(head, false);
            var skin = skinObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = BuildEllipsoid(64, 32, HeadRadii); AddSkinningFixture(mesh);
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            skin.sharedMesh = mesh; skin.bones = new[] { head, secondary }; skin.rootBone = head;
            var material = new Material(Shader.Find("Hidden/InternalErrorShader")); skin.sharedMaterials = new[] { material, material };
            HeadWoundMaterialProvider.Material = material;
            var body = new EFT.PlayerBody(); body.BodySkins.Add(EBodyModelPart.Head, new EFT.FixtureSkin { Renderers = new Renderer[] { skin } });
            var target = new CorpseInteractionTarget { ProfileId = "worker-" + mode, PlayerId = 1, DeathSequence = 1,
                VisualRoot = root.transform, PlayerBody = body, Head = new CorpseHeadContext { Bone = head } };
            var barrier = ManagedGeometryWorker.Enqueue(new BarrierJob());
            Require(barrier.Entered.Wait(TimeSpan.FromSeconds(5)), "barrier entered");
            try
            {
                if (mode == "headless") Rupture.MultiplayerRuntime.Current.IsHeadless = true;
                var descriptors = MultipleDescriptors("intersecting");
                if (mode == "ordered")
                    foreach (int index in new[] { 3, 1, 0, 2 }) HeadWoundSystem.Enqueue(target, Packet(target, descriptors[index], index + 1));
                else
                {
                    var first = Packet(target, descriptors[0], 1);
                    if (mode == "fallback") { first.EntryLocal = new Vector3(HeadRadii.x + 0.025f, 0, 0); first.DirectionLocal = Vector3.forward; }
                    HeadWoundSystem.Enqueue(target, first);
                }
                var capture = Stopwatch.StartNew();
                for (int i = 0; i < 4; i++) HeadWoundSystem.Update();
                capture.Stop(); _workerReport.captureMainMs = Math.Max(_workerReport.captureMainMs, capture.Elapsed.TotalMilliseconds);
                object active = typeof(HeadWoundSystem).GetField("_activeWork", Hidden).GetValue(null);
                if (mode == "headless") { Require(active == null && skin.enabled, "headless admits no geometry"); continue; }
                Require(active != null && skin.enabled && root.GetComponentInChildren<HeadWoundLifetime>() == null,
                    "capture leaves original skin visible while calculation is pending");
                var pendingJob = (HeadWoundGeometryJob)Field(Field(active, "Build"), "Job");
                for (int i = 0; i < 20; i++)
                {
                    var timer = Stopwatch.StartNew(); HeadWoundSystem.Update(); timer.Stop();
                    _workerReport.maximumPendingUpdateMs = Math.Max(_workerReport.maximumPendingUpdateMs, timer.Elapsed.TotalMilliseconds);
                    Require(!pendingJob.Completed && skin.enabled, "Update polls without waiting or exposing partial geometry");
                }
                if (mode == "reset") HeadWoundSystem.Reset();
                if (mode == "remove") HeadWoundSystem.Remove(target.ProfileId, target.PlayerId, target.DeathSequence);
                if (mode == "expired") active.GetType().GetField("ExpiresAt", Hidden).SetValue(active, -1f);
                if (mode == "revision")
                {
                    object state = Field(active, "State"); state.GetType().GetField("Revision", Hidden).SetValue(state, 99);
                }
                if (mode == "renderer") skin.sharedMesh = null;
                // Move the corpse and jaw after capture. Adoption must retain the
                // captured bind poses and follow the current skeleton correctly.
                root.transform.SetPositionAndRotation(new Vector3(8, 2, -3), Quaternion.Euler(10, 65, 0));
                secondary.localRotation = Quaternion.Euler(0, 0, 3);
                barrier.Release.Set(); Wait(pendingJob);
                HeadWoundSystem.Update();
                if (mode == "ordered" || mode == "fallback" || mode == "rejected" || mode == "destroyed")
                {
                    var timer = Stopwatch.StartNew();
                    int expectedCount = mode == "ordered" ? 4 : 1;
                    while (timer.Elapsed.TotalSeconds < 10)
                    {
                        HeadWoundSystem.Update();
                        object state = Field(active, "State");
                        if (((IList)Field(state, "Descriptors")).Count == expectedCount) break;
                        Thread.Sleep(1);
                    }
                    Require(((IList)Field(Field(active, "State"), "Descriptors")).Count == expectedCount, "ordered wound adoption");
                    var lifetime = root.GetComponentInChildren<HeadWoundLifetime>();
                    Require(lifetime != null && !skin.enabled, "validated replacement adopted");
                    if (mode == "fallback") Require(pendingJob.UsedFallback &&
                        BloodStreams.PulsatingBloodStreamSystem.LastDirection == pendingJob.ResolvedDirection &&
                        BloodStreams.PulsatingBloodStreamSystem.LastTerminal == pendingJob.TerminalSurfaceResolved,
                        "fallback adoption sends accepted direction and exit state to blood effects");
                    var replacement = (SkinnedMeshRenderer)lifetime.SkinRenderer;
                    Require(replacement.rootBone == head && replacement.localBounds == replacement.sharedMesh.bounds,
                        "initial and cumulative swaps retain head-local culling bounds");
                    var expected = new Mesh(); var actual = new Mesh();
                    skin.BakeMesh(expected); replacement.BakeMesh(actual);
                    var ev = expected.vertices; var av = actual.vertices;
                    Require(ev.Length <= av.Length && ev.Where((v, i) => Vector3.Distance(v, av[i]) > 0.0001f).Count() == 0,
                        "delayed adoption follows the moved corpse and jaw using the captured bind pose");
                    UnityEngine.Object.DestroyImmediate(expected); UnityEngine.Object.DestroyImmediate(actual);
                    Mesh committed = replacement.sharedMesh;
                    if (mode == "rejected" || mode == "destroyed")
                    {
                        var nextBarrier = ManagedGeometryWorker.Enqueue(new BarrierJob());
                        Require(nextBarrier.Entered.Wait(TimeSpan.FromSeconds(5)), "second-wound barrier entered");
                        var packet = Packet(target, descriptors[1], 2);
                        if (mode == "rejected") packet.DirectionLocal = Vector3.zero;
                        HeadWoundSystem.Enqueue(target, packet); HeadWoundSystem.Update();
                        var nextWork = typeof(HeadWoundSystem).GetField("_activeWork", Hidden).GetValue(null);
                        var nextJob = (HeadWoundGeometryJob)Field(Field(nextWork, "Build"), "Job");
                        Require(replacement.sharedMesh == committed, "prior wound stays visible during the next build");
                        if (mode == "destroyed") UnityEngine.Object.DestroyImmediate(lifetime.gameObject);
                        nextBarrier.Release.Set(); Wait(nextJob); HeadWoundSystem.Update();
                        if (mode == "rejected")
                        {
                            Require(replacement.sharedMesh == committed && ((IList)Field(Field(active, "State"), "Descriptors")).Count == 1,
                                "rejected second wound preserves the committed mesh and descriptors");
                            HeadWoundSystem.Enqueue(target, Packet(target, descriptors[2], 3));
                            timer.Restart();
                            while (((IList)Field(Field(active, "State"), "Descriptors")).Count != 2 && timer.Elapsed.TotalSeconds < 10)
                            { HeadWoundSystem.Update(); Thread.Sleep(1); }
                            Require(((IList)Field(Field(active, "State"), "Descriptors")).Count == 2 && replacement.sharedMesh != committed,
                                "failed ordinal releases the next valid wound without replay");
                            HeadWoundSystem.Enqueue(target, Packet(target, descriptors[1], 4));
                            HeadWoundSystem.Enqueue(target, Packet(target, descriptors[3], 5));
                            HeadWoundSystem.Enqueue(target, Packet(target, descriptors[0], 6));
                            timer.Restart();
                            while (((IList)Field(Field(active, "State"), "Descriptors")).Count != 4 && timer.Elapsed.TotalSeconds < 10)
                            { HeadWoundSystem.Update(); Thread.Sleep(1); }
                            Require(((IList)Field(Field(active, "State"), "Descriptors")).Count == 4,
                                "failed second attempt still allows four adopted wounds through ordinal five");
                            Mesh full = replacement.sharedMesh;
                            HeadWoundSystem.Update(); HeadWoundSystem.Enqueue(target, Packet(target, descriptors[0], 7)); HeadWoundSystem.Update();
                            Require(replacement.sharedMesh == full && typeof(HeadWoundSystem).GetField("_activeWork", Hidden).GetValue(null) == null,
                                "queued and future attempts cannot build a fifth visible wound");
                            committed = replacement.sharedMesh;
                        }
                        else Require(root.GetComponentInChildren<HeadWoundLifetime>() == null && skin.enabled,
                            "destroyed lifetime invalidates an in-flight cumulative result");
                    }
                    if (lifetime != null) UnityEngine.Object.DestroyImmediate(lifetime.gameObject);
                    yield return null;
                    Require(committed == null && skin.enabled, "lifetime destroys owned meshes and restores the source renderer");
                }
                else Require(root.GetComponentInChildren<HeadWoundLifetime>() == null && skin.enabled,
                    mode + " discards the result without changing original visibility");
            }
            finally
            {
                barrier.Release.Set(); Rupture.MultiplayerRuntime.Current.IsHeadless = false;
                HeadWoundSystem.Reset(); UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, Hidden).GetValue(target);
    private static HeadWoundData Packet(CorpseInteractionTarget target, ProceduralWoundDescriptor descriptor, int ordinal) =>
        new HeadWoundData { ProfileId = target.ProfileId, PlayerId = target.PlayerId, DeathSequence = target.DeathSequence,
            WoundOrdinal = (byte)ordinal, EntryLocal = descriptor.Entry, DirectionLocal = (descriptor.Exit - descriptor.Entry).normalized,
            EntryRadius = descriptor.EntryRadius, ExitRadius = descriptor.ExitRadius, ConeDepth = 0.3f, IrregularitySeed = descriptor.Seed };

    private static bool Equal<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) => a.Count == b.Count && !a.Where((v, i) => !EqualityComparer<T>.Default.Equals(v, b[i])).Any();
    private static void EqualWorkspace(ProceduralHeadWoundWorkspace a, ProceduralHeadWoundWorkspace b)
    {
        Require(Equal(a.SourceVertices, b.SourceVertices) && Equal(a.SourceNormals, b.SourceNormals) &&
            Equal(a.SourceTangents, b.SourceTangents) && Equal(a.SourceColors, b.SourceColors) &&
            Equal(a.SourceBoneWeights, b.SourceBoneWeights) && a.SubMeshCount == b.SubMeshCount, "exact skin channel parity");
        for (int i = 0; i < 8; i++) Require(Equal(a.SourceUvs[i], b.SourceUvs[i]), "UV channel parity " + i);
        for (int i = 0; i < a.SubMeshCount; i++) Require(Equal(a.GetFilteredSubmesh(i), b.GetFilteredSubmesh(i)), "submesh parity " + i);
        Require(Equal(a.CavityVertices, b.CavityVertices) && Equal(a.CavityNormals, b.CavityNormals) &&
            Equal(a.CavityTangents, b.CavityTangents) && Equal(a.CavityUvs, b.CavityUvs) && Equal(a.CavityColors, b.CavityColors) &&
            Equal(a.CavityTriangles, b.CavityTriangles), "exact cavity channel parity");
    }
    private static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
        _workerReport.checks++;
    }
}
}
#endif
