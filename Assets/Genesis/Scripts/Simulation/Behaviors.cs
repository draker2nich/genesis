using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.Simulation
{
    /// <summary>Логика машины. Регистрируется по имени (поле "behavior" в JSON) — код-моды добавляют свои.</summary>
    public interface IMachineBehavior
    {
        void Tick(MachineInstance m, Factory factory);
    }

    public static class MachineBehaviors
    {
        private static readonly Dictionary<string, IMachineBehavior> Registry = new Dictionary<string, IMachineBehavior>
        {
            { "processor", new ProcessorBehavior() },
            { "furnace", new ProcessorBehavior() },
            { "boiler", new BoilerBehavior() },
            { "pump", new PumpBehavior() },
            { "storage", new StorageBehavior() },
            { "station", new StationBehavior() },
            { "drill", new DrillBehavior() },
            { "inserter", new InserterBehavior() },
            { "tank", new StationBehavior() },
            { "generator", new GeneratorBehavior() },
            { "battery", new BatteryBehavior() },
            { "me_controller", new MeDeviceBehavior() },
            { "me_drive", new MeDeviceBehavior() },
            { "me_terminal", new MeDeviceBehavior() },
            { "me_import", new MeDeviceBehavior() },
            { "me_export", new MeDeviceBehavior() },
            { "me_provider", new MeDeviceBehavior() },
        };

        public static void Register(string name, IMachineBehavior behavior) => Registry[name] = behavior;

        public static IMachineBehavior Get(string name) =>
            name != null && Registry.TryGetValue(name, out var b) ? b : null;
    }

    // ================================================================ Энергия

    internal static class Energy
    {
        /// <summary>
        /// Доступная доля энергии на этот тик (0..1) с её потреблением.
        /// При нехватке машина замедляется пропорционально.
        /// </summary>
        public static float Consume(MachineInstance m, Factory f)
        {
            var spec = m.Def.Energy;
            switch (spec.Type)
            {
                case "steam":
                {
                    var tank = m.Tank("steam");
                    if (tank == null || spec.PerTick <= 0) return 1f;
                    int need = spec.PerTick;
                    int got = tank.Drain(need);
                    return (float)got / need;
                }
                case "fuel":
                {
                    if (m.BurnLeft <= 0 && !TryIgnite(m, f)) return 0f;
                    m.BurnLeft = Math.Max(0, m.BurnLeft - Math.Max(1, spec.PerTick));
                    return 1f;
                }
                case "fe":
                {
                    float need = spec.PerTick;
                    if (need <= 0) return 1f;
                    float got = Math.Min(m.EnergyStored, need);
                    m.EnergyStored -= got;
                    m.EnergyRate = -got;
                    return got / need;
                }
                default:
                    return 1f;
            }
        }

        public static bool TryIgnite(MachineInstance m, Factory f)
        {
            for (int i = 0; i < m.Fuel.Size; i++)
            {
                var s = m.Fuel[i];
                if (s.IsEmpty) continue;
                var def = f.Content.Item(s.Item);
                if (def == null || def.FuelTicks <= 0) continue;
                m.Fuel.ExtractFromSlot(i, 1);
                f.Stats.Consumed(s.Item, 1);
                m.BurnLeft = def.FuelTicks;
                m.BurnTotal = def.FuelTicks;
                return true;
            }
            return false;
        }
    }

    // ================================================================ Процессор (рецепты)

    /// <summary>
    /// Одна машина — одна операция: берёт подходящий рецепт для этой машины,
    /// потребляет входы в начале цикла, выдаёт выходы и побочку в конце.
    /// Если побочку некуда девать — машина стоит (OutputBlocked).
    /// </summary>
    public sealed class ProcessorBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Activity = 0;

            if (m.CurrentRecipe == null)
            {
                var recipes = f.Content.RecipesFor(m.Def.NumericId);
                if (recipes.Count == 0)
                {
                    m.Status = MachineStatus.NoRecipe;
                    return;
                }
                var recipe = FindRecipe(m, recipes, f.RecipeFilter);
                if (recipe == null)
                {
                    m.Status = MachineStatus.NoInput;
                    return;
                }
                // До старта проверяем энергию, чтобы не съесть сырьё без топлива.
                if (m.Def.Energy.Type == "fuel" && m.BurnLeft <= 0 && !Energy.TryIgnite(m, f))
                {
                    m.Status = MachineStatus.NoEnergy;
                    return;
                }
                if (m.Def.Energy.Type == "steam")
                {
                    var steam = m.Tank("steam");
                    if (steam != null && steam.Amount <= 0)
                    {
                        m.Status = MachineStatus.NoEnergy;
                        return;
                    }
                }
                if (m.Def.Energy.IsElectric && m.EnergyStored <= 0)
                {
                    m.Status = MachineStatus.NoEnergy;
                    return;
                }
                Start(m, recipe, f);
            }

            if (m.Progress < m.CurrentRecipe.TimeTicks)
            {
                float energy = Energy.Consume(m, f);
                if (energy <= 0)
                {
                    m.Status = MachineStatus.NoEnergy;
                    return;
                }
                float speed = Math.Max(0.01f, m.Def.Speed) * energy;
                m.Progress = Math.Min(m.CurrentRecipe.TimeTicks, m.Progress + speed);
                m.Activity = energy;
                m.Status = MachineStatus.Working;
            }

            if (m.Progress >= m.CurrentRecipe.TimeTicks)
                Finish(m, f);
        }

        private static RecipeDef FindRecipe(MachineInstance m, IReadOnlyList<RecipeDef> recipes, Func<RecipeDef, bool> filter)
        {
            foreach (var r in recipes)
            {
                if (filter != null && !filter(r)) continue;
                bool ok = true;
                foreach (var input in r.Inputs)
                    if (m.Input.CountOf(input.ItemId) < input.Count) { ok = false; break; }
                if (!ok) continue;
                foreach (var fi in r.FluidInputs)
                {
                    int have = 0;
                    foreach (var t in m.Tanks)
                        if (!t.IsOutput && t.Fluid == fi.FluidId) have += t.Amount;
                    if (have < fi.Amount) { ok = false; break; }
                }
                if (ok) return r;
            }
            return null;
        }

        private static void Start(MachineInstance m, RecipeDef r, Factory f)
        {
            foreach (var input in r.Inputs)
            {
                m.Input.Extract(input.ItemId, input.Count);
                f.Stats.Consumed(input.ItemId, input.Count);
            }
            foreach (var fi in r.FluidInputs)
            {
                int left = fi.Amount;
                foreach (var t in m.Tanks)
                    if (!t.IsOutput && t.Fluid == fi.FluidId && left > 0) left -= t.Drain(left);
            }

            m.PendingOutputs.Clear();
            foreach (var o in r.Outputs)
                m.PendingOutputs.Add(new ItemStack(o.ItemId, o.Count));
            foreach (var b in r.Byproducts)
                if (b.Chance >= 1f || m.Rng.Chance(b.Chance))
                    m.PendingOutputs.Add(new ItemStack(b.ItemId, b.Count));

            m.CurrentRecipe = r;
            m.Progress = 0;
        }

        private static void Finish(MachineInstance m, Factory f)
        {
            var r = m.CurrentRecipe;
            if (!m.Output.CanInsertAll(m.PendingOutputs))
            {
                m.Status = MachineStatus.OutputBlocked;
                return;
            }
            // Жидкие выходы — тоже атомарно
            foreach (var fo in r.FluidOutputs)
            {
                int space = 0;
                foreach (var t in m.Tanks)
                    if (t.IsOutput && t.Accepts(fo.FluidId)) space += t.Space;
                if (space < fo.Amount)
                {
                    m.Status = MachineStatus.OutputBlocked;
                    return;
                }
            }

            foreach (var s in m.PendingOutputs)
            {
                m.Output.Insert(s);
                f.Stats.Produced(s.Item, s.Count);
            }
            foreach (var fo in r.FluidOutputs)
            {
                int left = fo.Amount;
                foreach (var t in m.Tanks)
                    if (t.IsOutput && left > 0) left -= t.Fill(fo.FluidId, left);
            }

            m.PendingOutputs.Clear();
            m.CurrentRecipe = null;
            m.Progress = 0;
        }
    }

    // ================================================================ Котёл

    /// <summary>Котёл: топливо + вода → пар. Разогревается постепенно, без топлива остывает.</summary>
    public sealed class BoilerBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            var water = m.Tank("water");
            var steam = m.Tank("steam");
            m.Activity = 0;
            if (water == null || steam == null)
            {
                m.Status = MachineStatus.NoRecipe;
                return;
            }

            int steamPerTick = (int)m.Def.Param("steam_per_tick", 20);
            int waterPerTick = (int)m.Def.Param("water_per_tick", steamPerTick);
            float heatUp = 1f / (float)Math.Max(1, m.Def.Param("heat_up_ticks", 100));

            bool canBoil = water.Amount > 0 && steam.Space > 0;
            bool burning = false;
            if (canBoil)
            {
                if (m.BurnLeft > 0 || Energy.TryIgnite(m, f))
                {
                    m.BurnLeft--;
                    burning = true;
                }
            }

            m.Heat = burning ? Math.Min(1f, m.Heat + heatUp) : Math.Max(0f, m.Heat - heatUp * 0.5f);

            if (!canBoil)
            {
                m.Status = water.Amount <= 0 ? MachineStatus.NoInput : MachineStatus.OutputBlocked;
                return;
            }
            if (!burning)
            {
                m.Status = MachineStatus.NoEnergy;
                return;
            }

            int want = (int)Math.Ceiling(steamPerTick * m.Heat);
            int waterNeed = (int)Math.Ceiling(want * (float)waterPerTick / Math.Max(1, steamPerTick));
            int waterUsed = water.Drain(Math.Min(waterNeed, water.Amount));
            int produced = waterNeed > 0 ? (int)((long)want * waterUsed / waterNeed) : 0;
            steam.Fill(steam.Fluid >= 0 ? steam.Fluid : steam.Filter, produced);
            m.Activity = m.Heat;
            m.Status = MachineStatus.Working;
        }
    }

    // ================================================================ Насос

    /// <summary>Насос: стоит рядом с источником жидкости (водой) — бесконечно качает её в бак.</summary>
    public sealed class PumpBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Activity = 0;
            if (f.TickCount % 40 == (m.Pos.GetHashCode() & 31) || f.TickCount < 2)
                m.HasSource = f.FindAdjacentFluidSource(m) || (m.HasSource && !f.CanVerifySource(m));

            if (!m.HasSource)
            {
                m.Status = MachineStatus.NoSource;
                return;
            }
            int rate = (int)m.Def.Param("rate", 20);
            bool any = false;
            foreach (var t in m.Tanks)
            {
                if (!t.IsOutput) continue;
                int fluid = t.Filter >= 0 ? t.Filter : t.Fluid;
                if (fluid < 0) continue;
                if (t.Fill(fluid, rate) > 0) any = true;
            }
            m.Activity = any ? 1f : 0f;
            m.Status = any ? MachineStatus.Working : MachineStatus.OutputBlocked;
        }
    }

    // ================================================================ Хранилище

    public sealed class StorageBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Status = MachineStatus.Idle;
        }
    }

    /// <summary>Станция ручного крафта (верстак, наковальня): своей логики в тике нет, работает игрок.</summary>
    public sealed class StationBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Status = MachineStatus.Idle;
            m.Activity = 0;
        }
    }

    // ================================================================ Бур

    /// <summary>
    /// Бур стоит на рудной залежи и добывает её бесконечно. Скорость зависит от чистоты залежи
    /// (бедная ×0.5, обычная ×1, богатая ×2) и от пара; руда не кончается — ограничитель только скорость.
    /// </summary>
    public sealed class DrillBehavior : IMachineBehavior
    {
        public static float PurityFactor(int purity) => purity >= 3 ? 2f : (purity == 2 ? 1f : 0.5f);

        public void Tick(MachineInstance m, Factory f)
        {
            m.Activity = 0;
            if (f.TickCount < 2 || f.TickCount % 40 == (m.Pos.GetHashCode() & 31))
                f.RefreshDeposit(m);

            if (m.DepositItem < 0)
            {
                m.Status = MachineStatus.NoDeposit;
                return;
            }
            float need = (float)m.Def.Param("ticks_per_item", 40);
            if (m.Progress >= need)
            {
                var stack = new ItemStack(m.DepositItem, 1);
                if (m.Output.Insert(stack, true) < 1)
                {
                    m.Status = MachineStatus.OutputBlocked;
                    return;
                }
                m.Output.Insert(stack);
                f.Stats.Produced(m.DepositItem, 1);
                m.Progress -= need;
            }
            float energy = Energy.Consume(m, f);
            if (energy <= 0)
            {
                m.Status = MachineStatus.NoEnergy;
                return;
            }
            m.Progress += energy * Math.Max(0.01f, m.Def.Speed) * PurityFactor(m.DepositPurity);
            m.Activity = energy;
            m.Status = MachineStatus.Working;
        }
    }

    // ================================================================ Манипулятор

    /// <summary>
    /// Манипулятор (роборука): берёт предмет из блока сзади и кладёт в блок спереди.
    /// Progress — положение руки: 0 — над источником, 1 — над целью. Рука держит предмет в Input[0].
    /// </summary>
    public sealed class InserterBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            float step = 1f / (float)Math.Max(1, m.Def.Param("ticks_per_swing", 10));
            var back = m.Pos + m.Facing.Opposite().Offset();
            var front = m.Pos + m.Facing.Offset();
            bool holding = !m.Input[0].IsEmpty;
            m.Activity = 0;

            if (holding)
            {
                if (m.Progress < 1f)
                {
                    m.Progress = Math.Min(1f, m.Progress + step);
                    m.Activity = 1;
                    m.Status = MachineStatus.Working;
                    return;
                }
                int put = f.PutInto(front, m.Input[0], m.Facing.Opposite());
                if (put > 0)
                {
                    m.Input.ExtractFromSlot(0, put);
                    m.Status = MachineStatus.Working;
                }
                else m.Status = MachineStatus.OutputBlocked;
                return;
            }

            if (m.Progress > 0f)
            {
                m.Progress = Math.Max(0f, m.Progress - step);
                m.Activity = 1;
                m.Status = MachineStatus.Working;
                return;
            }
            // Берём, только если цель вообще может это принять
            var taken = f.TakeFrom(back, m.Facing, front, m.Facing.Opposite());
            if (taken.IsEmpty)
            {
                m.Status = MachineStatus.NoInput;
                return;
            }
            m.Input[0] = taken;
            m.Status = MachineStatus.Working;
        }
    }

    // ================================================================ Генераторы

    /// <summary>
    /// Генератор FE. Режим из params.mode:
    ///  steam — пар → FE (паровая турбина); fuel — топливо → FE (угольный генератор);
    ///  solar — днём, по высоте солнца; wind — выше над морем сильнее, ветер меняется;
    ///  water — рядом с водой (водяное колесо).
    /// Выработка идёт во внутренний буфер, сеть забирает оттуда. Полный буфер — генератор простаивает.
    /// </summary>
    public sealed class GeneratorBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Activity = 0;
            m.EnergyRate = 0;
            float fe = (float)m.Def.Param("fe_per_tick", 10);
            if (m.EnergySpace < 1f)
            {
                m.Status = MachineStatus.OutputBlocked;
                return;
            }
            string mode = ModeOf(m.Def);
            float produced = 0;
            switch (mode)
            {
                case "steam":
                {
                    var steam = m.Tank("steam");
                    int perTick = (int)m.Def.Param("steam_per_tick", 20);
                    int got = steam != null ? steam.Drain(Math.Min(perTick, (int)Math.Ceiling(m.EnergySpace / fe * perTick))) : 0;
                    produced = fe * got / Math.Max(1, perTick);
                    break;
                }
                case "fuel":
                    if (m.BurnLeft > 0 || Energy.TryIgnite(m, f))
                    {
                        m.BurnLeft--;
                        produced = fe;
                    }
                    break;
                case "solar":
                    produced = fe * f.Daylight;
                    break;
                case "wind":
                {
                    float height = Math.Max(0f, Math.Min(1f, (m.Pos.Y - 64) / 64f));
                    produced = fe * (0.15f + 0.85f * height) * f.Wind;
                    break;
                }
                case "water":
                    if (f.TickCount < 2 || f.TickCount % 40 == (m.Pos.GetHashCode() & 31))
                        m.HasSource = f.FindAdjacentFluidSourceOf(m, "genesis:water") || (m.HasSource && !f.CanVerifySource(m));
                    produced = m.HasSource ? fe : 0f;
                    break;
            }
            produced = Math.Min(produced, m.EnergySpace);
            m.EnergyStored += produced;
            m.EnergyRate = produced;
            m.Activity = fe > 0 ? Math.Min(1f, produced / fe) : 0f;
            m.Status = produced > 0 ? MachineStatus.Working : (mode == "water" ? MachineStatus.NoSource : MachineStatus.NoEnergy);
        }

        public static string ModeOf(MachineDef def)
        {
            if (def.Params != null && def.Params.ContainsKey("mode_steam")) return "steam";
            if (def.Params != null && def.Params.ContainsKey("mode_fuel")) return "fuel";
            if (def.Params != null && def.Params.ContainsKey("mode_solar")) return "solar";
            if (def.Params != null && def.Params.ContainsKey("mode_wind")) return "wind";
            if (def.Params != null && def.Params.ContainsKey("mode_water")) return "water";
            return "fuel";
        }
    }

    /// <summary>Энергоячейка: копит излишек сети и отдаёт при нехватке. Логика — в энергосети.</summary>
    public sealed class BatteryBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Activity = m.EnergyCapacity > 0 ? m.EnergyStored / m.EnergyCapacity : 0;
            m.Status = MachineStatus.Idle;
        }
    }

    /// <summary>Устройства ME-сети: вся логика — в MeSystem (статус выставляет сеть).</summary>
    public sealed class MeDeviceBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
        }
    }
}
