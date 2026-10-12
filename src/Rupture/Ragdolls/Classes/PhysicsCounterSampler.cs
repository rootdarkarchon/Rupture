using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static class PhysicsCounterSampler
{
	private const float SampleDurationSeconds = 30f;
	private const int MaximumRecorders = 192;
	private const int MaximumTimeResults = 40;
	private const int MaximumCounterResults = 60;
	private const int MaximumAvailableMarkersToLog = 64;
	private const int MaximumGateTransitions = 96;

	// These names are present in the installed Unity 2022.3 player binary but are not
	// necessarily returned by ProfilerRecorderHandle.GetAvailable in a release player.
	private static readonly string[] InstalledPhysicsMarkerNames =
	{
		"Physics.Simulate",
		"Physics.PhysX",
		"ScScene.advanceStep",
		"ScScene.collideStep",
		"ScScene.broadPhase",
		"ScScene.postBroadPhase",
		"ScScene.preRigidBodyNarrowPhase",
		"ScScene.rigidBodyNarrowPhase",
		"ScScene.secondPassNarrowPhase",
		"ScScene.postNarrowPhase",
		"ScScene.islandGen",
		"ScScene.postIslandGen",
		"ScScene.updateDynamics",
		"ScScene.beforeSolver",
		"ScScene.rigidBodySolver",
		"ScScene.postSolver",
		"ScScene.afterIntegration",
		"ScScene.updateBodiesAndShapes",
		"ScScene.updateSimulationController",
		"ScScene.finalizationPhase",
		"ScScene.constraintProjection",
		"ScScene.processLostContact",
		"ScScene.registerContactManagers",
		"ScScene.preallocateContactManagers",
		"PxsContext.contactManagerDiscreteUpdate",
		"PxsDynamics.preIntegrate",
		"PxsDynamics.parallelSolver",
		"PxsDynamics.solverStart",
		"PxsDynamics.solverConstraintPartition",
		"PxsDynamics.solverSetupSolve",
		"PxsDynamics.solverCreateFinalizeConstraints",
		"PxsDynamics.createFinalizeContacts",
		"PxsDynamics.solverEnd",
		"BroadphaseSap.batchUpdate",
		"ContactDistanceUpdateTask",
		"ContactCallbackTask",
		"ContactModifyCallback"
	};

	private sealed class Sample
	{
		internal string Category;
		internal string Name;
		internal ProfilerMarkerDataUnit Unit;
		internal ProfilerRecorder Recorder;
		internal double Sum;
		internal long Maximum;
		internal int NonZeroFrames;
	}

	private sealed class GateTransition
	{
		internal float Elapsed;
		internal bool UpdateEnabled;
		internal int Supported;
		internal int Active;
		internal int Dynamic;
		internal int Awake;
		internal int RptTracked;
	}

	private static readonly List<Sample> Samples = new List<Sample>();
	private static readonly List<GateTransition> GateTransitions = new List<GateTransition>();
	private static float _startedAt;
	private static float _lastObservedAt;
	private static float _updateEnabledSeconds;
	private static float _updateEnabledWithoutAwakeSeconds;
	private static float _supportedAwakeSeconds;
	private static int _frames;
	private static int _maximumSupported;
	private static int _maximumSupportedAwake;
	private static int _lastSupported = -1;
	private static int _lastActive = -1;
	private static int _lastDynamic = -1;
	private static int _lastAwake = -1;
	private static int _lastRptTracked = -1;
	private static bool _lastUpdateEnabled;
	private static bool _hasLastGateState;
	private static bool _gateErrorLogged;
	private static bool _running;

	internal static void Toggle()
	{
		if (_running)
		{
			StopAndLog();
			return;
		}
		Start();
	}

	internal static void Update()
	{
		if (!_running)
		{
			return;
		}

		float now = Time.realtimeSinceStartup;
		ObserveGate(now);
		_frames++;
		foreach (Sample sample in Samples)
		{
			if (!sample.Recorder.Valid)
			{
				continue;
			}

			long value = sample.Recorder.LastValue;
			sample.Sum += value;
			if (value > sample.Maximum)
			{
				sample.Maximum = value;
			}
			if (value != 0)
			{
				sample.NonZeroFrames++;
			}
		}

		if (now >= _startedAt + SampleDurationSeconds)
		{
			StopAndLog();
		}
	}

	internal static void Reset()
	{
		DisposeRecorders();
		_running = false;
		ResetGateSample();
	}

	private static void Start()
	{
		DisposeRecorders();
		List<ProfilerRecorderHandle> handles = new List<ProfilerRecorderHandle>();
		ProfilerRecorderHandle.GetAvailable(handles);
		HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);

		foreach (ProfilerRecorderHandle handle in handles)
		{
			if (Samples.Count >= MaximumRecorders)
			{
				break;
			}

			try
			{
				ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
				string category = description.Category.Name ?? string.Empty;
				string name = description.Name ?? string.Empty;
				if (!IsPhysicsMarker(category, name) || !keys.Add(category + "\n" + name))
				{
					continue;
				}

				ProfilerRecorder recorder = new ProfilerRecorder(
					handle,
					capacity: 1,
					ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
				if (!recorder.Valid)
				{
					recorder.Dispose();
					continue;
				}

				Samples.Add(new Sample
				{
					Category = category,
					Name = name,
					Unit = description.UnitType,
					Recorder = recorder
				});
			}
			catch (Exception)
			{
				// Individual counters can be unavailable in a release player. Keep the remaining sample useful.
			}
		}

		TryAddInstalledMarkers(keys, ProfilerCategory.Physics, "Physics");
		TryAddInstalledMarkers(keys, ProfilerCategory.Internal, "Internal");

		_startedAt = Time.realtimeSinceStartup;
		_lastObservedAt = _startedAt;
		_frames = 0;
		ResetGateSample();
		LogDeadbodyCollisionMatrix();
		_running = true;
		if (Samples.Count > 0)
		{
			RupturePlugin.Log.LogInfo($"RPT physics counter sample started: duration={SampleDurationSeconds:F0}s, recorders={Samples.Count}, availableMarkers={handles.Count}. Run RPTPhysicsSample again to stop early.");
		}
		else
		{
			RupturePlugin.Log.LogWarning($"RPT physics capture started without native counters: duration={SampleDurationSeconds:F0}s, availableMarkers={handles.Count}. Gate and sleep transitions will still be recorded.");
			LogAvailableMarkers(handles);
		}
	}

	private static void TryAddInstalledMarkers(HashSet<string> keys, ProfilerCategory category, string categoryName)
	{
		foreach (string name in InstalledPhysicsMarkerNames)
		{
			if (Samples.Count >= MaximumRecorders || !keys.Add(categoryName + "\n" + name))
			{
				continue;
			}

			try
			{
				ProfilerRecorder recorder = ProfilerRecorder.StartNew(
					category,
					name,
					capacity: 1,
					ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
				if (!recorder.Valid)
				{
					recorder.Dispose();
					continue;
				}

				Samples.Add(new Sample
				{
					Category = categoryName,
					Name = name,
					Unit = recorder.UnitType,
					Recorder = recorder
				});
			}
			catch (Exception)
			{
				// The marker is compiled into the player but may not be recordable in this build.
			}
		}
	}

	private static void LogAvailableMarkers(List<ProfilerRecorderHandle> handles)
	{
		int logged = 0;
		foreach (ProfilerRecorderHandle handle in handles)
		{
			if (logged >= MaximumAvailableMarkersToLog)
			{
				break;
			}

			try
			{
				ProfilerRecorderDescription description = ProfilerRecorderHandle.GetDescription(handle);
				RupturePlugin.Log.LogInfo($"RPT available profiler marker [{logged++}]: category={description.Category.Name}, marker={description.Name}, unit={description.UnitType}.");
			}
			catch (Exception)
			{
				// Ignore handles whose description is unavailable in this player build.
			}
		}
	}

	private static void StopAndLog()
	{
		float duration = Math.Max(0f, Time.realtimeSinceStartup - _startedAt);
		_running = false;
		ObserveGate(Time.realtimeSinceStartup);
		RupturePlugin.Log.LogInfo($"RPT physics counter sample complete: duration={duration:F2}s, frames={_frames}, recorders={Samples.Count}, nonzero={Samples.Count(sample => sample.NonZeroFrames > 0)}.");
		RupturePlugin.Log.LogInfo($"RPT physics gate summary: updateEnabledSeconds={_updateEnabledSeconds:F3}, enabledWithoutSupportedAwakeSeconds={_updateEnabledWithoutAwakeSeconds:F3}, " +
			$"supportedAwakeSeconds={_supportedAwakeSeconds:F3}, maxSupported={_maximumSupported}, maxSupportedAwake={_maximumSupportedAwake}, transitions={GateTransitions.Count}.");
		for (int index = 0; index < GateTransitions.Count; index++)
		{
			GateTransition transition = GateTransitions[index];
			RupturePlugin.Log.LogInfo($"RPT physics gate transition [{index}]: elapsed={transition.Elapsed:F3}s, updateEnabled={transition.UpdateEnabled}, " +
				$"supported={transition.Supported}, active={transition.Active}, dynamic={transition.Dynamic}, awake={transition.Awake}, rptTracked={transition.RptTracked}.");
		}

		foreach (Sample sample in Samples
			.Where(sample => sample.NonZeroFrames > 0 && sample.Unit == ProfilerMarkerDataUnit.TimeNanoseconds)
			.OrderByDescending(sample => sample.Sum)
			.Take(MaximumTimeResults))
		{
			RupturePlugin.Log.LogInfo($"RPT physics time: category={sample.Category}, marker={sample.Name}, avgMs={ToMilliseconds(sample.Sum / Math.Max(1, _frames)):F4}, " +
				$"avgActiveMs={ToMilliseconds(sample.Sum / Math.Max(1, sample.NonZeroFrames)):F4}, maxMs={ToMilliseconds(sample.Maximum):F4}, activeFrames={sample.NonZeroFrames}.");
		}

		foreach (Sample sample in Samples
			.Where(sample => sample.NonZeroFrames > 0 && sample.Unit != ProfilerMarkerDataUnit.TimeNanoseconds)
			.OrderByDescending(sample => sample.Sum)
			.Take(MaximumCounterResults))
		{
			RupturePlugin.Log.LogInfo($"RPT physics counter: category={sample.Category}, marker={sample.Name}, unit={sample.Unit}, " +
				$"average={sample.Sum / Math.Max(1, _frames):F2}, max={sample.Maximum}, activeFrames={sample.NonZeroFrames}.");
		}

		DisposeRecorders();
		ResetGateSample();
	}

	private static void ObserveGate(float now)
	{
		try
		{
			int supported = 0;
			int active = 0;
			int dynamic = 0;
			int awake = 0;
			int rptTracked = 0;
			IReadOnlyList<PhysicsExtensions.UpdateController.RigidbodyData> entries = PhysicsExtensions.UpdateController._rigidbodies;
			if (entries != null)
			{
				foreach (PhysicsExtensions.UpdateController.RigidbodyData entry in entries)
				{
					Rigidbody rigidbody = entry.rigidbody;
					if (rigidbody == null)
					{
						continue;
					}

					supported++;
					if (rigidbody.gameObject.activeInHierarchy)
					{
						active++;
					}
					if (!rigidbody.isKinematic)
					{
						dynamic++;
						if (!rigidbody.IsSleeping())
						{
							awake++;
						}
					}
					if (RagdollCoordinator.IsTrackedBody(rigidbody))
					{
						rptTracked++;
					}
				}
			}

			bool updateEnabled = PhysicsExtensions.Simulation.UpdateEnabled;
			float delta = Math.Max(0f, now - _lastObservedAt);
			_lastObservedAt = now;
			if (updateEnabled)
			{
				_updateEnabledSeconds += delta;
				if (awake == 0)
				{
					_updateEnabledWithoutAwakeSeconds += delta;
				}
			}
			if (awake > 0)
			{
				_supportedAwakeSeconds += delta;
			}
			_maximumSupported = Math.Max(_maximumSupported, supported);
			_maximumSupportedAwake = Math.Max(_maximumSupportedAwake, awake);

			if (!_hasLastGateState ||
				updateEnabled != _lastUpdateEnabled ||
				supported != _lastSupported ||
				active != _lastActive ||
				dynamic != _lastDynamic ||
				awake != _lastAwake ||
				rptTracked != _lastRptTracked)
			{
				if (GateTransitions.Count < MaximumGateTransitions)
				{
					GateTransitions.Add(new GateTransition
					{
						Elapsed = now - _startedAt,
						UpdateEnabled = updateEnabled,
						Supported = supported,
						Active = active,
						Dynamic = dynamic,
						Awake = awake,
						RptTracked = rptTracked
					});
				}
				_lastUpdateEnabled = updateEnabled;
				_lastSupported = supported;
				_lastActive = active;
				_lastDynamic = dynamic;
				_lastAwake = awake;
				_lastRptTracked = rptTracked;
				_hasLastGateState = true;
			}
		}
		catch (Exception exception)
		{
			if (!_gateErrorLogged)
			{
				_gateErrorLogged = true;
				RupturePlugin.Log.LogWarning($"RPT physics gate sampling is unavailable: {exception.Message}");
			}
		}
	}

	private static void LogDeadbodyCollisionMatrix()
	{
		int deadbodyLayer = LayerMask.NameToLayer("Deadbody");
		if (deadbodyLayer < 0)
		{
			RupturePlugin.Log.LogWarning("RPT physics layer matrix: Deadbody layer is unavailable.");
			return;
		}

		List<string> collides = new List<string>(32);
		List<string> ignores = new List<string>(32);
		for (int layer = 0; layer < 32; layer++)
		{
			string name = LayerMask.LayerToName(layer);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			(Physics.GetIgnoreLayerCollision(deadbodyLayer, layer) ? ignores : collides).Add(name);
		}

		RupturePlugin.Log.LogInfo($"RPT physics Deadbody collision layers: {string.Join(", ", collides)}.");
		RupturePlugin.Log.LogInfo($"RPT physics Deadbody ignored layers: {string.Join(", ", ignores)}.");
	}

	private static void ResetGateSample()
	{
		GateTransitions.Clear();
		_frames = 0;
		_updateEnabledSeconds = 0f;
		_updateEnabledWithoutAwakeSeconds = 0f;
		_supportedAwakeSeconds = 0f;
		_maximumSupported = 0;
		_maximumSupportedAwake = 0;
		_lastSupported = -1;
		_lastActive = -1;
		_lastDynamic = -1;
		_lastAwake = -1;
		_lastRptTracked = -1;
		_lastUpdateEnabled = false;
		_hasLastGateState = false;
		_gateErrorLogged = false;
	}

	private static bool IsPhysicsMarker(string category, string name)
	{
		return category.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("PhysX", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Pxs", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("ScScene", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Broadphase", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Narrowphase", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Collider", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Rigidbody", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Overlap", StringComparison.OrdinalIgnoreCase) >= 0 ||
			name.IndexOf("Contact", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static double ToMilliseconds(double nanoseconds)
	{
		return nanoseconds / 1_000_000d;
	}

	private static void DisposeRecorders()
	{
		foreach (Sample sample in Samples)
		{
			sample.Recorder.Dispose();
		}
		Samples.Clear();
	}
}
