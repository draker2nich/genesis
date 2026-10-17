using System.Collections.Generic;
using Genesis.Data;
using Genesis.Simulation;

namespace Genesis.Gameplay
{
    /// <summary>Совпадение содержимого сетки с рецептом: какой ингредиент приходится на какую клетку.</summary>
    public sealed class GridMatch
    {
        public CraftDef Craft;
        /// <summary>Ингредиент для каждой из 9 клеток сетки (null — клетка пуста).</summary>
        public readonly GridIngredient[] Slots = new GridIngredient[9];
    }

    /// <summary>
    /// Сопоставление сетки крафта с рецептами (как в Minecraft): форменные рецепты — с любым сдвигом и зеркально,
    /// бесформенные — в любых клетках. Используется и сервером, и клиентом (предпросмотр результата).
    /// </summary>
    public static class GridCrafting
    {
        private static ContentDatabase _cachedFor;
        private static List<CraftDef> _gridCrafts;

        public static List<CraftDef> GridCrafts(ContentDatabase content)
        {
            if (_cachedFor != content || _gridCrafts == null)
            {
                var list = new List<CraftDef>();
                foreach (var c in content.Crafts.Items)
                    if (c.IsGrid && c.Outputs.Count > 0) list.Add(c);
                _gridCrafts = list;
                _cachedFor = content;
            }
            return _gridCrafts;
        }

        /// <summary>Найти рецепт для сетки размера size (2 или 3). null — ничего не подходит.</summary>
        public static GridMatch Match(ContentDatabase content, Inventory grid, int size)
        {
            // Границы занятой области
            int minX = 3, minY = 3, maxX = -1, maxY = -1, filled = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (grid[y * 3 + x].IsEmpty) continue;
                filled++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            // Вне активной области (после верстака в инвентаре) ничего не должно лежать
            for (int i = 0; i < 9; i++)
                if ((i % 3 >= size || i / 3 >= size) && !grid[i].IsEmpty) return null;
            if (filled == 0) return null;
            int w = maxX - minX + 1, h = maxY - minY + 1;

            foreach (var c in GridCrafts(content))
            {
                if (c.GridSize > size) continue;
                if (c.Shapeless)
                {
                    var m = MatchShapeless(c, grid, size, filled);
                    if (m != null) return m;
                    continue;
                }
                if (c.GridW != w || c.GridH != h) continue;
                for (int mirror = 0; mirror < 2; mirror++)
                {
                    var m = new GridMatch { Craft = c };
                    bool ok = true;
                    for (int y = 0; y < h && ok; y++)
                    for (int x = 0; x < w && ok; x++)
                    {
                        int px = mirror == 1 ? w - 1 - x : x;
                        var ing = c.Cells[y * w + px];
                        int slot = (minY + y) * 3 + minX + x;
                        var st = grid[slot];
                        if (ing == null) ok = st.IsEmpty;
                        else
                        {
                            ok = !st.IsEmpty && ing.Accepts(st.Item) && st.Count >= ing.Count;
                            m.Slots[slot] = ing;
                        }
                    }
                    if (ok) return m;
                }
            }
            return null;
        }

        private static GridMatch MatchShapeless(CraftDef c, Inventory grid, int size, int filled)
        {
            if (c.Cells.Length != filled) return null;
            var slots = new List<int>();
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (!grid[y * 3 + x].IsEmpty) slots.Add(y * 3 + x);
            var used = new bool[c.Cells.Length];
            var m = new GridMatch { Craft = c };
            return Assign(c, grid, slots, 0, used, m) ? m : null;
        }

        // Перебор с возвратом: клеток не больше 9
        private static bool Assign(CraftDef c, Inventory grid, List<int> slots, int k, bool[] used, GridMatch m)
        {
            if (k == slots.Count) return true;
            var st = grid[slots[k]];
            for (int i = 0; i < c.Cells.Length; i++)
            {
                if (used[i]) continue;
                var ing = c.Cells[i];
                if (!ing.Accepts(st.Item) || st.Count < ing.Count) continue;
                used[i] = true;
                m.Slots[slots[k]] = ing;
                if (Assign(c, grid, slots, k + 1, used, m)) return true;
                used[i] = false;
                m.Slots[slots[k]] = null;
            }
            return false;
        }

        /// <summary>Рецепты, где предмет — ингредиент или результат (для справочника).</summary>
        public static bool Uses(CraftDef c, int item)
        {
            if (c.Cells == null) return false;
            foreach (var ing in c.Cells)
                if (ing != null && ing.Accepts(item)) return true;
            return false;
        }
    }
}
