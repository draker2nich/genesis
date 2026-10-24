using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.World;

namespace Genesis.Simulation
{
    /// <summary>
    /// Фабрика: все машины, конвейеры и трубы планеты. Хранится в компактных структурах
    /// отдельно от чанков и симулируется на фиксированном тике 20 TPS независимо от загрузки чанков.
    /// </summary>
    public sealed class Factory
    {
        public readonly ContentDatabase Content;
        public readonly VoxelWorld World;
        public readonly ProductionStats Stats = new ProductionStats();

        private readonly List<MachineInstance> _machines = new List<MachineInstance>();
        private readonly Dictionary<Int3, MachineInstance> _machineAt = new Dictionary<Int3, MachineInstance>();

        private readonly List<Conveyor> _conveyors = new List<Conveyor>();
        private readonly Dictionary<Int3, Conveyor> _conveyorAt = new Dictionary<Int3, Conveyor>();
        private bool _conveyorOrderDirty;

        private readonly Dictionary<Int3, Pipe> _pipeAt = new Dictionary<Int3, Pipe>();
        private readonly List<PipeNetwork> _networks = new List<PipeNetwork>();
        private bool _networksDirty;

        private readonly Dictionary<Int3, Wire> _wireAt = new Dictionary<Int3, Wire>();
        private readonly List<PowerNetwork> _power = new List<PowerNetwork>();
        private readonly HashSet<MachineInstance> _powerScratch = new HashSet<MachineInstance>();
        private bool _powerDirty;

        /// <summary>Освещённость 0..1 (солнечные панели). Задаёт сервер по времени суток.</summary>
        public float Daylight = 1f;

        /// <summary>Сила ветра 0.4..1.25, медленно меняется со временем.</summary>
        public float Wind => (float)(0.8 + 0.3 * Math.Sin(TickCount / 1300.0) + 0.15 * Math.Sin(TickCount / 370.0));

        private readonly Dictionary<int, HashSet<int>> _recipeInputs = new Dictionary<int, HashSet<int>>();
        private readonly List<Int3> _pendingRemovals = new List<Int3>();

        /// <summary>Убрать блок машины после текущего тика (машина сама решила исчезнуть: пустые останки).</summary>
        public void ScheduleRemoval(Int3 pos)
        {
            if (!_pendingRemovals.Contains(pos)) _pendingRemovals.Add(pos);
        }

        public long TickCount { get; private set; }

        /// <summary>
        /// Реплика на клиенте: машины не симулируются (состояние приходит от хоста),
        /// локально двигаются только ленты — для плавной картинки между снапшотами.
        /// </summary>
        public bool IsReplica;

        /// <summary>
        /// Фильтр машинных рецептов: дерево знаний закрывает автоматизацию,
        /// пока процесс не выполнен вручную. null — доступны все.
        /// </summary>
        public Func<RecipeDef, bool> RecipeFilter;

        public IReadOnlyList<MachineInstance> Machines => _machines;
        public IReadOnlyList<Conveyor> Conveyors => _conveyors;
        public IEnumerable<Pipe> Pipes => _pipeAt.Values;
        public IEnumerable<Wire> Wires => _wireAt.Values;
        public Wire WireAt(Int3 p) => _wireAt.TryGetValue(p, out var w) ? w : null;

        public IReadOnlyList<PowerNetwork> PowerNetworks
        {
            get
            {
                if (_powerDirty) RebuildPower();
                return _power;
            }
        }

        public IReadOnlyList<PipeNetwork> Networks
        {
            get
            {
                if (_networksDirty) RebuildNetworks();
                return _networks;
            }
        }

        public event Action<MachineInstance> MachineAdded;
        public event Action<Int3> MachineRemoved;
        /// <summary>Изменилась ориентация/настройка сущности — клиенту перерисовать блок.</summary>
        public event Action<Int3> EntityChanged;
        /// <summary>Удалена любая сущность (машина, лента, труба).</summary>
        public event Action<Int3> EntityRemoved;

        /// <summary>ME-сети (хранение, шины, автокрафт).</summary>
        public readonly MeSystem Me;

        public Factory(ContentDatabase content, VoxelWorld world)
        {
            Content = content;
            World = world;
            Me = new MeSystem(this);
        }

        // ============================================================ Размещение

        public MachineInstance MachineAt(Int3 p) => _machineAt.TryGetValue(p, out var m) ? m : null;
        public Conveyor ConveyorAt(Int3 p) => _conveyorAt.TryGetValue(p, out var c) ? c : null;
        public Pipe PipeAt(Int3 p) => _pipeAt.TryGetValue(p, out var c) ? c : null;

        /// <summary>Создаёт сущность фабрики для блока (если блок — машина/конвейер/труба).</summary>
        public void OnBlockPlaced(Int3 pos, BlockDef block, Direction facing)
        {
            if (block.MachineId != null)
            {
                var def = Content.Machines.Get(block.MachineId);
                var behavior = MachineBehaviors.Get(def.Behavior);
                if (behavior == null)
                {
                    Log.Error($"Unknown machine behavior '{def.Behavior}' for {def.Id}");
                    return;
                }
                AddMachine(new MachineInstance(Content, def, pos, facing, behavior));
            }
            else if (block.Transport != null)
            {
                if (block.Transport.Type == "conveyor")
                {
                    var c = new Conveyor(pos, facing, block);
                    _conveyors.Add(c);
                    _conveyorAt[pos] = c;
                    _conveyorOrderDirty = true;
                    EntityChanged?.Invoke(pos);
                }
                else if (block.Transport.Type == "pipe")
                {
                    _pipeAt[pos] = new Pipe(pos, block);
                    _networksDirty = true;
                }
                else if (block.Transport.Type == "wire")
                {
                    _wireAt[pos] = new Wire(pos, block);
                    _powerDirty = true;
                }
                else if (block.Transport.Type == "me")
                {
                    Me.AddCable(pos, block);
                }
            }
        }

