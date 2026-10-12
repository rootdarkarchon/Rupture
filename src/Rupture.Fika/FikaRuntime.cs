using System;
using EFT;
using UnityEngine;
using Rupture.Impacts;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.Classes.Packets;
using Rupture.Dismemberment.Classes.Packets;
using Comfort.Common;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Rupture.Dismemberment.Classes.BloodStreams;
using Fika.Core.Main.Components;

namespace Rupture.Fika;

internal sealed class FikaRuntime : IMultiplayerRuntime
{
    public bool IsHeadless => FikaBackendUtils.IsHeadless;
    public bool IsRemoteClient => FikaBackendUtils.IsClient && !FikaBackendUtils.IsServer;
    public bool IsNetworkAuthority => FikaBackendUtils.IsServer || FikaBackendUtils.IsHeadless;
    public bool HasServer => Singleton<FikaServer>.Instantiated;
    public bool HasClient => Singleton<FikaClient>.Instantiated;
    public int PlayerId(Player player) => player is FikaPlayer fika ? fika.NetId : -1;
    public bool IsObservedPlayer(Player player) => player is ObservedPlayer;
    public bool IsDowned(Player player) => player is FikaPlayer fika && fika.Downed;
    public bool IsRemoteVictim(string profileId) => FikaImpactNetwork.IsRemoteVictim(profileId);
    public string ValidateHeadgearOwner(Player player)
    {
        if (player == null) return "Fika player retired";
        if (!(player is FikaPlayer fika) || !CoopHandler.TryGetCoopHandler(out var handler) ||
            !handler.Players.TryGetValue(fika.NetId, out var registered) || !ReferenceEquals(registered, player))
            return "Fika player is no longer network-addressable";
        if (player is ObservedPlayer) return "observed inventory has no authoritative throw sender";
        return IsNetworkAuthority ? null : "peer does not own headgear detachment";
    }
    public void Initialize()
    {
        FikaImpactNetwork.Subscribe();
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(Created);
        FikaImpactHooks.Install();
        HeadgearNetworkDiagnostics.Install();
    }
    public void Update() => FikaImpactNetwork.Update();
    public void ResetRaid() => FikaImpactNetwork.ResetRaid();
    public void ConfigurationChanged() => FikaImpactNetwork.ConfigurationChanged();
    public void Dispose()
    {
        try { FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(Created); }
        finally
        {
            try { HeadgearNetworkDiagnostics.Shutdown(); }
            finally
            {
                try { FikaImpactHooks.Shutdown(); }
                finally { FikaImpactNetwork.Shutdown(); }
            }
        }
    }
    public void SendDeath(Player player, ImpactStateService.VictimState state, EFT.Ballistics.DamageInfo damage, EBodyPart part, ImpactData sample) =>
        FikaImpactNetwork.SendDeath(player, state, damage, part, sample);
    public void SendCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration) =>
        FikaImpactNetwork.SendCorpse(target, part, sample, point, direction, penetration);
    public void DeferCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration) =>
        FikaImpactNetwork.DeferCorpse(target, part, sample, point, direction, penetration);
    public void RequestBloodStream(BloodStreamRequestData request)
    {
        if (!HasClient) return;
        BloodStreamRequestPacket packet = request;
        Singleton<FikaClient>.Instance.SendData(ref packet, DeliveryMethod.ReliableOrdered);
    }
    private void Created(FikaNetworkManagerCreatedEvent evt)
    {
        FikaImpactNetwork.Register(evt.Manager);
        if (IsNetworkAuthority)
            evt.Manager.RegisterPacket<BloodStreamRequestPacket, NetPeer>((packet, peer) => PulsatingBloodStreamSystem.ReceiveRequest(packet, peer?.Player));
        if (!FikaBackendUtils.IsClient) return;
        evt.Manager.RegisterPacket<BloodStreamPacket>(packet => PulsatingBloodStreamSystem.Receive((BloodStreamData)packet));
        evt.Manager.RegisterPacket<DismembermentPacket>(packet => RagdollCoordinator.Receive((DismembermentData)packet));
        evt.Manager.RegisterPacket<HeadWoundPacket>(packet => RagdollCoordinator.Receive((HeadWoundData)packet));
        evt.Manager.RegisterPacket<RagdollStartPacket>(packet => RagdollCoordinator.Receive((RagdollStartData)packet));
        evt.Manager.RegisterPacket<RagdollPointImpulsePacket>(packet => RagdollCoordinator.Receive((RagdollPointImpulseData)packet));
        evt.Manager.RegisterPacket<RagdollRadialImpulsePacket>(packet => RagdollCoordinator.Receive((RagdollRadialImpulseData)packet));
        evt.Manager.RegisterPacket<RagdollForcePacket>(packet => RagdollCoordinator.Receive((RagdollForceData)packet));
        evt.Manager.RegisterPacket<RagdollWakePacket>(packet => RagdollCoordinator.Receive((RagdollWakeData)packet));
        evt.Manager.RegisterPacket<RagdollPoseSnapshotPacket>(packet => RagdollCoordinator.Receive((RagdollPoseSnapshotData)packet));
        evt.Manager.RegisterPacket<RagdollSettlePacket>(packet => RagdollCoordinator.Receive((RagdollSettleData)packet));
        ShadowVisiblePosePlayerLoop.EnsureInstalled();
    }
    public void Send(ref RagdollForceData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollForcePacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref RagdollPointImpulseData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollPointImpulsePacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref RagdollPoseSnapshotData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollPoseSnapshotPacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref RagdollRadialImpulseData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollRadialImpulsePacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref RagdollSettleData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollSettlePacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref RagdollStartData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollStartPacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref RagdollWakeData data, bool unreliable = false)
    {
        if (!HasServer) return;
        RagdollWakePacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref BloodStreamData data, bool unreliable = false)
    {
        if (!HasServer) return;
        BloodStreamPacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref DismembermentData data, bool unreliable = false)
    {
        if (!HasServer) return;
        DismembermentPacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref HeadWoundData data, bool unreliable = false)
    {
        if (!HasServer) return;
        HeadWoundPacket packet = data;
        Singleton<FikaServer>.Instance.SendData(ref packet, unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
    }
    public void Send(ref Rupture.Health.BotSeverData data, bool unreliable = false) => FikaImpactNetwork.SendBotSever(data, unreliable);
}
