using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.Limbs;
using Cutter = Rupture.Dismemberment.Classes.Limbs.ProceduralLimbCutGeometry;

namespace Rupture.ClothingHarness
{
    [Serializable] internal sealed class AttemptReport
    {
        public string Level, Stage, Failure;
        public float FractionOffset;
        public bool Cached;
        public int SelectedTriangles;
    }

    [Serializable] internal sealed class RendererReport
    {
        public int LodLevel;
        public string Renderer, Mesh, Status = "Pending", Stage = "fixture", Reason = "", Capture;
        public int SourceVertices, SourceTriangles, SelectedTriangles, BodyLoops, GibLoops;
        public double Milliseconds;
        public double ClassificationMilliseconds, AnalysisMilliseconds, BuildMilliseconds, CaptureMilliseconds;
        public List<AttemptReport> Attempts = new List<AttemptReport>();
        public List<ProceduralLimbWoundGeometry.FoldDiagnostic> Clothing;
    }

    [Serializable] internal sealed class CutCaseReport
    {
        public string Id, GarmentId, Garment, Bundle, BundleHash, Side, Level, Status = "Pending", Stage, Reason, EvidenceDirectory;
        public uint Seed;
        public float FractionOffset;
        public string EffectiveLevel;
        public float? CalibrationOffset;
        public double Milliseconds;
        public List<RendererReport> Renderers = new List<RendererReport>();
        [JsonIgnore] internal ClothingCaseView View;
        [JsonIgnore] internal GarmentEntry Entry;
    }

    internal static class ClothingCutRunner
    {
        private sealed class Work
        {
            internal RendererFixture Fixture;
            internal RendererReport Report;
            internal Transform Space;
            internal Mesh Source;
            internal Transform[] Bones;
            internal Cutter.ClassificationWork Classification;
            internal Cutter.Result Analysis, Result;
            internal bool Eligible;
            internal readonly Stopwatch Watch = new Stopwatch();
        }

        internal static CutCaseReport NewCase(GarmentEntry entry, bool left, LimbSeverLevel level, uint seed)
        {
            string side = (left ? "Left" : "Right") + (entry.Part == "Feet" ? "Leg" : "Arm");
            return new CutCaseReport { Id = entry.Id + "-" + side + "-" + level + "-" + seed,
                GarmentId = entry.Id, Garment = entry.Label, Bundle = entry.Bundle, BundleHash = entry.BundleHash,
                Side = side, Level = level.ToString(), Seed = seed, Entry = entry };
        }

