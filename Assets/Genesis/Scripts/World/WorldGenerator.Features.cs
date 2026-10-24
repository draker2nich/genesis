using System;
using System.Collections.Generic;
using Genesis.Core;

namespace Genesis.World
{
    /// <summary>Деревья, кактусы, валуны, упавшие стволы, скалы-останцы, руины и стартовый бункер.</summary>
    public sealed partial class WorldGenerator
    {
        private enum FeatureKind : byte
        {
            Oak, BigOak, Birch, Spruce, SwampOak, Dead, Cactus, Boulder, FallenLog, Spire
        }

        private struct Feature
        {
            public FeatureKind Kind;
            public int X, Y, Z; // Y — первый блок над землёй
            public ulong Seed;
        }

        /// <summary>Максимальный выступ объекта от точки посадки по горизонтали.</summary>
        private const int FeatureReach = 5;

        private List<Feature> FindFeatures(int ox, int oz)
        {
            var list = new List<Feature>();
            for (int wz = oz - FeatureReach; wz < oz + S + FeatureReach; wz++)
            for (int wx = ox - FeatureReach; wx < ox + S + FeatureReach; wx++)
            {
                ulong h = Hash.Coord(Seed ^ 0x7EE, wx, wz);
                double r = Hash.Unit(h);
                if (r > 0.075) continue; // быстрый отсев до вычисления колонки
                if (Math.Abs(wx) < 14 && wz > -14 && wz < 52) continue; // бункер и выход из него

                var col = Column(wx, wz);
                if (col.Canyon > 0.05f || col.OreIndex >= 0) continue;
                if (!PickFeature(col, r, out var kind)) continue;

                int surface = SurfaceY(wx, wz, col);
                bool sandy = kind == FeatureKind.Cactus;
                if (!sandy && surface <= Sea + 1) continue;
                if (sandy && surface <= Sea) continue;
                // Не сажаем деревья на крутых склонах гор
                if (col.Biome == Biome.Mountains && surface > Sea + 85 && kind != FeatureKind.Boulder) continue;
                list.Add(new Feature { Kind = kind, X = wx, Y = surface + 1, Z = wz, Seed = h });
            }
            return list;
        }

        private static bool PickFeature(in ColumnInfo col, double r, out FeatureKind kind)
        {
            kind = FeatureKind.Oak;
            // Таблица: (вероятность, вид) по биомам
            switch (col.Biome)
            {
                case Biome.Forest:
                    return Pick(r, out kind, (0.040, FeatureKind.Oak), (0.004, FeatureKind.BigOak), (0.012, FeatureKind.Birch),
                        (0.002, FeatureKind.FallenLog), (0.001, FeatureKind.Boulder));
                case Biome.BirchForest:
                    return Pick(r, out kind, (0.050, FeatureKind.Birch), (0.006, FeatureKind.Oak), (0.002, FeatureKind.FallenLog));
                case Biome.Plains:
                    return Pick(r, out kind, (0.004, FeatureKind.Oak), (0.0008, FeatureKind.BigOak), (0.002, FeatureKind.Boulder));
                case Biome.Meadow:
                    return Pick(r, out kind, (0.0015, FeatureKind.Oak), (0.002, FeatureKind.Birch));
                case Biome.Taiga:
                    return Pick(r, out kind, (0.050, FeatureKind.Spruce), (0.002, FeatureKind.FallenLog), (0.003, FeatureKind.Boulder));
                case Biome.Tundra:
                    return Pick(r, out kind, (0.006, FeatureKind.Spruce), (0.002, FeatureKind.Boulder));
                case Biome.Swamp:
                    return Pick(r, out kind, (0.020, FeatureKind.SwampOak));
                case Biome.Wasteland:
                    return Pick(r, out kind, (0.008, FeatureKind.Dead), (0.003, FeatureKind.Boulder), (0.0012, FeatureKind.Spire));
                case Biome.Mountains:
                    return Pick(r, out kind, (0.012, FeatureKind.Spruce), (0.002, FeatureKind.Boulder));
                case Biome.Desert:
                    return Pick(r, out kind, (0.006, FeatureKind.Cactus));
                case Biome.Badlands:
                    return Pick(r, out kind, (0.002, FeatureKind.Cactus), (0.0025, FeatureKind.Spire), (0.002, FeatureKind.Dead));
                default:
                    return false;
            }
        }

