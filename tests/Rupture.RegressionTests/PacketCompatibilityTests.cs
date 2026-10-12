using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Fika.Core.Networking.LiteNetLib.Utils;

internal static class PacketCompatibilityTests
{
    // Fixture captured from the pre-extraction Release DLL, using the same populated values.
    // Never regenerate it from the bridge to accommodate a failing compatibility assertion.
    private sealed class WireRecord
    {
        public ulong Id { get; set; }
        public string Bytes { get; set; }
    }

    private static object Populate(Type type, int seed = 1, string fieldName = "")
    {
        if (type == typeof(string)) return fieldName == "Session" ? new string('a', 32) : "wire-" + seed;
        if (type == typeof(bool)) return true;
        if (type == typeof(byte)) return (byte)seed;
        if (type == typeof(int)) return seed;
        if (type == typeof(uint)) return (uint)seed;
        if (type == typeof(ulong)) return (ulong)seed;
        if (type == typeof(float)) return seed + 0.25f;
        if (type.IsEnum) return Enum.ToObject(type, 1);
        if (type.IsArray)
        {
            int length = fieldName == "VisualConfiguration" ? 18 : 2;
            // The fixture format tracks the exact current authority configuration count.
            if (fieldName == "VisualConfiguration")
            {
                var core = System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies.Single(a => a.GetName().Name == "Rupture");
                length = (int)core.GetType("Rupture.RuptureConfiguration", true)
                    .GetField("SynchronizedCount", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).GetRawConstantValue();
            }
            var array = Array.CreateInstance(type.GetElementType(), length);
            for (int i = 0; i < length; i++) array.SetValue(Populate(type.GetElementType(), i + 1), i);
            return array;
        }
        object value = Activator.CreateInstance(type);
        int index = 0;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            field.SetValue(value, Populate(field.FieldType, ++index, field.Name));
        return value;
    }

    private static byte[] Serialize(object value)
    {
        var writer = new NetDataWriter();
        ((INetSerializable)value).Serialize(writer);
        return writer.CopyData();
    }

    private static ulong Id(Type type)
    {
        // Use the installed Fika packet processor's identifier implementation.
        return (ulong)typeof(NetPacketProcessor).GetMethod("GetHash", BindingFlags.Instance | BindingFlags.NonPublic)
            .MakeGenericMethod(type).Invoke(new NetPacketProcessor(), null);
    }

    private static IEnumerable<Type> Packets(Assembly assembly) => assembly.GetTypes()
        .Where(t => t.IsValueType && typeof(INetSerializable).IsAssignableFrom(t)).OrderBy(t => t.FullName);

    internal static void Capture(Assembly baseline, string path)
    {
        var records = Packets(baseline).ToDictionary(t => t.FullName,
            t => new WireRecord { Id = Id(t), Bytes = Convert.ToBase64String(Serialize(Populate(t))) });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        File.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Captured " + records.Count + " baseline packet fixtures.");
    }

    internal static void Run(Assembly bridge)
    {
        var records = JsonSerializer.Deserialize<Dictionary<string, WireRecord>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "FikaProtocol4.json")));
        var packets = Packets(bridge).Where(t => t.Name != "BotSeverPacket").ToArray();
        if (!packets.Select(t => t.FullName).SequenceEqual(records.Keys.OrderBy(n => n)))
            throw new Exception("Packet type inventory changed from protocol 4.");
        foreach (var type in packets)
        {
            object wire = Populate(type);
            var expected = records[type.FullName];
            if (type.Name == "RuptureHelloPacket")
            {
                if (Id(type) != expected.Id) throw new Exception("Hello packet identifier changed");
                type.GetField("Protocol").SetValue(wire, 14);
                object current = Activator.CreateInstance(type);
                var currentReader = new NetDataReader(Serialize(wire));
                ((INetSerializable)current).Deserialize(currentReader);
                if (currentReader.AvailableBytes != 0 || !Serialize(wire).SequenceEqual(Serialize(current)))
                    throw new Exception("Protocol-14 configuration hello did not roundtrip");
                // Historical configuration has a different schema and must be rejected.
                bool rejected = false;
                try { ((INetSerializable)current).Deserialize(new NetDataReader(Convert.FromBase64String(expected.Bytes))); }
                catch (FormatException) { rejected = true; }
                var compatible = bridge.GetType("Rupture.Impacts.FikaImpactNetwork", true)
                    .GetMethod("Compatible", BindingFlags.Static | BindingFlags.NonPublic);
                if (!rejected && (bool)compatible.Invoke(null, new[] { type.GetField("Protocol").GetValue(current), type.GetField("Version").GetValue(current) }))
                    throw new Exception("Historical protocol-4 configuration was accepted by the handshake");
                Console.WriteLine("PASS: unchanged hello identity, protocol-14 configuration and historical protocol rejection");
                continue;
            }
            if (Id(type) != expected.Id || Convert.ToBase64String(Serialize(wire)) != expected.Bytes)
                throw new Exception("Wire identity or bytes changed: " + type.FullName);
            object decoded = Activator.CreateInstance(type);
            var reader = new NetDataReader(Convert.FromBase64String(expected.Bytes));
            ((INetSerializable)decoded).Deserialize(reader);
            if (reader.AvailableBytes != 0 || Convert.ToBase64String(Serialize(decoded)) != expected.Bytes)
                throw new Exception("Baseline decode changed: " + type.FullName);
            var toData = type.GetMethods().SingleOrDefault(m => m.Name == "op_Implicit" && m.ReturnType != type);
            if (toData != null)
            {
                object data = toData.Invoke(null, new[] { wire });
                var toWire = type.GetMethods().Single(m => m.Name == "op_Implicit" && m.ReturnType == type);
                object converted = toWire.Invoke(null, new[] { data });
                if (Convert.ToBase64String(Serialize(converted)) != expected.Bytes)
                    throw new Exception("Core/bridge conversion lost data: " + type.FullName);
                var poses = type.GetField("Poses");
                if (poses != null && !ReferenceEquals(poses.GetValue(wire), poses.GetValue(converted)))
                    throw new Exception("Pose conversion copied the snapshot array.");
            }
            Console.WriteLine("PASS: protocol-4 identifier, baseline bytes, decode and data conversion: " + type.Name);
        }
    }
}
