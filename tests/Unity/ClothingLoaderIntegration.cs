#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using Rupture.Clothing;
using UnityEngine;

// Small API doubles for running the unchanged production installer on Unity Mono.
// The separate ABI regression verifies these hook signatures against installed EFT.
namespace Diz.Resources
{
public interface IEasyBundle { string Key { get; } }
public sealed class EasyBundle : IEasyBundle
{
    public string Key { get; set; }
    public string _path;
    public string[] DependencyKeys;
    public Func<string, Task> _bundleCheck;
    public EasyBundle(string key, string path, string[] dependencies, Func<string, Task> validation)
    { Key = key; _path = path; DependencyKeys = dependencies; _bundleCheck = validation; }
}
}
namespace Diz.DependencyManager
{
public sealed class Node<T> { public T Data; public Node(T data) { Data = data; } }
public static class DependencyGraph<T>
{
    public static int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ProcessPendingLoads(List<Diz.DependencyManager.Node<T>> pending) { Calls++; }
}
}
namespace EFT { public enum EBodyModelPart { Body, Feet } }
public sealed class ClothingPrefab { public string path; }
public sealed class TestClothing { public EFT.EBodyModelPart BodyPart; public ClothingPrefab Prefab; }
namespace EFT { public sealed class CustomizationSolver { public TestClothing[] ClothingTemplates; } }
namespace Comfort.Common { public static class Singleton<T> { public static T Instance; } }
namespace Rupture
{
    internal static class MultiplayerRuntime
    {
        internal static readonly TestRuntime Current = new TestRuntime();
        internal sealed class TestRuntime { public bool IsHeadless; }
    }
    internal static class WoundResources
    {
        internal static string PluginRoot => System.IO.Path.Combine(Application.dataPath, "Plugins");
    }
    internal static class RupturePlugin
    {
        internal static readonly TestLogger Log = new TestLogger();
        internal sealed class TestLogger
        {
            public void Initialization(string text) { Debug.Log(text); }
            public void LogInfo(string text) { Debug.Log(text); }
            public void LogWarning(string text) { throw new Exception(text); }
        }
    }
}

internal static class ClothingLoaderIntegration
{
    internal static async Task Run(ClothingBundleLoadTests.Discovery input, ClothingBundleLoadTests.Manifest manifest)
    {
        Rupture.MultiplayerRuntime.Current.IsHeadless = true;
        ClothingBundleLoader.Install();
        Func<string, Task> untouched = key => Task.CompletedTask;
        var headless = new Diz.Resources.EasyBundle("headless", "absent.bundle", new string[0], untouched);
        if (!ReferenceEquals(headless._bundleCheck, untouched) || Harmony.GetAllPatchedMethods().Any(method =>
            Harmony.GetPatchInfo(method).Owners.Contains("com.rootdarkarchon.rupture.clothing")))
            throw new Exception("Headless installed clothing hooks or preparation");
        Rupture.MultiplayerRuntime.Current.IsHeadless = false;
        ClothingBundleLoader.Install();
        int validated = 0;
        var helpers = input.sources.ToDictionary(s => s.key,
            s => new Diz.Resources.EasyBundle(s.key, s.path, s.dependencies, async key => { await Task.Yield(); validated++; }), StringComparer.OrdinalIgnoreCase);
        Comfort.Common.Singleton<EFT.CustomizationSolver>.Instance = new EFT.CustomizationSolver
        { ClothingTemplates = input.roots.Select(r => new TestClothing { Prefab = new ClothingPrefab { path = r } }).ToArray() };
        // Exercise a discovered installed garment; the baseline need not contain WTT clothing.
        var entry = manifest.bundles.First(e => e.meshes.Length > 0 && helpers.ContainsKey(e.request));
        var pending = new List<Diz.DependencyManager.Node<Diz.Resources.IEasyBundle>> { new Diz.DependencyManager.Node<Diz.Resources.IEasyBundle>(helpers[entry.request]) };
        Diz.DependencyManager.DependencyGraph<Diz.Resources.IEasyBundle>.ProcessPendingLoads(pending);
        if (pending.Count != 1 || Diz.DependencyManager.DependencyGraph<Diz.Resources.IEasyBundle>.Calls != 1) throw new Exception("Scheduler observation changed native execution");
        Diz.DependencyManager.DependencyGraph<object>.ProcessPendingLoads(new List<Diz.DependencyManager.Node<object>>());
        await helpers[entry.key]._bundleCheck(entry.key);
        if (validated != 1) throw new Exception("Native validation did not run once");
        var request = Load(entry.original);
        while (!request.isDone) await Task.Yield();
        if (request.assetBundle == null) throw new Exception("Production loader integration failed to load bundle");
        request.assetBundle.LoadAllAssets();
        var meshes = Resources.FindObjectsOfTypeAll<Mesh>().Where(m => entry.meshes.Contains(m.name)).ToArray();
        if (meshes.Length == 0 || meshes.Any(m => !m.isReadable)) throw new Exception("Production scheduler/validation/file hooks did not recover mesh");
        request.assetBundle.Unload(true);
        new Harmony("com.rootdarkarchon.rupture.clothing").UnpatchSelf();
        Debug.Log("PASS: production installer, generic scheduler observation, suspended native validation and file interception on Unity Mono");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static AssetBundleCreateRequest Load(string path) => AssetBundle.LoadFromFileAsync(path);
}
#endif
