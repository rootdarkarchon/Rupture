using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using Rupture.Dismemberment.Classes.HeadWounds;

namespace Rupture.Ragdolls.Classes;

internal static class LethalHeadgearDetach
{
	private sealed class PendingDetach : HeadgearThrowSequence<Item>
	{
		internal CorpseHeadContext Head;
		internal float Momentum;
		internal Vector3 Direction;
		internal uint Seed;
		internal float ExpiresAt;
		internal Func<Item, bool> Equipped;
		internal Action<Item> StartThrow;
		internal Action<Item> TimedOut;
		internal HeadgearThrowEvidence Evidence;
		internal Func<Item, bool> Completed;
		internal Func<Item, bool> Failed;
	}

	private readonly struct PendingVelocity
	{
		internal readonly Vector3 Velocity;
		internal readonly Vector3 AngularVelocity;
		internal readonly float ExpiresAt;

		internal PendingVelocity(Vector3 velocity, Vector3 angularVelocity, float expiresAt)
		{
			Velocity = velocity;
			AngularVelocity = angularVelocity;
			ExpiresAt = expiresAt;
		}
	}

	private static readonly List<PendingDetach> PendingDetaches = new List<PendingDetach>();
	private static readonly Dictionary<string, PendingVelocity> PendingVelocities = new Dictionary<string, PendingVelocity>(StringComparer.Ordinal);
	private const float PendingLifetimeSeconds = 10f;
	private sealed class ReplicatedThrow { internal float ExpiresAt; }
	private static ConditionalWeakTable<Item, ReplicatedThrow> ReplicatedThrows = new ConditionalWeakTable<Item, ReplicatedThrow>();

	// Called before Fika executes the inventory removal, while slot membership is known.
	internal static void ObserveReplicatedThrow(Player player, string itemId)
	{
		if (player?.HealthController?.IsAlive != false) return;
		var equipment = player.InventoryController?.Inventory?.Equipment;
		if (equipment == null) return;
		foreach (EquipmentSlot slot in HeadgearDiagnostics.Slots)
		{
			Item item = equipment.GetSlot(slot)?.ContainedItem;
			if (item == null || item.Id != itemId) continue;
			ReplicatedThrows.Remove(item);
			ReplicatedThrows.Add(item, new ReplicatedThrow { ExpiresAt = Time.time + PendingLifetimeSeconds });
			return;
		}
	}

	internal static bool ConsumeReplicatedThrow(Item item)
	{
		if (item == null || !ReplicatedThrows.TryGetValue(item, out ReplicatedThrow pending)) return false;
		ReplicatedThrows.Remove(item);
		return Time.time <= pending.ExpiresAt;
	}

	internal static void Queue(Player player, float projectileMomentum, Vector3 direction, uint seed) =>
		Queue(RagdollCoordinator.TryGetInteractionTarget(player, out var target) ? target.Head : null, projectileMomentum, direction, seed);

