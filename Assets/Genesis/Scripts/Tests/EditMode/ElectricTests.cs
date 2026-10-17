using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Этап 5a: FE, провода, генераторы, ячейки, день и ночь.</summary>
    public class ElectricTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private static int Item(string id) => Db.ItemId("genesis:" + id);

        private static MachineInstance Place(Factory f, string id, Int3 pos, Direction facing = Direction.North)
        {
            f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:" + id), facing);
            return f.MachineAt(pos);
        }

        private static void Cable(Factory f, Int3 pos) =>
            f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:copper_cable"), Direction.North);

        [Test]
        public void TurbinePowersElectricFurnaceThroughCable()
        {
            var f = new Factory(Db, null);
            var turbine = Place(f, "steam_turbine", new Int3(0, 0, 0));
            Cable(f, new Int3(1, 0, 0));
            var furnace = Place(f, "electric_furnace", new Int3(2, 0, 0));
            f.InsertIntoMachine(furnace, new ItemStack(Item("iron_ore"), 2));
            int steam = Db.FluidId("genesis:steam");

            for (int i = 0; i < 215; i++)
            {
                turbine.Tank("steam").Fill(steam, 20);
                f.Tick();
            }
            Assert.AreEqual(1, f.PowerNetworks.Count);
            Assert.AreEqual(2, furnace.Output.CountOf(Item("iron_ingot")), "electric furnace is twice as fast: 100 ticks per ingot");
            Assert.AreEqual(0, furnace.Fuel.Size, "no fuel needed");
        }

        [Test]
        public void EnergyShortageSlowsMachinesProportionally()
        {
            var f = new Factory(Db, null) { Daylight = 1f };
            Place(f, "solar_panel", new Int3(0, 0, 0));        // 8 FE/т
            Cable(f, new Int3(1, 0, 0));
            var crusher = Place(f, "electric_crusher", new Int3(2, 0, 0)); // нужно 16 FE/т
            f.InsertIntoMachine(crusher, new ItemStack(Item("iron_ore"), 64));

            for (int i = 0; i < 400; i++) f.Tick();

            int crushed = crusher.Output.CountOf(Item("crushed_iron_ore"));
            // На полной мощности было бы ~20 (10 операций по 40 тиков), на половине — ~10
            Assert.That(crushed, Is.InRange(7, 13), $"crushed {crushed}");
            Assert.AreEqual(0.5f, f.PowerNetworks[0].Satisfaction, 0.1f);
        }

        [Test]
        public void BatteryStoresSurplusAndCoversNight()
        {
            var f = new Factory(Db, null) { Daylight = 1f };
            Place(f, "solar_panel", new Int3(0, 0, 0));
            Cable(f, new Int3(1, 0, 0));
            var cell = Place(f, "battery_cell", new Int3(1, 1, 0));
            for (int i = 0; i < 200; i++) f.Tick();
            Assert.Greater(cell.EnergyStored, 1000f, "daytime surplus is stored");

            f.Daylight = 0f; // ночь
            var furnace = Place(f, "electric_furnace", new Int3(2, 0, 0));
            f.InsertIntoMachine(furnace, new ItemStack(Item("iron_ore"), 1));
            for (int i = 0; i < 110; i++) f.Tick();
            Assert.AreEqual(1, furnace.Output.CountOf(Item("iron_ingot")), "the cell powers the furnace at night");
        }

        [Test]
        public void CableThroughputLimitsTransfer()
        {
            var f = new Factory(Db, null);
            var cell = Place(f, "battery_cell", new Int3(-1, 0, 0));
            cell.EnergyStored = 200000;
            var crushers = new System.Collections.Generic.List<MachineInstance>();
            for (int x = 0; x < 30; x++)
            {
                Cable(f, new Int3(x, 0, 0));
                crushers.Add(Place(f, "electric_crusher", new Int3(x, 0, 1)));
            }
            f.Tick();
            float got = 0;
            foreach (var c in crushers) got += c.EnergyStored;
            Assert.LessOrEqual(got, 256.5f, "copper cable carries 256 FE/t");
            Assert.Greater(got, 250f);
        }

        [Test]
        public void SolarFollowsDaylight()
        {
            var f = new Factory(Db, null) { Daylight = 0f };
            var panel = Place(f, "solar_panel", Int3.Zero);
            f.Tick();
            Assert.AreEqual(0f, panel.EnergyStored);
            f.Daylight = 1f;
            f.Tick();
            Assert.AreEqual(8f, panel.EnergyStored, 0.01f);
        }

        [Test]
        public void ElectricFurnaceSharesStoneFurnaceRecipes()
        {
            var stone = Db.RecipesFor(Db.Machines.Get("genesis:stone_furnace").NumericId);
            var electric = Db.RecipesFor(Db.Machines.Get("genesis:electric_furnace").NumericId);
            Assert.AreSame(stone, electric);
            Assert.IsTrue(((System.Collections.Generic.List<RecipeDef>)electric).Exists(r => r.Id == "genesis:smelt_iron_ore"));
        }

        [Test]
        public void ServerDayNightDrivesDaylight()
        {
            var server = GameServer.Open(Db, null, 3, 0);
            server.ViewRadius = 1;
            server.VerticalRadius = 1;
            server.Enemies.SpawningEnabled = false;
            server.Join("t");
            server.WorldTime = GameServer.DayLength * 3 / 4 - 1; // полночь
            server.Update(0.05f);
            Assert.AreEqual(0f, server.Factory.Daylight, 0.01f);
            server.WorldTime = GameServer.DayLength / 4 - 1;      // полдень
            server.Update(0.05f);
            Assert.Greater(server.Factory.Daylight, 0.9f);
            server.Dispose();
        }

        [Test]
        public void SteamTurbineIsTheSteamEraMilestone()
        {
            var k = new KnowledgeState(Db);
            k.ForceUnlock("genesis:steam.milestone");
            Assert.AreEqual(Eras.IndexOf("electric"), k.Era);
            Assert.IsTrue(k.IsUnlocked("genesis:electric.basics"));
        }

        [Test]
        public void EnergyAndCablesSurviveSaveLoad()
        {
            var f = new Factory(Db, null);
            var cell = Place(f, "battery_cell", Int3.Zero);
            cell.EnergyStored = 12345;
            Cable(f, new Int3(1, 0, 0));
            byte[] data;
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                f.Write(w);
                w.Flush();
                data = ms.ToArray();
            }
            var g = new Factory(Db, null);
            using (var ms = new MemoryStream(data)) g.Read(new BinaryReader(ms));
            Assert.AreEqual(12345f, g.MachineAt(Int3.Zero).EnergyStored, 0.5f);
            Assert.IsNotNull(g.WireAt(new Int3(1, 0, 0)));
            Assert.AreEqual(1, g.PowerNetworks.Count);
        }
    }
}
