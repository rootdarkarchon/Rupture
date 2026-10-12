using System;
using System.Collections.Generic;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.Limbs;
using UnityEngine;

namespace Rupture.Dismemberment.Classes.Fractures
{

internal static class FractureSystem
{
    private sealed class Actor : IDisposable
    {
        internal readonly Player Player;
        internal readonly IHealthController Health;
        internal readonly Slot[] Slots = new Slot[4];
        internal bool Dirty = true;
        internal float NextCheck;
        internal int Seen;
        internal Actor(Player player)
        {
            Player = player; Health = player.HealthController;
            Health.EffectAddedEvent += Changed; Health.EffectStartedEvent += Changed;
            Health.EffectUpdatedEvent += Changed; Health.EffectResidualEvent += Changed; Health.EffectRemovedEvent += Changed;
        }
        private void Changed(EFT.HealthSystem.IHealthEffect effect) { if (effect is EFT.HealthSystem.IFracture) Dirty = true; }
        internal void Reconcile(float now)
        {
            if (!Dirty && now < NextCheck) return;
            Dirty = false; NextCheck = now + 0.5f;
            int mask = FractureMask(Health);
            for (int i = 0; i < Slots.Length; i++)
            {
                if ((mask & (1 << i)) == 0) { Slots[i]?.Dispose(); Slots[i] = null; }
                else Slots[i] ??= new Slot(this, (EBodyPart)(i + 3));
            }
        }
        public void Dispose()
        {
            Health.EffectAddedEvent -= Changed; Health.EffectStartedEvent -= Changed;
            Health.EffectUpdatedEvent -= Changed; Health.EffectResidualEvent -= Changed; Health.EffectRemovedEvent -= Changed;
            for (int i = 0; i < Slots.Length; i++) { Slots[i]?.Dispose(); Slots[i] = null; }
        }
    }

    private sealed class Candidate
    {
        internal SkinnedMeshRenderer Renderer;
        internal Mesh Mesh;
        internal LODGroup Group;
        internal Transform SkinRoot, Parent;
        internal int Level, View;
        internal string Key, Failure;
        internal readonly FractureSurface[] Surfaces = new FractureSurface[2];
    }

    private sealed class Slot : IDisposable
    {
        internal readonly Actor Actor;
        internal readonly EBodyPart Part;
        internal readonly FractureAppearance Appearance;
        internal readonly List<Representation> Representations = new List<Representation>();
        private readonly List<Candidate> _candidates = new List<Candidate>();
        private readonly List<SkinnedMeshRenderer> _scratch = new List<SkinnedMeshRenderer>();
        private float _refresh;
        private readonly uint[] _bloodIds = new uint[2];
        private readonly float[] _bloodRetries = new float[2];
        private readonly FractureVisual[] _bloodWounds = new FractureVisual[2];
        private Transform _root, _end;
        internal string RootName, EndName;
        internal bool Disposed;
        internal Slot(Actor actor, EBodyPart part)
        { Actor = actor; Part = part; Appearance = new FractureAppearance(actor.Player.ProfileId, (int)part); }

