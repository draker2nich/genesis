using System.IO;
using Genesis.Data;
using Genesis.Modding;
using NUnit.Framework;
using UnityEngine;

namespace Genesis.Tests
{
    /// <summary>Этап 5c: загрузчик модов — манифесты, зависимости, порядок, включение, контент.</summary>
    public class ModdingTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "genesis_mods_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string Mod(string id, string deps = "", string loadAfter = "")
        {
            string dir = Path.Combine(_root, id);
            Directory.CreateDirectory(Path.Combine(dir, "items"));
            File.WriteAllText(Path.Combine(dir, "mod.json"),
                $"{{\"id\":\"{id}\",\"version\":\"1.2.3\",\"dependencies\":[{deps}],\"load_after\":[{loadAfter}]}}");
            return dir;
        }

        private static string BaseMod => Path.Combine(Application.streamingAssetsPath, "Game");

        [Test]
        public void DependenciesDefineLoadOrder()
        {
            Mod("zeta", "\"alpha\"");
            Mod("alpha");
            Mod("beta", "", "\"zeta\"");
            var loader = new ModLoader(null);
            loader.Discover(new[] { _root }, new string[0]);
            var order = loader.LoadOrder;
            Assert.AreEqual(3, order.Count);
            Assert.Less(IndexOf(loader, "alpha"), IndexOf(loader, "zeta"));
            Assert.Less(IndexOf(loader, "zeta"), IndexOf(loader, "beta"), "load_after is respected");
            Assert.AreEqual("alpha@1.2.3,zeta@1.2.3,beta@1.2.3", loader.Fingerprint);
        }

        private static int IndexOf(ModLoader l, string id)
        {
            for (int i = 0; i < l.LoadOrder.Count; i++)
                if (l.LoadOrder[i].Id == id) return i;
            return -1;
        }

        [Test]
        public void MissingOrDisabledDependencyDeactivatesMod()
        {
            Mod("needs_ghost", "\"ghost\"");
            Mod("lib");
            Mod("plugin", "\"lib\"");
            var loader = new ModLoader(null);
            loader.Discover(new[] { _root }, new string[0]);
            Assert.IsFalse(loader.Mods.Find(m => m.Id == "needs_ghost").Active);
            Assert.IsTrue(loader.Mods.Find(m => m.Id == "plugin").Active);

            loader.SetEnabled("lib", false);
            Assert.IsFalse(loader.Mods.Find(m => m.Id == "plugin").Active, "disabling a dependency disables dependents");
            loader.SetEnabled("lib", true);
            Assert.IsTrue(loader.Mods.Find(m => m.Id == "plugin").Active);
        }

        [Test]
        public void DependencyCycleIsReported()
        {
            Mod("a", "\"b\"");
            Mod("b", "\"a\"");
            var loader = new ModLoader(null);
            loader.Discover(new[] { _root }, new string[0]);
            Assert.AreEqual(0, loader.LoadOrder.Count);
            Assert.IsNotEmpty(loader.Mods[0].Errors);
        }

        [Test]
        public void EnabledStatePersists()
        {
            Mod("opt");
            string cfg = Path.Combine(_root, "cfg", "mods.json");
            var a = new ModLoader(cfg);
            a.Discover(new[] { _root }, new string[0]);
            a.SetEnabled("opt", false);
            var b = new ModLoader(cfg);
            b.Discover(new[] { _root }, new string[0]);
            Assert.IsFalse(b.Mods.Find(m => m.Id == "opt").Enabled);
        }

        [Test]
        public void DataModAddsContentOnTopOfBase()
        {
            string dir = Mod("bronze", "\"genesis\"");
            File.WriteAllText(Path.Combine(dir, "items", "bronze_ingot.json"), "{\"id\":\"bronze:bronze_ingot\"}");
            Directory.CreateDirectory(Path.Combine(dir, "lang"));
            File.WriteAllText(Path.Combine(dir, "lang", "en.json"), "{\"item.bronze.bronze_ingot\":\"Bronze Ingot\"}");

            var loader = new ModLoader(null);
            loader.Discover(new[] { _root }, new string[0], BaseMod);
            Assert.AreEqual("genesis", loader.LoadOrder[0].Id, "base content first");

            var db = new ContentDatabase();
            var loc = new Localization { Language = "en" };
            loader.LoadContent(db, loc);
            loader.LoadCode(db, loc);
            Assert.IsTrue(db.Freeze(), string.Join("\n", db.Errors));
            Assert.IsNotNull(db.Items.Get("bronze:bronze_ingot"));
            Assert.AreEqual("Bronze Ingot", loc.Get("item.bronze.bronze_ingot"));
            Assert.AreNotEqual(TestContent.Db.ContentHash, db.ContentHash, "mods change the content fingerprint");
        }
    }
}
