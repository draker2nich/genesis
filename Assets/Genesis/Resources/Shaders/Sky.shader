// Небо: градиент зенит → горизонт, квадратные пиксельные солнце и луна, звёзды ночью.
// Рисуется на сфере вокруг камеры (Cull Front) до всей геометрии. Работает в URP и встроенном конвейере.
Shader "Genesis/Sky"
{
    Properties
    {
        _ZenithColor ("Zenith", Color) = (0.32, 0.5, 0.78, 1)
        _HorizonColor ("Horizon", Color) = (0.7, 0.78, 0.86, 1)
        _GroundColor ("Ground", Color) = (0.2, 0.22, 0.25, 1)
        _SunColor ("Sun", Color) = (1, 0.92, 0.7, 1)
        _SunDir ("Sun Direction", Vector) = (0, 1, 0, 0)
        _Night ("Night Amount", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Front
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _ZenithColor, _HorizonColor, _GroundColor, _SunColor, _SunDir;
            float _Night;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = mul((float3x3)unity_ObjectToWorld, v.vertex.xyz);
                return o;
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            // Квадратный диск: проекция направления на плоскость, перпендикулярную центру диска
            float SquareDisc(float3 d, float3 center, float size)
            {
                float facing = dot(d, center);
                if (facing <= 0) return 0;
                float3 up = abs(center.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 t1 = normalize(cross(center, up));
                float3 t2 = cross(center, t1);
                float2 p = float2(dot(d, t1), dot(d, t2)) / facing;
                return step(max(abs(p.x), abs(p.y)), size);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 sun = normalize(_SunDir.xyz);
                float h = d.y;

                float3 col = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(saturate(h), 0.45));
                col = lerp(col, _GroundColor.rgb, saturate(-h * 3.0));

                // Ореол вокруг солнца и закатное зарево у горизонта
                float sd = dot(d, sun);
                float horizonGlow = pow(1.0 - saturate(abs(h)), 6.0);
                col += _SunColor.rgb * (pow(saturate(sd), 48.0) * 0.45 + pow(saturate(sd), 5.0) * 0.18 * (0.4 + horizonGlow));

                // Солнце и луна — квадратные, в пиксельном стиле
                float sunDisc = SquareDisc(d, sun, 0.045);
                col = lerp(col, _SunColor.rgb * 3.0, sunDisc * saturate(1.0 - _Night * 0.6));
                float moonDisc = SquareDisc(d, -sun, 0.032);
                float moonShade = 0.75 + 0.25 * step(0.5, frac((d.x + d.z) * 40.0));
                col = lerp(col, float3(0.85, 0.88, 0.95) * moonShade * 1.4, moonDisc * _Night);

                // Звёзды: ячейки на сфере, редкие яркие точки, мерцание
                if (_Night > 0.01 && h > -0.05)
                {
                    float3 p = d * 260.0;
                    float3 cell = floor(p);
                    float r = Hash(cell);
                    if (r > 0.9965)
                    {
                        float3 f = frac(p) - 0.5;
                        float dist = max(abs(f.x), max(abs(f.y), abs(f.z)));
                        float twinkle = 0.65 + 0.35 * sin(_Time.y * (2.0 + r * 6.0) + r * 100.0);
                        col += step(dist, 0.22) * twinkle * _Night * saturate(h * 6.0 + 0.3) * 0.9;
                    }
                }
                return fixed4(col, 1);
            }
            ENDCG
        }
    }
}
