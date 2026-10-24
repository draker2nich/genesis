using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Genesis.Core;

namespace Genesis.World
{
    /// <summary>Генерация чанка: рельеф, материалы, пещеры, вода, растительность.</summary>
    public sealed partial class WorldGenerator
    {
        private const int S = WorldConstants.ChunkSize;
        private const int Border = 1;
        private const int CW = S + Border * 2;

        /// <summary>
        /// Данные колонок чанка (с рамкой в 1 блок для уклона) и найденные места деревьев вокруг.
        /// Чанки одной колонки по высоте используют их повторно — кешируем.
        /// </summary>
        private sealed class ColumnSet
        {
            public ColumnInfo[] Columns;
            public int[] Surface;          // верх твёрдого (с нависаниями) для внутренних колонок
            public List<Feature> Features; // деревья и прочее, задевающие этот чанк
            public int MaxTop;
        }

        private readonly ConcurrentDictionary<long, ColumnSet> _sets = new ConcurrentDictionary<long, ColumnSet>();

        private ColumnSet GetSet(int cx, int cz)
        {
            long key = ((long)cx << 32) ^ (uint)cz;
            if (_sets.TryGetValue(key, out var set)) return set;
            if (_sets.Count > 2048) _sets.Clear();
            set = BuildSet(cx, cz);
            _sets[key] = set;
            return set;
        }

        private ColumnSet BuildSet(int cx, int cz)
        {
            int ox = cx * S, oz = cz * S;
            var set = new ColumnSet { Columns = new ColumnInfo[CW * CW], Surface = new int[S * S], MaxTop = int.MinValue };
            for (int z = 0; z < CW; z++)
            for (int x = 0; x < CW; x++)
                set.Columns[z * CW + x] = Column(ox + x - Border, oz + z - Border);
            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                ref var c = ref set.Columns[(z + Border) * CW + x + Border];
                int top = c.Height + (int)Math.Ceiling(c.Amp * 1.7f) + 1;
                if (c.Island > 0.02f) top = Math.Max(top, (int)c.IslandY + 10);
                if (top > set.MaxTop) set.MaxTop = top;
                set.Surface[z * S + x] = SurfaceY(ox + x, oz + z, c);
            }
            set.Features = FindFeatures(ox, oz);
            return set;
        }

        /// <summary>Колонка из кеша чанка (дёшево для уже сгенерированных мест; потокобезопасно).</summary>
        public ColumnInfo ColumnAt(int x, int z)
        {
            int cx = Int3.FloorDiv(x, S), cz = Int3.FloorDiv(z, S);
            long key = ((long)cx << 32) ^ (uint)cz;
            return _sets.TryGetValue(key, out var set) ? Col(set, x - cx * S, z - cz * S) : Column(x, z);
        }

        private ref ColumnInfo Col(ColumnSet set, int lx, int lz) => ref set.Columns[(lz + Border) * CW + lx + Border];

        private int Slope(ColumnSet set, int lx, int lz)
        {
            int h = Col(set, lx, lz).Height;
            int s = Math.Abs(Col(set, lx + 1, lz).Height - h);
            s = Math.Max(s, Math.Abs(Col(set, lx - 1, lz).Height - h));
            s = Math.Max(s, Math.Abs(Col(set, lx, lz + 1).Height - h));
            s = Math.Max(s, Math.Abs(Col(set, lx, lz - 1).Height - h));
            return s;
        }

        // ------------------------------------------------------------ Генерация чанка

