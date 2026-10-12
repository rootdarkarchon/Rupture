#if UNITY_EDITOR
using System;
using Rupture.PhysXBackend;

internal static class PhysicsIdleRuntimeTests
{
    internal static int Run()
    {
        int checks=0;
        void Check(bool value,string label) { if(!value)throw new Exception(label);checks++; }
        var activity=new SimulationActivity();
        Check(!activity.NeedsStep,"empty scene is idle");
        activity.Changed();long submitted=activity.Version;
        Check(activity.NeedsStep,"force, shape or environment command wakes idle scene");
        activity.Changed();activity.Complete(submitted,false);
        Check(activity.NeedsStep,"command queued behind the completed step is not lost");
        activity.Complete(activity.Version,true);
        Check(activity.NeedsStep,"awake native bodies keep stepping");
        activity.Complete(activity.Version,false);
        Check(!activity.NeedsStep,"last sleeping result ends recurring steps");
        activity.Changed();activity.Reset();Check(!activity.NeedsStep,"raid reset clears prior commands and activity");

        var cadence=new EnvironmentPollCadence();
        Check(cadence.TryBegin(0)&&!cadence.TryBegin(.249f)&&cadence.TryBegin(.25f),"movable roots poll initially and at 250 ms intervals");
        Check(cadence.TryBegin(5)&&!cadence.TryBegin(5)&&!cadence.TryBegin(5.249f),"long frames never replay overdue polls");
        cadence=default;
        int polls=0;for(int frame=0;frame<600;frame++)if(cadence.TryBegin(frame/60f))polls++;
        Check(polls==40,"600 frames at 60 Hz produce 40 environment polls");
        cadence=default;Check(cadence.TryBegin(0),"raid reset allows the first poll immediately");
        var root=new UnityEngine.GameObject("idle environment mover");
        try
        {
            var probe=new EnvironmentTransformProbe();probe.Changed(root.transform);
            root.transform.position=UnityEngine.Vector3.one;
            Check(!cadence.TryBegin(.1f),"movement waits until the next requested poll");
            if(cadence.TryBegin(.25f)&&probe.Changed(root.transform))activity.Changed();
            Check(activity.NeedsStep,"detected mover motion still wakes a sleeping scene");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }

        var publication=new BodyStatePublication();
        var states=new NativeApi.BodyState[212];var originals=new NativeApi.BodyState[212];
        for(int i=0;i<states.Length;i++)originals[i]=new NativeApi.BodyState{Actor=(ulong)i+1,Sleeping=1,
            Pose=new NativeApi.Pose{Position=new NativeApi.Vec3{X=i},Rotation=new NativeApi.Quat{W=1}}};
        Array.Copy(originals,states,states.Length);
        Check(publication.Filter(states,out bool awake)==212&&!awake,"first final sleeping poses are delivered");
        Array.Copy(originals,states,states.Length);
        Check(publication.Filter(states,out awake)==0&&!awake,"212 unchanged sleepers produce no main-thread publications");
        originals[15].Sleeping=0;originals[15].Pose.Position.Y=2;
        Array.Copy(originals,states,states.Length);
        Check(publication.Filter(states,out awake)==1&&awake&&states[0].Actor==16,"one externally woken body is published");
        originals[15].Sleeping=1;Array.Copy(originals,states,states.Length);
        Check(publication.Filter(states,out awake)==1&&!awake,"woken body publishes its final sleep transition");
        originals[42].Pose.Position.X+=1;Array.Copy(originals,states,states.Length);
        Check(publication.Filter(states,out awake)==1&&states[0].Actor==43,"sleeping kinematic pose changes still publish");
        publication.Remove(43);Array.Copy(originals,states,states.Length);
        Check(publication.Filter(states,out awake)==1,"destroyed actor generation leaves no stale suppression");
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){Array.Copy(originals,states,states.Length);publication.Filter(states,out awake);}
        allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
        Check(allocated==0,"steady sleeping publication filter allocates nothing");
        UnityEngine.Debug.Log("PASS: idle scene and changed-body publication ("+checks+" checks)");
        return checks;
    }
}
#endif
