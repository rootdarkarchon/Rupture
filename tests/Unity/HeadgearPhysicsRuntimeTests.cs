#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EFT.Interactive;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.Patches;
using UnityEngine;

// Native loot/controller boundaries are doubled; production settle logic and
// Harmony hooks run unchanged against real Unity rigidbodies and colliders.
namespace EFT.InventoryLogic { public sealed class Item { } }
namespace EFT.Interactive
{
    public class LootItem : MonoBehaviour
    {
        public EFT.InventoryLogic.Item Item = new EFT.InventoryLogic.Item();
        public Rigidbody RigidBody;
        public int Stopped;
        [MethodImpl(MethodImplOptions.NoInlining)] public virtual bool IsRigidbodyDone() => RigidBody.IsSleeping();
        [MethodImpl(MethodImplOptions.NoInlining)] public void StopPhysics()
        {
            UnityEngine.Object.DestroyImmediate(RigidBody); RigidBody = null;
            OnRigidbodyStopped();
        }
        public virtual void OnRigidbodyStopped() { Stopped++; }
    }
    public sealed class ObservedLootItem : LootItem
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public override bool IsRigidbodyDone() => false;
    }
}
internal static class PhysicsExtensions
{
    internal static class UpdateController
    {
        internal struct RigidbodyData { internal Rigidbody rigidbody; }
        internal static readonly List<RigidbodyData> _rigidbodies = new List<RigidbodyData>();
        internal static bool UpdateEnabled;
        internal static void Support(Rigidbody body) { _rigidbodies.Add(new RigidbodyData { rigidbody = body }); UpdateEnabled = true; }
        internal static void UnsupportRigidbody(Rigidbody body)
        {
            int index = _rigidbodies.FindIndex(entry => entry.rigidbody == body);
            if (index >= 0) _rigidbodies.RemoveAt(index);
            UpdateEnabled = _rigidbodies.Count != 0;
        }
    }
}
internal static class HeadgearPhysicsRuntimeTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var roots = new List<GameObject>();
        LootItem Create(bool observed = false)
        {
            var root = new GameObject("headgear lifetime test"); roots.Add(root);
            root.AddComponent<BoxCollider>();
            var loot = observed ? (LootItem)root.AddComponent<ObservedLootItem>() : root.AddComponent<LootItem>();
            loot.RigidBody = root.AddComponent<Rigidbody>(); loot.RigidBody.useGravity = false;
            loot.RigidBody.WakeUp(); return loot;
        }
        HeadgearPhysicsLifetimePatch.Install();
        try
        {
            var four = new LootItem[4];
            for (int i = 0; i < four.Length; i++)
            {
                four[i] = Create(i % 2 != 0); HeadgearPhysicsLifetime.Track(four[i]);
                PhysicsExtensions.UpdateController.Support(four[i].RigidBody);
                PhysicsExtensions.UpdateController.Support(four[i].RigidBody);
                four[i].RigidBody.Sleep();
            }
            Check(PhysicsExtensions.UpdateController.UpdateEnabled, "sleeping registered headwear still opens the native simulation gate");
            foreach (var loot in four)
            {
                Check(loot.IsRigidbodyDone(), "both native and observed headwear recognize sleep through the installed patches");
                loot.StopPhysics();
                Check(loot.RigidBody == null && loot.GetComponent<Rigidbody>() == null && loot.Stopped == 1 &&
                    loot.Item != null && loot.GetComponent<Collider>().enabled,
                    "native stop removes the rigidbody, calls finalization once and retains the loot collider/item");
            }
            Check(!PhysicsExtensions.UpdateController.UpdateEnabled && PhysicsExtensions.UpdateController._rigidbodies.Count == 0,
                "all four headwear items and duplicate registrations release the simulation gate");

            var ordinary = Create(true); ordinary.RigidBody.Sleep();
            Check(!ordinary.IsRigidbodyDone(), "ordinary observed loot retains its native settle decision");
            var moving = Create(); HeadgearPhysicsLifetime.Track(moving);
            float now = Time.time + 1f;
            moving.RigidBody.velocity = Vector3.right;
            Check(!HeadgearPhysicsLifetime.IsDone(moving, false, now), "moving headwear is not frozen");
            moving.RigidBody.velocity = Vector3.zero; moving.RigidBody.angularVelocity = Vector3.up;
            Check(!HeadgearPhysicsLifetime.IsDone(moving, false, now + .2f), "spinning headwear is not frozen");
            moving.RigidBody.angularVelocity = Vector3.zero; moving.RigidBody.WakeUp();
            Check(!HeadgearPhysicsLifetime.IsDone(moving, false, now + .4f), "momentary rest starts a dwell instead of freezing at an apex");
            moving.RigidBody.position += Vector3.right;
            Check(!HeadgearPhysicsLifetime.IsDone(moving, false, now + .6f), "network pose movement resets the quiet dwell");
            Check(!HeadgearPhysicsLifetime.IsDone(moving, false, now + .8f) &&
                !HeadgearPhysicsLifetime.IsDone(moving, false, now + 1.1f) &&
                HeadgearPhysicsLifetime.IsDone(moving, false, now + 1.4f), "awake stable headwear settles after a sustained half second");
            moving.RigidBody.isKinematic = true;
            Check(!HeadgearPhysicsLifetime.IsDone(moving, false, now + 2f), "kinematic network proxy does not independently declare settlement");
            Check(HeadgearPhysicsLifetime.IsDone(moving, true, now + 2f), "native terminal decisions are preserved");

            var pooled = Create(true); HeadgearPhysicsLifetime.Track(pooled); pooled.Item = new EFT.InventoryLogic.Item(); pooled.RigidBody.Sleep();
            Check(!pooled.IsRigidbodyDone(), "reused loot with a new item does not inherit headwear cleanup");
            var replaced = Create(true); HeadgearPhysicsLifetime.Track(replaced);
            UnityEngine.Object.DestroyImmediate(replaced.RigidBody);
            replaced.RigidBody = replaced.gameObject.AddComponent<Rigidbody>(); replaced.RigidBody.Sleep();
            Check(!replaced.IsRigidbodyDone(), "replacement rigidbody does not inherit a prior throw");
            var reset = Create(true); HeadgearPhysicsLifetime.Track(reset); HeadgearPhysicsLifetime.Reset(); reset.RigidBody.Sleep();
            Check(!reset.IsRigidbodyDone(), "raid reset drops pending lifecycle ownership");
            Debug.Log("PASS: headgear settle lifecycle (" + checks + " checks)");
            return checks;
        }
        finally
        {
            HeadgearPhysicsLifetimePatch.Shutdown(); HeadgearPhysicsLifetime.Reset();
            PhysicsExtensions.UpdateController._rigidbodies.Clear(); PhysicsExtensions.UpdateController.UpdateEnabled = false;
            foreach (var root in roots) UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
#endif
