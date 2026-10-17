using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Сборщик небольших динамических мешей в формате вершин шейдера вокселей
    /// (позиция, нормаль, uv+слой, цвет: свет неба, свет блоков, затенение, флаг).
    /// </summary>
    public sealed class DynamicMeshBuilder
    {
        public readonly List<Vector3> Verts = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector3> Uvs = new List<Vector3>();
        public readonly List<Color32> Colors = new List<Color32>();
        public readonly List<int> Tris = new List<int>();

        public byte Sky = 255, Blk;
        public byte Flag = 255;

        private static readonly Vector3[] Corners =
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1)
        };

        // +Z, -Z, +X, -X, +Y, -Y — углы по часовой стрелке снаружи
        private static readonly int[][] Faces =
        {
            new[] { 5, 6, 7, 4 }, new[] { 0, 3, 2, 1 }, new[] { 1, 2, 6, 5 },
            new[] { 4, 7, 3, 0 }, new[] { 3, 7, 6, 2 }, new[] { 1, 5, 4, 0 }
        };

        private static readonly Vector3[] FaceNormals =
        {
            Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down
        };

        public static readonly byte[] FaceShade = { 209, 209, 173, 173, 255, 133 };

        public void Clear()
        {
            Verts.Clear();
            Normals.Clear();
            Uvs.Clear();
            Colors.Clear();
            Tris.Clear();
        }

        /// <summary>Коробка; layers — слой текстуры на каждую грань (порядок Faces) или один на все; uvRect — участок текстуры.</summary>
        public void Box(Vector3 center, Vector3 half, Quaternion rot, int[] layers, Rect uvRect, byte bright = 255)
        {
            for (int f = 0; f < 6; f++)
            {
                int layer = layers.Length == 6 ? layers[f] : layers[0];
                int b = Verts.Count;
                var idx = Faces[f];
                byte shade = (byte)(FaceShade[f] * bright / 255);
                for (int v = 0; v < 4; v++)
                {
                    var c = Corners[idx[v]];
                    Verts.Add(center + rot * new Vector3(c.x * half.x, c.y * half.y, c.z * half.z));
                    Normals.Add(rot * FaceNormals[f]);
                    Colors.Add(new Color32(Sky, Blk, shade, Flag));
                }
                Uvs.Add(new Vector3(uvRect.xMin, uvRect.yMin, layer));
                Uvs.Add(new Vector3(uvRect.xMin, uvRect.yMax, layer));
                Uvs.Add(new Vector3(uvRect.xMax, uvRect.yMax, layer));
                Uvs.Add(new Vector3(uvRect.xMax, uvRect.yMin, layer));
                Tris.Add(b); Tris.Add(b + 1); Tris.Add(b + 2);
                Tris.Add(b); Tris.Add(b + 2); Tris.Add(b + 3);
            }
        }

        /// <summary>Четырёхугольник a-b-c-d (по часовой стрелке со стороны нормали).</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, int layer, Rect uv, byte shade)
        {
            int i = Verts.Count;
            Verts.Add(a); Verts.Add(b); Verts.Add(c); Verts.Add(d);
            for (int k = 0; k < 4; k++)
            {
                Normals.Add(normal);
                Colors.Add(new Color32(Sky, Blk, shade, Flag));
            }
            Uvs.Add(new Vector3(uv.xMin, uv.yMin, layer));
            Uvs.Add(new Vector3(uv.xMin, uv.yMax, layer));
            Uvs.Add(new Vector3(uv.xMax, uv.yMax, layer));
            Uvs.Add(new Vector3(uv.xMax, uv.yMin, layer));
            Tris.Add(i); Tris.Add(i + 1); Tris.Add(i + 2);
            Tris.Add(i); Tris.Add(i + 2); Tris.Add(i + 3);
        }

        private static readonly List<Vector3> Zeros = new List<Vector3>();

        /// <summary>
        /// Нулевая окраска биома (UV1). Шейдер вокселей читает TEXCOORD1 — если канала в меше нет,
        /// GPU подставляет мусор (часто тот же UV0 со слоем текстуры), и предмет «светится» синим.
        /// </summary>
        public static List<Vector3> ZeroTints(int count)
        {
            while (Zeros.Count < count) Zeros.Add(Vector3.zero);
            if (Zeros.Count > count) Zeros.RemoveRange(count, Zeros.Count - count);
            return Zeros;
        }

        public void Upload(Mesh mesh, Bounds bounds)
        {
            mesh.Clear();
            mesh.indexFormat = Verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(Verts);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, Uvs);
            mesh.SetUVs(1, ZeroTints(Verts.Count));
            mesh.SetColors(Colors);
            mesh.SetTriangles(Tris, 0, false);
            mesh.bounds = bounds;
        }

        /// <summary>Перекрасить свет у готового меша (рука, переносимые объекты), не трогая затенение.</summary>
        public void Relight(byte sky, byte blk)
        {
            for (int i = 0; i < Colors.Count; i++)
            {
                var c = Colors[i];
                Colors[i] = new Color32(sky, blk, c.b, c.a);
            }
        }
    }
}