        private static bool Pick(double r, out FeatureKind kind, params (double p, FeatureKind k)[] table)
        {
            foreach (var e in table)
            {
                if (r < e.p)
                {
                    kind = e.k;
                    return true;
                }
                r -= e.p;
            }
            kind = FeatureKind.Oak;
            return false;
        }

        // ------------------------------------------------------------ Отрисовка объектов

        private void DrawFeature(Chunk c, int ox, int oy, int oz, Feature f)
        {
            // Быстрая проверка по высоте: объекты не выше 24 и не ниже 3 блоков от точки посадки
            if (f.Y + 24 < oy || f.Y - 3 > oy + S) return;
            var rng = new Random64(f.Seed | 1);
            switch (f.Kind)
            {
                case FeatureKind.Oak: RoundTree(c, ox, oy, oz, f, ref rng, _log, _leaves, 4 + rng.NextInt(3), 2); break;
                case FeatureKind.Birch: RoundTree(c, ox, oy, oz, f, ref rng, _birchLog, _birchLeaves, 5 + rng.NextInt(3), 2); break;
                case FeatureKind.BigOak: BigTree(c, ox, oy, oz, f, ref rng); break;
                case FeatureKind.Spruce: Spruce(c, ox, oy, oz, f, ref rng); break;
                case FeatureKind.SwampOak: SwampTree(c, ox, oy, oz, f, ref rng); break;
                case FeatureKind.Dead: DeadTree(c, ox, oy, oz, f, ref rng); break;
                case FeatureKind.Cactus:
                {
                    int h = 1 + rng.NextInt(3);
                    for (int i = 0; i < h; i++) Put(c, ox, oy, oz, f.X, f.Y + i, f.Z, _cactus, true);
                    break;
                }
                case FeatureKind.Boulder: Boulder(c, ox, oy, oz, f, ref rng); break;
                case FeatureKind.FallenLog:
                {
                    bool alongX = (f.Seed & 1) == 0;
                    int len = 3 + rng.NextInt(3);
                    ushort log = (f.Seed & 6) == 0 ? _birchLog : (_spruceLog != _log && (f.Seed & 8) == 0 ? _spruceLog : _log);
                    for (int i = 0; i < len; i++)
                        Put(c, ox, oy, oz, f.X + (alongX ? i : 0), f.Y, f.Z + (alongX ? 0 : i), log, true);
                    break;
                }
                case FeatureKind.Spire: Spire(c, ox, oy, oz, f, ref rng); break;
            }
        }

        private void RoundTree(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng, ushort log, ushort leaves, int trunk, int radius)
        {
            for (int i = 0; i < trunk; i++) Put(c, ox, oy, oz, f.X, f.Y + i, f.Z, log, false);
            int top = f.Y + trunk;
            for (int ly = top - 2; ly <= top + 1; ly++)
            {
                int rad = ly >= top ? 1 : radius;
                for (int dz = -rad; dz <= rad; dz++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    bool corner = Math.Abs(dx) == rad && Math.Abs(dz) == rad;
                    if (corner && (rad == radius ? rng.NextInt(3) != 0 : ly == top + 1)) continue;
                    if (dx == 0 && dz == 0 && ly < top) continue;
                    Put(c, ox, oy, oz, f.X + dx, ly, f.Z + dz, leaves, true);
                }
            }
        }

        private void BigTree(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng)
        {
            int trunk = 7 + rng.NextInt(4);
            for (int i = 0; i < trunk; i++) Put(c, ox, oy, oz, f.X, f.Y + i, f.Z, _log, false);
            // Корни
            for (int d = 0; d < 4; d++)
            {
                var o = Directions.Horizontal[d].Offset();
                if (rng.NextInt(2) == 0) Put(c, ox, oy, oz, f.X + o.X, f.Y, f.Z + o.Z, _log, true);
            }
            // Ветви и шары листвы на их концах
            int branches = 3 + rng.NextInt(2);
            for (int b = 0; b < branches; b++)
            {
                double ang = rng.NextDouble() * Math.PI * 2;
                int by = f.Y + trunk - 3 + rng.NextInt(3);
                int len = 2 + rng.NextInt(2);
                int ex = f.X, ez = f.Z;
                for (int i = 1; i <= len; i++)
                {
                    ex = f.X + (int)Math.Round(Math.Cos(ang) * i);
                    ez = f.Z + (int)Math.Round(Math.Sin(ang) * i);
                    Put(c, ox, oy, oz, ex, by + i / 2, ez, _log, false);
                }
                Blob(c, ox, oy, oz, ex, by + len / 2 + 1, ez, 2.3, _leaves, ref rng);
            }
            Blob(c, ox, oy, oz, f.X, f.Y + trunk, f.Z, 3.2, _leaves, ref rng);
        }

