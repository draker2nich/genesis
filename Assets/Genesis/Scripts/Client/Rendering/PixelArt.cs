using System;
using Genesis.Core;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Процедурный пиксель-арт 32×32: ограниченная палитра из 5–6 оттенков на материал
    /// (тени холоднее, блики теплее), тайлящийся value-noise, «рукотворные» детали — камни, доски,
    /// заклёпки, контуры у предметов. Строка 0 — низ текстуры.
    /// </summary>
    public static class PixelArt
    {
        public const int Size = 32;
        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        public static Color32[] Generate(string name, TextureSpec spec)
        {
            spec = spec ?? new TextureSpec { Pattern = "solid", Color = "#ff00ff", Accent = "#000000" };
            var c = Parse(spec.Color, new Color32(128, 128, 128, 255));
            var a = Parse(spec.Accent, new Color32(64, 64, 64, 255));
            var rng = new Random64(Hash.String(name) | 1);
            var px = new Color32[Size * Size];
            var ctx = new Ctx { Px = px, Rng = rng, Base = c, Acc = a, Seed = (int)(Hash.String(name) & 0x7fffffff), Name = name };

            switch (spec.Pattern)
            {
                case "stone": Stone(ctx); break;
                case "noise": Stone(ctx, 0.6f); break;
                case "dirt": Dirt(ctx); break;
                case "sand": Sand(ctx); break;
                case "gravel": Pebbles(ctx, 14, false); break;
                case "cobble": Pebbles(ctx, 11, true); break;
                case "snow": Snow(ctx); break;
                case "clay": Clay(ctx); break;
                case "ash": Ash(ctx); break;
                case "grass_top": GrassTop(ctx); break;
                case "grass_side": GrassSide(ctx); break;
                case "ore": Ore(ctx); break;
                case "planks": Planks(ctx); break;
                case "log_side": LogSide(ctx); break;
                case "log_top": LogTop(ctx); break;
                case "leaves": Leaves(ctx); break;
                case "needles": Needles(ctx); break;
                case "birch_side": BirchSide(ctx); break;
                case "plant": Plant(ctx, false); break;
                case "fern": Plant(ctx, true); break;
                case "flower": Flower(ctx); break;
                case "dead_bush": DeadBush(ctx); break;
                case "cactus": Cactus(ctx, false); break;
                case "cactus_top": Cactus(ctx, true); break;
                case "sandstone": Sandstone(ctx); break;
                case "terracotta": Terracotta(ctx); break;
                case "ice": Ice(ctx); break;
                case "mud": Mud(ctx); break;
                case "brick": Brick(ctx); break;
                case "water": Water(ctx); break;
                case "glass": Glass(ctx); break;
                case "machine": Machine(ctx, false); break;
                case "machine_front": Machine(ctx, true); break;
                case "belt": Belt(ctx); break;
                case "pipe": Pipe(ctx, 6); break;
                case "cable": Pipe(ctx, 3); break;
                case "solar": Solar(ctx); break;
                case "battery": Battery(ctx); break;
                case "drive": Drive(ctx); break;
                case "screen": Screen(ctx); break;
                case "workbench_top": Workbench(ctx); break;
                case "lamp": Lamp(ctx); break;
                case "torch": Torch(ctx); break;
                case "destroy": Destroy(ctx); break;
                case "item_lump": Lump(ctx); Outline(px); break;
                case "item_dust": Dust(ctx); Outline(px); break;
                case "item_ingot": Ingot(ctx); Outline(px); break;
                case "item_plate": Plate(ctx); Outline(px); break;
                case "item_rod": Rod(ctx); Outline(px); break;
                case "item_gear": Gear(ctx); Outline(px); break;
                case "item_circuit": Circuit(ctx); Outline(px); break;
                case "item_cell": Cell(ctx); Outline(px); break;
                case "item_tablet": Tablet(ctx); Outline(px); break;
                case "item_berries": Berries(ctx); Outline(px); break;
                case "item_mushroom": Mushroom(ctx); Outline(px); break;
                case "item_meat": Meat(ctx); Outline(px); break;
                case "item_drumstick": Drumstick(ctx); Outline(px); break;
                case "item_egg": Egg(ctx); Outline(px); break;
                case "item_hide": Hide(ctx); Outline(px); break;
                case "item_feather": Feather(ctx); Outline(px); break;
                case "item_shard": Shard(ctx); Outline(px); break;
                case "item_gland": Gland(ctx); Outline(px); break;
                case "item_jar": Jar(ctx); Outline(px); break;
                case "tool_pickaxe":
                case "tool_axe":
                case "tool_shovel":
                case "tool_sword":
                    Tool(ctx, spec.Pattern.Substring(5));
                    Outline(px);
                    break;
                case "solid":
                    for (int i = 0; i < px.Length; i++) px[i] = ((i % Size) / 4 + (i / Size) / 4) % 2 == 0 ? c : a;
                    break;
                default:
                    Lump(ctx);
                    Outline(px);
                    break;
            }
            Stylize(px, spec.Pattern);
            return px;
        }

        /// <summary>
        /// «Рисованный» проход в духе Hytale: мягкое сглаживание шума (тайлящееся), чуть больше насыщенности
        /// и контраста; у рукотворных блоков — фаска по краю (светлый верх-лево, тёмный низ-право).
        /// Предметы, растения, прозрачные и служебные текстуры не трогаем.
        /// </summary>
        private static void Stylize(Color32[] px, string pattern)
        {
            if (pattern == null) return;
            if (pattern.StartsWith("item_", StringComparison.Ordinal) || pattern.StartsWith("tool_", StringComparison.Ordinal)) return;
            switch (pattern)
            {
                case "destroy": case "torch": case "water": case "glass": case "leaves": case "needles":
                case "plant": case "fern": case "flower": case "dead_bush": case "solid": case "screen": case "lamp":
                    return;
            }
            bool bevel;
            switch (pattern)
            {
                case "planks": case "cobble": case "brick": case "machine": case "machine_front": case "sandstone":
                case "drive": case "battery": case "workbench_top": case "log_top": case "belt": case "pipe": case "cable":
                    bevel = true;
                    break;
                default:
                    bevel = false;
                    break;
            }

            var src = (Color32[])px.Clone();
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var o = src[y * Size + x];
                if (o.a < 128) continue;
                float r = 0, g = 0, b = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var c = src[((y + dy + Size) % Size) * Size + (x + dx + Size) % Size];
                    if (c.a < 128) continue;
                    float w = (dx == 0 ? 2 : 1) * (dy == 0 ? 2 : 1);
                    r += c.r * w;
                    g += c.g * w;
                    b += c.b * w;
                    n += w;
                }
                var col = new Color(
                    Mathf.Lerp(o.r, r / n, 0.55f) / 255f,
                    Mathf.Lerp(o.g, g / n, 0.55f) / 255f,
                    Mathf.Lerp(o.b, b / n, 0.55f) / 255f);
                Color.RGBToHSV(col, out float h, out float sat, out float val);
                sat = Mathf.Clamp01(sat * 1.15f);
                val = Mathf.Clamp01(0.5f + (val - 0.5f) * 1.06f);
                col = Color.HSVToRGB(h, sat, val);
                if (bevel)
                {
                    // Строка Size-1 — верх текстуры
                    float k = 1f;
                    if (y == Size - 1 || x == 0) k = 1.10f;
                    else if (y == Size - 2 || x == 1) k = 1.04f;
                    if (y == 0 || x == Size - 1) k = 0.80f;
                    else if (y == 1 || x == Size - 2) k = 0.92f;
                    col *= k;
                }
                px[y * Size + x] = new Color32((byte)(Mathf.Clamp01(col.r) * 255), (byte)(Mathf.Clamp01(col.g) * 255), (byte)(Mathf.Clamp01(col.b) * 255), o.a);
            }
        }

        // ================================================================ Основа

        private sealed class Ctx
        {
            public Color32[] Px;
            public Random64 Rng;
            public Color32 Base, Acc;
            public int Seed;
            public string Name;

            public void Set(int x, int y, Color32 c)
            {
                x = ((x % Size) + Size) % Size;
                y = ((y % Size) + Size) % Size;
                Px[y * Size + x] = c;
            }

            public void SetClip(int x, int y, Color32 c)
            {
                if ((uint)x < Size && (uint)y < Size) Px[y * Size + x] = c;
            }

            public Color32 Get(int x, int y)
            {
                x = ((x % Size) + Size) % Size;
                y = ((y % Size) + Size) % Size;
                return Px[y * Size + x];
            }

            public float Rand() => Rng.NextFloat();
            public int RandInt(int max) => Rng.NextInt(max);
        }

        private static Color32 Parse(string hex, Color32 fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            return ColorUtility.TryParseHtmlString(hex, out var col) ? (Color32)col : fallback;
        }

        /// <summary>Оттенок палитры: k&lt;1 — тень (сдвиг в холодное), k&gt;1 — блик (сдвиг в тёплое).</summary>
        private static Color32 Tone(Color32 c, float k)
        {
            float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f;
            r *= k;
            g *= k;
            b *= k;
            if (k < 1f)
            {
                float t = (1f - k) * 0.35f;
                r = Mathf.Lerp(r, r * 0.85f, t);
                g = Mathf.Lerp(g, g * 0.92f, t);
                b = Mathf.Lerp(b, b * 1.12f + 0.02f, t);
            }
            else if (k > 1f)
            {
                float t = (k - 1f) * 0.5f;
                r = Mathf.Lerp(r, r + 0.05f, t);
                b = Mathf.Lerp(b, b * 0.92f, t);
            }
            return new Color32((byte)(Mathf.Clamp01(r) * 255), (byte)(Mathf.Clamp01(g) * 255), (byte)(Mathf.Clamp01(b) * 255), c.a);
        }

        private static Color32 Mix(Color32 a, Color32 b, float t) => Color32.Lerp(a, b, t);

        /// <summary>Тайлящийся value-noise с периодом period (делитель 32), значения 0..1.</summary>
        private static float Noise(int seed, int x, int y, int period)
        {
            int cell = Size / period;
            float fx = (float)x / cell, fy = (float)y / cell;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);
            float v00 = Lattice(seed, x0, y0, period), v10 = Lattice(seed, x0 + 1, y0, period);
            float v01 = Lattice(seed, x0, y0 + 1, period), v11 = Lattice(seed, x0 + 1, y0 + 1, period);
            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), ty);
        }

        private static float Lattice(int seed, int x, int y, int period)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            return (float)Hash.Unit(Hash.Coord(seed, x, y));
        }

        private static float Fbm(int seed, int x, int y)
        {
            return Noise(seed, x, y, 4) * 0.5f + Noise(seed + 1, x, y, 8) * 0.3f + Noise(seed + 2, x, y, 16) * 0.2f;
        }

        /// <summary>Квантование шума в оттенки палитры.</summary>
        private static Color32 Band(Color32 c, float n, float contrast)
        {
            float[] ks = { 0.68f, 0.82f, 0.94f, 1.05f, 1.17f };
            int i = Mathf.Clamp(Mathf.FloorToInt((n - 0.5f) * 5f * contrast + 2.5f), 0, 4);
            return Tone(c, ks[i]);
        }

        /// <summary>Тёмный контур вокруг непрозрачных пикселей спрайта предмета.</summary>
        private static void Outline(Color32[] px)
        {
            var copy = (Color32[])px.Clone();
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (copy[y * Size + x].a > 0) continue;
                bool edge = false;
                for (int d = 0; d < 4 && !edge; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if ((uint)nx < Size && (uint)ny < Size && copy[ny * Size + nx].a > 0) edge = true;
                }
                if (edge) px[y * Size + x] = new Color32(18, 16, 22, 255);
            }
        }

        // ================================================================ Природа

        private static void Stone(Ctx k, float contrast = 0.8f)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                k.Set(x, y, Band(k.Base, Fbm(k.Seed, x, y), contrast));
            // Трещинки
            for (int n = 0; n < 4; n++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                int len = 3 + k.RandInt(5);
                for (int i = 0; i < len; i++)
                {
                    k.Set(x, y, Tone(k.Base, 0.6f));
                    k.Set(x, y + 1, Tone(k.Base, 1.1f));
                    x += k.RandInt(3) - 1;
                    y += k.Rand() < 0.5f ? 1 : 0;
                }
            }
            for (int n = 0; n < 10; n++) k.Set(k.RandInt(Size), k.RandInt(Size), Tone(k.Base, 1.22f));
        }

        private static void Dirt(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                k.Set(x, y, Band(k.Base, Fbm(k.Seed, x, y), 1.0f));
            for (int n = 0; n < 9; n++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                var pebble = Tone(k.Acc, 1.15f + k.Rand() * 0.2f);
                k.Set(x, y, pebble);
                k.Set(x + 1, y, pebble);
                k.Set(x, y - 1, Tone(k.Base, 0.62f));
                k.Set(x + 1, y - 1, Tone(k.Base, 0.62f));
            }
            for (int n = 0; n < 14; n++) k.Set(k.RandInt(Size), k.RandInt(Size), Tone(k.Base, 0.66f));
        }

        private static void Sand(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float ripple = Mathf.Sin((y + Noise(k.Seed, x, y, 4) * 6f) * 0.9f) * 0.12f;
                k.Set(x, y, Band(k.Base, 0.5f + ripple + (k.Rand() - 0.5f) * 0.18f, 0.9f));
            }
            for (int n = 0; n < 12; n++) k.Set(k.RandInt(Size), k.RandInt(Size), Tone(k.Acc, 0.9f));
        }

        private static void Snow(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float n = Fbm(k.Seed, x, y);
                k.Set(x, y, n < 0.42f ? Tone(k.Acc, 1.0f) : (n > 0.62f ? Tone(k.Base, 1.04f) : k.Base));
            }
            for (int n = 0; n < 8; n++) k.Set(k.RandInt(Size), k.RandInt(Size), new Color32(255, 255, 255, 255));
        }

        private static void Clay(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float layer = Noise(k.Seed, x / 4, y, 8) * 0.6f + Noise(k.Seed + 3, x, y, 4) * 0.4f;
                k.Set(x, y, Band(k.Base, layer, 0.7f));
            }
        }

        private static void Ash(Ctx k)
        {
            Stone(k, 1.0f);
            for (int n = 0; n < 4; n++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                k.Set(x, y, new Color32(230, 110, 40, 255));
                k.Set(x, y + 1, new Color32(120, 60, 30, 255));
            }
        }

        private static void GrassTop(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                k.Set(x, y, Band(k.Base, Fbm(k.Seed, x, y), 0.9f));
            // Травинки: короткие вертикальные штрихи светлее и темнее
            for (int n = 0; n < 70; n++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                bool light = k.Rand() < 0.55f;
                k.Set(x, y, Tone(k.Base, light ? 1.18f : 0.74f));
                k.Set(x, y + 1, Tone(k.Base, light ? 1.08f : 0.84f));
            }
            for (int n = 0; n < 6; n++) k.Set(k.RandInt(Size), k.RandInt(Size), Tone(k.Acc, 1.2f));
        }

        private static void GrassSide(Ctx k)
        {
            // Низ — земля (Base), верх — трава (Accent) с неровным краем
            var dirt = new Ctx { Px = k.Px, Rng = k.Rng, Base = k.Base, Acc = Tone(k.Base, 1.2f), Seed = k.Seed };
            Dirt(dirt);
            for (int x = 0; x < Size; x++)
            {
                int depth = 4 + (int)(Noise(k.Seed + 9, x, 0, 8) * 4f) + (k.Rand() < 0.25f ? 2 : 0);
                for (int i = 0; i < depth; i++)
                {
                    int y = Size - 1 - i;
                    float n = Noise(k.Seed + 5, x, y, 8);
                    k.Set(x, y, i == depth - 1 ? Tone(k.Acc, 0.7f) : Band(k.Acc, n, 0.9f));
                }
                k.Set(x, Size - 1 - depth, Tone(k.Base, 0.7f));
            }
        }

        private static void Ore(Ctx k)
        {
            Stone(k, 0.7f);
            int clusters = 5 + k.RandInt(3);
            for (int n = 0; n < clusters; n++)
            {
                int cx = 3 + k.RandInt(Size - 6), cy = 3 + k.RandInt(Size - 6);
                int pixels = 7 + k.RandInt(5);
                int x = cx, y = cy;
                // Сначала тень-контур, потом тело жилы, потом блики — чтобы контур не перекрывал тело
                var cells = new System.Collections.Generic.List<Vector2Int>();
                for (int i = 0; i < pixels; i++)
                {
                    cells.Add(new Vector2Int(x, y));
                    x += k.RandInt(3) - 1;
                    y += k.RandInt(3) - 1;
                }
                foreach (var p in cells)
                {
                    k.Set(p.x + 1, p.y - 1, Tone(k.Acc, 0.45f));
                    k.Set(p.x - 1, p.y, Tone(k.Base, 0.6f));
                }
                foreach (var p in cells) k.Set(p.x, p.y, Tone(k.Acc, 0.95f + k.Rand() * 0.1f));
                foreach (var p in cells)
                    if (k.Rand() < 0.3f) k.Set(p.x, p.y, Tone(k.Acc, 1.3f));
                k.Set(cx, cy + 1, Tone(k.Acc, 1.45f));
            }
        }

        private static void Pebbles(Ctx k, int count, bool mortar)
        {
            // Диаграмма Вороного: каждый пиксель принадлежит ближайшему «камню», граница темнее
            var pts = new Vector2[count];
            var tone = new float[count];
            for (int i = 0; i < count; i++)
            {
                pts[i] = new Vector2(k.Rand() * Size, k.Rand() * Size);
                tone[i] = 0.82f + k.Rand() * 0.3f;
            }
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float best = float.MaxValue, second = float.MaxValue;
                int bi = 0;
                for (int i = 0; i < count; i++)
                {
                    float dx = Mathf.Abs(x - pts[i].x), dy = Mathf.Abs(y - pts[i].y);
                    dx = Mathf.Min(dx, Size - dx);
                    dy = Mathf.Min(dy, Size - dy);
                    float d = dx * dx + dy * dy;
                    if (d < best) { second = best; best = d; bi = i; }
                    else if (d < second) second = d;
                }
                float edge = Mathf.Sqrt(second) - Mathf.Sqrt(best);
                Color32 c;
                if (edge < (mortar ? 1.4f : 0.9f)) c = mortar ? Tone(k.Acc, 0.9f) : Tone(k.Base, 0.6f);
                else
                {
                    // Блик к верхнему-левому краю камня
                    float lightDir = (y - pts[bi].y) - (x - pts[bi].x);
                    c = Tone(k.Base, tone[bi] + Mathf.Clamp(lightDir * 0.012f, -0.1f, 0.1f));
                }
                k.Set(x, y, c);
            }
        }

        private static void Leaves(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float n = Noise(k.Seed, x, y, 8) * 0.6f + Noise(k.Seed + 1, x, y, 16) * 0.4f;
                float r = k.Rand();
                Color32 c;
                if (n < 0.33f && r < 0.6f) c = Clear;
                else if (r < 0.12f) c = Tone(k.Acc, 0.85f);
                else c = Band(k.Base, n + (r - 0.5f) * 0.3f, 1.1f);
                k.Set(x, y, c);
            }
            for (int n = 0; n < 6; n++) k.Set(k.RandInt(Size), k.RandInt(Size), Tone(k.Acc, 1.3f));
        }

        private static void Needles(Ctx k)
        {
            // Хвоя: мелкие диагональные «иголки» поверх тёмной основы, редкие просветы
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float n = Noise(k.Seed, x, y, 8);
                k.Set(x, y, n < 0.26f && k.Rand() < 0.5f ? Clear : Band(k.Base, n * 0.6f + 0.2f, 1.0f));
            }
            for (int i = 0; i < 90; i++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                bool light = k.Rand() < 0.5f;
                var c = Tone(light ? k.Acc : k.Base, light ? 1.05f : 0.7f);
                k.Set(x, y, c);
                k.Set(x + 1, y + 1, c);
            }
        }

        private static void BirchSide(Ctx k)
        {
            // Белая кора с чёрными горизонтальными «чечевичками»
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                k.Set(x, y, Tone(k.Base, 0.93f + Noise(k.Seed, x, y * 2, 8) * 0.12f));
            for (int n = 0; n < 9; n++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                int len = 2 + k.RandInt(6);
                for (int i = 0; i < len; i++)
                {
                    k.Set(x + i, y, Tone(k.Acc, 0.9f + k.Rand() * 0.2f));
                    if (k.Rand() < 0.4f) k.Set(x + i, y - 1, Tone(k.Base, 0.75f));
                }
            }
            for (int x = 0; x < Size; x += 8) for (int y = 0; y < Size; y++)
                if (Noise(k.Seed + 4, x, y, 8) > 0.7f) k.Set(x, y, Tone(k.Base, 0.8f));
        }

        private static void Plant(Ctx k, bool fern)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            int blades = fern ? 5 : 9;
            for (int b = 0; b < blades; b++)
            {
                float x = 4 + k.Rand() * 24, lean = (k.Rand() - 0.5f) * (fern ? 1.2f : 0.6f);
                int h = fern ? 16 + k.RandInt(12) : 12 + k.RandInt(18);
                for (int y = 0; y < h; y++)
                {
                    int px = Mathf.RoundToInt(x + lean * y * y / h);
                    var c = Tone(y > h * 0.7f ? k.Acc : k.Base, 0.75f + 0.45f * y / h);
                    k.SetClip(px, y, c);
                    if (y < h / 3) k.SetClip(px + 1, y, Tone(k.Base, 0.7f));
                    if (fern && y > 3 && y % 3 == 0)
                    {
                        int w = Mathf.Max(1, (h - y) / 4);
                        for (int j = 1; j <= w; j++)
                        {
                            k.SetClip(px - j, y + j / 2, Tone(k.Base, 0.95f));
                            k.SetClip(px + j, y + j / 2, Tone(k.Acc, 0.95f));
                        }
                    }
                }
            }
        }

        private static void Flower(Ctx k)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            var stem = new Color32(70, 130, 50, 255);
            // Два-три цветка разной высоты
            int count = 2 + k.RandInt(2);
            for (int f = 0; f < count; f++)
            {
                int x = 7 + f * 8 + k.RandInt(4), h = 12 + k.RandInt(10);
                for (int y = 0; y < h; y++) k.SetClip(x, y, Tone(stem, 0.8f + 0.3f * y / h));
                k.SetClip(x - 1, h / 3, Tone(stem, 1.1f));
                k.SetClip(x - 2, h / 3 + 1, Tone(stem, 1.1f));
                // Венчик: 4 лепестка вокруг серединки
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int d = Mathf.Abs(dx) + Mathf.Abs(dy);
                    if (d > 3 || (Mathf.Abs(dx) == 2 && Mathf.Abs(dy) == 2)) continue;
                    var c = Tone(k.Base, 1.15f - d * 0.12f + (dy > 0 ? 0.08f : -0.04f));
                    if (dx == 0 && dy == 0) c = k.Acc;
                    k.SetClip(x + dx, h + 1 + dy, c);
                }
            }
        }

        private static void DeadBush(Ctx k)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            void Branch(float x, float y, float ang, int len, int depth)
            {
                for (int i = 0; i < len; i++)
                {
                    x += Mathf.Cos(ang);
                    y += Mathf.Sin(ang);
                    k.SetClip(Mathf.RoundToInt(x), Mathf.RoundToInt(y), Tone(k.Base, 0.8f + i * 0.03f));
                }
                if (depth <= 0) return;
                Branch(x, y, ang + 0.5f + k.Rand() * 0.3f, len * 2 / 3, depth - 1);
                Branch(x, y, ang - 0.5f - k.Rand() * 0.3f, len * 2 / 3, depth - 1);
            }
            Branch(15.5f, 0, Mathf.PI / 2, 8, 3);
        }

        private static void Cactus(Ctx k, bool top)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                Color32 c;
                if (top)
                {
                    float dx = x - 15.5f, dy = y - 15.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    c = Tone(k.Base, d < 4 ? 1.15f : (((int)(Mathf.Atan2(dy, dx) * 4f) & 1) == 0 ? 1.0f : 0.85f));
                }
                else
                {
                    // Вертикальные рёбра
                    int rib = x % 8;
                    c = Tone(k.Base, rib == 0 ? 0.72f : (rib == 1 || rib == 7 ? 0.88f : 1.0f + (rib == 4 ? 0.12f : 0f)));
                    if (rib == 4 && y % 6 == 2) c = k.Acc; // колючки
                }
                k.Set(x, y, c);
            }
        }

        private static void Sandstone(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float layer = Mathf.Sin(y * 0.8f + Noise(k.Seed, x, y, 4) * 2f) * 0.5f + 0.5f;
                var c = Band(k.Base, 0.35f + layer * 0.3f + (k.Rand() - 0.5f) * 0.12f, 0.8f);
                if (y % 11 == 0) c = Tone(k.Acc, 0.9f);
                k.Set(x, y, c);
            }
        }

        private static void Terracotta(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                k.Set(x, y, Band(k.Base, 0.5f + (Noise(k.Seed, x, y, 8) - 0.5f) * 0.35f + (k.Rand() - 0.5f) * 0.08f, 0.6f));
            for (int n = 0; n < 6; n++) k.Set(k.RandInt(Size), k.RandInt(Size), Tone(k.Acc, 1.0f));
        }

        private static void Ice(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                k.Set(x, y, Tone(k.Base, 0.92f + Noise(k.Seed, x, y, 4) * 0.14f));
            // Трещины и блики
            for (int n = 0; n < 5; n++)
            {
                int x = k.RandInt(Size), y = k.RandInt(Size);
                for (int i = 0; i < 8; i++)
                {
                    k.Set(x, y, Tone(k.Acc, 1.0f));
                    x += k.RandInt(3) - 1;
                    y += 1;
                }
            }
            for (int i = 0; i < 6; i++) k.Set(5 + i, 24 - i, new Color32(255, 255, 255, 255));
        }

        private static void Mud(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float n = Fbm(k.Seed, x, y);
                k.Set(x, y, n > 0.66f ? Tone(k.Acc, 1.1f) : Band(k.Base, n, 0.8f));
            }
        }

        private static void Water(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float wave = Mathf.Sin((x + Noise(k.Seed, x, y, 4) * 8f) * 0.4f + y * 0.6f);
                var c = wave > 0.75f ? Tone(k.Acc, 1.15f) : Band(k.Base, Fbm(k.Seed, x, y), 0.5f);
                c.a = 200;
                k.Set(x, y, c);
            }
        }

        private static void Glass(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool frame = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
                var c = frame ? k.Acc : new Color32(k.Base.r, k.Base.g, k.Base.b, 50);
                if (!frame && (x - y == 8 || x - y == 10 || x - y == -14)) c = new Color32(255, 255, 255, 120);
                k.Set(x, y, c);
            }
        }

        // ================================================================ Дерево и кладка

        private static void Planks(Ctx k)
        {
            for (int board = 0; board < 4; board++)
            {
                float boardTone = 0.9f + k.Rand() * 0.18f;
                int joint = k.RandInt(Size);
                for (int yy = 0; yy < 8; yy++)
                {
                    int y = board * 8 + yy;
                    for (int x = 0; x < Size; x++)
                    {
                        float grain = Noise(k.Seed + board, x, yy * 4, 8);
                        var c = Tone(k.Base, boardTone + (grain - 0.5f) * 0.16f);
                        if (yy == 0) c = Tone(k.Acc, 0.75f);                      // шов снизу
                        else if (yy == 7) c = Tone(k.Base, boardTone * 1.08f);    // светлая кромка
                        else if (x == joint) c = Tone(k.Acc, 0.8f);               // стык досок
                        else if (((x + board * 5) % 11 == 0 && yy % 3 == 1)) c = Tone(k.Base, boardTone * 0.82f);
                        k.Set(x, y, c);
                    }
                }
                k.Set(joint + 2, board * 8 + 4, Tone(k.Acc, 0.55f)); // гвоздь
            }
        }

        private static void LogSide(Ctx k)
        {
            for (int x = 0; x < Size; x++)
            {
                // Кора: полосы разной яркости, глубокие тёмные борозды, светлые гребни рядом с ними
                float colTone = 0.78f + Noise(k.Seed, x, 0, 16) * 0.2f + (x % 4 == 1 ? 0.12f : 0f);
                bool crack = Lattice(k.Seed + 7, x, 0, Size) > 0.62f;
                bool ridge = Lattice(k.Seed + 7, x - 1, 0, Size) > 0.62f;
                for (int y = 0; y < Size; y++)
                {
                    float n = Noise(k.Seed + 1, x * 3, y, 8);
                    var c = Tone(k.Base, colTone + (n - 0.5f) * 0.25f);
                    if (crack && Noise(k.Seed + 2, x, y, 4) > 0.3f) c = Tone(k.Acc, 0.62f);
                    else if (ridge) c = Tone(k.Base, colTone + 0.18f);
                    k.Set(x, y, c);
                }
            }
            // Пара сучков
            for (int n = 0; n < 2; n++)
            {
                int x = 4 + k.RandInt(Size - 8), y = 4 + k.RandInt(Size - 8);
                k.Set(x, y, Tone(k.Acc, 0.55f));
                k.Set(x + 1, y, Tone(k.Acc, 0.55f));
                k.Set(x, y + 1, Tone(k.Base, 1.2f));
                k.Set(x + 1, y - 1, Tone(k.Base, 0.7f));
            }
        }

        private static void LogTop(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = x - 15.5f, dy = y - 15.5f;
                // «Квадратные» кольца для пиксельного стиля
                float d = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) * 0.7f + Mathf.Sqrt(dx * dx + dy * dy) * 0.3f;
                Color32 c;
                if (d > 14.2f) c = Tone(k.Acc, 0.95f);
                else if (d > 13.2f) c = Tone(k.Acc, 0.7f);
                else c = ((int)d) % 3 == 0 ? Tone(k.Base, 0.8f) : Tone(k.Base, 1.0f + Noise(k.Seed, x, y, 8) * 0.08f);
                k.Set(x, y, c);
            }
        }

        private static void Brick(Ctx k)
        {
            for (int row = 0; row < 4; row++)
            {
                int off = row % 2 == 0 ? 0 : 8;
                for (int b = 0; b < 2; b++)
                {
                    float tone = 0.86f + k.Rand() * 0.24f;
                    for (int yy = 0; yy < 8; yy++)
                    for (int xx = 0; xx < 16; xx++)
                    {
                        int x = b * 16 + xx + off, y = row * 8 + yy;
                        Color32 c;
                        if (yy == 0 || xx == 0) c = Tone(k.Acc, 1.0f + (k.Rand() - 0.5f) * 0.1f);
                        else if (yy == 7) c = Tone(k.Base, tone * 1.1f);
                        else if (xx == 15) c = Tone(k.Base, tone * 0.8f);
                        else c = Tone(k.Base, tone + (Noise(k.Seed + row, x, y, 8) - 0.5f) * 0.14f);
                        k.Set(x, y, c);
                    }
                    if (k.Rand() < 0.5f) k.Set(b * 16 + off + 3 + k.RandInt(10), row * 8 + 2 + k.RandInt(4), Tone(k.Acc, 0.8f)); // скол
                }
            }
        }

        // ================================================================ Техника

        /// <summary>Стальная панель: фаска, швы, заклёпки; лицевая сторона — окно с подсветкой.</summary>
        private static void Machine(Ctx k, bool front)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float grad = 0.94f + (y / (float)Size) * 0.1f;
                var c = Tone(k.Base, grad + (Noise(k.Seed, x, y, 16) - 0.5f) * 0.05f);
                if (x < 2 || y > Size - 3) c = Tone(k.Base, 1.2f);
                if (x > Size - 3 || y < 2) c = Tone(k.Base, 0.62f);
                if (x == 0 || y == 0 || x == Size - 1 || y == Size - 1) c = Tone(k.Base, 0.42f);
                k.Set(x, y, c);
            }
            // Заклёпки
            int[] r = { 4, Size - 5 };
            foreach (int rx in r)
            foreach (int ry in r)
            {
                k.Set(rx, ry, Tone(k.Base, 1.35f));
                k.Set(rx + 1, ry - 1, Tone(k.Base, 0.6f));
            }
            if (!front)
            {
                // Шов посередине и вентиляционные прорези
                for (int x = 3; x < Size - 3; x++) k.Set(x, 15, Tone(k.Base, 0.75f));
                for (int i = 0; i < 4; i++)
                for (int x = 10; x < 22; x++) k.Set(x, 20 + i * 2, Tone(k.Acc, 0.6f));
                return;
            }
            // Окно 18×14 с рамкой и светящимся содержимым
            for (int y = 7; y < 23; y++)
            for (int x = 7; x < 25; x++)
            {
                bool frame = x == 7 || y == 7 || x == 24 || y == 22;
                Color32 c;
                if (frame) c = Tone(k.Base, 0.45f);
                else
                {
                    float t = (y - 8) / 14f;
                    c = Mix(Tone(k.Acc, 0.7f), Tone(k.Acc, 1.25f), t);
                    if ((y + x) % 7 == 0) c = Tone(k.Acc, 1.35f);
                    if (y % 3 == 0) c = Tone(c, 0.9f); // сканлайны
                }
                k.Set(x, y, c);
            }
            for (int x = 8; x < 24; x++) k.Set(x, 21, Tone(k.Acc, 1.45f)); // блик сверху окна
        }

        private static void Belt(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool rail = x < 3 || x > Size - 4;
                Color32 c;
                if (rail) c = Tone(k.Acc, x == 1 || x == Size - 2 ? 1.1f : 0.75f);
                else
                {
                    c = Tone(k.Base, 0.95f + Noise(k.Seed, x, y, 16) * 0.1f);
                    if (y % 4 == 0) c = Tone(k.Base, 0.75f); // рёбра ленты
                    int cx = Mathf.Abs(x - 15) + (y % 16);
                    if (cx >= 9 && cx <= 10 && x > 4 && x < Size - 5) c = Tone(k.Acc, 1.15f); // шеврон
                }
                k.Set(x, y, c);
            }
        }

        /// <summary>Труба/кабель крестом (прозрачный фон для блока-«каркаса»), объёмное затенение.</summary>
        private static void Pipe(Ctx k, int half)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            int lo = 16 - half, hi = 16 + half;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool vert = x >= lo && x < hi, horz = y >= lo && y < hi;
                if (!vert && !horz) continue;
                float t = vert && !horz ? (x - lo) / (float)(hi - lo) : (y - lo) / (float)(hi - lo);
                if (vert && horz) t = 0.5f;
                float shade = 0.75f + Mathf.Sin(t * Mathf.PI) * 0.4f;
                k.Set(x, y, Tone(k.Base, shade));
            }
            // Фланцы у краёв
            for (int i = lo - 1; i <= hi; i++)
            {
                k.Set(i, 2, Tone(k.Acc, 1.0f));
                k.Set(i, Size - 3, Tone(k.Acc, 1.0f));
                k.Set(2, i, Tone(k.Acc, 1.0f));
                k.Set(Size - 3, i, Tone(k.Acc, 1.0f));
            }
        }

        private static void Solar(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool grid = x % 8 == 0 || y % 8 == 0;
                var c = grid ? Tone(k.Acc, 0.9f) : Tone(k.Base, 0.9f + (y % 8) / 8f * 0.3f);
                if (!grid && (x % 8) == (y % 8)) c = Tone(k.Base, 1.45f);
                k.Set(x, y, c);
            }
        }

        private static void Battery(Ctx k)
        {
            Machine(k, false);
            for (int i = 0; i < 5; i++)
            for (int y = 6 + i * 4; y < 9 + i * 4; y++)
            for (int x = 11; x < 21; x++)
                k.Set(x, y, i < 3 ? Tone(k.Acc, 1.0f + i * 0.1f) : new Color32(25, 30, 25, 255));
        }

        private static void Drive(Ctx k)
        {
            Machine(k, false);
            for (int row = 0; row < 4; row++)
            for (int col = 0; col < 2; col++)
            {
                int x0 = 5 + col * 12, y0 = 4 + row * 6;
                for (int y = y0; y < y0 + 4; y++)
                for (int x = x0; x < x0 + 10; x++)
                    k.Set(x, y, y == y0 + 3 ? Tone(k.Base, 0.4f) : new Color32(20, 20, 26, 255));
                k.Set(x0 + 8, y0 + 1, (row + col) % 3 == 0 ? Tone(k.Acc, 1.3f) : new Color32(90, 220, 120, 255));
            }
        }

        private static void Screen(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                bool frame = x < 3 || y < 3 || x > Size - 4 || y > Size - 4;
                Color32 c = frame ? new Color32(60, 62, 72, 255) : Tone(k.Base, 1.0f);
                if (!frame && y % 4 == 1 && x > 4 && x < 6 + (int)(Lattice(k.Seed, y, 0, Size) * 20)) c = Tone(k.Acc, 1.0f);
                if (!frame && y % 2 == 0) c = Tone(c, 0.85f);
                k.Set(x, y, c);
            }
        }

        private static void Workbench(Ctx k)
        {
            Planks(k);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (x == 0 || y == 0 || x == Size - 1 || y == Size - 1) k.Set(x, y, Tone(k.Acc, 0.8f));
                if (x == 16 || y == 16) k.Set(x, y, Tone(k.Acc, 0.9f));
            }
            // Силуэты инструментов на столешнице
            for (int i = 0; i < 9; i++) k.Set(5 + i, 22 + i / 3, new Color32(150, 150, 160, 255));
            for (int i = 0; i < 6; i++) k.Set(22, 6 + i, new Color32(120, 90, 60, 255));
            for (int i = 0; i < 5; i++) k.Set(20 + i, 12, new Color32(150, 150, 160, 255));
        }

        private static void Lamp(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = x - 15.5f, dy = y - 15.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                bool frame = x < 3 || y < 3 || x > Size - 4 || y > Size - 4 || x == 15 || x == 16 || y == 15 || y == 16;
                k.Set(x, y, frame ? Tone(k.Acc, x < 2 || y > Size - 3 ? 1.2f : 0.8f) : Tone(k.Base, 1.15f - d * 0.015f));
            }
        }

        private static void Torch(Ctx k)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            // Ручка: x 14..17, y 0..15; пламя: y 16..19 (совпадает с UV модели факела)
            for (int y = 0; y < 16; y++)
            for (int x = 14; x < 18; x++)
                k.Set(x, y, Tone(k.Base, x == 14 ? 1.2f : (x == 17 ? 0.7f : 1.0f)));
            var flame = new[] { new Color32(255, 248, 200, 255), new Color32(255, 210, 90, 255), new Color32(255, 150, 40, 255), new Color32(220, 80, 20, 255) };
            for (int y = 16; y < 20; y++)
            for (int x = 14; x < 18; x++)
            {
                bool core = (x == 15 || x == 16) && y > 16;
                k.Set(x, y, core ? flame[0] : flame[Mathf.Clamp(19 - y, 1, 3)]);
            }
        }

        /// <summary>Трещины ломания: стадия из последней цифры имени (0..5), прозрачный фон.</summary>
        private static void Destroy(Ctx k)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            int stage = k.Name[k.Name.Length - 1] - '0';
            var rng = new Random64(0xC4AC5);
            var dark = new Color32(20, 18, 16, 210);
            var light = new Color32(255, 255, 255, 60);
            int lines = 2 + stage * 3;
            for (int n = 0; n < lines; n++)
            {
                int x = 16, y = 16;
                if (n >= 3) { x = rng.NextInt(Size); y = rng.NextInt(Size); }
                int dirX = rng.NextInt(3) - 1, dirY = rng.NextInt(3) - 1;
                if (dirX == 0 && dirY == 0) dirX = 1;
                int len = 4 + stage * 2 + rng.NextInt(4);
                for (int i = 0; i < len; i++)
                {
                    k.SetClip(x, y, dark);
                    k.SetClip(x + 1, y + 1, light);
                    x += dirX + (rng.NextInt(5) == 0 ? rng.NextInt(3) - 1 : 0);
                    y += dirY + (rng.NextInt(5) == 0 ? rng.NextInt(3) - 1 : 0);
                }
            }
        }

        // ================================================================ Предметы

        private static void Fill(Ctx k, Func<int, int, bool> shape, Func<int, int, Color32> color)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (shape(x, y)) k.Px[y * Size + x] = color(x, y);
        }

        private static void Lump(Ctx k)
        {
            var bumps = new float[8];
            for (int i = 0; i < bumps.Length; i++) bumps[i] = 9f + k.Rand() * 3.5f;
            Fill(k, (x, y) =>
            {
                float dx = x - 15.5f, dy = (y - 14.5f) * 1.15f;
                float ang = (Mathf.Atan2(dy, dx) + Mathf.PI) / (2 * Mathf.PI) * bumps.Length;
                int i0 = (int)ang % bumps.Length;
                float r = Mathf.Lerp(bumps[i0], bumps[(i0 + 1) % bumps.Length], ang - Mathf.Floor(ang));
                return dx * dx + dy * dy < r * r;
            }, (x, y) =>
            {
                float light = ((y - 14.5f) - (x - 15.5f)) / 24f;
                var c = Tone(k.Base, 0.95f + light * 0.45f);
                if (Lattice(k.Seed, x, y, Size) > 0.86f) c = Tone(k.Acc, 1.1f);
                return c;
            });
        }

        private static void Dust(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                float dx = (x - 15.5f) / 12f, dy = (y - 8f) / 11f;
                return dy >= 0 && dx * dx + dy < 1f - Lattice(k.Seed, x, y, Size) * 0.08f;
            }, (x, y) =>
            {
                float n = Lattice(k.Seed + 1, x, y, Size);
                var c = Tone(k.Base, 0.85f + (y - 8) / 24f + (n - 0.5f) * 0.2f);
                if (n > 0.9f) c = Tone(k.Acc, 1.0f);
                return c;
            });
        }

        private static void Ingot(Ctx k)
        {
            // Слиток в лёгкой изометрии: верхняя грань светлее, торец темнее
            Fill(k, (x, y) => y >= 9 && y <= 21 && x >= 4 + (21 - y) / 3 && x <= 27 - (y - 9) / 3, (x, y) =>
            {
                if (y >= 17) return Tone(k.Base, 1.25f - (21 - y) * 0.03f);
                if (x > 23 - (y - 9) / 3) return Tone(k.Base, 0.7f);
                return Tone(k.Base, 0.95f + (y - 9) * 0.01f);
            });
            for (int x = 10; x < 20; x++) k.SetClip(x, 20, Tone(k.Acc, 1.0f));
        }

        private static void Plate(Ctx k)
        {
            Fill(k, (x, y) => x >= 5 && x <= 26 && y >= 7 && y <= 24, (x, y) =>
            {
                if (x == 5 || y == 24) return Tone(k.Base, 1.25f);
                if (x == 26 || y == 7) return Tone(k.Base, 0.65f);
                float t = ((y - 7) - (x - 5)) / 40f;
                return Tone(k.Base, 0.95f + t * 0.2f);
            });
            k.SetClip(8, 21, Tone(k.Acc, 0.6f));
            k.SetClip(23, 10, Tone(k.Acc, 0.6f));
        }

        private static void Rod(Ctx k)
        {
            Fill(k, (x, y) => Mathf.Abs((x - 4) - (y - 4)) <= 1 && x >= 4 && x <= 27 && y >= 4 && y <= 27, (x, y) =>
            {
                int t = (x - 4) - (y - 4);
                return Tone(k.Base, t < 0 ? 1.25f : (t == 0 ? 1.0f : 0.7f));
            });
        }

        private static void Gear(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                float dx = x - 15.5f, dy = y - 15.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float ang = Mathf.Atan2(dy, dx);
                float outer = 10.5f + (Mathf.Cos(ang * 8f) > 0.25f ? 3f : 0f);
                return d < outer && d > 3.5f;
            }, (x, y) =>
            {
                float dx = x - 15.5f, dy = y - 15.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                var c = Tone(k.Base, 0.9f + (dy - dx) / 60f);
                if (d < 5.5f) c = Tone(k.Base, 0.65f);
                if (d > 7.5f && d < 8.5f) c = Tone(k.Acc, 0.9f);
                return c;
            });
        }

        private static void Circuit(Ctx k)
        {
            Fill(k, (x, y) => x >= 4 && x <= 27 && y >= 6 && y <= 25, (x, y) =>
            {
                bool chip = x >= 12 && x < 20 && y >= 12 && y < 20;
                bool pin = (x == 11 || x == 20) && y >= 13 && y < 19 && y % 2 == 1;
                bool trace = (y % 5 == 0 && x > 5 && x < 26) || (x % 7 == 3 && y > 7 && y < 24);
                if (chip) return x == 12 || y == 19 ? new Color32(60, 60, 70, 255) : new Color32(28, 28, 34, 255);
                if (pin) return new Color32(210, 210, 220, 255);
                if (trace) return Tone(k.Acc, 1.0f);
                return Tone(k.Base, 0.9f + ((x + y) % 2) * 0.06f);
            });
        }

        private static void Cell(Ctx k)
        {
            Fill(k, (x, y) => x >= 7 && x <= 24 && y >= 4 && y <= 27 && !((x == 7 || x == 24) && (y == 4 || y == 27)), (x, y) =>
            {
                if (y >= 22) return Tone(k.Acc, y == 26 ? 1.3f : 1.0f);          // цветная крышка
                if (x == 7 || y == 4) return Tone(k.Base, 0.7f);
                if (x == 24) return Tone(k.Base, 0.6f);
                if (y >= 9 && y <= 17 && x >= 11 && x <= 20) return y % 3 == 0 ? Tone(k.Acc, 0.8f) : new Color32(30, 30, 40, 255);
                return Tone(k.Base, 1.0f + (y - 4) / 80f);
            });
            k.SetClip(20, 7, new Color32(110, 240, 140, 255));
        }

        private static void Tablet(Ctx k)
        {
            Fill(k, (x, y) => x >= 6 && x <= 25 && y >= 3 && y <= 28, (x, y) =>
            {
                bool screen = x >= 8 && x <= 23 && y >= 8 && y <= 26;
                if (screen) return y % 4 == 2 && x < 12 + (y * 7) % 10 ? Tone(k.Acc, 1.2f) : new Color32(18, 24, 30, 255);
                if (y <= 6 && (x == 14 || x == 17)) return Tone(k.Acc, 1.0f);
                return Tone(k.Base, x == 6 || y == 28 ? 1.4f : 1.0f);
            });
        }

        private static void Berries(Ctx k)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            int[] bx = { 11, 20, 15, 22, 10, 16 }, by = { 13, 14, 20, 22, 21, 9 };
            for (int b = 0; b < bx.Length; b++)
            for (int y = -4; y <= 4; y++)
            for (int x = -4; x <= 4; x++)
            {
                if (x * x + y * y > 15) continue;
                var c = Tone(k.Base, 0.9f + (y - x) * 0.04f);
                if (x == -1 && y == 1) c = new Color32(255, 220, 220, 255);
                k.SetClip(bx[b] + x, by[b] + y, c);
            }
            for (int i = 0; i < 5; i++) k.SetClip(15 + i, 26 + i / 2, Tone(k.Acc, 1.0f));
        }

        private static void Meat(Ctx k)
        {
            // Кусок мяса: овал с прожилками жира и косточкой
            Fill(k, (x, y) =>
            {
                float dx = (x - 14.5f) / 11f, dy = (y - 15f) / 8.5f;
                bool body = dx * dx + dy * dy <= 1f;
                bool bone = y >= 13 && y <= 17 && x >= 24 && x <= 29;
                return body || bone;
            }, (x, y) =>
            {
                if (x >= 24) return (x == 28 || x == 29) && (y == 13 || y == 17) ? new Color32(250, 246, 230, 255) : new Color32(232, 224, 200, 255);
                float light = ((y - 15f) - (x - 14.5f) * 0.5f) / 20f;
                var c = Tone(k.Base, 1f + light * 0.5f);
                if ((x * 3 + y * 7) % 13 == 0 || (y == 15 && x % 4 != 0)) c = Tone(k.Acc, 1.1f); // жир
                return c;
            });
        }

        private static void Drumstick(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                float dx = (x - 13f) / 8f, dy = (y - 17f) / 9f;
                bool meat = dx * dx + dy * dy <= 1f;
                bool bone = Mathf.Abs((x - 13) - (13 - y) * -0.9f) < 2f && y < 10 && y > 2;
                bool knob = (x - 21) * (x - 21) + (y - 4) * (y - 4) <= 6;
                return meat || bone || knob;
            }, (x, y) =>
            {
                float dx = (x - 13f) / 8f, dy = (y - 17f) / 9f;
                if (dx * dx + dy * dy > 1f) return new Color32(236, 228, 206, 255);
                return Tone(k.Base, 0.9f + (y - x) * 0.02f);
            });
        }

        private static void Egg(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                float dx = (x - 15.5f) / 8.5f, dy = (y - 14f) / (y > 14 ? 12f : 10f);
                return dx * dx + dy * dy <= 1f;
            }, (x, y) =>
            {
                var c = Tone(k.Base, 0.88f + (y - x) * 0.012f);
                if (Lattice(k.Seed, x, y, Size) > 0.88f) c = Tone(k.Acc, 1f); // крапинки
                if (x == 11 && y > 16 && y < 21) c = new Color32(255, 255, 250, 255);
                return c;
            });
        }

        private static void Hide(Ctx k)
        {
            // Растянутая шкура неправильной формы
            Fill(k, (x, y) =>
            {
                float dx = (x - 15.5f) / 12f, dy = (y - 15.5f) / 11f;
                float wob = Mathf.Sin(x * 0.9f) * 0.08f + Mathf.Cos(y * 0.8f) * 0.08f;
                return dx * dx + dy * dy <= 0.85f + wob || (Mathf.Abs(x - 15.5f) > 9 && Mathf.Abs(y - 15.5f) > 7 && Mathf.Abs(x - 15.5f) < 14 && Mathf.Abs(y - 15.5f) < 12);
            }, (x, y) =>
            {
                float n = Noise(k.Seed, x, y, 8);
                return n > 0.62f ? Tone(k.Acc, 1.0f) : Tone(k.Base, 0.85f + n * 0.3f);
            });
        }

        private static void Feather(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                // Перо по диагонали
                float u = (x + y) / 2f, v = (x - y) / 2f;
                float len = 13f, w = 4.2f * Mathf.Sin(Mathf.Clamp01((u - 3f) / len) * Mathf.PI);
                return u > 2 && u < 29 && Mathf.Abs(v) <= Mathf.Max(0.6f, w);
            }, (x, y) =>
            {
                float v = (x - y) / 2f;
                if (Mathf.Abs(v) < 0.6f) return Tone(k.Acc, 1.1f);
                return Tone(k.Base, v > 0 ? 1.08f : 0.86f);
            });
        }

        private static void Shard(Ctx k)
        {
            // Светящийся осколок (тлеющий уголёк)
            Fill(k, (x, y) =>
            {
                float dx = Mathf.Abs(x - 15.5f), dy = y - 4f;
                return dy >= 0 && dy <= 24 && dx <= (dy < 12 ? dy * 0.5f : (24 - dy) * 0.5f) + 0.5f;
            }, (x, y) =>
            {
                float dx = Mathf.Abs(x - 15.5f);
                if (dx < 1.2f) return new Color32(255, 240, 180, 255);
                return x < 15.5f ? Tone(k.Base, 1.15f) : Tone(k.Acc, 1.0f);
            });
        }

        private static void Gland(Ctx k)
        {
            // Мешочек с жидкостью: блик и прожилки
            Fill(k, (x, y) =>
            {
                float dx = (x - 15.5f) / 10f, dy = (y - 13f) / 10f;
                bool sac = dx * dx + dy * dy <= 1f;
                bool tip = Mathf.Abs(x - 15.5f) < 2.5f && y >= 22 && y <= 26;
                return sac || tip;
            }, (x, y) =>
            {
                var c = Tone(k.Base, 0.85f + (y - x + 4) * 0.015f);
                if ((x + y * 2) % 9 == 0) c = Tone(k.Acc, 1f);
                if ((x - 11) * (x - 11) + (y - 17) * (y - 17) < 4) c = new Color32(255, 255, 240, 255);
                return c;
            });
        }

        private static void Jar(Ctx k)
        {
            Fill(k, (x, y) => x >= 8 && x <= 23 && y >= 3 && y <= 24 || (x >= 10 && x <= 21 && y >= 24 && y <= 28), (x, y) =>
            {
                if (y >= 24) return new Color32(150, 110, 70, 255); // крышка
                bool glass = x == 8 || x == 23 || y == 3;
                if (glass) return new Color32(200, 220, 230, 255);
                var c = Tone(k.Base, 0.95f + (y - 12) * 0.015f);
                if (x == 10 && y > 6 && y < 20) c = new Color32(255, 255, 220, 255);
                return c;
            });
        }

        private static void Mushroom(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                bool stem = x >= 13 && x <= 18 && y >= 3 && y <= 15;
                float dx = (x - 15.5f) / 11.5f, dy = (y - 14f) / 10f;
                bool cap = y >= 14 && dx * dx + dy * dy <= 1f;
                return stem || cap;
            }, (x, y) =>
            {
                if (y < 15) return Tone(k.Acc, x == 13 ? 1.1f : (x == 18 ? 0.75f : 0.95f));
                var c = Tone(k.Base, 0.9f + (y - 14) * 0.025f);
                if ((x * 7 + y * 3) % 11 == 0) c = new Color32(240, 230, 210, 255);
                return c;
            });
        }

        /// <summary>Инструмент: рукоять по диагонали, голова по типу, металл с бликом.</summary>
        private static void Tool(Ctx k, string kind)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
            var handle = k.Acc;
            var head = k.Base;

            void Line(int x0, int y0, int x1, int y1, Color32 c, Color32 shade)
            {
                int n = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0));
                for (int i = 0; i <= n; i++)
                {
                    float t = n == 0 ? 0 : (float)i / n;
                    int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
                    k.SetClip(x, y, c);
                    k.SetClip(x + 1, y, shade);
                }
            }

            if (kind == "sword")
            {
                Line(5, 5, 9, 9, handle, Tone(handle, 0.7f));
                Line(6, 12, 12, 6, Tone(head, 0.7f), Tone(head, 0.55f));
                for (int i = 0; i < 16; i++)
                {
                    int x = 10 + i, y = 10 + i;
                    k.SetClip(x, y, Tone(head, 1.3f));
                    k.SetClip(x + 1, y, Tone(head, 1.0f));
                    k.SetClip(x + 1, y - 1, Tone(head, 0.75f));
                }
                return;
            }
            Line(5, 5, 21, 21, handle, Tone(handle, 0.7f));
            switch (kind)
            {
                case "pickaxe":
                    for (int x = 8; x <= 28; x++)
                    {
                        int y = Mathf.RoundToInt(27 - (x - 18) * (x - 18) / 16f);
                        k.SetClip(x, y, Tone(head, 1.3f));
                        k.SetClip(x, y - 1, Tone(head, 1.0f));
                        k.SetClip(x, y - 2, Tone(head, 0.7f));
                    }
                    break;
                case "axe":
                    for (int y = 15; y <= 29; y++)
                    for (int x = 19; x <= 29; x++)
                    {
                        if (x - 19 > (y - 15) * 0.9f + 3) continue;
                        k.SetClip(x, y, x >= 27 ? Tone(head, 1.3f) : Tone(head, 0.95f - (29 - y) * 0.01f));
                    }
                    break;
                default:
                    for (int y = -5; y <= 5; y++)
                    for (int x = -4; x <= 4; x++)
                    {
                        if (x * x / 16f + y * y / 30f > 1f) continue;
                        k.SetClip(24 + x, 24 + y, Tone(head, 1.0f + (y - x) * 0.04f));
                    }
                    break;
            }
        }
    }
}