        public Chunk Generate(Int3 coord)
        {
            var chunk = new Chunk(coord, _air);
            if (!WorldConstants.ChunkInBounds(coord)) return chunk;

            int ox = coord.X << WorldConstants.ChunkBits;
            int oy = coord.Y << WorldConstants.ChunkBits;
            int oz = coord.Z << WorldConstants.ChunkBits;
            var set = GetSet(coord.X, coord.Z);

            // Чанк выше всего рельефа, островов и моря — только кроны деревьев
            bool aboveAll = oy > Math.Max(set.MaxTop, Sea) + 1;
            if (!aboveAll)
            {
                const int Scan = S + 5; // y от oy-1 до oy+S+3
                var solid = new bool[Scan];
                for (int lz = 0; lz < S; lz++)
                for (int lx = 0; lx < S; lx++)
                {
                    ref var col = ref Col(set, lx, lz);
                    int wx = ox + lx, wz = oz + lz;
                    for (int i = 0; i < Scan; i++) solid[i] = IsSolid(wx, oy - 1 + i, wz, col);
                    int slope = Slope(set, lx, lz);
                    int lake = LakeLevel(wx, wz);

                    // depth: 0 — у блока сверху воздух; сверху вниз
                    int depth = solid[Scan - 1] ? Math.Max(4, col.Height - (oy + S + 3)) : -1;
                    ushort surfaceBlock = _air;
                    for (int i = Scan - 2; i >= 0; i--)
                    {
                        int y = oy - 1 + i;
                        depth = solid[i] ? depth + 1 : -1;
                        if (depth > 0 && !solid[i + 1]) depth = 0;
                        if (i == 0)
                        {
                            // Блок под чанком: нужен только чтобы посадить траву на нижний слой
                            if (solid[0] && !solid[1] && !InCanyon(y, col))
                                surfaceBlock = SurfaceMaterial(wx, y, wz, col, 0, slope);
                            break;
                        }
                        int ly = y - oy;
                        if (ly >= S) continue;
                        int idx = Chunk.Index(lx, ly, lz);
                        ushort b;
                        if (solid[i])
                        {
                            b = Material(wx, y, wz, col, depth, slope, lake);
                        }
                        else if (y <= Sea && y >= col.Height - 1 && !IsAboveIslandOnly(y, col))
                        {
                            bool frozen = y == Sea && (col.Biome == Biome.Tundra || (col.Biome == Biome.Taiga && col.Temperature < -0.22f) ||
                                                       col.Biome == Biome.SnowyPeaks);
                            b = frozen ? _ice : _water;
                        }
                        else b = _air;
                        if (b != _air) chunk.SetRaw(idx, b);
                    }

                    // Растительность на верхнем блоке колонки внутри чанка
                    for (int ly = 0; ly < S; ly++)
                    {
                        int idx = Chunk.Index(lx, ly, lz);
                        if (chunk.GetByIndex(idx) != _air) continue;
                        ushort below = ly > 0 ? chunk.GetByIndex(Chunk.Index(lx, ly - 1, lz)) : surfaceBlock;
                        if (below == _air || below == _water || below == _ice) continue;
                        ushort plant = Flora(wx, oy + ly, wz, col, below);
                        if (plant != _air) chunk.SetRaw(idx, plant);
                    }
                }
            }

            foreach (var f in set.Features) DrawFeature(chunk, ox, oy, oz, f);
            DecorateRuins(chunk, ox, oy, oz);
            CarveBunker(chunk, ox, oy, oz);

            chunk.TryCompact();
            return chunk;
        }

        /// <summary>Вода только у основного рельефа, а не под парящими островами.</summary>
        private static bool IsAboveIslandOnly(int y, in ColumnInfo col) => y > col.Height + col.Amp * 1.7f + 2;

        // ------------------------------------------------------------ Материалы

