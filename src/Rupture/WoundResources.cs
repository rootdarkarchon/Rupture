using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace Rupture;

// Plugin-owned, explicit resource lifetime. Never discover or open unrelated bundles.
internal static class WoundResources
{
    internal static string PluginRoot => Path.Combine(BepInEx.Paths.PluginPath, "rupture");
    private static readonly Dictionary<string, Shader> Shaders = new Dictionary<string, Shader>();
    private static readonly Dictionary<string, ComputeShader> ComputeShaders = new Dictionary<string, ComputeShader>();
    private static AssetBundle _bundle;
    private static AssetBundleCreateRequest _request;
    private static bool _stopped;
    internal static bool IsLoading { get; private set; }

    internal static IEnumerator Load()
    {
        if (MultiplayerRuntime.Current.IsHeadless || _bundle != null || IsLoading) yield break;
        _stopped = false;
        string path = Path.Combine(PluginRoot, "rupture-wound.bundle");
        if (!File.Exists(path))
        {
            RupturePlugin.Log.InitializationWarning("Rupture wound bundle is missing; procedural surfaces are unavailable.");
            yield break;
        }
        IsLoading = true;
        _request = AssetBundle.LoadFromFileAsync(path);
        _request.completed += Complete;
        yield return _request;
    }

    private static void Complete(AsyncOperation operation)
    {
        _bundle = ((AssetBundleCreateRequest)operation).assetBundle;
        _request = null;
        IsLoading = false;
        if (_stopped) { Shutdown(); return; }
        if (_bundle == null)
        {
            RupturePlugin.Log.InitializationWarning("Rupture wound bundle failed to load; procedural surfaces are unavailable.");
            return;
        }
        foreach (Shader shader in _bundle.LoadAllAssets<Shader>())
            if (shader != null) Shaders[shader.name] = shader;
        foreach (ComputeShader shader in _bundle.LoadAllAssets<ComputeShader>())
            if (shader != null) ComputeShaders[shader.name] = shader;
    }

    internal static Shader GetShader(string name) => Shaders.TryGetValue(name, out Shader shader) ? shader : null;
    internal static ComputeShader GetComputeShader(string name) => ComputeShaders.TryGetValue(name, out ComputeShader shader) ? shader : null;

    internal static void Shutdown()
    {
        _stopped = true;
        Shaders.Clear();
        ComputeShaders.Clear();
        // Materials are destroyed by their owning systems before this call.
        // Do not invalidate assets still awaiting Unity's deferred Destroy pass.
        if (_bundle != null) _bundle.Unload(false);
        _bundle = null;
    }
}
