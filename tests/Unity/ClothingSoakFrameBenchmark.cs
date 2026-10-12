#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using EFT;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.Packets;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

// Real editor frames allow GPU profiler queries to retire without a per-frame
// readback. This isolated fixture is not an in-raid GPU/FPS benchmark.
public static class ClothingSoakFrameBenchmark
{
    [Serializable] private sealed class Result
    {
        public string backend; public int wounds, measuredFrames, gpuSamples, distinctProfilerFrames;
        public double meanCpuMs, p99CpuMs, meanGpuMsPerFrame;
        public long allocatedBytes, fields, paintTargets, commandExecutions; public bool gpuTimingAvailable;
    }
    [Serializable] private sealed class Report
    {
        public bool passed; public string gpu = SystemInfo.graphicsDeviceName;
        public string limitations = "3969 vertices and three 128x128 garment textures per wound at simulated 60 Hz. Current fields paint at 2 Hz; the frozen baseline uses 10 Hz. CPU timing includes profiler overhead. GPU time is -1 when distinct profiler frames/samples are unavailable. Initialization and final texture readback excluded. Final PNGs show a stationary fixture after 7.5 simulated seconds following initialization; they are not live garment screenshots.";
        public Result[] cases;
    }
    private delegate void SupplyDelegate(BloodStreamData data, Player player, Transform anchor, float now);
    private static Action _begin, _reset; private static Action<float> _update; private static SupplyDelegate _supply;
    private static readonly List<Result> Results = new List<Result>();
    private static readonly List<GameObject> Roots = new List<GameObject>();
    private static readonly List<Mesh> Meshes = new List<Mesh>();
    private static Player[] _players; private static BloodStreamData[] _descriptors; private static Transform[] _bones;
    private static Material _material; private static Camera _camera; private static Recorder _recorder;
    private static int _case, _frame; private static float _now; private static double _deadline;
    private static readonly Stopwatch Timer = new Stopwatch(); private static readonly double[] Cpu = new double[360];
    private static long _allocated, _gpuTime; private static int _gpuSamples, _lastProfilerFrame, _profilerFrames;
    private static bool _baseline;
    private static Renderer _firstRenderer;
    public static void RunBatch()
    {
        try
        {
            string game=Environment.GetEnvironmentVariable("RPT_SOAK_GAME_ROOT");
            Rupture.WoundResources.Bundle=AssetBundle.LoadFromFile(Environment.GetEnvironmentVariable("RPT_SOAK_BUNDLE"));
            var shaders=AssetBundle.LoadFromFile(Path.Combine(game,"EscapeFromTarkov_Data/StreamingAssets/Windows/shaders"));
            foreach(var shader in shaders.LoadAllAssets<Shader>())if(shader.name=="Decal/Merge")TextureDecalsPainter.Compositor=shader;
            AssetBundle.LoadFromFile(Path.Combine(game,"EscapeFromTarkov_Data/StreamingAssets/Windows/assets/systems/effects/particlesystems/effects.bundle")).LoadAllAssets();
            Texture2D blood=null;foreach(var t in Resources.FindObjectsOfTypeAll<Texture2D>())if(t.name=="blood1")blood=t;
            if(blood==null||TextureDecalsPainter.Compositor==null)throw new Exception("Native resources missing");
            Comfort.Common.Singleton<Systems.Effects.Effects>.Instance=new Systems.Effects.Effects();
            Comfort.Common.Singleton<Systems.Effects.Effects>.Instance.TexDecals.Blood=blood;
            _material=new Material(Shader.Find("Hidden/Rupture_Test_Decal"));
            _camera=new GameObject("GPU query frame camera").AddComponent<Camera>();_camera.enabled=false;_camera.cullingMask=0;
            _camera.targetTexture=new RenderTexture(16,16,0);_camera.targetTexture.Create();
            UnityEditorInternal.ProfilerDriver.profileEditor=true;
            Profiler.enabled=true;Profiler.SetAreaEnabled(ProfilerArea.GPU,true);
            Setup();EditorApplication.update+=Tick;
        }
        catch(Exception e){UnityEngine.Debug.LogException(e);EditorApplication.Exit(1);}
    }
    private static void Setup()
    {
        _baseline=_case>=3;
        Type type=_baseline?typeof(ClothingSoakingSystem).Assembly.GetType("Rupture.Dismemberment.Classes.BloodStreams.BaselineClothingSoakingSystem"):typeof(ClothingSoakingSystem);
        if(type==null){Finish();return;}
        const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
        _begin=(Action)Delegate.CreateDelegate(typeof(Action),type.GetMethod("BeginFrame",flags));
        _reset=(Action)Delegate.CreateDelegate(typeof(Action),type.GetMethod("Reset",flags));
        _update=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),type.GetMethod("Update",flags));
        _supply=(SupplyDelegate)Delegate.CreateDelegate(typeof(SupplyDelegate),type.GetMethod("Supply",flags));
        int count=new[]{1,8,24}[_case%3];_players=new Player[count];_descriptors=new BloodStreamData[count];_bones=new Transform[count];
        var skin=typeof(ClothingSoakRuntimeTests).GetMethod("Skin",BindingFlags.Static|BindingFlags.NonPublic);
        for(int i=0;i<count;i++)
        {
            var root=new GameObject("frame fixture "+i);Roots.Add(root);root.transform.position=Vector3.right*i*2;
            var bone=new GameObject("arm");bone.transform.SetParent(root.transform,false);_bones[i]=bone.transform;
            var p=new Player();_players[i]=p;var body=new Garment();var hands=new Garment();p.PlayerBody.BodySkins.Add(EBodyModelPart.Body,body);p.PlayerBody.BodySkins.Add(EBodyModelPart.Hands,hands);
            for(int j=0;j<3;j++)
            {
                var renderer=(Renderer)skin.Invoke(null,new object[]{root.transform,bone.transform,_material,j==1,Meshes});
                (j==2?hands:body).Renderers.Add(renderer);if(i==0&&j==0)_firstRenderer=renderer;
            }
            _descriptors[i]=new BloodStreamData{StreamId=(uint)(i+1),BodyPart=EBodyPart.LeftArm,LocalPoint=new Vector3(.31f,.31f,0),TriggerKind=BloodStreamTriggerKind.HeavyWound};
        }
        _frame=-1;_now=Time.realtimeSinceStartup;_deadline=EditorApplication.timeSinceStartup+30;_allocated=0;_gpuTime=0;_gpuSamples=0;
        _recorder=null;_profilerFrames=0;_lastProfilerFrame=-1;
    }
    private static void Tick()
    {
        try
        {
            if(_frame<0)
            {
                if(EditorApplication.timeSinceStartup>_deadline)throw new Exception("Fixture initialization timed out");
                _now+=1f/60;Step();
                if(LimbDecalTextureTransfer.Textures.Count==_players.Length*3)
                {_frame=0;_recorder=Recorder.Get(_baseline?"RPT.GPU.ClothingSoak.Baseline":"RPT.GPU.ClothingSoak");_recorder.enabled=true;}
            }
            else
            {
                // Three-frame query delay is inside the 90-frame warm-up.
                if(_frame==90)Rupture.Ragdolls.Classes.ShadowRagdollWorld.ResetWork();
                if(_frame>=90&&_frame<450)
                {
                    int profilerFrame=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                    if(profilerFrame!=_lastProfilerFrame)
                    {_lastProfilerFrame=profilerFrame;_profilerFrames++;_gpuTime+=_recorder.gpuElapsedNanoseconds;_gpuSamples+=_recorder.gpuSampleBlockCount;}
                    long before=GC.GetAllocatedBytesForCurrentThread();Timer.Restart();Step();Timer.Stop();
                    _allocated+=GC.GetAllocatedBytesForCurrentThread()-before;Cpu[_frame-90]=Timer.Elapsed.TotalMilliseconds;
                }
                else Step();
                _frame++;_now+=1f/60;
                if(_frame==450)
                {
                    double total=0;foreach(double ms in Cpu)total+=ms;Array.Sort(Cpu);
                    bool gpuAvailable=_gpuSamples>0&&_profilerFrames>=Cpu.Length*.95;
                    Results.Add(new Result{backend=_baseline?"Frozen baseline":"GPU resident, 2 Hz painting",wounds=_players.Length,measuredFrames=Cpu.Length,
                        meanCpuMs=total/Cpu.Length,p99CpuMs=Cpu[(int)(Cpu.Length*.99)],allocatedBytes=_allocated,gpuSamples=_gpuSamples,
                        fields=Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakFields),
                        paintTargets=Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakPaintTargets),
                        commandExecutions=Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakCommandExecutions),
                        distinctProfilerFrames=_profilerFrames,gpuTimingAvailable=gpuAvailable,meanGpuMsPerFrame=gpuAvailable?_gpuTime/1000000.0/_profilerFrames:-1});
                    var previous=RenderTexture.active;var texture=LimbDecalTextureTransfer.Textures[_firstRenderer];
                    var pixels=new Texture2D(texture.width,texture.height,TextureFormat.RGBA32,false);
                    RenderTexture.active=texture;pixels.ReadPixels(new Rect(0,0,texture.width,texture.height),0,0);pixels.Apply();RenderTexture.active=previous;
                    File.WriteAllBytes(Path.Combine(Directory.GetParent(Application.dataPath).FullName,"SoakVerification/frame-"+(_baseline?"baseline-":"coarse-")+_players.Length+".png"),pixels.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(pixels);
                    _recorder.enabled=false;_reset();
                    foreach(var rt in LimbDecalTextureTransfer.Textures.Values){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}LimbDecalTextureTransfer.Textures.Clear();
                    foreach(var root in Roots)UnityEngine.Object.DestroyImmediate(root);Roots.Clear();foreach(var mesh in Meshes)UnityEngine.Object.DestroyImmediate(mesh);Meshes.Clear();
                    if(++_case==6){Finish();return;}Setup();
                }
            }
            _camera.Render();EditorApplication.QueuePlayerLoopUpdate();
        }
        catch(Exception e){EditorApplication.update-=Tick;UnityEngine.Debug.LogException(e);EditorApplication.Exit(1);}
    }
    private static void Step()
    {
        _begin();for(int i=0;i<_players.Length;i++)_supply(_descriptors[i],_players[i],_bones[i],_now);_update(_now);
    }
    private static void Finish()
    {
        EditorApplication.update-=Tick;
        foreach(var result in Results)if(result.allocatedBytes!=0)throw new Exception("Steady allocation regression");
        string output=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"SoakVerification/frame-benchmark.json");
        File.WriteAllText(output,JsonUtility.ToJson(new Report{passed=true,cases=Results.ToArray()},true));EditorApplication.Exit(0);
    }
}
#endif
