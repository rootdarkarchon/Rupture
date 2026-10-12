using System.Collections.Generic;
using EFT.Ballistics;
using UnityEngine;

namespace Rupture.Impacts;

// Native blast radius/identity is retained briefly for delayed grenade fragments.
// No guessed radius or unlimited-range fragment severing when metadata is absent.
internal static class LivingExplosionEvents
{
    private readonly struct Blast
    {
        internal readonly long Sequence;
        internal readonly Vector3 Center;
        internal readonly float Radius, Expires;
        internal readonly string Weapon;
        internal Blast(long sequence, Vector3 center, float radius, string weapon, float now)
        { Sequence = sequence; Center = center; Radius = radius; Weapon = weapon; Expires = now + 15f; }
    }
    private static readonly List<Blast> Blasts = new List<Blast>();
    internal static void Register(string weapon) => RegisterAt(weapon, Time.realtimeSinceStartup);
    internal static void RegisterAt(string weapon, float now)
    {
        if (!ExplosionEventScope.TryGetBlast(out long sequence, out Vector3 center, out float radius)) return;
        Prune(now);
        if (Blasts.Count >= 128) Blasts.RemoveAt(0);
        Blasts.Add(new Blast(sequence, center, radius, weapon, now));
    }
    internal static bool Resolve(DamageInfo damage, ImpactData sample, out long sequence, out Vector3 center, out float radius)
        => ResolveAt(damage, sample, Time.realtimeSinceStartup, out sequence, out center, out radius);
    internal static bool ResolveAt(DamageInfo damage, ImpactData sample, float now, out long sequence, out Vector3 center, out float radius)
    {
        sequence = 0; center = default; radius = 0f;
        if (!DeathImpactPolicy.IsExplosion(damage.DamageType)) return false;
        bool nativeOrigin = ExplosionSeverPolicy.Finite(damage.MasterOrigin) && damage.MasterOrigin != Vector3.zero;
        bool sampledOrigin = sample.HasMasterOrigin && ExplosionSeverPolicy.Finite(sample.MasterOrigin);
        Vector3 origin = nativeOrigin ? damage.MasterOrigin : sample.MasterOrigin;
        if (ExplosionEventScope.TryGetBlast(out long active, out Vector3 activeCenter, out float activeRadius) &&
            ((!nativeOrigin && !sampledOrigin) || !nativeOrigin && activeCenter == Vector3.zero ||
                (activeCenter - origin).sqrMagnitude <= .000001f))
        { sequence = active; center = activeCenter; radius = activeRadius; return true; }
        if (!nativeOrigin && !sampledOrigin) return false;
        string weapon = damage.Weapon?.Id;
        Prune(now);
        int match = -1;
        for (int i = Blasts.Count - 1; i >= 0; i--)
        {
            Blast blast = Blasts[i];
            if ((blast.Center - origin).sqrMagnitude > .000001f ||
                !string.IsNullOrEmpty(weapon) && !string.IsNullOrEmpty(blast.Weapon) && weapon != blast.Weapon) continue;
            // Identical centers/source IDs cannot identify which old blast a
            // delayed fragment came from. Keep radial damage valid, but do not
            // guess an identity that could grant an extra roll for that fragment.
            if (match >= 0) return false;
            match = i;
        }
        if (match < 0) return false;
        Blast resolved = Blasts[match]; sequence = resolved.Sequence; center = resolved.Center; radius = resolved.Radius; return true;
    }
    private static void Prune(float now)
    {
        for (int i = Blasts.Count - 1; i >= 0; i--) if (Blasts[i].Expires < now) Blasts.RemoveAt(i);
    }
    internal static void Reset() => Blasts.Clear();
}
