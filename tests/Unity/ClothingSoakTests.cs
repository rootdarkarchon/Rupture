#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Rupture.Dismemberment.Classes.BloodStreams;

public static class ClothingSoakTests
{
    [Serializable] public sealed class Report { public bool passed; public int checks; public Sample[] samples; public ClothingSoakSparseTests.Benchmark sparseComparison; }
    [Serializable] public sealed class Sample { public int wounds; public double millisecondsPerTick; public long allocatedBytes; }
    private static int _checks;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks++; }
    public static void RunBatch()
    {
        string output = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "SoakVerification");
        Directory.CreateDirectory(output);
        var p = new[] { Vector3.zero, Vector3.down * 0.02f, Vector3.up * 0.02f, Vector3.right * 0.02f,
            Vector3.zero, Vector3.left * 0.02f, new Vector3(0,0,0.001f), new Vector3(0,0.02f,0.001f), new Vector3(0.02f,0,0.001f) };
        var n = new Vector3[p.Length]; for (int i=0;i<n.Length;i++) n[i]=Vector3.forward;
        var topology = new ClothingSoakTopology(p, n, new[] {0,1,3,0,3,2,4,5,2,6,7,8}, new int[p.Length]);
        var field = new ClothingSoakField(topology); field.Seed(0);
        field.Step(p, Vector3.down, 0.1f, 1);
        Check(field.Saturation[1] > field.Saturation[3] && field.Saturation[3] > field.Saturation[2], "gravity bias");
        Check(field.Saturation[4] == 1 && field.Saturation[5] > 0, "verified seam shares supply");
        Check(field.Saturation[6] == 0, "disconnected garment stays dry");
        var patch = new ClothingSoakField(topology); patch.SeedPatch(0,p,0.025f);
        Check(patch.Saturation[1] == 1 && patch.Saturation[4] == 1, "initial patch covers connected fabric and verified seams");
        Check(patch.Saturation[6] == 0, "initial patch cannot jump to a layer only one millimetre away");
        var heavy = new ClothingSoakField(topology); heavy.Seed(0); heavy.Step(p, Vector3.down, 0.1f, 3);
        Check(Mathf.Abs(heavy.Saturation[1] - field.Saturation[1]*3) < 0.00001f, "heavy transports three times faster");
        float up = field.Saturation[2], down = field.Saturation[1];
        field.Step(p, Vector3.up, 0.1f, 1);
        Check(field.Saturation[2]-up > field.Saturation[1]-down, "new growth responds to changed pose/gravity");
        float[] stopped = (float[])field.Saturation.Clone(); field.Step(p, Vector3.down, 0.1f, 0);
        for (int i=0;i<stopped.Length;i++) Check(stopped[i] == field.Saturation[i], "stopped wound freezes");
        VerifyPixels(output);
        var samples = new Sample[3]; int[] counts={1,8,24};
        // An actual 3,969-vertex garment grid, rather than a tiny policy fixture.
        int side=63; var vertices=new Vector3[side*side]; var normals=new Vector3[vertices.Length];
        var indices=new int[(side-1)*(side-1)*6]; int k=0;
        for(int y=0;y<side;y++) for(int x=0;x<side;x++) { int i=y*side+x; vertices[i]=new Vector3(x*0.01f,y*0.01f,0); normals[i]=Vector3.forward;
            if(x==side-1||y==side-1)continue; indices[k++]=i;indices[k++]=i+1;indices[k++]=i+side;indices[k++]=i+1;indices[k++]=i+side+1;indices[k++]=i+side; }
        var grid=new ClothingSoakTopology(vertices,normals,indices,new int[vertices.Length]);
        ClothingSoakSparseTests.Verify(topology);
        ClothingSoakSparseTests.Verify(grid);
        var comparison = ClothingSoakSparseTests.Compare(grid);
        for (int c=0;c<counts.Length;c++) {
            var fields=new ClothingSoakField[counts[c]];
            for(int i=0;i<fields.Length;i++){fields[i]=new ClothingSoakField(grid);fields[i].Seed(vertices.Length/2);fields[i].Step(vertices,Vector3.down,0.1f,1);}
            var watch=new Stopwatch(); long before=GC.GetAllocatedBytesForCurrentThread();watch.Start();
            for(int tick=0;tick<100;tick++)for(int i=0;i<fields.Length;i++)fields[i].Step(vertices,Vector3.down,0.1f,1);
            watch.Stop();long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
            samples[c]=new Sample{wounds=fields.Length,millisecondsPerTick=watch.Elapsed.TotalMilliseconds/100,allocatedBytes=bytes};
            Check(bytes==0,"allocation-free transport");
        }
        File.WriteAllText(Path.Combine(output,"verification.json"),JsonUtility.ToJson(new Report{passed=true,checks=_checks,samples=samples,sparseComparison=comparison},true));
        UnityEngine.Debug.Log("PASS: clothing soaking transport and accumulation");
    }
    private static void VerifyPixels(string output)
    {
        var shader=Shader.Find("Rupture/ClothingSoak"); Check(shader!=null&&shader.isSupported,"soak shader supported");
        var material=new Material(shader);var tex=new Texture2D(1,1);tex.SetPixel(0,0,new Color(0.2f,0.005f,0.002f,1));tex.Apply();material.SetTexture("_BloodTex",tex);
        var mesh=new Mesh {vertices=new[]{new Vector3(0,0,0),new Vector3(1,0,0),new Vector3(1,1,0),new Vector3(0,1,0)},
            uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,1,2,0,2,3}};
        var colors=new[]{Color.clear,Color.clear,Color.clear,Color.clear};mesh.colors=colors;
        var rt=new RenderTexture(128,128,0,RenderTextureFormat.ARGB32);rt.Create();var buffer=new CommandBuffer();
        var read=new Texture2D(128,128,TextureFormat.RGBA32,false);var previous=RenderTexture.active;
        try {
            buffer.SetRenderTarget(rt);buffer.ClearRenderTarget(false,true,new Color(0.03f,0.04f,0.05f,0.3f));Graphics.ExecuteCommandBuffer(buffer);
            Color32[] baseline=Read(rt,read);buffer.Clear();buffer.SetRenderTarget(rt);buffer.DrawMesh(mesh,Matrix4x4.identity,material,0,0);Graphics.ExecuteCommandBuffer(buffer);
            Color32[] dry=Read(rt,read);for(int i=0;i<dry.Length;i++)Check(dry[i].Equals(baseline[i]),"dry pixels preserve existing decals");
            colors[0]=colors[3]=Color.red;mesh.colors=colors;Graphics.ExecuteCommandBuffer(buffer);
            Color32[] wet=Read(rt,read);int changed=0;for(int i=0;i<wet.Length;i++)if(!wet[i].Equals(dry[i]))changed++;
            Check(changed>100,"wet front produces pixels");File.WriteAllBytes(Path.Combine(output,"soak-front.png"),read.EncodeToPNG());
            Color32[] frozen=Read(rt,read);for(int i=0;i<frozen.Length;i++)Check(frozen[i].Equals(wet[i]),"stopped accumulation preserves pixels");
        } finally {RenderTexture.active=previous;buffer.Release();UnityEngine.Object.DestroyImmediate(mesh);UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(tex);UnityEngine.Object.DestroyImmediate(read);rt.Release();UnityEngine.Object.DestroyImmediate(rt);}
    }
    private static Color32[] Read(RenderTexture rt,Texture2D read){RenderTexture.active=rt;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();return read.GetPixels32();}
}
#endif
