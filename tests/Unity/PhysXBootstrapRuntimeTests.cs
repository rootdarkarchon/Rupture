#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rupture.PhysXBackend;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;
using UnityEngine;

// EFT's installed gate has been inspected with Cecil: only registered, active
// bodies enable Script simulation. Real Unity contacts and the production native
// extraction bridge run below; no corpse or player is created by this fixture.
internal static class PhysicsExtensions
{
    internal static class UpdateController
    {
        internal struct RigidbodyData { internal Rigidbody rigidbody; }
        internal static readonly List<RigidbodyData> _rigidbodies = new List<RigidbodyData>();
        internal static bool Enabled = true;
        internal static bool UpdateEnabled;
        internal static void SupportRigidbody(Rigidbody body, float quality, object visibility)
        { if (!Enabled) return; _rigidbodies.Add(new RigidbodyData { rigidbody = body }); Check(); }
        internal static void UnsupportRigidbody(Rigidbody body)
        {
            if (!Enabled) return;
            int index = _rigidbodies.FindIndex(entry => entry.rigidbody == body);
            if (index >= 0) _rigidbodies.RemoveAt(index);
            Check();
        }
        internal static void Check() => UpdateEnabled = _rigidbodies.Any(entry => entry.rigidbody != null && entry.rigidbody.gameObject.activeInHierarchy);
        internal static bool Step()
        {
            Check();
            if (!UpdateEnabled || Physics.simulationMode != SimulationMode.Script) return false;
            Physics.Simulate(1f / 60f); return true;
        }
    }
}

