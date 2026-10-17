using System.Collections.Generic;
using System.IO;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Собирает Texture2DArray для всех блоков/предметов.
    /// Если в игре или моде есть PNG — берётся он, иначе текстура рисуется процедурно (PixelArt) по описанию из textures.json.
    /// Так игра выглядит цельно без единого файла арта, а художник может заменять текстуры по одной.
    /// Размер слоя — 32×32 или 64×64: если среди PNG есть 64×64, весь массив становится 64 (процедурные
    /// текстуры и PNG других размеров масштабируются без сглаживания).
    /// </summary>
    public sealed class BlockTextureFactory
    {
        /// <summary>Сторона слоя текстуры: 32 или 64.</summary>
        public static int Size { get; private set; } = PixelArt.Size;
        public const int MaxSize = 64;

        public Texture2DArray Array { get; private set; }
        /// <summary>Иконки для UI (те же пиксели, отдельные Texture2D).</summary>
        public Texture2D[] Icons { get; private set; }
        /// <summary>Средний цвет каждой текстуры — для частиц.</summary>
        public Color32[] AverageColors { get; private set; }
        /// <summary>Пиксели каждого слоя (для объёмных спрайтов предмета в руке).</summary>
        public Color32[][] Pixels { get; private set; }

        private IReadOnlyList<Genesis.Modding.ModInfo> _mods;

        /// <param name="mods">Порядок загрузки модов: PNG ищется в обратном порядке, поздние моды переопределяют ранние.</param>
        public void Build(ContentDatabase content, string modsRoot, IReadOnlyList<Genesis.Modding.ModInfo> mods = null)
        {
            _mods = mods;
            // Служебные текстуры: стадии трещин при ломании блока
            for (int i = 0; i < 6; i++) content.EnsureTexture("genesis:destroy_" + i);

            int count = content.TextureNames.Count;
            // Сначала PNG: по ним выбираем размер массива
            var png = new Color32[count][];
            var pngSize = new int[count];
            int size = PixelArt.Size;
            for (int i = 0; i < count; i++)
            {
                png[i] = FindPng(content.TextureNames[i], modsRoot, out pngSize[i]);
                if (png[i] != null && pngSize[i] > size) size = Mathf.Min(MaxSize, pngSize[i]);
            }
            Size = size;

            Array = new Texture2DArray(Size, Size, count, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                anisoLevel = 0,
                name = "GenesisBlocks"
            };
            Icons = new Texture2D[count];
            AverageColors = new Color32[count];
            Pixels = new Color32[count][];

            for (int i = 0; i < count; i++)
            {
                string name = content.TextureNames[i];
                var pixels = png[i] != null
                    ? Rescale(png[i], pngSize[i], Size)
                    : Rescale(PixelArt.Generate(name, content.TextureSpecFor(name)), PixelArt.Size, Size);
                Array.SetPixels32(pixels, i, 0);
                WriteMips(Array, i, pixels, Size);
                AverageColors[i] = Average(pixels);
                Pixels[i] = pixels;

                var icon = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = name
                };
                icon.SetPixels32(pixels);
                icon.Apply(false, true);
                Icons[i] = icon;
            }
            Array.Apply(false, true); // мип-уровни уже записаны вручную (WriteMips)
        }

        /// <summary>
        /// Мип-уровни вручную. Стандартные мипы Unity усредняют цвет вместе с прозрачными пикселями
        /// (листва темнеет) и «растворяют» вырезанную листву/траву вдали: альфа падает ниже порога,
        /// сквозь кроны просвечивает небо, и при движении это выглядит как мерцающие блёстки.
        /// Здесь цвет усредняется с весом альфы, а альфа масштабируется так, чтобы доля непрозрачного
        /// на каждом уровне совпадала с исходной текстурой (сохранение покрытия).
        /// </summary>
        private static void WriteMips(Texture2DArray array, int layer, Color32[] src, int size)
        {
            const byte cut = 128;
            int opaque = 0, holes = 0, soft = 0;
            foreach (var c in src)
            {
                if (c.a >= cut) opaque++;
                if (c.a < 8) holes++;
                else if (c.a < 248) soft++;
            }
            float coverage = opaque / (float)src.Length;
            // Только «вырезанные» текстуры (листва, трава): альфа 0 или 255. Стекло с полупрозрачностью не трогаем.
            bool cutout = holes > 0 && soft == 0;

            var prev = src;
            int n = size;
            for (int level = 1; level < array.mipmapCount; level++)
            {
                int m = Mathf.Max(1, n / 2);
                var mip = new Color32[m * m];
                var alpha = new float[m * m];
                for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0, w = 0;
                    for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Mathf.Min(n - 1, x * 2 + dx), sy = Mathf.Min(n - 1, y * 2 + dy);
                        var c = prev[sy * n + sx];
                        float wa = c.a / 255f + 0.0001f;
                        r += c.r * wa; g += c.g * wa; b += c.b * wa;
                        a += c.a;
                        w += wa;
                    }
                    mip[y * m + x] = new Color32((byte)(r / w), (byte)(g / w), (byte)(b / w), 0);
                    alpha[y * m + x] = a / 4f;
                }

                // Подбираем множитель альфы, чтобы покрытие совпало с исходным
                float scale = 1f;
                if (cutout && coverage > 0f)
                {
                    float lo = 0f, hi = 8f;
                    for (int it = 0; it < 14; it++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        int cnt = 0;
                        foreach (var av in alpha) if (av * mid >= cut) cnt++;
                        if (cnt / (float)alpha.Length < coverage) lo = mid; else hi = mid;
                    }
                    scale = hi;
                }
                for (int k = 0; k < mip.Length; k++)
                {
                    var c = mip[k];
                    c.a = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha[k] * scale), 0, 255);
                    mip[k] = c;
                }
                array.SetPixels32(mip, layer, level);
                prev = mip;
                n = m;
            }
        }

        private static Color32 Average(Color32[] px)
        {
            long r = 0, g = 0, b = 0, n = 0;
            foreach (var c in px)
            {
                if (c.a < 128) continue;
                r += c.r;
                g += c.g;
                b += c.b;
                n++;
            }
            if (n == 0) return new Color32(128, 128, 128, 255);
            return new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
        }

        /// <summary>
        /// PNG текстуры: &lt;мод&gt;/textures/&lt;ns&gt;/&lt;имя&gt;.png (переопределение из любого мода)
        /// или &lt;мод ns&gt;/textures/&lt;имя&gt;.png.
        /// </summary>
        /// <summary>Масштаб без сглаживания (пиксель-арт остаётся чётким).</summary>
        private static Color32[] Rescale(Color32[] px, int from, int to)
        {
            if (from == to) return px;
            var r = new Color32[to * to];
            for (int y = 0; y < to; y++)
            for (int x = 0; x < to; x++)
                r[y * to + x] = px[(y * from / to) * from + x * from / to];
            return r;
        }

        private Color32[] FindPng(string name, string modsRoot, out int size)
        {
            size = 0;
            int colon = name.IndexOf(':');
            if (colon <= 0) return null;
            string ns = name.Substring(0, colon), file = name.Substring(colon + 1) + ".png";
            if (_mods != null)
            {
                for (int i = _mods.Count - 1; i >= 0; i--)
                {
                    var px = LoadPng(Path.Combine(_mods[i].Folder, "textures", ns, file), out size);
                    if (px != null) return px;
                    if (_mods[i].Id == ns && (px = LoadPng(Path.Combine(_mods[i].Folder, "textures", file), out size)) != null) return px;
                }
                return null;
            }
            return modsRoot == null ? null : LoadPng(Path.Combine(modsRoot, "textures", file), out size);
        }

        private static Color32[] LoadPng(string path, out int size)
        {
            size = 0;
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            bool ok = tex.LoadImage(File.ReadAllBytes(path));
            int w = tex.width;
            if (!ok || w != tex.height || (w != 16 && w != 32 && w != 64 && w != 128))
            {
                Debug.LogWarning($"Texture {path} must be a square PNG 16, 32, 64 or 128 px");
                Object.Destroy(tex);
                return null;
            }
            var px = tex.GetPixels32();
            Object.Destroy(tex);
            if (w > MaxSize)
            {
                px = Rescale(px, w, MaxSize);
                w = MaxSize;
            }
            size = w;
            return px;
        }
    }
}
