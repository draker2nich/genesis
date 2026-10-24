using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;
using NUnit.Framework;

namespace Genesis.Tests
{
    public class SimulationTests
    {
        private static ContentDatabase Db => TestContent.Db;

        private static MachineInstance Place(Factory f, string id, Int3 pos, Direction facing)
        {
            f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:" + id), facing);
            return f.MachineAt(pos);
        }

        private static void Run(Factory f, int ticks)
        {
            for (int i = 0; i < ticks; i++) f.Tick();
        }

        [Test]
        public void FurnaceSmeltsOreWithFuel()
        {
            var f = new Factory(Db, null);
            var furnace = Place(f, "stone_furnace", Int3.Zero, Direction.North);
            Assert.Greater(f.InsertIntoMachine(furnace, new ItemStack(TestContent.Item("iron_ore"), 2)), 0);
            Assert.Greater(f.InsertIntoMachine(furnace, new ItemStack(TestContent.Item("coal"), 1)), 0);
            Assert.AreEqual(1, furnace.Fuel.CountOf(TestContent.Item("coal")), "coal must go to the fuel slot");

            Run(f, 205);
            Assert.AreEqual(1, furnace.Output.CountOf(TestContent.Item("iron_ingot")));
            Run(f, 205);
            Assert.AreEqual(2, furnace.Output.CountOf(TestContent.Item("iron_ingot")));
        }

        [Test]
        public void FurnaceWithoutFuelReportsNoEnergyAndKeepsOre()
        {
            var f = new Factory(Db, null);
            var furnace = Place(f, "stone_furnace", Int3.Zero, Direction.North);
            f.InsertIntoMachine(furnace, new ItemStack(TestContent.Item("iron_ore"), 1));
            Run(f, 50);
            Assert.AreEqual(MachineStatus.NoEnergy, furnace.Status);
            Assert.AreEqual(1, furnace.Input.CountOf(TestContent.Item("iron_ore")));
        }

        /// <summary>
        /// Цепочка из 5 блоков: сундук → лента → горн → лента → сундук.
        /// </summary>
        [Test]
        public void ChestBeltFurnaceBeltChestChain()
        {
            var f = new Factory(Db, null);
            var src = Place(f, "chest", new Int3(0, 0, 0), Direction.North);
            f.SetSide(src.Pos, RelativeSide.Right, SideType.ItemOut); // Right у North = East
            f.OnBlockPlaced(new Int3(1, 0, 0), Db.Blocks.Get("genesis:conveyor"), Direction.East);
            var furnace = Place(f, "stone_furnace", new Int3(2, 0, 0), Direction.East); // back = West (вход), front = East (выход)
            f.OnBlockPlaced(new Int3(3, 0, 0), Db.Blocks.Get("genesis:conveyor"), Direction.East);
            var dst = Place(f, "chest", new Int3(4, 0, 0), Direction.North);

            src.Input.Insert(new ItemStack(TestContent.Item("iron_ore"), 3));
            f.InsertIntoMachine(furnace, new ItemStack(TestContent.Item("coal"), 2));

            Run(f, 1200);
            Assert.AreEqual(3, dst.Input.CountOf(TestContent.Item("iron_ingot")),
                $"furnace status {furnace.Status}, src left {src.Input.CountOf(TestContent.Item("iron_ore"))}");
            Assert.AreEqual(3, f.Stats.TotalProduced(TestContent.Item("iron_ingot")));
        }

        [Test]
        public void BeltRespectsSpacingAndCapacity()
        {
            var f = new Factory(Db, null);
            f.OnBlockPlaced(Int3.Zero, Db.Blocks.Get("genesis:conveyor"), Direction.East);
            var c = f.ConveyorAt(Int3.Zero);
            int ore = TestContent.Item("iron_ore");
            int accepted = 0;
            for (int i = 0; i < 200; i++)
            {
                accepted += f.InsertInto(Int3.Zero, new ItemStack(ore, 1), Direction.West);
                f.Tick();
            }
            Assert.AreEqual(4, c.Items.Count, "dead-end belt holds exactly 4 items");
            Assert.AreEqual(4, accepted);
            for (int i = 1; i < c.Items.Count; i++)
                Assert.GreaterOrEqual(c.Items[i - 1].Pos - c.Items[i].Pos, Conveyor.Spacing - 1e-4f);
        }

