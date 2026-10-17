using System.Collections.Generic;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Шаблон мини-игры с параметрами (сложность, допуски, время, число шагов).
    /// Одна реализация шаблона обслуживает сотни операций — ТЗ, раздел 8.
    /// </summary>
    public abstract class Minigame
    {
        public bool Finished { get; protected set; }
        public bool Success { get; protected set; }
        public readonly int Difficulty;
        public string HintKey;

        protected Minigame(int difficulty)
        {
            Difficulty = Mathf.Clamp(difficulty, 1, 5);
        }

        /// <summary>Логика кадра. Ввод читается напрямую — мини-игра модальная.</summary>
        public abstract void Update(float dt);

        /// <summary>Отрисовка в прямоугольнике окна (IMGUI).</summary>
        public abstract void Draw(Rect area, MinigameDrawer d);

        protected void Finish(bool success)
        {
            Finished = true;
            Success = success;
        }

        public static Minigame Create(MinigameSpec spec, List<int> partItems)
        {
            switch (spec.Template)
            {
                case "forging": return new ForgingGame(spec.Difficulty) { HintKey = "ui.minigame.forging" };
                case "smelting": return new SmeltingGame(spec.Difficulty) { HintKey = "ui.minigame.smelting" };
                case "soldering": return new SolderingGame(spec.Difficulty) { HintKey = "ui.minigame.soldering" };
                default: return new AssemblyGame(spec.Difficulty, partItems) { HintKey = "ui.minigame.assembly" };
            }
        }

        protected static bool ActionDown() => Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0);
        protected static bool ActionHeld() => Input.GetKey(KeyCode.Space) || Input.GetMouseButton(0);
    }

    /// <summary>Набор примитивов рисования, который даёт HUD.</summary>
    public sealed class MinigameDrawer
    {
        public System.Action<Rect, Color> Fill;
        public System.Func<int, Texture2D> Icon;
        public System.Func<string, string> Loc;
        public GUIStyle Label, Small, Center;
    }

    // ================================================================ Ковка

    /// <summary>Метка бегает по шкале; удар засчитывается, если метка в светящейся зоне.</summary>
    public sealed class ForgingGame : Minigame
    {
        private readonly int _hitsNeeded;
        private readonly int _missesAllowed = 2;
        private readonly float _zoneWidth;
        private readonly float _speed;
        private float _marker, _dir = 1f;
        private float _zoneCenter;
        private int _hits, _misses;
        private float _flash;
        private bool _lastHit;

        public ForgingGame(int difficulty) : base(difficulty)
        {
            _hitsNeeded = 3 + Difficulty;
            _zoneWidth = Mathf.Max(0.08f, 0.24f - 0.035f * Difficulty);
            _speed = 0.7f + 0.3f * Difficulty;
            _zoneCenter = Random.Range(0.25f, 0.75f);
        }

        public override void Update(float dt)
        {
            if (Finished) return;
            _flash = Mathf.Max(0, _flash - dt * 3f);
            _marker += _dir * _speed * dt;
            if (_marker > 1f) { _marker = 1f; _dir = -1f; }
            if (_marker < 0f) { _marker = 0f; _dir = 1f; }

            if (!ActionDown()) return;
            _lastHit = Mathf.Abs(_marker - _zoneCenter) <= _zoneWidth / 2f;
            _flash = 1f;
            if (_lastHit)
            {
                _hits++;
                _zoneCenter = Random.Range(0.15f, 0.85f);
                if (_hits >= _hitsNeeded) Finish(true);
            }
            else if (++_misses > _missesAllowed) Finish(false);
        }

        public override void Draw(Rect a, MinigameDrawer d)
        {
            var bar = new Rect(a.x + 20, a.y + a.height * 0.45f, a.width - 40, 34);
            d.Fill(bar, new Color(0.15f, 0.12f, 0.1f));
            float zx = bar.x + (_zoneCenter - _zoneWidth / 2f) * bar.width;
            d.Fill(new Rect(zx, bar.y, _zoneWidth * bar.width, bar.height), new Color(1f, 0.55f + 0.2f * Mathf.Sin(Time.time * 8f), 0.15f, 0.9f));
            float mx = bar.x + _marker * bar.width;
            d.Fill(new Rect(mx - 3, bar.y - 8, 6, bar.height + 16), Color.white);
            if (_flash > 0)
            {
                var c = _lastHit ? new Color(1f, 0.9f, 0.4f, _flash) : new Color(0.9f, 0.2f, 0.2f, _flash);
                d.Fill(new Rect(bar.x, bar.yMax + 6, bar.width, 4), c);
            }
            GUI.Label(new Rect(bar.x, bar.yMax + 16, bar.width, 22),
                $"{d.Loc("ui.hits")}: {_hits}/{_hitsNeeded}    {d.Loc("ui.misses")}: {_misses}/{_missesAllowed}", d.Center);
        }
    }

    // ================================================================ Плавка

    /// <summary>Мехи раздувают жар (держать), без них он падает; удерживать температуру в зоне нужное время.</summary>
    public sealed class SmeltingGame : Minigame
    {
        private readonly float _zoneCenter, _zoneWidth, _needSeconds, _timeLimit;
        private float _temp = 0.15f, _vel, _inZone, _elapsed, _drift;

        public SmeltingGame(int difficulty) : base(difficulty)
        {
            _zoneCenter = Random.Range(0.45f, 0.7f);
            _zoneWidth = Mathf.Max(0.08f, 0.2f - 0.03f * Difficulty);
            _needSeconds = 3f + Difficulty;
            _timeLimit = 14f + 3f * Difficulty;
        }

        public override void Update(float dt)
        {
            if (Finished) return;
            _elapsed += dt;
            _drift = Mathf.Lerp(_drift, Random.Range(-0.25f, 0.25f), dt * 2f);
            float accel = ActionHeld() ? 0.75f : -0.55f;
            _vel = Mathf.Clamp(_vel + (accel + _drift * Difficulty * 0.4f) * dt, -0.6f, 0.6f);
            _vel *= 1f - Mathf.Min(1f, dt * 1.2f);
            _temp = Mathf.Clamp01(_temp + _vel * dt);

            if (Mathf.Abs(_temp - _zoneCenter) <= _zoneWidth / 2f) _inZone += dt;
            if (_inZone >= _needSeconds) Finish(true);
            else if (_temp >= 0.98f || _elapsed >= _timeLimit) Finish(false);
        }

        public override void Draw(Rect a, MinigameDrawer d)
        {
            var bar = new Rect(a.x + a.width / 2 - 30, a.y + 50, 60, a.height - 110);
            d.Fill(bar, new Color(0.1f, 0.1f, 0.12f));
            float zy = bar.yMax - (_zoneCenter + _zoneWidth / 2f) * bar.height;
            d.Fill(new Rect(bar.x, zy, bar.width, _zoneWidth * bar.height), new Color(0.3f, 0.8f, 0.3f, 0.5f));
            float h = _temp * bar.height;
            var heat = Color.Lerp(new Color(0.9f, 0.3f, 0.1f), new Color(1f, 0.95f, 0.7f), _temp);
            d.Fill(new Rect(bar.x + 14, bar.yMax - h, bar.width - 28, h), heat);
            d.Fill(new Rect(bar.x - 6, bar.y + 0.02f * bar.height, bar.width + 12, 2), new Color(1f, 0.2f, 0.2f));

            var prog = new Rect(a.x + 30, a.yMax - 50, a.width - 60, 12);
            d.Fill(prog, new Color(0, 0, 0, 0.6f));
            d.Fill(new Rect(prog.x, prog.y, prog.width * Mathf.Clamp01(_inZone / _needSeconds), prog.height), new Color(0.3f, 0.85f, 0.3f));
            GUI.Label(new Rect(prog.x, prog.yMax + 2, prog.width, 20),
                $"{d.Loc("ui.heat")}: {_temp * 100:0}%    {d.Loc("ui.time")}: {_timeLimit - _elapsed:0.0}s", d.Center);
        }
    }

    // ================================================================ Сборка

    /// <summary>
    /// Чертёж показывает порядок деталей несколько секунд, затем скрывается —
    /// детали нужно установить по памяти слева направо.
    /// </summary>
    public sealed class AssemblyGame : Minigame
    {
        private readonly List<int> _order = new List<int>();
        private readonly List<int> _tray = new List<int>();
        private readonly float _showTime;
        private readonly float _timeLimit;
        private readonly int _mistakesAllowed;
        private float _elapsed;
        private int _placed, _mistakes;
        private float _wrongFlash;

        public AssemblyGame(int difficulty, List<int> partItems) : base(difficulty)
        {
            var parts = partItems.Count > 0 ? partItems : new List<int> { 0 };
            int n = Mathf.Clamp(parts.Count + Difficulty, 3, 7);
            for (int i = 0; i < n; i++) _order.Add(parts[Random.Range(0, parts.Count)]);
            // Гарантируем, что все типы деталей встречаются
            for (int i = 0; i < parts.Count && i < n; i++) _order[i] = parts[i];
            Shuffle(_order);
            _tray.AddRange(_order);
            Shuffle(_tray);
            _showTime = Mathf.Max(1.2f, 4f - 0.6f * Difficulty);
            _timeLimit = 20f + 2f * n;
            _mistakesAllowed = Difficulty <= 2 ? 2 : 1;
        }

        private static void Shuffle(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                int t = list[i];
                list[i] = list[j];
                list[j] = t;
            }
        }

        private bool BlueprintVisible => _elapsed < _showTime;

        public override void Update(float dt)
        {
            if (Finished) return;
            _elapsed += dt;
            _wrongFlash = Mathf.Max(0, _wrongFlash - dt * 2f);
            if (_elapsed > _timeLimit + _showTime) Finish(false);
        }

        /// <summary>Клик по детали в лотке (индекс в _tray).</summary>
        private void Pick(int trayIndex)
        {
            if (Finished || BlueprintVisible) return;
            if (_tray[trayIndex] == _order[_placed])
            {
                _tray.RemoveAt(trayIndex);
                _placed++;
                if (_placed >= _order.Count) Finish(true);
            }
            else
            {
                _wrongFlash = 1f;
                if (++_mistakes > _mistakesAllowed) Finish(false);
            }
        }

        public override void Draw(Rect a, MinigameDrawer d)
        {
            const float S = 48, G = 8;
            float w = _order.Count * (S + G) - G;
            float x0 = a.x + (a.width - w) / 2f;
            float y0 = a.y + 50;

            // Чертёж
            d.Fill(new Rect(x0 - 12, y0 - 12, w + 24, S + 24), new Color(0.12f, 0.2f, 0.32f, 0.9f));
            for (int i = 0; i < _order.Count; i++)
            {
                var r = new Rect(x0 + i * (S + G), y0, S, S);
                d.Fill(r, i < _placed ? new Color(0.25f, 0.45f, 0.3f) : new Color(0.18f, 0.28f, 0.42f));
                bool show = BlueprintVisible || i < _placed;
                if (show)
                {
                    var icon = d.Icon(_order[i]);
                    if (icon != null) GUI.DrawTexture(new Rect(r.x + 6, r.y + 6, S - 12, S - 12), icon, ScaleMode.ScaleToFit);
                }
                else GUI.Label(r, "?", d.Center);
                GUI.Label(new Rect(r.x, r.yMax + 2, S, 16), (i + 1).ToString(), d.Center);
            }
            if (BlueprintVisible)
                d.Fill(new Rect(x0, y0 + S + 22, w * (1f - _elapsed / _showTime), 4), new Color(0.5f, 0.8f, 1f));

            // Лоток с деталями
            float ty = y0 + S + 50;
            float tw = _tray.Count * (S + G) - G;
            float tx = a.x + (a.width - tw) / 2f;
            d.Fill(new Rect(a.x + 20, ty - 10, a.width - 40, S + 20), _wrongFlash > 0 ? new Color(0.6f, 0.15f, 0.15f, 0.6f * _wrongFlash + 0.3f) : new Color(0.15f, 0.15f, 0.17f, 0.9f));
            for (int i = 0; i < _tray.Count; i++)
            {
                var r = new Rect(tx + i * (S + G), ty, S, S);
                bool hover = r.Contains(Event.current.mousePosition);
                d.Fill(r, hover ? new Color(0.35f, 0.35f, 0.38f) : new Color(0.25f, 0.25f, 0.28f));
                var icon = d.Icon(_tray[i]);
                if (icon != null) GUI.DrawTexture(new Rect(r.x + 6, r.y + 6, S - 12, S - 12), icon, ScaleMode.ScaleToFit);
                if (hover && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Event.current.Use();
                    Pick(i);
                    break;
                }
            }

            float left = Mathf.Max(0, _timeLimit + _showTime - _elapsed);
            GUI.Label(new Rect(a.x, ty + S + 20, a.width, 20),
                $"{d.Loc("ui.misses")}: {_mistakes}/{_mistakesAllowed}    {d.Loc("ui.time")}: {left:0.0}s", d.Center);
        }
    }

    // ================================================================ Пайка

    /// <summary>Плата с выводами: соединить пары одинаковых контактов (клик по выводу, затем по паре).</summary>
    public sealed class SolderingGame : Minigame
    {
        private readonly List<Vector2> _pins = new List<Vector2>();   // нормированные координаты 0..1
        private readonly List<int> _pair = new List<int>();          // номер пары
        private readonly List<bool> _done = new List<bool>();
        private readonly List<(int, int)> _links = new List<(int, int)>();
        private readonly int _pairs;
        private readonly int _mistakesAllowed;
        private readonly float _timeLimit;
        private int _selected = -1;
        private int _mistakes;
        private float _elapsed, _wrongFlash;

        private static readonly Color[] PairColors =
        {
            new Color(0.95f, 0.35f, 0.3f), new Color(0.3f, 0.75f, 0.95f), new Color(0.95f, 0.8f, 0.25f),
            new Color(0.5f, 0.9f, 0.4f), new Color(0.8f, 0.45f, 0.95f), new Color(0.95f, 0.6f, 0.3f),
            new Color(0.4f, 0.95f, 0.85f)
        };

        public SolderingGame(int difficulty) : base(difficulty)
        {
            _pairs = Mathf.Clamp(2 + Difficulty, 3, 7);
            _mistakesAllowed = Difficulty <= 2 ? 3 : 2;
            _timeLimit = 18f + 4f * _pairs;
            for (int p = 0; p < _pairs; p++)
                for (int k = 0; k < 2; k++)
                {
                    Vector2 pos;
                    int guard = 0;
                    do
                    {
                        pos = new Vector2(Random.Range(0.08f, 0.92f), Random.Range(0.1f, 0.9f));
                    } while (TooClose(pos) && ++guard < 60);
                    _pins.Add(pos);
                    _pair.Add(p);
                    _done.Add(false);
                }
        }

        private bool TooClose(Vector2 p)
        {
            foreach (var q in _pins)
                if ((p - q).sqrMagnitude < 0.012f) return true;
            return false;
        }

        public override void Update(float dt)
        {
            if (Finished) return;
            _elapsed += dt;
            _wrongFlash = Mathf.Max(0, _wrongFlash - dt * 2f);
            if (_elapsed > _timeLimit) Finish(false);
        }

        private void Click(int i)
        {
            if (Finished || _done[i]) return;
            if (_selected < 0)
            {
                _selected = i;
                return;
            }
            if (_selected == i)
            {
                _selected = -1;
                return;
            }
            if (_pair[_selected] == _pair[i])
            {
                _done[_selected] = _done[i] = true;
                _links.Add((_selected, i));
                _selected = -1;
                if (_links.Count >= _pairs) Finish(true);
            }
            else
            {
                _selected = -1;
                _wrongFlash = 1f;
                if (++_mistakes > _mistakesAllowed) Finish(false);
            }
        }

        public override void Draw(Rect a, MinigameDrawer d)
        {
            var board = new Rect(a.x + 30, a.y + 10, a.width - 60, a.height - 60);
            d.Fill(board, _wrongFlash > 0 ? Color.Lerp(new Color(0.1f, 0.3f, 0.15f), new Color(0.5f, 0.1f, 0.1f), _wrongFlash) : new Color(0.1f, 0.3f, 0.15f));

            // Проложенные дорожки — «уголком», как на плате
            foreach (var (i, j) in _links)
            {
                var p1 = PinPos(board, i);
                var p2 = PinPos(board, j);
                var c = PairColors[_pair[i] % PairColors.Length];
                d.Fill(new Rect(Mathf.Min(p1.x, p2.x), p1.y - 1.5f, Mathf.Abs(p2.x - p1.x), 3), c);
                d.Fill(new Rect(p2.x - 1.5f, Mathf.Min(p1.y, p2.y), 3, Mathf.Abs(p2.y - p1.y)), c);
            }

            for (int i = 0; i < _pins.Count; i++)
            {
                var p = PinPos(board, i);
                var r = new Rect(p.x - 9, p.y - 9, 18, 18);
                var c = PairColors[_pair[i] % PairColors.Length];
                bool hover = r.Contains(Event.current.mousePosition);
                d.Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), i == _selected ? Color.white : new Color(0.75f, 0.7f, 0.5f));
                d.Fill(r, _done[i] ? c * 0.6f : (hover ? Color.Lerp(c, Color.white, 0.3f) : c));
                if (hover && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Event.current.Use();
                    Click(i);
                }
            }

            GUI.Label(new Rect(a.x, board.yMax + 8, a.width, 20),
                $"{d.Loc("ui.misses")}: {_mistakes}/{_mistakesAllowed}    {d.Loc("ui.time")}: {Mathf.Max(0, _timeLimit - _elapsed):0.0}s", d.Center);
        }

        private Vector2 PinPos(Rect board, int i) =>
            new Vector2(board.x + _pins[i].x * board.width, board.y + _pins[i].y * board.height);
    }
}