        internal void Refresh(float now)
        {
            if (now < _refresh) return;
            _refresh = now + 0.5f;
            var body = Actor.Player.PlayerBody;
            if (body?.BodySkins == null || !LimbDismembermentSystem.TryGetLimbSegment(Actor.Player, Part, Appearance.Level,
                out Transform root, out Transform end)) { Clear(); Warn("fracture skeleton or clothing not ready"); return; }
            bool changed = _root != root || _end != end;
            _root = root; _end = end;
            RootName = root.name; EndName = end.name;
            _scratch.Clear();
            EBodyModelPart model = Appearance.IsLeg ? EBodyModelPart.Feet : EBodyModelPart.Body;
            Gather(model);
            int bodyCount = _scratch.Count;
            if (!Appearance.IsLeg) Gather(EBodyModelPart.Hands);
            if (_scratch.Count != _candidates.Count) changed = true;
            if (!changed) for (int i = 0; i < _scratch.Count; i++)
                if (_candidates[i].Renderer != _scratch[i] || _scratch[i] == null ||
                    _candidates[i].Mesh != _scratch[i].sharedMesh || _candidates[i].SkinRoot != _scratch[i].rootBone ||
                    _candidates[i].Parent != _scratch[i].transform.parent ||
                    _candidates[i].Group != _scratch[i].GetComponentInParent<LODGroup>()) { changed = true; break; }
            if (!changed) return;
            Clear();
            if (_scratch.Count > 64) { Warn("fracture representation exceeds 64 source renderers"); return; }
            for (int i = 0; i < _scratch.Count; i++)
            {
                var renderer = _scratch[i]; var group = renderer.GetComponentInParent<LODGroup>();
                int level = -1;
                if (group != null)
                {
                    LOD[] lods = group.GetLODs();
                    for (int lod = 0; lod < lods.Length; lod++)
                        if (Array.IndexOf(lods[lod].renderers ?? Array.Empty<Renderer>(), renderer) >= 0) { level = lod; break; }
                }
                var candidate = new Candidate { Renderer = renderer, Mesh = renderer.sharedMesh, Group = group,
                    SkinRoot = renderer.rootBone, Parent = renderer.transform.parent,
                    Level = level, View = i < bodyCount ? 0 : 1, Key = HierarchyKey(renderer.transform) };
                _candidates.Add(candidate);
                for (int site = 0; site < Appearance.SiteCount; site++)
                {
                    Representation representation = null;
                    foreach (var existing in Representations)
                        if (existing.Level == level && existing.View == candidate.View && existing.Appearance.Site == site)
                        { representation = existing; break; }
                    if (representation == null)
                    { representation = new Representation(this, level, candidate.View, Appearance.ForSite(site)); Representations.Add(representation); }
                    representation.Candidates.Add(candidate);
                }
            }
            // Native LOD arbitration must be common to overlapping sources. Never guess
            // a camera LOD or attach a hidden skin wound across unsynchronized groups.
            foreach (var representation in Representations)
            {
                LODGroup group = representation.Candidates[0].Group;
                foreach (var candidate in representation.Candidates)
                    if (candidate.Group != group) candidate.Failure = "overlapping fracture coverings use different LOD groups";
            }

            void Gather(EBodyModelPart part)
            {
                if (!body.BodySkins.TryGetValue(part, out var garment) || garment == null) return;
                foreach (Renderer candidate in garment.GetRenderers())
                    if (candidate is SkinnedMeshRenderer skin && !_scratch.Contains(skin)) _scratch.Add(skin);
            }
        }
        internal void Present(float now)
        {
            Array.Clear(_bloodWounds, 0, _bloodWounds.Length);
            foreach (var representation in Representations)
            {
                representation.Present(now);
                var visual = representation.Visible;
                if (visual?.Renderer == null || !visual.Renderer.enabled) continue;
                // One emitter per wound site, shared across camera/LOD variants.
                // Prefer a surface currently drawn by a camera, with stable list ties.
                int site = representation.Appearance.Site;
                var wound = _bloodWounds[site];
                if (wound == null || (!wound.Owner.Renderer.isVisible && visual.Owner.Renderer.isVisible)) _bloodWounds[site] = visual;
            }
            for (int site = 0; site < Appearance.SiteCount; site++)
            {
                var wound = _bloodWounds[site];
                if (wound == null || !wound.TryGetWound(out Transform anchor, out Vector3 point, out Vector3 direction))
                { StopBlood(site); continue; }
                if (_bloodIds[site] != 0 && PulsatingBloodStreamSystem.UpdateFractureWound(_bloodIds[site], anchor, point, direction)) continue;
                if (now < _bloodRetries[site]) continue;
                _bloodRetries[site] = now + 0.5f;
                _bloodIds[site] = PulsatingBloodStreamSystem.StartFractureWound(Actor.Player, Part, anchor, point, direction, Appearance.ForSite(site).BloodSeed);
            }
        }
        private void StopBlood(int site)
        { PulsatingBloodStreamSystem.StopFractureWound(_bloodIds[site]); _bloodIds[site] = 0; _bloodRetries[site] = 0f; _bloodWounds[site] = null; }
        private void Clear()
        { for (int site = 0; site < Appearance.SiteCount; site++) StopBlood(site);
            foreach (var representation in Representations) representation.Dispose(); Representations.Clear(); _candidates.Clear(); }
        public void Dispose() { Disposed = true; Clear(); }
    }