        [Test]
        public void BoilerFeedsSteamHammerThroughPipes()
        {
            var f = new Factory(Db, null);
            var boiler = Place(f, "steam_boiler", new Int3(10, 0, 0), Direction.North);
            boiler.Tank("water").Fill(Db.FluidId("genesis:water"), 4000);
            f.InsertIntoMachine(boiler, new ItemStack(TestContent.Item("coal"), 4));

            // Пар идёт вверх из котла в трубы
            f.OnBlockPlaced(new Int3(10, 1, 0), Db.Blocks.Get("genesis:pipe"), Direction.North);
            f.OnBlockPlaced(new Int3(11, 1, 0), Db.Blocks.Get("genesis:pipe"), Direction.North);
            // Молот касается трубы левой гранью (у North левая = West)
            var hammer = Place(f, "steam_hammer", new Int3(12, 1, 0), Direction.North);
            f.InsertIntoMachine(hammer, new ItemStack(TestContent.Item("iron_ingot"), 10));

            Run(f, 600);
            Assert.AreEqual(1, f.Networks.Count);
            Assert.Greater(hammer.Output.CountOf(TestContent.Item("iron_plate")), 5,
                $"boiler {boiler.Status} heat {boiler.Heat}, hammer {hammer.Status}, net steam {f.Networks[0].Amount}");
        }

        [Test]
        public void PumpNextToBoilerPushesWaterDirectly()
        {
            var world = new VoxelWorld(Db, 5, null, 0);
            var basePos = new Int3(100, 300, 100);
            world.LoadNow(WorldConstants.ChunkOf(basePos));
            world.SetBlock(basePos + Int3.Down, Db.BlockId("genesis:water"));

            var f = new Factory(Db, world);
            var pump = Place(f, "water_pump", basePos, Direction.North);
            // Котёл восточнее насоса; у котла по умолчанию left/right/bottom — fluid_in.
            var boiler = Place(f, "steam_boiler", basePos + new Int3(1, 0, 0), Direction.North);

            Run(f, 40);
            Assert.IsTrue(pump.HasSource);
            Assert.Greater(boiler.Tank("water").Amount, 0);
        }

        [Test]
        public void ByproductWithNoSpaceBlocksMachine()
        {
            var f = new Factory(Db, null);
            var press = Place(f, "gear_press", Int3.Zero, Direction.North);
            press.Tank("steam").Fill(Db.FluidId("genesis:steam"), 2000);
            int stone = TestContent.Item("stone");
            press.Output[0] = new ItemStack(stone, 128);
            press.Output[1] = new ItemStack(stone, 128);
            f.InsertIntoMachine(press, new ItemStack(TestContent.Item("iron_plate"), 2));

            Run(f, 100);
            Assert.AreEqual(MachineStatus.OutputBlocked, press.Status);
            Assert.AreEqual(0, press.Output.CountOf(TestContent.Item("iron_gear")));
        }

        [Test]
        public void SteamShortageSlowsMachineProportionally()
        {
            var f = new Factory(Db, null);
            var hammer = Place(f, "steam_hammer", Int3.Zero, Direction.North);
            f.InsertIntoMachine(hammer, new ItemStack(TestContent.Item("iron_ingot"), 1));
            // 4 пара/тик нужно; даём по 2 каждый тик → скорость 50%
            int steam = Db.FluidId("genesis:steam");
            for (int i = 0; i < 40; i++)
            {
                hammer.Tank("steam").Fill(steam, 2);
                f.Tick();
            }
            Assert.AreEqual(0, hammer.Output.CountOf(TestContent.Item("iron_plate")), "30-tick recipe at 50% needs ~60 ticks");
            for (int i = 0; i < 30; i++)
            {
                hammer.Tank("steam").Fill(steam, 2);
                f.Tick();
            }
            Assert.AreEqual(1, hammer.Output.CountOf(TestContent.Item("iron_plate")));
        }

        [Test]
        public void FactorySaveLoadRoundTrip()
        {
            var f = new Factory(Db, null);
            var furnace = Place(f, "stone_furnace", new Int3(1, 2, 3), Direction.West);
            f.InsertIntoMachine(furnace, new ItemStack(TestContent.Item("iron_ore"), 5));
            f.InsertIntoMachine(furnace, new ItemStack(TestContent.Item("coal"), 3));
            f.SetSide(furnace.Pos, RelativeSide.Top, SideType.ItemOut);
            f.OnBlockPlaced(new Int3(0, 0, 0), Db.Blocks.Get("genesis:conveyor"), Direction.South);
            f.InsertInto(Int3.Zero, new ItemStack(TestContent.Item("coal"), 1), Direction.North);
            f.OnBlockPlaced(new Int3(5, 5, 5), Db.Blocks.Get("genesis:pipe"), Direction.North);
            Run(f, 50);

            byte[] data;
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                f.Write(w);
                w.Flush();
                data = ms.ToArray();
            }

