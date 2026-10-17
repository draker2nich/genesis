using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using NUnit.Framework;

namespace Genesis.Tests
{
    /// <summary>Мирные животные и враги с особыми атаками.</summary>
    public class CreatureTests
    {
        private static ContentDatabase Db => TestContent.Db;
        private GameServer _server;
        private PlayerState _p;

        [SetUp]
        public void SetUp()
        {
            _server = GameServer.Open(Db, null, 9090, 0);
            _server.ViewRadius = 1;
            _server.VerticalRadius = 1;
            _server.Enemies.SpawningEnabled = false;
            _p = _server.Join("hunter");
        }

        [TearDown]
        public void TearDown() => _server.Dispose();

        private void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++) _server.Update(0.05f);
        }

        [Test]
        public void ContentHasPassiveAndHostileCreatures()
        {
            int passive = 0, hostile = 0;
            foreach (var e in Db.Enemies.Items)
            {
                if (e.IsPassive) passive++;
                else hostile++;
            }
            Assert.GreaterOrEqual(passive, 4);
            Assert.GreaterOrEqual(hostile, 6);
            Assert.GreaterOrEqual(Db.Items.Get("genesis:cooked_meat").Food, 6, "cooked meat is real food");
        }

        [Test]
        public void PassiveAnimalFleesWhenHurtAndDropsFood()
        {
            var def = Db.Enemies.Get("genesis:mossback");
            var e = _server.Enemies.Spawn(def, _p.Position + new Vec3(1.5f, 0, 0));
            Run(5);
            Assert.AreEqual(PlayerState.MaxHealth, _p.Health, "passive animals do not attack");
            _server.Attack(_p, e.Id);
            Assert.Greater(e.FleeTicks, 0, "runs away when hit");
            for (int i = 0; i < 30 && _server.Enemies.Get(e.Id) != null; i++)
            {
                e.Position = _p.Position + new Vec3(1.2f, 0, 0);
                _server.Attack(_p, e.Id);
                Run(1);
            }
            Assert.IsNull(_server.Enemies.Get(e.Id));
            Assert.Greater(_server.Drops.Count, 0, "meat falls into the world");
        }

        [Test]
        public void FeedingTwoAnimalsBreedsAThird()
        {
            var def = Db.Enemies.Get("genesis:burrow_hare");
            var a = _server.Enemies.Spawn(def, _p.Position + new Vec3(1, 0, 0));
            var b = _server.Enemies.Spawn(def, _p.Position + new Vec3(1, 0, 1));
            Assert.IsTrue(_server.Enemies.Feed(a));
            Assert.IsTrue(_server.Enemies.Feed(b));
            int before = _server.Enemies.All.Count;
            Run(100);
            Assert.AreEqual(before + 1, _server.Enemies.All.Count, "a baby appears");
        }

        [Test]
        public void ArmorAndWeakness()
        {
            var def = Db.Enemies.Get("genesis:rust_sentinel");
            var e = _server.Enemies.Spawn(def, _p.Position + new Vec3(3, 0, 0));
            _server.Enemies.Damage(e, 10, _p.Position, "sword");
            Assert.AreEqual(def.Health - 4f, e.Health, 0.01f, "armor absorbs 60%");
            _server.Enemies.Damage(e, 10, _p.Position, "pickaxe");
            Assert.AreEqual(def.Health - 14f, e.Health, 0.01f, "pickaxe ignores armor");
        }

        [Test]
        public void SporelingLeavesPoisonCloud()
        {
            var def = Db.Enemies.Get("genesis:sporeling");
            var e = _server.Enemies.Spawn(def, _p.Position + new Vec3(1, 0, 0));
            _server.Enemies.Damage(e, 100, _p.Position);
            Assert.AreEqual(1, _server.Enemies.Clouds.Count);
            Run(3);
            Assert.Greater(_p.PoisonTicks, 0, "standing in spores poisons");
        }
    }
}
