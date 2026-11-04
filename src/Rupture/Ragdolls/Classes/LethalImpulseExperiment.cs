using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Rupture.Impacts;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

// Per-death impulse transaction. Console overrides are retained internally for
// regression fixtures; normal gameplay captures persisted settings at death.
internal static class LethalImpulseExperiment
{
    internal const int MaximumRecords = 32, MaximumEvents = 256;
    private const float Lifetime = 4f;
    internal sealed class Record
    {
        internal uint Id;
        internal Player Player;
        internal string Profile;
        internal CorpseRagdoll Ragdoll;
        internal Collider Collider;
        internal ulong Projectile;
        internal int FireIndex;
        internal Vector3 Point, Direction;
        internal float CreatedAt;
        internal int CreatedFrame;
        internal LethalImpulsePolicy Policy;
        internal Rigidbody StockBody, RuptureBody;
        internal Vector3 StockImpulse, StockPoint, RuptureImpulse, OriginalRuptureImpulse, RupturePoint;
        internal bool StockSeen, RuptureSeen, StockNativePending, RuptureNativePending;
        internal bool StockExternalPending, RuptureExternalPending;
        internal bool StockExternalInFlight, RuptureExternalInFlight;
        internal int StockNativeSteps, RuptureNativeSteps;
        internal Vector3 StockBeforeVelocity, StockBeforeAngular, RuptureBeforeVelocity, RuptureBeforeAngular;
        internal bool StockStepObserved, RuptureStepObserved;
        internal bool StockCapturedPending, RuptureCapturedPending, NativeStepUncertain;
        internal int DescriptorCount;
        internal EFT.Ballistics.Shot ContactShot;
        internal uint ShotInstance;
        internal float ContactMass, ContactSpeed;
        internal Rigidbody OriginalRuptureBody;
        internal Collider OriginalRuptureCollider;
        internal Vector3 OriginalRupturePoint;
        internal bool ComparisonInvalidated;
        internal bool Production;
    }
    internal struct QueueState { internal bool Observed, HadPending; }
    internal struct ShotContext
    {
        internal EFT.Ballistics.Shot Tracked;
        internal uint Instance;
        internal ImpactData Sample;
        internal Collider Collider;
        internal Vector3 Point, Direction;
    }
    [ThreadStatic] private static ShotContext _shotContext;

    private static readonly List<Record> Records = new List<Record>(MaximumRecords);
    private static readonly string[] Events = new string[MaximumEvents];
    private static int _eventCount, _eventCursor, _dropped;
    private static uint _nextId;
    private static bool _enabled, _physical, _replaying;
    private static LethalImpulseSource _source;
    internal static bool Enabled => _enabled || RuptureConfiguration.CorrectedLethalImpulse?.Value == true || Records.Count != 0;
    internal static bool ProductionAuthorityAllowed
    {
        get
        {
            var runtime = MultiplayerRuntime.Current;
            return !runtime.IsRemoteClient && !runtime.HasClient &&
                (runtime.IsNetworkAuthority == runtime.HasServer);
        }
    }
    private static bool Allowed(Record record) => record.Production ? ProductionAuthorityAllowed : LocalComparisonAllowed;
    internal static ShotContext EnterShot(EFT.Ballistics.Shot shot, ImpactData sample)
    {
        ShotContext previous = _shotContext;
        _shotContext = default;
        if (Enabled && shot != null && sample.Valid && ShotImpactProcessor.TryGetTrackedContact(shot, sample.Projectile,
            out EFT.Ballistics.Shot tracked, out uint instance))
            _shotContext = new ShotContext { Tracked = tracked, Instance = instance, Sample = sample,
                Collider = shot.HitCollider, Point = shot.HitPoint, Direction = shot.Direction };
        return previous;
    }
    internal static void RestoreShot(ShotContext previous) => _shotContext = previous;
    internal static bool LocalComparisonAllowed
    {
        get
        {
            var runtime = MultiplayerRuntime.Current;
            // A loaded adapter with no session and an initialized graphical solo
            // host are both local. Half-created/torn-down host state is ambiguous.
            return !runtime.IsHeadless && !runtime.IsRemoteClient && !runtime.HasClient && !runtime.HasRemotePeers &&
                (runtime.IsNetworkAuthority == runtime.HasServer);
        }
    }

