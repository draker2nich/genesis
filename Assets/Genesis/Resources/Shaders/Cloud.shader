// Объёмные «воксельные» облака: цвет вершины × оттенок времени суток, плавное исчезновение к краю слоя
// и смешивание с цветом горизонта вместо обычного тумана (облака выше дальности тумана).
Shader "Genesis/Cloud"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 0.9)
        _HorizonColor ("Horizon", Color) = (0.7, 0.78, 0.86, 1)
        _FadeStart ("Fade Start", Float) = 150
        _FadeEnd ("Fade End", Float) = 260
    }

    SubShader
    {
        Tags { "Queue" = "Transparent-10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color, _HorizonColor;
            float _FadeStart, _FadeEnd;

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float dist : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 ws = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.dist = length(ws.xz - _WorldSpaceCameraPos.xz);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float fade = saturate((i.dist - _FadeStart) / max(1.0, _FadeEnd - _FadeStart));
                fixed4 c = i.color * _Color;
                c.rgb = lerp(c.rgb, _HorizonColor.rgb, fade * 0.85);
                c.a *= 1.0 - fade * fade;
                return c;
            }
            ENDCG
        }
    }
}
