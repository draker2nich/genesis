using System;

namespace Genesis.Core
{
    /// <summary>Float-вектор для логики без зависимостей от Unity (физика игрока, рейкаст).</summary>
    [Serializable]
    public struct Vec3 : IEquatable<Vec3>
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public float LengthSquared => X * X + Y * Y + Z * Z;

        public Vec3 Normalized
        {
            get
            {
                float l = Length;
                return l > 1e-6f ? this / l : Zero;
            }
        }

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;

        public Int3 Floor() => new Int3((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Floor(Z));

        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() => $"({X:0.##}, {Y:0.##}, {Z:0.##})";
    }

    /// <summary>Axis-aligned bounding box.</summary>
    public struct Aabb
    {
        public Vec3 Min;
        public Vec3 Max;

        public Aabb(Vec3 min, Vec3 max)
        {
            Min = min;
            Max = max;
        }

        public static Aabb FromFeet(Vec3 feet, float halfWidth, float height) =>
            new Aabb(new Vec3(feet.X - halfWidth, feet.Y, feet.Z - halfWidth),
                     new Vec3(feet.X + halfWidth, feet.Y + height, feet.Z + halfWidth));

        public bool Intersects(Aabb o) =>
            Min.X < o.Max.X && Max.X > o.Min.X &&
            Min.Y < o.Max.Y && Max.Y > o.Min.Y &&
            Min.Z < o.Max.Z && Max.Z > o.Min.Z;

        public static Aabb Block(Int3 p) =>
            new Aabb(new Vec3(p.X, p.Y, p.Z), new Vec3(p.X + 1, p.Y + 1, p.Z + 1));
    }
}
