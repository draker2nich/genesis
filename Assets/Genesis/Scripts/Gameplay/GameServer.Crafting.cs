using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;

namespace Genesis.Gameplay
{
    /// <summary>Контейнеры окон игрока для кликов по слотам.</summary>
    public enum SlotContainer : byte
    {
        Inventory = 0,
        Grid = 1,
        Result = 2,
        /// <summary>Слот левой руки.</summary>
        Offhand = 3
    }

    /// <summary>
    /// Окна в стиле Minecraft: курсор с предметом, клики ЛКМ/ПКМ/Shift, сетка крафта 2×2 (инвентарь) и 3×3 (верстак),
    /// крафт с результата, заполнение сетки из справочника, выброс предметов в мир.
    /// Особенности Genesis: ингредиенты «по несколько штук в клетке», инструменты-катализаторы (изнашиваются),
    /// мастерство рецепта (шанс бонусного результата и мини-игры проще), мини-игры для сложных сборок.
    /// </summary>
    public sealed partial class GameServer
    {
        private Random64 _craftRng = new Random64(0xC4AF7);

        // ============================================================ Окно сетки

        /// <summary>Открыть сетку: 2 — инвентарь, 3 — верстак в station.</summary>
        public void OpenGrid(PlayerState p, int size, Int3 station)
        {
            if (size == 3 && !IsGridStation(p, station)) size = 2;
            if (size == 2 && p.GridSize == 3)
                for (int i = 0; i < 9; i++)
                    if (i % 3 >= 2 || i / 3 >= 2) ReturnToInventory(p, p.CraftGrid, i);
            p.GridSize = size;
            p.GridStation = station;
        }

        /// <summary>Окно закрыто: сетка и курсор — обратно в инвентарь (не влезло — выпадает под ноги).</summary>
        public void CloseGrid(PlayerState p)
        {
            for (int i = 0; i < 9; i++) ReturnToInventory(p, p.CraftGrid, i);
            ReturnToInventory(p, p.Cursor, 0);
            p.GridSize = 2;
        }

        private void ReturnToInventory(PlayerState p, Inventory from, int slot)
        {
            var st = from[slot];
            if (st.IsEmpty) return;
            int put = p.Inventory.Insert(st);
            from[slot] = st.WithCount(st.Count - put);
            if (put < st.Count) DropFromPlayer(p, st.WithCount(st.Count - put), false);
            from[slot] = ItemStack.Empty;
        }

        private bool IsGridStation(PlayerState p, Int3 station)
        {
            var m = Factory.MachineAt(station);
            return m != null && m.Def.CraftGrid >= 3 && InReach(p, station);
        }

        // ============================================================ Клики по слотам

        public void ClickSlot(PlayerState p, SlotContainer container, int slot, int button, bool shift)
        {
            if (container == SlotContainer.Result)
            {
                CraftFromGrid(p, shift, false, -1);
                return;
            }
            var inv = container == SlotContainer.Grid ? p.CraftGrid : (container == SlotContainer.Offhand ? p.Offhand : p.Inventory);
            if (slot < 0 || slot >= inv.Size) return;
            if (container == SlotContainer.Grid && (slot % 3 >= p.GridSize || slot / 3 >= p.GridSize)) return;

            if (shift)
            {
                QuickMove(p, container, slot);
                return;
            }

            var cur = p.Cursor[0];
            var st = inv[slot];
            int max = inv.MaxStackOf(cur.IsEmpty ? st.Item : cur.Item);
            if (button == 0)
            {
                if (cur.IsEmpty)
                {
                    p.Cursor[0] = st;
                    inv[slot] = ItemStack.Empty;
                }
                else if (st.IsEmpty)
                {
                    int put = Math.Min(max, cur.Count);
                    inv[slot] = cur.WithCount(put);
                    p.Cursor[0] = cur.WithCount(cur.Count - put);
                }
                else if (st.SameKind(cur))
                {
                    int put = Math.Min(max - st.Count, cur.Count);
                    if (put <= 0) return;
                    inv[slot] = st.WithCount(st.Count + put);
                    p.Cursor[0] = cur.WithCount(cur.Count - put);
                }
                else
                {
                    inv[slot] = cur;
                    p.Cursor[0] = st;
                }
            }
            else
            {
                if (cur.IsEmpty)
                {
                    if (st.IsEmpty) return;
                    int half = (st.Count + 1) / 2;
                    p.Cursor[0] = st.WithCount(half);
                    inv[slot] = st.WithCount(st.Count - half);
                }
                else if (st.IsEmpty || (st.SameKind(cur) && st.Count < max))
                {
                    inv[slot] = cur.WithCount((st.IsEmpty ? 0 : st.Count) + 1);
                    p.Cursor[0] = cur.WithCount(cur.Count - 1);
                }
                else if (!st.SameKind(cur))
                {
                    inv[slot] = cur;
                    p.Cursor[0] = st;
                }
            }
        }

