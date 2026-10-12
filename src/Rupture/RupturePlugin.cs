using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.UI;
using SPT.Reflection.Patching;
using UnityEngine;
using Rupture.Combined.Patches;
using Rupture.Impacts;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.HeadWounds;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.Fractures;
using Rupture.Dismemberment.Classes.Packets;
using Rupture.Dismemberment.Patches;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.Classes.Packets;
using Rupture.Ragdolls.External;
using Rupture.Ragdolls.Patches;
using MainThreadStage = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadStage;

namespace Rupture;

[BepInPlugin("com.rootdarkarchon.rupture", "Rupture", "1.2.1")]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("com.janky.hollywoodfx", BepInDependency.DependencyFlags.SoftDependency)]
[DefaultExecutionOrder(32000)]
public class RupturePlugin : BaseUnityPlugin
{
	private bool _initialized;
	private bool _wasInRaid;
	private bool _raidSimulationStarted;

	public static RupturePlugin Instance { get; private set; }
	internal static RuptureLog Log { get; private set; }

	public void Awake()
	{
		Integration.CorpseDragV1.InitializeThread();
		Instance = this;
		Log = new RuptureLog(Logger);
		RuptureConfiguration.Initialize(Config);
		try
		{
			MultiplayerRuntime.Initialize();
		}
		catch (Exception exception)
		{
			Log.LogError("Rupture initialization stopped: optional Fika integration failed. " + exception);
			enabled = false;
			return;
		}
		_initialized = true;
		if (!MultiplayerRuntime.Current.IsHeadless) Casings.CasingHooks.Install();
		if (!MultiplayerRuntime.Current.IsHeadless) BloodDecalBatch.Install();
		Clothing.ClothingBundleLoader.Install();
		new ConfigurationSavePatch().Enable();
		StartCoroutine(WoundResources.Load());
		ImpactHooks.Install();
		Health.BotSeverHooks.Install();

		HollywoodFxCompatibility.Install();
		new DecalCapacityPatch().Enable();
		new DecalMeshIndexFormatPatch().Enable();
		BodyDecalLodPatch.Install();
		new HeavyBleedingPatch().Enable();
		new KillPatch().Enable();
		new BodiesImpulsePatch().Enable();
		new GrenadeDeadBodiesPatch().Enable();
		new Rupture.Ragdolls.Patches.GameStartedPatch().Enable();
		new HelmetThrowPhysicsPatch().Enable();
		HeadgearPhysicsLifetimePatch.Install();
		new HeadgearOperationPatch().Enable();
		EnableNativeRagdollIntegration();
		EnableShadowRigidbodyBridge();

		ConsoleScreen.Processor.RegisterCommand("RPTPhysicsStatus", RagdollPhysicsDiagnostics.LogStatus);
		ConsoleScreen.Processor.RegisterCommand("RPTPhysicsBodies", RagdollPhysicsDiagnostics.LogSupportedBodies);
		ConsoleScreen.Processor.RegisterCommand("RPTPhysicsScene", RagdollPhysicsDiagnostics.LogSceneCensus);
		ConsoleScreen.Processor.RegisterCommand("RPTPhysicsSample", RagdollPhysicsDiagnostics.ToggleCounterSample);
		ConsoleScreen.Processor.RegisterCommand("RPTShadowStatus", ShadowRagdollWorld.LogStatus);
		ConsoleScreen.Processor.RegisterCommand("RPTHeadWoundStatus", HeadWoundSystem.LogStatus);
		Log.Initialization("Rupture initialized. Performance trace schema=12 (half-second soaking, sleep-aware physics, 4 Hz movable-root polling). Runtime diagnostics require Enable Debug Logging.");
	}

