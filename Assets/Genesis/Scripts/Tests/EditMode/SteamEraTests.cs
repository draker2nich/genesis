using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Этап 4: бур на залежи, манипулятор, умножение руды ×2, бак.</summary>
    public class SteamEraTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private static int Item(string id) => Db.ItemId("genesis:" + id);

        private static MachineInstance Place(Factory f, string id, Int3 pos, Direction facing)
        {
            f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:" + id), facing);
            return f.MachineAt(pos);
        }

        private static void Steam(MachineInstance m, int amount) =>
            m.Tank("steam").Fill(Db.FluidId("genesis:steam"), amount);

        private static int DrillFor(string depositBlock, int ticks)
        {
            var world = new VoxelWorld(Db, 7, null, 0);
            var pos = new Int3(200, 300, 200);
            world.LoadNow(WorldConstants.ChunkOf(pos));
            world.LoadNow(WorldConstants.ChunkOf(pos + Int3.Down));
            world.SetBlock(pos + Int3.Down, Db.BlockId("genesis:" + depositBlock));
            var f = new Factory(Db, world);
            var drill = Place(f, "steam_drill", pos, Direction.North);
            for (int i = 0; i < ticks; i++)
            {
                Steam(drill, 8);
                f.Tick();
            }
            return drill.Output.CountOf(drill.DepositItem >= 0 ? drill.DepositItem : 0);
        }

        [Test]
        public void DrillMinesDepositScaledByPurity()
        {
            int rich = DrillFor("iron_ore_rich", 400);   // 400 тиков × 2 / 40 = 20
            int normal = DrillFor("iron_ore_normal", 400); // 10
            int poor = DrillFor("iron_ore_poor", 400);   // 5
            Assert.AreEqual(20, rich, 1);
            Assert.AreEqual(10, normal, 1);
            Assert.AreEqual(5, poor, 1);
        }

        [Test]
        public void DrillOnPlainStoneReportsNoDeposit()
        {
            var world = new VoxelWorld(Db, 7, null, 0);
            var pos = new Int3(200, 300, 200);
            world.LoadNow(WorldConstants.ChunkOf(pos));
            world.SetBlock(pos + Int3.Down, Db.BlockId("genesis:stone"));
            var f = new Factory(Db, world);
            var drill = Place(f, "steam_drill", pos, Direction.North);
            Steam(drill, 1000);
            for (int i = 0; i < 10; i++) f.Tick();
            Assert.AreEqual(MachineStatus.NoDeposit, drill.Status);
            Assert.IsTrue(drill.Output.IsEmpty);
        }

        [Test]
        public void InserterMovesOnlyWhatTargetAccepts()
        {
            var f = new Factory(Db, null);
            var chest = Place(f, "chest", new Int3(0, 0, 0), Direction.North);
            var arm = Place(f, "inserter", new Int3(1, 0, 0), Direction.East); // сзади сундук, спереди горн
            var furnace = Place(f, "stone_furnace", new Int3(2, 0, 0), Direction.North);
            chest.Input.Insert(new ItemStack(Item("stone"), 5));     // горн это не берёт
            chest.Input.Insert(new ItemStack(Item("iron_ore"), 3));

            for (int i = 0; i < 200; i++) f.Tick();

            Assert.AreEqual(3, furnace.Input.CountOf(Item("iron_ore")));
            Assert.AreEqual(5, chest.Input.CountOf(Item("stone")), "manipulator does not grab what it cannot put");
            Assert.IsTrue(arm.Input[0].IsEmpty);
        }

        [Test]
        public void InserterTakesFromConveyorAndFeedsChest()
        {
            var f = new Factory(Db, null);
            f.OnBlockPlaced(new Int3(0, 0, 0), Db.Blocks.Get("genesis:conveyor"), Direction.East);
            var arm = Place(f, "inserter", new Int3(1, 0, 0), Direction.East);
            var chest = Place(f, "chest", new Int3(2, 0, 0), Direction.North);
            var belt = f.ConveyorAt(Int3.Zero);
            belt.Accept(Item("coal"), 0.5f, 0.5f);
            belt.Accept(Item("coal"), 0.2f, 0.2f);

            for (int i = 0; i < 120; i++) f.Tick();
            Assert.AreEqual(2, chest.Input.CountOf(Item("coal")));
            Assert.AreEqual(0, belt.Items.Count);
        }

        /// <summary>Дробление → промывка → плавка: из одной руды два слитка.</summary>
        [Test]
        public void OreDoublingChain()
        {
            var f = new Factory(Db, null);
            var crusher = Place(f, "steam_crusher", new Int3(0, 0, 0), Direction.North);
            var washer = Place(f, "steam_washer", new Int3(5, 0, 0), Direction.North);
            var furnace = Place(f, "stone_furnace", new Int3(10, 0, 0), Direction.North);

            Steam(crusher, 2000);
            f.InsertIntoMachine(crusher, new ItemStack(Item("iron_ore"), 1));
            for (int i = 0; i < 100; i++) f.Tick();
            int crushed = crusher.Output.CountOf(Item("crushed_iron_ore"));
            Assert.AreEqual(2, crushed);

            Steam(washer, 2000);
            washer.Tank("water").Fill(Db.FluidId("genesis:water"), 4000);
            crusher.Output.Extract(Item("crushed_iron_ore"), crushed);
            f.InsertIntoMachine(washer, new ItemStack(Item("crushed_iron_ore"), crushed));
            for (int i = 0; i < 100; i++) f.Tick();
            int purified = washer.Output.CountOf(Item("purified_iron_ore"));
            Assert.AreEqual(2, purified);
            Assert.AreEqual(4000 - 200, washer.Tank("water").Amount, "100 water per wash");

            f.InsertIntoMachine(furnace, new ItemStack(Item("purified_iron_ore"), purified));
            f.InsertIntoMachine(furnace, new ItemStack(Item("coal"), 1));
            for (int i = 0; i < 340; i++) f.Tick();
            Assert.AreEqual(2, furnace.Output.CountOf(Item("iron_ingot")), "×2 per ore");
        }

        [Test]
        public void CrushedOreCannotBeSmeltedDirectly()
        {
            var f = new Factory(Db, null);
            var furnace = Place(f, "stone_furnace", Int3.Zero, Direction.North);
            Assert.AreEqual(0, f.InsertIntoMachine(furnace, new ItemStack(Item("crushed_iron_ore"), 1)),
                "washing is a required step of the ×2 chain");
        }

        [Test]
        public void FluidTankStoresAndFeedsSteam()
        {
            var f = new Factory(Db, null);
            var tank = Place(f, "fluid_tank", new Int3(0, 0, 0), Direction.North); // спереди (+Z) — выход
            int steam = Db.FluidId("genesis:steam");
            Assert.AreEqual(1000, f.FillMachine(tank, steam, 1000, false), "tank accepts fill");
            var hammer = Place(f, "steam_hammer", new Int3(0, 0, 1), Direction.East); // к баку — правая грань (steam_in)
            for (int i = 0; i < 5; i++) f.Tick();
            Assert.Greater(hammer.Tank("steam").Amount, 0);
            Assert.Less(tank.Tank("tank").Amount, 1000);
        }

        [Test]
        public void InserterIsPlacedFacingAwayFromPlayer()
        {
            var server = GameServer.Open(Db, null, 11, 0);
            var p = server.Join("t");
            p.Inventory[0] = new ItemStack(Item("inserter"), 1);
            p.SelectedSlot = 0;
            var pos = server.World.Generator.SpawnPoint + new Int3(2, 0, 0);
            Assert.IsTrue(server.TryPlaceSelected(p, pos, Direction.East));
            Assert.AreEqual(Direction.East, server.Factory.MachineAt(pos).Facing);
            server.Dispose();
        }
    }
}
