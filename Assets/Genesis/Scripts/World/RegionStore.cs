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
            /// <summary>Номер последнего сохранения, при котором к региону обращались (для выгрузки из памяти).</summary>
            public int Touched;
        }

        /// <summary>Готовый к записи на диск снимок региона (пишется в фоновом потоке).</summary>
        public sealed class RegionWrite
        {
            public string Path;
            public List<KeyValuePair<Int3, byte[]>> Chunks;
        }

        private readonly Dictionary<(int, int), Region> _regions = new Dictionary<(int, int), Region>();
        private int _generation;

        /// <summary>Регион не прочитался ни из файла, ни из копии (файл отложен как .corrupt-…).</summary>
        public event Action<string> RegionDamaged;

        public RegionStore(string directory, ContentDatabase content)
        {
            _dir = directory;
            _content = content;
            if (_dir != null)
            {
                Directory.CreateDirectory(_dir);
                foreach (var tmp in Directory.GetFiles(_dir, "*" + SafeFile.TmpExt))
                {
                    try { File.Delete(tmp); } catch (IOException) { }
                }
            }
        }

        /// <summary>Сколько регионов держится в памяти.</summary>
        public int LoadedRegions => _regions.Count;

        private static (int, int) RegionKey(Int3 chunk) =>
            (Int3.FloorDiv(chunk.X, RegionChunks), Int3.FloorDiv(chunk.Z, RegionChunks));

        private string PathOf((int, int) key) => System.IO.Path.Combine(_dir, $"r.{key.Item1}.{key.Item2}.gcr");

        private Region GetRegion(Int3 chunk)
        {
            var key = RegionKey(chunk);
            if (_regions.TryGetValue(key, out var region))
            {
                region.Touched = _generation;
                return region;
            }
            region = new Region { Touched = _generation };
            _regions[key] = region;
            if (_dir == null) return region;
            string path = PathOf(key);
            string bak = path + SafeFile.BakExt;
            if (!File.Exists(path) && !File.Exists(bak)) return region;

            // Основной файл → копия .bak. Повреждённый файл НЕ перезаписывается: он откладывается как .corrupt-…,
            // а прочитанное (из копии или целая часть основного) сохраняется заново.
            var partial = new Dictionary<Int3, byte[]>();
            string error = null;
            if (File.Exists(path) && TryReadRegion(path, region.Chunks, out error)) return region;
            if (File.Exists(path))
            {
                foreach (var kv in region.Chunks) partial[kv.Key] = kv.Value;
                region.Chunks.Clear();
                Log.Error($"Failed to read region {path}: {error}");
            }
            bool fromBackup = File.Exists(bak) && TryReadRegion(bak, region.Chunks, out _);
            if (!fromBackup)
            {
                region.Chunks.Clear();
                foreach (var kv in partial) region.Chunks[kv.Key] = kv.Value; // что успели прочитать до ошибки
            }
            else
            {
                // Чанки, которые есть только в целой части основного файла (новее копии), тоже берём
                foreach (var kv in partial) region.Chunks[kv.Key] = kv.Value;
            }
            if (File.Exists(path))
            {
                string moved = SafeFile.Quarantine(path);
                RegionDamaged?.Invoke(moved ?? path);
            }
            region.Dirty = true; // записать восстановленное
            Log.Warn($"Region {System.IO.Path.GetFileName(path)}: restored {region.Chunks.Count} chunks" + (fromBackup ? " from backup" : ""));
            return region;
        }

        private static bool TryReadRegion(string path, Dictionary<Int3, byte[]> into, out string error)
        {
            error = null;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = SafeFile.DecompressReader(fs))
                using (var r = new BinaryReader(gz))
                {
                    if (r.ReadInt32() != Magic) throw new InvalidDataException("bad magic");
                    int ver = r.ReadInt32();
                    if (ver > Version) throw new InvalidDataException("newer region version " + ver);
                    int count = r.ReadInt32();
                    if (count < 0 || count > RegionChunks * RegionChunks * 64) throw new InvalidDataException("bad chunk count");
                    for (int i = 0; i < count; i++)
                    {
                        var c = new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                        int len = r.ReadInt32();
                        if (len < 0 || len > 1 << 22) throw new InvalidDataException("bad chunk length");
                        var bytes = r.ReadBytes(len);
                        if (bytes.Length != len) throw new EndOfStreamException();
                        into[c] = bytes;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
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

        /// <summary>Записать изменённые регионы на диск (синхронно).</summary>
        public void Flush()
        {
            foreach (var w in SnapshotDirty()) WriteRegion(w);
        }

        /// <summary>
        /// Снимок изменённых регионов для записи (главный поток): байты чанков неизменяемы, поэтому копии
        /// списка достаточно. Заодно выгружает из памяти давно не используемые неизменённые регионы.
        /// </summary>
        public List<RegionWrite> SnapshotDirty()
        {
            var list = new List<RegionWrite>();
            if (_dir == null) return list;
            foreach (var kv in _regions)
            {
                if (!kv.Value.Dirty) continue;
                list.Add(new RegionWrite { Path = PathOf(kv.Key), Chunks = new List<KeyValuePair<Int3, byte[]>>(kv.Value.Chunks) });
                kv.Value.Dirty = false;
            }
            EvictIdle();
            _generation++;
            return list;
        }

        /// <summary>Регион не трогали два сохранения подряд и он сохранён — данные есть на диске, из памяти убираем.</summary>
        private void EvictIdle()
        {
            List<(int, int)> idle = null;
            foreach (var kv in _regions)
                if (!kv.Value.Dirty && _generation - kv.Value.Touched >= 2)
                    (idle ?? (idle = new List<(int, int)>())).Add(kv.Key);
            if (idle != null)
                foreach (var k in idle) _regions.Remove(k);
        }

        /// <summary>Запись не удалась — пометить регионы изменёнными снова (главный поток).</summary>
        public void MarkDirty(List<RegionWrite> failed)
        {
            foreach (var w in failed)
                foreach (var kv in _regions)
                    if (PathOf(kv.Key) == w.Path)
                    {
                        kv.Value.Dirty = true;
                        break;
                    }
        }

        /// <summary>Сжать и атомарно записать регион (можно из фонового потока).</summary>
        public static void WriteRegion(RegionWrite region)
        {
            byte[] data;
            using (var ms = new MemoryStream())
            {
                using (var gz = SafeFile.CompressWriter(ms, true))
                using (var w = new BinaryWriter(gz))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(region.Chunks.Count);
                    foreach (var c in region.Chunks)
                    {
                        w.Write(c.Key.X);
                        w.Write(c.Key.Y);
                        w.Write(c.Key.Z);
                        w.Write(c.Value.Length);
                        w.Write(c.Value);
                    }
                }
                data = ms.ToArray();
            }
            SafeFile.WriteAllBytes(region.Path, data);
        }
    }
}
