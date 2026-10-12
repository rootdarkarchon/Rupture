using System;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;

namespace Rupture;

internal static class MultiplayerRuntime
{
    internal static IMultiplayerRuntime Current { get; private set; } = new StandaloneRuntime();

    internal static void Initialize()
    {
        if (!Chainloader.PluginInfos.ContainsKey("com.fika.core"))
        {
            RupturePlugin.Log.Initialization("Rupture running standalone; Fika is not loaded.");
            return;
        }

        IMultiplayerRuntime runtime = null;
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(RupturePlugin).Assembly.Location), "Rupture.Fika.dll");
            Assembly assembly = Assembly.LoadFrom(path);
            if (assembly.GetName().Version != typeof(RupturePlugin).Assembly.GetName().Version)
                throw new InvalidOperationException("Rupture.Fika.dll must match the installed Rupture version.");
            Type type = assembly.GetType("Rupture.Fika.FikaRuntime", throwOnError: true);
            runtime = (IMultiplayerRuntime)Activator.CreateInstance(type, nonPublic: true);
            // Callbacks and role checks during initialization must see the Fika runtime.
            Current = runtime;
            runtime.Initialize();
            RupturePlugin.Log.Initialization("Rupture Fika integration initialized.");
        }
        catch
        {
            try { runtime?.Dispose(); }
            catch (Exception cleanup) { RupturePlugin.Log.LogError("Fika bridge cleanup failed: " + cleanup); }
            finally { Current = new StandaloneRuntime(); }
            throw;
        }
    }

    internal static void Shutdown()
    {
        try { Current.Dispose(); }
        finally { Current = new StandaloneRuntime(); }
    }
}
