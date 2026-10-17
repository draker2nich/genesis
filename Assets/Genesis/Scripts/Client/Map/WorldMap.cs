using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Genesis.Client.Rendering;
using Genesis.Core;
using Genesis.Data;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client.Map
{
    /// <summary>Метка на карте.</summary>
    public sealed class MapWaypoint
    {
        public string Name;
        public int X, Y, Z;
        public Color32 Color;
    }

    /// <summary>
    /// Карта исследованного мира (как карты в Minecraft): для каждого загруженного столбца чанков — цвет верхнего
    /// блока с окраской биома, тенями по рельефу и глубиной воды. Хранится по регионам 256×256 блоков
    /// (одна текстура на регион), сохраняется в файл мира, обновляется при загрузке чанков и изменениях блоков.
    /// </summary>
    public sealed class WorldMap : MonoBehaviour
    {
        public const int RegionChunks = 16;
        public const int RegionSize = RegionChunks * WorldConstants.ChunkSize; // 256 блоков
        private const int ColumnsPerFrame = 6;

        private sealed class Region
        {
            public Texture2D Texture;
            public Color32[] Pixels;
            public bool Dirty;
            public readonly bool[] Explored = new bool[RegionChunks * RegionChunks];
        }

        private ClientContext _ctx;
        private string _file;
        private VoxelWorld _world;
        private ContentDatabase _content;
        private MeshBlockTable _table;
        private readonly Dictionary<long, Region> _regions = new Dictionary<long, Region>();
        private readonly Queue<long> _queue = new Queue<long>();
        private readonly HashSet<long> _queued = new HashSet<long>();
        public readonly List<MapWaypoint> Waypoints = new List<MapWaypoint>();
        private static readonly Color32 Unknown = new Color32(0, 0, 0, 0);

        public void Init(ClientContext ctx, string file)
        {
            _ctx = ctx;
            _file = file;
            _world = ctx.Session.World;
            _content = ctx.Content;
            _table = new MeshBlockTable(_content);
            Load();
            _world.ChunkLoaded += OnChunkLoaded;
            _world.BlockChanged += OnBlockChanged;
            foreach (var c in _world.Chunks) OnChunkLoaded(c);
        }

        private void OnDestroy()
        {
            if (_world != null)
            {
                _world.ChunkLoaded -= OnChunkLoaded;
                _world.BlockChanged -= OnBlockChanged;
            }
            Save();
            foreach (var r in _regions.Values)
                if (r.Texture != null) Destroy(r.Texture);
        }

        private static long Key(int a, int b) => ((long)a << 32) ^ (uint)b;

        private void OnChunkLoaded(Chunk c) => Enqueue(c.Coord.X, c.Coord.Z);

        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock) =>
            Enqueue(Int3.FloorDiv(pos.X, WorldConstants.ChunkSize), Int3.FloorDiv(pos.Z, WorldConstants.ChunkSize));

        private void Enqueue(int cx, int cz)
        {
            long k = Key(cx, cz);
            if (_queued.Add(k)) _queue.Enqueue(k);
        }

        private void Update()
        {
            if (_world == null) return;
            for (int i = 0; i < ColumnsPerFrame && _queue.Count > 0; i++)
            {
                long k = _queue.Dequeue();
                _queued.Remove(k);
                RenderColumn((int)(k >> 32), (int)(uint)k);
            }
            foreach (var r in _regions.Values)
                if (r.Dirty)
                {
                    r.Texture.SetPixels32(r.Pixels);
                    r.Texture.Apply(false);
                    r.Dirty = false;
                }
        }

        // ============================================================ Доступ для интерфейса

        /// <summary>Текстура региона (rx, rz в регионах). null — регион не исследован.</summary>
        public Texture2D RegionTexture(int rx, int rz) => _regions.TryGetValue(Key(rx, rz), out var r) ? r.Texture : null;

        public bool IsExplored(int x, int z)
        {
            int rx = Int3.FloorDiv(x, RegionSize), rz = Int3.FloorDiv(z, RegionSize);
            if (!_regions.TryGetValue(Key(rx, rz), out var r)) return false;
            int lcx = Int3.FloorDiv(x - rx * RegionSize, WorldConstants.ChunkSize), lcz = Int3.FloorDiv(z - rz * RegionSize, WorldConstants.ChunkSize);
            return r.Explored[lcz * RegionChunks + lcx];
        }

        public void AddWaypoint(string name, Int3 pos, Color32 color)
        {
            Waypoints.Add(new MapWaypoint { Name = name, X = pos.X, Y = pos.Y, Z = pos.Z, Color = color });
        }

        // ============================================================ Отрисовка столбца чанков

        private Region GetRegion(int rx, int rz)
        {
            long k = Key(rx, rz);
            if (_regions.TryGetValue(k, out var r)) return r;
            r = new Region { Pixels = new Color32[RegionSize * RegionSize] };
            r.Texture = new Texture2D(RegionSize, RegionSize, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = $"MapRegion {rx},{rz}"
            };
            r.Texture.SetPixels32(r.Pixels);
            r.Texture.Apply(false);
            _regions[k] = r;
            return r;
        }

        private void RenderColumn(int cx, int cz)
        {
            const int S = WorldConstants.ChunkSize;
            int ox = cx * S, oz = cz * S;
            // Нужна хотя бы поверхность: чанк с верхним блоком колонки должен быть загружен
            var gen = _world.Generator;
            int rx = Int3.FloorDiv(cx, RegionChunks), rz = Int3.FloorDiv(cz, RegionChunks);
            var region = GetRegion(rx, rz);
            int lcx = cx - rx * RegionChunks, lcz = cz - rz * RegionChunks;
            bool any = false;
            var heights = new int[(S + 1) * S];

            for (int lz = 0; lz < S; lz++)
            for (int lx = -1; lx < S; lx++)
            {
                int x = ox + lx, z = oz + lz;
                var col = gen.ColumnAt(x, z);
                int top = Math.Max(col.Height + (int)(col.Amp * 2) + 4, WorldConstants.SeaLevel + 1);
                if (col.Island > 0.02f) top = Math.Max(top, (int)col.IslandY + 12);
                top = Math.Min(top, WorldConstants.MaxY);
                if (!SurfaceAt(x, top, z, out int y, out ushort block, out int waterDepth))
                {
                    if (lx >= 0) heights[lz * (S + 1) + lx + 1] = int.MinValue;
                    continue;
                }
                heights[lz * (S + 1) + lx + 1] = y;
                if (lx < 0) continue;

                // Тень по рельефу: сравнение с соседом на западе (как в картах Minecraft — «освещение» с северо-запада)
                int west = heights[lz * (S + 1) + lx];
                float shade = 1f;
                if (west != int.MinValue && waterDepth == 0)
                {
                    if (y > west) shade = 1.12f;
                    else if (y < west) shade = 0.84f;
                }
                var color = ColorOf(block, col, waterDepth) ;
                color = new Color32((byte)Mathf.Clamp(color.r * shade, 0, 255), (byte)Mathf.Clamp(color.g * shade, 0, 255), (byte)Mathf.Clamp(color.b * shade, 0, 255), 255);
                int px = lcx * S + lx, py = RegionSize - 1 - (lcz * S + lz); // строка 0 текстуры — юг (низ карты)
                region.Pixels[py * RegionSize + px] = color;
                any = true;
            }
            if (any)
            {
                region.Explored[lcz * RegionChunks + lcx] = true;
                region.Dirty = true;
            }
        }

        /// <summary>Верхний видимый блок колонки (растения-кресты пропускаем), глубина воды над дном.</summary>
        private bool SurfaceAt(int x, int top, int z, out int y, out ushort block, out int waterDepth)
        {
            waterDepth = 0;
            block = 0;
            y = top;
            bool seenLoaded = false;
            for (int yy = top; yy > top - 200 && yy > WorldConstants.MinY; yy--)
            {
                if (!_world.IsLoaded(x, yy, z))
                {
                    if (seenLoaded) return false;
                    continue;
                }
                seenLoaded = true;
                ushort id = _world.GetBlock(x, yy, z);
                if (id == 0) continue;
                if (id < _table.Render.Length)
                {
                    if (_table.Render[id] == (byte)RenderLayer.Invisible) continue;
                    if (_table.Shape[id] == (byte)BlockShape.Cross && waterDepth == 0) continue;
                    if (_table.Liquid[id])
                    {
                        if (waterDepth == 0) y = yy;
                        waterDepth++;
                        if (waterDepth > 12) break;
                        continue;
                    }
                }
                if (waterDepth == 0) y = yy;
                block = id;
                return true;
            }
            if (waterDepth > 0)
            {
                block = _content.BlockId("genesis:water");
                return true;
            }
            return false;
        }

        private Color32 ColorOf(ushort block, in ColumnInfo col, int waterDepth)
        {
            var def = _content.Block(block);
            var colors = _ctx.Textures.AverageColors;
            int layer = def.FaceTexture[(int)Direction.Up];
            Color c = layer >= 0 && layer < colors.Length ? (Color)colors[layer] : Color.magenta;
            if (def.Tint != null)
            {
                var t = WorldRenderer.BiomeTint(col);
                c = new Color(c.r * t.r / 128f, c.g * t.g / 128f, c.b * t.b / 128f);
            }
            if (waterDepth > 0)
            {
                var water = new Color(0.20f, 0.38f, 0.72f);
                float k = Mathf.Clamp01(0.45f + waterDepth * 0.06f);
                c = Color.Lerp(c, water, k) * (1f - Mathf.Min(0.35f, waterDepth * 0.025f));
            }
            c.a = 1;
            return c;
        }

        // ============================================================ Сохранение

        public void Save()
        {
            if (string.IsNullOrEmpty(_file) || _regions.Count == 0 && Waypoints.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                using (var fs = File.Create(_file))
                using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
                using (var w = new BinaryWriter(gz))
                {
                    w.Write(1);
                    w.Write(_regions.Count);
                    foreach (var kv in _regions)
                    {
                        w.Write(kv.Key);
                        for (int i = 0; i < kv.Value.Explored.Length; i++) w.Write(kv.Value.Explored[i]);
                        var px = kv.Value.Pixels;
                        var bytes = new byte[px.Length * 3];
                        for (int i = 0; i < px.Length; i++)
                        {
                            bytes[i * 3] = px[i].r;
                            bytes[i * 3 + 1] = px[i].g;
                            bytes[i * 3 + 2] = px[i].a == 0 ? (byte)1 : px[i].b; // 0,0,1 — «не исследовано»
                            if (px[i].a == 0) bytes[i * 3] = bytes[i * 3 + 1] = 0;
                        }
                        w.Write(bytes);
                    }
                    w.Write(Waypoints.Count);
                    foreach (var p in Waypoints)
                    {
                        w.Write(p.Name ?? "");
                        w.Write(p.X);
                        w.Write(p.Y);
                        w.Write(p.Z);
                        w.Write(p.Color.r);
                        w.Write(p.Color.g);
                        w.Write(p.Color.b);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Map] save failed: " + e.Message);
            }
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_file) || !File.Exists(_file)) return;
            try
            {
                using (var fs = File.OpenRead(_file))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                {
                    r.ReadInt32();
                    int n = r.ReadInt32();
                    for (int k = 0; k < n; k++)
                    {
                        long key = r.ReadInt64();
                        var region = GetRegion((int)(key >> 32), (int)(uint)key);
                        for (int i = 0; i < region.Explored.Length; i++) region.Explored[i] = r.ReadBoolean();
                        var bytes = r.ReadBytes(region.Pixels.Length * 3);
                        for (int i = 0; i < region.Pixels.Length; i++)
                        {
                            byte cr = bytes[i * 3], cg = bytes[i * 3 + 1], cb = bytes[i * 3 + 2];
                            region.Pixels[i] = cr == 0 && cg == 0 && cb == 1 ? Unknown : new Color32(cr, cg, cb, 255);
                        }
                        region.Dirty = true;
                    }
                    int wp = r.ReadInt32();
                    for (int i = 0; i < wp; i++)
                        Waypoints.Add(new MapWaypoint
                        {
                            Name = r.ReadString(),
                            X = r.ReadInt32(),
                            Y = r.ReadInt32(),
                            Z = r.ReadInt32(),
                            Color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255)
                        });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Map] load failed: " + e.Message);
            }
        }
    }
}
