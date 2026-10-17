using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Единый стиль интерфейса: тёмные «стальные» панели с пиксельной фаской, янтарный акцент,
    /// бирюзовые подсветки. Текстуры рисуются процедурно (9-slice), шрифт — встроенный.
    /// </summary>
    public static class UiSkin
    {
        public static readonly Color Bg = new Color32(20, 23, 27, 240);
        public static readonly Color Panel = new Color32(34, 38, 44, 245);
        public static readonly Color PanelLight = new Color32(64, 71, 80, 255);
        public static readonly Color PanelDark = new Color32(14, 16, 19, 255);
        public static readonly Color Slot = new Color32(24, 27, 32, 255);
        public static readonly Color SlotHover = new Color32(46, 52, 60, 255);
        public static readonly Color Accent = new Color32(242, 163, 58, 255);
        public static readonly Color AccentDark = new Color32(150, 92, 24, 255);
        public static readonly Color Teal = new Color32(95, 211, 196, 255);
        public static readonly Color Text = new Color32(232, 230, 222, 255);
        public static readonly Color TextDim = new Color32(150, 154, 160, 255);
        public static readonly Color Danger = new Color32(235, 92, 80, 255);
        public static readonly Color Good = new Color32(120, 214, 120, 255);

        private static bool _ready;
        private static Texture2D _panel, _slot, _slotHover, _button, _buttonHover, _buttonActive, _field, _fieldFocus, _accentButton, _accentHover, _white;

        public static GUIStyle Title, Header, Label, Small, Tiny, Center, Count, Button, AccentButton, Field, Tab, TabActive;

        public static Texture2D White => _white;
        public static Texture2D PanelTex => _panel;
        public static Texture2D SlotTex => _slot;
        public static Texture2D SlotHoverTex => _slotHover;

        public static void Ensure()
        {
            if (_ready && _panel != null) return;
            _ready = true;
            _white = Texture2D.whiteTexture;
            _panel = Bevel(Panel, PanelLight, PanelDark, PanelDark, 2);
            _slot = Bevel(Slot, PanelDark, PanelLight, new Color32(10, 11, 13, 255), 2);
            _slotHover = Bevel(SlotHover, PanelDark, new Color32(90, 98, 110, 255), new Color32(10, 11, 13, 255), 2);
            _button = Bevel(new Color32(52, 58, 66, 255), new Color32(88, 96, 108, 255), new Color32(22, 25, 29, 255), PanelDark, 2);
            _buttonHover = Bevel(new Color32(66, 74, 84, 255), new Color32(110, 120, 132, 255), new Color32(26, 29, 34, 255), PanelDark, 2);
            _buttonActive = Bevel(new Color32(40, 45, 52, 255), new Color32(22, 25, 29, 255), new Color32(80, 88, 100, 255), PanelDark, 2);
            _accentButton = Bevel(new Color32(200, 128, 38, 255), new Color32(255, 196, 110, 255), new Color32(120, 70, 16, 255), PanelDark, 2);
            _accentHover = Bevel(new Color32(226, 150, 52, 255), new Color32(255, 214, 140, 255), new Color32(140, 84, 20, 255), PanelDark, 2);
            _field = Bevel(new Color32(16, 18, 21, 255), new Color32(10, 11, 13, 255), new Color32(70, 78, 88, 255), PanelDark, 1);
            _fieldFocus = Bevel(new Color32(18, 20, 24, 255), AccentDark, Accent, PanelDark, 1);

            var font = GUI.skin.font;
            Label = Make(14, Text, TextAnchor.UpperLeft, font);
            Small = Make(12, Text, TextAnchor.UpperLeft, font);
            Tiny = Make(11, TextDim, TextAnchor.UpperLeft, font);
            Center = Make(14, Text, TextAnchor.MiddleCenter, font);
            Header = Make(17, Text, TextAnchor.UpperLeft, font);
            Header.fontStyle = FontStyle.Bold;
            Title = Make(34, Accent, TextAnchor.MiddleCenter, font);
            Title.fontStyle = FontStyle.Bold;
            Count = Make(12, Text, TextAnchor.LowerRight, font);
            Count.fontStyle = FontStyle.Bold;

            Button = ButtonStyle(_button, _buttonHover, _buttonActive, Text);
            AccentButton = ButtonStyle(_accentButton, _accentHover, _buttonActive, new Color32(30, 20, 8, 255));
            AccentButton.fontStyle = FontStyle.Bold;
            Tab = ButtonStyle(_button, _buttonHover, _buttonActive, TextDim);
            TabActive = ButtonStyle(_accentButton, _accentHover, _accentButton, new Color32(30, 20, 8, 255));

            Field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 14,
                padding = new RectOffset(8, 8, 5, 5),
                border = new RectOffset(3, 3, 3, 3),
                alignment = TextAnchor.MiddleLeft
            };
            Field.normal.background = _field;
            Field.hover.background = _field;
            Field.focused.background = _fieldFocus;
            Field.normal.textColor = Field.hover.textColor = Field.focused.textColor = Text;
        }

        private static GUIStyle Make(int size, Color color, TextAnchor anchor, Font font)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = anchor, richText = true, wordWrap = false, font = font };
            s.normal.textColor = color;
            return s;
        }

        private static GUIStyle ButtonStyle(Texture2D normal, Texture2D hover, Texture2D active, Color text)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                border = new RectOffset(4, 4, 4, 4),
                padding = new RectOffset(8, 8, 4, 4),
                alignment = TextAnchor.MiddleCenter,
                richText = true
            };
            s.normal.background = normal;
            s.hover.background = hover;
            s.active.background = active;
            s.focused.background = normal;
            s.onNormal.background = active;
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = text;
            return s;
        }

        /// <summary>9-slice текстура 12×12: заливка, светлая фаска сверху-слева, тёмная снизу-справа, контур.</summary>
        private static Texture2D Bevel(Color fill, Color light, Color dark, Color outline, int bevel)
        {
            const int S = 12;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                // y = 0 — низ текстуры
                Color c = fill;
                bool edge = x == 0 || y == 0 || x == S - 1 || y == S - 1;
                if (edge) c = outline;
                else if (x <= bevel || y >= S - 1 - bevel) c = light;
                else if (x >= S - 1 - bevel || y <= bevel) c = dark;
                // Углы контура прозрачные — «скруглённый» пиксельный угол
                if ((x == 0 || x == S - 1) && (y == 0 || y == S - 1)) c = new Color(0, 0, 0, 0);
                t.SetPixel(x, y, c);
            }
            t.Apply(false, true);
            return t;
        }

        // ================================================================ Примитивы

        public static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        public static void DrawPanel(Rect r)
        {
            Ensure();
            // Тень
            Fill(new Rect(r.x + 4, r.y + 5, r.width, r.height), new Color(0, 0, 0, 0.35f));
            GUI.Box(r, GUIContent.none, PanelBoxStyle);
        }

        private static GUIStyle _panelBox, _slotBox, _slotHoverBox;

        private static GUIStyle PanelBoxStyle
        {
            get
            {
                if (_panelBox == null || _panelBox.normal.background == null)
                {
                    _panelBox = new GUIStyle { border = new RectOffset(4, 4, 4, 4) };
                    _panelBox.normal.background = _panel;
                }
                return _panelBox;
            }
        }

        public static void DrawSlot(Rect r, bool hover)
        {
            Ensure();
            if (_slotBox == null || _slotBox.normal.background == null)
            {
                _slotBox = new GUIStyle { border = new RectOffset(4, 4, 4, 4) };
                _slotBox.normal.background = _slot;
                _slotHoverBox = new GUIStyle { border = new RectOffset(4, 4, 4, 4) };
                _slotHoverBox.normal.background = _slotHover;
            }
            GUI.Box(r, GUIContent.none, hover ? _slotHoverBox : _slotBox);
        }

        /// <summary>Заголовок панели: янтарная полоса и текст с тенью.</summary>
        public static void PanelTitle(Rect panel, string title)
        {
            Fill(new Rect(panel.x + 3, panel.y + 3, panel.width - 6, 3), Accent);
            Shadowed(new Rect(panel.x + 14, panel.y + 9, panel.width - 28, 26), title, Header);
        }

        public static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var shadow = new GUIStyle(style);
            shadow.normal.textColor = new Color(0, 0, 0, 0.7f);
            GUI.Label(new Rect(r.x + 1, r.y + 2, r.width, r.height), StripColor(text), shadow);
            GUI.Label(r, text, style);
        }

        private static string StripColor(string s)
        {
            if (s.IndexOf('<') < 0) return s;
            return System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        }

        /// <summary>Полоса заполнения с «делениями» в пиксельном стиле.</summary>
        public static void Bar(Rect r, float t, Color c)
        {
            Fill(r, PanelDark);
            Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), new Color(0.05f, 0.06f, 0.07f, 1f));
            float w = (r.width - 2) * Mathf.Clamp01(t);
            if (w <= 0) return;
            var inner = new Rect(r.x + 1, r.y + 1, w, r.height - 2);
            Fill(inner, c);
            Fill(new Rect(inner.x, inner.y, inner.width, Mathf.Max(1, inner.height * 0.35f)), Color.Lerp(c, Color.white, 0.35f));
            Fill(new Rect(inner.x, inner.yMax - 1, inner.width, 1), Color.Lerp(c, Color.black, 0.4f));
        }
    }

    /// <summary>
    /// Пиксельный шрифт 5×7 для заголовков (латиница, цифры): логотип GENESIS и крупные надписи
    /// без внешних ассетов шрифтов.
    /// </summary>
    public static class PixelFont
    {
        private static readonly string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-:!? ";

        // Каждый символ — 7 строк по 5 бит (старший бит слева)
        private static readonly byte[,] Glyphs =
        {
            { 14, 17, 17, 31, 17, 17, 17 }, { 30, 17, 17, 30, 17, 17, 30 }, { 14, 17, 16, 16, 16, 17, 14 },
            { 30, 17, 17, 17, 17, 17, 30 }, { 31, 16, 16, 30, 16, 16, 31 }, { 31, 16, 16, 30, 16, 16, 16 },
            { 14, 17, 16, 23, 17, 17, 15 }, { 17, 17, 17, 31, 17, 17, 17 }, { 14, 4, 4, 4, 4, 4, 14 },
            { 7, 2, 2, 2, 2, 18, 12 }, { 17, 18, 20, 24, 20, 18, 17 }, { 16, 16, 16, 16, 16, 16, 31 },
            { 17, 27, 21, 21, 17, 17, 17 }, { 17, 17, 25, 21, 19, 17, 17 }, { 14, 17, 17, 17, 17, 17, 14 },
            { 30, 17, 17, 30, 16, 16, 16 }, { 14, 17, 17, 17, 21, 18, 13 }, { 30, 17, 17, 30, 20, 18, 17 },
            { 15, 16, 16, 14, 1, 1, 30 }, { 31, 4, 4, 4, 4, 4, 4 }, { 17, 17, 17, 17, 17, 17, 14 },
            { 17, 17, 17, 17, 17, 10, 4 }, { 17, 17, 17, 21, 21, 21, 10 }, { 17, 17, 10, 4, 10, 17, 17 },
            { 17, 17, 10, 4, 4, 4, 4 }, { 31, 1, 2, 4, 8, 16, 31 },
            { 14, 17, 19, 21, 25, 17, 14 }, { 4, 12, 4, 4, 4, 4, 14 }, { 14, 17, 1, 2, 4, 8, 31 },
            { 30, 1, 1, 14, 1, 1, 30 }, { 2, 6, 10, 18, 31, 2, 2 }, { 31, 16, 30, 1, 1, 17, 14 },
            { 6, 8, 16, 30, 17, 17, 14 }, { 31, 1, 2, 4, 8, 8, 8 }, { 14, 17, 17, 14, 17, 17, 14 },
            { 14, 17, 17, 15, 1, 2, 12 },
            { 0, 0, 0, 0, 0, 12, 12 }, { 0, 0, 0, 31, 0, 0, 0 }, { 0, 12, 12, 0, 12, 12, 0 },
            { 4, 4, 4, 4, 4, 0, 4 }, { 14, 17, 1, 2, 4, 0, 4 }, { 0, 0, 0, 0, 0, 0, 0 }
        };

        public static float Width(string text, float px) => text.Length * 6 * px - px;

        /// <summary>Нарисовать текст: px — размер «пикселя», gradient — от верхнего цвета к нижнему, тень снизу-справа.</summary>
        public static void Draw(float x, float y, string text, float px, Color top, Color bottom, Color shadow)
        {
            text = text.ToUpperInvariant();
            for (int pass = 0; pass < 2; pass++)
            {
                float ox = pass == 0 ? px : 0, oy = pass == 0 ? px : 0;
                float cx = x;
                foreach (char ch in text)
                {
                    int gi = Chars.IndexOf(ch);
                    if (gi >= 0)
                    {
                        for (int row = 0; row < 7; row++)
                        {
                            int bits = Glyphs[gi, row];
                            var c = pass == 0 ? shadow : Color.Lerp(top, bottom, row / 6f);
                            for (int col = 0; col < 5; col++)
                                if ((bits & (16 >> col)) != 0)
                                    UiSkin.Fill(new Rect(cx + col * px + ox, y + row * px + oy, px, px), c);
                        }
                    }
                    cx += 6 * px;
                }
            }
        }
    }
}