        private ushort Material(int x, int y, int z, in ColumnInfo col, int depth, int slope, int lake)
        {
            if (y == WorldConstants.MinY) return _bedrock;
            if (y <= WorldConstants.MinY + 3)
            {
                double r = Hash.Unit(Hash.Coord(Seed, x, y, z));
                if (r < 0.6 - (y - WorldConstants.MinY) * 0.2) return _bedrock;
            }

            if (InCanyon(y, col)) return y < col.Height - 40 ? _water : _air;

            // Пещеры (не прорезаем верхние слои, чтобы не дырявить поверхность и залежи)
            if (depth > 5 && y > WorldConstants.MinY + 4 && IsCave(x, y, z))
            {
                if (y <= lake) return _water; // подземное озеро
                if (!IsCave(x, y - 1, z))
                {
                    double r = Hash.Unit(Hash.Coord(Seed ^ 0x51, x, y, z));
                    if (r < 0.006 && y < 40) return _quartz;
                    if (r > 0.985) return _mushroom; // пещерные грибы — еда под землёй
                }
                return _air;
            }

            // Парящий остров: трава, земля, камень
            if (col.Island > 0.02f && y > col.Height + col.Amp * 1.7f + 2)
                return depth == 0 ? _grass : (depth < 3 ? _dirt : _stone);

            // Рудные пятна выходят на поверхность и уходят на глубину
            if (col.OreIndex >= 0 && depth < 4 + col.OrePurity * 3)
            {
                var ore = _ores[col.OreIndex];
                return col.OrePurity == 3 ? ore.Rich : (col.OrePurity == 2 ? ore.Normal : ore.Poor);
            }

            return SurfaceMaterial(x, y, z, col, depth, slope);
        }

        private ushort SurfaceMaterial(int x, int y, int z, in ColumnInfo col, int depth, int slope)
        {
            int h = col.Height;
            bool underwater = y < Sea;
            bool steep = slope >= 4;
            ulong hash = Hash.Coord(Seed, x, z);
            switch (col.Biome)
            {
                case Biome.Ocean:
                    if (depth < 3) return (hash & 3) == 0 ? _clay : (h < Sea - 12 ? _gravel : _sand);
                    break;
                case Biome.River:
                    if (depth < 3) return (hash % 6) == 0 ? _clay : ((hash % 3) == 0 ? _gravel : _sand);
                    break;
                case Biome.Beach:
                    if (depth < 3) return _sand;
                    if (depth < 6) return _sandstone;
                    break;
                case Biome.Desert:
                    if (depth < 4) return _sand;
                    if (depth < 10) return _sandstone;
                    break;
                case Biome.Badlands:
                    if (depth == 0 && !steep) return _redSand;
                    if (depth < 24)
                    {
                        int off = (int)(_bands.Sample(x / 70.0, z / 70.0) * 4);
                        int i = ((y + off) % _terracottaBands.Length + _terracottaBands.Length) % _terracottaBands.Length;
                        return _terracottaBands[i];
                    }
                    break;
                case Biome.Tundra:
                    if (depth == 0) return underwater ? _gravel : _snow;
                    if (depth < 4) return _dirt;
                    break;
                case Biome.Taiga:
                    if (depth == 0)
                    {
                        if (underwater) return _gravel;
                        if (steep) return _stone;
                        return col.Temperature < -0.22f && (hash & 3) != 0 ? _snow : _grass;
                    }
                    if (depth < 4) return _dirt;
                    break;
                case Biome.Swamp:
                    if (depth == 0) return _detail.Sample(x / 9.0, z / 9.0) > 0.1 ? _mud : _grass;
                    if (depth < 3) return _dirt;
                    if (depth < 5) return _clay;
                    break;
                case Biome.Wasteland:
                    if (depth == 0) return underwater ? _gravel : ((hash & 7) < 5 ? _ash : _deadGrass);
                    if (depth < 3) return _dirt;
                    break;
                case Biome.Mountains:
                case Biome.SnowyPeaks:
                {
                    int snowLine = Sea + 100 + (int)(hash % 7);
                    if (y > snowLine && depth < 2 && slope < 7) return _snow;
                    if (!steep && y < Sea + 80)
                    {
                        if (depth == 0) return _grass;
                        if (depth < 3) return _dirt;
                    }
                    if (depth < 2 && (hash % 9) == 0) return _gravel;
                    break;
                }
                default: // Plains, Forest, BirchForest, Meadow
                    if (h <= Sea + 1)
                    {
                        if (depth < 3) return _sand;
                    }
                    else
                    {
                        if (depth == 0) return steep && slope > 6 ? _stone : _grass;
                        if (depth < 4) return _dirt;
                    }
                    break;
            }
            return _stone;
        }

