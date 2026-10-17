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
            // Верстак 2×2 из досок — в инвентаре и в любом углу сетки 3×3
            Put(0, "planks", 1); Put(1, "planks", 1); Put(3, "planks", 1); Put(4, "planks", 1);
            Assert.AreEqual("genesis:workbench", GridCrafting.Match(Db, _p.CraftGrid, 2)?.Craft.Id);
            _p.CraftGrid.Clear();
            Put(4, "planks", 1); Put(5, "planks", 1); Put(7, "planks", 1); Put(8, "planks", 1);
            Assert.AreEqual("genesis:workbench", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id);
            Assert.IsNull(GridCrafting.Match(Db, _p.CraftGrid, 2), "outside the 2x2 area");

            // Топор: шаблон и его зеркальное отражение
            _p.CraftGrid.Clear();
            Put(0, "planks", 1); Put(1, "planks", 1); Put(3, "planks", 1); Put(4, "stick", 1); Put(7, "stick", 1);
            Assert.AreEqual("genesis:wooden_axe", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id);
            _p.CraftGrid.Clear();
            Put(0, "planks", 1); Put(1, "planks", 1); Put(4, "planks", 1); Put(3, "stick", 1); Put(6, "stick", 1);
            Assert.AreEqual("genesis:wooden_axe", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id, "mirrored");
        }

        [Test]
        public void WorkbenchRecipesNeedTheWorkbenchButInventoryRecipesWorkThere()
        {
            var pick = Db.Crafts.Get("genesis:wooden_pickaxe");
            Assert.AreEqual(3, pick.GridSize);
            var planks = Db.Crafts.Get("genesis:planks");
            Assert.AreEqual(2, planks.GridSize);
            // Рецепт инвентаря работает и в сетке верстака
            Put(4, "birch_log", 1);
            Assert.AreEqual("genesis:planks", GridCrafting.Match(Db, _p.CraftGrid, 3)?.Craft.Id, "tag #logs in a 3x3 grid");
        }

        [Test]
        public void CraftToCursorAndShiftCraftAll()
        {
            Put(0, "oak_log", 5);
            Assert.AreEqual(1, _server.CraftFromGrid(_p, false, false, -1));
            Assert.AreEqual(S("planks", 4), _p.Cursor[0]);
            Assert.AreEqual(4, _p.CraftGrid[0].Count);
            Assert.AreEqual(1, _server.CraftFromGrid(_p, false, false, -1), "stacks onto the cursor");
            Assert.AreEqual(8, _p.Cursor[0].Count);

            Assert.AreEqual(3, _server.CraftFromGrid(_p, true, false, -1), "shift: all remaining logs");
            Assert.AreEqual(12, _p.Inventory.CountOf(S("planks", 1).Item));
            Assert.IsTrue(_p.CraftGrid[0].IsEmpty);
            Assert.Greater(_p.Mastery["genesis:planks"], 4);
        }

        [Test]
        public void CatalystToolIsNotConsumedButWears()
        {
            Put(0, "stone_axe", 1);
            Put(3, "oak_log", 2);
            var m = GridCrafting.Match(Db, _p.CraftGrid, 2);
            Assert.AreEqual("genesis:planks_axe", m?.Craft.Id);
            _server.CraftFromGrid(_p, true, false, -1);
            Assert.AreEqual(12, _p.Inventory.CountOf(S("planks", 1).Item), "6 planks per log with an axe");
            Assert.AreEqual(Db.ItemId("genesis:stone_axe"), _p.CraftGrid[0].Item, "axe stays in the grid");
            Assert.AreEqual(2, _p.CraftGrid[0].Meta, "and wears by one per craft");
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
            _p.Inventory.Insert(S("planks", 10));
            var c = Db.Crafts.Get("genesis:workbench");
            Assert.IsTrue(_server.FillGrid(_p, c.NumericId, false));
            Assert.AreEqual(1, _p.CraftGrid[0].Count);
            Assert.AreEqual(1, _p.CraftGrid[4].Count);
            Assert.AreEqual(6, _p.Inventory.CountOf(S("planks", 1).Item));
            Assert.IsTrue(_server.FillGrid(_p, c.NumericId, true), "shift: as many as possible");
            Assert.AreEqual(2, _p.CraftGrid[0].Count);
            Assert.AreEqual(2, _p.Inventory.CountOf(S("planks", 1).Item));

            _p.Cursor[0] = S("stick", 3);
            _server.CloseGrid(_p);
            Assert.AreEqual(10, _p.Inventory.CountOf(S("planks", 1).Item));
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
