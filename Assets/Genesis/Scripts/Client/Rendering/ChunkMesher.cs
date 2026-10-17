using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    public enum BlockShape : byte
    {
        Cube = 0,
        Cross = 1,
        Torch = 2,
        /// <summary>Рисуется моделью Blockbench (ModelBlockRenderer), в меш чанка не попадает.</summary>
        Model = 3
    }

    /// <summary>Плоские таблицы свойств блоков для быстрого мешинга в фоновых потоках.</summary>
    public sealed class MeshBlockTable
    {
        public readonly byte[] Render;
        public readonly bool[] Liquid;
        public readonly int[] Textures; // 7 на блок
        public readonly bool[] Orientable;
        public readonly bool[] Conveyor;
        public readonly byte[] Shape;
        /// <summary>Собственное свечение 0–15.</summary>
        public readonly byte[] Emit;
        /// <summary>Затухание света при прохождении через блок: 15 — непрозрачный.</summary>
        public readonly byte[] Opacity;
        public readonly bool[] Foliage;
        /// <summary>Окраска по биому: 0 — нет, 1 — листва/трава целиком, 2 — блок травы (верх полностью, бока наполовину).</summary>
        public readonly byte[] Tint;

        public MeshBlockTable(ContentDatabase content)
        {
            int n = content.Blocks.Count;
            Render = new byte[n];
            Liquid = new bool[n];
            Textures = new int[n * 7];
            Orientable = new bool[n];
            Conveyor = new bool[n];
            Shape = new byte[n];
            Emit = new byte[n];
            Opacity = new byte[n];
            Foliage = new bool[n];
            Tint = new byte[n];
            for (int i = 0; i < n; i++)
            {
                var b = content.Blocks.Get(i);
                Render[i] = (byte)b.Render;
                Liquid[i] = b.Liquid;
                Orientable[i] = b.Orientable || b.MachineId != null;
                Conveyor[i] = b.Transport != null && b.Transport.Type == "conveyor";
                Shape[i] = (byte)(b.Model != null ? BlockShape.Model :
                    b.Shape == "cross" ? BlockShape.Cross : (b.Shape == "torch" ? BlockShape.Torch : BlockShape.Cube));
                Emit[i] = (byte)Mathf.Clamp(b.Light, 0, 15);
                Foliage[i] = b.Id.EndsWith("leaves", System.StringComparison.Ordinal) || Shape[i] == (byte)BlockShape.Cross;
                if (b.Render == RenderLayer.Opaque && Shape[i] == (byte)BlockShape.Cube) Opacity[i] = 15;
                else if (b.Liquid) Opacity[i] = 2;
                else if (b.Render == RenderLayer.Cutout && Shape[i] == (byte)BlockShape.Cube) Opacity[i] = 1;
                else Opacity[i] = 0;
                for (int f = 0; f < 7; f++) Textures[i * 7 + f] = b.FaceTexture[f];
                Tint[i] = (byte)(b.Tint == "grass" ? 2 : (b.Tint == "foliage" ? 1 : 0));
            }
        }

        public bool IsOpaque(ushort id) => id < Render.Length && Opacity[id] >= 15;
        public bool IsCubeFace(ushort id) => id < Render.Length && Shape[id] == (byte)BlockShape.Cube;
    }

    /// <summary>
    /// Снимок области вокруг чанка (поля по 8 блоков — дальность света) для мешинга в другом потоке.
    /// SkyCovered[x,z] — над снимком в этой колонке есть непрозрачный блок (небо закрыто).
    /// </summary>
    public sealed class ChunkSnapshot
    {
        public const int Pad = 8;
        public const int P = WorldConstants.ChunkSize + 2 * Pad; // 32
        public readonly ushort[] Blocks = new ushort[P * P * P];
        public readonly bool[] SkyCovered = new bool[P * P];
        public readonly Dictionary<int, Direction> Facing = new Dictionary<int, Direction>();
        /// <summary>Цвет биома по колонкам чанка (16×16, x + z·16); a = 0 — без окраски.</summary>
        public readonly Color32[] Tint = new Color32[WorldConstants.ChunkSize * WorldConstants.ChunkSize];
        public Int3 Coord;
        public int Version;

        public static int Index(int x, int y, int z) => (y * P + z) * P + x; // x,y,z в [0,P)
    }

    public sealed class MeshData
    {
        public readonly List<Vector3> Vertices = new List<Vector3>(4096);
        public readonly List<Vector3> Normals = new List<Vector3>(4096);
        public readonly List<Vector3> Uvs = new List<Vector3>(4096);
        public readonly List<Color32> Colors = new List<Color32>(4096);
        /// <summary>UV1: окраска биома минус 1 (нет данных в меше = без окраски).</summary>
        public readonly List<Vector3> Tints = new List<Vector3>(4096);
        public readonly List<int> Solid = new List<int>(6144);
        public readonly List<int> Translucent = new List<int>(256);
        public Int3 Coord;
        public int Version;

        // Буферы света (переиспользуются между запусками мешера)
        internal byte[] Sky, Blk;
        internal int[] Queue;

        public void Clear()
        {
            Vertices.Clear();
            Normals.Clear();
            Uvs.Clear();
            Colors.Clear();
            Tints.Clear();
            Solid.Clear();
            Translucent.Clear();
        }

        public bool IsEmpty => Solid.Count == 0 && Translucent.Count == 0;
    }

    /// <summary>
    /// Мешер: свет неба и блоков (распространение по снимку), гладкое освещение и AO по вершинам,
    /// формы блоков (куб, крест для растений, факел), поворот текстур у ориентируемых блоков.
    /// Цвет вершины: R — свет неба, G — свет блоков, B — AO×затенение грани, A — флаги для шейдера.
    /// </summary>
    public static class ChunkMesher
    {
        // Флаги в альфе вершины (читает шейдер)
        public const byte FlagNormal = 255;
        public const byte FlagWater = 128;
        public const byte FlagSway = 192;
        public const byte FlagEmissive = 224;

        private const int LightStep = 2; // затухание за блок: дальность ~7 блоков укладывается в поля снимка

        // Углы граней (порядок по часовой стрелке при взгляде снаружи — фронт для Unity).
        private static readonly int[][] FaceCorners =
        {
            new[] { 1,0,1, 1,1,1, 0,1,1, 0,0,1 }, // North +Z
            new[] { 0,0,0, 0,1,0, 1,1,0, 1,0,0 }, // South -Z
            new[] { 1,0,0, 1,1,0, 1,1,1, 1,0,1 }, // East +X
            new[] { 0,0,1, 0,1,1, 0,1,0, 0,0,0 }, // West -X
            new[] { 0,1,0, 0,1,1, 1,1,1, 1,1,0 }, // Up +Y
            new[] { 1,0,0, 1,0,1, 0,0,1, 0,0,0 }, // Down -Y
        };

        private static readonly Vector3[] FaceNormals =
        {
            new Vector3(0, 0, 1), new Vector3(0, 0, -1), new Vector3(1, 0, 0),
            new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0)
        };

        private static readonly Vector2[] BaseUv =
        {
            new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0)
        };

        private static readonly float[] FaceShade = { 0.82f, 0.82f, 0.68f, 0.68f, 1.0f, 0.52f };
        private static readonly float[] AoLevels = { 0.42f, 0.62f, 0.81f, 1.0f };
        private static readonly int[] NormalAxis = { 2, 2, 0, 0, 1, 1 };

        public static void Build(ChunkSnapshot s, MeshBlockTable t, MeshData mesh)
        {
            mesh.Clear();
            mesh.Coord = s.Coord;
            mesh.Version = s.Version;
            ComputeLight(s, t, mesh);

            const int S = WorldConstants.ChunkSize;
            const int O = ChunkSnapshot.Pad;
            var blocks = s.Blocks;
            var ao = new int[4];
            var verts = new Vector3[4];
            var sky = new byte[4];
            var blk = new byte[4];

            for (int y = 0; y < S; y++)
            for (int z = 0; z < S; z++)
            for (int x = 0; x < S; x++)
            {
                int pi = ChunkSnapshot.Index(x + O, y + O, z + O);
                ushort id = blocks[pi];
                if (id == 0 || id >= t.Render.Length) continue;
                byte render = t.Render[id];
                if (render == (byte)RenderLayer.Invisible) continue;

                var shape = (BlockShape)t.Shape[id];
                if (shape == BlockShape.Model) continue;
                if (shape == BlockShape.Cross)
                {
                    AddCross(s, mesh, t, id, x, y, z, mesh.Sky[pi], mesh.Blk[pi]);
                    continue;
                }
                if (shape == BlockShape.Torch)
                {
                    AddTorch(mesh, t, id, x, y, z);
                    continue;
                }

                bool liquid = t.Liquid[id];
                bool translucent = render == (byte)RenderLayer.Translucent;
                bool emissive = t.Emit[id] > 0;
                bool sway = t.Foliage[id];
                Direction facing = Direction.North;
                bool oriented = t.Orientable[id] && s.Facing.TryGetValue(Chunk.Index(x, y, z), out facing);
                bool lowered = liquid && blocks[ChunkSnapshot.Index(x + O, y + O + 1, z + O)] != id;

                for (int f = 0; f < 6; f++)
                {
                    var dir = (Direction)f;
                    var off = dir.Offset();
                    int px = x + O + off.X, py = y + O + off.Y, pz = z + O + off.Z;
                    ushort nb = blocks[ChunkSnapshot.Index(px, py, pz)];
                    if (t.IsOpaque(nb)) continue;
                    if (nb == id && render != (byte)RenderLayer.Cutout) continue;
                    if (liquid && nb < t.Liquid.Length && t.Liquid[nb]) continue;
                    // Вода сверху у поверхности — грань видна, даже если сосед прозрачный блок
                    if (!liquid && nb != 0 && nb < t.Render.Length && t.Render[nb] == render && render == (byte)RenderLayer.Translucent) continue;

                    int tex = t.Textures[id * 7 + f];
                    int uvRot = 0;
                    if (oriented)
                    {
                        if (dir == facing && !t.Conveyor[id]) tex = t.Textures[id * 7 + 6];
                        if (dir == Direction.Up && t.Conveyor[id]) uvRot = ConveyorUvRotation(facing);
                    }

                    var corners = FaceCorners[f];
                    int na = NormalAxis[f];
                    for (int v = 0; v < 4; v++)
                    {
                        int cx = corners[v * 3], cy = corners[v * 3 + 1], cz = corners[v * 3 + 2];
                        float vy = cy;
                        if (lowered && cy == 1) vy = 0.875f;
                        verts[v] = new Vector3(x + cx, y + vy, z + cz);
                        VertexLight(s, t, mesh, px, py, pz, na, cx, cy, cz, translucent, out ao[v], out sky[v], out blk[v]);
                    }

                    var list = translucent ? mesh.Translucent : mesh.Solid;
                    int baseIndex = mesh.Vertices.Count;
                    float shade = FaceShade[f];
                    byte flag = liquid ? FlagWater : (emissive ? FlagEmissive : (sway ? FlagSway : FlagNormal));
                    for (int v = 0; v < 4; v++)
                    {
                        mesh.Vertices.Add(verts[v]);
                        mesh.Normals.Add(FaceNormals[f]);
                        var uv = BaseUv[(v + uvRot) & 3];
                        mesh.Uvs.Add(new Vector3(uv.x, uv.y, tex));
                        byte b = (byte)Mathf.Clamp(shade * AoLevels[ao[v]] * 255f, 0, 255);
                        mesh.Colors.Add(new Color32(sky[v], blk[v], b, flag));
                        mesh.Tints.Add(TintOf(s, t, id, x, z, f));
                    }

                    // Диагональ квада — по AO и свету, чтобы не было «полосы»
                    int d0 = ao[0] + ao[2] + (sky[0] + sky[2] + blk[0] + blk[2]) / 64;
                    int d1 = ao[1] + ao[3] + (sky[1] + sky[3] + blk[1] + blk[3]) / 64;
                    if (d0 < d1)
                    {
                        list.Add(baseIndex + 1); list.Add(baseIndex + 2); list.Add(baseIndex + 3);
                        list.Add(baseIndex + 1); list.Add(baseIndex + 3); list.Add(baseIndex + 0);
                    }
                    else
                    {
                        list.Add(baseIndex + 0); list.Add(baseIndex + 1); list.Add(baseIndex + 2);
                        list.Add(baseIndex + 0); list.Add(baseIndex + 2); list.Add(baseIndex + 3);
                    }
                }
            }
        }

        // ================================================================ Окраска биома

        private static Vector3 TintOf(ChunkSnapshot s, MeshBlockTable t, ushort id, int x, int z, int face)
        {
            byte mode = id < t.Tint.Length ? t.Tint[id] : (byte)0;
            if (mode == 0) return Vector3.zero;
            var c = s.Tint[(z & 15) * 16 + (x & 15)];
            if (c.a == 0) return Vector3.zero;
            if (mode == 2 && face == 5) return Vector3.zero;
            float k = mode == 2 && face != 4 ? 0.5f : 1f; // бока травы — наполовину (там земля)
            return new Vector3((c.r / 128f - 1f) * k, (c.g / 128f - 1f) * k, (c.b / 128f - 1f) * k);
        }

        // ================================================================ Свет

        /// <summary>
        /// Свет неба: столб сверху без затухания до первого препятствия, дальше расползается BFS
        /// с шагом 2 за блок. Свет блоков — BFS от источников. Всё в пределах снимка 32³.
        /// </summary>
        private static void ComputeLight(ChunkSnapshot s, MeshBlockTable t, MeshData m)
        {
            const int P = ChunkSnapshot.P;
            const int N = P * P * P;
            if (m.Sky == null || m.Sky.Length != N)
            {
                m.Sky = new byte[N];
                m.Blk = new byte[N];
                m.Queue = new int[N * 2];
            }
            var sky = m.Sky;
            var blk = m.Blk;
            var q = m.Queue;
            System.Array.Clear(sky, 0, N);
            System.Array.Clear(blk, 0, N);
            var blocks = s.Blocks;
            var opacity = t.Opacity;

            // --- Небо: столбы сверху вниз
            int head = 0, tail = 0;
            for (int z = 0; z < P; z++)
            for (int x = 0; x < P; x++)
            {
                if (s.SkyCovered[z * P + x]) continue;
                int level = 15;
                for (int y = P - 1; y >= 0 && level > 0; y--)
                {
                    int i = ChunkSnapshot.Index(x, y, z);
                    ushort id = blocks[i];
                    int op = id < opacity.Length ? opacity[id] : 15;
                    if (op >= 15) break;
                    level -= op;
                    if (level <= 0) break;
                    sky[i] = (byte)level;
                    q[tail++] = i;
                }
            }
            Spread(sky, blocks, opacity, q, head, tail);

            // --- Блоки-источники
            tail = 0;
            var emit = t.Emit;
            for (int i = 0; i < N; i++)
            {
                ushort id = blocks[i];
                if (id < emit.Length && emit[id] > 0)
                {
                    blk[i] = emit[id];
                    q[tail++] = i;
                }
            }
            if (tail > 0) Spread(blk, blocks, opacity, q, 0, tail);
        }

        private static void Spread(byte[] light, ushort[] blocks, byte[] opacity, int[] q, int head, int tail)
        {
            const int P = ChunkSnapshot.P;
            int cap = q.Length;
            while (head != tail)
            {
                int i = q[head];
                head = (head + 1) % cap;
                int level = light[i] - LightStep;
                if (level <= 0) continue;
                int x = i % P, z = (i / P) % P, y = i / (P * P);
                for (int d = 0; d < 6; d++)
                {
                    int nx = x, ny = y, nz = z;
                    switch (d)
                    {
                        case 0: nx++; break;
                        case 1: nx--; break;
                        case 2: ny++; break;
                        case 3: ny--; break;
                        case 4: nz++; break;
                        default: nz--; break;
                    }
                    if ((uint)nx >= P || (uint)ny >= P || (uint)nz >= P) continue;
                    int ni = (ny * P + nz) * P + nx;
                    ushort id = blocks[ni];
                    int op = id < opacity.Length ? opacity[id] : 15;
                    if (op >= 15) continue;
                    int nl = level - op;
                    if (nl <= light[ni]) continue;
                    light[ni] = (byte)nl;
                    q[tail] = ni;
                    tail = (tail + 1) % cap;
                }
            }
        }

        /// <summary>
        /// AO и гладкий свет вершины: (px,py,pz) — клетка перед гранью (координаты снимка),
        /// na — ось нормали, cx/cy/cz — угол грани (0/1). Свет — среднее по прозрачным клеткам у угла.
        /// </summary>
        private static void VertexLight(ChunkSnapshot snap, MeshBlockTable t, MeshData m, int px, int py, int pz, int na,
            int cx, int cy, int cz, bool flat, out int ao, out byte sky, out byte blk)
        {
            var b = snap.Blocks;
            int sx = cx == 1 ? 1 : -1, sy = cy == 1 ? 1 : -1, sz = cz == 1 ? 1 : -1;
            int ax, ay, az, bx, by, bz;
            if (na == 0) { ax = 0; ay = sy; az = 0; bx = 0; by = 0; bz = sz; }
            else if (na == 1) { ax = sx; ay = 0; az = 0; bx = 0; by = 0; bz = sz; }
            else { ax = sx; ay = 0; az = 0; bx = 0; by = sy; bz = 0; }

            int i0 = Idx(px, py, pz);
            int i1 = Idx(px + ax, py + ay, pz + az);
            int i2 = Idx(px + bx, py + by, pz + bz);
            int i3 = Idx(px + ax + bx, py + ay + by, pz + az + bz);

            bool s1 = i1 >= 0 && t.IsOpaque(b[i1]);
            bool s2 = i2 >= 0 && t.IsOpaque(b[i2]);
            bool c = i3 >= 0 && t.IsOpaque(b[i3]);
            ao = flat ? 3 : (s1 && s2 ? 0 : 3 - ((s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0)));

            int skySum = 0, blkSum = 0, n = 0;
            Accumulate(m, i0, ref skySum, ref blkSum, ref n);
            if (!s1) Accumulate(m, i1, ref skySum, ref blkSum, ref n);
            if (!s2) Accumulate(m, i2, ref skySum, ref blkSum, ref n);
            if (!c && !(s1 && s2)) Accumulate(m, i3, ref skySum, ref blkSum, ref n);
            if (n == 0) n = 1;
            sky = (byte)Mathf.Min(255, skySum * 17 / n);
            blk = (byte)Mathf.Min(255, blkSum * 17 / n);
        }

        private static void Accumulate(MeshData m, int i, ref int sky, ref int blk, ref int n)
        {
            if (i < 0) return;
            sky += m.Sky[i];
            blk += m.Blk[i];
            n++;
        }

        private static int Idx(int x, int y, int z)
        {
            const int P = ChunkSnapshot.P;
            if ((uint)x >= P || (uint)y >= P || (uint)z >= P) return -1;
            return ChunkSnapshot.Index(x, y, z);
        }

        // ================================================================ Формы

        /// <summary>Растения: два диагональных двусторонних квада, верх покачивается.</summary>
        private static void AddCross(ChunkSnapshot s, MeshData mesh, MeshBlockTable t, ushort id, int x, int y, int z, byte skyLevel, byte blkLevel)
        {
            int tex = t.Textures[id * 7 + (int)Direction.North];
            byte sky = (byte)Mathf.Min(255, skyLevel * 17);
            byte blk = (byte)Mathf.Min(255, Mathf.Max(blkLevel, t.Emit[id]) * 17);
            const float a = 0.15f, b = 0.85f;
            var quads = new[]
            {
                new Vector3(x + a, y, z + a), new Vector3(x + a, y + 1, z + a), new Vector3(x + b, y + 1, z + b), new Vector3(x + b, y, z + b),
                new Vector3(x + b, y, z + a), new Vector3(x + b, y + 1, z + a), new Vector3(x + a, y + 1, z + b), new Vector3(x + a, y, z + b)
            };
            for (int qd = 0; qd < 2; qd++)
            {
                for (int side = 0; side < 2; side++)
                {
                    int bi = mesh.Vertices.Count;
                    for (int v = 0; v < 4; v++)
                    {
                        int vi = side == 0 ? v : 3 - v;
                        var p = quads[qd * 4 + vi];
                        mesh.Vertices.Add(p);
                        mesh.Normals.Add(Vector3.up);
                        var uv = BaseUv[vi];
                        mesh.Uvs.Add(new Vector3(uv.x, uv.y, tex));
                        bool top = p.y > y + 0.5f;
                        mesh.Colors.Add(new Color32(sky, blk, 220, top ? FlagSway : FlagNormal));
                        mesh.Tints.Add(TintOf(s, t, id, x, z, 4));
                    }
                    mesh.Solid.Add(bi); mesh.Solid.Add(bi + 1); mesh.Solid.Add(bi + 2);
                    mesh.Solid.Add(bi); mesh.Solid.Add(bi + 2); mesh.Solid.Add(bi + 3);
                }
            }
        }

        /// <summary>Факел: тонкий брусок 2×10/16 в центре блока, UV — центральная полоса текстуры.</summary>
        private static void AddTorch(MeshData mesh, MeshBlockTable t, ushort id, int x, int y, int z)
        {
            int tex = t.Textures[id * 7 + (int)Direction.North];
            const float w0 = 7f / 16f, w1 = 9f / 16f, h = 10f / 16f;
            var min = new Vector3(x + w0, y, z + w0);
            var max = new Vector3(x + w1, y + h, z + w1);
            byte full = 255;
            for (int f = 0; f < 6; f++)
            {
                if (f == (int)Direction.Down) continue;
                var c = FaceCorners[f];
                int bi = mesh.Vertices.Count;
                for (int v = 0; v < 4; v++)
                {
                    var p = new Vector3(c[v * 3] == 1 ? max.x : min.x, c[v * 3 + 1] == 1 ? max.y : min.y, c[v * 3 + 2] == 1 ? max.z : min.z);
                    mesh.Vertices.Add(p);
                    mesh.Normals.Add(FaceNormals[f]);
                    var uv = BaseUv[v];
                    // Боковые грани: полоса 7..9/16 по U, 0..10/16 по V; верх — пламя 7..9/16
                    float u = Mathf.Lerp(w0, w1, uv.x);
                    float vv = f == (int)Direction.Up ? Mathf.Lerp(h - 2f / 16f, h, uv.y) : uv.y * h;
                    mesh.Uvs.Add(new Vector3(u, vv, tex));
                    mesh.Colors.Add(new Color32(full, full, (byte)(FaceShade[f] * 255), FlagEmissive));
                    mesh.Tints.Add(Vector3.zero);
                }
                mesh.Solid.Add(bi); mesh.Solid.Add(bi + 1); mesh.Solid.Add(bi + 2);
                mesh.Solid.Add(bi); mesh.Solid.Add(bi + 2); mesh.Solid.Add(bi + 3);
            }
        }

        /// <summary>Сдвиг UV, чтобы стрелки ленты смотрели по направлению движения.</summary>
        public static int ConveyorUvRotation(Direction facing)
        {
            switch (facing)
            {
                case Direction.East: return 3;
                case Direction.South: return 2;
                case Direction.West: return 1;
                default: return 0;
            }
        }
    }
}