        private void AddMachine(MachineInstance m)
        {
            _machines.Add(m);
            _machineAt[m.Pos] = m;
            _networksDirty = true;
            _powerDirty = true;
            if (m.Def.IsMeDevice) Me.MarkDirty();
            MachineAdded?.Invoke(m);
        }

        /// <summary>Удаляет сущность; возвращает выпавшие предметы.</summary>
        public List<ItemStack> OnBlockRemoved(Int3 pos)
        {
            var drops = ContentsOf(pos);
            if (_machineAt.TryGetValue(pos, out var m))
            {
                _machineAt.Remove(pos);
                _machines.Remove(m);
                _networksDirty = true;
                _powerDirty = true;
                if (m.Def.IsMeDevice) Me.MarkDirty();
                MachineRemoved?.Invoke(pos);
            }
            if (_conveyorAt.TryGetValue(pos, out var c))
            {
                _conveyorAt.Remove(pos);
                _conveyors.Remove(c);
                _conveyorOrderDirty = true;
            }
            if (Me.RemoveCable(pos))
            {
                EntityRemoved?.Invoke(pos);
                return drops;
            }
            if (_wireAt.Remove(pos))
            {
                _powerDirty = true;
                EntityRemoved?.Invoke(pos);
                return drops;
            }
            if (_pipeAt.TryGetValue(pos, out var p))
            {
                PrepareNetworkShares();
                p.ShareAmount = 0; // жидкость в удалённом сегменте теряется
                _pipeAt.Remove(pos);
                _networksDirty = true;
                EntityRemoved?.Invoke(pos);
                return drops;
            }
            if (m != null || c != null) EntityRemoved?.Invoke(pos);
            return drops;
        }

        /// <summary>
        /// Что выпадет при поломке блока: инвентари машины, предметы на ленте и сырьё текущего цикла
        /// (входы списываются в начале цикла — при поломке посреди работы они возвращаются).
        /// </summary>
        public List<ItemStack> ContentsOf(Int3 pos)
        {
            var drops = new List<ItemStack>();
            if (_machineAt.TryGetValue(pos, out var m))
            {
                drops.AddRange(m.Input.NonEmpty());
                drops.AddRange(m.Output.NonEmpty());
                drops.AddRange(m.Fuel.NonEmpty());
                if (m.CurrentRecipe != null && m.Def.Behavior != "drill")
                    foreach (var i in m.CurrentRecipe.Inputs)
                        if (i.ItemId >= 0 && i.Count > 0) drops.Add(new ItemStack(i.ItemId, i.Count));
            }
            if (_conveyorAt.TryGetValue(pos, out var c))
                foreach (var it in c.Items) drops.Add(new ItemStack(it.Item, 1, it.Meta));
            return drops;
        }

        // ============================================================ Реплика (клиент)

        /// <summary>Создать/обновить машину по данным хоста.</summary>
        public MachineInstance EnsureMachine(MachineDef def, Int3 pos, Direction facing)
        {
            if (_machineAt.TryGetValue(pos, out var m) && m.Def != def)
            {
                OnBlockRemoved(pos);
                m = null;
            }
            if (m == null)
            {
                var behavior = MachineBehaviors.Get(def.Behavior) ?? new StorageBehavior();
                m = new MachineInstance(Content, def, pos, facing, behavior);
                AddMachine(m);
                EntityChanged?.Invoke(pos);
            }
            else if (m.Facing != facing)
            {
                m.Facing = facing;
                EntityChanged?.Invoke(pos);
            }
            return m;
        }

        /// <summary>Создать/обновить ленту по данным хоста.</summary>
        public Conveyor EnsureConveyor(BlockDef block, Int3 pos, Direction facing)
        {
            if (_conveyorAt.TryGetValue(pos, out var c) && c.Block != block)
            {
                OnBlockRemoved(pos);
                c = null;
            }
            if (c == null)
            {
                c = new Conveyor(pos, facing, block);
                _conveyors.Add(c);
                _conveyorAt[pos] = c;
                _conveyorOrderDirty = true;
                EntityChanged?.Invoke(pos);
            }
            else if (c.Facing != facing)
            {
                c.Facing = facing;
                _conveyorOrderDirty = true;
                EntityChanged?.Invoke(pos);
            }
            return c;
        }

        /// <summary>Повернуть машину/конвейер (гаечный ключ).</summary>
        public bool Rotate(Int3 pos)
        {
            if (_machineAt.TryGetValue(pos, out var m))
            {
                m.Facing = m.Facing.RotateCW();
                _networksDirty = true;
                EntityChanged?.Invoke(pos);
                return true;
            }
            if (_conveyorAt.TryGetValue(pos, out var c))
            {
                c.Facing = c.Facing.RotateCW();
                _conveyorOrderDirty = true;
                EntityChanged?.Invoke(pos);
                return true;
            }
            return false;
        }

        /// <summary>Выбрать рецепт машины (null — авто). Возвращает false, если рецепт не этой машины.</summary>
        public bool SetLockedRecipe(Int3 pos, RecipeDef recipe)
        {
            if (!_machineAt.TryGetValue(pos, out var m)) return false;
            if (recipe != null)
            {
                bool own = false;
                foreach (var r in Content.RecipesFor(m.Def.NumericId))
                    if (r == recipe) { own = true; break; }
                if (!own) return false;
            }
            m.LockedRecipe = recipe;
            EntityChanged?.Invoke(pos);
            return true;
        }

