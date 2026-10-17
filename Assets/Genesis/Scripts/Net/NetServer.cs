using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Net
{
    /// <summary>
    /// Сетевая часть хоста. Сервер авторитетен: применяет команды клиентов к GameServer и рассылает:
    ///  • изменения блоков (всем, сразу);
    ///  • чанки по запросу (нетронутые клиент генерирует сам по seed — экономия трафика);
    ///  • состояние машин и лент рядом с игроком (только изменившиеся, по хешу);
    ///  • позиции игроков и инвентарь владельцу.
    /// </summary>
    public sealed class NetServer : IDisposable
    {
        public const int MachineRadius = 24;
        public const int ConveyorRadius = 40;
        public const int MaxClients = 3; // + хост = 4 игрока

        private readonly GameServer _server;
        private readonly IServerTransport _transport;

        private sealed class Client
        {
            public int Connection;
            public PlayerState Player;
            public readonly Dictionary<Int3, ulong> SentMachines = new Dictionary<Int3, ulong>();
            public readonly Dictionary<Int3, ulong> SentConveyors = new Dictionary<Int3, ulong>();
            public ulong InventoryHash;
            public ulong VitalsHash;
            public bool HadEnemies;
            public bool HadDrops;
        }

        private readonly Dictionary<int, Client> _clients = new Dictionary<int, Client>();
        private long _lastTick = -1;
        private bool _knowledgeDirty;
        public const int EnemyRadius = 48;

        public int ClientCount => _clients.Count;
        public event Action<string> Log;

        public NetServer(GameServer server, IServerTransport transport)
        {
            _server = server;
            _transport = transport;
            _transport.ClientConnected += OnConnected;
            _transport.ClientDisconnected += OnDisconnected;
            _transport.Received += OnReceived;

            _server.World.BlockChanged += OnBlockChanged;
            _server.Factory.EntityRemoved += OnEntityRemoved;
            _server.Notify += OnNotify;
            _server.Knowledge.Changed += OnKnowledgeChanged;
        }

        private void OnKnowledgeChanged() => _knowledgeDirty = true;

        public void Dispose()
        {
            _server.World.BlockChanged -= OnBlockChanged;
            _server.Factory.EntityRemoved -= OnEntityRemoved;
            _server.Notify -= OnNotify;
            _server.Knowledge.Changed -= OnKnowledgeChanged;
            foreach (var c in _clients.Values) _server.Leave(c.Player.Id);
            _clients.Clear();
            _transport.Dispose();
        }

        // ============================================================ Соединения

        private void OnConnected(int conn)
        {
            Log?.Invoke($"connection {conn} opened");
        }

        private void OnDisconnected(int conn)
        {
            if (_clients.TryGetValue(conn, out var c))
            {
                Log?.Invoke($"{c.Player.Name} left");
                _server.Leave(c.Player.Id);
                _clients.Remove(conn);
            }
        }

        private void Reject(int conn, string reason)
        {
            var w = Protocol.Begin(MsgType.Reject, out var ms);
            w.Write(reason);
            _transport.Send(conn, Protocol.End(w, ms), true);
            _transport.Disconnect(conn);
        }

        private void OnReceived(int conn, byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms))
                {
                    var type = (MsgType)r.ReadByte();
                    if (type == MsgType.Hello)
                    {
                        HandleHello(conn, r);
                        return;
                    }
                    if (!_clients.TryGetValue(conn, out var client)) return;
                    switch (type)
                    {
                        case MsgType.Command:
                            var cmd = CommandCodec.Read(r);
                            cmd.PlayerId = client.Player.Id;
                            _server.Enqueue(cmd);
                            break;
                        case MsgType.ChunkRequest:
                            int n = r.ReadUInt16();
                            for (int i = 0; i < n; i++) SendChunk(client, Protocol.ReadPos(r));
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log?.Invoke($"bad packet from {conn}: {e.Message}");
            }
        }

        private void HandleHello(int conn, BinaryReader r)
        {
            int version = r.ReadInt32();
            ulong contentHash = r.ReadUInt64();
            string mods = r.ReadString();
            string name = r.ReadString().Trim();

            if (version != Protocol.Version) { Reject(conn, $"protocol {version} != {Protocol.Version}"); return; }
            if (contentHash != _server.Content.ContentHash)
            {
                Reject(conn, mods != _server.ModList
                    ? $"different mods. Host: [{_server.ModList}]. Yours: [{mods}]"
                    : "content mismatch (different game version)");
                return;
            }
            if (_clients.Count >= MaxClients) { Reject(conn, "server is full"); return; }
            if (name.Length == 0 || name.Length > 24) { Reject(conn, "bad name"); return; }
            foreach (var p in _server.Players)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { Reject(conn, "name is taken"); return; }

            var player = _server.Join(name);
            var client = new Client { Connection = conn, Player = player };
            _clients[conn] = client;

            var w = Protocol.Begin(MsgType.Welcome, out var ms);
            w.Write(player.Id);
            w.Write(_server.Seed);
            Protocol.WriteVec(w, player.Position);
            w.Write(player.Yaw);
            w.Write(player.Pitch);
            w.Write(player.Flying);
            w.Write(player.SelectedSlot);
            _transport.Send(conn, Protocol.End(w, ms), true);

            SendInventory(client, true);
            SendVitals(client, true);
            _transport.Send(conn, KnowledgePacket(), true);
            _transport.Send(conn, TimePacket(), true);
            Log?.Invoke($"{name} joined");
        }

        // ============================================================ Чанки и блоки

        private void SendChunk(Client client, Int3 coord)
        {
            if (!WorldConstants.ChunkInBounds(coord)) return;
            var world = _server.World;
            byte[] payload = null;

            if (world.TryGetChunk(coord, out var chunk))
            {
                if (chunk.Modified)
                {
                    using (var cms = new MemoryStream())
                    using (var cw = new BinaryWriter(cms))
                    {
                        ChunkCodec.Write(cw, chunk, _server.Content);
                        cw.Flush();
                        payload = cms.ToArray();
                    }
                }
            }
            else if (world.Store.TryGetRaw(coord, out var raw))
            {
                payload = raw;
            }

            if (payload == null)
            {
                var w = Protocol.Begin(MsgType.ChunkPristine, out var ms);
                Protocol.WritePos(w, coord);
                _transport.Send(client.Connection, Protocol.End(w, ms), true);
            }
            else
            {
                var w = Protocol.Begin(MsgType.ChunkData, out var ms);
                Protocol.WritePos(w, coord);
                w.Write(payload.Length);
                w.Write(payload);
                _transport.Send(client.Connection, Protocol.End(w, ms), true);
            }
        }

        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock)
        {
            if (_clients.Count == 0) return;
            var w = Protocol.Begin(MsgType.BlockChange, out var ms);
            Protocol.WritePos(w, pos);
            w.Write(newBlock);
            Broadcast(Protocol.End(w, ms));
        }

        private void OnEntityRemoved(Int3 pos)
        {
            if (_clients.Count == 0) return;
            foreach (var c in _clients.Values)
            {
                c.SentMachines.Remove(pos);
                c.SentConveyors.Remove(pos);
            }
            var w = Protocol.Begin(MsgType.EntityRemoved, out var ms);
            Protocol.WritePos(w, pos);
            Broadcast(Protocol.End(w, ms));
        }

        private void OnNotify(int playerId, string key)
        {
            foreach (var c in _clients.Values)
            {
                if (c.Player.Id != playerId) continue;
                var w = Protocol.Begin(MsgType.Toast, out var ms);
                w.Write(key);
                _transport.Send(c.Connection, Protocol.End(w, ms), true);
            }
        }

        private void Broadcast(byte[] data)
        {
            foreach (var c in _clients.Values) _transport.Send(c.Connection, data, true);
        }

        // ============================================================ Периодическая репликация

        /// <summary>Вызывать каждый кадр после GameServer.Update.</summary>
        public void Update()
        {
            _transport.Poll();
            long tick = _server.Factory.TickCount;
            if (tick == _lastTick || _clients.Count == 0)
            {
                _lastTick = tick;
                return;
            }
            _lastTick = tick;

            SendPlayers();
            if (tick % 100 == 0) Broadcast(TimePacket());
            if (_knowledgeDirty)
            {
                _knowledgeDirty = false;
                Broadcast(KnowledgePacket());
            }
            foreach (var c in _clients.Values)
            {
                if (tick % 10 == 4 && c.Player.MeView.HasValue) SendMe(c);
                SendVitals(c, false);
                if (tick % 2 == 0) SendEnemies(c);
                if (tick % 2 == 1) SendDrops(c);
                if (tick % 2 == 0) SendInventory(c, false);
                if (tick % 2 == 0) SendConveyors(c);
                if (tick % 4 == 1) SendMachines(c);
            }
        }

        private void SendPlayers()
        {
            var w = Protocol.Begin(MsgType.Players, out var ms);
            var list = new List<PlayerState>(_server.Players);
            w.Write((byte)list.Count);
            foreach (var p in list)
            {
                w.Write(p.Id);
                w.Write(p.Name ?? "");
                Protocol.WriteVec(w, p.Position);
                w.Write(p.Yaw);
                w.Write(p.Pitch);
            }
            Broadcast(Protocol.End(w, ms));
        }

        private void SendInventory(Client c, bool force)
        {
            byte[] body;
            using (var bms = new MemoryStream())
            using (var bw = new BinaryWriter(bms))
            {
                ReplicationCodec.WritePlayerItems(bw, c.Player);
                bw.Flush();
                body = bms.ToArray();
            }
            ulong h = Protocol.HashBytes(body);
            if (!force && h == c.InventoryHash) return;
            c.InventoryHash = h;
            var w = Protocol.Begin(MsgType.Inventory, out var ms);
            w.Write(body);
            _transport.Send(c.Connection, Protocol.End(w, ms), true);
        }

        private void SendMe(Client c)
        {
            var snap = _server.Factory.Me.Snapshot(c.Player.MeView.Value);
            var w = Protocol.Begin(MsgType.MeSnapshot, out var ms);
            snap.Write(w);
            _transport.Send(c.Connection, Protocol.End(w, ms), true);
        }

        private byte[] TimePacket()
        {
            var w = Protocol.Begin(MsgType.Time, out var ms);
            w.Write(_server.WorldTime);
            return Protocol.End(w, ms);
        }

        private byte[] KnowledgePacket()
        {
            var w = Protocol.Begin(MsgType.Knowledge, out var ms);
            var k = _server.Knowledge;
            w.Write(k.Era);
            var ids = new List<string>(k.UnlockedIds);
            w.Write(ids.Count);
            foreach (var id in ids) w.Write(id);
            return Protocol.End(w, ms);
        }

        private void SendVitals(Client c, bool force)
        {
            var p = c.Player;
            byte[] body;
            using (var bms = new MemoryStream())
            using (var bw = new BinaryWriter(bms))
            {
                bw.Write(p.Health);
                bw.Write(p.Hunger);
                bw.Write(p.TeleportVersion);
                bw.Write(p.LastDamageTick);
                bw.Write((short)p.PoisonTicks);
                bw.Write(p.KnockbackVersion);
                Protocol.WriteVec(bw, p.Knockback);
                bw.Flush();
                body = bms.ToArray();
            }
            ulong h = Protocol.HashBytes(body);
            if (!force && h == c.VitalsHash) return;
            c.VitalsHash = h;
            var w = Protocol.Begin(MsgType.Vitals, out var ms);
            w.Write(body);
            Protocol.WriteVec(w, p.Position); // нужна клиенту при возрождении (TeleportVersion изменился)
            _transport.Send(c.Connection, Protocol.End(w, ms), true);
        }

        private void SendDrops(Client c)
        {
            var list = new List<DroppedItem>();
            foreach (var d in _server.Drops.All)
                if ((d.Position - c.Player.Position).LengthSquared <= EnemyRadius * EnemyRadius && list.Count < 400) list.Add(d);
            if (list.Count == 0 && !c.HadDrops) return;
            c.HadDrops = list.Count > 0;
            var w = Protocol.Begin(MsgType.Drops, out var ms);
            w.Write((ushort)list.Count);
            foreach (var d in list)
            {
                w.Write(d.Id);
                w.Write((ushort)(d.Stack.Item + 1));
                w.Write((ushort)d.Stack.Count);
                Protocol.WriteVec(w, d.Position);
                w.Write(d.Age);
            }
            _transport.Send(c.Connection, Protocol.End(w, ms), false);
        }

        private void SendEnemies(Client c)
        {
            var list = new List<Enemy>();
            foreach (var e in _server.Enemies.All)
                if ((e.Position - c.Player.Position).LengthSquared <= EnemyRadius * EnemyRadius) list.Add(e);
            bool fx = _server.Enemies.Projectiles.Count > 0 || _server.Enemies.Clouds.Count > 0;
            if (list.Count == 0 && !c.HadEnemies && !fx) return;
            c.HadEnemies = list.Count > 0 || fx;
            var w = Protocol.Begin(MsgType.Enemies, out var ms);
            w.Write((ushort)list.Count);
            foreach (var e in list)
            {
                w.Write(e.Id);
                w.Write((ushort)e.Def.NumericId);
                Protocol.WriteVec(w, e.Position);
                w.Write(e.Yaw);
                w.Write(e.Health);
                w.Write((byte)e.HurtTicks);
                w.Write((byte)e.State);
            }
            // Снаряды и облака спор
            var fxList = new List<ProjectileView>();
            foreach (var p in _server.Enemies.Projectiles)
                if ((p.Position - c.Player.Position).LengthSquared < EnemyRadius * EnemyRadius) fxList.Add(new ProjectileView { Id = p.Id, Position = p.Position });
            foreach (var cl in _server.Enemies.Clouds)
                if ((cl.Position - c.Player.Position).LengthSquared < EnemyRadius * EnemyRadius) fxList.Add(new ProjectileView { Id = cl.Id, Position = cl.Position, Cloud = true, Radius = cl.Radius });
            w.Write((ushort)fxList.Count);
            foreach (var f in fxList)
            {
                w.Write(f.Id);
                Protocol.WriteVec(w, f.Position);
                w.Write(f.Cloud);
                w.Write(f.Radius);
            }
            _transport.Send(c.Connection, Protocol.End(w, ms), false);
        }

        private static bool Near(Int3 a, Vec3 b, int radius)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz <= radius * radius;
        }

        private void SendMachines(Client c)
        {
            var pos = c.Player.Position;
            foreach (var m in _server.Factory.Machines)
            {
                if (!Near(m.Pos, pos, MachineRadius)) continue;
                byte[] body;
                using (var bms = new MemoryStream())
                using (var bw = new BinaryWriter(bms))
                {
                    ReplicationCodec.WriteMachine(bw, m);
                    bw.Flush();
                    body = bms.ToArray();
                }
                ulong h = Protocol.HashBytes(body);
                if (c.SentMachines.TryGetValue(m.Pos, out var old) && old == h) continue;
                c.SentMachines[m.Pos] = h;
                var w = Protocol.Begin(MsgType.Machine, out var ms);
                w.Write(body);
                _transport.Send(c.Connection, Protocol.End(w, ms), true);
            }
        }

        private void SendConveyors(Client c)
        {
            var pos = c.Player.Position;
            foreach (var conv in _server.Factory.Conveyors)
            {
                if (!Near(conv.Pos, pos, ConveyorRadius)) continue;
                byte[] body;
                using (var bms = new MemoryStream())
                using (var bw = new BinaryWriter(bms))
                {
                    ReplicationCodec.WriteConveyor(bw, conv);
                    bw.Flush();
                    body = bms.ToArray();
                }
                ulong h = Protocol.HashBytes(body);
                if (c.SentConveyors.TryGetValue(conv.Pos, out var old) && old == h) continue;
                c.SentConveyors[conv.Pos] = h;
                var w = Protocol.Begin(MsgType.Conveyor, out var ms);
                w.Write(body);
                _transport.Send(c.Connection, Protocol.End(w, ms), true);
            }
        }
    }
}