        /// <summary>Shift-клик: из сетки — в инвентарь; в инвентаре — между хотбаром и рюкзаком.</summary>
        private void QuickMove(PlayerState p, SlotContainer container, int slot)
        {
            if (container == SlotContainer.Grid || container == SlotContainer.Offhand)
            {
                var src = container == SlotContainer.Grid ? p.CraftGrid : p.Offhand;
                var st = src[slot];
                int put = p.Inventory.Insert(st);
                src[slot] = st.WithCount(st.Count - put);
                return;
            }
            var inv = p.Inventory;
            var s = inv[slot];
            if (s.IsEmpty) return;
            bool hotbar = slot < PlayerState.HotbarSize;
            int from = hotbar ? PlayerState.HotbarSize : 0, to = hotbar ? inv.Size : PlayerState.HotbarSize;
            int left = s.Count;
            int max = inv.MaxStackOf(s.Item);
            for (int pass = 0; pass < 2 && left > 0; pass++)
                for (int i = from; i < to && left > 0; i++)
                {
                    var t = inv[i];
                    if (pass == 0 && (t.IsEmpty || !t.SameKind(s) || t.Count >= max)) continue;
                    if (pass == 1 && !t.IsEmpty) continue;
                    int add = Math.Min(max - (t.IsEmpty ? 0 : t.Count), left);
                    inv[i] = s.WithCount((t.IsEmpty ? 0 : t.Count) + add);
                    left -= add;
                }
            inv[slot] = s.WithCount(left);
        }

        // ============================================================ Крафт с сетки

        /// <summary>Текущий результат сетки игрока (для проверки и предпросмотра).</summary>
        public GridMatch GridResult(PlayerState p) => GridCrafting.Match(Content, p.CraftGrid, p.GridSize);

        /// <summary>
        /// Забрать результат: обычный клик — на курсор (стакается с тем, что уже на нём), Shift — сколько получится
        /// прямо в инвентарь. Рецепт с мини-игрой выдаётся по одному и только после успешной мини-игры.
        /// expectCraft — id рецепта, для которого клиент сыграл мини-игру (-1 — любой).
        /// </summary>
        public int CraftFromGrid(PlayerState p, bool shift, bool minigameSuccess, int expectCraft)
        {
            if (p.GridSize == 3 && !IsGridStation(p, p.GridStation)) return 0;
            var match = GridResult(p);
            if (match == null) return 0;
            var craft = match.Craft;
            if (expectCraft >= 0 && craft.NumericId != expectCraft) return 0;
            if (!Knowledge.IsCraftUnlocked(craft))
            {
                Notify?.Invoke(p.Id, "ui.locked");
                return 0;
            }
            if (craft.Minigame != null && !minigameSuccess) return 0;

            int times = shift && craft.Minigame == null ? 64 : 1;
            int done = 0;
            for (int t = 0; t < times; t++)
            {
                if (t > 0)
                {
                    match = GridResult(p);
                    if (match == null || match.Craft != craft) break;
                }
                var outputs = RollOutputs(p, craft);
                if (!CarryMeta(p, match, outputs)) break;
                if (shift)
                {
                    if (!p.Inventory.CanInsertAll(outputs)) break;
                }
                else
                {
                    var cur = p.Cursor[0];
                    var main = outputs[0];
                    int max = p.Inventory.MaxStackOf(main.Item);
                    if (!cur.IsEmpty && (!cur.SameKind(main) || cur.Count + main.Count > max)) break;
                }

                Consume(p, match);
                if (shift)
                    foreach (var o in outputs) p.Inventory.Insert(o);
                else
                {
                    var cur = p.Cursor[0];
                    p.Cursor[0] = cur.IsEmpty ? outputs[0] : cur.WithCount(cur.Count + outputs[0].Count);
                    for (int i = 1; i < outputs.Count; i++)
                    {
                        int put = p.Inventory.Insert(outputs[i]);
                        if (put < outputs[i].Count) DropFromPlayer(p, outputs[i].WithCount(outputs[i].Count - put), false);
                    }
                }
                p.Mastery[craft.Id] = (p.Mastery.TryGetValue(craft.Id, out var n) ? n : 0) + 1;
                if (p.Mastery[craft.Id] == 16 || p.Mastery[craft.Id] == 64 || p.Mastery[craft.Id] == 256)
                    Notify?.Invoke(p.Id, "ui.mastery_up|" + (Content.Item(craft.Outputs[0].ItemId)?.NameKey ?? craft.Id));
                Knowledge.Notify("craft", craft.Id);
                Crafted?.Invoke(p, craft);
                done++;
            }
            return done;
        }

