using System;
using System.Linq;
using BepInEx.Configuration;
using Rupture.Impacts;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;

namespace Rupture;

public static class RuptureConfiguration
{
	public static ConfigEntry<bool> PersistentCasings { get; private set; }
	public static ConfigEntry<int> MaximumCasings { get; private set; }
	public static ConfigEntry<bool> CasingSounds { get; private set; }
	public static ConfigEntry<bool> CasingImpactScatter { get; private set; }
	public static ConfigEntry<bool> EnableDebugLogging { get; private set; }
	public static ConfigEntry<bool> ForceHeadlessConfiguration { get; private set; }
	public static ConfigEntry<bool> EnableDismemberment { get; private set; }
	public static ConfigEntry<bool> LivingBotLegSevering { get; private set; }
	public static ConfigEntry<bool> LivingLegSeveringExplosionsOnly { get; private set; }
	public static ConfigEntry<bool> LivingLegSeveringSpecialAI { get; private set; }
	public static ConfigEntry<bool> AutomaticHeadgearDetachment { get; private set; }
	public static ConfigEntry<float> DismembermentMomentumThreshold { get; private set; }
	public static ConfigEntry<float> LivingLegMomentumThreshold { get; private set; }
	public static ConfigEntry<bool> EnableBloodEffects { get; private set; }
	public static ConfigEntry<float> KillshotStreamDuration { get; private set; }
	public static ConfigEntry<float> DismembermentStreamDuration { get; private set; }
	public static ConfigEntry<int> MaxDecals { get; private set; }
	public static ConfigEntry<float> ShotIntensity { get; private set; }
	public static ConfigEntry<float> GrenadeExplIntensity { get; private set; }
	public static ConfigEntry<int> RagdollSleepTime { get; private set; }
	public static ConfigEntry<bool> LocalizedRagdollPhysics { get; private set; }
	public static ConfigEntry<bool> WakeCorpsesOnImpact { get; private set; }
	public static ConfigEntry<bool> ContinuouslySimulateShadowScene { get; private set; }
	public static ConfigEntry<bool> FreezeCorpseRagdollsForWoundDebug { get; private set; }
	public static ConfigEntry<bool> ProceduralLimbCutDebugOverlay { get; private set; }
	public static ConfigEntry<bool> ProceduralLimbClothingCapture { get; private set; }
	public static ConfigEntry<float> ProceduralLimbCutMainThreadBudget { get; private set; }
	public static ConfigEntry<bool> LogShadowSceneStatusEverySecond { get; private set; }
	public static ConfigEntry<bool> LogShadowScenePerformanceEverySecond { get; private set; }
	public static ConfigEntry<ExternalEnvironmentMode> ExternalEnvironmentPolicy { get; private set; }
	public static ConfigEntry<int> ExternalPhysXDispatcherThreads { get; private set; }

