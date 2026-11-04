using System.Collections.Generic;
using UnityEngine;

namespace Rupture.Ragdolls.External;

// Optional capability: existing backend/runtime and network contracts unchanged.
public readonly struct ExternalAngularDrive
{
    public ExternalAngularDrive(IExternalPhysicsJoint joint, Quaternion parentToChild, float stiffness, float damping, float torquePerRow, float duration)
    { Joint = joint; ParentToChild = parentToChild; Stiffness = stiffness; Damping = damping; TorquePerRow = torquePerRow; Duration = duration; }
    public IExternalPhysicsJoint Joint { get; }
    public Quaternion ParentToChild { get; }
    public float Stiffness { get; }
    public float Damping { get; }
    public float TorquePerRow { get; }
    public float Duration { get; }
}
public readonly struct ExternalAngularDriveState
{
    public ExternalAngularDriveState(bool active, float stiffness, float damping, float torquePerRow, float remaining)
    { Active = active; Stiffness = stiffness; Damping = damping; TorquePerRow = torquePerRow; Remaining = remaining; }
    public bool Active { get; }
    public float Stiffness { get; }
    public float Damping { get; }
    public float TorquePerRow { get; }
    public float Remaining { get; }
}
public interface IExternalAngularDriveRuntime
{
    bool AngularDrivesAvailable { get; }
    bool TrySetAngularDrives(IReadOnlyList<ExternalAngularDrive> drives, out string failure);
    bool TryReadAngularDrives(IReadOnlyList<IExternalPhysicsJoint> joints, out ExternalAngularDriveState[] states, out string failure);
}
