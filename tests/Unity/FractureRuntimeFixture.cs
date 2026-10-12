#if UNITY_EDITOR
// Only EFT/Fika boundaries are doubled. Selection, descriptor, geometry, tracker,
// Unity skinning, rendering and native LOD registration run the production code.
using System;
using System.Collections.Generic;
using UnityEngine;
public enum EBodyPart { Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg, Common }
namespace EFT.HealthSystem
{
public interface IHealthEffect { bool Active { get; } EBodyPart BodyPart { get; } }
public interface IFracture : IHealthEffect { }
}
public sealed class FixtureFracture : EFT.HealthSystem.IFracture { public bool Active { get; set; } = true; public EBodyPart BodyPart { get; set; } }
namespace EFT.HealthSystem
{
    public interface IHealthController
    {
        bool IsAlive { get; }
        bool IsBodyPartBroken(EBodyPart part);
        event Action<EFT.HealthSystem.IHealthEffect> EffectAddedEvent, EffectStartedEvent, EffectUpdatedEvent, EffectResidualEvent, EffectRemovedEvent;
    }
    public class ActiveHealthController : IHealthController
    {
        public bool IsAlive { get; set; } = true;
        public List<EFT.HealthSystem.IHealthEffect> Effects = new List<EFT.HealthSystem.IHealthEffect>();
        public event Action<EFT.HealthSystem.IHealthEffect> EffectAddedEvent, EffectStartedEvent, EffectUpdatedEvent, EffectResidualEvent, EffectRemovedEvent;
        public bool IsBodyPartBroken(EBodyPart part) { foreach (var effect in Effects) if (effect is EFT.HealthSystem.IFracture && effect.Active && effect.BodyPart == part) return true; return false; }
        public void Add(EFT.HealthSystem.IHealthEffect effect) { Effects.Add(effect); EffectAddedEvent?.Invoke(effect); EffectStartedEvent?.Invoke(effect); }
        public void Remove(EFT.HealthSystem.IHealthEffect effect) { Effects.Remove(effect); EffectRemovedEvent?.Invoke(effect); }
        public void Changed(EFT.HealthSystem.IHealthEffect effect) { EffectUpdatedEvent?.Invoke(effect); EffectResidualEvent?.Invoke(effect); }
        public int Subscriptions => (EffectAddedEvent?.GetInvocationList().Length ?? 0) + (EffectStartedEvent?.GetInvocationList().Length ?? 0) +
            (EffectUpdatedEvent?.GetInvocationList().Length ?? 0) + (EffectResidualEvent?.GetInvocationList().Length ?? 0) + (EffectRemovedEvent?.GetInvocationList().Length ?? 0);
    }
}
namespace EFT.HealthSystem
{
public class OfflineHealthController : ActiveHealthController { }
public class NetworkHealthController : ActiveHealthController { }
}
namespace EFT
{
    public enum EBodyModelPart { Body, Feet, Hands }
    public sealed class FixtureSkin { public readonly List<Renderer> Renderers = new List<Renderer>(); public IEnumerable<Renderer> GetRenderers() => Renderers; }
    public sealed class FixtureBody { public readonly Dictionary<EBodyModelPart, FixtureSkin> BodySkins = new Dictionary<EBodyModelPart, FixtureSkin>(); }
    public sealed class Player : MonoBehaviour
    {
        public string ProfileId = "fixture-player";
        public EFT.HealthSystem.IHealthController HealthController = new EFT.HealthSystem.ActiveHealthController();
        public FixtureBody PlayerBody = new FixtureBody();
        public Transform SegmentStart, SegmentEnd;
    }
    public sealed class GameWorld { public readonly List<Player> AllAlivePlayersList = new List<Player>(); }
}
namespace Comfort.Common { public static class Singleton<T> where T : class { public static T Instance; public static bool Instantiated => Instance != null; } }
namespace Rupture
{
    public sealed class FixtureConfig { public bool Value = true; }
    public static class RuptureConfiguration { public static FixtureConfig EnableBloodEffects = new FixtureConfig(); }
    public static class MultiplayerRuntime { public static FixtureRuntime Current = new FixtureRuntime(); }
    public sealed class FixtureRuntime { public bool IsHeadless; }
    public static class RupturePlugin { public static readonly FixtureLog Log = new FixtureLog(); }
    public sealed class FixtureLog { public void LogWarning(string text) => Debug.LogWarning(text); }
}
namespace Rupture.Dismemberment.Classes.Limbs
{
    internal static class LimbDismembermentSystem
    {
        internal static bool TryGetLimbSegment(EFT.Player player, EBodyPart part, LimbSeverLevel level, out Transform start, out Transform end)
        { start = player.SegmentStart; end = player.SegmentEnd; return start != null && end != null; }
    }
    internal static class LimbSeverMaterialProvider
    {
        internal static Material Material;
        internal static bool TryGet(out Material material, out string failure) { material = Material; failure = "fixture material unavailable"; return material != null; }
        internal static void ApplyVariation(Renderer renderer, int index, uint seed, bool gib) { }
    }
}
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    // The blood renderer is covered by its own production regressions. Here the
    // boundary double records logical fracture supply across camera/LOD changes.
    internal static class PulsatingBloodStreamSystem
    {
        internal static readonly Dictionary<uint, Transform> Active = new Dictionary<uint, Transform>();
        internal static int Starts;
        internal static uint StartFractureWound(EFT.Player player, EBodyPart part, Transform anchor,
            Vector3 point, Vector3 direction, uint seed)
        { uint id = seed == 0 ? 1u : seed; Active[id] = anchor; Starts++; return id; }
        internal static bool UpdateFractureWound(uint id, Transform anchor, Vector3 point, Vector3 direction)
        { if (!Active.ContainsKey(id)) return false; Active[id] = anchor; return true; }
        internal static void StopFractureWound(uint id) { Active.Remove(id); }
    }
}
#endif
