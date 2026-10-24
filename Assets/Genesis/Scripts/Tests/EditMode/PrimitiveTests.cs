using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>
    /// Первобытная эпоха: две руки, крафт в руках, режим создания, конструкции, предмет по блоку, валка деревьев,
    /// угасающие предметы, справочник, лежанка, предметы на полу и сохранение нового состояния игрока.
    /// </summary>
    public class PrimitiveTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private GameServer _server;
        private PlayerState _p;
        private Int3 _spawn;
        private int _floor;

        [SetUp]
        public void SetUp()
        {
            _server = GameServer.Open(Db, null, 31337, 0);
            _server.ViewRadius = 1;
            _server.VerticalRadius = 1;
            _server.Enemies.SpawningEnabled = false;
            _p = _server.Join("tester");
            _spawn = _server.World.Generator.SpawnPoint;
            _floor = _server.World.Generator.BunkerFloorY + 1;
            // Чистая площадка в лаборатории (без капсул и столов): x −3..2, z −2..3
            for (int x = -3; x <= 2; x++)
            for (int z = -2; z <= 3; z++)
            for (int y = _floor; y < _floor + 4; y++)
                _server.World.SetBlock(new Int3(x, y, z), Db.AirNumeric);
            _p.Position = new Vec3(-2.5f, _floor + 0.01f, -1.5f);
        }

        [TearDown]
        public void TearDown() => _server.Dispose();

        private static int Item(string id) => Db.ItemId("genesis:" + id);
        private static ushort Block(string id) => Db.BlockId("genesis:" + id);
        private static ItemStack S(string id, int count = 1) => new ItemStack(Item(id), count);
        private bool Done(string quest) => _server.Knowledge.IsUnlocked("genesis:primitive." + quest);
        private int Count(string id) => _p.Inventory.CountOf(Item(id)) + _p.Offhand.CountOf(Item(id));

        private void Hands(ItemStack main, ItemStack off)
        {
            _p.SelectedSlot = 0;
            _p.Inventory[0] = main;
            _p.Offhand[0] = off;
        }

        private bool HandCraft(string id)
        {
            Run(45); // крафт в руках не чаще, чем раз в его время
            return _server.HandCraft(_p, Db.HandCrafts.IndexOf("genesis:" + id));
        }

        private Int3 At(int x, int y, int z) => new Int3(x, _floor + y, z);

        private void Set(Int3 p, string block) => _server.World.SetBlock(p, Block(block));

        private void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) _server.Update(0.05f);
        }

        /// <summary>Все удары операции (с паузами между ними, как у игрока).</summary>
        private bool Shape(Int3 pos, string shaping)
        {
            var def = Db.Shapings.Get("genesis:" + shaping);
            bool ok = true;
            for (int i = 0; i < def.Hits; i++)
            {
                ok &= _server.ShapeHitAt(_p, pos, def.NumericId);
                Run(def.IntervalTicks + 1);
            }
            return ok;
        }

        private int DropsOf(string id)
        {
            int n = 0;
            foreach (var d in _server.Drops.All)
                if (d.Stack.Item == Item(id)) n += d.Stack.Count;
            return n;
        }

        // ================================================================ Руки

        [Test]
        public void HandCraftChainMakesChopper()
        {
            Hands(S("grass_tuft", 4), S("grass_tuft", 3));
            Assert.IsTrue(HandCraft("plant_fiber"));
            Assert.AreEqual(1, Count("plant_fiber"), "one fiber per craft, into the hotbar");
            Assert.AreEqual(3, _p.Inventory[0].Count);
            Assert.AreEqual(2, _p.Offhand[0].Count);
            Assert.IsTrue(Done("fiber"));

            Hands(S("plant_fiber"), S("plant_fiber"));
            Assert.IsTrue(HandCraft("rope"));
            Assert.AreEqual(1, Count("rope"));
            Assert.IsTrue(Done("rope"));

            // Порядок рук не важен
            Hands(S("pebble"), S("pebble"));
            Assert.IsTrue(HandCraft("sharp_stone"));
            Assert.IsTrue(Done("blade"));

            _p.Inventory.Clear();
            Hands(S("rope"), S("stick"));
            Assert.IsTrue(HandCraft("wrapped_handle"));
            Assert.AreEqual(Item("wrapped_handle"), _p.Offhand[0].Item, "the handle stays in the left hand");

            _p.Inventory[0] = S("sharp_stone");
            Assert.IsTrue(HandCraft("chopper"));
            Assert.AreEqual(1, Count("chopper"));
            Assert.IsTrue(_p.Offhand[0].IsEmpty);
            Assert.IsTrue(Done("tool"));
            Assert.AreEqual("universal", Db.Items.Get("genesis:chopper").Tool.Type);
        }

        [Test]
        public void HandCraftRejectsWrongPairsAndSpam()
        {
            Hands(S("grass_tuft"), S("pebble"));
            Assert.IsFalse(HandCraft("plant_fiber"));
            Hands(S("grass_tuft"), ItemStack.Empty);
            Assert.IsFalse(HandCraft("plant_fiber"), "both hands are needed");

            Hands(S("grass_tuft", 10), S("grass_tuft", 10));
            Assert.IsTrue(HandCraft("plant_fiber"));
            Assert.IsFalse(_server.HandCraft(_p, Db.HandCrafts.IndexOf("genesis:plant_fiber")), "server limits the rate");

            // Сноп: канат + 4 травы
            Hands(S("rope"), S("grass_tuft", 3));
            Assert.IsFalse(HandCraft("hay_bundle"), "needs four grass");
            Hands(S("rope"), S("grass_tuft", 4));
            Assert.IsTrue(HandCraft("hay_bundle"));
            Assert.IsTrue(_p.Offhand[0].IsEmpty);
        }

        [Test]
        public void ChopperRepairsWithRope()
        {
            Hands(new ItemStack(Item("chopper"), 1, 40), S("rope", 2));
            Assert.IsTrue(HandCraft("chopper_repair"));
            Assert.AreEqual(0, _p.Inventory[0].Meta);
            Assert.AreEqual(1, _p.Offhand[0].Count);
            Assert.IsFalse(HandCraft("chopper_repair"), "an intact tool needs no repair");
        }

        [Test]
        public void SwapHandsExchangesSlots()
        {
            Hands(S("stick"), S("rope"));
            _server.SwapHands(_p);
            Assert.AreEqual(Item("rope"), _p.Inventory[0].Item);
            Assert.AreEqual(Item("stick"), _p.Offhand[0].Item);
        }

        // ================================================================ Режим создания

        [Test]
        public void LogSplitsIntoFourPlanksAfterFourHits()
        {
            var log = At(0, 0, 0);
            Set(log, "oak_log");
            Hands(S("stick"), ItemStack.Empty);
            Assert.IsFalse(_server.ShapeHitAt(_p, log, Db.Shapings.IndexOf("genesis:log_planks")), "needs a tool");

            Hands(S("chopper"), ItemStack.Empty);
            var def = Db.Shapings.Get("genesis:log_planks");
            Assert.IsTrue(_server.ShapeHitAt(_p, log, def.NumericId));
            Assert.IsFalse(_server.ShapeHitAt(_p, log, def.NumericId), "too fast");
            for (int i = 1; i < 4; i++)
            {
                Run(def.IntervalTicks + 1);
                Assert.IsTrue(_server.ShapeHitAt(_p, log, def.NumericId));
            }
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(log));
            Assert.AreEqual(4, DropsOf("planks"));
            Assert.IsTrue(Done("wood"));
            Assert.Greater(_p.Inventory[0].Meta, 0, "the chopper wears");
        }

        [Test]
        public void PlankOptionsDependOnHeldItem()
        {
            var plank = At(0, 0, 0);
            Set(plank, "planks");
            var withChopper = Shaping.Options(Db, _server.World, _server.Factory, plank, S("chopper"), true);
            Assert.AreEqual(3, withChopper.Count, "fence, slab, sticks");
            var withStick = Shaping.Options(Db, _server.World, _server.Factory, plank, S("stick"), true);
            Assert.AreEqual(1, withStick.Count);
            Assert.AreEqual("genesis:fire_drill", withStick[0].Id);
        }

        [Test]
        public void FireByFrictionAndCampfire()
        {
            var plank = At(0, 0, 0);
            Set(plank, "planks");
            Hands(S("stick"), ItemStack.Empty);
            Assert.IsTrue(Shape(plank, "fire_drill"));
            Assert.AreEqual(Block("planks"), _server.World.GetBlock(plank), "the plank stays");
            Assert.AreEqual(1, DropsOf("ember"));
            Assert.IsTrue(Done("fire"));

            // Уголёк по хворосту — костёр
            var kindling = At(1, 0, 0);
            Set(kindling, "kindling");
            Hands(S("ember"), ItemStack.Empty);
            Assert.IsTrue(_server.UseOnBlock(_p, kindling));
            Assert.AreEqual(Block("campfire"), _server.World.GetBlock(kindling));
            Assert.IsTrue(_p.Inventory[0].IsEmpty);
            Assert.IsTrue(Done("campfire"));
        }

        [Test]
        public void EmberFadesAndHotIngotCools()
        {
            Hands(S("ember"), S("stick"));
            _p.Inventory[1] = S("hot_iron_ingot");
            Run(30);
            Assert.Greater(_p.Inventory[0].Meta, 0, "stamped when first seen");
            Assert.AreEqual(Item("hot_iron_ingot"), _p.Inventory[1].Item);
            Assert.IsTrue(HandCraft("torch"), "a fresh ember lights a torch");
            Assert.AreEqual(2, Count("torch"));

            _p.Inventory[0] = S("ember");
            Run(30);
            _server.WorldTime += 20 * 61;
            Run(25);
            Assert.IsTrue(_p.Inventory[0].IsEmpty, "the ember went out");
            Assert.AreEqual(Item("iron_ingot"), _p.Inventory[1].Item, "the hot ingot cooled down");
        }

        // ================================================================ Конструкции

        [Test]
        public void FencesAndSlabsBecomeWorkbench()
        {
            var a = At(0, 0, 0);
            var b = At(1, 0, 0);
            Set(a, "fence");
            Set(b, "fence");
            Set(a + Int3.Up, "plank_slab");
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsFalse(_server.BuildStructure(_p, a, -1), "not complete yet");
            Set(b + Int3.Up, "plank_slab");
            Assert.IsNotNull(Structures.Find(Db, _server.World, b + Int3.Up, _p.Position));

            Hands(S("stick"), ItemStack.Empty);
            Assert.IsFalse(_server.BuildStructure(_p, a, -1), "needs a tool");
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(_server.BuildStructure(_p, b + Int3.Up, -1));
            Assert.IsTrue(Done("workbench"));

            var bench = MultiBlock.OwnerOf(_server.Factory, a);
            Assert.IsNotNull(bench, "workbench occupies both cells");
            Assert.AreSame(bench, MultiBlock.OwnerOf(_server.Factory, b));
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(a + Int3.Up), "slabs are used up");
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(b + Int3.Up));

            // Сломать за вторую половину — выпадает верстак, обе клетки свободны
            Assert.IsTrue(_server.TryBreakBlock(_p, bench.Pos == a ? b : a));
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(a));
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(b));
            Assert.AreEqual(1, DropsOf("workbench"));

            // Поставить снова — две клетки
            _p.Inventory[0] = S("workbench");
            Assert.IsTrue(_server.TryPlaceSelected(_p, a, Direction.South));
            var placed = _server.Factory.MachineAt(a);
            Assert.IsNotNull(placed);
            int parts = 0;
            foreach (var c in MultiBlock.Cells(placed.Def, placed.Pos, placed.Facing))
                if (_server.World.GetBlockDef(c).PartOf == "genesis:workbench") parts++;
            Assert.AreEqual(1, parts);
        }

        [Test]
        public void WorkbenchSurfaceMakesBellows()
        {
            var bench = At(0, 0, 0);
            Set(bench, "air");
            _p.Inventory[0] = S("workbench");
            Assert.IsTrue(_server.TryPlaceSelected(_p, bench, Direction.South));
            Hands(S("planks", 2), ItemStack.Empty);
            Assert.IsTrue(_server.UseSurface(_p, bench, true));
            Assert.IsTrue(_server.UseSurface(_p, bench, true));
            Hands(S("hide"), ItemStack.Empty);
            Assert.IsTrue(_server.UseSurface(_p, bench, true));
            var opts = Shaping.Options(Db, _server.World, _server.Factory, bench, S("chopper"), true);
            Assert.AreEqual(1, opts.Count);
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(Shape(bench, "bellows"));
            Assert.AreEqual(1, DropsOf("bellows"));
            Assert.IsTrue(_server.Factory.MachineAt(bench).Input.IsEmpty);
            Assert.IsTrue(Done("bellows"));

            // Забрать с поверхности обратно
            Hands(S("stick"), ItemStack.Empty);
            Assert.IsTrue(_server.UseSurface(_p, bench, true));
            Assert.IsTrue(_server.UseSurface(_p, bench, false));
            Assert.AreEqual(1, Count("stick"));
        }

        /// <summary>Печка: булыжник «шахматкой» 3×3 в два слоя, в остальных клетках — любые блоки.</summary>
        private Int3 BuildFurnacePattern(int ox, int oz)
        {
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
                Set(At(ox + x, y, oz + z), (x + z) % 2 == 1 ? "cobblestone" : "dirt");
            return At(ox + 1, 0, oz + 1);
        }

        [Test]
        public void CobblestoneCheckerboardBecomesTwoBlockFurnace()
        {
            var center = BuildFurnacePattern(-1, 1);
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(_server.BuildStructure(_p, At(-1, 1, 1), -1));
            var furnace = _server.Factory.MachineAt(center);
            Assert.IsNotNull(furnace);
            Assert.AreEqual("genesis:stone_furnace", furnace.Def.Id);
            Assert.AreEqual(Block("stone_furnace_part"), _server.World.GetBlock(center + Int3.Up));
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(At(-1, 0, 1)), "the whole structure turns into the furnace");
            Assert.IsTrue(Done("furnace"));

            // Обмазка глиной: 4 раза, содержимое сохраняется
            furnace.Fuel[0] = S("charcoal", 3);
            Hands(new ItemStack(Item("clay"), 4), ItemStack.Empty);
            for (int i = 0; i < 3; i++) Assert.IsTrue(_server.UseOnBlock(_p, center + Int3.Up));
            Assert.AreEqual("genesis:stone_furnace", _server.Factory.MachineAt(center).Def.Id);
            Assert.IsTrue(_server.UseOnBlock(_p, center));
            var coated = _server.Factory.MachineAt(center);
            Assert.AreEqual("genesis:clay_furnace", coated.Def.Id);
            Assert.AreEqual(3, coated.Fuel.CountOf(Item("charcoal")));
            Assert.AreEqual(Block("clay_furnace_part"), _server.World.GetBlock(center + Int3.Up));
            Assert.IsTrue(Done("coating"));

            // Мех рядом — кузня
            Set(center + new Int3(-1, 0, 0), "bellows");
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(_server.BuildStructure(_p, center + new Int3(-1, 0, 0), -1));
            var forge = _server.Factory.MachineAt(center);
            Assert.AreEqual("genesis:forge", forge.Def.Id);
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(center + new Int3(-1, 0, 0)), "bellows become part of the forge");
            Assert.IsTrue(Done("forge"));

            // Плавка в кузне → крица; разогрев слитка
            _p.Inventory.Clear();
            _p.Inventory.Insert(S("iron_ore", 2));
            _p.Inventory.Insert(S("charcoal", 3));
            Assert.IsFalse(_server.Craft(_p, Db.Crafts.IndexOf("genesis:forge_bloom"), center, false), "minigame required");
            Assert.IsTrue(_server.Craft(_p, Db.Crafts.IndexOf("genesis:forge_bloom"), center, true));
            Assert.AreEqual(1, Count("iron_bloom"));
        }

        [Test]
        public void StoneAnvilForgesBloomAndHeads()
        {
            var stone = At(0, 0, 0);
            Set(stone, "cobblestone");
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(Shape(stone, "stone_anvil"));
            var anvil = _server.Factory.MachineAt(stone);
            Assert.IsNotNull(anvil);
            Assert.AreEqual("genesis:stone_anvil", anvil.Def.Id);
            Assert.IsTrue(Done("stone_anvil"));

            anvil.Input.Insert(S("iron_bloom"));
            Assert.IsTrue(Shape(stone, "bloom_to_ingot"));
            Assert.AreEqual(1, DropsOf("iron_ingot"));
            Assert.IsTrue(Done("iron"));
            Assert.IsTrue(_server.Knowledge.IsRecipeUnlocked(Db.Recipes.Get("genesis:smelt_iron_ore")), "iron smelting automation opens");

            // Холодный слиток головку не даёт
            anvil.Input.Insert(S("iron_ingot"));
            Assert.AreEqual(0, Shaping.Options(Db, _server.World, _server.Factory, stone, S("chopper"), true).Count);
            anvil.Input.Clear();
            anvil.Input.Insert(S("hot_iron_ingot"));
            var opts = Shaping.Options(Db, _server.World, _server.Factory, stone, S("chopper"), true);
            Assert.AreEqual(4, opts.Count, "pickaxe, axe, shovel and hoe heads");
            Assert.IsTrue(Shape(stone, "pickaxe_head"));
            Assert.AreEqual(1, DropsOf("pickaxe_head"));

            Hands(S("pickaxe_head"), S("stick"));
            Assert.IsTrue(HandCraft("iron_pickaxe"));
            Assert.AreEqual(1, Count("iron_pickaxe"));
            Assert.IsTrue(Done("iron_tools"));
        }

        [Test]
        public void HayBundlesMakeBedThatSetsSpawnAndSkipsNight()
        {
            var a = At(0, 0, 0);
            Set(a, "hay_bundle");
            Set(At(1, 0, 0), "hay_bundle");
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(_server.BuildStructure(_p, a, -1));
            Assert.IsTrue(Done("bed"));
            var bed = MultiBlock.OwnerOf(_server.Factory, a);
            Assert.IsNotNull(bed);

            // День — точка возрождения
            _server.WorldTime = GameServer.DayLength / 4;
            Assert.IsTrue(_server.UseBlockAction(_p, a));
            Assert.AreEqual(bed.Pos, _p.SpawnPoint);
            _server.Kill(_p);
            Assert.AreEqual(bed.Pos.Y + 1, (int)System.Math.Floor(_p.Position.Y));

            // Ночь — сон до утра
            _p.Position = new Vec3(-2.5f, _floor + 0.01f, -1.5f);
            _server.WorldTime = GameServer.DayLength * 3 + GameServer.DayLength * 3 / 4;
            Assert.IsTrue(_server.UseBlockAction(_p, a));
            Assert.Greater(GameServer.SunHeight(_server.DayPhase), 0f, "morning came");
            Assert.AreEqual(4, _server.WorldTime / GameServer.DayLength);
        }

        // ================================================================ Мир

        [Test]
        public void ChopperFellsWholeTree()
        {
            var basePos = At(0, 0, 0);
            for (int y = 0; y < 3; y++) Set(At(0, y, 0), "oak_log");
            foreach (var d in new[] { new Int3(1, 0, 0), new Int3(-1, 0, 0), new Int3(0, 0, 1), new Int3(0, 0, -1) })
                Set(At(0, 2, 0) + d, "leaves");
            Set(At(0, 3, 0), "leaves");
            Hands(S("chopper"), ItemStack.Empty);
            Assert.IsTrue(_server.TryBreakBlock(_p, basePos));
            for (int y = 0; y < 4; y++) Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(At(0, y, 0)));
            Assert.AreEqual(Db.AirNumeric, _server.World.GetBlock(At(1, 2, 0)), "leaves fall too");
            Assert.AreEqual(3, DropsOf("oak_log"));
        }

        [Test]
        public void GrassAndGravelDropPrimitiveMaterialsByHand()
        {
            var g = At(0, 0, 0);
            Set(g, "tall_grass");
            Hands(ItemStack.Empty, ItemStack.Empty);
            Assert.IsTrue(_server.TryBreakBlock(_p, g));
            Assert.AreEqual(1, DropsOf("grass_tuft"));
            var gr = At(1, 0, 0);
            Set(gr, "gravel");
            Assert.IsTrue(_server.TryBreakBlock(_p, gr));
            Assert.GreaterOrEqual(DropsOf("pebble"), 1);
        }

        [Test]
        public void HandbookIsTakenFromTheLab()
        {
            var book = _server.World.Generator.HandbookPos;
            Assert.AreEqual(Block("handbook"), _server.World.GetBlock(book), "the handbook lies on the lab table");
            Assert.IsFalse(_p.HasHandbook);
            _p.Position = new Vec3(book.X - 1.5f, _floor + 0.01f, book.Z + 0.5f);
            Assert.IsTrue(_server.UseBlockAction(_p, book));
            Assert.IsTrue(_p.HasHandbook);
            Assert.IsTrue(Done("awakening"));
            Assert.AreEqual(Block("handbook"), _server.World.GetBlock(book), "stays for other players");
            Assert.IsFalse(_server.TryBreakBlock(_p, book), "can not be destroyed");
        }

        [Test]
        public void ItemsPlacedOnFloorStayUntilPickedUp()
        {
            var ground = At(0, -1, 0);
            Hands(S("rope", 3), ItemStack.Empty);
            Assert.IsTrue(_server.PlaceOnFloor(_p, ground, false));
            Assert.AreEqual(2, _p.Inventory[0].Count);
            DroppedItem placed = null;
            foreach (var d in _server.Drops.All) placed = d;
            Assert.IsNotNull(placed);
            Assert.IsTrue(placed.Placed);
            _p.Position = new Vec3(ground.X + 0.5f, ground.Y + 1.01f, ground.Z + 1.5f); // рядом — не притягивается
            Run(20 * 60 * 6);
            Assert.IsNotNull(_server.Drops.Get(placed.Id), "placed items do not despawn or fly to the player");
            Assert.AreEqual(2, Count("rope"));
            Assert.IsTrue(_server.PickupDrop(_p, placed.Id));
            Assert.AreEqual(3, Count("rope"));
            Assert.IsNull(_server.Drops.Get(placed.Id));
        }

        // ================================================================ Сохранение

        [Test]
        public void OffhandHandbookAndSpawnSurviveSave()
        {
            string dir = Path.Combine(Path.GetTempPath(), "genesis_prim_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                using (var a = GameServer.Open(Db, dir, 7, 0))
                {
                    var p = a.Join("saver");
                    p.Offhand[0] = S("rope", 5);
                    p.HasHandbook = true;
                    p.SpawnPoint = new Int3(3, 70, -2);
                    a.Leave(p.Id);
                    a.Save();
                }
                using (var b = GameServer.Open(Db, dir, 7, 0))
                {
                    var p = b.Join("saver");
                    Assert.AreEqual(S("rope", 5), p.Offhand[0]);
                    Assert.IsTrue(p.HasHandbook);
                    Assert.AreEqual(new Int3(3, 70, -2), p.SpawnPoint);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void LegacyKnowledgeEraMapsToEpochs()
        {
            var k = new KnowledgeState(Db);
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                w.Write(1); // старый формат
                w.Write(4); // «электрический век» в старой нумерации (дерево, камень, железо, пар, электро)
                w.Write(0);
                w.Flush();
                ms.Position = 0;
                k.Read(new BinaryReader(ms));
            }
            Assert.AreEqual(Eras.IndexOf("electric"), k.Era);
            Assert.AreEqual(2, k.Epoch);
            Assert.AreEqual(0, Eras.IndexOf("stone"), "old ages are the primitive epoch");
            Assert.AreEqual(1, Eras.EpochOf(0));
            Assert.AreEqual(3, Eras.EpochOf(Eras.IndexOf("quantum")));
        }

        [Test]
        public void PrimitiveQuestsAreOrdered()
        {
            var quests = _server.Knowledge.Quests(1);
            Assert.Greater(quests.Count, 15);
            Assert.AreEqual("genesis:primitive.awakening", quests[0].Id);
            Assert.AreEqual("genesis:primitive.fiber", quests[1].Id);
            foreach (var q in quests) Assert.IsTrue(q.Quest);
            Assert.AreEqual(-1, _server.Knowledge.Quests(2).FindIndex(q => q.Id.StartsWith("genesis:primitive.")), "steam quests are separate");
        }
    }
}
