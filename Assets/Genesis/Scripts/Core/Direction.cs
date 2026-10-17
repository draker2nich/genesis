using System;

namespace Genesis.Core
{
    /// <summary>Шесть граней блока. Порядок важен — используется в мешере и сохранениях.</summary>
    public enum Direction : byte
    {
        North = 0, // +Z
        South = 1, // -Z
        East = 2,  // +X
        West = 3,  // -X
        Up = 4,    // +Y
        Down = 5   // -Y
    }

    /// <summary>Относительные стороны машины (для настройки сторон в стиле Mekanism).</summary>
    public enum RelativeSide : byte
    {
        Front = 0,
        Back = 1,
        Left = 2,
        Right = 3,
        Top = 4,
        Bottom = 5
    }

    public static class Directions
    {
        public const int Count = 6;

        public static readonly Direction[] All =
        {
            Direction.North, Direction.South, Direction.East, Direction.West, Direction.Up, Direction.Down
        };

        public static readonly Direction[] Horizontal =
        {
            Direction.North, Direction.East, Direction.South, Direction.West
        };

        private static readonly Int3[] Offsets =
        {
            new Int3(0, 0, 1),
            new Int3(0, 0, -1),
            new Int3(1, 0, 0),
            new Int3(-1, 0, 0),
            new Int3(0, 1, 0),
            new Int3(0, -1, 0)
        };

        public static Int3 Offset(this Direction d) => Offsets[(int)d];

        public static Direction Opposite(this Direction d)
        {
            switch (d)
            {
                case Direction.North: return Direction.South;
                case Direction.South: return Direction.North;
                case Direction.East: return Direction.West;
                case Direction.West: return Direction.East;
                case Direction.Up: return Direction.Down;
                default: return Direction.Up;
            }
        }

        public static bool IsHorizontal(this Direction d) => d != Direction.Up && d != Direction.Down;

        /// <summary>Поворот по часовой стрелке (вид сверху).</summary>
        public static Direction RotateCW(this Direction d)
        {
            switch (d)
            {
                case Direction.North: return Direction.East;
                case Direction.East: return Direction.South;
                case Direction.South: return Direction.West;
                case Direction.West: return Direction.North;
                default: return d;
            }
        }

        public static Direction RotateCCW(this Direction d)
        {
            switch (d)
            {
                case Direction.North: return Direction.West;
                case Direction.West: return Direction.South;
                case Direction.South: return Direction.East;
                case Direction.East: return Direction.North;
                default: return d;
            }
        }

        /// <summary>Абсолютное направление для относительной стороны машины, смотрящей в facing.</summary>
        public static Direction ToAbsolute(RelativeSide side, Direction facing)
        {
            switch (side)
            {
                case RelativeSide.Front: return facing;
                case RelativeSide.Back: return facing.Opposite();
                case RelativeSide.Left: return facing.RotateCCW();
                case RelativeSide.Right: return facing.RotateCW();
                case RelativeSide.Top: return Direction.Up;
                default: return Direction.Down;
            }
        }

        public static RelativeSide ToRelative(Direction absolute, Direction facing)
        {
            if (absolute == Direction.Up) return RelativeSide.Top;
            if (absolute == Direction.Down) return RelativeSide.Bottom;
            if (absolute == facing) return RelativeSide.Front;
            if (absolute == facing.Opposite()) return RelativeSide.Back;
            if (absolute == facing.RotateCCW()) return RelativeSide.Left;
            return RelativeSide.Right;
        }

        /// <summary>Горизонтальное направление по вектору взгляда (x,z).</summary>
        public static Direction FromLook(float x, float z)
        {
            if (Math.Abs(x) > Math.Abs(z)) return x > 0 ? Direction.East : Direction.West;
            return z > 0 ? Direction.North : Direction.South;
        }

        public static bool TryParseRelative(string s, out RelativeSide side)
        {
            switch (s)
            {
                case "front": side = RelativeSide.Front; return true;
                case "back": side = RelativeSide.Back; return true;
                case "left": side = RelativeSide.Left; return true;
                case "right": side = RelativeSide.Right; return true;
                case "top": side = RelativeSide.Top; return true;
                case "bottom": side = RelativeSide.Bottom; return true;
                default: side = RelativeSide.Front; return false;
            }
        }
    }
}
