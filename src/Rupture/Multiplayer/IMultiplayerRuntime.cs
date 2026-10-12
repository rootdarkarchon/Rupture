using System;
using EFT;
using UnityEngine;
using Rupture.Impacts;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.Classes.Packets;
using Rupture.Dismemberment.Classes.Packets;

namespace Rupture;

internal interface IMultiplayerRuntime : IDisposable
{
    bool IsHeadless { get; }
    bool IsRemoteClient { get; }
    bool IsNetworkAuthority { get; }
    bool HasServer { get; }
    bool HasClient { get; }
    int PlayerId(Player player);
    bool IsObservedPlayer(Player player);
    bool IsDowned(Player player);
    bool IsRemoteVictim(string profileId);
    string ValidateHeadgearOwner(Player player);
    void Initialize();
    void Update();
    void ResetRaid();
    void ConfigurationChanged();
    void SendDeath(Player player, ImpactStateService.VictimState state, EFT.Ballistics.DamageInfo damage, EBodyPart part, ImpactData sample);
    void SendCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration);
    void DeferCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration);
    void RequestBloodStream(BloodStreamRequestData request);
    void Send(ref RagdollForceData data, bool unreliable = false);
    void Send(ref RagdollPointImpulseData data, bool unreliable = false);
    void Send(ref RagdollPoseSnapshotData data, bool unreliable = false);
    void Send(ref RagdollRadialImpulseData data, bool unreliable = false);
    void Send(ref RagdollSettleData data, bool unreliable = false);
    void Send(ref RagdollStartData data, bool unreliable = false);
    void Send(ref RagdollWakeData data, bool unreliable = false);
    void Send(ref BloodStreamData data, bool unreliable = false);
    void Send(ref DismembermentData data, bool unreliable = false);
    void Send(ref HeadWoundData data, bool unreliable = false);
    void Send(ref Health.BotSeverData data, bool unreliable = false);
}
