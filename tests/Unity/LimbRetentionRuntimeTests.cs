using System;
using UnityEngine;
using Rupture.Ragdolls.Classes;

// Minimal EFT ownership fixture. Real Unity Transform lifetime and the unmodified
// production context are exercised; the managed build checks the installed EFT ABI.
public enum EBodyPart { Common, Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg }
public sealed class PlayerBones
{
    public Transform[] Upperarms, Forearms;
    public Transform LeftPalm, RightPalm;
    public FixtureBifacial LeftThigh1, RightThigh1, Head;
}
public sealed class FixtureBifacial { public Transform Original; }
namespace EFT
{
    public sealed class Player : MonoBehaviour
    {
        public PlayerBody PlayerBody;
        public PlayerBones PlayerBones;
        public FixtureInventory Inventory;
        public FixtureController InventoryController;
    }
    public sealed class PlayerBody : MonoBehaviour
    {
        public Transform SkeletonRootJoint;
        public PlayerBones PlayerBones;
    }
}

public struct BodyRenderer { }
public sealed class FixtureInventory { public FixtureEquipment Equipment; }
public sealed class FixtureEquipment
{
    public readonly FixtureSlot Headwear = new FixtureSlot();
    public FixtureSlot GetSlot(EFT.InventoryLogic.EquipmentSlot slot) => Headwear;
}
public sealed class FixtureSlot { public EFT.InventoryLogic.Item ContainedItem; }
public sealed class FixtureController : EFT.InventoryLogic.InventoryController { }
namespace Comfort.Common { public delegate void Callback(bool result); }
namespace EFT.InventoryLogic
{
    public enum EquipmentSlot { Headwear }
    public class Item { public FixtureAddress Parent; public FixtureAddress CurrentAddress => Parent; }
    public class FixtureAddress { public object Container; }
    public class InventoryController
    {
        public FixtureInventory Inventory;
        public void ThrowItem(Item item, bool downDirection, Comfort.Common.Callback callback) { callback?.Invoke(true); }
    }
}
namespace Rupture.Dismemberment.Classes.HeadWounds
{
    internal static class HeadWoundSystem
    {
        internal static System.Collections.Generic.List<BodyRenderer> CaptureDecalRenderers(EFT.Player player) => null;
        internal static void Remove(string profile, int player, uint sequence) { }
    }
}
namespace Rupture.Ragdolls.Classes
{
    internal static class LethalHeadgearDetach { internal static void Remove(CorpseHeadContext context) { } }
    internal static class HeadgearDiagnostics
    {
        internal static readonly EFT.InventoryLogic.EquipmentSlot[] Slots = { EFT.InventoryLogic.EquipmentSlot.Headwear };
        internal static void ObserveEquipment(EFT.Player player, string stage) { }
    }
}

internal static class LimbRetentionRuntimeTests
{
    internal static void Run(Action<bool, string> check)
    {
        var owner = new GameObject("temporary player");
        var corpse = new GameObject("retained corpse skeleton");
        var player = owner.AddComponent<EFT.Player>();
        var body = corpse.AddComponent<EFT.PlayerBody>();
        body.SkeletonRootJoint = corpse.transform;
        player.PlayerBody = body;
        string[] names = { "Base HumanLUpperarm", "Base HumanLForearm1", "Base HumanLPalm",
            "Base HumanRUpperarm", "Base HumanRForearm1", "Base HumanRPalm",
            "Base HumanLThigh1", "Base HumanLCalf", "Base HumanLFoot",
            "Base HumanRThigh1", "Base HumanRCalf", "Base HumanRFoot" };
        var transforms = new Transform[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            transforms[i] = new GameObject(names[i]).transform;
            transforms[i].SetParent(corpse.transform);
            transforms[i].localPosition = new Vector3(i % 3 * .3f, i / 3, 0);
        }
        var retained = CorpseLimbContext.Capture(player);
        player.PlayerBody = null;
        UnityEngine.Object.DestroyImmediate(owner);
        foreach (var part in new[] { EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg })
        {
            check(retained.TryGet(part, out var start, out var joint, out var end), "retained limb resolves after player destruction: " + part);
            check(Vector3.Distance(start.position, joint.position) > .1f && Vector3.Distance(joint.position, end.position) > .1f,
                "retained cut segment remains nondegenerate: " + part);
        }
        var rebound = CorpseLimbContext.Capture(null, body);
        check(rebound.TryGet(EBodyPart.RightLeg, out _, out _, out _), "late corpse binding resolves skeleton with no Player or PlayerBones");
        check(CorpseHeadContext.Capture(null, body) == null, "early corpse head capture can wait for skeleton availability");
        var head = new GameObject("Base HumanHead").transform;
        head.SetParent(corpse.transform);
        head.gameObject.SetActive(false);
        var retainedHead = CorpseHeadContext.Capture(null, body);
        check(retainedHead?.Bone == head, "late inactive corpse head resolves without the retired Player or PlayerBones");
        check(ReferenceEquals(retainedHead, CorpseHeadContext.Capture(null, body)), "reacquiring a head reuses its retained ownership context");
        var reboundOwner = new GameObject("late head inventory owner");
        var reboundPlayer = reboundOwner.AddComponent<EFT.Player>();
        reboundPlayer.PlayerBody = body;
        var equipment = new FixtureEquipment();
        var helmet = new EFT.InventoryLogic.Item { Parent = new EFT.InventoryLogic.FixtureAddress { Container = equipment.Headwear } };
        equipment.Headwear.ContainedItem = helmet;
        reboundPlayer.InventoryController = new FixtureController { Inventory = new FixtureInventory { Equipment = equipment } };
        check(ReferenceEquals(retainedHead, CorpseHeadContext.Capture(reboundPlayer, body)) && retainedHead.HasCurrentAddress(helmet),
            "late player capture repairs missing inventory bindings on the retained head");
        var replacement = new FixtureEquipment();
        var newHelmet = new EFT.InventoryLogic.Item { Parent = new EFT.InventoryLogic.FixtureAddress { Container = replacement.Headwear } };
        replacement.Headwear.ContainedItem = newHelmet;
        reboundPlayer.InventoryController.Inventory = new FixtureInventory { Equipment = replacement };
        check(ReferenceEquals(retainedHead.Equipped(EFT.InventoryLogic.EquipmentSlot.Headwear), newHelmet) && !retainedHead.HasCurrentAddress(helmet),
            "inventory replacement invalidates queued old items and resolves current equipment");
        newHelmet.Parent.Container = equipment.Headwear;
        check(!retainedHead.HasCurrentAddress(newHelmet), "slot membership with a mismatched item address fails validation");
        UnityEngine.Object.DestroyImmediate(reboundOwner);
        retainedHead.BindIdentity("fixture", 7, 4);
        check(head.GetComponent<CorpseHeadRetention>().DeathSequence == 4, "recovered head retention receives the corpse generation");
        UnityEngine.Object.DestroyImmediate(head.gameObject);
        check(retainedHead.Bone == null && CorpseHeadContext.Capture(null, body) == null, "destroyed head is not returned as a usable target");
        UnityEngine.Object.DestroyImmediate(transforms[7].gameObject);
        check(!retained.TryGet(EBodyPart.LeftLeg, out _, out _, out _), "destroyed retained segment rejects without resurrecting stale anatomy");
        check(retained.TryGet(EBodyPart.RightLeg, out _, out _, out _), "missing left segment leaves right anatomy usable");
        UnityEngine.Object.DestroyImmediate(corpse);
        check(!retained.TryGet(EBodyPart.RightLeg, out _, out _, out _), "corpse destruction invalidates retained Unity transforms");
    }
}
