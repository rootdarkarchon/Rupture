#if UNITY_EDITOR
using System;
using UnityEngine;
using Comfort.Common;
using Systems.Effects;
using Rupture.Ragdolls.Classes;
namespace Rupture.Dismemberment.Classes.BloodStreams
{
internal static partial class PulsatingBloodStreamSystem
{
    internal static int VerifyReferenceParcels()
    {
        int checks = 0;
        Action<bool,string> check = (ok,message) => { if (!ok) throw new Exception(message); checks++; };
        var root = new GameObject("reference lifetime fixture");
        var effects = Singleton<Effects>.Instance = new Effects();
        int mask = EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK;
        try
        {
            Array.Clear(DripImpacts,0,DripImpacts.Length); Array.Clear(DecalCells,0,DecalCells.Length);
            _decalSecondStarted = 0; _decalsThisSecond = 0;
            root.transform.position = new Vector3(0,1.4f,0);
            var e = Emitters[0] = new StreamEmitter { Active=true,Visible=true,Anchor=root.transform,
                Descriptor=new BloodStreamData { TriggerKind=BloodStreamTriggerKind.HeavyWound,Seed=47,PeakSpeed=1.5f,Radius=.0052f,LocalDirection=Vector3.right } };
            TriggerHeartbeat(e,0); ShadowRagdollWorld.ResetWork(); UpdateStreamFlow(0,e,0);
            var first=GetStreamSample(0,e,0); var arrival=DripImpacts[0];
            check(e.BaseFlow.HasImpact && arrival.Active,"reference captures a real floor hit and queues its arrival");
            check(Mathf.Abs(e.BaseFlow.NextTraceAt-.055f)<.00001f,"pressure retains 55 ms reference interval");
            check(Mathf.Abs(first.EndAt-arrival.ArrivesAt)<.00001f,"traced parcel and decal share arrival time");
            // Large translation, rotation and another heartbeat all occur before
            // the timer. They affect launch state, never trigger extra prediction.
            root.transform.position=new Vector3(5,2.5f,0); root.transform.rotation=Quaternion.Euler(0,110,0);
            TriggerHeartbeat(e,.02f); UpdateStreamFlow(0,e,.02f);
            var second=GetStreamSample(0,e,1);
            check(ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions)==1,"movement/pulse did not force prediction");
            check((second.Origin-first.Origin).sqrMagnitude>1,"new parcel follows current moving wound");
            check(Mathf.Abs((second.EndAt-second.StartedAt)-(first.EndAt-first.StartedAt))<.00001f,"copies duration rather than remaining lifetime");
            check(GetStreamSample(0,e,0).EndAt==first.EndAt,"old parcel expiry is immutable");
            UpdateStreamFlow(0,e,.06f);
            check(ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions)==2,"timer refreshes moving reference");
            check(GetStreamSample(0,e,0).EndAt==first.EndAt,"new reference never retimes old parcel");
            EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK=0;
            UpdateStreamFlow(0,e,.13f); var missed=GetStreamSample(0,e,e.SampleCount-1);
            check(!e.BaseFlow.HasImpact && Mathf.Abs(missed.EndAt-missed.StartedAt-MaximumFlightSeconds)<.00001f,"miss replaces old impact with finite maximum lifetime");
            UpdateStreamFlow(0,e,.16f); var reusedMiss=GetStreamSample(0,e,e.SampleCount-1);
            check(Mathf.Abs(reusedMiss.EndAt-reusedMiss.StartedAt-MaximumFlightSeconds)<.00001f,"untraced parcel also expires after reference miss");
            EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK=mask;
            // Keep just the original queued record, then retire/move the emitter.
            Array.Clear(DripImpacts,0,DripImpacts.Length); DripImpacts[0]=arrival; e.Active=false;
            _decalFrame=-1; UpdateDecals(arrival.ArrivesAt-.001f);
            check(effects.Points.Count==0,"no painting before arrival");
            _decalFrame=-1; UpdateDecals(arrival.ArrivesAt+.001f);
            check(effects.Points.Count>0 && (effects.Points[0]-arrival.Point).sqrMagnitude<.000001f,"stopped/moved wound paints original arriving parcel");
            int painted=effects.Points.Count; _decalFrame=-1; UpdateDecals(arrival.ArrivesAt+.1f);
            check(effects.Points.Count==painted,"arrival paints only once");
            PruneStreamSamples(0,3f); check(e.SampleCount==0,"all parcels disappear including missed impacts");
            // Saturate the impact queue: collision sampling must still continue.
            for(int i=0;i<DripImpacts.Length;i++)DripImpacts[i]=new DripImpact{Active=true,ArrivesAt=100};
            e.Active=true;e.BaseFlow.NextTraceAt=0;e.BaseFlow.NextDecalAt=0;e.NextSampleAt=0;
            long before=ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions);
            UpdateStreamFlow(0,e,1f);
            check(ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodImpactPredictions)==before+1 && e.BaseFlow.HasImpact,"painting queue cannot suppress lifetime reference");
            check(ShadowRagdollWorld.Work(ShadowRagdollWorld.MainThreadWork.BloodImpactQueueFull)>0,"bounded impact queue reports overflow");
            check(Mathf.Abs(e.BaseFlow.NextTraceAt-1.12f)<.00001f,"between pulses retains 120 ms interval");
        }
        finally { EFTHardSettings.Instance.ENVIRONMENT_HIT_MASK=mask; Singleton<Effects>.Instance=null;Array.Clear(DripImpacts,0,DripImpacts.Length);UnityEngine.Object.DestroyImmediate(root); }
        return checks;
    }
}
}
#endif
