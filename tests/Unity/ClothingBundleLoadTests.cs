#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Rupture.Clothing;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class ClothingBundleLoadTests
{
    [Serializable] public sealed class Entry { public string original, patched, request, key; public string[] meshes; public int bytes; }
    [Serializable] public sealed class Manifest { public Entry[] bundles; }
    [Serializable] public sealed class Source { public string key, path; public string[] dependencies; }
    [Serializable] public sealed class Discovery { public string[] roots; public Source[] sources; }
    private const string Pending = "RuptureClothingBundleLoad";
    static ClothingBundleLoadTests()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            new GameObject("Clothing bundle loading tests").AddComponent<ClothingBundleLoadRunner>();
        };
    }
    public static void RunBatch() { SessionState.SetBool(Pending, true); EditorApplication.EnterPlaymode(); }
}

public sealed class ClothingBundleLoadRunner : MonoBehaviour
{
    private IEnumerator Start()
    {
        IEnumerator tests = Execute();
        while (true)
        {
            object current;
            try { if (!tests.MoveNext()) break; current = tests.Current; }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); yield break; }
            yield return current;
        }
        EditorApplication.Exit(0);
    }

    private IEnumerator Execute()
    {
        string root = Directory.GetParent(Application.dataPath).FullName;
        if (!ClothingBundleDecompression.Initialize(Rupture.WoundResources.PluginRoot, Debug.LogWarning))
            throw new Exception("Native clothing decoder is required for this integration run");
        var manifest = JsonUtility.FromJson<ClothingBundleLoadTests.Manifest>(File.ReadAllText(Path.Combine(root, "bundles.json")));
        var input = JsonUtility.FromJson<ClothingBundleLoadTests.Discovery>(File.ReadAllText(Path.Combine(root, "discovery.json")));
        var sources = input.sources.ToDictionary(s => s.key, s => new ClothingBundleSource { Key = s.key, Path = s.path, Dependencies = s.dependencies }, StringComparer.OrdinalIgnoreCase);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var requests = new ClothingBundleRequests(input.roots, sources, Debug.Log);
        double indexSeconds = timer.Elapsed.TotalSeconds;
        requests.Request(new[] { "unrelated.bundle" });
        if (requests.RequestedGarments != 0 || !requests.TargetFor("unrelated.bundle").IsCompleted)
            throw new Exception("Unrelated startup load started clothing IO");
        double discoverySeconds = 0, worstDiscoverySeconds = 0, rewriteSeconds = 0, worstRewriteSeconds = 0;
        Debug.Log($"PASS: metadata-only startup, seconds={indexSeconds:F4}, requested={requests.RequestedGarments}");
        var integration = ClothingLoaderIntegration.Run(input, manifest);
        while (!integration.IsCompleted) yield return null;
        integration.GetAwaiter().GetResult();
        var harmony = new Harmony("rupture.tests.clothing");
        ClothingBundleHooks.Install(harmony);
        Task callbackTests = CheckCallbacks();
        while (!callbackTests.IsCompleted) yield return null;
        callbackTests.GetAwaiter().GetResult();
        int verified = 0;
        foreach (var entry in manifest.bundles)
        {
            var originalRequest = NativeFileLoad(entry.original);
            yield return originalRequest;
            var original = originalRequest.assetBundle;
            if (original == null) throw new Exception("Original bundle failed to load: " + entry.original);
            original.LoadAllAssets();
            var originals = Resources.FindObjectsOfTypeAll<Mesh>();
            foreach (string name in entry.meshes)
            {
                var meshes = originals.Where(m => m.name == name).ToArray();
                if (meshes.Length == 0 || meshes.Any(m => m.isReadable)) throw new Exception("Expected unreadable original mesh: " + name + "; matches=" + meshes.Length + "; available=" + string.Join(",", originals.Select(m => m.name + ":" + m.isReadable)));
            }
            original.Unload(true);
            yield return null;
            var requestTimer = System.Diagnostics.Stopwatch.StartNew();
            requests.Request(new[] { entry.request });
            var targetTask = requests.TargetFor(entry.key);
            while (!targetTask.IsCompleted) yield return null;
            var target = targetTask.GetAwaiter().GetResult();
            if (target == null) throw new Exception("Requested clothing target missing: " + entry.key);
            double discoveryTime = requestTimer.Elapsed.TotalSeconds;
            discoverySeconds += discoveryTime; worstDiscoverySeconds = Math.Max(worstDiscoverySeconds, discoveryTime);
            int count = requests.RequestedGarments;
            requests.Request(new[] { entry.request });
            if (requests.RequestedGarments != count) throw new Exception("Repeated request rescanned clothing");
            var rewriteTimer = System.Diagnostics.Stopwatch.StartNew();
            var rewrite = ClothingBundleWork.Run(token => ClothingBundlePlan.Rewrite(target, token), TimeSpan.FromSeconds(30), "Test rewrite");
            while (!rewrite.IsCompleted) yield return null;
            byte[] recovered = rewrite.GetAwaiter().GetResult();
            double seconds = rewriteTimer.Elapsed.TotalSeconds;
            rewriteSeconds += seconds; worstRewriteSeconds = Math.Max(worstRewriteSeconds, seconds);
            if (!recovered.SequenceEqual(File.ReadAllBytes(entry.patched))) throw new Exception("Mono rewrite differs from losslessly verified .NET output");
            // Exercise the production Harmony prefix on Unity Mono, including an
            // actually suspended validation callback before the native file call.
            int completed = 0;
            var validate = ClothingBundleHooks.WrapValidation(async key => { await Task.Yield(); completed++; },
                () => { ClothingBundleHooks.Stage(entry.original, recovered); return Task.CompletedTask; });
            Task validation = validate(entry.original);
            while (!validation.IsCompleted) yield return null;
            validation.GetAwaiter().GetResult();
            if (completed != 1) throw new Exception("Validation did not complete exactly once");
            var request = NativeFileLoad(entry.original);
            yield return request;
            var patched = request.assetBundle;
            if (patched == null) throw new Exception("Patched bundle failed to load: " + entry.original);
            patched.LoadAllAssets();
            var loaded = Resources.FindObjectsOfTypeAll<Mesh>();
            foreach (string name in entry.meshes)
            {
                var meshes = loaded.Where(m => m.name == name).ToArray();
                if (meshes.Length == 0) throw new Exception("Recovered mesh missing: " + name);
                foreach (var mesh in meshes)
                {
                    if (!mesh.isReadable || mesh.vertices.Length == 0 || mesh.boneWeights.Length != mesh.vertexCount || mesh.bindposes.Length == 0)
                        throw new Exception("Recovered mesh data inaccessible: " + name);
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        if (mesh.GetIndices(i).Any(v => v < 0 || v >= mesh.vertexCount)) throw new Exception("Invalid recovered topology: " + name);
                    verified++;
                }
            }
            patched.Unload(true);
            yield return null;
            var reloadRequest = NativeFileLoad(entry.original);
            yield return reloadRequest;
            var reload = reloadRequest.assetBundle;
            if (reload == null) throw new Exception("Native reload failed: " + entry.original);
            reload.LoadAllAssets();
            var reloaded = Resources.FindObjectsOfTypeAll<Mesh>().Where(m => entry.meshes.Contains(m.name)).ToArray();
            if (reloaded.Length == 0 || reloaded.Any(m => m.isReadable)) throw new Exception("Prepared bytes leaked into a later load");
            reload.Unload(true);
            yield return null;
        }
        harmony.UnpatchSelf();
        File.WriteAllText(Path.Combine(root, "verification.json"), FormattableString.Invariant($"{{\"passed\":true,\"meshes\":{verified},\"bundles\":{manifest.bundles.Length},\"indexSeconds\":{indexSeconds:F4},\"discoverySeconds\":{discoverySeconds:F3},\"worstDiscoverySeconds\":{worstDiscoverySeconds:F3},\"rewriteSeconds\":{rewriteSeconds:F3},\"worstRewriteSeconds\":{worstRewriteSeconds:F3},\"unity\":\"{Application.unityVersion}\"}}"));
        Debug.Log("PASS: in-memory readability recovery and CPU skinning access for " + verified + " meshes");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AssetBundleCreateRequest NativeFileLoad(string path) => AssetBundle.LoadFromFileAsync(path);

    private static async Task CheckCallbacks()
    {
        int prepared = 0;
        var gate = new TaskCompletionSource<bool>();
        var callback = ClothingBundleHooks.WrapValidation(key => gate.Task, () => { prepared++; return Task.CompletedTask; });
        Task pending = callback("delayed");
        if (pending.IsCompleted || prepared != 0) throw new Exception("Preparation bypassed native validation");
        gate.SetResult(true);
        await pending;
        if (prepared != 1) throw new Exception("Preparation repeated");
        var expected = new InvalidOperationException("validation failure");
        callback = ClothingBundleHooks.WrapValidation(key => Task.FromException(expected), () => { prepared++; return Task.CompletedTask; });
        try { await callback("failed"); throw new Exception("Validation failure swallowed"); }
        catch (InvalidOperationException e) { if (!ReferenceEquals(e, expected)) throw; }
        if (prepared != 1) throw new Exception("Invalid bundle was prepared");
        callback = ClothingBundleHooks.WrapValidation(null, () => { prepared++; return Task.CompletedTask; });
        await callback("no-validator");
        if (prepared != 2) throw new Exception("Missing validator prevented preparation");
        callback = ClothingBundleHooks.WrapValidation(key => Task.FromCanceled(new System.Threading.CancellationToken(true)),
            () => { prepared++; return Task.CompletedTask; });
        try { await callback("cancelled"); throw new Exception("Validation cancellation swallowed"); }
        catch (OperationCanceledException) { }
        if (prepared != 2) throw new Exception("Cancelled bundle was prepared");
        var delayed = new TaskCompletionSource<int>();
        bool cancellationRequested = false;
        try
        {
            await ClothingBundleWork.RunAsync(token => { token.Register(() => cancellationRequested = true); return delayed.Task; }, TimeSpan.FromMilliseconds(30), "deliberately stalled worker");
            throw new Exception("Stalled recovery did not time out");
        }
        catch (TimeoutException) { }
        if (!cancellationRequested) throw new Exception("Timed out recovery was not cancelled");
        delayed.SetResult(42); // Late work must not replace the completed timeout outcome.
        int result = await ClothingBundleWork.Run(token => 7, TimeSpan.FromSeconds(1), "subsequent recovery");
        if (result != 7) throw new Exception("Timeout blocked subsequent recovery");
        Debug.Log("PASS: stalled recovery times out, requests cancellation and permits subsequent work");
        Debug.Log("PASS: suspended, failed, cancelled and absent validation callbacks complete correctly");
    }
}
#endif
