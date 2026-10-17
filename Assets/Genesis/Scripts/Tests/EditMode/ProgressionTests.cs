using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Этап 3: эпохи, дерево знаний, ручной крафт, добыча, выживание, враги.</summary>
    public class ProgressionTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private GameServer _server;
        private PlayerState _p;
        private Int3 _spawn;

        [SetUp]
        public void SetUp()
        {
            _server = GameServer.Open(Db, null, 31337, 0);
            _server.ViewRadius = 1;
            _server.VerticalRadius = 1;
            _server.Enemies.SpawningEnabled = false;
            _p = _server.Join("tester");
            _spawn = _server.World.Generator.SpawnPoint;
        }

        [TearDown]
        public void TearDown() => _server.Dispose();

        private int Give(string id, int count)
        {
            int item = Db.ItemId("genesis:" + id);
            _p.Inventory.Insert(new ItemStack(item, count));
            return item;
        }

        private void Select(string id)
        {
            int item = Db.ItemId("genesis:" + id);
            for (int i = 0; i < _p.Inventory.Size; i++)
            {
                if (_p.Inventory[i].IsEmpty || _p.Inventory[i].Item != item) continue;
                if (i >= PlayerState.HotbarSize)
                {
                    var tmp = _p.Inventory[0];
                    _p.Inventory[0] = _p.Inventory[i];
                    _p.Inventory[i] = tmp;
                    i = 0;
                }
                _p.SelectedSlot = i;
                return;
            }
            Assert.Fail("no " + id + " in inventory");
        }

        private bool Craft(string id, Int3 station = default, bool success = false) =>
            _server.Craft(_p, Db.Crafts.IndexOf("genesis:" + id), station, success);

        private void Run(int frames)
        {
            for (int i = 0; i < frames; i++) _server.Update(0.05f);
        }

        private bool Unlocked(string node) => _server.Knowledge.IsUnlocked("genesis:" + node);

        [Test]
        public void KnowledgeGraphIsConsistent()
        {
            foreach (var n in Db.Knowledge.Items)
                foreach (var r in n.Requires)
                    Assert.LessOrEqual(Db.Knowledge.Get(r).EraIndex, n.EraIndex, $"{n.Id} requires later-era {r}");
            // В каждой реализованной эпохе есть рубеж
            for (int e = 0; e < 3; e++)
            {
                bool hasMilestone = false;
                foreach (var n in Db.Knowledge.Items)
                    if (n.EraIndex == e && n.Milestone) hasMilestone = true;
                Assert.IsTrue(hasMilestone, "era " + Eras.All[e] + " has no milestone");
            }
        }

        [Test]
        public void FullPathFromWoodToIronEra()
        {
            Assert.AreEqual(0, _server.Knowledge.Era);
            Assert.IsTrue(Unlocked("wood.basics"));
            Assert.IsFalse(Craft("wooden_pickaxe"), "wooden tools are locked before the workbench");

            // Руками: брёвна → доски → верстак
            Give("oak_log", 3);
            Assert.IsTrue(Craft("planks"));
            Assert.IsTrue(Craft("workbench"));
            Select("workbench");
            var bench = _spawn + new Int3(2, 0, 0);
            Assert.IsTrue(_server.TryPlaceSelected(_p, bench, Direction.North));
            Assert.IsTrue(Unlocked("wood.workbench"));

            // На верстаке: кирка → открывается горн
            Give("planks", 3);
            Give("stick", 2);
            Assert.IsFalse(Craft("wooden_pickaxe"), "workbench craft needs the station");
            Assert.IsTrue(Craft("wooden_pickaxe", bench));
            Assert.IsTrue(Unlocked("wood.tools"));

            Give("cobblestone", 8);
            Assert.IsTrue(Craft("stone_furnace", bench));
            Select("stone_furnace");
            var furnace = _spawn + new Int3(-2, 0, 0);
            Assert.IsTrue(_server.TryPlaceSelected(_p, furnace, Direction.North));
            Assert.AreEqual(1, _server.Knowledge.Era, "stone furnace is the wood-era milestone");
            Assert.IsTrue(Unlocked("stone.tools"), "start-nodes of the new era open automatically");

            // Плавка вручную: без успешной мини-игры — нельзя
            Give("iron_ore", 2);
            Give("coal", 2);
            Assert.IsFalse(Craft("hand_smelt_iron", furnace, false));
            Assert.IsTrue(Craft("hand_smelt_iron", furnace, true));
            Assert.IsTrue(Unlocked("stone.smelt_iron"));
            Assert.IsTrue(_server.Knowledge.IsRecipeUnlocked(Db.Recipes.Get("genesis:smelt_iron_ore")), "automation unlocked");

            // «Получить первое железо» — рубеж эпохи камня
            Run(25);
            Assert.AreEqual(2, _server.Knowledge.Era);
            Assert.IsTrue(Unlocked("iron.anvil"));
        }

        [Test]
        public void LockedRecipeDoesNotRunInMachine()
        {
            var pos = _spawn + new Int3(-2, 0, 2);
            _server.World.SetBlock(pos, Db.BlockId("genesis:stone_furnace"));
            _server.Factory.OnBlockPlaced(pos, Db.Blocks.Get("genesis:stone_furnace"), Direction.North);
            var m = _server.Factory.MachineAt(pos);
            _server.Factory.InsertIntoMachine(m, new ItemStack(Db.ItemId("genesis:iron_ore"), 1));
            _server.Factory.InsertIntoMachine(m, new ItemStack(Db.ItemId("genesis:coal"), 1));

            Run(60);
            Assert.AreEqual(MachineStatus.NoInput, m.Status, "smelting iron is not automated until done by hand");

            _server.Knowledge.ForceUnlock("genesis:stone.smelt_iron");
            Run(230);
            Assert.AreEqual(1, m.Output.CountOf(Db.ItemId("genesis:iron_ingot")));
        }

        [Test]
        public void StoneNeedsPickaxeAndToolsWearOut()
        {
            var pos = _spawn + new Int3(1, 0, 1);
            ushort stone = Db.BlockId("genesis:stone");
            int cobble = Db.ItemId("genesis:cobblestone");

            _server.World.SetBlock(pos, stone);
            Assert.IsTrue(_server.TryBreakBlock(_p, pos));
            Run(40);
            Assert.AreEqual(0, _p.Inventory.CountOf(cobble), "no drop without a pickaxe");

            _server.World.SetBlock(pos, stone);
            Give("wooden_pickaxe", 1);
            Select("wooden_pickaxe");
            Assert.IsTrue(_server.TryBreakBlock(_p, pos));
            Assert.AreEqual(0, _p.Inventory.CountOf(cobble), "drop falls into the world first");
            Run(40);
            Assert.AreEqual(1, _p.Inventory.CountOf(cobble), "and flies to the player");
            Assert.AreEqual(1, _p.SelectedStack.Meta, "one use of durability");

            // Почти сломанная кирка ломается на следующем блоке
            var tool = Db.Item(_p.SelectedStack.Item).Tool;
            _p.Inventory[_p.SelectedSlot] = new ItemStack(_p.SelectedStack.Item, 1, tool.Durability - 1);
            _server.World.SetBlock(pos, stone);
            Assert.IsTrue(_server.TryBreakBlock(_p, pos));
            Assert.IsTrue(_p.SelectedStack.IsEmpty);

            // Скорость: кирка быстрее рук, железо требует каменную кирку
            var ore = Db.Blocks.Get("genesis:iron_ore_normal");
            Assert.IsFalse(Mining.CanHarvest(ore, Db.Items.Get("genesis:wooden_pickaxe").Tool));
            Assert.IsTrue(Mining.CanHarvest(ore, Db.Items.Get("genesis:stone_pickaxe").Tool));
            Assert.Less(Mining.BreakTime(Db.Blocks.Get("genesis:stone"), Db.Items.Get("genesis:stone_pickaxe").Tool),
                        Mining.BreakTime(Db.Blocks.Get("genesis:stone"), null));
        }

        [Test]
        public void HungerStarvationAndEating()
        {
            _p.Hunger = 1;
            Run(905);
            Assert.AreEqual(0f, _p.Hunger, 0.01f);
            Run(90);
            Assert.Less(_p.Health, PlayerState.MaxHealth, "starving hurts");

            Give("berries", 2);
            Select("berries");
            _server.Eat(_p);
            Assert.AreEqual(3f, _p.Hunger, 0.01f);
            Assert.AreEqual(1, _p.Inventory.CountOf(Db.ItemId("genesis:berries")));
        }

        [Test]
        public void DeathLeavesRemainsAndRespawns()
        {
            var deathPos = _spawn + new Int3(3, 0, 3);
            _p.Position = new Vec3(deathPos.X + 0.5f, deathPos.Y, deathPos.Z + 0.5f);
            Give("coal", 10);
            int tp = _p.TeleportVersion;

            _server.DamagePlayer(_p, 25, "test");

            Assert.AreEqual(PlayerState.MaxHealth, _p.Health);
            Assert.AreEqual(tp + 1, _p.TeleportVersion);
            Assert.IsTrue(_p.Inventory.IsEmpty);
            var grave = _server.Factory.MachineAt(deathPos);
            Assert.IsNotNull(grave);
            Assert.AreEqual("genesis:grave", grave.Def.Id);
            Assert.AreEqual(10, grave.Input.CountOf(Db.ItemId("genesis:coal")));
            Assert.AreEqual(_spawn.X + 0.5f, _p.Position.X, 0.01f);
        }

        [Test]
        public void EnemiesAttackAndCanBeKilled()
        {
            var def = Db.Enemies.Get("genesis:crawler");
            var e = _server.Enemies.Spawn(def, _p.Position + new Vec3(1.2f, 0, 0));
            Run(30);
            Assert.Less(_p.Health, PlayerState.MaxHealth, "crawler bites");

            Give("iron_sword", 1);
            Select("iron_sword");
            for (int i = 0; i < 10 && e.Health > 0; i++) _server.Attack(_p, e.Id);
            Assert.LessOrEqual(e.Health, 0f);
            Run(1);
            Assert.IsNull(_server.Enemies.Get(e.Id));
        }

        [Test]
        public void KnowledgeAndVitalsPersist()
        {
            string dir = Path.Combine(Path.GetTempPath(), "genesis_test_" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var a = GameServer.Open(Db, dir, 5, 0);
                a.ViewRadius = 1;
                var pa = a.Join("Saver");
                a.Knowledge.ForceUnlock("genesis:wood.milestone");
                pa.Health = 7;
                pa.Hunger = 11;
                a.Save();
                a.Dispose();

                var b = GameServer.Open(Db, dir, 999, 0);
                Assert.AreEqual(5, b.Seed, "seed comes from the save");
                Assert.AreEqual(1, b.Knowledge.Era);
                Assert.IsTrue(b.Knowledge.IsUnlocked("genesis:wood.milestone"));
                var pb = b.Join("Saver");
                Assert.AreEqual(7f, pb.Health);
                Assert.AreEqual(11f, pb.Hunger);
                b.Dispose();
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