        /// <summary>
        /// Поставив трубу в pos, игрок соединил бы сети с разными жидкостями (одна из них исчезла бы при слиянии).
        /// </summary>
        public bool WouldMixFluids(Int3 pos)
        {
            if (_networksDirty) RebuildNetworks();
            int fluid = -1;
            foreach (var d in Directions.All)
            {
                if (!_pipeAt.TryGetValue(pos + d.Offset(), out var p) || p.Network == null) continue;
                var net = p.Network;
                if (net.Amount <= 0 || net.Fluid < 0) continue;
                if (fluid >= 0 && fluid != net.Fluid) return true;
                fluid = net.Fluid;
            }
            return false;
        }

        public void SetSide(Int3 pos, RelativeSide side, SideType type)
        {
            if (!_machineAt.TryGetValue(pos, out var m)) return;
            m.SetSide(side, type);
            _networksDirty = true;
        }

        // ============================================================ Тик

        public void Tick()
        {
            if (_conveyorOrderDirty) RebuildConveyorOrder();
            if (IsReplica)
            {
                for (int i = 0; i < _conveyors.Count; i++)
                    TickConveyor(_conveyors[i]);
                TickCount++;
                return;
            }
            if (_networksDirty) RebuildNetworks();
            if (_powerDirty) RebuildPower();

            // 1. Логика машин
            for (int i = 0; i < _machines.Count; i++)
                _machines[i].Behavior.Tick(_machines[i], this);

            // 2. Выталкивание предметов и жидкостей из машин в соседей
            for (int i = 0; i < _machines.Count; i++)
                Eject(_machines[i]);

            // 3. Трубопроводные сети и электросети
            for (int i = 0; i < _networks.Count; i++)
                TickNetwork(_networks[i]);
            for (int i = 0; i < _power.Count; i++)
                TickPower(_power[i]);

            // 4. ME-сети: хранение, шины, автокрафт
            Me.Tick();

            // 4. Конвейеры (от конца цепочки к началу)
            for (int i = 0; i < _conveyors.Count; i++)
                TickConveyor(_conveyors[i]);

            Stats.Tick();
            TickCount++;

            if (_pendingRemovals.Count > 0)
            {
                foreach (var pos in _pendingRemovals)
                {
                    if (!_machineAt.ContainsKey(pos)) continue;
                    if (World != null && !World.IsLoaded(pos.X, pos.Y, pos.Z)) continue; // уберём, когда чанк загрузится
                    OnBlockRemoved(pos);
                    World?.SetBlock(pos, Content.AirNumeric);
                }
                _pendingRemovals.Clear();
            }
        }

        // ============================================================ Предметы между блоками

        /// <summary>Входы открытых (RecipeFilter) рецептов машины. Кэш сбрасывается при изменении знаний.</summary>
        private HashSet<int> RecipeInputsOf(MachineDef def)
        {
            if (_recipeInputs.TryGetValue(def.NumericId, out var set)) return set;
            set = new HashSet<int>();
            foreach (var r in Content.RecipesFor(def.NumericId))
            {
                if (RecipeFilter != null && !RecipeFilter(r)) continue; // неоткрытый рецепт не забивает вход сырьём
                foreach (var i in r.Inputs) set.Add(i.ItemId);
            }
            _recipeInputs[def.NumericId] = set;
            return set;
        }

        /// <summary>Открылись новые рецепты — пересчитать, что машины принимают на вход.</summary>
        public void InvalidateRecipeCache() => _recipeInputs.Clear();

        /// <summary>Подходит ли предмет как вход машины (сырьё). Выбран рецепт — только его входы.</summary>
        public bool AcceptsAsInput(MachineInstance m, int item)
        {
            if (m.Def.Behavior == "storage") return true;
            var pinned = m.PinnedRecipe;
            if (pinned != null)
            {
                foreach (var i in pinned.Inputs)
                    if (i.ItemId == item) return true;
                return false;
            }
            return RecipeInputsOf(m.Def).Contains(item);
        }

        /// <summary>
        /// Вставить предметы в блок pos со стороны грани face (грань целевого блока).
        /// Возвращает вставленное количество.
        /// </summary>
        public int InsertInto(Int3 pos, ItemStack stack, Direction face, bool simulate = false)
        {
            if (stack.IsEmpty) return 0;

            if (_conveyorAt.TryGetValue(pos, out var conv))
            {
                // Лента не принимает с собственного выхода
                if (conv.Facing == face) return 0;
                if (!conv.CanAccept()) return 0;
                if (!simulate) conv.Accept(stack.Item, 0f, -conv.Speed, stack.Meta);
                return 1;
            }

            if (_machineAt.TryGetValue(pos, out var m))
            {
                if (m.SideAt(face) != SideType.ItemIn) return 0;
                return InsertIntoMachine(m, stack, simulate);
            }
            return 0;
        }

        /// <summary>Вставка в машину с маршрутизацией сырьё/топливо (без проверки стороны — для GUI).</summary>
        public int InsertIntoMachine(MachineInstance m, ItemStack stack, bool simulate = false)
        {
            if (m.Def.Behavior == "storage") return m.Input.Insert(stack, simulate);
            var def = Content.Item(stack.Item);
            if (m.Def.Behavior == "me_drive") return def?.StorageCell != null ? m.Input.Insert(stack, simulate) : 0;
            if (m.Def.IsMeDevice) return 0;
            // Заполненная ячейка ME в обрабатывающую машину не попадает — её съел бы рецепт вместе с содержимым
            if (def?.StorageCell != null && Me.CellHasContents(stack.Meta)) return 0;

            bool isFuel = def != null && def.IsFuel && m.Fuel.Size > 0;
            bool isInput = m.Input.Size > 0 && AcceptsAsInput(m, stack.Item);

            // И сырьё, и топливо (бревно в горне): если рецепт не выбран и машине нечем топиться — сначала в топливо.
            // Раньше бревно с ленты всегда уходило на пережиг в уголь, и горн, который кормят одними брёвнами, вставал.
            if (isInput && isFuel && m.PinnedRecipe == null && m.BurnLeft <= 0 && m.Fuel.IsEmpty)
            {
                int f = m.Fuel.Insert(stack.WithCount(Math.Min(stack.Count, 4)), simulate);
                if (f > 0) return f;
            }

            if (isInput)
            {
                int n = m.Input.Insert(stack, simulate);
                if (n > 0 || !isFuel) return n;
            }
            if (isFuel) return m.Fuel.Insert(stack, simulate);
            return 0;
        }

