using System;
using System.Collections.Generic;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>Справочник рецептов (аналог JEI/EMI): «как сделать» и «где используется». Клавиша J.</summary>
    public sealed partial class GameHud
    {
        private string _jeiSearch = "";
        private Vector2 _jeiListScroll, _jeiScroll;
        private int _jeiItem = -1;
        private int _jeiTab; // 0 — как сделать, 1 — где используется
        private readonly Stack<int> _jeiHistory = new Stack<int>();

        /// <summary>Открыть справочник на предмете (например, из подсказки слота).</summary>
        public void ShowRecipesFor(int item)
        {
            if (_jeiItem >= 0 && _jeiItem != item) _jeiHistory.Push(_jeiItem);
            _jeiItem = item;
            _jeiScroll = Vector2.zero;
        }

        private void DrawRecipesWindow()
        {
            float w = Mathf.Min(UiScale.W - 40, 980), h = Mathf.Min(UiScale.H - 40, 640);
            var p = Panel(w, h, _ctx.Loc.Get("ui.jei"));
            const int Cols = 7;
            float listW = Cols * (Slot + Gap) - Gap + 18;
            float x = p.x + 12, y = p.y + 40;

            GUI.Label(new Rect(x, y + 3, 60, 20), _ctx.Loc.Get("ui.me.search"), _small);
            _jeiSearch = GUI.TextField(new Rect(x + 56, y, listW - 56, 24), _jeiSearch ?? "", 32);
            y += 30;

            // Список всех предметов
            var items = new List<int>();
            string q = (_jeiSearch ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < _ctx.Content.Items.Count; i++)
                if (q.Length == 0 || _ctx.ItemName(i).ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0) items.Add(i);
            var view = new Rect(x, y, listW, p.yMax - y - 12);
            int rows = (items.Count + Cols - 1) / Cols;
            _jeiListScroll = GUI.BeginScrollView(view, _jeiListScroll, new Rect(0, 0, listW - 18, rows * (Slot + Gap)));
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rect((i % Cols) * (Slot + Gap), (i / Cols) * (Slot + Gap), Slot, Slot);
                if (DrawSlot(r, new Simulation.ItemStack(items[i], 1), items[i] == _jeiItem))
                {
                    _jeiHistory.Clear();
                    ShowRecipesFor(items[i]);
                }
            }
            GUI.EndScrollView();

            // Правая часть
            var right = new Rect(x + listW + 14, p.y + 40, p.xMax - 12 - (x + listW + 14), h - 52);
            Fill(right, new Color(1, 1, 1, 0.03f));
            if (_jeiItem < 0)
            {
                GUI.Label(new Rect(right.x + 12, right.y + 12, right.width - 24, 40), _ctx.Loc.Get("ui.jei.hint"), new GUIStyle(_small) { wordWrap = true });
                DrawTooltip();
                return;
            }

            float ry = right.y + 8;
            if (_jeiHistory.Count > 0 && GUI.Button(new Rect(right.x + 8, ry, 34, 34), "←")) _jeiItem = _jeiHistory.Pop();
            var icon = _ctx.IconOf(_jeiItem);
            if (icon != null) GUI.DrawTexture(new Rect(right.x + 48, ry, 34, 34), icon, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(right.x + 90, ry + 6, right.width - 100, 24), $"<b>{_ctx.ItemName(_jeiItem)}</b>  <size=11><color=#969aa0>{_ctx.Content.Items.IdOf(_jeiItem)}</color></size>", _label);
            ry += 42;
            if (TabButton(new Rect(right.x + 8, ry, 170, 24), _ctx.Loc.Get("ui.jei.how"), _jeiTab == 0)) { _jeiTab = 0; _jeiScroll = Vector2.zero; }
            if (TabButton(new Rect(right.x + 182, ry, 170, 24), _ctx.Loc.Get("ui.jei.uses"), _jeiTab == 1)) { _jeiTab = 1; _jeiScroll = Vector2.zero; }
            ry += 32;

            var entries = _jeiTab == 0 ? HowToMake(_jeiItem) : WhereUsed(_jeiItem);
            var rv = new Rect(right.x + 4, ry, right.width - 8, right.yMax - ry - 4);
            float total = 0;
            foreach (var en in entries) total += EntryHeight(en, false);
            _jeiScroll = GUI.BeginScrollView(rv, _jeiScroll, new Rect(0, 0, rv.width - 18, Mathf.Max(1, total)));
            if (entries.Count == 0) GUI.Label(new Rect(8, 8, rv.width - 30, 22), _ctx.Loc.Get("ui.jei.none"), _small);
            float ey = 0;
            foreach (var en in entries)
            {
                float eh = EntryHeight(en, false);
                DrawEntry(en, new Rect(0, ey, rv.width - 18, eh - 6), false, 0, it => ShowRecipesFor(it));
                ey += eh;
            }
            GUI.EndScrollView();
            HandleRecipeKeys(false);
            DrawTooltip();
        }

        private sealed class JeiEntry
        {
            public string Where;          // машина / станция / источник
            public int WhereIcon = -1;
            public string Note;           // время, мини-игра
            public bool Locked;
            public string LockedBy;
            public readonly List<KeyValuePair<int, int>> Inputs = new List<KeyValuePair<int, int>>();
            public readonly List<string> FluidInputs = new List<string>();
            public readonly List<KeyValuePair<int, int>> Outputs = new List<KeyValuePair<int, int>>();
            /// <summary>Рецепт сетки — рисуется раскладкой.</summary>
            public CraftDef Craft;
        }

        private JeiEntry FromRecipe(RecipeDef r)
        {
            var machine = _ctx.Content.Machines.Get(r.Machine);
            var e = new JeiEntry
            {
                Where = machine != null ? _ctx.Loc.Get(machine.NameKey) : r.Machine,
                WhereIcon = machine != null ? _ctx.Content.Items.IndexOf(machine.Id) : -1,
                Note = $"{r.TimeTicks / 20f:0.##} s" + (r.SourceCraft != null ? " · " + _ctx.Loc.Get("ui.jei.auto") : ""),
                Locked = !_ctx.Session.Knowledge.IsRecipeUnlocked(r)
            };
            if (e.Locked)
            {
                var gate = r.SourceCraft != null ? _ctx.Session.Knowledge.GateOfCraft(r.SourceCraft) : _ctx.Session.Knowledge.GateOfRecipe(r.Id);
                e.LockedBy = gate != null ? _ctx.Loc.Get(gate.NameKey) : null;
            }
            foreach (var i in r.Inputs) e.Inputs.Add(new KeyValuePair<int, int>(i.ItemId, i.Count));
            foreach (var f in r.FluidInputs)
            {
                var fd = _ctx.Content.Fluids.Get(f.FluidId);
                e.FluidInputs.Add($"{(fd != null ? _ctx.Loc.Get(fd.NameKey) : f.Fluid)} {f.Amount} mB");
            }
            foreach (var o in r.Outputs) e.Outputs.Add(new KeyValuePair<int, int>(o.ItemId, o.Count));
            foreach (var b in r.Byproducts) e.Outputs.Add(new KeyValuePair<int, int>(b.ItemId, b.Count));
            return e;
        }

        private JeiEntry FromCraft(CraftDef c)
        {
            string where;
            int icon = -1;
            if (c.IsHand) where = _ctx.Loc.Get("ui.jei.hand");
            else
            {
                var st = _ctx.Content.Machines.Get(c.Station);
                where = st != null ? _ctx.Loc.Get(st.NameKey) : c.Station;
                icon = st != null ? _ctx.Content.Items.IndexOf(st.Id) : -1;
            }
            var e = new JeiEntry
            {
                Craft = c,
                Where = where,
                WhereIcon = icon,
                Note = c.Minigame != null ? _ctx.Loc.Get("ui.jei.minigame." + c.Minigame.Template) + " · " + new string('*', Mathf.Clamp(c.Minigame.Difficulty, 1, 5)) : _ctx.Loc.Get("ui.jei.manual"),
                Locked = !_ctx.Session.Knowledge.IsCraftUnlocked(c)
            };
            if (e.Locked)
            {
                var gate = _ctx.Session.Knowledge.GateOfCraft(c.Id);
                e.LockedBy = gate != null ? _ctx.Loc.Get(gate.NameKey) : null;
            }
            foreach (var i in c.Inputs) e.Inputs.Add(new KeyValuePair<int, int>(i.ItemId, i.Count));
            foreach (var o in c.Outputs) e.Outputs.Add(new KeyValuePair<int, int>(o.ItemId, o.Count));
            return e;
        }

        private List<JeiEntry> HowToMake(int item)
        {
            var list = new List<JeiEntry>();
            foreach (var c in _ctx.Content.Crafts.Items)
                if (c.Outputs.Exists(o => o.ItemId == item)) list.Add(FromCraft(c));
            foreach (var r in _ctx.Content.Recipes.Items)
                if (r.Outputs.Exists(o => o.ItemId == item) || r.Byproducts.Exists(o => o.ItemId == item)) list.Add(FromRecipe(r));

            // Источники в мире: из каких блоков выпадает, залежи под буром, враги
            string id = _ctx.Content.Items.IdOf(item);
            foreach (var b in _ctx.Content.Blocks.Items)
            {
                if (b.Drop != id || b.Id == id || b.Liquid) continue;
                var e = new JeiEntry { Where = _ctx.Loc.Get("ui.jei.mined"), Note = _ctx.Loc.Get(b.NameKey) + (b.DepositOf == id ? " · " + _ctx.Loc.Get("ui.jei.deposit") : "") };
                int bi = _ctx.Content.Items.IndexOf(b.Id);
                if (bi >= 0) e.Inputs.Add(new KeyValuePair<int, int>(bi, 1));
                e.Outputs.Add(new KeyValuePair<int, int>(item, Math.Max(1, b.DropCount)));
                list.Add(e);
            }
            foreach (var en in _ctx.Content.Enemies.Items)
                foreach (var d in en.Drops)
                    if (d.ItemId == item)
                    {
                        var e = new JeiEntry { Where = _ctx.Loc.Get("ui.jei.enemy"), Note = $"{_ctx.Loc.Get(en.NameKey)} · {d.Chance * 100:0}%" };
                        e.Outputs.Add(new KeyValuePair<int, int>(item, d.Count));
                        list.Add(e);
                    }
            return list;
        }

        private List<JeiEntry> WhereUsed(int item)
        {
            var list = new List<JeiEntry>();
            foreach (var c in _ctx.Content.Crafts.Items)
                if (c.IsGrid ? Genesis.Gameplay.GridCrafting.Uses(c, item) : c.Inputs.Exists(i => i.ItemId == item)) list.Add(FromCraft(c));
            foreach (var r in _ctx.Content.Recipes.Items)
                if (r.Inputs.Exists(i => i.ItemId == item)) list.Add(FromRecipe(r));
            var def = _ctx.Content.Item(item);
            if (def != null && def.IsFuel)
            {
                var e = new JeiEntry { Where = _ctx.Loc.Get("ui.fuel"), Note = $"{def.FuelTicks / 20f:0.#} s" };
                e.Inputs.Add(new KeyValuePair<int, int>(item, 1));
                list.Add(e);
            }
            return list;
        }

        private void DrawJeiEntry(JeiEntry e, Rect r)
        {
            UiSkin.DrawSlot(r, false);
            if (e.Locked) Fill(new Rect(r.x, r.y, 3, r.height), UiSkin.Danger);

            // Где: иконка и название
            if (e.WhereIcon >= 0)
            {
                var wi = _ctx.IconOf(e.WhereIcon);
                if (wi != null) GUI.DrawTexture(new Rect(r.x + 8, r.y + 8, 28, 28), wi, ScaleMode.ScaleToFit);
            }
            GUI.Label(new Rect(r.x + 42, r.y + 6, 180, 20), e.Where, _small);
            string note = e.Note ?? "";
            if (e.Locked) note = $"<color=#eb5c50>{_ctx.Loc.Get("ui.locked")}{(e.LockedBy != null ? ": " + e.LockedBy : "")}</color>";
            GUI.Label(new Rect(r.x + 42, r.y + 26, 200, 30), $"<size=10><color=#969aa0>{note}</color></size>", new GUIStyle(_small) { wordWrap = true });

            float x = r.x + 250;
            const float S = 34;
            foreach (var kv in e.Inputs) x = JeiItem(kv.Key, kv.Value, x, r.y + 13, S);
            foreach (var f in e.FluidInputs)
            {
                GUI.Label(new Rect(x, r.y + 20, 120, 20), $"<size=10>{f}</size>", _small);
                x += 110;
            }
            GUI.Label(new Rect(x, r.y + 18, 24, 24), "→", _center);
            x += 26;
            foreach (var kv in e.Outputs) x = JeiItem(kv.Key, kv.Value, x, r.y + 13, S);
        }

        private float JeiItem(int item, int count, float x, float y, float s)
        {
            var r = new Rect(x, y, s, s);
            bool hover = r.Contains(Event.current.mousePosition);
            UiSkin.DrawSlot(r, hover);
            var icon = _ctx.IconOf(item);
            if (icon != null) GUI.DrawTexture(new Rect(r.x + 4, r.y + 4, s - 8, s - 8), icon, ScaleMode.ScaleToFit);
            if (count > 1) GUI.Label(new Rect(r.x, r.y, r.width - 2, r.height), count.ToString(), _count);
            if (hover)
            {
                _tooltip = _ctx.ItemName(item);
                if (Event.current.type == EventType.MouseDown && item != _jeiItem)
                {
                    Event.current.Use();
                    ShowRecipesFor(item);
                }
            }
            return x + s + 4;
        }
    }
}
