using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Rupture.Ragdolls.External;

[StructLayout(LayoutKind.Sequential)]
public struct CasingPose
{
    public Vector3 Position;
    public Quaternion Rotation;
    public CasingPose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
}

[StructLayout(LayoutKind.Sequential)]
public struct CasingDescriptor
{
    public ulong Id;
    public CasingPose Pose, ShapePose;
    public Vector3 Velocity, AngularVelocity;
    public float Radius, HalfHeight, Mass;
    public uint LayerBits, CollisionMask, Box;
}

[StructLayout(LayoutKind.Sequential)]
public struct CasingImpact
{
    public ulong EventId;
    public uint Kind;
    public Vector3 Start, End, Direction;
    public float Radius, Strength;
}

[StructLayout(LayoutKind.Sequential)]
public struct CasingState
{
    public ulong Id;
    public CasingPose Pose;
    public Vector3 Velocity;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct CasingContact
{
    public ulong Id;
    public Vector3 Position, Normal;
    public float Speed;
}

// Main owns this reusable batch until QueueCasingFrame. Owner owns it until
// CompleteStep publishes it. No Unity objects cross that boundary.
public sealed class ExternalCasingFrame
{
    public CasingDescriptor[] Creates = new CasingDescriptor[128];
    public ulong[] Removes = new ulong[128];
    public CasingImpact[] Impacts = new CasingImpact[128];
    public CasingState[] States = new CasingState[128];
    public CasingContact[] Contacts = new CasingContact[512];
    public int CreateCount, RemoveCount, ImpactCount, StateCount, ContactCount;
    public double CommandMilliseconds, SimulationMilliseconds, ReadMilliseconds;
    public Vector3 Listener;
    public bool Audio;
    public void Clear() { CreateCount = RemoveCount = ImpactCount = StateCount = ContactCount = 0; }
    public void Create(CasingDescriptor value) { Grow(ref Creates, CreateCount); Creates[CreateCount++] = value; }
    public void Remove(ulong id) { Grow(ref Removes, RemoveCount); Removes[RemoveCount++] = id; }
    public void Impact(CasingImpact value) { Grow(ref Impacts, ImpactCount); Impacts[ImpactCount++] = value; }
    private static void Grow<T>(ref T[] array, int count) { if (count == array.Length) Array.Resize(ref array, checked(array.Length * 2)); }
}
