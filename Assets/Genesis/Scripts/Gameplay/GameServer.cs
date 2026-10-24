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
        // 3: настройки мира
        private const int SaveVersion = 3;
        /// <summary>Длина суток в тиках: 24000 = 20 минут.</summary>
        public const int DayLength = 24000;
        // 4: токен игрока; 5: левая рука, справочник, точка возрождения
        private const int PlayersVersion = 5;

        /// <summary>Файлы состояния, которые пишутся одной транзакцией (и попадают в резервные копии).</summary>
        private static readonly string[] StateFiles = { "world.dat", "factory.dat", "knowledge.dat", "items.dat", "players.dat", "creatures.dat" };
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
        /// <summary>Настройки мира (креатив, враги на поверхности, правила смерти, сложность).</summary>
        public WorldSettings Settings = new WorldSettings();
        /// <summary>Имя игрока-хоста (локальный игрок): ему доступен креатив и настройки мира.</summary>
        public string HostName;

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
            Knowledge.Changed += Factory.InvalidateRecipeCache;
            Enemies = new EnemyManager(this);
            Drops = new DroppedItems(World, content);
            // Заполненная ячейка ME, выпавшая в мир, не исчезает (иначе пропало бы всё её содержимое)
            Drops.NeverDespawn = st => st.Meta > 0 && Content.Item(st.Item)?.StorageCell != null && Factory.Me.CellHasContents(st.Meta);
            Knowledge.NodeUnlocked += n => NotifyAll("ui.unlocked|" + n.NameKey);
            Knowledge.EraAdvanced += era => NotifyAll("ui.new_era|era." + Eras.All[era]);
            World.Store.RegionDamaged += path => NotifyAll("ui.region_damaged");
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
        /// <param name="settingsForNew">Настройки для нового мира (у существующего читаются из world.dat).</param>
        public static GameServer Open(ContentDatabase content, string saveDir, long newSeed, int workerThreads, WorldSettings settingsForNew = null)
        {
            long seed = newSeed;
            long savedTime = -1;
            var settings = settingsForNew?.Clone() ?? new WorldSettings();
            if (saveDir != null)
            {
                Directory.CreateDirectory(saveDir);
                SaveTransaction.Recover(saveDir); // прерванное сохранение — довести до конца
                string meta = Path.Combine(saveDir, "world.dat");
                if (SafeFile.Exists(meta))
                {
                    bool newer = false;
                    bool ok = SafeFile.TryRead(meta, r =>
                    {
                        int ver = r.ReadInt32();
                        if (ver > SaveVersion)
                        {
                            newer = true;
                            return;
                        }
                        seed = r.ReadInt64();
                        if (ver >= 2) savedTime = r.ReadInt64();
                        // Миры до настроек создавались с креативом — сохраняем его хосту
                        settings = ver >= 3 ? WorldSettings.Read(r) : new WorldSettings { Creative = true };
                    });
                    if (newer) throw new InvalidDataException("Save is from a newer version");
                    if (!ok) throw new InvalidDataException("world.dat is damaged and has no backup (see backups folder)");
                }
            }
            var server = new GameServer(content, saveDir, seed, workerThreads) { Settings = settings };
            if (savedTime >= 0) server.WorldTime = savedTime;
            server.LoadState();
            return server;
        }

        /// <summary>Может ли игрок пользоваться креативом в этом мире.</summary>
        public bool CreativeAllowed(PlayerState p) =>
            Settings.Creative && (Settings.CreativeForGuests || HostName == null || p.Name == HostName);

        /// <summary>Хост меняет настройки мира (меню паузы).</summary>
        public void ApplySettings(PlayerState by, WorldSettings s)
        {
            if (HostName != null && by.Name != HostName) return;
            Settings = s.Clone();
            foreach (var p in _players.Values)
            {
                p.Creative = CreativeAllowed(p);
                if (!p.Creative) p.Flying = false;
            }
        }

        // ============================================================ Игроки

        /// <summary>
        /// Вход игрока. token — постоянный секрет клиента: сохранённого игрока может занять только владелец
        /// того же токена (иначе любой мог зайти под ником отсутствующего друга и забрать его инвентарь).
        /// Возвращает null, если ник занят (онлайн или чужим токеном).
        /// </summary>
        public PlayerState Join(string name, string token = null, bool host = false)
        {
            token = token ?? "";
            foreach (var online in _players.Values)
                if (online.Name == name) return null;
            if (host) HostName = name;
            PlayerState p;
            if (_savedPlayers.TryGetValue(name, out var saved))
            {
                if (saved.Token.Length > 0 && saved.Token != token && !host) return null;
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
            if (token.Length > 0) player.Token = token;
            else player.Token = p.Token;
            player.Creative = CreativeAllowed(player);
            if (!player.Creative) player.Flying = false;
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
            to.Offhand[0] = from.Offhand[0];
            to.HasHandbook = from.HasHandbook;
            to.SpawnPoint = from.SpawnPoint;
            to.Token = from.Token;
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

        // ============================================================ Настройка машин (с проверкой дистанции)

        /// <summary>Поворот гаечным ключом/клавишей R — только в пределах досягаемости.</summary>
        public void RotateEntity(PlayerState p, Int3 pos)
        {
            if (!InReach(p, pos)) return;
            Factory.Rotate(pos);
        }

        public void SetMachineSide(PlayerState p, Int3 pos, RelativeSide side, SideType type)
        {
            if (!InReach(p, pos)) return;
            Factory.SetSide(pos, side, type);
        }

        /// <summary>Выбрать рецепт машины в её интерфейсе; recipeId &lt; 0 — авто.</summary>
        public void SetMachineRecipe(PlayerState p, Int3 pos, int recipeId)
        {
            if (!InReach(p, pos)) return;
            var recipe = recipeId >= 0 ? Content.Recipes.Get(recipeId) : null;
            if (recipeId >= 0 && recipe == null) return;
            if (recipe != null && !Knowledge.IsRecipeUnlocked(recipe))
            {
                Notify?.Invoke(p.Id, "ui.locked");
                return;
            }
            Factory.SetLockedRecipe(pos, recipe);
        }

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

            // Многоблочная машина ломается целиком; оторванное продолжение — просто исчезает
            var owner = MultiBlock.OwnerOf(Factory, pos);
            if (owner != null && owner.Def.IsMultiBlock)
            {
                BreakMachine(p, owner, tool);
                return true;
            }
            if (def.PartOf != null)
            {
                World.SetBlock(pos, Content.AirNumeric);
                return true;
            }

            // Топор или рубило валит дерево целиком
            if (def.Fellable && tool != null && tool.CanFell && Mining.IsCorrectTool(def, tool))
            {
                FellTree(p, pos, tool);
                return true;
            }

            var drops = RollDrops(def, tool); // выпадают в мир (подбираются, подлетая к игроку)
            if ((def.Drop != null || def.Drops != null) && !Mining.CanHarvest(def, tool))
                Notify?.Invoke(p.Id, "ui.wrong_tool");
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
                foreach (var it in c.Items) list.Add(new ItemStack(it.Item, 1, it.Meta));
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
            // Труба между сетями с разными жидкостями слила бы их, и одна исчезла бы
            if (block.Transport != null && block.Transport.Type == "pipe" && Factory.WouldMixFluids(pos))
            {
                Notify?.Invoke(p.Id, "ui.pipe_mixed");
                return false;
            }
            foreach (var other in _players.Values)
                if (other != p && block.Solid && PlayerPhysics.IntersectsBlock(other, pos)) return false;

            var horizontal = look.IsHorizontal() ? look : Direction.North;
            // Машина «смотрит» на игрока; лента едет туда, куда смотрит игрок.
            var mdef = block.MachineId != null ? Content.Machines.Get(block.MachineId) : null;
            var facing = mdef != null && mdef.Orient != "away" ? horizontal.Opposite() : horizontal;

            if (mdef != null && mdef.IsMultiBlock)
            {
                // Верстак, печка: все клетки должны быть свободны
                if (!CanPlaceCells(p, MultiBlock.Cells(mdef, pos, facing), block.Solid))
                {
                    Notify?.Invoke(p.Id, "ui.structure.no_room");
                    return false;
                }
                PlaceBlockAt(pos, block, facing, p);
            }
            else
            {
                if (!World.SetBlock(pos, block.NumericId)) return false;
                Factory.OnBlockPlaced(pos, block, facing);
            }
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
            c.Accept(stack.Item, 0f, 0f, stack.Meta);
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
            if (t % 20 == 7) TickDecay();

            foreach (var p in _players.Values)
            {
                if (p.SleepTick >= 0 && t - p.SleepTick > 60) p.SleepTick = -1;
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
                        if (p.Inventory.CountOf(item) > 0 || p.Offhand.CountOf(item) > 0) { hits.Add(node.Trigger.Target); break; }
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
            CloseGrid(p); // сетка крафта и курсор — в инвентарь (раньше переживали смерть)
            p.Shaping = null;
            if (!Settings.KeepInventory && (!p.Inventory.IsEmpty || !p.Offhand.IsEmpty))
            {
                var items = new List<ItemStack>(p.Inventory.NonEmpty());
                items.AddRange(p.Offhand.NonEmpty());
                p.Inventory.Clear();
                p.Offhand.Clear();
                var grave = PlaceGrave(p.Position.Floor());
                var deathPos = new Vec3(p.Position.X, p.Position.Y + 0.5f, p.Position.Z);
                foreach (var st in items)
                {
                    int put = grave != null ? grave.Input.Insert(st) : 0;
                    // Не влезло в останки или их негде поставить — вещи выпадают на месте гибели и лежат дольше
                    // обычного, а не исчезают молча
                    if (put < st.Count)
                    {
                        var d = Drops.Spawn(st.WithCount(st.Count - put), deathPos, Vec3.Zero, 0, true);
                        if (d != null) d.Age = -DroppedItems.DespawnTicks * 2;
                    }
                }
                if (grave == null) Notify?.Invoke(p.Id, "ui.items_dropped");
            }

            var sp = World.Generator.SpawnPoint;
            // Лежанка цела — возрождение на ней, иначе — в лаборатории
            if (p.SpawnPoint.HasValue)
            {
                var bed = p.SpawnPoint.Value;
                if (World.IsLoaded(bed.X, bed.Y, bed.Z) || World.LoadNow(WorldConstants.ChunkOf(bed)) != null)
                {
                    if (World.GetBlockDef(bed).Action == "sleep") sp = bed + Int3.Up;
                    else p.SpawnPoint = null;
                }
            }
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

        /// <summary>Найти место для останков рядом с точкой гибели (столб вверх, потом соседние клетки).</summary>
        private MachineInstance PlaceGrave(Int3 feet)
        {
            var graveBlock = Content.Blocks.Get("genesis:grave");
            if (graveBlock == null) return null;
            for (int ring = 0; ring <= 2; ring++)
            for (int dy = 0; dy < 6; dy++)
            for (int dz = -ring; dz <= ring; dz++)
            for (int dx = -ring; dx <= ring; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                var pos = feet + new Int3(dx, dy, dz);
                if (pos.Y < WorldConstants.MinY + 1 || pos.Y > WorldConstants.MaxY) continue;
                if (!World.IsLoaded(pos.X, pos.Y, pos.Z) || !World.GetBlockDef(pos).Replaceable) continue;
                if (World.GetBlockDef(pos).Liquid && ring < 2) continue; // в воде — только если иначе никак
                if (Factory.MachineAt(pos) != null || Factory.ConveyorAt(pos) != null) continue;
                if (!World.SetBlock(pos, graveBlock.NumericId)) continue;
                Factory.OnBlockPlaced(pos, graveBlock, Direction.North);
                return Factory.MachineAt(pos);
            }
            return null;
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

        public const int AttackCooldownTicks = 7;

        public void Attack(PlayerState p, int enemyId)
        {
            var e = Enemies.Get(enemyId);
            // Уже мёртв (удаляется только в следующем тике) — иначе пачка команд за кадр давала лут много раз
            if (e == null || e.Health <= 0 || p.Health <= 0) return;
            if ((e.Position - p.Position).LengthSquared > AttackReach * AttackReach + 4) return;
            // Серверная перезарядка удара (клиентская — 0.45 с; небольшой запас на задержку сети)
            if (TickCount < p.NextAttackTick) return;
            p.NextAttackTick = TickCount + AttackCooldownTicks;
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

            // Непустые ячейки ME в крафт не идут (их содержимое пропало бы)
            Func<ItemStack, bool> usable = s => !Factory.Me.CellHasContents(s.Meta) || Content.Item(s.Item)?.StorageCell == null;
            foreach (var i in craft.Inputs)
                if (p.Inventory.CountOf(i.ItemId, usable) < i.Count) return false;

            // Места под выход: проверяем после изъятия входов; не влезло — откат по снимку (Meta сохраняется,
            // изношенный инструмент не превращается в новый)
            var outputs = new List<ItemStack>();
            foreach (var o in craft.Outputs) outputs.Add(new ItemStack(o.ItemId, o.Count));
            var snapshot = p.Inventory.Snapshot();
            foreach (var i in craft.Inputs) p.Inventory.Extract(i.ItemId, i.Count, usable);
            if (!p.Inventory.CanInsertAll(outputs))
            {
                p.Inventory.Restore(snapshot);
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

        private System.Threading.Tasks.Task _saveTask;
        private List<RegionStore.RegionWrite> _savingRegions;

        /// <summary>Сохранить мир синхронно (выход из игры, ручное сохранение, тесты).</summary>
        public void Save()
        {
            SaveAsync();
            WaitForSave();
        }

        /// <summary>
        /// Сохранение без фриза: в главном потоке только снимок состояния в память (быстро), сжатие регионов
        /// и запись на диск — в фоне. Все файлы состояния коммитятся одной транзакцией; раз в 15 минут —
        /// ротация резервных копий (backups/1..3).
        /// </summary>
        public System.Threading.Tasks.Task SaveAsync()
        {
            if (SaveDir == null) return System.Threading.Tasks.Task.CompletedTask;
            WaitForSave(); // предыдущее сохранение ещё пишется — дождаться
            Directory.CreateDirectory(SaveDir);

            CollectCellGarbage();
            var regions = World.PrepareSave();
            _savingRegions = regions;
            var tx = new SaveTransaction(SaveDir);
            tx.Add("world.dat", w =>
            {
                w.Write(SaveVersion);
                w.Write(Seed);
                w.Write(WorldTime);
                Settings.Write(w);
            });
            tx.Add("factory.dat", w => Factory.Write(w));
            tx.Add("knowledge.dat", w => Knowledge.Write(w));
            tx.Add("items.dat", w => Drops.Write(w));
            tx.Add("creatures.dat", w => Enemies.Write(w));
            tx.Add("players.dat", w =>
            {
                var all = new List<PlayerState>(_players.Values);
                all.AddRange(_savedPlayers.Values);
                w.Write(-PlayersVersion); // отрицательное число — маркер версии (старый формат начинался с количества)
                w.Write(all.Count);
                foreach (var p in all) p.Write(w);
            });

            string dir = SaveDir;
            Action job = () =>
            {
                foreach (var r in regions) RegionStore.WriteRegion(r);
                tx.Commit();
                try
                {
                    SafeFile.RotateBackups(dir, StateFiles, 3, TimeSpan.FromMinutes(15));
                }
                catch (Exception e)
                {
                    Log.Warn("Backup rotation failed: " + e.Message);
                }
                Log.Info($"World saved to {dir}");
            };
            if (Platform.Threads)
            {
                _saveTask = System.Threading.Tasks.Task.Run(job);
                return _saveTask;
            }
            // Браузер: потоков нет — пишем сразу (файловая система в памяти, это быстро), затем сброс в IndexedDB
            try
            {
                job();
                _saveTask = System.Threading.Tasks.Task.CompletedTask;
            }
            catch (Exception e)
            {
                _saveTask = System.Threading.Tasks.Task.FromException(e);
            }
            Platform.AfterSave?.Invoke();
            return _saveTask;
        }

        /// <summary>Убрать из реестра ячеек ME записи, на которые больше не ссылается ни один предмет.</summary>
        private void CollectCellGarbage()
        {
            var refs = new HashSet<int>();
            void Scan(Inventory inv)
            {
                for (int i = 0; i < inv.Size; i++)
                {
                    var s = inv[i];
                    if (!s.IsEmpty && s.Meta > 0 && Content.Item(s.Item)?.StorageCell != null) refs.Add(s.Meta);
                }
            }
            foreach (var m in Factory.Machines)
            {
                Scan(m.Input);
                Scan(m.Output);
                Scan(m.Fuel);
            }
            foreach (var c in Factory.Conveyors)
                foreach (var it in c.Items)
                    if (it.Meta > 0 && Content.Item(it.Item)?.StorageCell != null) refs.Add(it.Meta);
            foreach (var p in _players.Values)
            {
                Scan(p.Inventory);
                Scan(p.CraftGrid);
                Scan(p.Cursor);
            }
            foreach (var p in _savedPlayers.Values)
            {
                Scan(p.Inventory);
                Scan(p.CraftGrid);
                Scan(p.Cursor);
            }
            foreach (var d in Drops.All)
                if (d.Stack.Meta > 0 && Content.Item(d.Stack.Item)?.StorageCell != null) refs.Add(d.Stack.Meta);
            int removed = Factory.Me.CollectGarbage(refs);
            if (removed > 0) Log.Info($"ME: removed {removed} orphaned storage cells");
        }

        /// <summary>Дождаться фонового сохранения (ошибка записи — в лог и наружу).</summary>
        public void WaitForSave()
        {
            var t = _saveTask;
            if (t == null) return;
            _saveTask = null;
            try
            {
                t.Wait();
            }
            catch (AggregateException e)
            {
                var inner = e.InnerException ?? e;
                Log.Error("Save failed: " + inner);
                // Регионы снова «грязные» — запишутся при следующем сохранении
                if (_savingRegions != null) World.Store.MarkDirty(_savingRegions);
                _savingRegions = null;
                throw new IOException("Save failed: " + inner.Message, inner);
            }
            _savingRegions = null;
        }

        public bool IsSaving => _saveTask != null && !_saveTask.IsCompleted;

        /// <summary>
        /// Загрузка состояния: каждый файл читается с откатом на .bak. Фабрика не прочиталась ни из файла,
        /// ни из копии — мир не открываем (иначе следующее сохранение затёрло бы постройки пустой фабрикой).
        /// </summary>
        private void LoadState()
        {
            if (SaveDir == null) return;
            string factory = Path.Combine(SaveDir, "factory.dat");
            if (SafeFile.Exists(factory) && !SafeFile.TryRead(factory, r => Factory.Read(r)))
                throw new InvalidDataException("factory.dat is damaged and its backup too. Restore a copy from the 'backups' folder of the world.");

            string players = Path.Combine(SaveDir, "players.dat");
            SafeFile.TryRead(players, r =>
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
            }, () => _savedPlayers.Clear());

            string itemsFile = Path.Combine(SaveDir, "items.dat");
            SafeFile.TryRead(itemsFile, r => Drops.Read(r), () => Drops.Clear());

            string creatures = Path.Combine(SaveDir, "creatures.dat");
            SafeFile.TryRead(creatures, r => Enemies.Read(r), () => Enemies.Clear());

            string knowledge = Path.Combine(SaveDir, "knowledge.dat");
            SafeFile.TryRead(knowledge, r => Knowledge.Read(r));
        }

        public void Dispose()
        {
            try
            {
                WaitForSave();
            }
            catch (IOException)
            {
                // уже в логе
            }
            World.Dispose();
        }
    }
}
