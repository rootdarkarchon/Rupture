Shader "Rupture/LimbSeverSurface"
{
    Properties
    {
        _FleshTex ("EFT Flesh Albedo", 2D) = "white" {}
        [Normal] _FleshNormal ("EFT Flesh Normal", 2D) = "bump" {}
        _BloodTex ("EFT Blood Albedo", 2D) = "white" {}
        [Normal] _BloodNormal ("EFT Blood Normal", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1.15
        _VariationOffset ("Per Cut Variation", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry+1" }
        LOD 350

        CGPROGRAM
        #pragma surface surf StandardSpecular fullforwardshadows addshadow
        #pragma target 3.0

        sampler2D _FleshTex;
        sampler2D _FleshNormal;
        sampler2D _BloodTex;
        sampler2D _BloodNormal;
        half _NormalStrength;
        float4 _VariationOffset;

        struct Input
        {
            float2 uv_FleshTex;
            float4 color : COLOR;
        };

        void surf(Input IN, inout SurfaceOutputStandardSpecular output)
        {
            float2 uvA = IN.uv_FleshTex * 1.31 + _VariationOffset.xy;
            float2 uvB = float2(-IN.uv_FleshTex.y, IN.uv_FleshTex.x) * 2.17 +
                _VariationOffset.yx * 0.73 + float2(0.19, 0.43);
            float2 uvC = IN.uv_FleshTex * 4.07 + _VariationOffset.xy * 1.91 + float2(0.61, 0.07);

            fixed4 fleshA = tex2D(_FleshTex, uvA);
            fixed4 fleshB = tex2D(_FleshTex, uvB);
            fixed4 bloodA = tex2D(_BloodTex, uvB);
            fixed4 bloodB = tex2D(_BloodTex, uvC);
            half3 fleshNormalA = UnpackNormal(tex2D(_FleshNormal, uvA));
            half3 fleshNormalB = UnpackNormal(tex2D(_FleshNormal, uvB));
            half3 bloodNormalA = UnpackNormal(tex2D(_BloodNormal, uvB));
            half3 bloodNormalB = UnpackNormal(tex2D(_BloodNormal, uvC));

            half freshBlood = saturate(IN.color.r);
            half bone = saturate(IN.color.g);
            half tissueVariation = saturate(IN.color.b);
            half wetness = saturate(IN.color.a);
            // Alpha zero belongs only to generated cloth-lip vertices. Legacy
            // flesh/bone wetness stays above this reserved range.
            half cloth = 1.0h - step(0.05h, IN.color.a);
            half clot = saturate(tissueVariation * (1.0h - freshBlood * 0.45h));

            half3 variedFlesh = lerp(fleshA.rgb, fleshB.rgb, tissueVariation);
            half3 variedBlood = lerp(bloodA.rgb, bloodB.rgb, clot);
            half3 softTissue = lerp(variedFlesh, variedBlood, saturate(freshBlood * 0.88h + 0.12h));
            softTissue *= lerp(1.0h, 0.34h, clot * 0.78h);

            half grime = saturate((bloodB.r + fleshB.g) * 0.5h);
            half3 dirtyBone = lerp(half3(0.46h, 0.39h, 0.27h), half3(0.82h, 0.76h, 0.59h), grime);
            dirtyBone = lerp(dirtyBone, variedBlood * 0.72h, freshBlood * 0.62h);
            output.Albedo = lerp(softTissue, dirtyBone, bone);
            half3 soakedCloth = lerp(half3(0.009h, 0.001h, 0.001h),
                half3(0.055h, 0.004h, 0.003h), saturate(bloodB.r * 1.8h + tissueVariation * 0.2h));
            output.Albedo = lerp(output.Albedo, soakedCloth, cloth);

            half3 softNormal = normalize(lerp(fleshNormalA, bloodNormalA, freshBlood));
            softNormal = normalize(lerp(softNormal, bloodNormalB, clot * 0.62h));
            half3 boneNormal = normalize(lerp(fleshNormalB, bloodNormalB, freshBlood * 0.45h));
            output.Normal = normalize(lerp(softNormal, boneNormal, bone) * half3(_NormalStrength, _NormalStrength, 1));
            output.Normal = normalize(lerp(output.Normal, half3(0, 0, 1), cloth * 0.85h));
            // Keep the existing dielectric tissue response, but make the cloth
            // reflection weak and blood-tinted instead of a broad gray highlight.
            output.Specular = lerp(unity_ColorSpaceDielectricSpec.rgb, half3(0.006h, 0.0008h, 0.0006h), cloth);
            output.Smoothness = lerp(0.16h, 0.93h, wetness) * lerp(1.0h, 0.48h, bone);
            output.Smoothness = lerp(output.Smoothness, 0.12h, cloth);
            output.Occlusion = lerp(0.52h, 0.91h, freshBlood) * lerp(1.0h, 0.86h, clot);
            output.Alpha = 1;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
