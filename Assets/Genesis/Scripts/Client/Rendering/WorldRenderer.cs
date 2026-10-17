using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Genesis.Core;
using Genesis.Net;
using Genesis.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Отображение чанков: очередь перестройки мешей (ближние — первыми), мешинг в фоне,
    /// применение готовых мешей в главном потоке. Сам мир не меняет — только смотрит.
    /// </summary>
    public sealed class WorldRenderer : MonoBehaviour
    {
        public int MaxMeshJobsInFlight = 8;
        public int MaxMeshUploadsPerFrame = 8;

        private IGameSession _session;
        private VoxelWorld _world;
        private readonly Dictionary<long, Color32[]> _tints = new Dictionary<long, Color32[]>();
        private MeshBlockTable _table;
        private Material _opaque, _translucent;
        private Transform _camera;

        private sealed class ChunkView
        {
            public GameObject Go;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public int Version = -1;
        }

        private readonly Dictionary<Int3, ChunkView> _views = new Dictionary<Int3, ChunkView>();
        private readonly HashSet<Int3> _dirty = new HashSet<Int3>();
        private readonly HashSet<Int3> _inFlight = new HashSet<Int3>();
        private readonly ConcurrentQueue<MeshData> _ready = new ConcurrentQueue<MeshData>();
        private readonly ConcurrentBag<MeshData> _meshPool = new ConcurrentBag<MeshData>();
        private readonly Stack<ChunkView> _viewPool = new Stack<ChunkView>();
        private readonly List<Int3> _sortBuffer = new List<Int3>();

        public int VisibleChunks => _views.Count;
        public int QueuedChunks => _dirty.Count + _inFlight.Count;

        public void Init(IGameSession session, MeshBlockTable table, Material opaque, Material translucent, Transform cameraTransform)
        {
            _session = session;
            _world = session.World;
            _table = table;
            _opaque = opaque;
            _translucent = translucent;
            _camera = cameraTransform;

            _world.ChunkLoaded += OnChunkLoaded;
            _world.ChunkUnloaded += OnChunkUnloaded;
            _world.BlockChanged += OnBlockChanged;
            _session.Factory.EntityChanged += MarkBlockDirty;

            foreach (var c in _world.Chunks) OnChunkLoaded(c);
        }

        private void OnDestroy()
        {
            if (_world == null) return;
            _world.ChunkLoaded -= OnChunkLoaded;
            _world.ChunkUnloaded -= OnChunkUnloaded;
            _world.BlockChanged -= OnBlockChanged;
            _session.Factory.EntityChanged -= MarkBlockDirty;
        }

        // ------------------------------------------------------------ События мира

        private void OnChunkLoaded(Chunk c)
        {
            _dirty.Add(c.Coord);
            // Соседям нужно перестроить грани на стыке и свет (поля снимка — 8 блоков во все стороны),
            // нижним — ещё и небо (чанк сверху мог закрыть его)
            for (int dy = -2; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                if (dy == -2 && (dx != 0 || dz != 0)) continue;
                var n = c.Coord + new Int3(dx, dy, dz);
                if (_views.ContainsKey(n)) _dirty.Add(n);
            }
        }

        private void OnChunkUnloaded(Int3 coord)
        {
            _dirty.Remove(coord);
            if (_views.TryGetValue(coord, out var v))
            {
                v.Go.SetActive(false);
                v.Mesh.Clear();
                v.Version = -1;
                _viewPool.Push(v);
                _views.Remove(coord);
            }
        }

        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock) => MarkBlockDirty(pos);

        /// <summary>Блок изменился: перестроить все чанки, чьи снимки его видят (±8 блоков — свет), и чанк ниже (небо).</summary>
        private void MarkBlockDirty(Int3 pos)
        {
            const int R = ChunkSnapshot.Pad;
            var min = WorldConstants.ChunkOf(pos.X - R, pos.Y - R, pos.Z - R);
            var max = WorldConstants.ChunkOf(pos.X + R, pos.Y + R, pos.Z + R);
            for (int y = min.Y - 1; y <= max.Y; y++)
            for (int z = min.Z; z <= max.Z; z++)
            for (int x = min.X; x <= max.X; x++)
            {
                var c = new Int3(x, y, z);
                if (_views.ContainsKey(c) || _world.TryGetChunk(c, out _)) _dirty.Add(c);
            }
        }

        // ------------------------------------------------------------ Кадр

        private void LateUpdate()
        {
            if (_world == null) return;
            ApplyReadyMeshes();
            ScheduleMeshing();
        }

        private void ScheduleMeshing()
        {
            if (_dirty.Count == 0 || _inFlight.Count >= MaxMeshJobsInFlight) return;

            var camPos = _camera.position;
            var camChunk = WorldConstants.ChunkOf((int)Mathf.Floor(camPos.x), (int)Mathf.Floor(camPos.y), (int)Mathf.Floor(camPos.z));

            _sortBuffer.Clear();
            foreach (var c in _dirty)
                if (!_inFlight.Contains(c)) _sortBuffer.Add(c);
            _sortBuffer.Sort((a, b) => a.DistanceSquared(camChunk).CompareTo(b.DistanceSquared(camChunk)));

            foreach (var coord in _sortBuffer)
            {
                if (_inFlight.Count >= MaxMeshJobsInFlight) break;
                _dirty.Remove(coord);
                if (!_world.TryGetChunk(coord, out var chunk)) continue;

                var snap = TakeSnapshot(chunk);
                _inFlight.Add(coord);
                if (!_meshPool.TryTake(out var mesh)) mesh = new MeshData();
                var table = _table;
                Task.Run(() =>
                {
                    try
                    {
                        ChunkMesher.Build(snap, table, mesh);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                        mesh.Clear();
                        mesh.Coord = snap.Coord;
                    }
                    _ready.Enqueue(mesh);
                });
            }
        }

        /// <summary>Множитель цвета листвы и травы: тёплые сухие края — желтее, холодные — сизые, болота — оливковые.</summary>
        public static Color32 BiomeTint(in ColumnInfo c)
        {
            float t = Mathf.Clamp(c.Temperature, -1f, 1f), h = Mathf.Clamp(c.Humidity, -1f, 1f);
            float r = 1f + 0.22f * t - 0.10f * Mathf.Max(0f, h);
            float g = 1f + 0.03f * t + 0.04f * h;
            float b = 1f - 0.24f * t;
            switch (c.Biome)
            {
                case Biome.Swamp: r *= 0.82f; g *= 0.9f; b *= 0.7f; break;
                case Biome.Wasteland: r *= 1.05f; g *= 0.9f; b *= 0.72f; break;
                case Biome.BirchForest: r *= 1.06f; g *= 1.06f; b *= 0.95f; break;
                case Biome.Taiga:
                case Biome.Tundra: r *= 0.9f; b *= 1.04f; break;
                case Biome.Badlands:
                case Biome.Desert: r *= 1.1f; g *= 0.98f; b *= 0.8f; break;
                case Biome.Meadow: g *= 1.04f; break;
            }
            return new Color32((byte)Mathf.Clamp(r * 128f, 0, 255), (byte)Mathf.Clamp(g * 128f, 0, 255), (byte)Mathf.Clamp(b * 128f, 0, 255), 255);
        }

        private ChunkSnapshot TakeSnapshot(Chunk center)
        {
            var snap = new ChunkSnapshot { Coord = center.Coord, Version = center.Version };
            const int S = WorldConstants.ChunkSize;
            const int P = ChunkSnapshot.P;

            // 27 чанков вокруг (null — не загружен → воздух)
            var around = new Chunk[27];
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                _world.TryGetChunk(center.Coord + new Int3(dx, dy, dz), out var c);
                around[(dy + 1) * 9 + (dz + 1) * 3 + (dx + 1)] = c;
            }

            const int O = ChunkSnapshot.Pad;
            for (int py = 0; py < P; py++)
            {
                int ly = py - O;
                int cy = ly < 0 ? 0 : (ly >= S ? 2 : 1);
                int iy = ly < 0 ? ly + S : (ly >= S ? ly - S : ly);
                for (int pz = 0; pz < P; pz++)
                {
                    int lz = pz - O;
                    int cz = lz < 0 ? 0 : (lz >= S ? 2 : 1);
                    int iz = lz < 0 ? lz + S : (lz >= S ? lz - S : lz);
                    for (int px = 0; px < P; px++)
                    {
                        int lx = px - O;
                        int cx = lx < 0 ? 0 : (lx >= S ? 2 : 1);
                        int ix = lx < 0 ? lx + S : (lx >= S ? lx - S : lx);
                        var c = around[cy * 9 + cz * 3 + cx];
                        snap.Blocks[ChunkSnapshot.Index(px, py, pz)] = c != null ? c.Get(ix, iy, iz) : (ushort)0;
                    }
                }
            }

            // Окраска травы и листвы по биому
            long tintKey = ((long)center.Coord.X << 32) ^ (uint)center.Coord.Z;
            if (!_tints.TryGetValue(tintKey, out var tints))
            {
                if (_tints.Count > 4096) _tints.Clear();
                tints = new Color32[S * S];
                var gen = _world.Generator;
                var chunkOrigin = WorldConstants.ChunkOrigin(center.Coord);
                for (int lz = 0; lz < S; lz++)
                for (int lx = 0; lx < S; lx++)
                    tints[lz * S + lx] = BiomeTint(gen.ColumnAt(chunkOrigin.X + lx, chunkOrigin.Z + lz));
                _tints[tintKey] = tints;
            }
            System.Array.Copy(tints, snap.Tint, tints.Length);

            // Небо: есть ли непрозрачный блок выше снимка в каждой колонке (по загруженным чанкам выше)
            int topWorldY = WorldConstants.ChunkOrigin(center.Coord).Y + S + O; // первый y выше снимка
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int cyAbove = center.Coord.Y + 1; cyAbove <= WorldConstants.MaxChunkY; cyAbove++)
                {
                    if (!_world.TryGetChunk(new Int3(center.Coord.X + dx, cyAbove, center.Coord.Z + dz), out var above)) continue;
                    int baseY = cyAbove << WorldConstants.ChunkBits;
                    if (baseY + S <= topWorldY) continue;
                    if (above.IsUniform && !_table.IsOpaque(above.UniformBlock)) continue;
                    for (int lz = 0; lz < S; lz++)
                    {
                        int pz = lz + dz * S + O;
                        if ((uint)pz >= P) continue;
                        for (int lx = 0; lx < S; lx++)
                        {
                            int px = lx + dx * S + O;
                            if ((uint)px >= P) continue;
                            int col = pz * P + px;
                            if (snap.SkyCovered[col]) continue;
                            for (int ly = 0; ly < S; ly++)
                            {
                                if (baseY + ly < topWorldY) continue;
                                if (_table.IsOpaque(above.Get(lx, ly, lz)))
                                {
                                    snap.SkyCovered[col] = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Ориентация машин и лент внутри чанка
            var origin = WorldConstants.ChunkOrigin(center.Coord);
            var factory = _session.Factory;
            foreach (var m in factory.Machines)
                if (WorldConstants.ChunkOf(m.Pos) == center.Coord)
                {
                    var l = m.Pos - origin;
                    snap.Facing[Chunk.Index(l.X, l.Y, l.Z)] = m.Facing;
                }
            foreach (var cv in factory.Conveyors)
                if (WorldConstants.ChunkOf(cv.Pos) == center.Coord)
                {
                    var l = cv.Pos - origin;
                    snap.Facing[Chunk.Index(l.X, l.Y, l.Z)] = cv.Facing;
                }
            return snap;
        }

        private void ApplyReadyMeshes()
        {
            for (int i = 0; i < MaxMeshUploadsPerFrame && _ready.TryDequeue(out var data); i++)
            {
                _inFlight.Remove(data.Coord);
                if (_world.TryGetChunk(data.Coord, out _))
                    Upload(data);
                _meshPool.Add(data);
            }
        }

        private void Upload(MeshData data)
        {
            if (!_views.TryGetValue(data.Coord, out var view))
            {
                if (data.IsEmpty) return;
                view = _viewPool.Count > 0 ? _viewPool.Pop() : CreateView();
                var origin = WorldConstants.ChunkOrigin(data.Coord);
                view.Go.transform.position = new Vector3(origin.X, origin.Y, origin.Z);
                view.Go.name = $"Chunk {data.Coord}";
                view.Go.SetActive(true);
                _views[data.Coord] = view;
            }

            var mesh = view.Mesh;
            mesh.Clear();
            if (data.IsEmpty)
            {
                view.Go.SetActive(false);
                return;
            }
            view.Go.SetActive(true);
            mesh.indexFormat = data.Vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(data.Vertices);
            mesh.SetNormals(data.Normals);
            mesh.SetUVs(0, data.Uvs);
            mesh.SetUVs(1, data.Tints.Count == data.Vertices.Count ? data.Tints : DynamicMeshBuilder.ZeroTints(data.Vertices.Count));
            mesh.SetColors(data.Colors);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(data.Solid, 0, false);
            mesh.SetTriangles(data.Translucent, 1, false);
            mesh.bounds = new Bounds(new Vector3(8, 8, 8), new Vector3(16, 16, 16));
            view.Version = data.Version;
        }

        private ChunkView CreateView()
        {
            var go = new GameObject("Chunk");
            go.transform.SetParent(transform, false);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { _opaque, _translucent };
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            var mesh = new Mesh { name = "ChunkMesh" };
            mesh.MarkDynamic();
            filter.sharedMesh = mesh;
            return new ChunkView { Go = go, Filter = filter, Renderer = renderer, Mesh = mesh };
        }
    }
}
