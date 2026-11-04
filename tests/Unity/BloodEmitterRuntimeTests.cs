#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using EFT;
using Fika.Core.Main.Players;
namespace Rupture.Dismemberment.Classes.BloodStreams
{
    internal sealed class EFTHardSettings { internal static readonly EFTHardSettings Instance = new EFTHardSettings(); internal int ENVIRONMENT_HIT_MASK = Physics.DefaultRaycastLayers; }
    [Serializable] public sealed class EmitterScenarioResult { public bool moving, postmortem; public int fps, emissions; public uint trajectoryHash = 2166136261u, launchHash = 2166136261u; public double meanFrameMs, trajectorySum; public long raycasts, predictions, skipped, allocatedBytes; }
    public static class BloodEmitterRuntimeTests
    {
        [Serializable] private sealed class Report
        {
            public bool passed;
            public int postmortemDepositScenarios, referenceChecks;
            public int eligibilityAdapterChecks, eligibilityPolicyChecks, eligibilityStartChecks, eligibilityFunnelChecks, eligibilityRestoreBudgetChecks;
            public EmitterScenarioResult[] current, baseline;
        }
        public static void RunBatch()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(10, -0.1f, 0); floor.transform.localScale = new Vector3(100, 0.2f, 100); Physics.SyncTransforms();
            var current = new List<EmitterScenarioResult>(); var baseline = new List<EmitterScenarioResult>();
            Type old = typeof(BloodEmitterRuntimeTests).Assembly.GetType("Rupture.Dismemberment.Classes.BloodStreams.BaselinePulsatingBloodStreamSystem");
            try
            {
                var eligibility = BloodStreamEligibilityRuntimeTests.Run();
                foreach (int fps in new[] { 60, 120 }) foreach (bool moving in new[] { false, true }) foreach (bool postmortem in new[] { false, true })
                {
                    var result = PulsatingBloodStreamSystem.RunScenario(moving, fps, postmortem); current.Add(result);
                    if (result.allocatedBytes != 0 || result.skipped == 0) throw new Exception("Emitter steady-state allocation/scheduling failed: " + JsonUtility.ToJson(result));
                    if (old != null)
                    {
                        var previous = (EmitterScenarioResult)old.GetMethod("RunScenario", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { moving, fps, postmortem }); baseline.Add(previous);
                        if (!postmortem && (result.emissions != previous.emissions || result.launchHash != previous.launchHash))
                            throw new Exception("Emission cadence/captured trajectories changed");
                        if (result.raycasts != previous.raycasts || result.predictions != previous.predictions || result.trajectoryHash != previous.trajectoryHash)
                            throw new Exception("Closest-hit optimization changed sampling or impact trajectories");
                    }
                }
                PulsatingBloodStreamSystem.VerifyHistory();
                PulsatingBloodStreamSystem.VerifyPostMortemDeposits();
                int referenceChecks = PulsatingBloodStreamSystem.VerifyReferenceParcels();
                BloodTrackingRuntimeTests.Run();
                LivingIndexRuntimeTests.Run();
                PulsatingBloodStreamSystem.VerifyClosestHits();
                File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "verification.json"), JsonUtility.ToJson(new Report
                {
                    passed = true, postmortemDepositScenarios = 12, referenceChecks = referenceChecks,
                    eligibilityAdapterChecks = eligibility.AdapterChecks, eligibilityPolicyChecks = eligibility.PolicyChecks,
                    eligibilityStartChecks = eligibility.StartChecks, eligibilityFunnelChecks = eligibility.FunnelChecks,
                    eligibilityRestoreBudgetChecks = eligibility.RestoreBudgetChecks,
                    current = current.ToArray(), baseline = baseline.ToArray()
                }, true));
            }
            finally { UnityEngine.Object.DestroyImmediate(floor); }
        }
    }
    // Eligibility and all start/receive/pending/restore bodies are extracted verbatim.
    // Only EFT/Fika fields, network validation/resolution, and clothing output are boundaries.
    internal static partial class BloodStreamEligibilityRuntimeTests
    {
        internal sealed class Result { internal int AdapterChecks, PolicyChecks, StartChecks, FunnelChecks, RestoreBudgetChecks; }
        private sealed class Actor
        {
            internal string Name;
            internal Player Player;
            internal bool Human;
            internal Func<Player, bool> Classify;
        }
        private static readonly StreamEmitter[] Emitters = new StreamEmitter[64];
        private static readonly PendingDescriptor[] PendingDescriptors = new PendingDescriptor[64];
        private static readonly uint[] RetiredStreamIds = new uint[256];
        private static readonly EffectLifetimes<LivingStream> LivingStreams = new EffectLifetimes<LivingStream>();
        private static readonly Dictionary<(string, EBodyPart, BloodStreamTriggerKind), HashSet<uint>> LivingWounds =
            new Dictionary<(string, EBodyPart, BloodStreamTriggerKind), HashSet<uint>>();
        private static readonly Dictionary<string, Player> Players = new Dictionary<string, Player>();
        private static int _retiredWriteIndex;
        private static float _nextRestoreAt;
        private static bool AnchorAvailable;
        private static Player ResolvePlayer(string profile) => Players.TryGetValue(profile, out var player) ? player : null;
        private static Transform ResolvePacketAnchor(Player player, EBodyPartColliderType collider, EBodyPart part, string bone) =>
            AnchorAvailable ? player.Transform.Original : null;
        private static bool Validate(BloodStreamData packet, out string failure)
        {
            failure = null;
            return packet.StreamId != 0 && !string.IsNullOrEmpty(packet.ProfileId);
        }
        private static bool AcceptSequence(BloodStreamData packet) => packet.EventSequence != 0;
        private static bool CanCreateStreams() => true;
        private static void Check(bool condition, string context)
        {
            if (!condition) throw new Exception("Human-head stream regression: " + context);
        }
        private static void Reset(Player player)
        {
            for (int i = 0; i < Emitters.Length; i++)
            {
                if (Emitters[i] == null) Emitters[i] = new StreamEmitter();
                ClearEmitter(Emitters[i]);
            }
            Array.Clear(PendingDescriptors, 0, PendingDescriptors.Length);
            Array.Clear(RetiredStreamIds, 0, RetiredStreamIds.Length);
            _retiredWriteIndex = 0; _nextRestoreAt = 0; AnchorAvailable = true;
            LivingStreams.Clear(); LivingWounds.Clear(); Players.Clear();
            if (player != null) Players.Add(player.ProfileId, player);
        }
        private static void CheckStarted(BloodStreamData descriptor, bool expected, string context)
        {
            int index = FindActive(descriptor.StreamId);
            Check((index >= 0) == expected, context + ": emitter existence");
            if (expected)
            {
                var emitter = Emitters[index];
                Check(emitter.Descriptor.TriggerKind == descriptor.TriggerKind &&
                    emitter.Descriptor.BodyPart == descriptor.BodyPart &&
                    emitter.Descriptor.EventSequence == descriptor.EventSequence &&
                    emitter.Anchor != null && ReferenceEquals(emitter.Player, ResolvePlayer(descriptor.ProfileId)),
                    context + ": production emitter initialization");
            }
        }
        internal static Result Run()
        {
            var result = new Result();
            var standalone = new StandaloneRuntime();
            var fika = new Rupture.Fika.FikaRuntime();
            var actors = new[]
            {
                new Actor { Name = "standalone self overrides native AI", Player = new Player { IsYourPlayer = true, AIData = new object() }, Human = true, Classify = standalone.IsHumanPlayer },
                new Actor { Name = "standalone nonself human", Player = new Player { IsYourPlayer = false }, Human = true, Classify = standalone.IsHumanPlayer },
                new Actor { Name = "standalone bot", Player = new Player { IsYourPlayer = false, AIData = new object() }, Human = false, Classify = standalone.IsHumanPlayer },
                new Actor { Name = "Fika host local human", Player = new FikaPlayer { IsYourPlayer = true, AIData = new object() }, Human = true, Classify = fika.IsHumanPlayer },
                new Actor { Name = "Fika client local human", Player = new FikaPlayer { IsYourPlayer = true }, Human = true, Classify = fika.IsHumanPlayer },
                new Actor { Name = "Fika observed remote human", Player = new ObservedPlayer { IsYourPlayer = false, IsObservedAI = false }, Human = true, Classify = fika.IsHumanPlayer },
                new Actor { Name = "Fika host local bot", Player = new FikaPlayer { IsYourPlayer = false, AIData = new object() }, Human = false, Classify = fika.IsHumanPlayer },
                new Actor { Name = "Fika observed bot with native AI", Player = new ObservedPlayer { IsYourPlayer = false, IsObservedAI = true, AIData = new object() }, Human = false, Classify = fika.IsHumanPlayer },
                new Actor { Name = "Fika observed bot without AIData", Player = new ObservedPlayer { IsYourPlayer = false, IsObservedAI = true }, Human = false, Classify = fika.IsHumanPlayer }
            };
            var root = new GameObject("human-head eligibility fixture");
            var previousClassifier = MultiplayerRuntime.Current.HumanClassifier;
            try
            {
                Check(!standalone.IsHumanPlayer(null) && !fika.IsHumanPlayer(null), "typed adapters reject null");
                result.AdapterChecks += 2;
                foreach (var actor in actors)
                {
                    actor.Player.ProfileId = actor.Name;
                    actor.Player.Transform.Original = root.transform;
                    Check(actor.Classify(actor.Player) == actor.Human, actor.Name + ": typed production adapter");
                    result.AdapterChecks++;
                    MultiplayerRuntime.Current.HumanClassifier = actor.Classify;
                    foreach (BloodStreamTriggerKind kind in Enum.GetValues(typeof(BloodStreamTriggerKind)))
                    for (int part = 0; part < 7; part++)
                    {
                        var descriptor = new BloodStreamData
                        {
                            Operation = BloodStreamOperation.Start, StreamId = 1, EventSequence = 2,
                            ProfileId = actor.Player.ProfileId, TriggerKind = kind, BodyPart = (EBodyPart)part,
                            Seed = 37, Duration = 3, PeakSpeed = 1.5f, Radius = 0.0052f, LocalDirection = Vector3.right
                        };
                        bool allowed = !(actor.Human && kind == BloodStreamTriggerKind.HeavyWound && part == (int)EBodyPart.Head);
                        string context = actor.Name + "/" + kind + "/" + (EBodyPart)part;
                        Check(CanStartStream(descriptor, actor.Player) == allowed, context + ": extracted policy");
                        result.PolicyChecks++;
                        foreach (bool localFracture in new[] { false, true })
                        {
                            Reset(actor.Player);
                            StartOrReplace(descriptor, actor.Player, root.transform, localFracture);
                            CheckStarted(descriptor, allowed, context + ": direct start, fracture=" + localFracture);
                            result.StartChecks++;
                            Reset(actor.Player);
                            var existing = descriptor;
                            existing.TriggerKind = BloodStreamTriggerKind.KillEntry; existing.EventSequence = 1;
                            StartOrReplace(existing, actor.Player, root.transform, localFracture);
                            CheckStarted(existing, true, context + ": replacement precondition");
                            StartOrReplace(descriptor, actor.Player, root.transform, localFracture);
                            CheckStarted(allowed ? descriptor : existing, true, context + ": guarded replacement, fracture=" + localFracture);
                            result.StartChecks++;
                        }
                        Reset(actor.Player);
                        Receive(descriptor);
                        CheckStarted(descriptor, allowed, context + ": production receive");
                        result.FunnelChecks++;
                        Reset(actor.Player);
                        AnchorAvailable = false;
                        Receive(descriptor);
                        Check(PendingDescriptors[0].Active && FindActive(descriptor.StreamId) < 0, context + ": receive queues unresolved anchor");
                        AnchorAvailable = true;
                        ProcessPending(Time.realtimeSinceStartup);
                        CheckStarted(descriptor, allowed, context + ": receive then pending resolution");
                        Check(!PendingDescriptors[0].Active, context + ": resolved pending consumed");
                        result.FunnelChecks++;
                        Reset(actor.Player);
                        QueuePending(descriptor);
                        ProcessPending(Time.realtimeSinceStartup);
                        CheckStarted(descriptor, allowed, context + ": production pending");
                        result.FunnelChecks++;
                        Reset(actor.Player);
                        // Seed a logical record directly so every kind reaches the real restore wrapper.
                        LivingStreams.Start(descriptor.StreamId, new LivingStream { Descriptor = descriptor, Player = actor.Player });
                        RestoreLivingVisuals(Time.realtimeSinceStartup + 1f);
                        CheckStarted(descriptor, allowed, context + ": production restore");
                        Check(LivingStreams.TryGet(descriptor.StreamId, out _), context + ": restore preserves logical state");
                        result.FunnelChecks++;
                    }
                }
                VerifyRestoreBudget(actors[5], actors[8], result);
                return result;
            }
            finally
            {
                Reset(null);
                MultiplayerRuntime.Current.HumanClassifier = previousClassifier;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        private static void VerifyRestoreBudget(Actor human, Actor bot, Result result)
        {
            Reset(bot.Player);
            Players.Add(human.Player.ProfileId, human.Player);
            MultiplayerRuntime.Current.HumanClassifier = human.Classify;
            // More denied heads than the four-stream restore budget must not starve bot heads.
            for (uint id = 1; id <= 12; id++)
            {
                Player player = id <= 8 ? human.Player : bot.Player;
                var packet = new BloodStreamData { StreamId = id, EventSequence = 1, ProfileId = player.ProfileId,
                    TriggerKind = BloodStreamTriggerKind.HeavyWound, BodyPart = EBodyPart.Head, Seed = id };
                LivingStreams.Start(id, new LivingStream { Descriptor = packet, Player = player });
            }
            // Assert the actual enumeration order, making the starvation precondition explicit.
            int position = 0;
            foreach (var stream in LivingStreams.Values)
            {
                Check(ReferenceEquals(stream.Player, position++ < 8 ? human.Player : bot.Player), "restore budget ordering precondition");
            }
            RestoreLivingVisuals(Time.realtimeSinceStartup + 1f);
            for (uint id = 1; id <= 12; id++) Check((FindActive(id) >= 0) == (id > 8), "restore budget skips denied human heads, stream=" + id);
            result.RestoreBudgetChecks++;
        }
    }
    internal static class ClothingSoakingSystem
    {
        // Foreign output boundary; no policy or emitter-start decisions live here.
        internal static readonly List<uint> Stopped = new List<uint>();
        internal static void Stop(uint streamId) => Stopped.Add(streamId);
    }
}
namespace Systems.Effects
{
    public sealed class Effects
    {
        public readonly List<Vector3> Points = new List<Vector3>();
        public void EmitBleeding(Vector3 point, Vector3 normal) { Points.Add(point); }
        public void EmitBloodOnEnvironment(Vector3 point, Vector3 normal) { Points.Add(point); }
    }
}
#endif
