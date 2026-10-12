using System;
using System.Collections.Generic;
using System.Diagnostics;
using EFT.Interactive;
using UnityEngine;
using Rupture.Ragdolls.Classes.Packets;
using Rupture.Ragdolls.External;

namespace Rupture.Ragdolls.Classes;

internal enum ShadowForceOperation : byte
{
	AddForce = 1,
	AddRelativeForce = 2,
	AddForceAtPosition = 3,
	AddExplosionForce = 4,
	AddTorque = 5,
	AddRelativeTorque = 6
}

internal static partial class ShadowRagdollWorld
{
	private static int _weaponColliderAudits;
	private sealed class TimingMetric : IFormattable
	{
		private const int RecentCapacity = 512;
		private readonly double[] _recent;
		private readonly double[] _percentileScratch;
		internal TimingMetric() : this(RecentCapacity) { }
		internal TimingMetric(int capacity) { _recent = new double[capacity]; _percentileScratch = new double[capacity]; }
		private int _recentCount;
		private int _recentCursor;

		internal long Samples { get; private set; }
		internal double LastMilliseconds { get; private set; }
		internal double AverageMilliseconds { get; private set; }
		internal double MaximumMilliseconds { get; private set; }
		internal double TotalMilliseconds => AverageMilliseconds * Samples;
		internal double AveragePerFrame(long frames) => frames <= 0 ? 0d : TotalMilliseconds / frames;
		internal TimingMetric CopyTo(TimingMetric target)
		{
			target.Samples = Samples; target.LastMilliseconds = LastMilliseconds;
			target.AverageMilliseconds = AverageMilliseconds; target.MaximumMilliseconds = MaximumMilliseconds;
			target._recentCount = _recentCount; target._recentCursor = _recentCursor;
			Array.Copy(_recent, target._recent, _recentCount);
			return target;
		}
		public string ToString(string format, IFormatProvider provider) => DescribeTiming(this);
		internal double RecentAverageMilliseconds
		{
			get
			{
				double total = 0d;
				for (int index = 0; index < _recentCount; index++) total += _recent[index];
				return _recentCount == 0 ? 0d : total / _recentCount;
			}
		}

		internal void RecordElapsed(long startedTimestamp)
		{
			Record((Stopwatch.GetTimestamp() - startedTimestamp) * 1000d / Stopwatch.Frequency);
		}

		internal void Record(double milliseconds)
		{
			LastMilliseconds = milliseconds;
			Samples++;
			AverageMilliseconds += (milliseconds - AverageMilliseconds) / Samples;
			MaximumMilliseconds = Math.Max(MaximumMilliseconds, milliseconds);
			_recent[_recentCursor] = milliseconds;
			_recentCursor = (_recentCursor + 1) % _recent.Length;
			if (_recentCount < _recent.Length) _recentCount++;
		}

		internal double P99Milliseconds()
		{
			if (_recentCount == 0) return 0d;
			Array.Copy(_recent, _percentileScratch, _recentCount);
			Array.Sort(_percentileScratch, 0, _recentCount);
			int percentileIndex = Math.Min(_recentCount - 1, (int)Math.Ceiling(_recentCount * 0.99d) - 1);
			return _percentileScratch[Math.Max(0, percentileIndex)];
		}

		internal void Reset()
		{
			Samples = 0;
			LastMilliseconds = 0d;
			AverageMilliseconds = 0d;
			MaximumMilliseconds = 0d;
			_recentCount = 0;
			_recentCursor = 0;
			Array.Clear(_recent, 0, _recent.Length);
			Array.Clear(_percentileScratch, 0, _percentileScratch.Length);
		}
	}

	private const float EnvironmentRadius = 2f;
	private const float EnvironmentRefreshDistance = 0.5f;
	private const float EnvironmentPredictionSeconds = 0.5f;
	private const float MaximumEnvironmentSweepDistance = 12f;
	private const float SettledCollisionActivationRadius = 16f;
	private const float HeadlessSimulationInterval = HeadlessSimulationPolicy.MaximumStepRateInterval;
	private const int RequiredSleepingSteps = 3;
	private const int ProximityEnvironmentRefreshIntervalFrames = 4;
	private const int MaximumProximityAcquiresPerFrame = 2;
	private const int FullPrebakeRuntimeProbeIntervalFrames = 60;
	private const string WeaponPath = "#weapon";
	private const float WeaponBoxPadding = 0.01f;
	private const float MaximumWeaponBoxDimension = 3f;
	private const float DetachedEffectPendingLifetime = 3f;
	private const float LocalGibPendingLifetime = 10f;
	private const float DetachedEffectMaximumDynamicDuration = 5f;
	private const float DetachedEffectOutwardSpeed = 0.75f;
	private const float DetachedEffectUpwardSpeed = 0.15f;
	private const float PostMortemDetachedEffectOutwardSpeed = 0.35f;
	private const float PostMortemDetachedEffectUpwardSpeed = 0.05f;
	private const float MaximumInitialDetachedInheritedSpeed = 2f;
	private const float MaximumPostMortemDetachedInheritedSpeed = 1f;
	private const float MaximumInitialDetachedLaunchSpeed = 3f;
	private const float MaximumPostMortemDetachedLaunchSpeed = 2f;
	private const float LocalGibInitialOutwardSpeed = 1.4f;
	private const float LocalGibPostMortemOutwardSpeed = 0.9f;
	private const float LocalGibUpwardSpeed = 0.25f;
	private const float LocalGibMaximumInheritedSpeed = 1.5f;
	private const float LocalGibMaximumInitialLaunchSpeed = 2.75f;
	private const float LocalGibMaximumPostMortemLaunchSpeed = 2f;
	private const float LocalGibMinimumAngularSpeed = 0.35f;
	private const float LocalGibMaximumAngularSpeed = 1.5f;
	private const float LocalGibMaximumDepenetrationSpeed = 8f;
	private const float LocalGibMaximumRootOffsetFromCut = 1.25f;
	private const int ExternalStepWatchdogMilliseconds = 500;

	private sealed class PendingBuild
	{
		internal EFT.Interactive.CorpseRagdoll Ragdoll;
		internal int EarliestFrame;
	}

	private sealed class PendingDetachedEffect
	{
		internal string ProfileId;
		internal GameObject Root;
		internal Vector3 SourcePosition;
		internal Vector3 Direction;
		internal bool InitialDeathSpawn;
		internal uint EventSequence;
		internal byte EffectIndex;
		internal float ExpiresAt;
		internal bool LocalEnvironmentOnly;
		internal ExternalTransientConvexShape[] TransientShapes;
		internal Action<bool, string> Completion;
	}

	private sealed class PendingLocalGibActor
	{
		internal ShadowRig Rig;
		internal DetachedEffect Detached;
		internal Rigidbody Body;
		internal Collider[] Colliders;
		internal IExternalBodyCreation Creation;
		internal Vector3 IntendedPosition;
		internal Vector3 LaunchVelocity;
		internal Vector3 AngularVelocity;
		internal int EnabledRenderers;
		internal int TotalRenderers;
		internal bool SettledEnvironmentAcquired;
		internal Action<bool, string> Completion;
		internal long EnqueuedAt;
	}

	private sealed class DetachedEffect
	{
		internal GameObject Root;
		internal readonly List<Rigidbody> Bodies = new List<Rigidbody>();
		internal readonly List<IShadowBodyProxy> SimulationBodies = new List<IShadowBodyProxy>();
		internal int SleepingSteps;
		internal float StartedAt;
		internal Vector3 SpawnPosition;
		internal bool InitialDeathSpawn;
		internal uint EventSequence;
		internal byte EffectIndex;
		internal bool MotionDiagnosticLogged;
		internal bool LocalEnvironmentOnly;
		internal bool NetworkPoseEligible = true;
		internal bool NativeRestored;
		internal readonly List<ColliderContactState> Contacts = new List<ColliderContactState>();
	}

	private sealed class ShadowBody
	{
		internal ShadowRig Rig;
		internal Rigidbody Native;
		internal Transform NativeTransform;
		internal bool IsDragChest;
		internal IShadowBodyProxy Proxy;
		internal string Path;
		internal bool CountsForSleep;
		internal bool NativeWasKinematic;
		internal int NativeSupportCount;
		internal CollisionDetectionMode NativeCollisionMode;
		internal Vector3 InitialPosition;
		internal Quaternion InitialRotation;
		internal Vector3 NativeVelocity;
		internal Vector3 NativeAngularVelocity;
		internal bool NativeHandedOff;
		internal bool UsesCustomBox;
		internal bool UsesCustomRoundedBox;
		internal Vector3 CustomBoxCenter;
		internal Vector3 CustomBoxSize;
		internal ShadowBody ParkedAnchor;
		internal Vector3 ParkedLocalPosition;
		internal Quaternion ParkedLocalRotation = Quaternion.identity;
		internal readonly List<Collider> NativeColliders = new List<Collider>();
	}

	private sealed class ExternalJointBinding
	{
		internal IExternalPhysicsJoint Joint;
		internal CharacterJoint NativeJoint;
		internal Quaternion AdmissionRelativeRotation;
		internal ShadowBody Body;
		internal ShadowBody ConnectedBody;
	}

	private sealed class ShadowRig
	{
		internal int JointDiagnosticSamples;
		internal float NextJointDiagnosticAt;
		internal EFT.Interactive.CorpseRagdoll NativeRagdoll;
		internal Corpse Corpse;
		internal GameObject Root;
		internal bool Active;
		internal bool Dormant;
		internal bool Settled;
		internal bool IgnoreNativeStopped;
		internal bool Committed;
		internal uint ControlDeathSequence;
		internal int CoreBodyCount;
		internal int SleepingSteps;
		internal float StartedAt;
		internal Vector3 EnvironmentCenter;
		internal Vector3 EnvironmentSweepEnd;
		internal ShadowBody CenterBody;
		internal ShadowBody WeaponBody;
		internal bool WeaponReleased;
		internal int WeaponSleepingSteps;
		internal bool WeaponSleeping;
		internal bool EnvironmentAcquired;
		internal uint ExternalGroup;
		internal int NextEnvironmentRefreshFrame;
		internal readonly List<ShadowBody> Bodies = new List<ShadowBody>();
		internal readonly List<ShadowBody> CoreBodies = new List<ShadowBody>();
		internal readonly Dictionary<Rigidbody, ShadowBody> BodiesByNative = new Dictionary<Rigidbody, ShadowBody>();
		internal readonly List<DetachedEffect> DetachedEffects = new List<DetachedEffect>();
		internal readonly HashSet<ShadowBody> PermanentlyDisabledBodies = new HashSet<ShadowBody>();
		internal readonly List<ExternalJointBinding> ExternalJoints = new List<ExternalJointBinding>();
	}

	private sealed class PendingColliderDisable
	{
		internal readonly List<Collider> Colliders = new List<Collider>();
		internal readonly HashSet<int> NativeBodyIds = new HashSet<int>();
		internal readonly List<Transform> RemovedRoots = new List<Transform>();
	}

	private static readonly List<PendingBuild> PendingBuilds = new List<PendingBuild>();
	private static readonly List<PendingDetachedEffect> PendingDetachedEffects = new List<PendingDetachedEffect>();
	private static readonly List<PendingLocalGibActor> PendingLocalGibActors = new List<PendingLocalGibActor>();
	private static readonly List<ShadowRig> Rigs = new List<ShadowRig>();
	private static readonly List<ShadowRig> RigSnapshot = new List<ShadowRig>();
	private static readonly Dictionary<EFT.Interactive.CorpseRagdoll, ShadowRig> RigsByRagdoll = new Dictionary<EFT.Interactive.CorpseRagdoll, ShadowRig>();
	private static readonly Dictionary<Corpse, ShadowRig> RigsByCorpse = new Dictionary<Corpse, ShadowRig>();
	private static readonly Dictionary<Rigidbody, ShadowBody> BodiesByNative = new Dictionary<Rigidbody, ShadowBody>();
	private static readonly Dictionary<string, PendingColliderDisable> PendingColliderDisables =
		new Dictionary<string, PendingColliderDisable>(StringComparer.Ordinal);
	private static readonly HashSet<string> FailedColliderDisableProfiles = new HashSet<string>(StringComparer.Ordinal);
	private static readonly List<Vector3> ActiveRigCenters = new List<Vector3>();
	private static readonly List<ShadowRig> ImpactWakeQueue = new List<ShadowRig>();
	private static readonly HashSet<ShadowRig> ImpactWakeVisited = new HashSet<ShadowRig>();
	private static readonly HashSet<IShadowBodyProxy> LateVisibleBodies = new HashSet<IShadowBodyProxy>();
	private static int _buildFailures;
	private static int _successfulBuilds;
	private static int _environmentWakeCount;
	private static int _independentWeaponSleepCount;
	private static int _independentWeaponWakeCount;
	private static int _independentWeaponEnvironmentReacquireCount;
	private static long _deferredProximityRefreshes;
	private static int _proximityAcquireFrame = -1;
	private static int _proximityAcquiresThisFrame;
	private static int _environmentRefreshCursor;
	private static int _corpseCollisionColliderCount;
	private static int _activeEnvironmentColliders;
	private static int _enabledSettledRigs;
	private static int _enabledSettledBodies;
	private static int _lastSimulationActiveRigs;
	private static int _lastSimulationActiveBodies;
	private static int _lastSimulationEnvironmentColliders;
	private static int _lastSimulationSettledBodies;
	private static double _lastSimulationMilliseconds;
	private static double _averageSimulationMilliseconds;
	private static int _simulationSamples;
	private static double _emptySimulationAverageMilliseconds;
	private static int _emptySimulationSamples;
	private static double _passiveSimulationAverageMilliseconds;
	private static int _passiveSimulationSamples;
	private static double _dynamicSimulationAverageMilliseconds;
	private static int _dynamicSimulationSamples;
	private static int _lastSimulationDormantBodies;
	private static int _latePoseCorrectionFrames;
	private static long _latePoseCorrectedBodies;
	private static float _maximumLatePositionError;
	private static float _maximumLateAngleError;
	private static long _nextStatusLogTimestamp;
	private static long _nextPerformanceLogTimestamp;
	private static bool _performanceLoggingWasEnabled;
	private static float _headlessSimulationAccumulator;
	private static bool _continuousSceneFailureLogged;
	private static bool _runtimeAvailable = true;
	private static IExternalPhysicsRuntime _externalRuntime;
	private static bool _externalStepSubmitted;
	private static float _submittedExternalSimulationDelta;
	private static float _completedExternalSimulationDelta = HeadlessSimulationInterval;
	private static float _completedExternalSimulationTime;
	private static uint _externalGroupSequence;
	private static bool _externalFallbackLogged;
	private static string _externalRaidFailure;
	private static readonly double[] ExternalMainWaitSamples = new double[1000];
	private static int _externalMainWaitSampleCount;
	private static int _externalMainWaitSampleCursor;
	private static long _externalCompletedResults;
	private static long _externalReadyResults;
	private static double _externalMainWaitTotalMilliseconds;
	private static double _externalMainWaitMaximumMilliseconds;
	private static readonly TimingMetric PluginUpdateTiming = new TimingMetric();
	private static readonly TimingMetric PluginLateUpdateTiming = new TimingMetric();
	private static readonly TimingMetric ShadowUpdateTiming = new TimingMetric();
	private static readonly TimingMetric EnvironmentCaptureTiming = new TimingMetric();
	private static readonly TimingMetric PendingConstructionTiming = new TimingMetric();
	private static readonly TimingMetric RigSynchronizationTiming = new TimingMetric();
	private static readonly TimingMetric RigStateTiming = new TimingMetric();
	private static readonly TimingMetric EnvironmentRefreshTiming = new TimingMetric();
	private static readonly TimingMetric ActivationTiming = new TimingMetric();
	private static readonly TimingMetric ExternalFetchTiming = new TimingMetric();
	private static readonly TimingMetric ExternalApplyTiming = new TimingMetric();
	private static readonly TimingMetric ExternalSubmitTiming = new TimingMetric();

    private static Func<bool> _localHasWork;
    private static Action<IExternalPhysicsRuntime> _prepareLocalSimulation, _receiveLocalSimulation;
    private static Action _resetLocalSimulation, _failLocalSimulation;
    internal static IExternalPhysicsRuntime LocalSimulationRuntime => _externalRuntime;
    private static int _localEnvironmentMask;
    internal static int LocalEnvironmentMask => _localEnvironmentMask;
    internal static void RegisterLocalSimulation(Func<bool> hasWork, Action<IExternalPhysicsRuntime> prepare, Action reset, Action fail,
        Action<IExternalPhysicsRuntime> receive)
    {
        _localHasWork = hasWork; _prepareLocalSimulation = prepare; _resetLocalSimulation = reset; _failLocalSimulation = fail;
        _receiveLocalSimulation = receive;
    }

	internal static bool Enabled => _runtimeAvailable && RuptureConfiguration.LocalizedRagdollPhysics?.Value == true;
	private static bool ContinuousSimulationEnabled => Enabled && RuptureConfiguration.ContinuouslySimulateShadowScene?.Value == true;
	private static bool StatusLoggingEnabled => RuptureConfiguration.EnableDebugLogging?.Value == true && RuptureConfiguration.LogShadowSceneStatusEverySecond?.Value == true;
	internal static bool PerformanceLoggingEnabled => RuptureConfiguration.EnableDebugLogging?.Value == true && RuptureConfiguration.LogShadowScenePerformanceEverySecond?.Value == true;
	private static bool UsingExternalBackend => _externalRuntime != null;
	internal static bool RequiresAuthorityStateBoundary => UsingExternalBackend;
	internal static void GetAuthoritySimulationTimeline(out float authorityTime, out float simulationDeltaTime)
	{
		authorityTime = _completedExternalSimulationTime;
		simulationDeltaTime = _completedExternalSimulationDelta > 0f
			? _completedExternalSimulationDelta
			: HeadlessSimulationInterval;
	}
	internal static void Disable(string reason)
	{
		_runtimeAvailable = false;
		RupturePlugin.Log.LogWarning($"Localized shadow ragdolls are unavailable; EFT native physics remains active. {reason}");
	}

	internal static void OnGameStarted()
	{
		_resetLocalSimulation?.Invoke();
		_externalRuntime = null;
		_externalStepSubmitted = false;
		_submittedExternalSimulationDelta = 0f;
		_completedExternalSimulationDelta = HeadlessSimulationInterval;
		_completedExternalSimulationTime = 0f;
		_externalFallbackLogged = false;
		_externalRaidFailure = null;
		_externalGroupSequence = 0;
		ResetExternalResultMetrics();
		ResetMainThreadTimingMetrics();
		int deadbodyLayer = LayerMask.NameToLayer("Deadbody");
		if (!Enabled && _prepareLocalSimulation == null && RuptureConfiguration.LivingBotLegSevering?.Value != true)
		{
			return;
		}

		ExternalBackendOptions options = new ExternalBackendOptions
		{
			GraphicalCasings = _prepareLocalSimulation != null,
			EnvironmentMode = RuptureConfiguration.ExternalEnvironmentPolicy?.Value ?? ExternalEnvironmentMode.ProximityCache,
			DispatcherThreads = Mathf.Clamp(RuptureConfiguration.ExternalPhysXDispatcherThreads?.Value ?? 2, 0, 4),
			DeadbodyLayer = deadbodyLayer,
			EnvironmentMask = BuildEnvironmentMask(),
			EftBuild = Application.version,
			Log = (level, message) =>
			{
				if (level == ExternalLogLevel.Lifecycle) { RupturePlugin.Log.Initialization(message); return; }
				RupturePlugin.Log.Log(
				level == ExternalLogLevel.Error ? BepInEx.Logging.LogLevel.Error :
				level == ExternalLogLevel.Warning ? BepInEx.Logging.LogLevel.Warning : BepInEx.Logging.LogLevel.Info,
				message);
			}
		};
		_localEnvironmentMask = options.EnvironmentMask;
		if (!ExternalPhysicsBackendLoader.TryLoad(options, out _externalRuntime, out string failure))
		{
			FailExternalRaid("backend load failed: " + failure);
			return;
		}
		try
		{
			_externalRuntime.BeginRaid();
			RupturePlugin.Log.LogInfo($"RPT shadow physics selected ExternalPhysX for this raid; environment={options.EnvironmentMode}, meshExtraction=UnityPhysXExtraction, dispatcherThreads={options.DispatcherThreads}.");
		}
		catch (Exception exception)
		{
			FailExternalRaid("backend raid initialization failed: " + exception.Message);
		}
	}

	private static void FailExternalRaid(string reason)
	{
		ResetLivingFalls();
		ResetDragLeases();
		_failLocalSimulation?.Invoke();
		_externalRaidFailure = reason;
		if (!_externalFallbackLogged)
		{
			_externalFallbackLogged = true;
			RupturePlugin.Log.LogWarning($"External PhysX is unavailable for this raid; EFT native corpse physics remains active. {reason}");
		}
		try { _externalRuntime?.ResetRaid(); } catch { }
		_externalRuntime = null;
		_externalStepSubmitted = false;
		_submittedExternalSimulationDelta = 0f;
	}

	internal static void Schedule(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (!Enabled || ragdoll == null || ragdoll._owner is not Corpse || RigsByRagdoll.ContainsKey(ragdoll))
		{
			return;
		}
		for (int index = 0; index < PendingBuilds.Count; index++)
		{
			if (ReferenceEquals(PendingBuilds[index].Ragdoll, ragdoll))
			{
				return;
			}
		}

		PendingBuilds.Add(new PendingBuild
		{
			Ragdoll = ragdoll,
			EarliestFrame = Time.frameCount + 1
		});
	}

	internal static void RecordPluginUpdate(long startedTimestamp, bool inRaid)
	{
		if (inRaid) RecordTiming(PluginUpdateTiming, startedTimestamp);
	}

	internal static void RecordPluginLateUpdate(long startedTimestamp, bool inRaid)
	{
		if (inRaid) RecordTiming(PluginLateUpdateTiming, startedTimestamp);
	}

	internal static void Update(bool raidWorldActive, bool raidSimulationActive)
	{
		long started = BeginTiming();
		try
		{
			UpdateCore(raidWorldActive, raidSimulationActive);
		}
		finally
		{
			if (raidWorldActive) RecordTiming(ShadowUpdateTiming, started);
		}
	}

