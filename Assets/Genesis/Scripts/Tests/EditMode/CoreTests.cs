using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;
using NUnit.Framework;
using UnityEngine;

namespace Genesis.Tests
{
    /// <summary>Общий загруженный контент базового мода для всех тестов.</summary>
    public static class TestContent
    {
        private static ContentDatabase _db;

        public static ContentDatabase Db
        {
            get
            {
                if (_db != null) return _db;
                _db = new ContentDatabase();
                _db.LoadModDirectory(Path.Combine(Application.streamingAssetsPath, "Game"));
                _db.Freeze();
                return _db;
            }
        }

        public static int Item(string id) => Db.ItemId("genesis:" + id);
        public static ushort Block(string id) => Db.BlockId("genesis:" + id);
    }

    public class ContentTests
    {
        [Test]
        public void BaseModLoadsWithoutErrors()
        {
            var db = TestContent.Db;
            Assert.IsEmpty(db.Errors, string.Join("\n", db.Errors));
            Assert.Greater(db.Blocks.Count, 20);
            Assert.Greater(db.Recipes.Count, 5);
            Assert.IsNotNull(db.Machines.Get("genesis:gear_press"));
        }

        [Test]
        public void MachinesGetBlockAndItem()
        {
            var db = TestContent.Db;
            foreach (var m in db.Machines.Items)
            {
                Assert.IsNotNull(db.Blocks.Get(m.Id), m.Id);
                Assert.AreEqual(m.Id, db.Blocks.Get(m.Id).MachineId);
                Assert.AreEqual(m.Id, db.Items.Get(m.Id).PlacesBlock);
            }
        }

        [Test]
        public void GearRecipeMatchesSpec()
        {
            var r = TestContent.Db.Recipes.Get("genesis:iron_gear");
            Assert.AreEqual("genesis:gear_press", r.Machine);
            Assert.AreEqual(2, r.Inputs[0].Count);
            Assert.AreEqual(0.1f, r.Byproducts[0].Chance, 1e-6);
            Assert.AreEqual("forging", r.Unlock.Minigame);
        }

        [Test]
        public void JsonModCanOverrideAndExtend()
        {
            var db = new ContentDatabase();
            db.AddItemJson("{\"id\":\"testmod:widget\",\"max_stack\":16}");
            db.AddBlockJson("{\"id\":\"testmod:widget_block\",\"render\":\"opaque\",\"drop\":\"testmod:widget\"}");
            Assert.IsTrue(db.Freeze(), string.Join("\n", db.Errors));
            Assert.AreEqual(16, db.Items.Get("testmod:widget").MaxStack);
        }

        [Test]
        public void InvalidReferencesAreReported()
        {
            var db = new ContentDatabase();
            db.AddRecipeJson("{\"id\":\"x:r\",\"machine\":\"x:missing\"}");
            Assert.IsFalse(db.Freeze());
            Assert.IsNotEmpty(db.Errors);
        }
    }

    public class CoreTests
    {
        [Test]
        public void FloorDivAndModHandleNegatives()
        {
            Assert.AreEqual(-1, Int3.FloorDiv(-1, 16));
            Assert.AreEqual(15, Int3.FloorMod(-1, 16));
            Assert.AreEqual(-2, Int3.FloorDiv(-17, 16));
            Assert.AreEqual(0, Int3.FloorDiv(15, 16));
        }

        [Test]
        public void NoiseIsDeterministicAndBounded()
        {
            var a = new PerlinNoise(42);
            var b = new PerlinNoise(42);
            var c = new PerlinNoise(43);
            bool differs = false;
            for (int i = 0; i < 200; i++)
            {
                double x = i * 0.37, z = i * 0.71;
                Assert.AreEqual(a.Sample(x, z), b.Sample(x, z));
                Assert.LessOrEqual(System.Math.Abs(a.Fbm(x, z, 4)), 1.01);
                if (System.Math.Abs(a.Sample(x, z) - c.Sample(x, z)) > 1e-6) differs = true;
            }
            Assert.IsTrue(differs);
        }

        [Test]
        public void RelativeSidesRoundTrip()
        {
            foreach (var facing in Directions.Horizontal)
            for (int s = 0; s < 6; s++)
            {
                var abs = Directions.ToAbsolute((RelativeSide)s, facing);
                Assert.AreEqual((RelativeSide)s, Directions.ToRelative(abs, facing));
            }
        }
    }

    public class WorldTests
    {
        [Test]
        public void ChunkSetGetAndCompact()
        {
            var c = new Chunk(Int3.Zero);
            Assert.IsTrue(c.IsUniform);
            c.Set(1, 2, 3, 5);
            Assert.AreEqual(5, c.Get(1, 2, 3));
            Assert.AreEqual(0, c.Get(0, 0, 0));
            c.Set(1, 2, 3, 0);
            c.TryCompact();
            Assert.IsTrue(c.IsUniform);
        }

        [Test]
        public void GenerationIsDeterministic()
        {
            var g1 = new WorldGenerator(1234, TestContent.Db);
            var g2 = new WorldGenerator(1234, TestContent.Db);
            var coord = new Int3(3, 3, -2);
            var a = new ushort[WorldConstants.ChunkVolume];
            var b = new ushort[WorldConstants.ChunkVolume];
            g1.Generate(coord).CopyTo(a);
            g2.Generate(coord).CopyTo(b);
            CollectionAssert.AreEqual(a, b);
        }