    internal static void Baseline() => Select(LethalImpulseSource.Baseline);
    internal static void StockOnly() => Select(LethalImpulseSource.StockOnly);
    internal static void RuptureOnly() => Select(LethalImpulseSource.RuptureOnly);
    internal static void Physical() { _physical = true; Status(); }
    internal static void Amplified()
    {
        if (RootVelocityExperiment.Enabled) { VelocityLocked(); return; }
        _physical = false; Status();
    }
    internal static void FixForVelocity() { _source = LethalImpulseSource.RuptureOnly; _physical = _enabled = true; }
    private static void VelocityLocked() => RupturePlugin.Log.InitializationWarning("RPT velocity experiment fixes lethal impulses at RuptureOnly 1x. RPTVelocityOff restores defaults before other lethal comparisons.");
    private static void Select(LethalImpulseSource source)
    {
        if (RootVelocityExperiment.Enabled && source != LethalImpulseSource.RuptureOnly) { VelocityLocked(); return; }
        if (source != LethalImpulseSource.Baseline && (!LocalComparisonAllowed || !Patches.LethalImpulseHooks.Available))
        {
            RupturePlugin.Log.InitializationWarning("RPT lethal experiment: mode unchanged. Comparisons require a local raid or initialized solo Fika host with zero connected peers, and installed impulse hooks. Remote clients, headless and hosts with peers support RPTLethalBaseline tracing.");
            Status(); return;
        }
        _source = source; _enabled = true; Status();
    }
    private static void Status()
    {
        var runtime = MultiplayerRuntime.Current;
        RupturePlugin.Log.Initialization($"RPT lethal experiment: trace={_enabled}, source={_source}, RuptureOnlyGain={(_physical ? 1 : 4)}x, localComparisonAllowed={LocalComparisonAllowed}, headless={runtime.IsHeadless}, remoteClient={runtime.IsRemoteClient}, authority={runtime.IsNetworkAuthority}, server={runtime.HasServer}, client={runtime.HasClient}, remotePeers={runtime.HasRemotePeers}, hooks={Patches.LethalImpulseHooks.Available}. Selection is captured for NEW deaths; resets at raid end. Dump with RPTLethalDump.");
    }

    internal static void Off()
    {
        if (RootVelocityExperiment.Enabled) { RootVelocityExperiment.Off(); return; }
        foreach (Record record in Records) Release(record, "experiment-off");
        Records.Clear();
        _enabled = false; _source = LethalImpulseSource.Baseline; _physical = false; Status();
    }

    internal static void Death(Player player, EFT.Ballistics.DamageInfo damage, ImpactData sample, bool allowCreate = true)
    {
        if (!Enabled || player == null || damage.DamageType != EDamageType.Bullet) return;
        // OnDead is the terminal boundary. A second notification must not replace
        // a held transaction or turn a delayed packet into a new death experiment.
        foreach (Record existing in Records)
            if (ReferenceEquals(existing.Player, player) && Time.realtimeSinceStartup - existing.CreatedAt < Lifetime)
            {
                // Fika's authoritative lethal sample can arrive after native corpse
                // construction. It enriches evidence; a failed transaction stays failed.
                if (existing.Projectile == 0 && sample.Valid && ImpactStateService.Eligible(damage) && damage.FireIndex == sample.FireIndex)
                {
                    existing.Projectile = sample.Projectile; existing.FireIndex = sample.FireIndex;
                    existing.Collider = damage.HitCollider; existing.Point = damage.HitPoint; existing.Direction = damage.Direction;
                    CaptureContact(existing, damage, sample);
                    Event(existing, "death-sample-upgrade", "lethal damage snapshot supplied ballistic identity");
                }
                return;
            }
        if (!allowCreate) return; // only OnDead can open a terminal transaction
        if (!_enabled && (RuptureConfiguration.CorrectedLethalImpulse?.Value != true || !ProductionAuthorityAllowed)) return;
        // The diagnostic history limit must never change gameplay selection.
        // Production records retire with the existing four-second transaction
        // lifetime; only synthetic comparison fixtures retain their 32-slot cap.
        if (_enabled && Records.Count >= MaximumRecords) { _dropped++; return; }
        Record record = new Record
        {
            Id = ++_nextId, Player = player, Profile = player.ProfileId,
            Collider = damage.HitCollider, Projectile = sample.Valid && ImpactStateService.Eligible(damage) && damage.FireIndex == sample.FireIndex ? sample.Projectile : 0,
            FireIndex = sample.FireIndex, Point = damage.HitPoint, Direction = damage.Direction,
            CreatedAt = Time.realtimeSinceStartup,
            CreatedFrame = Time.frameCount,
            Production = !_enabled,
            Policy = new LethalImpulsePolicy(_enabled ? _source : LethalImpulseSource.RuptureOnly, _enabled ? _physical : true)
        };
        Records.Add(record);
        CaptureContact(record, damage, sample);
        Event(record, "death", $"point={record.Point:F4}, direction={record.Direction:F4}, sampleMomentum={sample.Momentum:F4}");
    }