	internal static void Queue(CorpseHeadContext head, float projectileMomentum, Vector3 direction, uint seed)
	{
		if (RagdollCoordinator.IsRemoteClient || RuptureConfiguration.AutomaticHeadgearDetachment?.Value == false ||
			head?.Equipped == null || head.Throw == null ||
			!IsFinitePositive(projectileMomentum))
		{
			return;
		}

		foreach (PendingDetach pending in PendingDetaches)
			if (ReferenceEquals(pending.Head, head)) return;

		PendingDetach request = new PendingDetach
		{
			Head = head,
			Momentum = projectileMomentum,
			Direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : head.Bone != null ? head.Bone.forward : Vector3.forward,
			Seed = seed == 0 ? 1u : seed,
			ExpiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds
		};
		request.Equipped = item =>
		{
			bool equipped = IsEquipped(request.Head, item);
			if (!equipped && !ReferenceEquals(item, request.WaitingItem))
				HeadgearDiagnostics.Record(item.Id, $"queued item no longer equipped; skipped,{HeadgearDiagnostics.State(item)}", true);
			return equipped;
		};
		request.StartThrow = item =>
		{
			request.Evidence = HeadgearDiagnostics.Begin(item, HeadgearDiagnostics.Identity(head.Owner));
			string invalid = MultiplayerRuntime.Current.ValidateHeadgearOwner(head.Owner);
			if (invalid == null && head.HasCurrentAddress?.Invoke(item) != true) invalid = "equipment/address changed before throw";
			if (invalid != null)
			{
				request.Evidence.Finished = true; request.Evidence.Error = invalid;
				HeadgearDiagnostics.Record(item.Id, $"skipped: {invalid},{HeadgearDiagnostics.State(item)}", true);
				return;
			}
			ResolveThrowVelocity(item, request.Momentum, request.Direction, ref request.Seed, out Vector3 velocity, out Vector3 angularVelocity);
			PendingVelocities[item.Id] = new PendingVelocity(velocity, angularVelocity, Time.realtimeSinceStartup + PendingLifetimeSeconds);
			HeadgearThrowEvidence evidence = request.Evidence;
			request.Head.Throw(item, result =>
			{
				try { HeadgearDiagnostics.ControllerResult(item, evidence, result); }
				catch (Exception error) { HeadgearDiagnostics.ObserverFailure(error); }
			});
		};
		request.Completed = item => request.Evidence?.Complete == true;
		request.Failed = item =>
		{
			if (request.Evidence?.Failure == null) return false;
			PendingVelocities.Remove(item.Id);
			HeadgearDiagnostics.Record(item.Id, $"sequence stopped: {request.Evidence.Failure},{HeadgearDiagnostics.State(item)}", true);
			return true;
		};
		request.TimedOut = item =>
		{
			PendingVelocities.Remove(item.Id);
			HeadgearDiagnostics.Record(item.Id, $"timeout: equipped={IsEquipped(head, item)},operationFinished={request.Evidence?.Finished}," +
				$"success={request.Evidence?.Successful},worldLoot={request.Evidence?.WorldCreated},{HeadgearDiagnostics.State(item)}", true);
		};
		AddContainedItem(head, EquipmentSlot.Headwear, request.Items);
		AddContainedItem(head, EquipmentSlot.Earpiece, request.Items);
		AddContainedItem(head, EquipmentSlot.FaceCover, request.Items);
		AddContainedItem(head, EquipmentSlot.Eyewear, request.Items);
		if (request.Items.Count != 0)
		{
			PendingDetaches.Add(request);
		}
	}

	internal static void Update()
	{
		float now = Time.realtimeSinceStartup;
		PruneVelocities(now);
		for (int index = PendingDetaches.Count - 1; index >= 0; index--)
		{
			PendingDetach request = PendingDetaches[index];
			if (RuptureConfiguration.AutomaticHeadgearDetachment?.Value == false)
			{
				// Stop starting new operations; never undo an already submitted throw.
				PendingDetaches.RemoveAt(index);
				continue;
			}
			if (request.Head?.Bone == null || request.ExpiresAt <= now)
			{
				if (request.WaitingItem != null)
				{
					PendingVelocities.Remove(request.WaitingItem.Id);
					HeadgearDiagnostics.Record(request.WaitingItem.Id, $"pending sequence retired: headAlive={request.Head?.Bone != null},expired={request.ExpiresAt <= now}", true);
				}
				PendingDetaches.RemoveAt(index);
				continue;
			}

			try
			{
				if (request.Advance(now, request.Equipped, request.StartThrow, request.TimedOut, request.Completed, request.Failed)) PendingDetaches.RemoveAt(index);
			}
			catch (Exception exception)
			{
				if (request.WaitingItem != null) PendingVelocities.Remove(request.WaitingItem.Id);
				PendingDetaches.RemoveAt(index);
				if (request.WaitingItem != null) HeadgearDiagnostics.Record(request.WaitingItem.Id,
					$"throw exception: {exception.GetType().Name}: {exception.Message}", true);
			}
		}
	}