        [Test]
        public void BedrockAtBottom()
        {
            var g = new WorldGenerator(7, TestContent.Db);
            var c = g.Generate(new Int3(0, WorldConstants.MinChunkY, 0));
            ushort bedrock = TestContent.Block("bedrock");
            for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
                Assert.AreEqual(bedrock, c.Get(x, 0, z));
        }

        [Test]
        public void SpawnIsInsideBunkerAir()
        {
            var world = new VoxelWorld(TestContent.Db, 99, null, 0);
            var sp = world.Generator.SpawnPoint;
            for (int dy = -1; dy <= 2; dy++)
                world.LoadNow(WorldConstants.ChunkOf(sp + new Int3(0, dy * 16, 0)));
            Assert.AreEqual(0, world.GetBlock(sp));
            Assert.AreEqual(0, world.GetBlock(sp + Int3.Up));
            Assert.IsTrue(world.GetBlockDef(sp + Int3.Down).Solid);
        }

        [Test]
        public void GeneratorProducesVariedBiomesAndIsDeterministic()
        {
            var g = new WorldGenerator(2026, TestContent.Db);
            var seen = new System.Collections.Generic.HashSet<Biome>();
            for (int z = -3000; z < 3000; z += 40)
            for (int x = -3000; x < 3000; x += 40)
                seen.Add(g.Column(x, z).Biome);
            Assert.GreaterOrEqual(seen.Count, 10, "expected many biomes, got: " + string.Join(",", seen));

            // Чанки в разных местах генерируются без исключений и одинаково при повторе
            var g2 = new WorldGenerator(2026, TestContent.Db);
            for (int i = 0; i < 24; i++)
            {
                var coord = new Int3(i * 37 - 400, (i % 8) - 2, i * 23 - 200);
                var a = g.Generate(coord);
                var b = g2.Generate(coord);
                for (int k = 0; k < WorldConstants.ChunkVolume; k += 7)
                    Assert.AreEqual(a.GetByIndex(k), b.GetByIndex(k));
            }
        }

        [Test]
        public void OrePatchesExistNearSpawn()
        {
            var g = new WorldGenerator(2026, TestContent.Db);
            int ores = 0;
            for (int z = -400; z < 400; z += 4)
            for (int x = -400; x < 400; x += 4)
                if (g.Column(x, z).OreIndex >= 0) ores++;
            Assert.Greater(ores, 50, "expected surface ore patches around spawn");
        }

        [Test]
        public void RaycastHitsBlockAndReportsAdjacent()
        {
            var world = new VoxelWorld(TestContent.Db, 1, null, 0);
            var target = new Int3(5000 % 4096, 300, 5); // высоко в воздухе
            world.LoadNow(WorldConstants.ChunkOf(target));
            world.SetBlock(target, TestContent.Block("stone"));
            var origin = new Vec3(target.X + 0.5f, target.Y + 0.5f, target.Z - 3.5f);
            Assert.IsTrue(VoxelRaycast.Cast(world, origin, new Vec3(0, 0, 1), 10, out var hit));
            Assert.AreEqual(target, hit.Block);
            Assert.AreEqual(target + new Int3(0, 0, -1), hit.Adjacent);
            Assert.AreEqual(Direction.South, hit.Face);
        }

        [Test]
        public void ChunkCodecRoundTrip()
        {
            var db = TestContent.Db;
            var c = new Chunk(new Int3(1, 2, 3));
            c.Set(0, 0, 0, TestContent.Block("stone"));
            c.Set(15, 15, 15, TestContent.Block("iron_ore_rich"));
            using (var ms = new MemoryStream())
            {
                var w = new BinaryWriter(ms);
                ChunkCodec.Write(w, c, db);
                w.Flush();
                ms.Position = 0;
                var r = ChunkCodec.Read(new BinaryReader(ms), c.Coord, db);
                Assert.AreEqual(c.Get(0, 0, 0), r.Get(0, 0, 0));
                Assert.AreEqual(c.Get(15, 15, 15), r.Get(15, 15, 15));
                Assert.AreEqual(0, r.Get(7, 7, 7));
            }
        }
    }

    public class InventoryTests
    {
        [Test]
        public void StacksUpTo128AndSpills()
        {
            var inv = new Inventory(TestContent.Db, 2);
            int coal = TestContent.Item("coal");
            Assert.AreEqual(200, inv.Insert(new ItemStack(coal, 200)));
            Assert.AreEqual(128, inv[0].Count);
            Assert.AreEqual(72, inv[1].Count);
            Assert.AreEqual(56, inv.Insert(new ItemStack(coal, 100)));
            Assert.AreEqual(256, inv.CountOf(coal));
        }

        [Test]
        public void CanInsertAllIsAtomic()
        {
            var inv = new Inventory(TestContent.Db, 1);
            int a = TestContent.Item("iron_gear"), b = TestContent.Item("iron_scrap");
            Assert.IsFalse(inv.CanInsertAll(new[] { new ItemStack(a, 1), new ItemStack(b, 1) }));
            Assert.IsTrue(inv.IsEmpty);
            Assert.IsTrue(inv.CanInsertAll(new[] { new ItemStack(a, 1), new ItemStack(a, 1) }));
        }
    }
}
