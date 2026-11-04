using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rupture.Ragdolls.External;

namespace Rupture.PhysXBackend;

public sealed partial class ExternalPhysXRuntime
{
    public bool AngularDrivesAvailable => _api?.SetD6Drives != null && _api.GetD6Drives != null;
    private bool OwnsDriveJoint(IExternalPhysicsJoint joint) => joint is ExternalJoint external && external.IsValid &&
        _joints.TryGetValue(external.Handle, out ExternalJoint current) && ReferenceEquals(current, external);
    public bool TrySetAngularDrives(IReadOnlyList<ExternalAngularDrive> drives, out string failure)
    {
        failure = null;
        if (!AngularDrivesAvailable || !AtControlBoundary(out failure) || drives == null || drives.Count == 0 || drives.Count > 32)
        { failure ??= "angular capability unavailable or invalid batch"; return false; }
        var commands = new NativeApi.AngularDriveCommand[drives.Count];
        for (int i = 0; i < drives.Count; i++)
        {
            var d = drives[i];
            if (!OwnsDriveJoint(d.Joint)) { failure = "stale/foreign angular joint"; return false; }
            commands[i] = new NativeApi.AngularDriveCommand { StructSize = (uint)Marshal.SizeOf<NativeApi.AngularDriveCommand>(),
                Joint = d.Joint.Handle, ParentToChild = new NativeApi.Quat(d.ParentToChild), Stiffness = d.Stiffness,
                Damping = d.Damping, TorquePerRow = d.TorquePerRow, Duration = d.Duration };
        }
        try { InvokeOwner(() => _scene.SetAngularDrives(commands)); return true; }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; return false; }
    }
    public bool TryReadAngularDrives(IReadOnlyList<IExternalPhysicsJoint> joints, out ExternalAngularDriveState[] states, out string failure)
    {
        states = Array.Empty<ExternalAngularDriveState>(); failure = null;
        if (!AngularDrivesAvailable || !AtControlBoundary(out failure) || joints == null || joints.Count == 0 || joints.Count > 32)
        { failure ??= "angular capability unavailable or invalid read"; return false; }
        var native = new NativeApi.AngularDriveState[joints.Count];
        for (int i = 0; i < joints.Count; i++)
        {
            if (!OwnsDriveJoint(joints[i])) { failure = "stale/foreign angular joint"; return false; }
            native[i] = new NativeApi.AngularDriveState { StructSize = (uint)Marshal.SizeOf<NativeApi.AngularDriveState>(), Joint = joints[i].Handle };
        }
        try
        {
            InvokeOwner(() => _scene.ReadAngularDrives(native));
            states = new ExternalAngularDriveState[native.Length];
            for (int i = 0; i < states.Length; i++) states[i] = new ExternalAngularDriveState(native[i].Active != 0,
                native[i].Stiffness, native[i].Damping, native[i].TorquePerRow, native[i].Remaining);
            return true;
        }
        catch (Exception exception) { failure = exception.GetType().Name + ": " + exception.Message; return false; }
    }
}