	internal static bool TryConsume(Item item, out Vector3 velocity, out Vector3 angularVelocity)
	{
		velocity = default;
		angularVelocity = default;
		if (item == null)
		{
			return false;
		}
		PruneVelocities(Time.realtimeSinceStartup);
		if (!PendingVelocities.TryGetValue(item.Id, out PendingVelocity pending))
		{
			return false;
		}
		PendingVelocities.Remove(item.Id);
		velocity = pending.Velocity;
		angularVelocity = pending.AngularVelocity;
		return true;
	}

	internal static void Reset()
	{
		PendingDetaches.Clear();
		PendingVelocities.Clear();
		ReplicatedThrows = new ConditionalWeakTable<Item, ReplicatedThrow>();
		HeadgearPhysicsLifetime.Reset();
		HeadgearDiagnostics.Reset();
	}

	private static void AddContainedItem(CorpseHeadContext head, EquipmentSlot equipmentSlot, List<Item> items)
	{
		Item item = head.Equipped(equipmentSlot);
		if (item != null && !items.Contains(item))
		{
			items.Add(item);
		}
	}

	private static bool IsEquipped(CorpseHeadContext head, Item item)
	{
		if (head?.Equipped == null || head.Throw == null || item == null)
		{
			return false;
		}
		return ReferenceEquals(head.Equipped(EquipmentSlot.Headwear), item) ||
			ReferenceEquals(head.Equipped(EquipmentSlot.Earpiece), item) ||
			ReferenceEquals(head.Equipped(EquipmentSlot.FaceCover), item) ||
			ReferenceEquals(head.Equipped(EquipmentSlot.Eyewear), item);
	}

	private static void ResolveThrowVelocity(Item item, float momentum, Vector3 direction, ref uint seed,
		out Vector3 velocity, out Vector3 angularVelocity)
	{
		float itemMass = item != null && IsFinitePositive(item.TotalWeight) ? item.TotalWeight : 0.5f;
		float speed = Mathf.Clamp(momentum / Mathf.Max(0.25f, itemMass), 3f, 14f);
		Vector3 axis = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;
		Vector3 lateralAxis = Vector3.Cross(axis, Vector3.up);
		if (lateralAxis.sqrMagnitude < 0.0001f)
		{
			lateralAxis = Vector3.right;
		}
		lateralAxis.Normalize();
		float lateral = (HeadWoundBallistics.Next01(ref seed) * 2f - 1f) * Mathf.Min(1.5f, speed * 0.15f);
		float upward = Mathf.Clamp(1.5f + speed * 0.15f, 2f, 4f);
		velocity = axis * speed + lateralAxis * lateral + Vector3.up * upward;

		Vector3 spin = new Vector3(
			HeadWoundBallistics.Next01(ref seed) * 2f - 1f,
			HeadWoundBallistics.Next01(ref seed) * 2f - 1f,
			HeadWoundBallistics.Next01(ref seed) * 2f - 1f);
		if (spin.sqrMagnitude < 0.0001f)
		{
			spin = Vector3.up;
		}
		angularVelocity = spin.normalized * Mathf.Clamp(4f + speed * 0.4f, 4f, 10f);
	}

	private static void PruneVelocities(float now)
	{
		if (PendingVelocities.Count == 0)
		{
			return;
		}
		List<string> expired = null;
		foreach (KeyValuePair<string, PendingVelocity> pair in PendingVelocities)
		{
			if (pair.Value.ExpiresAt <= now)
			{
				(expired ??= new List<string>()).Add(pair.Key);
			}
		}
		if (expired == null)
		{
			return;
		}
		foreach (string itemId in expired)
		{
			PendingVelocities.Remove(itemId);
		}
	}

	internal static void Remove(CorpseHeadContext head)
	{
		for (int i = PendingDetaches.Count - 1; i >= 0; i--)
			if (ReferenceEquals(PendingDetaches[i].Head, head))
			{
				foreach (Item item in PendingDetaches[i].Items) PendingVelocities.Remove(item.Id);
				PendingDetaches.RemoveAt(i);
			}
	}

	private static bool IsFinitePositive(float value)
	{
		return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
	}
}
