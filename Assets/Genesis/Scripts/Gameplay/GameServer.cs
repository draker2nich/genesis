using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Игровой сервер. Одиночная игра = этот сервер внутри клиента (как integrated server в Minecraft),
    /// кооп = тот же сервер на хосте + сетевой транспорт команд. Вся логика — в фиксированных тиках 20 TPS.
    /// </summary>
    public sealed partial class GameServer : IDisposable
    {
        public const float ReachDistance = 6.5f;
        private const int SaveVersion = 2;
        /// <summary>Длина суток в тиках: 24000 = 20 минут.</summary>
        public const int DayLength = 24000;
        private const int PlayersVersion = 3;
        public const float AttackReach = 4.5f;

        public readonly ContentDatabase Content;
        public readonly VoxelWorld World;
        public readonly Factory Factory;
        public readonly string SaveDir;
        public readonly long Seed;
        public readonly KnowledgeState Knowledge;
        public readonly EnemyManager Enemies;
        /// <summary>Предметы, лежащие в мире.</summary>
        public readonly DroppedItems Drops;

        private readonly Dictionary<int, PlayerState> _players = new Dictionary<int, PlayerState>();
        private readonly Dictionary<string, PlayerState> _savedPlayers = new Dictionary<string, PlayerState>();
        private readonly ConcurrentQueue<GameCommand> _commands = new ConcurrentQueue<GameCommand>();
        private int _nextPlayerId = 1;
        private float _accumulator;
        private int _frame;

        public int ViewRadius = 8;
        public int VerticalRadius = 5;

        /// <summary>Доля между тиками (0..1) — для интерполяции рендера.</summary>
        public float TickAlpha => _accumulator / GameConstants.TickDuration;

        public IEnumerable<PlayerState> Players => _players.Values;

        /// <summary>Сообщения игроку (тосты): playerId, текст/ключ локализации.</summary>
        public event Action<int, string> Notify;

        // ---- Хуки для код-модов (ТЗ, раздел 15: «события, хуки симуляции»)
        /// <summary>Каждый тик симуляции (20 раз в секунду), после фабрики.</summary>
        public event Action<GameServer> Ticked;
        public event Action<PlayerState> PlayerJoined;
        public event Action<PlayerState, Int3, BlockDef> BlockPlaced;
        public event Action<PlayerState, Int3, BlockDef> BlockBroken;
        public event Action<PlayerState, CraftDef> Crafted;

        /// <summary>Список модов хоста «id@версия» — сверяется с подключающимися клиентами.</summary>
        public string ModList = "";

        private GameServer(ContentDatabase content, string saveDir, long seed, int workers)
        {
            Content = content;
            SaveDir = saveDir;
            Seed = seed;
            World = new VoxelWorld(content, seed, saveDir != null ? Path.Combine(saveDir, "regions") : null, workers);
            Factory = new Factory(content, World);
            Knowledge = new KnowledgeState(content);
            Factory.RecipeFilter = Knowledge.IsRecipeUnlocked;
            Enemies = new EnemyManager(this);
            Drops = new DroppedItems(World, content);
            Knowledge.NodeUnlocked += n => NotifyAll("ui.unlocked|" + n.NameKey);
            Knowledge.EraAdvanced += era => NotifyAll("ui.new_era|era." + Eras.All[era]);
        }

        public long TickCount => Factory.TickCount;

        /// <summary>Время мира в тиках (идёт с первого запуска мира, сохраняется).</summary>
        public long WorldTime = DayLength / 20; // старт — раннее утро

        /// <summary>Фаза суток 0..1: 0 — рассвет, 0.25 — полдень, 0.5 — закат, 0.75 — полночь.</summary>
        public float DayPhase => (float)(WorldTime % DayLength) / DayLength;

        /// <summary>Высота солнца: &gt;0 днём, &lt;0 ночью.</summary>
        public static float SunHeight(float phase) => (float)Math.Sin(phase * Math.PI * 2);

        private void NotifyAll(string key)
        {
            foreach (var p in _players.Values) Notify?.Invoke(p.Id, key);
        }

        /// <summary>Открыть существующее сохранение или создать новый мир.</summary>
        public static GameServer Open(ContentDatabase content, string saveDir, long newSeed, int workerThreads)
        {
            long seed = newSeed;
            long savedTime = -1;
            if (saveDir != null)
            {
                Directory.CreateDirectory(saveDir);
                string meta = Path.Combine(saveDir, "world.dat");
                if (File.Exists(meta))
                {
                    using (var r = new BinaryReader(File.OpenRead(meta)))
                    {
                        int ver = r.ReadInt32();
                        if (ver > SaveVersion) throw new InvalidDataException("Save is from a newer version");
                        seed = r.ReadInt64();
                        if (ver >= 2) savedTime = r.ReadInt64();
                    }
                }
            }
            var server = new GameServer(content, saveDir, seed, workerThreads);
            if (savedTime >= 0) server.WorldTime = savedTime;
            server.LoadState();
            return server;
        }

        // ============================================================ Игроки

        public PlayerState Join(string name)
        {
            PlayerState p;
            if (_savedPlayers.TryGetValue(name, out var saved))
            {
                p = saved;
                _savedPlayers.Remove(name);
            }
            else
            {
                p = new PlayerState(_nextPlayerId, name, Content);
                var sp = World.Generator.SpawnPoint;
                p.Position = new Vec3(sp.X + 0.5f, sp.Y + 0.01f, sp.Z + 0.5f);
            }
            var player = new PlayerState(_nextPlayerId++, p.Name, Content);
            CopyPlayer(p, player);
            CloseGrid(player); // предметы, оставшиеся в сетке и на курсоре, — обратно в инвентарь
            _players[player.Id] = player;
            PlayerJoined?.Invoke(player);

            // Синхронно грузим чанки вокруг игрока, чтобы он не провалился
            var c = WorldConstants.ChunkOf(player.Position.Floor());
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                World.LoadNow(new Int3(c.X + dx, c.Y + dy, c.Z + dz));
            return player;
        }

        private static void CopyPlayer(PlayerState from, PlayerState to)
        {
            to.Position = from.Position;
            to.Yaw = from.Yaw;
            to.Pitch = from.Pitch;
            to.Flying = from.Flying;
            to.SelectedSlot = from.SelectedSlot;
            to.Health = from.Health;
            to.Hunger = from.Hunger;
            for (int i = 0; i < from.Inventory.Size && i < to.Inventory.Size; i++) to.Inventory[i] = from.Inventory[i];
            for (int i = 0; i < 9; i++) to.CraftGrid[i] = from.CraftGrid[i];
            to.Cursor[0] = from.Cursor[0];
            foreach (var kv in from.Mastery) to.Mastery[kv.Key] = kv.Value;
        }

        public void Leave(int playerId)
        {
            if (_players.TryGetValue(playerId, out var p))
            {
                _savedPlayers[p.Name] = p;
                _players.Remove(playerId);
            }
        }

        public PlayerState GetPlayer(int id) => _players.TryGetValue(id, out var p) ? p : null;


        // ============================================================ Цикл

        public void Enqueue(GameCommand cmd) => _commands.Enqueue(cmd);

        /// <summary>Вызывать каждый кадр с реальным dt.</summary>
        public void Update(float dt)
        {
            World.Pump();

            while (_commands.TryDequeue(out var cmd))
            {
                if (!_players.TryGetValue(cmd.PlayerId, out var p)) continue;
                try
                {
                    cmd.Apply(this, p);
                }
                catch (Exception e)
                {
                    Log.Error($"Command {cmd.GetType().Name} failed: {e}");
                }
            }

            _accumulator += Math.Min(dt, 0.25f); // защита от «спирали смерти»
            while (_accumulator >= GameConstants.TickDuration)
            {
                Tick();
                _accumulator -= GameConstants.TickDuration;
            }

            if (_frame++ % 15 == 0)
            {
                var centers = new List<Int3>();
                foreach (var p in _players.Values) centers.Add(p.Position.Floor());
                if (centers.Count > 0) World.UpdateLoadedArea(centers, ViewRadius, VerticalRadius);
            }
        }

        // ============================================================ Действия

        private bool InReach(PlayerState p, Int3 pos)
        {
            var eye = p.EyePosition;
            var center = new Vec3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
            return (center - eye).LengthSquared <= (ReachDistance + 1f) * (ReachDistance + 1f);
        }

        public bool TryBreakBlock(PlayerState p, Int3 pos)
        {
            if (!InReach(p, pos) || !World.IsLoaded(pos.X, pos.Y, pos.Z)) return false;
            var def = World.GetBlockDef(pos);
            if (def.Render == RenderLayer.Invisible || def.Unbreakable || def.Liquid) return false;

            var tool = Mining.ToolOf(Content, p.SelectedStack);
            var drops = new List<ItemStack>(); // выпадают в мир (подбираются, подлетая к игроку)
            if (def.Drop != null && Mining.CanHarvest(def, tool))
            {
                int item = Content.Items.IndexOf(def.Drop);
                if (item >= 0) drops.Add(new ItemStack(item, Math.Max(1, def.DropCount)));
            }
            else if (def.Drop != null)
            {
                Notify?.Invoke(p.Id, "ui.wrong_tool");
            }
            drops.AddRange(PeekFactoryDrops(pos));

            Factory.OnBlockRemoved(pos);
            World.SetBlock(pos, Content.AirNumeric);
            var center = new Vec3(pos.X + 0.5f, pos.Y + 0.4f, pos.Z + 0.5f);
            foreach (var d in drops) Drops.Spawn(d, center, Vec3.Zero, 6, true);
            BlockBroken?.Invoke(p, pos, def);
            if (tool != null) WearTool(p, p.SelectedSlot, 1);
            return true;
        }

        private List<ItemStack> PeekFactoryDrops(Int3 pos)
        {
            var list = new List<ItemStack>();
            var m = Factory.MachineAt(pos);
            if (m != null)
            {
                list.AddRange(m.Input.NonEmpty());
                list.AddRange(m.Output.NonEmpty());
                list.AddRange(m.Fuel.NonEmpty());
            }
            var c = Factory.ConveyorAt(pos);
            if (c != null)
                foreach (var it in c.Items) list.Add(new ItemStack(it.Item, 1));
            return list;
        }

        public bool TryPlaceSelected(PlayerState p, Int3 pos, Direction look)
        {
            var stack = p.SelectedStack;
            if (stack.IsEmpty) return false;
            var item = Content.Item(stack.Item);
            if (item == null || item.PlacesBlock == null) return false;
            if (!InReach(p, pos) || !World.IsLoaded(pos.X, pos.Y, pos.Z)) return false;
            if (pos.Y < WorldConstants.MinY || pos.Y > WorldConstants.MaxY) return false;

            var current = World.GetBlockDef(pos);
            if (!current.Replaceable) return false;

            var block = Content.Blocks.Get(item.PlacesBlock);
            if (block.Solid && PlayerPhysics.IntersectsBlock(p, pos)) return false;
            foreach (var other in _players.Values)
                if (other != p && block.Solid && PlayerPhysics.IntersectsBlock(other, pos)) return false;

            var horizontal = look.IsHorizontal() ? look : Direction.North;
            // Машина «смотрит» на игрока; лента едет туда, куда смотрит игрок.
            var mdef = block.MachineId != null ? Content.Machines.Get(block.MachineId) : null;
            var facing = mdef != null && mdef.Orient != "away" ? horizontal.Opposite() : horizontal;

            if (!World.SetBlock(pos, block.NumericId)) return false;
            Factory.OnBlockPlaced(pos, block, facing);
            p.Inventory.ExtractFromSlot(p.SelectedSlot, 1);
            Knowledge.Notify("place", block.Id);
            BlockPlaced?.Invoke(p, pos, block);
            return true;
        }

        public void TryPutOnConveyor(PlayerState p, Int3 pos)
        {
            var c = Factory.ConveyorAt(pos);
            var stack = p.SelectedStack;
            if (c == null || stack.IsEmpty || !InReach(p, pos)) return;
            if (!c.CanAccept()) return;
            c.Accept(stack.Item, 0f, 0f);
            p.Inventory.ExtractFromSlot(p.SelectedSlot, 1);
        }

        public void TakeFromMachine(PlayerState p, Int3 pos, MachineInventoryKind kind, int slot)
        {
            var m = Factory.MachineAt(pos);
            if (m == null || !InReach(p, pos)) return;
            var inv = kind == MachineInventoryKind.Input ? m.Input : (kind == MachineInventoryKind.Output ? m.Output : m.Fuel);
            if (slot < 0 || slot >= inv.Size) return;
            var s = inv[slot];
            if (s.IsEmpty) return;
            int moved = p.Inventory.Insert(s);
            if (moved > 0) inv.ExtractFromSlot(slot, moved);
        }

        public void PutIntoMachine(PlayerState p, Int3 pos, int playerSlot)
        {
            var m = Factory.MachineAt(pos);
            if (m == null || !InReach(p, pos)) return;
            if (playerSlot < 0 || playerSlot >= p.Inventory.Size) return;
            var s = p.Inventory[playerSlot];
            if (s.IsEmpty) return;
            int moved = Factory.InsertIntoMachine(m, s);
            if (moved > 0) p.Inventory.ExtractFromSlot(playerSlot, moved);
        }

        // ============================================================ Тик выживания и прогресса

        private void Tick()
        {
            WorldTime++;
            Factory.Daylight = Math.Max(0f, Math.Min(1f, SunHeight(DayPhase) * 1.6f));
            Factory.Tick();
            Enemies.Tick();
            Drops.Tick(_players.Values, Factory.TickCount);
            if (Ticked != null)
            {
                try { Ticked(this); }
                catch (Exception e) { Log.Error("Mod tick hook failed: " + e); }
            }
            long t = Factory.TickCount;

            foreach (var p in _players.Values)
            {
                if (p.PoisonTicks > 0)
                {
                    p.PoisonTicks--;
                    if (p.PoisonTicks % 30 == 29 && p.Health > 1) DamagePlayer(p, 1, "poison"); // яд не добивает
                }
                if (++p.HungerTimer >= 900) // −1 сытости каждые 45 с
                {
                    p.HungerTimer = 0;
                    p.Hunger = Math.Max(0, p.Hunger - 1);
                }
                if (p.Hunger >= 16 && p.Health < PlayerState.MaxHealth)
                {
                    if (++p.RegenTimer >= 80)
                    {
                        p.RegenTimer = 0;
                        p.Health = Math.Min(PlayerState.MaxHealth, p.Health + 1);
                        p.Hunger = Math.Max(0, p.Hunger - 0.3f);
                    }
                }
                else p.RegenTimer = 0;

                if (p.Hunger <= 0 && p.Health > 1)
                {
                    if (++p.StarveTimer >= 80)
                    {
                        p.StarveTimer = 0;
                        DamagePlayer(p, 1, "starve");
                    }
                }
            }

            // «Получить предмет» — проверяем инвентари раз в секунду, только если есть такие узлы
            if (t % 20 == 3 && Knowledge.WantsTrigger("obtain"))
            {
                var hits = new List<string>();
                foreach (var node in Knowledge.AvailableWithTrigger("obtain"))
                {
                    int item = Content.Items.IndexOf(node.Trigger.Target);
                    foreach (var p in _players.Values)
                        if (p.Inventory.CountOf(item) > 0) { hits.Add(node.Trigger.Target); break; }
                }
                foreach (var h in hits) Knowledge.Notify("obtain", h);
            }
        }

        // ============================================================ Здоровье и смерть

        public void DamagePlayer(PlayerState p, float amount, string cause)
        {
            if (p.Health <= 0 || amount <= 0) return;
            p.Health -= amount;
            p.LastDamageTick = TickCount;
            if (p.Health <= 0) Kill(p);
        }

        /// <summary>Смерть: вещи остаются в «останках» на месте гибели, игрок возрождается в бункере.</summary>
        public void Kill(PlayerState p)
        {
            var graveBlock = Content.Blocks.Get("genesis:grave");
            if (graveBlock != null && !p.Inventory.IsEmpty)
            {
                var feet = p.Position.Floor();
                for (int dy = 0; dy < 6; dy++)
                {
                    var pos = feet + new Int3(0, dy, 0);
                    if (!World.IsLoaded(pos.X, pos.Y, pos.Z) || !World.GetBlockDef(pos).Replaceable) continue;
                    if (Factory.MachineAt(pos) != null || Factory.ConveyorAt(pos) != null) continue;
                    if (!World.SetBlock(pos, graveBlock.NumericId)) continue;
                    Factory.OnBlockPlaced(pos, graveBlock, Direction.North);
                    var grave = Factory.MachineAt(pos);
                    if (grave != null)
                        foreach (var st in p.Inventory.NonEmpty()) grave.Input.Insert(st);
                    break;
                }
            }
            p.Inventory.Clear();

            var sp = World.Generator.SpawnPoint;
            p.Position = new Vec3(sp.X + 0.5f, sp.Y + 0.01f, sp.Z + 0.5f);
            p.Velocity = Vec3.Zero;
            p.Health = PlayerState.MaxHealth;
            p.Hunger = PlayerState.MaxHunger;
            p.TeleportVersion++;
            var c = WorldConstants.ChunkOf(sp);
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                World.LoadNow(new Int3(c.X + dx, c.Y + dy, c.Z + dz));
            Notify?.Invoke(p.Id, "ui.died");
        }

        public void Eat(PlayerState p)
        {
            var stack = p.SelectedStack;
            if (stack.IsEmpty) return;
            var def = Content.Item(stack.Item);
            if (def == null || !def.IsFood || p.Hunger >= PlayerState.MaxHunger) return;
            p.Hunger = Math.Min(PlayerState.MaxHunger, p.Hunger + def.Food);
            p.Inventory.ExtractFromSlot(p.SelectedSlot, 1);
        }

        /// <summary>Износ инструмента в слоте; сломанный исчезает.</summary>
        private void WearTool(PlayerState p, int slot, int amount)
        {
            var st = p.Inventory[slot];
            var tool = Mining.ToolOf(Content, st);
            if (tool == null) return;
            int meta = st.Meta + amount;
            if (meta >= tool.Durability)
            {
                p.Inventory[slot] = ItemStack.Empty;
                Notify?.Invoke(p.Id, "ui.tool_broke");
            }
            else p.Inventory[slot] = new ItemStack(st.Item, st.Count, meta);
        }

        /// <summary>ПКМ по существу с едой в руке: накормить (приручение и разведение).</summary>
        public void Interact(PlayerState p, int creatureId)
        {
            var e = Enemies.Get(creatureId);
            if (e == null || (e.Position - p.Position).LengthSquared > AttackReach * AttackReach + 4) return;
            var held = p.SelectedStack;
            if (held.IsEmpty || held.Item != e.Def.FoodItem) return;
            if (Enemies.Feed(e)) p.Inventory[p.SelectedSlot] = held.WithCount(held.Count - 1);
        }

        public void Attack(PlayerState p, int enemyId)
        {
            var e = Enemies.Get(enemyId);
            if (e == null || p.Health <= 0) return;
            if ((e.Position - p.Position).LengthSquared > AttackReach * AttackReach + 4) return;
            var tool = Mining.ToolOf(Content, p.SelectedStack);
            if (Enemies.Damage(e, Mining.AttackDamage(tool), p.Position, tool?.Type))
                foreach (var d in Enemies.RollDrops(e))
                    Drops.Spawn(d, e.Position + new Vec3(0, 0.5f, 0), Vec3.Zero, 6, true);
            if (tool != null) WearTool(p, p.SelectedSlot, 1);
        }

        // ============================================================ Ручной крафт

        public bool CanCraftAt(PlayerState p, CraftDef craft, Int3 stationPos)
        {
            if (craft.IsHand) return true;
            var m = Factory.MachineAt(stationPos);
            return m != null && m.Def.Id == craft.Station && InReach(p, stationPos);
        }

        /// <summary>Выполнить ручную операцию. Для операций с мини-игрой клиент присылает результат.</summary>
        public bool Craft(PlayerState p, int craftId, Int3 stationPos, bool minigameSuccess)
        {
            var craft = Content.Crafts.Get(craftId);
            if (craft == null || !Knowledge.IsCraftUnlocked(craft)) return false;
            if (!CanCraftAt(p, craft, stationPos)) return false;
            if (craft.Minigame != null && !minigameSuccess) return false;

            foreach (var i in craft.Inputs)
                if (p.Inventory.CountOf(i.ItemId) < i.Count) return false;

            // Места под выход: проверяем на копии с уже изъятыми входами
            var outputs = new List<ItemStack>();
            foreach (var o in craft.Outputs) outputs.Add(new ItemStack(o.ItemId, o.Count));
            foreach (var i in craft.Inputs) p.Inventory.Extract(i.ItemId, i.Count);
            if (!p.Inventory.CanInsertAll(outputs))
            {
                foreach (var i in craft.Inputs) p.Inventory.Insert(new ItemStack(i.ItemId, i.Count));
                Notify?.Invoke(p.Id, "ui.inventory_full");
                return false;
            }
            foreach (var o in outputs) p.Inventory.Insert(o);
            Knowledge.Notify("craft", craft.Id);
            Crafted?.Invoke(p, craft);
            return true;
        }

        // ============================================================ ME-сеть

        /// <summary>
        /// Сеть, к которой у игрока есть доступ через точку access: терминал/контроллер рядом
        /// или беспроводной терминал в инвентаре, привязанный к этой сети (дальность бесконечная).
        /// </summary>
        public MeNetwork MeAccess(PlayerState p, Int3 access)
        {
            var dev = Factory.MachineAt(access);
            if (dev == null || (dev.Def.Behavior != "me_terminal" && dev.Def.Behavior != "me_controller")) return null;
            var net = Factory.Me.NetworkAt(access);
            if (net == null) return null;
            if (InReach(p, access)) return net;
            for (int i = 0; i < p.Inventory.Size; i++)
            {
                var st = p.Inventory[i];
                if (st.IsEmpty || Content.Item(st.Item)?.MeWireless != true) continue;
                if (Factory.Me.TryGetLink(st.Meta, out var link) && Factory.Me.NetworkAt(link) == net) return net;
            }
            return null;
        }

        /// <summary>Точка доступа беспроводного терминала в руке (контроллер, к которому он привязан).</summary>
        public bool ResolveWireless(PlayerState p, out Int3 access)
        {
            access = default;
            var st = p.SelectedStack;
            if (st.IsEmpty || Content.Item(st.Item)?.MeWireless != true) return false;
            return Factory.Me.TryGetLink(st.Meta, out access) && Factory.MachineAt(access) != null;
        }

        public void MeView(PlayerState p, Int3 access, bool wireless, bool open)
        {
            if (!open)
            {
                p.MeView = null;
                return;
            }
            if (wireless && !ResolveWireless(p, out access))
            {
                Notify?.Invoke(p.Id, "ui.me.not_linked");
                return;
            }
            p.MeView = MeAccess(p, access) != null ? access : (Int3?)null;
        }

        public void MeExtract(PlayerState p, Int3 access, int item, int count)
        {
            var net = MeAccess(p, access);
            if (net == null || !net.Online || Content.Item(item) == null) return;
            int max = Content.Item(item).MaxStack;
            count = Math.Max(1, Math.Min(count, max));
            int fit = p.Inventory.Insert(new ItemStack(item, count), true);
            long got = Factory.Me.Extract(net, item, fit);
            if (got > 0) p.Inventory.Insert(new ItemStack(item, (int)got));
        }

        public void MeInsert(PlayerState p, Int3 access, int slot)
        {
            var net = MeAccess(p, access);
            if (net == null || !net.Online || slot < 0 || slot >= p.Inventory.Size) return;
            var st = p.Inventory[slot];
            if (st.IsEmpty || st.Meta != 0) return; // инструменты с износом и ячейки в сеть не кладём
            long put = Factory.Me.Insert(net, st.Item, st.Count);
            if (put > 0) p.Inventory.ExtractFromSlot(slot, (int)put);
        }

        public void MeCraft(PlayerState p, Int3 access, int item, int count)
        {
            var net = MeAccess(p, access);
            if (net == null || !net.Online || count <= 0) return;
            var missing = new List<KeyValuePair<int, long>>();
            if (Factory.Me.RequestCraft(net, item, Math.Min(count, 100000), missing)) return;
            if (missing.Count == 0) return;
            var m = missing[0];
            string name = m.Key >= 0 ? Content.Item(m.Key)?.NameKey : Content.Fluids.Get(-1 - m.Key)?.NameKey;
            Notify?.Invoke(p.Id, "ui.me.missing|" + (name ?? "?"));
        }

        public void MeStock(PlayerState p, Int3 access, int item, long target)
        {
            var net = MeAccess(p, access);
            if (net == null || Content.Item(item) == null) return;
            Factory.Me.SetStock(net, item, Math.Max(0, Math.Min(target, 1_000_000)));
        }

        public void MeCancel(PlayerState p, Int3 access)
        {
            var net = MeAccess(p, access);
            if (net != null) Factory.Me.CancelJobs(net);
        }

        private MachineInstance MeDeviceInReach(PlayerState p, Int3 pos, string behavior1, string behavior2 = null)
        {
            var m = Factory.MachineAt(pos);
            if (m == null || !InReach(p, pos)) return null;
            if (m.Def.Behavior != behavior1 && m.Def.Behavior != behavior2) return null;
            return m;
        }

        public void MeFilter(PlayerState p, Int3 pos, int slot, int item)
        {
            var m = MeDeviceInReach(p, pos, "me_import", "me_export");
            if (m == null || slot < 0 || slot >= m.Filter.Length) return;
            m.Filter[slot] = item >= 0 && Content.Item(item) != null ? item : -1;
        }

        public void MeFluidFilter(PlayerState p, Int3 pos, int fluid)
        {
            var m = MeDeviceInReach(p, pos, "me_import", "me_export");
            if (m == null) return;
            m.FluidFilter = fluid >= 0 && Content.Fluids.Get(fluid) != null ? fluid : -1;
        }

        public void MePattern(PlayerState p, Int3 pos, int recipe, bool enabled)
        {
            var provider = MeDeviceInReach(p, pos, "me_provider");
            if (provider == null) return;
            var target = Factory.MachineAt(pos + provider.Facing.Offset());
            var r = Content.Recipes.Get(recipe);
            if (target == null || r == null) return;
            bool belongs = false;
            foreach (var x in Content.RecipesFor(target.Def.NumericId))
                if (x == r) belongs = true;
            if (!belongs || !Knowledge.IsRecipeUnlocked(r)) return;
            provider.Patterns.Remove(recipe);
            if (enabled) provider.Patterns.Add(recipe);
        }

        public void MeLink(PlayerState p, Int3 controller)
        {
            var m = MeDeviceInReach(p, controller, "me_controller");
            var st = p.SelectedStack;
            if (m == null || st.IsEmpty || Content.Item(st.Item)?.MeWireless != true) return;
            p.Inventory[p.SelectedSlot] = new ItemStack(st.Item, st.Count, Factory.Me.LinkOf(controller));
            Notify?.Invoke(p.Id, "ui.me.linked");
        }

        // ============================================================ Сохранение

        public void Save()
        {
            if (SaveDir == null) return;
            Directory.CreateDirectory(SaveDir);

            WriteAtomic(Path.Combine(SaveDir, "world.dat"), w =>
            {
                w.Write(SaveVersion);
                w.Write(Seed);
                w.Write(WorldTime);
            });

            World.Save();

            WriteAtomic(Path.Combine(SaveDir, "factory.dat"), w => Factory.Write(w));

            WriteAtomic(Path.Combine(SaveDir, "knowledge.dat"), w => Knowledge.Write(w));
            WriteAtomic(Path.Combine(SaveDir, "items.dat"), w => Drops.Write(w));

            WriteAtomic(Path.Combine(SaveDir, "players.dat"), w =>
            {
                var all = new List<PlayerState>(_players.Values);
                all.AddRange(_savedPlayers.Values);
                w.Write(-PlayersVersion); // отрицательное число — маркер версии (старый формат начинался с количества)
                w.Write(all.Count);
                foreach (var p in all) p.Write(w);
            });
            Log.Info($"World saved to {SaveDir}");
        }

        private void LoadState()
        {
            if (SaveDir == null) return;
            string factory = Path.Combine(SaveDir, "factory.dat");
            if (File.Exists(factory))
                using (var r = new BinaryReader(File.OpenRead(factory)))
                    Factory.Read(r);

            string players = Path.Combine(SaveDir, "players.dat");
            if (File.Exists(players))
            {
                using (var r = new BinaryReader(File.OpenRead(players)))
                {
                    int first = r.ReadInt32();
                    int version = first < 0 ? -first : 1;
                    int n = first < 0 ? r.ReadInt32() : first;
                    for (int i = 0; i < n; i++)
                    {
                        var p = new PlayerState(0, "", Content);
                        p.Read(r, version);
                        _savedPlayers[p.Name] = p;
                    }
                }
            }

            string itemsFile = Path.Combine(SaveDir, "items.dat");
            if (File.Exists(itemsFile))
                using (var r = new BinaryReader(File.OpenRead(itemsFile)))
                    Drops.Read(r);

            string knowledge = Path.Combine(SaveDir, "knowledge.dat");
            if (File.Exists(knowledge))
                using (var r = new BinaryReader(File.OpenRead(knowledge)))
                    Knowledge.Read(r);
        }

        private static void WriteAtomic(string path, Action<BinaryWriter> write)
        {
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new BinaryWriter(fs))
                write(w);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public void Dispose()
        {
            World.Dispose();
        }
    }
}
