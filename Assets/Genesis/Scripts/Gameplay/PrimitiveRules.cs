using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Правила первобытных механик без побочных эффектов: что можно сделать в руках, что — в режиме создания,
    /// какая конструкция собрана и что делает предмет с блоком. Общие для сервера (проверка) и клиента
    /// (подсказки, колесо выбора, призрак конструкции).
    /// </summary>
    public static class HandCrafting
    {
        /// <summary>
        /// Крафт для предметов в правой (main) и левой (off) руке. mainIsA — правая рука держит ингредиент A.
        /// Порядок рук не важен; ингредиентов должно хватать (a_count / b_count).
        /// </summary>
        public static HandCraftDef Find(ContentDatabase content, ItemStack main, ItemStack off, out bool mainIsA)
        {
            mainIsA = true;
            if (main.IsEmpty || off.IsEmpty) return null;
            foreach (var h in content.HandCrafts.Items)
            {
                if (Fits(h, main, off, true))
                {
                    mainIsA = true;
                    return h;
                }
                if (Fits(h, main, off, false))
                {
                    mainIsA = false;
                    return h;
                }
            }
            return null;
        }

        private static bool Fits(HandCraftDef h, ItemStack main, ItemStack off, bool mainIsA)
        {
            var a = mainIsA ? main : off;
            var b = mainIsA ? off : main;
            if (Array.IndexOf(h.AMatches, a.Item) < 0 || Array.IndexOf(h.BMatches, b.Item) < 0) return false;
            if (a.Count < (h.KeepA ? 1 : h.ACount) || b.Count < (h.KeepB ? 1 : h.BCount)) return false;
            // Починка не нужна, если инструмент цел
            if (h.Repair && (h.KeepA ? a : b).Meta <= 0) return false;
            return true;
        }
    }

    /// <summary>Режим создания: обработка блока ударами (или удержанием ПКМ).</summary>
    public static class Shaping
    {
        /// <summary>Подходит ли предмет в правой руке.</summary>
        public static bool ToolOk(ContentDatabase content, ShapingDef def, ItemStack held)
        {
            if (def.ItemId >= 0) return !held.IsEmpty && held.Item == def.ItemId;
            if (def.Tools.Count == 0) return true;
            var tool = Mining.ToolOf(content, held);
            if (tool == null) return false;
            foreach (var t in def.Tools)
                if (t == tool.Type || t == "any") return true; // any — любой инструмент (молотом служит что угодно)
            return false;
        }

        /// <summary>Хватает ли предметов на рабочей поверхности станции.</summary>
        public static bool InputsPresent(ShapingDef def, MachineInstance station)
        {
            if (!def.IsStation) return true;
            if (station == null || !station.Def.Surface) return false;
            foreach (var i in def.Inputs)
                if (station.Input.CountOf(i.ItemId) < i.Count) return false;
            return true;
        }

        /// <summary>
        /// Что можно сделать с блоком pos в режиме создания. Для станций — только рецепты, чьи предметы лежат
        /// на поверхности. Инструмент в руке не проверяется (подсказка скажет, чем бить), если checkTool = false.
        /// </summary>
        public static List<ShapingDef> Options(ContentDatabase content, VoxelWorld world, Factory factory, Int3 pos,
            ItemStack held, bool checkTool)
        {
            var list = new List<ShapingDef>();
            if (!world.IsLoaded(pos.X, pos.Y, pos.Z)) return list;
            var station = factory != null ? MultiBlock.OwnerOf(factory, pos) : null;
            ushort block = station != null ? world.GetBlock(station.Pos) : world.GetBlock(pos);
            foreach (var s in content.Shapings.Items)
            {
                if (Array.IndexOf(s.TargetBlocks, block) < 0) continue;
                if (!InputsPresent(s, station)) continue;
                if (checkTool && !ToolOk(content, s, held)) continue;
                list.Add(s);
            }
            return list;
        }
    }

    /// <summary>Найденная конструкция: шаблон, поворот, где стоит результат и куда смотрит.</summary>
    public sealed class StructureMatch
    {
        public StructureDef Def;
        /// <summary>Поворот шаблона по часовой (вид сверху), 0..3.</summary>
        public int Rotation;
        /// <summary>Мировая позиция клетки шаблона (0, 0, 0).</summary>
        public Int3 Origin;
        public Int3 ResultPos;
        public Direction Facing;
        /// <summary>Все клетки шаблона в мире (кроме «неважно»).</summary>
        public readonly List<Int3> Cells = new List<Int3>();
    }

    /// <summary>Конструкции из блоков (верстак, печка, лежанка, кузня).</summary>
    public static class Structures
    {
        /// <summary>Смещение клетки шаблона (x, z) при повороте r по часовой стрелке (вид сверху, z — север).</summary>
        public static Int3 Rotate(int x, int y, int z, int r)
        {
            switch (r & 3)
            {
                case 1: return new Int3(z, y, -x);
                case 2: return new Int3(-x, y, -z);
                case 3: return new Int3(-z, y, x);
                default: return new Int3(x, y, z);
            }
        }

        private static Direction RotateFacing(Direction d, int r)
        {
            for (int i = 0; i < (r & 3); i++) d = d.RotateCW();
            return d;
        }

        /// <summary>Блок подходит под клетку шаблона.</summary>
        public static bool CellMatches(ContentDatabase content, ushort expected, ushort actual)
        {
            if (expected == StructureDef.Ignore) return true;
            if (expected == StructureDef.Any)
            {
                var def = content.Block(actual);
                return def.Solid && !def.Liquid && !def.Replaceable && def.PartOf == null && def.MachineId == null;
            }
            return expected == actual;
        }

        /// <summary>
        /// Конструкция, в которую входит блок clicked. Если подходят несколько поворотов (симметричный шаблон),
        /// выбирается тот, где результат смотрит на игрока (viewer). structureId &lt; 0 — любая.
        /// </summary>
        public static StructureMatch Find(ContentDatabase content, VoxelWorld world, Int3 clicked, Vec3 viewer, int structureId = -1)
        {
            if (!world.IsLoaded(clicked.X, clicked.Y, clicked.Z)) return null;
            ushort clickedBlock = world.GetBlock(clicked);
            StructureMatch best = null;
            float bestScore = float.NegativeInfinity;
            foreach (var st in content.Structures.Items)
            {
                if (structureId >= 0 && st.NumericId != structureId) continue;
                if (st.Cells == null || st.Cells.Length == 0) continue;
                for (int r = 0; r < 4; r++)
                for (int y = 0; y < st.SizeY; y++)
                for (int z = 0; z < st.SizeZ; z++)
                for (int x = 0; x < st.SizeX; x++)
                {
                    ushort expected = st.Cells[st.CellIndex(x, y, z)];
                    if (expected == StructureDef.Ignore || !CellMatches(content, expected, clickedBlock)) continue;
                    var origin = clicked - Rotate(x, y, z, r);
                    var m = TryAt(content, world, st, origin, r);
                    if (m == null) continue;
                    // Предпочитаем ориентацию «лицом к игроку»
                    var toViewer = new Vec3(viewer.X - (m.ResultPos.X + 0.5f), 0, viewer.Z - (m.ResultPos.Z + 0.5f));
                    var f = m.Facing.Offset();
                    float score = f.X * toViewer.X + f.Z * toViewer.Z;
                    if (best == null || score > bestScore + 1e-4f)
                    {
                        best = m;
                        bestScore = score;
                    }
                }
            }
            return best;
        }

        /// <summary>Проверить шаблон с началом origin и поворотом r.</summary>
        public static StructureMatch TryAt(ContentDatabase content, VoxelWorld world, StructureDef st, Int3 origin, int r)
        {
            var match = new StructureMatch { Def = st, Rotation = r, Origin = origin };
            for (int y = 0; y < st.SizeY; y++)
            for (int z = 0; z < st.SizeZ; z++)
            for (int x = 0; x < st.SizeX; x++)
            {
                ushort expected = st.Cells[st.CellIndex(x, y, z)];
                if (expected == StructureDef.Ignore) continue;
                var p = origin + Rotate(x, y, z, r);
                if (!world.IsLoaded(p.X, p.Y, p.Z)) return null;
                if (!CellMatches(content, expected, world.GetBlock(p))) return null;
                match.Cells.Add(p);
            }
            match.ResultPos = origin + Rotate(st.ResultAt[0], st.ResultAt[1], st.ResultAt[2], r);
            match.Facing = RotateFacing(st.Facing, r);
            return match;
        }

        /// <summary>Клетки, которые займёт результат (для многоблочных машин — все его клетки).</summary>
        public static List<Int3> ResultCells(ContentDatabase content, StructureMatch m)
        {
            var block = content.Block(m.Def.ResultBlock);
            var mdef = block.MachineId != null ? content.Machines.Get(block.MachineId) : null;
            if (mdef != null && mdef.IsMultiBlock) return MultiBlock.Cells(mdef, m.ResultPos, m.Facing);
            return new List<Int3> { m.ResultPos };
        }
    }

    /// <summary>ПКМ предметом по блоку.</summary>
    public static class Interactions
    {
        public static InteractionDef Find(ContentDatabase content, ItemStack held, ushort block)
        {
            if (held.IsEmpty) return null;
            foreach (var it in content.Interactions.Items)
            {
                if (Array.IndexOf(it.HeldMatches, held.Item) < 0) continue;
                if (Array.IndexOf(it.TargetBlocks, block) < 0) continue;
                return it;
            }
            return null;
        }
    }

    /// <summary>Остывание/угасание предметов (уголёк, раскалённый слиток): метка времени хранится в Meta.</summary>
    public static class Decay
    {
        /// <summary>Текущая секунда мира для меток (0 — «ещё не помечен», поэтому +1).</summary>
        public static int Stamp(long worldTime) => (int)(worldTime / 20) + 1;

        /// <summary>Сколько секунд осталось (для подсказки); -1 — предмет не угасает или ещё не помечен.</summary>
        public static int SecondsLeft(ContentDatabase content, ItemStack st, long worldTime)
        {
            if (st.IsEmpty) return -1;
            var def = content.Item(st.Item);
            if (def == null || !def.Decays || st.Meta <= 0) return -1;
            return Math.Max(0, def.DecaySeconds - (Stamp(worldTime) - st.Meta));
        }

        /// <summary>Обновить стак: пометить свежий, заменить угасший. true — стак изменился.</summary>
        public static bool Process(ContentDatabase content, ref ItemStack st, long worldTime)
        {
            if (st.IsEmpty) return false;
            var def = content.Item(st.Item);
            if (def == null || !def.Decays) return false;
            int now = Stamp(worldTime);
            if (st.Meta <= 0)
            {
                st = new ItemStack(st.Item, st.Count, now);
                return true;
            }
            if (now - st.Meta < def.DecaySeconds) return false;
            st = def.DecayToId >= 0 ? new ItemStack(def.DecayToId, st.Count) : ItemStack.Empty;
            return true;
        }
    }
}
