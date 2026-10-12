Shader "Rupture/HeadWoundInterior"
{
    Properties
    {
        _FleshTex ("Flesh Albedo", 2D) = "white" {}
        [Normal] _FleshNormal ("Flesh Normal", 2D) = "bump" {}
        _BloodTex ("Blood Albedo", 2D) = "white" {}
        [Normal] _BloodNormal ("Blood Normal", 2D) = "bump" {}
        _BloodAmount ("Blood Amount", Range(0, 1)) = 0.78
        _FleshSmoothness ("Flesh Smoothness", Range(0, 1)) = 0.22
        _WetSmoothness ("Wet Blood Smoothness", Range(0, 1)) = 0.9
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 300

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow
        #pragma target 3.0

        sampler2D _FleshTex;
        sampler2D _FleshNormal;
        sampler2D _BloodTex;
        sampler2D _BloodNormal;
        half _BloodAmount;
        half _FleshSmoothness;
        half _WetSmoothness;
        half _NormalStrength;

        struct Input
        {
            float2 uv_FleshTex;
            float4 color : COLOR;
        };

        void surf(Input IN, inout SurfaceOutputStandard output)
        {
            half irregularBlood = saturate(IN.color.r * 0.85h + _BloodAmount * 0.45h);
            fixed4 flesh = tex2D(_FleshTex, IN.uv_FleshTex);
            fixed4 blood = tex2D(_BloodTex, IN.uv_FleshTex * 1.37h + float2(0.13h, 0.07h));
            half3 fleshNormal = UnpackNormal(tex2D(_FleshNormal, IN.uv_FleshTex));
            half3 bloodNormal = UnpackNormal(tex2D(_BloodNormal, IN.uv_FleshTex * 1.37h + float2(0.13h, 0.07h)));

            output.Albedo = lerp(flesh.rgb, blood.rgb, irregularBlood);
            output.Normal = normalize(lerp(fleshNormal, bloodNormal, irregularBlood) * half3(_NormalStrength, _NormalStrength, 1));
            output.Metallic = 0;
            output.Smoothness = lerp(_FleshSmoothness, _WetSmoothness, irregularBlood);
            output.Occlusion = lerp(0.72h, 1.0h, irregularBlood);
            output.Alpha = 1;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
