using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Rupture.Dismemberment.Classes.Limbs;

namespace Rupture.ClothingHarness
{
    public sealed class ClothingHarnessWindow : EditorWindow
    {
        private const string InstalledPath = "Assets/Editor/RuptureClothing";
        private static string Project => Directory.GetParent(Application.dataPath).FullName;
        [SerializeField] private string fixtures = "";
        [SerializeField] private string garmentFilter = "";
        [SerializeField] private string search = "";
        [SerializeField] private int statusFilter;
        [SerializeField] private int seed = 123456;
        [SerializeField] private bool calibrate;
        private string runDirectory, error;
        private ClothingCatalog catalog;
        private GameObject root;
        private readonly List<CutCaseReport> cases = new List<CutCaseReport>();
        private readonly Queue<CutCaseReport> pending = new Queue<CutCaseReport>();
        private CutCaseReport selected;
        private Vector2 scroll;
        private bool running;
        private object sourceStamp;

        private sealed class SourceStamp
        {
            public string Source, File, Hash, CompiledHash;
        }

        [MenuItem("Tools/Rupture/Clothing Cutter Harness")]
        public static void Open() => GetWindow<ClothingHarnessWindow>("Clothing Cutter");

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(fixtures)) fixtures = Path.Combine(Project, "ClothingFixtures");
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Clear;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Clear;
            Clear();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Production limb cutter · garment LODs", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Four independent clones per garment: Left Proximal, Left Distal, Right Proximal, Right Distal. Upper garments test arms; lower garments test legs. Select a case to inspect its exact failure stage and boundary evidence.", MessageType.Info);
            fixtures = EditorGUILayout.TextField("Fixture directory", fixtures);
            garmentFilter = EditorGUILayout.TextField("Catalog name/path filter", garmentFilter);
            seed = EditorGUILayout.IntField("Wound seed", seed);
            calibrate = EditorGUILayout.Toggle("Measure all candidate offsets", calibrate);
            using (new EditorGUI.DisabledScope(running))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Run catalog")) Guard(Start);
                if (GUILayout.Button("Sync production sources")) Guard(SyncSources);
                if (GUILayout.Button("Clear scene")) Clear();
                EditorGUILayout.EndHorizontal();
            }
            if (running && GUILayout.Button("Stop after current case")) { running = false; SaveReport("Stopped"); }
            if (!running && pending.Count > 0 && GUILayout.Button("Resume"))
                Guard(() => { ValidateSources(); running = true; });
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            if (cases.Count == 0) return;
            EditorGUILayout.LabelField($"{cases.Count - pending.Count}/{cases.Count} completed · " +
                string.Join(" · ", cases.GroupBy(c => c.Status).Select(g => g.Key + " " + g.Count())));
            EditorGUILayout.BeginHorizontal();
            search = EditorGUILayout.TextField("Result filter", search);
            statusFilter = EditorGUILayout.Popup(statusFilter, new[] { "All", "Failed", "Unsupported", "Passed", "Pending", "Unreadable" }, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply visibility filter"))
                foreach (CutCaseReport c in cases) if (c.View != null) c.View.gameObject.SetActive(Matches(c));
            if (GUILayout.Button("Show all clones"))
                foreach (CutCaseReport c in cases) if (c.View != null) c.View.gameObject.SetActive(true);
            if (GUILayout.Button("Open reports")) EditorUtility.RevealInFinder(runDirectory);
            EditorGUILayout.EndHorizontal();

            if (selected != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(selected.Garment + " · " + selected.Side + " " + selected.Level, EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(selected.Status + " / " + selected.Stage + "\nEffective cut: " + (selected.EffectiveLevel ?? "none") + " " + selected.FractionOffset.ToString("P0") + "\n" + selected.Reason,
                    selected.Status == "Failed" ? MessageType.Warning : MessageType.Info);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Frame clone")) Frame(selected);
                using (new EditorGUI.DisabledScope(running || pending.Count > 0))
                    if (GUILayout.Button("Rerun selected (current seed)")) Guard(RerunSelected);
                if (GUILayout.Button("Open case evidence") && selected.EvidenceDirectory != null)
                    EditorUtility.RevealInFinder(Path.Combine(selected.EvidenceDirectory, "result.json"));
                if (GUILayout.Button("Save preview PNG")) Guard(() => EditorUtility.RevealInFinder(ClothingPreviewCapture.Save(selected)));
                EditorGUILayout.EndHorizontal();
                foreach (RendererReport r in selected.Renderers)
                {
                    EditorGUILayout.LabelField(r.Renderer + " / " + r.Status + " / " + r.Stage +
                        " / selected " + r.SelectedTriangles + " / loops " + r.BodyLoops + "/" + r.GibLoops, EditorStyles.wordWrappedLabel);
                    foreach (var attempt in r.Attempts)
                        EditorGUILayout.LabelField(attempt.Level + " " + attempt.FractionOffset.ToString("P0") +
                            (attempt.Cached ? " (cached)" : "") + " / " + attempt.Stage + " / " + (attempt.Failure ?? "passed"), EditorStyles.wordWrappedLabel);
                    foreach (var cloth in r.Clothing ?? new List<ProceduralLimbWoundGeometry.FoldDiagnostic>())
                        EditorGUILayout.LabelField($"{cloth.Face}: {cloth.Status}; diameter {cloth.DiameterWorld*1000:F1} / threshold {cloth.ThresholdWorld*1000:F1} mm; {cloth.Reason}", EditorStyles.wordWrappedLabel);
                }
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (CutCaseReport c in cases.Where(Matches))
                if (GUILayout.Button($"[{c.Status}] {c.Garment} | {c.Side} {c.Level} | {c.Stage}",
                    selected == c ? EditorStyles.miniButtonMid : EditorStyles.miniButton))
                { selected = c; Frame(c); }
            EditorGUILayout.EndScrollView();
        }

        private bool Matches(CutCaseReport c)
        {
            string status = new[] { "All", "Failed", "Unsupported", "Passed", "Pending", "Unreadable" }[statusFilter];
            return (status == "All" || c.Status == status) &&
                (c.Garment + " " + c.Bundle + " " + c.Side + " " + c.Level + " " + c.Stage + " " + c.Reason)
                    .IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Guard(Action action)
        {
            try { error = null; action(); }
            catch (Exception exception) { error = exception.ToString(); Debug.LogException(exception); }
        }

        private void Start()
        {
            ValidateSources();
            var loaded = JsonConvert.DeserializeObject<ClothingCatalog>(File.ReadAllText(Path.Combine(fixtures, "catalog.json")));
            if (loaded.Schema != 1 || loaded.Garments == null) throw new InvalidDataException("Unsupported fixture catalog");
            GarmentEntry[] entries = loaded.Garments.Where(e =>
                garmentFilter.Split('|').Any(filter => (e.Label + " " + e.Bundle).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToArray();
            if (entries.Length == 0) throw new InvalidDataException("No garments match the catalog filter");
            Clear(); catalog = loaded;
            runDirectory = Path.Combine(Project, "ClothingHarnessRuns", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(runDirectory);
            File.Copy(Path.Combine(InstalledPath, "production-sources.json"), Path.Combine(runDirectory, "production-sources.json"));
            sourceStamp = JsonConvert.DeserializeObject(File.ReadAllText(Path.Combine(InstalledPath, "production-sources.json")));
            root = ClothingCutRunner.NewObject("RPT Clothing Cutter Harness", null);
            foreach (GarmentEntry entry in entries)
                foreach (bool left in new[] { true, false })
                    foreach (LimbSeverLevel level in new[] { LimbSeverLevel.Proximal, LimbSeverLevel.Distal })
                    foreach (float offset in calibrate ? new[] { 0f, -0.05f, 0.05f } : new[] { 0f })
                    {
                        CutCaseReport c = ClothingCutRunner.NewCase(entry, left, level, unchecked((uint)seed));
                        if (calibrate)
                        {
                            c.CalibrationOffset = offset;
                            c.Id += "-offset" + offset.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                        }
                        cases.Add(c); pending.Enqueue(c);
                    }
            running = true; SaveReport("Running");
        }

        private void Tick()
        {
            if (!running || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                // Prevent a long catalog run from mixing source revisions silently.
                ValidateSources();
                if (pending.Count == 0) { Finish(); return; }
                CutCaseReport c = pending.Dequeue();
                Execute(c, cases.IndexOf(c));
                SaveReport(pending.Count == 0 ? "Complete" : "Running");
                if (pending.Count == 0) Finish();
                Repaint();
            }
            catch (Exception exception)
            {
                running = false; error = exception.ToString();
                SaveReport("HarnessError"); Debug.LogException(exception);
            }
        }

        private void Execute(CutCaseReport c, int index)
        {
            ClothingCutRunner.Run(c, fixtures, runDirectory, root.transform,
                new Vector3((index % 4) * 2.4f, 0, -(index / 4) * 2.4f));
            if ((index + 1) % 25 == 0) Debug.Log($"Clothing harness: {index + 1}/{cases.Count} cases processed");
        }

        private void Finish()
        {
            running = false; SaveReport("Complete");
            selected = cases.FirstOrDefault(c => c.Status == "Failed") ?? cases.FirstOrDefault();
            if (!Application.isBatchMode && selected != null) Frame(selected);
            Debug.Log("Clothing harness complete: " + string.Join(", ", cases.GroupBy(c => c.Status).Select(g => g.Count() + " " + g.Key)) + ". " + runDirectory);
        }

        private void RerunSelected()
        {
            ValidateSources();
            CutCaseReport previous = selected;
            int index = cases.IndexOf(previous);
            if (previous.View != null) DestroyImmediate(previous.View.gameObject);
            selected = ClothingCutRunner.NewCase(previous.Entry, previous.Side.StartsWith("Left", StringComparison.Ordinal),
                (LimbSeverLevel)Enum.Parse(typeof(LimbSeverLevel), previous.Level), unchecked((uint)seed));
            selected.CalibrationOffset = previous.CalibrationOffset;
            // A separate run preserves the old seed/source evidence without
            // invoking the production capture writer against existing filenames.
            runDirectory = Path.Combine(Project, "ClothingHarnessRuns", "rerun-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(runDirectory);
            File.Copy(Path.Combine(InstalledPath, "production-sources.json"), Path.Combine(runDirectory, "production-sources.json"));
            cases[index] = selected; Execute(selected, index); SaveReport("SelectedCaseRerun"); Frame(selected);
        }

        private static void Frame(CutCaseReport c)
        {
            if (c.View == null) return;
            c.View.gameObject.SetActive(true); Selection.activeGameObject = c.View.gameObject;
            if (SceneView.lastActiveSceneView != null)
            {
                Renderer[] renderers = c.View.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0)
                {
                    Bounds bounds = renderers[0].bounds;
                    foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);
                    SceneView.lastActiveSceneView.Frame(bounds, false);
                }
            }
        }

        private void SaveReport(string state)
        {
            if (string.IsNullOrEmpty(runDirectory)) return;
            Directory.CreateDirectory(runDirectory);
            File.WriteAllText(Path.Combine(runDirectory, "summary.json"), JsonConvert.SerializeObject(new
            {
                Schema = 1, State = state, Error = error, Unity = Application.unityVersion, Fixtures = fixtures, Calibration = calibrate,
                CatalogHash = catalog?.CatalogHash, Sources = sourceStamp, ExpectedCases = cases.Count,
                CompletedCases = cases.Count(c => c.Status != "Pending"),
                Counts = cases.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count()),
                FailureGroups = cases.Where(c => c.Status == "Failed").GroupBy(c => c.Stage + ": " + c.Reason)
                    .Select(g => new { Reason = g.Key, Count = g.Count(), Cases = g.Select(c => c.Id).ToArray() }), Cases = cases
            }, Formatting.Indented));
            Func<string, string> q = s => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var csv = new StringBuilder("id,garment,side,level,effectiveLevel,fractionOffset,seed,status,stage,reason,milliseconds\n");
            foreach (CutCaseReport c in cases)
                csv.AppendLine(string.Join(",", new[] { c.Id, c.Garment, c.Side, c.Level, c.EffectiveLevel, c.FractionOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture), c.Seed.ToString(), c.Status, c.Stage,
                    c.Reason, c.Milliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) }.Select(q)));
            File.WriteAllText(Path.Combine(runDirectory, "cases.csv"), csv.ToString());
        }

        private static SourceStamp[] ReadStamp() => JsonConvert.DeserializeObject<SourceStamp[]>(
            File.ReadAllText(Path.Combine(InstalledPath, "production-sources.json")));

        private static void ValidateSources()
        {
            foreach (SourceStamp item in ReadStamp())
            {
                string compiled = Path.Combine(InstalledPath, "Shared", item.File);
                if (!string.Equals(FixtureIO.Hash(item.Source), item.Hash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(FixtureIO.Hash(compiled), item.CompiledHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Production source changed: " + item.File +
                        ". Sync production sources, allow compilation, then rerun the catalog/filter. Existing results retain their old source hashes.");
            }
        }

        private void SyncSources()
        {
            SourceStamp[] stamp = ReadStamp();
            if (selected != null) garmentFilter = selected.Bundle;
            Clear();
            foreach (SourceStamp item in stamp)
            {
                string code = File.ReadAllText(item.Source);
                if (Regex.IsMatch(code, @"(?m)^namespace ([\w.]+);\r?$"))
                    code = Regex.Replace(code, @"(?m)^namespace ([\w.]+);\r?$", "namespace $1 {") + "\n}\n";
                string target = Path.Combine(InstalledPath, "Shared", item.File);
                File.WriteAllText(target, code, new UTF8Encoding(false));
                item.Hash = FixtureIO.Hash(item.Source); item.CompiledHash = FixtureIO.Hash(target);
            }
            File.WriteAllText(Path.Combine(InstalledPath, "production-sources.json"), JsonConvert.SerializeObject(stamp, Formatting.Indented));
            AssetDatabase.Refresh();
        }

        private void Clear()
        {
            if (running && !string.IsNullOrEmpty(runDirectory)) SaveReport("Stopped");
            running = false; pending.Clear();
            if (root != null) DestroyImmediate(root);
            root = null; cases.Clear(); selected = null;
            ProceduralLimbCutGeometry.ResetCaches();
        }

        // -executeMethod Rupture.ClothingHarness.ClothingHarnessWindow.RunBatch
        // Optional: -rptFixtures <directory> -rptGarment <substring> -rptSeed <uint>
        public static void RunBatch()
        {
            var window = CreateInstance<ClothingHarnessWindow>();
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                Func<string, string> arg = key => { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; };
                window.fixtures = arg("-rptFixtures") ?? Path.Combine(Project, "ClothingFixtures");
                window.garmentFilter = arg("-rptGarment") ?? "";
                window.calibrate = Array.IndexOf(args, "-rptCalibrate") >= 0;
                if (arg("-rptSeed") != null) window.seed = unchecked((int)uint.Parse(arg("-rptSeed")));
                window.Start();
                if (arg("-rptRetryReport") != null)
                {
                    var prior = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(arg("-rptRetryReport")));
                    var retry = new System.Collections.Generic.HashSet<string>(prior["Cases"]
                        .Where(c => (string)c["Stage"] == "diagnostic-export").Select(c => (string)c["Id"]));
                    window.cases.RemoveAll(c => !retry.Contains(c.Id));
                    window.pending.Clear();
                    foreach (var c in window.cases) window.pending.Enqueue(c);
                    window.SaveReport("Running");
                }
                while (window.running) window.Tick();
                if (!string.IsNullOrEmpty(window.error)) throw new InvalidOperationException(window.error);
                if (Array.IndexOf(args, "-rptPreviews") >= 0)
                    foreach (CutCaseReport c in window.cases) ClothingPreviewCapture.Save(c);
                // Cutter rejections are findings, not harness infrastructure errors.
                EditorApplication.Exit(0);
            }
            catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
        }
    }
}
