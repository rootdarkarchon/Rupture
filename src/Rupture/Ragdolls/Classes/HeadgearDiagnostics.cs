using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.Interactive;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static class HeadgearDiagnostics
{
    internal static readonly EquipmentSlot[] Slots = { EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear };
    private sealed class Entry
    {
        internal WeakReference<Item> Item;
        internal HeadgearThrowEvidence Evidence;
    }
    private static readonly HeadgearAuditTrail History = new HeadgearAuditTrail();
    private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private static uint _epoch;
    internal static bool Tracked(string id) => History.Contains(id);
    internal static string Role => MultiplayerRuntime.Current.IsHeadless ? "headless" : MultiplayerRuntime.Current.IsRemoteClient ? "client" : "host/solo";
    internal static string Identity(Player player) => player == null ? "retired" : $"profile={player.ProfileId},netId={MultiplayerRuntime.Current.PlayerId(player)},controller={player.InventoryController?.GetType().Name}";
    internal static string State(Item item) => item == null ? "item=null" :
        $"instance={RuntimeHelpers.GetHashCode(item)},parent={item.CurrentAddress?.ToString() ?? "none"},owner={item.Owner?.GetType().Name ?? "none"}";

    internal static void Record(string id, string message, bool failure = false)
    {
        if (string.IsNullOrEmpty(id)) return;
        string evicted = History.Add(id, $"{Time.realtimeSinceStartup:F3}:{message}");
        if (evicted != null) Entries.Remove(evicted);
        if (!failure) return;
        string history = History.Report(id);
        if (history != null) RupturePlugin.Log?.LogWarning($"Headgear audit [{Role}] item={id}: {history}");
    }

    internal static void ObserveEquipment(Player player, string stage)
    {
        if (player == null) return;
        var equipment = player.InventoryController?.Inventory?.Equipment;
        if (equipment == null) return;
        foreach (var slot in Slots)
        {
            var item = equipment.GetSlot(slot)?.ContainedItem;
            if (item == null) continue;
            bool restored = stage == "after inventory replacement" && Entries.TryGetValue(item.Id, out var entry) && entry.Evidence?.WorldCreated == true;
            Record(item.Id, $"{stage},slot={slot},{Identity(player)},{State(item)}", restored);
        }
    }

    internal static HeadgearThrowEvidence Begin(Item item, string context)
    {
        Record(item.Id, $"begin {context},{State(item)}");
        var evidence = new HeadgearThrowEvidence();
        Entries[item.Id] = new Entry { Item = new WeakReference<Item>(item), Evidence = evidence };
        return evidence;
    }

    internal static void ObserveOperation(AbstractOperation operation, ref Callback callback)
    {
        if (!(operation is IOneItemOperation one) || one.Item1 == null) return;
        Item item = one.Item1;
        if (!Tracked(item.Id)) return;
        bool throwing = operation is ThrowOperation;
        if (!throwing && !(operation is MoveOperation)) return;
        HeadgearThrowEvidence evidence = null;
        if (throwing)
        {
            if (!Entries.TryGetValue(item.Id, out var entry) || entry.Evidence.OperationStarted ||
                !entry.Item.TryGetTarget(out var original) || !ReferenceEquals(original, item))
                evidence = Begin(item, "observed throw");
            else evidence = entry.Evidence;
            evidence.OperationStarted = true;
        }
        string operationInfo = $"operation={operation.GetType().Name},id={operation.Id}";
        Record(item.Id, $"execute {operationInfo},{State(item)}");
        Callback previous = callback;
        uint epoch = _epoch;
        // Wrap the operation itself, before Fika's pooled handler can be cleared/reused.
        callback = result =>
        {
            try
            {
                if (epoch == _epoch)
                {
                    if (evidence != null) SetResult(evidence, result);
                    Record(item.Id, $"result {operationInfo},success={result?.Succeed},error={result?.Error},{State(item)}", result?.Succeed != true);
                }
            }
            catch (Exception error) { ObserverFailure(error); }
            previous?.Invoke(result);
        };
    }

    private static void SetResult(HeadgearThrowEvidence evidence, IResult result)
    {
        evidence.Finished = true; evidence.Successful = result?.Succeed == true;
        evidence.Error = result?.Error?.ToString() ?? (result == null ? "missing operation result" : null);
    }
    internal static void ControllerResult(Item item, HeadgearThrowEvidence evidence, IResult result)
    {
        if (!Entries.TryGetValue(item.Id, out var entry) || !ReferenceEquals(entry.Evidence, evidence)) return;
        // Simulation can reject the request before an AbstractOperation is created.
        if (!evidence.OperationStarted) SetResult(evidence, result);
        Record(item.Id, $"controller callback success={result?.Succeed},error={result?.Error}", result?.Succeed != true);
    }
    internal static void OperationException(AbstractOperation operation, Exception exception)
    {
        if (!(operation is IOneItemOperation one) || one.Item1 == null || !Tracked(one.Item1.Id)) return;
        Record(one.Item1.Id, $"execute exception operation={operation.Id}: {exception},{State(one.Item1)}", true);
    }

    internal static void WorldCreated(Item item, LootItem loot)
    {
        if (item == null || !Tracked(item.Id)) return;
        if (Entries.TryGetValue(item.Id, out var entry) && entry.Item.TryGetTarget(out var original) && ReferenceEquals(item, original))
            entry.Evidence.WorldCreated = loot != null && ReferenceEquals(loot.Item, item);
        Record(item.Id, $"world-loot={(loot != null)},sameItem={(loot != null && ReferenceEquals(loot.Item, item))},{State(item)}", loot == null);
    }

    internal static void ObserverFailure(Exception error) => RupturePlugin.Log?.LogWarning($"Headgear audit observer failed: {error.GetType().Name}: {error.Message}");
    internal static void Reset() { _epoch++; Entries.Clear(); History.Clear(); }
}
