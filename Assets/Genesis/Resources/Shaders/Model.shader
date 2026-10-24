// Модели Blockbench: обычная 2D-текстура, освещение как у вокселей (свет неба × день × тень солнца, тёплый свет блоков).
// Свет точки задаёт клиент через MaterialPropertyBlock: _InstLight = (небо 0..1, блоки 0..1, вспышка урона 0..1, свечение 0..1).
// Затенение граней — по нормали (как у блоков: верх светлее, низ темнее).
Shader "Genesis/Model"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _InstLight ("Light (sky, block, flash, emissive)", Vector) = (1, 0, 0, 0)
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [HideInInspector] _ZTest ("__zt", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float _Cutoff;
            float4 _InstLight;
            float4 _Tint;
            float _ZTest;
        CBUFFER_END

        float _GenesisDaylight;
        float4 _GenesisSkyLight;
        float4 _GenesisBlockLight;
        float4 _GenesisSunDir;
        float _GenesisShadowStrength;
        float4 _GenesisHandLight;
        // Яркость из настроек: нижняя граница освещённости (ночью и в пещерах не кромешная тьма)
        float _GenesisMinLight;

        half FaceShade(float3 n)
        {
            float3 n2 = n * n;
            return n2.z * 0.82 + n2.x * 0.68 + n2.y * (n.y > 0 ? 1.0 : 0.52);
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            ZTest [_ZTest]
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = input.uv;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv) * _Tint;
                clip(albedo.a - _Cutoff);
                float3 n = normalize(i.normalWS);
                half sky = _InstLight.x, blk = _InstLight.y;
                float3 hd = _GenesisHandLight.xyz - i.positionWS;
                float hl = length(hd);
                blk = max(blk, saturate((_GenesisHandLight.w - hl) / 15.0) * (0.55 + 0.45 * saturate(dot(n, hd / max(hl, 1e-3)))));
                half day = _GenesisDaylight;

                half shadow = 1;
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(i.positionWS));
                #endif
                half ndl = saturate(dot(n, _GenesisSunDir.xyz));
                half shade = lerp(1.0, shadow * lerp(0.8, 1.0, ndl), _GenesisShadowStrength * day * sky);
                half skyL = pow(sky, 1.6) * lerp(0.09, 1.0, day);
                half3 light = max(skyL * _GenesisSkyLight.rgb * shade, pow(blk, 1.35) * _GenesisBlockLight.rgb) + 0.018;
                light = max(light, _GenesisMinLight);
                light = lerp(light, 1.65, _InstLight.w);

                half4 c = albedo;
                c.rgb *= light * FaceShade(n);
                c.rgb = lerp(c.rgb, half3(1.0, 0.25, 0.2), _InstLight.z * 0.6);
                c.rgb = MixFog(c.rgb, i.fogFactor);
                c.a = 1;
                return c;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(input.positionOS.xyz);
                float3 nws = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - ws);
                #else
                    float3 lightDir = _LightDirection;
                #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(ws, nws, lightDir));
                #if UNITY_REVERSED_Z
                    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = cs;
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    // ------------------------------------------------------------------ Built-in (фолбэк)
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "AlphaTest" }
        Pass
        {
            ZTest [_ZTest]
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Cutoff;
            float4 _InstLight;
            float4 _Tint;
            float _GenesisDaylight;
            float4 _GenesisSkyLight;
            float4 _GenesisBlockLight;
            float4 _GenesisHandLight;
            // Яркость из настроек: нижняя граница освещённости (ночью и в пещерах не кромешная тьма)
            float _GenesisMinLight;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 n : TEXCOORD1; UNITY_FOG_COORDS(2) float3 wpos : TEXCOORD3; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.n = UnityObjectToWorldNormal(v.normal);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * _Tint;
                clip(c.a - _Cutoff);
                float3 n = normalize(i.n);
                float3 n2 = n * n;
                half face = n2.z * 0.82 + n2.x * 0.68 + n2.y * (n.y > 0 ? 1.0 : 0.52);
                half skyL = pow(_InstLight.x, 1.6) * lerp(0.09, 1.0, _GenesisDaylight);
                float3 hd = _GenesisHandLight.xyz - i.wpos;
                float hl = length(hd);
                half blk = max(_InstLight.y, saturate((_GenesisHandLight.w - hl) / 15.0) * (0.55 + 0.45 * saturate(dot(n, hd / max(hl, 1e-3)))));
                half3 light = max(skyL * _GenesisSkyLight.rgb, pow(blk, 1.35) * _GenesisBlockLight.rgb) + 0.018;
                light = max(light, _GenesisMinLight);
                light = lerp(light, 1.4, _InstLight.w);
                c.rgb *= light * face;
                c.rgb = lerp(c.rgb, half3(1.0, 0.25, 0.2), _InstLight.z * 0.6);
                c.a = 1;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