        internal static void Run(CutCaseReport report, string fixtureDirectory, string runDirectory,
            Transform parent, Vector3 position)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string caseDirectory = Path.Combine(runDirectory, report.CalibrationOffset.HasValue ?
                "cal-" + LimbCutCaptureSession.Hash(System.Text.Encoding.UTF8.GetBytes(report.Id)).Substring(0, 16) : report.Id);
            report.EvidenceDirectory = caseDirectory;
            Directory.CreateDirectory(caseDirectory);
            GameObject root = NewObject(report.Id + " " + report.Garment, parent);
            root.transform.localPosition = position;
            ClothingCaseView view = root.AddComponent<ClothingCaseView>();
            report.View = view;
            List<Work> works = new List<Work>();
            try
            {
                report.Stage = "fixture";
                GarmentFixture fixture = FixtureIO.Read(fixtureDirectory, report.Entry);
                bool leg = report.Entry.Part == "Feet", left = report.Side.StartsWith("Left", StringComparison.Ordinal);
                LimbSeverLevel level = (LimbSeverLevel)Enum.Parse(typeof(LimbSeverLevel), report.Level);
                foreach (RendererFixture renderer in fixture.Renderers)
                {
                    Work work = new Work { Fixture = renderer, Report = new RendererReport
                        { Renderer = renderer.Renderer, Mesh = renderer.Mesh.Name, LodLevel = renderer.LodLevel } };
                    works.Add(work); report.Renderers.Add(work.Report); work.Watch.Start();
                    try
                    {
                        Prepare(work, view, leg, left, level, report.Seed);
                        if (!work.Eligible) continue;
                        work.Report.Status = "Ready";
                    }
                    catch (Exception exception) { Fail(work, exception.ToString()); }
                }

                // Shared production transaction: every layer uses the same candidate.
                Work[] analyzed = works.Where(w => w.Report.Status == "Ready").ToArray();
                bool rejected = works.Any(w => w.Report.Status == "Failed" || w.Report.Status == "Unreadable");
                if (!rejected && analyzed.Length > 0)
                {
                    var batch = analyzed.Select(w => new Cutter.BatchItem { Label = w.Report.Renderer,
                        Classification = w.Classification, NewCutIndex = 0, LodLevel = w.Fixture.LodLevel,
                        BuildDistal = w.Fixture.LodLevel == 0 }).ToArray();
                    bool succeeded = Cutter.TryBuildBatchWithRetries(batch, !Environment.GetCommandLineArgs().Contains("-rptBenchmark"), null, out report.FractionOffset, out string failure,
                        allowLevelFallback: !report.CalibrationOffset.HasValue, singleCandidateOffset: report.CalibrationOffset);
                    for (int index = 0; index < analyzed.Length; index++)
                    {
                        Work work = analyzed[index];
                        work.Report.Stage = batch[index].Stage;
                        work.Report.ClassificationMilliseconds = batch[index].ClassificationMilliseconds;
                        work.Report.AnalysisMilliseconds = batch[index].AnalysisMilliseconds;
                        work.Report.BuildMilliseconds = batch[index].BuildMilliseconds;
                        work.Report.Attempts = work.Classification.Attempts.Select(a => new AttemptReport {
                            Level = a.Level, Stage = a.Stage, Failure = a.Failure, FractionOffset = a.FractionOffset,
                            Cached = a.Cached, SelectedTriangles = a.SelectedTriangles }).ToList();
                        work.Analysis = batch[index].Analysis;
                        work.Result = batch[index].Built;
                        if (!succeeded) { Fail(work, failure); continue; }
                        try
                        {
                            report.EffectiveLevel = work.Result.EffectiveLevel.ToString();
                            work.Report.Stage = "mesh-upload";
                            if (!work.Result.TryMaterialize(out failure)) { Fail(work, failure); continue; }
                            OwnResult(view, work.Result);
                            ValidateOutput(work.Result);
                            work.Report.SelectedTriangles = work.Result.NewSelectedTriangleCount;
                            work.Report.BodyLoops = work.Result.NewBodyLoopCount;
                            work.Report.GibLoops = work.Result.GibLoopCount;
                            work.Report.Clothing = work.Result.Clothing;
                            work.Report.Status = "Passed";
                        }
                        catch (Exception exception) { Fail(work, exception.ToString()); }
                    }
                }

                if (works.Any(w => w.Report.Status == "Failed"))
                {
                    RendererReport failed = works.First(w => w.Report.Status == "Failed").Report;
                    report.Status = "Failed"; report.Stage = failed.Stage; report.Reason = failed.Reason;
                }
                else if (works.Any(w => w.Report.Status == "Unreadable"))
                {
                    report.Status = "Unreadable"; report.Stage = "source-readability";
                    report.Reason = "Source mesh is unreadable; no cut or positional retry attempted";
                }
                else if (report.Status != "Failed")
                {
                    report.Status = analyzed.Length == 0 ? "Unsupported" : "Passed";
                    report.Stage = analyzed.Length == 0 ? "eligibility" : "complete";
                    report.Reason = analyzed.Length == 0 ? "No LOD0 renderer has the required weighted limb palette; see renderer reasons" : "";
                }

                foreach (Work work in works)
                {
                    work.Watch.Stop(); work.Report.Milliseconds = work.Watch.Elapsed.TotalMilliseconds;
                    string rendererDirectory = Path.Combine(caseDirectory, "renderer-" + works.IndexOf(work));
                    if (work.Classification != null)
                    {
                        try
                        {
                            // Per-renderer session avoids raid capture quotas and
                            // seed deduplication. Source + case snapshots stay exact.
                            long captureStarted = Stopwatch.GetTimestamp();
                            work.Report.Capture = work.Classification.Capture(new LimbCutCaptureSession(rendererDirectory),
                                work.Fixture.Renderer, work.Fixture.LodLevel, report.Side, 0, report.Id,
                                work.Report.Status == "Passed" ? "prepared" : work.Report.Stage + ": " + work.Report.Reason);
                            work.Report.CaptureMilliseconds = (Stopwatch.GetTimestamp() - captureStarted) * 1000d / Stopwatch.Frequency;
                            AddDiagnosticViews(work, view, rendererDirectory, report.Status != "Passed");
                        }
                        catch (Exception exception)
                        {
                            report.Status = "Failed"; report.Stage = "diagnostic-export";
                            report.Reason = exception.ToString();
                        }
                    }
                }
                // Commit visibility only when the entire case succeeded. Failed
                // cases retain ALL original garment renderers, including siblings.
                if (report.Status == "Passed")
                    foreach (Work work in works.Where(w => w.Report.Status == "Passed"))
                    {
                        view.Results.Add(Surface(view, work.Space, "Retained body", work.Result.Body, new Color(0.55f, 0.61f, 0.67f)));
                        view.Results.Add(Surface(view, work.Space, "Body wound", work.Result.BodySocket, new Color(0.40f, 0.025f, 0.025f)));
                        view.Results.Add(Surface(view, work.Space, "Detached limb", work.Result.NewDistal, new Color(0.58f, 0.63f, 0.68f), true));
                        if (work.Result.HasBodyStreamAnchor)
                            view.CutViews.Add(new ClothingCaseView.CutFocus { Face = "body", LodLevel = work.Fixture.LodLevel,
                                Center = work.Space.TransformPoint(work.Result.BodyStreamAnchor.Position),
                                Normal = work.Space.TransformDirection(work.Result.BodyStreamAnchor.Normal), Radius = report.Side.EndsWith("Leg") ? .20f : .15f });
                        if (work.Result.HasGibStreamAnchor)
                            view.CutViews.Add(new ClothingCaseView.CutFocus { Face = "gib", LodLevel = work.Fixture.LodLevel,
                                Center = work.Space.TransformPoint(work.Result.GibStreamAnchor.Position),
                                Normal = work.Space.TransformDirection(work.Result.GibStreamAnchor.Normal), Radius = report.Side.EndsWith("Leg") ? .20f : .15f });
                        GameObject debug = Surface(view, work.Space, "Production classification overlay", work.Result.DebugOverlay, Color.yellow);
                        if (debug != null) { view.Classification.Add(debug); debug.SetActive(false); }
                    }
                // Unsupported companion renderers are untouched, as in runtime.
                if (report.Status == "Passed")
                    foreach (Work work in works.Where(w => !w.Eligible && w.Source != null))
                        view.Results.Add(Surface(view, work.Space, "Unchanged companion", work.Source, new Color(0.55f, 0.61f, 0.67f)));
                foreach (Work work in works.Where(w => w.Source != null)) ValidateImmutableSource(work);
            }
            catch (Exception exception)
            {
                report.Status = "Failed"; report.Reason = exception.ToString();
            }
            finally
            {
                report.Milliseconds = timer.Elapsed.TotalMilliseconds;
                view.Failed = report.Status != "Passed";
                view.Summary = report.Status + " / " + report.Stage + "\n" + report.Side + " " + report.Level +
                    " seed=" + report.Seed + " effective=" + (report.EffectiveLevel ?? "none") + " offset=" + report.FractionOffset.ToString("P0") + "\n" + report.Reason + "\n" +
                    string.Join("\n", report.Renderers.Select(r => r.Renderer + ": " + r.Status + " / " + r.Stage + " " + r.Reason + "\n" +
                        string.Join("\n", r.Attempts.Select(a => a.Level + " " + a.FractionOffset.ToString("P0") + (a.Cached ? " cached" : "") + ": " + a.Stage + " " + (a.Failure ?? "passed")))));
                root.name = "[" + report.Status + "] " + report.Side + " " + report.Level + " " + report.Garment;
                view.ApplyVisibility();
                File.WriteAllText(Path.Combine(caseDirectory, "result.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
                Cutter.ResetCaches(); // no cross-case classification/source cache
            }
        }

        private static void Prepare(Work work, ClothingCaseView view, bool leg, bool left, LimbSeverLevel level, uint seed)
        {
            RendererFixture fixture = work.Fixture;
            // WTT's 2022.3.43f2 bundles use the same decoded fixture channels; their
            // in-memory readability recovery is also load-tested in the f1 editor.
            if (fixture.UnityVersion != "2022.3.43f1" && fixture.UnityVersion != "2022.3.43f2")
                throw new InvalidDataException("Unexpected asset Unity version: " + fixture.UnityVersion);
            Transform space = NewObject(fixture.Renderer, view.transform).transform;
            view.LodSpaces.Add(space.gameObject, fixture.LodLevel);
            foreach (FixtureTransform local in fixture.TransformChain)
            {
                space = NewObject("Prefab transform", space).transform;
                space.localPosition = FixtureMesh.V(local.Position);
                space.localRotation = new Quaternion(local.Rotation[0], local.Rotation[1], local.Rotation[2], local.Rotation[3]);
                space.localScale = FixtureMesh.V(local.Scale);
            }
            work.Space = space;
            work.Source = fixture.Mesh.Create(); view.Owned.Add(work.Source);
            work.Report.SourceVertices = work.Source.vertexCount;
            work.Report.SourceTriangles = fixture.Mesh.Submeshes.Sum(s => s.Length / 3);
            view.Originals.Add(Surface(view, space, "Intact reference", work.Source, new Color(0.55f, 0.61f, 0.67f)));
            if (!fixture.Readable)
            {
                Cutter.MarkSourceUnreadable(work.Source);
                work.Report.Status = "Unreadable"; work.Report.Stage = "source-readability";
                work.Report.Reason = "Native source is unreadable; extracted preview data does not permit cutting";
                return;
            }
            if (fixture.BonePaths.Length != fixture.Mesh.Bindposes.Length) throw new InvalidDataException("Bone palette / bind pose mismatch");
            Transform rig = NewObject("Bind-space skeleton", space).transform;
            Dictionary<string, Transform> paths = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (string path in fixture.BonePaths.OrderBy(p => p.Length))
            {
                string current = ""; Transform parent = rig;
                foreach (string part in path.Split('/'))
                {
                    current = current.Length == 0 ? part : current + "/" + part;
                    if (!paths.TryGetValue(current, out Transform bone))
                        paths[current] = bone = NewObject(part, parent).transform;
                    parent = bone;
                }
            }
            work.Bones = fixture.BonePaths.Select(p => paths[p]).ToArray();
            // Parent-first placement preserves every bone's bind-space location.
            foreach (int index in Enumerable.Range(0, work.Bones.Length).OrderBy(i => fixture.BonePaths[i].Length))
                work.Bones[index].position = space.TransformPoint(work.Source.bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero));
            view.Bones.AddRange(work.Bones);
            string side = left ? "L" : "R";
            Func<string, Transform> find = suffix => paths.Values.FirstOrDefault(t => t.name == "Base Human" + side + suffix);
            Transform start = find(leg ? "Thigh1" : "Upperarm"), joint = find(leg ? "Calf" : "Forearm1"), end = find(leg ? "Foot" : "Palm");
            Transform limbRoot = level == LimbSeverLevel.Proximal ? start : joint;
            Transform cutStart = level == LimbSeverLevel.Proximal ? start : joint;
            Transform cutEnd = level == LimbSeverLevel.Proximal ? joint : end;
            work.Report.Stage = "eligibility";
            if (joint == null || !work.Bones.Contains(joint) || (start == null && end == null))
            {
                work.Report.Status = "Unsupported";
                work.Report.Reason = "Renderer palette lacks the selected limb segment endpoints/root (runtime RendererSupportsCut gate)";
                return;
            }
            HashSet<int> limbIndices = new HashSet<int>(Enumerable.Range(0, work.Bones.Length)
                .Where(i => work.Bones[i] == start || start != null && work.Bones[i].IsChildOf(start)));
            bool weighted = Enumerable.Range(0, fixture.Mesh.Weights.Length).Any(i =>
                Enumerable.Range(0, 4).Where(j => limbIndices.Contains(fixture.Mesh.BoneIndices[i][j])).Sum(j => fixture.Mesh.Weights[i][j]) > 0.01f);
            if (!weighted)
            {
                work.Report.Status = "Unsupported"; work.Report.Reason = "No vertex carries > 0.01 weight from this limb";
                return;
            }
            work.Eligible = true;
            work.Report.Stage = "source-capture";
            var spec = new Cutter.CutSpec { ProximalStart = start, Joint = joint, DistalEnd = end,
                LimbRoot = limbRoot, Level = level, IsLeg = leg, Seed = seed, IncludeAnatomicalDetails = false };
            if (!Cutter.TryCaptureClassificationWork(work.Source, space, work.Bones, new[] { spec }, out work.Classification, out string failure))
                throw new InvalidDataException(failure);
            work.Classification.CaptureEnabled = true;
        }

