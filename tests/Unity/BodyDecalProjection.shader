Shader "Hidden/Rupture_Test_Decal"
{
    SubShader { Pass {
        Cull Off ZWrite Off ZTest Always
        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "UnityCG.cginc"
        float3 _Point; float _Radius;
        struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct Output { float4 position : SV_POSITION; float3 world : TEXCOORD0; };
        Output vert(Input v) {
            Output o; o.position = float4(v.uv * 2 - 1, 0, 1);
            o.world = mul(unity_ObjectToWorld, v.vertex).xyz; return o;
        }
        fixed4 frag(Output i) : SV_Target { clip(_Radius - length(i.world - _Point)); return fixed4(1,0,0,1); }
        ENDCG
    } }
}
