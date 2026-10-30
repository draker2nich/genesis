using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Экранные эффекты главного меню поверх живой сцены: мягкая виньетка, затенение стороны с текстом
    /// (чтобы меню читалось на любом фоне), пылинки в воздухе и подсветка пункта под курсором.
    /// Текстуры — процедурные, создаются один раз.
    /// </summary>
    public static class MenuFx
    {
        private static Texture2D _vignette, _ramp;

        private static Texture2D VignetteTex
        {
            get
            {
                if (_vignette != null) return _vignette;
                const int S = 128;
                _vignette = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S * 2f - 1f, v = ((y + 0.5f) / S * 2f - 1f) * 0.85f;
                    float d = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1.35f, d));
                    _vignette.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
                _vignette.Apply(false, true);
                return _vignette;
            }
        }

        /// <summary>Горизонтальный спад 1 → 0 слева направо (плавнее линейного).</summary>
        private static Texture2D RampTex
        {
            get
            {
                if (_ramp != null) return _ramp;
                const int W = 256;
                _ramp = new Texture2D(W, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                for (int x = 0; x < W; x++)
                {
                    float t = x / (W - 1f);
                    _ramp.SetPixel(x, 0, new Color(1f, 1f, 1f, Mathf.Pow(1f - t, 1.7f)));
                }
                _ramp.Apply(false, true);
                return _ramp;
            }
        }

        private static void Draw(Rect r, Texture2D tex, Color c, bool flip = false)
        {
            if (Event.current.type != EventType.Repaint || c.a <= 0.001f) return;
            var old = GUI.color;
            GUI.color = new Color(c.r, c.g, c.b, c.a * old.a);
            if (flip) GUI.DrawTextureWithTexCoords(r, tex, new Rect(1f, 0f, -1f, 1f));
            else GUI.DrawTexture(r, tex, ScaleMode.StretchToFill);
            GUI.color = old;
        }

        /// <summary>Тёмные края кадра.</summary>
        public static void Vignette(Rect r, float alpha) =>
            Draw(r, VignetteTex, new Color(0.01f, 0.012f, 0.02f, alpha));

        /// <summary>Затенение от левого (fromLeft) или правого края — под колонкой текста или панелью.</summary>
        public static void SideShade(Rect r, bool fromLeft, float alpha) =>
            Draw(r, RampTex, new Color(0.012f, 0.014f, 0.02f, alpha), !fromLeft);

        /// <summary>Тёплая полоса за пунктом меню под курсором.</summary>
        public static void HoverBar(Rect r, float k) =>
            Draw(r, RampTex, new Color(0.95f, 0.55f, 0.15f, 0.16f * k));

        /// <summary>
        /// Пылинки: медленно плывут, мерцают; ближние (крупные) сильнее смещаются за мышью — глубина без 3D.
        /// alpha — общая видимость (0 — нет пыли).
        /// </summary>
        public static void Dust(Rect r, float t, Vector2 parallax, float alpha)
        {
            if (Event.current.type != EventType.Repaint || alpha <= 0.01f) return;
            const int Count = 70;
            for (int i = 0; i < Count; i++)
            {
                float h1 = Hash01(i * 3 + 1), h2 = Hash01(i * 3 + 2), h3 = Hash01(i * 3 + 3);
                float depth = 0.25f + 0.75f * h3;
                float dir = i % 3 == 0 ? -1f : 1f;
                float x = Frac(h1 + t * (0.003f + 0.009f * depth) + Mathf.Sin(t * 0.21f + i) * 0.006f);
                float y = Frac(h2 + dir * t * (0.002f + 0.004f * depth) + Mathf.Sin(t * 0.33f + i * 1.7f) * 0.01f);
                float size = 1f + 2.2f * depth * depth;
                float twinkle = 0.55f + 0.45f * Mathf.Sin(t * (0.8f + h1 * 1.5f) + i);
                float a = alpha * twinkle * (0.10f + 0.28f * depth);
                a *= Mathf.SmoothStep(0f, 1f, y * 5f) * Mathf.SmoothStep(0f, 1f, (1f - y) * 5f);
                float px = r.x + x * r.width - parallax.x * 40f * depth;
                float py = r.y + y * r.height + parallax.y * 24f * depth;
                UiSkin.Fill(new Rect(px, py, size, size), new Color(1f, 0.86f, 0.64f, a));
            }
        }

        private static float Frac(float v) => v - Mathf.Floor(v);

        private static float Hash01(int n)
        {
            uint x = (uint)n * 747796405u + 2891336453u;
            x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
            x = (x >> 22) ^ x;
            return (x & 0xFFFFFF) / 16777216f;
        }
    }
}
