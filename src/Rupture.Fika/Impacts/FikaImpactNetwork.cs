using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Players;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using Fika.Core.Networking.Packets.Player.Common.SubPackets;
using Fika.Core.Networking.Packets.Backend;
using Rupture.Ragdolls.Classes;
using UnityEngine;

namespace Rupture.Impacts;

internal static class FikaImpactNetwork
{
    internal const int Protocol = 14;
    internal const string Version = "1.2.2";
    private sealed class PeerState { internal NetPeer Peer; internal string Session; internal float Deadline; internal uint ConfigurationRevision; }
    internal enum Reconciliation { Applied, Stale, WaitingIdentity, WaitingDeathData, Rejected }
    private sealed class PendingState
    {
        internal RuptureImpactStatePacket Packet;
        internal float Deadline;
        internal Corpse Source;
        internal bool Outgoing;
        internal bool BoundToCorpse;
        internal object Lifetime;
        internal Reconciliation LastResult;
    }
    private static readonly Dictionary<int, PeerState> Peers = new Dictionary<int, PeerState>();
    private static readonly Dictionary<string, uint> Received = new Dictionary<string, uint>();
    private static readonly List<PendingState> Pending = new List<PendingState>();
    private static readonly List<int> Disconnected = new List<int>();
    private static IFikaNetworkManager _manager;
    private static NetPeer _serverPeer;
    private static string _session;
    private static uint _sequence;
    private static int _warnings;
    private static bool _configurationDirty = true, _configurationBroadcastPending;
    private static uint _configurationRevision;
    private static string[] _visualConfiguration;
    private static string _requesterSession;
    private static NetPeer _pendingRaidStartPeer;
    private static bool _forceHeadlessConfiguration;

    private static void SelectConfigurationSource(bool headless) =>
        _forceHeadlessConfiguration = headless && RuptureConfiguration.ForceHeadlessConfiguration?.Value == true;

    internal static void ConfigurationChanged() => _configurationDirty = true;
    private static RuptureHelloPacket CreateHello()
    {
        bool authority = RagdollCoordinator.IsNetworkAuthority;
        bool requester = !authority && FikaBackendUtils.IsHeadlessGame &&
            FikaBackendUtils.IsHeadlessRequester && !FikaBackendUtils.IsReconnect;
        if (authority && _configurationDirty)
        {
            _visualConfiguration = RuptureConfiguration.CaptureVisuals();
            _configurationRevision++;
            _configurationDirty = false; _configurationBroadcastPending = true;
        }
        return new RuptureHelloPacket { Protocol = Protocol, Version = Version, Session = _session,
            IsHeadlessRequester = requester,
            ConfigurationRevision = authority ? _configurationRevision : 0,
            // Connected runs before incoming authority settings can replace local preferences.
            VisualConfiguration = authority ? _visualConfiguration : requester ? RuptureConfiguration.CaptureVisuals() : null };
    }

    // Trust the requester declaration, but adopt only one complete snapshot per raid.
    // Later declarations/reconnects cannot replace it or restore the headless defaults.
    internal static bool ApplyRequesterConfiguration(RuptureHelloPacket packet, bool headless, bool raidStarted)
    {
        if (!headless) return false;
        if (_forceHeadlessConfiguration) return true; // Ignore the payload without overriding or rejecting the requester.
        if (!packet.IsHeadlessRequester || packet.ConfigurationRevision != 0 ||
            packet.VisualConfiguration == null || packet.Session?.Length != 32) return false;
        if (_requesterSession != null) return true;
        if (raidStarted || !RuptureConfiguration.ApplyHostVisuals(packet.VisualConfiguration)) return false;
        _requesterSession = packet.Session;
        _configurationDirty = true;
        return true;
    }

    internal static bool ConfigurationReadyForStart =>
        (_forceHeadlessConfiguration || _requesterSession != null) && !_configurationDirty && !_configurationBroadcastPending;

