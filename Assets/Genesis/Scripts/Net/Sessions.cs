using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Net
{
    /// <summary>Другой игрок, как его видит клиент (для отрисовки).</summary>
    public sealed class RemotePlayerView
    {
        public int Id;
        public string Name;
        public Vec3 Position, PrevPosition;
        public float Yaw, Pitch;
        public float LastUpdate;
    }

    /// <summary>Враг, как его видит клиент.</summary>
    public sealed class EnemyView
    {
        public int Id;
        public EnemyDef Def;
        public Vec3 Position;
        public float Yaw;
        public float Health;
        public int Hurt;
        public CreatureState State;
    }

    /// <summary>Снаряд или облако спор (для отрисовки).</summary>
    public sealed class ProjectileView
    {
        public int Id;
        public Vec3 Position;
        public bool Cloud;
        public float Radius;
    }

    public enum SessionState
    {
        Connecting,
        Playing,
        Failed
    }

    /// <summary>
    /// То, с чем работает клиент Unity: мир, фабрика, локальный игрок, отправка команд.
    /// LocalSession — одиночная игра или хост (сервер в этом же процессе), RemoteSession — подключение к хосту.
    /// </summary>
    public interface IGameSession : IDisposable
    {
        SessionState State { get; }
        string Error { get; }
        ContentDatabase Content { get; }
        VoxelWorld World { get; }
        Factory Factory { get; }
        /// <summary>Локальный игрок: инвентарь/слот — с сервера, позиция — клиент-авторитетно.</summary>
        PlayerState LocalPlayer { get; }
        float TickAlpha { get; }
        bool CanSave { get; }
        IReadOnlyList<RemotePlayerView> OtherPlayers { get; }
        IReadOnlyList<EnemyView> Enemies { get; }
        /// <summary>Предметы, лежащие в мире рядом с игроком.</summary>
        IReadOnlyList<DroppedItem> DroppedItems { get; }
        /// <summary>Снаряды существ и облака спор.</summary>
        IReadOnlyList<ProjectileView> Projectiles { get; }
        KnowledgeState Knowledge { get; }
        /// <summary>Фаза суток 0..1 (0 — рассвет, 0.25 — полдень, 0.5 — закат).</summary>
        float DayPhase { get; }
        string StatusLine { get; }

        /// <summary>Снимок открытой ME-сети (null — окно не открыто или ответ ещё не пришёл).</summary>
        MeSnapshot Me { get; }
        /// <summary>Открыть ME-сеть через терминал/контроллер или беспроводной терминал в руке.</summary>
        void OpenMe(Int3 access, bool wireless);
        void CloseMe();

        event Action<string> Toast;

        void Send(GameCommand cmd);
        void Update(float dt, float time);
        /// <param name="background">Писать на диск в фоне (автосохранение без фриза).</param>
        void Save(bool background = false);
    }

    // ================================================================ Локальная сессия

    public sealed class LocalSession : IGameSession
    {
        public readonly GameServer Server;
        private NetServer _net;
        private readonly List<RemotePlayerView> _others = new List<RemotePlayerView>();
        private readonly Dictionary<int, RemotePlayerView> _othersById = new Dictionary<int, RemotePlayerView>();

        public SessionState State => SessionState.Playing;
        public string Error => null;
        public ContentDatabase Content => Server.Content;
        public VoxelWorld World => Server.World;
        public Factory Factory => Server.Factory;
        public PlayerState LocalPlayer { get; }
        public float TickAlpha => Server.TickAlpha;
        public bool CanSave => true;
        public IReadOnlyList<RemotePlayerView> OtherPlayers => _others;
        public IReadOnlyList<EnemyView> Enemies => _enemies;
        public IReadOnlyList<DroppedItem> DroppedItems => _drops;
        private readonly List<DroppedItem> _drops = new List<DroppedItem>();
        public IReadOnlyList<ProjectileView> Projectiles => _projectiles;
        private readonly List<ProjectileView> _projectiles = new List<ProjectileView>();
        public KnowledgeState Knowledge => Server.Knowledge;
        public float DayPhase => Server.DayPhase;

        private Int3? _meAccess;
        private MeSnapshot _me;
        private int _meFrame;
        public MeSnapshot Me => _meAccess.HasValue ? _me : null;

        public void OpenMe(Int3 access, bool wireless)
        {
            if (wireless && !Server.ResolveWireless(LocalPlayer, out access))
            {
                Toast?.Invoke("ui.me.not_linked");
                return;
            }
            if (Server.MeAccess(LocalPlayer, access) == null) return;
            _meAccess = access;
            _me = Server.Factory.Me.Snapshot(access);
        }

        public void CloseMe()
        {
            _meAccess = null;
            _me = null;
        }
        public bool IsHosting => _net != null;
        private readonly List<EnemyView> _enemies = new List<EnemyView>();
        private readonly List<EnemyView> _enemyPool = new List<EnemyView>();
        private readonly List<ProjectileView> _fxPool = new List<ProjectileView>();

        public string StatusLine => _net != null ? $"Host: {_net.ClientCount} connected via {_hostTransportName}" : "Singleplayer";
        private string _hostTransportName;

        public event Action<string> Toast;
        public event Action<string> NetLog;

        public LocalSession(GameServer server, string playerName, string token = null)
        {
            Server = server;
            LocalPlayer = server.Join(playerName, token, true); // локальный игрок — хост
            server.Notify += (id, key) =>
            {
                if (id == LocalPlayer.Id) Toast?.Invoke(key);
            };
        }

        /// <summary>Открыть мир для друзей (кооп до 4 игроков).</summary>
        public void StartHosting(IServerTransport transport, string transportName)
        {
            if (_net != null) return;
            _net = new NetServer(Server, transport);
            _net.Log += s => NetLog?.Invoke(s);
            _hostTransportName = transportName;
        }

        public void Send(GameCommand cmd)
        {
            cmd.PlayerId = LocalPlayer.Id;
            Server.Enqueue(cmd);
        }

        public void Update(float dt, float time)
        {
            Server.Update(dt);
            _net?.Update();

            // Список других игроков для отрисовки (на хосте — прямо из сервера)
            var seen = new HashSet<int>();
            foreach (var p in Server.Players)
            {
                if (p.Id == LocalPlayer.Id) continue;
                seen.Add(p.Id);
                if (!_othersById.TryGetValue(p.Id, out var v))
                {
                    v = new RemotePlayerView { Id = p.Id, Name = p.Name, Position = p.Position };
                    _othersById[p.Id] = v;
                    _others.Add(v);
                }
                v.PrevPosition = v.Position;
                v.Position = p.Position;
                v.Yaw = p.Yaw;
                v.Pitch = p.Pitch;
                v.LastUpdate = time;
            }
            _others.RemoveAll(v => !seen.Contains(v.Id));
            foreach (var id in new List<int>(_othersById.Keys))
                if (!seen.Contains(id)) _othersById.Remove(id);

            if (_meAccess.HasValue && ++_meFrame % 6 == 0)
            {
                if (Server.MeAccess(LocalPlayer, _meAccess.Value) == null) CloseMe();
                else _me = Server.Factory.Me.Snapshot(_meAccess.Value);
            }

            // Объекты представлений переиспользуются (раньше — новые каждый кадр, лишний мусор для GC)
            var all = Server.Enemies.All;
            while (_enemyPool.Count < all.Count) _enemyPool.Add(new EnemyView());
            _enemies.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                var v = _enemyPool[i];
                v.Id = e.Id;
                v.Def = e.Def;
                v.Position = e.Position;
                v.Yaw = e.Yaw;
                v.Health = e.Health;
                v.Hurt = e.HurtTicks;
                v.State = e.State;
                _enemies.Add(v);
            }
            int fx = Server.Enemies.Projectiles.Count + Server.Enemies.Clouds.Count;
            while (_fxPool.Count < fx) _fxPool.Add(new ProjectileView());
            _projectiles.Clear();
            int k = 0;
            foreach (var p in Server.Enemies.Projectiles)
            {
                var v = _fxPool[k++];
                v.Id = p.Id;
                v.Position = p.Position;
                v.Cloud = false;
                v.Radius = 0;
                _projectiles.Add(v);
            }
            foreach (var c in Server.Enemies.Clouds)
            {
                var v = _fxPool[k++];
                v.Id = c.Id;
                v.Position = c.Position;
                v.Cloud = true;
                v.Radius = c.Radius;
                _projectiles.Add(v);
            }
            _drops.Clear();
            _drops.AddRange(Server.Drops.All);
        }

        public void Save(bool background = false)
        {
            if (background) Server.SaveAsync();
            else Server.Save();
        }

        public void Dispose()
        {
            _net?.Dispose();
            _net = null;
            Server.Dispose();
        }
    }

    // ================================================================ Удалённая сессия

    /// <summary>
    /// Клиент в коопе: реплика мира (нетронутые чанки генерируются локально по seed),
    /// реплика фабрики (ленты двигаются локально, остальное — снапшоты хоста), отправка команд.
    /// </summary>
    public sealed class RemoteSession : IGameSession, IChunkSource
    {
        private readonly IClientTransport _transport;
        private readonly string _name;
        private readonly int _workers;
        private readonly List<RemotePlayerView> _others = new List<RemotePlayerView>();
        private readonly Dictionary<int, RemotePlayerView> _othersById = new Dictionary<int, RemotePlayerView>();
        private readonly List<Int3> _chunkRequests = new List<Int3>();
        private readonly List<Int3> _chunkRequestsFull = new List<Int3>();
        private readonly string _token;
        private float _lastReceived;
        private float _lastPing;
        /// <summary>Хост молчит дольше (с) — соединение считается потерянным.</summary>
        public const float HostTimeout = 30f;
        private readonly Dictionary<Int3, List<(Int3, ushort)>> _bufferedChanges = new Dictionary<Int3, List<(Int3, ushort)>>();
        private float _accumulator;
        private float _lastMoveSent = -1f;
        private int _frame;
        private float _time;

        public SessionState State { get; private set; } = SessionState.Connecting;
        public string Error { get; private set; }
        public ContentDatabase Content { get; }
        public VoxelWorld World { get; private set; }
        public Factory Factory { get; private set; }
        public PlayerState LocalPlayer { get; private set; }
        public float TickAlpha => _accumulator / GameConstants.TickDuration;
        public bool CanSave => false;
        public IReadOnlyList<RemotePlayerView> OtherPlayers => _others;
        public IReadOnlyList<EnemyView> Enemies => _enemies;
        public IReadOnlyList<DroppedItem> DroppedItems => _drops;
        private readonly List<DroppedItem> _drops = new List<DroppedItem>();
        public IReadOnlyList<ProjectileView> Projectiles => _projectiles;
        private readonly List<ProjectileView> _projectiles = new List<ProjectileView>();
        public KnowledgeState Knowledge { get; }
        private readonly List<EnemyView> _enemies = new List<EnemyView>();
        private long _worldTime = GameServer.DayLength / 20;
        public float DayPhase => (float)(_worldTime % GameServer.DayLength) / GameServer.DayLength;

        private bool _meOpen;
        private MeSnapshot _me;
        public MeSnapshot Me => _meOpen ? _me : null;

        public void OpenMe(Int3 access, bool wireless)
        {
            _meOpen = true;
            _me = null;
            Send(new MeViewCommand { Access = access, Wireless = wireless, Open = true });
        }

        public void CloseMe()
        {
            if (!_meOpen) return;
            _meOpen = false;
            _me = null;
            Send(new MeViewCommand { Open = false });
        }
        public string StatusLine => State == SessionState.Playing ? $"Client: {World.LoadedCount} chunks" : State.ToString();

        public int ViewRadius = 8;
        public int VerticalRadius = 5;
        /// <summary>Свой список модов (для понятной ошибки при несовпадении с хостом).</summary>
        public string ModList = "";

        public event Action<string> Toast;

        public RemoteSession(ContentDatabase content, IClientTransport transport, string playerName, int workerThreads, string token = "")
        {
            _token = token ?? "";
            Content = content;
            Knowledge = new KnowledgeState(content);
            _transport = transport;
            _name = playerName;
            _workers = workerThreads;
            _transport.Connected += OnConnected;
            _transport.Disconnected += OnDisconnected;
            _transport.Received += OnReceived;
        }

        private void OnConnected()
        {
            var w = Protocol.Begin(MsgType.Hello, out var ms);
            w.Write(Protocol.Version);
            w.Write(Content.ContentHash);
            w.Write(ModList ?? "");
            w.Write(_name);
            w.Write(_token);
            w.Write(WorldGenerator.Version);
            _transport.Send(Protocol.End(w, ms), true);
        }

        private void OnDisconnected(string reason)
        {
            if (State == SessionState.Failed) return;
            Error = Error ?? ("Disconnected: " + reason);
            State = SessionState.Failed;
        }

        // ============================================================ Входящие

        private void OnReceived(byte[] data)
        {
            _lastReceived = _time;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                    Handle((MsgType)r.ReadByte(), r);
            }
            catch (Exception e)
            {
                Log.Error("Bad packet from host: " + e);
            }
        }

        private void Handle(MsgType type, BinaryReader r)
        {
            if (type == MsgType.Welcome)
            {
                HandleWelcome(r);
                return;
            }
            if (type == MsgType.Reject)
            {
                Error = "Rejected: " + r.ReadString();
                State = SessionState.Failed;
                return;
            }
            if (State != SessionState.Playing) return;

            switch (type)
            {
                case MsgType.ChunkPristine:
                {
                    var coord = Protocol.ReadPos(r);
                    World.ProvidePristine(coord, r.ReadUInt64());
                    break;
                }
                case MsgType.ChunkData:
                {
                    var coord = Protocol.ReadPos(r);
                    int len = r.ReadInt32();
                    var bytes = r.ReadBytes(len);
                    using (var cms = new MemoryStream(bytes))
                    using (var cr = new BinaryReader(cms))
                        World.ProvideData(ChunkCodec.Read(cr, coord, Content));
                    break;
                }
                case MsgType.BlockChange:
                {
                    var pos = Protocol.ReadPos(r);
                    ushort block = r.ReadUInt16();
                    ApplyBlockChange(pos, block);
                    break;
                }
                case MsgType.Players:
                    ReadPlayers(r);
                    break;
                case MsgType.Inventory:
                    ReplicationCodec.ReadPlayerItems(r, LocalPlayer);
                    break;
                case MsgType.Machine:
                    ReplicationCodec.ReadMachine(r, Factory);
                    break;
                case MsgType.Conveyor:
                    ReplicationCodec.ReadConveyor(r, Factory);
                    break;
                case MsgType.Machines:
                {
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++) ReplicationCodec.ReadMachine(r, Factory);
                    break;
                }
                case MsgType.Conveyors:
                {
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++) ReplicationCodec.ReadConveyor(r, Factory);
                    break;
                }
                case MsgType.EntityRemoved:
                    Factory.OnBlockRemoved(Protocol.ReadPos(r));
                    break;
                case MsgType.Toast:
                    Toast?.Invoke(r.ReadString());
                    break;
                case MsgType.Time:
                    _worldTime = r.ReadInt64();
                    break;
                case MsgType.MeSnapshot:
                {
                    var snap = MeSnapshot.Read(r);
                    if (_meOpen) _me = snap;
                    break;
                }
                case MsgType.Knowledge:
                {
                    int era = r.ReadInt32();
                    int n = r.ReadInt32();
                    var ids = new List<string>(n);
                    for (int i = 0; i < n; i++) ids.Add(r.ReadString());
                    Knowledge.ApplyReplica(era, ids);
                    break;
                }
                case MsgType.Vitals:
                {
                    var p = LocalPlayer;
                    p.Health = r.ReadSingle();
                    p.Hunger = r.ReadSingle();
                    int tp = r.ReadInt32();
                    p.LastDamageTick = r.ReadInt64();
                    p.PoisonTicks = r.ReadInt16();
                    int kv = r.ReadInt32();
                    var kb = Protocol.ReadVec(r);
                    p.Creative = r.ReadBoolean();
                    if (!p.Creative) p.Flying = false;
                    if (kv != p.KnockbackVersion)
                    {
                        p.KnockbackVersion = kv;
                        p.Knockback = kb;
                    }
                    var pos = Protocol.ReadVec(r);
                    if (tp != p.TeleportVersion)
                    {
                        p.TeleportVersion = tp;
                        p.Position = pos;
                    }
                    break;
                }
                case MsgType.Enemies:
                {
                    int n = r.ReadUInt16();
                    _enemies.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var view = new EnemyView
                        {
                            Id = r.ReadInt32(),
                            Def = Content.Enemies.Get(r.ReadUInt16()),
                            Position = Protocol.ReadVec(r),
                            Yaw = r.ReadSingle(),
                            Health = r.ReadSingle(),
                            Hurt = r.ReadByte(),
                            State = (CreatureState)r.ReadByte()
                        };
                        if (view.Def != null) _enemies.Add(view);
                    }
                    int fxn = r.ReadUInt16();
                    _projectiles.Clear();
                    for (int i = 0; i < fxn; i++)
                        _projectiles.Add(new ProjectileView { Id = r.ReadInt32(), Position = Protocol.ReadVec(r), Cloud = r.ReadBoolean(), Radius = r.ReadSingle() });
                    break;
                }
                case MsgType.Drops:
                {
                    int n = r.ReadUInt16();
                    _drops.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var d = new DroppedItem { Id = r.ReadInt32() };
                        int item = r.ReadUInt16() - 1;
                        int count = r.ReadUInt16();
                        d.Stack = new ItemStack(item, count);
                        d.Position = Protocol.ReadVec(r);
                        d.Age = r.ReadInt32();
                        if (item >= 0) _drops.Add(d);
                    }
                    break;
                }
            }
        }

        private void HandleWelcome(BinaryReader r)
        {
            int id = r.ReadInt32();
            long seed = r.ReadInt64();
            World = new VoxelWorld(Content, seed, null, _workers) { Remote = this };
            Factory = new Factory(Content, World) { IsReplica = true };
            LocalPlayer = new PlayerState(id, _name, Content)
            {
                Position = Protocol.ReadVec(r),
                Yaw = r.ReadSingle(),
                Pitch = r.ReadSingle(),
                Flying = r.ReadBoolean(),
                SelectedSlot = r.ReadInt32(),
                Creative = r.ReadBoolean()
            };
            World.ChunkLoaded += OnChunkLoaded;
            World.PendingCanceled += coord => _bufferedChanges.Remove(coord); // устаревший буфер не применится поверх свежих данных

            // Чанки вокруг точки появления — сразу
            var c = WorldConstants.ChunkOf(LocalPlayer.Position.Floor());
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                World.Request(new Int3(c.X + dx, c.Y + dy, c.Z + dz));
            State = SessionState.Playing;
        }

        private void ApplyBlockChange(Int3 pos, ushort block)
        {
            var chunk = WorldConstants.ChunkOf(pos);
            if (World.TryGetChunk(chunk, out _))
            {
                World.SetBlock(pos, block);
            }
            else if (World.IsPending(chunk))
            {
                // Чанк ещё в пути/генерируется — применим после загрузки
                if (!_bufferedChanges.TryGetValue(chunk, out var list))
                    _bufferedChanges[chunk] = list = new List<(Int3, ushort)>();
                list.Add((pos, block));
            }
        }

        private void OnChunkLoaded(Chunk chunk)
        {
            if (!_bufferedChanges.TryGetValue(chunk.Coord, out var list)) return;
            _bufferedChanges.Remove(chunk.Coord);
            foreach (var (pos, block) in list) World.SetBlock(pos, block);
        }

        private void ReadPlayers(BinaryReader r)
        {
            int n = r.ReadByte();
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                string name = r.ReadString();
                var pos = Protocol.ReadVec(r);
                float yaw = r.ReadSingle(), pitch = r.ReadSingle();
                if (id == LocalPlayer.Id) continue;
                seen.Add(id);
                if (!_othersById.TryGetValue(id, out var v))
                {
                    v = new RemotePlayerView { Id = id, Name = name, Position = pos, PrevPosition = pos };
                    _othersById[id] = v;
                    _others.Add(v);
                }
                v.PrevPosition = v.Position;
                v.Position = pos;
                v.Yaw = yaw;
                v.Pitch = pitch;
                v.LastUpdate = _time;
            }
            _others.RemoveAll(v => !seen.Contains(v.Id));
            foreach (var id in new List<int>(_othersById.Keys))
                if (!seen.Contains(id)) _othersById.Remove(id);
        }

        // ============================================================ Исходящие

        public void RequestChunk(Int3 coord) => _chunkRequests.Add(coord);

        public void RequestChunkFull(Int3 coord) => _chunkRequestsFull.Add(coord);

        public void Send(GameCommand cmd)
        {
            if (State != SessionState.Playing) return;
            cmd.PlayerId = LocalPlayer.Id;

            if (cmd is PlayerMoveCommand move)
            {
                // Локально — сразу, хосту — не чаще 20 раз в секунду
                LocalPlayer.Position = move.Position;
                LocalPlayer.Yaw = move.Yaw;
                LocalPlayer.Pitch = move.Pitch;
                LocalPlayer.Flying = move.Flying;
                if (_time - _lastMoveSent < GameConstants.TickDuration) return;
                _lastMoveSent = _time;
            }
            else if (cmd is SelectSlotCommand sel)
            {
                LocalPlayer.SelectedSlot = sel.Slot;
            }

            var w = Protocol.Begin(MsgType.Command, out var ms);
            CommandCodec.Write(w, cmd);
            _transport.Send(Protocol.End(w, ms), true);
        }

        private void FlushChunkRequests()
        {
            int i = 0;
            while (i < _chunkRequests.Count)
            {
                int n = Math.Min(256, _chunkRequests.Count - i);
                var w = Protocol.Begin(MsgType.ChunkRequest, out var ms);
                w.Write((ushort)n);
                for (int k = 0; k < n; k++) Protocol.WritePos(w, _chunkRequests[i + k]);
                _transport.Send(Protocol.End(w, ms), true);
                i += n;
            }
            _chunkRequests.Clear();
            if (_chunkRequestsFull.Count > 0)
            {
                int n = Math.Min(64, _chunkRequestsFull.Count);
                var w = Protocol.Begin(MsgType.ChunkRequestFull, out var ms);
                w.Write((ushort)n);
                for (int k = 0; k < n; k++) Protocol.WritePos(w, _chunkRequestsFull[k]);
                _transport.Send(Protocol.End(w, ms), true);
                _chunkRequestsFull.RemoveRange(0, n);
            }
        }

        // ============================================================ Цикл

        public void Update(float dt, float time)
        {
            _time = time;
            _transport.Poll();
            if (State != SessionState.Playing) return;
            if (_time - _lastReceived > HostTimeout)
            {
                Error = "Host is not responding";
                State = SessionState.Failed;
                return;
            }
            if (_time - _lastPing > 2f)
            {
                _lastPing = _time;
                var pw = Protocol.Begin(MsgType.Ping, out var pms);
                _transport.Send(Protocol.End(pw, pms), true);
            }

            World.Pump();

            _accumulator += Math.Min(dt, 0.25f);
            while (_accumulator >= GameConstants.TickDuration)
            {
                Factory.Tick(); // в режиме реплики — только ленты
                _worldTime++;
                _accumulator -= GameConstants.TickDuration;
            }

            if (_frame++ % 15 == 0)
                World.UpdateLoadedArea(new[] { LocalPlayer.Position.Floor() }, ViewRadius, VerticalRadius);
            FlushChunkRequests();
        }

        public void Save(bool background = false)
        {
        }

        public void Dispose()
        {
            _transport.Dispose();
            World?.Dispose();
        }
    }
}