	public static void Initialize(ConfigFile config)
	{
		if (_config != null) _config.SettingChanged -= Changed;
		RestoreLocal();
		_config = config;
        PersistentCasings = config.Bind("Casings", "Persistent Casing Physics", true, new ConfigDescription("Locally simulates spent casings until capacity eviction or raid end. Graphical clients only; never synchronized or processed on headless.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
        MaximumCasings = config.Bind("Casings", "Maximum Retained Casings", 4096, new ConfigDescription("Local retained casing limit. Oldest casings are removed first. Zero disables takeover; lowering the value evicts excess immediately.", new AcceptableValueRange<int>(0, 32768), new ConfigurationManagerAttributes { IsAdvanced = true }));
        CasingSounds = config.Bind("Casings", "Casing Collision Sounds", true, new ConfigDescription("Play nearby casing contact sounds. Local graphical setting.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
        CasingImpactScatter = config.Bind("Casings", "Nearby Bullet Impact Scatter", true, new ConfigDescription("Environmental bullet impacts scatter nearby casings within 0.35 metres. Local visual physics only.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
		EnableDebugLogging = config.Bind("General", "Enable Debug Logging", false, new ConfigDescription(
			"Enables verbose runtime diagnostics in the BepInEx log and console. Initialization and rate-limited warnings/errors are always logged. This option remains local.",
			null, new ConfigurationManagerAttributes { IsAdvanced = false, Order = 100 }));
		ForceHeadlessConfiguration = config.Bind("Multiplayer", "Force Headless Configuration", false, new ConfigDescription(
			"Headless hosts use their own Rupture settings and ignore requester settings. Clients receive the headless settings instead. Only affects headless hosts; this option remains local. Changes take effect on the next raid.",
			null, new ConfigurationManagerAttributes { IsAdvanced = false }));
		EnableDismemberment = config.Bind("Dismemberment", "Dismemberment Enabled", true, new ConfigDescription(
			"Enables Rupture dismemberment.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));
		AutomaticHeadgearDetachment = config.Bind("Dismemberment", "Automatic Headgear Detachment", true, new ConfigDescription(
			"Throws equipped headgear after eligible head impacts. Disable to keep equipment on corpses while retaining head wounds and other effects. Synchronized from the Fika host.",
			null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		LivingBotLegSevering = config.Bind("Dismemberment", "Living Bot Leg Severing", true, new ConfigDescription(
			"Allows living humanoid bot legs to be severed. Bullets require a leg already at 0 HP before the hit and use separate momentum rolls. Explosions bypass blacked legs and momentum, using blast distance and actual health damage. Bots remain prone; surgery stops severed bleeding and caps the stump at 1 HP. Host synchronized. Disabling prevents new injuries and does not heal existing stumps.",
			null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
		LivingLegSeveringExplosionsOnly = config.Bind("Dismemberment", "Living Leg Severing From Explosions Only", false, new ConfigDescription(
			"Restricts new living leg severing to explosions; bullet hits neither build living momentum nor sever legs. Off by default: both bullets and explosions can sever. Explosions always bypass the blacked-leg requirement and momentum: a damaging leg hit within 0.5 m is guaranteed inside the blast radius; beyond that, cubic distance falloff to the native damage radius is multiplied by damage / leg maximum HP, capped at 1. One roll per leg per blast. Requires Living Bot Leg Severing and respects special AI protection. Host synchronized. Existing injuries and corpse rules are unchanged.",
			null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
		LivingLegSeveringSpecialAI = config.Bind("Dismemberment", "Living Leg Severing For Bosses And Special AI", false, new ConfigDescription(
			"Allows living leg severing for bosses, followers, raiders, rogues, cultists and other special AI. Off by default: only regular scavs (including sniper scavs) and AI BEAR/USEC PMCs are eligible. Human-controlled players and player scavs are always excluded. Requires Living Bot Leg Severing. Host synchronized; disabling prevents new injuries and does not restore existing missing legs.",
			null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
		LivingLegMomentumThreshold = config.Bind("Dismemberment", "Living Leg Severing Momentum Threshold (N s)", Health.BotSeverPolicy.DefaultMomentumThreshold, new ConfigDescription(
			"Accumulated projectile momentum per living bot leg at which an eligible hit is guaranteed to sever. Only shots hitting a leg already at 0 HP add buildup; healthy hits and the blackening shot add none. Below this value, chance is (accumulated momentum / threshold) squared. Defaults to 24 N s, twice the corpse default. Independent buildup and threshold from corpse dismemberment; any HP recovery, surgery/full restoration, or completed bleeding/fracture treatment clears living buildup. Synchronized from the Fika host.",
			new AcceptableValueRange<float>(0.1f, LimbImpactPolicy.MaximumThreshold),
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 0 }));
		DismembermentMomentumThreshold = config.Bind("Dismemberment", "Limb Dismemberment Momentum Threshold (N s)", LimbImpactPolicy.DefaultThreshold, new ConfigDescription(
			"Accumulated projectile momentum per arm or leg at which a sever attempt is guaranteed on death or a later corpse hit. Living bot legs have a separate threshold and buildup. Below this value, chance is (accumulated momentum / threshold) squared. Lower values make dismemberment easier. Full healing clears buildup. Does not affect explosion distance rules or ragdoll force. Changes apply to subsequent hits and sever attempts; previously capped momentum is not recovered. Synchronized from the Fika host.",
			new AcceptableValueRange<float>(0.1f, LimbImpactPolicy.MaximumThreshold),
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		EnableBloodEffects = config.Bind("Blood", "Blood Effects Enabled", true, new ConfigDescription(
			"Enables Rupture blood effects.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));
		KillshotStreamDuration = config.Bind("Blood | Trails & Flows", "Killshot Stream Duration (Seconds)", 6f, new ConfigDescription(
			"Lifetime of the continuous, heartbeat-thickened stream from projectile kill wounds.",
			new AcceptableValueRange<float>(0.25f, 30f),
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));
		DismembermentStreamDuration = config.Bind("Blood | Trails & Flows", "Dismemberment Stream Duration (Seconds)", 10f, new ConfigDescription(
			"Lifetime of the thick ballistic death-stump flow; post-mortem stump flow remains shorter and connected.",
			new AcceptableValueRange<float>(0.25f, 30f),
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));
		MaxDecals = config.Bind("Blood | Performance", "Maximum Ground Decals", 2048, new ConfigDescription(
			"Capacity of EFT's static and dynamic environment decal pools, including blood and impact marks. Applied when the next world loads. Values below one use one decal per pool.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 3 }));

		ShotIntensity = config.Bind("Ragdolls | Physical Properties", "Bullet Momentum Multiplier", 1f, new ConfigDescription(
			"Multiplies RPT's visually amplified projectile momentum applied to a dynamic corpse.",
			new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { IsAdvanced = true }));
		GrenadeExplIntensity = config.Bind("Ragdolls | Physical Properties", "Grenade Blast Multiplier", 1f, new ConfigDescription(
			"Multiplies EFT grenade strength when applying a radial corpse impulse.",
			new AcceptableValueRange<float>(0f, 10f), new ConfigurationManagerAttributes { IsAdvanced = true }));
		RagdollSleepTime = config.Bind("Ragdolls | Performance", "Ragdoll Sleep Time", 5, new ConfigDescription(
			"Maximum total time in seconds that a corpse may keep dynamic physics. Bodies can settle earlier.",
			new AcceptableValueRange<int>(CorpseSleepPolicy.MinimumDurationSeconds, CorpseSleepPolicy.MaximumDurationSeconds),
			new ConfigurationManagerAttributes { IsAdvanced = true }));
		LocalizedRagdollPhysics = config.Bind("Ragdolls | Performance", "Localized Ragdoll Physics", true, new ConfigDescription(
			"Simulates temporary corpse dynamics in standalone PhysX while retaining the native EFT corpse for hits, looting, dismemberment and cleanup. Unity's already-cooked PhysX geometry is reused; failures leave EFT native corpse physics active.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		ExternalEnvironmentPolicy = config.Bind("Ragdolls | Performance", "External Environment Mode", ExternalEnvironmentMode.ProximityCache, new ConfigDescription(
			"ProximityCache extracts Unity-cooked geometry near each ragdoll. PrebakeEntireRaid blocks at raid start and commits the complete loaded environment atomically. Changes take effect on the next raid.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
		ExternalPhysXDispatcherThreads = config.Bind("Ragdolls | Performance", "External PhysX Dispatcher Threads", 2, new ConfigDescription(
			"PhysX CPU dispatcher worker count for the standalone scene. Zero executes PhysX tasks on its dedicated scene-owner thread. Changes take effect on the next raid.",
			new AcceptableValueRange<int>(0, 4),
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 0 }));
		WakeCorpsesOnImpact = config.Bind("Ragdolls | Physical Properties", "Wake Corpses On Impact", true, new ConfigDescription(
			"Allows later bullets and explosions to reactivate an inert RPT shadow corpse for another bounded Ragdoll Sleep Time. Corpses resting in contact are woken with it. This uses native ragdoll physics, not animated active ragdolls.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
		ContinuouslySimulateShadowScene = config.Bind("Ragdolls | Debug", "Continuously Simulate Shadow Scene", false, new ConfigDescription(
			"Continuously steps the standalone PhysX scene while in raid, including with no active ragdolls. This is a profiling aid and does not prevent bodies from sleeping or settling.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 1 }));
		FreezeCorpseRagdollsForWoundDebug = config.Bind("Ragdolls | Debug", "Freeze Corpse Ragdolls For Wound Debug", false, new ConfigDescription(
			"Freezes newly created corpse ragdoll bodies at their death pose so procedural entry and exit wounds can be inspected. This affects only corpse ragdoll motion; wound generation, blood, looting, detached equipment and other physics remain active. Enable before creating the corpse.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		ProceduralLimbCutDebugOverlay = config.Bind("Dismemberment | Debug", "Procedural Limb Cut Overlay", false, new ConfigDescription(
			"Creates a skinned diagnostic overlay for procedural limb cuts and performs a one-shot EFT decal audit around each cut. The audit logs exact seam UVs, renderer/material bindings, painter queue entries, before/after GPU texture hashes, and immutable/current/prepared/baked mesh vertex layouts including UV0-UV7, color, and bone-weight summaries; its synchronous texture readback has debug-only cost. Green is strongly target-limb weighted, red is selected only through mesh connectivity, yellow is a nearby capsule candidate that was rejected, and cyan crosses a cut-band plane. Enable before making the cut; it can be hidden again live.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		ProceduralLimbClothingCapture = config.Bind("Dismemberment | Debug", "Procedural Limb Clothing Capture", false, new ConfigDescription(
			"Captures distinct successful and failed clothing cuts under BepInEx/diagnostics/Rupture/LimbCuts. Includes bind meshes, weights, selection, exposed edges and loops. Exports on a separate bounded background writer without delaying cut commit or GPU readback; up to four outstanding exports, with excess captures explicitly skipped; limited to 64 successful and 64 failed cases per raid with a 128 MiB soft disk limit. Repeated cases log a stable ID and repeat count. Does not require the overlay.",
			null, new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		ProceduralLimbCutMainThreadBudget = config.Bind("Dismemberment | Performance", "Procedural Limb Cut Main Thread Budget (ms)", 2f, new ConfigDescription(
			"Soft main-thread time budget per limb-cut update. Several stages may run within the budget, but at most one cut commits per update. Expensive native operations remain atomic and may exceed the budget.",
			new AcceptableValueRange<float>(0.5f, 8f),
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 2 }));
		LogShadowSceneStatusEverySecond = config.Bind("Ragdolls | Debug", "Log Shadow Scene Status Every Second", false, new ConfigDescription(
			"Writes backend state, ragdoll counts, environment state and correctness diagnostics to the BepInEx log once per real-time second while in raid.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = 0 }));
		LogShadowScenePerformanceEverySecond = config.Bind("Ragdolls | Debug", "Log Shadow Scene Performance Every Second", false, new ConfigDescription(
			"Collects and writes RPT main-thread, synchronization and external-step timing metrics once per real-time second while in raid. Timing collection is disabled when this toggle is off.",
			null,
			new ConfigurationManagerAttributes { IsAdvanced = true, Order = -1 }));
		// Wire order is fixed by the impact protocol. Diagnostics and machine budgets
		// are deliberately absent; visual simulation settings match the authority.
		_synced = new ConfigEntryBase[] { EnableDismemberment, EnableBloodEffects,
			KillshotStreamDuration, DismembermentStreamDuration, MaxDecals, ShotIntensity,
			GrenadeExplIntensity, RagdollSleepTime, LocalizedRagdollPhysics, WakeCorpsesOnImpact,
			DismembermentMomentumThreshold, AutomaticHeadgearDetachment, LivingBotLegSevering, LivingLegMomentumThreshold, LivingLegSeveringSpecialAI, LivingLegSeveringExplosionsOnly };
		_config.SettingChanged += Changed;
	}

	internal const int SynchronizedCount = 16;
	private static ConfigFile _config;
	private static ConfigEntryBase[] _synced = Array.Empty<ConfigEntryBase>();
	private static object[] _localValues, _hostValues;
	private static bool _applying;
	internal static string[] CaptureVisuals() => _synced.Select(entry => entry.GetSerializedValue()).ToArray();

	internal static bool ApplyHostVisuals(string[] values)
	{
		if (values == null || values.Length != SynchronizedCount || _synced.Length != SynchronizedCount) return false;
		var parsed = new object[values.Length];
		try
		{
			for (int i = 0; i < values.Length; i++)
			{
				if (values[i] == null || values[i].Length > 64) return false;
				parsed[i] = TomlTypeConverter.ConvertToValue(values[i], _synced[i].SettingType);
				if (parsed[i] == null || !_synced[i].SettingType.IsInstanceOfType(parsed[i])) return false;
				if (parsed[i] is float number && (float.IsNaN(number) || float.IsInfinity(number))) return false;
				if (_synced[i].Description.AcceptableValues?.IsValid(parsed[i]) == false) return false;
			}
		}
		catch { return false; }
		// Parse the whole snapshot before changing any live settings.
		_localValues ??= _synced.Select(entry => entry.BoxedValue).ToArray();
		_hostValues = parsed;
		Assign(parsed);
		return true;
	}

	internal static void RestoreLocal()
	{
		if (_localValues != null) Assign(_localValues);
		_localValues = null; _hostValues = null;
	}

	private static void Assign(object[] values)
	{
		bool previousApplying = _applying, previousSave = _config.SaveOnConfigSet;
		_applying = true; _config.SaveOnConfigSet = false;
		try { for (int i = 0; i < values.Length; i++) _synced[i].BoxedValue = values[i]; }
		finally { _config.SaveOnConfigSet = previousSave; _applying = previousApplying; }
	}

	private static void Changed(object sender, SettingChangedEventArgs args)
	{
		if (_applying || !_synced.Contains(args.ChangedSetting)) return;
		if (_hostValues != null) Assign(_hostValues); // Client UI cannot override the authority.
		else ImpactNetwork.ConfigurationChanged();
	}

	// ConfigFile.Save also runs when a local debug option changes. Serialize the
	// client's own visual preferences while keeping host overrides in memory.
	internal static bool BeforeSave(ConfigFile config)
	{
		if (!ReferenceEquals(config, _config) || _localValues == null) return false;
		Assign(_localValues);
		return true;
	}
	internal static void AfterSave(bool restore)
	{
		if (restore && _hostValues != null) Assign(_hostValues);
	}
}
