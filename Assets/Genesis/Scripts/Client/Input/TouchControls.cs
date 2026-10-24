using System.Collections.Generic;
using Genesis.Client.UI;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Сенсорное управление (телефоны и планшеты — обязательное требование Poki):
    /// <list type="bullet">
    /// <item>левая часть экрана — плавающий джойстик ходьбы (до края — бег);</item>
    /// <item>правая часть — взгляд перетаскиванием; короткий тап — «использовать/поставить», удержание — ломать/бить;</item>
    /// <item>кнопки: прыжок, присесть/вниз, пауза, инвентарь, справочник, карта; тап по хотбару — выбор слота.</item>
    /// </list>
    /// Включается само на мобильных и при первом касании экрана. В открытых окнах касания работают как мышь
    /// (Unity сама превращает их в клики интерфейса).
    /// </summary>
    public static class TouchControls
    {
        public static bool Enabled { get; private set; }

        /// <summary>Хотбар в координатах GUI (задаёт GameHud каждый кадр) — тап по нему выбирает слот.</summary>
        public static Rect HotbarRect;
        public static int HotbarSlots = 9;

        /// <summary>Открыто окно/пауза: касания идут в интерфейс, а не в мир (задаёт GameHud каждый кадр).</summary>
        public static bool Suspended;

        private enum Role { None, Stick, Look, Button, Hotbar }

        private sealed class Finger
        {
            public Role Role;
            public int Button;
            public Vector2 Start;     // GUI
            public Vector2 Last;      // GUI
            public float StartTime;
            public float Travel;
            public bool Holding;
        }

        private static readonly Dictionary<int, Finger> Fingers = new Dictionary<int, Finger>();
        private static Vector2 _stickCenter, _stickPos;
        private static bool _stickActive;
        private static float _lookX, _lookY;
        private static bool _tap, _hold, _jump, _sneak;
        private static bool _pause, _inventory, _recipes, _map;
        private static int _hotbar = -1;
        private static int _lastFrame = -1;

        private const float StickRadius = 64f;
        private const float TapTime = 0.25f, HoldTime = 0.35f, TapSlop = 14f;

        // Кнопки (координаты GUI считаются от текущего размера экрана)
        private const int BtnJump = 0, BtnSneak = 1, BtnPause = 2, BtnInv = 3, BtnRecipes = 4, BtnMap = 5;

        private static Rect ButtonRect(int b)
        {
            float w = UiScale.W, h = UiScale.H;
            float big = 84f, small = 52f, m = 14f;
            switch (b)
            {
                case BtnJump: return new Rect(w - big - m - 6, h - big - 96, big, big);
                case BtnSneak: return new Rect(w - big * 2 - m * 2 - 6, h - big * 0.8f - 96, big * 0.8f, big * 0.8f);
                case BtnPause: return new Rect(m, m, small, small);
                case BtnInv: return new Rect(m + (small + 8), m, small, small);
                case BtnRecipes: return new Rect(m + (small + 8) * 2, m, small, small);
                case BtnMap: return new Rect(m + (small + 8) * 3, m, small, small);
            }
            return Rect.zero;
        }

        private const int ButtonCount = 6;

        /// <summary>Включить на сенсорных устройствах (вызывать каждый кадр — дёшево).</summary>
        public static void Detect()
        {
            if (!Enabled && (Application.isMobilePlatform || Input.touchCount > 0)) Enabled = true;
            // Гибридный ноутбук: начали играть на клавиатуре — прячем экранные кнопки
            else if (Enabled && !Application.isMobilePlatform &&
                     (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.D)))
                Enabled = false;
        }

        private static Vector2 ToGui(Vector2 screen) => new Vector2(screen.x / UiScale.S, (Screen.height - screen.y) / UiScale.S);

        /// <summary>Разобрать касания (один раз за кадр).</summary>
        private static void Poll()
        {
            if (_lastFrame == Time.frameCount) return;
            _lastFrame = Time.frameCount;
            _lookX = _lookY = 0;
            _tap = _pause = _inventory = _recipes = _map = false;
            _hotbar = -1;
            _jump = _sneak = false;
            if (!Enabled) return;

            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                var p = ToGui(t.position);
                if (t.phase == TouchPhase.Began)
                {
                    var f = new Finger { Start = p, Last = p, StartTime = Time.unscaledTime };
                    int b = Suspended ? -1 : HitButton(p);
                    if (Suspended) f.Role = Role.None; // палец, начавший касание в окне, миру не достаётся до отпускания
                    else if (b >= 0)
                    {
                        f.Role = Role.Button;
                        f.Button = b;
                        if (b == BtnPause) _pause = true;
                        else if (b == BtnInv) _inventory = true;
                        else if (b == BtnRecipes) _recipes = true;
                        else if (b == BtnMap) _map = true;
                    }
                    else if (HotbarRect.width > 0 && HotbarRect.Contains(p))
                    {
                        f.Role = Role.Hotbar;
                        int slot = Mathf.FloorToInt((p.x - HotbarRect.x) / (HotbarRect.width / HotbarSlots));
                        if (slot >= 0 && slot < HotbarSlots) _hotbar = slot;
                    }
                    else if (p.x < UiScale.W * 0.42f && !_stickActive)
                    {
                        f.Role = Role.Stick;
                        _stickActive = true;
                        _stickCenter = p;
                        _stickPos = p;
                    }
                    else f.Role = Role.Look;
                    Fingers[t.fingerId] = f;
                    continue;
                }
                if (!Fingers.TryGetValue(t.fingerId, out var fin)) continue;

                var delta = p - fin.Last;
                fin.Travel += delta.magnitude;
                fin.Last = p;
                switch (fin.Role)
                {
                    case Role.Stick:
                        _stickPos = p;
                        break;
                    case Role.Look:
                        // Градусы на пиксель — независимо от разрешения: полный экран по высоте ≈ 120°
                        float k = 120f / Mathf.Max(200f, UiScale.H) * InputReader.MouseSensitivity / 2.2f;
                        _lookX += delta.x * k;
                        _lookY -= delta.y * k;
                        if (!fin.Holding && fin.Travel < TapSlop && Time.unscaledTime - fin.StartTime > HoldTime) fin.Holding = true;
                        break;
                    case Role.Button:
                        if (fin.Button == BtnJump) _jump = true;
                        if (fin.Button == BtnSneak) _sneak = true;
                        break;
                }

                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    if (fin.Role == Role.Stick) _stickActive = false;
                    if (fin.Role == Role.Look && !fin.Holding && fin.Travel < TapSlop && Time.unscaledTime - fin.StartTime < TapTime && t.phase == TouchPhase.Ended)
                        _tap = true;
                    Fingers.Remove(t.fingerId);
                }
            }

            _hold = false;
            foreach (var f in Fingers.Values)
            {
                if (f.Role == Role.Look && f.Holding) _hold = true;
                if (f.Role == Role.Button && f.Button == BtnJump) _jump = true;
                if (f.Role == Role.Button && f.Button == BtnSneak) _sneak = true;
            }
            if (Input.touchCount == 0)
            {
                Fingers.Clear();
                _stickActive = false;
            }
        }

        private static int HitButton(Vector2 p)
        {
            for (int b = 0; b < ButtonCount; b++)
            {
                var r = ButtonRect(b);
                r.xMin -= 6; r.yMin -= 6; r.xMax += 6; r.yMax += 6; // палец толще курсора
                if (r.Contains(p)) return b;
            }
            return -1;
        }

        private static bool _wasHold;

        /// <summary>Подмешать касания в снимок ввода (мышиные клики от касаний при этом не учитываются).</summary>
        public static void Apply(ref InputSnapshot s)
        {
            if (!Enabled) return;
            Poll();
            if (_stickActive)
            {
                var d = (_stickPos - _stickCenter) / StickRadius;
                if (d.magnitude > 1f) d.Normalize();
                s.Strafe = Mathf.Abs(d.x) > 0.15f ? d.x : 0f;
                s.Forward = Mathf.Abs(d.y) > 0.15f ? -d.y : 0f;
                s.Sprint |= d.magnitude > 0.95f;
            }
            s.LookX = _lookX;
            s.LookY = _lookY;
            s.Attack = _hold;
            s.AttackDown = _hold && !_wasHold;
            _wasHold = _hold;
            s.Use = _tap;
            s.UseDown = _tap;
            s.Jump |= _jump;
            s.Sneak |= _sneak;
            s.Descend |= _sneak;
            s.Escape |= _pause;
            s.Inventory |= _inventory;
            s.Recipes |= _recipes;
            s.Map |= _map;
            if (_hotbar >= 0) s.HotbarKey = _hotbar;
            s.Scroll = 0;
        }

        // ------------------------------------------------------------ Отрисовка

        private static Texture2D _circle;

        private static Texture2D Circle
        {
            get
            {
                if (_circle != null) return _circle;
                const int n = 64;
                _circle = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - n / 2f + 0.5f, dy = y - n / 2f + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / (n / 2f);
                    byte a = (byte)(Mathf.Clamp01((1f - d) * 12f) * 255);
                    px[y * n + x] = new Color32(255, 255, 255, a);
                }
                _circle.SetPixels32(px);
                _circle.Apply();
                return _circle;
            }
        }

        /// <summary>Экранные кнопки и джойстик (только в мире без открытых окон).</summary>
        public static void Draw(Localization loc)
        {
            if (!Enabled) return;
            var old = GUI.color;
            if (_stickActive)
            {
                GUI.color = new Color(1, 1, 1, 0.18f);
                GUI.DrawTexture(new Rect(_stickCenter.x - StickRadius, _stickCenter.y - StickRadius, StickRadius * 2, StickRadius * 2), Circle);
                var d = Vector2.ClampMagnitude(_stickPos - _stickCenter, StickRadius);
                GUI.color = new Color(1, 1, 1, 0.45f);
                GUI.DrawTexture(new Rect(_stickCenter.x + d.x - 26, _stickCenter.y + d.y - 26, 52, 52), Circle);
            }
            else
            {
                // Подсказка, где джойстик
                GUI.color = new Color(1, 1, 1, 0.08f);
                float r = StickRadius;
                GUI.DrawTexture(new Rect(70, UiScale.H - 110 - r * 2, r * 2, r * 2), Circle);
            }

            string[] labels =
            {
                loc.Get("touch.jump"), loc.Get("touch.sneak"), "II", loc.Get("touch.inventory"), loc.Get("touch.recipes"), loc.Get("touch.map")
            };
            var style = new GUIStyle(UiSkin.Small) { alignment = TextAnchor.MiddleCenter, wordWrap = true, fontSize = 12 };
            for (int b = 0; b < ButtonCount; b++)
            {
                var r = ButtonRect(b);
                bool pressed = (b == BtnJump && _jump) || (b == BtnSneak && _sneak);
                GUI.color = new Color(0.08f, 0.09f, 0.11f, pressed ? 0.75f : 0.45f);
                GUI.DrawTexture(r, Circle);
                GUI.color = pressed ? UiSkin.Accent : new Color(1, 1, 1, 0.9f);
                GUI.Label(r, labels[b], style);
            }
            GUI.color = old;
        }
    }
}