    private sealed class Representation : IDisposable
    {
        internal readonly Slot Slot;
        internal readonly FractureAppearance Appearance;
        internal readonly int Level, View;
        internal readonly List<Candidate> Candidates = new List<Candidate>();
        private readonly Dictionary<ulong, FractureVisual> _variants = new Dictionary<ulong, FractureVisual>();
        private readonly HashSet<ulong> _failed = new HashSet<ulong>();
        private FractureVisual _visible;
        internal FractureVisual Visible => _visible;
        private bool _disposed;
        internal bool Enqueued;
        private float _retry;
        internal Representation(Slot slot, int level, int view, FractureAppearance appearance)
        { Slot = slot; Level = level; View = view; Appearance = appearance; }
        private ulong VisibleMask()
        {
            ulong mask = 0;
            for (int i = 0; i < Candidates.Count; i++)
            {
                var candidate = Candidates[i];
                // A replaced covering invalidates the whole selection until Refresh.
                if (candidate.Renderer == null || candidate.Renderer.sharedMesh != candidate.Mesh) return 0;
                if (FractureVisual.Visible(candidate.Renderer)) mask |= 1UL << i;
            }
            return mask;
        }
        internal void Present(float now)
        {
            if (_disposed || Slot.Disposed) return;
            ulong mask = VisibleMask();
            _variants.TryGetValue(mask, out FractureVisual next);
            if (_visible != next) { _visible?.Show(false); _visible = next; }
            _visible?.Show(true);
            if (mask != 0 && next == null && !_failed.Contains(mask) && now >= _retry) Queue(this);
        }
        internal void Work(float now)
        {
            if (_disposed || Slot.Disposed || !Enabled || !Slot.Actor.Health.IsAlive) return;
            ulong mask = VisibleMask();
            if (mask == 0 || _failed.Contains(mask) || _variants.ContainsKey(mask)) return;
            for (int i = 0; i < Candidates.Count; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                Candidate candidate = Candidates[i];
                if (candidate.Failure != null) { Reject(mask, candidate.Failure); return; }
                if (candidate.Surfaces[Appearance.Site] != null) continue;
                try
                {
                    if (!Sources.TryGetValue(candidate.Mesh, out var data))
                    { data = new FractureSurface.Snapshot(candidate.Mesh); Sources.Add(candidate.Mesh, data); }
                    candidate.Surfaces[Appearance.Site] = new FractureSurface(candidate.Renderer, data, Slot.RootName, Slot.EndName, Appearance, candidate.Key);
                }
                catch (Exception exception) { candidate.Failure = exception.Message; Reject(mask, candidate.Failure); return; }
                Queue(this); return; // At most one source capture or mesh construction per work step.
            }
            if (!LimbSeverMaterialProvider.TryGet(out Material material, out string failure))
            { _retry = now + 1f; Warn("fracture material: " + failure); return; }
            FractureSurface owner = null; float distance = -1f;
            for (int i = 0; i < Candidates.Count; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                var source = Candidates[i].Surfaces[Appearance.Site];
                if (source.Raycast(source.Origin, out var hit) &&
                    (owner == null || FractureSurface.Prefer(hit.Distance, source.StableKey, distance, owner.StableKey)))
                { owner = source; distance = hit.Distance; }
            }
            if (owner == null) { Reject(mask, "fracture site misses eligible outer covering"); return; }
            try
            {
                var visual = new FractureVisual(owner, Appearance, material);
                if (_variants.Count >= 16)
                {
                    ulong evict = 0; foreach (var entry in _variants) if (entry.Value != _visible) { evict = entry.Key; break; }
                    if (_variants.TryGetValue(evict, out var old)) { old.Dispose(); _variants.Remove(evict); }
                }
                _variants.Add(mask, visual);
            }
            catch (Exception exception) { Reject(mask, exception.Message); }
        }
        private void Reject(ulong mask, string reason)
        { _failed.Add(mask); Warn($"fracture {Slot.Part}, site={Appearance.Site}, view={View}, LOD={Level}: {reason}"); }
        public void Dispose()
        { _disposed = true; foreach (var visual in _variants.Values) visual.Dispose(); _variants.Clear(); _visible = null; }
    }