        private void Eject(MachineInstance m)
        {
            for (int s = 0; s < 6; s++)
            {
                var side = m.Sides[s];
                if (side != SideType.ItemOut && side != SideType.FluidOut && side != SideType.SteamOut) continue;
                var dir = Directions.ToAbsolute((RelativeSide)s, m.Facing);
                var target = m.Pos + dir.Offset();
                var face = dir.Opposite();

                if (side == SideType.ItemOut)
                {
                    // Пробуем слоты по очереди: если соседу не подходит первый предмет (например, побочка),
                    // остальное всё равно уходит, а не стоит в очереди за ним
                    var inv = m.EjectInventory;
                    int limit = _conveyorAt.ContainsKey(target) ? 1 : 4;
                    for (int slot = 0; slot < inv.Size; slot++)
                    {
                        var st = inv[slot];
                        if (st.IsEmpty) continue;
                        int n = InsertInto(target, st.WithCount(Math.Min(limit, st.Count)), face);
                        if (n <= 0) continue;
                        inv.ExtractFromSlot(slot, n);
                        break;
                    }
                }
                else if (_machineAt.TryGetValue(target, out var other))
                {
                    // Прямая передача жидкости машина → машина (насос вплотную к котлу)
                    var otherSide = other.SideAt(face);
                    if (otherSide != SideType.FluidIn && otherSide != SideType.SteamIn) continue;
                    foreach (var t in m.Tanks)
                    {
                        if (!t.IsOutput || t.IsEmpty) continue;
                        if (!SideAllowsFluid(side, t.Fluid) || !SideAllowsFluid(otherSide, t.Fluid)) continue;
                        if (other == m) continue;
                        int moved = FillMachine(other, t.Fluid, Math.Min(t.Amount, 200), false);
                        t.Drain(moved);
                    }
                }
            }
        }

        private int SteamId => Content.Fluids.IndexOf("genesis:steam");

        private bool SideAllowsFluid(SideType side, int fluid)
        {
            int steam = SteamId;
            if (side == SideType.SteamIn || side == SideType.SteamOut) return fluid == steam;
            return true;
        }

        /// <summary>Залить жидкость во входные баки машины.</summary>
        public int FillMachine(MachineInstance m, int fluid, int amount, bool simulate)
        {
            int left = amount;
            foreach (var t in m.Tanks)
            {
                if (!t.AcceptsFill || left <= 0) continue;
                left -= t.Fill(fluid, left, simulate);
            }
            return amount - left;
        }

        // ============================================================ Манипуляторы и буры

        /// <summary>Положить предмет в блок (для манипулятора): машины — без учёта сторон, ленты — как обычно.</summary>
        public int PutInto(Int3 target, ItemStack stack, Direction face, bool simulate = false)
        {
            if (stack.IsEmpty) return 0;
            if (_conveyorAt.ContainsKey(target)) return InsertInto(target, stack.WithCount(1), face, simulate);
            if (_machineAt.TryGetValue(target, out var m))
            {
                if (m.Def.Behavior == "inserter") return 0;
                if (m.Def.IsMeDevice && m.Def.Behavior != "me_drive") return 0;
                return InsertIntoMachine(m, stack, simulate);
            }
            return 0;
        }

        /// <summary>
        /// Взять один предмет из источника, но только такой, который примет цель —
        /// манипулятор не хватает то, что некуда положить.
        /// </summary>
        public ItemStack TakeFrom(Int3 source, Direction sourceFace, Int3 target, Direction targetFace)
        {
            if (_conveyorAt.TryGetValue(source, out var conv))
            {
                for (int i = 0; i < conv.Items.Count; i++)
                {
                    var cand = conv.StackAt(i);
                    if (PutInto(target, cand, targetFace, true) <= 0) continue;
                    conv.Items.RemoveAt(i);
                    return cand;
                }
                return ItemStack.Empty;
            }
            if (_machineAt.TryGetValue(source, out var m) && m.Def.Behavior != "inserter" && !m.Def.IsMeDevice)
            {
                var inv = m.EjectInventory;
                for (int slot = 0; slot < inv.Size; slot++)
                {
                    var s = inv[slot];
                    if (s.IsEmpty) continue;
                    var cand = s.WithCount(1);
                    if (PutInto(target, cand, targetFace, true) <= 0) continue;
                    return inv.ExtractFromSlot(slot, 1);
                }
            }
            return ItemStack.Empty;
        }

        /// <summary>Что за залежь под буром (блок снизу). Незагруженный чанк — оставляем прежнее значение.</summary>
        public void RefreshDeposit(MachineInstance m)
        {
            if (World == null) return;
            var below = m.Pos + Int3.Down;
            if (!World.IsLoaded(below.X, below.Y, below.Z)) return; // чанк выгружен — залежь из сохранения
            var def = World.GetBlockDef(below);
            if (def.DepositOf != null)
            {
                m.DepositItem = Content.Items.IndexOf(def.DepositOf);
                m.DepositPurity = Math.Max(1, def.Purity);
            }
            else m.DepositItem = -1;
        }

        // ============================================================ Конвейеры