	private static void UpdateCore(bool raidWorldActive, bool raidSimulationActive)
	{
		_dragSimulationActive = raidWorldActive && raidSimulationActive;
		if (!raidWorldActive)
		{
			return;
		}
		if (UsingExternalBackend)
		{
			long environmentStarted = BeginTiming();
			try
			{
				_externalRuntime.CaptureEnvironmentChanges();
				if (!string.IsNullOrEmpty(_externalRuntime.Failure))
				{
					HandleExternalEnvironmentFailure("environment transaction failed: " + _externalRuntime.Failure);
				}
			}
			catch (Exception exception)
			{
				HandleExternalEnvironmentFailure("environment update failed: " + exception.Message);
			}
			finally
			{
				RecordTiming(EnvironmentCaptureTiming, environmentStarted);
			}
		}
		// GameWorld exists throughout map loading. That is the intended window for
		// full-environment prebake, but ragdoll work and continuous-debug simulation
		// must wait until AbstractGame reaches Started and the main player exists.
		if (!raidSimulationActive)
		{
			return;
		}
		long constructionStarted = BeginTiming();
		bool continuousSimulation = ContinuousSimulationEnabled;
		if (continuousSimulation && !EnsureScene(out string sceneFailure))
		{
			if (!_continuousSceneFailureLogged)
			{
				_continuousSceneFailureLogged = true;
				RupturePlugin.Log.LogWarning($"Continuous shadow-scene profiling could not create its local physics scene: {sceneFailure}");
			}
			return;
		}
		_continuousSceneFailureLogged = false;
		ProcessPendingBuilds();
		UpdateLivingFalls();
		ProcessPendingDetachedEffects();
		ProcessPendingLocalGibActors();
		RecordTiming(PendingConstructionTiming, constructionStarted);
		if (Rigs.Count == 0 && !continuousSimulation)
		{
			return;
		}

		long synchronizationStarted = BeginTiming();
		RigSnapshot.Clear();
		RigSnapshot.AddRange(Rigs);
		long rigStateStarted = BeginTiming();
		for (int index = RigSnapshot.Count - 1; index >= 0; index--)
		{
			ShadowRig rig = RigSnapshot[index];
			if (rig == null || rig.Corpse == null || rig.Root == null)
			{
				DestroyRig(rig, restoreNative: false);
				continue;
			}
			if ((rig.Active || rig.Dormant) && !IsDragHeld(rig.Corpse) && !rig.IgnoreNativeStopped && rig.NativeRagdoll?._isPhysicsDone == true)
			{
				Settle(rig);
			}
		}
		RecordTiming(RigStateTiming, rigStateStarted);

		long environmentRefreshStarted = BeginTiming();
		int refreshCount = RigSnapshot.Count;
		int refreshStart = refreshCount == 0 ? 0 : _environmentRefreshCursor % refreshCount;
		for (int offset = 0; offset < refreshCount; offset++)
		{
			ShadowRig rig = RigSnapshot[(refreshStart + offset) % refreshCount];
			if (rig == null || (!rig.Active && !rig.Dormant && CountAwakeIndependentBodies(rig) == 0))
			{
				continue;
			}
			if (!RefreshEnvironmentIfNeeded(rig))
			{
				AbortToNative(rig, "nearby collision proxy refresh failed");
			}
		}
		if (refreshCount != 0)
		{
			_environmentRefreshCursor = (refreshStart + MaximumProximityAcquiresPerFrame) % refreshCount;
		}
		RecordTiming(EnvironmentRefreshTiming, environmentRefreshStarted);
		RecordTiming(RigSynchronizationTiming, synchronizationStarted);

		long activationStarted = BeginTiming();
		if (UsingExternalBackend)
		{
			WakeDormantRigsFromExternalEnvironment();
			UpdateSettledCollisionActivation();
			RecordTiming(ActivationTiming, activationStarted);
		}
	}

	private static void HandleExternalEnvironmentFailure(string reason)
	{
		bool affectsActiveRig = _externalStepSubmitted;
		if (!affectsActiveRig)
		{
			foreach (ShadowRig rig in Rigs)
			{
				if (rig?.Active == true || rig?.Dormant == true)
				{
					affectsActiveRig = true;
					break;
				}
			}
		}
		if (affectsActiveRig) AbortExternalBackend(reason);
		else FailExternalRaid(reason);
	}

	internal static void LateUpdate(bool inRaid)
	{
		if (!inRaid)
		{
			_headlessSimulationAccumulator = 0f;
			return;
		}
		if (!MultiplayerRuntime.Current.IsHeadless)
		{
			_headlessSimulationAccumulator = 0f;
			if (GraphicalSimulationPolicy.TryConsume(Time.unscaledDeltaTime, out float elapsed))
				Simulate(inRaid, elapsed, elapsed);
			return;
		}

		if (!HeadlessSimulationPolicy.TryConsume(Time.deltaTime, ref _headlessSimulationAccumulator, out float simulationDeltaTime))
		{
			return;
		}
		// The policy submits at most once per Update: fixed 30 Hz while the server is
		// faster, or one longer elapsed-time step when the AI-heavy server is slower.
		Simulate(inRaid, simulationDeltaTime, simulationDeltaTime);
	}

	private static void Simulate(bool inRaid, float requestedDeltaTime, float maximumDeltaTime)
	{
		if (!inRaid || !UsingExternalBackend)
		{
			return;
		}
		// Publish the completed step before deciding whether another is needed.
		// This delivers sleep/wake transitions and final casing poses without
		// forcing a perpetual empty step just to drain the preceding result.
		if (_externalStepSubmitted && !CompleteExternalStep()) return;
		ProcessDragBoundary();
		bool continuousSimulation = ContinuousSimulationEnabled;
		bool hasActiveRig = false;
		int activeRigs = 0;
		int activeBodies = 0;
		int dormantBodies = 0;
		foreach (ShadowRig rig in Rigs)
		{
			if (rig?.Active == true)
			{
				int dynamicBodies = CountDynamicBodies(rig);
				hasActiveRig |= dynamicBodies > 0;
				activeRigs++;
				activeBodies += dynamicBodies;
			}
			else if (rig?.Settled == true || rig?.Dormant == true)
			{
				int dynamicIndependentBodies = CountAwakeIndependentBodies(rig);
				hasActiveRig |= dynamicIndependentBodies > 0;
				if (dynamicIndependentBodies > 0)
				{
					activeRigs++;
					activeBodies += dynamicIndependentBodies;
				}
				if (rig.Dormant)
				{
					dormantBodies += rig.CoreBodies.Count;
				}
			}
		}
		hasActiveRig |= RagdollCoordinator.HasAuthorityPlayback;
		hasActiveRig |= _localHasWork?.Invoke() == true;
		hasActiveRig |= _externalRuntime.NeedsSimulation;
		if (!hasActiveRig && !continuousSimulation)
		{
			RecordMainThreadWork(MainThreadWork.ExternalIdleSkips, 1);
			return;
		}
		float deltaTime = Mathf.Clamp(requestedDeltaTime, 0.0001f, maximumDeltaTime);
		_lastSimulationActiveRigs = activeRigs;
		_lastSimulationActiveBodies = activeBodies;
		_lastSimulationDormantBodies = dormantBodies;
		_lastSimulationEnvironmentColliders = _activeEnvironmentColliders;
		_lastSimulationSettledBodies = _enabledSettledBodies;
		if (UsingExternalBackend)
		{
			if (!_externalRuntime.IsRaidReady)
			{
				if (!string.IsNullOrEmpty(_externalRuntime.Failure))
				{
					HandleExternalEnvironmentFailure("environment transaction failed: " + _externalRuntime.Failure);
				}
				return;
			}
			if (_externalStepSubmitted && !CompleteExternalStep())
			{
				return;
			}
			// A received authority snapshot must replace the completed local result,
			// never race the step that produced it. Apply complete snapshot sets at
			// this scene-owner boundary before enqueuing the next prediction step.
			using (TraceMainThread(MainThreadStage.AuthorityPlayback)) RagdollCoordinator.ApplyPendingAuthorityStatesBeforeSimulation();
			DriveDragLeases(deltaTime);
			ProcessDragBoundary(); // dispose a failed drive before another step can pull it
			using (TraceMainThread(MainThreadStage.CasingsPrepare)) _prepareLocalSimulation?.Invoke(_externalRuntime);
			long submitStarted = BeginTiming();
			bool submitted = _externalRuntime.SubmitStep(deltaTime, out string submitFailure);
			RecordTiming(ExternalSubmitTiming, submitStarted);
			if (!submitted)
			{
				AbortExternalBackend("step submission failed: " + submitFailure);
				return;
			}
			_submittedExternalSimulationDelta = deltaTime;
			_externalStepSubmitted = true;
			return;
		}
	}

	private static bool CompleteExternalStep()
	{
		long fetchStarted = BeginTiming();
		bool completed = _externalRuntime.CompleteStep(ExternalStepWatchdogMilliseconds, out ExternalStepResult result, out string failure);
		RecordTiming(ExternalFetchTiming, fetchStarted);
		if (!completed)
		{
			AbortExternalBackend(result.TimedOut ? "500 ms scene-owner watchdog expired: " + failure : "step failed: " + failure);
			return false;
		}
		if (!result.Completed)
		{
			return true;
		}
		_externalStepSubmitted = false;
		RecordMainThreadWork(MainThreadWork.ExternalBodiesPublished, result.PublishedBodies);
		RecordMainThreadWork(MainThreadWork.ExternalSleepingBodiesSkipped, result.UnchangedBodies);
		using (TraceMainThread(MainThreadStage.CasingsPrepare)) _receiveLocalSimulation?.Invoke(_externalRuntime);
		float completedDeltaTime = _submittedExternalSimulationDelta;
		_submittedExternalSimulationDelta = 0f;
		if (!float.IsNaN(completedDeltaTime) && !float.IsInfinity(completedDeltaTime) && completedDeltaTime > 0f)
		{
			_completedExternalSimulationDelta = completedDeltaTime;
			_completedExternalSimulationTime += completedDeltaTime;
		}
		PublishDragViews((ulong)result.Sequence);
		PublishLivingPhysics();
		if (PerformanceLoggingEnabled)
		{
			RecordExternalResult(result);
			RecordSimulationTiming(_lastSimulationActiveBodies, _lastSimulationDormantBodies, _lastSimulationEnvironmentColliders, _lastSimulationSettledBodies, result.Milliseconds);
		}

		long applyStarted = BeginTiming();
		RigSnapshot.Clear();
		RigSnapshot.AddRange(Rigs);
		foreach (ShadowRig rig in RigSnapshot)
		{
			if (rig?.Active == true)
			{
				bool authorityPlayback = RagdollCoordinator.IsAuthorityPlaybackActive(rig.Corpse);
				using (TraceMainThread(MainThreadStage.PoseSync))
					if (!authorityPlayback) SyncNativeFromProxy(rig);
				using (TraceMainThread(MainThreadStage.SleepAndWeaponState))
				{
					if (!authorityPlayback) UpdateIndependentWeaponState(rig);
					if (!RagdollCoordinator.IsRemoteClient) UpdateSleepState(rig);
				}
				using (TraceMainThread(MainThreadStage.PoseSnapshots)) RagdollCoordinator.SendPoseSnapshot(rig.Corpse);
			}
			else if ((rig?.Settled == true || rig?.Dormant == true) && CountAwakeIndependentBodies(rig) > 0)
			{
				using (TraceMainThread(MainThreadStage.PoseSync)) SyncIndependentBodiesFromProxy(rig);
				using (TraceMainThread(MainThreadStage.SleepAndWeaponState))
				{
					UpdateIndependentWeaponState(rig);
					UpdateSettledDetachedEffects(rig);
				}
				using (TraceMainThread(MainThreadStage.PoseSnapshots)) RagdollCoordinator.SendPoseSnapshot(rig.Corpse);
			}
		}
		RecordTiming(ExternalApplyTiming, applyStarted);
		return true;
	}

	private static void AbortExternalBackend(string reason)
	{
		ResetLivingFalls();
		RupturePlugin.Log.LogError($"External PhysX failed catastrophically; affected shadow rigs are returning to native EFT physics. {reason}");
		RigSnapshot.Clear();
		RigSnapshot.AddRange(Rigs);
		foreach (ShadowRig rig in RigSnapshot)
		{
			if (rig?.Active == true || rig?.Dormant == true || rig?.Settled == true)
			{
				AbortToNative(rig, reason, log: false);
			}
		}
		FailExternalRaid(reason);
	}

	internal static void LateUpdateVisibleShell()
	{
		PresentLivingFalls();
		if (Rigs.Count == 0)
		{
			return;
		}

		int correctedBodies = 0;
		float maximumPositionError = 0f;
		float maximumAngleError = 0f;
		LateVisibleBodies.Clear();
		foreach (ShadowRig rig in Rigs)
		{
			if (rig == null)
			{
				continue;
			}
			ReleaseInvalidWeapon(rig);
			if (rig.Active || rig.Dormant)
			{
				foreach (ShadowBody body in rig.Bodies)
				{
					if (rig.PermanentlyDisabledBodies.Contains(body))
					{
						UpdateParkedBodyPose(body, updateVisible: true);
						continue;
					}
					ReassertVisiblePose(body?.Proxy, body?.NativeTransform, ref correctedBodies,
						ref maximumPositionError, ref maximumAngleError);
				}
				foreach (DetachedEffect detached in rig.DetachedEffects)
				{
					ReassertDetachedEffectPose(detached, dynamicOnly: false, ref correctedBodies,
						ref maximumPositionError, ref maximumAngleError);
				}
			}
			else if (rig.Settled)
			{
				ShadowBody weapon = rig.WeaponBody;
				if (weapon?.Proxy != null && !weapon.Proxy.isKinematic)
				{
					ReassertVisiblePose(weapon.Proxy, weapon.NativeTransform, ref correctedBodies,
						ref maximumPositionError, ref maximumAngleError);
				}
				foreach (DetachedEffect detached in rig.DetachedEffects)
				{
					ReassertDetachedEffectPose(detached, dynamicOnly: true, ref correctedBodies,
						ref maximumPositionError, ref maximumAngleError);
				}
			}
		}

		if (correctedBodies > 0)
		{
			_latePoseCorrectionFrames++;
			_latePoseCorrectedBodies += correctedBodies;
			_maximumLatePositionError = Mathf.Max(_maximumLatePositionError, maximumPositionError);
			_maximumLateAngleError = Mathf.Max(_maximumLateAngleError, maximumAngleError);
		}
	}

	private static void ReassertDetachedEffectPose(DetachedEffect detached, bool dynamicOnly, ref int correctedBodies,
		ref float maximumPositionError, ref float maximumAngleError)
	{
		if (detached == null) return;
		int count = Mathf.Min(detached.Bodies.Count, detached.SimulationBodies.Count);
		for (int index = 0; index < count; index++)
		{
			Rigidbody native = detached.Bodies[index];
			IShadowBodyProxy simulation = detached.SimulationBodies[index];
			if (native == null || simulation == null || simulation.UnityBody != null ||
				(dynamicOnly && simulation.isKinematic)) continue;
			ReassertVisiblePose(simulation, native.transform, ref correctedBodies,
				ref maximumPositionError, ref maximumAngleError);
		}
	}

	private static void ReassertVisiblePose(IShadowBodyProxy body, Transform visible, ref int correctedBodies,
		ref float maximumPositionError, ref float maximumAngleError)
	{
		if (body == null || visible == null || !LateVisibleBodies.Add(body)) return;
		Vector3 targetPosition = body.position;
		Quaternion targetRotation = body.rotation;
		float positionError = Vector3.Distance(visible.position, targetPosition);
		float angleError = Quaternion.Angle(visible.rotation, targetRotation);
		if (positionError > 0.001f || angleError > 0.1f)
		{
			correctedBodies++;
			maximumPositionError = Mathf.Max(maximumPositionError, positionError);
			maximumAngleError = Mathf.Max(maximumAngleError, angleError);
		}
		visible.SetPositionAndRotation(targetPosition, targetRotation);
	}

	internal static void UpdateStatusLogging(bool inRaid)
	{
		bool performanceLogging = inRaid && PerformanceLoggingEnabled;
		if (performanceLogging != _performanceLoggingWasEnabled)
		{
			_performanceLoggingWasEnabled = performanceLogging;
			_nextPerformanceLogTimestamp = 0L;
			if (performanceLogging) ResetPerformanceMetrics();
			else _mainThreadTimingGeneration++; // Cancel a report captured before disable/raid exit.
		}

		if (!inRaid)
		{
			_nextStatusLogTimestamp = 0L;
			_nextPerformanceLogTimestamp = 0L;
			return;
		}

		bool statusLogging = StatusLoggingEnabled;
		if (!statusLogging) _nextStatusLogTimestamp = 0L;
		if (!performanceLogging) _nextPerformanceLogTimestamp = 0L;
		if (!statusLogging && !performanceLogging) return;

		long now = Stopwatch.GetTimestamp();
		if (statusLogging && _nextStatusLogTimestamp <= 0L)
		{
			_nextStatusLogTimestamp = now + Stopwatch.Frequency;
		}
		else if (statusLogging && now >= _nextStatusLogTimestamp)
		{
			// Schedule from now rather than replaying missed intervals after a hitch.
			_nextStatusLogTimestamp = now + Stopwatch.Frequency;
			LogStatus();
		}

		if (performanceLogging && _nextPerformanceLogTimestamp <= 0L)
		{
			_nextPerformanceLogTimestamp = now + Stopwatch.Frequency;
		}
		else if (performanceLogging && now >= _nextPerformanceLogTimestamp)
		{
			_nextPerformanceLogTimestamp = now + Stopwatch.Frequency;
			LogMainThreadStageTimings();
		}
	}

	internal static void Reset()
	{
		ResetLivingFalls();
		ResetDragLeases();
		_resetLocalSimulation?.Invoke();
		_weaponColliderAudits = 0;
		PendingBuilds.Clear();
		foreach (PendingDetachedEffect pending in PendingDetachedEffects)
		{
			if (pending?.LocalEnvironmentOnly == true && pending.Root != null) DestroyObject(pending.Root);
		}
		PendingDetachedEffects.Clear();
		foreach (PendingLocalGibActor pending in PendingLocalGibActors)
		{
			pending?.Creation?.Cancel();
			pending?.Creation?.Dispose();
			if (pending?.Detached?.Root != null) DestroyObject(pending.Detached.Root);
		}
		PendingLocalGibActors.Clear();
		RigSnapshot.Clear();
		RigSnapshot.AddRange(Rigs);
		foreach (ShadowRig rig in RigSnapshot)
		{
			DestroyRig(rig, restoreNative: rig?.Active == true || rig?.Dormant == true);
		}
		Rigs.Clear();
		RigsByRagdoll.Clear();
		RigsByCorpse.Clear();
		BodiesByNative.Clear();
		PendingColliderDisables.Clear();
		FailedColliderDisableProfiles.Clear();
		ExternalPhysicsBackendLoader.ResetRaid();
		_externalRuntime = null;
		_externalStepSubmitted = false;
		_externalGroupSequence = 0;
		_externalFallbackLogged = false;
		_externalRaidFailure = null;
		ResetExternalResultMetrics();
		ResetMainThreadTimingMetrics();

		ActiveRigCenters.Clear();
		ImpactWakeQueue.Clear();
		ImpactWakeVisited.Clear();
		LateVisibleBodies.Clear();
		_buildFailures = 0;
		_successfulBuilds = 0;
		_environmentWakeCount = 0;
		_independentWeaponSleepCount = 0;
		_independentWeaponWakeCount = 0;
		_independentWeaponEnvironmentReacquireCount = 0;
		_deferredProximityRefreshes = 0;
		_proximityAcquireFrame = -1;
		_proximityAcquiresThisFrame = 0;
		_environmentRefreshCursor = 0;
		_corpseCollisionColliderCount = 0;
		_activeEnvironmentColliders = 0;
		_enabledSettledRigs = 0;
		_enabledSettledBodies = 0;
		_lastSimulationActiveRigs = 0;
		_lastSimulationActiveBodies = 0;
		_lastSimulationEnvironmentColliders = 0;
		_lastSimulationSettledBodies = 0;
		_lastSimulationDormantBodies = 0;
		_lastSimulationMilliseconds = 0d;
		_averageSimulationMilliseconds = 0d;
		_simulationSamples = 0;
		_emptySimulationAverageMilliseconds = 0d;
		_emptySimulationSamples = 0;
		_passiveSimulationAverageMilliseconds = 0d;
		_passiveSimulationSamples = 0;
		_dynamicSimulationAverageMilliseconds = 0d;
		_dynamicSimulationSamples = 0;
		_latePoseCorrectionFrames = 0;
		_latePoseCorrectedBodies = 0L;
		_maximumLatePositionError = 0f;
		_maximumLateAngleError = 0f;
		_nextStatusLogTimestamp = 0L;
		_nextPerformanceLogTimestamp = 0L;
		_performanceLoggingWasEnabled = false;
		_headlessSimulationAccumulator = 0f;
		_submittedExternalSimulationDelta = 0f;
		_completedExternalSimulationDelta = HeadlessSimulationInterval;
		_completedExternalSimulationTime = 0f;
		_continuousSceneFailureLogged = false;
	}

	internal static Rigidbody GetSimulationBody(Rigidbody nativeBody)
	{
		if (nativeBody != null && BodiesByNative.TryGetValue(nativeBody, out ShadowBody binding) && binding?.Rig?.Active == true && binding.Proxy != null)
		{
			return binding.Proxy.UnityBody;
		}
		return nativeBody;
	}

	internal static bool TrySetSimulationVelocity(Rigidbody nativeBody, Vector3 velocity)
	{
		if (nativeBody != null && BodiesByNative.TryGetValue(nativeBody, out ShadowBody binding) && binding?.Rig?.Active == true && binding.Proxy != null)
		{
			if (binding.Proxy.isKinematic) return false;
			binding.Proxy.velocity = velocity;
			return true;
		}
		if (nativeBody == null || nativeBody.isKinematic) return false;
		nativeBody.velocity = velocity;
		return true;
	}

	internal static bool IsSimulationBodyDynamic(Rigidbody nativeBody)
	{
		if (nativeBody != null && BodiesByNative.TryGetValue(nativeBody, out ShadowBody binding) && binding?.Rig?.Active == true && binding.Proxy != null)
		{
			return !binding.Proxy.isKinematic;
		}
		return nativeBody != null && !nativeBody.isKinematic;
	}

	internal static bool TryApplySimulationPointImpulse(Rigidbody nativeBody, Vector3 impulse, Vector3 worldPoint)
	{
		if (nativeBody != null && BodiesByNative.TryGetValue(nativeBody, out ShadowBody binding) && binding?.Rig != null && binding.Proxy != null)
		{
			if (ReferenceEquals(binding, binding.Rig.WeaponBody))
			{
				if (!TryWakeIndependentWeapon(binding.Rig, out _)) return false;
			}
			else if (!binding.Rig.Active)
			{
				return false;
			}
			if (binding.Proxy.isKinematic) return false;
			binding.Proxy.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
			return true;
		}
		if (nativeBody == null || nativeBody.isKinematic) return false;
		nativeBody.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
		return true;
	}

	internal static bool TryCaptureMotionSnapshot(Corpse corpse, out RagdollPose[] poses)
	{
		poses = Array.Empty<RagdollPose>();
		if (corpse == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) ||
			rig.CoreBodyCount <= 0 || rig.CoreBodies.Count != rig.CoreBodyCount)
		{
			return false;
		}
		foreach (ShadowBody parked in rig.PermanentlyDisabledBodies) UpdateParkedBodyPose(parked, updateVisible: false);

