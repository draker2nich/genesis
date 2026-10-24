using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Genesis.Core;
using Genesis.Data;

namespace Genesis.World
{
    public delegate void BlockChangedHandler(Int3 pos, ushort oldBlock, ushort newBlock);

    /// <summary>
    /// Удалённый источник чанков (клиент в коопе): мир запрашивает чанк у хоста,
    /// хост отвечает либо «нетронут» (генерируем локально по seed), либо данными изменённого чанка.
    /// </summary>
    public interface IChunkSource
    {
        void RequestChunk(Int3 coord);
        /// <summary>Запросить полные данные чанка (сгенерированный по seed не совпал с хостом).</summary>
        void RequestChunkFull(Int3 coord);
    }

    /// <summary>
    /// Воксельный мир одной планеты: чанки, загрузка/генерация в фоне, доступ к блокам.
    /// Все публичные методы — только из главного потока (потока симуляции).
    /// </summary>
    public sealed class VoxelWorld : IDisposable
    {
        public readonly ContentDatabase Content;
        public readonly WorldGenerator Generator;
        public readonly RegionStore Store;

        private readonly Dictionary<Int3, Chunk> _chunks = new Dictionary<Int3, Chunk>();
        private readonly HashSet<Int3> _pending = new HashSet<Int3>();
        private readonly ConcurrentQueue<Int3> _requests = new ConcurrentQueue<Int3>();
        private readonly ConcurrentQueue<Chunk> _completed = new ConcurrentQueue<Chunk>();
        private readonly Thread[] _workers;
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private volatile bool _running = true;

        public event BlockChangedHandler BlockChanged;
        public event Action<Chunk> ChunkLoaded;
        public event Action<Int3> ChunkUnloaded;
        /// <summary>Ожидаемый чанк больше не нужен (игрок ушёл) — реплика сбрасывает буфер изменений для него.</summary>
        public event Action<Int3> PendingCanceled;

        /// <summary>Ожидаемые отпечатки нетронутых чанков от хоста (сверка генерации).</summary>
        private readonly Dictionary<Int3, ulong> _expectedHash = new Dictionary<Int3, ulong>();

        public readonly ushort Bedrock;

        /// <summary>Если задан — мир работает как реплика: чанки запрашиваются у хоста.</summary>
        public IChunkSource Remote { get; set; }

        /// <summary>
        /// Без потоков (браузер): генерация в главном потоке, но не больше стольких миллисекунд за кадр (Pump).
        /// 0 — синхронно сразу при запросе (тесты).
        /// </summary>
        public int MainThreadBudgetMs;

        public int LoadedCount => _chunks.Count;
        public int PendingCount => _pending.Count;
        public IEnumerable<Chunk> Chunks => _chunks.Values;

