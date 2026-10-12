#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

public static class ClothingSoakAppearanceTests
{
    [Serializable] public sealed class Report
    {
        public bool passed, unchangedStainRetained, commandBufferMatchesImmediate;
        public int nativeCoreRed, maximumSoakRed, maximumNativeChannelDifference;
    }

    public static Report Run(Texture2D blood, Shader compositor, Shader projection, string output, bool compareCommandsOnly = false)
    {
        if (projection == null) throw new Exception("Native garment projection shader unavailable");
        var native = new Material(projection);
        native.SetTexture("_Decal", blood);
        native.SetVector("_Right", new Vector4(1, 0, 0, 1));
        native.SetVector("_Up", new Vector4(0, 0, 1, .4f));
        native.SetVector("_Fwd", new Vector4(0, 1, 0, 1));
        native.SetVector("_Point", Vector4.zero);
        var paint = new Material(Shader.Find("Rupture/ClothingSoak"));
        paint.SetTexture("_BloodTex", blood); paint.SetInt("_SrcBlend", 1); paint.SetInt("_DstBlend", 0);
        var compose = new Material(compositor);
        var mesh = new Mesh
        {
            vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) },
            uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
            triangles = new[] { 0,1,2,0,2,3 }, colors = new[] { Color.red, Color.red, Color.red, Color.red }
        };
        var stamp = new RenderTexture(256,256,0); stamp.Create();
        var destination = new RenderTexture(stamp.descriptor); destination.Create();
        var merged = new RenderTexture(stamp.descriptor); merged.Create();
        var read = new Texture2D(256,256,TextureFormat.RGBA32,false);
        var commands = new CommandBuffer(); var previous = RenderTexture.active;
        try
        {
            if (compareCommandsOnly)
            {
                VerifyCommandBuffer(paint, native, compose, mesh, stamp, merged, destination, commands, read);
                return new Report { passed=true, commandBufferMatchesImmediate=true };
            }
            Clear(destination, commands); Draw(native, mesh, stamp, commands); Merge(stamp, destination, merged, compose);
            var nativePixels = Read(destination, read);
            File.WriteAllBytes(Path.Combine(output,"native-stain-reference.png"),read.EncodeToPNG());
            int nativeCore = nativePixels[128*256+128].r;
            if (nativeCore < 5) throw new Exception("Native projection reference did not cover the centre");
            InitializeTexture(destination, compose); Draw(paint, mesh, stamp, commands); Blend(stamp, destination, paint, commands);
            var soaked = Read(destination, read); int maximum = 0, covered = 0;
            foreach (var pixel in soaked) { maximum = Math.Max(maximum,pixel.r); if (pixel.r > 0) covered++; }
            if (covered < soaked.Length * .99f) throw new Exception("Saturated fabric contains unintended holes");
            if (maximum > nativeCore + 1) throw new Exception("Soaking is brighter than the native blood core: " + maximum + " > " + nativeCore);
            File.WriteAllBytes(Path.Combine(output,"soak-appearance.png"),read.EncodeToPNG());
            mesh.colors = new[] { Color.yellow, Color.yellow, Color.yellow, Color.yellow };
            for (int tick = 0; tick < 100; tick++) { Draw(paint,mesh,stamp,commands); Blend(stamp,destination,paint,commands); }
            var repeated = Read(destination,read);
            for (int i = 0; i < soaked.Length; i++)
                if (!soaked[i].Equals(repeated[i])) throw new Exception("Unchanged soaked texels were repainted through native composition");
            VerifyCommandBuffer(paint, native, compose, mesh, stamp, merged, destination, commands, read);
            return new Report { passed=true, unchangedStainRetained=true, commandBufferMatchesImmediate=true, nativeCoreRed=nativeCore, maximumSoakRed=maximum, maximumNativeChannelDifference=MaximumNativeChannelDifference };
        }
        finally
        {
            RenderTexture.active=previous; commands.Release();
            foreach(var rt in new[]{stamp,destination,merged}){rt.Release();UnityEngine.Object.DestroyImmediate(rt);}
            foreach(var obj in new UnityEngine.Object[]{native,paint,compose,mesh,read})UnityEngine.Object.DestroyImmediate(obj);
        }
    }
    private static void VerifyCommandBuffer(Material paint, Material native, Material compose, Mesh mesh,
        RenderTexture stamp, RenderTexture merged, RenderTexture reference, CommandBuffer commands, Texture2D read)
    {
        var candidate = new RenderTexture(reference.descriptor); candidate.Create();
        try
        {
            Clear(reference, commands); Clear(candidate, commands);
            // Replay native impact blood followed by an advancing front, unchanged ticks,
            // then another wound over the same texels. Compare all RGBA bytes each step.
            float previous = 0;
            for (int step = 0; step < 40; step++)
            {
                float current = step < 20 ? Mathf.Min(1, step * .07f) : Mathf.Min(1, (step - 20) * .07f);
                if (step == 20) previous = 0;
                mesh.colors = new[] { new Color(current, previous, 0), new Color(current * .8f, previous * .8f, 0),
                    new Color(current * .6f, previous * .6f, 0), new Color(current * .4f, previous * .4f, 0) };
                Material material = step == 0 ? native : paint;
                Draw(material, mesh, stamp, commands); Merge(stamp, reference, merged, compose);
                // Both compositors consume exactly the same immutable stamp.
                commands.Clear();
                if (step == 0) {
                    compose.SetTexture("_ObjectDecalTexture", candidate);
                    commands.Blit(stamp, merged, compose); commands.Blit(merged, candidate);
                } else RecordBlend(stamp, candidate, paint, commands);
                Graphics.ExecuteCommandBuffer(commands);
                var expected = Read(reference, read); var actual = Read(candidate, read);
                try { CompareNativePixels(expected,actual,"native blood followed by two-pass stains, step "+step); }
                catch {
                    string folder=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"SoakVerification");
                    Read(reference,read);File.WriteAllBytes(Path.Combine(folder,"blend-expected.png"),read.EncodeToPNG());
                    Read(candidate,read);File.WriteAllBytes(Path.Combine(folder,"blend-actual.png"),read.EncodeToPNG());
                    Read(stamp,read);File.WriteAllBytes(Path.Combine(folder,"blend-stamp.png"),read.EncodeToPNG());
                    throw;
                }
                previous = current;
            }
        }
        finally { candidate.Release(); UnityEngine.Object.DestroyImmediate(candidate); }
    }
    private static void Clear(RenderTexture rt,CommandBuffer commands){commands.Clear();commands.SetRenderTarget(rt);commands.ClearRenderTarget(false,true,Color.clear);Graphics.ExecuteCommandBuffer(commands);}
    public static void InitializeTexture(RenderTexture texture, Material native)
    {
        var clear = new Texture2D(1,1,TextureFormat.RGBA32,false,true);
        clear.SetPixel(0,0,Color.clear);clear.Apply();
        native.SetTexture("_ObjectDecalTexture",clear);Graphics.Blit(clear,texture,native);
        UnityEngine.Object.DestroyImmediate(clear);
    }
    private static void Draw(Material material,Mesh mesh,RenderTexture rt,CommandBuffer commands){Clear(rt,commands);commands.Clear();commands.SetRenderTarget(rt);commands.DrawMesh(mesh,Matrix4x4.identity,material,0,0);Graphics.ExecuteCommandBuffer(commands);}
    public static int MaximumNativeChannelDifference;
    public static void CompareNativePixels(Color32[] expected,Color32[] actual,string context)
    {
        for(int i=0;i<expected.Length;i++) {
            var a=actual[i];var b=expected[i];
            int difference=Math.Max(Math.Max(Math.Abs(a.r-b.r),Math.Abs(a.g-b.g)),Math.Max(Math.Abs(a.b-b.b),Math.Abs(a.a-b.a)));
            MaximumNativeChannelDifference=Math.Max(MaximumNativeChannelDifference,difference);
            // Fixed-function blend has one fewer quantization step than native
            // merge+copy. The prior exact-native probe bounded 8-bit error at 3.
            if(difference>3)throw new Exception(context+": native RGBA difference "+difference+" at pixel "+i+" actual="+a+" expected="+b);
        }
    }
    private static void RecordBlend(RenderTexture stamp,RenderTexture destination,Material paint,CommandBuffer commands)
    {
        commands.SetRenderTarget(destination,RenderBufferLoadAction.Load,RenderBufferStoreAction.Store);
        commands.Blit(stamp,destination,paint,1);
    }
    private static void Blend(RenderTexture stamp,RenderTexture destination,Material paint,CommandBuffer commands)
    { commands.Clear();RecordBlend(stamp,destination,paint,commands);Graphics.ExecuteCommandBuffer(commands); }
    private static void Merge(RenderTexture stamp,RenderTexture destination,RenderTexture merged,Material material){material.SetTexture("_ObjectDecalTexture",destination);Graphics.Blit(stamp,merged,material);Graphics.Blit(merged,destination);}
    private static Color32[] Read(RenderTexture rt,Texture2D read){RenderTexture.active=rt;read.ReadPixels(new Rect(0,0,256,256),0,0);read.Apply();return read.GetPixels32();}
}
#endif
