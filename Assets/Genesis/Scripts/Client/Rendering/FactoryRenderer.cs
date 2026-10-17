using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Net;
using Genesis.Simulation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// «Живые» части фабрики, которые меняются каждый кадр: предметы на лентах
    /// (с интерполяцией между тиками) и крутящиеся шестерни работающих машин.
    /// Всё собирается в один динамический меш с шейдером вокселей.
    /// </summary>
    public sealed class FactoryRenderer : MonoBehaviour
    {
        public float DrawDistance = 64f;

        private IGameSession _server;
        private ContentDatabase _content;
        private Transform _camera;
        private Mesh _mesh;
        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Vector3> _uvs = new List<Vector3>();
        private readonly List<Color32> _colors = new List<Color32>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private LightProbe _probe;
        private byte _sky = 255, _blk;

        private static readonly Vector3[] FaceNormals =
        {
            Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down
        };
        private readonly List<int> _tris = new List<int>();
        private int _gearTexture;
        private float _spin;

        public void Init(IGameSession server, Material material, Transform cameraTransform)
        {
            _server = server;
            _probe = new LightProbe(server.World, new MeshBlockTable(server.Content));
            _content = server.Content;
            _camera = cameraTransform;
            _mesh = new Mesh { name = "FactoryDynamic", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            int gear = _content.Items.IndexOf("genesis:iron_gear");
            _gearTexture = gear >= 0 ? _content.Item(gear).TextureLayer : 0;
        }

        private void OnDestroy() => _probe?.Dispose();

        private void LateUpdate()
        {
            if (_server == null) return;
            _verts.Clear();
            _normals.Clear();
            _uvs.Clear();
            _colors.Clear();
            _tris.Clear();

            float alpha = Mathf.Clamp01(_server.TickAlpha);
            var cam = _camera.position;
            float maxSq = DrawDistance * DrawDistance;

            foreach (var c in _server.Factory.Conveyors)
            {
                var center = new Vector3(c.Pos.X + 0.5f, c.Pos.Y + 1f, c.Pos.Z + 0.5f);
                if ((center - cam).sqrMagnitude > maxSq) continue;
                var off = c.Facing.Offset();
                var dir = new Vector3(off.X, off.Y, off.Z);
                if (c.Items.Count > 0) _probe.Sample(c.Pos + Int3.Up, out _sky, out _blk);
                foreach (var it in c.Items)
                {
                    float p = Mathf.Lerp(it.PrevPos, it.Pos, alpha);
                    var pos = center + dir * (p - 0.5f) + new Vector3(0, 0.16f, 0);
                    var def = _content.Item(it.Item);
                    AddCube(pos, 0.15f, def != null ? def.TextureLayer : 0, 0f, 255);
                }
            }

            _spin += Time.deltaTime * 180f;
            _time += Time.deltaTime;
            foreach (var m in _server.Factory.Machines)
            {
                var top = new Vector3(m.Pos.X + 0.5f, m.Pos.Y + 1.0f, m.Pos.Z + 0.5f);
                if ((top - cam).sqrMagnitude > maxSq) continue;
                if (m.Def.Anim == "none") continue;
                _probe.Sample(m.Pos + Int3.Up, out _sky, out _blk);
                switch (m.Def.Anim)
                {
                    case "none":
                        break;
                    case "piston":
                        DrawPiston(m, top);
                        break;
                    case "drill":
                        DrawDrill(m, top);
                        break;
                    case "arm":
                        DrawArm(m, top);
                        break;
                    case "rotor":
                        DrawRotor(m);
                        break;
                    default:
                        if (m.Activity > 0.01f)
                            AddSpinningPlate(top + new Vector3(0, 0.02f, 0), 0.32f, _gearTexture, _spin * m.Activity + (m.Pos.GetHashCode() & 255));
                        break;
                }
            }

            _mesh.Clear();
            _mesh.SetVertices(_verts);
            _mesh.SetNormals(_normals);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetUVs(1, DynamicMeshBuilder.ZeroTints(_verts.Count));
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0, false);
            _mesh.bounds = new Bounds(cam, Vector3.one * DrawDistance * 2.5f);
        }

        private static readonly Vector3[] CubeCorners =
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1)
        };

        // Грани куба: индексы углов по часовой стрелке снаружи + оттенок
        private static readonly int[][] CubeFaces =
        {
            new[] { 5, 6, 7, 4 }, // +Z
            new[] { 0, 3, 2, 1 }, // -Z
            new[] { 1, 2, 6, 5 }, // +X
            new[] { 4, 7, 3, 0 }, // -X
            new[] { 3, 7, 6, 2 }, // +Y
            new[] { 1, 5, 4, 0 }, // -Y
        };

        private static readonly byte[] CubeShade = { 204, 204, 166, 166, 255, 128 };

        private void AddCube(Vector3 center, float half, int layer, float yawDeg, byte bright)
        {
            var rot = Quaternion.Euler(0, yawDeg, 0);
            for (int f = 0; f < 6; f++)
            {
                int b = _verts.Count;
                var idx = CubeFaces[f];
                byte s = (byte)(CubeShade[f] * bright / 255);
                for (int v = 0; v < 4; v++)
                {
                    _verts.Add(center + rot * (CubeCorners[idx[v]] * half));
                    _normals.Add(rot * FaceNormals[f]);
                    _colors.Add(new Color32(_sky, _blk, s, 255));
                }
                _uvs.Add(new Vector3(0, 0, layer));
                _uvs.Add(new Vector3(0, 1, layer));
                _uvs.Add(new Vector3(1, 1, layer));
                _uvs.Add(new Vector3(1, 0, layer));
                _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2);
                _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
            }
        }

        // ------------------------------------------------------------ Анимации машин (Create-style)

        private float _time;
        private readonly Dictionary<Int3, float> _armShown = new Dictionary<Int3, float>();
        private readonly Dictionary<string, int> _casingLayer = new Dictionary<string, int>();

        private int CasingOf(MachineInstance m)
        {
            if (_casingLayer.TryGetValue(m.Def.Id, out var l)) return l;
            var b = _content.Blocks.Get(m.Def.Id);
            l = b != null ? b.FaceTexture[(int)Direction.North] : 0;
            _casingLayer[m.Def.Id] = l;
            return l;
        }

        /// <summary>Поршень молота/пресса: головка ходит вверх-вниз, пока машина работает.</summary>
        private void DrawPiston(MachineInstance m, Vector3 top)
        {
            float phase = m.Activity > 0.01f ? Mathf.Abs(Mathf.Sin((_time * 5f + (m.Pos.GetHashCode() & 63)) * m.Activity)) : 0f;
            float lift = 0.06f + 0.3f * phase;
            int layer = CasingOf(m);
            AddBox(top + new Vector3(0, lift / 2f, 0), new Vector3(0.08f, lift / 2f, 0.08f), Quaternion.identity, layer, 200);
            AddBox(top + new Vector3(0, lift + 0.08f, 0), new Vector3(0.34f, 0.08f, 0.34f), Quaternion.identity, layer, 255);
        }

        /// <summary>Бур: крестовина вращается и подрагивает.</summary>
        private void DrawDrill(MachineInstance m, Vector3 top)
        {
            float a = m.Activity > 0.01f ? _time * 540f * m.Activity : 0f;
            float bob = m.Activity > 0.01f ? 0.03f * Mathf.Sin(_time * 30f) : 0f;
            var rot = Quaternion.Euler(0, a, 0);
            int layer = CasingOf(m);
            var c = top + new Vector3(0, 0.12f + bob, 0);
            AddBox(c, new Vector3(0.42f, 0.05f, 0.08f), rot, _gearTexture, 230);
            AddBox(c, new Vector3(0.08f, 0.05f, 0.42f), rot, _gearTexture, 230);
            AddBox(top + new Vector3(0, 0.06f, 0), new Vector3(0.12f, 0.06f, 0.12f), Quaternion.identity, layer, 200);
        }

        /// <summary>Рука манипулятора: качается от источника (сзади) к цели (спереди), несёт предмет.</summary>
        private void DrawArm(MachineInstance m, Vector3 top)
        {
            if (!_armShown.TryGetValue(m.Pos, out var shown)) shown = m.Progress;
            shown = Mathf.MoveTowards(shown, m.Progress, Time.deltaTime * 2.2f);
            _armShown[m.Pos] = shown;

            var off = m.Facing.Offset();
            var dir = new Vector3(off.X, 0, off.Z);
            var pivot = top + new Vector3(0, 0.35f, 0);
            var hand = top + dir * ((shown * 2f - 1f) * 0.62f) + new Vector3(0, 0.12f, 0);
            int layer = CasingOf(m);

            AddBox(top + new Vector3(0, 0.17f, 0), new Vector3(0.1f, 0.17f, 0.1f), Quaternion.identity, layer, 210);
            var seg = hand - pivot;
            var mid = (pivot + hand) / 2f;
            var rot = Quaternion.LookRotation(seg.normalized, Vector3.up);
            AddBox(mid, new Vector3(0.05f, 0.05f, seg.magnitude / 2f), rot, layer, 255);
            AddBox(hand, new Vector3(0.09f, 0.04f, 0.09f), Quaternion.identity, layer, 230);

            var held = m.Input.Size > 0 ? m.Input[0] : ItemStack.Empty;
            if (!held.IsEmpty)
            {
                var def = _content.Item(held.Item);
                AddCube(hand - new Vector3(0, 0.15f, 0), 0.12f, def != null ? def.TextureLayer : 0, 0f, 255);
            }
        }

        private readonly Dictionary<Int3, float> _rotorAngle = new Dictionary<Int3, float>();

        /// <summary>Лопасти ветряка/колеса на передней грани, крутятся со скоростью выработки.</summary>
        private void DrawRotor(MachineInstance m)
        {
            _rotorAngle.TryGetValue(m.Pos, out var angle);
            angle += Time.deltaTime * 140f * Mathf.Max(0.05f, m.Activity);
            _rotorAngle[m.Pos] = angle;
            var off = m.Facing.Offset();
            var dir = new Vector3(off.X, 0, off.Z);
            var center = new Vector3(m.Pos.X + 0.5f, m.Pos.Y + 0.5f, m.Pos.Z + 0.5f) + dir * 0.58f;
            var baseRot = Quaternion.LookRotation(dir, Vector3.up);
            int layer = CasingOf(m);
            for (int b = 0; b < 4; b++)
            {
                var rot = baseRot * Quaternion.Euler(0, 0, angle + b * 90f);
                AddBox(center + rot * new Vector3(0, 0.5f, 0), new Vector3(0.07f, 0.5f, 0.03f), rot, layer, 245);
            }
            AddBox(center, new Vector3(0.1f, 0.1f, 0.08f), baseRot, layer, 200);
        }

        private void AddBox(Vector3 center, Vector3 half, Quaternion rot, int layer, byte bright)
        {
            for (int f = 0; f < 6; f++)
            {
                int b = _verts.Count;
                var idx = CubeFaces[f];
                byte s = (byte)(CubeShade[f] * bright / 255);
                for (int v = 0; v < 4; v++)
                {
                    var c = CubeCorners[idx[v]];
                    _verts.Add(center + rot * new Vector3(c.x * half.x, c.y * half.y, c.z * half.z));
                    _normals.Add(rot * FaceNormals[f]);
                    _colors.Add(new Color32(_sky, _blk, s, 255));
                }
                _uvs.Add(new Vector3(0, 0, layer));
                _uvs.Add(new Vector3(0, 1, layer));
                _uvs.Add(new Vector3(1, 1, layer));
                _uvs.Add(new Vector3(1, 0, layer));
                _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2);
                _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
            }
        }

        private void AddSpinningPlate(Vector3 center, float half, int layer, float angle)
        {
            var rot = Quaternion.Euler(0, angle, 0);
            int b = _verts.Count;
            var c0 = new Vector3(-half, 0, -half);
            var c1 = new Vector3(-half, 0, half);
            var c2 = new Vector3(half, 0, half);
            var c3 = new Vector3(half, 0, -half);
            _verts.Add(center + rot * c0);
            _verts.Add(center + rot * c1);
            _verts.Add(center + rot * c2);
            _verts.Add(center + rot * c3);
            for (int i = 0; i < 4; i++)
            {
                _colors.Add(new Color32(_sky, _blk, 255, 255));
                _normals.Add(Vector3.up);
            }
            _uvs.Add(new Vector3(0, 0, layer));
            _uvs.Add(new Vector3(0, 1, layer));
            _uvs.Add(new Vector3(1, 1, layer));
            _uvs.Add(new Vector3(1, 0, layer));
            _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2);
            _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
        }
    }
}
