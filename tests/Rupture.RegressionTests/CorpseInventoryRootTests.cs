using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT.InventoryLogic;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.HostClasses;
using Mono.Cecil;

internal static class CorpseInventoryRootTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static T Bare<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static void Id(Item item, string id) => typeof(Item).GetField("<Id>k__BackingField", Flags).SetValue(item, id);
    private static InventoryEquipment Equipment(string id, string itemId, out Slot slot, out Item item)
    {
        var equipment = Bare<InventoryEquipment>(); Id(equipment, id);
        item = Bare<Item>(); Id(item, itemId);
        slot = Bare<Slot>();
        typeof(Slot).GetField("<ContainedItem>k__BackingField", Flags).SetValue(slot, item);
        equipment.Slots = new[] { slot }; equipment.Grids = Array.Empty<Grid>();
        return equipment;
    }
    private static void Inventory(InventoryController controller, InventoryEquipment equipment)
    {
        var inventory = Bare<Inventory>(); typeof(Inventory).GetField("Equipment", Flags).SetValue(inventory, equipment);
        typeof(InventoryController).GetField("<Inventory>k__BackingField", Flags).SetValue(controller, inventory);
    }

    internal static void Run(Assembly core, Assembly bridge)
    {
        var fix = bridge.GetType("Rupture.Fika.CorpseInventoryRootFix", true).GetMethod("Synchronize", Flags);
        bool Sync(InventoryController controller) => (bool)fix.Invoke(null, new object[] { controller });
        var oldRoot = Equipment("equipment", "mask", out var oldSlot, out var oldItem);
        var current = Equipment("equipment", "mask", out var slot, out var currentItem);
        var controller = Bare<ObservedInventoryController>(); controller._rootItem = oldRoot;
        Inventory(controller, current);
        Check(controller.TryFindItem("mask", out var stale) && ReferenceEquals(stale, oldItem),
            "installed EFT reproduces stale lookup after equipment replacement");
        var corpseOwner = Bare<CorpseItemController>(); corpseOwner._rootItem = current;
        var address = corpseOwner.CreateItemAddress(); current.CurrentAddress = address;
        Check(Sync(controller) && controller.TryFindItem("mask", out var resolved) && ReferenceEquals(resolved, currentItem),
            "observed lookup resolves the exact corpse item after root synchronization");
        Check(ReferenceEquals(controller.Inventory.Equipment, current) && ReferenceEquals(current.CurrentAddress, address) &&
            ReferenceEquals(slot.ContainedItem, currentItem) && ReferenceEquals(oldSlot.ContainedItem, oldItem),
            "lookup repair preserves corpse address and both item trees without cloning or moving items");
        Check(!Sync(controller), "repeated inventory synchronization is idempotent");
        typeof(Slot).GetField("<ContainedItem>k__BackingField", Flags).SetValue(slot, null);
        Check(!controller.TryFindItem("mask", out _) && !corpseOwner.TryFindItem("mask", out _),
            "removed corpse item cannot reappear through the old observed root");
        var lootOwner = Bare<CorpseItemController>(); lootOwner._rootItem = currentItem;
        Check(lootOwner.TryFindItem("mask", out var loot) && ReferenceEquals(loot, currentItem),
            "world item remains discoverable independently after corpse removal");
        var later = Equipment("equipment", "mask", out _, out var laterItem);
        Inventory(controller, later);
        Check(Sync(controller) && controller.TryFindItem("mask", out var latest) && ReferenceEquals(latest, laterItem),
            "a subsequent replacement follows the newest item instances");
        var unrelated = Equipment("other-equipment", "mask", out _, out _);
        Inventory(controller, unrelated);
        Check(!Sync(controller) && ReferenceEquals(controller.RootItem, later), "different equipment IDs are never substituted");
        Inventory(controller, null);
        Check(!Sync(controller), "missing replacement equipment leaves lookup unchanged");
        var host = Bare<HostInventoryController>(); host._rootItem = oldRoot; Inventory(host, current);
        Check(!Sync(host) && ReferenceEquals(host.RootItem, oldRoot) && !Sync(null), "host and null controllers are unaffected");
        var state = core.GetType("Rupture.Ragdolls.Classes.HeadgearDiagnostics", true).GetMethod("State", Flags);
        string orphan = (string)state.Invoke(null, new object[] { Bare<Item>() });
        Check(orphan.Contains("parent=none") && orphan.Contains("owner=none"), "parentless items remain safe to audit");

        using var adapter = AssemblyDefinition.ReadAssembly(bridge.Location);
        var install = adapter.MainModule.GetType("Rupture.Fika.HeadgearNetworkDiagnostics").Methods.Single(m => m.Name == "Install");
        Check(install.Body.Instructions.Any(i => i.Operand is TypeReference t && t.Name == "CorpseInventoryRootFix") &&
            install.Body.Instructions.Any(i => Equals(i.Operand, "ReplaceInventory")), "root repair is installed on actual inventory replacement");
        var replace = typeof(InventoryController).GetMethod("ReplaceInventory", new[] { typeof(Inventory) });
        Check(replace != null && typeof(ItemController).GetField("_rootItem").FieldType == typeof(Item),
            "installed EFT inventory replacement and mutable root ABI match");
    }
}
