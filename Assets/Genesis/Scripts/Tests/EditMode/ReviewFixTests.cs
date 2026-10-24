using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Регрессии по ревью MVP: Meta на лентах, апгрейд ячеек, дюп лута, выбор рецепта, буры, сохранения.</summary>
    public class ReviewFixTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private static int Item(string id) => Db.ItemId("genesis:" + id);

        private static MachineInstance Place(Factory f, string id, Int3 pos, Direction facing)
        {
            f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:" + id), facing);
            return f.MachineAt(pos);
        }

        // ------------------------------------------------------------ 1.1 Meta едет по ленте

        [Test]
        public void ConveyorKeepsToolWear()
        {
            var f = new Factory(Db, null);
            f.OnBlockPlaced(new Int3(0, 0, 0), Db.Blocks.Get("genesis:conveyor"), Direction.East);
            Place(f, "inserter", new Int3(1, 0, 0), Direction.East);
            var chest = Place(f, "chest", new Int3(2, 0, 0), Direction.North);
            f.ConveyorAt(Int3.Zero).Accept(Item("iron_pickaxe"), 0.5f, 0.5f, 37);

            for (int i = 0; i < 120; i++) f.Tick();
            Assert.AreEqual(1, chest.Input.CountOf(Item("iron_pickaxe")));
            foreach (var st in chest.Input.NonEmpty())
                Assert.AreEqual(37, st.Meta, "worn pickaxe must stay worn after the belt");
        }

        [Test]
        public void ConveyorMetaSurvivesSave()
        {
            var f = new Factory(Db, null);
            f.OnBlockPlaced(new Int3(0, 0, 0), Db.Blocks.Get("genesis:conveyor"), Direction.East);
            f.ConveyorAt(Int3.Zero).Accept(Item("cell_1k"), 0.4f, 0.4f, 12);
            var ms = new MemoryStream();
            f.Write(new BinaryWriter(ms));
            ms.Position = 0;
            var g = new Factory(Db, null);
            g.Read(new BinaryReader(ms));
            Assert.AreEqual(12, g.ConveyorAt(Int3.Zero).Items[0].Meta);
        }

        // ------------------------------------------------------------ 1.4 Выбор рецепта

        [Test]
        public void LockedRecipeIsTheOnlyOneMade()
        {
            var f = new Factory(Db, null);
            var m = Place(f, "auto_assembler", Int3.Zero, Direction.North);
            var recipes = Db.RecipesFor(m.Def.NumericId);
            Assume.That(recipes.Count, Is.GreaterThan(1));
            var locked = recipes[recipes.Count - 1];
            Assert.IsTrue(f.SetLockedRecipe(Int3.Zero, locked));

            // Сырьё и для другого рецепта тоже лежит — машина всё равно делает выбранный
            foreach (var i in locked.Inputs) m.Input.Insert(new ItemStack(i.ItemId, i.Count));
            foreach (var i in recipes[0].Inputs) m.Input.Insert(new ItemStack(i.ItemId, i.Count));
            for (int t = 0; t < 4000 && m.LastRecipe == null; t++)
            {
                m.EnergyStored = m.EnergyCapacity;
                f.Tick();
            }
            Assert.AreEqual(locked, m.LastRecipe);

            // Лента/манипулятор не кладут то, что выбранному рецепту не нужно
            int foreign = -1;
            foreach (var i in recipes[0].Inputs)
            {
                bool used = false;
                foreach (var j in locked.Inputs) used |= j.ItemId == i.ItemId;
                if (!used) foreign = i.ItemId;
            }
            if (foreign >= 0) Assert.IsFalse(f.AcceptsAsInput(m, foreign));
        }

        // ------------------------------------------------------------ 1.5 Залежь бура в сохранении

        [Test]
        public void DrillRemembersDepositWithoutItsChunk()
        {
            var world = new VoxelWorld(Db, 7, null, 0);
            var pos = new Int3(200, 300, 200);
            world.LoadNow(WorldConstants.ChunkOf(pos));
            world.LoadNow(WorldConstants.ChunkOf(pos + Int3.Down));
            world.SetBlock(pos + Int3.Down, Db.BlockId("genesis:iron_ore_rich"));
            var f = new Factory(Db, world);
            var drill = Place(f, "steam_drill", pos, Direction.North);
            for (int i = 0; i < 5; i++) f.Tick();
            Assume.That(drill.DepositItem, Is.GreaterThanOrEqualTo(0));

            var ms = new MemoryStream();
            f.Write(new BinaryWriter(ms));
            ms.Position = 0;
            var g = new Factory(Db, new VoxelWorld(Db, 7, null, 0)); // чанки не загружены
            g.Read(new BinaryReader(ms));
            var d2 = g.MachineAt(pos);
            Assert.AreEqual(drill.DepositItem, d2.DepositItem);
            Assert.AreEqual(drill.DepositPurity, d2.DepositPurity);
            for (int i = 0; i < 200; i++)
            {
                d2.Tank("steam").Fill(Db.FluidId("genesis:steam"), 8);
                g.Tick();
            }
            Assert.Greater(d2.Output.CountOf(d2.DepositItem), 0, "far-away drill keeps mining after load");
        }

        // ------------------------------------------------------------ 1.6 Повреждённый регион

        [Test]
        public void DamagedRegionIsRestoredFromBackupAndKept()
        {
            string dir = Path.Combine(Path.GetTempPath(), "genesis_region_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var pos = new Int3(3, 70, 3);
                var w1 = new VoxelWorld(Db, 7, dir, 0);
                w1.LoadNow(WorldConstants.ChunkOf(pos));
                w1.SetBlock(pos, Db.BlockId("genesis:planks"));
                w1.Save();
                w1.SetBlock(pos + Int3.Up, Db.BlockId("genesis:planks"));
                w1.Save(); // вторая запись: прежняя версия уходит в .bak
                w1.Dispose();

                string region = Directory.GetFiles(dir, "*.gcr")[0];
                File.WriteAllBytes(region, new byte[] { 1, 2, 3, 4, 5 }); // «сбой питания»

                var w2 = new VoxelWorld(Db, 7, dir, 0);
                w2.LoadNow(WorldConstants.ChunkOf(pos));
                Assert.AreEqual(Db.BlockId("genesis:planks"), w2.GetBlock(pos), "restored from .bak");
                Assert.AreEqual(1, Directory.GetFiles(dir, "*.corrupt-*").Length, "damaged file is kept aside, not overwritten");
                w2.Dispose();
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ------------------------------------------------------------ 1.7 Атомарная запись и транзакция

        [Test]
        public void InterruptedSaveIsFinishedOnLoad()
        {
            string dir = Path.Combine(Path.GetTempPath(), "genesis_tx_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "a.dat"), "old-a");
                File.WriteAllText(Path.Combine(dir, "b.dat"), "old-b");
                // Как будто упали посреди коммита: журнал есть, a уже подменён, b — ещё во временном файле
                File.WriteAllText(Path.Combine(dir, "a.dat"), "new-a");
                File.WriteAllText(Path.Combine(dir, "b.dat" + SafeFile.TmpExt), "new-b");
                File.WriteAllLines(Path.Combine(dir, SaveTransaction.JournalName), new[] { "a.dat", "b.dat" });

                SaveTransaction.Recover(dir);
                Assert.AreEqual("new-a", File.ReadAllText(Path.Combine(dir, "a.dat")));
                Assert.AreEqual("new-b", File.ReadAllText(Path.Combine(dir, "b.dat")));
                Assert.IsFalse(File.Exists(Path.Combine(dir, SaveTransaction.JournalName)));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void DamagedFactoryFileFallsBackToBackup()
        {
            string dir = Path.Combine(Path.GetTempPath(), "genesis_bak_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var a = GameServer.Open(Db, dir, 5, 0);
                var pa = a.Join("Saver");
                var chestPos = pa.Position.Floor() + new Int3(1, 0, 0);
                a.World.SetBlock(chestPos, Db.BlockId("genesis:chest"));
                a.Factory.OnBlockPlaced(chestPos, Db.Blocks.Get("genesis:chest"), Direction.North);
                a.Save();
                a.Save(); // есть и основной файл, и .bak
                a.Dispose();
                File.WriteAllBytes(Path.Combine(dir, "factory.dat"), new byte[] { 9, 9, 9 });

                var b = GameServer.Open(Db, dir, 5, 0);
                Assert.IsNotNull(b.Factory.MachineAt(chestPos), "factory restored from factory.dat.bak");
                b.Dispose();
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>Сервер: дюп лута, апгрейд ячеек, смерть, токены, настройки мира.</summary>
    public class ReviewServerTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private GameServer _server;
        private PlayerState _p;

        [SetUp]
        public void SetUp()
        {
            _server = GameServer.Open(Db, null, 4243, 0);
            _server.ViewRadius = 1;
            _server.VerticalRadius = 1;
            _server.Enemies.SpawningEnabled = false;
            _p = _server.Join("tester");
            foreach (var n in Db.Knowledge.Items) _server.Knowledge.ForceUnlock(n.Id);
            _p.Inventory.Clear();
        }

        [TearDown]
        public void TearDown() => _server.Dispose();

        private ItemStack S(string id, int count, int meta = 0) => new ItemStack(Db.ItemId("genesis:" + id), count, meta);

        // ------------------------------------------------------------ 1.3

        [Test]
        public void SpammingAttacksDropsLootOnce()
        {
            var def = Db.Enemies.Get("genesis:mossback");
            var e = _server.Enemies.Spawn(def, _p.Position + new Vec3(1.2f, 0, 0));
            e.Health = 0.5f;
            for (int i = 0; i < 20; i++) _server.Attack(_p, e.Id); // 20 пакетов в одном кадре
            Assert.LessOrEqual(_server.Drops.Count, def.Drops.Count, "loot is rolled once");
        }

        [Test]
        public void ServerEnforcesAttackCooldown()
        {
            var def = Db.Enemies.Get("genesis:mossback");
            var e = _server.Enemies.Spawn(def, _p.Position + new Vec3(1.2f, 0, 0));
            float before = e.Health;
            _server.Attack(_p, e.Id);
            float afterOne = e.Health;
            for (int i = 0; i < 10; i++) _server.Attack(_p, e.Id);
            Assert.AreEqual(afterOne, e.Health, 0.001f, "extra hits within the cooldown are ignored");
            Assert.Less(afterOne, before);
        }

        // ------------------------------------------------------------ 1.2

        private Int3 PlaceWorkbench()
        {
            var pos = _p.Position.Floor() + new Int3(1, 0, 0);
            var wb = Db.Blocks.Get("genesis:workbench");
            _server.World.SetBlock(pos, wb.NumericId);
            _server.Factory.OnBlockPlaced(pos, wb, Direction.North);
            _server.OpenGrid(_p, 3, pos);
            return pos;
        }

        [Test]
        public void CellUpgradeCarriesContents()
        {
            PlaceWorkbench();
            var cell = _server.Factory.Me.CreateCell(false, 1024);
            int stone = Db.ItemId("genesis:stone");
            cell.Insert(stone, 100, false);
            _p.CraftGrid[0] = S("cell_1k", 1, cell.Id);
            _p.CraftGrid[1] = S("logic_processor", 1);
            _p.CraftGrid[2] = S("cell_1k", 1);
            _p.CraftGrid[4] = S("cell_1k", 1);
            Assert.AreEqual(1, _server.CraftFromGrid(_p, false, false, -1));
            Assert.AreEqual(Db.ItemId("genesis:cell_4k"), _p.Cursor[0].Item);
            Assert.AreEqual(cell.Id, _p.Cursor[0].Meta, "new cell keeps the storage id");
            Assert.AreEqual(100, _server.Factory.Me.Cell(cell.Id).Count(stone));
        }

        [Test]
        public void TwoFilledCellsAreNotBurned()
        {
            PlaceWorkbench();
            int stone = Db.ItemId("genesis:stone");
            var a = _server.Factory.Me.CreateCell(false, 1024);
            var b = _server.Factory.Me.CreateCell(false, 1024);
            a.Insert(stone, 10, false);
            b.Insert(stone, 10, false);
            _p.CraftGrid[0] = S("cell_1k", 1, a.Id);
            _p.CraftGrid[1] = S("logic_processor", 1);
            _p.CraftGrid[2] = S("cell_1k", 1, b.Id);
            _p.CraftGrid[4] = S("cell_1k", 1);
            Assert.AreEqual(0, _server.CraftFromGrid(_p, false, false, -1));
            Assert.AreEqual(a.Id, _p.CraftGrid[0].Meta);
            Assert.AreEqual(b.Id, _p.CraftGrid[2].Meta);
        }

        // ------------------------------------------------------------ 2.3 Смерть

        [Test]
        public void DeathKeepsGridAndCursorItems()
        {
            _p.Inventory.Insert(S("planks", 10));
            _p.CraftGrid[0] = S("oak_log", 3);
            _p.Cursor[0] = S("stick", 5);
            var deathPos = _p.Position;
            _server.Kill(_p);
            Assert.IsTrue(_p.Inventory.IsEmpty && _p.CraftGrid[0].IsEmpty && _p.Cursor[0].IsEmpty);

            int logs = 0, sticks = 0, planks = 0;
            foreach (var m in _server.Factory.Machines)
            {
                if (m.Def.Id != "genesis:grave") continue;
                logs += m.Input.CountOf(Db.ItemId("genesis:oak_log"));
                sticks += m.Input.CountOf(Db.ItemId("genesis:stick"));
                planks += m.Input.CountOf(Db.ItemId("genesis:planks"));
            }
            foreach (var d in _server.Drops.All)
            {
                if (d.Stack.Item == Db.ItemId("genesis:oak_log")) logs += d.Stack.Count;
                if (d.Stack.Item == Db.ItemId("genesis:stick")) sticks += d.Stack.Count;
                if (d.Stack.Item == Db.ItemId("genesis:planks")) planks += d.Stack.Count;
            }
            Assert.AreEqual(3, logs);
            Assert.AreEqual(5, sticks);
            Assert.AreEqual(10, planks);
            _ = deathPos;
        }

        [Test]
        public void GraveDropsNothingWhenBroken()
        {
            Assert.AreEqual("none", Db.Blocks.Get("genesis:grave").Drop);
        }

        // ------------------------------------------------------------ 2.1 / 2.12

        [Test]
        public void CreativeIsOffByDefaultAndFlyingIsRejected()
        {
            Assert.IsFalse(_p.Creative);
            new PlayerMoveCommand { Position = _p.Position, Flying = true }.Apply(_server, _p);
            Assert.IsFalse(_p.Flying);
            new CreativeGiveCommand { Item = Db.ItemId("genesis:iron_ingot"), Count = 5 }.Apply(_server, _p);
            Assert.AreEqual(0, _p.Inventory.CountOf(Db.ItemId("genesis:iron_ingot")));
        }

        [Test]
        public void SavedPlayerBelongsToItsToken()
        {
            var a = _server.Join("Friend", "token-A");
            _server.Leave(a.Id);
            Assert.IsNull(_server.Join("Friend", "token-B"), "someone else can't take the name and inventory");
            Assert.IsNotNull(_server.Join("Friend", "token-A"));
        }
    }
}
