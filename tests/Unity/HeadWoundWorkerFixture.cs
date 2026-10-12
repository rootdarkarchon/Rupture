#if UNITY_EDITOR
// Only EFT/Fika/effect boundaries are doubled. Production capture, worker,
// geometry, ordering, Unity upload, skinning and lifetime code run unchanged.
using System;
using System.Collections.Generic;
using UnityEngine;
using Rupture.Dismemberment.Classes.Packets;
namespace Diz.Skinning { }
namespace HarmonyLib { }
namespace Rupture.Ragdolls.Patches { }
public enum EBodyModelPart { Head }
namespace EFT.InventoryLogic
{
    public class Item { public string TemplateId; }
    public class Ammo : Item { public string Caliber; }
}
namespace EFT.Ballistics
{
    public class DamageInfo { public Vector3 HitPoint, Direction; public int FireIndex; public string SourceId; public Collider HitCollider; public float PenetrationPower; }
    public class Shot { public int FireIndex; public Collider HitCollider; public Vector3 HitPoint; public EFT.InventoryLogic.Item Ammo; public float BulletDiameterMilimeters; }
}
namespace EFT
{
    public class FixtureBone { public Transform Original; }
    public class FixtureBones { public FixtureBone Head = new FixtureBone(); }
    public class FixtureSkin { public Renderer[] Renderers; public IEnumerable<Renderer> GetRenderers() => Renderers; }
    public class PlayerBody { public void GetBodyRenderersNonAlloc(List<Systems.Effects.BodyRenderer> output) { } public Dictionary<EBodyModelPart, FixtureSkin> BodySkins = new Dictionary<EBodyModelPart, FixtureSkin>(); }
    public class Player : MonoBehaviour
    {
        public string ProfileId;
        public PlayerBody PlayerBody;
        public FixtureBones PlayerBones;
        public void GetBodyRenderersNonAlloc(List<Systems.Effects.BodyRenderer> output) { }
    }
}
namespace Comfort.Common { public static class Singleton<T> where T : class { public static T Instance; public static bool Instantiated => Instance != null; } }
namespace Systems.Effects
{
    public enum EDecalTextureType { Blood }
    public class BodyRenderer { public EDecalTextureType DecalType; public Renderer[] Renderers; }
    public class Effects { public object TexDecals; public bool UseDecalPainter; public void PlayerMeshesHit(List<BodyRenderer> renderers, Vector3 point, Vector3 direction) { } }
}
namespace Rupture
{
    public class FixtureConfig { public bool Value; }
    public static class RuptureConfiguration { public static FixtureConfig EnableDismemberment = new FixtureConfig(), EnableBloodEffects = new FixtureConfig(), AutomaticHeadgearDetachment = new FixtureConfig(); }
    public static class RupturePlugin { public static FixtureLog Log = new FixtureLog(); }
    public class FixtureLog { public bool IsDebugEnabled; public void LogInfo(string s) { } public void LogWarning(string s) { } }
    public static class MultiplayerRuntime { public static FixtureRuntime Current = new FixtureRuntime(); }
    public class FixtureRuntime { public bool IsHeadless, HasServer; public void Send(ref HeadWoundData data) { } }
}
namespace Rupture.Impacts
{
    public class ImpactData
    {
        public bool Valid = true; public ulong Projectile; public int FireIndex; public float Momentum, MassGrams, Speed, Diameter, PenetrationPower; public string Caliber;
        public static ulong ProjectileIdentity(EFT.Ballistics.Shot shot) => 1;
    }
    public static class ImpactStateService { public static ImpactData Current = new ImpactData(); }
    public static class LimbImpactPolicy { public static float Momentum(float mass, float velocity) => mass * velocity; }
}
namespace Rupture.Ragdolls.Classes
{
    public class CorpseInteractionTarget
    {
        public string ProfileId; public int PlayerId; public uint DeathSequence;
        public EFT.Player Player; public EFT.PlayerBody PlayerBody; public Transform VisualRoot; public CorpseHeadContext Head;
    }
    public class CorpseHeadContext
    {
        public Transform Bone;
        public static CorpseHeadContext Capture(EFT.Player p, EFT.PlayerBody b) => null;
        public void BindIdentity(string profile, int player, uint death) { }
    }
    public static class RagdollCoordinator
    {
        public static bool IsNetworkAuthority, IsRemoteClient;
        public static uint NextNetworkEventSequence() => 1;
        public static bool TryGetInteractionTarget(EFT.Player p, out CorpseInteractionTarget t) { t = null; return false; }
        public static bool TryGetInteractionTarget(string p, int id, out CorpseInteractionTarget t) { t = null; return false; }
    }
    public static class LethalHeadgearDetach { public static void Queue(CorpseHeadContext h, float m, Vector3 d, uint s) { } }
}
namespace Rupture.Dismemberment.Classes.Limbs
{
    public static class LimbDecalTextureTransfer
    {
        public static bool CanAcceptNativeDecal(Renderer r) => false;
        public static void CopyExisting(Renderer source, Renderer dest, out int count) { count = 0; }
    }
}
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    public static class PulsatingBloodStreamSystem
    {
        public static void RejectHeadExit(string p, uint d) { }
        public static Vector3 LastDirection;
        public static bool LastTerminal;
        public static void ResolveHeadExit(HeadWoundData p, Transform t, Vector3 e, Vector3 d, bool terminal, Vector3 entry) { LastDirection = d; LastTerminal = terminal; }
        public static void StartPostMortemHeadWound(Rupture.Ragdolls.Classes.CorpseInteractionTarget t, HeadWoundData p, Transform h, Vector3 e, Vector3 x, Vector3 d, bool terminal) { LastDirection = d; LastTerminal = terminal; }
    }
}
namespace Rupture.Dismemberment.Classes.HeadWounds
{
    internal static class HeadWoundMaterialProvider
    {
        internal static Material Material;
        internal static bool TryGet(out Material material, out string failure) { material = Material; failure = null; return material != null; }
        internal static void Reset() { }
    }
}
#endif