		int count = rig.CoreBodies.Count + (rig.WeaponBody?.Proxy != null ? 1 : 0) + CountNetworkDetachedBodies(rig);
		List<RagdollPose> captured = new List<RagdollPose>(count);
		for (byte index = 0; index < rig.CoreBodies.Count; index++)
		{
			captured.Add(CaptureMotion(rig.CoreBodies[index]?.Proxy, RagdollMotionKind.CoreBody, index, 0, 0));
		}
		if (rig.WeaponBody?.Proxy != null)
		{
			captured.Add(CaptureMotion(rig.WeaponBody.Proxy, RagdollMotionKind.Weapon, 0, 0, 0));
		}
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null || !detached.NetworkPoseEligible || detached.EventSequence == 0) continue;
			for (byte index = 0; index < detached.SimulationBodies.Count; index++)
			{
				captured.Add(CaptureMotion(detached.SimulationBodies[index], RagdollMotionKind.DetachedGib,
					index, detached.EffectIndex, detached.EventSequence));
			}
		}
		poses = captured.ToArray();
		return poses.Length != 0;
	}

	internal static bool ApplyAuthorityPose(Corpse corpse, RagdollPose[] poses, bool terminal)
	{
		if (corpse == null || poses == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) ||
			rig.CoreBodyCount <= 0 || rig.CoreBodies.Count != rig.CoreBodyCount)
		{
			return false;
		}
		if (!terminal)
		{
			return ApplyExactLiveAuthorityPose(rig, poses);
		}
		return ApplyExactTerminalAuthorityPose(rig, poses);
	}

	private static bool ApplyExactLiveAuthorityPose(ShadowRig rig, RagdollPose[] poses)
	{
		if (rig == null || poses == null || !UsingExternalBackend) return false;
		bool[] coreSeen = new bool[rig.CoreBodyCount];
		List<ExternalBodyState> states = new List<ExternalBodyState>(poses.Length);
		HashSet<ulong> handles = new HashSet<ulong>();
		foreach (RagdollPose pose in poses)
		{
			if (pose.IsValid == 0) continue;
			if (pose.Kind == RagdollMotionKind.Weapon && rig.WeaponReleased) continue;
			if (!TryResolveMotionBody(rig, pose, out IShadowBodyProxy body)) return false;
			if (pose.Kind == RagdollMotionKind.CoreBody)
			{
				if (pose.BodyIndex >= coreSeen.Length || coreSeen[pose.BodyIndex]) return false;
				coreSeen[pose.BodyIndex] = true;
			}
			if (body is not ExternalShadowBodyProxy external || external.ExternalBody == null ||
				!handles.Add(external.ExternalBody.Handle)) return false;
			states.Add(new ExternalBodyState(external.ExternalBody, pose.Position, pose.Rotation,
				pose.Velocity, pose.AngularVelocity, isKinematic: true, isSleeping: pose.IsSleeping != 0));
		}
		for (int index = 0; index < coreSeen.Length; index++)
			if (!coreSeen[index]) return false;
		if (!_externalRuntime.TryApplyBodyStates(states, out _)) return false;

		// Remote playback is solver-exclusive: all tracked bodies are kinematic while
		// the buffered authority stream owns them. A local event explicitly restores
		// dynamics before it applies its predicted force.
		return true;
	}

	private static bool ApplyExactTerminalAuthorityPose(ShadowRig rig, RagdollPose[] poses)
	{
		if (rig == null || poses == null || !UsingExternalBackend) return false;
		bool[] coreSeen = new bool[rig.CoreBodyCount];
		List<ExternalBodyState> states = new List<ExternalBodyState>(poses.Length);
		HashSet<ulong> handles = new HashSet<ulong>();
		foreach (RagdollPose pose in poses)
		{
			if (pose.IsValid == 0) continue;
			if (pose.Kind == RagdollMotionKind.Weapon && rig.WeaponReleased) continue;
			if (!TryResolveMotionBody(rig, pose, out IShadowBodyProxy body)) return false;
			if (pose.Kind == RagdollMotionKind.CoreBody)
			{
				if (pose.BodyIndex >= coreSeen.Length || coreSeen[pose.BodyIndex]) return false;
				coreSeen[pose.BodyIndex] = true;
			}
			if (body is not ExternalShadowBodyProxy external || external.ExternalBody == null ||
				!handles.Add(external.ExternalBody.Handle)) return false;
			states.Add(new ExternalBodyState(external.ExternalBody, pose.Position, pose.Rotation,
				Vector3.zero, Vector3.zero, isKinematic: true,
				isSleeping: pose.IsSleeping != 0));
		}
		for (int index = 0; index < coreSeen.Length; index++)
			if (!coreSeen[index]) return false;
		if (!_externalRuntime.TryApplyBodyStates(states, out _)) return false;

		SyncNativeFromProxy(rig);
		Settle(rig);
		return true;
	}

	private static RagdollPose CaptureMotion(IShadowBodyProxy body, RagdollMotionKind kind, byte bodyIndex, byte effectIndex, uint eventSequence)
	{
		if (body == null) return default;
		return new RagdollPose
		{
			IsValid = 1,
			IsSleeping = body.IsSleeping() ? (byte)1 : (byte)0,
			Kind = kind,
			BodyIndex = bodyIndex,
			EffectIndex = effectIndex,
			EventSequence = eventSequence,
			Position = body.position,
			Rotation = body.rotation,
			Velocity = body.velocity,
			AngularVelocity = body.angularVelocity
		};
	}

	private static bool TryResolveMotionBody(ShadowRig rig, RagdollPose pose, out IShadowBodyProxy body)
	{
		body = null;
		switch (pose.Kind)
		{
		case RagdollMotionKind.CoreBody:
			if (pose.BodyIndex < rig.CoreBodies.Count) body = rig.CoreBodies[pose.BodyIndex]?.Proxy;
			break;
		case RagdollMotionKind.Weapon:
			body = rig.WeaponBody?.Proxy;
			break;
		case RagdollMotionKind.DetachedGib:
			foreach (DetachedEffect detached in rig.DetachedEffects)
			{
				if (detached == null || detached.EventSequence != pose.EventSequence || detached.EffectIndex != pose.EffectIndex) continue;
				if (pose.BodyIndex < detached.SimulationBodies.Count) body = detached.SimulationBodies[pose.BodyIndex];
				break;
			}
			break;
		}
		return body != null;
	}

	internal static bool TryResolveHitBody(Corpse corpse, Collider hitCollider, out Rigidbody nativeBody)
	{
		nativeBody = null;
		if (corpse == null || hitCollider == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) ||
			!TryResolveHitShadowBody(rig, hitCollider, allowNearest: true, out ShadowBody body))
		{
			return false;
		}
		nativeBody = body.Native;
		return nativeBody != null;
	}

	internal static bool IsIndependentWeaponHit(Corpse corpse, Collider hitCollider)
	{
		return corpse != null && hitCollider != null && RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) &&
			TryResolveHitShadowBody(rig, hitCollider, allowNearest: false, out ShadowBody body) &&
			ReferenceEquals(body, rig.WeaponBody);
	}

	internal static bool IsIndependentWeaponPath(string bodyPath)
	{
		return string.Equals(bodyPath, WeaponPath, StringComparison.Ordinal);
	}

	internal static bool TryResolveHitCorpse(Collider hitCollider, out Corpse corpse)
	{
		corpse = null;
		if (hitCollider == null)
		{
			return false;
		}
		foreach (ShadowRig rig in Rigs)
		{
			if (rig?.Corpse != null && TryResolveHitShadowBody(rig, hitCollider, allowNearest: false, out _))
			{
				corpse = rig.Corpse;
				return true;
			}
		}
		return false;
	}

	internal static bool TryGetHitBodyPath(Corpse corpse, Collider hitCollider, out string bodyPath)
	{
		bodyPath = null;
		if (corpse == null || hitCollider == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) ||
			!TryResolveHitShadowBody(rig, hitCollider, allowNearest: true, out ShadowBody body))
		{
			return false;
		}
		bodyPath = body.Path;
		return !string.IsNullOrEmpty(bodyPath);
	}

	internal static bool DisableCorpseColliders(Corpse corpse, string profileId,
		IReadOnlyCollection<Collider> colliders)
	{
		if (colliders == null || colliders.Count == 0) return false;
		profileId ??= corpse?.PlayerProfileID;
		if (corpse != null && RigsByCorpse.TryGetValue(corpse, out ShadowRig rig))
		{
			PendingColliderDisable immediate = CaptureColliderDisable(colliders);
			ApplyColliderDisable(rig, immediate);
			return true;
		}
		if (!Enabled || !UsingExternalBackend || string.IsNullOrEmpty(profileId) ||
			FailedColliderDisableProfiles.Contains(profileId)) return false;
		if (!PendingColliderDisables.TryGetValue(profileId, out PendingColliderDisable pending))
		{
			pending = new PendingColliderDisable();
			PendingColliderDisables[profileId] = pending;
		}
		MergeColliderDisable(pending, colliders);
		return pending.Colliders.Count != 0 || pending.NativeBodyIds.Count != 0;
	}

	private static PendingColliderDisable CaptureColliderDisable(IReadOnlyCollection<Collider> colliders)
	{
		PendingColliderDisable captured = new PendingColliderDisable();
		MergeColliderDisable(captured, colliders);
		return captured;
	}

	private static void MergeColliderDisable(PendingColliderDisable target,
		IReadOnlyCollection<Collider> colliders)
	{
		if (target == null || colliders == null) return;
		foreach (Collider collider in colliders)
		{
			if (collider == null) continue;
			if (!target.Colliders.Contains(collider)) target.Colliders.Add(collider);
			Rigidbody nativeBody = collider.attachedRigidbody ?? collider.GetComponentInParent<Rigidbody>();
			if (nativeBody != null) target.NativeBodyIds.Add(nativeBody.GetInstanceID());
		}
	}

	private static void ApplyColliderDisable(ShadowRig rig, PendingColliderDisable disabled)
	{
		if (rig == null || disabled == null) return;
		List<ShadowBody> matchedBodies = new List<ShadowBody>();
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body == null || ReferenceEquals(body, rig.WeaponBody)) continue;
			bool matched = IsRemovedBody(disabled, body.NativeTransform) ||
				body.Native != null && disabled.NativeBodyIds.Contains(body.Native.GetInstanceID());
			if (!matched)
			{
				foreach (Collider nativeCollider in body.NativeColliders)
				{
					if (nativeCollider == null) continue;
					foreach (Collider candidate in disabled.Colliders)
					{
						if (candidate == nativeCollider) { matched = true; break; }
					}
					if (matched) break;
				}
			}
			if (!matched) continue;
			rig.PermanentlyDisabledBodies.Add(body);
			matchedBodies.Add(body);
			foreach (Collider nativeCollider in body.NativeColliders)
				if (nativeCollider != null) nativeCollider.enabled = false;
		}
		foreach (ShadowBody body in matchedBodies) ParkSeveredBody(rig, body);
		RemoveSeveredBodyJoints(rig);
	}

	private static bool IsRemovedBody(PendingColliderDisable disabled, Transform bone)
	{
		if (disabled == null || bone == null) return false;
		foreach (Transform root in disabled.RemovedRoots)
			if (root != null && (bone == root || bone.IsChildOf(root))) return true;
		return false;
	}

	private static bool IsPreseveredBody(ShadowRig rig, Transform bone)
	{
		return rig.Corpse != null && !string.IsNullOrEmpty(rig.Corpse.PlayerProfileID) &&
			PendingColliderDisables.TryGetValue(rig.Corpse.PlayerProfileID, out PendingColliderDisable disabled) && IsRemovedBody(disabled, bone);
	}

	private static List<Collider> GetAttachedBodyColliders(Rigidbody nativeBody)
	{
		var attached = new List<Collider>();
		foreach (Collider collider in nativeBody.GetComponentsInChildren<Collider>(includeInactive: true))
			if (collider != null && (collider.attachedRigidbody ?? collider.GetComponentInParent<Rigidbody>(true)) == nativeBody)
				attached.Add(collider);
		return attached;
	}

	private static void ParkSeveredBody(ShadowRig rig, ShadowBody body)
	{
		if (body?.Proxy == null) return;
		ShadowBody anchor = FindRetainedParentBody(rig, body);
		body.ParkedAnchor = anchor;
		if (anchor?.Proxy != null)
		{
			Quaternion inverse = Quaternion.Inverse(anchor.Proxy.rotation);
			body.ParkedLocalPosition = inverse * (body.Proxy.position - anchor.Proxy.position);
			body.ParkedLocalRotation = inverse * body.Proxy.rotation;
		}
		body.Proxy.velocity = Vector3.zero;
		body.Proxy.angularVelocity = Vector3.zero;
		body.Proxy.collisionDetectionMode = CollisionDetectionMode.Discrete;
		body.Proxy.isKinematic = true;
		if (body.Proxy is ExternalShadowBodyProxy external) external.ExternalBody.CollisionEnabled = false;
		UpdateParkedBodyPose(body, updateVisible: true);
	}

	private static ShadowBody FindRetainedParentBody(ShadowRig rig, ShadowBody body)
	{
		for (Transform current = body?.NativeTransform?.parent; current != null; current = current.parent)
		{
			Rigidbody candidate = current.GetComponent<Rigidbody>();
			if (candidate != null && rig.BodiesByNative.TryGetValue(candidate, out ShadowBody anchor) &&
				!rig.PermanentlyDisabledBodies.Contains(anchor)) return anchor;
		}
		if (rig?.CenterBody != null && !rig.PermanentlyDisabledBodies.Contains(rig.CenterBody)) return rig.CenterBody;
		if (rig?.Bodies == null) return null;
		foreach (ShadowBody candidate in rig.Bodies)
		{
			if (candidate?.Proxy != null && !rig.PermanentlyDisabledBodies.Contains(candidate)) return candidate;
		}
		return null;
	}

	private static void UpdateParkedBodyPose(ShadowBody body, bool updateVisible)
	{
		if (body?.Proxy == null || body.ParkedAnchor?.Proxy == null) return;
		IShadowBodyProxy anchor = body.ParkedAnchor.Proxy;
		Vector3 position = anchor.position + anchor.rotation * body.ParkedLocalPosition;
		Quaternion rotation = anchor.rotation * body.ParkedLocalRotation;
		body.Proxy.position = position;
		body.Proxy.rotation = rotation;
		body.Proxy.velocity = Vector3.zero;
		body.Proxy.angularVelocity = Vector3.zero;
		body.Proxy.isKinematic = true;
		if (updateVisible && body.NativeTransform != null) body.NativeTransform.SetPositionAndRotation(position, rotation);
	}

	private static void RemoveSeveredBodyJoints(ShadowRig rig)
	{
		if (rig == null) return;
		for (int index = rig.ExternalJoints.Count - 1; index >= 0; index--)
		{
			ExternalJointBinding binding = rig.ExternalJoints[index];
			if (binding == null || (!rig.PermanentlyDisabledBodies.Contains(binding.Body) &&
				!rig.PermanentlyDisabledBodies.Contains(binding.ConnectedBody))) continue;
			_externalRuntime?.DestroyJoint(binding.Joint);
			rig.ExternalJoints.RemoveAt(index);
		}
	}

	private static void ApplyPendingColliderDisable(ShadowRig rig)
	{
		string profileId = rig?.Corpse?.PlayerProfileID;
		if (string.IsNullOrEmpty(profileId)) return;
		FailedColliderDisableProfiles.Remove(profileId);
		if (!PendingColliderDisables.TryGetValue(profileId, out PendingColliderDisable pending)) return;
		PendingColliderDisables.Remove(profileId);
		ApplyColliderDisable(rig, pending);
	}

	private static void ApplyPendingColliderDisableToNative(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		string profileId = (ragdoll?._owner as Corpse)?.PlayerProfileID;
		if (string.IsNullOrEmpty(profileId)) return;
		FailedColliderDisableProfiles.Add(profileId);
		if (!PendingColliderDisables.TryGetValue(profileId, out PendingColliderDisable pending)) return;
		PendingColliderDisables.Remove(profileId);
		foreach (Collider collider in pending.Colliders)
			if (collider != null) collider.enabled = false;
		if (ragdoll?._rigidbodySpawners == null) return;
		foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
		{
			Rigidbody body = spawner?.Rigidbody;
			if (body == null || !pending.NativeBodyIds.Contains(body.GetInstanceID())) continue;
			foreach (Collider collider in body.GetComponentsInChildren<Collider>(includeInactive: true))
				if (collider != null && collider.attachedRigidbody == body) collider.enabled = false;
		}
	}

	internal static bool TryApplyPointImpulse(Corpse corpse, Collider hitCollider, Vector3 impulse, Vector3 worldPoint,
		out string bodyPath, out string bodyName, out Vector3 localPoint)
	{
		bodyPath = null;
		bodyName = null;
		localPoint = Vector3.zero;
		if (corpse == null || hitCollider == null || !IsFinite(impulse) || !IsFinite(worldPoint) || impulse.sqrMagnitude <= 0f ||
			!RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) || !rig.Active ||
			!TryResolveHitShadowBody(rig, hitCollider, allowNearest: true, out ShadowBody body) ||
			body.Proxy == null || body.Proxy.isKinematic)
		{
			return false;
		}

		body.Proxy.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
		bodyPath = body.Path;
		bodyName = body.NativeTransform != null ? body.NativeTransform.name : body.Path;
		localPoint = body.Proxy.transform.InverseTransformPoint(worldPoint);
		return true;
	}

	internal static bool TryApplyIndependentWeaponPointImpulse(Corpse corpse, Collider hitCollider, Vector3 impulse, Vector3 worldPoint,
		out string bodyPath, out string bodyName, out Vector3 localPoint)
	{
		bodyPath = null;
		bodyName = null;
		localPoint = Vector3.zero;
		if (corpse == null || hitCollider == null || !IsFinite(impulse) || !IsFinite(worldPoint) || impulse.sqrMagnitude <= 0f ||
			!RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) || !IsIndependentWeaponHit(corpse, hitCollider) ||
			rig.WeaponBody?.Proxy == null || !TryWakeIndependentWeapon(rig, out _))
		{
			return false;
		}

		ShadowBody weapon = rig.WeaponBody;
		if (weapon.Proxy.isKinematic)
		{
			return false;
		}
		weapon.Proxy.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
		bodyPath = WeaponPath;
		bodyName = weapon.NativeTransform != null ? weapon.NativeTransform.name : WeaponPath;
		localPoint = weapon.NativeTransform != null ? weapon.NativeTransform.InverseTransformPoint(worldPoint) : Vector3.zero;
		return true;
	}

	private static bool TryResolveHitShadowBody(ShadowRig rig, Collider hitCollider, bool allowNearest, out ShadowBody resolvedBody)
	{
		resolvedBody = null;
		if (rig == null || hitCollider == null)
		{
			return false;
		}

		Rigidbody attached = hitCollider.attachedRigidbody;
		if (attached != null && rig.BodiesByNative.TryGetValue(attached, out resolvedBody))
		{
			return resolvedBody?.Proxy != null;
		}

		Transform hitTransform = hitCollider.transform;
		int bestHierarchyDistance = int.MaxValue;
		foreach (ShadowBody body in rig.Bodies)
		{
			Transform bodyTransform = body.NativeTransform;
			if (body.CountsForSleep && bodyTransform != null && hitTransform.IsChildOf(bodyTransform))
			{
				int hierarchyDistance = GetHierarchyDistance(hitTransform, bodyTransform);
				if (hierarchyDistance < bestHierarchyDistance)
				{
					bestHierarchyDistance = hierarchyDistance;
					resolvedBody = body;
				}
			}
			foreach (Collider nativeCollider in body.NativeColliders)
			{
				if (nativeCollider == hitCollider || (nativeCollider != null &&
					(hitTransform.IsChildOf(nativeCollider.transform) || nativeCollider.transform.IsChildOf(hitTransform))))
				{
					resolvedBody = body;
					return body.Proxy != null;
				}
			}
		}
		if (resolvedBody?.Proxy != null)
		{
			return true;
		}
		if (!allowNearest)
		{
			return false;
		}

		Vector3 hitCenter = hitCollider.bounds.center;
		float bestDistanceSquared = float.PositiveInfinity;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (!body.CountsForSleep || body.Proxy == null)
			{
				continue;
			}
			foreach (Collider nativeCollider in body.NativeColliders)
			{
				if (nativeCollider == null)
				{
					continue;
				}
				float distanceSquared = nativeCollider.bounds.SqrDistance(hitCenter);
				if (distanceSquared <= bestDistanceSquared)
				{
					bestDistanceSquared = distanceSquared;
					resolvedBody = body;
				}
			}
		}
		return resolvedBody?.Proxy != null;
	}

	internal static void RegisterDetachedEffect(EFT.Player player, GameObject effectRoot, Vector3 sourcePosition, Vector3 direction,
		bool initialDeathSpawn = false, uint eventSequence = 0, byte effectIndex = 0)
	{
		if (effectRoot == null || player?.Profile == null)
		{
			return;
		}
		RegisterDetachedEffect(player.Profile.Id, effectRoot, sourcePosition, direction, initialDeathSpawn, eventSequence, effectIndex);
	}

	internal static void RegisterDetachedEffect(string profileId, GameObject effectRoot, Vector3 sourcePosition, Vector3 direction,
		bool initialDeathSpawn = false, uint eventSequence = 0, byte effectIndex = 0)
	{
		RegisterDetachedEffectCore(profileId, effectRoot, sourcePosition, direction, initialDeathSpawn, eventSequence,
			effectIndex, localEnvironmentOnly: false, transientShapes: null);
	}

	internal static bool RegisterLocalDetachedGib(string profileId, GameObject effectRoot, Vector3 sourcePosition,
		Vector3 direction, bool initialDeathSpawn, uint eventSequence,
		ExternalTransientConvexShape[] transientShapes, Action<bool, string> completion = null)
	{
		if (MultiplayerRuntime.Current.IsHeadless || !UsingExternalBackend || effectRoot == null || string.IsNullOrEmpty(profileId))
		{
			if (effectRoot != null) DestroyObject(effectRoot);
			completion?.Invoke(false, "external backend or local gib input is unavailable");
			return false;
		}
		return RegisterDetachedEffectCore(profileId, effectRoot, sourcePosition, direction, initialDeathSpawn,
			eventSequence, effectIndex: 0, localEnvironmentOnly: true,
			transientShapes: transientShapes, completion: completion);
	}

	private static bool RegisterDetachedEffectCore(string profileId, GameObject effectRoot, Vector3 sourcePosition,
		Vector3 direction, bool initialDeathSpawn, uint eventSequence, byte effectIndex, bool localEnvironmentOnly,
		ExternalTransientConvexShape[] transientShapes, Action<bool, string> completion = null)
	{
		if (effectRoot == null)
		{
			return false;
		}
		if (string.IsNullOrEmpty(profileId))
		{
			if (localEnvironmentOnly) DestroyObject(effectRoot);
			completion?.Invoke(false, "profile id is unavailable");
			return false;
		}
		if (localEnvironmentOnly && (!IsFinite(sourcePosition) || !IsFinite(direction) ||
			!IsFinite(effectRoot.transform.position) || !IsFinite(effectRoot.transform.rotation) ||
			Vector3.Distance(effectRoot.transform.position, sourcePosition) > LocalGibMaximumRootOffsetFromCut))
		{
			RupturePlugin.Log.LogWarning($"Local procedural gib rejected before shadow registration: source={sourcePosition}, root={effectRoot.transform.position}.");
			DestroyObject(effectRoot);
			completion?.Invoke(false, "local gib pose is invalid");
			return false;
		}
		if (localEnvironmentOnly) ParkLocalDetachedEffectShell(effectRoot);

		foreach (ShadowRig rig in Rigs)
		{
			if (rig?.Corpse == null || !string.Equals(rig.Corpse.PlayerProfileID, profileId, StringComparison.Ordinal))
			{
				continue;
			}
			if (TryAttachDetachedEffect(rig, effectRoot, sourcePosition, direction, initialDeathSpawn, eventSequence,
				effectIndex, localEnvironmentOnly, transientShapes, completion))
			{
				return true;
			}
			if (localEnvironmentOnly)
			{
				if (effectRoot != null) DestroyObject(effectRoot);
				return false;
			}
		}

		PendingDetachedEffects.Add(new PendingDetachedEffect
		{
			ProfileId = profileId,
			Root = effectRoot,
			SourcePosition = sourcePosition,
			Direction = direction,
			InitialDeathSpawn = initialDeathSpawn,
			EventSequence = eventSequence,
			EffectIndex = effectIndex,
			ExpiresAt = Time.realtimeSinceStartup +
				(localEnvironmentOnly ? LocalGibPendingLifetime : DetachedEffectPendingLifetime),
			LocalEnvironmentOnly = localEnvironmentOnly,
			TransientShapes = transientShapes,
			Completion = completion
		});
		return true;
	}

	private static void ParkLocalDetachedEffectShell(GameObject effectRoot)
	{
		if (effectRoot == null) return;
		foreach (Rigidbody body in effectRoot.GetComponentsInChildren<Rigidbody>(includeInactive: true))
		{
			if (body == null) continue;
			body.velocity = Vector3.zero;
			body.angularVelocity = Vector3.zero;
			body.useGravity = false;
			body.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.isKinematic = true;
		}
		foreach (Collider collider in effectRoot.GetComponentsInChildren<Collider>(includeInactive: true))
			if (collider != null && !collider.isTrigger) collider.enabled = false;
	}

	private static void EnableLocalDetachedEffectShapesForCapture(GameObject effectRoot)
	{
		if (effectRoot == null) return;
		foreach (Collider collider in effectRoot.GetComponentsInChildren<Collider>(includeInactive: true))
			if (collider != null && !collider.isTrigger) collider.enabled = true;
	}

	internal static bool IsActive(Corpse corpse)
	{
		return corpse != null && RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) && (rig.Active || rig.Dormant);
	}

	internal static bool IsSettled(Corpse corpse)
	{
		return corpse != null && RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) && rig.Settled;
	}

	internal static bool TryApplyRadialImpulse(Corpse corpse, Vector3 origin, float radius, float force, out int affectedBodies)
	{
		affectedBodies = 0;
		if (corpse == null || !IsFinite(origin) || !IsFinite(radius) || !IsFinite(force) || radius <= 0f || force <= 0f ||
			!RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) || (!rig.Active && rig.WeaponBody?.Proxy == null))
		{
			return false;
		}

		bool independentWeaponOnly = !rig.Active;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (independentWeaponOnly && !ReferenceEquals(body, rig.WeaponBody))
			{
				continue;
			}
			if (ReferenceEquals(body, rig.WeaponBody) &&
				((body.Proxy.worldCenterOfMass - origin).sqrMagnitude > radius * radius || !TryWakeIndependentWeapon(rig, out _)))
			{
				continue;
			}
			if (ApplyRadialImpulse(body?.Proxy, origin, radius, force))
			{
				affectedBodies++;
			}
		}
		if (!independentWeaponOnly)
		{
			foreach (DetachedEffect detached in rig.DetachedEffects)
			{
				if (detached == null)
				{
					continue;
				}
				foreach (IShadowBodyProxy body in detached.SimulationBodies)
				{
					if (ApplyRadialImpulse(body, origin, radius, force))
					{
						affectedBodies++;
					}
				}
			}
		}
		if (affectedBodies == 0)
		{
			int dynamicBodies = 0;
			foreach (ShadowBody body in rig.Bodies)
			{
				if (body?.Proxy != null && !body.Proxy.isKinematic)
				{
					dynamicBodies++;
				}
			}
			RupturePlugin.Log.LogWarning($"Shadow radial impulse reached a rig but no dynamic body was in range: corpse={GetCorpseName(rig.NativeRagdoll)}, " +
				$"proxies={rig.Bodies.Count}, dynamicProxies={dynamicBodies}, detachedBodies={CountDetachedBodies(rig)}, radius={radius:F2}.");
		}
		return true;
	}

	internal static bool TryApplyLocalDetachedGibPointImpulse(Collider hitCollider, Vector3 impulse,
		Vector3 worldPoint, out string bodyName)
	{
		bodyName = null;
		if (!UsingExternalBackend || _externalRuntime == null || hitCollider == null ||
			!IsFinite(impulse) || !IsFinite(worldPoint) || impulse.sqrMagnitude <= 0f)
			return false;
		Rigidbody hitBody = hitCollider.attachedRigidbody;
		foreach (ShadowRig rig in Rigs)
		{
			if (rig == null) continue;
			foreach (DetachedEffect detached in rig.DetachedEffects)
			{
				if (detached?.LocalEnvironmentOnly != true) continue;
				int count = Mathf.Min(detached.Bodies.Count, detached.SimulationBodies.Count);
				for (int index = 0; index < count; index++)
				{
					Rigidbody native = detached.Bodies[index];
					IShadowBodyProxy simulation = detached.SimulationBodies[index];
					if (native == null || simulation == null ||
						(hitBody != native && !hitCollider.transform.IsChildOf(native.transform))) continue;
					Vector3 center = simulation.worldCenterOfMass;
					Vector3 sweepEnd = center + Vector3.ClampMagnitude(impulse, 2f);
					if (!_externalRuntime.IsFullPrebake && !rig.EnvironmentAcquired &&
						!TryAcquireEnvironment(rig, center, sweepEnd, out _, out _, out _)) return false;
					if (_externalRuntime.IsFullPrebake) rig.EnvironmentAcquired = true;
					simulation.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
					simulation.isKinematic = false;
					if (simulation is ExternalShadowBodyProxy external) external.ExternalBody.CollisionEnabled = true;
					simulation.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
					simulation.WakeUp();
					detached.SleepingSteps = 0;
					detached.StartedAt = Time.realtimeSinceStartup;
					bodyName = native.name;
					return true;
				}
			}
		}
		return false;
	}

	internal static bool ForceSettle(Corpse corpse)
	{
		if (corpse == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) || (!rig.Active && !rig.Dormant))
		{
			return false;
		}
		Settle(rig);
		return true;
	}

	internal static bool Wake(Corpse corpse)
	{
		if (corpse == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) || rig.Settled)
		{
			return false;
		}
		if (rig.Active)
		{
			return true;
		}
		if (!rig.Dormant)
		{
			return false;
		}
		Wake(rig);
		return true;
	}

	internal static bool WakeFromImpact(Corpse corpse, List<Corpse> awakenedCorpses)
	{
		awakenedCorpses?.Clear();
		if (!Enabled || corpse == null || awakenedCorpses == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig primary))
		{
			return false;
		}

		ImpactWakeQueue.Clear();
		ImpactWakeVisited.Clear();
		ImpactWakeQueue.Add(primary);
		ImpactWakeVisited.Add(primary);
		for (int queueIndex = 0; queueIndex < ImpactWakeQueue.Count; queueIndex++)
		{
			ShadowRig rig = ImpactWakeQueue[queueIndex];
			if (!TryWakeFromImpact(rig))
			{
				continue;
			}
			awakenedCorpses.Add(rig.Corpse);
			foreach (ShadowRig candidate in Rigs)
			{
				if (candidate?.Settled != true || candidate.Corpse == null || ImpactWakeVisited.Contains(candidate) ||
					!AreNativeCollidersTouching(rig, candidate))
				{
					continue;
				}
				ImpactWakeVisited.Add(candidate);
				ImpactWakeQueue.Add(candidate);
			}
		}

		ImpactWakeQueue.Clear();
		ImpactWakeVisited.Clear();
		return awakenedCorpses.Count != 0;
	}

	internal static bool WakeFromNetwork(Corpse corpse)
	{
		return corpse != null && RigsByCorpse.TryGetValue(corpse, out ShadowRig rig) && TryWakeFromImpact(rig);
	}

	internal static bool BeginLocalPrediction(Corpse corpse, bool independentWeaponOnly)
	{
		if (corpse == null || !RigsByCorpse.TryGetValue(corpse, out ShadowRig rig)) return false;
		if (independentWeaponOnly) return TryWakeIndependentWeapon(rig, out _);
		if (!TryWakeFromImpact(rig)) return false;

		// Authority playback makes the complete tracked island kinematic. A local
		// event must undo that ownership immediately, before its force is queued.
		rig.Root.SetActive(true);
		SetRigCollisionEnabled(rig, true);
		foreach (ShadowBody body in rig.CoreBodies)
		{
			if (body?.Proxy == null || rig.PermanentlyDisabledBodies.Contains(body)) continue;
			body.Proxy.collisionDetectionMode = body.NativeCollisionMode;
			body.Proxy.isKinematic = false;
			body.Proxy.WakeUp();
		}
		SetDetachedEffectsKinematic(rig, kinematic: false, clearVelocity: false);
		rig.Settled = false;
		rig.Dormant = false;
		rig.Active = true;
		rig.IgnoreNativeStopped = true;
		rig.SleepingSteps = 0;
		rig.StartedAt = Time.realtimeSinceStartup;
		return true;
	}

	internal static bool TryGetCorpse(string profileId, out Corpse corpse)
	{
		if (!string.IsNullOrEmpty(profileId))
		{
			foreach (ShadowRig rig in Rigs)
			{
				if (rig?.Corpse != null && string.Equals(rig.Corpse.PlayerProfileID, profileId, StringComparison.Ordinal))
				{
					corpse = rig.Corpse;
					return true;
				}
			}
		}
		corpse = null;
		return false;
	}

	internal static void WakeFromNative(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll == null || !RigsByRagdoll.TryGetValue(ragdoll, out ShadowRig rig) || !rig.Dormant || rig.Settled)
		{
			return;
		}
		Wake(rig);
		if (!RagdollCoordinator.IsRemoteClient)
		{
			RagdollCoordinator.SendShadowWake(rig.Corpse);
		}
	}

	internal static void AttachLateWeapon(EFT.Interactive.CorpseRagdoll ragdoll, Rigidbody weapon)
	{
		if (ragdoll == null || weapon == null || !RigsByRagdoll.TryGetValue(ragdoll, out ShadowRig rig) ||
			(!rig.Active && !rig.Dormant) || rig.WeaponBody != null || rig.BodiesByNative.ContainsKey(weapon))
		{
			return;
		}

		try
		{
			if (weapon.isKinematic)
			{
				AbortToNative(rig, "the late-attached native weapon was already kinematic");
				return;
			}
			if (!TryCreateBody(rig, weapon, WeaponPath, countsForSleep: false, out string failure))
			{
				AbortToNative(rig, "the late-attached weapon could not be cloned: " + failure);
				return;
			}

			ShadowBody body = rig.BodiesByNative[weapon];
			DetachWeaponFromCorpse(rig);
			ApplyNativeIgnorePairs(rig);
			body.Proxy.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
			body.Proxy.isKinematic = false;
			rig.WeaponSleeping = false;
			rig.WeaponSleepingSteps = 0;
			body.Proxy.velocity = body.NativeVelocity;
			body.Proxy.angularVelocity = body.NativeAngularVelocity;
			body.Proxy.WakeUp();

			BodiesByNative[body.Native] = body;
			body.NativeHandedOff = true;
			for (int supportIndex = 0; supportIndex < body.NativeSupportCount; supportIndex++)
			{
				PhysicsExtensions.UpdateController.UnsupportRigidbody(body.Native);
			}
			body.Native.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.Native.isKinematic = true;
			RupturePlugin.Log.LogInfo($"Late native weapon joined shadow physics independently for {GetCorpseName(ragdoll)}; the corpse hand joint was removed; geometry={DescribeWeaponGeometry(body)}.");
		}
		catch (Exception exception)
		{
			AbortToNative(rig, "late weapon integration failed: " + exception.GetType().Name + ": " + exception.Message);
		}
	}

	internal static void ReleaseWeapon(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		if (ragdoll != null && RigsByRagdoll.TryGetValue(ragdoll, out ShadowRig rig)) ReleaseWeapon(rig);
		CorpseWeaponCollisionFilter.Forget(ragdoll);
	}

	private static void ReleaseInvalidWeapon(ShadowRig rig)
	{
		ShadowBody weapon = rig?.WeaponBody;
		if (weapon != null && (weapon.Native == null || weapon.NativeTransform == null ||
			rig.NativeRagdoll == null || rig.NativeRagdoll.WeaponRigidbody != weapon.Native)) ReleaseWeapon(rig);
	}

	private static void ReleaseWeapon(ShadowRig rig)
	{
		ShadowBody weapon = rig?.WeaponBody;
		if (weapon == null) return;
		// Unbind first. Never move, destroy, reactivate or restore the looted object.
		rig.WeaponBody = null;
		rig.WeaponReleased = true;
		rig.WeaponSleeping = false;
		rig.WeaponSleepingSteps = 0;
		rig.Bodies.Remove(weapon);
		rig.PermanentlyDisabledBodies.Remove(weapon);
		if (!ReferenceEquals(weapon.Native, null))
		{
			rig.BodiesByNative.Remove(weapon.Native);
			if (BodiesByNative.TryGetValue(weapon.Native, out ShadowBody current) && ReferenceEquals(current, weapon))
				BodiesByNative.Remove(weapon.Native);
		}
		weapon.NativeTransform = null;
		weapon.NativeHandedOff = false;
		for (int i = rig.ExternalJoints.Count - 1; i >= 0; i--)
		{
			ExternalJointBinding joint = rig.ExternalJoints[i];
			if (!ReferenceEquals(joint.Body, weapon) && !ReferenceEquals(joint.ConnectedBody, weapon)) continue;
			_externalRuntime?.DestroyJoint(joint.Joint);
			rig.ExternalJoints.RemoveAt(i);
		}
		// The backend queues destruction on its scene owner even during an in-flight step.
		if (weapon.Proxy is ExternalShadowBodyProxy external) _externalRuntime?.DestroyBody(external.ExternalBody);
		weapon.Proxy = null;
		weapon.NativeColliders.Clear();
		if (rig.Settled && CountDynamicDetachedBodies(rig) == 0) ReleaseEnvironment(rig);
		RecordMainThreadWork(MainThreadWork.WeaponsReleased, 1);
	}

	internal static bool RedirectForce(Rigidbody nativeBody, ShadowForceOperation operation, Vector3 force, Vector3 point, float scalar, float radius, float upwardsModifier, ForceMode mode)
	{
		if (nativeBody == null || !BodiesByNative.TryGetValue(nativeBody, out ShadowBody binding) || binding?.Rig == null)
		{
			return false;
		}
		if (binding.Proxy == null)
		{
			return true;
		}
		if (!IsFinite(force) || !IsFinite(point) || !IsFinite(scalar) || !IsFinite(radius) || !IsFinite(upwardsModifier) || !Enum.IsDefined(typeof(ForceMode), mode))
		{
			return true;
		}
		if (RagdollCoordinator.IsRemoteClient)
		{
			if (!RagdollCoordinator.BeginLocalForcePrediction(binding.Rig.Corpse, binding.Path)) return true;
		}
		if (ReferenceEquals(binding, binding.Rig.WeaponBody))
		{
			if (!TryWakeIndependentWeapon(binding.Rig, out string wakeFailure))
			{
				RupturePlugin.Log.LogWarning($"Independent shadow weapon could not wake for {GetCorpseName(binding.Rig.NativeRagdoll)}: {wakeFailure}");
				return true;
			}
		}
		else if (!binding.Rig.Active)
		{
			return true;
		}

		ApplyForce(binding.Proxy, operation, force, point, scalar, radius, upwardsModifier, mode);
		RagdollCoordinator.RecordLocalForcePrediction(binding.Rig.Corpse, binding.Path, operation,
			force, point, scalar, radius, upwardsModifier, mode);
		RagdollCoordinator.SendShadowForce(binding.Rig.Corpse, binding.Path, operation, force, point, scalar, radius, upwardsModifier, mode);
		return true;
	}

	internal static bool ApplyReceivedForce(EFT.Interactive.CorpseRagdoll ragdoll, string bodyPath, ShadowForceOperation operation, Vector3 force, Vector3 point, float scalar, float radius, float upwardsModifier, ForceMode mode)
	{
		if (ragdoll == null || !TryFindNativeBody(ragdoll, bodyPath, out Rigidbody nativeBody))
		{
			return false;
		}
		if (BodiesByNative.TryGetValue(nativeBody, out ShadowBody binding) && binding?.Rig != null && binding.Proxy != null)
		{
			if (ReferenceEquals(binding, binding.Rig.WeaponBody))
			{
				if (!TryWakeIndependentWeapon(binding.Rig, out _)) return false;
			}
			else if (!binding.Rig.Active) return false;
			if (binding.Proxy.isKinematic) return false;
			ApplyForce(binding.Proxy, operation, force, point, scalar, radius, upwardsModifier, mode);
			return true;
		}
		if (nativeBody.isKinematic) return false;
		ApplyForce(new UnityShadowBodyProxy(nativeBody), operation, force, point, scalar, radius, upwardsModifier, mode);
		return true;
	}

	internal static void LogStatus()
	{
		int activeRigs = 0;
		int dormantRigs = 0;
		int settledRigs = 0;
		int dynamicBodies = 0;
		int retainedBodies = 0;
		int detachedBodies = 0;
		int dynamicDetachedBodies = 0;
		int independentWeapons = 0;
		int dynamicIndependentWeapons = 0;
		int awakeIndependentWeapons = 0;
		foreach (ShadowRig rig in Rigs)
		{
			detachedBodies += CountDetachedBodies(rig);
			dynamicDetachedBodies += CountDynamicDetachedBodies(rig);
			if (rig?.WeaponBody?.Proxy != null)
			{
				independentWeapons++;
				if (!rig.WeaponBody.Proxy.isKinematic)
				{
					dynamicIndependentWeapons++;
					if (!rig.WeaponSleeping || !rig.WeaponBody.Proxy.IsSleeping())
					{
						awakeIndependentWeapons++;
					}
				}
			}
			if (rig?.Active == true)
			{
				activeRigs++;
				dynamicBodies += CountDynamicBodies(rig);
			}
			else if (rig?.Dormant == true)
			{
				dormantRigs++;
				retainedBodies += rig.Bodies.Count;
			}
			else if (rig?.Settled == true)
			{
				settledRigs++;
				retainedBodies += rig.Bodies.Count;
			}
		}
		GetNativeQueryShellCounts(out int nativeQueryColliders, out int enabledNativeQueryColliders);
		RagdollCoordinator.GetPoseSnapshotDiagnosticCounts(out int snapshotsSent, out int snapshotsReceived,
			out int snapshotsRejected, out int snapshotsApplied, out int snapshotChunksSent,
			out int snapshotChunksReceived, out int incompleteSnapshotsDiscarded, out int snapshotSendFailures);
		RagdollCoordinator.GetAuthorityPlaybackDiagnosticCounts(out int playbackQueued, out int playbackActive,
			out int playbackFrames, out int predictedEchoes, out int playbackBacklogFramesDiscarded,
			out int playbackTimelineFastForwards, out int playbackWaitingForCorpse, out int playbackWaitingForLifecycle);
		RupturePlugin.Log.LogInfo($"RPT shadow physics: enabled={Enabled}, backend=ExternalPhysX, meshExtraction=UnityPhysXExtraction, continuousDebug={ContinuousSimulationEnabled}, statusLogDebug={StatusLoggingEnabled}, performanceLogDebug={PerformanceLoggingEnabled}, sceneValid={(_externalRuntime?.IsRaidReady == true)}, pending={PendingBuilds.Count}, " +
			$"simulationCadence={(MultiplayerRuntime.Current.IsHeadless ? "headless-variable<=30Hz" : "local-LateUpdate")}, completedSimulationTime={_completedExternalSimulationTime:F3}s, completedStepDelta={_completedExternalSimulationDelta:F4}s, " +
			$"activeRigs={activeRigs}, activeBodies={dynamicBodies}, clientPrediction={RagdollCoordinator.IsRemoteClient}, dormantRigs={dormantRigs}, retainedSettledRigs={settledRigs}, retainedBodies={retainedBodies}, detachedBodies={detachedBodies}/dynamic:{dynamicDetachedBodies}, independentWeapons={independentWeapons}/dynamic:{dynamicIndependentWeapons}/awake:{awakeIndependentWeapons}, " +
			$"independentWeaponLifecycle=slept:{_independentWeaponSleepCount}/woke:{_independentWeaponWakeCount}/environmentReacquired:{_independentWeaponEnvironmentReacquireCount}, " +
			$"poseSnapshots=sent:{snapshotsSent}/received:{snapshotsReceived}/rejected:{snapshotsRejected}/applied:{snapshotsApplied}/chunks:{snapshotChunksSent}:{snapshotChunksReceived}/incompleteDiscarded:{incompleteSnapshotsDiscarded}/sendFailures:{snapshotSendFailures}, " +
			$"authorityPlayback=queued:{playbackQueued}/active:{playbackActive}/frames:{playbackFrames}/predictedEchoes:{predictedEchoes}/backlogDiscarded:{playbackBacklogFramesDiscarded}/fastForwards:{playbackTimelineFastForwards}/waitingForCorpse:{playbackWaitingForCorpse}/waitingForLifecycle:{playbackWaitingForLifecycle}, " +
			$"environmentWakes={_environmentWakeCount}, deferredProximityRefreshes={_deferredProximityRefreshes}, " +
			$"corpseCollisionLayer={DescribeCorpseCollisionLayer()}, corpseCollisionColliders={_corpseCollisionColliderCount}, " +
			$"builds={_successfulBuilds}, failures={_buildFailures}, " +
			$"impactReactivation={RagdollCoordinator.ImpactReactivationEnabled}, currentlyEnabledSettled={_enabledSettledRigs}/{_enabledSettledBodies}, " +
			$"nativeQueryShellColliders={enabledNativeQueryColliders}/{nativeQueryColliders}, " +
			$"latePoseCorrectionFrames={_latePoseCorrectionFrames}, latePoseCorrectedBodies={_latePoseCorrectedBodies}, " +
			$"maxLatePositionError={_maximumLatePositionError:F3}m, maxLateAngleError={_maximumLateAngleError:F1}deg, " +
			$"external=[{(_externalRuntime?.Status ?? "inactive")}].");
	}

	private static FormattableString CapturePerformanceStatus(TimingMetric[] snapshot, TimingMetric wait)
	{
		wait.Reset();
		for (int i = 0; i < _externalMainWaitSampleCount; i++) wait.Record(ExternalMainWaitSamples[i]);
		// Interpolation arguments are frozen values or exclusively owned snapshots.
		// Sorting, formatting and the log sink run on the worker, never Unity APIs.
		return $"RPT shadow physics performance: backend=ExternalPhysX, lastSimMs={_lastSimulationMilliseconds:F3}, avgSimMs={_averageSimulationMilliseconds:F3}, samples={_simulationSamples}, mainThreadMs=last/avg/p99/max:update:{PluginUpdateTiming.CopyTo(snapshot[0])},late:{PluginLateUpdateTiming.CopyTo(snapshot[1])}, shadowUpdateMs=total:{ShadowUpdateTiming.CopyTo(snapshot[2])},environment:{EnvironmentCaptureTiming.CopyTo(snapshot[3])},construction:{PendingConstructionTiming.CopyTo(snapshot[4])},sync:{RigSynchronizationTiming.CopyTo(snapshot[5])},state:{RigStateTiming.CopyTo(snapshot[6])},proximity:{EnvironmentRefreshTiming.CopyTo(snapshot[7])},activation:{ActivationTiming.CopyTo(snapshot[8])}, externalMainMs=fetch:{ExternalFetchTiming.CopyTo(snapshot[9])},apply:{ExternalApplyTiming.CopyTo(snapshot[10])},submit:{ExternalSubmitTiming.CopyTo(snapshot[11])}, externalResults=ready:{_externalReadyResults}/{_externalCompletedResults}({ExternalReadyPercent():F3}%)/mainWaitAvg:{ExternalMainWaitAverage():F4}ms/p99:{new WaitPercentile(wait):F4}ms/max:{_externalMainWaitMaximumMilliseconds:F4}ms, lastSimPopulation=activeRigs:{_lastSimulationActiveRigs}/activeBodies:{_lastSimulationActiveBodies}/dormantBodies:{_lastSimulationDormantBodies}/environment:{_lastSimulationEnvironmentColliders}/settledBodies:{_lastSimulationSettledBodies}, timingBuckets=empty:{_emptySimulationAverageMilliseconds:F3}ms/{_emptySimulationSamples},passive:{_passiveSimulationAverageMilliseconds:F3}ms/{_passiveSimulationSamples},dynamic:{_dynamicSimulationAverageMilliseconds:F3}ms/{_dynamicSimulationSamples}.";
	}
	private readonly struct WaitPercentile : IFormattable
	{
		private readonly TimingMetric _snapshot;
		internal WaitPercentile(TimingMetric snapshot) { _snapshot = snapshot; }
		public string ToString(string format, IFormatProvider provider) => _snapshot.P99Milliseconds().ToString(format, provider);
	}

	private static void GetNativeQueryShellCounts(out int total, out int enabled)
	{
		total = 0;
		enabled = 0;
		foreach (ShadowRig rig in Rigs)
		{
			if (rig?.Settled != true)
			{
				continue;
			}
			foreach (ShadowBody body in rig.Bodies)
			{
				foreach (Collider collider in body.NativeColliders)
				{
					if (collider == null)
					{
						continue;
					}
					total++;
					if (collider.enabled && collider.gameObject.activeInHierarchy)
					{
						enabled++;
					}
				}
			}
		}
	}

	private static void RecordPopulationTiming(int activeBodies, int dormantBodies, int environmentColliders, int settledBodies, double milliseconds)
	{
		if (activeBodies > 0)
		{
			_dynamicSimulationSamples++;
			_dynamicSimulationAverageMilliseconds += (milliseconds - _dynamicSimulationAverageMilliseconds) / _dynamicSimulationSamples;
			return;
		}
		if (dormantBodies > 0 || environmentColliders > 0 || settledBodies > 0)
		{
			_passiveSimulationSamples++;
			_passiveSimulationAverageMilliseconds += (milliseconds - _passiveSimulationAverageMilliseconds) / _passiveSimulationSamples;
			return;
		}
		_emptySimulationSamples++;
		_emptySimulationAverageMilliseconds += (milliseconds - _emptySimulationAverageMilliseconds) / _emptySimulationSamples;
	}

	private static void RecordSimulationTiming(int activeBodies, int dormantBodies, int environmentColliders, int settledBodies, double milliseconds)
	{
		_lastSimulationMilliseconds = milliseconds;
		_simulationSamples++;
		_averageSimulationMilliseconds += (milliseconds - _averageSimulationMilliseconds) / _simulationSamples;
		RecordPopulationTiming(activeBodies, dormantBodies, environmentColliders, settledBodies, milliseconds);
	}

	private static void RecordExternalResult(ExternalStepResult result)
	{
		_externalCompletedResults++;
		if (result.WasReady) _externalReadyResults++;
		_externalMainWaitTotalMilliseconds += result.MainWaitMilliseconds;
		_externalMainWaitMaximumMilliseconds = Math.Max(_externalMainWaitMaximumMilliseconds, result.MainWaitMilliseconds);
		ExternalMainWaitSamples[_externalMainWaitSampleCursor] = result.MainWaitMilliseconds;
		_externalMainWaitSampleCursor = (_externalMainWaitSampleCursor + 1) % ExternalMainWaitSamples.Length;
		if (_externalMainWaitSampleCount < ExternalMainWaitSamples.Length) _externalMainWaitSampleCount++;
	}

	private static void ResetExternalResultMetrics()
	{
		_externalMainWaitSampleCount = 0;
		_externalMainWaitSampleCursor = 0;
		_externalCompletedResults = 0;
		_externalReadyResults = 0;
		_externalMainWaitTotalMilliseconds = 0d;
		_externalMainWaitMaximumMilliseconds = 0d;
		Array.Clear(ExternalMainWaitSamples, 0, ExternalMainWaitSamples.Length);
	}

	private static string DescribeTiming(TimingMetric timing)
	{
		return FormattableString.Invariant($"{timing.LastMilliseconds:F3}/{timing.AverageMilliseconds:F3}/{timing.P99Milliseconds():F3}/{timing.MaximumMilliseconds:F3}ms/{timing.Samples}");
	}

	private static long BeginTiming() => PerformanceLoggingEnabled ? Stopwatch.GetTimestamp() : 0L;
	private static double TicksToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;

	private static void RecordTiming(TimingMetric timing, long startedTimestamp)
	{
		if (startedTimestamp != 0L && PerformanceLoggingEnabled) timing.RecordElapsed(startedTimestamp);
	}

	private static void ResetPerformanceMetrics()
	{
		_lastSimulationMilliseconds = 0d;
		_averageSimulationMilliseconds = 0d;
		_simulationSamples = 0;
		_emptySimulationAverageMilliseconds = 0d;
		_emptySimulationSamples = 0;
		_passiveSimulationAverageMilliseconds = 0d;
		_passiveSimulationSamples = 0;
		_dynamicSimulationAverageMilliseconds = 0d;
		_dynamicSimulationSamples = 0;
		ResetExternalResultMetrics();
		ResetMainThreadTimingMetrics();
	}

	private static void ResetMainThreadTimingMetrics()
	{
		ResetMainThreadStageTimings();
		PluginUpdateTiming.Reset();
		PluginLateUpdateTiming.Reset();
		ShadowUpdateTiming.Reset();
		EnvironmentCaptureTiming.Reset();
		PendingConstructionTiming.Reset();
		RigSynchronizationTiming.Reset();
		RigStateTiming.Reset();
		EnvironmentRefreshTiming.Reset();
		ActivationTiming.Reset();
		ExternalFetchTiming.Reset();
		ExternalApplyTiming.Reset();
		ExternalSubmitTiming.Reset();
	}

	private static double ExternalReadyPercent() => _externalCompletedResults == 0 ? 100d : _externalReadyResults * 100d / _externalCompletedResults;
	private static double ExternalMainWaitAverage() => _externalCompletedResults == 0 ? 0d : _externalMainWaitTotalMilliseconds / _externalCompletedResults;

	private static double ExternalMainWaitP99()
	{
		if (_externalMainWaitSampleCount == 0) return 0d;
		double[] samples = new double[_externalMainWaitSampleCount];
		Array.Copy(ExternalMainWaitSamples, samples, samples.Length);
		Array.Sort(samples);
		int index = Math.Min(samples.Length - 1, (int)Math.Ceiling(samples.Length * 0.99d) - 1);
		return samples[Math.Max(0, index)];
	}

	private static void ProcessPendingBuilds()
	{
		for (int index = PendingBuilds.Count - 1; index >= 0; index--)
		{
			PendingBuild pending = PendingBuilds[index];
			if (pending.Ragdoll == null || pending.Ragdoll._owner == null)
			{
				PendingBuilds.RemoveAt(index);
				continue;
			}
			if (Time.frameCount < pending.EarliestFrame)
			{
				continue;
			}
			if (UsingExternalBackend && !_externalRuntime.IsRaidReady && string.IsNullOrEmpty(_externalRuntime.Failure))
			{
				continue;
			}
			PendingBuilds.RemoveAt(index);
			if (!TryBuild(pending.Ragdoll, out string failure))
			{
				_buildFailures++;
				ApplyPendingColliderDisableToNative(pending.Ragdoll);
				RupturePlugin.Log.LogWarning($"Shadow ragdoll was not installed for {GetCorpseName(pending.Ragdoll)}; EFT native physics remains active. {failure}");
			}
		}
	}

	private static void ProcessPendingDetachedEffects()
	{
		float now = Time.realtimeSinceStartup;
		for (int index = PendingDetachedEffects.Count - 1; index >= 0; index--)
		{
			PendingDetachedEffect pending = PendingDetachedEffects[index];
			if (pending?.Root == null || pending.ExpiresAt <= now)
			{
				if (pending?.LocalEnvironmentOnly == true && pending.Root != null)
				{
					RupturePlugin.Log.LogWarning($"Local procedural gib expired before its shadow rig became available: profile={pending.ProfileId}, root={pending.Root.transform.position}.");
					DestroyObject(pending.Root);
				}
				pending?.Completion?.Invoke(false, "shadow rig was unavailable before the local gib expired");
				PendingDetachedEffects.RemoveAt(index);
				continue;
			}

			ShadowRig matchingRig = null;
			foreach (ShadowRig rig in Rigs)
			{
				if (rig?.Corpse != null && string.Equals(rig.Corpse.PlayerProfileID, pending.ProfileId, StringComparison.Ordinal))
				{
					matchingRig = rig;
					break;
				}
			}
			if (matchingRig != null && TryAttachDetachedEffect(matchingRig, pending.Root, pending.SourcePosition, pending.Direction,
				pending.InitialDeathSpawn, pending.EventSequence, pending.EffectIndex, pending.LocalEnvironmentOnly,
				pending.TransientShapes, pending.Completion))
			{
				PendingDetachedEffects.RemoveAt(index);
			}
			else if (matchingRig != null && pending.LocalEnvironmentOnly)
			{
				if (pending.Root != null) DestroyObject(pending.Root);
				pending.Completion?.Invoke(false, "local gib could not begin shadow-scene attachment");
				PendingDetachedEffects.RemoveAt(index);
			}
		}
	}

	private static void ProcessPendingLocalGibActors()
	{
		for (int index = PendingLocalGibActors.Count - 1; index >= 0; index--)
		{
			PendingLocalGibActor pending = PendingLocalGibActors[index];
			if (pending?.Detached?.Root == null || pending.Rig == null)
			{
				pending?.Creation?.Cancel();
				pending?.Creation?.Dispose();
				pending?.Completion?.Invoke(false, "local gib was destroyed before external creation completed");
				PendingLocalGibActors.RemoveAt(index);
				continue;
			}
			ExternalBodyCreationStatus status = pending.Creation.Poll(out IExternalPhysicsBody externalBody,
				out string failure);
			if (status == ExternalBodyCreationStatus.Pending) continue;
			PendingLocalGibActors.RemoveAt(index);
			if (status != ExternalBodyCreationStatus.Succeeded || externalBody == null)
			{
				pending.Creation.Dispose();
				if (pending.SettledEnvironmentAcquired && pending.Rig.Settled &&
					CountDynamicDetachedBodies(pending.Rig) == 0 && !HasDynamicIndependentWeapon(pending.Rig))
					ReleaseEnvironment(pending.Rig);
				RupturePlugin.Log.LogWarning($"Local procedural gib external creation failed asynchronously: {status}: {failure}; " +
					$"externalWorkerMs={pending.Creation.WorkerMilliseconds:F3}, externalEndToEndMs={TicksToMilliseconds(Stopwatch.GetTimestamp() - pending.EnqueuedAt):F3}.");
				DestroyObject(pending.Detached.Root);
				pending.Completion?.Invoke(false, failure ?? status.ToString());
				continue;
			}
			Vector3 captured = externalBody.Position;
			if (!IsFinite(captured) || Vector3.Distance(captured, pending.IntendedPosition) > 0.05f)
			{
				_externalRuntime.DestroyBody(externalBody);
				RupturePlugin.Log.LogWarning($"Local procedural gib external pose diverged asynchronously (captured={captured}, intended={pending.IntendedPosition}).");
				DestroyObject(pending.Detached.Root);
				pending.Completion?.Invoke(false, "external gib pose diverged during capture");
				continue;
			}
			ExternalShadowBodyProxy simulationBody = new ExternalShadowBodyProxy(externalBody, pending.Body);
			simulationBody.isKinematic = false;
			simulationBody.velocity = pending.LaunchVelocity;
			simulationBody.angularVelocity = pending.AngularVelocity;
			simulationBody.WakeUp();
			pending.Detached.SimulationBodies.Add(simulationBody);
			foreach (Collider collider in pending.Colliders)
				if (collider != null && !collider.isTrigger)
				{
					pending.Detached.Contacts.Add(new ColliderContactState(collider));
					ConfigureLocalGibQueryCollider(collider);
				}
			pending.Rig.DetachedEffects.Add(pending.Detached);
			if (pending.Rig.Dormant) Wake(pending.Rig);
			RupturePlugin.Log.LogInfo($"Detached gore joined shadow ragdoll asynchronously: source={(pending.Detached.InitialDeathSpawn ? "initial-death" : "post-mortem")}, " +
				$"asset={pending.Detached.Root.name}, renderers={pending.EnabledRenderers}/{pending.TotalRenderers}, " +
				$"bodies=1, colliders={pending.Colliders.Length}, spawn={pending.Detached.SpawnPosition}, " +
				$"launchSpeed={pending.LaunchVelocity.magnitude:F2}m/s, capturedSpeed={simulationBody.velocity.magnitude:F2}m/s, " +
				$"externalWorkerMs={pending.Creation.WorkerMilliseconds:F3}, externalEndToEndMs={TicksToMilliseconds(Stopwatch.GetTimestamp() - pending.EnqueuedAt):F3}, " +
				"ccd=continuous, transientConvex=true.");
			pending.Completion?.Invoke(true, null);
		}
	}

	private static bool TryAttachDetachedEffect(ShadowRig rig, GameObject effectRoot, Vector3 sourcePosition, Vector3 direction,
		bool initialDeathSpawn, uint eventSequence, byte effectIndex, bool localEnvironmentOnly = false,
		ExternalTransientConvexShape[] transientShapes = null, Action<bool, string> completion = null)
	{
		if (rig == null || effectRoot == null || !UsingExternalBackend)
		{
			completion?.Invoke(false, "shadow rig or external backend is unavailable");
			return false;
		}
		DetachedEffect detached = null;
		bool settledEnvironmentAcquired = false;
		try
		{
			effectRoot.SetActive(true);
			if (localEnvironmentOnly) EnableLocalDetachedEffectShapesForCapture(effectRoot);
			bool renderDetachedEffect = !MultiplayerRuntime.Current.IsHeadless;
			Renderer[] effectRenderers = effectRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
			int enabledRenderers = 0;
			foreach (Renderer renderer in effectRenderers)
			{
				if (renderer == null)
				{
					continue;
				}
				renderer.enabled = renderDetachedEffect;
				if (renderDetachedEffect) enabledRenderers++;
			}
			if (rig.Settled && !_externalRuntime.IsFullPrebake)
			{
				Vector3 environmentCenter = rig.EnvironmentCenter;
				if (!TryAcquireEnvironment(rig, environmentCenter, sourcePosition, out string environmentFailure, out _, out _))
				{
					throw new InvalidOperationException("detached effect environment could not be acquired: " + environmentFailure);
				}
				settledEnvironmentAcquired = true;
			}
			Rigidbody[] bodies = effectRoot.GetComponentsInChildren<Rigidbody>(includeInactive: true);
			if (bodies.Length == 0 && effectRoot.GetComponentInChildren<Collider>(includeInactive: true) != null)
			{
				bodies = new[] { effectRoot.AddComponent<Rigidbody>() };
			}
			if (bodies.Length == 0)
			{
				throw new InvalidOperationException("detached effect has no rigidbody or collider");
			}
			effectRoot.transform.SetParent(null, worldPositionStays: true);
			ObjectLifetime.Bind(rig.Corpse.gameObject, "gib:" + effectRoot.GetInstanceID(), () => DestroyObject(effectRoot));
			detached = new DetachedEffect
			{
				Root = effectRoot,
				StartedAt = Time.realtimeSinceStartup,
				SpawnPosition = sourcePosition,
				InitialDeathSpawn = initialDeathSpawn,
				EventSequence = eventSequence,
				EffectIndex = effectIndex,
				LocalEnvironmentOnly = localEnvironmentOnly,
				NetworkPoseEligible = !localEnvironmentOnly
			};

			Vector3 outward = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.up;
			float maximumInheritedSpeed = localEnvironmentOnly
				? LocalGibMaximumInheritedSpeed
				: initialDeathSpawn ? MaximumInitialDetachedInheritedSpeed : MaximumPostMortemDetachedInheritedSpeed;
			float outwardSpeed = localEnvironmentOnly
				? initialDeathSpawn ? LocalGibInitialOutwardSpeed : LocalGibPostMortemOutwardSpeed
				: initialDeathSpawn ? DetachedEffectOutwardSpeed : PostMortemDetachedEffectOutwardSpeed;
			float upwardSpeed = localEnvironmentOnly
				? LocalGibUpwardSpeed
				: initialDeathSpawn ? DetachedEffectUpwardSpeed : PostMortemDetachedEffectUpwardSpeed;
			float maximumLaunchSpeed = localEnvironmentOnly
				? initialDeathSpawn ? LocalGibMaximumInitialLaunchSpeed : LocalGibMaximumPostMortemLaunchSpeed
				: initialDeathSpawn ? MaximumInitialDetachedLaunchSpeed : MaximumPostMortemDetachedLaunchSpeed;
			Vector3 inheritedVelocity = Vector3.ClampMagnitude(GetNearestBodyVelocity(rig, sourcePosition), maximumInheritedSpeed);
			Vector3 launchVelocity = Vector3.ClampMagnitude(inheritedVelocity + outward * outwardSpeed + Vector3.up * upwardSpeed, maximumLaunchSpeed);
			Vector3 spinAxis = UnityEngine.Random.insideUnitSphere;
			if (spinAxis.sqrMagnitude < 0.0001f)
				spinAxis = Vector3.Cross(outward, Mathf.Abs(Vector3.Dot(outward, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up);
			Vector3 localGibAngularVelocity = spinAxis.normalized *
				UnityEngine.Random.Range(LocalGibMinimumAngularSpeed, LocalGibMaximumAngularSpeed);
			int deadbodyLayer = LayerMask.NameToLayer("Deadbody");
			foreach (Rigidbody body in bodies)
			{
				if (body == null)
				{
					continue;
				}
				// SetPositionAndRotation moved the instantiated effect hierarchy this
				// frame. A child Rigidbody can still expose its prefab/default physics
				// pose through Rigidbody.position until Unity synchronizes transforms.
				// Capture the intended world transform first, then explicitly hand that
				// pose to the Rigidbody before the external snapshot reads it. This is
				// local and avoids a global Physics.SyncTransforms call.
				Vector3 intendedBodyPosition = body.transform.position;
				Quaternion intendedBodyRotation = body.transform.rotation;
				if (localEnvironmentOnly && (!IsFinite(intendedBodyPosition) || !IsFinite(intendedBodyRotation) ||
					Vector3.Distance(intendedBodyPosition, sourcePosition) > LocalGibMaximumRootOffsetFromCut))
				{
					throw new InvalidOperationException(
						$"local gib body pose does not match its cut point (body={intendedBodyPosition}, cut={sourcePosition})");
				}
				body.isKinematic = false;
				body.position = intendedBodyPosition;
				body.rotation = intendedBodyRotation;
				body.useGravity = true;
				if (localEnvironmentOnly)
				{
					body.maxAngularVelocity = LocalGibMaximumAngularSpeed;
					body.maxDepenetrationVelocity = LocalGibMaximumDepenetrationSpeed;
				}
				// Small severed parts are launched fast enough to tunnel through a floor in
				// one rendered step. Capture ContinuousDynamic into the external body flags
				// before the Unity shell is returned to a kinematic/discrete presentation role.
				body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
				body.velocity = launchVelocity;
				body.angularVelocity = localEnvironmentOnly
					? localGibAngularVelocity
					: UnityEngine.Random.insideUnitSphere * 6f;
				body.WakeUp();
				detached.Bodies.Add(body);
				List<Collider> attached = new List<Collider>();
				foreach (Collider collider in body.GetComponentsInChildren<Collider>(includeInactive: true))
				{
					if (collider != null && collider.attachedRigidbody == body && !collider.isTrigger) attached.Add(collider);
				}
				string bodyFailure = null;
				IExternalPhysicsBody externalBody = null;
				ExternalCollisionProfile profile = localEnvironmentOnly
					? ExternalCollisionProfile.EnvironmentOnly
					: ExternalCollisionProfile.DeadbodyAndEnvironment;
				if (localEnvironmentOnly)
				{
					if (attached.Count == 0 || transientShapes == null || transientShapes.Length == 0)
						throw new InvalidOperationException("detached effect proxy DTOs were not supplied by the cut transaction");
					IExternalBodyCreation creation = _externalRuntime.BeginCreateTransientConvexBody(body,
						transientShapes, rig.ExternalGroup, kinematic: false, profile, out bodyFailure);
					if (creation == null)
						throw new InvalidOperationException("detached effect body could not begin external creation: " + bodyFailure);
					body.collisionDetectionMode = CollisionDetectionMode.Discrete;
					body.isKinematic = true;
					PendingLocalGibActors.Add(new PendingLocalGibActor
					{
						Rig = rig,
						Detached = detached,
						Body = body,
						Colliders = attached.ToArray(),
						Creation = creation,
						IntendedPosition = intendedBodyPosition,
						LaunchVelocity = launchVelocity,
						AngularVelocity = body.angularVelocity,
						EnabledRenderers = enabledRenderers,
						TotalRenderers = effectRenderers.Length,
						SettledEnvironmentAcquired = settledEnvironmentAcquired,
						Completion = completion,
						EnqueuedAt = Stopwatch.GetTimestamp()
					});
					return true;
				}
				if (attached.Count == 0 || !_externalRuntime.TryCreateBody(body, attached, rig.ExternalGroup,
					kinematic: false, profile, out externalBody, out bodyFailure))
				{
					throw new InvalidOperationException("detached effect body could not be created externally: " + (bodyFailure ?? "no supported collider"));
				}
				Vector3 capturedBodyPosition = externalBody.Position;
				if (localEnvironmentOnly && (!IsFinite(capturedBodyPosition) ||
					Vector3.Distance(capturedBodyPosition, intendedBodyPosition) > 0.05f))
				{
					_externalRuntime.DestroyBody(externalBody);
					throw new InvalidOperationException(
						$"external gib pose diverged during capture (captured={capturedBodyPosition}, intended={intendedBodyPosition})");
				}
				ExternalShadowBodyProxy simulationBody = new ExternalShadowBodyProxy(externalBody, body);
				// Re-apply motion to the external actor after shape creation. This avoids
				// relying on a newly created PhysX actor inheriting the Unity shell's
				// transient velocity/wake state during the same frame.
				simulationBody.isKinematic = false;
				simulationBody.velocity = launchVelocity;
				simulationBody.angularVelocity = body.angularVelocity;
				simulationBody.WakeUp();
				detached.SimulationBodies.Add(simulationBody);
				body.collisionDetectionMode = CollisionDetectionMode.Discrete;
				body.isKinematic = true;
			}

			Collider[] effectColliders = effectRoot.GetComponentsInChildren<Collider>(includeInactive: true);
			foreach (Collider effectCollider in effectColliders)
			{
				if (effectCollider == null || effectCollider.isTrigger)
				{
					continue;
				}
				if (localEnvironmentOnly)
				{
					detached.Contacts.Add(new ColliderContactState(effectCollider));
					ConfigureLocalGibQueryCollider(effectCollider);
					continue;
				}
				detached.Contacts.Add(new ColliderContactState(effectCollider));
				ConfigureCorpseCollision(effectCollider, deadbodyLayer);
				foreach (ShadowBody corpseBody in rig.Bodies)
				{
					foreach (Collider corpseCollider in corpseBody.NativeColliders)
					{
						if (corpseCollider != null)
						{
							_externalRuntime.IgnoreCollision(effectCollider, corpseCollider);
						}
					}
				}
			}

			rig.DetachedEffects.Add(detached);
			if (rig.Dormant)
			{
				Wake(rig);
			}
			RupturePlugin.Log.LogInfo($"Detached gore joined shadow ragdoll for {GetCorpseName(rig.NativeRagdoll)}: source={(initialDeathSpawn ? "initial-death" : "post-mortem")}, " +
				$"asset={effectRoot.name}, active={effectRoot.activeInHierarchy}, renderers={enabledRenderers}/{effectRenderers.Length}, " +
				$"bodies={detached.Bodies.Count}, colliders={effectColliders.Length}, spawn={sourcePosition}, " +
				$"capturedBodyPose={(detached.SimulationBodies.Count > 0 ? detached.SimulationBodies[0].position.ToString() : "none")}, " +
				$"launchSpeed={launchVelocity.magnitude:F2}m/s, " +
				$"capturedSpeed={(detached.SimulationBodies.Count > 0 ? detached.SimulationBodies[0].velocity.magnitude.ToString("F2") : "none")}m/s, " +
				$"ccd=continuous, settledOwner={rig.Settled}.");
			return true;
		}
		catch (Exception exception)
		{
			if (detached != null)
			{
				for (int index = 0; index < detached.SimulationBodies.Count; index++)
				{
					IShadowBodyProxy simulation = detached.SimulationBodies[index];
					if (simulation is ExternalShadowBodyProxy externalBody) _externalRuntime?.DestroyBody(externalBody.ExternalBody);
					if (index < detached.Bodies.Count && detached.Bodies[index] != null)
					{
						detached.Bodies[index].position = simulation.position;
						detached.Bodies[index].rotation = simulation.rotation;
						if (!localEnvironmentOnly)
						{
							detached.Bodies[index].isKinematic = false;
							detached.Bodies[index].velocity = simulation.velocity;
							detached.Bodies[index].angularVelocity = simulation.angularVelocity;
						}
					}
				}
			}
			if (settledEnvironmentAcquired && rig.Settled && CountDynamicDetachedBodies(rig) == 0 && !HasDynamicIndependentWeapon(rig))
			{
				ReleaseEnvironment(rig);
			}
			RupturePlugin.Log.LogWarning($"Detached gore could not join the shadow scene: {exception.GetType().Name}: {exception.Message}");
			if (localEnvironmentOnly && effectRoot != null) DestroyObject(effectRoot);
			completion?.Invoke(false, exception.GetType().Name + ": " + exception.Message);
			return false;
		}
	}

	private static Vector3 GetNearestBodyVelocity(ShadowRig rig, Vector3 sourcePosition)
	{
		float bestDistanceSquared = float.MaxValue;
		Vector3 velocity = Vector3.zero;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (!body.CountsForSleep || body.Proxy == null)
			{
				continue;
			}
			float distanceSquared = (body.Proxy.worldCenterOfMass - sourcePosition).sqrMagnitude;
			if (distanceSquared < bestDistanceSquared)
			{
				bestDistanceSquared = distanceSquared;
				velocity = body.Proxy.velocity;
			}
		}
		return velocity;
	}

	private static int CountDetachedBodies(ShadowRig rig)
	{
		if (rig == null)
		{
			return 0;
		}
		int count = 0;
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			count += detached?.Bodies.Count ?? 0;
		}
		return count;
	}

	private static int CountNetworkDetachedBodies(ShadowRig rig)
	{
		if (rig == null) return 0;
		int count = 0;
		foreach (DetachedEffect detached in rig.DetachedEffects)
			if (detached?.NetworkPoseEligible == true) count += detached.Bodies.Count;
		return count;
	}

	private static int CountDynamicDetachedBodies(ShadowRig rig)
	{
		if (rig == null)
		{
			return 0;
		}
		int count = 0;
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null)
			{
				continue;
			}
			foreach (IShadowBodyProxy body in detached.SimulationBodies)
			{
				if (body != null && !body.isKinematic)
				{
					count++;
				}
			}
		}
		return count;
	}

	private static int CountAwakeIndependentBodies(ShadowRig rig)
	{
		int count = CountDynamicDetachedBodies(rig);
		if (rig?.WeaponBody?.Proxy != null && !rig.WeaponBody.Proxy.isKinematic &&
			(!rig.WeaponSleeping || !rig.WeaponBody.Proxy.IsSleeping()))
		{
			count++;
		}
		return count;
	}

	private static bool HasDynamicIndependentWeapon(ShadowRig rig)
	{
		return rig?.WeaponBody?.Proxy != null && !rig.WeaponBody.Proxy.isKinematic;
	}

	private static int CountDynamicBodies(ShadowRig rig)
	{
		if (rig == null)
		{
			return 0;
		}

		int count = 0;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body?.Proxy != null && !body.Proxy.isKinematic)
			{
				count++;
			}
		}
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null)
			{
				continue;
			}
			foreach (IShadowBodyProxy body in detached.SimulationBodies)
			{
				if (body != null && !body.isKinematic)
				{
					count++;
				}
			}
		}
		return count;
	}

	private static bool TryBuild(EFT.Interactive.CorpseRagdoll ragdoll, out string failure)
	{
		failure = null;
		if (!Enabled)
		{
			failure = "the feature is disabled";
			return false;
		}
		if (!PhysicsExtensions.UpdateController.Enabled)
		{
			failure = "EFT supported-rigidbody simulation is disabled";
			return false;
		}
		if (ragdoll?._owner is not Corpse corpse || ragdoll._rigidbodySpawners == null || ragdoll._rigidbodySpawners.Length == 0)
		{
			failure = "native corpse or rigidbody spawners are unavailable";
			return false;
		}
		if (ragdoll._isPhysicsDone)
		{
			failure = "the native ragdoll was already stopped";
			return false;
		}
		if (!EnsureScene(out failure))
		{
			return false;
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		ShadowRig rig = new ShadowRig
		{
			NativeRagdoll = ragdoll,
			Corpse = corpse,
			ControlDeathSequence = RagdollCoordinator.DragDeathSequence(corpse),
			StartedAt = Time.realtimeSinceStartup,
			CoreBodyCount = ragdoll._rigidbodySpawners.Length,
			ExternalGroup = ++_externalGroupSequence
		};
		uint refreshIdentity = rig.ExternalGroup;
		rig.NextEnvironmentRefreshFrame = Time.frameCount + 1 + (int)((refreshIdentity * 17u) % ProximityEnvironmentRefreshIntervalFrames);
		try
		{
			double phaseStartedAt = stopwatch.Elapsed.TotalMilliseconds;
			rig.Root = new GameObject($"RPT Shadow {corpse.PlayerProfileID ?? "corpse"}");
			rig.Root.SetActive(false);
			Transform skeletonRoot = ragdoll._playerBody?.SkeletonRootJoint?.transform;
			foreach (RigidbodySpawner spawner in ragdoll._rigidbodySpawners)
			{
				Rigidbody nativeBody = spawner?.Rigidbody;
				if (nativeBody == null || nativeBody.isKinematic)
				{
					failure = "a required native ragdoll body is missing or already kinematic";
					DestroyRig(rig, restoreNative: false);
					return false;
				}
				string path = BodyHierarchyPath.RelativeTo(skeletonRoot, nativeBody.transform) ?? nativeBody.name;
				if (!TryCreateBody(rig, nativeBody, path, countsForSleep: true, out failure))
				{
					DestroyRig(rig, restoreNative: false);
					return false;
				}
				ShadowBody coreBody = rig.BodiesByNative[nativeBody];
				coreBody.IsDragChest = spawner.TryGetComponent(out BodyPartCollider dragPart) &&
					dragPart.BodyPartColliderType == EBodyPartColliderType.RibcageUp;
				rig.CoreBodies.Add(coreBody);
			}

			Rigidbody weapon = ragdoll.WeaponRigidbody;
			if (weapon != null && !rig.BodiesByNative.ContainsKey(weapon))
			{
				if (weapon.isKinematic || !TryCreateBody(rig, weapon, WeaponPath, countsForSleep: false, out failure))
				{
					failure = failure ?? "the attached weapon rigidbody is already kinematic";
					DestroyRig(rig, restoreNative: false);
					return false;
				}
			}
			double bodyBuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds - phaseStartedAt;
			phaseStartedAt = stopwatch.Elapsed.TotalMilliseconds;

			if (!TryCloneJoints(rig, out failure))
			{
				DestroyRig(rig, restoreNative: false);
				return false;
			}
			DetachWeaponFromCorpse(rig);
			rig.Bodies.Sort((left, right) => GetHierarchyDepth(left.Native?.transform).CompareTo(GetHierarchyDepth(right.Native?.transform)));
			ApplyNativeIgnorePairs(rig);
			ApplyPendingColliderDisable(rig);
			double jointBuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds - phaseStartedAt;
			phaseStartedAt = stopwatch.Elapsed.TotalMilliseconds;

			Vector3 center = CalculateCenter(rig);
			Vector3 sweepEnd = CalculateEnvironmentSweepEnd(rig, center);
			int environmentCreated = 0;
			int environmentReused = 0;
			if (!_externalRuntime.IsFullPrebake &&
				!TryAcquireEnvironment(rig, center, sweepEnd, out failure, out environmentCreated, out environmentReused))
			{
				DestroyRig(rig, restoreNative: false);
				return false;
			}
			if (_externalRuntime.IsFullPrebake) rig.EnvironmentAcquired = true;
			double environmentBuildMilliseconds = stopwatch.Elapsed.TotalMilliseconds - phaseStartedAt;
			phaseStartedAt = stopwatch.Elapsed.TotalMilliseconds;

			rig.Root.SetActive(true);
			SetRigCollisionEnabled(rig, true);
			foreach (ShadowBody body in rig.Bodies)
			{
				if (rig.PermanentlyDisabledBodies.Contains(body))
				{
					UpdateParkedBodyPose(body, updateVisible: true);
					continue;
				}
				body.Proxy.isKinematic = false;
				body.Proxy.velocity = body.NativeVelocity;
				body.Proxy.angularVelocity = body.NativeAngularVelocity;
				body.Proxy.WakeUp();
			}

			rig.Committed = true;
			foreach (ShadowBody body in rig.Bodies)
			{
				BodiesByNative[body.Native] = body;
				body.NativeHandedOff = true;
				for (int supportIndex = 0; supportIndex < body.NativeSupportCount; supportIndex++)
				{
					PhysicsExtensions.UpdateController.UnsupportRigidbody(body.Native);
				}
				body.Native.collisionDetectionMode = CollisionDetectionMode.Discrete;
				body.Native.isKinematic = true;
			}

			rig.Active = true;
			Rigs.Add(rig);
			RigsByRagdoll[ragdoll] = rig;
			RigsByCorpse[corpse] = rig;
			_successfulBuilds++;
			// Do not make remote clients predict until the authority's next completed step
			// before they receive any server pose. This initial committed pose is cheap,
			// ordered like every other snapshot, and becomes the playback bootstrap.
			RagdollCoordinator.SendPoseSnapshot(rig.Corpse, force: true);
			stopwatch.Stop();
			double handoffMilliseconds = stopwatch.Elapsed.TotalMilliseconds - phaseStartedAt;
			int bodyColliderCount = 0;
			foreach (ShadowBody body in rig.Bodies) bodyColliderCount += body.NativeColliders.Count;
			RupturePlugin.Log.LogInfo($"Shadow ragdoll active for {GetCorpseName(ragdoll)}: bodies={rig.Bodies.Count}, bodyColliders={bodyColliderCount}, " +
				$"environmentCreated={environmentCreated}, environmentReused={environmentReused}, " +
				$"weaponGeometry={DescribeWeaponGeometry(rig.WeaponBody)}, " +
				$"buildMs={stopwatch.Elapsed.TotalMilliseconds:F2} (bodies={bodyBuildMilliseconds:F2}, joints={jointBuildMilliseconds:F2}, " +
				$"environment={environmentBuildMilliseconds:F2}, handoff={handoffMilliseconds:F2}, clientPrediction={RagdollCoordinator.IsRemoteClient}).");
			return true;
		}
		catch (Exception exception)
		{
			failure = exception.GetType().Name + ": " + exception.Message;
			if (rig.Committed)
			{
				AbortToNative(rig, failure, log: false);
			}
			else
			{
				DestroyRig(rig, restoreNative: false);
			}
			return false;
		}
	}

	private static bool EnsureScene(out string failure)
	{
		failure = _externalRaidFailure;
		if (!UsingExternalBackend)
		{
			failure = failure ?? "the standalone PhysX backend is unavailable for this raid";
			return false;
		}
		if (!string.IsNullOrEmpty(_externalRuntime.Failure))
		{
			failure = _externalRuntime.Failure;
			return false;
		}
		if (!_externalRuntime.IsRaidReady)
		{
			failure = "the external environment transaction is still pending";
			return false;
		}
		failure = null;
		return true;
	}

	private static bool TryCreateBody(ShadowRig rig, Rigidbody nativeBody, string path, bool countsForSleep, out string failure)
	{
		failure = null;
		bool isWeapon = string.Equals(path, WeaponPath, StringComparison.Ordinal);
		bool removed = !isWeapon && IsPreseveredBody(rig, nativeBody.transform);
		List<Collider> attachedColliders = GetAttachedBodyColliders(nativeBody);
		if (attachedColliders.Count == 0 && !removed)
		{
			failure = $"ragdoll body {path} has no attached colliders";
			return false;
		}
		ExternalBoxShape weaponBox = default;
		if (isWeapon && !TryBuildWeaponBox(nativeBody, attachedColliders, GetCorpseName(rig.NativeRagdoll), out weaponBox, out failure))
		{
			return false;
		}
		ExternalRoundedBoxShape weaponRoundedBox = isWeapon
			? new ExternalRoundedBoxShape(weaponBox.Center, weaponBox.Size, weaponBox.Material, weaponBox.ContactOffset)
			: null;

		IExternalPhysicsBody externalBody;
		bool usedRoundedBox = false;
		bool created;
		if (isWeapon)
		{
			created = _externalRuntime.TryCreateRoundedBoxBody(nativeBody, attachedColliders, weaponRoundedBox, rig.ExternalGroup, kinematic: false, out externalBody, out string roundedBoxFailure);
			usedRoundedBox = created;
			if (!created)
			{
				created = _externalRuntime.TryCreateBoxBody(nativeBody, attachedColliders, weaponBox, rig.ExternalGroup, kinematic: false, out externalBody, out failure);
				if (created)
				{
					RupturePlugin.Log.LogWarning($"Weapon {nativeBody.name} procedural rounded-box cooking failed; using the fitted box fallback. {roundedBoxFailure}");
				}
			}
		}
		else
		{
			// Preserve native core indices for corpse pose replication, but known
			// missing anatomy needs neither collision geometry nor a dynamic actor.
			created = _externalRuntime.TryCreateBody(nativeBody, removed ? Array.Empty<Collider>() : (IReadOnlyList<Collider>)attachedColliders,
				rig.ExternalGroup, kinematic: removed, out externalBody, out failure);
		}
		if (!created)
		{
			return false;
		}
		ShadowBody external = new ShadowBody
		{
			Rig = rig,
			Native = nativeBody,
			NativeTransform = nativeBody.transform,
			Proxy = new ExternalShadowBodyProxy(externalBody, nativeBody),
			Path = path,
			CountsForSleep = countsForSleep,
			NativeWasKinematic = nativeBody.isKinematic,
			NativeSupportCount = GetSupportCount(nativeBody),
			NativeCollisionMode = nativeBody.collisionDetectionMode,
			InitialPosition = nativeBody.position,
			InitialRotation = nativeBody.rotation,
			NativeVelocity = nativeBody.velocity,
			NativeAngularVelocity = nativeBody.angularVelocity,
			UsesCustomBox = isWeapon && !usedRoundedBox,
			UsesCustomRoundedBox = usedRoundedBox,
			CustomBoxCenter = weaponBox.Center,
			CustomBoxSize = weaponBox.Size
		};
		if (isWeapon) external.Proxy.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
		external.NativeColliders.AddRange(attachedColliders);
		rig.Bodies.Add(external);
		rig.BodiesByNative[nativeBody] = external;
		if (removed) rig.PermanentlyDisabledBodies.Add(external);
		RegisterSpecialBody(rig, external);
		return true;
	}

	private static bool TryBuildWeaponBox(Rigidbody body, IReadOnlyList<Collider> sourceColliders, string profile, out ExternalBoxShape box, out string failure)
	{
		box = default;
		failure = null;
		// EFT marks magazine asset roots independently of their current reload
		// parent. Their animated offsets must not enlarge the weapon's fixed box.
		List<Transform> excludedRoots = new List<Transform>();
		foreach (EFT.MagazineInHandsVisualController magazine in body.GetComponentsInChildren<EFT.MagazineInHandsVisualController>(true))
			excludedRoots.Add(magazine.transform);
		foreach (MagVisualController magazine in body.GetComponentsInChildren<MagVisualController>(true))
			excludedRoots.Add(magazine.transform);
		// Native DropItemDead omits Shells colliders. Headless cartridge visibility
		// patches can leave these pooled reload/ejection objects in a different pose.
		foreach (EFT.AssetsManager.AmmoPoolObject ammo in body.GetComponentsInChildren<EFT.AssetsManager.AmmoPoolObject>(true))
			excludedRoots.Add(ammo.transform);
		int shellsLayer = LayerMask.NameToLayer("Shells");
		bool fitted = WeaponColliderGeometry.TryFit(body, sourceColliders, out Bounds bounds, excludedRoots, shellsLayer);
		AuditWeaponColliders(body, sourceColliders, excludedRoots, shellsLayer, profile, fitted, bounds);
		if (!fitted || !ValidWeaponBoxSize(bounds.size))
		{
			failure = $"weapon {body.name} has unsupported or invalid native collider geometry";
			return false;
		}
		Vector3 size = bounds.size;
		PhysicMaterial material = null;
		float contactOffset = 0.01f;
		foreach (Collider collider in sourceColliders)
		{
			if (collider == null || collider.isTrigger || collider.GetComponentInParent<Rigidbody>(true) != body) continue;
			if (collider.gameObject.layer == shellsLayer || WeaponColliderGeometry.IsExcluded(collider.transform, excludedRoots)) continue;
			if (material == null) material = collider.sharedMaterial;
			contactOffset = Mathf.Min(contactOffset, Mathf.Max(0.001f, collider.contactOffset));
		}
		Vector3 paddedSize = size + Vector3.one * (WeaponBoxPadding * 2f);
		paddedSize.x = Mathf.Max(0.04f, paddedSize.x);
		paddedSize.y = Mathf.Max(0.04f, paddedSize.y);
		paddedSize.z = Mathf.Max(0.04f, paddedSize.z);
		box = new ExternalBoxShape(bounds.center, paddedSize, material, contactOffset);
		return true;
	}

	private static void AuditWeaponColliders(Rigidbody body, IReadOnlyList<Collider> colliders,
		IReadOnlyList<Transform> excludedRoots, int shellsLayer, string profile, bool fitted, Bounds bounds)
	{
		if (RupturePlugin.Log?.IsDebugEnabled != true || _weaponColliderAudits >= 24) return;
		int audit = ++_weaponColliderAudits;
		RupturePlugin.Log.LogDebug($"RPT weapon collider audit {audit}: profile={profile}, weapon={body.name}, " +
			$"bodyRotation={FormatVector(body.transform.eulerAngles)}, bodyScale={FormatVector(body.transform.lossyScale)}, " +
			$"colliders={colliders.Count}, excludedRoots={excludedRoots.Count}, fitted={fitted}, " +
			$"unpaddedCenter={FormatVector(bounds.center)}, unpaddedSize={FormatVector(bounds.size)}, omitted={Math.Max(0, colliders.Count - 32)}.");
		for (int i = 0; i < Math.Min(32, colliders.Count); i++)
		{
			Collider collider = colliders[i];
			if (collider == null) continue;
			string reason = collider.isTrigger ? "trigger" : collider.GetComponentInParent<Rigidbody>(true) != body ? "foreign-body" :
				collider.gameObject.layer == shellsLayer ? "shells-layer" : WeaponColliderGeometry.IsExcluded(collider.transform, excludedRoots) ? "magazine-or-ammo-root" : "included";
			Matrix4x4 relative = body.transform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
			bool supported = WeaponColliderGeometry.TryBounds(body.transform, collider, out Bounds local);
			string shape = collider is BoxCollider box ? $"box center={FormatVector(box.center)} size={FormatVector(box.size)}" :
				collider is MeshCollider mesh ? $"mesh={mesh.sharedMesh?.name} bounds={mesh.sharedMesh?.bounds}" :
				collider is SphereCollider sphere ? $"sphere center={FormatVector(sphere.center)} radius={sphere.radius:F4}" :
				collider is CapsuleCollider capsule ? $"capsule center={FormatVector(capsule.center)} radius={capsule.radius:F4} height={capsule.height:F4} axis={capsule.direction}" : collider.GetType().Name;
			RupturePlugin.Log.LogDebug($"RPT weapon collider audit {audit}/{i}: path={BodyHierarchyPath.RelativeTo(body.transform, collider.transform)}, " +
				$"filter={reason}, layer={collider.gameObject.layer}, enabled={collider.enabled}, active={collider.gameObject.activeInHierarchy}, shape={shape}, " +
				$"position={FormatVector(relative.MultiplyPoint3x4(Vector3.zero))}, axisX={FormatVector(relative.MultiplyVector(Vector3.right))}, " +
				$"axisY={FormatVector(relative.MultiplyVector(Vector3.up))}, axisZ={FormatVector(relative.MultiplyVector(Vector3.forward))}, " +
				$"supported={supported}, bodyCenter={FormatVector(local.center)}, bodySize={FormatVector(local.size)}.");
		}
		if (audit == 24) RupturePlugin.Log.LogDebug("RPT weapon collider audit limit reached for this raid (24 weapons, 32 colliders each).");
	}

	private static string DescribeWeaponGeometry(ShadowBody body)
	{
		if (body?.UsesCustomRoundedBox == true) return $"rounded-box/center:{FormatVector(body.CustomBoxCenter)}/size:{FormatVector(body.CustomBoxSize)}";
		if (body?.UsesCustomBox == true) return $"box/center:{FormatVector(body.CustomBoxCenter)}/size:{FormatVector(body.CustomBoxSize)}";
		return "none";
	}

	private static string FormatVector(Vector3 value)
	{
		return $"({value.x:F3},{value.y:F3},{value.z:F3})";
	}

	private static bool ValidWeaponBoxSize(Vector3 size)
	{
		return IsFinite(size) && size.x > 0.001f && size.y > 0.001f && size.z > 0.001f &&
			size.x <= MaximumWeaponBoxDimension && size.y <= MaximumWeaponBoxDimension && size.z <= MaximumWeaponBoxDimension;
	}

	private static void RegisterSpecialBody(ShadowRig rig, ShadowBody body)
	{
		if (rig == null || body == null)
		{
			return;
		}
		if (string.Equals(body.Path, WeaponPath, StringComparison.Ordinal))
		{
			rig.WeaponBody = body;
			rig.WeaponReleased = false;
			return;
		}
		if (!body.CountsForSleep)
		{
			return;
		}
		if (rig.CenterBody == null ||
			(body.Path?.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) >= 0 &&
			rig.CenterBody.Path?.IndexOf("Pelvis", StringComparison.OrdinalIgnoreCase) < 0))
		{
			rig.CenterBody = body;
		}
	}

	private static void DetachWeaponFromCorpse(ShadowRig rig)
	{
		ShadowBody weapon = rig?.WeaponBody;
		if (weapon?.Native == null)
		{
			return;
		}
		Joint nativeJoint = rig.NativeRagdoll?._weaponJoint;
		if (nativeJoint != null)
		{
			DestroyObject(nativeJoint);
		}
		CorpseWeaponCollisionFilter.Restore(rig.NativeRagdoll, weapon.Native);
	}

	private static bool TryWakeIndependentWeapon(ShadowRig rig, out string failure)
	{
		failure = null;
		ShadowBody weapon = rig?.WeaponBody;
		if (weapon?.Proxy == null)
		{
			failure = "the independent weapon proxy is unavailable";
			return false;
		}
		bool wasSleeping = rig.WeaponSleeping || weapon.Proxy.IsSleeping() || weapon.Proxy.isKinematic;
		bool fullPrebake = UsingExternalBackend && _externalRuntime.IsFullPrebake;
		if (fullPrebake)
		{
			rig.EnvironmentAcquired = true;
		}
		else if (!rig.EnvironmentAcquired)
		{
			Vector3 center = weapon.Proxy.worldCenterOfMass;
			Vector3 velocity = IsFinite(weapon.Proxy.velocity) ? weapon.Proxy.velocity : Vector3.zero;
			Vector3 sweepEnd = center + velocity * EnvironmentPredictionSeconds;
			if (!TryAcquireEnvironment(rig, center, sweepEnd, out failure, out _, out _))
			{
				return false;
			}
			_independentWeaponEnvironmentReacquireCount++;
		}
		weapon.Proxy.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
		if (weapon.Proxy.isKinematic) weapon.Proxy.isKinematic = false;
		rig.WeaponSleeping = false;
		rig.WeaponSleepingSteps = 0;
		rig.NextEnvironmentRefreshFrame = Time.frameCount + ProximityEnvironmentRefreshIntervalFrames;
		weapon.Proxy.WakeUp();
		if (wasSleeping)
		{
			_independentWeaponWakeCount++;
		}
		return true;
	}

	private static bool TryCloneJoints(ShadowRig rig, out string failure)
	{
		failure = null;
		foreach (CharacterJointSpawner spawner in rig.NativeRagdoll._jointSpawners ?? Array.Empty<CharacterJointSpawner>())
		{
			CharacterJoint nativeJoint = spawner?.GetComponent<CharacterJoint>();
			Rigidbody nativeBody = nativeJoint?.GetComponent<Rigidbody>();
			ShadowBody connectedBody = null;
			if (nativeJoint?.connectedBody != null) rig.BodiesByNative.TryGetValue(nativeJoint.connectedBody, out connectedBody);
			if (nativeBody != null && rig.BodiesByNative.TryGetValue(nativeBody, out ShadowBody severedBody) &&
				(rig.PermanentlyDisabledBodies.Contains(severedBody) || connectedBody != null && rig.PermanentlyDisabledBodies.Contains(connectedBody))) continue;
			if (nativeJoint == null || nativeBody == null || !rig.BodiesByNative.TryGetValue(nativeBody, out ShadowBody body) ||
				!TryGetConnectedExternalBody(nativeJoint.connectedBody, rig, out IExternalPhysicsBody connected) ||
				body.Proxy is not ExternalShadowBodyProxy externalBody ||
				!_externalRuntime.TryCreateJoint(nativeJoint, externalBody.ExternalBody, connected, out IExternalPhysicsJoint externalJoint, out failure))
			{
				failure = failure ?? "a native CharacterJoint could not be mapped to the external rig";
				return false;
			}
			rig.ExternalJoints.Add(new ExternalJointBinding
			{
				Joint = externalJoint,
				NativeJoint = nativeJoint,
				AdmissionRelativeRotation = connectedBody?.Proxy == null ? body.Proxy.rotation :
					Quaternion.Inverse(connectedBody.Proxy.rotation) * body.Proxy.rotation,
				Body = body,
				ConnectedBody = connectedBody
			});
		}
		// Bound diagnostics to the first four corpse rigs, three snapshots each.
		if (rig.ExternalGroup <= 4) rig.JointDiagnosticSamples = 3;
		return true;
	}

	private static bool TryGetConnectedExternalBody(Rigidbody nativeConnectedBody, ShadowRig rig, out IExternalPhysicsBody connectedBody)
	{
		if (nativeConnectedBody == null)
		{
			connectedBody = null;
			return true;
		}
		if (rig.BodiesByNative.TryGetValue(nativeConnectedBody, out ShadowBody body) && body.Proxy is ExternalShadowBodyProxy external)
		{
			connectedBody = external.ExternalBody;
			return connectedBody != null;
		}
		connectedBody = null;
		return false;
	}

	private static void ApplyNativeIgnorePairs(ShadowRig rig)
	{
		for (int leftIndex = 0; leftIndex < rig.Bodies.Count; leftIndex++)
		{
			ShadowBody leftBody = rig.Bodies[leftIndex];
			for (int rightIndex = leftIndex; rightIndex < rig.Bodies.Count; rightIndex++)
			{
				ShadowBody rightBody = rig.Bodies[rightIndex];
				// The weapon is released from the hand joint and must collide freely
				// with every corpse body. EFT's attached-weapon ignore pairs are not
				// applicable to the independent shadow actor.
				if (ReferenceEquals(leftBody, rig.WeaponBody) || ReferenceEquals(rightBody, rig.WeaponBody))
				{
					continue;
				}
				foreach (Collider left in leftBody.NativeColliders)
				{
					foreach (Collider right in rightBody.NativeColliders)
					{
						if (left == null || right == null || left == right ||
							(!PhysicsExtensions.GetIgnoreCollision(left, right) && !Physics.GetIgnoreCollision(left, right)))
						{
							continue;
						}
						_externalRuntime.IgnoreCollision(left, right);
					}
				}
			}
		}
	}

	private static bool TryAcquireEnvironment(ShadowRig rig, Vector3 center, Vector3 sweepEnd, out string failure, out int created, out int reused)
	{
		if (!_externalRuntime.EnsureEnvironment(rig.ExternalGroup, center, sweepEnd, out ExternalEnvironmentAcquireResult result, out failure))
		{
			created = 0;
			reused = 0;
			return false;
		}
		created = result.Created;
		reused = result.Reused;
		rig.EnvironmentCenter = center;
		rig.EnvironmentSweepEnd = sweepEnd;
		rig.EnvironmentAcquired = true;
		return true;
	}

	private static void ConfigureCorpseCollision(Collider collider, int deadbodyLayer)
	{
		if (collider == null || deadbodyLayer < 0)
		{
			return;
		}

		// Shadow colliders are physics-only. The native shell still owns hit queries,
		// so normalize every limb to EFT's corpse layer and explicitly add that layer
		// to the per-collider override. This preserves global environment filtering
		// while guaranteeing active/settled and active/active shadow contact even when
		// EFT's global Deadbody-to-Deadbody matrix entry is disabled.
		int deadbodyMask = 1 << deadbodyLayer;
		collider.gameObject.layer = deadbodyLayer;
		collider.includeLayers = collider.includeLayers.value | deadbodyMask;
		collider.excludeLayers = collider.excludeLayers.value & ~deadbodyMask;
		_corpseCollisionColliderCount++;
	}

	private static void ConfigureLocalGibQueryCollider(Collider collider)
	{
		if (collider == null) return;
		int hitColliderLayer = LayerMask.NameToLayer("HitCollider");
		if (hitColliderLayer >= 0) collider.gameObject.layer = hitColliderLayer;
		// Keep an enabled non-trigger shape for EFT ballistic queries, while the
		// per-collider contact override excludes every Unity physics layer. Actual
		// motion and environment contact remain owned only by external PhysX.
		collider.includeLayers = new LayerMask { value = 0 };
		collider.excludeLayers = new LayerMask { value = ~0 };
		collider.isTrigger = false;
		collider.enabled = true;
	}

	private static bool RefreshEnvironmentIfNeeded(ShadowRig rig)
	{
		bool fullPrebake = UsingExternalBackend && _externalRuntime.IsFullPrebake;
		if (Time.frameCount < rig.NextEnvironmentRefreshFrame)
		{
			return true;
		}
		rig.NextEnvironmentRefreshFrame = Time.frameCount +
			(fullPrebake ? FullPrebakeRuntimeProbeIntervalFrames : ProximityEnvironmentRefreshIntervalFrames);
		Vector3 center = CalculateCenter(rig);
		Vector3 sweepEnd = CalculateEnvironmentSweepEnd(rig, center);
		float refreshDistanceSquared = EnvironmentRefreshDistance * EnvironmentRefreshDistance;
		if (!fullPrebake && rig.EnvironmentAcquired && (center - rig.EnvironmentCenter).sqrMagnitude < refreshDistanceSquared &&
			(sweepEnd - rig.EnvironmentSweepEnd).sqrMagnitude < refreshDistanceSquared)
		{
			return true;
		}
		if (!fullPrebake && !TryReserveProximityAcquire())
		{
			// Preserve the existing corridor for one more frame. Its two-metre radius
			// and predictive sweep provide room to distribute dense corpse piles
			// instead of issuing every overlapping Unity query in one Update.
			rig.NextEnvironmentRefreshFrame = Time.frameCount + 1;
			_deferredProximityRefreshes++;
			return true;
		}
		if (TryAcquireEnvironment(rig, center, sweepEnd, out string failure, out _, out _))
		{
			return true;
		}
		RupturePlugin.Log.LogWarning($"Shadow environment expansion failed for {GetCorpseName(rig.NativeRagdoll)}: {failure}");
		return false;
	}

	private static bool TryReserveProximityAcquire()
	{
		int frame = Time.frameCount;
		if (_proximityAcquireFrame != frame)
		{
			_proximityAcquireFrame = frame;
			_proximityAcquiresThisFrame = 0;
		}
		if (_proximityAcquiresThisFrame >= MaximumProximityAcquiresPerFrame)
		{
			return false;
		}
		_proximityAcquiresThisFrame++;
		return true;
	}

	private static void UpdateSettledCollisionActivation()
	{
		_enabledSettledRigs = 0;
		_enabledSettledBodies = 0;
		// Permanent impact reactivation is the common interactive-corpse mode. In
		// that mode every settled actor is enabled regardless of active-rig distance,
		// so avoid calculating centers and performing the redundant proximity scan.
		if (RagdollCoordinator.ImpactReactivationEnabled)
		{
			foreach (ShadowRig settled in Rigs)
			{
				if (settled?.Settled != true || settled.Root == null) continue;
				if (!settled.Root.activeSelf)
				{
					settled.Root.SetActive(true);
					SetRigCollisionEnabled(settled, true);
				}
				_enabledSettledRigs++;
				_enabledSettledBodies += settled.Bodies.Count;
			}
			return;
		}

		float activationRadiusSquared = SettledCollisionActivationRadius * SettledCollisionActivationRadius;
		ActiveRigCenters.Clear();
		foreach (ShadowRig active in Rigs)
		{
			if (active?.Active == true)
			{
				ActiveRigCenters.Add(CalculateCenter(active));
			}
		}
		foreach (ShadowRig settled in Rigs)
		{
			if (settled?.Settled != true || settled.Root == null)
			{
				continue;
			}
			// Opting into post-mortem reactivation means the retained collision actor
			// must remain addressable even when no other ragdoll is currently active.
			// Otherwise old bodies silently disappear from the wake/contact world.
			bool enable = false;
			Vector3 settledCenter = CalculateCenter(settled);
			foreach (Vector3 activeCenter in ActiveRigCenters)
			{
				if ((activeCenter - settledCenter).sqrMagnitude <= activationRadiusSquared)
				{
					enable = true;
					break;
				}
			}
			if (settled.Root.activeSelf != enable)
			{
				settled.Root.SetActive(enable);
				SetRigCollisionEnabled(settled, enable);
			}
			if (enable)
			{
				_enabledSettledRigs++;
				_enabledSettledBodies += settled.Bodies.Count;
			}
		}
	}

	private static void WakeDormantRigsFromExternalEnvironment()
	{
		IReadOnlyList<Bounds> movedBounds = _externalRuntime?.RecentlyMovedBounds;
		if (movedBounds == null || movedBounds.Count == 0 || RagdollCoordinator.IsRemoteClient)
		{
			return;
		}
		foreach (ShadowRig rig in Rigs)
		{
			if (rig == null || (!rig.Dormant && !rig.Settled))
			{
				continue;
			}
			bool intersects = false;
			bool weaponIntersects = false;
			foreach (Bounds sourceBounds in movedBounds)
			{
				Bounds expanded = sourceBounds;
				expanded.Expand(0.05f);
				weaponIntersects = BodyIntersectsBounds(rig.WeaponBody, expanded);
				intersects = weaponIntersects;
				foreach (ShadowBody body in rig.Bodies)
				{
					if (ReferenceEquals(body, rig.WeaponBody)) continue;
					foreach (Collider collider in body.NativeColliders)
					{
						if (collider != null && collider.enabled && expanded.Intersects(collider.bounds))
						{
							intersects = true;
							break;
						}
					}
					if (intersects) break;
				}
				if (intersects) break;
			}
			if (!intersects) continue;
			bool wasSettled = rig.Settled;
			bool reactivated = wasSettled
				? RagdollCoordinator.ReactivateFromMovingEnvironment(rig.Corpse)
				: Wake(rig.Corpse);
			bool independentWeaponWoke = !reactivated && weaponIntersects && TryWakeIndependentWeapon(rig, out _);
			if (!reactivated && !independentWeaponWoke)
			{
				continue;
			}
			_environmentWakeCount++;
			if (!wasSettled)
			{
				RagdollCoordinator.SendShadowWake(rig.Corpse);
			}
			RupturePlugin.Log.LogInfo($"Shadow ragdoll{(independentWeaponWoke && !reactivated ? " weapon" : string.Empty)} woke for {GetCorpseName(rig.NativeRagdoll)} after contact with moved external environment geometry.");
		}
	}

	private static bool BodyIntersectsBounds(ShadowBody body, Bounds bounds)
	{
		if (body == null) return false;
		foreach (Collider collider in body.NativeColliders)
		{
			if (collider != null && collider.enabled && bounds.Intersects(collider.bounds)) return true;
		}
		return false;
	}

	private static void SyncNativeFromProxy(ShadowRig rig)
	{
		ReleaseInvalidWeapon(rig);
		LogJointDiagnostic(rig);
		foreach (ShadowBody body in rig.Bodies)
		{
			if (rig.PermanentlyDisabledBodies.Contains(body))
			{
				UpdateParkedBodyPose(body, updateVisible: true);
				continue;
			}
			if (body.NativeTransform == null || body.Proxy == null ||
				(ReferenceEquals(body, rig.WeaponBody) && (body.Proxy.isKinematic || rig.WeaponSleeping)))
			{
				continue;
			}
			SetPresentedPose(body.Proxy, body.NativeTransform);
		}
		SyncDetachedEffectsFromProxy(rig);
	}

	private static void LogJointDiagnostic(ShadowRig rig)
	{
		if (rig.JointDiagnosticSamples <= 0 || Time.realtimeSinceStartup < rig.NextJointDiagnosticAt) return;
		rig.JointDiagnosticSamples--;
		rig.NextJointDiagnosticAt = Time.realtimeSinceStartup + 1f;
		float largestAnchorGap = 0f;
		string largestGapBody = "none";
		foreach (ExternalJointBinding binding in rig.ExternalJoints)
		{
			CharacterJoint joint = binding.NativeJoint;
			IShadowBodyProxy child = binding.Body?.Proxy;
			IShadowBodyProxy parent = binding.ConnectedBody?.Proxy;
			if (joint == null || child == null || parent == null ||
				rig.PermanentlyDisabledBodies.Contains(binding.Body)) continue;
			// Measure the exact unscaled anchor coordinates passed to the backend.
			float gap = Vector3.Distance(child.position + child.rotation * joint.anchor,
				parent.position + parent.rotation * joint.connectedAnchor);
			if (gap > largestAnchorGap) { largestAnchorGap = gap; largestGapBody = binding.Body.Path; }
			if (joint.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) < 0 &&
				joint.name.IndexOf("neck", StringComparison.OrdinalIgnoreCase) < 0) continue;
			Quaternion relative = Quaternion.Inverse(parent.rotation) * child.rotation;
			RupturePlugin.Log.LogInfo($"RPT head joint audit: profile={rig.Corpse?.PlayerProfileID}, body={joint.name}, " +
				$"samplesLeft={rig.JointDiagnosticSamples}, admissionDeltaDeg={Quaternion.Angle(binding.AdmissionRelativeRotation, relative):F2}, " +
				$"anchorGapMm={gap * 1000f:F2}, scale={joint.transform.lossyScale}, axis={joint.axis}, swingAxis={joint.swingAxis}, " +
				$"twist={joint.lowTwistLimit.limit:F1}/{joint.highTwistLimit.limit:F1}, swing={joint.swing1Limit.limit:F1}/{joint.swing2Limit.limit:F1}, " +
				$"spring={joint.twistLimitSpring.spring:F1}/{joint.swingLimitSpring.spring:F1}, damper={joint.twistLimitSpring.damper:F1}/{joint.swingLimitSpring.damper:F1}, " +
				$"projection={joint.enableProjection}/{joint.projectionAngle:F1}/{joint.projectionDistance:F3}, " +
				$"massScale={joint.massScale:F2}/{joint.connectedMassScale:F2}, angularSpeed={child.angularVelocity.magnitude:F2}.");
		}
		RupturePlugin.Log.LogInfo($"RPT joint separation audit: profile={rig.Corpse?.PlayerProfileID}, " +
			$"maxAnchorGapMm={largestAnchorGap * 1000f:F2}, body={largestGapBody}, samplesLeft={rig.JointDiagnosticSamples}.");
	}

	private static void SyncIndependentBodiesFromProxy(ShadowRig rig)
	{
		ReleaseInvalidWeapon(rig);
		ShadowBody weapon = rig?.WeaponBody;
		if (weapon?.NativeTransform != null && weapon.Proxy != null)
		{
			SetPresentedPose(weapon.Proxy, weapon.NativeTransform);
		}
		SyncDetachedEffectsFromProxy(rig);
	}

	private static void UpdateIndependentWeaponState(ShadowRig rig)
	{
		IShadowBodyProxy weapon = rig?.WeaponBody?.Proxy;
		if (weapon == null || weapon.isKinematic)
		{
			return;
		}
		if (!weapon.IsSleeping())
		{
			rig.WeaponSleeping = false;
			rig.WeaponSleepingSteps = 0;
			return;
		}
		rig.WeaponSleepingSteps++;
		if (rig.WeaponSleepingSteps < RequiredSleepingSteps || rig.WeaponSleeping)
		{
			return;
		}
		rig.WeaponSleeping = true;
		_independentWeaponSleepCount++;
	}

	private static void SyncDetachedEffectsFromProxy(ShadowRig rig)
	{
		if (rig == null)
		{
			return;
		}
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null) continue;
			int count = Mathf.Min(detached.Bodies.Count, detached.SimulationBodies.Count);
			for (int index = 0; index < count; index++)
			{
				Rigidbody native = detached.Bodies[index];
				IShadowBodyProxy simulation = detached.SimulationBodies[index];
				if (native == null || simulation == null || simulation.UnityBody != null) continue;
				SetPresentedPose(simulation, native.transform);
			}
			LogDetachedEffectMotion(detached);
		}
	}

	private static void SetPresentedPose(IShadowBodyProxy body, Transform visible)
	{
		if (body == null || visible == null) return;
		visible.SetPositionAndRotation(body.position, body.rotation);
	}

	private static void LogDetachedEffectMotion(DetachedEffect detached)
	{
		if (detached == null || detached.MotionDiagnosticLogged ||
			Time.realtimeSinceStartup - detached.StartedAt < 0.75f || detached.SimulationBodies.Count == 0)
		{
			return;
		}

		IShadowBodyProxy body = detached.SimulationBodies[0];
		if (body == null)
		{
			return;
		}
		detached.MotionDiagnosticLogged = true;
		Vector3 currentPosition = body.position;
		float displacement = Vector3.Distance(detached.SpawnPosition, currentPosition);
		float verticalDelta = currentPosition.y - detached.SpawnPosition.y;
		string outcome = verticalDelta < -1.5f ? "suspected-floor-tunnel" : displacement < 0.03f ? "stationary-or-blocked" : "moved";
		RupturePlugin.Log.LogInfo($"Detached gore motion: asset={detached.Root?.name ?? "<destroyed>"}, source={(detached.InitialDeathSpawn ? "initial-death" : "post-mortem")}, " +
			$"outcome={outcome}, spawn={detached.SpawnPosition}, current={currentPosition}, displacement={displacement:F3}m, verticalDelta={verticalDelta:F3}m, " +
			$"speed={body.velocity.magnitude:F2}m/s, sleeping={body.IsSleeping()}, kinematic={body.isKinematic}.");
	}

	private static void UpdateSettledDetachedEffects(ShadowRig rig)
	{
		if (rig == null || !rig.Settled)
		{
			return;
		}
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null)
			{
				continue;
			}
			bool hasDynamicBody = false;
			bool sleeping = true;
			foreach (IShadowBodyProxy body in detached.SimulationBodies)
			{
				if (body == null || body.isKinematic)
				{
					continue;
				}
				hasDynamicBody = true;
				if (!body.IsSleeping())
				{
					sleeping = false;
				}
			}
			if (!hasDynamicBody)
			{
				continue;
			}
			detached.SleepingSteps = sleeping ? detached.SleepingSteps + 1 : 0;
			bool dynamicDeadlineExpired = Time.realtimeSinceStartup - detached.StartedAt >= DetachedEffectMaximumDynamicDuration;
			if (detached.SleepingSteps < RequiredSleepingSteps && !dynamicDeadlineExpired)
			{
				continue;
			}
			foreach (IShadowBodyProxy body in detached.SimulationBodies)
			{
				if (body == null)
				{
					continue;
				}
				body.collisionDetectionMode = CollisionDetectionMode.Discrete;
				body.isKinematic = true;
			}
			detached.SleepingSteps = 0;
		}
		if (CountDynamicDetachedBodies(rig) == 0 && !HasDynamicIndependentWeapon(rig))
		{
			ReleaseEnvironment(rig);
		}
	}

	private static void UpdateSleepState(ShadowRig rig)
	{
		if (IsDragHeld(rig.Corpse)) { rig.SleepingSteps = 0; return; }
		bool sleeping = true;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (!rig.PermanentlyDisabledBodies.Contains(body) && body.CountsForSleep &&
				body.Proxy != null && !body.Proxy.IsSleeping())
			{
				sleeping = false;
				break;
			}
		}
		if (sleeping)
		{
			foreach (DetachedEffect detached in rig.DetachedEffects)
			{
				foreach (IShadowBodyProxy body in detached.SimulationBodies)
				{
					if (body != null && !body.IsSleeping())
					{
						sleeping = false;
						break;
					}
				}
				if (!sleeping)
				{
					break;
				}
			}
		}
		rig.SleepingSteps = sleeping ? rig.SleepingSteps + 1 : 0;
		if (rig.SleepingSteps >= RequiredSleepingSteps)
		{
			MakeDormant(rig);
		}
	}

	private static void MakeDormant(ShadowRig rig)
	{
		if (rig == null || !rig.Active)
		{
			return;
		}
		SyncNativeFromProxy(rig);
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body.Proxy == null || ReferenceEquals(body, rig.WeaponBody) ||
				rig.PermanentlyDisabledBodies.Contains(body))
			{
				continue;
			}
			body.Proxy.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.Proxy.isKinematic = true;
		}
		SetDetachedEffectsKinematic(rig, kinematic: true, clearVelocity: false);
		rig.Active = false;
		rig.Dormant = true;
		rig.SleepingSteps = 0;
		RagdollCoordinator.SendPoseSnapshot(rig.Corpse, force: true);
		GetMaximumPoseChange(rig, out float displacement, out float rotation);
		RupturePlugin.Log.LogInfo($"Shadow ragdoll dormant for {GetCorpseName(rig.NativeRagdoll)} after {Time.realtimeSinceStartup - rig.StartedAt:F2}s; " +
			$"maxPoseChange={displacement:F3}m/{rotation:F1}deg; waiting for a native or moving-environment wake until the hard deadline.");
	}

	private static void Wake(ShadowRig rig)
	{
		if (rig == null || !rig.Dormant || rig.Settled)
		{
			return;
		}
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body.Proxy == null || ReferenceEquals(body, rig.WeaponBody) ||
				rig.PermanentlyDisabledBodies.Contains(body))
			{
				continue;
			}
			body.Proxy.collisionDetectionMode = body.NativeCollisionMode;
			body.Proxy.isKinematic = false;
			body.Proxy.WakeUp();
		}
		SetDetachedEffectsKinematic(rig, kinematic: false, clearVelocity: false);
		rig.Dormant = false;
		rig.Active = true;
		rig.SleepingSteps = 0;
	}

	private static bool TryWakeFromImpact(ShadowRig rig)
	{
		if (rig == null || rig.Corpse == null || rig.Root == null)
		{
			return false;
		}
		if (rig.Active)
		{
			return true;
		}
		if (rig.Dormant)
		{
			Wake(rig);
			return rig.Active;
		}
		if (!rig.Settled)
		{
			return false;
		}

		Vector3 center = CalculateCenter(rig);
		Vector3 sweepEnd = CalculateEnvironmentSweepEnd(rig, center);
		if (!(UsingExternalBackend && _externalRuntime.IsFullPrebake) &&
			!TryAcquireEnvironment(rig, center, sweepEnd, out string failure, out _, out _))
		{
			RupturePlugin.Log.LogWarning($"Settled shadow ragdoll could not wake for {GetCorpseName(rig.NativeRagdoll)}: {failure}");
			return false;
		}

		rig.Root.SetActive(true);
		SetRigCollisionEnabled(rig, true);
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body.Proxy == null || ReferenceEquals(body, rig.WeaponBody) ||
				rig.PermanentlyDisabledBodies.Contains(body))
			{
				continue;
			}
			body.Proxy.collisionDetectionMode = body.NativeCollisionMode;
			body.Proxy.isKinematic = false;
			body.Proxy.velocity = Vector3.zero;
			body.Proxy.angularVelocity = Vector3.zero;
			body.Proxy.WakeUp();
		}
		SetDetachedEffectsKinematic(rig, kinematic: false, clearVelocity: true);
		rig.Settled = false;
		rig.Dormant = false;
		rig.Active = true;
		rig.IgnoreNativeStopped = true;
		rig.SleepingSteps = 0;
		rig.StartedAt = Time.realtimeSinceStartup;
		RupturePlugin.Log.LogInfo($"Settled shadow ragdoll reactivated for {GetCorpseName(rig.NativeRagdoll)}.");
		return true;
	}

	private static bool AreNativeCollidersTouching(ShadowRig leftRig, ShadowRig rightRig)
	{
		if (leftRig == null || rightRig == null)
		{
			return false;
		}
		foreach (ShadowBody leftBody in leftRig.Bodies)
		{
			if (!leftBody.CountsForSleep)
			{
				continue;
			}
			foreach (Collider leftCollider in leftBody.NativeColliders)
			{
				if (leftCollider == null || !leftCollider.enabled || !leftCollider.gameObject.activeInHierarchy)
				{
					continue;
				}
				Bounds leftBounds = leftCollider.bounds;
				leftBounds.Expand(0.1f);
				foreach (ShadowBody rightBody in rightRig.Bodies)
				{
					if (!rightBody.CountsForSleep)
					{
						continue;
					}
					foreach (Collider rightCollider in rightBody.NativeColliders)
					{
						if (rightCollider != null && rightCollider.enabled && rightCollider.gameObject.activeInHierarchy && leftBounds.Intersects(rightCollider.bounds))
						{
							return true;
						}
					}
				}
			}
		}
		return false;
	}

	private static void Settle(ShadowRig rig)
	{
		if (rig != null && IsDragHeld(rig.Corpse)) return;
		if (rig == null || (!rig.Active && !rig.Dormant))
		{
			return;
		}
		SyncNativeFromProxy(rig);
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body.Proxy == null || ReferenceEquals(body, rig.WeaponBody))
			{
				continue;
			}
			body.Proxy.collisionDetectionMode = CollisionDetectionMode.Discrete;
			body.Proxy.isKinematic = true;
		}
		SetDetachedEffectsKinematic(rig, kinematic: true, clearVelocity: false);
		if (CountDynamicDetachedBodies(rig) == 0 && !HasDynamicIndependentWeapon(rig))
		{
			ReleaseEnvironment(rig);
		}
		rig.Active = false;
		rig.Dormant = false;
		rig.Settled = true;
		GetMaximumPoseChange(rig, out float displacement, out float rotation);
		RupturePlugin.Log.LogInfo($"Shadow ragdoll settled for {GetCorpseName(rig.NativeRagdoll)} after {Time.realtimeSinceStartup - rig.StartedAt:F2}s; " +
			$"maxPoseChange={displacement:F3}m/{rotation:F1}deg; retained {rig.CoreBodies.Count} kinematic corpse bodies; independentWeaponAwake={rig.WeaponBody?.Proxy != null && !rig.WeaponBody.Proxy.isKinematic && !rig.WeaponSleeping}.");
	}

	private static void SetDetachedEffectsKinematic(ShadowRig rig, bool kinematic, bool clearVelocity)
	{
		if (rig == null)
		{
			return;
		}
		for (int effectIndex = rig.DetachedEffects.Count - 1; effectIndex >= 0; effectIndex--)
		{
			DetachedEffect detached = rig.DetachedEffects[effectIndex];
			if (detached?.Root == null)
			{
				rig.DetachedEffects.RemoveAt(effectIndex);
				continue;
			}
			foreach (IShadowBodyProxy body in detached.SimulationBodies)
			{
				if (body == null)
				{
					continue;
				}
				if (kinematic)
				{
					body.collisionDetectionMode = CollisionDetectionMode.Discrete;
				}
				body.isKinematic = kinematic;
				if (!kinematic)
				{
					if (clearVelocity)
					{
						body.velocity = Vector3.zero;
						body.angularVelocity = Vector3.zero;
					}
					body.WakeUp();
				}
			}
		}
	}

	private static void SetRigCollisionEnabled(ShadowRig rig, bool enabled)
	{
		if (rig == null || !UsingExternalBackend) return;
		foreach (ShadowBody body in rig.Bodies)
			if (!ReferenceEquals(body, rig.WeaponBody) && body?.Proxy is ExternalShadowBodyProxy external)
				external.ExternalBody.CollisionEnabled = enabled && !rig.PermanentlyDisabledBodies.Contains(body);
		foreach (DetachedEffect detached in rig.DetachedEffects)
			foreach (IShadowBodyProxy body in detached.SimulationBodies)
				if (body is ExternalShadowBodyProxy external) external.ExternalBody.CollisionEnabled = enabled;
	}

	private static bool ApplyRadialImpulse(IShadowBodyProxy body, Vector3 origin, float radius, float force)
	{
		if (body == null || body.isKinematic)
		{
			return false;
		}
		Vector3 offset = body.worldCenterOfMass - origin;
		float distance = offset.magnitude;
		if (distance > radius)
		{
			return false;
		}
		float falloff = Mathf.Max(0.2f, 1f - distance / radius);
		Vector3 direction = distance > 0.001f ? offset / distance : Vector3.up;
		direction = (direction + Vector3.up * 0.15f).normalized;
		body.AddForce(direction * (force * falloff), ForceMode.Impulse);
		return true;
	}

	private static bool ApplyRadialImpulse(Rigidbody body, Vector3 origin, float radius, float force)
	{
		return body != null && ApplyRadialImpulse(new UnityShadowBodyProxy(body), origin, radius, force);
	}

	private static void GetMaximumPoseChange(ShadowRig rig, out float displacement, out float rotation)
	{
		displacement = 0f;
		rotation = 0f;
		foreach (ShadowBody body in rig.Bodies)
		{
			if (!body.CountsForSleep || body.Proxy == null || rig.PermanentlyDisabledBodies.Contains(body))
			{
				continue;
			}
			displacement = Mathf.Max(displacement, Vector3.Distance(body.InitialPosition, body.Proxy.position));
			rotation = Mathf.Max(rotation, Quaternion.Angle(body.InitialRotation, body.Proxy.rotation));
		}
	}

	private static void AbortToNative(ShadowRig rig, string reason, bool log = true)
	{
		if (rig == null)
		{
			return;
		}
		if (rig.Committed)
		{
			foreach (ShadowBody body in rig.Bodies)
			{
				if (!body.NativeHandedOff || body.Native == null || body.Proxy == null)
				{
					continue;
				}
				body.Native.position = body.Proxy.position;
				body.Native.rotation = body.Proxy.rotation;
				body.Native.isKinematic = body.NativeWasKinematic;
				body.Native.collisionDetectionMode = body.NativeCollisionMode;
				if (!body.NativeWasKinematic)
				{
					body.Native.velocity = body.Proxy.velocity;
					body.Native.angularVelocity = body.Proxy.angularVelocity;
					for (int supportIndex = 0; supportIndex < body.NativeSupportCount; supportIndex++)
					{
						PhysicsExtensions.UpdateController.SupportRigidbody(body.Native, 0f, null);
					}
				}
				body.NativeHandedOff = false;
			}
			foreach (EFT.AssetsManager.PlayerRigidbodySleepHierarchy hierarchy in rig.NativeRagdoll?._rigidbodySleepHierarchy ?? new List<EFT.AssetsManager.PlayerRigidbodySleepHierarchy>())
			{
				hierarchy?.Reset();
			}
			RestoreDetachedEffectsToNative(rig);
		}
		DestroyRig(rig, restoreNative: false);
		if (log)
		{
			_buildFailures++;
			RupturePlugin.Log.LogWarning($"Shadow ragdoll returned to native EFT physics for {GetCorpseName(rig.NativeRagdoll)}: {reason}.");
		}
	}

	private static void DestroyRig(ShadowRig rig, bool restoreNative)
	{
		if (rig == null)
		{
			return;
		}
		InvalidateRigDrag(rig);
		if (restoreNative && (rig.Active || rig.Dormant))
		{
			AbortToNative(rig, "raid reset", log: false);
			return;
		}
		ReleaseEnvironment(rig);
		foreach (ExternalJointBinding binding in rig.ExternalJoints)
		{
			_externalRuntime?.DestroyJoint(binding?.Joint);
		}
		rig.ExternalJoints.Clear();
		foreach (ShadowBody body in rig.Bodies)
		{
			if (body?.Proxy is ExternalShadowBodyProxy externalBody)
			{
				_externalRuntime?.DestroyBody(externalBody.ExternalBody);
			}
			else if (body?.Proxy?.gameObject != null)
			{
				DestroyObject(body.Proxy.gameObject);
			}
		}
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null) continue;
			foreach (IShadowBodyProxy simulation in detached.SimulationBodies)
			{
				if (simulation is ExternalShadowBodyProxy externalBody) _externalRuntime?.DestroyBody(externalBody.ExternalBody);
			}
			if (!detached.NativeRestored) DestroyObject(detached.Root);
		}
		rig.DetachedEffects.Clear();
		Rigs.Remove(rig);
		if (rig.NativeRagdoll != null && RigsByRagdoll.TryGetValue(rig.NativeRagdoll, out ShadowRig byRagdoll) && ReferenceEquals(byRagdoll, rig))
		{
			RigsByRagdoll.Remove(rig.NativeRagdoll);
		}
		if (!ReferenceEquals(rig.Corpse, null) && RigsByCorpse.TryGetValue(rig.Corpse, out ShadowRig byCorpse) && ReferenceEquals(byCorpse, rig))
		{
			RigsByCorpse.Remove(rig.Corpse);
		}
		foreach (ShadowBody body in rig.Bodies)
		{
			if (!ReferenceEquals(body.Native, null) && BodiesByNative.TryGetValue(body.Native, out ShadowBody current) && ReferenceEquals(current, body))
			{
				BodiesByNative.Remove(body.Native);
			}
		}
		if (rig.Root != null)
		{
			rig.Root.SetActive(false);
		}
		DestroyObject(rig.Root);
		rig.Root = null;
		rig.Active = false;
		rig.Dormant = false;
	}

	private static void RestoreDetachedEffectsToNative(ShadowRig rig)
	{
		if (rig == null) return;
		foreach (DetachedEffect detached in rig.DetachedEffects)
		{
			if (detached == null) continue;
			foreach (ColliderContactState contact in detached.Contacts) contact.Restore();
			detached.NativeRestored = true;
			int count = Mathf.Min(detached.Bodies.Count, detached.SimulationBodies.Count);
			for (int index = 0; index < count; index++)
			{
				Rigidbody native = detached.Bodies[index];
				IShadowBodyProxy simulation = detached.SimulationBodies[index];
				if (native == null || simulation is not ExternalShadowBodyProxy externalBody) continue;
				native.position = simulation.position;
				native.rotation = simulation.rotation;
				native.isKinematic = false;
				native.useGravity = true;
				native.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
				native.velocity = simulation.velocity;
				native.angularVelocity = simulation.angularVelocity;
				_externalRuntime?.DestroyBody(externalBody.ExternalBody);
			}
		}
	}

	private static void ReleaseEnvironment(ShadowRig rig)
	{
		if (rig != null && !(UsingExternalBackend && _externalRuntime.IsFullPrebake))
		{
			rig.EnvironmentAcquired = false;
		}
		if (rig != null && UsingExternalBackend && !_externalRuntime.IsFullPrebake && rig.ExternalGroup != 0)
		{
			_externalRuntime.ReleaseEnvironment(rig.ExternalGroup);
		}
	}

	private static bool TryFindNativeBody(EFT.Interactive.CorpseRagdoll ragdoll, string path, out Rigidbody nativeBody)
	{
		nativeBody = null;
		if (string.Equals(path, WeaponPath, StringComparison.Ordinal))
		{
			nativeBody = ragdoll.WeaponRigidbody;
			return nativeBody != null;
		}
		if (!RigsByRagdoll.TryGetValue(ragdoll, out ShadowRig rig))
		{
			return false;
		}
		foreach (ShadowBody body in rig.Bodies)
		{
			if (string.Equals(body.Path, path, StringComparison.Ordinal))
			{
				nativeBody = body.Native;
				return nativeBody != null;
			}
		}
		return false;
	}

	private static void ApplyForce(IShadowBodyProxy body, ShadowForceOperation operation, Vector3 force, Vector3 point, float scalar, float radius, float upwardsModifier, ForceMode mode)
	{
		switch (operation)
		{
		case ShadowForceOperation.AddForce:
			body.AddForce(force, mode);
			break;
		case ShadowForceOperation.AddRelativeForce:
			body.AddRelativeForce(force, mode);
			break;
		case ShadowForceOperation.AddForceAtPosition:
			body.AddForceAtPosition(force, body.TransformPoint(point), mode);
			break;
		case ShadowForceOperation.AddExplosionForce:
			body.AddExplosionForce(scalar, point, radius, upwardsModifier, mode);
			break;
		case ShadowForceOperation.AddTorque:
			body.AddTorque(force, mode);
			break;
		case ShadowForceOperation.AddRelativeTorque:
			body.AddRelativeTorque(force, mode);
			break;
		}
	}

	private static int BuildEnvironmentMask()
	{
		int deadbodyLayer = LayerMask.NameToLayer("Deadbody");
		if (deadbodyLayer < 0)
		{
			return 0;
		}
		int mask = 0;
		for (int layer = 0; layer < 32; layer++)
		{
			if (layer != deadbodyLayer && !Physics.GetIgnoreLayerCollision(deadbodyLayer, layer))
			{
				mask |= 1 << layer;
			}
		}
		return mask;
	}

	private static Vector3 CalculateCenter(ShadowRig rig)
	{
		ShadowBody centerBody = GetEnvironmentTrackingBody(rig);
		return centerBody?.Proxy != null
			? centerBody.Proxy.TransformPoint(centerBody.Proxy.centerOfMass)
			: rig?.Corpse != null ? rig.Corpse.transform.position : Vector3.zero;
	}

	private static string DescribeCorpseCollisionLayer()
	{
		int deadbodyLayer = LayerMask.NameToLayer("Deadbody");
		return deadbodyLayer < 0
			? "missing"
			: $"{deadbodyLayer}:Deadbody/globalSelfIgnored={Physics.GetIgnoreLayerCollision(deadbodyLayer, deadbodyLayer)}";
	}

	private static Vector3 CalculateEnvironmentSweepEnd(ShadowRig rig, Vector3 center)
	{
		Vector3 velocity = CalculateCenterVelocity(rig);
		Vector3 displacement = velocity * EnvironmentPredictionSeconds +
			Physics.gravity * (0.5f * EnvironmentPredictionSeconds * EnvironmentPredictionSeconds);
		if (displacement.sqrMagnitude > MaximumEnvironmentSweepDistance * MaximumEnvironmentSweepDistance)
		{
			displacement = displacement.normalized * MaximumEnvironmentSweepDistance;
		}
		return center + displacement;
	}

	private static Vector3 CalculateCenterVelocity(ShadowRig rig)
	{
		ShadowBody centerBody = GetEnvironmentTrackingBody(rig);
		if (centerBody?.Proxy == null)
		{
			return Vector3.zero;
		}
		return rig.Active || ReferenceEquals(centerBody, rig.WeaponBody)
			? centerBody.Proxy.velocity
			: !rig.Committed ? centerBody.NativeVelocity : Vector3.zero;
	}

	private static ShadowBody GetEnvironmentTrackingBody(ShadowRig rig)
	{
		if (rig?.Active != true && rig?.WeaponBody?.Proxy != null && !rig.WeaponBody.Proxy.isKinematic)
		{
			return rig.WeaponBody;
		}
		return rig?.CenterBody;
	}

	private static int GetSupportCount(Rigidbody body)
	{
		if (body == null || PhysicsExtensions.UpdateController._rigidbodies == null)
		{
			return 0;
		}
		int count = 0;
		foreach (PhysicsExtensions.UpdateController.RigidbodyData entry in PhysicsExtensions.UpdateController._rigidbodies)
		{
			if (entry.rigidbody == body)
			{
				count++;
			}
		}
		return count;
	}

	private static string GetHierarchyPath(Transform transform)
	{
		if (transform == null)
		{
			return "<missing>";
		}
		Stack<string> names = new Stack<string>();
		for (Transform current = transform; current != null; current = current.parent)
		{
			names.Push(current.name);
		}
		return string.Join("/", names.ToArray());
	}

	private static int GetHierarchyDepth(Transform transform)
	{
		int depth = 0;
		while (transform != null)
		{
			depth++;
			transform = transform.parent;
		}
		return depth;
	}

	private static int GetHierarchyDistance(Transform child, Transform ancestor)
	{
		int distance = 0;
		for (Transform current = child; current != null; current = current.parent)
		{
			if (current == ancestor)
			{
				return distance;
			}
			distance++;
		}
		return int.MaxValue;
	}

	private static string GetCorpseName(EFT.Interactive.CorpseRagdoll ragdoll)
	{
		return (ragdoll?._owner as Corpse)?.PlayerProfileID ?? "unknown corpse";
	}

	private static bool IsFinite(Vector3 value)
	{
		return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
	}

	private static bool IsFinite(Quaternion value)
	{
		return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
	}

	private static bool IsFinite(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value);
	}

	private static void DestroyObject(UnityEngine.Object value)
	{
		if (value != null)
		{
			UnityEngine.Object.Destroy(value);
		}
	}
}
