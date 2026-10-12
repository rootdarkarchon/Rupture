#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.BloodStreams;

public static class ClothingSoakGpuTests
{
    public static float MaximumError;
    public static int Checks;
    public static void RunBatch()
    {
        Rupture.WoundResources.Bundle = AssetBundle.LoadFromFile(Environment.GetEnvironmentVariable("RPT_SOAK_BUNDLE"));
        if(Rupture.WoundResources.Bundle == null) throw new Exception("Built Rupture bundle unavailable");
        var shader = Rupture.WoundResources.GetComputeShader("ClothingSoak");
        if (!SystemInfo.supportsComputeShaders || shader == null) throw new Exception("GPU soaking compute unavailable");
        // A seam, a disconnected layer and mixed bone weights. Rotate the pose
        // and gravity throughout, including paused steps and independent fields.
        var rest = new[]{Vector3.zero,Vector3.down*.02f,Vector3.up*.02f,Vector3.right*.02f,
            Vector3.zero,Vector3.left*.02f,new Vector3(0,0,.001f),new Vector3(0,.02f,.001f),new Vector3(.02f,0,.001f)};
        var normals = new Vector3[rest.Length];var weights = new BoneWeight[rest.Length];
        for(int i=0;i<rest.Length;i++){normals[i]=Vector3.forward;weights[i]=new BoneWeight{boneIndex0=0,weight0=.7f,boneIndex1=1,weight1=.3f};}
        var topology = new ClothingSoakTopology(rest,normals,new[]{0,1,3,0,3,2,4,5,2,6,7,8},new int[rest.Length]);
        Compare(shader,topology,weights,2,180);
        const int side=63;var p=new Vector3[side*side];var n=new Vector3[p.Length];var bw=new BoneWeight[p.Length];var triangles=new int[(side-1)*(side-1)*6];int k=0;
        for(int y=0;y<side;y++)for(int x=0;x<side;x++)
        {int i=y*side+x;p[i]=new Vector3(x*.01f,y*.01f,0);n[i]=Vector3.forward;bw[i]=new BoneWeight{boneIndex0=0,weight0=1};if(x==side-1||y==side-1)continue;triangles[k++]=i;triangles[k++]=i+1;triangles[k++]=i+side;triangles[k++]=i+1;triangles[k++]=i+side+1;triangles[k++]=i+side;}
        Compare(shader,new ClothingSoakTopology(p,n,triangles,new int[p.Length]),bw,1,600);
        CompareInterleaved(shader);
        CompareBatched(shader);
        string directory=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"SoakVerification");Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"gpu-field.json"),"{\"passed\":true,\"checks\":"+Checks+",\"maximumSaturationError\":"+MaximumError.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"}");
        Debug.Log("GPU field matches CPU: "+Checks+" values; maximum error "+MaximumError);
    }
    private static void CompareInterleaved(ComputeShader shader)
    {
        int shadersBefore = Resources.FindObjectsOfTypeAll<ComputeShader>().Length;
        var restA = new[] { Vector3.zero, Vector3.down * .02f, Vector3.right * .02f };
        var restB = new[] { Vector3.zero, Vector3.up * .02f, Vector3.right * .02f, Vector3.right * .02f + Vector3.up * .02f };
        ClothingSoakTopology Topology(Vector3[] p, int[] triangles) { var n = new Vector3[p.Length]; for (int i=0;i<n.Length;i++) n[i]=Vector3.forward; return new ClothingSoakTopology(p,n,triangles,new int[p.Length]); }
        BoneWeight[] Weights(int count) { var w=new BoneWeight[count];for(int i=0;i<count;i++)w[i]=new BoneWeight{boneIndex0=0,weight0=1};return w; }
        var topologyA=Topology(restA,new[]{0,1,2});var topologyB=Topology(restB,new[]{0,1,2,1,3,2});
        var cpuA=new ClothingSoakField(topologyA);var cpuB=new ClothingSoakField(topologyB);cpuA.Seed(0);cpuB.Seed(0);
        using(var a=new ClothingSoakGpuGeometry(shader,topologyA,Weights(3),1))
        using(var b=new ClothingSoakGpuGeometry(shader,topologyB,Weights(4),1))
        using(var fieldA=new ClothingSoakGpuField(a,cpuA.Saturation))
        using(var fieldB=new ClothingSoakGpuField(b,cpuB.Saturation))
        using(var commands=new CommandBuffer())
        {
            var matrices=new[]{Matrix4x4.identity};var valuesA=new float[3];var valuesB=new float[4];
            for(int tick=0;tick<60;tick++)
            {
                cpuA.Step(restA,Vector3.down,.1f,3);cpuB.Step(restB,Vector3.left,.1f,1);
                commands.Clear();a.RecordSkin(commands,matrices,(uint)tick+1);b.RecordSkin(commands,matrices,(uint)tick+1);
                fieldA.RecordStep(commands,Vector3.down,.1f,3);fieldB.RecordStep(commands,Vector3.left,.1f,1);
                Graphics.ExecuteCommandBuffer(commands);fieldA.CommitStep();fieldB.CommitStep();
                fieldA.Current.GetData(valuesA);fieldB.Current.GetData(valuesB);
                for(int i=0;i<valuesA.Length;i++){Checks++;if(Math.Abs(valuesA[i]-cpuA.Saturation[i])>.0001)throw new Exception("interleaved garment A bindings changed");}
                for(int i=0;i<valuesB.Length;i++){Checks++;if(Math.Abs(valuesB[i]-cpuB.Saturation[i])>.0001)throw new Exception("interleaved garment B bindings changed");}
            }
        }
        Checks++;if(Resources.FindObjectsOfTypeAll<ComputeShader>().Length!=shadersBefore)throw new Exception("owned compute bindings leaked after disposal");
    }
    private static void CompareBatched(ComputeShader shader)
    {
        var rest=new[]{Vector3.zero,Vector3.down*.006f,Vector3.right*.006f,Vector3.down*.012f};
        var normals=new[]{Vector3.forward,Vector3.forward,Vector3.forward,Vector3.forward};
        var weights=new BoneWeight[4];for(int i=0;i<4;i++)weights[i]=new BoneWeight{boneIndex0=0,weight0=1};
        var topology=new ClothingSoakTopology(rest,normals,new[]{0,1,2,1,3,2},new int[4]);
        var cpu=new ClothingSoakField(topology);cpu.Seed(0);
        using(var geometry=new ClothingSoakGpuGeometry(shader,topology,weights,1))
        using(var field=new ClothingSoakGpuField(geometry,cpu.Saturation))
        using(var target=new ClothingSoakGpuTarget(field,null,4))
        using(var commands=new CommandBuffer())
        {
            var matrices=new[]{Matrix4x4.identity};var values=new float[4];var history=new float[4];
            // Even/odd ping-pong batches, changed gravity, seam/front propagation,
            // and the per-target paint history must agree with small CPU steps.
            for(int tick=0;tick<30;tick++)
            {
                int steps=tick%3==0?8:tick%3==1?5:2;var gravity=tick<15?Vector3.down:Vector3.right;
                for(int i=0;i<steps;i++)cpu.Step(rest,gravity,.1f,3);
                commands.Clear();geometry.RecordSkin(commands,matrices,(uint)tick+1);
                field.RecordSteps(commands,gravity,steps*.1f,3,steps);target.RecordCommit(commands);
                Graphics.ExecuteCommandBuffer(commands);field.CommitStep();
                field.Current.GetData(values);target.Previous.GetData(history);
                for(int i=0;i<4;i++){Checks++;if(Math.Abs(values[i]-cpu.Saturation[i])>.0001f||history[i]!=values[i])throw new Exception("batched transport/history differs at "+tick);}
            }
        }
    }
    private static void Compare(ComputeShader shader,ClothingSoakTopology topology,BoneWeight[] weights,int boneCount,int ticks)
    {
        var cpu=new ClothingSoakField(topology);cpu.Seed(topology.Rest.Length/2);
        var posed=new Vector3[weights.Length];var values=new float[weights.Length];var matrices=new Matrix4x4[boneCount];
        using(var geometry=new ClothingSoakGpuGeometry(shader,topology,weights,boneCount))
        using(var gpu=new ClothingSoakGpuField(geometry,cpu.Saturation))
        using(var commands=new CommandBuffer())
        {
            for(int tick=0;tick<ticks;tick++)
            {
                for(int j=0;j<boneCount;j++)matrices[j]=Matrix4x4.TRS(new Vector3(j*.01f,0,0),Quaternion.Euler(tick*.1f,j*17,tick*.7f),Vector3.one);
                for(int i=0;i<posed.Length;i++)
                {var w=weights[i];var p=topology.Rest[i];posed[i]=matrices[w.boneIndex0].MultiplyPoint3x4(p)*w.weight0+matrices[w.boneIndex1].MultiplyPoint3x4(p)*w.weight1+matrices[w.boneIndex2].MultiplyPoint3x4(p)*w.weight2+matrices[w.boneIndex3].MultiplyPoint3x4(p)*w.weight3;}
                Vector3 gravity=tick%100<70?Vector3.down:Vector3.right;float severity=tick%41==0?0:tick%3==0?3:1;
                cpu.Step(posed,gravity,.1f,severity);
                commands.Clear();geometry.RecordSkin(commands,matrices,(uint)tick+1);gpu.RecordStep(commands,gravity,.1f,severity);Graphics.ExecuteCommandBuffer(commands);gpu.CommitStep();
                gpu.Current.GetData(values); // Test-only synchronization; runtime has no readback.
                for(int i=0;i<values.Length;i++)
                {float error=Math.Abs(cpu.Saturation[i]-values[i]);MaximumError=Math.Max(MaximumError,error);Checks++;if(error>.0001f)throw new Exception("GPU saturation mismatch tick="+tick+" vertex="+i+" CPU="+cpu.Saturation[i]+" GPU="+values[i]);}
            }
        }
    }
}
#endif