    private static void CaptureContact(Record record, EFT.Ballistics.DamageInfo damage, ImpactData sample)
    {
        ShotContext contact = _shotContext;
        if (record.ContactShot != null || contact.Tracked == null || contact.Instance == 0 || !contact.Sample.Valid ||
            !sample.Valid || sample.Projectile != record.Projectile || contact.Sample.Projectile != record.Projectile ||
            contact.Sample.FireIndex != record.FireIndex || damage.FireIndex != record.FireIndex || contact.Collider == null || contact.Collider != record.Collider ||
            !SameHit(record, contact.Point, contact.Direction)) return;
        record.ContactShot = contact.Tracked; record.ShotInstance = contact.Instance;
        record.ContactMass = contact.Sample.MassGrams; record.ContactSpeed = contact.Sample.Speed;
        Event(record, "lethal-contact-captured", $"instance={record.ShotInstance}, collider={record.Collider.name}, point={record.Point:F4}, direction={record.Direction:F4}, momentum={contact.Sample.Momentum:F4}");
    }

    internal static void Bind(CorpseRagdoll ragdoll)
    {
        if (!Enabled || ragdoll?._owner is not Corpse corpse) return;
        foreach (Record record in Records)
            if (record.Ragdoll == null && record.Player != null && record.Player.gameObject == corpse.gameObject && record.Profile == corpse.PlayerProfileID)
            { record.Ragdoll = ragdoll; Event(record, "native-start", "awaiting-admission"); return; }
    }

    private static Record Find(CorpseRagdoll ragdoll)
    {
        if (!Enabled || ragdoll == null) return null;
        foreach (Record record in Records) if (ReferenceEquals(record.Ragdoll, ragdoll)) return record;
        return null;
    }

    internal static Record FindVelocityDeath(CorpseRagdoll ragdoll)
    {
        if (!Enabled || ragdoll?._owner is not Corpse corpse) return null;
        foreach (Record record in Records)
            if (record.Player != null && record.Player.gameObject == corpse.gameObject && record.Profile == corpse.PlayerProfileID &&
                record.Projectile != 0 && record.ContactShot != null && record.ShotInstance != 0 &&
                record.Policy.Source == LethalImpulseSource.RuptureOnly && record.Policy.Physical && !record.Policy.Fallback &&
                (record.Ragdoll == null || ReferenceEquals(record.Ragdoll, ragdoll)) && Time.realtimeSinceStartup - record.CreatedAt < 4f)
                return record;
        return null;
    }

    private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    private static string BodyMotion(Rigidbody body) => body == null ? "body=destroyed-or-unavailable" :
        $"body={body.name}, mass={body.mass:F3}, velocity={body.velocity:F4}, angular={body.angularVelocity:F4}";
    private static bool SameHit(Record record, Vector3 point, Vector3 direction) =>
        record.Projectile != 0 && Finite(point) && Finite(direction) && Finite(record.Point) && Finite(record.Direction) &&
        (record.Point - point).sqrMagnitude <= 0.0004f && record.Direction.sqrMagnitude > 0.0001f &&
        direction.sqrMagnitude > 0.0001f && Vector3.Dot(record.Direction.normalized, direction.normalized) >= 0.995f;

    internal static QueueState BeforeStockQueue(CorpseRagdoll ragdoll) =>
        new QueueState { Observed = Find(ragdoll) != null, HadPending = Enabled && ragdoll._hasImpulse };

    internal static void StockQueued(CorpseRagdoll ragdoll, Collider collider, Vector3 direction, Vector3 point, float thrust,
        QueueState state, bool originalRan)
    {
        if (!state.Observed) return;
        Record record = Find(ragdoll);
        if (record == null) return;
        if (record.Policy.Expired(Time.realtimeSinceStartup, Time.frameCount)) Release(record, "250ms-timeout-before-descriptor");
        record.DescriptorCount++;
        bool lookup = ragdoll.FindRigidbodySpawner(collider, out _);
        Collider first = collider != null ? collider.GetComponent<Collider>() : null;
        bool sameObjectSpawner = collider != null && collider.GetComponent<RigidbodySpawner>() != null;
        Event(record, "eft-queue", $"matchedCollider={collider == record.Collider}, magnitude={(direction * thrust).magnitude:F4}, point={point:F4}, hasImpulse={ragdoll._hasImpulse}, pendingBefore={state.HadPending}, originalRan={originalRan}, lookupNow={lookup}, hit={(collider != null ? collider.name + "/" + collider.GetType().Name : "null")}, first={(first != null ? first.GetType().Name : "null")}, hitIsFirst={first == collider}, spawnerOnHitObject={sameObjectSpawner}, attachedBody={(collider != null && collider.attachedRigidbody != null ? collider.attachedRigidbody.name : "none")}");
        bool correlated = collider != null && collider == record.Collider && SameHit(record, point, direction) && Finite(direction * thrust);
        if (record.DescriptorCount != 1 || !correlated)
        {
            record.Policy.StockRejected = false;
            if (record.Policy.Finished && !record.Policy.Fallback)
                Event(record, "comparison-invalidated", "another stock descriptor arrived after selection; subsequent impacts remain unchanged");
            else Release(record, "ambiguous-stock-descriptor");
            return;
        }
        // A missing submission is not evidence of absence. Require the original
        // descriptor to have run, with neither an older pending descriptor nor a
        // stock submission, and a failed exact EFT collider lookup at return.
        record.Policy.StockRejected = originalRan && !state.HadPending && !ragdoll._hasImpulse && !lookup && !record.StockSeen;
        if (record.Policy.StockRejected)
        {
            Event(record, "stock-unarmed-confirmed", sameObjectSpawner && first != collider ?
                "reason=non-primary-collider-on-spawner; EFT compares only GetComponent<Collider>()" :
                "reason=no-exact-spawner-collider; EFT descriptor ran without arming any force");
            if (record.Policy.Source == LethalImpulseSource.StockOnly) Release(record, "stock-unarmed-no-stock-only-sample");
            else Resolve(record);
        }
    }

