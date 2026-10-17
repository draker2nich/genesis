using System;
using Genesis.Core;

namespace Genesis.World
{
    public struct VoxelHit
    {
        public Int3 Block;
        /// <summary>Позиция соседнего блока (куда ставить новый).</summary>
        public Int3 Adjacent;
        public Direction Face;
        public float Distance;
    }

    /// <summary>Обход вокселей по лучу (Amanatides–Woo DDA).</summary>
    public static class VoxelRaycast
    {
        public static bool Cast(VoxelWorld world, Vec3 origin, Vec3 direction, float maxDistance, out VoxelHit hit)
        {
            return Cast(origin, direction, maxDistance, p =>
            {
                var def = world.GetBlockDef(p);
                return def.Render != Genesis.Data.RenderLayer.Invisible && !def.Liquid;
            }, out hit);
        }

        public static bool Cast(Vec3 origin, Vec3 direction, float maxDistance, Func<Int3, bool> isHit, out VoxelHit hit)
        {
            hit = default;
            var dir = direction.Normalized;
            if (dir.LengthSquared < 1e-8f) return false;

            int x = (int)Math.Floor(origin.X);
            int y = (int)Math.Floor(origin.Y);
            int z = (int)Math.Floor(origin.Z);

            int stepX = dir.X > 0 ? 1 : (dir.X < 0 ? -1 : 0);
            int stepY = dir.Y > 0 ? 1 : (dir.Y < 0 ? -1 : 0);
            int stepZ = dir.Z > 0 ? 1 : (dir.Z < 0 ? -1 : 0);

            float tDeltaX = stepX != 0 ? Math.Abs(1f / dir.X) : float.PositiveInfinity;
            float tDeltaY = stepY != 0 ? Math.Abs(1f / dir.Y) : float.PositiveInfinity;
            float tDeltaZ = stepZ != 0 ? Math.Abs(1f / dir.Z) : float.PositiveInfinity;

            float tMaxX = stepX > 0 ? (x + 1 - origin.X) * tDeltaX : (stepX < 0 ? (origin.X - x) * tDeltaX : float.PositiveInfinity);
            float tMaxY = stepY > 0 ? (y + 1 - origin.Y) * tDeltaY : (stepY < 0 ? (origin.Y - y) * tDeltaY : float.PositiveInfinity);
            float tMaxZ = stepZ > 0 ? (z + 1 - origin.Z) * tDeltaZ : (stepZ < 0 ? (origin.Z - z) * tDeltaZ : float.PositiveInfinity);

            Direction face = Direction.Up;
            float t = 0;
            int prevX = x, prevY = y, prevZ = z;

            for (int i = 0; i < 1024 && t <= maxDistance; i++)
            {
                var p = new Int3(x, y, z);
                if (i > 0 && isHit(p))
                {
                    hit.Block = p;
                    hit.Adjacent = new Int3(prevX, prevY, prevZ);
                    hit.Face = face;
                    hit.Distance = t;
                    return true;
                }

                prevX = x; prevY = y; prevZ = z;
                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    x += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                    face = stepX > 0 ? Direction.West : Direction.East;
                }
                else if (tMaxY < tMaxZ)
                {
                    y += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                    face = stepY > 0 ? Direction.Down : Direction.Up;
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                    face = stepZ > 0 ? Direction.South : Direction.North;
                }
            }
            return false;
        }
    }
}
