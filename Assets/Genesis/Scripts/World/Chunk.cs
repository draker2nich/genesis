using System;
using Genesis.Core;

namespace Genesis.World
{
    public static class WorldConstants
    {
        public const int ChunkSize = 16;
        public const int ChunkBits = 4;
        public const int ChunkMask = 15;
        public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

        public const int MinY = -64;
        public const int MaxY = 319; // включительно → высота 384
        public const int Height = MaxY - MinY + 1;
        public const int MinChunkY = MinY >> ChunkBits; // -4
        public const int MaxChunkY = MaxY >> ChunkBits; // 19

        /// <summary>Мир 8192×8192: координаты X/Z в [-4096, 4096).</summary>
        public const int WorldSize = 8192;
        public const int HalfWorld = WorldSize / 2;
        public const int MinChunkXZ = -HalfWorld >> ChunkBits;      // -256
        public const int MaxChunkXZ = (HalfWorld - 1) >> ChunkBits; // 255

        public const int SeaLevel = 62;

        public static bool InHorizontalBounds(int x, int z) =>
            x >= -HalfWorld && x < HalfWorld && z >= -HalfWorld && z < HalfWorld;

        public static bool ChunkInBounds(Int3 c) =>
            c.X >= MinChunkXZ && c.X <= MaxChunkXZ &&
            c.Z >= MinChunkXZ && c.Z <= MaxChunkXZ &&
            c.Y >= MinChunkY && c.Y <= MaxChunkY;

        public static Int3 ChunkOf(int x, int y, int z) =>
            new Int3(x >> ChunkBits, y >> ChunkBits, z >> ChunkBits);

        public static Int3 ChunkOf(Int3 p) => ChunkOf(p.X, p.Y, p.Z);

        public static Int3 ChunkOrigin(Int3 chunk) =>
            new Int3(chunk.X << ChunkBits, chunk.Y << ChunkBits, chunk.Z << ChunkBits);
    }

    /// <summary>
    /// Чанк 16×16×16. Хранит числовые ID блоков. Полностью однородный чанк (воздух, камень)
    /// не выделяет массив — экономия памяти на 8k×8k мире.
    /// </summary>
    public sealed class Chunk
    {
        public readonly Int3 Coord;

        private ushort[] _blocks;
        private ushort _uniform;

        /// <summary>Изменён игроком с момента генерации — нужно сохранять.</summary>
        public bool Modified;

        /// <summary>Растёт при каждом изменении — клиент пересобирает меш.</summary>
        public int Version { get; private set; }

        public Chunk(Int3 coord, ushort fill = 0)
        {
            Coord = coord;
            _uniform = fill;
        }

        public Chunk(Int3 coord, ushort[] blocks)
        {
            if (blocks == null || blocks.Length != WorldConstants.ChunkVolume)
                throw new ArgumentException("blocks must have 4096 elements");
            Coord = coord;
            _blocks = blocks;
            TryCompact();
        }

        public bool IsUniform => _blocks == null;
        public ushort UniformBlock => _uniform;

        public static int Index(int x, int y, int z) => (y << 8) | (z << 4) | x;

        public ushort Get(int x, int y, int z) => _blocks == null ? _uniform : _blocks[Index(x, y, z)];

        public ushort GetByIndex(int i) => _blocks == null ? _uniform : _blocks[i];

        /// <summary>Установить блок. Возвращает предыдущее значение.</summary>
        public ushort Set(int x, int y, int z, ushort id)
        {
            int i = Index(x, y, z);
            ushort old = GetByIndex(i);
            if (old == id) return old;
            if (_blocks == null)
            {
                _blocks = new ushort[WorldConstants.ChunkVolume];
                if (_uniform != 0)
                    for (int k = 0; k < _blocks.Length; k++) _blocks[k] = _uniform;
            }
            _blocks[i] = id;
            Version++;
            return old;
        }

        /// <summary>Запись без инкремента версии — для генератора.</summary>
        internal void SetRaw(int index, ushort id)
        {
            if (_blocks == null)
            {
                if (id == _uniform) return;
                _blocks = new ushort[WorldConstants.ChunkVolume];
                if (_uniform != 0)
                    for (int k = 0; k < _blocks.Length; k++) _blocks[k] = _uniform;
            }
            _blocks[index] = id;
        }

        /// <summary>Скопировать блоки в массив 4096 (для мешинга/сохранения).</summary>
        public void CopyTo(ushort[] dst)
        {
            if (_blocks == null)
            {
                for (int i = 0; i < dst.Length; i++) dst[i] = _uniform;
            }
            else
            {
                Array.Copy(_blocks, dst, WorldConstants.ChunkVolume);
            }
        }

        public void TryCompact()
        {
            if (_blocks == null) return;
            ushort first = _blocks[0];
            for (int i = 1; i < _blocks.Length; i++)
                if (_blocks[i] != first) return;
            _uniform = first;
            _blocks = null;
        }

        public void MarkChanged() => Version++;

        /// <summary>
        /// Отпечаток содержимого (FNV-1a по блокам). Хост шлёт его вместе с «чанк не менялся»: если клиент
        /// сгенерировал по seed что-то другое (другая версия генератора, расхождение математики на другом CPU),
        /// он запрашивает настоящие данные чанка.
        /// </summary>
        public ulong ContentHash()
        {
            unchecked
            {
                // Не зависит от представления (однородный чанк и массив из одинаковых блоков дают одно и то же)
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < WorldConstants.ChunkVolume; i++)
                    h = (h ^ (_blocks == null ? _uniform : _blocks[i])) * 1099511628211UL;
                return h == 0 ? 1 : h;
            }
        }
    }
}