    // Called at the stock Rigidbody overload, after EFT consumes _hasImpulse,
    // before AddForceAtPosition can submit any native or external force.
    internal static bool BeforeStock(CorpseRagdoll ragdoll, Rigidbody body, Vector3 direction, Vector3 point, float thrust, out Record record)
    {
        record = _replaying ? null : Find(ragdoll);
        if (record == null) return true;
        if (record.StockSeen)
        { Release(record, "repeated-stock-call"); Event(record, "stock-unmatched", "repeated call left unchanged"); record = null; return true; }
        record.StockSeen = true; record.StockBody = body; record.StockImpulse = direction * thrust; record.StockPoint = point;
        if (record.Policy.StockRejected)
        {
            record.Policy.StockRejected = false;
            if (record.Policy.Finished && !record.Policy.Fallback)
                Event(record, "comparison-invalidated", "late stock submission after confirmed-unarmed selection; original submission left unchanged");
        }
        if (record.Policy.Expired(Time.realtimeSinceStartup, Time.frameCount)) Release(record, "250ms-timeout-before-stock");
        bool correlated = SameHit(record, point, direction) && body != null && record.Collider != null &&
            ragdoll.FindRigidbodySpawner(record.Collider, out RigidbodySpawner spawner) && spawner.Rigidbody == body && Finite(record.StockImpulse);
        if (!record.Policy.CanHold(correlated, Allowed(record), ShadowRagdollWorld.Enabled && Physics.simulationMode == SimulationMode.Script))
        { Release(record, correlated ? "baseline-or-unavailable" : "stock-correlation-failed"); return true; }
        record.Policy.Held(true, Time.realtimeSinceStartup, Time.frameCount);
        Event(record, "stock-held", $"magnitude={record.StockImpulse.magnitude:F4}, point={point:F4}");
        Resolve(record);
        return false;
    }

    internal static void AfterStock(Record record, bool ran)
    {
        if (record == null || !ran) return;
        ObserveSubmission(record, true);
    }

    internal static Record CompletedShot(EFT.Ballistics.Shot shot, uint instance, Corpse corpse, Collider collider, Rigidbody body,
        ulong projectile, Vector3 impulse, Vector3 point, float mass, float speed, float multiplier, out bool handled)
    {
        Record contact = corpse != null ? Find(corpse.Ragdoll) : null;
        bool captured = contact != null && contact.Policy.Source != LethalImpulseSource.Baseline && !contact.Policy.Fallback &&
            contact.Projectile == projectile && contact.ShotInstance == instance && instance != 0 && ReferenceEquals(contact.ContactShot, shot);
        if (!captured && contact?.ContactShot != null && contact.Policy.Source != LethalImpulseSource.Baseline &&
            !contact.Policy.Fallback && contact.Projectile == projectile)
        {
            // Do not fall back to geometric matching after a stronger binding
            // exists: a pooled lifetime or another continuation may share it.
            Event(contact, "completed-unmatched", $"projectile={projectile:X16}, instance={instance}, expectedInstance={contact.ShotInstance}; tracked lethal contact belongs to another completion");
            if (ReferenceEquals(contact.ContactShot, shot)) Release(contact, "tracked-shot-instance-mismatch");
            handled = false; return null;
        }
        return Complete(corpse, collider, body, projectile, impulse, point, mass, speed, multiplier, out handled, captured ? contact : null);
    }

    internal static Record Completed(Corpse corpse, Collider collider, Rigidbody body, ulong projectile, Vector3 impulse, Vector3 point,
        float mass, float speed, float multiplier, out bool handled) =>
        Complete(corpse, collider, body, projectile, impulse, point, mass, speed, multiplier, out handled, null);