        private void RebuildConveyorOrder()
        {
            _conveyorOrderDirty = false;
            foreach (var c in _conveyors) c.Depth = -1;
            foreach (var c in _conveyors) ComputeDepth(c);
            _conveyors.Sort((a, b) =>
            {
                int d = a.Depth.CompareTo(b.Depth);
                if (d != 0) return d;
                // Стабильный детерминированный порядок
                int x = a.Pos.X.CompareTo(b.Pos.X);
                if (x != 0) return x;
                int y = a.Pos.Y.CompareTo(b.Pos.Y);
                return y != 0 ? y : a.Pos.Z.CompareTo(b.Pos.Z);
            });
        }

        private int ComputeDepth(Conveyor start)
        {
            if (start.Depth >= 0) return start.Depth;
            // Итеративно идём вниз по цепочке, защищаясь от циклов
            var chain = new List<Conveyor>();
            var visited = new HashSet<Conveyor>();
            var c = start;
            int baseDepth = 0;
            while (c != null)
            {
                if (c.Depth >= 0)
                {
                    baseDepth = c.Depth + 1;
                    break;
                }
                if (!visited.Add(c)) break; // цикл
                chain.Add(c);
                c = ConveyorAt(c.Next);
            }
            for (int i = chain.Count - 1; i >= 0; i--)
                chain[i].Depth = baseDepth + (chain.Count - 1 - i);
            return start.Depth;
        }

        private void TickConveyor(Conveyor c)
        {
            var items = c.Items;
            for (int i = 0; i < items.Count; i++) items[i].PrevPos = items[i].Pos;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                float limit = i == 0 ? 1f : items[i - 1].Pos - Conveyor.Spacing;
                float target = it.Pos + c.Speed;

                if (i == 0 && target >= 1f)
                {
                    // Пытаемся передать дальше
                    var next = c.Next;
                    var face = c.Facing.Opposite();
                    var nextConv = ConveyorAt(next);
                    bool moved;
                    if (nextConv != null)
                    {
                        if (nextConv.Facing != face && nextConv.CanAccept(0f))
                        {
                            float overshoot = Math.Min(target - 1f, Conveyor.Spacing * 0.5f);
                            nextConv.Accept(it.Item, overshoot, overshoot - c.Speed, it.Meta);
                            moved = true;
                        }
                        else moved = false;
                    }
                    else
                    {
                        // Реплика не знает, примет ли машина предмет, — ждёт снапшот хоста.
                        moved = !IsReplica && InsertInto(next, new ItemStack(it.Item, 1, it.Meta), face) > 0;
                    }

                    if (moved)
                    {
                        items.RemoveAt(0);
                        i--;
                        continue;
                    }
                    it.Pos = 1f;
                    continue;
                }

                it.Pos = Math.Max(it.Pos, Math.Min(target, limit));
            }
        }

        // ============================================================ Трубы

        private void PrepareNetworkShares()
        {
            foreach (var p in _pipeAt.Values)
            {
                var net = p.Network;
                if (net == null || net.Pipes.Count == 0)
                {
                    if (p.ShareFluid < 0) p.ShareAmount = 0;
                    continue;
                }
                p.ShareFluid = net.Fluid;
                p.ShareAmount = net.Amount / net.Pipes.Count;
            }
            // Остаток от деления — первому сегменту каждой сети
            foreach (var net in _networks)
                if (net.Pipes.Count > 0) net.Pipes[0].ShareAmount += net.Amount % net.Pipes.Count;
            foreach (var net in _networks) net.Pipes.Clear(); // помечаем: доли уже сняты
        }

