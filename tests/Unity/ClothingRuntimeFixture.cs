#if UNITY_EDITOR
// Isolated EFT boundary doubles. The compiled clothing runtime and Unity skinning,
// textures, shader and command buffers are real; this does not validate EFT's
// live character asset correspondence. The runtime test supplies EFT's actual
// compositor and blood texture from the read-only installed bundles.
using System;
using System.Collections.Generic;
using UnityEngine;
// Production cleanup is deferred in play mode. Editor fixtures must destroy
// their own transient resources immediately; Unity rejects Destroy in edit mode.
internal static class FixtureDestroy
{
    internal static void Destroy(UnityEngine.Object value)
    { if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
}
public enum EBodyPart { Head, Chest, Stomach, LeftArm, RightArm, LeftLeg, RightLeg, Common }
public enum EDecalTextureType { Blood }
public class TextureDecalsPainter
{
    public static Shader Compositor;
    public Shader _blitShader=Compositor ?? Shader.Find("Hidden/Rupture_SoakTest_Compose");
    public Texture2D Blood;
    public Texture2D GetTextureByType(EDecalTextureType type)=>Blood;
}
namespace EFT
{
    public enum EBodyModelPart { Body, Feet, Hands }
    public sealed class Health { public bool IsAlive=true; }
    public sealed class Garment { public readonly List<Renderer> Renderers=new List<Renderer>();public IEnumerable<Renderer> GetRenderers()=>Renderers; }
    public sealed class Body { public readonly Dictionary<EBodyModelPart,Garment> BodySkins=new Dictionary<EBodyModelPart,Garment>(); }
    public sealed class Player { public Health HealthController=new Health();public Body PlayerBody=new Body();public bool IsYourPlayer=true; }
}
namespace Comfort.Common { public static class Singleton<T> where T:class { public static T Instance;public static bool Instantiated=>Instance!=null; } }
namespace Systems.Effects { public sealed class Effects { public TextureDecalsPainter TexDecals=new TextureDecalsPainter(); } }
namespace Rupture
{
    public static class WoundResources { public static bool IsLoading=>false; public static AssetBundle Bundle;
        public static Shader GetShader(string name)=>Bundle == null ? Shader.Find(name) : Bundle.LoadAsset<Shader>("assets/rupture/"+name.Substring(name.LastIndexOf('/')+1).ToLowerInvariant()+".shader");
        public static ComputeShader GetComputeShader(string name)=>Bundle == null ? UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/"+name+".compute") : Bundle.LoadAsset<ComputeShader>("assets/rupture/"+name.ToLowerInvariant()+".compute"); }
    public static class RupturePlugin { public static readonly LogSink Log=new LogSink(); }
    public sealed class LogSink { public void LogWarning(string message)=>Debug.LogWarning(message); }
}
namespace Rupture.Dismemberment.Classes.Packets
{
    public enum BloodStreamTriggerKind:byte { HeavyWound=1, LightWound=6 }
    public struct BloodStreamData { public uint StreamId;public EBodyPart BodyPart;public Vector3 LocalPoint;public BloodStreamTriggerKind TriggerKind; }
}
namespace Rupture.Dismemberment.Classes.Limbs
{
    internal static class LimbDecalTextureTransfer
    {
        internal static readonly Dictionary<Renderer,RenderTexture> Textures=new Dictionary<Renderer,RenderTexture>();
        internal static Renderer Blocked;
        internal static bool EnsureRegisteredTexture(Renderer renderer,out RenderTexture texture,out string failure,out bool createdTexture)
        {
            failure=null;createdTexture=false;
            if (ReferenceEquals(renderer,Blocked)) { texture=null; failure="test registration unavailable"; return false; }
            if(Textures.TryGetValue(renderer,out texture))return true;
            texture=new RenderTexture(128,128,0,RenderTextureFormat.ARGB32);texture.Create();
            var previous=RenderTexture.active;RenderTexture.active=texture;GL.Clear(false,true,Color.clear);RenderTexture.active=previous;
            Textures.Add(renderer,texture);createdTexture=true;return true;
        }
    }
}
#endif
