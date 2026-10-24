using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Сетка крафта как в Minecraft, курсор и клики по слотам, справочник «+», предметы в мире.</summary>
    public class CraftingTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private GameServer _server;
        private PlayerState _p;

        [SetUp]
        public void SetUp()
        {
            _server = GameServer.Open(Db, null, 4242, 0);
            _server.ViewRadius = 1;
            _server.VerticalRadius = 1;
            _server.Enemies.SpawningEnabled = false;
            _p = _server.Join("crafter");
            foreach (var n in Db.Knowledge.Items) _server.Knowledge.ForceUnlock(n.Id);
            _p.Inventory.Clear();
        }

        [TearDown]
        public void TearDown() => _server.Dispose();

        private ItemStack S(string id, int count, int meta = 0) => new ItemStack(Db.ItemId("genesis:" + id), count, meta);

        private void Put(int slot, string id, int count) => _p.CraftGrid[slot] = S(id, count);

        [Test]
        public void ShapedRecipeMatchesAnywhereAndMirrored()
        {
            // Труба из двух медных пластин — в инвентаре и в любом углу сетки 3×3
            Put(0, "copper_plate", 1); Put(3, "copper_plate", 1);
            Assert.AreEqual("genesis:pipe", GridCrafting.Match(Db, _p.CraftGrid, 2)?.Craft.Id);
            _p.CraftGrid.Clear();
            Put(5, "copper_plate", 1); Put(8, "copper_plate", 1);
            Assert.AreEqual("genesis:pipe", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id);
            Assert.IsNull(GridCrafting.Match(Db, _p.CraftGrid, 2), "outside the 2x2 area");

            // Несимметричный шаблон и его зеркальное отражение
            _p.CraftGrid.Clear();
            Put(0, "cell_16k", 1); Put(1, "logic_processor", 1); Put(2, "cell_16k", 1); Put(3, "logic_processor", 1); Put(4, "cell_16k", 1);
            Assert.AreEqual("genesis:cell_64k", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id);
            _p.CraftGrid.Clear();
            Put(0, "cell_16k", 1); Put(1, "logic_processor", 1); Put(2, "cell_16k", 1); Put(4, "cell_16k", 1); Put(5, "logic_processor", 1);
            Assert.AreEqual("genesis:cell_64k", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id, "mirrored");
        }

        [Test]
        public void WorkbenchRecipesNeedTheWorkbenchButInventoryRecipesWorkThere()
        {
            Assert.AreEqual(3, Db.Crafts.Get("genesis:chest").GridSize);
            Assert.AreEqual(2, Db.Crafts.Get("genesis:pipe").GridSize);
            // Рецепт инвентаря работает и в сетке верстака
            Put(4, "hide", 1);
            Assert.AreEqual("genesis:leather", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id);
        }

        [Test]
        public void CraftToCursorAndShiftCraftAll()
        {
            Put(0, "hide", 5);
            Assert.AreEqual(1, _server.CraftFromGrid(_p, false, false, -1));
            Assert.AreEqual(S("leather", 1), _p.Cursor[0]);
            Assert.AreEqual(4, _p.CraftGrid[0].Count);
            Assert.AreEqual(1, _server.CraftFromGrid(_p, false, false, -1), "stacks onto the cursor");
            Assert.AreEqual(2, _p.Cursor[0].Count);

            Assert.AreEqual(3, _server.CraftFromGrid(_p, true, false, -1), "shift: all remaining hides");
            Assert.AreEqual(3, _p.Inventory.CountOf(S("leather", 1).Item));
            Assert.IsTrue(_p.CraftGrid[0].IsEmpty);
            Assert.AreEqual(5, _p.Mastery["genesis:leather"]);
        }

        [Test]
        public void CatalystToolIsNotConsumed()
        {
            _server.OpenGrid(_p, 3, default);
            _p.GridSize = 3; // без станции рядом — проверяем только саму сетку
            Put(0, "copper_wire", 1); Put(1, "quartz", 1); Put(2, "copper_wire", 1);
            Put(3, "solder", 1); Put(4, "iron_plate", 1); Put(5, "solder", 1);
            Put(6, "flux_brush", 1); Put(7, "copper_wire", 1);
            var m = GridCrafting.Match(Db, _p.CraftGrid, 3);
            Assert.AreEqual("genesis:solder_circuit_fine", m?.Craft.Id);
            Assert.IsTrue(m.Slots[6].Keep);
        }

        [Test]
        public void SeveralItemsPerCell()
        {
            var c = Db.Crafts.Get("genesis:me_controller");
            Assert.IsTrue(c.IsGrid);
            int plates = 0;
            foreach (var i in c.Inputs)
                if (i.Item == "genesis:iron_plate") plates = i.Count;
            Assert.AreEqual(8, plates, "four cells of 2 plates");
        }

        [Test]
        public void SlotClicksWorkLikeMinecraft()
        {
            _p.Inventory[10] = S("cobblestone", 9);
            _server.ClickSlot(_p, SlotContainer.Inventory, 10, 1, false); // ПКМ — половина
            Assert.AreEqual(5, _p.Cursor[0].Count);
            Assert.AreEqual(4, _p.Inventory[10].Count);
            _server.ClickSlot(_p, SlotContainer.Grid, 0, 1, false); // ПКМ — положить один
            Assert.AreEqual(1, _p.CraftGrid[0].Count);
            Assert.AreEqual(4, _p.Cursor[0].Count);
            _server.ClickSlot(_p, SlotContainer.Inventory, 10, 0, false); // ЛКМ — сложить
            Assert.IsTrue(_p.Cursor[0].IsEmpty);
            Assert.AreEqual(8, _p.Inventory[10].Count);
            _server.ClickSlot(_p, SlotContainer.Grid, 0, 0, true); // Shift — из сетки в инвентарь
            Assert.IsTrue(_p.CraftGrid[0].IsEmpty);
            Assert.AreEqual(9, _p.Inventory.CountOf(S("cobblestone", 1).Item));
        }

        [Test]
        public void FillGridFromRecipeBookAndReturnOnClose()
        {
            _p.Inventory.Insert(S("copper_plate", 10));
            var c = Db.Crafts.Get("genesis:pipe");
            Assert.IsTrue(_server.FillGrid(_p, c.NumericId, false));
            Assert.AreEqual(1, _p.CraftGrid[0].Count);
            Assert.AreEqual(1, _p.CraftGrid[3].Count);
            Assert.AreEqual(8, _p.Inventory.CountOf(S("copper_plate", 1).Item));
            Assert.IsTrue(_server.FillGrid(_p, c.NumericId, true), "shift: as many as possible");
            Assert.AreEqual(5, _p.CraftGrid[0].Count);
            Assert.AreEqual(0, _p.Inventory.CountOf(S("copper_plate", 1).Item));

            _p.Cursor[0] = S("stick", 3);
            _server.CloseGrid(_p);
            Assert.AreEqual(10, _p.Inventory.CountOf(S("copper_plate", 1).Item));
            Assert.AreEqual(3, _p.Inventory.CountOf(S("stick", 1).Item));
            Assert.IsTrue(_p.Cursor[0].IsEmpty);
        }

        [Test]
        public void DroppedItemsFallAndArePickedUpLater()
        {
            _p.Inventory[0] = S("cobblestone", 5);
            _server.DropSlot(_p, 0, false);
            Assert.AreEqual(4, _p.Inventory[0].Count);
            Assert.AreEqual(1, _server.Drops.Count);
            // Брошенный предмет сразу не возвращается (задержка), но потом подбирается
            for (int i = 0; i < 10; i++) _server.Update(0.05f);
            Assert.AreEqual(4, _p.Inventory.CountOf(S("cobblestone", 1).Item));
            for (int i = 0; i < 80; i++) _server.Update(0.05f);
            Assert.AreEqual(5, _p.Inventory.CountOf(S("cobblestone", 1).Item));
            Assert.AreEqual(0, _server.Drops.Count);
            // Выброшенное игроком не исчезает со временем
            _server.DropSlot(_p, 0, true);
            foreach (var d in _server.Drops.All) Assert.IsTrue(d.Persistent);
        }

        [Test]
        public void DroppedItemsMerge()
        {
            var pos = _p.Position + new Vec3(20, 2, 0); // далеко от игрока
            _server.Drops.Spawn(S("stick", 3), pos, Vec3.Zero, 0);
            _server.Drops.Spawn(S("stick", 4), pos + new Vec3(0.2f, 0, 0), Vec3.Zero, 0);
            for (int i = 0; i < 25; i++) _server.Update(0.05f);
            Assert.AreEqual(1, _server.Drops.Count);
            foreach (var d in _server.Drops.All) Assert.AreEqual(7, d.Stack.Count);
        }
    }
}