        private void RebuildNetworks()
        {
            _networksDirty = false;
            PrepareNetworkShares();
            _networks.Clear();
            foreach (var p in _pipeAt.Values) p.Network = null;

            var stack = new Stack<Pipe>();
            foreach (var start in _pipeAt.Values)
            {
                if (start.Network != null) continue;
                var net = new PipeNetwork();
                start.Network = net;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    net.Pipes.Add(p);
                    net.Capacity += p.Capacity;
                    net.Throughput = Math.Min(net.Throughput, p.Throughput);
                    if (p.ShareAmount > 0 && p.ShareFluid >= 0 && (net.Fluid < 0 || net.Fluid == p.ShareFluid))
                    {
                        net.Fluid = p.ShareFluid;
                        net.Amount += p.ShareAmount;
                    }
                    p.ShareAmount = 0;
                    p.ShareFluid = -1;

                    foreach (var d in Directions.All)
                    {
                        var np = p.Pos + d.Offset();
                        if (_pipeAt.TryGetValue(np, out var n) && n.Network == null)
                        {
                            n.Network = net;
                            stack.Push(n);
                        }
                        else if (_machineAt.TryGetValue(np, out var m))
                        {
                            var face = d.Opposite();
                            var side = m.SideAt(face);
                            if (side.IsFluid())
                                net.Endpoints.Add(new FluidEndpoint { Machine = m, Face = face, Side = side });
                        }
                    }
                }
                net.Amount = Math.Min(net.Amount, net.Capacity);
                if (net.Throughput == int.MaxValue) net.Throughput = 50;
                _networks.Add(net);
            }
        }

        private void TickNetwork(PipeNetwork net)
        {
            if (net.Endpoints.Count == 0) return;

            // Забор из машин с выходом жидкости
            foreach (var e in net.Endpoints)
            {
                if (e.Side != SideType.FluidOut && e.Side != SideType.SteamOut) continue;
                foreach (var t in e.Machine.Tanks)
                {
                    if (!t.IsOutput || t.IsEmpty) continue;
                    if (!SideAllowsFluid(e.Side, t.Fluid)) continue;
                    if (net.Amount > 0 && net.Fluid != t.Fluid) continue;
                    int take = Math.Min(Math.Min(t.Amount, net.Space), net.Throughput);
                    if (take <= 0) continue;
                    t.Drain(take);
                    net.Fluid = t.Fluid;
                    net.Amount += take;
                }
            }

            if (net.Amount <= 0) return;

            // Раздача потребителям поровну
            int consumers = 0;
            foreach (var e in net.Endpoints)
                if ((e.Side == SideType.FluidIn || e.Side == SideType.SteamIn) && SideAllowsFluid(e.Side, net.Fluid))
                    consumers++;
            if (consumers == 0) return;

            int share = Math.Max(1, net.Amount / consumers);
            foreach (var e in net.Endpoints)
            {
                if (net.Amount <= 0) break;
                if (e.Side != SideType.FluidIn && e.Side != SideType.SteamIn) continue;
                if (!SideAllowsFluid(e.Side, net.Fluid)) continue;
                int give = Math.Min(Math.Min(share, net.Amount), net.Throughput);
                int filled = FillMachine(e.Machine, net.Fluid, give, false);
                net.Amount -= filled;
            }
            if (net.Amount <= 0)
            {
                net.Amount = 0;
                net.Fluid = -1;
            }
        }

        // ============================================================ Электросети

        private void RebuildPower()
        {
            _powerDirty = false;
            _power.Clear();
            foreach (var w in _wireAt.Values) w.Network = null;
            var stack = new Stack<Wire>();
            foreach (var start in _wireAt.Values)
            {
                if (start.Network != null) continue;
                var net = new PowerNetwork();
                _powerScratch.Clear();
                start.Network = net;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var w = stack.Pop();
                    net.Wires.Add(w);
                    net.Throughput = Math.Min(net.Throughput, w.Throughput);
                    foreach (var d in Directions.All)
                    {
                        var np = w.Pos + d.Offset();
                        if (_wireAt.TryGetValue(np, out var n) && n.Network == null)
                        {
                            n.Network = net;
                            stack.Push(n);
                        }
                        else if (_machineAt.TryGetValue(np, out var m) && m.Def.IsPowerNode && _powerScratch.Add(m))
                        {
                            switch (m.Def.PowerRole)
                            {
                                case "generator": net.Generators.Add(m); break;
                                case "storage": net.Storage.Add(m); break;
                                default: net.Consumers.Add(m); break;
                            }
                        }
                    }
                }
                _power.Add(net);
            }
        }

        /// <summary>
        /// Тик сети: генераторы → потребители (поровну по запросу), излишек → ячейки,
        /// нехватка ← из ячеек. Всё ограничено пропускной способностью проводов.
        /// Потребители при нехватке замедляются пропорционально (Energy.Consume).
        /// </summary>
        private void TickPower(PowerNetwork net)
        {
            float cap = net.Throughput;

            // Запрос потребителей: дополнить буфер, но не больше 4 тиков потребления за раз
            float demand = 0;
            foreach (var c in net.Consumers)
                demand += Math.Min(c.EnergySpace, PerTickOf(c) * 4f);

            float genAvail = 0;
            foreach (var g in net.Generators)
                genAvail += RateLimit(g, g.EnergyStored);
            float storeAvail = 0;
            foreach (var s in net.Storage)
                storeAvail += RateLimit(s, s.EnergyStored);

            float toConsumers = Math.Min(Math.Min(demand, genAvail + storeAvail), cap);
            float fromGen = Math.Min(toConsumers, genAvail);
            float fromStore = toConsumers - fromGen;

            // Излишек генераторов — в ячейки
            float storeSpace = 0;
            foreach (var s in net.Storage)
                storeSpace += RateLimit(s, s.EnergySpace);
            float toStore = Math.Min(Math.Min(genAvail - fromGen, storeSpace), cap - toConsumers);
            if (toStore < 0) toStore = 0;

            Drain(net.Generators, fromGen + toStore, genAvail);
            Drain(net.Storage, fromStore, storeAvail);

            if (toConsumers > 0 && demand > 0)
            {
                float k = toConsumers / demand;
                foreach (var c in net.Consumers)
                    c.EnergyStored += Math.Min(c.EnergySpace, PerTickOf(c) * 4f) * k;
            }
            if (toStore > 0 && storeSpace > 0)
            {
                float k = toStore / storeSpace;
                foreach (var s in net.Storage)
                    s.EnergyStored += RateLimit(s, s.EnergySpace) * k;
            }

            // «Обеспечено %»: доля от того, что потребители реально тратят за тик (буферы полны — 100%)
            float need = 0;
            foreach (var c in net.Consumers) need += PerTickOf(c);
            float basis = Math.Min(demand, need);
            net.LastDemand = demand;
            net.LastSupplied = toConsumers;
            net.Satisfaction = basis > 0.01f ? Math.Min(1f, toConsumers / basis) : 1f;
        }

        private static float PerTickOf(MachineInstance m) =>
            Math.Max(1f, m.PowerDraw >= 0 ? m.PowerDraw : m.Def.Energy.PerTick);

        private static float RateLimit(MachineInstance m, float amount)
        {
            int rate = m.Def.Energy.Rate;
            return rate > 0 ? Math.Min(amount, rate) : amount;
        }

        /// <summary>Списать amount пропорционально доступному у каждого.</summary>
        private static void Drain(List<MachineInstance> list, float amount, float available)
        {
            if (amount <= 0 || available <= 0) return;
            float k = Math.Min(1f, amount / available);
            foreach (var m in list)
                m.EnergyStored = Math.Max(0f, m.EnergyStored - RateLimit(m, m.EnergyStored) * k);
        }

        // ============================================================ Источники жидкости

        /// <summary>Есть ли рядом с насосом блок-источник жидкости его бака.</summary>
        public bool FindAdjacentFluidSource(MachineInstance m)
        {
            foreach (var t in m.Tanks)
            {
                int fluid = t.Filter >= 0 ? t.Filter : t.Fluid;
                var fdef = Content.Fluids.Get(fluid);
                if (fdef == null || fdef.SourceBlock == null) continue;
                int src = Content.Blocks.IndexOf(fdef.SourceBlock);
                foreach (var d in Directions.All)
                {
                    var p = m.Pos + d.Offset();
                    if (World != null && World.IsLoaded(p.X, p.Y, p.Z) && World.GetBlock(p) == src) return true;
                }
            }
            return false;
        }

        /// <summary>Есть ли рядом блок-источник указанной жидкости (водяное колесо).</summary>
        public bool FindAdjacentFluidSourceOf(MachineInstance m, string fluidId)
        {
            var fdef = Content.Fluids.Get(fluidId);
            if (fdef == null || fdef.SourceBlock == null || World == null) return false;
            int src = Content.Blocks.IndexOf(fdef.SourceBlock);
            foreach (var d in Directions.All)
            {
                var p = m.Pos + d.Offset();
                if (World.IsLoaded(p.X, p.Y, p.Z) && World.GetBlock(p) == src) return true;
            }
            return false;
        }

        /// <summary>Загружены ли все соседи — можно ли достоверно проверить источник.</summary>
        public bool CanVerifySource(MachineInstance m)
        {
            if (World == null) return true;
            foreach (var d in Directions.All)
            {
                var p = m.Pos + d.Offset();
                if (!World.IsLoaded(p.X, p.Y, p.Z)) return false;
            }
            return true;
        }

        // ============================================================ Сохранение

        // 4: Meta предметов на лентах, залежь буров, выбранный рецепт машины и заказ ME
        private const int SaveVersion = 4;

        public void Write(BinaryWriter w)
        {
            if (_networksDirty) RebuildNetworks();
            w.Write(SaveVersion);
            w.Write(TickCount);

            w.Write(_machines.Count);
            foreach (var m in _machines)
            {
                w.Write(m.Def.Id);
                WritePos(w, m.Pos);
                w.Write((byte)m.Facing);
                for (int i = 0; i < 6; i++) w.Write((byte)m.Sides[i]);
                m.Input.Write(w);
                m.Output.Write(w);
                m.Fuel.Write(w);
                w.Write(m.Tanks.Count);
                foreach (var t in m.Tanks)
                {
                    w.Write(t.Name ?? "");
                    w.Write(t.Fluid >= 0 ? Content.Fluids.IdOf(t.Fluid) : "");
                    w.Write(t.Amount);
                }
                w.Write(m.CurrentRecipe != null ? m.CurrentRecipe.Id : "");
                w.Write(m.Progress);
                w.Write(m.PendingOutputs.Count);
                foreach (var s in m.PendingOutputs)
                {
                    w.Write(Content.Items.IdOf(s.Item) ?? "");
                    w.Write(s.Count);
                }
                w.Write(m.BurnLeft);
                w.Write(m.BurnTotal);
                w.Write(m.Heat);
                w.Write(m.HasSource);
                w.Write(m.Rng.State);
                w.Write(m.EnergyStored);
                MeSystem.WriteDevice(w, m, Content);
                // v4
                w.Write(m.LockedRecipe != null ? m.LockedRecipe.Id : "");
                w.Write(m.QueuedRecipe != null && m.QueuedRuns > 0 ? m.QueuedRecipe.Id : "");
                w.Write(m.QueuedRuns);
                w.Write(m.DepositItem >= 0 ? Content.Items.IdOf(m.DepositItem) ?? "" : "");
                w.Write(m.DepositPurity);
            }

            w.Write(_conveyors.Count);
            foreach (var c in _conveyors)
            {
                w.Write(c.Block.Id);
                WritePos(w, c.Pos);
                w.Write((byte)c.Facing);
                w.Write(c.Items.Count);
                foreach (var it in c.Items)
                {
                    w.Write(Content.Items.IdOf(it.Item) ?? "");
                    w.Write(it.Pos);
                    w.Write(it.Meta);
                }
            }

            w.Write(_pipeAt.Count);
            foreach (var p in _pipeAt.Values)
            {
                w.Write(p.Block.Id);
                WritePos(w, p.Pos);
            }
            w.Write(_networks.Count);
            foreach (var n in _networks)
            {
                WritePos(w, n.Pipes.Count > 0 ? n.Pipes[0].Pos : Int3.Zero);
                w.Write(n.Fluid >= 0 ? Content.Fluids.IdOf(n.Fluid) : "");
                w.Write(n.Amount);
            }

            Stats.Write(w, Content);

            w.Write(_wireAt.Count);
            foreach (var wire in _wireAt.Values)
            {
                w.Write(wire.Block.Id);
                WritePos(w, wire.Pos);
            }
            Me.Write(w);
        }

        public void Read(BinaryReader r)
        {
            _machines.Clear();
            _machineAt.Clear();
            _conveyors.Clear();
            _conveyorAt.Clear();
            _pipeAt.Clear();
            _networks.Clear();
            _wireAt.Clear();
            _power.Clear();

            int ver = r.ReadInt32();
            if (ver > SaveVersion) throw new InvalidDataException($"Factory save version {ver} is newer than supported");
            TickCount = r.ReadInt64();

            int mc = r.ReadInt32();
            for (int i = 0; i < mc; i++)
            {
                string id = r.ReadString();
                var pos = ReadPos(r);
                var facing = (Direction)r.ReadByte();
                var sides = new SideType[6];
                for (int k = 0; k < 6; k++) sides[k] = (SideType)r.ReadByte();

                var def = Content.Machines.Get(id);
                var behavior = def != null ? MachineBehaviors.Get(def.Behavior) : null;
                // Если машина пропала из контента — читаем данные во временный объект и выбрасываем
                var m = def != null && behavior != null
                    ? new MachineInstance(Content, def, pos, facing, behavior)
                    : null;
                var dummyInv = new Inventory(Content, 0);

                (m?.Input ?? dummyInv).Read(r);
                (m?.Output ?? dummyInv).Read(r);
                (m?.Fuel ?? dummyInv).Read(r);

                int tc = r.ReadInt32();
                for (int k = 0; k < tc; k++)
                {
                    string tname = r.ReadString();
                    string fid = r.ReadString();
                    int amount = r.ReadInt32();
                    var tank = m?.Tank(tname);
                    if (tank != null)
                    {
                        tank.Fluid = fid.Length > 0 ? Content.Fluids.IndexOf(fid) : tank.Filter;
                        tank.Amount = tank.Fluid >= 0 ? Math.Min(amount, tank.Capacity) : 0;
                    }
                }
                string recipe = r.ReadString();
                float progress = r.ReadSingle();
                int pc = r.ReadInt32();
                var pending = new List<ItemStack>();
                for (int k = 0; k < pc; k++)
                {
                    string iid = r.ReadString();
                    int cnt = r.ReadInt32();
                    int idx = Content.Items.IndexOf(iid);
                    if (idx >= 0) pending.Add(new ItemStack(idx, cnt));
                }
                int burnLeft = r.ReadInt32();
                int burnTotal = r.ReadInt32();
                float heat = r.ReadSingle();
                bool hasSource = r.ReadBoolean();
                ulong rng = r.ReadUInt64();
                float energy = ver >= 2 ? r.ReadSingle() : 0f;
                if (ver >= 3) MeSystem.ReadDevice(r, m, Content);
                string locked = "", queued = "", deposit = "";
                int queuedRuns = 0, purity = 0;
                if (ver >= 4)
                {
                    locked = r.ReadString();
                    queued = r.ReadString();
                    queuedRuns = r.ReadInt32();
                    deposit = r.ReadString();
                    purity = r.ReadInt32();
                }

                if (m == null)
                {
                    Log.Warn($"Machine '{id}' at {pos} no longer exists, removed");
                    continue;
                }
                for (int k = 0; k < 6; k++) m.Sides[k] = sides[k];
                m.CurrentRecipe = recipe.Length > 0 ? Content.Recipes.Get(recipe) : null;
                m.Progress = m.CurrentRecipe != null ? progress : 0;
                m.PendingOutputs.AddRange(pending);
                m.BurnLeft = burnLeft;
                m.BurnTotal = burnTotal;
                m.Heat = heat;
                m.HasSource = hasSource;
                m.Rng.State = rng;
                m.EnergyStored = Math.Min(energy, m.EnergyCapacity);
                m.LockedRecipe = locked.Length > 0 ? Content.Recipes.Get(locked) : null;
                m.QueuedRecipe = queued.Length > 0 ? Content.Recipes.Get(queued) : null;
                m.QueuedRuns = m.QueuedRecipe != null ? queuedRuns : 0;
                // Залежь бура сохраняется: дальний бур работает сразу после загрузки, не дожидаясь своего чанка
                m.DepositItem = deposit.Length > 0 ? Content.Items.IndexOf(deposit) : -1;
                m.DepositPurity = Math.Max(1, purity);
                AddMachine(m);
            }

            int cc = r.ReadInt32();
            for (int i = 0; i < cc; i++)
            {
                string bid = r.ReadString();
                var pos = ReadPos(r);
                var facing = (Direction)r.ReadByte();
                int ic = r.ReadInt32();
                var block = Content.Blocks.Get(bid);
                Conveyor c = null;
                if (block != null && block.Transport != null)
                {
                    c = new Conveyor(pos, facing, block);
                    _conveyors.Add(c);
                    _conveyorAt[pos] = c;
                }
                for (int k = 0; k < ic; k++)
                {
                    string iid = r.ReadString();
                    float ipos = r.ReadSingle();
                    int imeta = ver >= 4 ? r.ReadInt32() : 0;
                    int idx = Content.Items.IndexOf(iid);
                    if (c != null && idx >= 0) c.Items.Add(new BeltItem { Item = idx, Meta = imeta, Pos = ipos, PrevPos = ipos });
                }
            }
            _conveyorOrderDirty = true;

            int pcnt = r.ReadInt32();
            for (int i = 0; i < pcnt; i++)
            {
                string bid = r.ReadString();
                var pos = ReadPos(r);
                var block = Content.Blocks.Get(bid);
                if (block != null && block.Transport != null) _pipeAt[pos] = new Pipe(pos, block);
            }
            int nc = r.ReadInt32();
            for (int i = 0; i < nc; i++)
            {
                var rep = ReadPos(r);
                string fid = r.ReadString();
                int amount = r.ReadInt32();
                if (_pipeAt.TryGetValue(rep, out var p) && fid.Length > 0)
                {
                    p.ShareFluid = Content.Fluids.IndexOf(fid);
                    p.ShareAmount = amount;
                }
            }
            // Пересобрать сети сразу: у труб ещё нет сетей, поэтому доли из сохранения
            // (ShareFluid/ShareAmount) сохранятся и сложатся в новые сети.
            RebuildNetworks();

            Stats.Read(r, Content);

            _wireAt.Clear();
            if (ver >= 2)
            {
                int wc = r.ReadInt32();
                for (int i = 0; i < wc; i++)
                {
                    string bid = r.ReadString();
                    var pos = ReadPos(r);
                    var block = Content.Blocks.Get(bid);
                    if (block != null && block.Transport != null) _wireAt[pos] = new Wire(pos, block);
                }
            }
            _powerDirty = true;

            if (ver >= 3) Me.Read(r);
            Me.MarkDirty();
        }

        private static void WritePos(BinaryWriter w, Int3 p)
        {
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
        }

        private static Int3 ReadPos(BinaryReader r) => new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }
}