        // ------------------------------------------------------------ Трава и цветы

        private ushort Flora(int x, int y, int z, in ColumnInfo col, ushort below)
        {
            if (Math.Abs(x) < 8 && z > -8 && z < 50) return _air; // вход в бункер
            if (y <= Sea) return _air;
            double r = Hash.Unit(Hash.Coord(Seed ^ 0xF10A, x, z));
            // Галька — первый камень для острия. У выхода из бункера её больше, чтобы было с чего начать
            double pebble = Hash.Unit(Hash.Coord(Seed ^ 0x9EB1, x, z));
            bool nearExit = Math.Abs(x) < 10 && z >= 50 && z < 62;
            bool rocky = below == _gravel || below == _stone || below == _sand || below == _cobble || below == _dirt;
            if (_pebbles != _air && pebble < (nearExit ? 0.06 : (rocky ? 0.03 : 0.006))) return _pebbles;
            bool onGrass = below == _grass;
            bool onSand = below == _sand || below == _redSand || below == _ash || below == _deadGrass || below == _terracottaBands[0];

            if (onGrass)
            {
                double grass = 0, fern = 0, flower = 0, berry = 0;
                switch (col.Biome)
                {
                    case Biome.Plains: grass = 0.22; flower = 0.02 + (col.FlowerPatch > 0.25 ? 0.15 : 0); berry = 0.005; break;
                    case Biome.Meadow: grass = 0.30; flower = 0.12 + (col.FlowerPatch > 0.2 ? 0.15 : 0); break;
                    case Biome.Forest: grass = 0.10; fern = 0.03; flower = 0.008; berry = 0.012; break;
                    case Biome.BirchForest: grass = 0.15; flower = 0.03 + (col.FlowerPatch > 0.3 ? 0.1 : 0); berry = 0.004; break;
                    case Biome.Taiga: grass = 0.05; fern = 0.12; berry = 0.006; break;
                    case Biome.Swamp: grass = 0.15; fern = 0.06; break;
                    case Biome.Mountains: grass = 0.06; flower = 0.004; break;
                    default: grass = 0.04; break;
                }
                if ((r -= berry) < 0) return _berryBush;
                if ((r -= flower) < 0) return FlowerAt(x, z, col);
                if ((r -= fern) < 0) return _fern;
                if ((r -= grass) < 0) return _tallGrass;
                return _air;
            }
            if (onSand)
            {
                bool dry = col.Biome == Biome.Desert || col.Biome == Biome.Badlands || col.Biome == Biome.Wasteland;
                if (dry && r < 0.012) return _deadBush;
            }
            return _air;
        }

        private ushort FlowerAt(int x, int z, in ColumnInfo col)
        {
            // В пятне цветов — один цвет, иначе случайный
            double p = col.FlowerPatch;
            ulong h = Hash.Coord(Seed ^ 0xF1, x, z);
            int kind = p > 0.25 ? (int)(Hash.Coord(Seed ^ 0xF2, (int)(x / 64), (int)(z / 64)) % 3) : (int)(h % 3);
            if ((h & 7) == 0) kind = (kind + 1) % 3;
            return kind == 0 ? _flowerRed : (kind == 1 ? _flowerYellow : _flowerBlue);
        }

        private void Put(Chunk chunk, int ox, int oy, int oz, int wx, int wy, int wz, ushort id, bool onlyIntoAir)
        {
            int lx = wx - ox, ly = wy - oy, lz = wz - oz;
            if ((uint)lx >= S || (uint)ly >= S || (uint)lz >= S) return;
            int idx = Chunk.Index(lx, ly, lz);
            if (onlyIntoAir)
            {
                ushort cur = chunk.GetByIndex(idx);
                if (cur != _air && cur != _tallGrass && cur != _fern) return;
            }
            chunk.SetRaw(idx, id);
        }
    }
}
