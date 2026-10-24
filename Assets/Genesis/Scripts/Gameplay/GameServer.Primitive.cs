using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Первобытная эпоха: две руки, крафт в руках, режим создания (обработка блоков ударами), конструкции из блоков,
    /// предметы по блокам, многоблочные машины, валка деревьев, угасающие предметы, лежанка, справочник,
    /// предметы на полу.
    /// </summary>
    public sealed partial class GameServer
    {
        private Random64 _primRng = new Random64(0x9F1A7E);

        /// <summary>Игрок сделал что-то в руках (для звуков/модов).</summary>
        public event Action<PlayerState, HandCraftDef> HandCrafted;
        /// <summary>Удар в режиме создания: игрок, позиция, операция, ударов сделано.</summary>
        public event Action<PlayerState, Int3, ShapingDef, int> ShapeHit;
        /// <summary>Конструкция собрана.</summary>
        public event Action<PlayerState, StructureMatch> StructureBuilt;

        // ============================================================ Руки

        /// <summary>F — поменять местами предметы в правой и левой руке.</summary>
        public void SwapHands(PlayerState p)
        {
            var main = p.Inventory[p.SelectedSlot];
            p.Inventory[p.SelectedSlot] = p.Offhand[0];
            p.Offhand[0] = main;
        }

        /// <summary>Крафт в двух руках (клиент держал ПКМ нужное время). Производится по одному.</summary>
        public bool HandCraft(PlayerState p, int craftId)
        {
            var def = Content.HandCrafts.Get(craftId);
            if (def == null || p.Health <= 0) return false;
            if (TickCount < p.NextHandCraftTick) return false;
            var main = p.Inventory[p.SelectedSlot];
            var off = p.Offhand[0];
            var found = HandCrafting.Find(Content, main, off, out bool mainIsA);
            if (found != def) return false;

            bool keepMain = mainIsA ? def.KeepA : def.KeepB;
            bool keepOff = mainIsA ? def.KeepB : def.KeepA;
            int useMain = mainIsA ? def.ACount : def.BCount;
            int useOff = mainIsA ? def.BCount : def.ACount;

            if (def.Repair)
            {
                if (keepMain) p.Inventory[p.SelectedSlot] = new ItemStack(main.Item, main.Count, 0);
                if (keepOff) p.Offhand[0] = new ItemStack(off.Item, off.Count, 0);
            }
            if (!keepMain) p.Inventory[p.SelectedSlot] = main.WithCount(main.Count - useMain);
            if (!keepOff) p.Offhand[0] = off.WithCount(off.Count - useOff);

            if (def.Output != null && def.Output.ItemId >= 0)
            {
                var result = new ItemStack(def.Output.ItemId, def.Output.Count);
                if (def.ToOffhand && p.Offhand[0].IsEmpty) p.Offhand[0] = result;
                else GiveOrDrop(p, result);
            }
            p.NextHandCraftTick = TickCount + Math.Max(1, def.SecondsTicks * 3 / 4);
            Knowledge.Notify("handcraft", def.Id);
            HandCrafted?.Invoke(p, def);
            return true;
        }

        /// <summary>Положить в инвентарь (сначала хотбар), не влезло — под ноги.</summary>
        private void GiveOrDrop(PlayerState p, ItemStack st)
        {
            if (st.IsEmpty) return;
            int put = p.Inventory.Insert(st);
            if (put < st.Count) DropFromPlayer(p, st.WithCount(st.Count - put), false);
        }

        // ============================================================ Режим создания

        /// <summary>
        /// Удар в режиме создания по блоку pos. Последний удар завершает операцию: блок раскалывается на результат
        /// (или превращается в result_block), на станции тратятся предметы с её поверхности.
        /// </summary>
        public bool ShapeHitAt(PlayerState p, Int3 pos, int shapingId)
        {
            var def = Content.Shapings.Get(shapingId);
            if (def == null || p.Health <= 0 || !InReach(p, pos) || !World.IsLoaded(pos.X, pos.Y, pos.Z)) return false;
            var held = p.Inventory[p.SelectedSlot];
            if (!Shaping.ToolOk(Content, def, held))
            {
                Notify?.Invoke(p.Id, "ui.shape.need_tool");
                return false;
            }
            var station = MultiBlock.OwnerOf(Factory, pos);
            var anchor = station != null ? station.Pos : pos;
            ushort block = World.GetBlock(anchor);
            if (Array.IndexOf(def.TargetBlocks, block) < 0) return false;
            if (!Shaping.InputsPresent(def, station)) return false;

            var s = p.Shaping;
            if (s == null || s.Pos != anchor || s.ShapingId != def.NumericId || TickCount - s.LastHitTick > 20 * 4)
            {
                s = p.Shaping = new ShapeSession { Pos = anchor, ShapingId = def.NumericId };
            }
            else if (TickCount - s.LastHitTick < def.IntervalTicks)
            {
                return false; // слишком часто
            }
            s.Hits++;
            s.LastHitTick = TickCount;
            if (def.Wear > 0 && Mining.ToolOf(Content, held) != null) WearTool(p, p.SelectedSlot, def.Wear);
            ShapeHit?.Invoke(p, anchor, def, s.Hits);
            if (s.Hits < def.Hits) return true;

            p.Shaping = null;
            CompleteShaping(p, def, anchor, station);
            return true;
        }

        private void CompleteShaping(PlayerState p, ShapingDef def, Int3 pos, MachineInstance station)
        {
            var center = new Vec3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
            if (def.IsStation)
            {
                foreach (var i in def.Inputs) station.Input.Extract(i.ItemId, i.Count);
                center = new Vec3(pos.X + 0.5f, pos.Y + 1.15f, pos.Z + 0.5f);
            }
            else if (def.ConsumeBlock)
            {
                var drops = Factory.OnBlockRemoved(pos);
                World.SetBlock(pos, Content.AirNumeric);
                foreach (var d in drops) Drops.Spawn(d, center, Vec3.Zero, 6, true);
                if (def.ResultBlock != null)
                {
                    var rb = Content.Blocks.Get(def.ResultBlock);
                    var look = Directions.FromLook(pos.X + 0.5f - p.Position.X, pos.Z + 0.5f - p.Position.Z);
                    PlaceBlockAt(pos, rb, look.Opposite(), p);
                    Knowledge.Notify("place", rb.Id);
                }
            }
            foreach (var o in def.Outputs)
                if (o.ItemId >= 0) Drops.Spawn(new ItemStack(o.ItemId, o.Count), center, Vec3.Zero, 4, true);
            Knowledge.Notify("shape", def.Id);
        }

        // ============================================================ Конструкции

        /// <summary>Собрать конструкцию, в которую входит блок pos (удар инструментом в режиме создания).</summary>
        public bool BuildStructure(PlayerState p, Int3 pos, int structureId)
        {
            if (p.Health <= 0 || !InReach(p, pos)) return false;
            var m = Structures.Find(Content, World, pos, p.Position, structureId);
            if (m == null) return false;
            var tool = Mining.ToolOf(Content, p.SelectedStack);
            if (tool == null)
            {
                Notify?.Invoke(p.Id, "ui.shape.need_tool");
                return false;
            }
            if (m.Def.Tools.Count > 0 && !m.Def.Tools.Contains(tool.Type))
            {
                Notify?.Invoke(p.Id, "ui.shape.need_tool");
                return false;
            }

            // Результат должен поместиться: в клетки шаблона или в пустые клетки рядом
            var resultCells = Structures.ResultCells(Content, m);
            var cellSet = new HashSet<Int3>(m.Cells);
            foreach (var c in resultCells)
            {
                if (cellSet.Contains(c)) continue;
                if (!World.IsLoaded(c.X, c.Y, c.Z) || !World.GetBlockDef(c).Replaceable || Factory.MachineAt(c) != null)
                {
                    Notify?.Invoke(p.Id, "ui.structure.no_room");
                    return false;
                }
            }

            // Разобрать шаблон (содержимое машин внутри — выпадает)
            var center = new Vec3(m.ResultPos.X + 0.5f, m.ResultPos.Y + 0.6f, m.ResultPos.Z + 0.5f);
            foreach (var c in m.Cells)
            {
                foreach (var d in Factory.OnBlockRemoved(c)) Drops.Spawn(d, center, Vec3.Zero, 6, true);
                World.SetBlock(c, Content.AirNumeric);
            }
            var block = Content.Block(m.Def.ResultBlock);
            PlaceBlockAt(m.ResultPos, block, m.Facing, p);
            WearTool(p, p.SelectedSlot, 1);
            Knowledge.Notify("build", m.Def.Id);
            Knowledge.Notify("place", block.Id);
            StructureBuilt?.Invoke(p, m);
            BlockPlaced?.Invoke(p, m.ResultPos, block);
            return true;
        }

        // ============================================================ Многоблочные машины

        /// <summary>
        /// Поставить блок (машину — со всеми клетками). Клетки должны быть свободны; проверку места делает вызывающий.
        /// </summary>
        private void PlaceBlockAt(Int3 pos, BlockDef block, Direction facing, PlayerState by)
        {
            var mdef = block.MachineId != null ? Content.Machines.Get(block.MachineId) : null;
            World.SetBlock(pos, block.NumericId);
            if (mdef != null && mdef.IsMultiBlock)
            {
                var part = Content.Blocks.Get(mdef.PartBlock);
                var cells = MultiBlock.Cells(mdef, pos, facing);
                for (int i = 1; i < cells.Count; i++) World.SetBlock(cells[i], part.NumericId);
            }
            Factory.OnBlockPlaced(pos, block, facing);
        }

        /// <summary>Можно ли поставить многоблочную машину: все клетки заменяемы и свободны от игроков.</summary>
        private bool CanPlaceCells(PlayerState p, List<Int3> cells, bool solid)
        {
            foreach (var c in cells)
            {
                if (c.Y < WorldConstants.MinY || c.Y > WorldConstants.MaxY) return false;
                if (!World.IsLoaded(c.X, c.Y, c.Z) || !World.GetBlockDef(c).Replaceable) return false;
                if (Factory.MachineAt(c) != null || Factory.ConveyorAt(c) != null) return false;
                if (!solid) continue;
                foreach (var other in _players.Values)
                    if (PlayerPhysics.IntersectsBlock(other, c)) return false;
            }
            return true;
        }

        /// <summary>Сломать многоблочную машину целиком: выпадает сама машина и её содержимое.</summary>
        private void BreakMachine(PlayerState p, MachineInstance m, ToolSpec tool)
        {
            var mainDef = World.GetBlockDef(m.Pos);
            var drops = new List<ItemStack>();
            if (mainDef.Drop != null && Mining.CanHarvest(mainDef, tool))
            {
                int item = Content.Items.IndexOf(mainDef.Drop);
                if (item >= 0) drops.Add(new ItemStack(item, 1));
            }
            var cells = MultiBlock.Cells(m.Def, m.Pos, m.Facing);
            drops.AddRange(Factory.OnBlockRemoved(m.Pos));
            foreach (var c in cells)
            {
                var d = World.GetBlockDef(c);
                if (c == m.Pos || d.PartOf == m.Def.Id) World.SetBlock(c, Content.AirNumeric);
            }
            var center = new Vec3(m.Pos.X + 0.5f, m.Pos.Y + 0.4f, m.Pos.Z + 0.5f);
            foreach (var d in drops) Drops.Spawn(d, center, Vec3.Zero, 6, true);
            BlockBroken?.Invoke(p, m.Pos, mainDef);
            if (tool != null) WearTool(p, p.SelectedSlot, 1);
        }

        /// <summary>
        /// Заменить машину другой (печь → обмазанная печь) с сохранением содержимого и ориентации.
        /// Клетки новой машины должны совпадать с клетками старой или быть свободными.
        /// </summary>
        private bool ReplaceMachine(MachineInstance old, BlockDef newBlock, PlayerState p)
        {
            var newDef = newBlock.MachineId != null ? Content.Machines.Get(newBlock.MachineId) : null;
            var oldCells = MultiBlock.Cells(old.Def, old.Pos, old.Facing);
            var newCells = newDef != null ? MultiBlock.Cells(newDef, old.Pos, old.Facing) : new List<Int3> { old.Pos };
            var oldSet = new HashSet<Int3>(oldCells);
            foreach (var c in newCells)
                if (!oldSet.Contains(c) && (!World.IsLoaded(c.X, c.Y, c.Z) || !World.GetBlockDef(c).Replaceable)) return false;

            var facing = old.Facing;
            var contents = Factory.OnBlockRemoved(old.Pos);
            foreach (var c in oldCells) World.SetBlock(c, Content.AirNumeric);
            PlaceBlockAt(old.Pos, newBlock, facing, p);
            var created = Factory.MachineAt(old.Pos);
            var center = new Vec3(old.Pos.X + 0.5f, old.Pos.Y + 1.1f, old.Pos.Z + 0.5f);
            foreach (var st in contents)
            {
                int put = created != null ? Factory.InsertIntoMachine(created, st) : 0;
                if (put < st.Count) Drops.Spawn(st.WithCount(st.Count - put), center, Vec3.Zero, 6, true);
            }
            return true;
        }

        // ============================================================ Деревья

        public const int MaxTreeLogs = 160;

        /// <summary>
        /// Рубка топором или рубилом: дерево падает целиком (связанные брёвна выше удара) и раскалывается на брёвна,
        /// листва вокруг осыпается (палки — с шансом).
        /// </summary>
        private void FellTree(PlayerState p, Int3 start, ToolSpec tool)
        {
            var logs = new List<Int3>();
            var seen = new HashSet<Int3> { start };
            var queue = new Queue<Int3>();
            queue.Enqueue(start);
            while (queue.Count > 0 && logs.Count < MaxTreeLogs)
            {
                var c = queue.Dequeue();
                logs.Add(c);
                for (int dy = 0; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    var n = new Int3(c.X + dx, c.Y + dy, c.Z + dz);
                    if (n.Y < start.Y || seen.Contains(n) || !World.IsLoaded(n.X, n.Y, n.Z)) continue;
                    if (!World.GetBlockDef(n).Fellable) continue;
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }

            // Листва в пределах 4 блоков от брёвен
            var leaves = new List<Int3>();
            var dist = new Dictionary<Int3, int>();
            var lq = new Queue<Int3>();
            foreach (var l in logs)
            {
                dist[l] = 0;
                lq.Enqueue(l);
            }
            while (lq.Count > 0 && leaves.Count < 600)
            {
                var c = lq.Dequeue();
                int d = dist[c];
                if (d >= 4) continue;
                foreach (var dir in Directions.All)
                {
                    var n = c + dir.Offset();
                    if (dist.ContainsKey(n) || n.Y < start.Y - 1 || !World.IsLoaded(n.X, n.Y, n.Z)) continue;
                    if (!World.GetBlockDef(n).Foliage) continue;
                    dist[n] = d + 1;
                    leaves.Add(n);
                    lq.Enqueue(n);
                }
            }

            var totals = new Dictionary<int, int>();
            void Add(ItemStack st)
            {
                if (st.IsEmpty) return;
                totals[st.Item] = (totals.TryGetValue(st.Item, out var n) ? n : 0) + st.Count;
            }
            foreach (var l in logs)
            {
                foreach (var d in RollDrops(World.GetBlockDef(l), tool)) Add(d);
                World.SetBlock(l, Content.AirNumeric);
            }
            foreach (var l in leaves)
            {
                foreach (var d in RollDrops(World.GetBlockDef(l), tool)) Add(d);
                World.SetBlock(l, Content.AirNumeric);
            }
            var at = new Vec3(start.X + 0.5f, start.Y + 0.6f, start.Z + 0.5f);
            foreach (var kv in totals)
            {
                int left = kv.Value;
                int max = Content.Item(kv.Key)?.MaxStack ?? GameConstants.MaxStackSize;
                while (left > 0)
                {
                    int n = Math.Min(left, max);
                    Drops.Spawn(new ItemStack(kv.Key, n), at, Vec3.Zero, 6, true);
                    left -= n;
                }
            }
            BlockBroken?.Invoke(p, start, World.GetBlockDef(start));
            WearTool(p, p.SelectedSlot, 1 + logs.Count / 4);
        }

        /// <summary>Что выпадает из блока (drops с шансами или drop/drop_count). Неподходящий инструмент — ничего.</summary>
        private List<ItemStack> RollDrops(BlockDef def, ToolSpec tool)
        {
            var list = new List<ItemStack>();
            if (!Mining.CanHarvest(def, tool)) return list;
            if (def.Drops != null)
            {
                foreach (var d in def.Drops)
                {
                    if (d.ItemId < 0) continue;
                    if (d.Chance >= 1f || _primRng.NextDouble() < d.Chance) list.Add(new ItemStack(d.ItemId, d.Count));
                }
                return list;
            }
            if (def.Drop != null)
            {
                int item = Content.Items.IndexOf(def.Drop);
                if (item >= 0) list.Add(new ItemStack(item, Math.Max(1, def.DropCount)));
            }
            return list;
        }

        // ============================================================ Предмет по блоку

        /// <summary>ПКМ предметом из правой руки по блоку (уголёк по хворосту, глина по печке, мотыга по земле).</summary>
        public bool UseOnBlock(PlayerState p, Int3 pos)
        {
            if (p.Health <= 0 || !InReach(p, pos) || !World.IsLoaded(pos.X, pos.Y, pos.Z)) return false;
            var owner = MultiBlock.OwnerOf(Factory, pos);
            var anchor = owner != null ? owner.Pos : pos;
            ushort block = World.GetBlock(anchor);
            var held = p.Inventory[p.SelectedSlot];
            var def = Interactions.Find(Content, held, block);
            if (def == null) return false;

            if (p.UseId != def.NumericId || p.UsePos != anchor) p.UseCount = 0;
            p.UseId = def.NumericId;
            p.UsePos = anchor;

            if (def.Wear > 0 && Mining.ToolOf(Content, held) != null) WearTool(p, p.SelectedSlot, def.Wear);
            else if (def.Consume > 0) p.Inventory[p.SelectedSlot] = held.WithCount(held.Count - def.Consume);
            p.UseCount++;
            if (p.UseCount < def.Uses)
            {
                Notify?.Invoke(p.Id, "ui.use.progress|" + p.UseCount + "/" + def.Uses);
                return true;
            }
            p.UseCount = 0;
            p.UseId = -1;

            if (def.ResultBlock != null)
            {
                var rb = Content.Block(def.ResultBlockId);
                if (owner != null) ReplaceMachine(owner, rb, p);
                else
                {
                    var look = Directions.FromLook(pos.X + 0.5f - p.Position.X, pos.Z + 0.5f - p.Position.Z);
                    World.SetBlock(pos, Content.AirNumeric);
                    PlaceBlockAt(pos, rb, look.Opposite(), p);
                }
                Knowledge.Notify("place", rb.Id);
            }
            if (def.Give != null && def.Give.ItemId >= 0) GiveOrDrop(p, new ItemStack(def.Give.ItemId, def.Give.Count));
            Knowledge.Notify("use", def.Id);
            return true;
        }

        // ============================================================ Действия блоков: справочник, лежанка

        /// <summary>ПКМ по блоку с действием: взять справочник, лечь на лежанку.</summary>
        public bool UseBlockAction(PlayerState p, Int3 pos)
        {
            if (p.Health <= 0 || !InReach(p, pos) || !World.IsLoaded(pos.X, pos.Y, pos.Z)) return false;
            var def = World.GetBlockDef(pos);
            switch (def.Action)
            {
                case "handbook":
                    if (!p.HasHandbook)
                    {
                        p.HasHandbook = true;
                        Notify?.Invoke(p.Id, "ui.handbook.taken");
                    }
                    Knowledge.Notify("event", "handbook");
                    return true;
                case "sleep":
                    return Sleep(p, MultiBlock.OwnerOf(Factory, pos)?.Pos ?? pos);
                default:
                    return false;
            }
        }

        /// <summary>Ночью — сон (ночь пропускается, когда легли все игроки), днём — точка возрождения.</summary>
        private bool Sleep(PlayerState p, Int3 bed)
        {
            bool night = SunHeight(DayPhase) < 0.05f;
            p.SpawnPoint = bed;
            if (!night)
            {
                Notify?.Invoke(p.Id, "ui.bed.spawn_set");
                return true;
            }
            p.SleepTick = TickCount;
            Knowledge.Notify("event", "sleep");
            if (!TrySkipNight()) Notify?.Invoke(p.Id, "ui.bed.waiting");
            return true;
        }

        /// <summary>Все игроки легли (последние 3 секунды) — наступает утро.</summary>
        private bool TrySkipNight()
        {
            if (_players.Count == 0) return false;
            foreach (var other in _players.Values)
                if (other.SleepTick < 0 || TickCount - other.SleepTick > 60) return false;
            long day = WorldTime / DayLength;
            long morning = (day + 1) * DayLength + DayLength / 50; // чуть после рассвета
            if (DayPhase < 0.02f) morning = day * DayLength + DayLength / 50;
            WorldTime = morning;
            foreach (var other in _players.Values)
            {
                other.SleepTick = -1;
                Notify?.Invoke(other.Id, "ui.bed.morning");
            }
            return true;
        }

        // ============================================================ Рабочая поверхность (верстак, наковальня)

        /// <summary>ПКМ предметом по поверхности — положить один; пустой рукой — забрать последний.</summary>
        public bool UseSurface(PlayerState p, Int3 pos, bool put)
        {
            var m = MultiBlock.OwnerOf(Factory, pos);
            if (m == null || !m.Def.Surface || !InReach(p, pos) || p.Health <= 0) return false;
            if (put)
            {
                var held = p.Inventory[p.SelectedSlot];
                if (held.IsEmpty) return false;
                var one = held.WithCount(1);
                if (m.Input.Insert(one) <= 0)
                {
                    Notify?.Invoke(p.Id, "ui.surface.full");
                    return false;
                }
                p.Inventory[p.SelectedSlot] = held.WithCount(held.Count - 1);
                return true;
            }
            for (int i = m.Input.Size - 1; i >= 0; i--)
            {
                var st = m.Input[i];
                if (st.IsEmpty) continue;
                int moved = p.Inventory.Insert(st);
                if (moved <= 0) return false;
                m.Input[i] = st.WithCount(st.Count - moved);
                return true;
            }
            return false;
        }

        // ============================================================ Предметы на полу

        /// <summary>Положить предмет из правой руки на верх блока pos (один или весь стак).</summary>
        public bool PlaceOnFloor(PlayerState p, Int3 pos, bool all)
        {
            if (p.Health <= 0 || !InReach(p, pos) || !World.IsLoaded(pos.X, pos.Y + 1, pos.Z)) return false;
            if (!World.GetBlockDef(pos).Solid) return false;
            var above = pos + Int3.Up;
            if (World.GetBlockDef(above).Solid) return false;
            var held = p.Inventory[p.SelectedSlot];
            if (held.IsEmpty) return false;
            int n = all ? held.Count : 1;
            p.Inventory[p.SelectedSlot] = held.WithCount(held.Count - n);
            var d = Drops.Spawn(held.WithCount(n), new Vec3(pos.X + 0.5f, pos.Y + 1.15f, pos.Z + 0.5f), Vec3.Zero, 0);
            if (d != null) d.Placed = true;
            return true;
        }

        /// <summary>Подобрать предмет с пола (ПКМ по нему).</summary>
        public bool PickupDrop(PlayerState p, int dropId)
        {
            var d = Drops.Get(dropId);
            if (d == null || p.Health <= 0) return false;
            if ((d.Position - p.EyePosition).LengthSquared > (ReachDistance + 1f) * (ReachDistance + 1f)) return false;
            int put = p.Inventory.Insert(d.Stack);
            if (put <= 0) return false;
            d.Stack = d.Stack.WithCount(d.Stack.Count - put);
            if (d.Stack.IsEmpty) Drops.Remove(d.Id);
            return true;
        }

        // ============================================================ Угасание предметов

        private void TickDecay()
        {
            long t = WorldTime;
            void Scan(Inventory inv)
            {
                for (int i = 0; i < inv.Size; i++)
                {
                    var st = inv[i];
                    if (Decay.Process(Content, ref st, t)) inv[i] = st;
                }
            }
            foreach (var p in _players.Values)
            {
                Scan(p.Inventory);
                Scan(p.Offhand);
                Scan(p.Cursor);
                Scan(p.CraftGrid);
            }
            foreach (var m in Factory.Machines)
            {
                Scan(m.Input);
                Scan(m.Output);
                Scan(m.Fuel);
            }
            foreach (var d in Drops.All)
            {
                var st = d.Stack;
                if (Decay.Process(Content, ref st, t)) d.Stack = st;
            }
        }
    }
}
