using System;
using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

// Captures the pre-clamp root once per corpse and transfers only its missing
// linear velocity at the first external submission. Existing forces are retained.
internal static class RootVelocityExperiment
{
    internal const int MaximumRecords = 32, MaximumEvents = 256;
    internal sealed class Record
    {
        internal CorpseRagdoll Ragdoll;
        internal LethalImpulseExperiment.Record Lethal;
        internal Vector3 Raw, Seed, Delta;
        internal float CreatedAt;
        internal bool Inherited, Started, Evaluated, Prepared, InFlight;
        internal bool Production, Angular;
        internal int CreatedFrame;
    }
    private static readonly List<Record> Records = new List<Record>(MaximumRecords);
    private static readonly string[] Events = new string[MaximumEvents];
    private static int _cursor, _count, _dropped;
    private static bool _inherited;
    internal static bool Enabled { get; private set; }
    internal static void Baseline() => Select(false);
    internal static void Inherited() => Select(true);
    private static void Select(bool inherited)
    {
        if (AngularDriveExperiment.Enabled && !inherited)
        { RupturePlugin.Log.InitializationWarning("RPT angular experiment fixes velocity inheritance ON. RPTAngularOff restores other comparison controls."); return; }
        if (!LethalImpulseExperiment.LocalComparisonAllowed || !Patches.LethalImpulseHooks.Available ||
            !Patches.RootVelocityHooks.Available || !ShadowRagdollWorld.Enabled || Physics.simulationMode != SimulationMode.Script)
        {
            RupturePlugin.Log.InitializationWarning("RPT velocity mode unchanged: requires external physics, installed capture hooks and a local raid / solo graphical Fika host with zero peers.");
            return;
        }
        LethalImpulseExperiment.FixForVelocity();
        Enabled = true; _inherited = inherited;
        RupturePlugin.Log.Initialization($"RPT velocity experiment: mode={(inherited ? "Inherited" : "Baseline")}, lethal=RuptureOnly 1x fixed. NEW deaths only; resets at raid end. Dump with RPTVelocityDump; restore defaults with RPTVelocityOff.");
    }
    internal static void Off()
    {
        if (AngularDriveExperiment.Enabled) { AngularDriveExperiment.Off(); return; }
        foreach (Record record in Records) Event(record, "ended", "experiment-off; existing motion retained");
        Records.Clear(); Enabled = false;
        LethalImpulseExperiment.Off();
    }
    internal static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) &&
        !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    internal static Vector3 StockSeed(Vector3 raw) => raw.normalized.Equals(Vector3.up) ? Vector3.up : Vector3.ClampMagnitude(raw, 2f);
    internal static Record BeforeStart(CorpseRagdoll ragdoll, Vector3 raw)
    {
        bool production = !Enabled;
        if (production && (ragdoll?._owner is not Corpse || !LethalImpulseExperiment.ProductionAuthorityAllowed ||
            (RuptureConfiguration.InheritRootVelocity?.Value != true && RuptureConfiguration.AngularResistance?.Value != true))) return null;
        // Retire before allocating a slot, even if no external step was possible.
        Expire();
        LethalImpulseExperiment.Record lethal = LethalImpulseExperiment.FindVelocityDeath(ragdoll);
        if (!production && lethal == null) { Event(null, "excluded", "no reliable Rupture-only 1x terminal bullet identity"); return null; }
        foreach (Record previous in Records)
            if (ReferenceEquals(previous.Ragdoll, ragdoll))
            { previous.Evaluated = true; Event(previous, "excluded", "repeated-native-start"); return null; }
        if (!production && Records.Count >= MaximumRecords) { _dropped++; return null; }
        Record record = new Record { Ragdoll = ragdoll, Lethal = lethal, Raw = raw, Seed = StockSeed(raw),
            CreatedAt = Time.realtimeSinceStartup, CreatedFrame = Time.frameCount, Production = production,
            Angular = production && RuptureConfiguration.AngularResistance.Value,
            Inherited = production ? RuptureConfiguration.InheritRootVelocity.Value : _inherited };
        record.Delta = raw - record.Seed;
        Records.Add(record);
        if (!Finite(raw) || raw.sqrMagnitude > 225f || !Allowed(record))
        { record.Evaluated = true; Event(record, "excluded", "invalid/over-15mps-root-or-network-role"); }
        else Event(record, "captured", $"raw={raw:F4}, stockSeed={record.Seed:F4}, missing={record.Delta:F4}");
        return record;
    }
    internal static void AfterStart(Record record, Vector3 seed, bool ran)
    {
        if (record == null || record.Evaluated) return;
        record.Started = ran && Finite(seed) && (seed - record.Seed).sqrMagnitude < 0.000001f;
        if (!record.Started) { record.Evaluated = true; Event(record, "excluded", $"native-start-mismatch, ran={ran}, actualSeed={seed:F4}"); }
    }
    // Called only after the previous scene result is published and immediately
    // before submission. A late source decision never edits a previously stepped rig.
    internal static void BeforeExternalStep()
    {
        if (Records.Count == 0) return;
        Expire();
        foreach (Record record in Records)
        {
            if (record.Evaluated || !record.Started) continue;
            if (record.Production ? record.Ragdoll?._owner is not Corpse corpse || !ShadowRagdollWorld.IsActive(corpse) : !record.Lethal.Policy.Admitted) continue;
            record.Evaluated = true;
            var lethal = record.Lethal;
            if (!Allowed(record) || (!record.Production && (!lethal.Policy.Finished || lethal.Policy.Fallback || lethal.ComparisonInvalidated ||
                lethal.Policy.Source != LethalImpulseSource.RuptureOnly || !lethal.Policy.Physical ||
                !ReferenceEquals(lethal.Ragdoll, record.Ragdoll) || Time.realtimeSinceStartup - record.CreatedAt > 0.5f)))
            { Event(record, "excluded", "first-step-source-not-selected-1x-or-stale-or-ownership-lost"); continue; }
            if (!ShadowRagdollWorld.TryAdjustRootVelocity(record.Ragdoll, record.Delta, record.Inherited,
                out int bodies, out Vector3 before, out Vector3 after, out string reason))
            { Event(record, "excluded", reason); continue; }
            record.Prepared = true;
            Event(record, "prepared", $"bodies={bodies}, raw={record.Raw:F4}, stockSeed={record.Seed:F4}, delta={(record.Inherited ? record.Delta : Vector3.zero):F4}, meanBefore={before:F4}, meanAfter={after:F4}; linear only, pending forces preserved");
        }
    }
    internal static void ExternalStepSubmitted()
    {
        if (Records.Count == 0) return;
        foreach (Record record in Records)
            if (record.Prepared) { record.Prepared = false; record.InFlight = true; Event(record, "submitted", "first external step"); }
    }
    internal static void ExternalStepCompleted()
    {
        if (Records.Count == 0) return;
        foreach (Record record in Records)
        {
            if (!record.InFlight) continue;
            record.InFlight = false;
            bool read = ShadowRagdollWorld.TryAdjustRootVelocity(record.Ragdoll, Vector3.zero, false,
                out int bodies, out Vector3 velocity, out _, out string reason);
            Event(record, "completed", $"valid={read && (record.Production || record.Lethal == null || !record.Lethal.ComparisonInvalidated) && Allowed(record)}, readback={read}, bodies={bodies}, meanVelocity={velocity:F4}, reason={reason}; includes impulse/gravity/contacts/joints");
        }
    }
    private static void Expire()
    {
        for (int i = Records.Count - 1; i >= 0; i--)
            if (Time.realtimeSinceStartup - Records[i].CreatedAt >= 4f && (!Records[i].Production || Time.frameCount > Records[i].CreatedFrame + 1))
            { if (!Records[i].Evaluated || Records[i].Prepared || Records[i].InFlight) Event(Records[i], "ended", "capture-expired"); Records.RemoveAt(i); }
    }
    internal static void End(CorpseRagdoll ragdoll, string reason)
    {
        if (Records.Count == 0) return;
        for (int i = Records.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Records[i].Ragdoll, ragdoll)) { Event(Records[i], "ended", reason); Records.RemoveAt(i); }
    }
    internal static void Reset()
    {
        AngularDriveExperiment.Reset();
        Enabled = _inherited = false; Records.Clear(); Array.Clear(Events, 0, Events.Length);
        _cursor = _count = _dropped = 0;
    }
    private static void Event(Record record, string stage, string detail)
    {
        string text = $"RPT velocity lethalId={record?.Lethal?.Id ?? 0}, profile={record?.Lethal?.Profile ?? (record?.Ragdoll?._owner as Corpse)?.PlayerProfileID}, mode={(record?.Inherited == true ? "Inherited" : "Baseline")}, frame={Time.frameCount}, t={Time.realtimeSinceStartup:F5}, ageMs={(record == null ? 0f : (Time.realtimeSinceStartup - record.CreatedAt) * 1000f):F2}, stage={stage}: {detail}";
        Events[_cursor] = text.Length <= 1200 ? text : text.Substring(0, 1200);
        _cursor = (_cursor + 1) % MaximumEvents; _count = Math.Min(_count + 1, MaximumEvents);
    }
    internal static bool Allowed(Record record) => record.Production ? LethalImpulseExperiment.ProductionAuthorityAllowed : LethalImpulseExperiment.LocalComparisonAllowed;
    internal static void Dump()
    {
        Expire();
        RupturePlugin.Log.Initialization($"RPT velocity trace: configured={RuptureConfiguration.InheritRootVelocity?.Value}, comparisonOverride={Enabled}, events={_count}/{MaximumEvents}, runtimeRecords={Records.Count}, droppedComparisonCaptures={_dropped}; only prepared+completed pairs are valid comparison samples.");
        for (int i = 0; i < _count; i++) RupturePlugin.Log.Initialization(Events[(_cursor - _count + i + MaximumEvents) % MaximumEvents]);
        LethalImpulseExperiment.Dump();
    }
}
