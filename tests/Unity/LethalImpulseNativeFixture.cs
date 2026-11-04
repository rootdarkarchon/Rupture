using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Tiny test adapter to the production ABI 10 bridge, not a replacement solver.
internal sealed class LethalImpulseNativeFixture : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] internal struct Pose { internal Vector3 Position; internal Quaternion Rotation; }
    [StructLayout(LayoutKind.Sequential)] internal struct SceneDesc
    { internal uint Size, Threads; internal Vector3 Gravity; internal float Bounce; internal uint Solver, Friction, Flags, Casings; }
    [StructLayout(LayoutKind.Sequential)] internal struct BodyDesc
    {
        internal uint Size, Kind; internal Pose Pose; internal Vector3 Velocity, Angular;
        internal float Mass, Damping, AngularDamping, Sleep, MaxAngular, MaxDepenetration;
        internal uint PositionIterations, VelocityIterations, Flags;
        internal Vector3 Center, Inertia; internal Quaternion InertiaRotation;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Force
    { internal uint Size, Operation; internal ulong Actor; internal Vector3 Impulse, Point; internal uint Mode, Wake; }
    [StructLayout(LayoutKind.Sequential)] internal struct State
    { internal uint Size, Sleeping; internal ulong Actor; internal Pose Pose; internal Vector3 Velocity, Angular; }
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern int rptpx_create_scene(ref SceneDesc desc, out IntPtr scene);
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern void rptpx_destroy_scene(IntPtr scene);
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern int rptpx_create_actor(IntPtr scene, ref BodyDesc desc, out ulong actor);
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern int rptpx_destroy_actor(IntPtr scene, ulong actor);
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern int rptpx_apply_forces(IntPtr scene, ref Force force, uint count);
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern int rptpx_set_actor_state(IntPtr scene, ulong actor, ref State state, uint kinematic, uint wake);
    [DllImport("rpt_physx_bridge", CallingConvention = CallingConvention.Cdecl)] private static extern int rptpx_step(IntPtr scene, float dt, [In, Out] State[] states, uint capacity, out uint written);
    private IntPtr _scene;
    private ulong _actor;
    private State[] _states = new State[1];
    private readonly System.Collections.Generic.List<LethalImpulseNativeFixture> _actors = new System.Collections.Generic.List<LethalImpulseNativeFixture>();
    private LethalImpulseNativeFixture _sceneOwner;
    internal IntPtr SceneHandle => _scene;
    internal ulong ActorHandle => _actor;
    internal Quaternion Rotation => _pose.Rotation;
    private Pose _pose;
    private bool _ownsScene;
    internal Vector3 Velocity, Angular;
    internal int Queued;
    internal LethalImpulseNativeFixture(Rigidbody body, LethalImpulseNativeFixture sceneOwner = null)
    {
        _ownsScene = sceneOwner == null;
        _sceneOwner = sceneOwner?._sceneOwner ?? this;
        if (_ownsScene)
        {
            SceneDesc scene = new SceneDesc { Size = (uint)Marshal.SizeOf(typeof(SceneDesc)), Bounce = 2f };
            Check(rptpx_create_scene(ref scene, out _scene));
        }
        else _scene = sceneOwner._scene;
        BodyDesc desc = new BodyDesc
        {
            Size = (uint)Marshal.SizeOf(typeof(BodyDesc)), Kind = 1,
            Pose = new Pose { Position = body.position, Rotation = body.rotation },
            Velocity = body.velocity, Angular = body.angularVelocity, Mass = body.mass,
            MaxAngular = 1000f, MaxDepenetration = 10f, PositionIterations = 6, VelocityIterations = 1,
            Flags = 2, Inertia = body.inertiaTensor, InertiaRotation = body.inertiaTensorRotation
        };
        Check(rptpx_create_actor(_scene, ref desc, out _actor)); Velocity = desc.Velocity; Angular = desc.Angular; _pose = desc.Pose;
        _sceneOwner._actors.Add(this);
    }
    internal void SetVelocity(Vector3 velocity)
    {
        var state = new State { Size = (uint)Marshal.SizeOf(typeof(State)), Actor = _actor, Pose = _pose, Velocity = velocity, Angular = Angular };
        Check(rptpx_set_actor_state(_scene, _actor, ref state, 0, 1)); Velocity = velocity;
    }
    internal void Add(Vector3 impulse, Vector3 point)
    {
        Force force = new Force { Size = (uint)Marshal.SizeOf(typeof(Force)), Operation = 2, Actor = _actor, Impulse = impulse, Point = point, Mode = 1, Wake = 1 };
        Check(rptpx_apply_forces(_scene, ref force, 1)); Queued++;
    }
    internal void Step()
    {
        if (_states.Length != _sceneOwner._actors.Count) Array.Resize(ref _states, _sceneOwner._actors.Count);
        Check(rptpx_step(_scene, 1f / 60f, _states, (uint)_states.Length, out uint count));
        if (count != _states.Length) throw new Exception("native fixture lost actor");
        foreach (var state in _states)
            foreach (var actor in _sceneOwner._actors)
                if (actor._actor == state.Actor) { actor.Velocity = state.Velocity; actor.Angular = state.Angular; actor._pose = state.Pose; }
    }
    private static void Check(int result) { if (result != 0) throw new Exception("PhysX bridge fixture error " + result); }
    public void Dispose()
    {
        if (_scene != IntPtr.Zero) { if (_ownsScene) rptpx_destroy_scene(_scene); else Check(rptpx_destroy_actor(_scene, _actor)); }
        _sceneOwner?._actors.Remove(this);
        _scene = IntPtr.Zero;
    }
}