    internal static bool FilterRaidStart(bool requested, NetPeer peer)
    {
        if (!requested) return false;
        if (ConfigurationReadyForStart && peer != null && Peers.TryGetValue(peer.Id, out PeerState state) && state.Session != null)
            return true;
        _pendingRaidStartPeer = peer;
        return false;
    }

    private static bool TakePendingRaidStart()
    {
        if (_pendingRaidStartPeer == null) return false;
        if (_pendingRaidStartPeer.ConnectionState != ConnectionState.Connected)
        { _pendingRaidStartPeer = null; return false; }
        if (!ConfigurationReadyForStart || !Peers.TryGetValue(_pendingRaidStartPeer.Id, out PeerState state) || state.Session == null)
            return false;
        _pendingRaidStartPeer = null;
        return true;
    }

    internal static void BeforeInformation(FikaServer server, ref InformationPacket packet, NetPeer peer)
    {
        if (FikaBackendUtils.IsHeadless && ReferenceEquals(server, _manager))
            packet.RequestStart = FilterRaidStart(packet.RequestStart, peer);
    }

    private static void BroadcastConfiguration()
    {
        if (_manager == null || !RagdollCoordinator.IsNetworkAuthority || (!_configurationDirty && !_configurationBroadcastPending)) return;
        RuptureHelloPacket hello = CreateHello();
        foreach (PeerState peer in Peers.Values)
            if (peer.Session != null) _manager.SendDataToPeer(ref hello, DeliveryMethod.ReliableOrdered, peer.Peer);
        _configurationBroadcastPending = false;
    }

