using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Инвентарь и верстак в стиле Minecraft: сетка крафта 2×2 / 3×3 с результатом, курсор с предметом
    /// (ЛКМ — взять/положить, ПКМ — половину/по одному, Shift — быстрый перенос, клик мимо окна — выбросить),
    /// справа — справочник (JEI): список предметов с поиском, рецепты с раскладкой и кнопкой «+», которая
    /// перекладывает ингредиенты в сетку. Наведи на предмет и нажми R — как сделать, U — где используется.
    /// </summary>
    public sealed partial class GameHud
    {
        private int _gridOpen;              // 0 — окно сетки закрыто, 2 или 3 — открыто
        private Int3 _gridStation;
        private int _hoverItem = -1;
        private int _panelItem = -1;        // предмет, открытый в боковом справочнике
        private int _panelTab;              // 0 — как сделать, 1 — где используется
        private int _panelPage;
        private string _panelSearch = "";
        private Vector2 _panelScroll;
        private readonly Stack<int> _panelHistory = new Stack<int>();
        private Rect _windowRect, _panelRect;

        private const float Cell = 30f;

        // ============================================================ Синхронизация окна с сервером

        private void SyncGridWindow()
        {
            int want = 0;
            Int3 station = default;
            if (_ctx.Window == UiWindow.Inventory) want = 2;
            else if (_ctx.Window == UiWindow.Machine)
            {
                var m = _ctx.Session.Factory.MachineAt(_ctx.OpenMachine);
                if (m != null && m.Def.CraftGrid >= 3)
                {
                    want = 3;
                    station = m.Pos;
                }
            }
            if (want == _gridOpen && (want != 3 || station == _gridStation)) return;
            if (want == 0) _ctx.Send(new OpenGridCommand { Open = false });
            else _ctx.Send(new OpenGridCommand { Open = true, Size = want, Station = station });
            _gridOpen = want;
            _gridStation = station;
        }

        // ============================================================ Окно

        private void DrawGridWindow(int size, string title)
        {
            float w = GridWidth + 24;
            float craftH = 3 * (Slot + Gap) + 4;
            float h = 44 + craftH + 18 + GridHeight + 16;
            float panelW = Mathf.Clamp(Screen.width - w - 60, 240, 380);
            var p = Panel(w, h, title, -(panelW + 12) / 2f);
            _windowRect = p;
            var player = _ctx.Player;

            // ---- Сетка крафта
            float gy = p.y + 44;
            float gx = p.x + 20 + (size == 2 ? (Slot + Gap) * 0.5f : 0f);
            float gyy = gy + (size == 2 ? (Slot + Gap) * 0.5f : 0f);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = y * 3 + x;
                var r = new Rect(gx + x * (Slot + Gap), gyy + y * (Slot + Gap), Slot, Slot);
                int click = ClickSlot(r, player.CraftGrid[i], false);
                if (click > 0) _ctx.Send(new SlotClickCommand { Container = SlotContainer.Grid, Slot = i, Button = click - 1, Shift = Event.current.shift });
            }

            // ---- Результат
            var match = GridCrafting.Match(_ctx.Content, player.CraftGrid, size);
            float ax = gx + size * (Slot + Gap) + 10;
            float midY = gy + 1.5f * (Slot + Gap) - Slot / 2f;
            GUI.Label(new Rect(ax, midY, 36, Slot), "→", new GUIStyle(_title) { alignment = TextAnchor.MiddleCenter });
            var rr = new Rect(ax + 44, midY - 6, Slot + 12, Slot + 12);
            bool locked = match != null && !_ctx.Session.Knowledge.IsCraftUnlocked(match.Craft);
            var result = match != null ? new ItemStack(match.Craft.Outputs[0].ItemId, match.Craft.Outputs[0].Count) : ItemStack.Empty;
            int rc = ClickSlot(rr, result, match != null && !locked);
            if (locked) Fill(rr, new Color(0.05f, 0.05f, 0.06f, 0.65f));
            if (rc > 0 && match != null) TakeResult(match, Event.current.shift);

            // Подпись к результату: мастерство, мини-игра, замок
            if (match != null)
            {
                var c = match.Craft;
                float tx = rr.xMax + 10, tw = p.xMax - 12 - tx;
                if (locked)
                {
                    var gate = _ctx.Session.Knowledge.GateOfCraft(c.Id);
                    GUI.Label(new Rect(tx, rr.y - 4, tw, 40), $"<color=#eb5c50>{_ctx.Loc.Get("ui.locked")}</color>\n<size=10>{(gate != null ? _ctx.Loc.Get(gate.NameKey) : "")}</size>",
                        new GUIStyle(_small) { wordWrap = true });
                }
                else
                {
                    DrawMastery(new Rect(tx, rr.y + 2, tw, 14), c);
                    if (c.Minigame != null)
                        GUI.Label(new Rect(tx, rr.y + 18, tw, 34), $"<size=10><color=#f2a33a>{_ctx.Loc.Get("ui.jei.minigame." + c.Minigame.Template)}</color></size>",
                            new GUIStyle(_small) { wordWrap = true });
                }
            }
            else if (size == 2)
            {
                GUI.Label(new Rect(ax + 44, midY + Slot + 12, p.xMax - ax - 56, 40), $"<size=10><color=#969aa0>{_ctx.Loc.Get("ui.grid.hint")}</color></size>",
                    new GUIStyle(_small) { wordWrap = true });
            }

            // ---- Инвентарь игрока
            float iy = gy + craftH + 18;
            var inv = player.Inventory;
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 9; col++)
            {
                int slot = PlayerState.HotbarSize + row * 9 + col;
                var r = new Rect(p.x + 12 + col * (Slot + Gap), iy + row * (Slot + Gap), Slot, Slot);
                int click = ClickSlot(r, inv[slot], false);
                if (click > 0) _ctx.Send(new SlotClickCommand { Container = SlotContainer.Inventory, Slot = slot, Button = click - 1, Shift = Event.current.shift });
            }
            float hy = iy + 3 * (Slot + Gap) + 8;
            for (int col = 0; col < 9; col++)
            {
                var r = new Rect(p.x + 12 + col * (Slot + Gap), hy, Slot, Slot);
                int click = ClickSlot(r, inv[col], col == player.SelectedSlot);
                if (click > 0) _ctx.Send(new SlotClickCommand { Container = SlotContainer.Inventory, Slot = col, Button = click - 1, Shift = Event.current.shift });
            }

            // ---- Справочник справа
            _panelRect = new Rect(p.xMax + 12, Mathf.Max(20, p.y - 40), panelW, Mathf.Min(Screen.height - 40, h + 80));
            DrawJeiPanel(_panelRect, size);

            // ---- Клик мимо окна — выбросить то, что на курсоре
            var e = Event.current;
            if (e.type == EventType.MouseDown && !_windowRect.Contains(e.mousePosition) && !_panelRect.Contains(e.mousePosition) && !player.Cursor[0].IsEmpty)
            {
                _ctx.Send(new DropCursorCommand { All = e.button == 0 });
                e.Use();
            }
            HandleRecipeKeys(_panelRect.width > 0);
            DrawTooltip();
        }

        private void TakeResult(GridMatch match, bool shift)
        {
            var c = match.Craft;
            if (!_ctx.Session.Knowledge.IsCraftUnlocked(c))
            {
                _ctx.ShowServerToast("ui.locked");
                return;
            }
            if (c.Minigame != null)
            {
                if (_ctx.InMinigame) return;
                // Мастерство упрощает мини-игру
                var spec = new MinigameSpec
                {
                    Template = c.Minigame.Template,
                    Difficulty = Mathf.Max(1, c.Minigame.Difficulty - _ctx.Player.MasteryLevel(c.Id))
                };
                var parts = new List<int>();
                foreach (var i in c.Inputs) parts.Add(i.ItemId);
                _ctx.Minigame = Minigame.Create(spec, parts);
                _ctx.MinigameCraft = c;
                _ctx.MinigameGrid = true;
                _ctx.MinigameCloseAt = 0;
                return;
            }
            _ctx.Send(new GridCraftCommand { Shift = shift });
        }

        /// <summary>Мастерство рецепта: три деления (16, 64, 256 крафтов) и счётчик.</summary>
        private void DrawMastery(Rect r, CraftDef c)
        {
            _ctx.Player.Mastery.TryGetValue(c.Id, out int n);
            int level = _ctx.Player.MasteryLevel(c.Id);
            GUI.Label(new Rect(r.x, r.y - 2, 90, 16), $"<size=10>{_ctx.Loc.Get("ui.mastery")}</size>", _small);
            for (int i = 0; i < 3; i++)
            {
                var pip = new Rect(r.x + 90 + i * 12, r.y + 2, 9, 9);
                Fill(pip, UiSkin.PanelDark);
                Fill(new Rect(pip.x + 1, pip.y + 1, 7, 7), i < level ? UiSkin.Accent : new Color(0.2f, 0.22f, 0.25f));
            }
            int next = level == 0 ? 16 : (level == 1 ? 64 : (level == 2 ? 256 : 0));
            if (next > 0) GUI.Label(new Rect(r.x + 128, r.y - 2, 60, 16), $"<size=10><color=#969aa0>{n}/{next}</color></size>", _small);
        }

        // ============================================================ Слоты с ЛКМ/ПКМ

        /// <summary>Нарисовать слот; 0 — нет клика, 1 — ЛКМ, 2 — ПКМ.</summary>
        private int ClickSlot(Rect r, ItemStack stack, bool highlight)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            UiSkin.DrawSlot(r, hover);
            if (highlight)
            {
                Fill(new Rect(r.x, r.y, r.width, 2), Accent);
                Fill(new Rect(r.x, r.yMax - 2, r.width, 2), Accent);
                Fill(new Rect(r.x, r.y, 2, r.height), Accent);
                Fill(new Rect(r.xMax - 2, r.y, 2, r.height), Accent);
            }
            DrawStackIcon(r, stack);
            if (hover && !stack.IsEmpty)
            {
                _tooltip = _ctx.ItemName(stack.Item);
                _hoverItem = stack.Item;
            }
            var e = Event.current;
            if (hover && e.type == EventType.MouseDown && (e.button == 0 || e.button == 1))
            {
                e.Use();
                return e.button + 1;
            }
            return 0;
        }

        private void DrawStackIcon(Rect r, ItemStack stack)
        {
            if (stack.IsEmpty) return;
            var icon = _ctx.IconOf(stack.Item);
            float pad = r.width * 0.14f;
            if (icon != null) GUI.DrawTexture(new Rect(r.x + pad, r.y + pad, r.width - pad * 2, r.height - pad * 2), icon, ScaleMode.ScaleToFit);
            if (stack.Count > 1) UiSkin.Shadowed(new Rect(r.x, r.y, r.width - 4, r.height - 2), stack.Count.ToString(), _count);
            var tool = _ctx.Content.Item(stack.Item)?.Tool;
            if (tool != null && stack.Meta > 0)
            {
                float left = 1f - (float)stack.Meta / tool.Durability;
                var bar = new Rect(r.x + 5, r.yMax - 7, r.width - 10, 3);
                Fill(bar, new Color(0, 0, 0, 0.8f));
                Fill(new Rect(bar.x, bar.y, bar.width * left, bar.height), Color.Lerp(new Color(0.9f, 0.2f, 0.1f), new Color(0.3f, 0.9f, 0.3f), left));
            }
        }

        /// <summary>Предмет на курсоре — рисуется последним, поверх всего.</summary>
        private void DrawCursorStack()
        {
            if (_ctx.Window == UiWindow.None) return;
            var st = _ctx.Player.Cursor[0];
            if (st.IsEmpty) return;
            var m = Event.current.mousePosition;
            DrawStackIcon(new Rect(m.x - Slot / 2f, m.y - Slot / 2f, Slot, Slot), st);
        }

        // ============================================================ R / U — рецепты под курсором

        private void HandleRecipeKeys(bool panel)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || _hoverItem < 0) return;
            if (GUIUtility.keyboardControl != 0) return; // печатают в поиске
            if (e.keyCode != KeyCode.R && e.keyCode != KeyCode.U) return;
            int tab = e.keyCode == KeyCode.R ? 0 : 1;
            if (panel) OpenPanelItem(_hoverItem, tab);
            else
            {
                _jeiTab = tab;
                ShowRecipesFor(_hoverItem);
                _ctx.Window = UiWindow.Recipes;
            }
            e.Use();
        }

        private void OpenPanelItem(int item, int tab)
        {
            if (_panelItem >= 0 && _panelItem != item) _panelHistory.Push(_panelItem);
            _panelItem = item;
            _panelTab = tab;
            _panelScroll = Vector2.zero;
        }

        // ============================================================ Боковой справочник

        private void DrawJeiPanel(Rect r, int gridSize)
        {
            UiSkin.DrawPanel(r);
            float x = r.x + 8, y = r.y + 8, w = r.width - 16;

            if (_panelItem >= 0)
            {
                if (GUI.Button(new Rect(x, y, 30, 28), "←"))
                {
                    _panelItem = _panelHistory.Count > 0 ? _panelHistory.Pop() : -1;
                    _panelScroll = Vector2.zero;
                    return;
                }
                var icon = _ctx.IconOf(_panelItem);
                if (icon != null) GUI.DrawTexture(new Rect(x + 36, y, 28, 28), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(x + 70, y + 4, w - 70, 22), $"<b>{_ctx.ItemName(_panelItem)}</b>", _small);
                y += 34;
                float tw = (w - 4) / 2f;
                if (TabButton(new Rect(x, y, tw, 22), _ctx.Loc.Get("ui.jei.how") + " (R)", _panelTab == 0)) { _panelTab = 0; _panelScroll = Vector2.zero; }
                if (TabButton(new Rect(x + tw + 4, y, tw, 22), _ctx.Loc.Get("ui.jei.uses") + " (U)", _panelTab == 1)) { _panelTab = 1; _panelScroll = Vector2.zero; }
                y += 28;

                var entries = _panelTab == 0 ? HowToMake(_panelItem) : WhereUsed(_panelItem);
                float total = 0;
                foreach (var en in entries) total += EntryHeight(en, true);
                var view = new Rect(x, y, w, r.yMax - y - 8);
                _panelScroll = GUI.BeginScrollView(view, _panelScroll, new Rect(0, 0, w - 18, Mathf.Max(total, 1)));
                if (entries.Count == 0) GUI.Label(new Rect(4, 4, w - 24, 22), _ctx.Loc.Get("ui.jei.none"), _small);
                float ey = 0;
                foreach (var en in entries)
                {
                    float eh = EntryHeight(en, true);
                    DrawEntry(en, new Rect(0, ey, w - 18, eh - 6), true, gridSize, it => OpenPanelItem(it, 0));
                    ey += eh;
                }
                GUI.EndScrollView();
                return;
            }

            // Список предметов с поиском и страницами
            GUI.SetNextControlName("jei_search");
            string q = GUI.TextField(new Rect(x, y, w - 70, 24), _panelSearch ?? "", 32);
            if (q != _panelSearch)
            {
                _panelSearch = q;
                _panelPage = 0;
            }
            var items = FilterItems(_panelSearch);
            int cols = Mathf.Max(1, Mathf.FloorToInt((w + Gap) / (Slot + Gap)));
            int rows = Mathf.Max(1, Mathf.FloorToInt((r.yMax - y - 40) / (Slot + Gap)));
            int perPage = cols * rows;
            int pages = Mathf.Max(1, (items.Count + perPage - 1) / perPage);
            _panelPage = Mathf.Clamp(_panelPage, 0, pages - 1);
            if (GUI.Button(new Rect(x + w - 64, y, 30, 24), "<")) _panelPage = (_panelPage + pages - 1) % pages;
            if (GUI.Button(new Rect(x + w - 30, y, 30, 24), ">")) _panelPage = (_panelPage + 1) % pages;
            y += 30;
            for (int k = 0; k < perPage; k++)
            {
                int idx = _panelPage * perPage + k;
                if (idx >= items.Count) break;
                var sr = new Rect(x + (k % cols) * (Slot + Gap), y + (k / cols) * (Slot + Gap), Slot, Slot);
                int click = ClickSlot(sr, new ItemStack(items[idx], 1), false);
                if (click == 1) OpenPanelItem(items[idx], 0);
                else if (click == 2) OpenPanelItem(items[idx], 1);
            }
            GUI.Label(new Rect(x, r.yMax - 24, w, 18), $"<size=10><color=#969aa0>{_panelPage + 1}/{pages} · {_ctx.Loc.Get("ui.jei.panel_hint")}</color></size>", _small);
            // Колесо мыши листает страницы
            var e = Event.current;
            if (e.type == EventType.ScrollWheel && r.Contains(e.mousePosition))
            {
                _panelPage = Mathf.Clamp(_panelPage + (e.delta.y > 0 ? 1 : -1), 0, pages - 1);
                e.Use();
            }
        }

        private List<int> FilterItems(string search)
        {
            var list = new List<int>();
            string q = (search ?? "").Trim().ToLowerInvariant();
            for (int i = 0; i < _ctx.Content.Items.Count; i++)
                if (q.Length == 0 || _ctx.ItemName(i).ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0) list.Add(i);
            return list;
        }

        // ============================================================ Запись справочника

        private static float EntryHeight(JeiEntry e, bool compact) =>
            e.Craft != null && e.Craft.IsGrid ? 3 * Cell + 34 : (compact ? 74 : 64);

        /// <summary>
        /// Запись справочника: рецепт сетки — раскладкой 3×3 с кнопкой «+» (разложить в открытую сетку,
        /// Shift — на максимум), остальное — строкой «откуда: входы → выходы».
        /// </summary>
        private void DrawEntry(JeiEntry e, Rect r, bool compact, int gridSize, Action<int> onItem)
        {
            UiSkin.DrawSlot(r, false);
            if (e.Locked) Fill(new Rect(r.x, r.y, 3, r.height), UiSkin.Danger);
            if (e.Craft != null && e.Craft.IsGrid)
            {
                DrawGridRecipe(e, r, gridSize, onItem);
                return;
            }
            if (!compact)
            {
                DrawJeiEntry(e, r);
                return;
            }
            GUI.Label(new Rect(r.x + 8, r.y + 4, r.width - 16, 18), $"<size=11>{e.Where}</size>", _small);
            string note = e.Note ?? "";
            if (e.Locked) note = $"<color=#eb5c50>{_ctx.Loc.Get("ui.locked")}{(e.LockedBy != null ? ": " + e.LockedBy : "")}</color>";
            GUI.Label(new Rect(r.x + 8, r.y + 20, r.width - 16, 16), $"<size=10><color=#969aa0>{note}</color></size>", _small);
            float x = r.x + 8;
            foreach (var kv in e.Inputs) x = MiniItem(kv.Key, kv.Value, x, r.y + 38, 28, onItem);
            if (e.FluidInputs.Count > 0)
            {
                GUI.Label(new Rect(x, r.y + 44, 90, 18), $"<size=9>{string.Join(", ", e.FluidInputs)}</size>", _small);
                x += 90;
            }
            GUI.Label(new Rect(x, r.y + 40, 20, 24), "→", _center);
            x += 20;
            foreach (var kv in e.Outputs) x = MiniItem(kv.Key, kv.Value, x, r.y + 38, 28, onItem);
        }

        private void DrawGridRecipe(JeiEntry e, Rect r, int gridSize, Action<int> onItem)
        {
            var c = e.Craft;
            float gx = r.x + 8, gy = r.y + 24;
            GUI.Label(new Rect(r.x + 8, r.y + 3, r.width - 50, 18),
                $"<size=11>{(c.GridSize == 2 ? _ctx.Loc.Get("ui.grid.any") : _ctx.Loc.Get("ui.grid.bench"))}</size>" +
                (c.Minigame != null ? $"<size=10><color=#f2a33a> · {_ctx.Loc.Get("ui.jei.minigame." + c.Minigame.Template)}</color></size>" : "") +
                (c.Shapeless ? $"<size=10><color=#969aa0> · {_ctx.Loc.Get("ui.grid.shapeless")}</color></size>" : ""), _small);

            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                var cr = new Rect(gx + x * Cell, gy + y * Cell, Cell - 2, Cell - 2);
                bool hover = cr.Contains(Event.current.mousePosition);
                UiSkin.DrawSlot(cr, hover);
                GridIngredient ing = null;
                if (c.Shapeless)
                {
                    int i = y * 3 + x;
                    if (i < c.Cells.Length) ing = c.Cells[i];
                }
                else if (x < c.GridW && y < c.GridH) ing = c.Cells[y * c.GridW + x];
                if (ing == null || ing.Matches.Length == 0) continue;
                // Тег — перебираем подходящие предметы раз в секунду
                int item = ing.Matches[(int)(Time.realtimeSinceStartup) % ing.Matches.Length];
                var icon = _ctx.IconOf(item);
                if (icon != null) GUI.DrawTexture(new Rect(cr.x + 3, cr.y + 3, cr.width - 6, cr.height - 6), icon, ScaleMode.ScaleToFit);
                if (ing.Count > 1) UiSkin.Shadowed(new Rect(cr.x, cr.y, cr.width - 2, cr.height - 1), ing.Count.ToString(), _count);
                if (ing.Keep) Fill(new Rect(cr.x + 2, cr.y + 2, 5, 5), UiSkin.Teal); // катализатор
                if (hover)
                {
                    string name = ing.Tag != null ? $"#{ing.Tag}: {_ctx.ItemName(item)}" : _ctx.ItemName(item);
                    if (ing.Keep) name += " — " + _ctx.Loc.Get("ui.grid.keep");
                    _tooltip = name;
                    if (Event.current.type == EventType.MouseDown)
                    {
                        Event.current.Use();
                        onItem(item);
                    }
                }
            }

            float ox = gx + 3 * Cell + 12, oy = gy + Cell - 4;
            GUI.Label(new Rect(ox, oy, 22, Cell), "→", _center);
            ox += 24;
            foreach (var o in c.Outputs) ox = MiniItem(o.ItemId, o.Count, ox, oy, Cell + 6, onItem);

            if (e.Locked)
            {
                GUI.Label(new Rect(gx + 3 * Cell + 12, gy + 2 * Cell + 4, r.xMax - gx - 3 * Cell - 20, 30),
                    $"<size=10><color=#eb5c50>{_ctx.Loc.Get("ui.locked")}{(e.LockedBy != null ? ": " + e.LockedBy : "")}</color></size>", new GUIStyle(_small) { wordWrap = true });
                return;
            }
            DrawMastery(new Rect(gx + 3 * Cell + 12, gy + 2 * Cell + 8, 200, 14), c);

            // «+» — разложить в сетку (если окно сетки открыто и рецепт в неё влезает)
            if (gridSize > 0)
            {
                var plus = new Rect(r.xMax - 36, r.y + 4, 30, 24);
                bool fits = c.GridSize <= gridSize;
                GUI.enabled = fits;
                if (GUI.Button(plus, "+"))
                    _ctx.Send(new FillGridCommand { CraftId = c.NumericId, Max = Event.current.shift });
                GUI.enabled = true;
                if (plus.Contains(Event.current.mousePosition))
                    _tooltip = fits ? _ctx.Loc.Get("ui.grid.fill") : _ctx.Loc.Get("ui.grid.need_bench");
            }
        }

        private float MiniItem(int item, int count, float x, float y, float s, Action<int> onItem)
        {
            var r = new Rect(x, y, s, s);
            bool hover = r.Contains(Event.current.mousePosition);
            UiSkin.DrawSlot(r, hover);
            var icon = _ctx.IconOf(item);
            if (icon != null) GUI.DrawTexture(new Rect(r.x + 3, r.y + 3, s - 6, s - 6), icon, ScaleMode.ScaleToFit);
            if (count > 1) UiSkin.Shadowed(new Rect(r.x, r.y, r.width - 2, r.height - 1), count.ToString(), _count);
            if (hover)
            {
                _tooltip = _ctx.ItemName(item);
                _hoverItem = item;
                if (Event.current.type == EventType.MouseDown)
                {
                    Event.current.Use();
                    onItem(item);
                }
            }
            return x + s + 3;
        }
    }
}