        private static void Fail(Work work, string reason) { work.Report.Status = "Failed"; work.Report.Reason = reason; }

        private static void ValidateImmutableSource(Work work)
        {
            FixtureMesh fixture = work.Fixture.Mesh;
            if (!work.Source.vertices.SequenceEqual(FixtureMesh.Vectors(fixture.Positions)) ||
                !work.Source.triangles.SequenceEqual(fixture.Submeshes.SelectMany(s => s)))
                throw new InvalidDataException("Harness invariant: cutter mutated the intact source mesh " + fixture.Name);
        }

        private static void OwnResult(ClothingCaseView view, Cutter.Result result)
        {
            foreach (Mesh mesh in new[] { result.Body, result.BodySocket, result.NewDistal, result.DebugOverlay })
                if (mesh != null) { mesh.hideFlags = HideFlags.DontSave; view.Owned.Add(mesh); }
        }

        private static void ValidateOutput(Cutter.Result result)
        {
            foreach (Mesh mesh in new[] { result.Body, result.BodySocket, result.NewDistal })
            {
                if (mesh == null) continue;
                if (mesh.vertices.Any(v => float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                    float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)))
                    throw new InvalidDataException(mesh.name + ": non-finite output vertices");
                if (mesh.triangles.Any(i => i < 0 || i >= mesh.vertexCount))
                    throw new InvalidDataException(mesh.name + ": invalid output indices");
            }
        }

