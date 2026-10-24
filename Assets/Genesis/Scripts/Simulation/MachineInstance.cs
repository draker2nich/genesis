using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.Simulation
{
    public enum MachineStatus : byte
    {
        Idle = 0,
        Working = 1,
        NoInput = 2,
        OutputBlocked = 3,
        NoEnergy = 4,
        NoRecipe = 5,
        NoSource = 6,
        NoDeposit = 7
    }

    /// <summary>
    /// Экземпляр машины в фабрике. Хранится отдельно от чанков и симулируется всегда,
    /// независимо от того, загружен ли чанк (как в Factorio).
    /// </summary>
    public sealed class MachineInstance
    {
        public readonly Int3 Pos;
        public readonly MachineDef Def;
        public Direction Facing;
        public readonly IMachineBehavior Behavior;

        /// <summary>Конфигурация сторон по относительным сторонам (RelativeSide).</summary>
        public readonly SideType[] Sides = new SideType[6];

        public readonly Inventory Input;
        public readonly Inventory Output;
        public readonly Inventory Fuel;
        public readonly List<FluidTank> Tanks = new List<FluidTank>();

        // ---- состояние процесса
        public RecipeDef CurrentRecipe;
        /// <summary>Рецепт, выбранный игроком в интерфейсе машины. null — машина выбирает сама.</summary>
        public RecipeDef LockedRecipe;
        /// <summary>
        /// Заказ провайдера ME: следующие QueuedRuns запусков — строго этот рецепт (входы под него уже положены).
        /// Важнее выбора игрока, иначе машина сделала бы из этих входов другое и автокрафт «потерял» бы заказ.
        /// </summary>
        public RecipeDef QueuedRecipe;
        public int QueuedRuns;
        /// <summary>Последний выполненный рецепт: при авто-выборе пробуется первым (машина не «прыгает» между рецептами).</summary>
        public RecipeDef LastRecipe;
        public float Progress;            // в тиках рецепта
        public readonly List<ItemStack> PendingOutputs = new List<ItemStack>();
        public int BurnLeft;              // тиков горения текущего топлива
        public int BurnTotal;
        public float Heat;                // 0..1 (котёл)
        public bool HasSource;            // насос стоит на источнике
        public int DepositItem = -1;      // бур: что добывает залежь под ним
        public int DepositPurity;         // 1..3
        public float EnergyStored;        // FE во внутреннем буфере
        public float EnergyRate;          // FE за последний тик: + выработано, − потреблено (для UI)
        public MachineStatus Status;
        public Random64 Rng;

        /// <summary>Переопределение потребления FE за тик (ME-контроллер считает его от размера сети). −1 — из описания.</summary>
        public float PowerDraw = -1f;

        // ---- ME-устройства
        /// <summary>Фильтр шины (id предметов, −1 — пусто).</summary>
        public readonly int[] Filter = { -1, -1, -1, -1, -1, -1, -1, -1, -1 };
        /// <summary>Фильтр жидкости шины (−1 — нет).</summary>
        public int FluidFilter = -1;
        /// <summary>Шаблоны провайдера: числовые id рецептов соседней машины.</summary>
        public readonly List<int> Patterns = new List<int>();
        /// <summary>Данные сети на контроллере: автоподдержание запаса и заказы автокрафта.</summary>
        public MeControllerData MeData;
        public bool MeOnline;

        /// <summary>Скорость за последний тик (0..1) — для анимации на клиенте.</summary>
        public float Activity;

        public MachineInstance(ContentDatabase content, MachineDef def, Int3 pos, Direction facing, IMachineBehavior behavior)
        {
            Def = def;
            Pos = pos;
            Facing = facing;
            Behavior = behavior;
            for (int i = 0; i < 6; i++) Sides[i] = def.DefaultSideConfig[i];

            Input = new Inventory(content, def.InputSlots);
            Output = new Inventory(content, def.OutputSlots);
            Fuel = new Inventory(content, def.FuelSlots);
            foreach (var t in def.Tanks)
            {
                int filter = t.Fluid != null ? content.Fluids.IndexOf(t.Fluid) : -1;
                Tanks.Add(new FluidTank(t.Name, t.Capacity, filter, t.Io == "out" || t.Io == "buffer", t.Io == "buffer"));
            }
            Rng = new Random64(Hash.Coord(def.NumericId * 7919L, pos.X, pos.Y, pos.Z));
        }

        public SideType SideAt(Direction absolute) => Sides[(int)Directions.ToRelative(absolute, Facing)];

        public void SetSide(RelativeSide side, SideType type) => Sides[(int)side] = type;

        public FluidTank Tank(string name)
        {
            foreach (var t in Tanks)
                if (t.Name == name) return t;
            return null;
        }

        /// <summary>Инвентарь, из которого машина выталкивает предметы через item_out.</summary>
        public Inventory EjectInventory => Output.Size > 0 ? Output : Input;

        /// <summary>Рецепт, под который машина сейчас принимает сырьё (заказ ME или выбор игрока), null — любой.</summary>
        public RecipeDef PinnedRecipe => QueuedRuns > 0 && QueuedRecipe != null ? QueuedRecipe : LockedRecipe;

        public float ProgressFraction => CurrentRecipe == null ? 0f : Progress / CurrentRecipe.TimeTicks;

        public float BurnFraction => BurnTotal <= 0 ? 0f : (float)BurnLeft / BurnTotal;

        public bool HasFilter
        {
            get
            {
                foreach (var f in Filter) if (f >= 0) return true;
                return false;
            }
        }

        public bool FilterAllows(int item)
        {
            bool any = false;
            foreach (var f in Filter)
            {
                if (f < 0) continue;
                any = true;
                if (f == item) return true;
            }
            return !any;
        }

        public float EnergyCapacity => Def.EnergyBuffer;
        public float EnergySpace => Math.Max(0f, EnergyCapacity - EnergyStored);
    }
}
