using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.Simulation
{
    /// <summary>Предмет на ленте. Pos 0 — начало ленты, 1 — конец (переход на следующий блок).</summary>
    public sealed class BeltItem
    {
        public int Item;
        /// <summary>Доп. данные стака (износ инструмента, id ячейки ME, привязка терминала) — едут вместе с предметом.</summary>
        public int Meta;
        public float Pos;
        public float PrevPos; // для интерполяции рендера между тиками
    }

    /// <summary>Сегмент конвейера (один блок). Одна полоса, до 4 предметов с шагом 0.25.</summary>
    public sealed class Conveyor
    {
        public const float Spacing = 0.25f;

        public readonly Int3 Pos;
        public Direction Facing;
        public readonly BlockDef Block;
        /// <summary>Блоков за тик.</summary>
        public readonly float Speed;

        /// <summary>Отсортированы по Pos по убыванию: [0] — ближе всех к выходу.</summary>
        public readonly List<BeltItem> Items = new List<BeltItem>(4);

        /// <summary>Порядок обработки: 0 — конец цепочки (обрабатывается первым).</summary>
        internal int Depth;

        public Conveyor(Int3 pos, Direction facing, BlockDef block)
        {
            Pos = pos;
            Facing = facing;
            Block = block;
            float bps = block.Transport != null ? block.Transport.Speed : 1.875f;
            Speed = bps / GameConstants.TicksPerSecond;
        }

        public Int3 Next => Pos + Facing.Offset();

        public bool CanAccept(float atPos = 0f)
        {
            if (Items.Count == 0) return true;
            if (Items.Count >= (int)(1f / Spacing)) return false;
            return Items[Items.Count - 1].Pos >= atPos + Spacing;
        }

        public void Accept(int item, float atPos, float prevPos, int meta = 0)
        {
            Items.Add(new BeltItem { Item = item, Meta = meta, Pos = atPos, PrevPos = prevPos });
        }

        public ItemStack StackAt(int i) => new ItemStack(Items[i].Item, 1, Items[i].Meta);
    }

    /// <summary>Сегмент трубы. Трубы объединяются в сети с общим буфером.</summary>
    public sealed class Pipe
    {
        public readonly Int3 Pos;
        public readonly BlockDef Block;
        public readonly int Capacity;
        public readonly int Throughput;
        public PipeNetwork Network;

        // Временные поля для перераспределения при пересборке сетей
        internal int ShareFluid = -1;
        internal int ShareAmount;

        public Pipe(Int3 pos, BlockDef block)
        {
            Pos = pos;
            Block = block;
            Capacity = block.Transport != null ? block.Transport.Capacity : 250;
            Throughput = block.Transport != null ? (int)block.Transport.Speed : 50;
        }
    }

    public struct FluidEndpoint
    {
        public MachineInstance Machine;
        /// <summary>Грань машины, которой она касается трубы.</summary>
        public Direction Face;
        public SideType Side;
    }

    /// <summary>
    /// Упрощённая модель: соединил трубой — течёт. Без давления и расчёта по сегментам.
    /// Сеть хранит одну жидкость; ёмкость = сумма ёмкостей труб.
    /// </summary>
    public sealed class PipeNetwork
    {
        public readonly List<Pipe> Pipes = new List<Pipe>();
        public readonly List<FluidEndpoint> Endpoints = new List<FluidEndpoint>();
        public int Fluid = -1;
        public int Amount;
        public int Capacity;
        public int Throughput = int.MaxValue;

        public int Space => Capacity - Amount;
    }
}

namespace Genesis.Simulation
{
    /// <summary>Сегмент провода. Тир задаёт пропускную способность (FE/тик).</summary>
    public sealed class Wire
    {
        public readonly Int3 Pos;
        public readonly BlockDef Block;
        public readonly int Throughput;
        public PowerNetwork Network;

        public Wire(Int3 pos, BlockDef block)
        {
            Pos = pos;
            Block = block;
            Throughput = block.Transport != null ? System.Math.Max(1, block.Transport.Capacity) : 100;
        }
    }

    /// <summary>
    /// Электросеть: провода + подключённые к ним машины (любой гранью).
    /// Пропускная способность сети = самый слабый провод (узкое место).
    /// </summary>
    public sealed class PowerNetwork
    {
        public readonly List<Wire> Wires = new List<Wire>();
        public readonly List<MachineInstance> Generators = new List<MachineInstance>();
        public readonly List<MachineInstance> Consumers = new List<MachineInstance>();
        public readonly List<MachineInstance> Storage = new List<MachineInstance>();
        public int Throughput = int.MaxValue;

        // Статистика последнего тика (для UI)
        public float LastSupplied;
        public float LastDemand;
        public float Satisfaction = 1f;
    }
}
