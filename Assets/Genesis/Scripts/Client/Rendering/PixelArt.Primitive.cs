using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Пиксель-арт первобытной эпохи: трава, волокно, канат, галька, острие, рукоять, рубило, уголёк, крица,
    /// головы железных инструментов, мотыга, иконки забора, полублока, хвороста, меха, лежанки, справочника,
    /// каменной наковальни; блоки соломы и грядки. Строка 0 — низ текстуры.
    /// </summary>
    public static partial class PixelArt
    {
        /// <summary>Нарисовать первобытный узор; false — узор неизвестен.</summary>
        private static bool Primitive(Ctx k, string pattern)
        {
            switch (pattern)
            {
                case "straw": Straw(k); return true;
                case "farmland": Farmland(k); return true;
                case "item_grass": GrassTuft(k); break;
                case "item_fiber": Fiber(k); break;
                case "item_rope": Rope(k); break;
                case "item_pebble": PebbleItem(k); break;
                case "item_blade": Blade(k); break;
                case "item_handle": Handle(k); break;
                case "tool_chopper": Chopper(k); break;
                case "tool_hoe":
                    Clear32(k);
                    Stroke(k, 5, 5, 22, 22, k.Acc, Tone(k.Acc, 0.7f));
                    HoeHead(k, 0, true);
                    break;
                case "item_ember": Ember(k); break;
                case "item_bloom": Bloom(k); break;
                case "item_head_pickaxe": Head(k, "pickaxe"); break;
                case "item_head_axe": Head(k, "axe"); break;
                case "item_head_shovel": Head(k, "shovel"); break;
                case "item_head_hoe": Head(k, "hoe"); break;
                case "item_kindling": Kindling(k); break;
                case "item_fence": FenceIcon(k); break;
                case "item_slab": SlabIcon(k); break;
                case "item_bellows": BellowsIcon(k); break;
                case "item_bed": BedIcon(k); break;
                case "item_book": Book(k); break;
                case "item_anvil": AnvilIcon(k); break;
                default: return false;
            }
            Outline(k.Px);
            return true;
        }

        private static void Clear32(Ctx k)
        {
            for (int i = 0; i < k.Px.Length; i++) k.Px[i] = Clear;
        }

        /// <summary>Линия толщиной в пиксель с тенью справа.</summary>
        private static void Stroke(Ctx k, float x0, float y0, float x1, float y1, Color32 c, Color32 shade)
        {
            int n = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)));
            for (int i = 0; i <= n; i++)
            {
                float t = n == 0 ? 0 : (float)i / n;
                int x = Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), y = Mathf.RoundToInt(Mathf.Lerp(y0, y1, t));
                k.SetClip(x, y, c);
                k.SetClip(x + 1, y, shade);
            }
        }

        // ================================================================ Блоки

        private static void Straw(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                // Длинные соломины вдоль x, со сдвигом по строкам
                float n = Noise(k.Seed, x, y, 16);
                float strand = Mathf.Sin((y + Lattice(k.Seed, 0, y, Size) * 6f) * 1.9f);
                var c = Tone(k.Base, 0.9f + strand * 0.12f + (n - 0.5f) * 0.2f);
                if (Lattice(k.Seed + 3, x / 3, y, Size) > 0.9f) c = Tone(k.Acc, 1.0f);
                k.Set(x, y, c);
            }
        }

        private static void Farmland(Ctx k)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float n = Fbm(k.Seed, x, y);
                bool furrow = (y % 8) < 2;
                var c = Band(k.Base, n, 0.8f);
                if (furrow) c = Tone(k.Acc, 0.9f + n * 0.2f);
                k.Set(x, y, c);
            }
        }

        // ================================================================ Предметы

        private static void GrassTuft(Ctx k)
        {
            Clear32(k);
            for (int b = 0; b < 7; b++)
            {
                float bx = 9 + b * 2.3f, lean = (b - 3) * 1.6f + (k.Rand() - 0.5f) * 2f;
                float h = 16 + k.Rand() * 10;
                var c = Tone(k.Base, 0.85f + b % 3 * 0.12f);
                Stroke(k, bx, 3, bx + lean, 3 + h, c, Tone(k.Acc, 0.9f));
            }
        }

        private static void Fiber(Ctx k)
        {
            Clear32(k);
            for (int s = 0; s < 3; s++)
            for (int i = 0; i < 22; i++)
            {
                float t = i / 21f;
                float x = 5 + t * 22, y = 16 + Mathf.Sin(t * 9f + s * 2.1f) * 5f + (s - 1) * 2f;
                k.SetClip(Mathf.RoundToInt(x), Mathf.RoundToInt(y), Tone(k.Base, 0.85f + s * 0.12f));
                k.SetClip(Mathf.RoundToInt(x), Mathf.RoundToInt(y) - 1, Tone(k.Acc, 0.95f));
            }
        }

        private static void Rope(Ctx k)
        {
            Clear32(k);
            // Свёрнутый моток: кольцо, витки по кругу
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float dx = x - 15.5f, dy = y - 15.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < 6.5f || d > 12.5f) continue;
                float ang = Mathf.Atan2(dy, dx);
                float twist = Mathf.Sin(ang * 14f + d * 1.2f);
                k.SetClip(x, y, Tone(twist > 0 ? k.Base : k.Acc, 0.9f + (dy - dx) * 0.012f));
            }
        }

        private static void PebbleItem(Ctx k)
        {
            Clear32(k);
            void Stone(float cx, float cy, float rx, float ry, float shade)
            {
                for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x - cx) / rx, dy = (y - cy) / ry;
                    if (dx * dx + dy * dy > 1f) continue;
                    float light = (dy - dx) * 0.18f;
                    var c = Tone(k.Base, shade + light);
                    if (Lattice(k.Seed, x, y, Size) > 0.88f) c = Tone(k.Acc, 1f);
                    k.SetClip(x, y, c);
                }
            }
            Stone(12, 11, 7, 5.5f, 0.95f);
            Stone(21, 15, 5.5f, 4.5f, 1.05f);
            Stone(14, 20, 4.5f, 3.5f, 0.85f);
        }

        private static void Blade(Ctx k)
        {
            // Отколотое острие: вытянутый листовидный кремень по диагонали, с зазубренной кромкой
            Fill(k, (x, y) =>
            {
                float u = (x + y - 31f) / 2f, v = (x - y) / 2f; // u — вдоль диагонали (−15..15), v — поперёк
                if (u < -12f || u > 13f) return false;
                float t = (u + 12f) / 25f;
                float w = 5.2f * Mathf.Sin(Mathf.Pow(t, 0.7f) * Mathf.PI) + 0.3f;
                return Mathf.Abs(v) <= w + (Lattice(k.Seed, x, y, Size) > 0.75f ? 0.6f : 0f);
            }, (x, y) =>
            {
                float v = (x - y) / 2f;
                if (v > 1.5f && (x + y) % 3 == 0) return Tone(k.Base, 1.35f); // сколы по кромке
                return Tone(v > 0 ? k.Base : k.Acc, v > 0 ? 1.08f : 1.0f);
            });
        }

        private static void Handle(Ctx k)
        {
            Clear32(k);
            Stroke(k, 5, 5, 26, 26, k.Base, Tone(k.Base, 0.7f));
            // Обмотка канатом посередине
            for (int i = 10; i <= 20; i += 2)
            {
                k.SetClip(i - 1, i + 1, Tone(k.Acc, 1.1f));
                k.SetClip(i, i + 1, Tone(k.Acc, 1.0f));
                k.SetClip(i + 1, i - 1, Tone(k.Acc, 0.8f));
                k.SetClip(i + 2, i, Tone(k.Acc, 0.75f));
            }
        }

        private static void Chopper(Ctx k)
        {
            Clear32(k);
            var wood = new Color32(138, 106, 60, 255);
            Stroke(k, 4, 4, 20, 20, wood, Tone(wood, 0.7f));
            var rope = new Color32(196, 164, 106, 255);
            for (int i = 14; i <= 20; i += 2)
            {
                k.SetClip(i - 1, i + 1, rope);
                k.SetClip(i + 1, i - 1, Tone(rope, 0.75f));
            }
            // Каменная голова — грубый клин
            Fill2(k, (x, y) =>
            {
                float dx = x - 22.5f, dy = y - 22.5f;
                float u = (dx + dy) * 0.7071f, v = (dx - dy) * 0.7071f;
                return Mathf.Abs(u) < 3.5f && v > -6.5f && v < 7.5f - Mathf.Abs(u) * 0.3f;
            }, (x, y) =>
            {
                float v = ((x - 22.5f) - (y - 22.5f)) * 0.7071f;
                var c = Tone(k.Base, 0.9f + v * 0.04f);
                if (Lattice(k.Seed, x, y, Size) > 0.85f) c = Tone(k.Base, 1.25f);
                return c;
            });
        }

        /// <summary>Как Fill, но поверх уже нарисованного.</summary>
        private static void Fill2(Ctx k, System.Func<int, int, bool> shape, System.Func<int, int, Color32> color)
        {
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                if (shape(x, y)) k.Px[y * Size + x] = color(x, y);
        }

        private static void Ember(Ctx k)
        {
            Lump(k);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var c = k.Px[y * Size + x];
                if (c.a == 0) continue;
                float dx = x - 15.5f, dy = y - 14.5f;
                float glow = 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / 9f);
                bool crack = Noise(k.Seed + 5, x, y, 8) > 0.58f;
                k.Px[y * Size + x] = crack || glow > 0.6f
                    ? Mix(new Color32(255, 140, 40, 255), new Color32(255, 230, 150, 255), glow)
                    : Tone(k.Acc, 0.8f + glow * 0.5f);
            }
        }

        private static void Bloom(Ctx k)
        {
            Lump(k);
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var c = k.Px[y * Size + x];
                if (c.a == 0) continue;
                float n = Noise(k.Seed + 7, x, y, 8);
                if (n < 0.32f) k.Px[y * Size + x] = Tone(k.Base, 0.55f); // поры
                else if (Lattice(k.Seed + 9, x, y, Size) > 0.9f) k.Px[y * Size + x] = Tone(k.Acc, 1.1f); // шлак
            }
        }

        /// <summary>Голова инструмента без рукояти (с отверстием под палку).</summary>
        private static void Head(Ctx k, string kind)
        {
            Clear32(k);
            var head = k.Base;
            switch (kind)
            {
                case "pickaxe":
                    for (int x = 3; x <= 28; x++)
                    {
                        int y = Mathf.RoundToInt(22 - (x - 15.5f) * (x - 15.5f) / 18f);
                        k.SetClip(x, y, Tone(head, 1.3f));
                        k.SetClip(x, y - 1, Tone(head, 1.0f));
                        k.SetClip(x, y - 2, Tone(head, 0.7f));
                    }
                    break;
                case "axe":
                    for (int y = 6; y <= 26; y++)
                    for (int x = 8; x <= 25; x++)
                    {
                        if (x - 8 > (y - 6) * 0.45f + 8 || x - 8 > (26 - y) * 0.45f + 8) continue;
                        k.SetClip(x, y, x >= 23 ? Tone(head, 1.3f) : Tone(head, 0.9f + (y - 6) * 0.01f));
                    }
                    break;
                case "hoe":
                    HoeHead(k, 0, false);
                    break;
                default:
                    for (int y = -7; y <= 7; y++)
                    for (int x = -6; x <= 6; x++)
                    {
                        if (x * x / 36f + y * y / 49f > 1f) continue;
                        k.SetClip(16 + x, 14 + y, Tone(head, 1.0f + (y - x) * 0.03f));
                    }
                    break;
            }
            // Проушина
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                k.SetClip(15 + x, 17 + y, Tone(k.Acc, 0.6f));
        }

        /// <summary>Лезвие мотыги: на рукояти (onHandle) — у её конца, иначе — по центру.</summary>
        private static void HoeHead(Ctx k, int unused, bool onHandle)
        {
            var head = onHandle ? new Color32(216, 216, 216, 255) : k.Base;
            int ox = onHandle ? 20 : 8, oy = onHandle ? 20 : 10;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 10; x++)
                k.SetClip(ox + x - (onHandle ? 4 : 0), oy + y + 4, Tone(head, 1.0f + (y - x) * 0.03f));
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 3; x++)
                k.SetClip(ox + 6 + x - (onHandle ? 4 : 0), oy + y - 3, Tone(head, 0.85f + y * 0.03f));
        }

        private static void Kindling(Ctx k)
        {
            Clear32(k);
            var wood = k.Base;
            Stroke(k, 4, 8, 27, 14, wood, Tone(wood, 0.7f));
            Stroke(k, 6, 15, 26, 7, Tone(wood, 1.1f), Tone(wood, 0.75f));
            Stroke(k, 9, 5, 22, 20, Tone(wood, 0.95f), Tone(wood, 0.65f));
            Stroke(k, 20, 5, 11, 21, Tone(wood, 1.05f), Tone(wood, 0.7f));
            if (k.Acc.r > 200) // костёр: язычки пламени
                for (int i = 0; i < 40; i++)
                {
                    int x = 11 + k.RandInt(10), y = 14 + k.RandInt(12 - System.Math.Abs(x - 16));
                    k.SetClip(x, y, i % 3 == 0 ? new Color32(255, 230, 120, 255) : k.Acc);
                }
        }

        private static void FenceIcon(Ctx k)
        {
            Clear32(k);
            for (int p = 0; p < 2; p++)
            for (int y = 3; y <= 28; y++)
            for (int x = 0; x < 5; x++)
                k.SetClip(5 + p * 17 + x, y, Tone(k.Base, x == 0 ? 1.2f : (x == 4 ? 0.7f : 0.95f)));
            for (int r = 0; r < 2; r++)
            for (int x = 10; x <= 21; x++)
            for (int y = 0; y < 3; y++)
                k.SetClip(x, 9 + r * 10 + y, Tone(k.Acc, y == 2 ? 1.15f : 0.9f));
        }

        private static void SlabIcon(Ctx k)
        {
            Fill(k, (x, y) => x >= 3 && x <= 28 && y >= 6 && y <= 17, (x, y) =>
            {
                if (y == 17) return Tone(k.Base, 1.25f);
                if (y == 6) return Tone(k.Base, 0.65f);
                return (y - 7) % 5 == 0 ? Tone(k.Acc, 0.9f) : Tone(k.Base, 0.95f + (x % 7) * 0.01f);
            });
        }

        private static void BellowsIcon(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                float dx = (x - 13f) / 10f, dy = (y - 15.5f) / 9f;
                bool bag = dx * dx + dy * dy <= 1f;
                bool nozzle = x >= 22 && x <= 29 && Mathf.Abs(y - 15.5f) <= 1.5f;
                bool handle = (x >= 3 && x <= 22 && (y == 24 || y == 25 || y == 6 || y == 7));
                return bag || nozzle || handle;
            }, (x, y) =>
            {
                if (y >= 24 || y <= 7) return Tone(k.Acc, y % 2 == 0 ? 1.1f : 0.85f);
                if (x >= 22) return new Color32(110, 110, 116, 255);
                return Tone(k.Base, 0.85f + Mathf.Sin(x * 1.3f) * 0.1f);
            });
        }

        private static void BedIcon(Ctx k)
        {
            Fill(k, (x, y) => x >= 2 && x <= 29 && y >= 8 && y <= 19, (x, y) =>
            {
                var c = Tone(k.Base, 0.9f + Mathf.Sin(y * 1.8f + (x % 5)) * 0.08f);
                if (x <= 8) c = Tone(k.Base, 1.12f); // изголовье
                if (y == 8) c = Tone(k.Acc, 0.8f);
                return c;
            });
        }

        private static void Book(Ctx k)
        {
            Fill(k, (x, y) => x >= 6 && x <= 25 && y >= 4 && y <= 27, (x, y) =>
            {
                if (x >= 23) return Tone(k.Acc, 0.95f - (y % 2) * 0.08f); // страницы
                if (x == 6) return Tone(k.Base, 0.7f);
                var c = Tone(k.Base, 0.95f + (y - x) * 0.006f);
                if ((y == 20 || y == 21) && x > 9 && x < 21) c = new Color32(220, 190, 110, 255); // тиснение
                return c;
            });
        }

        private static void AnvilIcon(Ctx k)
        {
            Fill(k, (x, y) =>
            {
                bool top = y >= 18 && y <= 23 && x >= 4 && x <= 27 - (y - 18);
                bool horn = y >= 20 && y <= 22 && x >= 2 && x < 4;
                bool waist = y >= 10 && y < 18 && x >= 11 && x <= 20;
                bool foot = y >= 4 && y < 10 && x >= 7 && x <= 24;
                return top || horn || waist || foot;
            }, (x, y) => Tone(k.Base, y >= 23 ? 1.25f : (y >= 18 ? 1.0f : 0.8f)));
        }
    }
}
