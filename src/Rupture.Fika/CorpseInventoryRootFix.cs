using System;
using System.Runtime.CompilerServices;
using EFT.InventoryLogic;
using Fika.Core.Main.ObservedClasses;
using Rupture.Ragdolls.Classes;

namespace Rupture.Fika;

// Fika rebuilds observed corpse equipment through ReplaceInventory. EFT updates
// Inventory there, but ItemController.TryFindItem still traverses _rootItem.
internal static class CorpseInventoryRootFix
{
    internal static bool Synchronize(InventoryController controller)
    {
        if (!(controller is ObservedInventoryController)) return false;
        var equipment = controller.Inventory?.Equipment;
        var previous = controller.RootItem as InventoryEquipment;
        if (equipment == null || previous == null || ReferenceEquals(previous, equipment) ||
            string.IsNullOrEmpty(equipment.Id) || !string.Equals(previous.Id, equipment.Id, StringComparison.Ordinal)) return false;

        // Only align the lookup alias. Native corpse creation retains ownership of
        // addresses, slots and world registration; no item is cloned or moved here.
        controller._rootItem = equipment;
        return true;
    }

    internal static void Replaced(InventoryController __instance)
    {
        if (!(__instance is ObservedInventoryController)) return;
        try
        {
            var previous = __instance.RootItem;
            bool updated = Synchronize(__instance);
            var equipment = __instance.Inventory?.Equipment;
            if (equipment == null || (!updated && ReferenceEquals(previous, equipment))) return;
            foreach (var slot in HeadgearDiagnostics.Slots)
            {
                var item = equipment.GetSlot(slot)?.ContainedItem;
                if (item == null) continue;
                HeadgearDiagnostics.Record(item.Id,
                    $"lookup-root synchronized={updated},oldRoot={previous?.Id},newRoot={equipment.Id}," +
                    $"oldInstance={(previous == null ? 0 : RuntimeHelpers.GetHashCode(previous))},newInstance={RuntimeHelpers.GetHashCode(equipment)}",
                    !updated);
            }
        }
        catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
    }
}
