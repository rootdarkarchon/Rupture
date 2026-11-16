using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using HarmonyLib;
using UnityEngine;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;
using TraceStage = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadStage;
using TraceWork = Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork;

namespace Rupture.Casings;

internal sealed class CasingWorld
{
    private sealed class Source
    {
        internal bool Used, Spawned, Activated;
        internal GameWorld World;
        internal Vector3 Position, Velocity, Spin;
    }
    private sealed class ShotIdentity { internal ulong Id; }
    private sealed class Entry
    {
        internal ulong Id;
        internal AmmoPoolObject Visual;
        internal Source Source;
        internal CasingDescriptor Descriptor;
        internal ECaliber Caliber;
        internal bool Submitted, Committed, Sleeping, RetireFallback;
        /// <summary>The local player fired this casing; vanilla then plays its contact sounds without the occlusion test.</summary>
        internal bool LocalShooter;
        internal float Deadline, Countdown, LastSound;
        /// <summary>Realtime a new casing first waited for an urgent pending environment cell; negative while it has not.</summary>
        internal float HeldSince = -1f;
        internal Vector3 Position, Velocity;
        internal LinkedListNode<Entry> Node;
        internal Collider[] Colliders;
        internal bool[] ColliderEnabled;
        internal Rigidbody[] Bodies;
        internal bool[] BodyKinematic, BodyCollisions;
        internal Vector3[] BodyVelocity, BodySpin;
        internal readonly HashSet<Vector3Int> Coverage = new HashSet<Vector3Int>();
        internal readonly HashSet<Vector3Int> DesiredCoverage = new HashSet<Vector3Int>();
    }
    /// <summary>One consumer's hold on a metre-cell environment region, see <see cref="AcquireCell"/>.</summary>
    private class Support
    {
        internal uint Consumer;
        /// <summary>The consumer holds the region's actors (a ready acquisition or a synchronous capture succeeded).</summary>
        internal bool Bound;
        internal float Refreshed = float.NegativeInfinity;
        /// <summary>Last <see cref="_epoch"/> the backend was asked about this cell; a pending cell is asked at most once per step.</summary>
        internal int PolledEpoch = -1;
    }
    private sealed class Cell : Support
    {
        internal int References;
        internal readonly HashSet<Entry> Owners = new HashSet<Entry>();
    }
    private sealed class ExposureCell : Support
    {
        internal float LastUsed;
    }
    private struct PendingImpact
    {
        internal CasingImpact Value;
        /// <summary>Realtime the impact first waited for a pending exposure cell; negative while it has not.</summary>
        internal float HeldSince;
        internal PendingImpact(CasingImpact value) { Value = value; HeldSince = -1f; }
    }
    /// <summary>
    /// Casing support and exposure consumers use <c>0x4000_0000..0x7FFF_FFFF</c> (bit 30 set, bit 31 clear). That is disjoint from raw
    /// <c>ShadowRig.ExternalGroup</c> sequence numbers (they never reach bit 30), from bot and local-player prefetches
    /// (<c>0x8000_0000 | Player.Id</c>) and from rig corridor prefetches (<c>0xC000_0000 | group</c>). The sequence wraps inside the range
    /// after 2^30 allocations, far beyond what a raid creates.
    /// </summary>
    private const uint CellConsumerFlag = 0x4000_0000u;
    private const uint CellConsumerMask = 0x3FFF_FFFFu;
    /// <summary>The local player's area prefetch uses the bot formula, <c>0x8000_0000 | (Player.Id &amp; 0x3FFF_FFFF)</c>.</summary>
    private const uint AreaConsumerFlag = 0x8000_0000u;
    private const uint AreaConsumerMask = 0x3FFF_FFFFu;
    /// <summary>A bound cell is re-validated (a ready acquisition only classifies, it never captures) at most this often.</summary>
    private const float CellRefreshSeconds = 1f;
    /// <summary>How long a not-yet-submitted casing or an impact waits for pending urgent cells before the synchronous capture runs.</summary>
    private const float PendingHoldSeconds = .2f;
    /// <summary>
    /// Cells the next step can reach must be bound before submission: at least the whole step (the graphical step simulates the full
    /// unscaled frame time, see <c>GraphicalSimulationPolicy</c>), and three frame intervals within <see cref="MinimumUrgentSeconds"/> and
    /// <see cref="MaximumUrgentSeconds"/> otherwise, so a long frame never outruns the bound cells.
    /// </summary>
    private const float MinimumUrgentSeconds = .05f;
    private const float MaximumUrgentSeconds = .1f;
    /// <summary>Exposure cells outlive the step that used them so repeated impacts in one area reuse them.</summary>
    private const float ExposureRetentionSeconds = 1f;
    private const float AreaPrefetchIntervalSeconds = .5f;
    private const float AreaPrefetchTimeToLiveSeconds = 4f;
    private const float AreaPrefetchDistance = 1f;
    private const float AreaPrefetchLookaheadSeconds = 1f;
    private const float AreaPrefetchMaximumSweep = 6f;
    private const float AreaPrefetchCenterHeight = 1f;
    /// <summary>Contact sounds started per frame at most; the rest of a burst is counted in the audio-dropped total.</summary>
    private const int MaximumSoundsPerFrame = 12;
    private static readonly IComparer<CasingContact> SpeedDescending = new SpeedDescendingComparer();
    private sealed class SpeedDescendingComparer : IComparer<CasingContact>
    {
        public int Compare(CasingContact a, CasingContact b) => b.Speed.CompareTo(a.Speed);
    }
    private static readonly FieldInfo NativeWorldCasings = AccessTools.Field(typeof(GameWorld), "_ammoShells");
    private static readonly FieldInfo NativeCaliber = AccessTools.Field(typeof(Shell), "_caliber");
    private ConditionalWeakTable<AmmoPoolObject, Source> _sources = new ConditionalWeakTable<AmmoPoolObject, Source>();
    private ConditionalWeakTable<EFT.Ballistics.Shot, ShotIdentity> _shots = new ConditionalWeakTable<EFT.Ballistics.Shot, ShotIdentity>();
    private readonly Dictionary<ulong, Entry> _entries = new Dictionary<ulong, Entry>();
    private readonly Dictionary<ulong, Entry> _retiring = new Dictionary<ulong, Entry>();
    private readonly Dictionary<AmmoPoolObject, Entry> _byVisual = new Dictionary<AmmoPoolObject, Entry>();
    private readonly LinkedList<Entry> _order = new LinkedList<Entry>();
    private readonly Dictionary<(string, Vector3), CasingDescriptor> _geometry = new Dictionary<(string, Vector3), CasingDescriptor>();
    private readonly Dictionary<Vector3Int, Cell> _cells = new Dictionary<Vector3Int, Cell>();
    private readonly List<ulong> _removes = new List<ulong>();
    private readonly List<PendingImpact> _impacts = new List<PendingImpact>();
    private readonly List<Entry> _pending = new List<Entry>();
    private readonly List<Entry> _moving = new List<Entry>();
    private readonly List<Vector3Int> _cellScratch = new List<Vector3Int>();
    private readonly HashSet<Entry> _affected = new HashSet<Entry>();
    private readonly Dictionary<Vector3Int, ExposureCell> _exposureCells = new Dictionary<Vector3Int, ExposureCell>();
    private readonly List<Vector3Int> _exposureScratch = new List<Vector3Int>();
    private readonly List<CasingContact> _audible = new List<CasingContact>(512);
    private ExternalCasingFrame _frame;
    private IExternalPhysicsRuntime _runtime;
    private Transform _root;
    private AmmoPoolObject _releasing;
    private ulong _nextId, _nextEvent;
    private uint _consumerSequence;
    private bool _failed, _warned;
    private int _awake;
    private bool _audioWarm;
    private float _lastStatus;
    private long _admitted, _evicted, _fallbacks, _poses, _sounds, _audioDropped;
    private long _syncPending, _syncDirect, _holds, _areaRequests;
    private double _mainMs;
    private double _poseMs, _audioMs, _commandMs, _simulationMs, _readMs;
    private IExternalEnvironmentPrefetchRuntime _prefetch, _areaRuntime;
    private bool _prefetchFaulted, _frameInFlight, _areaActive;
    private int _epoch;
    private float _areaNextCheck, _areaRequestedAt;
    private uint _areaConsumer;
    private Vector3 _areaCenter;

