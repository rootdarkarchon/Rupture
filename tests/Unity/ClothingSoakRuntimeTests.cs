#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using EFT;
using Rupture.Dismemberment.Classes.BloodStreams;
using Rupture.Dismemberment.Classes.Limbs;
using Rupture.Dismemberment.Classes.Packets;
using UnityEngine;
using UnityEngine.Rendering;

public static class ClothingSoakRuntimeTests
{
    [Serializable] private sealed class Report { public bool passed;public int checks;public Sample[] samples;public string colorSpace = QualitySettings.activeColorSpace.ToString();public string limitations; public bool nativeCompositorReference, twoPassPainting, gpuResident, builtBundle, destinationRecovery; public bool sharedWoundsMatchImmediate; public int initialPixels, pixelsAfter30Seconds; public float downhillCentimetres; public ClothingSoakAppearanceTests.Report appearance; }
    [Serializable] private sealed class Sample { public int wounds;public double initializationMs,meanFrameCpuMs,p99FrameCpuMs,gpuReadbackWaitMs;public long allocatedBytes,textureBytes; }
    private static int _checks;
    private static bool _commandComparisonOnly;
    private static bool _sharedComparisonOnly;
    public static void RunCommandComparisonBatch() { _commandComparisonOnly = true; RunBatch(); }
    public static void RunSharedComparisonBatch() { _sharedComparisonOnly = true; RunBatch(); }
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);_checks++;}
    public static void RunBatch()
    {
        string output=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"SoakVerification");Directory.CreateDirectory(output);
        string gameRoot=Environment.GetEnvironmentVariable("RPT_SOAK_GAME_ROOT");
        if(string.IsNullOrEmpty(gameRoot))throw new Exception("RPT_SOAK_GAME_ROOT must identify the matched EFT installation");
        Rupture.WoundResources.Bundle=AssetBundle.LoadFromFile(Environment.GetEnvironmentVariable("RPT_SOAK_BUNDLE"));
        Check(Rupture.WoundResources.Bundle!=null,"built Rupture bundle loaded");
        string bundleRoot=Path.Combine(gameRoot,"EscapeFromTarkov_Data/StreamingAssets/Windows");
        var shaderBundle=AssetBundle.LoadFromFile(Path.Combine(bundleRoot,"shaders"));
        Shader nativeProjection=null;
        foreach(var shader in shaderBundle.LoadAllAssets<Shader>())
        {
            if(shader.name=="Decal/Merge")TextureDecalsPainter.Compositor=shader;
            if(shader.name=="Decal/TexDecals")nativeProjection=shader;
        }
        Check(TextureDecalsPainter.Compositor!=null&&TextureDecalsPainter.Compositor.isSupported,"actual EFT compositor loaded");
        var effectsBundle=AssetBundle.LoadFromFile(Path.Combine(bundleRoot,"assets/systems/effects/particlesystems/effects.bundle"));
        // Blood is a referenced prefab subasset, not a top-level bundle asset.
        effectsBundle.LoadAllAssets();
        Texture2D blood=null;foreach(var texture in Resources.FindObjectsOfTypeAll<Texture2D>())if(texture.name=="blood1")blood=texture;
        Check(blood!=null,"actual EFT blood texture loaded");
        if (_sharedComparisonOnly)
        {
            Comfort.Common.Singleton<Systems.Effects.Effects>.Instance=new Systems.Effects.Effects();
            Comfort.Common.Singleton<Systems.Effects.Effects>.Instance.TexDecals.Blood=blood;
            var sharedMaterial=new Material(Shader.Find("Hidden/Rupture_Test_Decal"));
            try
            {
                VerifySharedWounds(sharedMaterial,blood);
                File.WriteAllText(Path.Combine(output,"shared-comparison-"+QualitySettings.activeColorSpace+".json"),
                    "{\"passed\":true,\"colorSpace\":\""+QualitySettings.activeColorSpace+"\",\"comparison\":\"shared wounds, partial uploads and two-pass composition vs dense immediate native merge; 100 steps; maximum 3-byte RGBA rounding error\"}");
            }
            finally {UnityEngine.Object.DestroyImmediate(sharedMaterial);effectsBundle.Unload(true);shaderBundle.Unload(false);}
            return;
        }
        if (_commandComparisonOnly)
        {
            var comparison = ClothingSoakAppearanceTests.Run(blood, TextureDecalsPainter.Compositor, nativeProjection, output, true);
            File.WriteAllText(Path.Combine(output,"command-comparison-" + QualitySettings.activeColorSpace + ".json"),
                "{\"passed\":" + comparison.commandBufferMatchesImmediate.ToString().ToLowerInvariant() + ",\"colorSpace\":\"" + QualitySettings.activeColorSpace + "\",\"comparison\":\"native merge vs two-pass composition; 40 steps; maximum 3-byte RGBA rounding error\"}");
            return;
        }
        var appearance=ClothingSoakAppearanceTests.Run(blood,TextureDecalsPainter.Compositor,nativeProjection,output);
        int initialPixels=0,finalPixels=0;float downhill=0;
        Comfort.Common.Singleton<Systems.Effects.Effects>.Instance=new Systems.Effects.Effects();
        Comfort.Common.Singleton<Systems.Effects.Effects>.Instance.TexDecals.Blood=blood;
        var material=new Material(Shader.Find("Hidden/Rupture_Test_Decal"));
        var samples=new List<Sample>();
        foreach(int count in new[]{1,8,24})
        {
            var roots=new List<GameObject>();var meshes=new List<Mesh>();var players=new Player[count];var descriptors=new BloodStreamData[count];var anchors=new Transform[count];
            SkinnedMeshRenderer first=null,lod=null,sleeve=null;
            for(int i=0;i<count;i++)
            {
                var root=new GameObject("fixture "+i);roots.Add(root);root.transform.position=Vector3.right*(i*2);
                var bone=new GameObject("arm");bone.transform.SetParent(root.transform,false);anchors[i]=bone.transform;
                var p=new Player();players[i]=p;var body=new Garment();var hands=new Garment();p.PlayerBody.BodySkins.Add(EBodyModelPart.Body,body);p.PlayerBody.BodySkins.Add(EBodyModelPart.Hands,hands);
                var a=Skin(root.transform,bone.transform,material,false,meshes);var b=Skin(root.transform,bone.transform,material,true,meshes);body.Renderers.Add(a);body.Renderers.Add(b);
                var handsBone=new GameObject("arm");handsBone.transform.SetParent(root.transform,false);handsBone.transform.localPosition=Vector3.right*5;
                var c=Skin(root.transform,handsBone.transform,material,false,meshes);hands.Renderers.Add(c);
                if(i==0){first=a;lod=b;sleeve=c;}
                descriptors[i]=new BloodStreamData{StreamId=(uint)(i+1),BodyPart=EBodyPart.LeftArm,LocalPoint=new Vector3(.31f,.31f,0),TriggerKind=BloodStreamTriggerKind.LightWound};
            }
            try
            {
                var watch=Stopwatch.StartNew();float now=Time.realtimeSinceStartup;
                while(LimbDecalTextureTransfer.Textures.Count<count*3&&watch.Elapsed.TotalSeconds<15)
                {ClothingSoakingSystem.BeginFrame();for(int i=0;i<count;i++)ClothingSoakingSystem.Supply(descriptors[i],players[i],anchors[i]);ClothingSoakingSystem.Update(Time.realtimeSinceStartup);System.Threading.Thread.Sleep(1);}
                watch.Stop();double init=watch.Elapsed.TotalMilliseconds;
                Check(LimbDecalTextureTransfer.Textures.Count==count*3,"all LODs and first-person sleeve textures registered");
                if(count==1)
                {
                    var progressPixels=new Texture2D(128,128,TextureFormat.RGBA32,false);
                    Color32[] initial=Read(first,progressPixels);initialPixels=Coverage(initial);
                    Check(initialPixels>=8,"initial wound has a visible continuous core with native blood texture");
                    File.WriteAllBytes(Path.Combine(output,"runtime-start.png"),progressPixels.EncodeToPNG());
                    float progressNow=Time.realtimeSinceStartup;
                    for(int tick=0;tick<300;tick++)
                    {
                        progressNow+=.101f;ClothingSoakingSystem.BeginFrame();
                        ClothingSoakingSystem.Supply(descriptors[0],players[0],anchors[0],progressNow);
                        ClothingSoakingSystem.Update(progressNow);
                    }
                    Color32[] grown=Read(first,progressPixels);finalPixels=Coverage(grown);
                    float ymin=1,ymax=0;
                    for(int pixel=0;pixel<grown.Length;pixel++)if(grown[pixel].r>=3&&grown[pixel].a>=16){float y=(pixel/128+.5f)/128*.62f;ymin=Mathf.Min(ymin,y);ymax=Mathf.Max(ymax,y);}
                    downhill=(.31f-ymin)*100;
                    Check(finalPixels>initialPixels*4,"native visible stain area grows materially over 30 seconds");
                    Check(downhill>=10&&.31f-ymin>(ymax-.31f)*2,"visible front moves downhill at least 10 cm and favours gravity");
                    File.WriteAllBytes(Path.Combine(output,"runtime-30-seconds.png"),progressPixels.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(progressPixels);
                    now=progressNow;
                }
                Rupture.Ragdolls.Classes.ShadowRagdollWorld.ResetWork();
                var timings=new double[120];watch.Reset();long startBytes=GC.GetAllocatedBytesForCurrentThread();
                now=Mathf.Max(now,Time.realtimeSinceStartup);
                for(int frame=0;frame<timings.Length;frame++)
                {
                    float frameNow=now+(count+3+frame)*.0167f;
                    watch.Restart();ClothingSoakingSystem.BeginFrame();for(int i=0;i<count;i++)ClothingSoakingSystem.Supply(descriptors[i],players[i],anchors[i],frameNow);
                    ClothingSoakingSystem.Update(frameNow);watch.Stop();timings[frame]=watch.Elapsed.TotalMilliseconds;
                }
                long allocation=GC.GetAllocatedBytesForCurrentThread()-startBytes;double total=0;foreach(double time in timings)total+=time;Array.Sort(timings);
                long fields=Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakFields);
                Check(fields>=count*3&&fields<=count*5,"half-second scheduler advances every wound without reverting to continuous paint");
                Check(Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakCommandExecutions)<=40,"paint batching bounds submission frequency");
                var pixels=new Texture2D(128,128,TextureFormat.RGBA32,false);
                watch.Restart();Color32[] before=Read(first,pixels);watch.Stop();double wait=watch.Elapsed.TotalMilliseconds;
                int covered=0;foreach(var pixel in before)if(pixel.a>0)covered++;Check(covered>0,"runtime shader deposits wound blood");
                Color32[] handsPixels=Read(sleeve,pixels);int different=0;for(int i=0;i<before.Length;i++)if(!before[i].Equals(handsPixels[i]))different++;
                Check(different==0,"sleeves with displaced first-person bones use anatomical correspondence");
                Color32[] lodPixels=Read(lod,pixels);int lodCovered=0;foreach(var pixel in lodPixels)if(pixel.a>0)lodCovered++;
                Check(Math.Abs(covered-lodCovered)<20,"rotated LOD UVs receive the same stain coverage");
                if(count==1)File.WriteAllBytes(Path.Combine(output,"runtime-lod.png"),pixels.EncodeToPNG());
                for(int i=0;i<count;i++)ClothingSoakingSystem.Stop(descriptors[i].StreamId);
                ClothingSoakingSystem.BeginFrame();ClothingSoakingSystem.Update(now+10);
                Color32[] stopped=Read(first,pixels);for(int i=0;i<before.Length;i++)Check(before[i].Equals(stopped[i]),"treatment leaves deposited pixels unchanged");
                Check(LimbDecalTextureTransfer.Textures.Count==count*3,"treatment does not destroy native-owned textures");
                samples.Add(new Sample{wounds=count,initializationMs=init,meanFrameCpuMs=total/timings.Length,p99FrameCpuMs=timings[(int)(timings.Length*.99)],gpuReadbackWaitMs=wait,allocatedBytes=allocation,textureBytes=count*3*128*128*4});
                Check(allocation==0,"runtime skin/transport/upload has zero steady-state managed allocations");
                UnityEngine.Object.DestroyImmediate(pixels);
            }
            finally
            {
                ClothingSoakingSystem.Reset();foreach(var root in roots)UnityEngine.Object.DestroyImmediate(root);foreach(var mesh in meshes)UnityEngine.Object.DestroyImmediate(mesh);
                foreach(var rt in LimbDecalTextureTransfer.Textures.Values){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}LimbDecalTextureTransfer.Textures.Clear();
            }
        }
        VerifySharedWounds(material, blood);
        appearance.maximumNativeChannelDifference=ClothingSoakAppearanceTests.MaximumNativeChannelDifference;
        UnityEngine.Object.DestroyImmediate(material);effectsBundle.Unload(true);shaderBundle.Unload(false);
        File.WriteAllText(Path.Combine(output,"runtime-verification.json"),JsonUtility.ToJson(new Report{passed=true,checks=_checks,samples=samples.ToArray(),nativeCompositorReference=true,twoPassPainting=true,gpuResident=true,builtBundle=true,destinationRecovery=true,sharedWoundsMatchImmediate=true,initialPixels=initialPixels,pixelsAfter30Seconds=finalPixels,downhillCentimetres=downhill,appearance=appearance,limitations="Synthetic 3969-vertex clothing, three renderers per wound; actual EFT blood texture, projection and compositor. CPU includes real GPU dispatch, matrix upload and composition submission; transport, posing and paint state remain on the GPU. GPU readback wait is synchronization wall time, not isolated GPU frame cost. Shared-wound RGBA agreement within 3 quantization levels and treatment ownership checked. Live garment correspondence and in-raid appearance still require verification."},true));
    }
    private static object Field(object target,string name) => target.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public).GetValue(target);
    private static object StaticField(string name) => typeof(ClothingSoakingSystem).GetField(name,System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null);
    private static void Set(object target,string name,object value) => target.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public).SetValue(target,value);
    private static void ClearTexture(RenderTexture texture)
    { var previous=RenderTexture.active; RenderTexture.active=texture; GL.Clear(false,true,Color.clear); RenderTexture.active=previous; }
    private static void VerifySharedWounds(Material garmentMaterial,Texture blood)
    {
        var root=new GameObject("shared garment fixture");var meshes=new List<Mesh>();
        var bone=new GameObject("arm");bone.transform.SetParent(root.transform,false);
        var player=new Player();var garment=new Garment();player.PlayerBody.BodySkins.Add(EBodyModelPart.Body,garment);
        var skin=Skin(root.transform,bone.transform,garmentMaterial,false,meshes);
        var lod=Skin(root.transform,bone.transform,garmentMaterial,true,meshes);garment.Renderers.Add(skin);garment.Renderers.Add(lod);
        var descriptors=new[] {
            new BloodStreamData {StreamId=501,BodyPart=EBodyPart.LeftArm,LocalPoint=new Vector3(.30f,.31f,0),TriggerKind=BloodStreamTriggerKind.LightWound},
            new BloodStreamData {StreamId=502,BodyPart=EBodyPart.LeftArm,LocalPoint=new Vector3(.32f,.32f,0),TriggerKind=BloodStreamTriggerKind.HeavyWound}};
        var paint=new Material(Shader.Find("Rupture/ClothingSoak"));paint.SetInt("_SrcBlend",1);paint.SetInt("_DstBlend",0);paint.SetTexture("_BloodTex",blood);
        var compose=new Material(TextureDecalsPainter.Compositor);
        var reference=new Dictionary<Renderer,RenderTexture>();var denseMeshes=new Dictionary<object,Mesh>();
        var pixels=new Texture2D(128,128,TextureFormat.RGBA32,false);var commands=new CommandBuffer();
        try
        {
            float now=Time.realtimeSinceStartup;
            var watch=Stopwatch.StartNew();object[] wounds=null;
            while(watch.Elapsed.TotalSeconds<15)
            {
                now+=.017f;ClothingSoakingSystem.BeginFrame();
                foreach(var descriptor in descriptors)ClothingSoakingSystem.Supply(descriptor,player,bone.transform,now);
                ClothingSoakingSystem.Update(now);System.Threading.Thread.Sleep(1);
                var slots=(Array)StaticField("Wounds");var a=slots.GetValue(0);var b=slots.GetValue(1);
                if(a!=null&&b!=null&&Field(a,"Gpu")!=null&&Field(b,"Gpu")!=null&&Field(a,"Preparation")==null&&Field(b,"Preparation")==null){wounds=new[]{a,b};break;}
            }
            Check(wounds!=null,"shared wounds finish preparation");
            var first=Field(wounds[0],"Source");var second=Field(wounds[1],"Source");
            Check(ReferenceEquals(Field(first,"Rest"),Field(second,"Rest"))&&ReferenceEquals(Field(first,"Topology"),Field(second,"Topology")),"same garment shares immutable skin/topology");
            Check(!ReferenceEquals(Field(wounds[0],"Gpu"),Field(wounds[1],"Gpu")),"shared geometry retains independent wound saturation");
            foreach(var pair in LimbDecalTextureTransfer.Textures)
            {ClothingSoakAppearanceTests.InitializeTexture(pair.Value,compose);var rt=new RenderTexture(pair.Value.descriptor);rt.Create();ClothingSoakAppearanceTests.InitializeTexture(rt,compose);reference.Add(pair.Key,rt);}
            var cpuFields=new Dictionary<object,ClothingSoakField>();
            var colorsByTarget=new Dictionary<object,Color[]>();var colorStates=new Dictionary<object,ClothingSoakColors>();
            foreach(var wound in wounds)
            {
                var source=Field(wound,"Source");var gpu=(ClothingSoakGpuField)Field(wound,"Gpu");
                var topology=(ClothingSoakTopology)Field(source,"Topology");
                var cpu=new ClothingSoakField(topology);var initial=new float[topology.Rest.Length];gpu.Current.GetData(initial);
                for(int i=0;i<initial.Length;i++)if(initial[i]>=1)cpu.Seed(i);
                gpu.Current.SetData(cpu.Saturation);gpu.Next.SetData(cpu.Saturation);cpuFields.Add(wound,cpu);
                foreach(var target in (System.Collections.IEnumerable)Field(wound,"Targets"))
                {
                    ((ClothingSoakGpuTarget)Field(target,"Gpu")).ResetHistory();
                    var original=(Mesh)Field(target,"Original");var colors=new Color[original.vertexCount];
                    colorsByTarget.Add(target,colors);colorStates.Add(target,new ClothingSoakColors(colors,(int[])Field(target,"Map"),initial.Length));
                    var mesh=new Mesh {vertices=original.vertices,uv=original.uv,triangles=original.triangles};denseMeshes.Add(target,mesh);
                }
            }
            for(int tick=0;tick<100;tick++)
            {
                now+=.101f;bone.transform.localRotation=Quaternion.Euler(0,0,tick*.7f);
                ClothingSoakingSystem.BeginFrame();foreach(var descriptor in descriptors)ClothingSoakingSystem.Supply(descriptor,player,bone.transform,now);
                foreach(var wound in wounds) { Set(wound,"NextTick",now); Set(wound,"LastAdvance",now - .1f); }
                int cursor=(int)StaticField("_cursor");
                Rupture.Ragdolls.Classes.ShadowRagdollWorld.ResetWork();
                ClothingSoakingSystem.Update(now);
                Check(Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakCommandExecutions)<=1,"one clothing submission per frame");
                Check(Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakColorUploadVertices)==0,"no steady CPU color upload");
                Check(Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakPoseComputedVertices)==0,"no steady CPU vertex posing");
                Check(Rupture.Ragdolls.Classes.ShadowRagdollWorld.Work(Rupture.Ragdolls.Classes.ShadowRagdollWorld.MainThreadWork.SoakGpuBoneUploadBytes)==64,"shared wounds upload one bone palette");
                // Independent dense-upload/immediate native-composition oracle,
                // in the actual scheduler's wound order, including overlapping stains.
                for(int offset=0;offset<64;offset++)
                {
                    int slot=(cursor+offset)%64;if(slot>1)continue;
                    var wound=wounds[slot];var source=Field(wound,"Source");
                    source.GetType().GetMethod("Pose",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic,null,new[]{typeof(bool[])},null).Invoke(source,new object[]{null});
                    var cpu=cpuFields[wound];
                    const float elapsed = .1f; const int steps = 1;
                    cpu.Step((Vector3[])Field(source,"Posed"),Physics.gravity,.1f,slot==1?3:1);
                    var gpuValues=new float[cpu.Saturation.Length];((ClothingSoakGpuField)Field(wound,"Gpu")).Current.GetData(gpuValues);
                    for(int vertex=0;vertex<gpuValues.Length;vertex++)
                        if(Math.Abs(gpuValues[vertex]-cpu.Saturation[vertex])>=.0001f)
                            throw new Exception("shared GPU field mismatch tick="+tick+" slot="+slot+" vertex="+vertex+" gpu="+gpuValues[vertex]+" cpu="+cpu.Saturation[vertex]+" steps="+steps+" elapsed="+elapsed);
                        else _checks++;
                    foreach(var target in (System.Collections.IEnumerable)Field(wound,"Targets"))
                    {
                        if(!colorStates[target].Update(cpu))continue;
                        var renderer=(Renderer)Field(target,"Renderer");var destination=reference[renderer];
                        var stamp=RenderTexture.GetTemporary(destination.descriptor);var merged=RenderTexture.GetTemporary(destination.descriptor);
                        var mesh=denseMeshes[target];mesh.colors=colorsByTarget[target];
                        try
                        {
                            commands.Clear();commands.SetRenderTarget(stamp);commands.ClearRenderTarget(false,true,Color.clear);commands.DrawMesh(mesh,Matrix4x4.identity,paint,0,0);Graphics.ExecuteCommandBuffer(commands);
                            compose.SetTexture("_ObjectDecalTexture",destination);Graphics.Blit(stamp,merged,compose);Graphics.Blit(merged,destination);
                        }
                        finally {RenderTexture.ReleaseTemporary(stamp);RenderTexture.ReleaseTemporary(merged);}
                    }
                }
                foreach(var pair in reference)
                {
                    Color32[] actual=Read(pair.Key,pixels);RenderTexture.active=pair.Value;pixels.ReadPixels(new Rect(0,0,128,128),0,0);pixels.Apply();var expected=pixels.GetPixels32();
                    ClothingSoakAppearanceTests.CompareNativePixels(expected,actual,"Shared wounds and two-pass painting at tick "+tick);
                }
                foreach(var cpu in cpuFields.Values)cpu.ClearChanges();
                Check(ReferenceEquals(((ClothingSoakGpuField)Field(wounds[0],"Gpu")).Geometry,((ClothingSoakGpuField)Field(wounds[1],"Gpu")).Geometry),"due wounds share GPU skinning");
            }
            // Registration failures must leave GPU history untouched so the next
            // available destination receives the accumulated change.
            var history = new Dictionary<ClothingSoakGpuTarget,float[]>();
            foreach(var wound in wounds) foreach(var target in (System.Collections.IEnumerable)Field(wound,"Targets"))
                if(ReferenceEquals(Field(target,"Renderer"),skin))
                {var gpu=(ClothingSoakGpuTarget)Field(target,"Gpu");var previous=new float[gpu.Previous.count];gpu.Previous.GetData(previous);history.Add(gpu,previous);}
            LimbDecalTextureTransfer.Blocked=skin;
            now+=.501f;ClothingSoakingSystem.BeginFrame();foreach(var descriptor in descriptors)ClothingSoakingSystem.Supply(descriptor,player,bone.transform,now);
            ClothingSoakingSystem.Update(now);
            foreach(var pair in history)
            {var actual=new float[pair.Value.Length];pair.Key.Previous.GetData(actual);for(int i=0;i<actual.Length;i++)Check(actual[i]==pair.Value[i],"missed registration does not consume GPU paint history");}
            LimbDecalTextureTransfer.Blocked=null;
            int oldCoverage=Coverage(Read(skin,pixels));
            var replaced=LimbDecalTextureTransfer.Textures[skin];LimbDecalTextureTransfer.Textures.Remove(skin);
            replaced.Release();UnityEngine.Object.DestroyImmediate(replaced);
            now+=.501f;ClothingSoakingSystem.BeginFrame();foreach(var descriptor in descriptors)ClothingSoakingSystem.Supply(descriptor,player,bone.transform,now);
            ClothingSoakingSystem.Update(now);
            Check(Coverage(Read(skin,pixels))>=oldCoverage*.9,"replacement native texture receives complete current stains");
            foreach(var wound in wounds) foreach(var target in (System.Collections.IEnumerable)Field(wound,"Targets"))
                if(ReferenceEquals(Field(target,"Renderer"),skin))
                {
                    var field=(ClothingSoakGpuField)Field(wound,"Gpu");var gpu=(ClothingSoakGpuTarget)Field(target,"Gpu");
                    var current=new float[field.Geometry.Count];var painted=new float[gpu.Previous.count];field.Current.GetData(current);gpu.Previous.GetData(painted);
                    for(int i=0;i<painted.Length;i++)Check(painted[i]==current[i],"replacement commits complete source-space GPU paint history");
                }
            Check(((System.Collections.ICollection)StaticField("ScratchTargets")).Count==1,"matching targets reuse one persistent scratch texture");
            ClothingSoakingSystem.Stop(501);
            Check(((System.Collections.IDictionary)StaticField("Geometry")).Count==2,"treating one wound retains sibling garment resources");
            ClothingSoakingSystem.BeginFrame();ClothingSoakingSystem.Supply(descriptors[1],player,bone.transform,now+.2f);ClothingSoakingSystem.Update(now+.2f);
            Check(!(bool)Field(wounds[1],"Failed"),"remaining wound continues after sibling treatment");
            var remainingField=(ClothingSoakGpuField)Field(wounds[1],"Gpu");var remainingGeometry=remainingField.Geometry;
            var remainingMesh=(Mesh)Field(Field(wounds[1],"Source"),"PaintMesh");
            ClothingSoakingSystem.Stop(502);
            Check(((System.Collections.IDictionary)StaticField("Geometry")).Count==0&&LimbDecalTextureTransfer.Textures.Count==2,"last treatment releases geometry and preserves native stains");
            Check(remainingField.Current==null&&remainingField.Next==null&&remainingGeometry.Skin==null&&remainingGeometry.Bones==null,"last treatment releases GPU field and shared geometry buffers");
            Check(remainingMesh==null,"last treatment destroys the owned projection mesh");
        }
        finally
        {
            RenderTexture.active=null;LimbDecalTextureTransfer.Blocked=null;ClothingSoakingSystem.Reset();commands.Release();
            foreach(var rt in reference.Values){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}
            foreach(var rt in LimbDecalTextureTransfer.Textures.Values){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}LimbDecalTextureTransfer.Textures.Clear();
            foreach(var mesh in denseMeshes.Values)UnityEngine.Object.DestroyImmediate(mesh);foreach(var mesh in meshes)UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(pixels);UnityEngine.Object.DestroyImmediate(paint);UnityEngine.Object.DestroyImmediate(compose);
        }
    }
    private static SkinnedMeshRenderer Skin(Transform root,Transform bone,Material material,bool rotated,List<Mesh> meshes)
    {
        const int side=63;var vertices=new Vector3[side*side];var normals=new Vector3[vertices.Length];var uv=new Vector2[vertices.Length];var weights=new BoneWeight[vertices.Length];var triangles=new int[(side-1)*(side-1)*6];int k=0;
        for(int y=0;y<side;y++)for(int x=0;x<side;x++){int i=y*side+x;vertices[i]=new Vector3(x*.01f,y*.01f,0);normals[i]=Vector3.forward;uv[i]=rotated?new Vector2(y/(float)(side-1),1-x/(float)(side-1)):new Vector2(x/(float)(side-1),y/(float)(side-1));weights[i]=new BoneWeight{boneIndex0=0,weight0=1};if(x==side-1||y==side-1)continue;triangles[k++]=i;triangles[k++]=i+1;triangles[k++]=i+side;triangles[k++]=i+1;triangles[k++]=i+side+1;triangles[k++]=i+side;}
        var mesh=new Mesh{vertices=vertices,normals=normals,uv=uv,triangles=triangles,boneWeights=weights,bindposes=new[]{Matrix4x4.identity}};meshes.Add(mesh);
        var obj=new GameObject("garment");obj.transform.SetParent(root,false);var skin=obj.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.sharedMaterial=material;skin.bones=new[]{bone};skin.rootBone=bone;return skin;
    }
    private static Color32[] Read(Renderer renderer,Texture2D pixels){var previous=RenderTexture.active;RenderTexture.active=LimbDecalTextureTransfer.Textures[renderer];pixels.ReadPixels(new Rect(0,0,128,128),0,0);pixels.Apply();RenderTexture.active=previous;return pixels.GetPixels32();}
    private static int Coverage(Color32[] pixels){int count=0;foreach(var pixel in pixels)if(pixel.r>=3&&pixel.a>=16)count++;return count;}
}
#endif
