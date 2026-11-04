using System;
using System.Collections.Generic;
using EFT.Interactive;
using Rupture.Ragdolls.External;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static class AngularDriveExperiment
{
    internal const float Duration = 0.45f;
    internal sealed class Target { internal Rigidbody Child, Parent; internal Quaternion Relative; }
    internal sealed class Record
    {
        internal RootVelocityExperiment.Record Root;
        internal Target[] Targets;
        internal IExternalPhysicsJoint[] Joints = Array.Empty<IExternalPhysicsJoint>();
        internal bool Residual, Evaluated, Active, Prepared, InFlight, Closed, LifecycleEnded;
        internal float PeakError, PeakSpeed;
        internal string StopReason;
        internal bool CleanupWarning;
        internal float StartedAt;
    }
    private static readonly List<Record> Records = new List<Record>(32);
    private static readonly string[] Events = new string[256];
    private static int _cursor, _count, _dropped;
    private static bool _residual;
    internal static bool Enabled { get; private set; }
    internal static void Baseline() => Select(false);
    internal static void Residual() => Select(true);
    private static void Select(bool residual)
    {
        if (!LethalImpulseExperiment.LocalComparisonAllowed || !ShadowRagdollWorld.AngularDrivesAvailable)
        { RupturePlugin.Log.InitializationWarning("RPT angular mode unchanged: requires a local raid / solo graphical host and the optional native angular capability."); return; }
        RootVelocityExperiment.Inherited();
        if (!RootVelocityExperiment.Enabled) return;
        Enabled = true; _residual = residual;
        RupturePlugin.Log.Initialization($"RPT angular experiment: mode={(residual ? "Residual" : "Baseline")}, velocity=Inherited fixed, lethal=RuptureOnly 1x fixed, window={Duration:F2}s. NEW deaths only; dump with RPTAngularDump; restore defaults with RPTAngularOff.");
    }
    internal static void Off()
    {
        Enabled = false; // live drives are cleared at the next safe owner boundary
        foreach (Record record in Records) if (!record.Active) record.Closed = true;
        RootVelocityExperiment.Off();
        RupturePlugin.Log.Initialization("RPT angular experiment off; active drive cancellation queued for the next scene-owner boundary. Defaults restored for new deaths.");
    }
    internal static bool Finite(Quaternion q) => RootVelocityExperiment.Finite(new Vector3(q.x, q.y, q.z)) &&
        !float.IsNaN(q.w) && !float.IsInfinity(q.w) && Mathf.Abs(q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w - 1f) < 0.01f;
    internal static void Capture(RootVelocityExperiment.Record root)
    {
        if (root == null || !root.Started || root.Evaluated ||
            (root.Production ? !root.Angular : !Enabled || !root.Inherited)) return;
        Expire(false);
        foreach (var existing in Records) if (ReferenceEquals(existing.Root.Ragdoll, root.Ragdoll)) return;
        if (!root.Production && Records.Count >= 32) { _dropped++; return; }
        var targets = new List<Target>(16);
        foreach (var spawner in root.Ragdoll._jointSpawners ?? Array.Empty<CharacterJointSpawner>())
        {
            var joint = spawner != null ? spawner.GetComponent<CharacterJoint>() : null;
            var child = joint != null ? joint.GetComponent<Rigidbody>() : null;
            var parent = joint != null ? joint.connectedBody : null;
            if (child == null || parent == null) continue;
            Quaternion relative = Quaternion.Inverse(parent.rotation) * child.rotation;
            if (!Finite(relative) || targets.Count >= 32) { Event(null, "excluded", "invalid pose or over-32 joints"); return; }
            targets.Add(new Target { Child = child, Parent = parent, Relative = relative });
        }
        var record = new Record { Root = root, Targets = targets.ToArray(), Residual = root.Production || _residual, StartedAt = root.CreatedAt };
        Records.Add(record);
        Event(record, "captured", $"connections={targets.Count}, target=relative-body-pose-after-native-Start-before-first-simulation; no animation continuation");
    }
    internal static void Strength(string name, out float spring, out float damping, out float torque)
    {
        name = name ?? "";
        if (name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("neck", StringComparison.OrdinalIgnoreCase) >= 0)
        { spring = 12f; damping = 1.2f; torque = 2f; }
        else if (name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0)
        { spring = 35f; damping = 3f; torque = 8f; }
        else if (name.IndexOf("thigh", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("calf", StringComparison.OrdinalIgnoreCase) >= 0)
        { spring = 25f; damping = 2f; torque = 6f; }
        else { spring = 18f; damping = 1.5f; torque = 4f; }
    }
    internal static void BeforeExternalStep()
    {
        if (Records.Count == 0) return;
        Expire(true);
        foreach (var record in Records)
        {
            if (record.Closed) continue;
            if (record.Active)
            {
                if (record.StopReason != null) { Stop(record, record.StopReason); continue; }
                if ((!record.Root.Production && (!Enabled || record.Root.Lethal?.ComparisonInvalidated == true)) || !RootVelocityExperiment.Allowed(record.Root) ||
                    Time.realtimeSinceStartup - record.StartedAt >= Duration ||
                    !ShadowRagdollWorld.TryObserveAngularDrives(record.Root.Ragdoll, record.Targets, out _, out _))
                    Stop(record, !record.Root.Production && !Enabled ? "experiment-off" : "window-ended-or-ownership-lost");
                continue;
            }
            if (record.Evaluated || !record.Root.Evaluated) continue;
            record.Evaluated = true;
            // Production resistance starts with simulation, so cold first-build
            // work cannot consume the entire window before any joint has stepped.
            if (record.Root.Production) record.StartedAt = Time.realtimeSinceStartup;
            float remaining = Duration - (Time.realtimeSinceStartup - record.StartedAt);
            if ((!record.Root.Production && (!Enabled || !record.Root.Inherited)) || !record.Root.Prepared || remaining <= 0f)
            { Event(record, "excluded", "velocity/source comparison did not qualify before the angular window"); record.Closed = true; continue; }
            if (!ShadowRagdollWorld.TryPrepareAngularDrives(record.Root.Ragdoll, record.Targets, record.Residual, remaining,
                out record.Joints, out float error, out string failure))
            { Event(record, "excluded", failure); record.Closed = true; continue; }
            record.PeakError = error; record.Active = record.Prepared = true;
            Event(record, "prepared", $"joints={record.Joints.Length}, remaining={remaining:F4}s, initialMaxErrorDeg={error:F2}, maxTorquePerRow={(record.Residual ? 8 : 0)}Nm; quadratic spring/damping/torque fade, no waking or pose/velocity writes");
        }
    }
    internal static void ExternalStepSubmitted()
    {
        foreach (var record in Records)
            if (record.Prepared) { record.Prepared = false; record.InFlight = true; Event(record, "submitted", "first external step"); }
    }
    internal static void ExternalStepCompleted()
    {
        foreach (var record in Records)
        {
            if (!record.Active) continue;
            if (ShadowRagdollWorld.TryObserveAngularDrives(record.Root.Ragdoll, record.Targets, out float error, out float speed))
            { record.PeakError = Mathf.Max(record.PeakError, error); record.PeakSpeed = Mathf.Max(record.PeakSpeed, speed); }
            if (!record.InFlight) continue;
            record.InFlight = false;
            bool read = ShadowRagdollWorld.TryReadAngularDrives(record.Joints, out int live, out int active, out float torque, out float remaining, out string failure);
            bool valid = read && live == record.Joints.Length && (record.Residual ? active == live && torque > 0f : active == 0) &&
                (record.Root.Production || record.Root.Lethal?.ComparisonInvalidated != true) && RootVelocityExperiment.Allowed(record.Root);
            Event(record, "completed", $"valid={valid}, nativeRead={read}, joints={live}, activeDrives={active}, maxTorquePerRow={torque:F3}Nm, remaining={remaining:F4}s, peakErrorDeg={record.PeakError:F2}, peakAngularSpeed={record.PeakSpeed:F2}rad/s, reason={failure}");
            if (!valid) Stop(record, "first-readback-invalid");
        }
    }
    private static void Stop(Record record, string reason)
    {
        if (record.Closed) return;
        record.StopReason ??= reason;
        bool cleared = !record.Residual || ShadowRagdollWorld.TryClearAngularDrives(record.Joints, out _);
        bool read = ShadowRagdollWorld.TryReadAngularDrives(record.Joints, out int live, out int active, out _, out _, out string failure);
        record.Prepared = record.InFlight = false;
        if (!cleared || !read || active != 0)
        {
            if (!record.CleanupWarning)
            {
                record.CleanupWarning = true;
                Event(record, "cleanup-pending", $"reason={record.StopReason}, failure={failure}; retry at next owner boundary");
                RupturePlugin.Log.InitializationWarning("RPT angular cleanup pending; will retry at the next owner boundary. Inspect RPTAngularDump.");
            }
            return;
        }
        Event(record, "disabled", $"reason={record.StopReason}, clearAccepted={cleared}, nativeRead={read}, retainedJoints={live}, activeDrives={active}, peakErrorDeg={record.PeakError:F2}, peakAngularSpeed={record.PeakSpeed:F2}rad/s, failure={failure}");
        record.Active = false; record.Closed = true;
    }
    internal static void End(CorpseRagdoll ragdoll, string reason)
    {
        foreach (var record in Records)
            if (ReferenceEquals(record.Root.Ragdoll, ragdoll))
            {
                if (record.Active) Stop(record, reason); else record.Closed = true;
                if (!record.LifecycleEnded) { Event(record, "lifecycle-ended", reason); record.LifecycleEnded = true; }
            }
    }
    private static void Expire(bool boundary)
    {
        for (int i = Records.Count - 1; i >= 0; i--)
            if (Time.realtimeSinceStartup - Records[i].Root.CreatedAt >= 10f)
            {
                if (Records[i].Active && !boundary) continue;
                if (Records[i].Active) Stop(Records[i], "record-expired");
                if (!Records[i].Active) Records.RemoveAt(i);
            }
    }
    internal static void Reset()
    { Enabled = _residual = false; Records.Clear(); Array.Clear(Events, 0, Events.Length); _cursor = _count = _dropped = 0; }
    internal static void CorpseImpact(Corpse corpse, bool applied, float magnitude, string body)
    {
        if (!Enabled && RuptureConfiguration.AngularResistance?.Value != true && Records.Count == 0) return;
        Record match = null;
        foreach (var record in Records) if (ReferenceEquals(record.Root.Ragdoll, corpse?.Ragdoll)) { match = record; break; }
        Event(match, "corpse-hit", $"hitProfile={corpse?.PlayerProfileID}, applied={applied}, magnitude={magnitude:F4}Ns, body={body}; ordinary postmortem route, acceptance is not isolated integration evidence");
    }
    private static void Event(Record record, string stage, string detail)
    {
        string text = $"RPT angular lethalId={record?.Root.Lethal?.Id ?? 0}, profile={record?.Root.Lethal?.Profile ?? (record?.Root.Ragdoll?._owner as Corpse)?.PlayerProfileID}, mode={(record?.Residual == true ? "Residual" : "Baseline")}, frame={Time.frameCount}, t={Time.realtimeSinceStartup:F5}, ageMs={(record == null ? 0f : (Time.realtimeSinceStartup - record.Root.CreatedAt) * 1000f):F2}, stage={stage}: {detail}";
        Events[_cursor] = text.Length <= 1200 ? text : text.Substring(0, 1200); _cursor = (_cursor + 1) % 256; _count = Math.Min(_count + 1, 256);
    }
    internal static void Dump()
    {
        // Dump never mutates physics; pending cleanup belongs to the owner boundary.
        RupturePlugin.Log.Initialization($"RPT angular trace: configured={RuptureConfiguration.AngularResistance?.Value}, comparisonOverride={Enabled}, events={_count}/256, runtimeRecords={Records.Count}, droppedComparisonCaptures={_dropped}; compare prepared/completed valid=True and disabled activeDrives=0.");
        for (int i = 0; i < _count; i++) RupturePlugin.Log.Initialization(Events[(_cursor - _count + i + 256) % 256]);
        RootVelocityExperiment.Dump();
    }
}
