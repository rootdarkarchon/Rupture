Shader "Rupture/ClothingSoak"
{
    Properties {
        _BloodTex ("Native blood decal", 2D) = "white" {}
        [HideInInspector] _MainTex ("Accumulation stamp", 2D) = "black" {}
        [HideInInspector] _SrcBlend ("Source blend", Float) = 5
        [HideInInspector] _DstBlend ("Destination blend", Float) = 10
    }
    SubShader { Pass {
        Cull Off ZWrite Off ZTest Always
        Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
        CGPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #pragma target 4.5
        #pragma multi_compile __ RPT_SOAK_GPU
        #include "UnityCG.cginc"
        sampler2D _BloodTex;
        #if defined(RPT_SOAK_GPU)
        StructuredBuffer<float> _SoakCurrent, _SoakPrevious;
        StructuredBuffer<int> _SoakMap;
        #endif
        struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; uint id : SV_VertexID; };
        struct Output { float4 position : SV_POSITION; float3 rest : TEXCOORD0; float2 wet : TEXCOORD1; };
        Output vert(Input v) {
            Output o; o.position = float4(v.uv * 2 - 1, 0, 1);
            #if UNITY_UV_STARTS_AT_TOP
            o.position.y = -o.position.y;
            #endif
            o.rest = v.vertex.xyz;
            #if defined(RPT_SOAK_GPU)
            int source = _SoakMap[v.id];
            o.wet = source >= 0 ? float2(_SoakCurrent[source], _SoakPrevious[source]) : float2(0, 0);
            #else
            o.wet = v.color.rg;
            #endif
            return o;
        }
        float hash3(float3 p) {
            p = frac(p * float3(0.1031, 0.1030, 0.0973));
            p += dot(p, p.yzx + 33.33);
            return frac((p.x + p.y) * p.z);
        }
        float noise(float3 p) {
            float3 cell = floor(p); float3 f = frac(p); f = f*f*(3-2*f);
            // Hash all three coordinates independently; a scalar dot-product
            // lattice aliases along fixed diagonal translations on flat fabric.
            return lerp(lerp(lerp(hash3(cell),hash3(cell+float3(1,0,0)),f.x),
                lerp(hash3(cell+float3(0,1,0)),hash3(cell+float3(1,1,0)),f.x),f.y),
                lerp(lerp(hash3(cell+float3(0,0,1)),hash3(cell+float3(1,0,1)),f.x),
                lerp(hash3(cell+float3(0,1,1)),hash3(cell+float3(1,1,1)),f.x),f.y),f.z);
        }
        fixed4 frag(Output i) : SV_Target {
            // GPU-resident fields cannot cheaply tell the CPU that nothing
            // changed. Reject dry/unchanged fragments before evaluating noise.
            [branch] if (i.wet.x <= i.wet.y) discard;
            float broad = noise(i.rest * 37);
            float fine = noise(i.rest * 137);
            float edge = 0.18 + broad * 0.18 + fine * 0.12;
            // Each texel is deposited only on crossing the advancing front.
            // Replaying even an unchanged translucent stamp through EFT's merge
            // shader repeatedly increases its RGB and changes its encoded alpha.
            clip(i.wet.x - edge);
            clip(edge - i.wet.y - 0.00001);
            // Use the native blood core, never tile the splatter across fabric.
            // Non-repeating 3D variation only reduces native colour/opacity.
            fixed4 blood = tex2D(_BloodTex, float2(0.5, 0.5));
            blood.rgb *= lerp(0.65, 0.9, broad);
            blood.a *= lerp(0.7, 0.9, fine);
            return blood;
        }
        ENDCG
    }
    Pass {
        Name "NativeAccumulation"
        Cull Off ZWrite Off ZTest Always
        // EFT Decal/Merge applies source alpha to all four channels, including
        // alpha itself. Keep stamp construction in pass zero for overlapping UVs.
        Blend SrcAlpha OneMinusSrcAlpha
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment frag
        #pragma target 3.0
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        float4 frag(v2f_img i) : SV_Target {
            // Decal/Merge reserves this corner for its transparent texture marker.
            // New textures receive native initialization once; never tint the marker.
            if (i.uv.x < 0.01 && i.uv.y < 0.01) discard;
            return tex2D(_MainTex, i.uv);
        }
        ENDCG
    } }
}
