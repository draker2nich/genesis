using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Net;
using Genesis.Simulation;
using Genesis.World;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Хост и клиент в одном процессе через loopback-транспорт: весь протокол без сети.</summary>
    public class NetTests
    {
        private GameServer _server;
        private LocalSession _host;
        private LoopbackServer _hub;
        private RemoteSession _client;
        private float _time;

        private static ContentDatabase Db => TestContent.Db;

        [SetUp]
        public void SetUp()
        {
            _server = GameServer.Open(Db, null, 4242, 0);
            _server.ViewRadius = 1;
            _server.VerticalRadius = 1;
            _host = new LocalSession(_server, "Host");
            _hub = new LoopbackServer();
            _host.StartHosting(_hub, "loopback");
            _time = 0;
        }

        [TearDown]
        public void TearDown()
        {
            _client?.Dispose();
            _host?.Dispose();
        }

        private RemoteSession Connect(string name, ContentDatabase content = null)
        {
            var c = new RemoteSession(content ?? Db, _hub.CreateClient(), name, 0) { ViewRadius = 1, VerticalRadius = 1 };
            for (int i = 0; i < 10 && c.State == SessionState.Connecting; i++) Step(c, 1);
            return c;
        }

        private void Step(RemoteSession c, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                _time += 0.05f;
                _host.Update(0.05f, _time);
                c.Update(0.05f, _time);
            }
        }

        private PlayerState ServerSideOf(RemoteSession c) => _server.GetPlayer(c.LocalPlayer.Id);

        [Test]
        public void ClientJoinsAndGetsSpawnChunks()
        {
            _client = Connect("Friend");
            Assert.AreEqual(SessionState.Playing, _client.State, _client.Error);
            Step(_client, 5);

            var sp = _server.World.Generator.SpawnPoint;
            Assert.IsTrue(_client.World.IsLoaded(sp.X, sp.Y, sp.Z));
            // Нетронутый чанк сгенерирован клиентом локально — совпадает с хостом
            for (int dy = -2; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                var p = sp + new Int3(dx, dy, 0);
                Assert.AreEqual(_server.World.GetBlock(p), _client.World.GetBlock(p), p.ToString());
            }
            Assert.AreEqual(1, _client.OtherPlayers.Count);
            Assert.AreEqual("Host", _client.OtherPlayers[0].Name);
            Assert.AreEqual(1, _host.OtherPlayers.Count);
        }

        [Test]
        public void HostBlockChangeReplicatesToClient()
        {
            _client = Connect("Friend");
            Step(_client, 5);
            var sp = _server.World.Generator.SpawnPoint;
            var pos = sp + new Int3(-2, 0, 0);
            ushort stone = Db.BlockId("genesis:stone");
            Assert.IsTrue(_server.World.SetBlock(pos, stone));
            Step(_client, 2);
            Assert.AreEqual(stone, _client.World.GetBlock(pos));
        }

        [Test]
        public void ClientCommandPlacesBlockOnHost()
        {
            _client = Connect("Friend");
            Step(_client, 5);
            int cobble = Db.ItemId("genesis:cobblestone");
            var serverPlayer = ServerSideOf(_client);
            serverPlayer.Inventory[0] = new ItemStack(cobble, 5);
            Step(_client, 3);
            Assert.AreEqual(5, _client.LocalPlayer.Inventory.CountOf(cobble), "inventory replicated to owner");

            var pos = _server.World.Generator.SpawnPoint + new Int3(2, 0, 2);
            _client.Send(new SelectSlotCommand { Slot = 0 });
            _client.Send(new PlaceBlockCommand { Pos = pos, LookFacing = Direction.North });
            Step(_client, 4);

            ushort block = Db.BlockId("genesis:cobblestone");
            Assert.AreEqual(block, _server.World.GetBlock(pos), "host applied the command");
            Assert.AreEqual(block, _client.World.GetBlock(pos), "change came back to the client");
            Assert.AreEqual(4, _client.LocalPlayer.Inventory.CountOf(cobble));
        }

        [Test]
        public void MachineStateReplicates()
        {
            _client = Connect("Friend");
            Step(_client, 5);
            _server.Knowledge.ForceUnlock("genesis:stone.smelt_iron"); // автоматизация плавки открыта
            var pos = _server.World.Generator.SpawnPoint + new Int3(3, 0, -3);
            _server.World.SetBlock(pos, Db.BlockId("genesis:stone_furnace"));
            _server.Factory.OnBlockPlaced(pos, Db.Blocks.Get("genesis:stone_furnace"), Direction.West);
            var furnace = _server.Factory.MachineAt(pos);
            _server.Factory.InsertIntoMachine(furnace, new ItemStack(Db.ItemId("genesis:iron_ore"), 3));
            _server.Factory.InsertIntoMachine(furnace, new ItemStack(Db.ItemId("genesis:coal"), 1));
            Step(_client, 10);

            var replica = _client.Factory.MachineAt(pos);
            Assert.IsNotNull(replica);
            Assert.AreEqual("genesis:stone_furnace", replica.Def.Id);
            Assert.AreEqual(Direction.West, replica.Facing);
            Assert.AreEqual(MachineStatus.Working, replica.Status);
            Assert.Greater(replica.Progress, 0f);
            Assert.AreEqual(2, replica.Input.CountOf(Db.ItemId("genesis:iron_ore")));

            // Удаление тоже реплицируется
            _server.Factory.OnBlockRemoved(pos);
            Step(_client, 2);
            Assert.IsNull(_client.Factory.MachineAt(pos));
        }

        [Test]
        public void ModifiedFarChunkIsSentAsData()
        {
            _client = Connect("Friend");
            Step(_client, 3);
            var far = new Int3(600, 250, 600);
            var coord = WorldConstants.ChunkOf(far);
            _server.World.LoadNow(coord);
            ushort stone = Db.BlockId("genesis:stone");
            _server.World.SetBlock(far, stone);

            _client.World.Request(coord);
            Step(_client, 3);
            Assert.IsTrue(_client.World.TryGetChunk(coord, out _));
            Assert.AreEqual(stone, _client.World.GetBlock(far));
        }

        [Test]
        public void ContentMismatchIsRejected()
        {
            var other = new ContentDatabase();
            other.LoadModDirectory(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Game"));
            other.AddItemJson("{\"id\":\"testmod:extra\"}");
            other.Freeze();

            _client = Connect("Stranger", other);
            Step(_client, 3);
            Assert.AreEqual(SessionState.Failed, _client.State);
            StringAssert.Contains("content", _client.Error);
        }

        [Test]
        public void ClientLeavingSavesItsPlayer()
        {
            _client = Connect("Friend");
            Step(_client, 3);
            Assert.AreEqual(2, new System.Collections.Generic.List<PlayerState>(_server.Players).Count);
            _client.Dispose();
            _client = Connect("Other");
            Step(_client, 3);
            Assert.AreEqual(2, new System.Collections.Generic.List<PlayerState>(_server.Players).Count);
        }

        [Test]
        public void KnowledgeAndVitalsReplicate()
        {
            _client = Connect("Friend");
            Step(_client, 3);
            _server.Knowledge.ForceUnlock("genesis:wood.milestone");
            var sp = ServerSideOf(_client);
            sp.Health = 9;
            Step(_client, 3);
            Assert.AreEqual(1, _client.Knowledge.Era);
            Assert.IsTrue(_client.Knowledge.IsUnlocked("genesis:stone.tools"));
            Assert.AreEqual(9f, _client.LocalPlayer.Health);

            _server.Kill(sp);
            Step(_client, 3);
            Assert.AreEqual(1, _client.LocalPlayer.TeleportVersion, "respawn reaches the client");
        }

        [Test]
        public void MeSnapshotReachesRemoteClient()
        {
            _client = Connect("Friend");
            Step(_client, 3);
            var pos = _server.World.Generator.SpawnPoint + new Int3(2, 0, 0);
            _server.Factory.OnBlockPlaced(pos, Db.Blocks.Get("genesis:me_controller"), Direction.North);
            _client.OpenMe(pos, false);
            Step(_client, 15);
            Assert.IsNotNull(_client.Me);
            Assert.IsTrue(_client.Me.Exists);
            Assert.AreEqual(pos, _client.Me.Access);

            _client.CloseMe();
            Step(_client, 2);
            Assert.IsNull(ServerSideOf(_client).MeView);
        }

        [Test]
        public void AllCommandsRoundTrip()
        {
            GameCommand[] cmds =
            {
                new PlayerMoveCommand { Position = new Vec3(1, 2, 3), Yaw = 45, Pitch = -10, Flying = true },
                new BreakBlockCommand { Pos = new Int3(1, -2, 3) },
                new PlaceBlockCommand { Pos = new Int3(4, 5, 6), LookFacing = Direction.East },
                new PutOnConveyorCommand { Pos = new Int3(7, 8, 9) },
                new RotateCommand { Pos = new Int3(1, 1, 1) },
                new SelectSlotCommand { Slot = 4 },
                new TakeFromMachineCommand { Pos = new Int3(2, 2, 2), Kind = MachineInventoryKind.Fuel, Slot = 0 },
                new PutIntoMachineCommand { Pos = new Int3(3, 3, 3), PlayerSlot = 12 },
                new SetSideCommand { Pos = new Int3(4, 4, 4), Side = RelativeSide.Top, Type = SideType.SteamOut },
                new SwapSlotsCommand { A = 1, B = 30 },
                new CreativeGiveCommand { Item = 5, Count = 64 },
                new DropSlotCommand { Slot = 3 },
                new CraftCommand { CraftId = 7, Station = new Int3(1, 2, 3), MinigameSuccess = true },
                new AttackCommand { EnemyId = 42 },
                new EatCommand(),
                new DamageSelfCommand { Amount = 3.5f },
                new CreativeAdvanceEraCommand(),
                new MeViewCommand { Access = new Int3(1, 2, 3), Wireless = true, Open = true },
                new MeExtractCommand { Access = new Int3(1, 2, 3), Item = 4, Count = 64 },
                new MeInsertCommand { Access = new Int3(1, 2, 3), PlayerSlot = 7 },
                new MeCraftCommand { Access = new Int3(1, 2, 3), Item = 4, Count = 10 },
                new SlotClickCommand { Container = SlotContainer.Grid, Slot = 4, Button = 1, Shift = true },
                new GridCraftCommand { Shift = true, MinigameSuccess = true, CraftId = 9 },
                new OpenGridCommand { Open = true, Size = 3, Station = new Int3(5, 6, 7) },
                new DropCursorCommand { All = false },
                new FillGridCommand { CraftId = 3, Max = true },
                new InteractCommand { CreatureId = 12 },
                new DropSlotCommand { Slot = 4, All = true },
                new MeStockCommand { Access = new Int3(1, 2, 3), Item = 4, Target = 500 },
                new MeCancelCommand { Access = new Int3(1, 2, 3) },
                new MeFilterCommand { Pos = new Int3(1, 2, 3), Slot = 2, Item = 9 },
                new MeFluidFilterCommand { Pos = new Int3(1, 2, 3), Fluid = 1 },
                new MePatternCommand { Pos = new Int3(1, 2, 3), Recipe = 5, Enabled = true },
                new MeLinkCommand { Controller = new Int3(1, 2, 3) },
                new SetRecipeCommand { Pos = new Int3(1, 2, 3), Recipe = 6 },
            };
            foreach (var cmd in cmds)
            {
                using (var ms = new MemoryStream())
                {
                    var w = new BinaryWriter(ms);
                    CommandCodec.Write(w, cmd);
                    w.Flush();
                    ms.Position = 0;
                    var back = CommandCodec.Read(new BinaryReader(ms));
                    Assert.AreEqual(cmd.GetType(), back.GetType());
                    Assert.AreEqual(ms.Length, ms.Position, cmd.GetType().Name + " read fully");
                }
            }
        }
    }
}