    internal static void Subscribe()
    {
        FikaEventDispatcher.SubscribeEvent<PeerConnectedEvent>(Connected);
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(Destroyed);
    }
    internal static void Shutdown()
    {
        FikaEventDispatcher.UnsubscribeEvent<PeerConnectedEvent>(Connected);
        FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerDestroyedEvent>(Destroyed);
        Reset();
    }
    private static void Destroyed(FikaNetworkManagerDestroyedEvent _) => Reset();
    internal static void Register(IFikaNetworkManager manager)
    {
        Reset();
        _manager = manager;
        SelectConfigurationSource(FikaBackendUtils.IsHeadless);
        if (_forceHeadlessConfiguration) RupturePlugin.Log.Initialization("RPT headless uses its own synchronized settings; requester settings are ignored.");
        _session = Guid.NewGuid().ToString("N");
        if (RagdollCoordinator.IsNetworkAuthority)
        {
            manager.RegisterPacket<RuptureHelloPacket, NetPeer>(Hello);
            manager.RegisterPacket<RuptureDamageEnvelope, NetPeer>(ReceiveDamage);
            manager.RegisterPacket<RuptureImpactStatePacket, NetPeer>(ReceiveState);
        }
        else
        {
            manager.RegisterPacket<RuptureHelloPacket>(packet => Hello(packet, _serverPeer));
            manager.RegisterPacket<RuptureDamageEnvelope>(packet => ReceiveDamage(packet, null));
            manager.RegisterPacket<BotSeverPacket>(ReceiveBotSever);
        }
    }
    private static void Connected(PeerConnectedEvent evt)
    {
        if (!ReferenceEquals(evt.NetworkManager, _manager)) return;
        Peers[evt.Peer.Id] = new PeerState { Peer = evt.Peer, Deadline = Time.realtimeSinceStartup + 15f };
        if (!RagdollCoordinator.IsNetworkAuthority) _serverPeer = evt.Peer;
        RuptureHelloPacket hello = CreateHello();
        _manager.SendDataToPeer(ref hello, DeliveryMethod.ReliableOrdered, evt.Peer);
    }
    internal static bool IsRemoteVictim(string profileId)
    {
        foreach (PeerState peer in Peers.Values)
            if (peer.Peer.Player?.ProfileId == profileId) return true;
        return false;
    }
    internal static bool Compatible(int protocol, string version) => protocol == Protocol && version == Version;
    private static void Hello(RuptureHelloPacket packet, NetPeer peer)
    {
        if (peer == null) return;
        if (!Compatible(packet.Protocol, packet.Version) || packet.Session?.Length != 32)
        {
            Reject(peer, "Rupture protocol/version mismatch. Every peer must install Rupture " + Version + " (impact protocol " + Protocol + ").");
            return;
        }
        if (!Peers.TryGetValue(peer.Id, out PeerState state)) return;
        if (state.Session != null && state.Session != packet.Session) { Reject(peer, "Rupture session changed without reconnecting."); return; }
        if (RagdollCoordinator.IsNetworkAuthority)
        {
            if (packet.IsHeadlessRequester || packet.ConfigurationRevision != 0 || packet.VisualConfiguration != null)
            {
                bool first = _requesterSession == null && !_forceHeadlessConfiguration;
                bool started = Singleton<IFikaGame>.Instantiated && Singleton<IFikaGame>.Instance.GameController?.RaidStarted == true;
                if (!ApplyRequesterConfiguration(packet, FikaBackendUtils.IsHeadless, started))
                { Reject(peer, "Invalid Rupture headless requester configuration."); return; }
                if (first) RupturePlugin.Log.Initialization("RPT headless adopted the raid requester's synchronized settings; overrides are temporary.");
            }
        }
        else
        {
            if (!ReferenceEquals(peer, _serverPeer)) return;
            if (packet.IsHeadlessRequester || packet.ConfigurationRevision == 0 || packet.VisualConfiguration == null)
            { Reject(peer, "Rupture authority did not provide visual configuration."); return; }
            if (packet.ConfigurationRevision <= state.ConfigurationRevision) return;
            if (!RuptureConfiguration.ApplyHostVisuals(packet.VisualConfiguration))
            { Reject(peer, "Invalid Rupture authority visual configuration."); return; }
            state.ConfigurationRevision = packet.ConfigurationRevision;
        }
        state.Session = packet.Session;
    }
    private static void Reject(NetPeer peer, string reason)
    {
        RupturePlugin.Log.LogError(reason);
        peer.Disconnect(System.Text.Encoding.UTF8.GetBytes(reason));
    }
    internal static bool AcceptSequence(Dictionary<string, uint> received, string session, uint sequence)
    {
        if (string.IsNullOrEmpty(session) || session.Length != 32 || sequence == 0) return false;
        if (received.TryGetValue(session, out uint previous) && sequence <= previous) return false;
        received[session] = sequence;
        return true;
    }
    private static bool Accept(string session, uint sequence, NetPeer peer)
    {
        if (_manager == null) return false;
        if (peer != null && (!Peers.TryGetValue(peer.Id, out PeerState state) || state.Session != session)) return false;
        if (peer == null && (_serverPeer == null || !Peers.TryGetValue(_serverPeer.Id, out PeerState server) || server.Session == null)) return false;
        return AcceptSequence(Received, session, sequence);
    }

    // Exact signature of the IFikaNetworkManager call being replaced, including its receiver.
    internal static void SendDamage(IFikaNetworkManager manager, in DamagePacket damage, int netId,
        DeliveryMethod deliveryMethod, bool broadcast, NetPeer peerToIgnore)
    {
        if (!UsesDamageEnvelope(damage.DamageType) || _manager == null)
        {
            manager.SendPlayerPacket(in damage, netId, deliveryMethod, broadcast, peerToIgnore);
            return;
        }
        NetDataWriter writer = new NetDataWriter();
        writer.Put(netId);
        damage.Serialize(writer);
        RuptureDamageEnvelope envelope = new RuptureDamageEnvelope { Session = _session, Sequence = ++_sequence,
            Sample = ImpactStateService.Current, Damage = writer.CopyData() };
        if (manager is FikaServer server) server.SendData(ref envelope, DeliveryMethod.ReliableOrdered, peerToIgnore);
        else manager.SendData(ref envelope, DeliveryMethod.ReliableOrdered, broadcast: false);
    }

