using System;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine.SceneManagement;

namespace Rupture.Ragdolls.External;

internal static class ExternalPhysicsBackendLoader
{
	private const string ManagedAssemblyName = "Rupture.PhysXBackend.dll";
	private const string RuntimeTypeName = "Rupture.PhysXBackend.ExternalPhysXRuntime";
	private static IExternalPhysicsRuntime _runtime;
	private static string _failure;
	private static bool _attempted;

	internal static IExternalPhysicsRuntime Runtime => _runtime;
	internal static string Failure => _failure;

	internal static bool TryLoad(ExternalBackendOptions options, out IExternalPhysicsRuntime runtime, out string failure)
	{
		if (_runtime != null)
		{
			runtime = _runtime;
			failure = null;
			return true;
		}
		if (_attempted)
		{
			runtime = null;
			failure = _failure;
			return false;
		}
		_attempted = true;

		try
		{
			string backendRoot = WoundResources.PluginRoot;
			string managedPath = Path.Combine(backendRoot, ManagedAssemblyName);
			if (!File.Exists(managedPath))
			{
				throw new FileNotFoundException("The opt-in external backend is not installed.", managedPath);
			}
			options.BackendRoot = backendRoot;
			options.CacheRoot = Path.Combine(backendRoot, "cache");
			Assembly assembly = Assembly.LoadFrom(managedPath);
			Type runtimeType = assembly.GetType(RuntimeTypeName, throwOnError: true, ignoreCase: false);
			if (!typeof(IExternalPhysicsRuntime).IsAssignableFrom(runtimeType))
			{
				throw new InvalidCastException($"{RuntimeTypeName} does not implement the RPT external backend contract.");
			}
			_runtime = (IExternalPhysicsRuntime)Activator.CreateInstance(runtimeType);
			_runtime.Initialize(options);
			SceneManager.sceneLoaded += OnSceneLoaded;
			runtime = _runtime;
			failure = null;
			return true;
		}
		catch (Exception exception)
		{
			_failure = $"{exception.GetType().Name}: {exception.Message}";
			try { _runtime?.Dispose(); } catch { }
			_runtime = null;
			runtime = null;
			failure = _failure;
			return false;
		}
	}

	internal static void ResetRaid()
	{
		try { _runtime?.ResetRaid(); }
		catch (Exception exception)
		{
			RupturePlugin.Log.LogWarning($"External PhysX raid reset failed: {exception.Message}");
		}
		finally
		{
			// Runtime options are raid-scoped. Unload the optional backend here so
			// environment, mesh-extraction and dispatcher changes are observed on
			// the next raid without requiring a game restart.
			Shutdown();
		}
	}

	internal static void Shutdown()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		try { _runtime?.Dispose(); } catch { }
		_runtime = null;
		_failure = null;
		_attempted = false;
	}

	private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		try { _runtime?.NotifySceneLoaded(scene); }
		catch (Exception exception)
		{
			RupturePlugin.Log.LogWarning($"External PhysX late-scene notification failed for {scene.name}: {exception.Message}");
		}
	}
}