    private static Record Complete(Corpse corpse, Collider collider, Rigidbody body, ulong projectile, Vector3 impulse, Vector3 point,
        float mass, float speed, float multiplier, out bool handled, Record contact)
    {
        handled = false;
        if (!Enabled || corpse == null) return null;
        Record record = Find(corpse.Ragdoll);
        if (record == null) return null;
        Collider selectedCollider = collider;
        Rigidbody selectedBody = body;
        Vector3 selectedPoint = point, selectedImpulse = impulse;
        if (contact != null)
        {
            selectedCollider = record.Collider; selectedPoint = record.Point;
            selectedBody = selectedCollider != null ? selectedCollider.attachedRigidbody : null;
            bool retained = false;
            if (selectedBody != null && record.Ragdoll._rigidbodySpawners != null)
                foreach (RigidbodySpawner spawner in record.Ragdoll._rigidbodySpawners)
                    if (spawner != null && spawner.Rigidbody == selectedBody) { retained = true; break; }
            if (!retained || !Finite(record.Direction) || record.Direction.sqrMagnitude <= 0.0001f)
            { Release(record, "lethal-contact-body-unavailable"); contact = null; selectedCollider = collider; selectedBody = body; selectedPoint = point; }
            else
            {
                mass = record.ContactMass; speed = record.ContactSpeed;
                selectedImpulse = record.Direction.normalized * PhysicsMath.CalculateProjectileImpulseWithGain(mass, speed, multiplier, 4f);
                Event(record, "completed-lethal-contact", $"instance={record.ShotInstance}, originalBody={(body != null ? body.name : "none")}, originalPoint={point:F4}, originalMagnitude={impulse.magnitude:F4}, selectedBody={selectedBody.name}, selectedPoint={selectedPoint:F4}, contactMomentum={mass * 0.001f * speed:F4}");
            }
        }
        // Never classify a distinct projectile, final penetration collider, later
        // corpse hit, or detached gib as the original lethal completed-shot event.
        if (projectile == 0 || projectile != record.Projectile || selectedCollider != record.Collider || !SameHit(record, selectedPoint, selectedImpulse))
        { Event(record, "completed-unmatched", $"projectile={projectile:X16}, magnitude={impulse.magnitude:F4}, point={point:F4}, {BodyMotion(body)}; unchanged"); return null; }
        if (record.RuptureSeen)
        { Release(record, "repeated-completed-call"); Event(record, "completed-unmatched", "repeated call left unchanged"); return null; }
        record.RuptureSeen = true; record.RuptureBody = selectedBody; record.RuptureImpulse = selectedImpulse; record.RupturePoint = selectedPoint;
        record.OriginalRuptureBody = body; record.OriginalRuptureCollider = collider;
        record.OriginalRuptureImpulse = impulse; record.OriginalRupturePoint = point;
        if (record.Policy.Expired(Time.realtimeSinceStartup, Time.frameCount)) Release(record, "250ms-timeout-before-completion");
        Event(record, "completed", $"magnitude={impulse.magnitude:F4}, point={point:F4}, {BodyMotion(body)}");
        bool correlated = selectedBody != null && Finite(selectedImpulse) && selectedImpulse.sqrMagnitude > 0f;
        if (!record.Policy.CanHold(correlated, Allowed(record), ShadowRagdollWorld.Enabled && Physics.simulationMode == SimulationMode.Script))
        { Release(record, "completed-baseline-or-unavailable"); return record; }
        if (record.Policy.Physical && record.Policy.Source == LethalImpulseSource.RuptureOnly)
        {
            float physical = PhysicsMath.CalculateProjectileImpulseWithGain(mass, speed, multiplier, 1f);
            if (physical <= 0f) { Release(record, "invalid-physical-momentum"); return record; }
            record.RuptureImpulse = selectedImpulse.normalized * physical;
        }
        record.Policy.Held(false, Time.realtimeSinceStartup, Time.frameCount);
        handled = true;
        Event(record, "rupture-held", $"selectedMagnitude={record.RuptureImpulse.magnitude:F4}, legacyMagnitude={impulse.magnitude:F4}");
        Resolve(record);
        return record;
    }

    internal static void AfterCompleted(Record record, bool applied)
    {
        if (record == null) return;
        // The caller ran its original route. Evidence/migration must describe
        // that route even if a captured contact was considered but not held.
        record.RuptureBody = record.OriginalRuptureBody; record.RupturePoint = record.OriginalRupturePoint;
        record.RuptureImpulse = record.OriginalRuptureImpulse;
        if (applied) ObserveSubmission(record, false);
        else Event(record, "rupture-not-applied", "original path returned false");
    }

