using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.World
{
    public enum Biome : byte
    {
        Plains = 0,
        Forest = 1,
        Desert = 2,
        Tundra = 3,
        Wasteland = 4, // выжженная катастрофой зона
        Mountains = 5,
        Ocean = 6,
        Beach = 7,
        River = 8,
        Taiga = 9,
        BirchForest = 10,
        Swamp = 11,
        Badlands = 12,
        Meadow = 13,
        SnowyPeaks = 14
    }

    /// <summary>Описание рудного пятна для генератора.</summary>
    public sealed class OreRule
    {
        public string Name;
        public ushort Poor, Normal, Rich;
        public double Threshold;
        public double Frequency;
        public Biome[] Biomes; // null = любые
        public bool NearSpawnBoost;
        internal PerlinNoise Noise;
    }

    /// <summary>Информация о колонке (x,z) — для генерации и UI (биом под ногами).</summary>
    public struct ColumnInfo
    {
        /// <summary>Высота поверхности по карте высот (без нависаний).</summary>
        public int Height;
        public Biome Biome;
        public int OreIndex;   // -1 — нет руды
        public int OrePurity;  // 1..3
        /// <summary>Сила объёмного рельефа (нависания, арки, утёсы) — 0 на равнинах.</summary>
        public float Amp;
        /// <summary>Маска парящих островов 0..1 и высота их центра.</summary>
        public float Island;
        public float IslandY;
        /// <summary>Каньон: 0 — нет, 1 — ось каньона.</summary>
        public float Canyon;
        public float Temperature;
        public float Humidity;
        public float FlowerPatch;
    }

    /// <summary>
    /// Процедурный генератор родной планеты. Полностью детерминирован по seed и потокобезопасен:
    /// чанки генерируются в фоновых потоках.
    ///
    /// Рельеф: материки и океаны, холмы, хребты гор, реки с долинами, плато бедлендов с террасами, дюны,
    /// плоские болота. Поверх карты высот — объёмный шум: нависания, утёсы и арки в горах и бедлендах,
    /// парящие острова. Под землёй — пещеры-«спагетти», большие залы, подземные озёра, каньоны.
    /// 15 биомов, 7 видов деревьев, трава, цветы, кактусы, валуны, скалы-останцы, руины.
    /// </summary>
    public sealed partial class WorldGenerator
    {
        /// <summary>
        /// Версия алгоритма генерации. Увеличивать при любом изменении, меняющем результат: клиент коопа
        /// генерирует нетронутые чанки сам, и разные версии дали бы разный мир (хост отклонит такого клиента).
        /// </summary>
        public const int Version = 4; // 4: криолаборатория в бункере, галька на поверхности

        public readonly long Seed;
        private const int Sea = WorldConstants.SeaLevel;

        private readonly PerlinNoise _continent, _hills, _mountains, _mountainMask;
        private readonly PerlinNoise _temperature, _humidity, _scorch;
        private readonly PerlinNoise _caveA, _caveB, _caveCheese, _detail;
        private readonly PerlinNoise _river, _erosion, _overhang, _canyon, _islandMask, _island3D, _islandY,
            _badlands, _lake, _variant, _dune, _flowers, _bands;
        private readonly OreRule[] _ores;

        private readonly ushort _air, _bedrock, _stone, _dirt, _grass, _deadGrass, _sand, _gravel, _clay,
            _ash, _snow, _water, _log, _deadLog, _leaves, _ruinBricks, _rubble, _quartz, _berryBush, _mushroom,
            _birchLog, _birchLeaves, _spruceLog, _spruceLeaves, _tallGrass, _fern, _flowerRed, _flowerYellow, _flowerBlue,
            _deadBush, _cactus, _redSand, _sandstone, _mud, _ice, _mossyCobble, _cobble,
            _pebbles, _capsule, _capsuleTop, _capsuleOpen, _labTable, _handbook, _terminal, _lamp;
        private readonly ushort[] _terracottaBands;

        public int BunkerFloorY { get; }
        public Int3 SpawnPoint { get; }

        public WorldGenerator(long seed, ContentDatabase content)
        {
            Seed = seed;
            _continent = new PerlinNoise(seed + 1);
            _hills = new PerlinNoise(seed + 2);
            _mountains = new PerlinNoise(seed + 3);
            _mountainMask = new PerlinNoise(seed + 4);
            _temperature = new PerlinNoise(seed + 5);
            _humidity = new PerlinNoise(seed + 6);
            _scorch = new PerlinNoise(seed + 7);
            _caveA = new PerlinNoise(seed + 8);
            _caveB = new PerlinNoise(seed + 9);
            _caveCheese = new PerlinNoise(seed + 10);
            _detail = new PerlinNoise(seed + 11);
            _river = new PerlinNoise(seed + 12);
            _erosion = new PerlinNoise(seed + 13);
            _overhang = new PerlinNoise(seed + 14);
            _canyon = new PerlinNoise(seed + 15);
            _islandMask = new PerlinNoise(seed + 16);
            _island3D = new PerlinNoise(seed + 17);
            _badlands = new PerlinNoise(seed + 18);
            _lake = new PerlinNoise(seed + 19);
            _variant = new PerlinNoise(seed + 20);
            _dune = new PerlinNoise(seed + 21);
            _flowers = new PerlinNoise(seed + 22);
            _bands = new PerlinNoise(seed + 23);
            _islandY = new PerlinNoise(seed + 24);

            _air = content.AirNumeric;
            ushort B(string id) => content.Blocks.Contains("genesis:" + id) ? content.BlockId("genesis:" + id) : _air;
            _bedrock = B("bedrock");
            _stone = B("stone");
            _cobble = B("cobblestone");
            _dirt = B("dirt");
            _grass = B("grass");
            _deadGrass = B("dead_grass");
            _sand = B("sand");
            _gravel = B("gravel");
            _clay = B("clay");
            _ash = B("ash");
            _snow = B("snow");
            _water = B("water");
            _log = B("oak_log");
            _deadLog = B("dead_log");
            _leaves = B("leaves");
            _ruinBricks = B("ruin_bricks");
            _rubble = B("concrete_rubble");
            _quartz = B("quartz_crystal");
            _berryBush = B("berry_bush");
            _mushroom = B("cave_mushroom");
            _birchLog = Or(B("birch_log"), _log);
            _birchLeaves = Or(B("birch_leaves"), _leaves);
            _spruceLog = Or(B("spruce_log"), _log);
            _spruceLeaves = Or(B("spruce_leaves"), _leaves);
            _tallGrass = B("tall_grass");
            _fern = Or(B("fern"), _tallGrass);
            _flowerRed = B("flower_red");
            _flowerYellow = Or(B("flower_yellow"), _flowerRed);
            _flowerBlue = Or(B("flower_blue"), _flowerRed);
            _deadBush = B("dead_bush");
            _cactus = B("cactus");
            _redSand = Or(B("red_sand"), _sand);
            _sandstone = Or(B("sandstone"), _sand);
            _mud = Or(B("mud"), _dirt);
            _ice = Or(B("ice"), _water);
            _mossyCobble = Or(B("mossy_cobblestone"), _cobble);
            _pebbles = B("pebbles");
            _capsule = B("cryo_capsule");
            _capsuleTop = B("cryo_capsule_top");
            _capsuleOpen = Or(B("cryo_capsule_open"), _capsule);
            _labTable = B("lab_table");
            _handbook = B("handbook");
            _terminal = B("lab_terminal");
            _lamp = B("lamp");
            ushort t = Or(B("terracotta"), _clay), tr = Or(B("terracotta_red"), t), tw = Or(B("terracotta_white"), t), tb = Or(B("terracotta_brown"), t);
            _terracottaBands = new[] { t, t, tr, t, tw, t, tb, tb, t, tr, tr, t, tw, t, t, tb };

            var grassy = new[] { Biome.Plains, Biome.Forest, Biome.BirchForest, Biome.Meadow };
            _ores = new[]
            {
                Ore(content, "coal", 0.50, 1 / 52.0, null, true),
                Ore(content, "iron", 0.52, 1 / 56.0, Concat(grassy, Biome.Wasteland, Biome.Mountains, Biome.Tundra, Biome.Taiga), true),
                Ore(content, "copper", 0.54, 1 / 56.0, Concat(grassy, Biome.Desert, Biome.Mountains, Biome.Wasteland, Biome.Badlands), true),
                Ore(content, "tin", 0.56, 1 / 60.0, new[] { Biome.Tundra, Biome.Mountains, Biome.Forest, Biome.Taiga, Biome.BirchForest, Biome.Swamp }, false),
            };
            for (int i = 0; i < _ores.Length; i++) _ores[i].Noise = new PerlinNoise(seed + 100 + i * 7);

            int h0 = Column(0, 0).Height;
            BunkerFloorY = Math.Max(h0 - 10, WorldConstants.MinY + 12);
            SpawnPoint = new Int3(-1, BunkerFloorY + 1, -2); // перед открытой криокамерой
        }

        private ushort Or(ushort a, ushort fallback) => a == _air ? fallback : a;

        private static Biome[] Concat(Biome[] a, params Biome[] b)
        {
            var r = new Biome[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        private static OreRule Ore(ContentDatabase c, string name, double threshold, double freq, Biome[] biomes, bool boost)
        {
            return new OreRule
            {
                Name = name,
                Poor = c.BlockId($"genesis:{name}_ore_poor"),
                Normal = c.BlockId($"genesis:{name}_ore_normal"),
                Rich = c.BlockId($"genesis:{name}_ore_rich"),
                Threshold = threshold,
                Frequency = freq,
                Biomes = biomes,
                NearSpawnBoost = boost
            };
        }

        public string OreName(int index) => index >= 0 && index < _ores.Length ? _ores[index].Name : null;

        private static double Smooth(double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            return v * v * (3 - 2 * v);
        }

        // ------------------------------------------------------------ Колонки

        public ColumnInfo Column(int x, int z)
        {
            double spawnDist = Math.Sqrt((double)x * x + (double)z * z);
            double spawn = Math.Max(0, 1 - spawnDist / 550.0);

            double cont = _continent.Fbm(x / 1500.0, z / 1500.0, 5) * 1.25 + spawn * 0.4 + 0.06;
            double ero = _erosion.Fbm(x / 750.0, z / 750.0, 3);
            double rug = Smooth(0.55 - ero * 1.8);
            double hills = _hills.Fbm(x / 190.0, z / 190.0, 5);
            double det = _detail.Fbm(x / 38.0, z / 38.0, 3);
            double mm = Smooth((_mountainMask.Fbm(x / 1000.0, z / 1000.0, 3) * 1.6 + 0.02) * 2.6) * (1 - 0.9 * spawn);
            double ridge = _mountains.Ridged(x / 340.0, z / 340.0, 6);
            double land = Smooth((cont + 0.02) * 6);

            double h = Sea + 2 + cont * 42
                       + hills * (6 + 26 * rug) * (0.4 + 0.6 * land)
                       + det * 2.0
                       + mm * Math.Pow(ridge, 1.6) * 150 * land;

            double temp = _temperature.Fbm(x / 1300.0, z / 1300.0, 3) * 1.9 * (1 - spawn) + 0.05 * spawn;
            double hum = _humidity.Fbm(x / 1100.0, z / 1100.0, 3) * 1.9 * (1 - spawn) + 0.08 * spawn;
            double scorch = _scorch.Fbm(x / 700.0, z / 700.0, 3) * 1.6 - spawn * 0.8;

            // Бедленды: плато с террасами в жарких краях
            double hot = Smooth((temp - 0.18) * 6), dry = Smooth(-hum * 6);
            double badW = hot * Smooth((_badlands.Fbm(x / 600.0, z / 600.0, 3) + 0.02) * 4) * land * (1 - mm);
            if (badW > 0.001)
            {
                double hb = h + 18 + Math.Abs(hills) * 30;
                const double step = 9.0;
                double tt = hb / step, f = tt - Math.Floor(tt);
                double terr = (Math.Floor(tt) + Smooth((f - 0.7) * 4)) * step;
                h += (terr - h) * badW;
            }
            // Пустыня: дюны
            double desW = hot * dry * (1 - badW) * land;
            if (desW > 0.001) h += Math.Abs(_dune.Fbm(x / 48.0, z / 90.0, 2)) * 9 * desW;
            // Болото: прижато к уровню моря
            double swW = Smooth((hum - 0.25) * 5) * Smooth((temp + 0.1) * 5) * Smooth(1 - (h - Sea) / 26) * land * (1 - mm);
            h += (Sea + 0.6 + det * 1.5 - h) * swW * 0.85;

            // Реки с долинами (не у старта и не в горах)
            double rd = Math.Abs(_river.Fbm(x / 900.0, z / 900.0, 3));
            double rmask = (1 - Smooth(mm * 2.2)) * Smooth((spawnDist - 140) / 90) * Smooth((cont + 0.05) * 8);
            double valley = Smooth(1 - rd / 0.07) * rmask;
            h += (Math.Min(h, Sea + 3 + (h - Sea) * 0.35) - h) * valley * 0.75;
            double rf = Smooth(1 - rd / 0.018) * rmask;
            h += (Sea - 4 - h) * rf;

            int height = (int)Math.Round(h);
            height = Math.Max(WorldConstants.MinY + 8, Math.Min(WorldConstants.MaxY - 40, height));

            // Биом
            double variant = _variant.Fbm(x / 400.0, z / 400.0, 2);
            Biome biome;
            if (hum > 0.04) biome = variant > 0.18 ? Biome.BirchForest : Biome.Forest;
            else biome = variant < -0.25 ? Biome.Meadow : Biome.Plains;
            if (temp < -0.12) biome = Biome.Taiga;
            if (temp < -0.30) biome = Biome.Tundra;
            if (hot > 0.5 && dry > 0.5) biome = Biome.Desert;
            if (badW > 0.5) biome = Biome.Badlands;
            if (swW > 0.45) biome = Biome.Swamp;
            if (scorch > 0.30) biome = Biome.Wasteland;
            bool mountain = mm * ridge > 0.40 && height > Sea + 45;
            if (mountain) biome = height > Sea + 105 ? Biome.SnowyPeaks : Biome.Mountains;
            if (height <= Sea + 1 && height >= Sea - 1 && cont < 0.10 && rf < 0.3 && biome != Biome.Swamp) biome = Biome.Beach;
            if (rf > 0.45) biome = Biome.River;
            if (height < Sea - 1 && rf < 0.45) biome = Biome.Ocean;

            var info = new ColumnInfo
            {
                Height = height,
                Biome = biome,
                OreIndex = -1,
                Temperature = (float)temp,
                Humidity = (float)hum,
                Amp = (float)((mm * ridge * 30 + badW * 12 + rug * 4 * (1 - swW)) * Smooth((height - Sea - 2) / 10.0) * (1 - spawn)),
                Island = (float)(Smooth((_islandMask.Fbm(x / 800.0, z / 800.0, 3) * 1.6 - 0.30) * 3) * (1 - spawn)),
                FlowerPatch = (float)_flowers.Fbm(x / 70.0, z / 70.0, 2)
            };
            if (info.Island > 0.02) info.IslandY = (float)(Sea + 112 + _islandY.Fbm(x / 300.0, z / 300.0, 2) * 10);

            // Каньоны: узкие глубокие разломы отрезками
            if (height > Sea + 8 && spawnDist > 220 && biome != Biome.River)
            {
                double cn = Math.Abs(_canyon.Fbm(x / 420.0, z / 420.0, 3));
                if (cn < 0.02)
                {
                    double seg = Smooth((_canyon.Fbm(x / 260.0 + 100, z / 260.0 + 100, 2) - 0.05) * 6);
                    info.Canyon = (float)(Smooth(1 - cn / 0.02) * seg * Smooth((height - Sea - 8) / 6.0));
                }
            }

            // Рудные пятна выходят на поверхность
            if (biome != Biome.Ocean && biome != Biome.River && biome != Biome.Beach && height > Sea && info.Canyon <= 0)
            {
                double best = 0;
                for (int i = 0; i < _ores.Length; i++)
                {
                    var ore = _ores[i];
                    if (ore.Biomes != null && Array.IndexOf(ore.Biomes, biome) < 0 &&
                        !(ore.NearSpawnBoost && spawnDist < 400)) continue;
                    if (spawnDist < 24) continue; // не прямо в бункере
                    double v = ore.Noise.Fbm(x * ore.Frequency, z * ore.Frequency, 3);
                    double t = ore.Threshold;
                    if (ore.NearSpawnBoost) t -= 0.12 * Math.Max(0, 1 - spawnDist / 450.0);
                    double excess = v - t;
                    if (excess > best)
                    {
                        best = excess;
                        info.OreIndex = i;
                        info.OrePurity = excess < 0.06 ? 1 : (excess < 0.13 ? 2 : 3);
                    }
                }
            }
            return info;
        }

        // ------------------------------------------------------------ Плотность (объёмный рельеф)

        /// <summary>Твёрдый ли блок до вырезания пещер: карта высот + нависания + парящие острова.</summary>
        private bool IsSolid(int x, int y, int z, in ColumnInfo col)
        {
            if (y <= WorldConstants.MinY) return true;
            double d = col.Height - y;
            bool solid;
            if (col.Amp > 0.05f && Math.Abs(d) < col.Amp * 1.7 + 1)
                solid = d + col.Amp * 1.7 * _overhang.Fbm(x / 42.0, y / 24.0, z / 42.0, 2) >= 0;
            else solid = d >= 0;
            if (solid) return true;
            return col.Island > 0.02f && IsIsland(x, y, z, col);
        }

        private bool IsIsland(int x, int y, int z, in ColumnInfo col)
        {
            double dy = y - col.IslandY;
            if (dy <= -34 || dy >= 9) return false;
            double prof = dy > 0 ? 1 - dy / 7.0 : 1 + dy / 30.0;
            double n = _island3D.Fbm(x / 38.0, y / 26.0, z / 38.0, 3);
            return n * 1.8 + prof * 0.45 + (col.Island - 1) * 0.9 > 0.30;
        }

        /// <summary>Верхний твёрдый блок колонки (с нависаниями, без островов и пещер).</summary>
        public int SurfaceY(int x, int z, in ColumnInfo col)
        {
            if (col.Amp <= 0.05f) return col.Height;
            int top = col.Height + (int)(col.Amp * 1.7f) + 2, bottom = col.Height - (int)(col.Amp * 1.7f) - 2;
            for (int y = top; y >= bottom; y--)
            {
                double d = col.Height - y;
                if (d + col.Amp * 1.7 * _overhang.Fbm(x / 42.0, y / 24.0, z / 42.0, 2) >= 0) return y;
            }
            return bottom;
        }

        /// <summary>Каньон вырезает блок: чем глубже, тем уже разлом.</summary>
        private static bool InCanyon(int y, in ColumnInfo col)
        {
            if (col.Canyon <= 0.05f) return false;
            double depth = col.Height + 2 - y;
            return depth >= 0 && col.Canyon > depth / 48.0;
        }

        public bool IsCave(int x, int y, int z)
        {
            // «Спагетти»-пещеры: пересечение двух узких нулевых поверхностей шума
            double a = _caveA.Sample(x / 48.0, y / 32.0, z / 48.0);
            double b = _caveB.Sample(x / 48.0, y / 32.0, z / 48.0);
            if (a * a + b * b < 0.0042) return true;
            // Большие «сырные» залы — глубже
            if (y < 24)
            {
                double c = _caveCheese.Fbm(x / 70.0, y / 40.0, z / 70.0, 2);
                double depthBoost = (24 - y) / 200.0;
                if (c > 0.42 - depthBoost) return true;
            }
            return false;
        }

        /// <summary>Уровень подземных озёр: пещерная пустота ниже него заполнена водой.</summary>
        private int LakeLevel(int x, int z)
        {
            double n = _lake.Fbm(x / 220.0, z / 220.0, 2);
            return n > 0.12 ? -34 + (int)(n * 20) : int.MinValue;
        }
    }
}
