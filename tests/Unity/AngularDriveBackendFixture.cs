#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Rupture.Ragdolls.External;
using UnityEngine;

// The production API loader, joint snapshot capture, angular backend methods and
// world transfer run unchanged. Only owner dispatch/scene lifetime are adapted.
namespace Rupture.PhysXBackend
{
    internal sealed class ExternalJoint : IExternalPhysicsJoint
    {
        internal ExternalJoint(ulong handle) { Handle = handle; }
        public ulong Handle { get; }
        public bool IsValid { get; internal set; } = true;
        public bool IsBroken { get; internal set; }
    }
    internal sealed class NativeScene
    {
        internal IntPtr Handle;
        internal NativeApi Api;
        internal void SetAngularDrives(NativeApi.AngularDriveCommand[] commands) => Api.Check(Api.SetD6Drives(Handle,commands,(uint)commands.Length),"fixture native angular batch");
        internal void ReadAngularDrives(NativeApi.AngularDriveState[] states) => Api.Check(Api.GetD6Drives(Handle,states,(uint)states.Length),"fixture native angular read");
    }
    public sealed partial class ExternalPhysXRuntime : IExternalAngularDriveRuntime, IDisposable
    {
        private NativeApi _api;
        private NativeScene _scene;
        private readonly Dictionary<ulong,ExternalJoint> _joints = new Dictionary<ulong,ExternalJoint>();
        internal bool FixtureInFlight;
        internal ExternalPhysXRuntime(IntPtr scene, string backendRoot = null)
        {
            _api = new NativeApi(backendRoot ?? Path.Combine(Application.dataPath,"Plugins/x86_64"),false);
            _scene = new NativeScene { Handle = scene, Api = _api };
        }
        private bool AtControlBoundary(out string failure)
        { failure = FixtureInFlight ? "step in flight" : null; return !FixtureInFlight; }
        private void InvokeOwner(Action action) => action();
        internal ExternalJoint Create(CharacterJoint joint, ulong child, ulong parent)
        {
            var desc = JointSnapshot.Capture(joint,child,parent).D6;
            _api.Check(_api.CreateD6Joint(_scene.Handle,ref desc,out ulong handle),"fixture create actual captured D6");
            var created = new ExternalJoint(handle); _joints.Add(handle,created); return created;
        }
        internal void Destroy(ExternalJoint joint)
        { _api.Check(_api.DestroyJoint(_scene.Handle,joint.Handle),"fixture destroy D6"); joint.IsValid=false; _joints.Remove(joint.Handle); }
        public void Dispose() { foreach (var joint in _joints.Values) joint.IsValid=false; _joints.Clear(); _api?.Dispose(); _api=null; }
    }
}
#endif
