using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using Mono.Cecil;
using System.Reflection.Emit;

internal static class HeadlessConfigurationTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }

    internal static void Run(Assembly core, Assembly bridge)
    {
        Type configuration = core.GetType("Rupture.RuptureConfiguration", true);
        Type network = bridge.GetType("Rupture.Impacts.FikaImpactNetwork", true);
        Type hello = bridge.GetType("Rupture.Impacts.RuptureHelloPacket", true);
        object Call(string method, params object[] args) => network.GetMethod(method, Flags).Invoke(null, args);
        string[] Capture() => (string[])configuration.GetMethod("CaptureVisuals", Flags).Invoke(null, null);
        void Reset() => Call("Reset");
        object Packet(string session, string[] values, bool requester = true, uint revision = 0)
        {
            object packet = Activator.CreateInstance(hello);
            hello.GetField("Protocol").SetValue(packet, 14);
            hello.GetField("Version").SetValue(packet, "1.2.1");
            hello.GetField("Session").SetValue(packet, session);
            hello.GetField("IsHeadlessRequester").SetValue(packet, requester);
            hello.GetField("VisualConfiguration").SetValue(packet, values);
            hello.GetField("ConfigurationRevision").SetValue(packet, revision);
            return packet;
        }
        object Roundtrip(object packet)
        {
            var writer = new NetDataWriter(); ((INetSerializable)packet).Serialize(writer);
            object decoded = Activator.CreateInstance(hello);
            var reader = new NetDataReader(writer.CopyData()); ((INetSerializable)decoded).Deserialize(reader);
            Check(reader.AvailableBytes == 0, "headless hello consumes its exact wire payload");
            return decoded;
        }
        bool Adopt(object packet, bool headless = true, bool started = false) =>
            (bool)Call("ApplyRequesterConfiguration", packet, headless, started);
        bool Ready() => (bool)network.GetProperty("ConfigurationReadyForStart", Flags).GetValue(null);

        string path = Path.Combine(Path.GetTempPath(), "rupture-headless-" + Guid.NewGuid().ToString("N") + ".cfg");
        var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
        var runtimeProperty = core.GetType("Rupture.MultiplayerRuntime", true).GetProperty("Current", Flags);
        object previousRuntime = runtimeProperty.GetValue(null);
        bool previousHeadless = FikaBackendUtils.IsHeadless;
        bool previousHeadlessGame = FikaBackendUtils.IsHeadlessGame;
        bool previousRequester = FikaBackendUtils.IsHeadlessRequester;
        var reconnectProperty = typeof(FikaBackendUtils).GetProperty(nameof(FikaBackendUtils.IsReconnect));
        object previousReconnect = reconnectProperty.GetValue(null);
        var clientTypeProperty = typeof(FikaBackendUtils).GetProperty(nameof(FikaBackendUtils.ClientType));
        object previousClientType = clientTypeProperty.GetValue(null);
        try
        {
            configuration.GetMethod("Initialize", Flags).Invoke(null, new object[] { config });
            Reset();
            string[] original = Capture();
            string[] requested = (string[])original.Clone(); requested[5] = "3"; requested[10] = "24"; requested[13] = "48"; requested[14] = "true"; requested[15] = "true";
            ConfigEntryBase Entry(string name) => (ConfigEntryBase)configuration.GetProperty(name, Flags).GetValue(null);
            runtimeProperty.SetValue(null, Activator.CreateInstance(bridge.GetType("Rupture.Fika.FikaRuntime", true), true));
            FikaBackendUtils.IsHeadless = false;
            FikaBackendUtils.IsHeadlessGame = true;
            FikaBackendUtils.IsHeadlessRequester = true;
            reconnectProperty.SetValue(null, false);
            clientTypeProperty.SetValue(null, Enum.Parse(clientTypeProperty.PropertyType, "Client"));
            network.GetField("_session", Flags).SetValue(null, new string('a', 32));
            Entry("ShotIntensity").BoxedValue = 3f;
            Entry("DismembermentMomentumThreshold").BoxedValue = 24f;
            Entry("LivingLegMomentumThreshold").BoxedValue = 48f;
            Entry("LivingLegSeveringSpecialAI").BoxedValue = true;
            Entry("LivingLegSeveringExplosionsOnly").BoxedValue = true;
            object requester = Roundtrip(Call("CreateHello"));
            Check((bool)hello.GetField("IsHeadlessRequester").GetValue(requester) &&
                (uint)hello.GetField("ConfigurationRevision").GetValue(requester) == 0 &&
                ((string[])hello.GetField("VisualConfiguration").GetValue(requester)).SequenceEqual(requested),
                "requester declaration and complete settings snapshot survive transport");
            reconnectProperty.SetValue(null, true);
            object reconnect = Call("CreateHello");
            Check(!(bool)hello.GetField("IsHeadlessRequester").GetValue(reconnect) && hello.GetField("VisualConfiguration").GetValue(reconnect) == null,
                "requester reconnect sends no replacement snapshot");
            reconnectProperty.SetValue(null, false);
            FikaBackendUtils.IsHeadlessRequester = false;
            object ordinary = Call("CreateHello");
            Check(!(bool)hello.GetField("IsHeadlessRequester").GetValue(ordinary) && hello.GetField("VisualConfiguration").GetValue(ordinary) == null,
                "ordinary joiners do not declare ownership or submit local settings");
            Entry("ShotIntensity").BoxedValue = 1f;
            Entry("DismembermentMomentumThreshold").BoxedValue = 12f;
            Entry("LivingLegMomentumThreshold").BoxedValue = 24f;
            Entry("LivingLegSeveringSpecialAI").BoxedValue = false;
            Entry("LivingLegSeveringExplosionsOnly").BoxedValue = false;
            Check(!Ready() && !Adopt(requester, headless: false) && !Adopt(requester, started: true) &&
                !Adopt(Packet(new string('a', 32), requested, requester: false)) &&
                !Adopt(Packet(new string('a', 32), requested, revision: 1)) && Capture().SequenceEqual(original),
                "graphical hosts, running raids and undeclared/authority-shaped submissions cannot adopt requester settings");
            string[] invalid = (string[])requested.Clone(); invalid[13] = "NaN";
            Check(!Adopt(Packet(new string('a', 32), invalid)) && !Adopt(Packet(new string('a', 32), new string[13])) &&
                !Adopt(Packet("bad-session", requested)) && Capture().SequenceEqual(original),
                "invalid requester snapshots preserve every headless default and leave adoption available");

            var manager = HeadlessRecordingManager.Create();
            HeadlessRecordingManager.Sent.Clear();
            network.GetField("_manager", Flags).SetValue(null, manager);
            network.GetField("_session", Flags).SetValue(null, new string('h', 32));
            var peers = (IDictionary)network.GetField("Peers", Flags).GetValue(null);
            NetPeer Peer(int id, string session)
            {
                var peer = (NetPeer)RuntimeHelpers.GetUninitializedObject(typeof(NetPeer));
                typeof(NetPeer).GetField("Id", Flags).SetValue(peer, id);
                typeof(NetPeer).BaseType.GetField("_connectionState", Flags).SetValue(peer, ConnectionState.Connected);
                Type stateType = network.GetNestedType("PeerState", Flags);
                object state = Activator.CreateInstance(stateType, true);
                stateType.GetField("Peer", Flags).SetValue(state, peer);
                stateType.GetField("Session", Flags).SetValue(state, session);
                peers.Add(id, state);
                return peer;
            }
            NetPeer first = Peer(1, new string('a', 32));
            Peer(2, new string('b', 32));
            Peer(3, null); // No configuration may be sent to an incompatible/unconfirmed peer.
            Check(!(bool)Call("FilterRaidStart", true, first) && !(bool)Call("TakePendingRaidStart"),
                "early start request waits for the requester snapshot");
            Check(Adopt(requester) && Capture().SequenceEqual(requested) && !Ready() && !(bool)Call("TakePendingRaidStart"),
                "headless adopts requester settings atomically but holds start until broadcast");

            bool restore = (bool)configuration.GetMethod("BeforeSave", Flags).Invoke(null, new object[] { config });
            try { config.Save(); } finally { configuration.GetMethod("AfterSave", Flags).Invoke(null, new object[] { restore }); }
            var saved = new ConfigFile(path, true) { SaveOnConfigSet = false };
            Check(saved.Bind("Ragdolls | Physical Properties", "Bullet Momentum Multiplier", 0f).Value == 1f && Capture().SequenceEqual(requested),
                "headless saves its own defaults while retaining requester settings in memory");

            FikaBackendUtils.IsHeadless = true;
            runtimeProperty.SetValue(null, Activator.CreateInstance(bridge.GetType("Rupture.Fika.FikaRuntime", true), true));
            Call("BroadcastConfiguration");
            Check(HeadlessRecordingManager.Sent.Count == 2 && HeadlessRecordingManager.Sent.Select(s => s.Peer.Id).OrderBy(id => id).SequenceEqual(new[] { 1, 2 }) &&
                HeadlessRecordingManager.Sent.All(s => s.Delivery == DeliveryMethod.ReliableOrdered &&
                    !(bool)hello.GetField("IsHeadlessRequester").GetValue(s.Packet) &&
                    (uint)hello.GetField("ConfigurationRevision").GetValue(s.Packet) > 0 &&
                    ((string[])hello.GetField("VisualConfiguration").GetValue(Roundtrip(s.Packet))).SequenceEqual(requested)),
                "headless relays adopted settings as authority to requester and other compatible clients");
            Check(Ready() && (bool)Call("TakePendingRaidStart") && !(bool)Call("TakePendingRaidStart"),
                "broadcast releases exactly one queued start request");
            Check(!(bool)Call("FilterRaidStart", true, (NetPeer)peers[3].GetType().GetField("Peer", Flags).GetValue(peers[3])) &&
                !(bool)Call("TakePendingRaidStart"), "unconfirmed peers cannot start a raid even after settings adoption");
            network.GetField("_pendingRaidStartPeer", Flags).SetValue(null, null);
            Call("BroadcastConfiguration");
            Check(HeadlessRecordingManager.Sent.Count == 2, "unchanged requester settings are not broadcast every frame");

            string[] other = (string[])requested.Clone(); other[5] = "7";
            Check(Adopt(Packet(new string('b', 32), other), started: true) &&
                Adopt(Packet(new string('a', 32), other)) && Capture().SequenceEqual(requested),
                "competing declarations and repeated submissions cannot replace the first snapshot");
            peers.Remove(1);
            Check(Capture().SequenceEqual(requested) &&
                ((string[])hello.GetField("VisualConfiguration").GetValue(Call("CreateHello"))).SequenceEqual(requested),
                "requester disconnect preserves adopted settings for later joiners");
            Reset();
            Check(!Ready() && Capture().SequenceEqual(original), "manager destruction restores headless defaults and clears start/adoption state");
            Check(Adopt(Packet(new string('c', 32), other)) && Capture().SequenceEqual(other),
                "a subsequent raid can adopt a different requester's settings");

            Reset();
            Entry("ForceHeadlessConfiguration").BoxedValue = true;
            Call("SelectConfigurationSource", false);
            Check(!Ready() && !Adopt(requester, headless: false), "force-headless policy is local and does not apply to graphical hosts");
            Call("SelectConfigurationSource", true);
            network.GetField("_manager", Flags).SetValue(null, manager);
            network.GetField("_session", Flags).SetValue(null, new string('h', 32));
            HeadlessRecordingManager.Sent.Clear();
            NetPeer forcedRequester = Peer(4, new string('a', 32));
            Peer(5, new string('b', 32));
            Check(Adopt(requester) && Adopt(Packet(new string('b', 32), invalid, requester: false, revision: 99), started: true) &&
                Capture().SequenceEqual(original) && network.GetField("_requesterSession", Flags).GetValue(null) == null,
                "forced headless configuration ignores client snapshots without adopting or rejecting them");
            Check(!(bool)Call("FilterRaidStart", true, forcedRequester) && !Ready(),
                "forced headless start waits for its own settings broadcast rather than requester adoption");
            Call("BroadcastConfiguration");
            Check(Ready() && (bool)Call("TakePendingRaidStart") && HeadlessRecordingManager.Sent.Count == 2 &&
                HeadlessRecordingManager.Sent.All(s => ((string[])hello.GetField("VisualConfiguration").GetValue(s.Packet)).SequenceEqual(original)),
                "forced headless broadcasts its own settings to requester and other clients and releases start");
            config.Save();
            saved = new ConfigFile(path, true) { SaveOnConfigSet = false };
            Check(saved.Bind("Multiplayer", "Force Headless Configuration", false).Value &&
                saved.Bind("Ragdolls | Physical Properties", "Bullet Momentum Multiplier", 0f).Value == 1f,
                "forced headless policy and own settings persist independently of incoming snapshots");
            Entry("ForceHeadlessConfiguration").BoxedValue = false;
            Check(Adopt(requester) && Capture().SequenceEqual(original), "configuration-source changes wait for the next raid");
            Reset();
            Call("SelectConfigurationSource", true);
            Check(!Ready() && Adopt(requester) && Capture().SequenceEqual(requested),
                "disabling the force toggle restores requester adoption for the subsequent raid");

            using var binary = AssemblyDefinition.ReadAssembly(bridge.Location);
            var compiled = binary.MainModule.GetType(network.FullName);
            string[] Calls(string method) => compiled.Methods.Single(m => m.Name == method).Body.Instructions
                .Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
            var update = Calls("Update");
            Check(Calls("Hello").Contains("ApplyRequesterConfiguration") &&
                Calls("Register").Contains("SelectConfigurationSource") &&
                Calls("BeforeInformation").Contains("FilterRaidStart") &&
                Array.IndexOf(update, "BroadcastConfiguration") < Array.IndexOf(update, "TakePendingRaidStart"),
                "production handshake and start hook enforce adoption and broadcast before starting gameplay");
            var information = typeof(FikaServer).GetMethod("OnInformationPacketReceived", Flags);
            Check(information != null && information.GetParameters()[0].Name == "packet" &&
                information.GetParameters()[0].ParameterType == typeof(Fika.Core.Networking.Packets.Backend.InformationPacket) &&
                information.GetParameters()[1].Name == "peer" && information.GetParameters()[1].ParameterType == typeof(NetPeer),
                "pinned Fika raid-start callback matches the Harmony prefix ABI");
        }
        finally
        {
            Reset();
            ((ConfigEntryBase)configuration.GetProperty("ForceHeadlessConfiguration", Flags).GetValue(null)).BoxedValue = false;
            runtimeProperty.SetValue(null, previousRuntime);
            FikaBackendUtils.IsHeadless = previousHeadless;
            FikaBackendUtils.IsHeadlessGame = previousHeadlessGame;
            FikaBackendUtils.IsHeadlessRequester = previousRequester;
            reconnectProperty.SetValue(null, previousReconnect);
            clientTypeProperty.SetValue(null, previousClientType);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public static class HeadlessRecordingManager
{
    public static readonly List<(object Packet, DeliveryMethod Delivery, NetPeer Peer)> Sent = new();
    public static void Record(object packet, DeliveryMethod deliveryMethod, NetPeer peer)
    {
        Sent.Add((packet, deliveryMethod, peer));
    }

    internal static IFikaNetworkManager Create()
    {
        // Fika exposes an internal abstract interface member. Emit a recorder
        // with access to that assembly so every interface slot can be implemented.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Rupture.HeadlessRecorder"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute)
            .GetConstructor(new[] { typeof(string) }), new object[] { "Fika.Core" }));
        var type = assembly.DefineDynamicModule("Recorder").DefineType("Recorder", System.Reflection.TypeAttributes.Public);
        type.AddInterfaceImplementation(typeof(IFikaNetworkManager));
        foreach (MethodInfo original in typeof(IFikaNetworkManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var method = type.DefineMethod(original.Name, System.Reflection.MethodAttributes.Public |
                System.Reflection.MethodAttributes.Virtual | System.Reflection.MethodAttributes.Final | System.Reflection.MethodAttributes.NewSlot);
            Type[] source = original.GetGenericArguments();
            Type[] target = source.Length == 0 ? Type.EmptyTypes : method.DefineGenericParameters(source.Select(t => t.Name).ToArray());
            Type Map(Type value)
            {
                if (value.IsGenericParameter) return target[value.GenericParameterPosition];
                if (value.IsByRef) return Map(value.GetElementType()).MakeByRefType();
                if (value.IsArray) return Map(value.GetElementType()).MakeArrayType();
                return value.IsGenericType ? value.GetGenericTypeDefinition().MakeGenericType(value.GetGenericArguments().Select(Map).ToArray()) : value;
            }
            for (int i = 0; i < source.Length; i++)
            {
                var parameter = (GenericTypeParameterBuilder)target[i];
                parameter.SetGenericParameterAttributes(source[i].GenericParameterAttributes);
                var constraints = source[i].GetGenericParameterConstraints().Select(Map).ToArray();
                parameter.SetInterfaceConstraints(constraints.Where(t => t.IsInterface).ToArray());
                var baseType = constraints.FirstOrDefault(t => !t.IsInterface);
                if (baseType != null) parameter.SetBaseTypeConstraint(baseType);
            }
            var parameters = original.GetParameters();
            method.SetSignature(Map(original.ReturnType), original.ReturnParameter.GetRequiredCustomModifiers(), original.ReturnParameter.GetOptionalCustomModifiers(),
                parameters.Select(p => Map(p.ParameterType)).ToArray(), parameters.Select(p => p.GetRequiredCustomModifiers()).ToArray(),
                parameters.Select(p => p.GetOptionalCustomModifiers()).ToArray());
            var il = method.GetILGenerator();
            if (original.Name == "SendDataToPeer")
            {
                il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldobj, target[0]); il.Emit(OpCodes.Box, target[0]);
                il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, typeof(HeadlessRecordingManager).GetMethod(nameof(Record)));
                il.Emit(OpCodes.Ret);
            }
            else
            {
                il.Emit(OpCodes.Ldstr, "Unexpected network recorder call: " + original.Name);
                il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }));
                il.Emit(OpCodes.Throw);
            }
            type.DefineMethodOverride(method, original);
        }
        return (IFikaNetworkManager)Activator.CreateInstance(type.CreateType());
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;
        public string AssemblyName { get; }
    }
}
