using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.Simulation
{
    /// <summary>
    /// Многоблочные машины (верстак 2×1, печка высотой в два блока): машина живёт в одной клетке (начало),
    /// остальные клетки заняты блоками-продолжениями. Размер [ширина, высота, глубина] считается от «лица»
    /// машины: ширина — вправо, высота — вверх, глубина — назад.
    /// </summary>
    public static class MultiBlock
    {
        /// <summary>Все клетки машины (первая — начало).</summary>
        public static List<Int3> Cells(MachineDef def, Int3 origin, Direction facing)
        {
            var list = new List<Int3>(def.Width * def.Height * def.Depth);
            var right = facing.RotateCW().Offset();
            var back = facing.Opposite().Offset();
            for (int k = 0; k < def.Depth; k++)
            for (int j = 0; j < def.Height; j++)
            for (int i = 0; i < def.Width; i++)
                list.Add(origin + right * i + Int3.Up * j + back * k);
            return list;
        }

        /// <summary>
        /// Машина, которой принадлежит клетка: сама машина или та, чьим продолжением является блок в pos.
        /// null — ни то, ни другое.
        /// </summary>
        public static MachineInstance OwnerOf(Factory factory, Int3 pos)
        {
            var m = factory.MachineAt(pos);
            if (m != null) return m;
            if (!factory.World.IsLoaded(pos.X, pos.Y, pos.Z)) return null;
            var block = factory.World.GetBlockDef(pos);
            if (block.PartOf == null) return null;
            var def = factory.Content.Machines.Get(block.PartOf);
            if (def == null) return null;
            // Перебираем возможные начала: клетка — это начало + вправо·i + вверх·j + назад·k при каком-то «лице»
            foreach (var facing in Directions.Horizontal)
            {
                var right = facing.RotateCW().Offset();
                var back = facing.Opposite().Offset();
                for (int k = 0; k < def.Depth; k++)
                for (int j = 0; j < def.Height; j++)
                for (int i = 0; i < def.Width; i++)
                {
                    var origin = pos - right * i - Int3.Up * j - back * k;
                    var cand = factory.MachineAt(origin);
                    if (cand != null && cand.Def == def && cand.Facing == facing) return cand;
                }
            }
            return null;
        }

        /// <summary>Клетка pos — часть машины (начало или продолжение).</summary>
        public static bool IsPartOf(MachineInstance m, Int3 pos)
        {
            if (m == null) return false;
            if (!m.Def.IsMultiBlock) return m.Pos == pos;
            foreach (var c in Cells(m.Def, m.Pos, m.Facing))
                if (c == pos) return true;
            return false;
        }
    }
}