    internal CasingWorld()
    {
        if (NativeWorldCasings?.FieldType != typeof(List<AmmoPoolObject>) || NativeCaliber?.FieldType != typeof(ECaliber))
            throw new MissingFieldException("The installed casing ownership ABI does not match.");
    }
    private bool Enabled => !_failed && RuptureConfiguration.PersistentCasings.Value && RuptureConfiguration.MaximumCasings.Value > 0;
    internal bool HasWork()
    {
        // The only callback that runs every simulation frame even while no casing exists, so the player's area is warmed before the first shot.
        MaintainAreaPrefetch();
        if (!_audioWarm)
        {
            if (Enabled && RuptureConfiguration.CasingSounds.Value) WarmAudio();
            // The gate closed mid warm-up (setting turned off): WarmAudio would never run again to return the borrowed sources.
            else if (CasingAudio.HoldsSources) CasingAudio.CancelWarmup();
        }
        // An idle world takes no step, so neither ReceiveCompleted nor BeforeStep would release exposure cells past their retention.
        if (_runtime != null && !_frameInFlight && _exposureCells.Count != 0)
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingExposureRelease)) ReleaseIdleExposure(_runtime);
        return _awake != 0 || _pending.Count != 0 || _moving.Count != 0 || _impacts.Count != 0 ||
            _removes.Count != 0 || _retiring.Count != 0 || _order.Count > (Enabled ? RuptureConfiguration.MaximumCasings.Value : 0);
    }
    internal void SetUsed(AmmoPoolObject visual, bool used) => _sources.GetOrCreateValue(visual).Used = used;
    internal void Spawn(GameWorld world, AmmoPoolObject visual)
    {
        if (visual == null) return;
        Source source = _sources.GetOrCreateValue(visual);
        source.World = world; source.Spawned = true; source.Activated = false;
    }
    internal void Activated(Shell shell, Vector3 position, Vector3 velocity, Vector3 rotation, Vector3 forward)
    {
        AmmoPoolObject visual = shell.GetComponent<AmmoPoolObject>();
        if (visual == null || !_sources.TryGetValue(visual, out Source source) || !source.Spawned) return;
        var settings = EFTHardSettings.Instance.Shells;
        if (settings.velocityRotation > 0f) velocity = Quaternion.AngleAxis(settings.velocityRotation, forward) * velocity;
        source.Position = position;
        source.Velocity = velocity * (settings.velocityMult * settings.playMult);
        source.Spin = shell.transform.rotation * (rotation * (100f * Mathf.Deg2Rad));
        source.Activated = true;
    }
    internal void Admit(AmmoPoolObject visual, float countdown)
    {
        if (!Enabled || visual == null || _byVisual.ContainsKey(visual) ||
            !_sources.TryGetValue(visual, out Source source) || !source.Used || !source.Spawned || !source.Activated) return;
        IExternalPhysicsRuntime runtime = ShadowRagdollWorld.LocalSimulationRuntime;
        if (runtime?.IsRaidReady != true || source.World == null ||
            !CasingGeometry.Finite(source.Position) || !CasingGeometry.Finite(source.Velocity) || !CasingGeometry.Finite(source.Spin))
        { _fallbacks++; return; }
        var key = (visual.Name, visual.transform.lossyScale);
        if (!_geometry.TryGetValue(key, out CasingDescriptor descriptor))
        {
            if (!CasingGeometry.TryFit(visual, out descriptor, out _)) { Warn("unsupported spent-casing geometry: " + visual.name); _fallbacks++; return; }
            _geometry.Add(key, descriptor);
        }
        int layer = LayerMask.NameToLayer("Deadbody");
        if (layer < 0) return;
        descriptor.Id = ++_nextId;
        descriptor.Pose = new CasingPose(source.Position, visual.transform.rotation);
        descriptor.Velocity = source.Velocity; descriptor.AngularVelocity = source.Spin;
        descriptor.LayerBits = 1u << layer;
        descriptor.CollisionMask = (uint)ShadowRagdollWorld.LocalEnvironmentMask | descriptor.LayerBits;
        while (_order.Count >= RuptureConfiguration.MaximumCasings.Value) Release(_order.First.Value, false);
        var entry = new Entry { Id = descriptor.Id, Visual = visual, Source = source, Descriptor = descriptor,
            Position = source.Position, Velocity = source.Velocity, Countdown = countdown,
            Deadline = Time.realtimeSinceStartup + 2f, Caliber = (ECaliber)NativeCaliber.GetValue(visual.Shell),
            // Read before Publish clears the listener when the casing is committed.
            LocalShooter = CasingAudio.IsLocalShooter(visual.Shell.CollisionListener) };
        entry.Node = _order.AddLast(entry); _entries.Add(entry.Id, entry); _byVisual.Add(visual, entry);
        _awake++;
        entry.Colliders = visual.GetComponentsInChildren<Collider>(true);
        entry.ColliderEnabled = new bool[entry.Colliders.Length];
        for (int i = 0; i < entry.Colliders.Length; i++) { entry.ColliderEnabled[i] = entry.Colliders[i].enabled; entry.Colliders[i].enabled = false; }
        entry.Bodies = visual.GetComponentsInChildren<Rigidbody>(true);
        entry.BodyKinematic = new bool[entry.Bodies.Length]; entry.BodyCollisions = new bool[entry.Bodies.Length];
        entry.BodyVelocity = new Vector3[entry.Bodies.Length]; entry.BodySpin = new Vector3[entry.Bodies.Length];
        for (int i = 0; i < entry.Bodies.Length; i++)
        {
            Rigidbody body = entry.Bodies[i];
            entry.BodyKinematic[i] = body.isKinematic; entry.BodyCollisions[i] = body.detectCollisions;
            entry.BodyVelocity[i] = body.velocity; entry.BodySpin[i] = body.angularVelocity;
            body.detectCollisions = false; body.isKinematic = true;
        }
        _pending.Add(entry);
        ObjectLifetime.Bind(visual.gameObject, "casing", () => Release(entry, false));
        visual.Shell.DisablePhysics(); visual.enabled = false; visual.ShouldBeDestroyed = false;
        _admitted++;
    }
    internal bool IsOwned(AmmoPoolObject visual) => visual != null && _byVisual.ContainsKey(visual);
    internal bool AllowReturn(AmmoPoolObject visual)
    {
        if (ReferenceEquals(visual, null)) return true;
        if (!ReferenceEquals(visual, _releasing) && _byVisual.ContainsKey(visual)) return false;
        _sources.Remove(visual);
        return true;
    }
    internal void NewShot(EFT.Ballistics.Shot shot)
    {
        if (shot != null) _shots.GetOrCreateValue(shot).Id = ++_nextEvent;
    }
    internal void Trace(EFT.Ballistics.Shot shot, Vector3 start, Vector3 end, bool hit)
    {
        if (!Enabled || _order.Count == 0 || shot == null || _impacts.Count >= 4096) return;
        ShotIdentity identity = _shots.GetOrCreateValue(shot);
        if (identity.Id == 0) identity.Id = ++_nextEvent;
        Vector3 delta = end - start;
        if (hit)
        {
            // Backward native traces can produce hits outside this segment.
            float t = delta.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(shot.HitPoint - start, delta) / delta.sqrMagnitude) : 0;
            end = start + delta * t;
        }
        if (!CasingGeometry.Finite(start) || !CasingGeometry.Finite(end)) return;
        QueueImpact(new CasingImpact { EventId = identity.Id, Start = start, End = end,
            Direction = delta.normalized, Strength = Mathf.Max(0, shot.BulletMassGram * .001f * shot.VelocityMagnitude) });
        Collider collider = hit ? shot.HitCollider : null;
        if (RuptureConfiguration.CasingImpactScatter.Value && collider != null &&
            (ShadowRagdollWorld.LocalEnvironmentMask & (1 << collider.gameObject.layer)) != 0)
            QueueImpact(new CasingImpact { EventId = identity.Id, Kind = 2, Start = end + shot.HitNormal * .015f,
                Direction = shot.HitNormal, Radius = .35f, Strength = 2f });
    }
    internal void Explosion(IExplosiveItem explosive, Vector3 position)
    {
        if (!Enabled || _order.Count == 0 || explosive == null || explosive.IsDummy || _impacts.Count >= 4096) return;
        QueueImpact(new CasingImpact { EventId = ++_nextEvent, Kind = 1, Start = position,
            Radius = Mathf.Clamp(explosive.MaxExplosionDistance, 0, 200), Strength = Mathf.Clamp(explosive.GetStrength * .5f, 0, 12) });
    }
    private void QueueImpact(CasingImpact impact) => _impacts.Add(new PendingImpact(impact));

    // Consume final sleeping poses and removal acknowledgements even when no
    // further simulation is necessary. Keep the cleared frame for the next wake.
    internal void ReceiveCompleted(IExternalPhysicsRuntime runtime)
    {
        if (!ReferenceEquals(_runtime, runtime))
        {
            _runtime = runtime;
            _prefetch = runtime as IExternalEnvironmentPrefetchRuntime;
        }
        ExternalCasingFrame completed = runtime.TakeCasingFrame();
        if (completed != null)
        {
            _frameInFlight = false;
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingReceiveAndRetire))
            {
                _frame = completed;
                _commandMs = completed.CommandMilliseconds; _simulationMs = completed.SimulationMilliseconds; _readMs = completed.ReadMilliseconds;
                for (int index = 0; index < completed.RemoveCount; index++)
                    if (_retiring.TryGetValue(completed.Removes[index], out Entry retired))
                    { _retiring.Remove(retired.Id); ReleaseVisual(retired, retired.RetireFallback); }
            }
            Publish(completed);
            completed.Clear();
        }
        // Support cells have separate persistent references and remain alive while casings sleep. Exposure cells served the
        // completed query batch and are kept briefly for the next impact; an in-flight batch still needs them.
        if (_frameInFlight) return;
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingExposureRelease)) ReleaseIdleExposure(runtime);
    }

    // Invoked after completing the previous scene step, before submitting the next.
    internal void BeforeStep(IExternalPhysicsRuntime runtime)
    {
        long started = Stopwatch.GetTimestamp();
        ReceiveCompleted(runtime);
        _epoch++;
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingListenerAndCapacity))
        {
            _frame ??= new ExternalCasingFrame();
            Camera listener = RuptureConfiguration.CasingSounds.Value ? Camera.main : null;
            _frame.Listener = listener != null ? listener.transform.position : Vector3.zero;
            _frame.Audio = listener != null;
            int capacity = Enabled ? RuptureConfiguration.MaximumCasings.Value : 0;
            while (_order.Count > capacity) Release(_order.First.Value, false);
        }
        try
        {
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingAdmission))
            {
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingPendingEntries, _pending.Count);
                int creates = 0;
                for (int pendingIndex = _pending.Count - 1; pendingIndex >= 0; pendingIndex--)
                {
                    Entry entry = _pending[pendingIndex];
                    if (!_entries.ContainsKey(entry.Id) || entry.Committed) { _pending.RemoveAt(pendingIndex); continue; }
                    if (entry.Visual == null) { Release(entry, false); continue; }
                    if (!entry.Committed && Time.realtimeSinceStartup > entry.Deadline) { Release(entry, true); continue; }
                    if (!entry.Submitted && creates < 128)
                    {
                        bool covered;
                        try { covered = EnsureCoverage(entry, entry.Position, entry.Velocity, Time.unscaledDeltaTime); }
                        catch (Exception exception)
                        {
                            Warn("casing admission rejected: " + exception.Message);
                            Release(entry, true);
                            continue;
                        }
                        // Held until the urgent cells are bound; the admission deadline still bounds the wait.
                        if (!covered) continue;
                        _frame.Create(entry.Descriptor); entry.Submitted = true; creates++;
                    }
                }
            }
            using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingMovingCoverage))
            {
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingMovingEntries, _moving.Count);
                foreach (Entry entry in _moving)
                    if (_entries.ContainsKey(entry.Id)) EnsureCoverage(entry, entry.Position, entry.Velocity, Time.unscaledDeltaTime);
                _moving.Clear();
            }
            // Environmental coverage must exist before a newly woken actor can travel.
            // This admission pass uses cached cells and poses, never Unity collider scans per casing.
            ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingImpactEvents, _impacts.Count);
            int kept = 0;
            for (int impactIndex = 0; impactIndex < _impacts.Count; impactIndex++)
            {
                PendingImpact pending = _impacts[impactIndex];
                CasingImpact impact = pending.Value;
                using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingImpactSelection))
                {
                    ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingImpactCells, _cells.Count);
                    _affected.Clear();
                    var query = new CasingImpactQuery(impact);
                    if (query.TryCellBounds(_cells.Count, out Vector3Int min, out Vector3Int max))
                    {
                        long visits = 0;
                        for (int x = min.x; x <= max.x; x++) for (int y = min.y; y <= max.y; y++) for (int z = min.z; z <= max.z; z++)
                        {
                            visits++; var key = new Vector3Int(x, y, z);
                            if (_cells.TryGetValue(key, out Cell cell)) CollectAffected(query, key, cell);
                        }
                        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingImpactQueries, visits);
                    }
                    else
                    {
                        foreach (var pair in _cells) CollectAffected(query, pair.Key, pair.Value);
                        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingImpactQueries, _cells.Count);
                    }
                }
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingAffectedEntries, _affected.Count);
                bool exposed = true;
                if (impact.Kind != 0)
                    foreach (Entry entry in _affected)
                        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingImpactExposure))
                            if (!EnsureExposure(impact.Start, entry.Position, ref pending.HeldSince)) exposed = false;
                if (!exposed)
                {
                    // The blast/scatter occlusion geometry is still being captured: replay the impact next step instead of sending it through a missing wall.
                    _impacts[kept++] = pending;
                    continue;
                }
                foreach (Entry entry in _affected)
                {
                    Vector3 velocity = impact.Kind == 0 ? impact.Direction * 20f :
                        ((entry.Position - impact.Start).normalized + (impact.Kind == 2 ? impact.Direction : Vector3.zero)).normalized * impact.Strength;
                    using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingImpactCoverage))
                        EnsureCoverage(entry, entry.Position, entry.Velocity + velocity, Time.unscaledDeltaTime);
                }
                _frame.Impact(impact);
            }
            _impacts.RemoveRange(kept, _impacts.Count - kept);
        }
        catch (Exception exception) { Fail(); Warn("casing environment preparation failed: " + exception.Message); }
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingQueueSubmit))
        {
            foreach (ulong id in _removes) _frame.Remove(id);
            _removes.Clear();
            runtime.QueueCasingFrame(_frame); _frame = null; _frameInFlight = true;
        }
        _mainMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        if (RuptureConfiguration.EnableDebugLogging.Value && Time.realtimeSinceStartup - _lastStatus >= 5f)
        {
            _lastStatus = Time.realtimeSinceStartup;
            RupturePlugin.Log.LogInfo($"RPT casings: retained={_order.Count}, retiring={_retiring.Count}, cells={_cells.Count}, exposure={_exposureCells.Count}, admitted={_admitted}, evicted={_evicted}, fallbacks={_fallbacks}, syncPending={_syncPending}, syncDirect={_syncDirect}, held={_holds}, areaRequests={_areaRequests}, prefetch={_prefetch != null && !_prefetchFaulted}, poses={_poses}, sounds={_sounds}, audioDropped={_audioDropped}, mainMs={_mainMs:F3}, posesMs={_poseMs:F3}, audioMs={_audioMs:F3}, commandsMs={_commandMs:F3}, sharedSceneMs={_simulationMs:F3}, readMs={_readMs:F3}.");
        }
    }
    private void CollectAffected(CasingImpactQuery query, Vector3Int key, Cell cell)
    {
        if (!query.MayReach((Vector3)key + Vector3.one * .5f, 1f)) return;
        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingImpactCandidates, cell.Owners.Count);
        foreach (Entry candidate in cell.Owners)
            if (candidate.Submitted && query.MayReach(candidate.Position)) _affected.Add(candidate);
    }
    private void Publish(ExternalCasingFrame frame)
    {
        long started = Stopwatch.GetTimestamp();
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingPosePublish))
        {
            ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingPoseStates, frame.StateCount);
            for (int i = 0; i < frame.StateCount; i++)
            {
                CasingState state = frame.States[i];
                if (!_entries.TryGetValue(state.Id, out Entry entry)) continue;
                if ((state.Flags & 2) != 0) { entry.Submitted = false; Release(entry, true); continue; }
                if (entry.Visual == null) { Release(entry, true); continue; }
                if (!entry.Committed)
                {
                    if (entry.Source.World == null) { Release(entry, true); continue; }
                    ((List<AmmoPoolObject>)NativeWorldCasings.GetValue(entry.Source.World)).Remove(entry.Visual);
                    if (_root == null) _root = new GameObject("Rupture casings").transform;
                    entry.Visual.transform.SetParent(_root, true);
                    entry.Visual.Shell.CollisionListener = null;
                    entry.Source.World = null;
                    entry.Committed = true;
                }
                if (!CasingGeometry.Finite(state.Pose.Position) || !CasingGeometry.Finite(state.Velocity)) { Release(entry, false); continue; }
                bool sleeping = (state.Flags & 1) != 0;
                if (sleeping != entry.Sleeping) _awake += sleeping ? -1 : 1;
                entry.Position = state.Pose.Position; entry.Velocity = state.Velocity; entry.Sleeping = sleeping;
                entry.Visual.transform.SetPositionAndRotation(state.Pose.Position, state.Pose.Rotation);
                _poses++;
                _moving.Add(entry); // Includes final sleeping pose, so support coverage follows it.
            }
        }
        _poseMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        started = Stopwatch.GetTimestamp();
        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingContacts, frame.ContactCount);
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingAudio)) PlayAudio(frame);
        _audioMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
    }
    /// <summary>
    /// Binds the environment cells a casing can reach. Cells it can enter within the next step are urgent and must be bound before the
    /// step is submitted; cells further along the lead are only requested, so the backend captures them asynchronously.
    /// </summary>
    /// <returns>
    /// False only for a not-yet-submitted casing waiting for an urgent cell: it stays out of the native scene (it cannot fall through
    /// anything) until the cell is bound or <see cref="PendingHoldSeconds"/> passes. A submitted casing cannot be paused, so its urgent
    /// cells fall back to the synchronous capture instead (counted as <c>syncPending</c>).
    /// </returns>
    private bool EnsureCoverage(Entry entry, Vector3 position, Vector3 velocity, float delta)
    {
        entry.DesiredCoverage.Clear();
        // Two frames of lead protects the current submission and its presentation latency.
        Vector3 end = position + velocity * Mathf.Max(.1f, delta * 2f) + Physics.gravity * (delta * delta * 2f);
        float urgentSeconds = UrgentSeconds(delta);
        Vector3 urgentEnd = position + velocity * urgentSeconds + Physics.gravity * (.5f * urgentSeconds * urgentSeconds);
        bool covered = CoverSegment(entry, position, urgentEnd, true);
        covered &= CoverSegment(entry, urgentEnd, end, false);
        _cellScratch.Clear();
        foreach (Vector3Int key in entry.Coverage) if (!entry.DesiredCoverage.Contains(key)) _cellScratch.Add(key);
        foreach (Vector3Int key in _cellScratch) { DropCell(key, entry); entry.Coverage.Remove(key); }
        return covered;
    }
    /// <summary>Seconds of travel that must be bound before a step of <paramref name="delta"/> seconds is submitted.</summary>
    internal static float UrgentSeconds(float delta) =>
        Mathf.Max(delta, Mathf.Clamp(delta * 3f, MinimumUrgentSeconds, MaximumUrgentSeconds));
    private bool CoverSegment(Entry entry, Vector3 from, Vector3 to, bool urgent)
    {
        int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to)));
        if (samples > 64) throw new InvalidOperationException("casing sweep exceeds bounded environment coverage");
        bool covered = true;
        for (int i = 0; i <= samples; i++)
        {
            Vector3 point = Vector3.Lerp(from, to, (float)i / samples);
            Vector3Int key = new Vector3Int(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));
            if (!entry.DesiredCoverage.Add(key)) continue;
            if (!_cells.TryGetValue(key, out Cell cell))
            {
                cell = new Cell { Consumer = NextCellConsumer() };
                _cells.Add(key, cell);
            }
            // Registered before the acquisition so a failing one is cleaned up by the entry's release.
            if (entry.Coverage.Add(key)) { cell.References++; cell.Owners.Add(entry); }
            if (!AcquireCell(key, cell, urgent, !entry.Submitted, ref entry.HeldSince)) covered = false;
        }
        return covered;
    }
    /// <summary>
    /// Binds one metre cell to its consumer: a bound cell is only re-validated every <see cref="CellRefreshSeconds"/>; otherwise the
    /// backend's non-blocking acquisition runs. A pending cell is tolerated while it is not urgent or an earlier acquisition still
    /// protects the casing, held while a new casing or impact may wait, and captured synchronously (counted) otherwise. A failed or
    /// unavailable asynchronous path uses the synchronous capture as before.
    /// </summary>
    /// <returns>False while the caller must hold; true when the cell is bound or may stay pending.</returns>
    private bool AcquireCell(Vector3Int key, Support cell, bool urgent, bool mayHold, ref float heldSince)
    {
        float now = Time.realtimeSinceStartup;
        if (cell.Bound && now - cell.Refreshed <= CellRefreshSeconds)
        {
            ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingCoverageReused, 1);
            return true;
        }
        Vector3 center = (Vector3)key + Vector3.one * .5f;
        ExternalEnvironmentPrefetchState state = ExternalEnvironmentPrefetchState.Failed;
        if (_prefetch != null && !_prefetchFaulted)
        {
            if (cell.PolledEpoch == _epoch) state = ExternalEnvironmentPrefetchState.Pending;
            else
            {
                cell.PolledEpoch = _epoch;
                ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingCoverageRequests, 1);
                try
                {
                    using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingCoverageRefresh))
                        state = _prefetch.TryAcquireEnvironment(cell.Consumer, center, center, true);
                }
                catch (Exception exception)
                {
                    // The optional backend extension faulted: the synchronous path stays in charge for the raid.
                    _prefetchFaulted = true;
                    Warn("asynchronous casing environment acquisition failed: " + exception.Message);
                }
            }
        }
        if (state == ExternalEnvironmentPrefetchState.Ready)
        {
            cell.Bound = true; cell.Refreshed = now;
            return true;
        }
        if (state == ExternalEnvironmentPrefetchState.Pending)
        {
            if (cell.Bound || !urgent) return true;
            if (mayHold)
            {
                if (heldSince < 0f) { heldSince = now; _holds++; }
                if (now - heldSince < PendingHoldSeconds) return false;
            }
            _syncPending++;
        }
        else _syncDirect++;
        bool acquired; string failure;
        ShadowRagdollWorld.RecordMainThreadWork(TraceWork.CasingCoverageRequests, 1);
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingCoverageRefresh))
            acquired = _runtime.EnsureCasingEnvironment(cell.Consumer, center, center, out failure);
        if (!acquired) throw new InvalidOperationException(failure);
        // heldSince is deliberately kept: the hold window belongs to the casing or impact, not to one cell, so a second pending cell cannot restart it.
        cell.Bound = true; cell.Refreshed = now;
        return true;
    }
    private void DropCell(Vector3Int key, Entry entry)
    {
        if (!_cells.TryGetValue(key, out Cell cell)) return;
        cell.Owners.Remove(entry);
        if (--cell.References > 0) return;
        _cells.Remove(key);
        try { _runtime?.ReleaseEnvironment(cell.Consumer); }
        catch (Exception exception) { Warn("environment release failed: " + exception.Message); }
    }
    /// <summary>Acquires the blast/scatter occlusion cells between an impact and a casing; false while any is pending inside the hold window.</summary>
    private bool EnsureExposure(Vector3 origin, Vector3 target, ref float heldSince)
    {
        bool covered = true;
        float now = Time.realtimeSinceStartup;
        int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(origin, target)));
        for (int index = 0; index <= count; index++)
        {
            Vector3 point = Vector3.Lerp(origin, target, (float)index / count);
            var key = new Vector3Int(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y), Mathf.FloorToInt(point.z));
            if (!_exposureCells.TryGetValue(key, out ExposureCell cell))
            {
                cell = new ExposureCell { Consumer = NextCellConsumer() };
                _exposureCells.Add(key, cell);
            }
            cell.LastUsed = now;
            if (!AcquireCell(key, cell, true, true, ref heldSince)) covered = false;
        }
        return covered;
    }
    private void ReleaseIdleExposure(IExternalPhysicsRuntime runtime)
    {
        if (_exposureCells.Count == 0) return;
        float now = Time.realtimeSinceStartup;
        _exposureScratch.Clear();
        foreach (KeyValuePair<Vector3Int, ExposureCell> pair in _exposureCells)
            if (now - pair.Value.LastUsed > ExposureRetentionSeconds) _exposureScratch.Add(pair.Key);
        foreach (Vector3Int key in _exposureScratch)
        {
            runtime.ReleaseEnvironment(_exposureCells[key].Consumer);
            _exposureCells.Remove(key);
        }
        _exposureScratch.Clear();
    }
    private void ReleaseExposureCells()
    {
        foreach (ExposureCell cell in _exposureCells.Values)
        {
            try { _runtime?.ReleaseEnvironment(cell.Consumer); }
            catch (Exception exception) { Warn("environment release failed: " + exception.Message); }
        }
        _exposureCells.Clear();
    }
    private uint NextCellConsumer()
    {
        _consumerSequence = (_consumerSequence + 1) & CellConsumerMask;
        return CellConsumerFlag | _consumerSequence;
    }
    /// <summary>
    /// Keeps the environment around the local player captured while casings are enabled, so ejected casings land in prefetched
    /// space: a corridor from the player's waist along their velocity, refreshed every <see cref="AreaPrefetchIntervalSeconds"/> after
    /// a metre of movement or half the lifetime, so it never expires while the player stands still.
    /// </summary>
    private void MaintainAreaPrefetch()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _areaNextCheck) return;
        _areaNextCheck = now + AreaPrefetchIntervalSeconds;
        IExternalPhysicsRuntime runtime = ShadowRagdollWorld.LocalSimulationRuntime;
        IExternalEnvironmentPrefetchRuntime prefetch = _prefetchFaulted || !Enabled || runtime?.IsRaidReady != true
            ? null : runtime as IExternalEnvironmentPrefetchRuntime;
        Player player = prefetch != null && Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
        if (player == null)
        {
            CancelAreaPrefetch();
            return;
        }
        uint consumer = AreaConsumerFlag | ((uint)player.Id & AreaConsumerMask);
        Vector3 center = player.Position + Vector3.up * AreaPrefetchCenterHeight;
        if (!CasingGeometry.Finite(center)) return;
        Vector3 velocity = player.Velocity;
        if (!CasingGeometry.Finite(velocity)) velocity = Vector3.zero;
        if (_areaActive && _areaConsumer == consumer && ReferenceEquals(_areaRuntime, prefetch))
        {
            bool moved = (center - _areaCenter).sqrMagnitude > AreaPrefetchDistance * AreaPrefetchDistance;
            if (!moved && now - _areaRequestedAt < AreaPrefetchTimeToLiveSeconds * .5f) return;
        }
        else CancelAreaPrefetch();
        Vector3 sweepEnd = center + Vector3.ClampMagnitude(velocity * AreaPrefetchLookaheadSeconds, AreaPrefetchMaximumSweep);
        try { prefetch.RequestEnvironmentPrefetch(consumer, center, sweepEnd, AreaPrefetchTimeToLiveSeconds); }
        catch (Exception exception)
        {
            _prefetchFaulted = true;
            Warn("area environment prefetch failed: " + exception.Message);
            return;
        }
        _areaActive = true; _areaConsumer = consumer; _areaRuntime = prefetch; _areaCenter = center; _areaRequestedAt = now;
        _areaRequests++;
    }
    private void CancelAreaPrefetch()
    {
        if (!_areaActive) return;
        _areaActive = false;
        IExternalEnvironmentPrefetchRuntime prefetch = _areaRuntime;
        _areaRuntime = null;
        try { prefetch?.CancelEnvironmentPrefetch(_areaConsumer); }
        catch (Exception exception) { Warn("area environment prefetch cancellation failed: " + exception.Message); }
    }
    private void Release(Entry entry, bool fallback)
    {
        if (!_entries.Remove(entry.Id)) return;
        if (!entry.Sleeping) _awake--;
        _order.Remove(entry.Node);
        if (entry.Submitted) _removes.Add(entry.Id);
        foreach (Vector3Int key in entry.Coverage) DropCell(key, entry);
        entry.Coverage.Clear();
        if (entry.Submitted && entry.Visual != null)
        {
            // Hide immediately, but keep the pool-return guard until the owner
            // acknowledges removal. Reuse can never overlap the old actor.
            entry.RetireFallback = fallback;
            entry.Visual.gameObject.SetActive(false);
            _retiring.Add(entry.Id, entry);
            return;
        }
        ReleaseVisual(entry, fallback);
    }
    private void ReleaseVisual(Entry entry, bool fallback)
    {
        _byVisual.Remove(entry.Visual);
        AmmoPoolObject visual = entry.Visual;
        if (visual == null) return;
        ObjectLifetime.Unbind(visual.gameObject, "casing");
        for (int i = 0; i < entry.Colliders.Length; i++) if (entry.Colliders[i] != null) entry.Colliders[i].enabled = entry.ColliderEnabled[i];
        for (int i = 0; i < entry.Bodies.Length; i++)
        {
            Rigidbody body = entry.Bodies[i];
            if (body == null) continue;
            body.isKinematic = entry.BodyKinematic[i]; body.detectCollisions = entry.BodyCollisions[i];
            if (!body.isKinematic) { body.velocity = entry.BodyVelocity[i]; body.angularVelocity = entry.BodySpin[i]; }
        }
        if (fallback && !entry.Committed && entry.Source.World != null)
        {
            _fallbacks++;
            entry.Source.Spawned = false;
            visual.gameObject.SetActive(true);
            visual.Shell.enabled = true;
            visual.StartAutoDestroyCountDown(Mathf.Max(.1f, entry.Countdown));
            // Countdown's postfix must not re-admit this failed generation.
            return;
        }
        if (entry.Source.World != null) ((List<AmmoPoolObject>)NativeWorldCasings.GetValue(entry.Source.World)).Remove(visual);
        _releasing = visual;
        try { AssetPoolObject.ReturnToPool(visual.gameObject, true); }
        finally { _releasing = null; }
        _evicted++;
    }
    internal void Fail()
    {
        _failed = true; while (_order.First != null) Release(_order.First.Value, false); _impacts.Clear(); ReleaseExposureCells();
        // The gate is closed for good; a half-finished warm-up would keep EFT's sources borrowed until the raid resets.
        CasingAudio.CancelWarmup();
    }
    internal void BackendFailed()
    {
        Fail();
        // The failed scene is quarantined/reset by its owner; no further casing
        // contacts can reach this graphical consumer.
        foreach (Entry entry in _retiring.Values) ReleaseVisual(entry, false);
        _retiring.Clear();
    }
    internal void Reset()
    {
        Fail();
        // Raid teardown owns the native scene and invalidates every generation;
        // drain visuals before Tarkov disposes their originating pools.
        foreach (Entry entry in _retiring.Values) ReleaseVisual(entry, false);
        _retiring.Clear();
        // Scene teardown owns any pending native work; never mutate an in-flight frame.
        _runtime = null; _prefetch = null; _frame = null; _frameInFlight = false; _removes.Clear(); _pending.Clear(); _moving.Clear(); _cells.Clear(); _geometry.Clear();
        CancelAreaPrefetch(); _areaNextCheck = 0f; _prefetchFaulted = false;
        _audioWarm = false;
        _affected.Clear(); _audible.Clear(); _cellScratch.Clear();
        _sources = new ConditionalWeakTable<AmmoPoolObject, Source>(); _shots = new ConditionalWeakTable<EFT.Ballistics.Shot, ShotIdentity>();
        if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        _root = null; _failed = false; _warned = false; _awake = 0;
    }
    private void Warn(string message)
    {
        if (_warned) return; _warned = true;
        RupturePlugin.Log.LogWarning("RPT persistent casings: " + message);
    }
    /// <summary>Takes one pooled-source step per frame so EFT's pool is already stocked when the first casing sounds.</summary>
    private void WarmAudio()
    {
        try { _audioWarm = CasingAudio.WarmPool(); }
        catch (Exception exception)
        {
            // Presentation only: stay on EFT's lazy pool instead of disabling casings.
            _audioWarm = true;
            CasingAudio.CancelWarmup();
            Warn("casing audio warm-up failed: " + exception.Message);
        }
    }
    private void PlayAudio(ExternalCasingFrame frame)
    {
        if (!RuptureConfiguration.CasingSounds.Value || frame.ContactCount == 0) return;
        Camera camera = Camera.main; if (camera == null) return;
        float now = Time.realtimeSinceStartup;
        using (ShadowRagdollWorld.TraceMainThread(TraceStage.CasingAudioSelection))
        {
            Vector3 listener = camera.transform.position;
            _audible.Clear();
            for (int i = 0; i < frame.ContactCount; i++)
                if ((frame.Contacts[i].Position - listener).sqrMagnitude < 900f) _audible.Add(frame.Contacts[i]);
            if (_audible.Count > 1) _audible.Sort(SpeedDescending);
        }
        int played = 0;
        foreach (CasingContact contact in _audible)
        {
            if (played >= MaximumSoundsPerFrame) { _audioDropped++; continue; }
            if (!_entries.TryGetValue(contact.Id, out Entry entry) || now - entry.LastSound < .08f) continue;
            if (CasingAudio.Play(contact, entry.Caliber, entry.LocalShooter)) { entry.LastSound = now; _sounds++; played++; }
        }
    }
}
