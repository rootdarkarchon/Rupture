using EFT;
using UnityEngine;
using Rupture.Ragdolls.Classes;

namespace Rupture.Impacts;

// Core-facing operations never expose Fika types or resolve the bridge in standalone play.
internal static class ImpactNetwork
{
    internal static void Update() => MultiplayerRuntime.Current.Update();
    internal static void ResetRaid() => MultiplayerRuntime.Current.ResetRaid();
    internal static void ConfigurationChanged() => MultiplayerRuntime.Current.ConfigurationChanged();
    internal static bool IsRemoteVictim(string profileId) => MultiplayerRuntime.Current.IsRemoteVictim(profileId);
    internal static Vector3 DeathMasterOrigin(EFT.Ballistics.DamageInfo damage, ImpactData sample) =>
        damage.DamageType == EDamageType.GrenadeFragment && sample.HasMasterOrigin ? sample.MasterOrigin : damage.MasterOrigin;
    internal static void SendDeath(Player player, ImpactStateService.VictimState state, EFT.Ballistics.DamageInfo damage, EBodyPart part, ImpactData sample) =>
        MultiplayerRuntime.Current.SendDeath(player, state, damage, part, sample);
    internal static void SendCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration = 0f) =>
        MultiplayerRuntime.Current.SendCorpse(target, part, sample, point, direction, penetration);
    internal static void DeferCorpse(CorpseInteractionTarget target, EBodyPart part, ImpactData sample, Vector3 point, Vector3 direction, float penetration = 0f) =>
        MultiplayerRuntime.Current.DeferCorpse(target, part, sample, point, direction, penetration);
}
