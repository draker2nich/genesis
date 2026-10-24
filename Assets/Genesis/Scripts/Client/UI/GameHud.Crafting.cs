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
    /// Инвентарь и верстак в стиле Minecraft: сетка крафта 2×2 / 3×3 с результатом, слот левой руки, курсор
    /// с предметом (ЛКМ — взять/положить, ПКМ — половину/по одному, Shift — быстрый перенос, клик мимо окна —
    /// выбросить). Окна рецептов (JEI) нет: механики объясняет справочник (Q).
    /// </summary>
    public sealed partial class GameHud
    {
        private int _gridOpen;              // 0 — окно сетки закрыто, 2 или 3 — открыто
        private Int3 _gridStation;
        private int _hoverItem = -1;
        private Rect _windowRect;

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
            var p = Panel(w, h, title);
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

            // ---- Левая рука (под результатом крафта)
            var offRect = new Rect(rr.x + 6, gy + craftH - Slot, Slot, Slot);
            GUI.Label(new Rect(offRect.xMax + 8, offRect.y + 13, 160, 18), $"<size=11><color=#969aa0>{_ctx.Loc.Get("ui.offhand")} (F)</color></size>", _small);
            int oc = ClickSlot(offRect, player.Offhand[0], false);
            if (oc > 0) _ctx.Send(new SlotClickCommand { Container = SlotContainer.Offhand, Slot = 0, Button = oc - 1, Shift = Event.current.shift });

            // ---- Клик мимо окна — выбросить то, что на курсоре
            var e = Event.current;
            if (e.type == EventType.MouseDown && !_windowRect.Contains(e.mousePosition) && !player.Cursor[0].IsEmpty)
            {
                _ctx.Send(new DropCursorCommand { All = e.button == 0 });
                e.Use();
            }
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
    }
}
