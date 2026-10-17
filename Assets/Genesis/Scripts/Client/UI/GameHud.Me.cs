using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>Этап 5b: ME-терминал, шины с фильтрами, провайдер шаблонов.</summary>
    public sealed partial class GameHud
    {
        private string _meSearch = "";
        private Vector2 _meScroll, _meSideScroll, _patternScroll;
        private int _meSelected = -1;
        private string _meAmount = "16";
        private string _meTarget = "128";
        private int _meTab;

        private struct MeEntry
        {
            public int Item;
            public long Count;
            public bool Craftable;
            public string Name;
        }

        public static string ShortCount(long n)
        {
            if (n < 1000) return n.ToString();
            if (n < 10000) return (n / 1000f).ToString("0.#") + "k";
            if (n < 1000000) return (n / 1000) + "k";
            return (n / 1000000f).ToString("0.#") + "M";
        }

        // ================================================================ Терминал

        private void DrawMeWindow()
        {
            const int Cols = 9;
            float leftW = Cols * (Slot + Gap) - Gap;
            float rightW = 300;
            float w = leftW + rightW + 36;
            float h = Mathf.Min(Screen.height - 40, 700);
            var p = Panel(w, h, _ctx.Loc.Get("ui.me.terminal"));
            var snap = _ctx.Session.Me;

            if (snap == null)
            {
                GUI.Label(new Rect(p.x + 12, p.y + 44, w - 24, 22), _ctx.Loc.Get("ui.loading"), _small);
                return;
            }
            var access = snap.Access;
            float x = p.x + 12, y = p.y + 40;

            // Вкладки и поиск
            if (TabButton(new Rect(x, y, 110, 24), _ctx.Loc.Get("ui.me.items"), _meTab == 0)) _meTab = 0;
            if (TabButton(new Rect(x + 114, y, 110, 24), _ctx.Loc.Get("ui.me.fluids"), _meTab == 1)) _meTab = 1;
            GUI.Label(new Rect(x + 236, y + 3, 60, 20), _ctx.Loc.Get("ui.me.search"), _small);
            _meSearch = GUI.TextField(new Rect(x + 290, y, leftW - 290, 24), _meSearch ?? "", 32);
            y += 32;

            float gridH = p.yMax - y - GridHeight - 34;
            var view = new Rect(x, y, leftW + 18, gridH);
            if (_meTab == 0) DrawMeItems(snap, view, Cols);
            else DrawMeFluids(snap, view);

            GUI.Label(new Rect(x, view.yMax + 2, leftW, 18), $"<size=11><color=#aaaaaa>{_ctx.Loc.Get("ui.me.take_hint")}</color></size>", _small);

            // Инвентарь игрока: клик — положить в сеть
            DrawPlayerGrid(x, p.yMax - GridHeight - 12, slot => _ctx.Send(new MeInsertCommand { Access = access, PlayerSlot = slot }));

            DrawMeSidePanel(snap, new Rect(x + leftW + 24, p.y + 40, rightW - 12, h - 52));
            DrawTooltip();
        }

        private bool TabButton(Rect r, string text, bool active)
        {
            Fill(r, active ? new Color(Accent.r, Accent.g, Accent.b, 0.85f) : SlotColor);
            GUI.Label(r, text, _center);
            if (r.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown)
            {
                Event.current.Use();
                return true;
            }
            return false;
        }

        private List<MeEntry> MeEntries(MeSnapshot snap)
        {
            var list = new List<MeEntry>();
            var seen = new HashSet<int>();
            string q = (_meSearch ?? "").Trim().ToLowerInvariant();
            var craftable = new HashSet<int>(snap.Craftable);
            foreach (var kv in snap.Items)
            {
                seen.Add(kv.Key);
                Add(kv.Key, kv.Value);
            }
            foreach (var c in snap.Craftable)
                if (!seen.Contains(c)) Add(c, 0);
            list.Sort((a, b) =>
            {
                int byCount = b.Count.CompareTo(a.Count);
                return byCount != 0 ? byCount : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            return list;

            void Add(int item, long count)
            {
                string name = _ctx.ItemName(item);
                if (q.Length > 0 && name.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) return;
                list.Add(new MeEntry { Item = item, Count = count, Craftable = craftable.Contains(item), Name = name });
            }
        }

        private void DrawMeItems(MeSnapshot snap, Rect view, int cols)
        {
            var entries = MeEntries(snap);
            int rows = Mathf.Max(1, (entries.Count + cols - 1) / cols);
            var content = new Rect(0, 0, view.width - 18, rows * (Slot + Gap));
            Fill(view, new Color(0, 0, 0, 0.25f));
            if (entries.Count == 0)
                GUI.Label(new Rect(view.x + 8, view.y + 8, view.width, 20), _ctx.Loc.Get("ui.me.empty"), _small);

            _meScroll = GUI.BeginScrollView(view, _meScroll, content);
            var e = Event.current;
            for (int i = 0; i < entries.Count; i++)
            {
                var en = entries[i];
                var r = new Rect((i % cols) * (Slot + Gap), (i / cols) * (Slot + Gap), Slot, Slot);
                bool hover = r.Contains(e.mousePosition);
                Fill(r, en.Item == _meSelected ? new Color(Accent.r, Accent.g, Accent.b, 0.5f) : (hover ? SlotHover : SlotColor));
                var icon = _ctx.IconOf(en.Item);
                if (icon != null)
                {
                    var old = GUI.color;
                    if (en.Count == 0) GUI.color = new Color(1, 1, 1, 0.4f);
                    GUI.DrawTexture(new Rect(r.x + 6, r.y + 6, r.width - 12, r.height - 12), icon, ScaleMode.ScaleToFit);
                    GUI.color = old;
                }
                if (en.Count > 0) GUI.Label(new Rect(r.x, r.y, r.width - 3, r.height - 1), ShortCount(en.Count), _count);
                if (en.Craftable) GUI.Label(new Rect(r.x + 3, r.y + 1, 16, 16), "<color=#7fd6ff>+</color>", _small);
                if (hover) _tooltip = en.Name + (en.Count > 0 ? $" — {en.Count}" : "");

                if (hover && e.type == EventType.MouseDown)
                {
                    e.Use();
                    if (e.button == 1 || en.Count == 0)
                    {
                        if (en.Craftable) _meSelected = en.Item;
                    }
                    else
                    {
                        int max = _ctx.Content.Item(en.Item)?.MaxStack ?? 64;
                        _ctx.Send(new MeExtractCommand { Access = snap.Access, Item = en.Item, Count = e.shift ? 1 : max });
                        _meSelected = en.Item;
                    }
                }
            }
            GUI.EndScrollView();
        }

        private void DrawMeFluids(MeSnapshot snap, Rect view)
        {
            Fill(view, new Color(0, 0, 0, 0.25f));
            if (snap.Fluids.Count == 0)
            {
                GUI.Label(new Rect(view.x + 8, view.y + 8, view.width, 20), _ctx.Loc.Get("ui.me.empty"), _small);
                return;
            }
            float y = view.y + 8;
            foreach (var kv in snap.Fluids)
            {
                var def = _ctx.Content.Fluids.Get(kv.Key);
                if (def == null) continue;
                ColorUtility.TryParseHtmlString(def.Color, out var color);
                var r = new Rect(view.x + 8, y, view.width - 34, 26);
                Fill(r, SlotColor);
                Fill(new Rect(r.x + 4, r.y + 4, 18, 18), color);
                GUI.Label(new Rect(r.x + 30, r.y + 4, r.width - 40, 20), $"{_ctx.Loc.Get(def.NameKey)} — {ShortCount(kv.Value)} mB", _small);
                y += 30;
            }
        }

        private void DrawMeSidePanel(MeSnapshot snap, Rect r)
        {
            Fill(r, new Color(1, 1, 1, 0.04f));
            float x = r.x + 8, w = r.width - 16, y = r.y + 6;

            string status = snap.Online
                ? "<color=#7fd67f>●</color> Online"
                : $"<color=#ff6b5b>●</color> {_ctx.Loc.Get(snap.Error ?? "ui.me.offline")}";
            GUI.Label(new Rect(x, y, w, 20), status, _small);
            y += 22;
            Bar(new Rect(x, y, w, 10), snap.ItemCapacity > 0 ? (float)snap.ItemUsed / snap.ItemCapacity : 0, new Color(0.6f, 0.4f, 0.9f));
            y += 12;
            Bar(new Rect(x, y, w, 10), snap.FluidCapacity > 0 ? (float)snap.FluidUsed / snap.FluidCapacity : 0, new Color(0.3f, 0.6f, 0.95f));
            y += 14;
            GUI.Label(new Rect(x, y, w, 34), $"<size=11>{_ctx.Loc.Format("ui.me.capacity", ShortCount(snap.ItemUsed), ShortCount(snap.ItemCapacity), ShortCount(snap.FluidUsed), ShortCount(snap.FluidCapacity), snap.Power.ToString("0.#"))}</size>",
                new GUIStyle(_small) { wordWrap = true });
            y += 38;

            // ---- Крафт выбранного предмета
            GUI.Label(new Rect(x, y, w, 20), "<b>" + _ctx.Loc.Get("ui.me.crafting") + "</b>", _label);
            y += 24;
            if (_meSelected >= 0)
            {
                var icon = _ctx.IconOf(_meSelected);
                if (icon != null) GUI.DrawTexture(new Rect(x, y, 28, 28), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(x + 34, y + 5, w - 34, 20), $"{_ctx.ItemName(_meSelected)} ({ShortCount(snap.CountOf(_meSelected))})", _small);
                y += 34;
                bool craftable = snap.Craftable.Contains(_meSelected);
                GUI.Label(new Rect(x, y + 3, 90, 20), _ctx.Loc.Get("ui.me.amount"), _small);
                _meAmount = GUI.TextField(new Rect(x + 92, y, 70, 24), _meAmount ?? "", 7);
                GUI.enabled = craftable && snap.Online;
                if (GUI.Button(new Rect(x + 168, y, w - 168, 24), _ctx.Loc.Get("ui.me.craft")) && int.TryParse(_meAmount, out int amount) && amount > 0)
                    _ctx.Send(new MeCraftCommand { Access = snap.Access, Item = _meSelected, Count = amount });
                y += 30;
                GUI.Label(new Rect(x, y + 3, 90, 20), _ctx.Loc.Get("ui.me.target"), _small);
                _meTarget = GUI.TextField(new Rect(x + 92, y, 70, 24), _meTarget ?? "", 7);
                if (GUI.Button(new Rect(x + 168, y, (w - 168) / 2 - 2, 24), _ctx.Loc.Get("ui.me.set")) && long.TryParse(_meTarget, out long target) && target > 0)
                    _ctx.Send(new MeStockCommand { Access = snap.Access, Item = _meSelected, Target = target });
                GUI.enabled = true;
                if (GUI.Button(new Rect(x + 168 + (w - 168) / 2 + 2, y, (w - 168) / 2 - 2, 24), _ctx.Loc.Get("ui.me.remove")))
                    _ctx.Send(new MeStockCommand { Access = snap.Access, Item = _meSelected, Target = 0 });
                y += 34;
            }

            // ---- Заказы и запас
            var listRect = new Rect(x, y, w, r.yMax - y - 40);
            float rows = snap.Jobs.Count + snap.Stock.Count + 2;
            _meSideScroll = GUI.BeginScrollView(listRect, _meSideScroll, new Rect(0, 0, w - 18, rows * 24 + 10));
            float ly = 0;
            GUI.Label(new Rect(0, ly, w, 20), "<b>" + _ctx.Loc.Get("ui.me.jobs") + "</b>", _small);
            ly += 22;
            foreach (var kv in snap.Jobs)
            {
                MeRow(ly, kv.Key, $"→ {ShortCount(kv.Value)}", w - 18);
                ly += 24;
            }
            GUI.Label(new Rect(0, ly, w, 20), "<b>" + _ctx.Loc.Get("ui.me.stock") + "</b>", _small);
            ly += 22;
            foreach (var s in snap.Stock)
            {
                var row = MeRow(ly, s.Item, $"{ShortCount(snap.CountOf(s.Item))}/{ShortCount(s.Target)}", w - 18);
                if (row.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown)
                {
                    Event.current.Use();
                    _meSelected = s.Item;
                    _meTarget = s.Target.ToString();
                }
                ly += 24;
            }
            GUI.EndScrollView();

            if (GUI.Button(new Rect(x, r.yMax - 32, w, 26), _ctx.Loc.Get("ui.me.cancel")))
                _ctx.Send(new MeCancelCommand { Access = snap.Access });
        }

        private Rect MeRow(float y, int item, string right, float w)
        {
            var row = new Rect(0, y, w, 22);
            Fill(row, SlotColor);
            var icon = _ctx.IconOf(item);
            if (icon != null) GUI.DrawTexture(new Rect(row.x + 2, row.y + 1, 20, 20), icon, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(row.x + 26, row.y + 2, w - 110, 20), _ctx.ItemName(item), _small);
            GUI.Label(new Rect(row.xMax - 84, row.y + 2, 80, 20), right, new GUIStyle(_small) { alignment = TextAnchor.UpperRight });
            return row;
        }

        // ================================================================ Шины

        private void DrawBusWindow(MachineInstance m)
        {
            float w = GridWidth + 24, h = GridHeight + 210;
            var p = Panel(w, h, _ctx.Loc.Get(m.Def.NameKey));
            float x = p.x + 12, y = p.y + 40;
            GUI.Label(new Rect(x, y, w, 20), $"<color=#f5a623>{StatusText(m.Status)}</color>", _label);
            y += 26;
            GUI.Label(new Rect(x, y, w, 20), _ctx.Loc.Get("ui.me.filter"), _label);
            y += 24;
            for (int i = 0; i < m.Filter.Length; i++)
            {
                var r = new Rect(x + i * (Slot + Gap), y, Slot, Slot);
                var st = m.Filter[i] >= 0 ? new ItemStack(m.Filter[i], 1) : ItemStack.Empty;
                if (DrawSlot(r, st, false)) _ctx.Send(new MeFilterCommand { Pos = m.Pos, Slot = i, Item = -1 });
            }
            y += Slot + 6;
            GUI.Label(new Rect(x, y, w - 24, 34), $"<size=11><color=#aaaaaa>{_ctx.Loc.Get("ui.me.filter_hint")}</color></size>",
                new GUIStyle(_small) { wordWrap = true });
            y += 36;

            // Фильтр жидкости — перебор по кругу
            string fluidName = m.FluidFilter >= 0 ? _ctx.Loc.Get(_ctx.Content.Fluids.Get(m.FluidFilter).NameKey) : _ctx.Loc.Get("ui.me.any");
            GUI.Label(new Rect(x, y + 3, 120, 20), _ctx.Loc.Get("ui.me.fluid_filter"), _small);
            if (GUI.Button(new Rect(x + 120, y, 180, 24), fluidName))
            {
                int next = m.FluidFilter + 1;
                if (next >= _ctx.Content.Fluids.Count) next = -1;
                _ctx.Send(new MeFluidFilterCommand { Pos = m.Pos, Fluid = next });
            }

            DrawPlayerGrid(x, p.yMax - GridHeight - 12, slot =>
            {
                var st = _ctx.Player.Inventory[slot];
                if (st.IsEmpty) return;
                int free = Array.IndexOf(m.Filter, -1);
                if (Array.IndexOf(m.Filter, st.Item) >= 0 || free < 0) return;
                _ctx.Send(new MeFilterCommand { Pos = m.Pos, Slot = free, Item = st.Item });
            });
            DrawTooltip();
        }

        // ================================================================ Провайдер шаблонов

        private void DrawProviderWindow(MachineInstance m)
        {
            float w = 460, h = Mathf.Min(Screen.height - 60, 520);
            var p = Panel(w, h, _ctx.Loc.Get(m.Def.NameKey));
            float x = p.x + 12, y = p.y + 40;
            GUI.Label(new Rect(x, y, w, 20), $"<color=#f5a623>{StatusText(m.Status)}</color>", _label);
            y += 24;
            GUI.Label(new Rect(x, y, w - 24, 34), $"<size=11><color=#aaaaaa>{_ctx.Loc.Get("ui.me.patterns_hint")}</color></size>",
                new GUIStyle(_small) { wordWrap = true });
            y += 36;

            var target = _ctx.Session.Factory.MachineAt(m.Pos + m.Facing.Offset());
            if (target == null || target.Def.IsMeDevice)
            {
                GUI.Label(new Rect(x, y, w - 24, 20), _ctx.Loc.Get("ui.me.no_machine"), _small);
                return;
            }
            GUI.Label(new Rect(x, y, w - 24, 20), "→ " + _ctx.Loc.Get(target.Def.NameKey), _label);
            y += 26;

            var recipes = new List<RecipeDef>();
            foreach (var r in _ctx.Content.RecipesFor(target.Def.NumericId))
                if (_ctx.Session.Knowledge.IsRecipeUnlocked(r) && r.Outputs.Count > 0) recipes.Add(r);

            const float RowH = 34;
            var view = new Rect(x, y, w - 24, p.yMax - y - 12);
            _patternScroll = GUI.BeginScrollView(view, _patternScroll, new Rect(0, 0, view.width - 18, recipes.Count * RowH));
            for (int i = 0; i < recipes.Count; i++)
            {
                var r = recipes[i];
                bool on = m.Patterns.Contains(r.NumericId);
                var row = new Rect(0, i * RowH, view.width - 18, RowH - 4);
                Fill(row, on ? new Color(0.2f, 0.4f, 0.3f, 0.95f) : SlotColor);
                var o = r.Outputs[0];
                var icon = _ctx.IconOf(o.ItemId);
                if (icon != null) GUI.DrawTexture(new Rect(row.x + 4, row.y + 3, 24, 24), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(row.x + 34, row.y + 6, row.width - 120, 20), _ctx.ItemName(o.ItemId) + (o.Count > 1 ? $" ×{o.Count}" : ""), _small);
                float ix = row.xMax - 84;
                foreach (var input in r.Inputs)
                {
                    if (ix > row.xMax - 20) break;
                    var ic = _ctx.IconOf(input.ItemId);
                    if (ic != null) GUI.DrawTexture(new Rect(ix, row.y + 7, 16, 16), ic, ScaleMode.ScaleToFit);
                    ix += 20;
                }
                if (row.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown)
                {
                    Event.current.Use();
                    _ctx.Send(new MePatternCommand { Pos = m.Pos, Recipe = r.NumericId, Enabled = !on });
                }
            }
            GUI.EndScrollView();
        }
    }
}
