using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using Mono.Cecil;

internal static class HeadgearDiagnosticsTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    internal static void Run(Assembly core, Assembly bridge)
    {
        CorpseInventoryRootTests.Run(core, bridge);
        var type = core.GetType("Rupture.Ragdolls.Classes.HeadgearAuditTrail", true);
        object trail = Activator.CreateInstance(type, true);
        object Call(string method, params object[] args) => type.GetMethod(method, Flags).Invoke(trail, args);
        for (int i = 0; i < 12; i++) Call("Add", "mask", "event-" + i);
        string report = (string)Call("Report", "mask");
        Check(!report.Contains("event-3") && report.Contains("event-4") && report.Contains("event-11"), "headgear audit retains the last eight events");
        Check(Call("Report", "mask") == null, "repeated pickup failures produce one bounded report per retained item");
        string evicted = null;
        for (int i = 0; i < 256; i++) evicted = (string)Call("Add", "item-" + i, new string('x', 2048));
        Check(evicted == "mask" && (int)type.GetProperty("Count", Flags).GetValue(trail) == 256 && !(bool)Call("Contains", "mask"),
            "headgear history bounds item count and reports eviction to the evidence cache");
        Check(((string)Call("Report", "item-255")).Length == 512, "headgear audit bounds external exception text");
        Call("Clear"); Call("Add", "mask", "new raid");
        Check((string)Call("Report", "mask") == "new raid", "raid reset clears failure suppression and old history");

        var evidenceType = core.GetType("Rupture.Ragdolls.Classes.HeadgearThrowEvidence", true);
        var evidence = Activator.CreateInstance(evidenceType, true);
        for (int mask = 0; mask < 8; mask++)
        {
            evidenceType.GetField("Finished", Flags).SetValue(evidence, (mask & 1) != 0);
            evidenceType.GetField("Successful", Flags).SetValue(evidence, (mask & 2) != 0);
            evidenceType.GetField("WorldCreated", Flags).SetValue(evidence, (mask & 4) != 0);
            Check((bool)evidenceType.GetProperty("Complete", Flags).GetValue(evidence) == (mask == 7),
                "throw completion requires successful terminal result and world loot: " + mask);
        }
        evidenceType.GetField("Successful", Flags).SetValue(evidence, false);
        Check(evidenceType.GetProperty("Failure", Flags).GetValue(evidence) != null, "failed operation is not mistaken for completed removal");

        // Verify the installed ABI targets, including parameter names used by Harmony injection.
        var execute = typeof(AbstractOperation).GetMethod("Execute", new[] { typeof(Callback) });
        Check(execute != null && execute.GetParameters()[0].Name == "callback", "installed operation callback observer signature");
        var receive = typeof(FikaClient).GetMethod("HandleInventoryPacket", Flags);
        Check(receive?.GetParameters().Length == 2 && receive.GetParameters()[0].ParameterType == typeof(InventoryPacket).MakeByRefType() &&
            receive.GetParameters()[0].IsIn && receive.GetParameters()[0].Name == "packet" &&
            receive.GetParameters()[1].ParameterType == typeof(FikaPlayer),
            "installed Fika readonly inventory receive observer signature");
        var convert = typeof(ObservedInventoryController).GetMethods(Flags).Single(m => m.Name.EndsWith(".CreateOperationFromDescriptor"));
        Check(convert.ReturnType.FullName == "Diz.LanguageExtensions.OperationCreationResult" && convert.GetParameters()[0].Name == "descriptor",
            "installed Fika conversion observer signature");
        Check(typeof(ObservedPlayer).GetMethod("SetInventory", Flags) != null &&
            typeof(ClientInventoryOperationHandler).GetMethod("ReceiveStatusFromServer", Flags)?.GetParameters()[0].Name == "serverStatus",
            "installed inventory replacement and rejection observers resolve");
        using var module = AssemblyDefinition.ReadAssembly(core.Location);
        using var game = AssemblyDefinition.ReadAssembly(typeof(EFT.Interactive.LootItem).Assembly.Location);
        var loot = game.MainModule.GetType("EFT.Interactive.LootItem");
        var stop = loot.Methods.Single(m => m.Name == "StopPhysics");
        var stopCalls = stop.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(stopCalls.Any(m => m.DeclaringType.FullName == "UnityEngine.Object" && m.Name == "Destroy") &&
            stopCalls.Any(m => m.Name == "OnRigidbodyStopped") && stopCalls.Any(m => m.Name == "StopCoroutine"),
            "installed loot stop destroys the rigidbody, preserves finalization and retires its coroutine");
        var start = loot.Methods.Single(m => m.Name == "StartPhysics");
        var startCalls = start.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(startCalls.Any(m => m.Name == "SupportRigidbody") && startCalls.Any(m => m.Name == "PhysicsStopWorker") &&
            loot.Methods.Single(m => m.Name == "CreateLootWithRigidbody").Body.Instructions.Select(i => i.Operand)
                .OfType<MethodReference>().Any(m => m.Name == "StartPhysics"),
            "native thrown-loot creation owns paired support registration and stop worker");
        foreach (string name in new[] { "EFT.Interactive.LootItem", "EFT.Interactive.ObservedLootItem" })
            Check(game.MainModule.GetType(name).Methods.Any(m => m.Name == "IsRigidbodyDone" && m.IsVirtual && m.Parameters.Count == 0),
                "settle hook resolves for " + name);
        var applied = game.MainModule.GetType("EFT.Interactive.ObservedLootItem").Methods.Single(m => m.Name == "ApplyNetPacket");
        Check(applied.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Any(m => m.Name == "SetPositionAndRotation"),
            "observed loot applies the authority pose independently of its rigidbody");
        var patch = module.MainModule.GetType("Rupture.Ragdolls.Patches.HeadgearOperationPatch");
        Check(patch.Methods.Single(m => m.Name == "Finalizer").ReturnType.FullName == "System.Void", "operation diagnostic finalizer does not replace game exceptions");
        using var adapter = AssemblyDefinition.ReadAssembly(bridge.Location);
        var network = adapter.MainModule.GetType("Rupture.Fika.HeadgearNetworkDiagnostics");
        Check(network.Methods.Single(m => m.Name == "Receive").Body.Instructions.Select(i => i.Operand)
            .OfType<MethodReference>().Any(m => m.Name == "ObserveReplicatedThrow"),
            "Fika receive marks equipped corpse headgear before inventory removal");
        var thrown = module.MainModule.GetType("Rupture.Ragdolls.Patches.HelmetThrowPhysicsPatch");
        var thrownCalls = thrown.Methods.Single(m => m.Name == "Postfix").Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check(thrownCalls.Any(m => m.Name == "ConsumeReplicatedThrow") && thrownCalls.Any(m => m.Name == "Track") &&
            !thrownCalls.Any(m => m.Name == "SupportRigidbody"),
            "authority and replicated throws enter cleanup without adding standalone support registrations");
        Check(network.Methods.Where(m => new[] { "Receive", "Converted", "Status", "ConversionException", "Replacing", "Replaced" }.Contains(m.Name))
            .All(m => m.ReturnType.FullName == "System.Void" && m.Parameters.All(p => !p.ParameterType.IsByReference || p.IsIn)),
            "Fika headgear observers do not suppress calls or replace packets/results");
    }
}
