Shader "Rupture/PulsatingBloodStream"
{
    Properties
    {
        [HideInInspector] _RPTParcelTime ("Parcel Time", Float) = 0
        _MainTex ("Blood Albedo", 2D) = "white" {}
        _BumpMap ("Blood Normal", 2D) = "bump" {}
        _Color ("Blood Tint", Color) = (0.82, 0.18, 0.16, 1)
        _Smoothness ("Wet Smoothness", Range(0, 1)) = 0.94
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1.1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 250
        Cull Back

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows addshadow vertex:ParcelVertex
        #pragma multi_compile_instancing
        #pragma instancing_options procedural:SetupParcel nolightmap
        #pragma target 4.5

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        half _Smoothness;
        half _NormalStrength;

        struct Input
        {
            float2 uv_MainTex;
            float4 color : COLOR;
        };

        #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
        struct ParcelData
        {
            float4 originTime;
            float4 velocityRadius;
            float4 gravityStrength;
            uint seed; uint padding0; uint padding1; uint padding2;
        };
        StructuredBuffer<ParcelData> _RPTParcels;
        float _RPTParcelTime;
        #endif

        void SetupParcel()
        {
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            unity_ObjectToWorld = float4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1);
            unity_WorldToObject = unity_ObjectToWorld;
            #endif
        }

        void ParcelVertex(inout appdata_full v)
        {
            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            ParcelData parcel = _RPTParcels[unity_InstanceID];
            float age = max(0, _RPTParcelTime - parcel.originTime.w);
            float3 velocity = parcel.velocityRadius.xyz;
            float3 gravity = parcel.gravityStrength.xyz;
            float3 center = parcel.originTime.xyz + velocity * age + gravity * (0.5 * age * age);
            float3 instantaneous = velocity + gravity * age;
            float launchSpeed = max(0.01, length(velocity));
            float speed = max(0.01, length(instantaneous));
            float3 forward = instantaneous / speed;
            float referenceLength = max(parcel.velocityRadius.w * 2.4, launchSpeed * (1.0 / 50.0) * 1.18);
            float span = clamp(max(referenceLength, speed * (1.0 / 50.0) * 1.18), parcel.velocityRadius.w * 2.4, 0.12);
            float radius = parcel.velocityRadius.w * clamp(sqrt(referenceLength / max(referenceLength, span)), 0.46, 1.05);
            float halfLength = span * 0.5;
            float3 tangent = normalize(cross(forward, abs(forward.y) < 0.85 ? float3(0,1,0) : float3(1,0,0)));
            float3 bitangent = normalize(cross(forward, tangent));
            if (v.texcoord1.y != 0)
            {
                v.vertex = float4(center + forward * (halfLength * v.texcoord1.y), 1);
                v.normal = forward * v.texcoord1.y;
            }
            else
            {
                uint state = parcel.seed ^ 0x27D4EB2Du;
                [unroll] for (uint step = 0; step < 24; step++)
                {
                    if (step <= (uint)v.texcoord1.x)
                    {
                        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                    }
                }
                float irregular = lerp(0.92, 1.08, (state & 0x00FFFFFFu) / 16777215.0);
                float t = v.vertex.z;
                float profile = sin(t * UNITY_PI);
                float3 radial = tangent * v.vertex.x + bitangent * v.vertex.y;
                v.vertex = float4(center + forward * ((t - 0.5) * span) + radial * radius * profile * irregular, 1);
                v.normal = normalize(radial * (profile / max(radius, 0.00001)) + forward * ((t * 2 - 1) / max(halfLength, 0.00001)));
            }
            float shade = round(lerp(188, 242, saturate(parcel.gravityStrength.w))) / 255.0;
            v.color = float4(shade, shade, shade, 1);
            #endif
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 blood = tex2D(_MainTex, IN.uv_MainTex) * _Color * IN.color;
            fixed3 normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            normal.xy *= _NormalStrength;
            o.Albedo = blood.rgb;
            o.Normal = normalize(normal);
            o.Metallic = 0;
            o.Smoothness = _Smoothness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Standard"
}
