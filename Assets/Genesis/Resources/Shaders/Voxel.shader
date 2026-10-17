// Шейдер вокселей Genesis.
// Цвет вершины: R — свет неба (0..1), G — свет блоков, B — AO × затенение грани, A — флаги:
//   1.0 обычный, ~0.88 светящийся (факел, лампа), ~0.75 покачивается (листва, растения), ~0.5 вода.
// Свет неба умножается на время суток и тень от солнца, свет блоков — тёплый. Текстуры — Texture2DArray (слой в uv.z).
// Глобальные параметры задаёт клиент (Shader.SetGlobal*): _GenesisDaylight, _GenesisSkyLight, _GenesisBlockLight,
// _GenesisSunDir, _GenesisShadowStrength.
Shader "Genesis/Voxel"
{
    Properties
    {
        _Blocks ("Block Texture Array", 2DArray) = "" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _SrcBlend ("__src", Float) = 1
        [HideInInspector] _DstBlend ("__dst", Float) = 0
        [HideInInspector] _ZWrite ("__zw", Float) = 1
        [HideInInspector] _ZTest ("__zt", Float) = 4
    }

    // ------------------------------------------------------------------ URP
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_ARRAY(_Blocks);
        SAMPLER(sampler_Blocks);

        CBUFFER_START(UnityPerMaterial)
            float _Cutoff;
            float _SrcBlend;
            float _DstBlend;
            float _ZWrite;
            float _ZTest;
        CBUFFER_END

        float _GenesisDaylight;
        float4 _GenesisSkyLight;
        float4 _GenesisBlockLight;
        // Свет в руке (факел): xyz — точка в мире, w — уровень 0..15 (0 — нет)
        float4 _GenesisHandLight;

        // Динамический свет от факела в руке: как свет блока, падает на 1 уровень за блок
        half HandLight(float3 positionWS, float3 n)
        {
            float3 d = _GenesisHandLight.xyz - positionWS;
            float dist = length(d);
            half l = saturate((_GenesisHandLight.w - dist) / 15.0);
            return l * (0.55 + 0.45 * saturate(dot(n, d / max(dist, 1e-3))));
        }
        float4 _GenesisSunDir;
        float _GenesisShadowStrength;

        bool IsFlag(half a, half v) { return abs(a - v) < 0.06; }

        // Покачивание листвы и волны воды — одинаковые для соседних вершин (без щелей)
        float3 Animate(float3 positionWS, half flag)
        {
            float t = _Time.y;
            if (IsFlag(flag, 0.753))
            {
                float w = sin(t * 1.7 + positionWS.x * 0.6 + positionWS.z * 0.45) * 0.035;
                positionWS.x += w;
                positionWS.z += w * 0.6;
            }
            else if (IsFlag(flag, 0.502))
            {
                positionWS.y += sin(t * 1.3 + positionWS.x * 0.8 + positionWS.z * 0.6) * 0.035 - 0.04;
            }
            return positionWS;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma require 2darray
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float3 uv : TEXCOORD0;
                float3 tint : TEXCOORD1;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
                half4 color : COLOR;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                half3 tint : TEXCOORD4;
                // Флаги вычисляются в вершине: у растения низ «обычный», верх «качается», и при интерполяции
                // альфа проходила через значение «светится» — трава светилась ночью полосой.
                half2 flags : TEXCOORD5; // x — светится, y — вода
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(input.positionOS.xyz);
                ws = Animate(ws, input.color.a);
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = input.uv;
                o.color = input.color;
                o.tint = clamp(1.0 + input.tint, 0.0, 1.6); // окраска биома хранится как (цвет − 1): нет данных — без окраски
                o.flags = half2(IsFlag(input.color.a, 0.878) ? 1 : 0, IsFlag(input.color.a, 0.502) ? 1 : 0);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                bool water = i.flags.y > 0.5;
                float3 uv = i.uv;
                if (water) uv.xy += _Time.y * float2(0.035, 0.02);

                half4 albedo = SAMPLE_TEXTURE2D_ARRAY(_Blocks, sampler_Blocks, uv.xy, uv.z);
                clip(albedo.a - _Cutoff);
                albedo.rgb *= i.tint;

                half sky = i.color.r;
                float3 n = normalize(i.normalWS);
                half blk = max(i.color.g, HandLight(i.positionWS, n));
                half ao = i.color.b;
                half day = _GenesisDaylight;

                half shadow = 1;
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                    shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(i.positionWS));
                #endif
                half ndl = saturate(dot(n, _GenesisSunDir.xyz));
                // Тень только там, где есть небо, и тем сильнее, чем выше солнце
                half shade = lerp(1.0, shadow * lerp(0.82, 1.0, ndl), _GenesisShadowStrength * day * sky);

                half skyL = pow(sky, 1.6) * lerp(0.09, 1.0, day);
                half3 skyCol = skyL * _GenesisSkyLight.rgb * shade;
                half3 blkCol = pow(blk, 1.35) * _GenesisBlockLight.rgb;
                half3 light = max(skyCol, blkCol) + 0.018;
                if (i.flags.x > 0.5) light = 1.65; // светится сам — даёт bloom

                half4 c = albedo;
                c.rgb *= light * ao;

                if (water)
                {
                    float3 v = normalize(_WorldSpaceCameraPos - i.positionWS);
                    half fresnel = pow(1.0 - saturate(dot(v, n)), 3.0);
                    float3 r = reflect(-v, n);
                    half spec = pow(saturate(dot(r, _GenesisSunDir.xyz)), 64.0) * day * sky * shadow;
                    c.rgb += spec * 1.4 + fresnel * _GenesisSkyLight.rgb * skyL * 0.25;
                    c.a = lerp(0.62, 0.92, fresnel);
                }

                c.rgb = MixFog(c.rgb, i.fogFactor);
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
            #pragma require 2darray
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float3 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 ws = Animate(TransformObjectToWorld(input.positionOS.xyz), input.color.a);
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
                half a = SAMPLE_TEXTURE2D_ARRAY(_Blocks, sampler_Blocks, i.uv.xy, i.uv.z).a;
                clip(a - max(_Cutoff, 0.5));
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
            #pragma require 2darray

            struct Attributes { float4 positionOS : POSITION; float3 uv : TEXCOORD0; half4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 uv : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformWorldToHClip(Animate(TransformObjectToWorld(input.positionOS.xyz), input.color.a));
                o.uv = input.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D_ARRAY(_Blocks, sampler_Blocks, i.uv.xy, i.uv.z).a;
                clip(a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    // ------------------------------------------------------------------ Built-in (фолбэк, без теней)
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest [_ZTest]
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma require 2darray
            #include "UnityCG.cginc"

            UNITY_DECLARE_TEX2DARRAY(_Blocks);
            float _Cutoff;
            float _GenesisDaylight;
            float4 _GenesisSkyLight;
            float4 _GenesisBlockLight;
            float4 _GenesisHandLight;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float3 uv : TEXCOORD0;
                float3 tint : TEXCOORD1;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_FOG_COORDS(1)
                half3 tint : TEXCOORD2;
                half2 flags : TEXCOORD3;
                float3 wpos : TEXCOORD4;
                float3 wn : TEXCOORD5;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wn = UnityObjectToWorldNormal(v.normal);
                o.uv = v.uv;
                o.color = v.color;
                o.tint = clamp(1.0 + v.tint, 0.0, 1.6);
                o.flags = half2(abs(v.color.a - 0.878) < 0.06 ? 1 : 0, abs(v.color.a - 0.502) < 0.06 ? 1 : 0);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                bool water = i.flags.y > 0.5;
                float3 uv = i.uv;
                if (water) uv.xy += _Time.y * float2(0.035, 0.02);
                fixed4 c = UNITY_SAMPLE_TEX2DARRAY(_Blocks, uv);
                clip(c.a - _Cutoff);
                c.rgb *= i.tint;
                half skyL = pow(i.color.r, 1.6) * lerp(0.09, 1.0, _GenesisDaylight);
                float3 hd = _GenesisHandLight.xyz - i.wpos;
                float hl = length(hd);
                half hand = saturate((_GenesisHandLight.w - hl) / 15.0) * (0.55 + 0.45 * saturate(dot(normalize(i.wn), hd / max(hl, 1e-3))));
                half blk = max(i.color.g, hand);
                half3 light = max(skyL * _GenesisSkyLight.rgb, pow(blk, 1.35) * _GenesisBlockLight.rgb) + 0.018;
                if (i.flags.x > 0.5) light = 1.4;
                c.rgb *= light * i.color.b;
                if (water) c.a = 0.75;
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
