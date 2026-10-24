using Genesis.Gameplay;
using Genesis.Simulation;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Первобытная эпоха на экране: слот левой руки у хотбара, подсказка под прицелом, прогресс крафта в руках,
    /// панель режима создания (удары) и колесо выбора (как колесо оружия в GTA).
    /// </summary>
    public sealed partial class GameHud
    {
        private void DrawPrimitiveHud()
        {
            DrawOffhandSlot();
            if (_ctx.Window != UiWindow.None || _ctx.Paused) return;
            DrawHandCraftProgress();
            DrawCreationPanel();
            DrawHint();
            if (_ctx.Creation.WheelOpen) DrawCreationWheel();
        }

        /// <summary>Левая рука — отдельный слот слева от хотбара.</summary>
        private void DrawOffhandSlot()
        {
            float w = PlayerState.HotbarSize * (Slot + Gap) - Gap;
            float x0 = (UiScale.W - w) / 2f;
            float y0 = UiScale.H - Slot - 14;
            var r = new Rect(x0 - Slot - 22, y0, Slot, Slot);
            UiSkin.DrawPanel(new Rect(r.x - 6, r.y - 6, r.width + 12, r.height + 12));
            DrawSlot(r, _ctx.Player.OffhandStack, false);
            GUI.Label(new Rect(r.x + 4, r.y + 2, 14, 14), "F", UiSkin.Tiny);
        }

        private void DrawHandCraftProgress()
        {
            if (_ctx.HandCraft == null || _ctx.HandCraftProgress <= 0f) return;
            float cx = UiScale.W / 2f, cy = UiScale.H / 2f;
            var r = new Rect(cx - 60, cy + 22, 120, 8);
            Fill(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), new Color(0, 0, 0, 0.6f));
            Bar(r, _ctx.HandCraftProgress, Accent);
        }

        private void DrawHint()
        {
            if (string.IsNullOrEmpty(_ctx.Hint)) return;
            float cy = UiScale.H / 2f;
            var style = new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, richText = true };
            UiSkin.Shadowed(new Rect(0, cy + 34, UiScale.W, 20), _ctx.Hint, style);
        }

        /// <summary>Режим создания: что делаем и сколько ударов осталось.</summary>
        private void DrawCreationPanel()
        {
            var c = _ctx.Creation;
            if (!c.Active || c.Option == null) return;
            string title = _ctx.Loc.Get("ui.create.mode") + ": " + _ctx.Loc.Get(c.Option.NameKey);
            float progress;
            string sub;
            if (c.Option.Structure != null)
            {
                progress = c.FinishedAt >= 0 ? 1f : 0f;
                sub = _ctx.Loc.Get("ui.create.build");
            }
            else
            {
                var s = c.Option.Shaping;
                float done = c.Hits + (s.IsHold ? c.HoldTimer / Mathf.Max(0.05f, s.Interval) : 0f);
                progress = Mathf.Clamp01(done / s.Hits);
                int left = Mathf.Max(0, s.Hits - c.Hits);
                sub = s.IsHold ? _ctx.Loc.Format("ui.create.hold_left", Mathf.CeilToInt(left * s.Interval)) : _ctx.Loc.Format("ui.create.hits_left", left);
            }
            var size = _label.CalcSize(new GUIContent(title));
            float w = Mathf.Max(260, size.x + 30);
            var r = new Rect((UiScale.W - w) / 2f, 44, w, 56);
            UiSkin.DrawPanel(r);
            Fill(new Rect(r.x + 3, r.y + 3, 3, r.height - 6), Accent);
            UiSkin.Shadowed(new Rect(r.x + 12, r.y + 4, w - 20, 22), title, _label);
            Bar(new Rect(r.x + 12, r.y + 30, w - 24, 8), progress, Accent);
            GUI.Label(new Rect(r.x + 12, r.y + 38, w - 24, 18), $"<size=10><color=#bbbbbb>{sub}</color></size>", _small);
        }

        /// <summary>Колесо выбора: сектора по кругу, мышь — выбор, отпустить R — подтвердить.</summary>
        private void DrawCreationWheel()
        {
            var c = _ctx.Creation;
            int n = c.Options.Count;
            if (n == 0) return;
            float cx = UiScale.W / 2f, cy = UiScale.H / 2f;
            const float Radius = 130f, Cell = 64f;
            Fill(new Rect(0, 0, UiScale.W, UiScale.H), new Color(0, 0, 0, 0.25f));

            // Центр: подсказка
            var hub = new Rect(cx - 70, cy - 22, 140, 44);
            UiSkin.DrawPanel(hub);
            GUI.Label(hub, $"<size=11>{_ctx.Loc.Get("ui.create.wheel")}</size>", new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, wordWrap = true });

            float step = 360f / n;
            for (int i = 0; i < n; i++)
            {
                float a = i * step * Mathf.Deg2Rad;
                var center = new Vector2(cx + Mathf.Sin(a) * Radius, cy - Mathf.Cos(a) * Radius);
                var r = new Rect(center.x - Cell / 2f, center.y - Cell / 2f, Cell, Cell);
                bool hover = i == c.WheelHover;
                if (hover) Fill(new Rect(r.x - 4, r.y - 4, r.width + 8, r.height + 8), new Color(Accent.r, Accent.g, Accent.b, 0.55f));
                UiSkin.DrawSlot(r, hover);
                var opt = c.Options[i];
                if (opt.Icon >= 0)
                {
                    var icon = _ctx.IconOf(opt.Icon);
                    if (icon != null) GUI.DrawTexture(new Rect(r.x + 10, r.y + 10, r.width - 20, r.height - 20), icon, ScaleMode.ScaleToFit);
                }
                var label = _ctx.Loc.Get(opt.NameKey);
                if (opt.Shaping != null) label += $"  ({_ctx.Loc.Format("ui.create.hits", opt.Shaping.Hits)})";
                var ls = new GUIStyle(_small) { alignment = TextAnchor.UpperCenter, wordWrap = true };
                UiSkin.Shadowed(new Rect(center.x - 80, r.yMax + 2, 160, 34), hover ? $"<b>{label}</b>" : label, ls);
            }
            // Указатель мыши в колесе
            var p = new Vector2(cx + c.WheelCursor.x, cy - c.WheelCursor.y);
            Fill(new Rect(p.x - 3, p.y - 3, 6, 6), Color.white);
        }
    }
}
