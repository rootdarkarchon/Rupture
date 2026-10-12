using System;
using System.Linq;
using System.Reflection;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.ClientClasses;
using Fika.Core.Main.Components;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.Generic.SubPackets;
using HarmonyLib;
using Rupture.Ragdolls.Classes;

namespace Rupture.Fika;

// Packet observers plus the guarded observed-inventory lookup-root repair.
internal static class HeadgearNetworkDiagnostics
{
    private static readonly Harmony Patches = new Harmony("com.rootdarkarchon.rupture.fika.headgear");
    internal static void Install()
    {
        Patches.Patch(AccessTools.Method(typeof(InventoryController), nameof(InventoryController.ReplaceInventory)),
            postfix: new HarmonyMethod(typeof(CorpseInventoryRootFix), nameof(CorpseInventoryRootFix.Replaced)));
        Patch(typeof(FikaClient), "HandleInventoryPacket", nameof(Receive));
        var create = typeof(ObservedInventoryController).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name.EndsWith(".CreateOperationFromDescriptor", StringComparison.Ordinal));
        Patches.Patch(create, postfix: Method(nameof(Converted)), finalizer: Method(nameof(ConversionException)));
        Patch(typeof(ClientInventoryOperationHandler), nameof(ClientInventoryOperationHandler.ReceiveStatusFromServer), nameof(Status));
        Patches.Patch(AccessTools.Method(typeof(ObservedPlayer), nameof(ObservedPlayer.SetInventory)),
            prefix: Method(nameof(Replacing)), postfix: Method(nameof(Replaced)));
    }
    private static HarmonyMethod Method(string name) => new HarmonyMethod(typeof(HeadgearNetworkDiagnostics), name);
    private static void Patch(Type type, string method, string prefix) =>
        Patches.Patch(AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method), prefix: Method(prefix));
    internal static void Shutdown() => Patches.UnpatchSelf();
    private static string ItemId(InventoryOperationDescriptor descriptor) => descriptor is ThrowOperationDescriptor thrown ? thrown.ItemId :
        descriptor is MoveOperationDescriptor moved ? moved.ItemId : null;

    private static void Receive(in InventoryPacket packet)
    {
        try
        {
            string id = ItemId(packet.Descriptor);
            if (id == null) return;
            FikaPlayer player = null;
            bool registered = CoopHandler.TryGetCoopHandler(out var handler) && handler.Players.TryGetValue(packet.NetId, out player);
            if (registered && packet.Descriptor is ThrowOperationDescriptor)
            {
                LethalHeadgearDetach.ObserveReplicatedThrow(player, id);
                HeadgearDiagnostics.ObserveEquipment(player, "receive snapshot");
            }
            if (HeadgearDiagnostics.Tracked(id))
                HeadgearDiagnostics.Record(id, $"receive netId={packet.NetId},operation={packet.CallbackId},registered={registered},descriptor={packet.Descriptor.GetType().Name}", !registered);
        }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
    private static void Converted(InventoryOperationDescriptor descriptor, OperationCreationResult __result)
    {
        try
        {
            string id = ItemId(descriptor);
            if (HeadgearDiagnostics.Tracked(id))
                HeadgearDiagnostics.Record(id, $"convert success={__result.Succeeded},error={__result.Error}", __result.Failed);
        }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
    private static void ConversionException(InventoryOperationDescriptor descriptor, Exception __exception)
    {
        if (__exception == null) return;
        try
        {
            string id = ItemId(descriptor);
            if (HeadgearDiagnostics.Tracked(id)) HeadgearDiagnostics.Record(id, $"conversion exception: {__exception}", true);
        }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
    private static void Status(ClientInventoryOperationHandler __instance, ClientInventoryController.ServerOperationStatus serverStatus)
    {
        try
        {
            if (__instance.Operation is IOneItemOperation one && one.Item1 != null && HeadgearDiagnostics.Tracked(one.Item1.Id))
                HeadgearDiagnostics.Record(one.Item1.Id, $"server status={serverStatus.Status},operation={__instance.Operation.Id},error={serverStatus.Error},{HeadgearDiagnostics.State(one.Item1)}",
                    serverStatus.Status == EOperationStatus.Failed);
        }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
    private static void Replacing(ObservedPlayer __instance)
    {
        try { HeadgearDiagnostics.ObserveEquipment(__instance, "before inventory replacement"); }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
    private static void Replaced(ObservedPlayer __instance)
    {
        try { HeadgearDiagnostics.ObserveEquipment(__instance, "after inventory replacement"); }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
}
