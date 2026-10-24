using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.Simulation
{
    /// <summary>
    /// Содержимое одной ячейки хранения. Живёт в реестре фабрики по id, который записан в Meta предмета-ячейки,
    /// поэтому ячейку можно вынуть из привода, отнести в другую сеть — содержимое сохранится.
    /// Ограничение только по общему объёму, без лимита по типам (ТЗ, раздел 11).
    /// </summary>
    public sealed class CellStorage
    {
        public int Id;
        public bool Fluid;
        public long Capacity;
        public long Used;
        public readonly Dictionary<int, long> Contents = new Dictionary<int, long>();

        public long Free => Math.Max(0, Capacity - Used);

        public long Count(int key) => Contents.TryGetValue(key, out var n) ? n : 0;

        public long Insert(int key, long amount, bool simulate)
        {
            long add = Math.Min(amount, Free);
            if (add <= 0) return 0;
            if (!simulate)
            {
                Contents[key] = Count(key) + add;
                Used += add;
            }
            return add;
        }

        public long Extract(int key, long amount, bool simulate)
        {
            long have = Count(key);
            long take = Math.Min(have, amount);
            if (take <= 0) return 0;
            if (!simulate)
            {
                if (have - take == 0) Contents.Remove(key);
                else Contents[key] = have - take;
                Used -= take;
            }
            return take;
        }
    }

    /// <summary>Заказ автокрафта: N запусков рецепта. Выходы возвращаются в сеть через провайдер.</summary>
    public sealed class CraftJob
    {
        public int Recipe;
        public int Item;      // ради чего заказан (для UI и учёта «в пути»)
        public int PerRun;    // сколько Item даёт один запуск
        public int Runs;
        public int Dispatched;

        public int RunsLeft => Runs - Dispatched;
    }

    public sealed class StockRule
    {
        public int Item;
        public long Target;
    }

    /// <summary>Данные сети, которые хранятся на контроллере (переживают перестройку сети).</summary>
    public sealed class MeControllerData
    {
        public readonly List<StockRule> Stock = new List<StockRule>();
        public readonly List<CraftJob> Jobs = new List<CraftJob>();
        /// <summary>Отправлено в машины, но ещё не вернулось в сеть (для учёта автозапаса).</summary>
        public readonly Dictionary<int, long> InFlight = new Dictionary<int, long>();
        public string LastError;
    }

    /// <summary>Сеть ME: всё, что соединено ME-кабелем или касается друг друга.</summary>
    public sealed class MeNetwork
    {
        public MachineInstance Controller;
        public int ControllerCount;
        public readonly List<MachineInstance> Devices = new List<MachineInstance>();
        public readonly List<MachineInstance> Drives = new List<MachineInstance>();
        public readonly List<MachineInstance> Imports = new List<MachineInstance>();
        public readonly List<MachineInstance> Exports = new List<MachineInstance>();
        public readonly List<MachineInstance> Providers = new List<MachineInstance>();
        public readonly List<CellStorage> ItemCells = new List<CellStorage>();
        public readonly List<CellStorage> FluidCells = new List<CellStorage>();
        public int Cables;
        public bool Online;
        public float PowerUsage;

        public MeControllerData Data => Controller?.MeData;
        public string Error;
    }

    /// <summary>Снимок сети для интерфейса терминала (и для отправки клиентам).</summary>
    public sealed class MeSnapshot
    {
        public bool Exists;
        public Int3 Access;
        public bool Online;
        public string Error;
        public float Power;
        public long ItemCapacity, ItemUsed, FluidCapacity, FluidUsed;
        public readonly List<KeyValuePair<int, long>> Items = new List<KeyValuePair<int, long>>();
        public readonly List<KeyValuePair<int, long>> Fluids = new List<KeyValuePair<int, long>>();
        public readonly List<int> Craftable = new List<int>();
        /// <summary>Заказы: предмет и сколько ещё штук будет сделано.</summary>
        public readonly List<KeyValuePair<int, long>> Jobs = new List<KeyValuePair<int, long>>();
        public readonly List<StockRule> Stock = new List<StockRule>();

        public long CountOf(int item)
        {
            foreach (var kv in Items)
                if (kv.Key == item) return kv.Value;
            return 0;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Exists);
            w.Write(Access.X);
            w.Write(Access.Y);
            w.Write(Access.Z);
            w.Write(Online);
            w.Write(Error ?? "");
            w.Write(Power);
            w.Write(ItemCapacity);
            w.Write(ItemUsed);
            w.Write(FluidCapacity);
            w.Write(FluidUsed);
            WriteList(w, Items);
            WriteList(w, Fluids);
            w.Write(Craftable.Count);
            foreach (var c in Craftable) w.Write(c);
            WriteList(w, Jobs);
            w.Write(Stock.Count);
            foreach (var s in Stock)
            {
                w.Write(s.Item);
                w.Write(s.Target);
            }
        }

        public static MeSnapshot Read(BinaryReader r)
        {
            var s = new MeSnapshot
            {
                Exists = r.ReadBoolean(),
                Access = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                Online = r.ReadBoolean(),
                Error = r.ReadString(),
                Power = r.ReadSingle(),
                ItemCapacity = r.ReadInt64(),
                ItemUsed = r.ReadInt64(),
                FluidCapacity = r.ReadInt64(),
                FluidUsed = r.ReadInt64()
            };
            if (s.Error.Length == 0) s.Error = null;
            ReadList(r, s.Items);
            ReadList(r, s.Fluids);
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) s.Craftable.Add(r.ReadInt32());
            ReadList(r, s.Jobs);
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) s.Stock.Add(new StockRule { Item = r.ReadInt32(), Target = r.ReadInt64() });
            return s;
        }

        private static void WriteList(BinaryWriter w, List<KeyValuePair<int, long>> list)
        {
            w.Write(list.Count);
            foreach (var kv in list)
            {
                w.Write(kv.Key);
                w.Write(kv.Value);
            }
        }

        private static void ReadList(BinaryReader r, List<KeyValuePair<int, long>> list)
        {
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++) list.Add(new KeyValuePair<int, long>(r.ReadInt32(), r.ReadInt64()));
        }
    }

    /// <summary>
    /// ME-сети фабрики (модель Refined Storage): соединил кабелем — работает, без каналов.
    /// Хранение в ячейках, шины импорта/экспорта с фильтрами, автокрафт через провайдеры у машин,
    /// автоподдержание запаса, беспроводной доступ. Сеть потребляет FE по числу устройств и ячеек.
    /// </summary>
    public sealed class MeSystem
    {
        public const int MaxBatch = 8;
        public const int BusItemsPerOp = 8;
        public const int BusFluidPerOp = 1000;
        private const int MaxPlanDepth = 16;

        private readonly Factory _f;
        private readonly Dictionary<int, CellStorage> _cells = new Dictionary<int, CellStorage>();
        private readonly HashSet<Int3> _cables = new HashSet<Int3>();
        private readonly Dictionary<Int3, BlockDef> _cableBlocks = new Dictionary<Int3, BlockDef>();
        private readonly Dictionary<Int3, MeNetwork> _netAt = new Dictionary<Int3, MeNetwork>();
        private readonly List<MeNetwork> _networks = new List<MeNetwork>();
        private readonly List<Int3> _links = new List<Int3>();
        private int _nextCellId = 1;
        private bool _dirty = true;

        public MeSystem(Factory factory)
        {
            _f = factory;
        }

        public IReadOnlyList<MeNetwork> Networks
        {
            get
            {
                if (_dirty) Rebuild();
                return _networks;
            }
        }

        public IEnumerable<Int3> Cables => _cables;
        public bool IsCable(Int3 p) => _cables.Contains(p);
        public void MarkDirty() => _dirty = true;

        public void AddCable(Int3 pos, BlockDef block)
        {
            _cables.Add(pos);
            _cableBlocks[pos] = block;
            _dirty = true;
        }

        public bool RemoveCable(Int3 pos)
        {
            if (!_cables.Remove(pos)) return false;
            _cableBlocks.Remove(pos);
            _dirty = true;
            return true;
        }

        public MeNetwork NetworkAt(Int3 pos)
        {
            if (_dirty) Rebuild();
            return _netAt.TryGetValue(pos, out var n) ? n : null;
        }

        public CellStorage Cell(int id) => _cells.TryGetValue(id, out var c) ? c : null;

        /// <summary>Завести запись ячейки в реестре (id идёт в Meta предмета-ячейки).</summary>
        public CellStorage CreateCell(bool fluid, long capacity)
        {
            var cell = new CellStorage { Id = _nextCellId++, Fluid = fluid, Capacity = capacity };
            _cells[cell.Id] = cell;
            return cell;
        }

        /// <summary>Ячейка с этим Meta что-то хранит (её нельзя уничтожать крафтом).</summary>
        public bool CellHasContents(int meta) => meta > 0 && _cells.TryGetValue(meta, out var c) && c.Used > 0;

        /// <summary>
        /// Сборка мусора реестра ячеек: записи, на которые не ссылается ни один предмет-ячейка в мире
        /// (инвентари, машины, ленты, лежащие предметы), удаляются. referenced — все Meta предметов-ячеек.
        /// </summary>
        public int CollectGarbage(HashSet<int> referenced)
        {
            var dead = new List<int>();
            foreach (var kv in _cells)
                if (!referenced.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var id in dead) _cells.Remove(id);
            return dead.Count;
        }

        /// <summary>Предмет-ячейка уничтожен — освободить запись реестра (только пустую).</summary>
        public void ReleaseCell(int meta)
        {
            if (meta > 0 && _cells.TryGetValue(meta, out var c) && c.Used <= 0) _cells.Remove(meta);
        }

        // ============================================================ Беспроводной доступ

        /// <summary>Номер привязки к контроллеру (для Meta беспроводного терминала), начиная с 1.</summary>
        public int LinkOf(Int3 controllerPos)
        {
            int i = _links.IndexOf(controllerPos);
            if (i < 0)
            {
                _links.Add(controllerPos);
                i = _links.Count - 1;
            }
            return i + 1;
        }

        public bool TryGetLink(int meta, out Int3 pos)
        {
            pos = default;
            if (meta <= 0 || meta > _links.Count) return false;
            pos = _links[meta - 1];
            return true;
        }

        // ============================================================ Топология

        private void Rebuild()
        {
            _dirty = false;
            _netAt.Clear();
            _networks.Clear();
            var queue = new Queue<Int3>();

            foreach (var start in AllNodes())
            {
                if (_netAt.ContainsKey(start)) continue;
                var net = new MeNetwork();
                _netAt[start] = net;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    var m = _f.MachineAt(p);
                    if (m != null && m.Def.IsMeDevice) AddDevice(net, m);
                    else net.Cables++;
                    foreach (var d in Directions.All)
                    {
                        var np = p + d.Offset();
                        if (_netAt.ContainsKey(np) || !IsNode(np)) continue;
                        _netAt[np] = net;
                        queue.Enqueue(np);
                    }
                }
                _networks.Add(net);
            }
        }

        private IEnumerable<Int3> AllNodes()
        {
            foreach (var c in _cables) yield return c;
            foreach (var m in _f.Machines)
                if (m.Def.IsMeDevice) yield return m.Pos;
        }

        private bool IsNode(Int3 p)
        {
            if (_cables.Contains(p)) return true;
            var m = _f.MachineAt(p);
            return m != null && m.Def.IsMeDevice;
        }

        private void AddDevice(MeNetwork net, MachineInstance m)
        {
            net.Devices.Add(m);
            switch (m.Def.Behavior)
            {
                case "me_controller":
                    net.ControllerCount++;
                    if (net.Controller == null) net.Controller = m;
                    if (m.MeData == null) m.MeData = new MeControllerData();
                    break;
                case "me_drive": net.Drives.Add(m); break;
                case "me_import": net.Imports.Add(m); break;
                case "me_export": net.Exports.Add(m); break;
                case "me_provider": net.Providers.Add(m); break;
            }
        }

        // ============================================================ Ячейки

        /// <summary>Выдать id новым ячейкам в приводах и собрать список ячеек сети.</summary>
        private void RefreshCells(MeNetwork net)
        {
            net.ItemCells.Clear();
            net.FluidCells.Clear();
            foreach (var d in net.Drives)
            {
                for (int i = 0; i < d.Input.Size; i++)
                {
                    var st = d.Input[i];
                    if (st.IsEmpty) continue;
                    var spec = _f.Content.Item(st.Item)?.StorageCell;
                    if (spec == null) continue;
                    if (st.Meta <= 0 || !_cells.ContainsKey(st.Meta))
                    {
                        var cell = new CellStorage { Id = _nextCellId++, Fluid = spec.IsFluid, Capacity = spec.Capacity };
                        _cells[cell.Id] = cell;
                        d.Input[i] = new ItemStack(st.Item, 1, cell.Id);
                    }
                    var c = _cells[d.Input[i].Meta];
                    c.Capacity = spec.Capacity;
                    (c.Fluid ? net.FluidCells : net.ItemCells).Add(c);
                }
            }
        }

        // ============================================================ Хранение

        public long Count(MeNetwork net, int item)
        {
            long n = 0;
            foreach (var c in net.ItemCells) n += c.Count(item);
            return n;
        }

        public long FluidCount(MeNetwork net, int fluid)
        {
            long n = 0;
            foreach (var c in net.FluidCells) n += c.Count(fluid);
            return n;
        }

        public long Insert(MeNetwork net, int item, long amount, bool simulate = false) =>
            InsertInto(net.ItemCells, item, amount, simulate);

        public long Extract(MeNetwork net, int item, long amount, bool simulate = false) =>
            ExtractFrom(net.ItemCells, item, amount, simulate);

        public long InsertFluid(MeNetwork net, int fluid, long amount, bool simulate = false) =>
            InsertInto(net.FluidCells, fluid, amount, simulate);

        public long ExtractFluid(MeNetwork net, int fluid, long amount, bool simulate = false) =>
            ExtractFrom(net.FluidCells, fluid, amount, simulate);

        private static long InsertInto(List<CellStorage> cells, int key, long amount, bool simulate)
        {
            if (amount <= 0) return 0;
            long left = amount;
            // Сначала в ячейки, где это уже лежит, потом в остальные
            foreach (var c in cells)
                if (left > 0 && c.Count(key) > 0) left -= c.Insert(key, left, simulate);
            foreach (var c in cells)
                if (left > 0 && c.Count(key) == 0) left -= c.Insert(key, left, simulate);
            return amount - left;
        }

        private static long ExtractFrom(List<CellStorage> cells, int key, long amount, bool simulate)
        {
            if (amount <= 0) return 0;
            long left = amount;
            for (int i = cells.Count - 1; i >= 0 && left > 0; i--) left -= cells[i].Extract(key, left, simulate);
            return amount - left;
        }

        public Dictionary<int, long> Aggregate(MeNetwork net, bool fluids)
        {
            var map = new Dictionary<int, long>();
            foreach (var c in fluids ? net.FluidCells : net.ItemCells)
                foreach (var kv in c.Contents)
                    map[kv.Key] = (map.TryGetValue(kv.Key, out var n) ? n : 0) + kv.Value;
            return map;
        }

        // ============================================================ Тик

        public void Tick()
        {
            if (_dirty) Rebuild();
            long t = _f.TickCount;
            foreach (var net in _networks)
            {
                RefreshCells(net);
                UpdatePower(net);
                foreach (var d in net.Devices)
                {
                    d.MeOnline = net.Online;
                    d.Status = net.Online ? MachineStatus.Working : MachineStatus.NoEnergy;
                    d.Activity = net.Online ? 1f : 0f;
                }
                if (!net.Online) continue;

                foreach (var bus in net.Imports)
                    if ((t + (bus.Pos.GetHashCode() & 7)) % 8 == 0) TickImport(net, bus);
                foreach (var bus in net.Exports)
                    if ((t + (bus.Pos.GetHashCode() & 7)) % 8 == 0) TickExport(net, bus);
                foreach (var p in net.Providers) CollectOutputs(net, p);
                TickJobs(net);
                if (t % 40 == 7) ReconcileInFlight(net);
                if (t % 40 == 11) TickStock(net);
            }
        }

        private void UpdatePower(MeNetwork net)
        {
            var c = net.Controller;
            if (c == null)
            {
                net.Online = false;
                net.Error = "ui.me.no_controller";
                return;
            }
            if (net.ControllerCount > 1)
            {
                net.Online = false;
                net.Error = "ui.me.two_controllers";
                return;
            }
            float usage = (float)c.Def.Param("base_fe", 4)
                          + (float)c.Def.Param("fe_per_device", 1) * (net.Devices.Count - 1)
                          + (float)c.Def.Param("fe_per_cell", 2) * (net.ItemCells.Count + net.FluidCells.Count)
                          + 0.05f * net.Cables;
            net.PowerUsage = usage;
            c.PowerDraw = usage;
            if (c.EnergyStored >= usage)
            {
                c.EnergyStored -= usage;
                c.EnergyRate = -usage;
                net.Online = true;
                net.Error = null;
            }
            else
            {
                c.EnergyRate = 0;
                net.Online = false;
                net.Error = "ui.status.no_energy";
            }
        }

        // ============================================================ Шины

        private Int3 TargetOf(MachineInstance bus) => bus.Pos + bus.Facing.Offset();

        private void TickImport(MeNetwork net, MachineInstance bus)
        {
            var target = TargetOf(bus);
            var face = bus.Facing.Opposite();

            var conv = _f.ConveyorAt(target);
            if (conv != null)
            {
                for (int i = conv.Items.Count - 1; i >= 0; i--)
                {
                    int item = conv.Items[i].Item;
                    if (conv.Items[i].Meta != 0) continue; // ME хранит предметы без Meta — ячейку/инструмент не берём
                    if (!bus.FilterAllows(item) || Insert(net, item, 1, true) < 1) continue;
                    Insert(net, item, 1);
                    conv.Items.RemoveAt(i);
                }
                return;
            }

            var m = _f.MachineAt(target);
            if (m == null || m.Def.IsMeDevice) return;

            int budget = BusItemsPerOp;
            var inv = m.EjectInventory;
            for (int slot = 0; slot < inv.Size && budget > 0; slot++)
            {
                var st = inv[slot];
                if (st.IsEmpty || st.Meta != 0 || !bus.FilterAllows(st.Item)) continue;
                long can = Insert(net, st.Item, Math.Min(budget, st.Count), true);
                if (can <= 0) continue;
                var taken = inv.ExtractFromSlot(slot, (int)can);
                Insert(net, taken.Item, taken.Count);
                budget -= taken.Count;
            }

            foreach (var tank in m.Tanks)
            {
                if (!tank.IsOutput || tank.IsEmpty) continue;
                if (bus.FluidFilter >= 0 && tank.Fluid != bus.FluidFilter) continue;
                long can = InsertFluid(net, tank.Fluid, Math.Min(tank.Amount, BusFluidPerOp), true);
                if (can <= 0) continue;
                int fluid = tank.Fluid;
                int drained = tank.Drain((int)can);
                InsertFluid(net, fluid, drained);
            }
            _ = face;
        }

        private void TickExport(MeNetwork net, MachineInstance bus)
        {
            var target = TargetOf(bus);
            var face = bus.Facing.Opposite();
            var m = _f.MachineAt(target);
            if (m != null && m.Def.IsMeDevice) return;

            foreach (int item in bus.Filter)
            {
                if (item < 0) continue;
                long have = Count(net, item);
                if (have <= 0) continue;
                int want = (int)Math.Min(have, BusItemsPerOp);
                int max = _f.Content.Item(item)?.MaxStack ?? GameConstants.MaxStackSize;
                int fit = _f.PutInto(target, new ItemStack(item, Math.Min(want, max)), face, true);
                if (fit <= 0) continue;
                long got = Extract(net, item, fit);
                int put = _f.PutInto(target, new ItemStack(item, (int)got), face);
                if (put < got) Insert(net, item, got - put);
            }

            if (bus.FluidFilter >= 0 && m != null)
            {
                long have = FluidCount(net, bus.FluidFilter);
                if (have > 0)
                {
                    int fit = _f.FillMachine(m, bus.FluidFilter, (int)Math.Min(have, BusFluidPerOp), true);
                    if (fit > 0)
                    {
                        long got = ExtractFluid(net, bus.FluidFilter, fit);
                        _f.FillMachine(m, bus.FluidFilter, (int)got, false);
                    }
                }
            }
        }

        // ============================================================ Автокрафт

        /// <summary>Провайдер забирает всё готовое из машины обратно в сеть.</summary>
        private void CollectOutputs(MeNetwork net, MachineInstance provider)
        {
            if (provider.Patterns.Count == 0) return;
            var m = _f.MachineAt(TargetOf(provider));
            if (m == null || m.Def.IsMeDevice || m.Output.Size == 0) return;
            for (int slot = 0; slot < m.Output.Size; slot++)
            {
                var st = m.Output[slot];
                if (st.IsEmpty) continue;
                long can = Insert(net, st.Item, st.Count, true);
                if (can <= 0) continue;
                var taken = m.Output.ExtractFromSlot(slot, (int)can);
                Insert(net, taken.Item, taken.Count);
                var inFlight = net.Data?.InFlight;
                if (inFlight != null && inFlight.TryGetValue(taken.Item, out long fl))
                {
                    if (fl - taken.Count <= 0) inFlight.Remove(taken.Item);
                    else inFlight[taken.Item] = fl - taken.Count;
                }
            }
            foreach (var tank in m.Tanks)
            {
                if (!tank.IsOutput || tank.IsBuffer || tank.IsEmpty) continue;
                int fluid = tank.Fluid;
                long can = InsertFluid(net, fluid, tank.Amount, true);
                if (can > 0) InsertFluid(net, fluid, tank.Drain((int)can));
            }
        }

        /// <summary>Рецепты, по которым сеть умеет крафтить (шаблоны всех провайдеров).</summary>
        public IEnumerable<RecipeDef> Patterns(MeNetwork net)
        {
            var seen = new HashSet<int>();
            foreach (var p in net.Providers)
                foreach (var r in p.Patterns)
                    if (seen.Add(r))
                    {
                        var def = _f.Content.Recipes.Get(r);
                        if (def != null && (_f.RecipeFilter == null || _f.RecipeFilter(def))) yield return def;
                    }
        }

        private RecipeDef PatternFor(MeNetwork net, int item, out int perRun)
        {
            foreach (var r in Patterns(net))
                foreach (var o in r.Outputs)
                    if (o.ItemId == item && o.Count > 0)
                    {
                        perRun = o.Count;
                        return r;
                    }
            perRun = 0;
            return null;
        }

        /// <summary>
        /// Заказать крафт amount штук item. Недостающие промежуточные детали заказываются рекурсивно,
        /// если на них есть шаблоны. Возвращает false и список недостающего сырья, если крафт невозможен.
        /// </summary>
        public bool RequestCraft(MeNetwork net, int item, long amount, List<KeyValuePair<int, long>> missing)
        {
            if (net?.Data == null || amount <= 0) return false;
            var virtualStock = new Dictionary<int, long>();
            var plan = new List<CraftJob>();
            if (!Plan(net, item, amount, false, virtualStock, plan, missing, 0)) return false;
            net.Data.Jobs.AddRange(plan);
            net.Data.LastError = null;
            return true;
        }

        private bool Plan(MeNetwork net, int item, long amount, bool useStock, Dictionary<int, long> virtualStock,
            List<CraftJob> plan, List<KeyValuePair<int, long>> missing, int depth)
        {
            long need = amount;
            if (useStock)
            {
                virtualStock.TryGetValue(item, out long delta);
                long available = Math.Max(0, Count(net, item) + delta);
                long take = Math.Min(available, need);
                virtualStock[item] = delta - take;
                need -= take;
                if (need <= 0) return true;
            }

            int perRun = 0;
            var recipe = depth < MaxPlanDepth ? PatternFor(net, item, out perRun) : null;
            if (recipe == null)
            {
                missing?.Add(new KeyValuePair<int, long>(item, need));
                return false;
            }
            perRun = 0;
            foreach (var o in recipe.Outputs) if (o.ItemId == item) perRun += o.Count;
            int runs = (int)((need + perRun - 1) / perRun);

            bool ok = true;
            foreach (var input in recipe.Inputs)
                ok &= Plan(net, input.ItemId, (long)input.Count * runs, true, virtualStock, plan, missing, depth + 1);
            foreach (var fi in recipe.FluidInputs)
            {
                long fneed = (long)fi.Amount * runs;
                if (FluidCount(net, fi.FluidId) < fneed)
                {
                    missing?.Add(new KeyValuePair<int, long>(-1 - fi.FluidId, fneed));
                    ok = false;
                }
            }
            if (!ok) return false;

            // Излишек (выход кратен партии) пригодится следующим шагам плана
            long surplus = (long)runs * perRun - need;
            if (surplus > 0)
            {
                virtualStock.TryGetValue(item, out long d);
                virtualStock[item] = d + surplus;
            }
            plan.Add(new CraftJob { Recipe = recipe.NumericId, Item = item, PerRun = perRun, Runs = runs });
            return true;
        }

        /// <summary>Сколько item уже «в пути» по незавершённым заказам.</summary>
        public long Pending(MeNetwork net, int item)
        {
            if (net.Data == null) return 0;
            long n = net.Data.InFlight.TryGetValue(item, out long fl) ? fl : 0;
            foreach (var j in net.Data.Jobs)
                if (j.Item == item) n += (long)j.RunsLeft * j.PerRun;
            return n;
        }

        private void TickJobs(MeNetwork net)
        {
            var jobs = net.Data?.Jobs;
            if (jobs == null || jobs.Count == 0) return;
            var busy = new HashSet<MachineInstance>();
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (job.RunsLeft <= 0) continue;
                var recipe = _f.Content.Recipes.Get(job.Recipe);
                if (recipe == null)
                {
                    job.Dispatched = job.Runs;
                    continue;
                }
                foreach (var p in net.Providers)
                {
                    if (job.RunsLeft <= 0) break;
                    if (!p.Patterns.Contains(job.Recipe)) continue;
                    var m = _f.MachineAt(TargetOf(p));
                    if (m == null || m.Def.IsMeDevice || busy.Contains(m)) continue;
                    if (!m.Input.IsEmpty || m.CurrentRecipe != null || m.QueuedRuns > 0) continue;
                    int k = BatchThatFits(net, m, recipe, Math.Min(job.RunsLeft, MaxBatch));
                    if (k <= 0) continue;
                    Dispatch(net, m, recipe, k);
                    job.Dispatched += k;
                    var inFlight = net.Data.InFlight;
                    inFlight[job.Item] = (inFlight.TryGetValue(job.Item, out long fl) ? fl : 0) + (long)k * job.PerRun;
                    busy.Add(m);
                }
            }
            jobs.RemoveAll(j => j.RunsLeft <= 0);
        }

        private int BatchThatFits(MeNetwork net, MachineInstance m, RecipeDef r, int maxRuns)
        {
            for (int k = maxRuns; k >= 1; k--)
            {
                bool ok = true;
                var stacks = new List<ItemStack>();
                foreach (var i in r.Inputs)
                {
                    long need = (long)i.Count * k;
                    if (Count(net, i.ItemId) < need) { ok = false; break; }
                    int max = _f.Content.Item(i.ItemId)?.MaxStack ?? GameConstants.MaxStackSize;
                    for (long left = need; left > 0; left -= max) stacks.Add(new ItemStack(i.ItemId, (int)Math.Min(left, max)));
                }
                if (!ok) continue;
                if (!m.Input.CanInsertAll(stacks)) continue;
                foreach (var fi in r.FluidInputs)
                {
                    int need = fi.Amount * k;
                    if (FluidCount(net, fi.FluidId) < need || _f.FillMachine(m, fi.FluidId, need, true) < need) { ok = false; break; }
                }
                if (ok) return k;
            }
            return 0;
        }

        private void Dispatch(MeNetwork net, MachineInstance m, RecipeDef r, int k)
        {
            foreach (var i in r.Inputs)
            {
                long got = Extract(net, i.ItemId, (long)i.Count * k);
                int max = _f.Content.Item(i.ItemId)?.MaxStack ?? GameConstants.MaxStackSize;
                for (long left = got; left > 0; left -= max) m.Input.Insert(new ItemStack(i.ItemId, (int)Math.Min(left, max)));
            }
            foreach (var fi in r.FluidInputs)
            {
                long got = ExtractFluid(net, fi.FluidId, (long)fi.Amount * k);
                _f.FillMachine(m, fi.FluidId, (int)got, false);
            }
            // Машина обязана сделать именно этот рецепт k раз (а не другой из тех же входов)
            m.QueuedRecipe = r;
            m.QueuedRuns = k;
        }

        /// <summary>
        /// Сверка «в пути» с реальностью: сколько каждого предмета действительно ещё придёт в сеть —
        /// заказанные запуски в машинах провайдеров, текущий цикл и готовое в выходе. Ошибочный учёт
        /// (машину сломали, заказ потерян, загрузка сохранения) больше не останавливает автозапас навсегда.
        /// </summary>
        private void ReconcileInFlight(MeNetwork net)
        {
            var data = net.Data;
            if (data == null) return;
            var real = new Dictionary<int, long>();
            void Add(int item, long n)
            {
                if (item < 0 || n <= 0) return;
                real[item] = (real.TryGetValue(item, out long v) ? v : 0) + n;
            }
            var seen = new HashSet<MachineInstance>();
            foreach (var p in net.Providers)
            {
                if (p.Patterns.Count == 0) continue;
                var m = _f.MachineAt(TargetOf(p));
                if (m == null || m.Def.IsMeDevice || !seen.Add(m)) continue;
                if (m.CurrentRecipe != null)
                    foreach (var s in m.PendingOutputs) Add(s.Item, s.Count);
                if (m.QueuedRecipe != null && m.QueuedRuns > 0)
                    foreach (var o in m.QueuedRecipe.Outputs) Add(o.ItemId, (long)o.Count * m.QueuedRuns);
                foreach (var s in m.Output.NonEmpty()) Add(s.Item, s.Count);
            }
            data.InFlight.Clear();
            foreach (var kv in real) data.InFlight[kv.Key] = kv.Value;
        }

        private void TickStock(MeNetwork net)
        {
            var data = net.Data;
            if (data == null) return;
            foreach (var rule in data.Stock)
            {
                long have = Count(net, rule.Item) + Pending(net, rule.Item);
                if (have >= rule.Target) continue;
                var missing = new List<KeyValuePair<int, long>>();
                if (!RequestCraft(net, rule.Item, rule.Target - have, missing))
                    data.LastError = "ui.me.stock_blocked";
            }
        }

        public void SetStock(MeNetwork net, int item, long target)
        {
            var data = net?.Data;
            if (data == null) return;
            data.Stock.RemoveAll(s => s.Item == item);
            if (target > 0) data.Stock.Add(new StockRule { Item = item, Target = target });
        }

        public void CancelJobs(MeNetwork net)
        {
            net?.Data?.Jobs.Clear();
            net?.Data?.InFlight.Clear();
        }

        // ============================================================ Снимок для UI

        public MeSnapshot Snapshot(Int3 access)
        {
            var s = new MeSnapshot { Access = access };
            var net = NetworkAt(access);
            if (net == null) return s;
            s.Exists = true;
            s.Online = net.Online;
            s.Error = net.Error ?? net.Data?.LastError;
            s.Power = net.PowerUsage;
            foreach (var c in net.ItemCells)
            {
                s.ItemCapacity += c.Capacity;
                s.ItemUsed += c.Used;
            }
            foreach (var c in net.FluidCells)
            {
                s.FluidCapacity += c.Capacity;
                s.FluidUsed += c.Used;
            }
            foreach (var kv in Aggregate(net, false)) s.Items.Add(kv);
            foreach (var kv in Aggregate(net, true)) s.Fluids.Add(kv);
            s.Items.Sort((a, b) => b.Value.CompareTo(a.Value));
            var craftable = new HashSet<int>();
            foreach (var r in Patterns(net))
                foreach (var o in r.Outputs)
                    if (craftable.Add(o.ItemId)) s.Craftable.Add(o.ItemId);
            if (net.Data != null)
            {
                var pending = new Dictionary<int, long>();
                foreach (var j in net.Data.Jobs)
                    pending[j.Item] = (pending.TryGetValue(j.Item, out var n) ? n : 0) + (long)j.RunsLeft * j.PerRun;
                foreach (var kv in net.Data.InFlight)
                    pending[kv.Key] = (pending.TryGetValue(kv.Key, out var n) ? n : 0) + kv.Value;
                foreach (var kv in pending) s.Jobs.Add(kv);
                s.Stock.AddRange(net.Data.Stock);
            }
            return s;
        }

        // ============================================================ Сохранение

        public void Write(BinaryWriter w)
        {
            w.Write(1);
            w.Write(_nextCellId);
            w.Write(_cells.Count);
            foreach (var c in _cells.Values)
            {
                w.Write(c.Id);
                w.Write(c.Fluid);
                w.Write(c.Capacity);
                w.Write(c.Contents.Count);
                foreach (var kv in c.Contents)
                {
                    w.Write(c.Fluid ? _f.Content.Fluids.IdOf(kv.Key) ?? "" : _f.Content.Items.IdOf(kv.Key) ?? "");
                    w.Write(kv.Value);
                }
            }
            w.Write(_cables.Count);
            foreach (var p in _cables)
            {
                w.Write(_cableBlocks[p].Id);
                w.Write(p.X);
                w.Write(p.Y);
                w.Write(p.Z);
            }
            w.Write(_links.Count);
            foreach (var p in _links)
            {
                w.Write(p.X);
                w.Write(p.Y);
                w.Write(p.Z);
            }
        }

        public void Read(BinaryReader r)
        {
            _cells.Clear();
            _cables.Clear();
            _cableBlocks.Clear();
            _links.Clear();
            int ver = r.ReadInt32();
            if (ver > 1) throw new InvalidDataException("ME save version " + ver);
            _nextCellId = r.ReadInt32();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var c = new CellStorage { Id = r.ReadInt32(), Fluid = r.ReadBoolean(), Capacity = r.ReadInt64() };
                int k = r.ReadInt32();
                for (int j = 0; j < k; j++)
                {
                    string id = r.ReadString();
                    long amount = r.ReadInt64();
                    int key = c.Fluid ? _f.Content.Fluids.IndexOf(id) : _f.Content.Items.IndexOf(id);
                    if (key < 0) continue;
                    c.Contents[key] = amount;
                    c.Used += amount;
                }
                _cells[c.Id] = c;
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var block = _f.Content.Blocks.Get(r.ReadString());
                var p = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                if (block != null) AddCable(p, block);
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++) _links.Add(new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
            _dirty = true;
        }

        /// <summary>Доп. данные ME-устройства в сохранении машины.</summary>
        public static void WriteDevice(BinaryWriter w, MachineInstance m, ContentDatabase content)
        {
            for (int i = 0; i < m.Filter.Length; i++) w.Write(m.Filter[i] >= 0 ? content.Items.IdOf(m.Filter[i]) ?? "" : "");
            w.Write(m.FluidFilter >= 0 ? content.Fluids.IdOf(m.FluidFilter) ?? "" : "");
            w.Write(m.Patterns.Count);
            foreach (var p in m.Patterns) w.Write(content.Recipes.IdOf(p) ?? "");
            var data = m.MeData;
            w.Write(data != null);
            if (data == null) return;
            w.Write(data.Stock.Count);
            foreach (var s in data.Stock)
            {
                w.Write(content.Items.IdOf(s.Item) ?? "");
                w.Write(s.Target);
            }
            w.Write(data.Jobs.Count);
            foreach (var j in data.Jobs)
            {
                w.Write(content.Recipes.IdOf(j.Recipe) ?? "");
                w.Write(content.Items.IdOf(j.Item) ?? "");
                w.Write(j.PerRun);
                w.Write(j.Runs);
                w.Write(j.Dispatched);
            }
        }

        public static void ReadDevice(BinaryReader r, MachineInstance m, ContentDatabase content)
        {
            for (int i = 0; i < 9; i++)
            {
                string id = r.ReadString();
                if (m != null) m.Filter[i] = id.Length > 0 ? content.Items.IndexOf(id) : -1;
            }
            string fid = r.ReadString();
            if (m != null) m.FluidFilter = fid.Length > 0 ? content.Fluids.IndexOf(fid) : -1;
            int pc = r.ReadInt32();
            for (int i = 0; i < pc; i++)
            {
                int rid = content.Recipes.IndexOf(r.ReadString());
                if (m != null && rid >= 0) m.Patterns.Add(rid);
            }
            if (!r.ReadBoolean()) return;
            var data = new MeControllerData();
            int sc = r.ReadInt32();
            for (int i = 0; i < sc; i++)
            {
                int item = content.Items.IndexOf(r.ReadString());
                long target = r.ReadInt64();
                if (item >= 0) data.Stock.Add(new StockRule { Item = item, Target = target });
            }
            int jc = r.ReadInt32();
            for (int i = 0; i < jc; i++)
            {
                int recipe = content.Recipes.IndexOf(r.ReadString());
                int item = content.Items.IndexOf(r.ReadString());
                var job = new CraftJob { Recipe = recipe, Item = item, PerRun = r.ReadInt32(), Runs = r.ReadInt32(), Dispatched = r.ReadInt32() };
                if (recipe >= 0 && item >= 0) data.Jobs.Add(job);
            }
            if (m != null) m.MeData = data;
        }
    }
}