    private static void ReceiveDamage(RuptureDamageEnvelope envelope, NetPeer peer)
    {
        if (!Accept(envelope.Session, envelope.Sequence, peer)) return;
        if (envelope.Damage == null || envelope.Damage.Length > 2048) { Warn("Rejected invalid Rupture damage payload length."); return; }
        ImpactSample previous = ImpactStateService.Current;
        try
        {
            NetDataReader reader = new NetDataReader(envelope.Damage);
            int netId = reader.GetInt();
            DamagePacket damage = new DamagePacket(reader);
            if (reader.AvailableBytes != 0 || !UsesDamageEnvelope(damage.DamageType) ||
                !Finite(envelope.Sample.MasterOrigin)) return;
            if (peer != null && (peer.Player == null || !damage.ProfileId.HasValue || damage.ProfileId.Value.ToString() != peer.Player.ProfileId)) return;
            if (peer != null && _manager is FikaServer server)
                server.SendData(ref envelope, DeliveryMethod.ReliableOrdered, peer);
            ImpactStateService.Current = envelope.Sample;
            // This is the same lookup/Execute path as Fika's HandlePlayerPacket callback.
            // Observed instances do not apply the damage; its owner does.
            if (_manager.CoopHandler?.Players.TryGetValue(netId, out FikaPlayer player) == true) damage.Execute(player);
        }
        catch (Exception exception) { Warn("Rejected malformed Rupture damage envelope: " + exception.Message); }
        finally { ImpactStateService.Current = previous; }
    }

    internal static bool UsesDamageEnvelope(EDamageType damageType) =>
        damageType == EDamageType.Bullet || damageType == EDamageType.GrenadeFragment;

    internal static Vector3 DeathMasterOrigin(EFT.Ballistics.DamageInfo damage, ImpactSample sample) =>
        // Stock Fika DamagePacket omits MasterOrigin; the enclosing fragment sample
        // preserves it across owner-side damage reconstruction.
        damage.DamageType == EDamageType.GrenadeFragment && sample.HasMasterOrigin ? sample.MasterOrigin : damage.MasterOrigin;

