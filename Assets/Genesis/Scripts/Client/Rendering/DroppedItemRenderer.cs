using System.Collections.Generic;
using Genesis.Client.Rendering.Models;
using Genesis.Core;
using Genesis.Data;
using Genesis.Net;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Предметы, лежащие в мире: маленький кубик (блоки), плоская картинка (предметы) или модель Blockbench.
    /// Выпавшие покачиваются и вращаются, положенные на пол — лежат неподвижно; стопка показывается двумя-тремя
    /// копиями. Позиция сглаживается между пакетами. Здесь же — предметы на рабочих поверхностях (верстак,
    /// наковальня) и поворот обрабатываемого блока в режиме создания.
    /// </summary>
    public sealed class DroppedItemRenderer : MonoBehaviour
    {
        /// <summary>Контекст игры (режим создания); null — только предметы.</summary>
        public ClientContext Ctx;
        private IGameSession _session;
        private Transform _camera;
        private Mesh _mesh;
        private LightProbe _probe;
        private readonly DynamicMeshBuilder _b = new DynamicMeshBuilder();
        private readonly Dictionary<int, Vector3> _shown = new Dictionary<int, Vector3>();
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly List<int> _drop = new List<int>();
        private MaterialPropertyBlock _propsBlock;
        private MaterialPropertyBlock Props => _propsBlock ?? (_propsBlock = new MaterialPropertyBlock());
        private static readonly int InstLight = Shader.PropertyToID("_InstLight");

        public void Init(IGameSession session, Material material, Transform camera)
        {
            _session = session;
            _camera = camera;
            _probe = new LightProbe(session.World, new MeshBlockTable(session.Content));
            _mesh = new Mesh { name = "DroppedItems" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        private void OnDestroy() => _probe?.Dispose();

        private void LateUpdate()
        {
            if (_session == null) return;
            _b.Clear();
            _seen.Clear();
            var content = _session.Content;
            var cam = _camera.position;
            float t = Time.time;
            float k = 1f - Mathf.Exp(-Time.deltaTime * 14f);
            var lib = ModelLibrary.Current;

            foreach (var d in _session.DroppedItems)
            {
                var target = new Vector3(d.Position.X, d.Position.Y, d.Position.Z);
                if ((target - cam).sqrMagnitude > 64f * 64f) continue;
                _seen.Add(d.Id);
                if (!_shown.TryGetValue(d.Id, out var pos) || (pos - target).sqrMagnitude > 9f) pos = target;
                pos = Vector3.Lerp(pos, target, k);
                _shown[d.Id] = pos;

                var item = content.Item(d.Stack.Item);
                if (item == null) continue;
                var block = item.PlacesBlock != null ? content.Blocks.Get(item.PlacesBlock) : null;
                int copies = d.Stack.Count >= 16 ? 3 : (d.Stack.Count > 1 ? 2 : 1);
                if (d.Placed)
                {
                    // Положен на пол: лежит неподвижно, повёрнут «как положили»
                    DrawResting(item, block, pos + new Vector3(0, -0.12f, 0), (d.Id * 53) % 360, copies, lib);
                    continue;
                }
                float bob = Mathf.Sin(t * 2.2f + d.Id) * 0.05f + 0.1f;
                var rot = Quaternion.Euler(0, (t * 70f + d.Id * 37) % 360f, 0);

                _probe.Sample(new Int3(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y + 0.2f), Mathf.FloorToInt(pos.z)), out _b.Sky, out _b.Blk);

                // Модель Blockbench
                var model = lib?.Get(item.Model) ?? (block?.Model != null ? lib?.Get(block.Model) : null);
                if (model != null)
                {
                    Props.SetVector(InstLight, new Vector4(_b.Sky / 255f, _b.Blk / 255f, 0, 0));
                    for (int c = 0; c < copies; c++)
                    {
                        var p = pos + new Vector3(c * 0.08f, bob + c * 0.06f, c * 0.05f);
                        var local = model.IsBlockFormat ? Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.5f)) : Matrix4x4.identity;
                        lib.Draw(model, Matrix4x4.TRS(p, rot, Vector3.one * 0.3f) * local, null, Props);
                    }
                    continue;
                }

                for (int c = 0; c < copies; c++)
                {
                    var p = pos + rot * new Vector3(c * 0.07f, bob + c * 0.05f, c * 0.04f);
                    if (block != null && block.Render == RenderLayer.Opaque && (block.Shape == null || block.Shape == "cube"))
                    {
                        var layers = new int[6];
                        for (int f = 0; f < 6; f++) layers[f] = block.FaceTexture[f];
                        _b.Box(p + new Vector3(0, 0.12f, 0), Vector3.one * 0.12f, rot, layers, new Rect(0, 0, 1, 1));
                    }
                    else if (item.TextureLayer >= 0)
                    {
                        // Плоская картинка с двух сторон
                        var right = rot * Vector3.right * 0.2f;
                        var up = Vector3.up * 0.4f;
                        var b0 = p - right;
                        var b1 = p + right;
                        var n = rot * Vector3.forward;
                        _b.Quad(b1, b1 + up, b0 + up, b0, n, item.TextureLayer, new Rect(0, 0, 1, 1), 230);
                        _b.Quad(b0, b0 + up, b1 + up, b1, -n, item.TextureLayer, new Rect(1, 0, -1, 1), 200);
                    }
                }
            }

            _drop.Clear();
            foreach (var id in _shown.Keys)
                if (!_seen.Contains(id)) _drop.Add(id);
            foreach (var id in _drop) _shown.Remove(id);

            DrawSurfaces(cam, lib);
            DrawCreationBlock(lib);
            _b.Upload(_mesh, new Bounds(cam, Vector3.one * 160f));
        }

        /// <summary>Предмет, лежащий неподвижно (на полу или на поверхности станции): кубик, плоская картинка или модель.</summary>
        private void DrawResting(ItemDef item, BlockDef block, Vector3 p, float yaw, int copies, ModelLibrary lib)
        {
            _probe.Sample(new Int3(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y + 0.3f), Mathf.FloorToInt(p.z)), out _b.Sky, out _b.Blk);
            var rot = Quaternion.Euler(0, yaw, 0);
            var model = lib?.Get(item.Model) ?? (block?.Model != null ? lib?.Get(block.Model) : null);
            if (model != null)
            {
                Props.SetVector(InstLight, new Vector4(_b.Sky / 255f, _b.Blk / 255f, 0, 0));
                for (int c = 0; c < copies; c++)
                {
                    var local = model.IsBlockFormat ? Matrix4x4.Translate(new Vector3(-0.5f, 0f, 0.5f)) : Matrix4x4.identity;
                    lib.Draw(model, Matrix4x4.TRS(p + rot * new Vector3(c * 0.09f, c * 0.02f, c * 0.05f), rot, Vector3.one * 0.3f) * local, null, Props);
                }
                return;
            }
            for (int c = 0; c < copies; c++)
            {
                var q = p + rot * new Vector3(c * 0.08f, c * 0.015f, c * 0.06f);
                if (block != null && block.Render == RenderLayer.Opaque && (block.Shape == null || block.Shape == "cube"))
                {
                    var layers = new int[6];
                    for (int f = 0; f < 6; f++) layers[f] = block.FaceTexture[f];
                    _b.Box(q + new Vector3(0, 0.12f, 0), Vector3.one * 0.12f, rot, layers, new Rect(0, 0, 1, 1));
                }
                else if (item.TextureLayer >= 0)
                {
                    // Плоская картинка лежит на поверхности (сверху и снизу)
                    var r = rot * Vector3.right * 0.22f;
                    var f = rot * Vector3.forward * 0.22f;
                    var y = new Vector3(0, 0.01f + c * 0.012f, 0);
                    var a0 = q - r - f + y;
                    var a1 = q + r - f + y;
                    var a2 = q + r + f + y;
                    var a3 = q - r + f + y;
                    _b.Quad(a0, a3, a2, a1, Vector3.up, item.TextureLayer, new Rect(0, 0, 1, 1), 240);
                    _b.Quad(a1, a2, a3, a0, Vector3.down, item.TextureLayer, new Rect(1, 0, -1, 1), 140);
                }
            }
        }

        /// <summary>Предметы на рабочих поверхностях (верстак, наковальня) — лежат сверху в ряд.</summary>
        private void DrawSurfaces(Vector3 cam, ModelLibrary lib)
        {
            var content = _session.Content;
            foreach (var m in _session.Factory.Machines)
            {
                if (!m.Def.Surface || m.Input.IsEmpty) continue;
                var center = new Vector3(m.Pos.X + 0.5f, m.Pos.Y + 1.0f, m.Pos.Z + 0.5f);
                if (m.Def.Width > 1)
                {
                    // Широкий верстак: предметы посередине обеих клеток
                    var right = m.Facing.RotateCW().Offset();
                    center += new Vector3(right.X, 0, right.Z) * ((m.Def.Width - 1) * 0.5f);
                }
                if ((center - cam).sqrMagnitude > 48f * 48f) continue;
                int n = 0, total = 0;
                for (int i = 0; i < m.Input.Size; i++) if (!m.Input[i].IsEmpty) total++;
                for (int i = 0; i < m.Input.Size; i++)
                {
                    var st = m.Input[i];
                    if (st.IsEmpty) continue;
                    var item = content.Item(st.Item);
                    if (item == null) continue;
                    var block = item.PlacesBlock != null ? content.Blocks.Get(item.PlacesBlock) : null;
                    float off = (n - (total - 1) / 2f) * 0.32f;
                    var dir = m.Facing.RotateCW().Offset();
                    var p = center + new Vector3(dir.X, 0, dir.Z) * off;
                    DrawResting(item, block, p, (st.Item * 41 + i * 23) % 360, st.Count > 1 ? 2 : 1, lib);
                    n++;
                }
            }
        }

        /// <summary>Режим создания: обрабатываемый блок поворачивается на 90° после каждого удара.</summary>
        private void DrawCreationBlock(ModelLibrary lib)
        {
            var c = Ctx?.Creation;
            if (c == null || !c.Active || c.Option?.Shaping == null || !c.Option.Shaping.Rotate) return;
            if (c.Option.Shaping.IsStation) return; // на станции крутятся предметы, а не станция
            var def = _session.Content.Block(c.Block);
            var center = new Vector3(c.Pos.X + 0.5f, c.Pos.Y + 0.5f, c.Pos.Z + 0.5f);
            var rot = Quaternion.Euler(0, c.Angle, 0);
            _probe.Sample(new Int3(c.Pos.X, c.Pos.Y + 1, c.Pos.Z), out _b.Sky, out _b.Blk);
            var model = def.Model != null ? lib?.Get(def.Model) : null;
            if (model != null)
            {
                Props.SetVector(InstLight, new Vector4(_b.Sky / 255f, _b.Blk / 255f, 0, 0));
                var local = Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.5f));
                lib.Draw(model, Matrix4x4.TRS(center, rot, Vector3.one * 1.004f) * local, null, Props);
                return;
            }
            var layers = new int[6];
            for (int f = 0; f < 6; f++) layers[f] = def.FaceTexture[f];
            // Чуть больше настоящего блока — закрывает его, а повёрнутые углы выглядывают
            _b.Box(center, Vector3.one * 0.503f, rot, layers, new Rect(0, 0, 1, 1));
        }
    }
}
