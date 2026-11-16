using System;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;
using Rupture.Ragdolls.External;
using Rupture.Ragdolls.Classes;

namespace Rupture.Casings;

internal static class CasingAudio
{
    /// <summary>
    /// Free sources every audio group used by the shell banks is topped up to. EFT's pool instantiates a source (prefab clone,
    /// curves, spatializer components) when it runs dry, which cost several milliseconds on the playing frame; a held source lives
    /// at least one second, so sustained fire needs about as many sources as the per-frame sound cap.
    /// </summary>
    private const int PrewarmSources = 12;
    private static readonly BaseBallistic.ESurfaceSound[] BankSurfaces =
    {
        BaseBallistic.ESurfaceSound.Concrete, BaseBallistic.ESurfaceSound.Metal, BaseBallistic.ESurfaceSound.Wood,
        BaseBallistic.ESurfaceSound.Plastic, BaseBallistic.ESurfaceSound.Soil,
    };
    private static readonly ECaliber[] BankCalibers = { ECaliber.Shell9Mm, ECaliber.Shell556Mm, ECaliber.Shell12Cal, ECaliber.ShellHeavy };
    private static readonly BetterSource[] Held = new BetterSource[PrewarmSources];
    private static readonly Audio.AuxiliaryAudioUtils.AudioSourcePriorityCalculator Priorities = new Audio.AuxiliaryAudioUtils.AudioSourcePriorityCalculator();
    private static BetterAudio _warmAudio;
    private static ulong _warmGroups;
    private static int _warmGroup = -1, _held;

    /// <summary>Whether a shell's collision listener is the local player's firearm, the case vanilla plays without occlusion.</summary>
    /// <param name="listener">The shell's <c>CollisionListener</c>, read while the casing is admitted.</param>
    internal static bool IsLocalShooter(object listener) => listener is EFT.Firearms firearms && firearms.Player != null && firearms.Player.IsYourPlayer;

