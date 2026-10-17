using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Net;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client.Rendering.Models
{
    /// <summary>
    /// Блоки и машины с моделями Blockbench: индекс позиций по чанкам (обновляется по событиям мира), каждый кадр —
    /// отрисовка в радиусе видимости. Машины смотрят «лицом» в свою сторону и играют анимацию
    /// «working» во время работы и «idle» в простое (если такие есть в модели).
    /// </summary>
    public sealed class ModelBlockRenderer : MonoBehaviour
    {
        public float DrawDistance = 72f;

        private IGameSession _session;
        private ModelLibrary _lib;
        private Transform _camera;
        private LightProbe _probe;
        private MeshBlockTable _table;
        private bool[] _hasModel;
        private readonly Dictionary<Int3, List<Int3>> _byChunk = new Dictionary<Int3, List<Int3>>();
        private readonly Dictionary<Int3, Instance> _instances = new Dictionary<Int3, Instance>();
        // Создаётся лениво: Unity не разрешает создавать его в инициализаторе поля MonoBehaviour
        private MaterialPropertyBlock _propsBlock;
        private MaterialPropertyBlock _props => _propsBlock ?? (_propsBlock = new MaterialPropertyBlock());
        private static readonly int InstLight = Shader.PropertyToID("_InstLight");

        private sealed class Instance
        {
            public ModelPose Pose;
            public Vector4 Light;
            public float LightAt = -10f;
        }

        public void Init(IGameSession session, ModelLibrary lib, Transform cameraTransform)
        {
            _session = session;
            _lib = lib;
            _camera = cameraTransform;
            _table = new MeshBlockTable(session.Content);
            _probe = new LightProbe(session.World, _table);
            int n = session.Content.Blocks.Count;
            _hasModel = new bool[n];
            for (int i = 0; i < n; i++) _hasModel[i] = session.Content.Blocks.Get(i).Model != null;

            var world = session.World;
            world.ChunkLoaded += OnChunkLoaded;
            world.ChunkUnloaded += OnChunkUnloaded;
            world.BlockChanged += OnBlockChanged;
            foreach (var c in world.Chunks) OnChunkLoaded(c);
        }

        private void OnDestroy()
        {
            _probe?.Dispose();
            if (_session == null) return;
            _session.World.ChunkLoaded -= OnChunkLoaded;
            _session.World.ChunkUnloaded -= OnChunkUnloaded;
            _session.World.BlockChanged -= OnBlockChanged;
        }

        private bool Has(ushort id) => id < _hasModel.Length && _hasModel[id];

        private void OnChunkLoaded(Chunk c)
        {
            _byChunk.Remove(c.Coord);
            if (c.IsUniform && !Has(c.UniformBlock)) return;
            var origin = WorldConstants.ChunkOrigin(c.Coord);
            List<Int3> list = null;
            for (int i = 0; i < WorldConstants.ChunkVolume; i++)
            {
                if (!Has(c.GetByIndex(i))) continue;
                list = list ?? new List<Int3>();
                list.Add(origin + new Int3(i & 15, i >> 8, (i >> 4) & 15));
            }
            if (list != null) _byChunk[c.Coord] = list;
        }

        private void OnChunkUnloaded(Int3 coord)
        {
            if (_byChunk.TryGetValue(coord, out var list))
                foreach (var p in list) _instances.Remove(p);
            _byChunk.Remove(coord);
        }

        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock)
        {
            if (!Has(oldBlock) && !Has(newBlock)) return;
            var c = WorldConstants.ChunkOf(pos);
            if (!_byChunk.TryGetValue(c, out var list)) _byChunk[c] = list = new List<Int3>();
            list.Remove(pos);
            _instances.Remove(pos);
            if (Has(newBlock)) list.Add(pos);
        }

        private void LateUpdate()
        {
            if (_session == null || _lib == null) return;
            var cam = _camera.position;
            var camChunk = WorldConstants.ChunkOf(new Int3(Mathf.FloorToInt(cam.x), Mathf.FloorToInt(cam.y), Mathf.FloorToInt(cam.z)));
            int r = Mathf.CeilToInt(DrawDistance / 16f);
            float maxSq = DrawDistance * DrawDistance;
            float dt = Time.deltaTime, now = Time.time;
            var world = _session.World;
            var factory = _session.Factory;

            foreach (var kv in _byChunk)
            {
                var cc = kv.Key;
                if (Math.Abs(cc.X - camChunk.X) > r || Math.Abs(cc.Y - camChunk.Y) > r || Math.Abs(cc.Z - camChunk.Z) > r) continue;
                foreach (var pos in kv.Value)
                {
                    var center = new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
                    if ((center - cam).sqrMagnitude > maxSq) continue;
                    var def = world.GetBlockDef(pos);
                    var model = _lib.Get(def.Model);
                    if (model == null) continue;

                    if (!_instances.TryGetValue(pos, out var inst)) _instances[pos] = inst = new Instance();
                    if (now - inst.LightAt > 0.5f)
                    {
                        _probe.Sample(pos + Int3.Up, out byte sky, out byte blk);
                        inst.Light = new Vector4(sky / 255f, Mathf.Max(blk, (byte)(def.Light * 17)) / 255f, 0, def.Light > 0 ? 0.35f : 0f);
                        inst.LightAt = now + UnityEngine.Random.value * 0.2f;
                    }

                    var machine = factory.MachineAt(pos);
                    var facing = machine != null ? machine.Facing : Direction.North;
                    Matrix4x4[] bones = null;
                    if (model.Animations.Count > 0)
                    {
                        if (inst.Pose == null || inst.Pose.Model != model) inst.Pose = new ModelPose(model);
                        bool working = machine != null && machine.Activity > 0.01f;
                        string anim = working && inst.Pose.HasAnimation("working") ? "working" : "idle";
                        inst.Pose.Play(anim);
                        inst.Pose.Update(dt);
                        bones = inst.Pose.Bones;
                    }
                    _props.SetVector(InstLight, inst.Light);
                    _lib.Draw(model, ModelLibrary.BlockMatrix(model, pos, facing), bones, _props);
                }
            }
        }
    }
}