	private static void EnableNativeRagdollIntegration()
	{
		MethodInfo ragdollStart = typeof(EFT.Interactive.CorpseRagdoll).GetMethod(
			"Start",
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			null,
			Type.EmptyTypes,
			null);
		MethodInfo corpseCheck = typeof(Corpse).GetMethod(
			nameof(Corpse.CheckCorpseIsStill),
			BindingFlags.Instance | BindingFlags.Public,
			null,
			new[] { typeof(bool), typeof(float) },
			null);
		MethodInfo observedCorpseCheck = typeof(ObservedCorpse).GetMethod(
			nameof(ObservedCorpse.CheckCorpseIsStill),
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
			null,
			new[] { typeof(bool), typeof(float) },
			null);
		if (ragdollStart == null || corpseCheck == null || observedCorpseCheck == null)
		{
			RupturePlugin.Log.InitializationWarning("Native ragdoll ABI targets are unavailable. RPT lifecycle patches were not installed; EFT corpse behavior remains unchanged.");
			ShadowRagdollWorld.Disable("The required native ragdoll lifecycle ABI is unavailable.");
			return;
		}

		new RagdollClassPatch().Enable();
		new CorpseSleepPatch().Enable();
		new ObservedCorpseSleepPatch().Enable();
		MethodInfo ragdollWake = typeof(EFT.Interactive.CorpseRagdoll).GetMethod(
			nameof(EFT.Interactive.CorpseRagdoll.WakeUp),
			BindingFlags.Instance | BindingFlags.Public,
			null,
			Type.EmptyTypes,
			null);
		if (ragdollWake != null)
		{
			new RagdollWakePatch().Enable();
		}
		else
		{
			ShadowRagdollWorld.Disable("EFT.Interactive.CorpseRagdoll.WakeUp is unavailable, so native wake requests cannot be forwarded.");
		}

		MethodInfo attachWeapon = typeof(EFT.Interactive.CorpseRagdoll).GetMethod(
			"AttachWeapon",
			BindingFlags.Instance | BindingFlags.Public,
			null,
			new[]
			{
				typeof(Rigidbody),
				typeof(GameObject),
				typeof(PlayerBones),
				typeof(TransformLinks),
				typeof(bool),
				typeof(Vector3)
			},
			null);
		if (attachWeapon != null)
		{
			new CorpseWeaponCollisionPatch().Enable();
			new CorpseWeaponReleasePatch().Enable();
		}
		else
		{
			RupturePlugin.Log.InitializationWarning("Native ragdoll weapon attachment ABI is unavailable. Corpse-to-own-weapon collision suppression was not installed.");
		}
	}