[UnityEditor.InitializeOnLoad]
public static class PhysXBootstrapRuntimeTests
{
    static PhysXBootstrapRuntimeTests()
    {
        UnityEditor.EditorApplication.playModeStateChanged += state =>
        {
            if (state != UnityEditor.PlayModeStateChange.EnteredPlayMode || !UnityEditor.SessionState.GetBool("PhysXBootstrapTests", false)) return;
            UnityEditor.SessionState.SetBool("PhysXBootstrapTests", false);
            new GameObject("bootstrap fixture runner").AddComponent<PhysXBootstrapRunner>();
        };
    }
    public static void RunBatch()
    {
        UnityEditor.SessionState.SetBool("PhysXBootstrapTests", true);
        UnityEditor.EditorApplication.EnterPlaymode();
    }
    private static int _checks;
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception(name); _checks++; Debug.Log("PASS: " + name); }
    private static GameObject[] Probes() => Resources.FindObjectsOfTypeAll<GameObject>()
        .Where(go => go.name.StartsWith("__RPT_UnityPhysXGeometryProbe_", StringComparison.Ordinal)).ToArray();
    private static void Clean(UnityPhysXMeshGeometrySource source, string phase)
    {
        Check(PhysicsExtensions.UpdateController._rigidbodies.Count == 0, phase + ": no support entries");
        Check(Probes().All(go => !go.activeInHierarchy), phase + ": probes disabled before deferred destruction");
        Check(!(bool)typeof(UnityPhysXMeshGeometrySource).GetField("_subscribed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(source),
            phase + ": both callback subscriptions retired");
    }
    internal static IEnumerator Run()
    {
        Physics.simulationMode = SimulationMode.Script;
        using (var api = new NativeApi(Path.Combine(Application.dataPath, "Plugins/x86_64"), false))
        {
            UnityPhysXMeshGeometrySource Source(Action<ExternalLogLevel, string> log = null,
                Func<Rigidbody, IDisposable> schedule = null) => new UnityPhysXMeshGeometrySource(api, 0, log,
                    schedule ?? PhysicsBootstrapProbeLease.TrySchedule);

            Check(!PhysicsExtensions.UpdateController.Step(), "empty support list skips native simulation");
            using (var source = Source())
            {
                source.BeginRaid();
                Check(PhysicsExtensions.UpdateController._rigidbodies.Count == 1, "probe schedules exactly one supported body without a death");
                int steps = 0;
                for (int frame = 0; frame < 8 && !source.IsReady; frame++)
                {
                    source.Update();
                    if (PhysicsExtensions.UpdateController.Step()) steps++;
                    yield return null;
                    source.Update();
                }
                Check(source.IsReady && steps > 0 && steps <= 8, "production bootstrap ready before any death or player exists");
                Check(source.Status.Contains("mappedShapes:0/refreshes:0"), "ProximityCache bootstrap performs zero global shape scans");
                var desc = NativeApi.SceneDesc.Create(0, source.SourceSceneSettings);
                api.Check(api.CreateScene(ref desc, out IntPtr scene), "fixture create external scene with actual captured source parity");
                Check(scene != IntPtr.Zero, "actual external PhysX scene created before any death");
                api.DestroyScene(scene);
                Clean(source, "success");
                Check(!PhysicsExtensions.UpdateController.Step(), "successful bootstrap closes the native simulation gate");
                yield return null;
                Check(Probes().Length == 0, "successful probe objects destroyed on the next frame");

                var floor = new GameObject("triangle floor");
                var mesh = new Mesh { vertices = new[] { new Vector3(-1,0,-1), new Vector3(1,0,-1), new Vector3(0,0,1) }, triangles = new[] { 0,2,1 } };
                var collider = floor.AddComponent<MeshCollider>(); collider.sharedMesh = mesh;
                Physics.SyncTransforms();
                source.PrepareProximity(Vector3.zero, Vector3.zero, 3f, new Collider[] { collider });
                Check(source.Read(collider).Indices.Length == 3, "proximity mapping still extracts real Unity-cooked geometry");
                Check(source.TransformsSyncedThisFrame && source.Status.Contains("syncs:") && !source.Status.Contains("syncs:0/"),
                    "a mapping runs one timed Physics.SyncTransforms and reports it: " + source.Status);
                source.PrepareProximity(Vector3.zero, Vector3.zero, 3f, Array.Empty<Collider>());
                Check(source.Read(collider).Indices.Length == 3, "an empty proximity request keeps the frame map and still resolves the mapped body mesh");
                var second = new GameObject("second triangle floor");
                var secondCollider = second.AddComponent<MeshCollider>(); secondCollider.sharedMesh = mesh;
                Physics.SyncTransforms();
                Check(source.Read(secondCollider).Indices.Length == 3, "an unmapped collider resolves through a targeted overlap");
                Check(source.ShapeMapRefreshes == 0 && source.TargetedShapeMappings >= 2 && source.Status.Contains("refreshes:0"),
                    "corpse-build style reads never trigger the whole-scene shape remap: " + source.Status);
                collider.enabled = false;
                bool shapeless = false;
                try { source.Read(collider); }
                catch (UnityPhysXShapeUnavailableException exception) { shapeless = exception.NoNativeShape; }
                Check(shapeless && source.ShapeMapRefreshes == 0, "a disabled collider reports no native shape without any shape remap");
                source.ClearNoShapeCache();
                Check(source.ShapeMapRefreshes == 0 && source.Status.Contains("noShapeCached:0/"), "retiring the negative shape cache for a staged late scene never remaps the scene: " + source.Status);

                // Terrain heightfields are kept for the raid: one native read, then cache hits across frames until the data changes.
                var terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(20, 5, 20) };
                var terrainObject = Terrain.CreateTerrainGameObject(terrainData);
                terrainObject.transform.position = new Vector3(100, 0, 100);
                var terrainCollider = terrainObject.GetComponent<TerrainCollider>();
                MeshGeometryRepository.Configure(source);
                Physics.SyncTransforms();
                Check(!MeshGeometryRepository.IsCached(terrainCollider), "a terrain is not cached before its first read");
                HeightFieldGeometry heightField = MeshGeometryRepository.Get(terrainCollider);
                Check(MeshGeometryRepository.IsCached(terrainCollider) && source.Status.Contains("extractedHeightFields:1"), "the first terrain read extracts the heightfield once: " + source.Status);
                yield return null;
                Check(MeshGeometryRepository.IsCached(terrainCollider) && ReferenceEquals(MeshGeometryRepository.Get(terrainCollider), heightField) &&
                    source.Status.Contains("extractedHeightFields:1") && MeshGeometryRepository.Describe().Contains("terrainCache=entries:1/reads:1/hits:1/"),
                    "a cached terrain survives frames without another native read: " + MeshGeometryRepository.Describe());
                terrainData.SetHeights(0, 0, new float[33, 33]);
                Check(!MeshGeometryRepository.IsCached(terrainCollider) && MeshGeometryRepository.Describe().Contains("heightmapEdits:1"),
                    "a heightmap edit retires the cached heightfield: " + MeshGeometryRepository.Describe());
                Physics.SyncTransforms();
                Check(!ReferenceEquals(MeshGeometryRepository.Get(terrainCollider), heightField) && MeshGeometryRepository.IsCached(terrainCollider) &&
                    MeshGeometryRepository.Describe().Contains("invalidations:1"), "an edited terrain is read again and cached again: " + MeshGeometryRepository.Describe());
                terrainData.heightmapResolution = 65;
                Check(!MeshGeometryRepository.IsCached(terrainCollider), "a changed heightmap resolution retires the cached heightfield");
                MeshGeometryRepository.Clear();
                Check(!MeshGeometryRepository.IsCached(terrainCollider) && MeshGeometryRepository.Describe().Contains("entries:0/"), "ResetRaid-style clearing drops the terrain cache");
                UnityEngine.Object.Destroy(terrainObject); UnityEngine.Object.Destroy(terrainData);
                UnityEngine.Object.Destroy(second); UnityEngine.Object.Destroy(floor); UnityEngine.Object.Destroy(mesh);
            }

            using (var source = Source())
            {
                source.BeginRaid();
                for (int frame = 0; frame < 8 && !source.IsReady; frame++)
                { source.Update(); PhysicsExtensions.UpdateController.Step(); yield return null; source.Update(); }
                Check(source.IsReady && source.Status.Contains("refreshes:0"), "bootstrap never maps globally, a full prebake included: " + source.Status);
                source.PrepareAll();
                Check(source.ShapeMapRefreshes == 1, "PrepareAll performs the one whole-scene shape map: " + source.Status);
                Clean(source, "full prebake");
            }

            using (var source = Source())
            {
                source.BeginRaid(); source.ResetRaid(); source.ResetRaid();
                Clean(source, "reset before first contact, repeated reset");
                source.BeginRaid(); source.Dispose(); source.Dispose();
                Clean(source, "dispose before first contact, repeated dispose");
            }

            using (var source = Source(log: (level, message) => { if (level == ExternalLogLevel.Lifecycle) throw new Exception("injected bootstrap completion failure"); }))
            {
                source.BeginRaid(); PhysicsExtensions.UpdateController.Step(); yield return null; source.Update();
                Check(source.Failure != null && !source.IsReady, "bootstrap exception fails open");
                Clean(source, "bootstrap exception");
            }

            using (var source = Source())
            {
                source.BeginRaid();
                for (int frame = 0; frame < 121; frame++) { source.Update(); yield return null; } // Inject a scheduler that never steps.
                Check(source.Failure != null, "missing contact cannot retain a support lease forever");
                Clean(source, "exhausted scheduled updates");
            }

            using (var source = Source())
            {
                PhysicsExtensions.UpdateController.Enabled = false;
                source.BeginRaid(); source.Update();
                Check(PhysicsExtensions.UpdateController._rigidbodies.Count == 0, "disabled controller does not create a false lease");
                PhysicsExtensions.UpdateController.Enabled = true;
                source.Update();
                Check(PhysicsExtensions.UpdateController._rigidbodies.Count == 1, "late controller enable schedules probe without gameplay activity");
                PhysicsExtensions.UpdateController.Step(); yield return null; source.Update();
                Check(source.IsReady, "late enable still bootstraps before a death");
                Clean(source, "late enable success");
            }

            using (var source = Source(schedule: body => throw new Exception("injected scheduling failure")))
            {
                bool failed = false;
                try { source.BeginRaid(); } catch (Exception) { failed = true; }
                Check(failed, "BeginRaid reports scheduling failure");
                Clean(source, "BeginRaid exception");
            }

            using (var source = Source())
            {
                source.BeginRaid(); PhysicsExtensions.UpdateController.Enabled = false; source.Dispose();
                Clean(source, "controller disabled before disposal");
                PhysicsExtensions.UpdateController.Enabled = true;
            }

            using (var source = Source())
            {
                Time.timeScale = 0f;
                yield return null;
                source.BeginRaid();
                for (int frame = 0; frame < 121; frame++) { source.Update(); yield return null; }
                Check(source.Failure == null && PhysicsExtensions.UpdateController._rigidbodies.Count == 1,
                    "paused frames do not exhaust the scheduling bound");
                source.ResetRaid(); Time.timeScale = 1f;
                Clean(source, "reset while paused");
            }

            var unrelated = new GameObject("unrelated supported physics");
            var unrelatedBody = unrelated.AddComponent<Rigidbody>(); unrelatedBody.useGravity = false;
            PhysicsExtensions.UpdateController.SupportRigidbody(unrelatedBody, 0f, null);
            using (var source = Source())
            {
                source.BeginRaid(); source.ResetRaid();
                Check(PhysicsExtensions.UpdateController._rigidbodies.Count == 1 &&
                    PhysicsExtensions.UpdateController._rigidbodies[0].rigidbody == unrelatedBody && PhysicsExtensions.UpdateController.UpdateEnabled,
                    "probe cleanup preserves unrelated support entries and their simulation gate");
                source.BeginRaid(); PhysicsExtensions.UpdateController.Enabled = false; source.Dispose();
                Check(PhysicsExtensions.UpdateController._rigidbodies.Count == 1 &&
                    PhysicsExtensions.UpdateController._rigidbodies[0].rigidbody == unrelatedBody,
                    "disabled-controller cleanup also preserves unrelated registrations");
                PhysicsExtensions.UpdateController.Enabled = true;
            }
            PhysicsExtensions.UpdateController.UnsupportRigidbody(unrelatedBody); UnityEngine.Object.Destroy(unrelated);
            yield return null;
            Check(Probes().Length == 0, "all reset and failure paths destroy probe objects");
        }
        File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json"),
            "{\"passed\":true,\"checks\":" + _checks + ",\"deaths\":0}");
    }
}

public sealed class PhysXBootstrapRunner : MonoBehaviour
{
    private IEnumerator Start()
    {
        var test = PhysXBootstrapRuntimeTests.Run();
        while (true)
        {
            bool more;
            try { more = test.MoveNext(); }
            catch (Exception exception) { Debug.LogException(exception); (test as IDisposable)?.Dispose(); UnityEditor.EditorApplication.Exit(1); yield break; }
            if (!more) break;
            yield return test.Current;
        }
        UnityEditor.EditorApplication.Exit(0);
    }
}
#endif
