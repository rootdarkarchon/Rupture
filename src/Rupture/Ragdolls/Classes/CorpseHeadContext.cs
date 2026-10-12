using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using Comfort.Common;

namespace Rupture.Ragdolls.Classes;

// Capture managed inventory ownership and native skeleton references before Player retirement.
internal sealed class CorpseHeadContext
{
	internal Transform Bone;
	internal Player Owner;
	internal Func<EquipmentSlot, Item> Equipped;
	internal Func<Item, bool> HasCurrentAddress;
	internal Action<Item, Callback> Throw;
	internal List<BodyRenderer> DecalRenderers;

	internal static CorpseHeadContext Find(PlayerBody body) =>
		body?.SkeletonRootJoint != null ? body.SkeletonRootJoint.GetComponentInChildren<CorpseHeadRetention>(true)?.Context : null;

	internal void BindIdentity(string profile, int playerId, uint deathSequence)
	{
		if (Bone == null) return;
		var retention = Bone.GetComponent<CorpseHeadRetention>();
		if (retention == null) return;
		retention.ProfileId = profile; retention.PlayerId = playerId; retention.DeathSequence = deathSequence;
	}

	internal static CorpseHeadContext Capture(Player player, PlayerBody body = null)
	{
		body = body != null ? body : player?.PlayerBody;
		var existing = Find(body);
		if (existing?.Bone != null)
		{
			existing.BindInventory(player);
			return existing;
		}
		Transform bone = body?.PlayerBones?.Head?.Original ?? player?.PlayerBones?.Head?.Original;
		if (bone == null && body?.SkeletonRootJoint != null)
			foreach (var candidate in body.SkeletonRootJoint.GetComponentsInChildren<Transform>(true))
				if (string.Equals(candidate.name, "Base HumanHead", StringComparison.Ordinal)) { bone = candidate; break; }
		if (bone == null) return null;
		var context = new CorpseHeadContext
		{
			Bone = bone,
			DecalRenderers = Dismemberment.Classes.HeadWounds.HeadWoundSystem.CaptureDecalRenderers(player)
		};
		context.BindInventory(player);
		var retention = bone.gameObject.AddComponent<CorpseHeadRetention>();
		retention.Context = context;
		return context;
	}

	private void BindInventory(Player player)
	{
		if (player == null || player.InventoryController == null) return;
		if (ReferenceEquals(Owner, player) && Equipped != null && Throw != null) return;
		Owner = player;
		var retainedController = player.InventoryController;
		// Resolve live inventory on every access: Fika replaces equipment at corpse creation.
		Func<InventoryController> controller = () => player != null ? player.InventoryController : retainedController;
		Equipped = slot => controller()?.Inventory?.Equipment?.GetSlot(slot)?.ContainedItem;
		HasCurrentAddress = item =>
		{
			var equipment = controller()?.Inventory?.Equipment;
			if (equipment == null || item == null) return false;
			foreach (var equipmentSlot in HeadgearDiagnostics.Slots)
			{
				var slot = equipment.GetSlot(equipmentSlot);
				if (ReferenceEquals(slot?.ContainedItem, item) && ReferenceEquals(item.CurrentAddress?.Container, slot)) return true;
			}
			return false;
		};
		Throw = (item, callback) => controller().ThrowItem(item, false, callback);
		HeadgearDiagnostics.ObserveEquipment(player, "bind");
	}
}

// Its lifetime follows the native skeleton, not temporary corpse physics registration.
internal sealed class CorpseHeadRetention : MonoBehaviour
{
	internal CorpseHeadContext Context;
	internal string ProfileId;
	internal int PlayerId;
	internal uint DeathSequence;
	private void OnDestroy()
	{
		Dismemberment.Classes.HeadWounds.HeadWoundSystem.Remove(ProfileId, PlayerId, DeathSequence);
		LethalHeadgearDetach.Remove(Context);
		Context = null;
	}
}
