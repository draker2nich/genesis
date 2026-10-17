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
    /// Покачиваются и вращаются, стопка показывается двумя-тремя копиями. Позиция сглаживается между пакетами.
    /// </summary>
    public sealed class DroppedItemRenderer : MonoBehaviour
    {
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
                float bob = Mathf.Sin(t * 2.2f + d.Id) * 0.05f + 0.1f;
                var rot = Quaternion.Euler(0, (t * 70f + d.Id * 37) % 360f, 0);
                int copies = d.Stack.Count >= 16 ? 3 : (d.Stack.Count > 1 ? 2 : 1);

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
            _b.Upload(_mesh, new Bounds(cam, Vector3.one * 160f));
        }
    }
}