    private static void ObserveSubmission(Record record, bool stock)
    {
        Rigidbody body = stock ? record.StockBody : record.RuptureBody;
        bool external = record.Ragdoll?._owner is Corpse corpse && ShadowRagdollWorld.HasActiveLethalExperimentBody(corpse, body);
        bool native = !external && body != null && !body.isKinematic;
        if (stock) { record.StockNativePending |= native; record.StockExternalPending |= external; }
        else { record.RuptureNativePending |= native; record.RuptureExternalPending |= external; }
        Event(record, stock ? "stock-submit" : "rupture-submit", $"route={(external ? "external-queued" : native ? "native-queued" : "inactive")}, magnitude={(stock ? record.StockImpulse : record.RuptureImpulse).magnitude:F4}, point={(stock ? record.StockPoint : record.RupturePoint):F4}, {BodyMotion(body)}");
        if (!external && !native) Lost(record, stock ? "stock submission target inactive" : "completed-shot submission target inactive");
    }

    internal static void Admission(CorpseRagdoll ragdoll, bool committed)
    {
        Record record = Find(ragdoll);
        if (record == null) return;
        record.Policy.Admitted = committed;
        // Admission resolves a complete transaction before checking age. A cold
        // build is not evidence of a missing callback or an unarmed stock force.
        if (committed) Resolve(record);
        if (record.Policy.Expired(Time.realtimeSinceStartup, Time.frameCount)) Release(record, "250ms-timeout-at-admission");
        Event(record, committed ? "admission-committed" : "admission-failed", $"stockNativeSteps={record.StockNativeSteps}, ruptureNativeSteps={record.RuptureNativeSteps}, stockNativePending={record.StockNativePending}, ruptureNativePending={record.RuptureNativePending}; pending forces are NOT copied by velocity capture");
        if (!committed) Release(record, "admission-failed");
        else
        {
            // A timeout can replay into native physics immediately before a late
            // admission. Only migrate sources PROVEN still queued at capture;
            // never subtract or reapply a source that crossed a native step.
            if (record.Policy.Fallback && record.Policy.Source != LethalImpulseSource.Baseline &&
                !record.NativeStepUncertain && Physics.simulationMode == SimulationMode.Script)
            {
                MigratePending(record, true); MigratePending(record, false);
            }
            Resolve(record);
        }
    }

    internal static void CaptureAdmissionMotion(CorpseRagdoll ragdoll, Rigidbody body, Vector3 velocity, Vector3 angular)
    {
        Record record = Find(ragdoll);
        if (record == null || body == null || body != record.StockBody && body != record.RuptureBody) return;
        if (body == record.StockBody) record.StockCapturedPending = record.StockNativePending && !body.isKinematic;
        if (body == record.RuptureBody) record.RuptureCapturedPending = record.RuptureNativePending && !body.isKinematic;
        Event(record, "admission-capture", $"body={body.name}, velocity={velocity:F4}, angular={angular:F4}, stockSteps={record.StockNativeSteps}, ruptureSteps={record.RuptureNativeSteps}; copies body velocity, not queued force accumulators");
    }

    private static void MigratePending(Record record, bool stock)
    {
        if (!(stock ? record.StockCapturedPending && record.StockNativePending : record.RuptureCapturedPending && record.RuptureNativePending)) return;
        Rigidbody body = stock ? record.StockBody : record.RuptureBody;
        Vector3 impulse = stock ? record.StockImpulse : record.OriginalRuptureImpulse;
        Vector3 point = stock ? record.StockPoint : record.RupturePoint;
        if (record.Ragdoll?._owner is not Corpse corpse || !ShadowRagdollWorld.TryApplyLethalExperimentImpulse(corpse, body, impulse, point))
        { Lost(record, "pending native source could not migrate to admitted actor"); return; }
        if (stock) { record.StockCapturedPending = record.StockNativePending = false; record.StockExternalPending = true; }
        else { record.RuptureCapturedPending = record.RuptureNativePending = false; record.RuptureExternalPending = true; }
        Event(record, "fallback-pending-migrated", $"source={(stock ? "stock" : "rupture")}, magnitude={impulse.magnitude:F4}; no native step consumed this source");
    }

