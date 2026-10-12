Shader "Hidden/Rupture_SoakTest_Compose"
{
    Properties { _MainTex ("Stamp", 2D) = "black" {} _ObjectDecalTexture ("Existing", 2D) = "black" {} }
    SubShader { Pass {
        Cull Off ZWrite Off ZTest Always
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex, _ObjectDecalTexture;
        fixed4 frag(v2f_img i):SV_Target {
            fixed4 stamp=tex2D(_MainTex,i.uv), old=tex2D(_ObjectDecalTexture,i.uv);
            return fixed4(stamp.rgb * stamp.a + old.rgb * (1-stamp.a), stamp.a + old.a * (1-stamp.a));
        }
        ENDCG
    } }
}
