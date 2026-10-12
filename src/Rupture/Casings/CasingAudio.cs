using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;
using Rupture.Ragdolls.External;
using Rupture.Ragdolls.Classes;

namespace Rupture.Casings;

internal static class CasingAudio
{
    internal static bool Play(CasingContact contact, ECaliber caliber)
    {
        if (!MonoBehaviourSingleton<BetterAudio>.Instantiated || EFT.CameraControl.CameraManager.Instance == null) return false;
        BaseBallistic.ESurfaceSound surface = BaseBallistic.ESurfaceSound.Soil;
        // This bounded presentation query resolves EFT surface audio, not simulation contacts.
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioSurface))
        if (Physics.Raycast(contact.Position + contact.Normal * .03f, -contact.Normal, out RaycastHit hit,
            .08f, ShadowRagdollWorld.LocalEnvironmentMask, QueryTriggerInteraction.Ignore))
        {
            BallisticCollider ballistic = hit.collider.GetComponent<BallisticCollider>();
            if (ballistic != null) surface = ballistic.GetSurfaceSound(contact.Position);
        }
        WeaponSounds sounds = EFT.Firearms.SoundsPrefab;
        SoundBank bank;
        switch (surface)
        {
            case BaseBallistic.ESurfaceSound.Concrete:
            case BaseBallistic.ESurfaceSound.Asphalt:
                bank = Select(caliber, sounds.Shell9mmConcrete, sounds.Shell556mmConcrete, sounds.Shell12calConcrete, sounds.ShellHeavyConcrete); break;
            case BaseBallistic.ESurfaceSound.Metal:
                bank = Select(caliber, sounds.Shell9mmMetal, sounds.Shell556mmMetal, sounds.Shell12calMetal, sounds.ShellHeavyMetal); break;
            case BaseBallistic.ESurfaceSound.Wood:
                bank = Select(caliber, sounds.Shell9mmWood, sounds.Shell556mmWood, sounds.Shell12calWood, sounds.ShellHeavyWood); break;
            case BaseBallistic.ESurfaceSound.Plastic:
                bank = Select(caliber, sounds.Shell9mmPlastic, sounds.Shell556mmPlastic, sounds.Shell12calPlastic, sounds.ShellHeavyPlastic); break;
            case BaseBallistic.ESurfaceSound.Soil:
            case BaseBallistic.ESurfaceSound.Gravel:
            case BaseBallistic.ESurfaceSound.Grass:
                bank = Select(caliber, sounds.Shell9mmSoil, sounds.Shell556mmSoil, sounds.Shell12calSoil, sounds.ShellHeavySoil); break;
            default: return false;
        }
        if (bank == null) return false;
        ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.CasingAudioAttempts, 1);
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioPlayback))
        {
            BetterSource source = MonoBehaviourSingleton<BetterAudio>.Instance.PlayAtPoint(contact.Position + Vector3.up * .05f, bank, (int)bank.SourceType,
                EFT.CameraControl.CameraManager.Instance.Distance(contact.Position), Mathf.Clamp01(contact.Speed / 2f), -1f,
                EnvironmentType.Outdoor, EOcclusionTest.OneShotPropagation, false, false, false);
            if (source != null) ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.CasingAudioSources, 1);
        }
        // Preserve the existing request budget/cooldown even when EFT has no source.
        return true;
    }
    private static SoundBank Select(ECaliber caliber, SoundBank small, SoundBank rifle, SoundBank shotgun, SoundBank heavy) =>
        caliber == ECaliber.Shell9Mm ? small : caliber == ECaliber.Shell556Mm ? rifle :
        caliber == ECaliber.Shell12Cal ? shotgun : caliber == ECaliber.ShellHeavy ? heavy : null;
}