    private static void Resolve(Record record)
    {
        if (!record.Policy.Ready) return;
        if (record.Policy.StockHeld && record.StockBody != record.RuptureBody)
        { Release(record, "source-body-mismatch"); return; }
        if (!Allowed(record) || record.Ragdoll?._owner is not Corpse corpse || !ShadowRagdollWorld.IsActive(corpse))
        { Release(record, "ownership-lost"); return; }
        Rigidbody body = record.Policy.Source == LethalImpulseSource.StockOnly ? record.StockBody : record.RuptureBody;
        Vector3 impulse = record.Policy.Source == LethalImpulseSource.StockOnly ? record.StockImpulse : record.RuptureImpulse;
        Vector3 point = record.Policy.Source == LethalImpulseSource.StockOnly ? record.StockPoint : record.RupturePoint;
        // Exact admitted actor only. Do not use nearest-anatomy fallback or reconnect
        // a limb removed while this short transaction was waiting.
        if (!ShadowRagdollWorld.HasActiveLethalExperimentBody(corpse, body) ||
            !(record.Production ? RagdollCoordinator.TryApplyPointImpulse(body, impulse, point) :
                ShadowRagdollWorld.TryApplyLethalExperimentImpulse(corpse, body, impulse, point)))
        { Release(record, "selected-actor-unavailable"); return; }
        record.Policy.StockHeld = record.Policy.RuptureHeld = false;
        record.Policy.Finish(false);
        record.StockExternalPending = record.Policy.Source == LethalImpulseSource.StockOnly;
        record.RuptureExternalPending = record.Policy.Source == LethalImpulseSource.RuptureOnly;
        Event(record, "single-source-queued", $"source={record.Policy.Source}, magnitude={impulse.magnitude:F4}, point={point:F4}, gain={(record.Policy.Physical ? 1 : 4)}x, stock={(record.Policy.StockRejected ? "confirmed-unarmed" : "held")}");
    }

    private static void Release(Record record, string reason)
    {
        if (record.Policy.Finished) return;
        record.Policy.Finish(true); // latch BEFORE replay; late events remain baseline
        bool stock = record.Policy.StockHeld, rupture = record.Policy.RuptureHeld;
        record.Policy.StockHeld = record.Policy.RuptureHeld = false;
        Event(record, "fallback", $"reason={reason}, replayStock={stock}, replayRupture={rupture}");
        if (stock)
        {
            if (record.StockBody != null)
            {
                _replaying = true;
                try { record.Ragdoll.ApplyImpulse(record.StockBody, record.StockImpulse, record.StockPoint, 1f); }
                catch (Exception exception) { Lost(record, "stock replay threw: " + exception.GetType().Name); }
                finally { _replaying = false; }
                ObserveSubmission(record, true);
            }
            else Lost(record, "stock target destroyed");
        }
        if (rupture)
        {
            // Replay the ORIGINAL amplified value if physical replacement did not
            // commit. Fallback must reproduce the pre-experiment path.
            Vector3 original = record.OriginalRuptureImpulse;
            record.RuptureBody = record.OriginalRuptureBody; record.RupturePoint = record.OriginalRupturePoint;
            bool applied = false;
            try
            {
                applied = record.Ragdoll?._owner is Corpse corpse && record.OriginalRuptureCollider != null &&
                    RagdollCoordinator.TryApplyCorpsePointImpulse(corpse, record.OriginalRuptureCollider, original, record.RupturePoint, out _);
                if (!applied && record.RuptureBody != null)
                    applied = RagdollCoordinator.TryApplyPointImpulse(record.RuptureBody, original, record.RupturePoint);
            }
            catch (Exception exception) { Lost(record, "completed-shot replay threw: " + exception.GetType().Name); }
            if (applied) { record.RuptureImpulse = original; ObserveSubmission(record, false); }
            else Lost(record, "completed-shot target no longer dynamic or available");
        }
    }

    private static void Lost(Record record, string reason)
    {
        Event(record, "replay-unavailable", reason);
        RupturePlugin.Log.LogWarning($"RPT lethal impulse {record.Id}: fallback target unavailable ({reason}); see RPTLethalDump.");
    }

    internal static void Update()
    {
        if (!Enabled) return;
        float now = Time.realtimeSinceStartup;
        for (int i = Records.Count - 1; i >= 0; i--)
        {
            Record record = Records[i];
            if (record.Policy.Expired(now, Time.frameCount)) Release(record, "250ms-timeout");
            if ((now - record.CreatedAt < Lifetime || record.Production && Time.frameCount <= record.CreatedFrame + 1) && record.Player != null) continue;
            Release(record, "record-expired-or-owner-destroyed");
            Records.RemoveAt(i);
        }
    }

