Shader "Hidden/Rupture/ClothingVertexColors"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct Output { float4 vertex : SV_POSITION; fixed4 color : COLOR; };
            Output vert(Input v) { Output o; o.vertex = UnityObjectToClipPos(v.vertex); o.color = v.color; return o; }
            fixed4 frag(Output i) : SV_Target { return i.color; }
            ENDCG
        }
    }
}
