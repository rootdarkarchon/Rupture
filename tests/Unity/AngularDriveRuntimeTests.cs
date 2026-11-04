#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Rupture;
using Rupture.Ragdolls.Classes;
using Rupture.Ragdolls.External;
using UnityEngine;

public static partial class LethalImpulseRuntimeTests
{
    private static IList AngularRecords => (IList)typeof(AngularDriveExperiment).GetField("Records",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
    private static CharacterJoint AddAngularJoint(Case c, string childName = "Base HumanSpine3")
    {
        c.Body.name = childName;
        var go = new GameObject("angular-parent"); go.transform.SetParent(c.Root.transform,false);
        var parent = go.AddComponent<Rigidbody>(); parent.mass=8;parent.useGravity=false;
        parent.rotation=Quaternion.Euler(0,30,0);c.Body.rotation=Quaternion.Euler(10,20,15);
        parent.drag=parent.angularDrag=0;
        var spawner = go.AddComponent<RigidbodySpawner>();spawner.Rigidbody=parent;
        c.Ragdoll._rigidbodySpawners=new[]{c.Ragdoll._rigidbodySpawners[0],spawner};
        var joint = c.Root.AddComponent<CharacterJoint>();joint.connectedBody=parent;
        joint.autoConfigureConnectedAnchor=false; joint.anchor=joint.connectedAnchor=Vector3.zero;
        joint.lowTwistLimit=new SoftJointLimit {limit=-60};joint.highTwistLimit=new SoftJointLimit {limit=60};
        joint.swing1Limit=joint.swing2Limit=new SoftJointLimit {limit=60};
        c.Ragdoll._jointSpawners=new[]{c.Root.AddComponent<CharacterJointSpawner>()};
        return joint;
    }
    private static void AngularDriveChecks()
    {
        string legacy=System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName,"LegacyBridge");
        if(System.IO.File.Exists(System.IO.Path.Combine(legacy,"rpt_physx_bridge.dll")))
        using(var backend=new Rupture.PhysXBackend.ExternalPhysXRuntime(IntPtr.Zero,legacy))
        {
            if(!backend.AngularDrivesAvailable)
            {
                Check(!backend.TrySetAngularDrives(null,out _),"actual older ABI 10 bridge loads normally and rejects only the optional angular experiment");
                Measurements.Add("legacy bridge without angular exports: production loader initialization succeeded; optional drive configuration rejected");
            }
            else Measurements.Add("legacy bridge fixture already includes optional angular exports; missing-export compatibility not exercised by this run");
        }
        foreach (bool residual in new[]{false,true}) foreach (string region in new[]{"Base HumanSpine3","Base HumanHead","Base HumanRThigh1","Base HumanRForearm1"})
        using (var c=new Case(capture:false))
        {
            var joint=AddAngularJoint(c,region);ShadowRagdollWorld.EnsureAngularFixtureCapability();
            if(residual) AngularDriveExperiment.Residual();else AngularDriveExperiment.Baseline();
            var root=StartVelocity(c,Vector3.right*3);
            var record=(AngularDriveExperiment.Record)AngularRecords[0];
            Check(record.Targets.Length==1 && Quaternion.Angle(record.Targets[0].Relative,Quaternion.Inverse(joint.connectedBody.rotation)*c.Body.rotation)<.001f,
                "angular production Start postfix captures last relative pose, region="+region);
            SelectVelocityImpulse(c);c.ExternalStep();
            Check(record.Evaluated && record.Active && record.Joints.Length==1 && !record.InFlight && root.Inherited && c.Record.Policy.Physical,
                "angular first-step comparison qualifies with inherited velocity and selected 1x, residual="+residual);
            bool read=ShadowRagdollWorld.TryReadAngularDrives(record.Joints,out int live,out int active,out float torque,out _,out _);
            AngularDriveExperiment.Strength(region,out _,out _,out float cap);
            Check(read && live==1 && active==(residual?1:0) && torque<=(residual?cap:0) && torque>=(residual?.01f:0),
                "production managed backend/native readback proves selected angular mode and per-region torque cap");
            record.StartedAt-=.5f;AngularDriveExperiment.BeforeExternalStep();
            Check(record.Closed && ShadowRagdollWorld.TryReadAngularDrives(record.Joints,out _,out active,out torque,out _,out _) && active==0 && torque==0,
                "wall-clock transition deadline clears native drives to zero without waiting for solver time");
            c.ExternalStep();
            Check(ShadowRagdollWorld.TryReadAngularDrives(record.Joints,out _,out active,out _,out _,out _) && active==0,
                "later steps and corpse impacts cannot restart expired angular resistance");
        }
        foreach(string reason in new[]{"off","peers","invalidated","drag","sever","settlement","dormancy","reset","inflight"})
        using(var c=new Case(capture:false))
        {
            AddAngularJoint(c);ShadowRagdollWorld.EnsureAngularFixtureCapability();AngularDriveExperiment.Residual();
            StartVelocity(c,Vector3.zero);SelectVelocityImpulse(c);c.ExternalStep();
            var record=(AngularDriveExperiment.Record)AngularRecords[0];
            if(reason=="off") AngularDriveExperiment.Off();
            if(reason=="peers") MultiplayerRuntime.Current.HasRemotePeers=true;
            if(reason=="invalidated") c.Record.ComparisonInvalidated=true;
            if(reason=="drag") ShadowRagdollWorld.FixtureDragHeld=true;
            if(reason=="sever")
            {
                var rig=ShadowRagdollWorld.RigsByRagdoll[c.Ragdoll];rig.PermanentlyDisabledBodies.Add(rig.CoreBodies[1]);
                ShadowRagdollWorld.FixtureBackend.Destroy((Rupture.PhysXBackend.ExternalJoint)record.Joints[0]);
            }
            if(reason=="settlement" || reason=="dormancy") AngularDriveExperiment.End(c.Ragdoll,reason);
            if(reason=="reset") { LethalImpulseExperiment.Reset(); ShadowRagdollWorld.Clear();Check(!AngularDriveExperiment.Enabled && AngularRecords.Count==0,"raid reset retires angular state and native scene");continue; }
            if(reason=="inflight")
            {
                ShadowRagdollWorld.FixtureBackend.FixtureInFlight=true;
                Check(!ShadowRagdollWorld.FixtureBackend.TryReadAngularDrives(record.Joints,out _,out _) &&
                    !ShadowRagdollWorld.FixtureBackend.TrySetAngularDrives(new[]{new ExternalAngularDrive(record.Joints[0],Quaternion.identity,0,0,0,0)},out _),
                    "production angular backend rejects reads/writes during an in-flight step");
                AngularDriveExperiment.Off();AngularDriveExperiment.BeforeExternalStep();
                Check(record.Active && !record.Closed && record.CleanupWarning,"unconfirmed cleanup stays queued until a safe boundary instead of losing the active transaction");
                ShadowRagdollWorld.FixtureBackend.FixtureInFlight=false;
            }
            AngularDriveExperiment.BeforeExternalStep();
            Check(record.Closed && ShadowRagdollWorld.TryReadAngularDrives(record.Joints,out _,out int active,out _,out _,out _) && active==0,
                "angular drives stop cleanly on "+reason);
            MultiplayerRuntime.Current.HasRemotePeers=false;ShadowRagdollWorld.FixtureDragHeld=false;
            AngularDriveExperiment.BeforeExternalStep();
            Check(!record.Active,"closed angular transaction never restarts after "+reason);
        }
        foreach(string reason in new[]{"pending","fallback","pose-diverged","missing-joint","locked","late"})
        using(var c=new Case(capture:false))
        {
            var joint=AddAngularJoint(c);ShadowRagdollWorld.EnsureAngularFixtureCapability();AngularDriveExperiment.Residual();
            var root=StartVelocity(c,Vector3.zero);var record=(AngularDriveExperiment.Record)AngularRecords[0];
            if(reason=="pending") c.Admit();else SelectVelocityImpulse(c);
            if(reason=="fallback") c.Record.Policy.Fallback=true;
            if(reason=="pose-diverged") record.Targets[0].Relative=Quaternion.Euler(170,0,0);
            if(reason=="missing-joint") ShadowRagdollWorld.FixtureBackend.Destroy((Rupture.PhysXBackend.ExternalJoint)ShadowRagdollWorld.RigsByRagdoll[c.Ragdoll].ExternalJoints[0].Joint);
            if(reason=="locked") {joint.lowTwistLimit=joint.highTwistLimit=new SoftJointLimit();joint.swing1Limit=joint.swing2Limit=new SoftJointLimit();}
            if(reason=="late") record.StartedAt-=.46f;
            c.ExternalStep();
            Check(record.Closed && !record.Active && record.Joints.Length==0,"angular comparison excludes unsafe or ambiguous sample: "+reason);
        }
        using(var c=new Case(capture:false))
        {
            AddAngularJoint(c);ShadowRagdollWorld.EnsureAngularFixtureCapability();AngularDriveExperiment.Residual();
            RootVelocityExperiment.Baseline();LethalImpulseExperiment.StockOnly();LethalImpulseExperiment.Amplified();
            var root=StartVelocity(c,Vector3.zero);
            Check(root.Inherited && c.Record.Policy.Physical && c.Record.Policy.Source==LethalImpulseSource.RuptureOnly,
                "angular experiment locks both earlier experiments consistently");
            SelectVelocityImpulse(c);c.ExternalStep();
            var liveRecord=(AngularDriveExperiment.Record)AngularRecords[0];
            var foreign=new Rupture.PhysXBackend.ExternalJoint(liveRecord.Joints[0].Handle);
            Check(!ShadowRagdollWorld.FixtureBackend.TrySetAngularDrives(new[]{new ExternalAngularDrive(foreign,Quaternion.identity,0,0,0,0)},out _) &&
                !ShadowRagdollWorld.FixtureBackend.TryReadAngularDrives(new[]{foreign},out _,out _),"matching numeric handle from a foreign joint object cannot change or read owned drives");
            RootVelocityExperiment.Off();
            Check(!AngularDriveExperiment.Enabled && !RootVelocityExperiment.Enabled && !LethalImpulseExperiment.Enabled,
                "existing velocity off command safely restores defaults across nested experiments");
            Check(!AngularDriveExperiment.Finite(new Quaternion(0,0,0,0)) && !AngularDriveExperiment.Finite(new Quaternion(float.NaN,0,0,1)),
                "malformed captured angular poses are rejected");
        }
        var bounded=new System.Collections.Generic.List<Case>();
        try
        {
            ShadowRagdollWorld.EnsureAngularFixtureCapability();AngularDriveExperiment.Residual();
            for(int i=0;i<40;i++)
            {
                var c=new Case(capture:false);bounded.Add(c);AddAngularJoint(c);
                var shot=MakeShot(c);CaptureTerminal(c,shot);c.Ragdoll.Start();
            }
            Check(AngularRecords.Count==32,"angular death captures remain bounded at 32 under overflow");
            foreach(AngularDriveExperiment.Record record in AngularRecords)record.Root.CreatedAt-=10.1f;
            AngularDriveExperiment.BeforeExternalStep();
            Check(AngularRecords.Count==0,"angular records expire without prolonging activity");
        }
        finally {foreach(var c in bounded)c.Dispose();}
        Measurements.Add("angular: production capture, joint snapshot, retained-core mapping, backend API loader/batch methods and real D6 bridge; owner dispatch is a synchronous fixture adapter, live solver cost and appearance remain unverified");
    }
}
#endif
