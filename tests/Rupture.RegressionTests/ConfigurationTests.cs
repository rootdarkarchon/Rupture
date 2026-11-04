using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fika.Core.Networking.LiteNetLib.Utils;
using Mono.Cecil;

internal static class ConfigurationTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }

    internal static void Run(Assembly assembly)
    {
        string path = Path.Combine(Path.GetTempPath(), "rupture-config-" + Guid.NewGuid().ToString("N") + ".cfg");
        File.WriteAllText(path, "[Blood | Trails & Flows]\nArterial Spraying = false\n");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
        Type type = assembly.GetType("Rupture.RuptureConfiguration", true);
        object Call(string name, params object[] args) => type.GetMethod(name, Flags).Invoke(null, args);
        ConfigEntryBase Entry(string name) => (ConfigEntryBase)type.GetProperty(name, Flags).GetValue(null);
        bool Apply(string[] values) => (bool)Call("ApplyHostVisuals", (object)values);
        string[] Capture() => (string[])Call("CaptureVisuals");
        Call("Initialize", config);
        Check(type.GetProperty("ArterySpray", Flags) == null && config.Count == type.GetProperties(BindingFlags.Public | BindingFlags.Static).Length,
            "no arterial, drip or soaking configuration toggle is bound");
        string[] original = Capture();
        Check(original.Length == 19, "protocol-15 synchronizes death reaction settings");
        Check(new[] { "CorrectedLethalImpulse", "InheritRootVelocity", "AngularResistance" }.All(name => (bool)Entry(name).BoxedValue && (bool)Entry(name).DefaultValue),
            "corrected lethal 1x, movement inheritance and brief angular resistance are production defaults");
        Call("SetDeathReactionPreset", false);
        Check(Capture().Take(16).SequenceEqual(original.Take(16)) && Capture()[16] == "false" && Capture()[17] == "true" && Capture()[18] == "false",
            "old preset keeps movement inheritance with amplified lethal impulses and passive joints, preserving unrelated settings");
        Entry("InheritRootVelocity").BoxedValue = false;
        Call("SetDeathReactionPreset", false);
        Check((bool)Entry("InheritRootVelocity").BoxedValue, "old preset restores shared movement inheritance even after a custom override");
        Entry("InheritRootVelocity").BoxedValue = false;
        Call("SetDeathReactionPreset", true);
        Check(Capture().SequenceEqual(original), "new preset restores reduced lethal impulse, inherited movement and angular resistance without changing other preferences");
        var presetTags = Entry("CorrectedLethalImpulse").Description.Tags.Single(t => t.GetType().Name == "ConfigurationManagerAttributes");
        Check(presetTags.GetType().GetField("CustomDrawer").GetValue(presetTags) is Action<ConfigEntryBase> &&
            (bool)presetTags.GetType().GetField("HideDefaultButton").GetValue(presetTags), "visible preset row exposes the installed ConfigurationManager custom IMGUI drawer ABI");
        Check((float)Entry("LivingLegMomentumThreshold").BoxedValue == 24f && (float)Entry("DismembermentMomentumThreshold").BoxedValue == 12f,
            "living-leg threshold defaults to twice the separate corpse threshold");
        Check((bool)Entry("LivingBotLegSevering").BoxedValue, "living bot severing defaults enabled");
        Check(!(bool)Entry("LivingLegSeveringSpecialAI").BoxedValue, "bosses and other special AI are protected by default");
        Check(!(bool)Entry("LivingLegSeveringExplosionsOnly").BoxedValue, "explosions-only living severing defaults off");
        Check((bool)Entry("AutomaticHeadgearDetachment").BoxedValue, "automatic headgear detachment preserves the enabled default");
        Check((float)Entry("DismembermentMomentumThreshold").BoxedValue == 12f, "dismemberment threshold preserves the 12 N.s default");
        Check((bool)Entry("EnableBloodEffects").BoxedValue, "legacy arterial=false does not disable the blood master switch");
        try
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                var entry = (ConfigEntryBase)property.GetValue(null);
                var tags = entry.Description.Tags.Single(t => t.GetType().Name == "ConfigurationManagerAttributes");
                bool advanced = (bool)tags.GetType().GetField("IsAdvanced").GetValue(tags);
                Check(advanced == (property.Name != "EnableDebugLogging" && property.Name != "ForceHeadlessConfiguration" && property.Name != "CorrectedLethalImpulse"), "configuration visibility: " + property.Name);
            }
            Check(!(bool)Entry("EnableDebugLogging").BoxedValue, "debug logging is off by default");
			Check(!(bool)Entry("WeaponColliderWireframe").BoxedValue, "weapon collider wireframes are off by default");
            Check(!(bool)Entry("ForceHeadlessConfiguration").BoxedValue, "headless requester settings remain the default policy");
            var source = new ManualLogSource("Rupture regression");
            int messages = 0; source.LogEvent += (_, _) => messages++;
            Type logType = assembly.GetType("Rupture.RuptureLog", true);
            object log = Activator.CreateInstance(logType, Flags, null, new object[] { source }, null);
            foreach (string method in new[] { "LogInfo", "LogWarning", "LogError", "LogDebug" })
                logType.GetMethod(method, Flags).Invoke(log, new object[] { "runtime" });
            Check(messages == 2, "warnings and errors remain visible while verbose logs are disabled");
            logType.GetMethod("Initialization", Flags).Invoke(log, new object[] { "startup" });
            Check(messages == 3, "basic initialization remains visible");
            Entry("EnableDebugLogging").BoxedValue = true;
            logType.GetMethod("LogInfo", Flags).Invoke(log, new object[] { "runtime" });
            Check(messages == 4, "debug toggle enables runtime diagnostics immediately");
            Entry("EnableDebugLogging").BoxedValue = false;
            for (int index = 0; index < 1000; index++)
                logType.GetMethod("LogWarning", Flags).Invoke(log, new object[] { "repeated failure " + index });
            Check(messages == 63, "default diagnostics bound a burst to 60 failures and one suppression notice");
            Entry("EnableDebugLogging").BoxedValue = true;

            Entry("FreezeCorpseRagdollsForWoundDebug").BoxedValue = true;
			Entry("WeaponColliderWireframe").BoxedValue = true;
            Entry("ExternalPhysXDispatcherThreads").BoxedValue = 4;
            string[] host = (string[])original.Clone(); host[0] = "false"; host[2] = "2.5"; host[5] = "3";
            host[10] = "24";
            host[11] = "false";
            host[13] = "48";
            host[14] = "true";
            host[15] = "true";
            host[16] = host[17] = host[18] = "false";
            Check(Apply(host) && Capture().SequenceEqual(host), "authority visual snapshot applies completely");
            Call("SetDeathReactionPreset", true);
            Entry("AngularResistance").BoxedValue = true;
            Check(Capture().SequenceEqual(host), "client preset and individual death settings cannot override the host");
            Check((float)Entry("LivingLegMomentumThreshold").BoxedValue == 48f,
                "authority synchronizes the independent living-leg threshold");
            Check((bool)Entry("LivingLegSeveringSpecialAI").BoxedValue, "authority synchronizes special AI eligibility");
            Entry("LivingLegSeveringSpecialAI").BoxedValue = false;
            Check((bool)Entry("LivingLegSeveringSpecialAI").BoxedValue, "client cannot override host special AI eligibility");
            Entry("LivingLegSeveringExplosionsOnly").BoxedValue = false;
            Check((bool)Entry("LivingLegSeveringExplosionsOnly").BoxedValue, "client cannot override host explosions-only mode");
            Entry("AutomaticHeadgearDetachment").BoxedValue = true;
            Check(!(bool)Entry("AutomaticHeadgearDetachment").BoxedValue, "client cannot override host headgear detachment");
            Check((bool)Entry("EnableDebugLogging").BoxedValue && (bool)Entry("FreezeCorpseRagdollsForWoundDebug").BoxedValue &&
                (int)Entry("ExternalPhysXDispatcherThreads").BoxedValue == 4 && (bool)Entry("WeaponColliderWireframe").BoxedValue,
				"debug options and machine performance settings remain local");
            Entry("ShotIntensity").BoxedValue = 7f;
            Check((float)Entry("ShotIntensity").BoxedValue == 3f, "client changes cannot override active authority visuals");
            Entry("DismembermentMomentumThreshold").BoxedValue = 6f;
            Check((float)Entry("DismembermentMomentumThreshold").BoxedValue == 24f, "client cannot override the authority dismemberment threshold");
            foreach (string invalid in new[] { "0", "-1", "121", "NaN", "Infinity" })
            {
                string[] invalidThreshold = (string[])host.Clone(); invalidThreshold[10] = invalid;
                Check(!Apply(invalidThreshold) && Capture().SequenceEqual(host), "invalid authority threshold rejected transactionally: " + invalid);
            }
            string[] bad = (string[])host.Clone(); bad[5] = "NaN";
            Check(!Apply(bad) && Capture().SequenceEqual(host), "nonfinite host snapshot rejected without partial application");
            bad[5] = "11";
            Check(!Apply(bad) && !Apply(new string[11]) && Capture().SequenceEqual(host), "range and schema mismatches reject transactionally");
            bad = (string[])host.Clone(); bad[0] = "invalid";
            Check(!Apply(bad) && Capture().SequenceEqual(host), "malformed boolean snapshot cannot change active visuals");
            bad = (string[])host.Clone(); bad[14] = "invalid";
            Check(!Apply(bad) && Capture().SequenceEqual(host), "malformed special AI eligibility rejects the entire authority snapshot");
            bad = (string[])host.Clone(); bad[15] = "invalid";
            Check(!Apply(bad) && Capture().SequenceEqual(host), "malformed explosions-only mode rejects the entire authority snapshot");
            foreach (int index in new[] { 16, 17, 18 })
            {
                bad = (string[])host.Clone(); bad[index] = "invalid";
                Check(!Apply(bad) && Capture().SequenceEqual(host), "malformed death setting rejects atomically: " + index);
            }

            bool restore = (bool)Call("BeforeSave", config);
            try { config.Save(); } finally { Call("AfterSave", restore); }
            var saved = new ConfigFile(path, true) { SaveOnConfigSet = false };
            Check(saved.Bind("Ragdolls | Physical Properties", "Bullet Momentum Multiplier", 0f).Value == 1f &&
                saved.Bind("Dismemberment", "Limb Dismemberment Momentum Threshold (N s)", 0f).Value == 12f &&
                !saved.Bind("Dismemberment", "Living Leg Severing For Bosses And Special AI", false).Value &&
                !saved.Bind("Dismemberment", "Living Leg Severing From Explosions Only", false).Value &&
                saved.Bind("General", "Enable Debug Logging", false).Value,
                "client saves retain local visual preferences and local debug choices");
            Check(Capture().SequenceEqual(host), "saving config restores host visuals in memory");
            Entry("MaximumCasings").BoxedValue = 1234;
            Entry("PersistentCasings").BoxedValue = false;
            Check(Apply(host) && (int)Entry("MaximumCasings").BoxedValue == 1234 && !(bool)Entry("PersistentCasings").BoxedValue,
                "host visual settings do not override local casing configuration");
            Entry("MaximumCasings").BoxedValue = 4096;
            Entry("PersistentCasings").BoxedValue = true;
            host[2] = "4";
            Check(Apply(host) && (float)Entry("KillshotStreamDuration").BoxedValue == 4f, "later authority changes replace the visual snapshot");
            Call("RestoreLocal");
            Check(Capture().SequenceEqual(original) && (bool)Entry("EnableDebugLogging").BoxedValue,
                "disconnect restores original visual preferences and retains local debug choices");

            Type helloType = Program.Bridge.GetType("Rupture.Impacts.RuptureHelloPacket", true);
            object hello = Activator.CreateInstance(helloType);
            void Field(string name, object value) => helloType.GetField(name).SetValue(hello, value);
            Field("Protocol", 15); Field("Version", assembly.GetName().Version.ToString(3)); Field("Session", new string('a', 32));
            Field("ConfigurationRevision", 2u); Field("VisualConfiguration", host);
            var writer = new NetDataWriter(); ((INetSerializable)hello).Serialize(writer);
            object received = Activator.CreateInstance(helloType);
            var reader = new NetDataReader(writer.CopyData()); ((INetSerializable)received).Deserialize(reader);
            Check(reader.AvailableBytes == 0 && ((string[])helloType.GetField("VisualConfiguration").GetValue(received)).SequenceEqual(host) &&
                (uint)helloType.GetField("ConfigurationRevision").GetValue(received) == 2u,
                "versioned authority hello roundtrips its bounded visual snapshot");
            writer.Reset(); writer.Put(7); writer.Put("1.1.6"); writer.Put(new string('a', 32)); writer.Put(1u); writer.Put((byte)255);
            bool rejected = false;
            try { ((INetSerializable)received).Deserialize(new NetDataReader(writer.CopyData())); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "oversized configuration counts are rejected before array allocation");

            using var module = AssemblyDefinition.ReadAssembly(assembly.Location);
            using var bridge = AssemblyDefinition.ReadAssembly(Program.Bridge.Location);
            var network = bridge.MainModule.GetType("Rupture.Impacts.FikaImpactNetwork");
            string[] Calls(string method) => network.Methods.Single(m => m.Name == method).Body.Instructions
                .Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
            Check(Calls("Hello").Contains("ApplyHostVisuals") && Calls("Hello").Contains("get_IsNetworkAuthority") &&
                Calls("Update").Contains("BroadcastConfiguration") && Calls("BroadcastConfiguration").Contains("SendDataToPeer") && Calls("Reset").Contains("RestoreLocal"),
                "compiled handshake, host broadcasts and disconnect reset use the visual configuration path");
            var savePatch = module.MainModule.GetType("Rupture.ConfigurationSavePatch");
            Check(savePatch.Methods.Single(m => m.Name == "Finalizer").CustomAttributes.Any(a => a.AttributeType.Name == "PatchFinalizerAttribute"),
                "configuration save restores remote values even if disk saving throws");
        }
        finally
        {
            Call("RestoreLocal");
            Entry("EnableDebugLogging").BoxedValue = false;
            Entry("FreezeCorpseRagdollsForWoundDebug").BoxedValue = false;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
