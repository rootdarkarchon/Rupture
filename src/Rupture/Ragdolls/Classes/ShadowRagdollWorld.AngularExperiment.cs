using System;
using System.Collections.Generic;
using EFT.Interactive;
using Rupture.Ragdolls.External;
using UnityEngine;

namespace Rupture.Ragdolls.Classes;

internal static partial class ShadowRagdollWorld
{
    internal static bool AngularDrivesAvailable => _externalRuntime is IExternalAngularDriveRuntime runtime && runtime.AngularDrivesAvailable;
    internal static bool TryPrepareAngularDrives(CorpseRagdoll ragdoll, AngularDriveExperiment.Target[] targets, bool residual, float duration,
        out IExternalPhysicsJoint[] joints, out float error, out string failure)
    {
        joints = Array.Empty<IExternalPhysicsJoint>(); error = 0f; failure = "inactive core or missing angular capability";
        if (!AngularDrivesAvailable || ragdoll == null || targets == null || targets.Length > 32 ||
            !RigsByRagdoll.TryGetValue(ragdoll, out ShadowRig rig) || !rig.Active || !rig.Committed || IsDragHeld(rig.Corpse) || IsHandoffHeld(rig.Corpse)) return false;
        var selected = new List<IExternalPhysicsJoint>(targets.Length);
        var drives = new List<ExternalAngularDrive>(targets.Length);
        foreach (AngularDriveExperiment.Target target in targets)
        {
            if (!TryAngularPair(rig, target, out ShadowBody child, out ShadowBody parent)) continue;
            foreach (ExternalJointBinding binding in rig.ExternalJoints)
            {
                if (!ReferenceEquals(binding.Body, child) || !ReferenceEquals(binding.ConnectedBody, parent) || binding.NativeJoint == null ||
                    binding.NativeJoint.connectedBody != target.Parent || binding.Joint?.IsValid != true || binding.Joint.IsBroken) continue;
                CharacterJoint joint = binding.NativeJoint;
                if (Mathf.Abs(joint.highTwistLimit.limit - joint.lowTwistLimit.limit) < 0.006f && joint.swing1Limit.limit < 0.006f && joint.swing2Limit.limit < 0.006f) continue;
                float angle = Quaternion.Angle(target.Relative, Quaternion.Inverse(parent.Proxy.rotation) * child.Proxy.rotation);
                if (float.IsNaN(angle) || angle > 45f) { failure = "captured pose already diverged over 45 degrees"; return false; }
                error = Mathf.Max(error, angle);
                selected.Add(binding.Joint);
                AngularDriveExperiment.Strength(target.Child.name, out float spring, out float damping, out float torque);
                drives.Add(new ExternalAngularDrive(binding.Joint, target.Relative, spring, damping, torque, duration));
                break;
            }
        }
        if (selected.Count == 0) { failure = "no retained dynamic core connections with angular freedom"; return false; }
        if (residual && !((IExternalAngularDriveRuntime)_externalRuntime).TrySetAngularDrives(drives, out failure)) return false;
        joints = selected.ToArray(); failure = "ok"; return true;
    }
    private static bool TryAngularPair(ShadowRig rig, AngularDriveExperiment.Target target, out ShadowBody child, out ShadowBody parent)
    {
        child = parent = null;
        return target.Child != null && target.Parent != null && rig.BodiesByNative.TryGetValue(target.Child, out child) &&
            rig.BodiesByNative.TryGetValue(target.Parent, out parent) && child.CountsForSleep && parent.CountsForSleep &&
            !rig.PermanentlyDisabledBodies.Contains(child) && !rig.PermanentlyDisabledBodies.Contains(parent) &&
            child.Proxy is ExternalShadowBodyProxy a && a.ExternalBody.IsValid && !a.isKinematic &&
            parent.Proxy is ExternalShadowBodyProxy b && b.ExternalBody.IsValid && !b.isKinematic;
    }
    internal static bool TryObserveAngularDrives(CorpseRagdoll ragdoll, AngularDriveExperiment.Target[] targets, out float error, out float speed)
    {
        error = speed = 0f;
        if (ragdoll == null || !RigsByRagdoll.TryGetValue(ragdoll, out ShadowRig rig) || !rig.Active || IsDragHeld(rig.Corpse) || IsHandoffHeld(rig.Corpse)) return false;
        int pairs = 0;
        foreach (var target in targets)
        {
            if (!TryAngularPair(rig, target, out ShadowBody child, out ShadowBody parent)) continue;
            pairs++;
            error = Mathf.Max(error, Quaternion.Angle(target.Relative, Quaternion.Inverse(parent.Proxy.rotation) * child.Proxy.rotation));
            speed = Mathf.Max(speed, Mathf.Max(child.Proxy.angularVelocity.magnitude, parent.Proxy.angularVelocity.magnitude));
        }
        return pairs > 0 && !float.IsNaN(error) && !float.IsInfinity(speed);
    }
    internal static bool TryClearAngularDrives(IExternalPhysicsJoint[] joints, out string failure)
    {
        failure = "angular capability unavailable";
        var commands = new List<ExternalAngularDrive>(joints.Length);
        foreach (var joint in joints)
            if (joint?.IsValid == true) commands.Add(new ExternalAngularDrive(joint, Quaternion.identity, 0, 0, 0, 0));
        if (commands.Count == 0) { failure = "all joints retired"; return true; }
        if (!AngularDrivesAvailable) return false;
        return ((IExternalAngularDriveRuntime)_externalRuntime).TrySetAngularDrives(commands, out failure);
    }
    internal static bool TryReadAngularDrives(IExternalPhysicsJoint[] joints, out int live, out int active, out float maxTorque, out float remaining, out string failure)
    {
        live = active = 0; maxTorque = remaining = 0f; failure = "angular capability unavailable";
        var retained = new List<IExternalPhysicsJoint>(joints.Length);
        foreach (var joint in joints) if (joint?.IsValid == true) retained.Add(joint);
        if (retained.Count == 0) { failure = "all joints retired"; return true; }
        if (!AngularDrivesAvailable) return false;
        if (!((IExternalAngularDriveRuntime)_externalRuntime).TryReadAngularDrives(retained, out ExternalAngularDriveState[] states, out failure)) return false;
        live = states.Length;
        foreach (var state in states)
        {
            if (state.Active) active++;
            maxTorque = Mathf.Max(maxTorque, state.TorquePerRow); remaining = Mathf.Max(remaining, state.Remaining);
        }
        return true;
    }
}