    private static readonly List<Actor> Actors = new List<Actor>();
    private static readonly Dictionary<Mesh, FractureSurface.Snapshot> Sources = new Dictionary<Mesh, FractureSurface.Snapshot>();
    private static readonly Queue<Representation> Pending = new Queue<Representation>();
    private static readonly HashSet<string> Warnings = new HashSet<string>();
    private static float _nextScan;
    private static int _scan;
    private static bool Enabled => RuptureConfiguration.EnableBloodEffects?.Value == true && !MultiplayerRuntime.Current.IsHeadless;
    internal static int FractureMask(IHealthController health)
    {
        IReadOnlyList<EFT.HealthSystem.IHealthEffect> effects = Rupture.Health.NativeHealthEffects.TryGet(health);
        int mask = 0;
        if (effects == null)
        { for (int i = 0; i < 4; i++) if (health.IsBodyPartBroken((EBodyPart)(i + 3))) mask |= 1 << i; return mask; }
        for (int i = 0; i < effects.Count; i++)
        {
            EFT.HealthSystem.IHealthEffect effect = effects[i]; int part = (int)effect.BodyPart - 3;
            if (part >= 0 && part < 4 && effect is EFT.HealthSystem.IFracture && effect.Active) mask |= 1 << part;
        }
        return mask;
    }
    private static void Queue(Representation representation)
    { if (!representation.Enqueued) { representation.Enqueued = true; Pending.Enqueue(representation); } }
    internal static void Update()
    {
        if (!Enabled || !Singleton<GameWorld>.Instantiated)
        { if (Actors.Count != 0 || Pending.Count != 0) Reset(); return; }
        float now = Time.realtimeSinceStartup;
        if (now >= _nextScan)
        {
            _nextScan = now + 0.5f; _scan++;
            var players = Singleton<GameWorld>.Instance.AllAlivePlayersList;
            for (int i = 0; i < players.Count; i++)
            {
                Player player = players[i];
                if (player?.HealthController == null || !player.HealthController.IsAlive || string.IsNullOrEmpty(player.ProfileId)) continue;
                Actor actor = null;
                for (int j = 0; j < Actors.Count; j++) if (Actors[j].Player == player) { actor = Actors[j]; break; }
                if (actor == null) { actor = new Actor(player); Actors.Add(actor); }
                actor.Seen = _scan;
            }
        }
        for (int i = Actors.Count - 1; i >= 0; i--)
        {
            Actor actor = Actors[i];
            if (actor.Player == null || !actor.Health.IsAlive || actor.Player.HealthController != actor.Health || actor.Seen != _scan)
            { actor.Dispose(); Actors.RemoveAt(i); continue; }
            actor.Reconcile(now);
            for (int j = 0; j < actor.Slots.Length; j++) actor.Slots[j]?.Refresh(now);
        }
        // A soft two-ms budget with one indivisible capture/build at a time.
        long started = Stopwatch.GetTimestamp(); int initial = Pending.Count;
        while (initial-- > 0 && Pending.Count > 0)
        {
            var representation = Pending.Dequeue(); representation.Enqueued = false;
            representation.Work(now);
            if ((Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency >= 2d) break;
        }
    }
    internal static void LateUpdate()
    {
        if (!Enabled) { if (Actors.Count != 0) Reset(); return; }
        float now = Time.realtimeSinceStartup;
        for (int i = Actors.Count - 1; i >= 0; i--)
        {
            Actor actor = Actors[i];
            if (actor.Player == null || !actor.Health.IsAlive || actor.Player.HealthController != actor.Health)
            { actor.Dispose(); Actors.RemoveAt(i); continue; }
            actor.Reconcile(now); // Effects changed after our Update must disappear before rendering.
            for (int j = 0; j < actor.Slots.Length; j++)
                actor.Slots[j]?.Present(now);
        }
    }
    internal static void Reset()
    { foreach (var actor in Actors) actor.Dispose(); Actors.Clear(); Pending.Clear(); Sources.Clear(); Warnings.Clear(); _nextScan = 0; _scan = 0; }
    private static void Warn(string message)
    { if (Warnings.Count < 64 && Warnings.Add(message)) RupturePlugin.Log.LogWarning("RPT " + message); }
    private static string HierarchyKey(Transform transform)
    {
        string key = string.Empty;
        for (Transform current = transform; current != null; current = current.parent)
            key = current.GetSiblingIndex().ToString("D5") + ":" + current.name + "/" + key;
        return key;
    }
}
}