        /// <summary>Выход рецепта с бонусом мастерства (+1 у стакаемых результатов: 8% за уровень).</summary>
        private List<ItemStack> RollOutputs(PlayerState p, CraftDef craft)
        {
            var list = new List<ItemStack>();
            int level = p.MasteryLevel(craft.Id);
            for (int i = 0; i < craft.Outputs.Count; i++)
            {
                var o = craft.Outputs[i];
                int count = o.Count;
                if (i == 0 && count >= 2 && level > 0 && _craftRng.NextDouble() < 0.08 * level) count++;
                list.Add(new ItemStack(o.ItemId, count));
            }
            return list;
        }

        /// <summary>
        /// Предметы с данными в Meta (ячейки ME, беспроводной терминал) не должны молча сгорать в крафте:
        /// апгрейд ячейки (1k → 4k) переносит её содержимое в новую ячейку (тот же id в реестре, ёмкость
        /// обновится в приводе), привязка терминала переходит к результату. Непустую ячейку, которой некуда
        /// перенести содержимое, крафт не берёт. false — крафт запрещён.
        /// </summary>
        private bool CarryMeta(PlayerState p, GridMatch match, List<ItemStack> outputs)
        {
            int carry = 0;
            for (int s = 0; s < 9; s++)
            {
                var ing = match.Slots[s];
                if (ing == null || ing.Keep) continue;
                var st = p.CraftGrid[s];
                if (st.IsEmpty || st.Meta == 0) continue;
                var def = Content.Item(st.Item);
                if (def?.StorageCell != null)
                {
                    if (!Factory.Me.CellHasContents(st.Meta)) continue; // пустая — просто расходуется
                    var cell = Factory.Me.Cell(st.Meta);
                    var outSpec = outputs.Count > 0 ? Content.Item(outputs[0].Item)?.StorageCell : null;
                    if (carry != 0 || outSpec == null || outSpec.IsFluid != cell.Fluid || outSpec.Capacity < cell.Used || outputs[0].Count != 1)
                    {
                        Notify?.Invoke(p.Id, "ui.cell_not_empty");
                        return false;
                    }
                    carry = st.Meta;
                }
                else if (def != null && def.MeWireless && carry == 0 && outputs.Count > 0 && Content.Item(outputs[0].Item)?.MeWireless == true)
                    carry = st.Meta;
            }
            if (carry != 0) outputs[0] = new ItemStack(outputs[0].Item, outputs[0].Count, carry);
            return true;
        }

        private void Consume(PlayerState p, GridMatch match)
        {
            for (int s = 0; s < 9; s++)
            {
                var ing = match.Slots[s];
                if (ing == null) continue;
                var st = p.CraftGrid[s];
                if (ing.Keep)
                {
                    // Катализатор: инструмент изнашивается, прочее не тратится
                    var tool = Content.Item(st.Item)?.Tool;
                    if (tool == null) continue;
                    int meta = st.Meta + 1;
                    p.CraftGrid[s] = meta >= tool.Durability ? ItemStack.Empty : new ItemStack(st.Item, st.Count, meta);
                    if (meta >= tool.Durability) Notify?.Invoke(p.Id, "ui.tool_broke");
                    continue;
                }
                if (Content.Item(st.Item)?.StorageCell != null) Factory.Me.ReleaseCell(st.Meta);
                p.CraftGrid[s] = st.WithCount(st.Count - ing.Count);
            }
        }

        // ============================================================ Заполнение из справочника