        /// <param name="saveDir">Папка регионов; null — мир без сохранения (тесты).</param>
        /// <param name="workerThreads">0 — синхронная генерация (тесты).</param>
        public VoxelWorld(ContentDatabase content, long seed, string saveDir, int workerThreads)
        {
            Content = content;
            Generator = new WorldGenerator(seed, content);
            Store = new RegionStore(saveDir, content);
            Bedrock = content.BlockId("genesis:bedrock");

            _workers = new Thread[Math.Max(0, workerThreads)];
            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = "ChunkGen-" + i, Priority = ThreadPriority.BelowNormal };
                _workers[i].Start();
            }
        }

        // ------------------------------------------------------------ Доступ к блокам

        public bool TryGetChunk(Int3 coord, out Chunk chunk) => _chunks.TryGetValue(coord, out chunk);

        public bool IsLoaded(int x, int y, int z) =>
            _chunks.ContainsKey(WorldConstants.ChunkOf(x, y, z));

        /// <summary>
        /// Блок по мировым координатам. За границей мира — бедрок (стена), выше мира — воздух,
        /// незагруженный чанк — воздух (используйте IsLoaded для физики).
        /// </summary>
        public ushort GetBlock(int x, int y, int z)
        {
            if (y < WorldConstants.MinY) return Bedrock;
            if (y > WorldConstants.MaxY) return 0;
            if (!WorldConstants.InHorizontalBounds(x, z)) return Bedrock;
            if (!_chunks.TryGetValue(WorldConstants.ChunkOf(x, y, z), out var c)) return 0;
            return c.Get(x & WorldConstants.ChunkMask, y & WorldConstants.ChunkMask, z & WorldConstants.ChunkMask);
        }

        public ushort GetBlock(Int3 p) => GetBlock(p.X, p.Y, p.Z);

        public BlockDef GetBlockDef(Int3 p) => Content.Block(GetBlock(p));

        /// <summary>Установить блок. false — чанк не загружен или вне мира.</summary>
        public bool SetBlock(Int3 p, ushort id)
        {
            if (p.Y < WorldConstants.MinY || p.Y > WorldConstants.MaxY) return false;
            if (!WorldConstants.InHorizontalBounds(p.X, p.Z)) return false;
            if (!_chunks.TryGetValue(WorldConstants.ChunkOf(p), out var c)) return false;
            ushort old = c.Set(p.X & WorldConstants.ChunkMask, p.Y & WorldConstants.ChunkMask, p.Z & WorldConstants.ChunkMask, id);
            if (old == id) return true;
            c.Modified = true;
            BlockChanged?.Invoke(p, old, id);
            return true;
        }

        // ------------------------------------------------------------ Загрузка чанков

        /// <summary>Запросить загрузку чанка (асинхронно).</summary>
        public void Request(Int3 coord)
        {
            if (!WorldConstants.ChunkInBounds(coord)) return;
            if (_chunks.ContainsKey(coord) || _pending.Contains(coord)) return;

            if (Remote != null)
            {
                _pending.Add(coord);
                Remote.RequestChunk(coord);
                return;
            }

            // Изменённые чанки читаем из регион-файла (быстро, в главном потоке).
            var saved = Store.Load(coord);
            if (saved != null)
            {
                Integrate(saved);
                return;
            }

            if (_workers.Length == 0 && MainThreadBudgetMs <= 0)
            {
                Integrate(Generator.Generate(coord));
                return;
            }
            _pending.Add(coord);
            _requests.Enqueue(coord);
            if (_workers.Length > 0) _signal.Release();
        }

        public bool IsPending(Int3 coord) => _pending.Contains(coord);

        /// <summary>Ответ хоста: чанк не менялся — генерируем по seed. hash — отпечаток хоста (0 — неизвестен).</summary>
        public void ProvidePristine(Int3 coord, ulong hash = 0)
        {
            if (!_pending.Contains(coord) || _chunks.ContainsKey(coord)) return;
            if (hash != 0) _expectedHash[coord] = hash;
            if (_workers.Length == 0 && MainThreadBudgetMs <= 0)
            {
                var gen = Generator.Generate(coord);
                if (!MatchesHost(gen)) return;
                _pending.Remove(coord);
                Integrate(gen);
                return;
            }
            _requests.Enqueue(coord);
            _signal.Release();
        }

        /// <summary>Ответ хоста: данные изменённого чанка.</summary>
        public void ProvideData(Chunk chunk)
        {
            if (!_pending.Remove(chunk.Coord) || _chunks.ContainsKey(chunk.Coord)) return;
            Integrate(chunk);
        }

        /// <summary>Синхронно загрузить чанк (для спавна, тестов).</summary>
        public Chunk LoadNow(Int3 coord)
        {
            if (_chunks.TryGetValue(coord, out var c)) return c;
            if (!WorldConstants.ChunkInBounds(coord)) return null;
            c = Store.Load(coord) ?? Generator.Generate(coord);
            _pending.Remove(coord);
            Integrate(c);
            return c;
        }

        /// <summary>Забрать готовые чанки из фоновых потоков. Вызывать каждый кадр.</summary>
        public void Pump(int maxPerCall = 64)
        {
            // Браузер: генерируем сами, сколько успеваем за бюджет кадра
            if (_workers.Length == 0 && MainThreadBudgetMs > 0 && !_requests.IsEmpty)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < MainThreadBudgetMs && _requests.TryDequeue(out var coord))
                {
                    if (!_pending.Contains(coord) || _chunks.ContainsKey(coord)) continue;
                    _completed.Enqueue(Generator.Generate(coord));
                }
            }
            for (int i = 0; i < maxPerCall && _completed.TryDequeue(out var chunk); i++)
            {
                if (!_pending.Contains(chunk.Coord)) continue; // отменён или уже загружен синхронно
                if (_chunks.ContainsKey(chunk.Coord)) continue;
                if (!MatchesHost(chunk)) continue; // ждём данные от хоста
                _pending.Remove(chunk.Coord);
                Integrate(chunk);
            }
        }

        /// <summary>Сгенерированный чанк совпал с отпечатком хоста? Нет — просим у хоста полные данные.</summary>
        private bool MatchesHost(Chunk chunk)
        {
            if (!_expectedHash.TryGetValue(chunk.Coord, out ulong h)) return true;
            _expectedHash.Remove(chunk.Coord);
            if (chunk.ContentHash() == h) return true;
            Log.Warn($"Chunk {chunk.Coord} generated differently from host, requesting data");
            Remote?.RequestChunkFull(chunk.Coord);
            return false;
        }

        private void Integrate(Chunk chunk)
        {
            _chunks[chunk.Coord] = chunk;
            ChunkLoaded?.Invoke(chunk);
        }

        public void Unload(Int3 coord)
        {
            if (_chunks.TryGetValue(coord, out var c))
            {
                if (c.Modified && Remote == null) Store.Store(c);
                _chunks.Remove(coord);
                ChunkUnloaded?.Invoke(coord);
            }
            _pending.Remove(coord);
        }

        /// <summary>
        /// Держать загруженными чанки вокруг точек интереса (игроков), выгружать дальние.
        /// </summary>
        public void UpdateLoadedArea(IReadOnlyList<Int3> centers, int radius, int verticalRadius)
        {
            var wanted = new List<(long, Int3)>();
            var wantedSet = new HashSet<Int3>();
            foreach (var center in centers)
            {
                var cc = WorldConstants.ChunkOf(center);
                for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dz * dz > radius * radius + radius) continue;
                    for (int dy = -verticalRadius; dy <= verticalRadius; dy++)
                    {
                        var c = new Int3(cc.X + dx, cc.Y + dy, cc.Z + dz);
                        if (!WorldConstants.ChunkInBounds(c) || !wantedSet.Add(c)) continue;
                        long d = (long)dx * dx + (long)dz * dz + (long)dy * dy * 2;
                        wanted.Add((d, c));
                    }
                }
            }

            wanted.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            foreach (var w in wanted) Request(w.Item2);

            // Выгрузка: с запасом +2, чтобы не дёргать на границе.
            var toUnload = new List<Int3>();
            foreach (var coord in _chunks.Keys)
            {
                bool keep = false;
                foreach (var center in centers)
                {
                    var cc = WorldConstants.ChunkOf(center);
                    int dx = coord.X - cc.X, dz = coord.Z - cc.Z, dy = coord.Y - cc.Y;
                    int r = radius + 2;
                    if (dx * dx + dz * dz <= r * r + r && Math.Abs(dy) <= verticalRadius + 2)
                    {
                        keep = true;
                        break;
                    }
                }
                if (!keep) toUnload.Add(coord);
            }
            foreach (var c in toUnload) Unload(c);

            // Отменяем ожидающие запросы, которые больше не нужны.
            if (_pending.Count > 0)
            {
                var drop = new List<Int3>();
                foreach (var p in _pending)
                    if (!wantedSet.Contains(p)) drop.Add(p);
                foreach (var p in drop)
                {
                    _pending.Remove(p);
                    _expectedHash.Remove(p);
                    PendingCanceled?.Invoke(p);
                }
            }
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                _signal.Wait(250);
                while (_running && _requests.TryDequeue(out var coord))
                {
                    try
                    {
                        _completed.Enqueue(Generator.Generate(coord));
                    }
                    catch (Exception e)
                    {
                        Log.Error($"Chunk generation failed at {coord}: {e}");
                    }
                }
            }
        }

        // ------------------------------------------------------------ Сохранение

        /// <summary>Сбросить все изменённые загруженные чанки в регион-файлы.</summary>
        public void Save()
        {
            foreach (var c in _chunks.Values)
                if (c.Modified) Store.Store(c);
            Store.Flush();
        }

        /// <summary>Положить изменённые чанки в регионы и вернуть снимок для записи в фоне.</summary>
        public List<RegionStore.RegionWrite> PrepareSave()
        {
            foreach (var c in _chunks.Values)
                if (c.Modified) Store.Store(c);
            return Store.SnapshotDirty();
        }

        public void Dispose()
        {
            _running = false;
            for (int i = 0; i < _workers.Length; i++) _signal.Release();
            foreach (var t in _workers) t.Join(500);
        }

        // ------------------------------------------------------------ Утилиты

        /// <summary>Самый верхний твёрдый блок в колонке среди загруженных чанков.</summary>
        public int TopSolidY(int x, int z)
        {
            for (int y = WorldConstants.MaxY; y >= WorldConstants.MinY; y--)
            {
                var def = Content.Block(GetBlock(x, y, z));
                if (def.Solid) return y;
            }
            return WorldConstants.MinY;
        }
    }
}
