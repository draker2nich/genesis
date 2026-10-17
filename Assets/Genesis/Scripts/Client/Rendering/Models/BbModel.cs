using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Genesis.Client.Rendering.Models
{
    /// <summary>
    /// Модель Blockbench (.bbmodel): кубоиды с поворотами, группы-кости с точками вращения, текстуры
    /// (встроенные base64 или PNG рядом с файлом) и анимации ключевыми кадрами (rotation/position/scale,
    /// значения — числа или Molang). Поддерживаются форматы «Java Block», «Bedrock Entity» и «Generic».
    ///
    /// Система координат Blockbench правая (x — восток, y — вверх, z — юг, 16 единиц = 1 блок).
    /// В Unity переводим как (x, y, -z) / 16: «север» модели (сторона North в Blockbench) смотрит в +Z — это
    /// «перед» модели (для машин — сторона facing, для существ — направление взгляда).
    /// Для блоков начало координат модели — угол блока (0..16), для существ — точка между ног (x, z = 0).
    /// </summary>
    public sealed class BbModel
    {
        public string Id;
        public string Path;
        public Texture2D[] Textures = new Texture2D[0];
        public Bone[] Bones;
        public readonly Dictionary<string, Anim> Animations = new Dictionary<string, Anim>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Габариты в единицах Unity (до анимации).</summary>
        public Bounds Bounds;
        /// <summary>Формат Blockbench: java_block — блок (угол в начале координат), иначе — существо.</summary>
        public bool IsBlockFormat;

        public sealed class Bone
        {
            public string Name, Uuid;
            public int Parent = -1;
            public Vector3 Pivot;     // в единицах Blockbench (пиксели), правая система
            public Vector3 Rotation;  // градусы
            public Mesh Mesh;         // вершины в пространстве модели Unity
            public int[] SubmeshTexture = new int[0];
        }

        public sealed class Anim
        {
            public string Name;
            public float Length;
            public string Loop = "loop";
            public readonly Dictionary<int, Track> Tracks = new Dictionary<int, Track>(); // кость → дорожки
        }

        public sealed class Track
        {
            public Key[] Rotation = new Key[0], Position = new Key[0], Scale = new Key[0];
        }

        public struct Key
        {
            public float Time;
            public Molang X, Y, Z;
            public byte Interp; // 0 linear, 1 step, 2 smooth
        }

        // ============================================================ Загрузка

        public static BbModel Load(string id, string path)
        {
            var root = JObject.Parse(File.ReadAllText(path));
            var m = new BbModel { Id = id, Path = path };
            var meta = root["meta"] as JObject;
            m.IsBlockFormat = (string)meta?["model_format"] == "java_block";
            float resW = F(root["resolution"]?["width"], 16), resH = F(root["resolution"]?["height"], 16);

            // ---- Текстуры
            var texSizes = new List<Vector2>();
            var textures = new List<Texture2D>();
            if (root["textures"] is JArray texArr)
            {
                foreach (var t in texArr)
                {
                    var tex = LoadTexture(t, System.IO.Path.GetDirectoryName(path));
                    textures.Add(tex);
                    float uw = F(t["uv_width"], resW), uh = F(t["uv_height"], resH);
                    texSizes.Add(new Vector2(uw, uh));
                }
            }
            if (textures.Count == 0)
            {
                textures.Add(Texture2D.whiteTexture);
                texSizes.Add(new Vector2(resW, resH));
            }
            m.Textures = textures.ToArray();

            // ---- Элементы
            var elements = new Dictionary<string, JObject>();
            if (root["elements"] is JArray elArr)
                foreach (JObject e in elArr)
                {
                    string type = (string)e["type"] ?? "cube";
                    if (type != "cube") continue;
                    if (e["visibility"] != null && !(bool)e["visibility"]) continue;
                    elements[(string)e["uuid"] ?? Guid.NewGuid().ToString()] = e;
                }

            // ---- Группы-кости
            var groupProps = new Dictionary<string, JObject>();
            if (root["groups"] is JArray groups)
                foreach (JObject g in groups)
                    if (g["uuid"] != null) groupProps[(string)g["uuid"]] = g;

            var bones = new List<Bone> { new Bone { Name = "root", Uuid = "" } };
            var boneElements = new List<List<JObject>> { new List<JObject>() };
            var used = new HashSet<string>();
            if (root["outliner"] is JArray outliner)
                foreach (var node in outliner) Walk(node, 0, bones, boneElements, elements, groupProps, used);
            // Элементы, не попавшие в иерархию, — в корень
            foreach (var kv in elements)
                if (!used.Contains(kv.Key)) boneElements[0].Add(kv.Value);

            // ---- Меши костей
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = -min;
            for (int b = 0; b < bones.Count; b++)
            {
                if (boneElements[b].Count == 0) continue;
                bones[b].Mesh = BuildMesh(boneElements[b], texSizes, textures.Count, out bones[b].SubmeshTexture, ref min, ref max, id + ":" + bones[b].Name);
            }
            m.Bones = bones.ToArray();
            m.Bounds = min.x <= max.x ? new Bounds((min + max) / 2, max - min) : new Bounds(Vector3.zero, Vector3.one);

            // ---- Анимации
            if (root["animations"] is JArray anims)
            {
                foreach (JObject a in anims)
                {
                    var anim = new Anim
                    {
                        Name = (string)a["name"] ?? "anim",
                        Length = F(a["length"], 1),
                        Loop = (string)a["loop"] ?? "loop"
                    };
                    // «animation.crawler.walk» → «walk»
                    string shortName = anim.Name;
                    int dot = shortName.LastIndexOf('.');
                    if (dot >= 0) shortName = shortName.Substring(dot + 1);
                    if (a["animators"] is JObject animators)
                    {
                        foreach (var prop in animators.Properties())
                        {
                            var an = prop.Value as JObject;
                            if (an == null) continue;
                            int bone = FindBone(bones, prop.Name, (string)an["name"]);
                            if (bone < 0) continue;
                            var track = new Track();
                            var rot = new List<Key>();
                            var pos = new List<Key>();
                            var scl = new List<Key>();
                            if (an["keyframes"] is JArray kfs)
                            {
                                foreach (JObject kf in kfs)
                                {
                                    var dp = (kf["data_points"] as JArray)?[0] as JObject;
                                    if (dp == null) continue;
                                    string interp = (string)kf["interpolation"] ?? "linear";
                                    var key = new Key
                                    {
                                        Time = F(kf["time"], 0),
                                        X = Molang.Parse(dp["x"]?.ToString()),
                                        Y = Molang.Parse(dp["y"]?.ToString()),
                                        Z = Molang.Parse(dp["z"]?.ToString()),
                                        Interp = (byte)(interp == "step" ? 1 : (interp == "linear" ? 0 : 2))
                                    };
                                    switch ((string)kf["channel"])
                                    {
                                        case "rotation": rot.Add(key); break;
                                        case "position": pos.Add(key); break;
                                        case "scale": scl.Add(key); break;
                                    }
                                }
                            }
                            rot.Sort((x, y) => x.Time.CompareTo(y.Time));
                            pos.Sort((x, y) => x.Time.CompareTo(y.Time));
                            scl.Sort((x, y) => x.Time.CompareTo(y.Time));
                            track.Rotation = rot.ToArray();
                            track.Position = pos.ToArray();
                            track.Scale = scl.ToArray();
                            anim.Tracks[bone] = track;
                        }
                    }
                    m.Animations[shortName] = anim;
                    m.Animations[anim.Name] = anim;
                }
            }
            return m;
        }

        private static int FindBone(List<Bone> bones, string uuid, string name)
        {
            for (int i = 1; i < bones.Count; i++)
                if (bones[i].Uuid == uuid) return i;
            for (int i = 1; i < bones.Count; i++)
                if (string.Equals(bones[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static void Walk(JToken node, int parent, List<Bone> bones, List<List<JObject>> boneElements,
            Dictionary<string, JObject> elements, Dictionary<string, JObject> groupProps, HashSet<string> used)
        {
            if (node.Type == JTokenType.String)
            {
                string uuid = (string)node;
                if (elements.TryGetValue(uuid, out var e))
                {
                    boneElements[parent].Add(e);
                    used.Add(uuid);
                }
                return;
            }
            if (!(node is JObject g)) return;
            string gid = (string)g["uuid"] ?? "";
            var props = groupProps.TryGetValue(gid, out var p) ? p : g;
            if (props["visibility"] != null && !(bool)props["visibility"]) return;
            var bone = new Bone
            {
                Name = (string)props["name"] ?? (string)g["name"] ?? "bone",
                Uuid = gid,
                Parent = parent,
                Pivot = V3(props["origin"] ?? g["origin"], Vector3.zero),
                Rotation = V3(props["rotation"] ?? g["rotation"], Vector3.zero)
            };
            bones.Add(bone);
            boneElements.Add(new List<JObject>());
            int index = bones.Count - 1;
            if (g["children"] is JArray children)
                foreach (var c in children) Walk(c, index, bones, boneElements, elements, groupProps, used);
        }

        // ============================================================ Геометрия

        private static readonly string[] FaceNames = { "north", "south", "east", "west", "up", "down" };

        private static Mesh BuildMesh(List<JObject> els, List<Vector2> texSizes, int texCount, out int[] submeshTexture,
            ref Vector3 min, ref Vector3 max, string name)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>[texCount];
            for (int i = 0; i < texCount; i++) tris[i] = new List<int>();
            var corners = new Vector3[4];

            foreach (var e in els)
            {
                float inflate = F(e["inflate"], 0);
                var from = V3(e["from"], Vector3.zero) - Vector3.one * inflate;
                var to = V3(e["to"], Vector3.zero) + Vector3.one * inflate;
                var origin = V3(e["origin"], Vector3.zero);
                var rotM = RotationBb(ElementRotation(e));
                var faces = e["faces"] as JObject;
                if (faces == null) continue;

                for (int f = 0; f < 6; f++)
                {
                    if (!(faces[FaceNames[f]] is JObject face)) continue;
                    var texTok = face["texture"];
                    if (texTok == null || texTok.Type == JTokenType.Null) continue;
                    int tex = texTok.Type == JTokenType.Integer ? (int)texTok : 0;
                    if (tex < 0 || tex >= texCount) tex = 0;
                    var uvArr = face["uv"] as JArray;
                    if (uvArr == null || uvArr.Count < 4) continue;
                    float u1 = F(uvArr[0], 0), v1 = F(uvArr[1], 0), u2 = F(uvArr[2], 0), v2 = F(uvArr[3], 0);
                    int uvRot = ((int)F(face["rotation"], 0) / 90 % 4 + 4) % 4;

                    FaceCorners(f, from, to, corners, out var nBb);
                    var tsz = texSizes[tex];
                    var uvc = new[]
                    {
                        new Vector2(u1 / tsz.x, 1 - v1 / tsz.y), new Vector2(u2 / tsz.x, 1 - v1 / tsz.y),
                        new Vector2(u2 / tsz.x, 1 - v2 / tsz.y), new Vector2(u1 / tsz.x, 1 - v2 / tsz.y)
                    };
                    int b = verts.Count;
                    var n = ToUnityDir(rotM.MultiplyVector(nBb));
                    for (int k = 0; k < 4; k++)
                    {
                        var p = ToUnity(rotM.MultiplyPoint3x4(corners[k] - origin) + origin);
                        verts.Add(p);
                        normals.Add(n);
                        uvs.Add(uvc[(k - uvRot + 4) % 4]);
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                    var t = tris[tex];
                    t.Add(b); t.Add(b + 1); t.Add(b + 2);
                    t.Add(b); t.Add(b + 2); t.Add(b + 3);
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            var subs = new List<int>();
            for (int i = 0; i < texCount; i++)
                if (tris[i].Count > 0) subs.Add(i);
            mesh.subMeshCount = Math.Max(1, subs.Count);
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(tris[subs[s]], s, true);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            submeshTexture = subs.ToArray();
            return mesh;
        }

        /// <summary>Углы грани в порядке ЛВ, ПВ, ПН, ЛН (как видит смотрящий снаружи), координаты Blockbench.</summary>
        private static void FaceCorners(int f, Vector3 a, Vector3 b, Vector3[] c, out Vector3 normal)
        {
            switch (f)
            {
                case 0: // north, z = a.z, снаружи слева — восток
                    c[0] = new Vector3(b.x, b.y, a.z); c[1] = new Vector3(a.x, b.y, a.z); c[2] = new Vector3(a.x, a.y, a.z); c[3] = new Vector3(b.x, a.y, a.z);
                    normal = new Vector3(0, 0, -1);
                    break;
                case 1: // south, z = b.z
                    c[0] = new Vector3(a.x, b.y, b.z); c[1] = new Vector3(b.x, b.y, b.z); c[2] = new Vector3(b.x, a.y, b.z); c[3] = new Vector3(a.x, a.y, b.z);
                    normal = new Vector3(0, 0, 1);
                    break;
                case 2: // east, x = b.x
                    c[0] = new Vector3(b.x, b.y, b.z); c[1] = new Vector3(b.x, b.y, a.z); c[2] = new Vector3(b.x, a.y, a.z); c[3] = new Vector3(b.x, a.y, b.z);
                    normal = new Vector3(1, 0, 0);
                    break;
                case 3: // west, x = a.x
                    c[0] = new Vector3(a.x, b.y, a.z); c[1] = new Vector3(a.x, b.y, b.z); c[2] = new Vector3(a.x, a.y, b.z); c[3] = new Vector3(a.x, a.y, a.z);
                    normal = new Vector3(-1, 0, 0);
                    break;
                case 4: // up, верх текстуры — север
                    c[0] = new Vector3(a.x, b.y, a.z); c[1] = new Vector3(b.x, b.y, a.z); c[2] = new Vector3(b.x, b.y, b.z); c[3] = new Vector3(a.x, b.y, b.z);
                    normal = new Vector3(0, 1, 0);
                    break;
                default: // down, верх текстуры — юг
                    c[0] = new Vector3(a.x, a.y, b.z); c[1] = new Vector3(b.x, a.y, b.z); c[2] = new Vector3(b.x, a.y, a.z); c[3] = new Vector3(a.x, a.y, a.z);
                    normal = new Vector3(0, -1, 0);
                    break;
            }
        }

        private static Vector3 ElementRotation(JObject e)
        {
            var r = e["rotation"];
            if (r is JArray) return V3(r, Vector3.zero);
            if (r is JObject o) // старый формат: {angle, axis, origin}
            {
                float ang = F(o["angle"], 0);
                switch ((string)o["axis"])
                {
                    case "x": return new Vector3(ang, 0, 0);
                    case "y": return new Vector3(0, ang, 0);
                    default: return new Vector3(0, 0, ang);
                }
            }
            return Vector3.zero;
        }

        // ============================================================ Математика (правая система Blockbench)

        /// <summary>Поворот Эйлера в порядке ZYX (как в Blockbench/three.js): M = Rz · Ry · Rx.</summary>
        public static Matrix4x4 RotationBb(Vector3 deg)
        {
            float x = deg.x * Mathf.Deg2Rad, y = deg.y * Mathf.Deg2Rad, z = deg.z * Mathf.Deg2Rad;
            float cx = Mathf.Cos(x), sx = Mathf.Sin(x), cy = Mathf.Cos(y), sy = Mathf.Sin(y), cz = Mathf.Cos(z), sz = Mathf.Sin(z);
            var rx = Matrix4x4.identity;
            rx.m11 = cx; rx.m12 = -sx; rx.m21 = sx; rx.m22 = cx;
            var ry = Matrix4x4.identity;
            ry.m00 = cy; ry.m02 = sy; ry.m20 = -sy; ry.m22 = cy;
            var rz = Matrix4x4.identity;
            rz.m00 = cz; rz.m01 = -sz; rz.m10 = sz; rz.m11 = cz;
            return rz * ry * rx;
        }

        public static Vector3 ToUnity(Vector3 bb) => new Vector3(bb.x / 16f, bb.y / 16f, -bb.z / 16f);
        private static Vector3 ToUnityDir(Vector3 bb) => new Vector3(bb.x, bb.y, -bb.z).normalized;

        /// <summary>Матрица из пространства Blockbench (пиксели, правая система) в пространство модели Unity и обратно.</summary>
        public static readonly Matrix4x4 BbToUnity = Matrix4x4.Scale(new Vector3(1 / 16f, 1 / 16f, -1 / 16f));
        public static readonly Matrix4x4 UnityToBb = Matrix4x4.Scale(new Vector3(16f, 16f, -16f));

        // ============================================================ Разбор JSON

        private static float F(JToken t, float fallback)
        {
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            return float.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static Vector3 V3(JToken t, Vector3 fallback)
        {
            if (!(t is JArray a) || a.Count < 3) return fallback;
            return new Vector3(F(a[0], 0), F(a[1], 0), F(a[2], 0));
        }

        private static Texture2D LoadTexture(JToken t, string dir)
        {
            byte[] png = null;
            string source = (string)t["source"];
            if (!string.IsNullOrEmpty(source) && source.StartsWith("data:", StringComparison.Ordinal))
            {
                int comma = source.IndexOf(',');
                if (comma > 0)
                {
                    try { png = Convert.FromBase64String(source.Substring(comma + 1)); }
                    catch (FormatException) { png = null; }
                }
            }
            if (png == null)
            {
                // Внешний PNG рядом с моделью (relative_path или имя файла)
                foreach (var key in new[] { "relative_path", "name", "path" })
                {
                    string rel = (string)t[key];
                    if (string.IsNullOrEmpty(rel)) continue;
                    string p = System.IO.Path.IsPathRooted(rel) ? rel : System.IO.Path.Combine(dir, rel);
                    if (!p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) p += ".png";
                    if (File.Exists(p))
                    {
                        png = File.ReadAllBytes(p);
                        break;
                    }
                    p = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(p));
                    if (File.Exists(p))
                    {
                        png = File.ReadAllBytes(p);
                        break;
                    }
                }
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = (string)t["name"] ?? "bbtexture"
            };
            if (png == null || !tex.LoadImage(png))
            {
                tex.SetPixels32(new[] { new Color32(255, 0, 255, 255), new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255), new Color32(255, 0, 255, 255) });
                tex.Apply();
            }
            tex.filterMode = FilterMode.Point;
            tex.anisoLevel = 0;
            return tex;
        }
    }
}