        /// <summary>
        /// Разложить ингредиенты рецепта из инвентаря в сетку (кнопка «+» в справочнике). max — сколько получится
        /// (до стопки), иначе на один крафт. Старое содержимое сетки возвращается в инвентарь.
        /// </summary>
        public bool FillGrid(PlayerState p, int craftId, bool max)
        {
            var craft = Content.Crafts.Get(craftId);
            if (craft == null || !craft.IsGrid || craft.GridSize > p.GridSize) return false;
            for (int i = 0; i < 9; i++) ReturnToInventory(p, p.CraftGrid, i);

            // Клетки рецепта → клетки сетки (форменный — от левого верхнего угла)
            var cells = new List<KeyValuePair<int, GridIngredient>>();
            if (craft.Shapeless)
            {
                for (int i = 0; i < craft.Cells.Length; i++)
                {
                    int slot = (i / p.GridSize) * 3 + i % p.GridSize;
                    cells.Add(new KeyValuePair<int, GridIngredient>(slot, craft.Cells[i]));
                }
            }
            else
            {
                for (int y = 0; y < craft.GridH; y++)
                for (int x = 0; x < craft.GridW; x++)
                {
                    var ing = craft.Cells[y * craft.GridW + x];
                    if (ing != null) cells.Add(new KeyValuePair<int, GridIngredient>(y * 3 + x, ing));
                }
            }

            // Выбрать конкретный предмет для каждого ингредиента — тот, которого больше всего
            var choice = new Dictionary<GridIngredient, int>();
            foreach (var kv in cells)
            {
                if (choice.ContainsKey(kv.Value)) continue;
                int best = -1, bestCount = 0;
                foreach (var item in kv.Value.Matches)
                {
                    int c = p.Inventory.CountOf(item);
                    if (c > bestCount)
                    {
                        best = item;
                        bestCount = c;
                    }
                }
                if (best < 0) return false;
                choice[kv.Value] = best;
            }

            int passes = max ? 64 : 1;
            int done = 0;
            for (int pass = 0; pass < passes; pass++)
            {
                // Хватит ли на ещё один крафт?
                var need = new Dictionary<int, int>();
                foreach (var kv in cells)
                {
                    if (kv.Value.Keep && pass > 0) continue;
                    int item = choice[kv.Value];
                    need[item] = (need.TryGetValue(item, out var n) ? n : 0) + kv.Value.Count;
                }
                bool ok = true;
                foreach (var kv in need)
                    if (p.Inventory.CountOf(kv.Key) < kv.Value) ok = false;
                foreach (var kv in cells)
                {
                    var cur = p.CraftGrid[kv.Key];
                    int mx = p.Inventory.MaxStackOf(choice[kv.Value]);
                    if (!kv.Value.Keep && !cur.IsEmpty && cur.Count + kv.Value.Count > mx) ok = false;
                }
                if (!ok) break;

                foreach (var kv in cells)
                {
                    if (kv.Value.Keep && pass > 0) continue;
                    int item = choice[kv.Value];
                    if (kv.Value.Keep)
                    {
                        // Инструмент — забираем стак целиком из слота (с его износом)
                        for (int i = 0; i < p.Inventory.Size; i++)
                            if (!p.Inventory[i].IsEmpty && p.Inventory[i].Item == item)
                            {
                                p.CraftGrid[kv.Key] = p.Inventory[i].WithCount(1);
                                p.Inventory[i] = p.Inventory[i].WithCount(p.Inventory[i].Count - 1);
                                break;
                            }
                        continue;
                    }
                    p.Inventory.Extract(item, kv.Value.Count);
                    var cur = p.CraftGrid[kv.Key];
                    p.CraftGrid[kv.Key] = new ItemStack(item, (cur.IsEmpty ? 0 : cur.Count) + kv.Value.Count);
                }
                done++;
            }
            return done > 0;
        }

        // ============================================================ Выброс предметов

        /// <summary>Выбросить предметы перед игроком (Z, клик мимо окна). Выброшенное не исчезает со временем.</summary>
        public void DropFromPlayer(PlayerState p, ItemStack st, bool thrown = true)
        {
            if (st.IsEmpty) return;
            var dir = p.LookDirection;
            var pos = new Vec3(p.Position.X + dir.X * 0.4f, p.Position.Y + PlayerPhysics.EyeHeight - 0.35f, p.Position.Z + dir.Z * 0.4f);
            var vel = thrown ? new Vec3(dir.X * 5f, dir.Y * 5f + 2f, dir.Z * 5f) : new Vec3(dir.X, 1.5f, dir.Z);
            var d = Drops.Spawn(st, pos, vel, 40);
            if (d != null) d.Persistent = true;
        }

        /// <summary>Выбросить из слота инвентаря: один предмет или весь стак.</summary>
        public void DropSlot(PlayerState p, int slot, bool all)
        {
            if (slot < 0 || slot >= p.Inventory.Size) return;
            var st = p.Inventory[slot];
            if (st.IsEmpty) return;
            int n = all ? st.Count : 1;
            p.Inventory[slot] = st.WithCount(st.Count - n);
            DropFromPlayer(p, st.WithCount(n));
        }

        /// <summary>Выбросить то, что на курсоре (клик мимо окна): всё или один.</summary>
        public void DropCursor(PlayerState p, bool all)
        {
            var st = p.Cursor[0];
            if (st.IsEmpty) return;
            int n = all ? st.Count : 1;
            p.Cursor[0] = st.WithCount(st.Count - n);
            DropFromPlayer(p, st.WithCount(n));
        }
    }
}
