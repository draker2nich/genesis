using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Приближённая освещённость точки мира для динамических объектов (предметы на лентах, рука, частицы, аватары):
    /// открыто ли небо над точкой и насколько близко светящиеся блоки. Светящиеся блоки индексируются по чанкам
    /// при загрузке и изменении, поэтому выборка дешёвая. Небо кешируется на пару секунд.
    /// </summary>
    public sealed class LightProbe : IDisposable
    {
        private const int SkyScan = 48;
        private const float CacheSeconds = 1.5f;

        private readonly VoxelWorld _world;
        private readonly MeshBlockTable _table;
        private readonly Dictionary<Int3, Entry> _cache = new Dictionary<Int3, Entry>();
        private readonly Dictionary<Int3, List<Int3>> _emitters = new Dictionary<Int3, List<Int3>>();

        private struct Entry
        {
            public byte Sky;
            public float Time;
        }

        public LightProbe(VoxelWorld world, MeshBlockTable table)
        {
            _world = world;
            _table = table;
            _world.ChunkLoaded += OnChunkLoaded;
            _world.ChunkUnloaded += OnChunkUnloaded;
            _world.BlockChanged += OnBlockChanged;
            foreach (var c in _world.Chunks) OnChunkLoaded(c);
        }

        public void Dispose()
        {
            _world.ChunkLoaded -= OnChunkLoaded;
            _world.ChunkUnloaded -= OnChunkUnloaded;
            _world.BlockChanged -= OnBlockChanged;
        }

        private void OnChunkLoaded(Chunk c)
        {
            _emitters.Remove(c.Coord);
            if (c.IsUniform)
            {
                if (Emit(c.UniformBlock) == 0) return;
            }
            var origin = WorldConstants.ChunkOrigin(c.Coord);
            List<Int3> list = null;
            for (int i = 0; i < WorldConstants.ChunkVolume; i++)
            {
                if (Emit(c.GetByIndex(i)) == 0) continue;
                list = list ?? new List<Int3>();
                list.Add(origin + new Int3(i & 15, i >> 8, (i >> 4) & 15));
            }
            if (list != null) _emitters[c.Coord] = list;
        }

        private void OnChunkUnloaded(Int3 coord) => _emitters.Remove(coord);

        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock)
        {
            if (Emit(oldBlock) == 0 && Emit(newBlock) == 0) return;
            var c = WorldConstants.ChunkOf(pos);
            if (!_emitters.TryGetValue(c, out var list)) _emitters[c] = list = new List<Int3>();
            list.Remove(pos);
            if (Emit(newBlock) > 0) list.Add(pos);
        }

        private int Emit(ushort id) => id < _table.Emit.Length ? _table.Emit[id] : 0;

        /// <summary>Свет в точке: sky и blk в диапазоне 0..255 (как в вершинах чанков).</summary>
        public void Sample(Int3 p, out byte sky, out byte blk)
        {
            float now = Time.time;
            if (_cache.TryGetValue(p, out var e) && now - e.Time < CacheSeconds) sky = e.Sky;
            else
            {
                if (_cache.Count > 4096) _cache.Clear();
                sky = 255;
                for (int y = p.Y + 1; y <= p.Y + SkyScan; y++)
                {
                    if (y > WorldConstants.MaxY || !_world.IsLoaded(p.X, y, p.Z)) break;
                    if (_table.IsOpaque(_world.GetBlock(p.X, y, p.Z)))
                    {
                        sky = 25;
                        break;
                    }
                }
                _cache[p] = new Entry { Sky = sky, Time = now };
            }

            int best = 0;
            var cc = WorldConstants.ChunkOf(p);
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!_emitters.TryGetValue(new Int3(cc.X + dx, cc.Y + dy, cc.Z + dz), out var list)) continue;
                foreach (var q in list)
                {
                    int dist = Mathf.Abs(q.X - p.X) + Mathf.Abs(q.Y - p.Y) + Mathf.Abs(q.Z - p.Z);
                    int level = Emit(_world.GetBlock(q)) - dist * 2;
                    if (level > best) best = level;
                }
            }
            blk = (byte)Mathf.Clamp(best * 17, 0, 255);
        }
    }
}
