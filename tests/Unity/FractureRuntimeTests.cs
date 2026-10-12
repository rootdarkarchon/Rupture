#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using Rupture;
using Rupture.Dismemberment.Classes.Fractures;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.BloodStreams;
using UnityEngine;
using Debug = UnityEngine.Debug;

[UnityEditor.InitializeOnLoad]
public static class FractureRuntimeTests
{
    private const string PendingRun = "RuptureFractureTests";
    private static int _cases;
    private static string Output => Directory.GetParent(Application.dataPath).FullName;
    static FractureRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool(PendingRun, false)) return;
            UnityEditor.SessionState.SetBool(PendingRun, false);
            new GameObject("Fracture test runner").AddComponent<Runner>().StartCoroutine(Execute());
        };
    }
    public sealed class Runner : MonoBehaviour { }
    public static void RunBatch() { UnityEditor.SessionState.SetBool(PendingRun, true); UnityEditor.EditorApplication.EnterPlaymode(); }
    private static void Check(bool condition, string text)
    { if (!condition) throw new Exception(text); _cases++; Debug.Log("PASS: " + text); }

    private static IEnumerator Execute()
    {
        IEnumerator tests = Run();
        while (true)
        {
            object next;
            try { if (!tests.MoveNext()) break; next = tests.Current; }
            catch (Exception exception) { Debug.LogException(exception); UnityEditor.EditorApplication.Exit(1); yield break; }
            yield return next;
        }
        UnityEditor.EditorApplication.Exit(0);
    }

    private static IEnumerator Run()
    {
        LimbSeverMaterialProvider.Material = new Material(Shader.Find("Rupture/LimbSeverSurface"));
        LoadNativeTextures();
        var fixture = new Fixture();
        var snapshot = new FractureSurface.Snapshot(fixture.Cloth.sharedMesh);
        double milliseconds = 0;
        // Exercise all sites/counts and compare the actual shared primitive's limits.
        for (int part = 3; part <= 6; part++)
        {
            var covered = new HashSet<string>();
            for (int seed = 0; seed < 100 && covered.Count < 4; seed++)
            {
                var logical = new FractureAppearance("preview-" + seed, part);
                string key = logical.Level + "/" + logical.SiteCount;
                if (!covered.Add(key)) continue;
                for (int site = 0; site < logical.SiteCount; site++)
                {
                var appearance = logical.ForSite(site);
                var timer = Stopwatch.StartNew();
                var source = new FractureSurface(fixture.Cloth, snapshot, "segment", "end", appearance, "cloth");
                using (var visual = new FractureVisual(source, appearance, LimbSeverMaterialProvider.Material))
                {
                    milliseconds += timer.Elapsed.TotalMilliseconds;
                    Check(visual.BoneCount == appearance.BoneCount, "anatomical site/count " + part + "/" + key);
                    BoneWeight[] weights = visual.Renderer.sharedMesh.boneWeights; Color[] colors = visual.Renderer.sharedMesh.colors;
                    Check(weights.Where((w, i) => colors[i].g > .5f).All(w => w.weight0 == 1f &&
                        visual.Renderer.bones[w.boneIndex0].parent == source.Bones[source.RootIndex]), "exposed bones have single rigid anatomical anchors");
                    Check(weights.Where((w, i) => colors[i].g < .5f).Any(w => w.weight1 > .01f), "collar retains interpolated garment weights");
                    Vector3[] points = visual.Renderer.sharedMesh.vertices;
                    for (int bone = 0; bone < appearance.BoneCount; bone++)
                    {
                        int[] indices = Enumerable.Range(0, points.Length).Where(i => colors[i].g > .5f &&
                            weights[i].boneIndex0 == source.Bones.Length + bone).ToArray();
                        // The shared primitive has three seven-vertex rings. Compare
                        // ring centers, independent of diameter, jaggedness and caps.
                        Vector3 start = Vector3.zero, tip = Vector3.zero;
                        for (int j = 0; j < 7; j++) { start += points[indices[j]] / 7f; tip += points[indices[14 + j]] / 7f; }
                        Vector3 growth = source.ToBone.MultiplyVector(tip - start).normalized;
                        Vector3 limb = Vector3.Cross(source.Direction, source.Tangent).normalized * (site == 0 ? 1f : -1f);
                        Check(Vector3.Dot(growth, limb) > .7f && Vector3.Dot(growth, source.Direction) > .1f,
                            site == 0 ? "every primary-site bone grows shallowly toward hand/foot" : "every opposite-site bone grows shallowly toward torso");
                        var primitive = ProceduralLimbWoundGeometry.GenerateFractureBone(appearance.IsLeg, appearance.Level, bone,
                            appearance.BoneSeed(bone), 1f, Vector3.zero, Vector3.forward, Vector3.right, 0);
                        Check(primitive.MinimumBoneExposedReachWorld >= .0225f && primitive.MinimumBoneExposedReachWorld <= .0525f &&
                            primitive.Vertices.All(v => v.Position.magnitude <= .06001f), "fracture-only extension remains within 60mm envelope");
                    }
                    var before = new Mesh(); var after = new Mesh();
                    visual.Renderer.BakeMesh(before);
                    fixture.Twist.localRotation = Quaternion.Euler(0, 0, 22);
                    fixture.Root.localRotation = Quaternion.Euler(15, 25, -20);
                    visual.Show(true);
                    source.Raycast(source.Origin, out var poseHit);
                    var poseSample = new FractureSurface.PoseSample(source, poseHit);
                    Check(source.TryPose(poseHit, out var fullPose) && poseSample.TryPose(out var fastPose) &&
                        Vector3.Distance(fullPose, fastPose) < .00001f, "precomputed influence points match full matrix skinning");
                    Check(visual.TryGetWound(out var woundAnchor, out var woundPoint, out _) &&
                        Vector3.Distance(woundAnchor.TransformPoint(woundPoint), visual.Renderer.bones[source.Bones.Length].position) < .00001f,
                        "blood origin reuses the presented fracture attachment");
                    visual.Renderer.BakeMesh(after);
                    foreach (var bone in Enumerable.Range(0, colors.Length).Where(i => colors[i].g > .5f).GroupBy(i => weights[i].boneIndex0))
                    {
                        int first = bone.First();
                        Check(bone.All(i => Mathf.Abs(Vector3.Distance(before.vertices[first], before.vertices[i]) -
                            Vector3.Distance(after.vertices[first], after.vertices[i])) < .00002f), "each animated bone retains every pairwise distance from its root vertex");
                        Check(Quaternion.Angle(visual.Renderer.bones[bone.Key].rotation, source.Bones[source.RootIndex].rotation) < .001f,
                            "bone orientation remains anatomical rather than cloth blended");
                    }
                    if (appearance.BoneCount == 1)
                    {
                        source.Raycast(source.Origin, out var attachment); source.TryPose(attachment, out Vector3 expected);
                        Check(Vector3.Distance(visual.Renderer.bones[source.Bones.Length].position, expected) < .00001f,
                            "rigid bone remains embedded at the moving garment collar");
                    }
                    Check(Vector3.Distance(before.vertices[0], after.vertices[0]) > .001f, "collar follows animated garment");
                    fixture.Twist.localRotation = Quaternion.identity; fixture.Root.localRotation = Quaternion.identity;
                    UnityEngine.Object.Destroy(before); UnityEngine.Object.Destroy(after);
                }
                }
            }
            Check(covered.Count == 4, "upper/lower segments with and without opposite wounds covered for limb " + part);
        }

        var descriptor = new FractureAppearance("fixture-player", 3);
        int expectedSites = descriptor.SiteCount;
        var cloth = new FractureSurface(fixture.Cloth, snapshot, "segment", "end", descriptor, "b-cloth");
        var skin = new FractureSurface(fixture.Skin, new FractureSurface.Snapshot(fixture.Skin.sharedMesh), "segment", "end", descriptor, "a-skin");
        Check(cloth.Raycast(cloth.Origin, out var clothHit) && skin.Raycast(skin.Origin, out var skinHit) && clothHit.Distance > skinHit.Distance,
            "outer covering lies beyond hidden skin");
        Check(FractureSurface.Prefer(.05f, "a", .05f, "b") && !FractureSurface.Prefer(.05f, "b", .05f, "a"), "coincident owner tie is stable");
        var oppositeDescriptor = descriptor.ForSite(1);
        var oppositeCloth = new FractureSurface(fixture.Cloth, snapshot, "segment", "end", oppositeDescriptor, "b-cloth");
        using (var oppositePreview = new FractureVisual(oppositeCloth, oppositeDescriptor, LimbSeverMaterialProvider.Material))
        using (var preview = new FractureVisual(cloth, descriptor, LimbSeverMaterialProvider.Material))
        {
            preview.Show(true); oppositePreview.Show(true); Render(fixture, "fracture-preview.png");
            fixture.Twist.localRotation = Quaternion.Euler(0, 0, 18); fixture.Root.localRotation = Quaternion.Euler(12, 15, -15);
            preview.Show(true); oppositePreview.Show(true);
            Render(fixture, "fracture-animated-preview.png");
            fixture.Twist.localRotation = Quaternion.identity; fixture.Root.localRotation = Quaternion.identity;
        }
        // Runtime tracker starts with an already fractured profile and overlapping skin/clothing.
        Singleton<GameWorld>.Instance = new GameWorld(); Singleton<GameWorld>.Instance.AllAlivePlayersList.Add(fixture.Player);
        ActiveHealthController health = (ActiveHealthController)fixture.Player.HealthController;
        var effect = new FixtureFracture { BodyPart = EBodyPart.LeftArm }; health.Add(effect);
        yield return Pump(16);
        var active = ActiveVisuals();
        Check(active.Length == expectedSites && active.All(r => r.transform.parent == fixture.Cloth.transform), "tracker selects one outer clothing owner per site with no hidden skin duplicates");
        Check(PulsatingBloodStreamSystem.Active.Count == expectedSites, "each wound starts one logical light-bleed supply");
        Vector3[] original = active[0].sharedMesh.vertices;
        Check(health.Subscriptions == 5, "tracker subscribes once");
        yield return Pump(4);
        Check(ActiveVisuals().Length == expectedSites, "unchanged fracture and HP-independent state do not duplicate");
        int starts = PulsatingBloodStreamSystem.Starts;
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) { FractureSystem.Update(); FractureSystem.LateUpdate(); }
        watch.Stop(); long updateBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        // Stopwatch construction itself is 40 bytes in this Unity runtime.
        Check(updateBytes <= 64, "1000 unchanged tracker updates allocate at most the measurement stopwatch: " + updateBytes);
        Check(PulsatingBloodStreamSystem.Starts == starts, "unchanged supply does not respawn the emitter");
        health.Remove(effect); FractureSystem.LateUpdate();
        Check(ActiveVisuals().Length == 0, "treatment removes the visual before rendering");
        Check(PulsatingBloodStreamSystem.Active.Count == 0, "fracture treatment stops clothing and drip supply");
        health.Add(effect); yield return Pump(12);
        Check(ActiveVisuals().Length == expectedSites && original.SequenceEqual(ActiveVisuals()[0].sharedMesh.vertices), "refracture reproduces exact vertices");
        Check(PulsatingBloodStreamSystem.Active.Count == expectedSites, "refracture restores supply without a native bleed effect");
        RuptureConfiguration.EnableBloodEffects.Value = false; FractureSystem.LateUpdate();
        Check(ActiveVisuals().Length == 0 && health.Subscriptions == 0, "disabled blood effects dispose renderers/subscriptions");
        Check(PulsatingBloodStreamSystem.Active.Count == 0, "master blood gate stops fracture supply");
        RuptureConfiguration.EnableBloodEffects.Value = true; yield return Pump(12);
        Check(ActiveVisuals().Length == expectedSites, "reenabling restores the existing fracture");
        // A renderer replacement must suppress stale visuals immediately, and an
        // unreadable covering must not expose a fallback wound on the inner skin.
        Mesh originalCloth = fixture.Cloth.sharedMesh;
        Mesh unreadable = UnityEngine.Object.Instantiate(originalCloth); unreadable.UploadMeshData(true);
        fixture.Cloth.sharedMesh = unreadable; FractureSystem.LateUpdate();
        Check(ActiveVisuals().Length == 0, "mesh replacement hides the old owner immediately");
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(12);
        Check(ActiveVisuals().Length == 0, "unreadable outer clothing never falls through to a skin collar");
        fixture.Cloth.sharedMesh = originalCloth;
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(12);
        Check(ActiveVisuals().Length == expectedSites, "replacement with readable covering recovers");

        // The covering can end between the two sites. Resolve each site on its
        // own outer surface rather than reusing the primary wound's owner.
        Mesh shortSleeve = UnityEngine.Object.Instantiate(originalCloth);
        Vector3[] shortPoints = shortSleeve.vertices;
        for (int i = 0; i < shortPoints.Length; i++) shortPoints[i].y = -.05f + (shortPoints[i].y + .05f) * (.255f / .60f);
        shortSleeve.vertices = shortPoints; shortSleeve.RecalculateBounds();
        fixture.Cloth.sharedMesh = shortSleeve;
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(16);
        Check(ActiveVisuals().Length == 2 && ActiveVisuals().Count(r => r.transform.parent == fixture.Cloth.transform) == 1 &&
            ActiveVisuals().Count(r => r.transform.parent == fixture.Skin.transform) == 1,
            "separate wound sites independently own covered clothing and uncovered skin");
        Check(PulsatingBloodStreamSystem.Active.Count == 2, "separate surface owners still have exactly one emitter per site");
        fixture.Cloth.sharedMesh = originalCloth;
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(16);
        UnityEngine.Object.Destroy(shortSleeve);
        PulsatingBloodStreamSystem.Active.Clear(); // Simulate bounded blood-pool eviction.
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(4);
        Check(PulsatingBloodStreamSystem.Active.Count == expectedSites, "evicted fracture blood supply retries without changing geometry");

        var hands = fixture.Surface("first-person sleeve", .065f, Color.gray);
        var handSkin = new FixtureSkin(); handSkin.Renderers.Add(hands);
        fixture.Player.PlayerBody.BodySkins.Add(EBodyModelPart.Hands, handSkin);
        fixture.Skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        fixture.Cloth.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(12);
        Check(ActiveVisuals().Length == expectedSites && ActiveVisuals()[0].transform.parent == hands.transform, "first-person sleeve replaces world collar without duplicate bones");
        hands.enabled = false;
        fixture.Skin.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        fixture.Cloth.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        yield return Pump(12);
        Check(ActiveVisuals().Length == expectedSites && ActiveVisuals()[0].transform.parent == fixture.Cloth.transform, "third-person visibility restores its owner");

        var lodSkin = fixture.Surface("skin LOD1", .039f, Color.gray);
        var lodCloth = fixture.Surface("clothing LOD1", .059f, Color.gray);
        fixture.Player.PlayerBody.BodySkins[EBodyModelPart.Body].Renderers.Add(lodSkin);
        fixture.Player.PlayerBody.BodySkins[EBodyModelPart.Body].Renderers.Add(lodCloth);
        var group = fixture.Host.AddComponent<LODGroup>();
        group.SetLODs(new[] { new LOD(.4f, new Renderer[] { fixture.Skin, fixture.Cloth }), new LOD(.02f, new Renderer[] { lodSkin, lodCloth }) });
        group.RecalculateBounds();
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(16);
        Check(group.GetLODs().All(l => l.renderers.Count(r => r != null && r.name == "RPT fracture") == expectedSites), "native LODs each own precisely one collar/bone representation per wound");
        Check(PulsatingBloodStreamSystem.Active.Count == expectedSites, "multiple LOD and camera variants share one blood emitter per wound");
        health.Remove(effect); FractureSystem.LateUpdate();
        Check(group.GetLODs().All(l => l.renderers.All(r => r.name != "RPT fracture")), "treatment removes only fracture LOD registrations");
        Check(group.GetLODs()[0].renderers.Length == 2 && group.GetLODs()[1].renderers.Length == 2, "native LOD renderer lists survive cleanup");
        // Return to one source level for the independent four-limb lifecycle cases.
        fixture.Player.PlayerBody.BodySkins[EBodyModelPart.Body].Renderers.Remove(lodSkin);
        fixture.Player.PlayerBody.BodySkins[EBodyModelPart.Body].Renderers.Remove(lodCloth);
        UnityEngine.Object.Destroy(group); UnityEngine.Object.Destroy(lodSkin.gameObject); UnityEngine.Object.Destroy(lodCloth.gameObject);
        health.Add(effect);
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(12);
        for (int part = 4; part <= 6; part++) health.Add(new FixtureFracture { BodyPart = (EBodyPart)part });
        yield return Pump(25);
        Check(ActiveVisuals().Length == Enumerable.Range(3, 4).Sum(p => new FractureAppearance(fixture.Player.ProfileId, p).SiteCount), "four independently fractured limbs can coexist");
        Check(PulsatingBloodStreamSystem.Active.Count == ActiveVisuals().Length, "each visible wound of four fractures owns an independent blood supply");
        health.IsAlive = false; FractureSystem.LateUpdate();
        Check(ActiveVisuals().Length == 0 && health.Subscriptions == 0, "death removes every fracture visual and subscription");
        Check(PulsatingBloodStreamSystem.Active.Count == 0, "death stops every fracture emitter");
        // Fika downing also reports IsAlive=false. Revival may silently remove
        // health effects; rediscovery must only present the remaining native list.
        health.Effects.Clear();
        health.Effects.Add(effect);
        health.IsAlive = true;
        yield return new WaitForSecondsRealtime(.55f);
        yield return Pump(25);
        Check(ActiveVisuals().Length == expectedSites && health.Effects.Count == 1,
            "revival refreshes native fractures without restoring removed effects; visuals=" + ActiveVisuals().Length);
        health.IsAlive = false; FractureSystem.LateUpdate();
        health.Effects.Clear(); health.IsAlive = true;
        yield return new WaitForSecondsRealtime(.55f);
        yield return Pump(12);
        Check(ActiveVisuals().Length == 0 && PulsatingBloodStreamSystem.Active.Count == 0,
            "repeated revival with no native fractures creates no visuals or blood");
        FractureSystem.Reset();
        MultiplayerRuntime.Current.IsHeadless = true; health.IsAlive = true; yield return Pump(3);
        Check(ActiveVisuals().Length == 0 && health.Subscriptions == 0, "headless creates no fracture representation");
        MultiplayerRuntime.Current.IsHeadless = false;
        yield return Pump(25);
        var replacement = new ActiveHealthController(); replacement.Add(new FixtureFracture { BodyPart = EBodyPart.LeftArm });
        fixture.Player.HealthController = replacement; FractureSystem.LateUpdate();
        Check(health.Subscriptions == 0 && ActiveVisuals().Length == 0, "health-controller replacement removes old subscriptions and geometry");
        yield return new WaitForSecondsRealtime(.55f); yield return Pump(12);
        Check(replacement.Subscriptions == 5 && ActiveVisuals().Length == expectedSites, "reconnected controller discovers pre-existing fracture");
        Singleton<GameWorld>.Instance = null; FractureSystem.Update();
        Check(ActiveVisuals().Length == 0 && replacement.Subscriptions == 0, "raid teardown clears state and pending work");
        FractureNativeFixtureTests.Run();
        File.WriteAllText(Path.Combine(Output, "verification.json"), JsonUtility.ToJson(new Report { passed = true, cases = _cases,
            constructionMilliseconds = milliseconds, unchangedUpdateMilliseconds = watch.Elapsed.TotalMilliseconds,
            unchangedUpdateAllocatedBytes = updateBytes, unityVersion = Application.unityVersion }, true));
    }

    private static IEnumerator Pump(int frames)
    { for (int i = 0; i < frames; i++) { FractureSystem.Update(); FractureSystem.LateUpdate(); yield return null; } }
    private static SkinnedMeshRenderer[] ActiveVisuals() => Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>()
        .Where(r => r.name == "RPT fracture" && r.enabled).ToArray();
    [Serializable] private sealed class Report { public bool passed; public int cases; public double constructionMilliseconds, unchangedUpdateMilliseconds; public long unchangedUpdateAllocatedBytes; public string unityVersion; }

    private sealed class Fixture
    {
        internal readonly GameObject Host;
        internal readonly Transform Root, End, Twist;
        internal readonly SkinnedMeshRenderer Skin, Cloth;
        internal readonly Player Player;
        internal Fixture()
        {
            Host = new GameObject("fracture fixture"); Root = new GameObject("segment").transform; Root.SetParent(Host.transform, false);
            End = new GameObject("end").transform; End.SetParent(Root, false); End.localPosition = new Vector3(0, .4f, 0);
            Twist = new GameObject("cloth twist").transform; Twist.SetParent(Root, false);
            Skin = Surface("skin", .04f, new Color(.48f, .29f, .2f)); Cloth = Surface("clothing", .06f, new Color(.14f, .22f, .14f));
            Player = Host.AddComponent<Player>(); Player.SegmentStart = Root; Player.SegmentEnd = End;
            var garment = new FixtureSkin(); garment.Renderers.Add(Skin); garment.Renderers.Add(Cloth);
            Player.PlayerBody.BodySkins.Add(EBodyModelPart.Body, garment); Player.PlayerBody.BodySkins.Add(EBodyModelPart.Feet, garment);
        }
        internal SkinnedMeshRenderer Surface(string name, float depth, Color color)
        {
            var child = new GameObject(name); child.transform.SetParent(Host.transform, false);
            var renderer = child.AddComponent<SkinnedMeshRenderer>(); renderer.bones = new[] { Root, End, Twist }; renderer.rootBone = Root;
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var weights = new List<BoneWeight>(); var triangles = new List<int>();
            const int n = 12;
            for (int y = 0; y <= n; y++) for (int x = 0; x <= n; x++)
            {
                vertices.Add(new Vector3((x / (float)n - .5f) * .35f, y / (float)n * .55f - .05f, depth)); normals.Add(Vector3.forward);
                weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = .65f, boneIndex1 = 2, weight1 = .35f });
                if (x < n && y < n) { int i = y * (n + 1) + x; triangles.AddRange(new[] { i, i + 1, i + n + 2, i, i + n + 2, i + n + 1 }); }
            }
            var mesh = new Mesh { name = name, vertices = vertices.ToArray(), normals = normals.ToArray(), boneWeights = weights.ToArray(),
                bindposes = new[] { Root.worldToLocalMatrix * child.transform.localToWorldMatrix, End.worldToLocalMatrix * child.transform.localToWorldMatrix,
                    Twist.worldToLocalMatrix * child.transform.localToWorldMatrix }, triangles = triangles.ToArray() };
            renderer.sharedMesh = mesh; renderer.sharedMaterial = new Material(Shader.Find("Standard")) { color = color };
            renderer.localBounds = new Bounds(new Vector3(0, .2f, 0), Vector3.one); renderer.updateWhenOffscreen = true;
            return renderer;
        }
    }
    private static void LoadNativeTextures()
    {
        string root = Environment.GetEnvironmentVariable("RPT_FRACTURE_GAME_ROOT");
        var bundle = AssetBundle.LoadFromFile(Path.Combine(root, "EscapeFromTarkov_Data/StreamingAssets/Windows/assets/systems/effects/particlesystems/effects.bundle"));
        Check(bundle != null, "read-only installed EFT effects bundle available");
        bundle.LoadAllAssets();
        string[] textureNames = { "decal_flesh2", "decal_flesh2_nrm", "blood_splash_d", "blood_splash_n" };
        string[] properties = { "_FleshTex", "_FleshNormal", "_BloodTex", "_BloodNormal" };
        var textures = Resources.FindObjectsOfTypeAll<Texture2D>();
        for (int i = 0; i < textureNames.Length; i++)
        {
            var texture = textures.FirstOrDefault(t => t.name == textureNames[i]);
            Check(texture != null, "native fracture material texture " + textureNames[i]);
            LimbSeverMaterialProvider.Material.SetTexture(properties[i], texture);
        }
    }
    private static void Render(Fixture fixture, string file)
    {
        var cameraObject = new GameObject("preview camera"); var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(.12f, .18f, .55f); camera.transform.LookAt(new Vector3(0, .18f, .055f));
        camera.orthographic = true; camera.orthographicSize = .17f; camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.06f, .065f, .075f);
        var lightObject = new GameObject("preview light"); var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(35, -140, 0); light.intensity = 1.4f;
        RenderSettings.ambientLight = new Color(.45f, .45f, .45f);
        var target = new RenderTexture(768, 768, 24); target.Create(); camera.targetTexture = target; camera.Render();
        var previous = RenderTexture.active; RenderTexture.active = target;
        var texture = new Texture2D(768, 768, TextureFormat.RGB24, false); texture.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); texture.Apply();
        File.WriteAllBytes(Path.Combine(Output, file), texture.EncodeToPNG()); RenderTexture.active = previous;
        UnityEngine.Object.Destroy(texture); camera.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target);
        UnityEngine.Object.Destroy(cameraObject); UnityEngine.Object.Destroy(lightObject);
    }
}
#endif