            var g = new Factory(Db, null);
            using (var ms = new MemoryStream(data))
                g.Read(new BinaryReader(ms));

            var m = g.MachineAt(new Int3(1, 2, 3));
            Assert.IsNotNull(m);
            Assert.AreEqual(Direction.West, m.Facing);
            Assert.AreEqual(SideType.ItemOut, m.Sides[(int)RelativeSide.Top]);
            Assert.AreEqual(furnace.Input.CountOf(TestContent.Item("iron_ore")), m.Input.CountOf(TestContent.Item("iron_ore")));
            Assert.AreEqual(furnace.Progress, m.Progress, 1e-4);
            Assert.AreEqual(furnace.BurnLeft, m.BurnLeft);
            Assert.AreEqual(f.TickCount, g.TickCount);
            Assert.AreEqual(1, g.Conveyors.Count);
            Assert.AreEqual(1, g.Conveyors[0].Items.Count);
            Assert.IsNotNull(g.PipeAt(new Int3(5, 5, 5)));

            // После загрузки симуляция продолжается идентично
            Run(f, 300);
            Run(g, 300);
            Assert.AreEqual(f.MachineAt(furnace.Pos).Output.CountOf(TestContent.Item("iron_ingot")),
                            m.Output.CountOf(TestContent.Item("iron_ingot")));
        }

        [Test]
        public void BreakingMachineReturnsContents()
        {
            var f = new Factory(Db, null);
            var chest = Place(f, "chest", Int3.Zero, Direction.North);
            chest.Input.Insert(new ItemStack(TestContent.Item("coal"), 50));
            var drops = f.OnBlockRemoved(Int3.Zero);
            Assert.AreEqual(1, drops.Count);
            Assert.AreEqual(50, drops[0].Count);
            Assert.IsNull(f.MachineAt(Int3.Zero));
        }
    }

    public class GameplayTests
    {
        [Test]
        public void PlayerFallsAndLandsOnGround()
        {
            var world = new VoxelWorld(TestContent.Db, 3, null, 0);
            var floor = new Int3(40, 250, 40);
            for (int dy = -1; dy <= 1; dy++) world.LoadNow(WorldConstants.ChunkOf(floor + new Int3(0, dy * 16, 0)));
            for (int x = -2; x <= 2; x++)
            for (int z = -2; z <= 2; z++)
                world.SetBlock(floor + new Int3(x, 0, z), TestContent.Block("stone"));

            var p = new PlayerState(1, "t", TestContent.Db) { Position = new Vec3(40.5f, 255f, 40.5f) };
            for (int i = 0; i < 120; i++) PlayerPhysics.Step(world, p, new MoveInput(), 1f / 60f);
            Assert.IsTrue(p.OnGround);
            Assert.AreEqual(251f, p.Position.Y, 0.01f);
        }

        [Test]
        public void ServerBreakAndPlaceRoundTrip()
        {
            var server = GameServer.Open(TestContent.Db, null, 77, 0);
            var player = server.Join("tester");
            var sp = server.World.Generator.SpawnPoint;
            var wall = sp + new Int3(2, 0, 0); // стена бункера справа от точки спавна в 4 блоках, кладём свой блок
            int stoneItem = TestContent.Item("cobblestone");
            player.Inventory[0] = new ItemStack(stoneItem, 10);
            player.SelectedSlot = 0;

            Assert.IsTrue(server.TryPlaceSelected(player, wall, Direction.East));
            Assert.AreEqual(TestContent.Block("cobblestone"), server.World.GetBlock(wall));
            Assert.AreEqual(9, player.Inventory[0].Count);

            // Булыжник требует инструмент (harvest_tier 1): берём рубило
            player.Inventory[1] = new ItemStack(TestContent.Item("chopper"), 1);
            player.SelectedSlot = 1;
            Assert.IsTrue(server.TryBreakBlock(player, wall));
            Assert.AreEqual(0, server.World.GetBlock(wall));
            for (int i = 0; i < 40; i++) server.Update(0.05f); // выпавший блок подлетает и подбирается
            Assert.AreEqual(10, player.Inventory.CountOf(stoneItem));
            server.Dispose();
        }
    }
}