        private void Blob(Chunk c, int ox, int oy, int oz, int cx, int cy, int cz, double r, ushort id, ref Random64 rng)
        {
            int ri = (int)Math.Ceiling(r);
            for (int dy = -ri + 1; dy <= ri - 1; dy++)
            for (int dz = -ri; dz <= ri; dz++)
            for (int dx = -ri; dx <= ri; dx++)
            {
                double d = Math.Sqrt(dx * dx + dy * dy * 1.6 + dz * dz);
                if (d > r || (d > r - 0.8 && rng.NextInt(3) == 0)) continue;
                Put(c, ox, oy, oz, cx + dx, cy + dy, cz + dz, id, true);
            }
        }

        private void Spruce(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng)
        {
            int trunk = 7 + rng.NextInt(6);
            int maxR = trunk > 10 ? 3 : 2;
            for (int i = 0; i < trunk; i++) Put(c, ox, oy, oz, f.X, f.Y + i, f.Z, _spruceLog, false);
            int top = f.Y + trunk;
            // Конус ярусами: радиус растёт книзу и «пульсирует» — характерные лапы ели
            for (int y = f.Y + 2; y <= top + 1; y++)
            {
                int fromTop = top - y;
                int rad = Math.Min(maxR, (fromTop + 2) / 2);
                if (fromTop % 3 == 2 && rad > 0) rad--;
                if (y >= top) rad = 0;
                for (int dz = -rad; dz <= rad; dz++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dz) > rad + (rad > 1 ? 1 : 0)) continue;
                    if (dx == 0 && dz == 0 && y < top) continue;
                    Put(c, ox, oy, oz, f.X + dx, y, f.Z + dz, _spruceLeaves, true);
                }
            }
            Put(c, ox, oy, oz, f.X, top, f.Z, _spruceLeaves, true);
            Put(c, ox, oy, oz, f.X, top + 1, f.Z, _spruceLeaves, true);
            Put(c, ox, oy, oz, f.X, top + 2, f.Z, _spruceLeaves, true);
        }

        private void SwampTree(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng)
        {
            int trunk = 4 + rng.NextInt(2);
            for (int i = 0; i < trunk; i++) Put(c, ox, oy, oz, f.X, f.Y + i, f.Z, _log, false);
            int top = f.Y + trunk;
            for (int ly = top - 1; ly <= top; ly++)
            {
                int rad = ly == top ? 2 : 3;
                for (int dz = -rad; dz <= rad; dz++)
                for (int dx = -rad; dx <= rad; dx++)
                {
                    if (Math.Abs(dx) == rad && Math.Abs(dz) == rad) continue;
                    if (dx == 0 && dz == 0 && ly < top) continue;
                    Put(c, ox, oy, oz, f.X + dx, ly, f.Z + dz, _leaves, true);
                    // Свисающие «лианы» из листвы по краю кроны
                    if (ly == top - 1 && (Math.Abs(dx) == rad || Math.Abs(dz) == rad) && rng.NextInt(4) == 0)
                    {
                        int hang = 1 + rng.NextInt(3);
                        for (int k = 1; k <= hang; k++) Put(c, ox, oy, oz, f.X + dx, ly - k, f.Z + dz, _leaves, true);
                    }
                }
            }
        }

        private void DeadTree(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng)
        {
            int trunk = 3 + rng.NextInt(4);
            for (int i = 0; i < trunk; i++) Put(c, ox, oy, oz, f.X, f.Y + i, f.Z, _deadLog, false);
            int branches = rng.NextInt(3);
            for (int b = 0; b < branches; b++)
            {
                var o = Directions.Horizontal[rng.NextInt(4)].Offset();
                int by = f.Y + 1 + rng.NextInt(Math.Max(1, trunk - 1));
                int len = 1 + rng.NextInt(2);
                for (int i = 1; i <= len; i++) Put(c, ox, oy, oz, f.X + o.X * i, by + (i == len ? 1 : 0), f.Z + o.Z * i, _deadLog, true);
            }
        }

        private void Boulder(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng)
        {
            ushort main = f.Kind == FeatureKind.Boulder && (f.Seed & 3) == 0 ? _mossyCobble : _stone;
            double r = 1.2 + rng.NextDouble() * 1.2;
            int ri = (int)Math.Ceiling(r);
            for (int dy = -1; dy <= ri; dy++)
            for (int dz = -ri; dz <= ri; dz++)
            for (int dx = -ri; dx <= ri; dx++)
            {
                double d = Math.Sqrt(dx * dx + dy * dy * 1.4 + dz * dz);
                if (d > r) continue;
                ushort id = rng.NextInt(4) == 0 ? _cobble : main;
                Put(c, ox, oy, oz, f.X + dx, f.Y + dy - 1, f.Z + dz, id, false);
            }
        }

        private void Spire(Chunk c, int ox, int oy, int oz, Feature f, ref Random64 rng)
        {
            // Скала-останец: столб, сужающийся кверху, слоистый
            int h = 8 + rng.NextInt(13);
            double r0 = 1.6 + rng.NextDouble() * 1.4;
            bool bad = _terracottaBands.Length > 0 && (f.Seed & 1) == 0;
            for (int i = -2; i < h; i++)
            {
                double r = r0 * (1 - 0.55 * Math.Max(0, i) / h) + (i % 4 == 0 ? 0.4 : 0);
                int ri = (int)Math.Ceiling(r);
                ushort id = bad ? _terracottaBands[((f.Y + i) % _terracottaBands.Length + _terracottaBands.Length) % _terracottaBands.Length] : _stone;
                for (int dz = -ri; dz <= ri; dz++)
                for (int dx = -ri; dx <= ri; dx++)
                    if (dx * dx + dz * dz <= r * r) Put(c, ox, oy, oz, f.X + dx, f.Y + i, f.Z + dz, id, false);
            }
        }

        // ------------------------------------------------------------ Руины

        private const int RuinCell = 112;

        private void DecorateRuins(Chunk chunk, int ox, int oy, int oz)
        {
            int cx0 = Int3.FloorDiv(ox - 16, RuinCell), cx1 = Int3.FloorDiv(ox + S + 16, RuinCell);
            int cz0 = Int3.FloorDiv(oz - 16, RuinCell), cz1 = Int3.FloorDiv(oz + S + 16, RuinCell);
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                ulong h = Hash.Coord(Seed ^ 0xB0B, cx, cz);
                if (Hash.Unit(h) > 0.35) continue;
                bool tower = (h >> 40) % 4 == 0;
                int sizeX = tower ? 5 : 6 + (int)((h >> 8) % 7);
                int sizeZ = tower ? 5 : 6 + (int)((h >> 16) % 7);
                int rx = cx * RuinCell + 8 + (int)((h >> 24) % (ulong)(RuinCell - 32));
                int rz = cz * RuinCell + 8 + (int)((h >> 32) % (ulong)(RuinCell - 32));
                if (rx + sizeX < ox || rx > ox + S || rz + sizeZ < oz || rz > oz + S) continue;
                if (Math.Abs(rx) < 40 && Math.Abs(rz) < 60) continue;

                var center = Column(rx + sizeX / 2, rz + sizeZ / 2);
                switch (center.Biome)
                {
                    case Biome.Ocean:
                    case Biome.River:
                    case Biome.Beach:
                    case Biome.Mountains:
                    case Biome.SnowyPeaks:
                    case Biome.Swamp:
                        continue;
                }
                if (center.Height <= Sea || center.Canyon > 0.05f) continue;
                int baseY = SurfaceY(rx + sizeX / 2, rz + sizeZ / 2, center);
                if (baseY + 20 < oy || baseY - 4 > oy + S) continue;
                int maxWall = tower ? 9 + (int)((h >> 44) % 7) : 4;

                for (int z = 0; z <= sizeZ; z++)
                for (int x = 0; x <= sizeX; x++)
                {
                    int wx = rx + x, wz = rz + z;
                    bool wall = x == 0 || z == 0 || x == sizeX || z == sizeZ;
                    ulong bh = Hash.Coord(Seed ^ 0xB0C, wx, wz);
                    // Фундамент до земли и пол из обломков
                    for (int y = baseY - 3; y < baseY; y++) Put(chunk, ox, oy, oz, wx, y, wz, _rubble, true);
                    Put(chunk, ox, oy, oz, wx, baseY, wz, (bh & 3) == 0 ? _rubble : _ruinBricks, false);
                    if (!wall)
                    {
                        // Внутри — расчистить траву
                        for (int y = 1; y <= maxWall; y++) Put(chunk, ox, oy, oz, wx, baseY + y, wz, _air, false);
                        continue;
                    }
                    int wallH = tower ? maxWall - (int)((bh >> 4) % 4) : (int)((bh >> 4) % 5);
                    for (int y = 1; y <= wallH; y++)
                    {
                        // Окна в башне
                        if (tower && y % 4 == 2 && (x == sizeX / 2 || z == sizeZ / 2)) continue;
                        ushort id = ((bh >> (8 + y % 16)) & 3) == 0 ? _mossyCobble : (((bh >> (9 + y % 16)) & 1) == 0 ? _ruinBricks : _rubble);
                        Put(chunk, ox, oy, oz, wx, baseY + y, wz, id, false);
                    }
                }
            }
        }

        // ------------------------------------------------------------ Бункер

        /// <summary>
        /// Стартовый бункер: комната 9×4×9 под землёй и лестничный тоннель на поверхность в сторону +Z.
        /// </summary>
        private void CarveBunker(Chunk chunk, int ox, int oy, int oz)
        {
            if (ox > 12 || ox + S < -12 || oz > 50 || oz + S < -12) return;
            int floor = BunkerFloorY;
            if (oy > floor + 50 || oy + S < floor - 2) return;

            for (int lz = 0; lz < S; lz++)
            for (int lx = 0; lx < S; lx++)
            {
                int x = ox + lx, z = oz + lz;
                bool roomColumn = x >= -5 && x <= 5 && z >= -5 && z <= 5;
                bool tunnelColumn = x >= -2 && x <= 2 && z >= 5 && z <= 50;
                if (!roomColumn && !tunnelColumn) continue;

                int surface = Column(x, z).Height;
                int tFloor = floor + (z - 5);
                bool tunnelActive = tunnelColumn && tFloor < surface + 1;

                for (int ly = 0; ly < S; ly++)
                {
                    int y = oy + ly;
                    int idx = Chunk.Index(lx, ly, lz);

                    bool inShell = roomColumn && y >= floor && y <= floor + 5;
                    bool inRoom = x >= -4 && x <= 4 && z >= -4 && z <= 4 && y >= floor + 1 && y <= floor + 4;

                    bool inTunnel = false, tunnelShell = false;
                    if (tunnelActive)
                    {
                        inTunnel = x >= -1 && x <= 1 && y > tFloor && y <= tFloor + 3;
                        tunnelShell = !inTunnel && y >= tFloor && y <= tFloor + 4;
                    }

                    if (inRoom || inTunnel)
                        chunk.SetRaw(idx, _air);
                    else if ((inShell || tunnelShell) && y <= surface)
                        chunk.SetRaw(idx, _rubble); // бетонная оболочка, только под землёй
                }
            }
            DecorateLab(chunk, ox, oy, oz);
        }

        /// <summary>Место справочника в лаборатории (на столе справа от криокамеры игрока).</summary>
        public Int3 HandbookPos => new Int3(3, BunkerFloorY + 2, 1);

        /// <summary>
        /// Криолаборатория: игрок — последний человек на Земле — выходит из открытой криокамеры. Вдоль задней стены —
        /// капсулы (остальные мертвы), справа стол со справочником, слева терминал, под потолком аварийные лампы.
        /// </summary>
        private void DecorateLab(Chunk chunk, int ox, int oy, int oz)
        {
            int f = BunkerFloorY + 1; // первый блок над полом
            void Set(int x, int y, int z, ushort id)
            {
                if (id == _air) return;
                Put(chunk, ox, oy, oz, x, y, z, id, false);
            }
            // Капсулы у задней стены (z = -4): своя (x = -1) — открытая
            for (int x = -3; x <= 3; x += 2)
            {
                Set(x, f, -4, x == -1 ? _capsuleOpen : _capsule);
                Set(x, f + 1, -4, _capsuleTop);
            }
            Set(HandbookPos.X, f, HandbookPos.Z, _labTable);
            Set(HandbookPos.X, HandbookPos.Y, HandbookPos.Z, _handbook);
            Set(-4, f, 1, _terminal);
            Set(-3, f + 3, -3, _lamp);
            Set(3, f + 3, -3, _lamp);
            Set(-3, f + 3, 3, _lamp);
            Set(3, f + 3, 3, _lamp);
        }
    }
}
