#if UNITY_EDITOR
// Only EFT/Fika boundaries are doubled. Selection, descriptor, geometry, tracker,
// Unity skinning, rendering and native LOD registration run the production code.
using System;
using System.Collections.Generic;
using System.Reflection;
using Rupture.Dismemberment.Classes.Fractures;
using Rupture.Dismemberment.Classes.Limbs;
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
namespace Diz.Skinning
{
    public class AbstractSkin { public SkinnedMeshRenderer SkinnedMeshRenderer; }
}
namespace EFT
{
    public enum EBodyModelPart { Body, Feet, Hands }
    // Mirrors LoddedSkin._lods: production walks the LOD skins directly instead of GetRenderers().
    public sealed class FixtureSkin
    {
        public readonly List<Renderer> Renderers = new List<Renderer>();
        public Diz.Skinning.AbstractSkin[] _lods
        {
            get
            {
                var lods = new Diz.Skinning.AbstractSkin[Renderers.Count];
                for (int i = 0; i < lods.Length; i++) lods[i] = new Diz.Skinning.AbstractSkin { SkinnedMeshRenderer = Renderers[i] as SkinnedMeshRenderer };
                return lods;
            }
        }
    }
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
/// <summary>
/// Reproduces what a raid does before its first fracture, so the fixture's first accepted case measures only what
/// is left. <c>RPT_FRACTURE_FIRST_USE</c> selects: <c>cold</c> (nothing), <c>prejit-old</c> (the four fracture types
/// that were pre-JIT before the warmup existed), <c>prejit</c> (the current RaidWarmup pre-JIT list) or
/// <c>warm</c> (that list plus the production <see cref="FractureWarmup"/>, the default).
/// </summary>
internal static class FractureFirstUseSetup
{
    internal static void Prepare(Material material)
    {
        string mode = Environment.GetEnvironmentVariable("RPT_FRACTURE_FIRST_USE") ?? "warm";
        UnityEngine.Debug.Log("Fracture first-use mode: " + mode);
        if (mode == "cold") return;
        var types = new List<Type> { typeof(FractureSystem), typeof(FractureSurface), typeof(FractureVisual), typeof(FractureAppearance) };
        if (mode != "prejit-old") { types.Add(typeof(ProceduralLimbWoundGeometry)); types.Add(typeof(LimbSeverMath)); }
        long mark = System.Diagnostics.Stopwatch.GetTimestamp();
        int methods = 0;
        foreach (Type type in types) methods += PreJit(type);
        UnityEngine.Debug.Log("Fracture first-use pre-JIT: methods=" + methods + ", ms=" + Lap(ref mark));
        if (mode != "warm") return;
        for (int stage = 0; ; stage++)
        {
            bool done = FractureWarmup.Step(material);
            UnityEngine.Debug.Log("Fracture first-use warmup stage " + stage + ": ms=" + Lap(ref mark));
            if (done) break;
        }
        if (!FractureWarmup.Warmed) throw new Exception("fracture warmup did not complete");
    }

    // Same member selection as RaidWarmup.PreJit: every constructor and method with an IL body, nested types included.
    private static int PreJit(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (type.ContainsGenericParameters) return 0;
        int count = 0;
        var methods = new List<MethodBase>();
        methods.AddRange(type.GetConstructors(flags));
        methods.AddRange(type.GetMethods(flags));
        foreach (MethodBase method in methods)
        {
            if (method.IsAbstract || method.ContainsGenericParameters || (method.Attributes & MethodAttributes.PinvokeImpl) != 0) continue;
            MethodImplAttributes impl = method.GetMethodImplementationFlags();
            if ((impl & MethodImplAttributes.InternalCall) != 0 || (impl & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL) continue;
            method.MethodHandle.GetFunctionPointer();
            count++;
        }
        foreach (Type nested in type.GetNestedTypes(flags)) count += PreJit(nested);
        return count;
    }

    private static double Lap(ref long mark)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double milliseconds = (now - mark) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        mark = now;
        return Math.Round(milliseconds, 3);
    }
}
#endif