        internal static GameObject NewObject(string name, Transform parent)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(parent, false); return go;
        }

        private static GameObject Surface(ClothingCaseView view, Transform space, string name, Mesh mesh, Color color, bool woundLast = false)
        {
            if (mesh == null || mesh.vertexCount == 0) return null;
            GameObject go = NewObject(name, space);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            Material[] materials = new Material[mesh.subMeshCount];
            for (int i = 0; i < materials.Length; i++)
            {
                bool wound = name == "Body wound" || woundLast && i == materials.Length - 1;
                var material = new Material(Shader.Find(name == "Production classification overlay" ?
                    "Hidden/Rupture/ClothingVertexColors" : wound ? "Rupture/LimbSeverSurface" : "Standard")) { hideFlags = HideFlags.DontSave };
                if (material.HasProperty("_Color")) material.color = color;
                if (wound)
                {
                    material.SetTexture("_FleshTex", PreviewTexture(view, new Color(.42f, .10f, .08f)));
                    material.SetTexture("_BloodTex", PreviewTexture(view, new Color(.18f, .005f, .003f)));
                }
                if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.15f);
                materials[i] = material; view.Owned.Add(material);
            }
            go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            return go;
        }

        private static Texture2D PreviewTexture(ClothingCaseView view, Color color)
        {
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.DontSave };
            texture.SetPixels(new[] { color, color * .75f, color * 1.12f, color * .9f });
            texture.Apply(); view.Owned.Add(texture); return texture;
        }

        private static void AddDiagnosticViews(Work work, ClothingCaseView view, string directory, bool partial)
        {
            string path = Directory.GetFiles(directory, "case-*.json.gz").Single();
            JObject capture;
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip)) capture = JObject.Parse(reader.ReadToEnd());
            work.Report.Capture = path;
            int[] selected = capture["Cuts"]?[0]?["Selected"]?.ToObject<int[]>();
            if (selected != null)
            {
                work.Report.SelectedTriangles = selected.Length;
            }
            if (selected != null && partial)
            {
                var triangles = work.Fixture.Mesh.Submeshes.SelectMany(s => Enumerable.Range(0, s.Length / 3)
                    .Select(i => new[] { s[i * 3], s[i * 3 + 1], s[i * 3 + 2] })).ToArray();
                Mesh selection = new Mesh { name = "Selected source triangles", hideFlags = HideFlags.DontSave,
                    indexFormat = work.Source.indexFormat, vertices = work.Source.vertices, normals = work.Source.normals };
                selection.triangles = selected.SelectMany(i => triangles[i]).ToArray(); selection.RecalculateBounds();
                view.Owned.Add(selection);
                var go = Surface(view, work.Space, "Selected source triangles (yellow)", selection, Color.yellow);
                if (go != null) view.Classification.Add(go);
            }
            foreach (JToken boundary in capture["Boundaries"] as JArray ?? new JArray())
            {
                float[][] positions = boundary["Positions"]?.ToObject<float[][]>();
                if (positions == null) continue;
                var set = new ClothingCaseView.EdgeSet { Space = work.Space, Positions = FixtureMesh.Vectors(positions),
                    Plane = boundary["PlaneEdges"]?.ToObject<int[][]>(), Selection = boundary["SelectionEdges"]?.ToObject<int[][]>(),
                    Open = boundary["ExistingOpenEdges"]?.ToObject<int[][]>(),
                    // Schema 2 prefixes each loop with its cut index, not a vertex.
                    Loops = boundary["Loops"]?.ToObject<int[][]>()?.Select(loop => loop.Skip(1).ToArray()).ToArray() };
                view.Edges.Add(set);
                if (!partial) continue;
                int[][] submeshes = boundary["ExteriorTriangles"]?.ToObject<int[][]>();
                if (submeshes == null) continue;
                Mesh mesh = new Mesh { name = "Partial " + boundary["Side"], hideFlags = HideFlags.DontSave,
                    indexFormat = IndexFormat.UInt32, vertices = set.Positions, subMeshCount = submeshes.Length };
                for (int s = 0; s < submeshes.Length; s++) mesh.SetTriangles(submeshes[s], s, false);
                mesh.RecalculateNormals(); mesh.RecalculateBounds(); view.Owned.Add(mesh);
                var go = Surface(view, work.Space, mesh.name, mesh, new Color(0.8f, 0.25f, 0.1f));
                if (go != null) view.Partial.Add(go);
            }
        }
    }
}
