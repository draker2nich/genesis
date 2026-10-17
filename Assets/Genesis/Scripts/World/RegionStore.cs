using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.World
{
    /// <summary>
    /// Кодирование чанка: палитра строковых ID (устойчива к смене порядка загрузки модов) + RLE.
    /// </summary>
    public static class ChunkCodec
    {
        public static void Write(BinaryWriter w, Chunk chunk, ContentDatabase content)
        {
            var palette = new List<ushort>();
            var paletteIndex = new Dictionary<ushort, ushort>();
            var data = new ushort[WorldConstants.ChunkVolume];
            chunk.CopyTo(data);
            for (int i = 0; i < data.Length; i++)
            {
                if (!paletteIndex.ContainsKey(data[i]))
                {
                    paletteIndex[data[i]] = (ushort)palette.Count;
                    palette.Add(data[i]);
                }
            }

            w.Write((ushort)palette.Count);
            foreach (var id in palette) w.Write(content.Blocks.IdOf(id) ?? ContentDatabase.AirId);

            // RLE: (длина серии, индекс в палитре)
            int pos = 0;
            var runs = new List<(ushort, ushort)>();
            while (pos < data.Length)
            {
                ushort v = data[pos];
                int len = 1;
                while (pos + len < data.Length && data[pos + len] == v && len < ushort.MaxValue) len++;
                runs.Add(((ushort)len, paletteIndex[v]));
                pos += len;
            }
            w.Write(runs.Count);
            foreach (var r in runs)
            {
                w.Write(r.Item1);
                w.Write(r.Item2);
            }
        }

        public static Chunk Read(BinaryReader r, Int3 coord, ContentDatabase content)
        {
            int paletteCount = r.ReadUInt16();
            var palette = new ushort[paletteCount];
            for (int i = 0; i < paletteCount; i++)
            {
                string id = r.ReadString();
                int idx = content.Blocks.IndexOf(id);
                if (idx < 0)
                {
                    Log.Warn($"Unknown block '{id}' in save, replaced with air");
                    idx = 0;
                }
                palette[i] = (ushort)idx;
            }

            var data = new ushort[WorldConstants.ChunkVolume];
            int runs = r.ReadInt32();
            int pos = 0;
            for (int i = 0; i < runs; i++)
            {
                int len = r.ReadUInt16();
                ushort v = palette[r.ReadUInt16()];
                for (int k = 0; k < len && pos < data.Length; k++) data[pos++] = v;
            }
            var chunk = new Chunk(coord, data) { Modified = true };
            return chunk;
        }
    }

    /// <summary>
    /// Регион-файлы: 32×32 колонки чанков на файл, хранятся только изменённые чанки.
    /// Немодифицированные чанки всегда восстанавливаются из seed.
    /// </summary>
    public sealed class RegionStore
    {
        public const int RegionChunks = 32;
        private const int Magic = 0x47524731; // "GRG1"
        private const int Version = 1;

        private readonly string _dir;
        private readonly ContentDatabase _content;

        private sealed class Region
        {
            public readonly Dictionary<Int3, byte[]> Chunks = new Dictionary<Int3, byte[]>();
            public bool Dirty;
        }

        private readonly Dictionary<(int, int), Region> _regions = new Dictionary<(int, int), Region>();

        public RegionStore(string directory, ContentDatabase content)
        {
            _dir = directory;
            _content = content;
            if (_dir != null) Directory.CreateDirectory(_dir);
        }

        private static (int, int) RegionKey(Int3 chunk) =>
            (Int3.FloorDiv(chunk.X, RegionChunks), Int3.FloorDiv(chunk.Z, RegionChunks));

        private string PathOf((int, int) key) => System.IO.Path.Combine(_dir, $"r.{key.Item1}.{key.Item2}.gcr");

        private Region GetRegion(Int3 chunk)
        {
            var key = RegionKey(chunk);
            if (_regions.TryGetValue(key, out var region)) return region;
            region = new Region();
            _regions[key] = region;
            if (_dir == null) return region;
            string path = PathOf(key);
            if (!File.Exists(path)) return region;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                {
                    if (r.ReadInt32() != Magic) throw new InvalidDataException("bad magic");
                    int ver = r.ReadInt32();
                    if (ver > Version) throw new InvalidDataException("newer region version " + ver);
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var c = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                        int len = r.ReadInt32();
                        region.Chunks[c] = r.ReadBytes(len);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"Failed to read region {path}: {e.Message}");
            }
            return region;
        }

        public bool Has(Int3 chunk) => GetRegion(chunk).Chunks.ContainsKey(chunk);

        /// <summary>Закодированные байты сохранённого чанка (формат ChunkCodec) — для отправки клиентам.</summary>
        public bool TryGetRaw(Int3 chunk, out byte[] bytes) => GetRegion(chunk).Chunks.TryGetValue(chunk, out bytes);

        public Chunk Load(Int3 chunk)
        {
            var region = GetRegion(chunk);
            if (!region.Chunks.TryGetValue(chunk, out var bytes)) return null;
            using (var ms = new MemoryStream(bytes))
            using (var r = new BinaryReader(ms))
                return ChunkCodec.Read(r, chunk, _content);
        }

        public void Store(Chunk chunk)
        {
            var region = GetRegion(chunk.Coord);
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    ChunkCodec.Write(w, chunk, _content);
                    w.Flush();
                    region.Chunks[chunk.Coord] = ms.ToArray();
                }
            }
            region.Dirty = true;
        }

        /// <summary>Записать изменённые регионы на диск.</summary>
        public void Flush()
        {
            if (_dir == null) return;
            foreach (var kv in _regions)
            {
                if (!kv.Value.Dirty) continue;
                string path = PathOf(kv.Key);
                string tmp = path + ".tmp";
                using (var fs = File.Create(tmp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                using (var w = new BinaryWriter(gz))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(kv.Value.Chunks.Count);
                    foreach (var c in kv.Value.Chunks)
                    {
                        w.Write(c.Key.X);
                        w.Write(c.Key.Y);
                        w.Write(c.Key.Z);
                        w.Write(c.Value.Length);
                        w.Write(c.Value);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                kv.Value.Dirty = false;
            }
        }
    }
}
