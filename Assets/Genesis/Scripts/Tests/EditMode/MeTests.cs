using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Этап 5b: ME-сеть — хранение, шины, автокрафт, автозапас, сохранение.</summary>
    public class MeTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private static int Item(string id) => Db.ItemId("genesis:" + id);

        private Factory _f;
        private MachineInstance _controller, _drive;

        private MachineInstance Place(string id, Int3 pos, Direction facing = Direction.North)
        {
            _f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:" + id), facing);
            return _f.MachineAt(pos);
        }

        private void Cable(Int3 pos) => _f.OnBlockPlaced(pos, Db.Blocks.Get("genesis:me_cable"), Direction.North);

        private MeNetwork Net => _f.Me.NetworkAt(_controller.Pos);

        /// <summary>Контроллер — кабель — привод с ячейкой 1k; питание подаётся вручную.</summary>
        [SetUp]
        public void SetUp()
        {
            _f = new Factory(Db, null);
            _controller = Place("me_controller", new Int3(0, 0, 0));
            Cable(new Int3(1, 0, 0));
            _drive = Place("me_drive", new Int3(2, 0, 0));
            Assert.Greater(_f.InsertIntoMachine(_drive, new ItemStack(Item("cell_1k"), 1)), 0);
            Run(1);
        }

        private void Run(int ticks, System.Action each = null)
        {
            for (int i = 0; i < ticks; i++)
            {
                _controller.EnergyStored = 1000;
                each?.Invoke();
                _f.Tick();
            }
        }

        [Test]
        public void NetworkStoresItemsUpToCellCapacity()
        {
            Assert.IsTrue(Net.Online);
            Assert.AreEqual(1, Net.ItemCells.Count);
            Assert.AreEqual(1000, _f.Me.Insert(Net, Item("coal"), 1000));
            Assert.AreEqual(24, _f.Me.Insert(Net, Item("iron_ore"), 500), "1k cell holds 1024 items of any types");
            Assert.AreEqual(1000, _f.Me.Count(Net, Item("coal")));
            Assert.AreEqual(300, _f.Me.Extract(Net, Item("coal"), 300));
            Assert.AreEqual(700, _f.Me.Count(Net, Item("coal")));
        }

        [Test]
        public void CellKeepsContentsWhenMoved()
        {
            _f.Me.Insert(Net, Item("coal"), 42);
            var cell = _drive.Input[0];
            Assert.Greater(cell.Meta, 0, "cell got an id");
            _drive.Input[0] = ItemStack.Empty;
            Run(1);
            Assert.AreEqual(0, _f.Me.Count(Net, Item("coal")));

            var drive2 = Place("me_drive", new Int3(0, 1, 0)); // касается контроллера сверху
            _f.InsertIntoMachine(drive2, cell);
            Run(1);
            Assert.AreEqual(42, _f.Me.Count(Net, Item("coal")));
        }

        [Test]
        public void NoPowerMeansOffline()
        {
            _controller.EnergyStored = 0;
            _f.Tick();
            Assert.IsFalse(Net.Online);
            Assert.AreEqual(MachineStatus.NoEnergy, _drive.Status);
            Assert.Greater(Net.PowerUsage, 4f, "usage grows with devices and cells");
        }

        [Test]
        public void ImportAndExportBusesWithFilters()
        {
            // Шина импорта смотрит в сундук-источник (−X), экспорта — в сундук-приёмник (+Z)
            var src = Place("chest", new Int3(-2, 0, 0));
            Place("me_import_bus", new Int3(-1, 0, 0), Direction.West);
            var dst = Place("chest", new Int3(0, 0, 2));
            var export = Place("me_export_bus", new Int3(0, 0, 1), Direction.North);
            export.Filter[0] = Item("iron_ore");

            src.Input.Insert(new ItemStack(Item("iron_ore"), 20));
            src.Input.Insert(new ItemStack(Item("coal"), 5));
            Run(200);

            Assert.IsTrue(src.Input.IsEmpty, "import bus without filter takes everything");
            Assert.AreEqual(20, dst.Input.CountOf(Item("iron_ore")), "export bus moves only filtered items");
            Assert.AreEqual(0, dst.Input.CountOf(Item("coal")));
            Assert.AreEqual(5, _f.Me.Count(Net, Item("coal")));
        }

        [Test]
        public void AutocraftThroughProvider()
        {
            var furnace = Place("electric_furnace", new Int3(0, 0, -2));
            var provider = Place("me_provider", new Int3(0, 0, -1), Direction.South); // смотрит на печь, касается контроллера
            provider.Patterns.Add(Db.Recipes.IndexOf("genesis:smelt_iron_ore"));
            _f.Me.Insert(Net, Item("iron_ore"), 3);

            var missing = new List<KeyValuePair<int, long>>();
            Assert.IsTrue(_f.Me.RequestCraft(Net, Item("iron_ingot"), 3, missing));
            Run(400, () => furnace.EnergyStored = 400);

            Assert.AreEqual(3, _f.Me.Count(Net, Item("iron_ingot")));
            Assert.AreEqual(0, _f.Me.Count(Net, Item("iron_ore")));
            Assert.AreEqual(0, Net.Data.Jobs.Count);
        }

        [Test]
        public void RecursiveCraftingPlansIntermediates()
        {
            int steam = Db.FluidId("genesis:steam");
            var hammer = Place("steam_hammer", new Int3(0, 0, -2));
            var p1 = Place("me_provider", new Int3(0, 0, -1), Direction.South);
            p1.Patterns.Add(Db.Recipes.IndexOf("genesis:iron_plate"));
            var press = Place("gear_press", new Int3(0, 2, 0));
            var p2 = Place("me_provider", new Int3(0, 1, 0), Direction.Up);
            p2.Patterns.Add(Db.Recipes.IndexOf("genesis:iron_gear"));
            _f.Me.Insert(Net, Item("iron_ingot"), 4);

            Assert.IsTrue(_f.Me.RequestCraft(Net, Item("iron_gear"), 2, null));
            Assert.AreEqual(2, Net.Data.Jobs.Count, "plates first, then gears");
            Run(600, () =>
            {
                hammer.Tank("steam").Fill(steam, 40);
                press.Tank("steam").Fill(steam, 40);
            });
            Assert.AreEqual(2, _f.Me.Count(Net, Item("iron_gear")));
            Assert.AreEqual(0, _f.Me.Count(Net, Item("iron_ingot")));
        }

        [Test]
        public void MissingMaterialsAreReported()
        {
            var provider = Place("me_provider", new Int3(0, 0, -1), Direction.South);
            Place("electric_furnace", new Int3(0, 0, -2));
            provider.Patterns.Add(Db.Recipes.IndexOf("genesis:smelt_iron_ore"));
            var missing = new List<KeyValuePair<int, long>>();
            Assert.IsFalse(_f.Me.RequestCraft(Net, Item("iron_ingot"), 5, missing));
            Assert.AreEqual(Item("iron_ore"), missing[0].Key);
            Assert.AreEqual(5, missing[0].Value);
            Assert.AreEqual(0, Net.Data.Jobs.Count);
        }

        [Test]
        public void StockKeepingCraftsUpToTarget()
        {
            var furnace = Place("electric_furnace", new Int3(0, 0, -2));
            var provider = Place("me_provider", new Int3(0, 0, -1), Direction.South);
            provider.Patterns.Add(Db.Recipes.IndexOf("genesis:smelt_iron_ore"));
            _f.Me.Insert(Net, Item("iron_ore"), 10);
            _f.Me.SetStock(Net, Item("iron_ingot"), 4);

            Run(800, () => furnace.EnergyStored = 400);
            Assert.AreEqual(4, _f.Me.Count(Net, Item("iron_ingot")), "keeps exactly the target");
            Assert.AreEqual(6, _f.Me.Count(Net, Item("iron_ore")));
        }

        [Test]
        public void MeStateSurvivesSaveLoad()
        {
            _f.Me.Insert(Net, Item("coal"), 77);
            _f.Me.SetStock(Net, Item("iron_ingot"), 500);
            var bus = Place("me_export_bus", new Int3(0, 0, 1), Direction.North);
            bus.Filter[2] = Item("coal");

            byte[] data;
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                _f.Write(w);
                w.Flush();
                data = ms.ToArray();
            }
            _f = new Factory(Db, null);
            using (var ms = new MemoryStream(data)) _f.Read(new BinaryReader(ms));
            _controller = _f.MachineAt(Int3.Zero);
            Run(1);

            Assert.IsTrue(_f.Me.IsCable(new Int3(1, 0, 0)));
            Assert.AreEqual(77, _f.Me.Count(Net, Item("coal")));
            Assert.AreEqual(500, Net.Data.Stock[0].Target);
            Assert.AreEqual(Item("coal"), _f.MachineAt(new Int3(0, 0, 1)).Filter[2]);
        }

        [Test]
        public void SnapshotListsContentsAndCraftables()
        {
            var provider = Place("me_provider", new Int3(0, 0, -1), Direction.South);
            Place("electric_furnace", new Int3(0, 0, -2));
            provider.Patterns.Add(Db.Recipes.IndexOf("genesis:smelt_iron_ore"));
            _f.Me.Insert(Net, Item("coal"), 9);
            var s = _f.Me.Snapshot(Int3.Zero);
            Assert.IsTrue(s.Exists && s.Online);
            Assert.AreEqual(9, s.CountOf(Item("coal")));
            Assert.Contains(Item("iron_ingot"), s.Craftable);

            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                s.Write(w);
                w.Flush();
                ms.Position = 0;
                var back = MeSnapshot.Read(new BinaryReader(ms));
                Assert.AreEqual(9, back.CountOf(Item("coal")));
                Assert.AreEqual(s.ItemCapacity, back.ItemCapacity);
            }
        }

        [Test]
        public void AutoAssemblerRunsWorkbenchCrafts()
        {
            var r = Db.Recipes.Get("genesis:auto_assembler.genesis.chest");
            Assert.IsNotNull(r, "workbench crafts become assembler recipes");
            Assert.AreEqual("genesis:chest", r.SourceCraft);
            Assert.IsNull(Db.Recipes.Get("genesis:auto_assembler.genesis.planks"), "hand crafts are not included");

            var k = new KnowledgeState(Db);
            Assert.IsFalse(k.IsRecipeUnlocked(r), "locked together with the hand craft");
            k.ForceUnlock("genesis:wood.workbench");
            Assert.IsTrue(k.IsRecipeUnlocked(r));

            // Автосборщик реально делает сундук из досок
            var assembler = Place("auto_assembler", new Int3(5, 5, 5));
            _f.InsertIntoMachine(assembler, new ItemStack(Item("planks"), 8));
            for (int i = 0; i < 50; i++)
            {
                assembler.EnergyStored = 640;
                _f.Tick();
            }
            Assert.AreEqual(1, assembler.Output.CountOf(Item("chest")));
        }
    }
}
