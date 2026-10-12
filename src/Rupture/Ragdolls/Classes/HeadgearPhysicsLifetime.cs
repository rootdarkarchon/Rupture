using System.Runtime.CompilerServices;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

// Let EFT own flight, final loot synchronization, culling and Rigidbody destruction.
// Only headgear from a lethal throw receives the earlier settle decision.
internal static class HeadgearPhysicsLifetime
{
    private sealed class Flight
    {
        internal Item Item;
        internal Rigidbody Body;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal float NextSample;
        internal float QuietSince = -1f;
    }

    private static ConditionalWeakTable<LootItem, Flight> Flights = new ConditionalWeakTable<LootItem, Flight>();

    internal static void Track(LootItem loot)
    {
        if (loot == null || loot.RigidBody == null) return;
        Flights.Remove(loot);
        Flights.Add(loot, new Flight
        {
            Item = loot.Item, Body = loot.RigidBody,
            Position = loot.RigidBody.position, Rotation = loot.RigidBody.rotation,
            NextSample = Time.time + 0.1f
        });
    }

    internal static bool IsDone(LootItem loot, bool nativeResult, float now)
    {
        if (nativeResult || !TryGetFlight(loot, out Flight flight)) return nativeResult;
        Rigidbody body = flight.Body;
        // A kinematic network proxy is not evidence that the authority has settled.
        if (body.isKinematic) { flight.QuietSince = -1f; return false; }
        if (body.IsSleeping()) return true;
        if (now < flight.NextSample) return false;
        flight.NextSample = now + 0.1f;
        bool quiet = body.velocity.sqrMagnitude <= 0.0025f && body.angularVelocity.sqrMagnitude <= 0.0225f &&
            (body.position - flight.Position).sqrMagnitude <= 0.000025f && Quaternion.Angle(body.rotation, flight.Rotation) <= 1f;
        flight.Position = body.position;
        flight.Rotation = body.rotation;
        if (!quiet) { flight.QuietSince = -1f; return false; }
        if (flight.QuietSince < 0f) flight.QuietSince = now;
        return now - flight.QuietSince >= 0.5f;
    }

    internal static void BeforeStop(LootItem loot)
    {
        if (!TryGetFlight(loot, out Flight flight)) return;
        Flights.Remove(loot);
        // Unity's Destroy is deferred. Release every matching support entry now,
        // including duplicates left by another StartPhysics invocation.
        var supported = PhysicsExtensions.UpdateController._rigidbodies;
        int count = 0;
        if (supported != null)
            foreach (var entry in supported)
                if (entry.rigidbody == flight.Body) count++;
        for (int i = 0; i < count; i++)
            PhysicsExtensions.UpdateController.UnsupportRigidbody(flight.Body);
        // StopPhysics itself destroys the body and invokes OnRigidbodyStopped.
        // Preserve that virtual callback: loot owners use it for final state.
    }

    private static bool TryGetFlight(LootItem loot, out Flight flight)
    {
        flight = null;
        if (loot == null || !Flights.TryGetValue(loot, out flight)) return false;
        if (ReferenceEquals(loot.Item, flight.Item) && loot.RigidBody != null && loot.RigidBody == flight.Body) return true;
        Flights.Remove(loot); // Pooled/replaced loot must not inherit an old throw.
        return false;
    }

    internal static void Reset() => Flights = new ConditionalWeakTable<LootItem, Flight>();
}
