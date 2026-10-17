namespace Genesis.Core
{
    /// <summary>
    /// Детерминированный ГПСЧ (SplitMix64 + xorshift*). Одинаковый результат на хосте и клиентах —
    /// важно для симуляции и генерации.
    /// </summary>
    public struct Random64
    {
        private ulong _state;

        public Random64(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong State
        {
            get => _state;
            set => _state = value == 0 ? 0x9E3779B97F4A7C15UL : value;
        }

        public ulong NextULong()
        {
            ulong x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>[0, max)</summary>
        public int NextInt(int max)
        {
            if (max <= 0) return 0;
            return (int)(NextULong() % (ulong)max);
        }

        public int NextInt(int min, int maxExclusive) => min + NextInt(maxExclusive - min);

        /// <summary>[0, 1)</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public float NextFloat() => (float)NextDouble();

        public bool Chance(double p) => NextDouble() < p;
    }

    /// <summary>Быстрые хеш-функции координат для детерминированной генерации.</summary>
    public static class Hash
    {
        public static ulong SplitMix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        public static ulong Coord(long seed, int x, int z)
        {
            unchecked
            {
                ulong h = (ulong)seed;
                h = SplitMix(h ^ (ulong)(uint)x);
                h = SplitMix(h ^ ((ulong)(uint)z << 32));
                return h;
            }
        }

        public static ulong Coord(long seed, int x, int y, int z)
        {
            unchecked
            {
                ulong h = Coord(seed, x, z);
                return SplitMix(h ^ (ulong)(uint)y * 0x632BE59BD9B4E019UL);
            }
        }

        /// <summary>Хеш в [0,1).</summary>
        public static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));

        /// <summary>Стабильный хеш строки (FNV-1a), не зависит от рантайма.</summary>
        public static ulong String(string s)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 1099511628211UL;
                }
                return h;
            }
        }
    }
}
