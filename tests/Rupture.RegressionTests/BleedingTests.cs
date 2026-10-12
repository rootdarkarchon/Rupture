using System;
using System.Linq;
using System.Reflection;
using Fika.Core.Networking.LiteNetLib.Utils;
using Mono.Cecil;

internal static class BleedingTests
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS: " + label); }
    internal static void Run(Assembly assembly, Assembly bridge)
    {
        var evaluate = assembly.GetType("Rupture.Dismemberment.Classes.BloodStreams.BleedEmissionPolicy", true).GetMethod("Evaluate", Flags);
        string Decision(object active, object supplied, byte kind, byte sent, bool ack = false, bool retry = true) =>
            evaluate.Invoke(null, new object[] { active, supplied, kind, sent, ack, retry }).ToString();
        object light = new object(), heavy = new object();
        Check(Decision(null, null, 6, 0) == "None", "delayed/nonbleeding states emit nothing");
        Check(Decision(light, null, 6, 0) == "Start", "new active light bleed starts once");
        Check(Decision(light, light, 6, 6, true) == "None", "repeated hits and acknowledged pool eviction retain the wound");
        Check(Decision(null, light, 6, 6) == "Stop", "treatment, pause, expiry, death and disabling stop supply");
        Check(Decision(null, null, 6, 0) == "None", "repeated stops do not republish");
        Check(Decision(light, null, 6, 0) == "Start", "resumption or reopening restarts supply");
        Check(Decision(heavy, light, 1, 6) == "Start", "severity replacement starts heavy supply");
        Check(Decision(light, light, 6, 6, false, false) == "None" && Decision(light, light, 6, 6, false, true) == "Start",
            "unacknowledged start retries after its bounded delay");

        Type packet = bridge.GetType("Rupture.Dismemberment.Classes.Packets.BloodStreamPacket", true);
        object value = Activator.CreateInstance(packet); packet.GetField("TriggerKind").SetValue(value, Enum.ToObject(packet.GetField("TriggerKind").FieldType, 6));
        packet.GetField("StreamId").SetValue(value, 77u); packet.GetField("PeakSpeed").SetValue(value, .06f); packet.GetField("Radius").SetValue(value, .0022f);
        var writer = new NetDataWriter(); ((INetSerializable)value).Serialize(writer); object decoded = Activator.CreateInstance(packet);
        var reader = new NetDataReader(writer.CopyData()); ((INetSerializable)decoded).Deserialize(reader);
        Check(reader.AvailableBytes == 0 && Convert.ToByte(packet.GetField("TriggerKind").GetValue(decoded)) == 6 &&
            (float)packet.GetField("PeakSpeed").GetValue(decoded) == .06f, "light-wound trigger and low-pressure profile roundtrip unchanged");

        using var module = AssemblyDefinition.ReadAssembly(assembly.Location);
        var streams = module.MainModule.GetType("Rupture.Dismemberment.Classes.BloodStreams.PulsatingBloodStreamSystem");
        string[] Calls(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name).Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.Name).ToArray();
        Check(!Calls(streams, "CanCreateStreams").Contains("get_ArterySpray") && Calls(streams, "CanCreateStreams").Contains("get_EnableBloodEffects"),
            "legacy arterial setting cannot disable blood effects");
        Check(Calls(streams, "LaunchLightDrip").Contains("PredictImpact") && Calls(streams, "LaunchLightDrip").Contains("AppendStreamSample"),
            "light droplets capture the existing ballistic parcel path");
        string[] fractureCalls = Calls(streams, "StartFractureWound");
        Check(fractureCalls.Contains("StartOrReplace") && !fractureCalls.Any(n => n == "Publish" || n == "Receive" || n == "TrackLivingStream"),
            "fracture supply reuses the local emitter pool without networking or native bleed lifetimes");
        Check(streams.Methods.Single(m => m.Name == "StartOrReplace").Body.Instructions.Count(i =>
            i.Operand is FieldReference f && f.Name == "LocalFracture") >= 2,
            "native bleed replacement excludes local fracture emitters");
        var stop = streams.Methods.Single(m => m.Name == "StopById");
        Check(stop.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "Draining") && Calls(streams, "StopById").Contains("Stop"),
            "living-wound stops drain airborne parcels and stop clothing supply");
        var soak = module.MainModule.GetType("Rupture.Dismemberment.Classes.BloodStreams.ClothingSoakingSystem");
        Check(!Calls(soak, "Stop").Contains("Reset") && !Calls(soak, "Stop").Contains("Release"), "treatment does not clear native accumulation textures");
        Type lightType = typeof(EFT.HealthSystem.ActiveHealthController).GetNestedType("LightBleeding", BindingFlags.Public);
        Check(typeof(EFT.HealthSystem.ILightBleeding).IsAssignableFrom(lightType) && typeof(EFT.HealthSystem.IHeavyBleeding).IsAssignableFrom(
            typeof(EFT.HealthSystem.ActiveHealthController).GetNestedType("HeavyBleeding", BindingFlags.Public)), "installed EFT ABI identifies light and heavy health effects");
        Check(typeof(TextureDecalsPainter).GetField("_blitShader", BindingFlags.Instance | BindingFlags.Public)?.FieldType == typeof(UnityEngine.Shader) &&
            typeof(TextureDecalsPainter).GetMethod("CreateDecalTexture")?.ReturnType == typeof(UnityEngine.RenderTexture) &&
            typeof(TextureDecalsPainter).GetMethod("GetTextureByType")?.ReturnType == typeof(UnityEngine.Texture2D), "installed painter exposes the matched texture allocator, blood texture and compositor ABI");
        var binding = soak.Methods.Single(m => m.IsConstructor && m.IsStatic).Body.Instructions
            .Single(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr && (string)i.Operand == "_blitShader").Next;
        var lookupFlags = (BindingFlags)Convert.ToInt32(binding.Operand);
        Check(typeof(TextureDecalsPainter).GetField("_blitShader", lookupFlags)?.FieldType == typeof(UnityEngine.Shader),
            "production soaking compositor lookup resolves the installed public shader field");
    }
}
