using System;
using EFT;
using UnityEngine;
using Rupture.Impacts;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.Classes.Packets;
using Rupture.Dismemberment.Classes.Packets;

namespace Rupture;

internal sealed class StandaloneRuntime : IMultiplayerRuntime
{
    public bool IsHeadless => false;
    public bool IsRemoteClient => false;
    public bool IsNetworkAuthority => false;
    public bool HasServer => false;
    public bool HasClient => false;
    public int PlayerId(Player player) => -1;
    public bool IsObservedPlayer(Player player) => false;
    public bool IsDowned(Player player) => false;
    public bool IsRemoteVictim(string profileId) => false;
    public string ValidateHeadgearOwner(Player player) => null;
    public void Initialize() { }
    public void Update() { }
    public void ResetRaid() => ImpactStateService.Reset();
    public void ConfigurationChanged() { }
    public void Dispose() { }
    public void SendDeath(Player player, ImpactStateService.VictimState state, EFT.Ballistics.DamageInfo damage, EBodyPart part, ImpactData sample) { }
    public void SendCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration) { }
    public void DeferCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration) { }
    public void RequestBloodStream(BloodStreamRequestData request) { }
    public void Send(ref RagdollForceData data, bool unreliable = false) { }
    public void Send(ref RagdollPointImpulseData data, bool unreliable = false) { }
    public void Send(ref RagdollPoseSnapshotData data, bool unreliable = false) { }
    public void Send(ref RagdollRadialImpulseData data, bool unreliable = false) { }
    public void Send(ref RagdollSettleData data, bool unreliable = false) { }
    public void Send(ref RagdollStartData data, bool unreliable = false) { }
    public void Send(ref RagdollWakeData data, bool unreliable = false) { }
    public void Send(ref BloodStreamData data, bool unreliable = false) { }
    public void Send(ref DismembermentData data, bool unreliable = false) { }
    public void Send(ref HeadWoundData data, bool unreliable = false) { }
    public void Send(ref Health.BotSeverData data, bool unreliable = false) { }
}
