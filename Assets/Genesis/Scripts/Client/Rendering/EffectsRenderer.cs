using System.Collections.Generic;
using Genesis.Core;
using Genesis.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Эффекты мира: трещины на ломаемом блоке и частицы-кубики при разрушении/постройке
    /// (кусочки текстуры самого блока, падают с гравитацией и ложатся на землю).
    /// </summary>
    public sealed class EffectsRenderer : MonoBehaviour
    {
        private ClientContext _ctx;
        private Camera _cam;
        private Mesh _mesh;
        private LightProbe _probe;
        private MeshBlockTable _table;
        private readonly DynamicMeshBuilder _b = new DynamicMeshBuilder();
        private int[] _crackLayers;

        private bool _breaking;
        private Int3 _breakPos;
        private float _breakProgress;

        private struct Particle
        {
            public Vector3 Pos, Vel;
            public float Life, MaxLife, Size;
            public int Layer;
            public Rect Uv;
            public bool Resting;
        }

        private readonly List<Particle> _particles = new List<Particle>();

        public void Init(ClientContext ctx, Material material, Camera cam)
        {
            _ctx = ctx;
            _cam = cam;
            _table = new MeshBlockTable(ctx.Content);
            _probe = new LightProbe(ctx.Session.World, _table);
            _mesh = new Mesh { name = "Effects" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            _crackLayers = new int[6];
            for (int i = 0; i < 6; i++) _crackLayers[i] = ctx.Content.TextureLayerOf("genesis:destroy_" + i);
            ctx.Session.World.BlockChanged += OnBlockChanged;
        }

        private void OnDestroy()
        {
            _probe?.Dispose();
            if (_ctx != null) _ctx.Session.World.BlockChanged -= OnBlockChanged;
        }

        // Любое разрушение рядом (своё, соседа по коопу, машины) — осколки; постановка — немного пыли
        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock)
        {
            if (_cam == null) return;
            var d = _cam.transform.position - new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
            if (d.sqrMagnitude > 32 * 32) return;
            bool oldSolid = oldBlock != 0 && oldBlock < _table.Liquid.Length && !_table.Liquid[oldBlock];
            bool newSolid = newBlock != 0 && newBlock < _table.Liquid.Length && !_table.Liquid[newBlock];
            if (oldSolid && !newSolid) Burst(pos, oldBlock);
            else if (!oldSolid && newSolid) Burst(pos, newBlock, 5, 1.2f);
        }

        /// <summary>Прогресс ломания блока (0 — не ломаем).</summary>
        public void SetBreaking(bool active, Int3 pos, float progress)
        {
            _breaking = active && progress > 0.02f;
            _breakPos = pos;
            _breakProgress = progress;
        }

        /// <summary>Блок разрушен (или поставлен — count поменьше): разлёт кусочков его текстуры.</summary>
        public void Burst(Int3 pos, ushort blockId, int count = 18, float speed = 3.2f)
        {
            var def = _ctx.Content.Block(blockId);
            if (def == null || def.Render == Genesis.Data.RenderLayer.Invisible) return;
            int layer = def.FaceTexture[(int)Direction.North];
            for (int i = 0; i < count && _particles.Count < 400; i++)
            {
                var local = new Vector3(Random.value, Random.value, Random.value);
                var p = new Vector3(pos.X, pos.Y, pos.Z) + local;
                var vel = (local - new Vector3(0.5f, 0.2f, 0.5f)).normalized * Random.Range(0.6f, 1.2f) * speed + Vector3.up * Random.Range(1f, 3f);
                float u = Mathf.Floor(Random.value * 7) / 8f, v = Mathf.Floor(Random.value * 7) / 8f;
                float life = Random.Range(0.55f, 1.1f);
                _particles.Add(new Particle
                {
                    Pos = p,
                    Vel = vel,
                    Life = life,
                    MaxLife = life,
                    Size = Random.Range(0.045f, 0.075f),
                    Layer = layer,
                    Uv = new Rect(u, v, 1f / 8f, 1f / 8f)
                });
            }
        }

        private void LateUpdate()
        {
            if (_ctx == null) return;
            _b.Clear();
            var world = _ctx.Session.World;
            float dt = Time.deltaTime;

            // Частицы: гравитация, остановка на твёрдом блоке, исчезновение
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Life -= dt;
                if (p.Life <= 0)
                {
                    _particles.RemoveAt(i);
                    continue;
                }
                if (!p.Resting)
                {
                    p.Vel.y -= 18f * dt;
                    var next = p.Pos + p.Vel * dt;
                    var cell = new Int3(Mathf.FloorToInt(next.x), Mathf.FloorToInt(next.y - p.Size), Mathf.FloorToInt(next.z));
                    if (_table.IsOpaque(world.GetBlock(cell)))
                    {
                        next.y = cell.Y + 1 + p.Size;
                        p.Vel = Vector3.zero;
                        p.Resting = true;
                    }
                    p.Pos = next;
                }
                _particles[i] = p;

                _probe.Sample(new Int3(Mathf.FloorToInt(p.Pos.x), Mathf.FloorToInt(p.Pos.y), Mathf.FloorToInt(p.Pos.z)), out _b.Sky, out _b.Blk);
                float s = p.Size * Mathf.Clamp01(p.Life / p.MaxLife * 3f);
                var rot = Quaternion.LookRotation(_cam.transform.position - p.Pos);
                _b.Box(p.Pos, new Vector3(s, s, s), rot, new[] { p.Layer }, p.Uv);
            }

            // Трещины: чуть увеличенный куб вокруг блока со стадией разрушения
            if (_breaking)
            {
                int stage = Mathf.Clamp(Mathf.FloorToInt(_breakProgress * 6f), 0, 5);
                _probe.Sample(_breakPos + Int3.Up, out _b.Sky, out _b.Blk);
                var c = new Vector3(_breakPos.X + 0.5f, _breakPos.Y + 0.5f, _breakPos.Z + 0.5f);
                _b.Box(c, Vector3.one * 0.503f, Quaternion.identity, new[] { _crackLayers[stage] }, new Rect(0, 0, 1, 1), 230);
            }

            _b.Upload(_mesh, new Bounds(_cam.transform.position, Vector3.one * 200f));
        }
    }
}
