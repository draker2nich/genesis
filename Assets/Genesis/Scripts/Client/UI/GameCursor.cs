using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Игровой курсор: пиксельная стрелка в стиле интерфейса (тёмный контур, тёплая заливка, блик),
    /// вместо системной стрелки. Рисуется процедурно, без файлов. Если положить PNG
    /// в StreamingAssets/Game/textures/cursor.png (32×32, остриё в левом верхнем углу), возьмётся он.
    /// </summary>
    public static class GameCursor
    {
        private static Texture2D _tex;

        // Форма стрелки 12×19: '#' — контур, 'o' — заливка, '+' — блик
        private static readonly string[] Shape =
        {
            "#           ",
            "##          ",
            "#+#         ",
            "#++#        ",
            "#+oo#       ",
            "#+ooo#      ",
            "#+oooo#     ",
            "#+ooooo#    ",
            "#+oooooo#   ",
            "#+ooooooo#  ",
            "#+oooooooo# ",
            "#+ooooo#####",
            "#+oo#oo#    ",
            "#+o# #oo#   ",
            "#o#  #oo#   ",
            "##    #oo#  ",
            "#     #oo#  ",
            "       #oo# ",
            "       ###  ",
        };

        public static void Apply(string coreRoot = null)
        {
            if (_tex == null) _tex = LoadPng(coreRoot) ?? Build();
            if (_tex == null) return;
            Cursor.SetCursor(_tex, Vector2.zero, CursorMode.Auto);
        }

        private static Texture2D LoadPng(string coreRoot)
        {
            try
            {
                string root = coreRoot ?? System.IO.Path.Combine(Application.streamingAssetsPath, "Game");
                string path = System.IO.Path.Combine(root, "textures", "cursor.png");
                if (!System.IO.File.Exists(path)) return null;
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                return t.LoadImage(System.IO.File.ReadAllBytes(path)) ? t : null;
            }
            catch
            {
                return null;
            }
        }

        private static Texture2D Build()
        {
            const int size = 32;
            const int scale = 1; // аппаратный курсор масштабируется системой; 1:1 — чёткие пиксели
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = "GenesisCursor"
            };
            var px = new Color32[size * size];
            var outline = new Color32(24, 18, 14, 255);
            var fill = new Color32(236, 196, 120, 255);
            var shade = new Color32(196, 146, 82, 255);
            var light = new Color32(255, 240, 200, 255);
            var shadow = new Color32(0, 0, 0, 90);

            void Put(int x, int y, Color32 c)
            {
                for (int dy = 0; dy < scale; dy++)
                for (int dx = 0; dx < scale; dx++)
                {
                    int px0 = x * scale + dx, py0 = y * scale + dy;
                    if (px0 >= size || py0 >= size) continue;
                    // Текстура хранится снизу вверх, стрелка рисуется сверху вниз
                    px[(size - 1 - py0) * size + px0] = c;
                }
            }

            // Тень на 1 пиксель вправо-вниз
            for (int y = 0; y < Shape.Length; y++)
            for (int x = 0; x < Shape[y].Length; x++)
                if (Shape[y][x] != ' ') Put(x + 1, y + 1, shadow);

            for (int y = 0; y < Shape.Length; y++)
            for (int x = 0; x < Shape[y].Length; x++)
            {
                char ch = Shape[y][x];
                if (ch == ' ') continue;
                Color32 c = ch == '#' ? outline : ch == '+' ? light : (x + y > 14 ? shade : fill);
                Put(x, y, c);
            }
            t.SetPixels32(px);
            t.Apply(false, false);
            return t;
        }
    }
}
