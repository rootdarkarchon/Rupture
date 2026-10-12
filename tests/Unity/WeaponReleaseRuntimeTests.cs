#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

// Only the backend and native EFT boundary are doubled. Release methods are
// extracted unchanged from ShadowRagdollWorld by Test-HotPathRuntime.ps1.
namespace EFT.Interactive
{
public sealed class CorpseRagdoll
{
    public Rigidbody WeaponRigidbody;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ClearWeapon() { UnityEngine.Object.DestroyImmediate(WeaponRigidbody); }
}
}
namespace SPT.Reflection.Patching
{
    public sealed class PatchTranspiler : Attribute { }
    public sealed class PatchPrefix : Attribute { }
    public abstract class ModulePatch
    {
        protected abstract MethodBase GetTargetMethod();
        public void Enable()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo prefix = GetType().GetMethod("Prefix", flags), transpiler = GetType().GetMethod("Transpile", flags);
            new Harmony("fixture." + GetType().Name).Patch(GetTargetMethod(),
                prefix: prefix == null ? null : new HarmonyMethod(prefix),
                transpiler: transpiler == null ? null : new HarmonyMethod(transpiler));
        }
    }
}
namespace Rupture.Ragdolls.Classes
{
    internal static class CorpseWeaponCollisionFilter { internal static void Forget(EFT.Interactive.CorpseRagdoll ragdoll) { } }
    internal sealed class ExternalShadowBodyProxy { internal readonly object ExternalBody = new object(); }
    internal static partial class ShadowRagdollWorld
    {
        private sealed class ShadowBody
        {
            internal Rigidbody Native; internal Transform NativeTransform; internal object Proxy;
            internal bool NativeHandedOff = true; internal readonly List<Collider> NativeColliders = new List<Collider>();
        }
        private sealed class ExternalJointBinding { internal ShadowBody Body, ConnectedBody; internal object Joint; }
        private sealed class ShadowRig
        {
            internal EFT.Interactive.CorpseRagdoll NativeRagdoll; internal ShadowBody WeaponBody;
            internal bool WeaponReleased, WeaponSleeping, Settled; internal int WeaponSleepingSteps, DetachedCount;
            internal readonly List<ShadowBody> Bodies = new List<ShadowBody>();
            internal readonly HashSet<ShadowBody> PermanentlyDisabledBodies = new HashSet<ShadowBody>();
            internal readonly Dictionary<Rigidbody, ShadowBody> BodiesByNative = new Dictionary<Rigidbody, ShadowBody>();
            internal readonly List<ExternalJointBinding> ExternalJoints = new List<ExternalJointBinding>();
        }
        private sealed class RecordingRuntime
        {
            internal int Bodies, Joints;
            internal void DestroyBody(object body) { Bodies++; }
            internal void DestroyJoint(object joint) { Joints++; }
        }
        private static readonly Dictionary<EFT.Interactive.CorpseRagdoll, ShadowRig> RigsByRagdoll = new Dictionary<EFT.Interactive.CorpseRagdoll, ShadowRig>();
        private static readonly Dictionary<Rigidbody, ShadowBody> BodiesByNative = new Dictionary<Rigidbody, ShadowBody>();
        private static readonly RecordingRuntime _externalRuntime = new RecordingRuntime();
        private static int _releasedEnvironment;
        private static int CountDynamicDetachedBodies(ShadowRig rig) => rig.DetachedCount;
        private static void ReleaseEnvironment(ShadowRig rig) { _releasedEnvironment++; }

        internal static void VerifyWeaponRelease()
        {
            new Rupture.Ragdolls.Patches.CorpseWeaponReleasePatch().Enable();
            for (int scenario = 0; scenario < 4; scenario++)
            {
                var root = new GameObject("weapon retained by EFT");
                var native = root.AddComponent<Rigidbody>(); var ragdoll = new EFT.Interactive.CorpseRagdoll { WeaponRigidbody = native };
                var weapon = new ShadowBody { Native = native, NativeTransform = root.transform, Proxy = new ExternalShadowBodyProxy() };
                var core = new ShadowBody(); var rig = new ShadowRig { NativeRagdoll = ragdoll, WeaponBody = weapon, Settled = scenario != 0, DetachedCount = scenario == 3 ? 1 : 0 };
                rig.Bodies.Add(core); rig.Bodies.Add(weapon); rig.BodiesByNative.Add(native, weapon); BodiesByNative.Add(native, weapon); RigsByRagdoll.Add(ragdoll, rig);
                rig.ExternalJoints.Add(new ExternalJointBinding { Body = core, ConnectedBody = core, Joint = new object() });
                rig.ExternalJoints.Add(new ExternalJointBinding { Body = weapon, ConnectedBody = core, Joint = new object() });
                int destroyed = _externalRuntime.Bodies, joints = _externalRuntime.Joints, released = _releasedEnvironment;
                try
                {
                    ReleaseInvalidWeapon(rig);
                    Require(rig.WeaponBody == weapon, "valid weapons stay bound");
                    if (scenario == 0 || scenario == 3) ragdoll.ClearWeapon();
                    else if (scenario == 1) { UnityEngine.Object.DestroyImmediate(native); ReleaseInvalidWeapon(rig); }
                    else { ragdoll.WeaponRigidbody = null; ReleaseInvalidWeapon(rig); }
                    root.transform.position = new Vector3(40, 3, 12); // EFT reuses the surviving object in hands.
                    Require(root != null && root.transform.position == new Vector3(40, 3, 12), "loot presentation remains EFT-owned");
                    Require(rig.WeaponReleased && rig.WeaponBody == null && weapon.NativeTransform == null && weapon.Proxy == null && !weapon.NativeHandedOff,
                        "all pose and restoration ownership is released");
                    Require(rig.Bodies.Count == 1 && rig.Bodies[0] == core && rig.ExternalJoints.Count == 1 && rig.ExternalJoints[0].Body == core,
                        "corpse body and joint remain untouched");
                    Require(!rig.BodiesByNative.ContainsKey(native) && !BodiesByNative.ContainsKey(native), "destroyed Unity keys are removed");
                    ReleaseWeapon(ragdoll); ReleaseInvalidWeapon(rig);
                    Require(_externalRuntime.Bodies == destroyed + 1 && _externalRuntime.Joints == joints + 1, "release is idempotent");
                    Require(_releasedEnvironment == released + (rig.Settled && rig.DetachedCount == 0 ? 1 : 0), "detached bodies keep their environment");
                }
                finally { RigsByRagdoll.Remove(ragdoll); UnityEngine.Object.DestroyImmediate(root); }
            }
        }
        private static void Require(bool value, string message) { if (!value) throw new Exception("Weapon release: " + message); }
    }
}
#endif