    internal static void BeforeNativeStep()
    {
        if (!Enabled) return;
        foreach (Record record in Records)
        {
            record.StockStepObserved = record.StockNativePending && record.StockBody != null && !record.StockBody.isKinematic;
            record.RuptureStepObserved = record.RuptureNativePending && record.RuptureBody != null && !record.RuptureBody.isKinematic;
            if (record.StockStepObserved) { record.StockBeforeVelocity = record.StockBody.velocity; record.StockBeforeAngular = record.StockBody.angularVelocity; }
            if (record.RuptureStepObserved) { record.RuptureBeforeVelocity = record.RuptureBody.velocity; record.RuptureBeforeAngular = record.RuptureBody.angularVelocity; }
        }
    }
    internal static void AfterNativeStep(bool completed)
    {
        if (!Enabled) return;
        foreach (Record record in Records)
        {
            if (!record.StockStepObserved && !record.RuptureStepObserved) continue;
            if (!completed) { record.NativeStepUncertain = true; Event(record, "native-step-failed", "integration unconfirmed; pending migration disabled"); continue; }
            if (record.StockStepObserved)
            {
                record.StockNativePending = false;
                if (record.StockBody != null) { record.StockNativeSteps++;
                    Event(record, "native-stock-step-consumed", $"beforeV={record.StockBeforeVelocity:F4}, beforeW={record.StockBeforeAngular:F4}, {BodyMotion(record.StockBody)}; net change includes contacts/gravity/joints"); }
                else { record.NativeStepUncertain = true; Event(record, "native-stock-target-retired", "target destroyed during simulation; integration unconfirmed"); }
            }
            if (record.RuptureStepObserved)
            {
                record.RuptureNativePending = false;
                if (record.RuptureBody != null) { record.RuptureNativeSteps++;
                    Event(record, "native-rupture-step-consumed", $"beforeV={record.RuptureBeforeVelocity:F4}, beforeW={record.RuptureBeforeAngular:F4}, {BodyMotion(record.RuptureBody)}; net change includes contacts/gravity/joints"); }
                else { record.NativeStepUncertain = true; Event(record, "native-rupture-target-retired", "target destroyed during simulation; integration unconfirmed"); }
            }
            record.StockStepObserved = record.RuptureStepObserved = false;
        }
    }

    internal static void ExternalStepSubmitted()
    {
        if (!Enabled) return;
        foreach (Record record in Records)
        {
            record.StockExternalInFlight = record.StockExternalPending; record.StockExternalPending = false;
            record.RuptureExternalInFlight = record.RuptureExternalPending; record.RuptureExternalPending = false;
        }
    }
    internal static void ExternalStepCompleted()
    {
        if (!Enabled) return;
        foreach (Record record in Records)
        {
            if (!record.StockExternalInFlight && !record.RuptureExternalInFlight) continue;
            bool stockAvailable = ShadowRagdollWorld.TryReadLethalExperimentMotion(record.StockBody, out Vector3 stockV, out Vector3 stockW);
            bool ruptureAvailable = ShadowRagdollWorld.TryReadLethalExperimentMotion(record.RuptureBody, out Vector3 rptV, out Vector3 rptW);
            Event(record, "external-step-completed", $"stockSubmitted={record.StockExternalInFlight}, ruptureSubmitted={record.RuptureExternalInFlight}, stockMotionAvailable={stockAvailable}, ruptureMotionAvailable={ruptureAvailable}, stockV={stockV:F4}, stockW={stockW:F4}, ruptureV={rptV:F4}, ruptureW={rptW:F4}; net result includes other forces and constraints");
            record.StockExternalInFlight = record.RuptureExternalInFlight = false;
        }
    }

    internal static void End(CorpseRagdoll ragdoll, string reason)
    { AngularDriveExperiment.End(ragdoll, reason); RootVelocityExperiment.End(ragdoll, reason); Record record = Find(ragdoll); if (record != null) Release(record, reason); }

    internal static void Reset()
    {
        RootVelocityExperiment.Reset();
        foreach (Record record in Records) Release(record, "raid-reset");
        _enabled = _physical = false; _source = LethalImpulseSource.Baseline;
        _shotContext = default;
        Records.Clear(); Array.Clear(Events, 0, Events.Length);
        _eventCount = _eventCursor = _dropped = 0; _nextId = 0;
    }
    private static void Event(Record record, string stage, string detail)
    {
        if (stage == "comparison-invalidated") record.ComparisonInvalidated = true;
        string text = $"RPT lethal id={record.Id}, profile={record.Profile}, projectile={record.Projectile:X16}, fire={record.FireIndex}, frame={Time.frameCount}, t={Time.realtimeSinceStartup:F5}, ageMs={(Time.realtimeSinceStartup - record.CreatedAt) * 1000f:F2}, admitted={record.Policy.Admitted}, stage={stage}: {detail}";
        Events[_eventCursor] = text.Length <= 1200 ? text : text.Substring(0, 1200);
        _eventCursor = (_eventCursor + 1) % MaximumEvents; _eventCount = Math.Min(MaximumEvents, _eventCount + 1);
    }
    internal static void Dump()
    {
        RupturePlugin.Log.Initialization($"RPT lethal trace: configured={RuptureConfiguration.CorrectedLethalImpulse?.Value}, retainedEvents={_eventCount}/{MaximumEvents}, liveRecords={Records.Count}, droppedComparisonDeaths={_dropped}. Native step consumption proves integration boundary, not isolated force contribution.");
        for (int i = 0; i < _eventCount; i++) RupturePlugin.Log.Initialization(Events[(_eventCursor - _eventCount + i + MaximumEvents) % MaximumEvents]);
    }
}