	private static void EnableShadowRigidbodyBridge()
	{
		(Type[] Parameters, string Name, Func<ModulePatch> Factory)[] targets =
		{
			(new[] { typeof(Vector3), typeof(ForceMode) }, nameof(Rigidbody.AddForce), () => new RigidbodyAddForcePatch()),
			(new[] { typeof(Vector3), typeof(ForceMode) }, nameof(Rigidbody.AddRelativeForce), () => new RigidbodyAddRelativeForcePatch()),
			(new[] { typeof(Vector3), typeof(Vector3), typeof(ForceMode) }, nameof(Rigidbody.AddForceAtPosition), () => new RigidbodyAddForceAtPositionPatch()),
			(new[] { typeof(float), typeof(Vector3), typeof(float), typeof(float), typeof(ForceMode) }, nameof(Rigidbody.AddExplosionForce), () => new RigidbodyAddExplosionForcePatch()),
			(new[] { typeof(Vector3), typeof(ForceMode) }, nameof(Rigidbody.AddTorque), () => new RigidbodyAddTorquePatch()),
			(new[] { typeof(Vector3), typeof(ForceMode) }, nameof(Rigidbody.AddRelativeTorque), () => new RigidbodyAddRelativeTorquePatch())
		};
		foreach ((Type[] parameters, string name, Func<ModulePatch> factory) in targets)
		{
			MethodInfo method = typeof(Rigidbody).GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, parameters, null);
			if (method == null || method.GetMethodBody() == null)
			{
				ShadowRagdollWorld.Disable($"Unity Rigidbody.{name} managed wrapper is unavailable.");
				return;
			}
		}
		foreach (var target in targets)
		{
			target.Factory().Enable();
		}
	}

	private void Start()
	{
		if (!_initialized) return;
		ShadowVisiblePosePlayerLoop.EnsureInstalled();

	}

	private void Update()
	{
		if (!_initialized) return;
		long callbackStarted = ShadowRagdollWorld.PerformanceLoggingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
		bool raidWorldActive = Singleton<GameWorld>.Instantiated;
		try
		{
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.UpdateSetup, raidWorldActive))
			{
				if (!raidWorldActive)
				{
					_raidSimulationStarted = false;
				}
				else if (!_raidSimulationStarted && IsRaidSimulationReady())
				{
					// Latch the playable-raid boundary. Some EFT/Fika flows transiently
					// change GameStatus again after Started without leaving the raid.
					_raidSimulationStarted = true;
				}
				if (raidWorldActive && !_wasInRaid)
				{
					// Fika or another plugin may have replaced Unity's PlayerLoop after RPT
					// initialized. Revalidate the presentation-only pass at the raid boundary.
					ShadowVisiblePosePlayerLoop.EnsureInstalled();
				}
			}
			bool raidSimulationActive = raidWorldActive && _raidSimulationStarted;
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.ImpactNetwork, raidWorldActive)) ImpactNetwork.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.ImpactState, raidWorldActive)) ImpactStateService.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.ShotImpacts, raidWorldActive)) ShotImpactProcessor.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.Headgear, raidWorldActive)) LethalHeadgearDetach.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.HeadWounds, raidWorldActive)) HeadWoundSystem.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.Blood, raidWorldActive)) PulsatingBloodStreamSystem.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.FracturesUpdate, raidWorldActive)) FractureSystem.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.Coordinator, raidWorldActive)) RagdollCoordinator.Update();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.ShadowUpdate, raidWorldActive)) ShadowRagdollWorld.Update(raidWorldActive, raidSimulationActive);
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.LimbCuts, raidWorldActive)) LimbDismembermentSystem.Update();
			Health.BotSeverStateService.Update(raidSimulationActive);
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.PhysicsDiagnostics, raidWorldActive)) RagdollPhysicsDiagnostics.Update();

			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.UpdateCleanup, raidWorldActive))
			{
				if (_wasInRaid && !raidWorldActive)
				{
					ResetRuntimeServices();
					BodyDecalRendering.Reset();
				}
				_wasInRaid = raidWorldActive;
			}
		}
		finally
		{
			ShadowRagdollWorld.RecordPluginUpdate(callbackStarted, raidWorldActive);
		}
	}

	private void LateUpdate()
	{
		if (!_initialized) return;
		long callbackStarted = ShadowRagdollWorld.PerformanceLoggingEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
		bool raidSimulationActive = _raidSimulationStarted && Singleton<GameWorld>.Instantiated;
		try
		{
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.LatePhysics, raidSimulationActive)) ShadowRagdollWorld.LateUpdate(raidSimulationActive);
			// Retain the correction pass for dormant and remote authority-driven rigs.
			// Locally simulated active rigs were already presented by LateUpdate above.
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.VisibleShell, raidSimulationActive)) ShadowRagdollWorld.LateUpdateVisibleShell();
			using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.FracturesLate, raidSimulationActive)) FractureSystem.LateUpdate();
		}
		finally
		{
			ShadowRagdollWorld.RecordPluginLateUpdate(callbackStarted, raidSimulationActive);
		}
		// Keep diagnostic formatting and log I/O outside the measured physics and
		// presentation callback. Either periodic diagnostic can run independently.
		using (ShadowRagdollWorld.TraceMainThread(MainThreadStage.DiagnosticLogging, raidSimulationActive)) ShadowRagdollWorld.UpdateStatusLogging(raidSimulationActive);
	}

	private static bool IsRaidSimulationReady()
	{
		if (!Singleton<AbstractGame>.Instantiated || Singleton<AbstractGame>.Instance.Status != GameStatus.Started || !Singleton<GameWorld>.Instantiated)
		{
			return false;
		}
		GameWorld gameWorld = Singleton<GameWorld>.Instance;
		// Dedicated Fika authorities intentionally have no local main player. Their
		// GameStatus.Started boundary is sufficient; continuing to require MainPlayer
		// leaves pending shadow rigs unbuilt, so no authoritative pose snapshots can
		// ever be produced even though terminal settle packets still are.
		return gameWorld != null && gameWorld is not HideoutGameWorld &&
			(MultiplayerRuntime.Current.IsHeadless || gameWorld.MainPlayer != null);
	}

	// Shared service retirement only. World loss and plugin destruction keep
	// their distinct decal, patch, network, backend and bundle shutdown scopes.
	private static void ResetRuntimeServices()
	{
		Health.BotSeverStateService.Reset();
		ShotImpactProcessor.Reset();
		LethalHeadgearDetach.Reset();
		HeadWoundSystem.Reset();
		PulsatingBloodStreamSystem.Reset();
		FractureSystem.Reset();
		RagdollCoordinator.Reset();
		RagdollPhysicsDiagnostics.Reset();
	}

	private void OnDestroy()
	{
		if (!_initialized) return;
		HeadgearPhysicsLifetimePatch.Shutdown();
		ShadowVisiblePosePlayerLoop.Remove();
		ResetRuntimeServices();
		ExternalPhysicsBackendLoader.Shutdown();
		WoundResources.Shutdown();
		ImpactHooks.Shutdown();
		Health.BotSeverHooks.Shutdown();
		BodyDecalLodPatch.Shutdown();
		if (!MultiplayerRuntime.Current.IsHeadless) Casings.CasingHooks.Shutdown();
		if (!MultiplayerRuntime.Current.IsHeadless) BloodDecalBatch.Shutdown();
		MultiplayerRuntime.Shutdown();
	}

}