    internal static void SendDeath(Player player, ImpactStateService.VictimState state, EFT.Ballistics.DamageInfo damage, EBodyPart part, ImpactSample sample)
    {
        if (_manager == null) return;
        // Core has already stripped non-projectile momentum while retaining a
        // validated blast origin, including an explicitly known world zero.
        RuptureImpactStatePacket packet = MakeState(player, part, sample, damage.HitPoint, damage.Direction);
        packet.Operation = ImpactStateOperation.Death;
        packet.DamageType = damage.Blunt && damage.DamageType == EDamageType.Bullet ? EDamageType.Blunt : damage.DamageType; packet.Damage = damage.Damage; packet.Penetration = damage.PenetrationPower;
        packet.ColliderType = damage.BodyPartColliderType;
        packet.MasterOrigin = DeathMasterOrigin(damage, sample);
        packet.LeftArm = state.Limbs[0].Total; packet.RightArm = state.Limbs[1].Total;
        packet.LeftLeg = state.Limbs[2].Total; packet.RightLeg = state.Limbs[3].Total;
        _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }
    private static RuptureImpactStatePacket MakeState(Player player, EBodyPart part, ImpactSample sample, Vector3 point, Vector3 direction) =>
        new RuptureImpactStatePacket { Session = _session, Sequence = ++_sequence, ProfileId = player.ProfileId,
            PlayerId = player is FikaPlayer fika ? fika.NetId : -1, Part = part, Sample = sample, Point = point, Direction = direction };
    private static RuptureImpactStatePacket MakeCorpseState(CorpseInteractionTarget target, EBodyPart part, ImpactSample sample, Vector3 point, Vector3 direction) =>
        new RuptureImpactStatePacket { ProfileId = target.ProfileId,
            PlayerId = target.PlayerId, Part = part, Sample = sample, Point = point, Direction = direction };
    internal static void SendCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactSample sample, Vector3 point, Vector3 direction, float penetration = 0f)
    {
        if (_manager == null) return;
        RuptureImpactStatePacket packet = MakeCorpseState(target, part, sample, point, direction);
        packet.Operation = ImpactStateOperation.Corpse; packet.DeathSequence = target.DeathSequence; packet.Penetration = penetration;
        // Allocate the wire sequence only when the identity is ready to send.
        // Later death/damage packets may legitimately overtake this local wait.
        if (target.PlayerId < 0 || target.DeathSequence == 0) Enqueue(packet, target.Corpse, outgoing: true);
        else SendCorpsePacket(packet);
    }
    internal static void SendBotSever(Health.BotSeverData data, bool unreliable)
    {
        if (!RagdollCoordinator.IsNetworkAuthority || _manager == null || !Health.BotSeverStateService.Valid(data)) return;
        var packet = new BotSeverPacket { Session = _session, Data = data };
        _manager.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    private static void ReceiveBotSever(BotSeverPacket packet)
    {
        if (RagdollCoordinator.IsNetworkAuthority || _serverPeer == null ||
            !Peers.TryGetValue(_serverPeer.Id, out PeerState peer) || peer.Session == null || peer.Session != packet.Session) return;
        Health.BotSeverStateService.Receive(packet.Data);
    }
    private static void SendCorpsePacket(RuptureImpactStatePacket packet)
    {
        packet.Session = _session; packet.Sequence = ++_sequence;
        _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }
    internal static void DeferCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactSample sample, Vector3 point, Vector3 direction, float penetration = 0f)
    {
        
        RuptureImpactStatePacket packet = MakeCorpseState(target, part, sample, point, direction);
        packet.Operation = ImpactStateOperation.Corpse; packet.DeathSequence = target.DeathSequence; packet.Penetration = penetration;
        Enqueue(packet, target.Corpse);
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    // A death snapshot can be in flight when the authority lowers the setting.
    // Accept bounded historical totals, then clamp to the current setting on import.
    private static bool TotalValid(float value) => Finite(value) && value >= 0 && value <= LimbImpactPolicy.MaximumThreshold;
    private static void ReceiveState(RuptureImpactStatePacket packet, NetPeer peer)
    {
        if (!RagdollCoordinator.IsNetworkAuthority || peer?.Player == null || !Accept(packet.Session, packet.Sequence, peer)) return;
        if (!Finite(packet.Point) || !Finite(packet.Direction) || !Finite(packet.MasterOrigin) || !Finite(packet.Damage) || !Finite(packet.Penetration) ||
            !Enum.IsDefined(typeof(EBodyPart), packet.Part)) { Warn("Rejected invalid Rupture impact state metadata."); return; }
        if (packet.Operation == ImpactStateOperation.Death)
        {
            if (packet.ProfileId != peer.Player.ProfileId || packet.PlayerId != peer.Player.NetId || !TotalValid(packet.LeftArm) || !TotalValid(packet.RightArm) ||
                !TotalValid(packet.LeftLeg) || !TotalValid(packet.RightLeg)) { Warn("Rejected Rupture death state ownership or totals."); return; }
        }
        else if (packet.Operation != ImpactStateOperation.Corpse || packet.PlayerId < 0 || packet.DeathSequence == 0 || !packet.Sample.Valid || !ImpactStateService.CorpsePart(packet.Part) ||
            (packet.Part == EBodyPart.Head && (packet.Point.sqrMagnitude > 4f || packet.Direction.sqrMagnitude < 0.0001f)))
        { Warn("Rejected invalid Rupture corpse impact sample or body part."); return; }
        Enqueue(packet);
    }
    private static void Enqueue(RuptureImpactStatePacket packet, Corpse source = null, bool outgoing = false)
    {
        if (Pending.Count >= 128) { Warn("Rupture impact reconciliation queue is full: " + Describe(packet, Reconciliation.Rejected)); return; }
        Pending.Add(new PendingState { Packet = packet, Source = source, Outgoing = outgoing, BoundToCorpse = !ReferenceEquals(source, null) || outgoing,
            Lifetime = RagdollCoordinator.CaptureCorpseImpactLifetime(source),
            LastResult = Reconciliation.WaitingIdentity, Deadline = Time.realtimeSinceStartup + 10f });
    }
    internal static void Update()
    {
        float now = Time.realtimeSinceStartup;
        Disconnected.Clear();
        foreach (var pair in Peers)
        {
            if (pair.Value.Peer.ConnectionState != ConnectionState.Connected) { Disconnected.Add(pair.Key); continue; }
            if (pair.Value.Session == null && now > pair.Value.Deadline)
            {
                Reject(pair.Value.Peer, "Rupture handshake timed out. Install Rupture " + Version + " on every peer.");
                Disconnected.Add(pair.Key);
            }
        }
        foreach (int id in Disconnected) Peers.Remove(id);
        if (_serverPeer != null && _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            RuptureConfiguration.RestoreLocal();
            _serverPeer = null;
        }
        BroadcastConfiguration();
        // Queue the authority snapshot before releasing a start request, including
        // requests from another client when Fika's AnyoneCanStartRaid is enabled.
        NetPeer startPeer = _pendingRaidStartPeer;
        if (_manager is FikaServer server && startPeer != null && Singleton<IFikaGame>.Instantiated && TakePendingRaidStart())
            FikaImpactHooks.ReleaseRaidStart(server, startPeer);
        for (int index = 0; index < Pending.Count;)
        {
            PendingState pending = Pending[index];
            if (now > pending.Deadline) { Warn("Rupture impact reconciliation expired: " + Describe(pending.Packet, pending.LastResult)); Pending.RemoveAt(index); continue; }
            pending.LastResult = pending.BoundToCorpse ? TryApplyBound(pending) : TryApply(pending.Packet);
            if (pending.LastResult == Reconciliation.WaitingIdentity || pending.LastResult == Reconciliation.WaitingDeathData) index++;
            else
            {
                if (pending.LastResult != Reconciliation.Applied) RupturePlugin.Log.LogDebug("Rupture impact reconciliation: " + Describe(pending.Packet, pending.LastResult));
                Pending.RemoveAt(index);
            }
        }
    }
    private static string Describe(RuptureImpactStatePacket packet, Reconciliation result) =>
        $"operation={packet.Operation}, profile={packet.ProfileId}, playerId={packet.PlayerId}, deathSequence={packet.DeathSequence}, packetSequence={packet.Sequence}, reason={result}";

    internal static Reconciliation MatchIdentity(string profile, int playerId, uint deathSequence, string currentProfile, int currentPlayerId, uint currentDeathSequence)
    {
        if (profile != currentProfile || (playerId >= 0 && currentPlayerId >= 0 && playerId != currentPlayerId)) return Reconciliation.Stale;
        if (deathSequence != 0 && currentDeathSequence != 0 && deathSequence != currentDeathSequence) return Reconciliation.Stale;
        return currentPlayerId < 0 || currentDeathSequence == 0 ? Reconciliation.WaitingIdentity : Reconciliation.Applied;
    }

    private static Reconciliation TryApplyBound(PendingState pending)
    {
        if (_manager == null || !RagdollCoordinator.TryGetBoundInteractionTarget(pending.Source, pending.Lifetime, out CorpseInteractionTarget target)) return Reconciliation.Stale;
        if (target.Corpse == null || target.VisualRoot == null || target.PlayerBody == null) return Reconciliation.WaitingIdentity;
        RuptureImpactStatePacket packet = pending.Packet;
        Reconciliation result = MatchIdentity(packet.ProfileId, packet.PlayerId, packet.DeathSequence, target.ProfileId, target.PlayerId, target.DeathSequence);
        if (result != Reconciliation.Applied) return result;
        packet.PlayerId = target.PlayerId; packet.DeathSequence = target.DeathSequence;
        pending.Packet = packet;
        if (!pending.Outgoing) return TryApply(packet);
        SendCorpsePacket(packet);
        return Reconciliation.Applied;
    }

    private static Reconciliation TryApply(RuptureImpactStatePacket packet)
    {
        if (packet.Operation == ImpactStateOperation.Corpse)
        {
            // Retained corpses outlive the Fika live-player dictionary and Unity Player.
            if (!RagdollCoordinator.TryGetInteractionTarget(packet.ProfileId, packet.PlayerId, out CorpseInteractionTarget corpse)) return Reconciliation.WaitingIdentity;
            if (packet.DeathSequence > corpse.DeathSequence) return Reconciliation.WaitingIdentity;
            if (packet.DeathSequence != corpse.DeathSequence) return Reconciliation.Stale;
            return ImpactStateService.ApplyCorpse(corpse, packet.Part, packet.Sample, packet.Point, packet.Direction, packet.Penetration)
                ? Reconciliation.Applied : Reconciliation.WaitingDeathData;
        }
        // Completed death snapshots remain recognizable after Fika removes Player.
        if (RagdollCoordinator.TryGetInteractionTarget(packet.ProfileId, packet.PlayerId, out CorpseInteractionTarget retained) &&
            ImpactStateService.Get(retained).DeathApplied) return Reconciliation.Stale;
        if (_manager?.CoopHandler?.Players.TryGetValue(packet.PlayerId, out FikaPlayer player) != true) return Reconciliation.WaitingIdentity;
        if (player.ProfileId != packet.ProfileId) return Reconciliation.Rejected;
        if (player.HealthController == null || player.HealthController.IsAlive) return Reconciliation.WaitingDeathData;
        if (!RagdollCoordinator.TryGetInteractionTarget(player, out CorpseInteractionTarget target)) return Reconciliation.WaitingIdentity;
        ImpactStateService.VictimState state = ImpactStateService.Get(player);
        if (state.DeathApplied) return Reconciliation.Stale;
        float threshold = RuptureConfiguration.DismembermentMomentumThreshold.Value;
        state.Limbs[0].Import(packet.LeftArm, threshold); state.Limbs[1].Import(packet.RightArm, threshold);
        state.Limbs[2].Import(packet.LeftLeg, threshold); state.Limbs[3].Import(packet.RightLeg, threshold);
        // The snapshot already includes the lethal projectile. Remember its identity without adding momentum.
        int limb = ImpactStateService.Index(packet.Part);
        if (limb >= 0 && packet.Sample.Valid) state.Limbs[limb].Remember(packet.Sample.Projectile);
        EFT.Ballistics.DamageInfo damage = new EFT.Ballistics.DamageInfo { DamageType = packet.DamageType, Damage = packet.Damage,
            PenetrationPower = packet.Penetration, HitPoint = packet.Point, Direction = packet.Direction, MasterOrigin = packet.MasterOrigin,
            BodyPartColliderType = packet.ColliderType, FireIndex = packet.Sample.FireIndex };
        ImpactStateService.ApplyDeath(player, state, damage, packet.Part, packet.Sample);
        return Reconciliation.Applied;
    }
    private static void Warn(string message) { if (_warnings++ < 10) RupturePlugin.Log.LogWarning(message); }
    internal static void ResetRaid() { Pending.Clear(); Health.BotSeverStateService.Reset(); ImpactStateService.Reset(); }
    internal static void Reset()
    {
        Peers.Clear(); Received.Clear(); Pending.Clear(); Disconnected.Clear();
        _manager = null; _serverPeer = null; _sequence = 0; _warnings = 0;
        _configurationRevision = 0; _visualConfiguration = null;
        _requesterSession = null; _pendingRaidStartPeer = null;
        _forceHeadlessConfiguration = false;
        _configurationDirty = true; _configurationBroadcastPending = false;
        RuptureConfiguration.RestoreLocal();
        Health.BotSeverStateService.Reset();
        ImpactStateService.Reset();
    }
}
