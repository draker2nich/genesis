using System;

namespace Genesis.Core
{
    /// <summary>Целочисленный 3D-вектор: позиции блоков, координаты чанков.</summary>
    [Serializable]
    public readonly struct Int3 : IEquatable<Int3>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public Int3(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Int3 Zero = new Int3(0, 0, 0);
        public static readonly Int3 One = new Int3(1, 1, 1);
        public static readonly Int3 Up = new Int3(0, 1, 0);
        public static readonly Int3 Down = new Int3(0, -1, 0);

        public static Int3 operator +(Int3 a, Int3 b) => new Int3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Int3 operator -(Int3 a, Int3 b) => new Int3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Int3 operator -(Int3 a) => new Int3(-a.X, -a.Y, -a.Z);
        public static Int3 operator *(Int3 a, int s) => new Int3(a.X * s, a.Y * s, a.Z * s);
        public static bool operator ==(Int3 a, Int3 b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        public static bool operator !=(Int3 a, Int3 b) => !(a == b);

        public bool Equals(Int3 other) => this == other;
        public override bool Equals(object obj) => obj is Int3 other && this == other;

        public override int GetHashCode()
        {
            unchecked
            {
                int h = X * 73856093;
                h ^= Y * 19349663;
                h ^= Z * 83492791;
                return h;
            }
        }

        public int ManhattanLength => Math.Abs(X) + Math.Abs(Y) + Math.Abs(Z);

        public long DistanceSquared(Int3 other)
        {
            long dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public override string ToString() => $"({X}, {Y}, {Z})";

        /// <summary>Деление с округлением вниз (корректно для отрицательных).</summary>
        public static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        /// <summary>Остаток, всегда неотрицательный.</summary>
        public static int FloorMod(int a, int b)
        {
            int m = a % b;
            if (m < 0) m += b;
            return m;
        }
    }
}