    /// <summary>
    /// Plays one contact sound. <paramref name="localShooter"/> mirrors vanilla <c>Firearms.PlayShellSound</c>, which skips the
    /// one-shot propagation occlusion test (a container, room lookup and propagation calculator per source) for the local player's
    /// own casings and only runs it for everyone else's.
    /// </summary>
    internal static bool Play(CasingContact contact, ECaliber caliber, bool localShooter)
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
        SoundBank bank = Bank(EFT.Firearms.SoundsPrefab, surface, caliber);
        if (bank == null) return false;
        ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.CasingAudioAttempts, 1);
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioPlayback))
        {
            if (StartSource(contact, bank, localShooter)) ShadowRagdollWorld.RecordMainThreadWork(ShadowRagdollWorld.MainThreadWork.CasingAudioSources, 1);
        }
        // Preserve the existing request budget/cooldown even when EFT has no source.
        return true;
    }

    /// <summary>
    /// Mirrors <c>BetterAudio.PlayAtPoint(Vector3, SoundBank, int, ...)</c> step for step so each EFT call has its own trace stage.
    /// The only omission is the second <c>ResetOcclusion</c>: <c>BorrowSource</c> already reset it and nothing in between changes it.
    /// Measured in an Editor-hosted raid (Collisions group, <c>ReverbSimpleSource</c>): borrow (component enables) about 0.13 ms,
    /// one-shot propagation occlusion about 0.17 ms, <c>AudioSource.Play</c> 0.05 ms typically but 1 to 3 ms on roughly a quarter of
    /// calls (audio-thread contention while the voice and its filter chain are created). Everything else is under 0.02 ms.
    /// </summary>
    private static BetterSource StartSource(CasingContact contact, SoundBank bank, bool localShooter)
    {
        BetterAudio audio = MonoBehaviourSingleton<BetterAudio>.Instance;
        float distance = EFT.CameraControl.CameraManager.Instance.Distance(contact.Position);
        if (distance > bank.Rolloff) return null;
        AudioClip clip = null, clip2 = null;
        float proportions = 1f, length;
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioPick))
            length = bank.PickClips(distance, ref clip, ref clip2, ref proportions, EnvironmentType.Outdoor);
        if (length <= 0f) return null;
        BetterAudio.ISourceGroup group = audio.SourceGroups[(int)bank.SourceType];
        BetterSource source;
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioBorrow))
            source = group.BorrowSource(true, false);
        if (source == null) return null;
        float volume = Mathf.Clamp01(contact.Speed / 2f) * bank.RandomVolume;
        float pitch = bank.RandomPitch;
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioConfigure))
        {
            source.Position = contact.Position + Vector3.up * .05f;
            source.SetRolloff(bank.Rolloff);
            source.SetPriority(Priorities.CalculatePriority(distance, bank.Rolloff));
            source.SetMixerGroup(group.MixerGroup);
            source.SetPitch(pitch);
        }
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioStart))
            source.Play(clip, clip2, proportions, volume, false, false);
        // Local shooters play without occlusion, like vanilla Firearms.PlayShellSound.
        if (!localShooter && !bank.IgnoreOcclusion && MonoBehaviourSingleton<Audio.SpatialSystem.SpatialAudioSystem>.Instantiated)
        {
            using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioOcclusion))
                MonoBehaviourSingleton<Audio.SpatialSystem.SpatialAudioSystem>.Instance.ProcessSourceOcclusion(source, EOcclusionTest.OneShotPropagation);
        }
        using (ShadowRagdollWorld.TraceMainThread(ShadowRagdollWorld.MainThreadStage.CasingAudioQueue))
            audio.AddToAudioSourceQueue(source, AudioSettings.dspTime + Math.Max(length / pitch, 1f));
        return source;
    }

    /// <summary>
    /// Takes at most one pooled-source step toward <see cref="PrewarmSources"/> free sources in each audio group the shell banks
    /// use, through EFT's own borrow and release path. Call once per frame until it returns <see langword="true"/>.
    /// </summary>
    /// <returns><see langword="true"/> once every group holds the target for the current <c>BetterAudio</c> instance.</returns>
    internal static bool WarmPool()
    {
        if (!MonoBehaviourSingleton<BetterAudio>.Instantiated) return false;
        BetterAudio audio = MonoBehaviourSingleton<BetterAudio>.Instance;
        if (!ReferenceEquals(audio, _warmAudio))
        {
            WeaponSounds sounds = audio.MiscCollisionSounds;
            if (sounds == null || audio.SourceGroups == null) return false;
            // A new audio system owns a new pool; sources borrowed from the previous one are gone with it.
            CancelWarmup();
            _warmGroups = BankGroups(audio, sounds);
            _warmAudio = audio;
        }
        if (_warmGroup < 0)
        {
            if (_warmGroups == 0) return true;
            int group = 0;
            while ((_warmGroups & (1UL << group)) == 0) group++;
            _warmGroup = group;
        }
        BetterSource source = audio.SourceGroups[_warmGroup].BorrowSource(false, false);
        if (source != null) Held[_held++] = source;
        if (source == null || _held == PrewarmSources)
        {
            ReleaseHeld();
            _warmGroups &= ~(1UL << _warmGroup);
            _warmGroup = -1;
        }
        return _warmGroups == 0;
    }

    /// <summary>Whether <see cref="WarmPool"/> currently has sources borrowed that only <see cref="CancelWarmup"/> or finishing a group returns.</summary>
    internal static bool HoldsSources => _held != 0;

    /// <summary>Returns any source borrowed by <see cref="WarmPool"/> to its pool; the next call restarts the warm-up.</summary>
    internal static void CancelWarmup()
    {
        ReleaseHeld();
        _warmAudio = null;
        _warmGroups = 0;
        _warmGroup = -1;
    }

    private static void ReleaseHeld()
    {
        int count = _held;
        _held = 0;
        for (int i = 0; i < count; i++)
        {
            // A source destroyed with its audio system is already gone; releasing it would only touch a dead object.
            BetterSource source = Held[i];
            Held[i] = null;
            if (source != null) source.Release();
        }
    }

    /// <summary>
    /// Collects the audio groups the shell banks play through. Only pooled <c>NonspatialAudioMixer</c> groups qualify: the bypass
    /// group (<c>NonspatialBypass</c>) owns one shared source that <c>BorrowSource</c> hands out every time and stops, so borrowing
    /// it would silence whatever EFT is playing there and hold the same object twelve times.
    /// </summary>
    private static ulong BankGroups(BetterAudio audio, WeaponSounds sounds)
    {
        ulong groups = 0;
        int limit = Math.Min(audio.SourceGroups.Length, 64);
        for (int surface = 0; surface < BankSurfaces.Length; surface++)
            for (int caliber = 0; caliber < BankCalibers.Length; caliber++)
            {
                SoundBank bank = Bank(sounds, BankSurfaces[surface], BankCalibers[caliber]);
                if (bank != null && (int)bank.SourceType < limit && IsPooled(audio.SourceGroups[(int)bank.SourceType])) groups |= 1UL << (int)bank.SourceType;
            }
        return groups;
    }

    private static bool IsPooled(BetterAudio.ISourceGroup group)
    {
        for (Type type = group?.GetType(); type != null; type = type.BaseType)
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BetterAudio.NonspatialAudioMixer<>)) return true;
        return false;
    }

    private static SoundBank Bank(WeaponSounds sounds, BaseBallistic.ESurfaceSound surface, ECaliber caliber)
    {
        switch (surface)
        {
            case BaseBallistic.ESurfaceSound.Concrete:
            case BaseBallistic.ESurfaceSound.Asphalt:
                return Select(caliber, sounds.Shell9mmConcrete, sounds.Shell556mmConcrete, sounds.Shell12calConcrete, sounds.ShellHeavyConcrete);
            case BaseBallistic.ESurfaceSound.Metal:
                return Select(caliber, sounds.Shell9mmMetal, sounds.Shell556mmMetal, sounds.Shell12calMetal, sounds.ShellHeavyMetal);
            case BaseBallistic.ESurfaceSound.Wood:
                return Select(caliber, sounds.Shell9mmWood, sounds.Shell556mmWood, sounds.Shell12calWood, sounds.ShellHeavyWood);
            case BaseBallistic.ESurfaceSound.Plastic:
                return Select(caliber, sounds.Shell9mmPlastic, sounds.Shell556mmPlastic, sounds.Shell12calPlastic, sounds.ShellHeavyPlastic);
            case BaseBallistic.ESurfaceSound.Soil:
            case BaseBallistic.ESurfaceSound.Gravel:
            case BaseBallistic.ESurfaceSound.Grass:
                return Select(caliber, sounds.Shell9mmSoil, sounds.Shell556mmSoil, sounds.Shell12calSoil, sounds.ShellHeavySoil);
            default: return null;
        }
    }

    private static SoundBank Select(ECaliber caliber, SoundBank small, SoundBank rifle, SoundBank shotgun, SoundBank heavy) =>
        caliber == ECaliber.Shell9Mm ? small : caliber == ECaliber.Shell556Mm ? rifle :
        caliber == ECaliber.Shell12Cal ? shotgun : caliber == ECaliber.ShellHeavy ? heavy : null;
}
