using Genesis.Client.Rendering.Models;
using Genesis.Core;
using Genesis.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Предмет в руке от первого лица: блок — маленьким кубом, остальное — объёмным пиксельным спрайтом
    /// (каждый непрозрачный пиксель вытянут в толщину, как в Minecraft). Взмах при ударе/постановке,
    /// покачивание при ходьбе, плавная смена предмета. Рисуется поверх мира (ZTest Always).
    /// </summary>
    public sealed class HeldItemRenderer : MonoBehaviour
    {
        private ClientContext _ctx;
        private Camera _cam;
        private Mesh _mesh;
        private Material _mat;
        private LightProbe _probe;
        private readonly DynamicMeshBuilder _b = new DynamicMeshBuilder();

        private int _shownItem = int.MinValue;
        private int _wantedItem = -1;
        private float _equip = 1f;   // 0 — опущен, 1 — в руке
        private float _swing = 1f;   // 0..1 — фаза взмаха (1 — покой)
        private bool _isBlock;
        private BbModel _model;
        // Создаётся лениво: Unity не разрешает создавать его в инициализаторе поля MonoBehaviour
        private MaterialPropertyBlock _propsBlock;
        private MaterialPropertyBlock _props => _propsBlock ?? (_propsBlock = new MaterialPropertyBlock());
        private static readonly int InstLight = Shader.PropertyToID("_InstLight");
        private static readonly int HandLightId = Shader.PropertyToID("_GenesisHandLight");
        private int _lightLevel;
        private float _lightNow, _flicker;
        private byte _lastSky = 1, _lastBlk = 1;

        /// <summary>Фаза шага (радианы) и сила покачивания — задаёт контроллер.</summary>
        public float BobPhase, BobAmount;
        public bool Hidden;
        /// <summary>Левая рука: показывает предмет из слота левой руки, зеркально справа налево.</summary>
        public bool LeftHand;
        /// <summary>Идёт крафт в руках: руки сходятся к центру и «работают».</summary>
        public bool Crafting;
        private float _craft;
        /// <summary>Свет предмета в левой руке (правая рука выставляет общий свет руки как максимум из двух).</summary>
        private static float _offhandLight;

        public void Init(ClientContext ctx, Material opaque, Camera cam)
        {
            _ctx = ctx;
            _cam = cam;
            _probe = new LightProbe(ctx.Session.World, new MeshBlockTable(ctx.Content));
            _mat = new Material(opaque) { name = "HeldItem", renderQueue = 4000 };
            _mat.SetFloat("_ZTest", (float)CompareFunction.Always);
            _mat.SetShaderPassEnabled("ShadowCaster", false);
            _mat.SetShaderPassEnabled("DepthOnly", false);
            _mesh = new Mesh { name = "HeldItem" };
            _mesh.MarkDynamic();
            transform.SetParent(cam.transform, false);
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        private void OnDestroy()
        {
            _probe?.Dispose();
            if (_mat != null) Destroy(_mat);
        }

        /// <summary>Взмах рукой (удар, постановка, использование).</summary>
        public void Swing()
        {
            if (_swing > 0.5f) _swing = 0f;
        }

        private void LateUpdate()
        {
            if (_ctx == null) return;
            var stack = LeftHand ? _ctx.Player.OffhandStack : _ctx.Player.SelectedStack;
            _wantedItem = stack.IsEmpty ? -1 : stack.Item;

            // Смена предмета: опустить старый, поднять новый
            if (_wantedItem != _shownItem)
            {
                _equip = Mathf.MoveTowards(_equip, 0f, Time.deltaTime * 8f);
                if (_equip <= 0f) Rebuild(_wantedItem);
            }
            else _equip = Mathf.MoveTowards(_equip, 1f, Time.deltaTime * 6f);
            _swing = Mathf.Min(1f, _swing + Time.deltaTime * 3.6f);
            _craft = Mathf.MoveTowards(_craft, Crafting ? 1f : 0f, Time.deltaTime * 5f);

            if (LeftHand) _offhandLight = _shownItem >= 0 ? _lightLevel * _equip : 0f;
            else UpdateHandLight();

            bool hide = Hidden || _shownItem < 0 || _ctx.Window != UiWindow.None;
            GetComponent<MeshRenderer>().enabled = !hide && _model == null;
            if (hide) return;

            // Свет: от точки, где стоит игрок
            var p = _cam.transform.position;
            _probe.Sample(new Int3(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z)), out byte sky, out byte blk);
            if (sky != _lastSky || blk != _lastBlk)
            {
                _lastSky = sky;
                _lastBlk = blk;
                _b.Relight(sky, blk);
                _mesh.SetColors(_b.Colors);
            }

            // Поза: правый нижний угол, взмах по дуге, покачивание при ходьбе
            float s = Mathf.Sin(_swing * Mathf.PI);
            float s2 = Mathf.Sin(Mathf.Sqrt(_swing) * Mathf.PI);
            var pos = new Vector3(0.52f, -0.46f, 0.78f);
            pos += new Vector3(-0.28f * s2, 0.14f * Mathf.Sin(Mathf.Sqrt(_swing) * Mathf.PI * 2f), 0.08f * s);
            pos += new Vector3(Mathf.Cos(BobPhase) * 0.025f, -Mathf.Abs(Mathf.Sin(BobPhase)) * 0.03f, 0f) * BobAmount;
            pos.y -= (1f - _equip) * 0.6f;
            var rot = _isBlock
                ? Quaternion.Euler(0f, 45f, 0f)
                : Quaternion.Euler(0f, -100f, 25f);
            rot = Quaternion.Euler(-70f * s, -20f * s2, 0f) * rot;
            if (_craft > 0f)
            {
                // Крафт в руках: обе руки ближе к центру, мелкие движения «скрутить, сплести»
                float wobble = Mathf.Sin(Time.time * 11f) * 0.025f;
                var craftPos = new Vector3(0.2f + wobble, -0.34f + Mathf.Abs(wobble), 0.62f);
                pos = Vector3.Lerp(pos, craftPos, _craft);
                rot = Quaternion.Slerp(rot, Quaternion.Euler(-25f, -35f + wobble * 400f, 10f) * (_isBlock ? Quaternion.identity : Quaternion.Euler(0f, -100f, 25f)), _craft);
            }
            if (LeftHand)
            {
                // Зеркально: левая рука в левом нижнем углу
                pos.x = -pos.x;
                rot = new Quaternion(rot.x, -rot.y, -rot.z, rot.w);
            }
            transform.localPosition = pos;
            transform.localRotation = rot;
            transform.localScale = Vector3.one * (_isBlock ? 0.38f : 0.62f);

            if (_model != null)
            {
                // Модель Blockbench: блочная — центрируем как куб, предметная — рукоять в начале координат
                var local = _model.IsBlockFormat
                    ? Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.5f)) // передом к игроку
                    : Matrix4x4.Translate(new Vector3(0f, -0.45f, 0f));
                _props.SetVector(InstLight, new Vector4(_lastSky / 255f, _lastBlk / 255f, 0f, 0f));
                ModelLibrary.Current.Draw(_model, transform.localToWorldMatrix * local, null, _props, false, true);
            }
        }

        /// <summary>Уровень света предмета в руке: поле light у предмета или свечение ставимого блока.</summary>
        public static int LightOf(ContentDatabase content, int itemId)
        {
            if (itemId < 0) return 0;
            var item = content.Item(itemId);
            if (item == null) return 0;
            if (item.Light > 0) return item.Light;
            var block = item.PlacesBlock != null ? content.Blocks.Get(item.PlacesBlock) : null;
            return block?.Light ?? 0;
        }

        /// <summary>
        /// Динамический свет: точка чуть впереди и справа от камеры (где рука). Свет плавно
        /// появляется/гаснет при смене предмета и немного мерцает, как живое пламя.
        /// </summary>
        private void UpdateHandLight()
        {
            float target = _ctx.Player.Health > 0 ? Mathf.Max(_lightLevel * _equip, _offhandLight) : 0f;
            _lightNow = Mathf.MoveTowards(_lightNow, target, Time.deltaTime * 40f);
            _flicker = Mathf.Lerp(_flicker, (Mathf.PerlinNoise(Time.time * 7f, 0.37f) - 0.5f) * 0.9f, Time.deltaTime * 12f);
            float level = _lightNow > 0.01f ? Mathf.Max(0f, _lightNow + _flicker) : 0f;
            var t = _cam.transform;
            var pos = t.position + t.right * 0.35f - t.up * 0.2f + t.forward * 0.5f;
            Shader.SetGlobalVector(HandLightId, new Vector4(pos.x, pos.y, pos.z, level));
        }

        private void OnDisable()
        {
            if (LeftHand) _offhandLight = 0f;
            else Shader.SetGlobalVector(HandLightId, Vector4.zero);
        }

        private void Rebuild(int itemId)
        {
            _lightLevel = LightOf(_ctx.Content, itemId);
            _shownItem = itemId;
            _b.Clear();
            _b.Sky = _lastSky;
            _b.Blk = _lastBlk;
            _b.Flag = 255;
            _isBlock = false;
            _model = null;
            if (itemId >= 0)
            {
                var item = _ctx.Content.Item(itemId);
                BlockDef block = item?.PlacesBlock != null ? _ctx.Content.Blocks.Get(item.PlacesBlock) : null;
                var lib = ModelLibrary.Current;
                _model = lib?.Get(item?.Model) ?? (block?.Model != null ? lib?.Get(block.Model) : null);
                if (_model != null) _isBlock = _model.IsBlockFormat;
                else if (block != null && block.Render == RenderLayer.Opaque && (block.Shape == null || block.Shape == "cube"))
                {
                    _isBlock = true;
                    var layers = new int[6];
                    for (int f = 0; f < 6; f++) layers[f] = block.FaceTexture[f];
                    // Машины показываем «лицом» к игроку
                    if (block.MachineId != null || block.Orientable) layers[(int)Direction.South] = block.FaceTexture[6];
                    if (block.Light > 0) _b.Flag = ChunkMesher.FlagEmissive;
                    _b.Box(Vector3.zero, Vector3.one * 0.5f, Quaternion.identity, layers, new Rect(0, 0, 1, 1));
                }
                else if (item != null && item.TextureLayer >= 0)
                {
                    if (block != null && block.Light > 0) _b.Flag = ChunkMesher.FlagEmissive;
                    BuildSprite(item.TextureLayer);
                }
            }
            _b.Upload(_mesh, new Bounds(Vector3.zero, Vector3.one * 2f));
        }

        /// <summary>Объёмный спрайт: лицо и спина плюс боковые грани по контуру непрозрачных пикселей.</summary>
        private void BuildSprite(int layer)
        {
            var px = _ctx.Textures.Pixels;
            if (px == null || layer >= px.Length || px[layer] == null) return;
            var tex = px[layer];
            int n = BlockTextureFactory.Size;
            float cell = 1f / n, depth = 1f / 32f;
            float z0 = -depth, z1 = depth;
            bool Solid(int x, int y) => x >= 0 && y >= 0 && x < n && y < n && tex[y * n + x].a >= 128;

            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                if (!Solid(x, y)) continue;
                float x0 = x * cell - 0.5f, x1 = x0 + cell, y0 = y * cell - 0.5f, y1 = y0 + cell;
                // Берём центр пикселя (UV — тот же пиксель, растянутый на всю грань)
                var uv = new Rect((x + 0.25f) * cell, (y + 0.25f) * cell, cell * 0.5f, cell * 0.5f);
                _b.Quad(new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), Vector3.forward, layer, uv, 209);
                _b.Quad(new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), Vector3.back, layer, uv, 230);
                if (!Solid(x + 1, y)) _b.Quad(new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), Vector3.right, layer, uv, 173);
                if (!Solid(x - 1, y)) _b.Quad(new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0), Vector3.left, layer, uv, 173);
                if (!Solid(x, y + 1)) _b.Quad(new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.up, layer, uv, 255);
                if (!Solid(x, y - 1)) _b.Quad(new Vector3(x0, y0, z1), new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), Vector3.down, layer, uv, 133);
            }
        }
    }
}
