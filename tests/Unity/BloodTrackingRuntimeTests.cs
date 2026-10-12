#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using EFT;
using EFT.HealthSystem;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Ragdolls.Classes;

public enum EBodyPart { Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg, Common }
public enum EBodyPartColliderType { Body }
namespace EFT.Ballistics { public struct DamageInfo { public Collider HitCollider; public Vector3 HitPoint; } }
namespace EFT.HealthSystem
{
    public interface IBleeding : IHealthEffect { }
    public interface IHeavyBleeding : IBleeding { }
    public interface ILightBleeding : IBleeding { }
    public interface IHealthEffect { EBodyPart BodyPart { get; } bool Active { get; } }
    public interface IHealthController
    {
        bool IsAlive { get; }
        event Action<EFT.HealthSystem.IHealthEffect> EffectResidualEvent, EffectRemovedEvent, EffectUpdatedEvent, EffectAddedEvent, EffectStartedEvent;
        T FindActiveEffect<T>(EBodyPart part) where T : class, EFT.HealthSystem.IHealthEffect;
    }
    public class FixtureHealth : IHealthController
    {
        public bool IsAlive { get; set; } = true;
        public readonly List<EFT.HealthSystem.IHealthEffect> Effects = new List<EFT.HealthSystem.IHealthEffect>();
        public int FallbackQueries;
        public event Action<EFT.HealthSystem.IHealthEffect> EffectResidualEvent, EffectRemovedEvent, EffectUpdatedEvent, EffectAddedEvent, EffectStartedEvent;
        public T FindActiveEffect<T>(EBodyPart part) where T : class, EFT.HealthSystem.IHealthEffect
        { FallbackQueries++; foreach(var e in Effects)if(e.BodyPart==part&&e.Active&&e is T typed)return typed;return null; }
        public void Changed(EFT.HealthSystem.IHealthEffect e) => EffectUpdatedEvent?.Invoke(e);
        public void Removed(EFT.HealthSystem.IHealthEffect e) { Effects.Remove(e); EffectRemovedEvent?.Invoke(e); }
        public void Residual(EFT.HealthSystem.IHealthEffect e) => EffectResidualEvent?.Invoke(e);
        public void Added(EFT.HealthSystem.IHealthEffect e) { Effects.Add(e); EffectAddedEvent?.Invoke(e); }
        public void Started(EFT.HealthSystem.IHealthEffect e) => EffectStartedEvent?.Invoke(e);
    }
    public sealed class ActiveHealthController : FixtureHealth { }
    public sealed class OfflineHealthController : FixtureHealth { }
    public sealed class NetworkHealthController : FixtureHealth { }
}
namespace EFT
{
    public sealed class GameWorld { public readonly List<Player> AllAlivePlayersList = new List<Player>(); }
    public sealed class Player { public int Id; public string ProfileId; public bool IsYourPlayer = true; public bool Downed, TerminalDeath; public IHealthController HealthController; public readonly Bones PlayerBones = new Bones(); public readonly Pose Transform = new Pose(); }
    public sealed class Pose { public Transform Original; }
    public sealed class Bones { public readonly List<BodyCollider> BodyPartColliders = new List<BodyCollider>(); }
    public sealed class BodyCollider { public EBodyPart BodyPartType; public EBodyPartColliderType BodyPartColliderType; public Collider Collider; }
}
namespace Comfort.Common { public static class Singleton<T> where T : class { public static T Instance; public static bool Instantiated => Instance != null; } }
namespace Rupture
{
    internal static class MultiplayerRuntime
    {
        internal static readonly Runtime Current = new Runtime();
        internal sealed class Runtime { internal bool IsDowned(Player player) => player.Downed; }
    }
    public sealed class Flag { public bool Value = true; }
    public static class RuptureConfiguration { public static Flag EnableBloodEffects = new Flag(); }
    public static class RupturePlugin { public static readonly Sink Log = new Sink(); public sealed class Sink { public void LogInfo(string message) { } public void LogWarning(string message) { } } }
}
namespace Rupture.Impacts { internal static class ImpactStateService { internal static bool IsTerminalDeath(Player player) => player.TerminalDeath; } }
namespace Rupture.Ragdolls.Classes { internal static class RagdollCoordinator { internal static bool IsRemoteClient; } }
namespace Rupture.Dismemberment.Classes.Packets { public sealed class NamespaceMarker { } }
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    internal static partial class PulsatingBloodStreamSystem
    {
        internal static int FixtureStops;
        internal static readonly Dictionary<(Player,EBodyPart),BloodStreamTriggerKind> FixtureStreams = new Dictionary<(Player,EBodyPart),BloodStreamTriggerKind>();
        internal static Transform ResolveDamageAnchor(Player player,Collider collider,EBodyPartColliderType type,EBodyPart part,out EBodyPartColliderType resolved) { resolved=type;return player.Transform.Original; }
        internal static Vector3 ResolveEntryDirection(EFT.Ballistics.DamageInfo damage) => Vector3.right;
        internal static bool HasLivingWound(Player player,EBodyPart part,BloodStreamTriggerKind kind) => FixtureStreams.TryGetValue((player,part),out var value)&&value==kind;
        internal static void PublishLivingWound(Player player,EBodyPart part,EBodyPartColliderType type,Vector3 point,Vector3 direction,BloodStreamTriggerKind kind,uint generation) => FixtureStreams[(player,part)]=kind;
        internal static void StopLivingWound(Player player,EBodyPart part) { FixtureStops++;FixtureStreams.Remove((player,part)); }
        internal static void StopLivingStreams(Player player,bool publish) { for(int i=0;i<7;i++)FixtureStreams.Remove((player,(EBodyPart)i)); }
    }
    internal static class BloodTrackingRuntimeTests
    {
        private class Effect : EFT.HealthSystem.IHealthEffect { public EBodyPart BodyPart { get; set; } public bool Active { get; set; } = true; }
        private sealed class Heavy : Effect,EFT.HealthSystem.IHeavyBleeding { }
        private sealed class Light : Effect,EFT.HealthSystem.ILightBleeding { }
        private static void Check(bool value,string name) { if(!value)throw new Exception(name); }
        internal static void Run()
        {
            var root=new GameObject("health fixture");var world=new GameWorld();Comfort.Common.Singleton<GameWorld>.Instance=world;
            var health=new ActiveHealthController();var player=new Player {HealthController=health};player.Transform.Original=root.transform;world.AllAlivePlayersList.Add(player);
            try
            {
                for(int i=0;i<7;i++) {health.Effects.Add(new Light {BodyPart=(EBodyPart)i});health.Effects.Add(new Heavy {BodyPart=(EBodyPart)i});health.Effects.Add(new Heavy {BodyPart=(EBodyPart)i});}
                health.Effects.Add(new Heavy {BodyPart=EBodyPart.Common});
                ShadowRagdollWorld.ResetWork();BleedingWoundTracker.Update();
                Check(ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodEffectScans)==1&&ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodEffectsVisited)==22,"single scan covers all body parts");
                for(int i=0;i<7;i++)Check(ReferenceEquals(BleedingWoundTracker.TrackedActiveEffect(player,(EBodyPart)i,BloodStreamTriggerKind.HeavyWound),health.Effects[i*3+1]),"first heavy match is retained");
                for(int i=0;i<100;i++)BleedingWoundTracker.Update();
                Check(ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodEffectScans)==1,"unchanged native list is not rescanned every frame");
                var paused=(Heavy)health.Effects[4]; paused.Active=false; BleedingWoundTracker.Update();
                Check(!ReferenceEquals(BleedingWoundTracker.TrackedActiveEffect(player,EBodyPart.Chest,BloodStreamTriggerKind.HeavyWound),paused),"silent pause is observed in the same update");
                paused.Active=true; BleedingWoundTracker.Update();
                Check(ReferenceEquals(BleedingWoundTracker.TrackedActiveEffect(player,EBodyPart.Chest,BloodStreamTriggerKind.HeavyWound),paused),"silent resume retains first-match priority");
                var first=(Heavy)health.Effects[1];first.Active=false;health.Changed(first);
                Check(!ReferenceEquals(BleedingWoundTracker.TrackedActiveEffect(player,EBodyPart.Head,BloodStreamTriggerKind.HeavyWound),first),"same-frame pause invalidates cached effects");
                var second=health.Effects[2];health.Removed(second);BleedingWoundTracker.Update();
                Check(PulsatingBloodStreamSystem.FixtureStreams[(player,EBodyPart.Head)]==BloodStreamTriggerKind.LightWound,"heavy removal reveals existing light effect");
                var light=(Light)health.Effects[0];light.Active=false;health.Residual(light);
                Check(!PulsatingBloodStreamSystem.FixtureStreams.ContainsKey((player,EBodyPart.Head)),"native stop event immediately stops supply");
                var added=new Heavy {BodyPart=EBodyPart.Head};health.Added(added);BleedingWoundTracker.Update();
                Check(ReferenceEquals(BleedingWoundTracker.TrackedActiveEffect(player,EBodyPart.Head,BloodStreamTriggerKind.HeavyWound),added),"added bleed invalidates membership immediately");
                Rupture.RuptureConfiguration.EnableBloodEffects.Value=false;BleedingWoundTracker.Update();
                Check(PulsatingBloodStreamSystem.FixtureStreams.Count==0,"configuration disable stops every stream");
                Rupture.RuptureConfiguration.EnableBloodEffects.Value=true;BleedingWoundTracker.Update();
                Check(PulsatingBloodStreamSystem.FixtureStreams.Count==7,"re-enable republishes active wounds");
                player.Downed=true;health.IsAlive=false;BleedingWoundTracker.Update();
                Check(PulsatingBloodStreamSystem.FixtureStreams.Count==0,"downing pauses all living supply");
                // Change membership without an event while downed; revival must refresh it.
                health.Effects.Clear();var revived=new Light {BodyPart=EBodyPart.Chest};health.Effects.Add(revived);
                player.Downed=false;health.IsAlive=true;BleedingWoundTracker.Update();
                Check(PulsatingBloodStreamSystem.FixtureStreams.Count==1&&PulsatingBloodStreamSystem.FixtureStreams[(player,EBodyPart.Chest)]==BloodStreamTriggerKind.LightWound,
                    "revival refreshes native effects without resurrecting removed bleeds");
                player.Downed=true;health.IsAlive=false;BleedingWoundTracker.Update();
                player.TerminalDeath=true;BleedingWoundTracker.Update();
                Check(PulsatingBloodStreamSystem.FixtureStreams.Count==0,"bleedout retires living supply despite lingering Downed");
                player.Downed=false;player.TerminalDeath=false;
                var unknown=new FixtureHealth();unknown.Effects.Add(new Light {BodyPart=EBodyPart.Chest});
                player.HealthController=unknown;BleedingWoundTracker.Update();BleedingWoundTracker.Capture(player,default,EBodyPart.Chest,default);BleedingWoundTracker.Update();
                Check(unknown.FallbackQueries>0&&PulsatingBloodStreamSystem.FixtureStreams[(player,EBodyPart.Chest)]==BloodStreamTriggerKind.LightWound,"replacement unknown controller retains native query fallback");
                unknown.IsAlive=false;BleedingWoundTracker.Update();Check(PulsatingBloodStreamSystem.FixtureStreams.Count==0,"death retires all living streams");
                // All three supported controller families expose the matched list.
                Check(Rupture.Health.NativeHealthEffects.TryGet(new EFT.HealthSystem.OfflineHealthController())!=null&&Rupture.Health.NativeHealthEffects.TryGet(new EFT.HealthSystem.NetworkHealthController())!=null,"observed/network controllers keep their native list path");
            }
            finally {BleedingWoundTracker.Reset();PulsatingBloodStreamSystem.FixtureStreams.Clear();Comfort.Common.Singleton<GameWorld>.Instance=null;UnityEngine.Object.DestroyImmediate(root);}
            Benchmark();
        }
        [Serializable] private sealed class TrackingResult { public string backend; public int actors=32, effectsPerActor=63; public double meanMs; public long allocatedBytes,effectScans,effectsVisited; }
        [Serializable] private sealed class TrackingReport { public TrackingResult[] cases; public string limitation="Production tracking, synthetic health controllers and a constant-time acknowledgment fixture; excludes the separate living-stream index gain."; }
        private static void Benchmark()
        {
            var results=new List<TrackingResult>();
            foreach(string name in new[]{"BleedingWoundTracker","BaselineBleedingWoundTracker"})
            {
                var type=typeof(BloodTrackingRuntimeTests).Assembly.GetType("Rupture.Dismemberment.Classes.BloodStreams."+name);if(type==null)continue;
                const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
                var update=(Action)Delegate.CreateDelegate(typeof(Action),type.GetMethod("Update",flags));
                var reset=(Action)Delegate.CreateDelegate(typeof(Action),type.GetMethod("Reset",flags));
                var root=new GameObject("tracking benchmark");var world=new GameWorld();Comfort.Common.Singleton<GameWorld>.Instance=world;
                try
                {
                    reset();
                    for(int actor=0;actor<32;actor++)
                    {
                        var health=new ActiveHealthController();var player=new Player{Id=actor,HealthController=health};player.Transform.Original=root.transform;world.AllAlivePlayersList.Add(player);
                        for(int i=0;i<56;i++)health.Effects.Add(new Effect{BodyPart=(EBodyPart)(i%7)});
                        for(int i=0;i<7;i++)health.Effects.Add(new Heavy{BodyPart=(EBodyPart)i});
                    }
                    for(int i=0;i<50;i++)update();ShadowRagdollWorld.ResetWork();
                    var watch=new System.Diagnostics.Stopwatch();long bytes=GC.GetAllocatedBytesForCurrentThread();watch.Start();
                    for(int i=0;i<2000;i++)update();watch.Stop();bytes=GC.GetAllocatedBytesForCurrentThread()-bytes;
                    results.Add(new TrackingResult{backend=name,meanMs=watch.Elapsed.TotalMilliseconds/2000,allocatedBytes=bytes,
                        effectScans=ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodEffectScans),effectsVisited=ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodEffectsVisited)});
                    Check(bytes==0,"steady bleed tracking allocates nothing");
                }
                finally {reset();PulsatingBloodStreamSystem.FixtureStreams.Clear();Comfort.Common.Singleton<GameWorld>.Instance=null;UnityEngine.Object.DestroyImmediate(root);}
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName,"tracking-benchmark.json"),JsonUtility.ToJson(new TrackingReport{cases=results.ToArray()},true));
        }
    }
}
#endif
