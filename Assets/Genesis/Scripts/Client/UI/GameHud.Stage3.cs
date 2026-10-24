using System.Collections.Generic;
using System.Text;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>Этап 3: выживание, ручной крафт на станциях, мини-игры.</summary>
    public sealed partial class GameHud
    {
        private const float CraftPanelWidth = 380f;
        private Vector2 _craftScroll;
        private Dictionary<string, List<CraftDef>> _craftsByStation;
        private long _lastDamageSeen = long.MinValue;
        private float _damageFlashUntil;
        private MinigameDrawer _drawer;

        // ================================================================ Выживание

        private void DrawVitals()
        {
            var p = _ctx.Player;
            if (_lastDamageSeen == long.MinValue) _lastDamageSeen = p.LastDamageTick;
            if (p.LastDamageTick != _lastDamageSeen)
            {
                _lastDamageSeen = p.LastDamageTick;
                _damageFlashUntil = Time.time + 0.35f;
            }

            float w = PlayerState.HotbarSize * (Slot + Gap) - Gap;
            float x0 = (UiScale.W - w) / 2f;
            float y = UiScale.H - Slot - 14 - 26;
            float half = w / 2f - 6;

            // Здоровье мигает при низком уровне, голод — тёплым цветом
            bool low = p.Health <= PlayerState.MaxHealth * 0.25f;
            var hpColor = low && Mathf.Repeat(Time.time, 0.6f) < 0.3f ? new Color(1f, 0.45f, 0.4f) : new Color(0.86f, 0.2f, 0.22f);
            if (p.PoisonTicks > 0) hpColor = new Color(0.45f, 0.78f, 0.25f); // отравлен
            Bar(new Rect(x0, y, half, 12), p.Health / PlayerState.MaxHealth, hpColor);
            UiSkin.Shadowed(new Rect(x0, y - 17, half, 16), $"{_ctx.Loc.Get("ui.health")} {Mathf.CeilToInt(p.Health)}" +
                (p.PoisonTicks > 0 ? $"  <color=#8fd14f>{_ctx.Loc.Get("ui.poisoned")}</color>" : ""), _small);
            var hungerColor = p.Hunger <= 4 ? new Color(0.9f, 0.4f, 0.1f) : new Color(0.78f, 0.58f, 0.26f);
            Bar(new Rect(x0 + half + 12, y, half, 12), p.Hunger / PlayerState.MaxHunger, hungerColor);
            UiSkin.Shadowed(new Rect(x0 + half + 12, y - 17, half, 16), $"{_ctx.Loc.Get("ui.hunger")} {Mathf.CeilToInt(p.Hunger)}", _small);

            // Эпоха (и век, если эпоха делится на века) — в правом верхнем углу
            bool day = Genesis.Gameplay.GameServer.SunHeight(_ctx.Session.DayPhase) > 0;
            var k = _ctx.Session.Knowledge;
            string era = _ctx.Loc.Get("epoch." + k.Epoch) +
                         (Eras.HasCenturies(k.Epoch) ? " · " + _ctx.Loc.Get("era." + k.EraName) : "") +
                         "   " + _ctx.Loc.Get(day ? "ui.time.day" : "ui.time.night");
            var size = _label.CalcSize(new GUIContent(era));
            UiSkin.DrawPanel(new Rect(UiScale.W - size.x - 26, 8, size.x + 18, 28));
            UiSkin.Shadowed(new Rect(UiScale.W - size.x - 17, 11, size.x + 4, 22), era, _label);
        }

        private void DrawDamageFlash()
        {
            if (Time.time > _damageFlashUntil) return;
            float a = (_damageFlashUntil - Time.time) / 0.35f * 0.35f;
            Fill(new Rect(0, 0, UiScale.W, UiScale.H), new Color(0.8f, 0f, 0f, a));
        }

        // ================================================================ Крафт

        private List<CraftDef> CraftsFor(string station)
        {
            if (_craftsByStation == null)
            {
                _craftsByStation = new Dictionary<string, List<CraftDef>>();
                foreach (var c in _ctx.Content.Crafts.Items)
                {
                    if (!_craftsByStation.TryGetValue(c.Station, out var list))
                        _craftsByStation[c.Station] = list = new List<CraftDef>();
                    list.Add(c);
                }
            }
            return _craftsByStation.TryGetValue(station, out var l) ? l : new List<CraftDef>();
        }

        private bool HasCrafts(string station) => CraftsFor(station).Count > 0;

        private void DrawStationWindow(MachineInstance m)
        {
            if (m.Def.CraftGrid >= 3)
            {
                DrawGridWindow(3, _ctx.Loc.Get(m.Def.NameKey));
                return;
            }
            float w = GridWidth + 24, h = GridHeight + 60;
            var p = Panel(w, h, _ctx.Loc.Get(m.Def.NameKey), -(CraftPanelWidth + 12) / 2f);
            DrawCraftPanel(new Rect(p.xMax + 12, p.y, CraftPanelWidth, Mathf.Max(h, 460)), m.Def.Id, m.Pos);
            DrawPlayerGrid(p.x + 12, p.y + 44, slot =>
            {
                if (_pickedSlot < 0)
                {
                    if (!_ctx.Player.Inventory[slot].IsEmpty) _pickedSlot = slot;
                }
                else
                {
                    _ctx.Send(new SwapSlotsCommand { A = _pickedSlot, B = slot });
                    _pickedSlot = -1;
                }
            });
            DrawTooltip();
        }

        private void DrawCraftPanel(Rect r, string station, Int3 stationPos)
        {
            PanelAt(r, _ctx.Loc.Get("ui.crafting"));
            var knowledge = _ctx.Session.Knowledge;
            var all = CraftsFor(station);
            var list = new List<CraftDef>();
            foreach (var c in all) if (knowledge.IsCraftUnlocked(c)) list.Add(c);
            foreach (var c in all) if (!knowledge.IsCraftUnlocked(c)) list.Add(c);

            const float RowH = 58f;
            var view = new Rect(r.x + 8, r.y + 40, r.width - 16, r.height - 48);
            var content = new Rect(0, 0, view.width - 18, list.Count * RowH);
            _craftScroll = GUI.BeginScrollView(view, _craftScroll, content);
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                bool unlocked = knowledge.IsCraftUnlocked(c);
                var row = new Rect(0, i * RowH, content.width, RowH - 4);
                Fill(row, unlocked ? SlotColor : new Color(0.16f, 0.16f, 0.17f, 0.9f));

                // Выход
                if (c.Outputs.Count > 0)
                {
                    var o = c.Outputs[0];
                    var icon = _ctx.IconOf(o.ItemId);
                    if (icon != null) GUI.DrawTexture(new Rect(row.x + 6, row.y + 7, 40, 40), icon, ScaleMode.ScaleToFit);
                    string name = _ctx.ItemName(o.ItemId) + (o.Count > 1 ? $" ×{o.Count}" : "");
                    GUI.Label(new Rect(row.x + 52, row.y + 3, row.width - 160, 20), unlocked ? name : $"<color=#888888>{name}</color>", _small);
                }

                bool enough = true;
                float ix = row.x + 52;
                foreach (var input in c.Inputs)
                {
                    int have = _ctx.Player.Inventory.CountOf(input.ItemId);
                    bool ok = have >= input.Count;
                    enough &= ok;
                    var icon = _ctx.IconOf(input.ItemId);
                    var ir = new Rect(ix, row.y + 24, 24, 24);
                    if (icon != null) GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit);
                    if (ir.Contains(Event.current.mousePosition)) _tooltip = _ctx.ItemName(input.ItemId);
                    GUI.Label(new Rect(ix + 22, row.y + 30, 30, 18), ok ? input.Count.ToString() : $"<color=#ff6b5b>{input.Count}</color>", _small);
                    ix += 50;
                }

                var btn = new Rect(row.xMax - 104, row.y + 13, 98, 28);
                if (!unlocked)
                {
                    var gate = knowledge.GateOfCraft(c.Id);
                    string why = gate != null ? _ctx.Loc.Get(gate.NameKey) : _ctx.Loc.Get("ui.locked");
                    GUI.Label(new Rect(btn.x - 30, btn.y - 4, btn.width + 30, 36), $"<color=#888888>{why}</color>",
                        new GUIStyle(_small) { wordWrap = true, alignment = TextAnchor.MiddleRight });
                    continue;
                }
                string label = c.Minigame != null ? _ctx.Loc.Get("ui.craft_minigame") : _ctx.Loc.Get("ui.craft");
                GUI.enabled = enough && !_ctx.InMinigame;
                if (GUI.Button(btn, label, new GUIStyle(GUI.skin.button) { fontSize = 11, wordWrap = true }))
                    StartCraft(c, stationPos);
                GUI.enabled = true;
            }
            GUI.EndScrollView();
        }

        private void StartCraft(CraftDef c, Int3 stationPos)
        {
            if (c.Minigame == null)
            {
                _ctx.Send(new CraftCommand { CraftId = c.NumericId, Station = stationPos });
                return;
            }
            var parts = new List<int>();
            foreach (var i in c.Inputs) parts.Add(i.ItemId);
            _ctx.Minigame = Minigame.Create(c.Minigame, parts);
            _ctx.MinigameCraft = c;
            _ctx.MinigameGrid = false;
            _ctx.MinigameStation = stationPos;
            _ctx.MinigameCloseAt = 0;
        }

        // ================================================================ Мини-игры

        private void UpdateMinigame()
        {
            if (_ctx == null || _ctx.Minigame == null) return;
            var g = _ctx.Minigame;
            if (!g.Finished)
            {
                g.Update(Time.deltaTime);
                if (g.Finished)
                {
                    _ctx.MinigameCloseAt = Time.time + 1.1f;
                    if (g.Success && _ctx.MinigameGrid)
                        _ctx.Send(new GridCraftCommand { CraftId = _ctx.MinigameCraft.NumericId, MinigameSuccess = true });
                    else if (g.Success)
                        _ctx.Send(new CraftCommand { CraftId = _ctx.MinigameCraft.NumericId, Station = _ctx.MinigameStation, MinigameSuccess = true });
                }
            }
            else if (Time.time >= _ctx.MinigameCloseAt)
            {
                _ctx.Minigame = null;
            }
        }

        private void DrawMinigame()
        {
            var g = _ctx.Minigame;
            if (g == null) return;
            if (_drawer == null)
            {
                _drawer = new MinigameDrawer
                {
                    Fill = Fill,
                    Icon = item => _ctx.IconOf(item),
                    Loc = key => _ctx.Loc.Get(key),
                    Label = _label,
                    Small = _small,
                    Center = _center
                };
            }

            Fill(new Rect(0, 0, UiScale.W, UiScale.H), new Color(0, 0, 0, 0.55f));
            float w = Mathf.Min(620, UiScale.W - 40), h = 380;
            var craft = _ctx.MinigameCraft;
            string title = craft.Outputs.Count > 0 ? _ctx.ItemName(craft.Outputs[0].ItemId) : craft.Id;
            var p = Panel(w, h, title);
            GUI.Label(new Rect(p.x + 14, p.y + 34, w - 28, 40), _ctx.Loc.Get(g.HintKey), new GUIStyle(_small) { wordWrap = true });
            g.Draw(new Rect(p.x, p.y + 60, w, h - 90), _drawer);

            if (g.Finished)
            {
                string res = g.Success ? _ctx.Loc.Get("ui.minigame.success") : _ctx.Loc.Get("ui.minigame.fail");
                var style = new GUIStyle(_title) { alignment = TextAnchor.MiddleCenter };
                style.normal.textColor = g.Success ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.45f);
                GUI.Label(new Rect(p.x, p.yMax - 34, w, 28), res, style);
            }
            else
            {
                GUI.Label(new Rect(p.x, p.yMax - 26, w - 12, 20), _ctx.Loc.Get("ui.minigame.quit"), new GUIStyle(_small) { alignment = TextAnchor.MiddleRight });
            }
        }
    }
}
