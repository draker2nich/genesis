using System;
using System.Collections;
using System.IO;
using Genesis.Client.Rendering;
using Genesis.Core;
using Genesis.Gameplay;
using Genesis.Net;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Автоматическая съёмка промо-скриншотов — настоящий кадр движка,
    /// а не рисунок. Создаёт мир с фиксированным seed, сам находит красивый вид и ровную площадку, строит линию
    /// завода (бур на руде → лента → печь, лампы), ставит время суток, ждёт прогрузки чанков и снимает камерой
    /// в 1920×1080 без интерфейса и предмета в руке. Три кадра: пейзаж на рассвете, завод днём, завод в сумерках.
    /// Запуск: меню Genesis → Скриншоты (редактор сам войдёт в Play Mode).
    /// Результат: Screenshots/*.png и *.jpg (рядом с Assets).
    /// </summary>
    public sealed class ScreenshotDirector : MonoBehaviour
    {
        public const int Width = 1920, Height = 1080;
        public const long Seed = 20261018;

        /// <summary>Фаза суток для «ночного» кадра: солнце чуть ниже горизонта (0.5 — закат, 0.75 — полночь).</summary>
        public const float NightPhase = 0.515f;

        public static string OutputDir => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

        public static void Begin()
        {
            if (FindObjectOfType<ScreenshotDirector>() != null) return;
            new GameObject("ScreenshotDirector").AddComponent<ScreenshotDirector>();
        }

        private GenesisBootstrap _boot;
        private PlayerController _ctrl;
        private GameServer _server;
        private Vector3 _eye;
        private float _yaw, _pitch;
        private Int3 _base;
        private bool _feedBelts;

        private IEnumerator Start()
        {
            while ((_boot = FindObjectOfType<GenesisBootstrap>()) == null || !_boot.ContentReady) yield return null;
            if (_boot.BlockCount <= 1)
            {
                Debug.LogError("[Screens] Контент игры не загружен (см. ошибку [Genesis] выше) — снимать нечего.");
                Destroy(gameObject);
                yield break;
            }
            Debug.Log("[Screens] Создаю мир для съёмки…");
            _boot.StartCaptureWorld("store_screenshots", Seed);
            float t0 = Time.realtimeSinceStartup;
            while (!_boot.InWorld)
            {
                if (Time.realtimeSinceStartup - t0 > 60f)
                {
                    Debug.LogError("[Screens] Мир не открылся.");
                    yield break;
                }
                yield return null;
            }
            var local = _boot.Session as LocalSession;
            if (local == null)
            {
                Debug.LogError("[Screens] Нужна одиночная игра.");
                yield break;
            }
            _server = local.Server;
            _server.Enemies.SpawningEnabled = false;
            _ctrl = _boot.GetComponent<PlayerController>();
            if (_ctrl.Hand != null) _ctrl.Hand.Hidden = true;
            Directory.CreateDirectory(OutputDir);
            var gen = _server.World.Generator;

            // ---- 1. Пейзаж на рассвете
            FindVista(gen, out _eye, out _yaw);
            _pitch = 9f;
            SetTime(0.07f);
            yield return Settle(10f);
            Save("1_landscape");

            // ---- 2. Завод днём
            _base = FindFlat(gen);
            LoadAround(_base);
            BuildFactory(_base);
            SpawnCreature("genesis:mossback", new Vector3(_base.X + 8.5f, _base.Y, _base.Z + 3.5f));
            SpawnCreature("genesis:burrow_hare", new Vector3(_base.X - 1.5f, _base.Y, _base.Z + 3f));
            _feedBelts = true;
            _eye = new Vector3(_base.X + 3.5f, _base.Y + 5.5f, _base.Z - 9.5f);
            _yaw = 0f;
            _pitch = 26f;
            CheckEye();
            SetTime(0.2f);
            yield return Settle(8f);
            Save("2_factory");

            // ---- 3. Та же линия в сумерках: небо ещё синее, лампы и факелы уже горят.
            // Глубокая ночь (фаза ~0.75) на скриншоте — почти чёрный кадр, поэтому берём «синий час» сразу после заката.
            SetTime(NightPhase);
            _eye = new Vector3(_base.X - 2.5f, _base.Y + 3.2f, _base.Z - 7.5f);
            _yaw = 40f;
            _pitch = 15f;
            CheckEye();
            yield return Settle(4f);
            // Существо — за мгновение до кадра, чтобы не успело добежать до камеры
            SpawnCreature("genesis:dusk_stalker", new Vector3(_base.X + 8.5f, _base.Y, _base.Z + 1.5f));
            for (int i = 0; i < 6; i++) yield return null;
            yield return new WaitForEndOfFrame();
            Save("3_night");

            _feedBelts = false;
            Debug.Log("[Screens] Готово: " + OutputDir);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(Path.Combine(OutputDir, "1_landscape.png"));
#endif
            Destroy(gameObject);
        }

        private void Update()
        {
            if (_ctrl == null) return;
            if (_boot.Context != null) _boot.Context.Paused = false; // клик по меню редактора не должен ставить паузу
            _ctrl.SetView(_eye, _yaw, _pitch); // камера стоит, сколько бы мышь ни двигали
            if (_feedBelts && Time.frameCount % 12 == 0) FeedFactory();
        }

        // ------------------------------------------------------------ Ожидание прогрузки

        /// <summary>Подождать minSeconds и пока догрузятся чанки и меши (не дольше 40 с).</summary>
        private IEnumerator Settle(float minSeconds)
        {
            float start = Time.realtimeSinceStartup;
            var renderer = FindObjectOfType<WorldRenderer>();
            while (true)
            {
                float t = Time.realtimeSinceStartup - start;
                bool loaded = _server.World.PendingCount == 0 && (renderer == null || renderer.QueuedChunks == 0);
                if ((t >= minSeconds && loaded) || t > 40f) break;
                yield return null;
            }
            yield return new WaitForEndOfFrame();
        }

        private void SetTime(float phase)
        {
            long day = _server.WorldTime / GameServer.DayLength;
            _server.WorldTime = day * GameServer.DayLength + (long)(phase * GameServer.DayLength);
        }

        // ------------------------------------------------------------ Съёмка

        private void Save(string name)
        {
            var cam = _boot.GameCamera;
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            rt.Release();
            Destroy(rt);
            File.WriteAllBytes(Path.Combine(OutputDir, name + ".png"), tex.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(OutputDir, name + ".jpg"), tex.EncodeToJPG(92));
            Destroy(tex);
            Debug.Log($"[Screens] {name} сохранён");
        }

        // ------------------------------------------------------------ Поиск мест

        /// <summary>Точка обзора: суша под ногами, впереди — горы и суша (по карте высот генератора).</summary>
        private static void FindVista(WorldGenerator gen, out Vector3 eye, out float yaw)
        {
            var sp = gen.SpawnPoint;
            float best = float.MinValue;
            eye = new Vector3(sp.X, gen.ColumnAt(sp.X, sp.Z).Height + 14, sp.Z);
            yaw = 0f;
            for (int a = 0; a < 16; a++)
            {
                float ang = a * 22.5f;
                float dx = Mathf.Sin(ang * Mathf.Deg2Rad), dz = Mathf.Cos(ang * Mathf.Deg2Rad);
                foreach (int dist in new[] { 30, 60, 90 })
                {
                    int px = sp.X + Mathf.RoundToInt(dx * dist), pz = sp.Z + Mathf.RoundToInt(dz * dist);
                    int h0 = gen.ColumnAt(px, pz).Height;
                    if (h0 < WorldConstants.SeaLevel + 2) continue;
                    int maxAhead = 0, land = 0, water = 0;
                    for (int t = 20; t <= 110; t += 10)
                    {
                        int hh = gen.ColumnAt(px + Mathf.RoundToInt(dx * t), pz + Mathf.RoundToInt(dz * t)).Height;
                        maxAhead = Mathf.Max(maxAhead, hh);
                        if (hh >= WorldConstants.SeaLevel) land++;
                        else water++;
                    }
                    // Горы впереди, но не стена прямо перед носом; немного воды — красиво, океан — нет
                    float score = Mathf.Min(maxAhead - h0, 40) + land * 2f + Mathf.Min(water, 2) * 3f - dist * 0.02f;
                    if (score > best)
                    {
                        best = score;
                        eye = new Vector3(px + 0.5f, Mathf.Max(h0, WorldConstants.SeaLevel) + 13f, pz + 0.5f);
                        yaw = ang;
                    }
                }
            }
        }

        /// <summary>Ровная суша 12×9 недалеко от точки появления — под завод.</summary>
        private static Int3 FindFlat(WorldGenerator gen)
        {
            var sp = gen.SpawnPoint;
            int bestScore = int.MaxValue;
            var best = new Int3(sp.X + 20, gen.ColumnAt(sp.X + 20, sp.Z + 60).Height + 1, sp.Z + 60);
            for (int r = 24; r <= 72; r += 8)
            for (int a = 0; a < 12; a++)
            {
                float ang = a * 30f * Mathf.Deg2Rad;
                int x = sp.X + Mathf.RoundToInt(Mathf.Sin(ang) * r), z = sp.Z + Mathf.RoundToInt(Mathf.Cos(ang) * r);
                int lo = int.MaxValue, hi = int.MinValue;
                for (int ix = -2; ix <= 9; ix++)
                for (int iz = -4; iz <= 4; iz++)
                {
                    int hh = gen.ColumnAt(x + ix, z + iz).Height;
                    lo = Mathf.Min(lo, hh);
                    hi = Mathf.Max(hi, hh);
                }
                if (lo < WorldConstants.SeaLevel + 1) continue;
                int score = (hi - lo) * 10 + r / 8;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Int3(x, hi + 1, z);
                }
            }
            return best;
        }

        // Зона вокруг завода, где убираем деревья (камеры стоят до 10 блоков к югу от линии)
        private const int ClearW = 12, ClearE = 18, ClearS = 16, ClearN = 10, ClearUp = 26;

        private void LoadAround(Int3 b)
        {
            var c0 = WorldConstants.ChunkOf(new Int3(b.X - ClearW, b.Y - 10, b.Z - ClearS));
            var c1 = WorldConstants.ChunkOf(new Int3(b.X + ClearE, b.Y + ClearUp, b.Z + ClearN));
            for (int cx = c0.X; cx <= c1.X; cx++)
            for (int cy = c0.Y; cy <= c1.Y; cy++)
            for (int cz = c0.Z; cz <= c1.Z; cz++)
                _server.World.LoadNow(new Int3(cx, cy, cz));
        }

        // ------------------------------------------------------------ Завод

        private void Set(int x, int y, int z, string blockId, Direction facing = Direction.North)
        {
            var def = _server.Content.Blocks.Get(blockId);
            if (def == null) return;
            var p = new Int3(x, y, z);
            if (!_server.World.IsLoaded(x, y, z)) return;
            _server.Factory.OnBlockRemoved(p);
            if (!_server.World.SetBlock(p, def.NumericId)) return;
            _server.Factory.OnBlockPlaced(p, def, facing);
        }

        private void BuildFactory(Int3 b)
        {
            // Площадка: камень под ногами, над ней — пусто (деревья и бугры убираем)
            for (int x = b.X - 3; x <= b.X + 10; x++)
            for (int z = b.Z - 5; z <= b.Z + 5; z++)
            {
                bool edge = x == b.X - 3 || x == b.X + 10 || z == b.Z - 5 || z == b.Z + 5;
                for (int y = b.Y - 4; y <= b.Y - 1; y++) Set(x, y, z, y == b.Y - 1 && !edge ? "genesis:stone" : "genesis:dirt");
                if (edge) Set(x, b.Y - 1, z, "genesis:grass");
                for (int y = b.Y; y <= b.Y + 12; y++) Set(x, y, z, "genesis:air");
            }
            // Вокруг: деревья долой (иначе камера оказывается в кроне), а между камерами и линией — вообще всё выше земли
            for (int x = b.X - ClearW; x <= b.X + ClearE; x++)
            for (int z = b.Z - ClearS; z <= b.Z + ClearN; z++)
            {
                bool corridor = x >= b.X - 8 && x <= b.X + 12 && z >= b.Z - ClearS && z < b.Z - 5;
                for (int y = b.Y - 10; y <= b.Y + ClearUp; y++)
                {
                    if (!_server.World.IsLoaded(x, y, z)) continue;
                    var def = _server.World.GetBlockDef(new Int3(x, y, z));
                    if (def == null || def.NumericId == 0) continue;
                    bool tree = def.Id != null && (def.Id.EndsWith("leaves") || def.Id.EndsWith("_log"));
                    bool hill = corridor && y >= b.Y + 1 && def.Solid;
                    if (tree || hill) Set(x, y, z, "genesis:air");
                }
            }
            // Бур на богатой залежи → лента → горн → сундук
            Set(b.X, b.Y - 1, b.Z, "genesis:iron_ore_rich");
            Set(b.X, b.Y, b.Z, "genesis:steam_drill", Direction.South);
            for (int k = 1; k <= 5; k++) Set(b.X + k, b.Y, b.Z, "genesis:conveyor", Direction.East);
            Set(b.X + 6, b.Y, b.Z, "genesis:stone_furnace", Direction.South);
            Set(b.X + 7, b.Y, b.Z, "genesis:chest", Direction.South);
            // Свет: лампы и факелы
            Set(b.X + 3, b.Y, b.Z + 2, "genesis:lamp");
            Set(b.X - 1, b.Y, b.Z - 2, "genesis:lamp");
            Set(b.X + 6, b.Y, b.Z - 2, "genesis:torch");
            Set(b.X + 1, b.Y, b.Z + 3, "genesis:torch");
            Set(b.X + 8, b.Y, b.Z + 2, "genesis:lamp");
            Set(b.X + 2, b.Y, b.Z - 2, "genesis:torch");
            Set(b.X + 9, b.Y, b.Z - 2, "genesis:torch");
            // Горн уже работает
            var furnace = _server.Factory.MachineAt(new Int3(b.X + 6, b.Y, b.Z));
            if (furnace != null)
            {
                _server.Factory.InsertIntoMachine(furnace, new Simulation.ItemStack(_server.Content.ItemId("genesis:coal"), 16));
                _server.Factory.InsertIntoMachine(furnace, new Simulation.ItemStack(_server.Content.ItemId("genesis:iron_ore"), 12));
            }
        }

        /// <summary>Предупредить, если камера всё-таки внутри блока (кадр будет испорчен).</summary>
        private void CheckEye()
        {
            var p = new Int3(Mathf.FloorToInt(_eye.x), Mathf.FloorToInt(_eye.y), Mathf.FloorToInt(_eye.z));
            var def = _server.World.GetBlockDef(p);
            if (def != null && def.NumericId != 0 && def.Solid)
            {
                Debug.LogWarning($"[Screens] Камера внутри блока {def.Id} в {p} — убираю его.");
                Set(p.X, p.Y, p.Z, "genesis:air");
            }
        }

        /// <summary>Пар буру и руда на ленту — чтобы линия двигалась на кадре.</summary>
        private void FeedFactory()
        {
            var drill = _server.Factory.MachineAt(_base);
            var steam = drill?.Tank("steam");
            int steamId = _server.Content.Fluids.IndexOf("genesis:steam");
            if (steam != null && steamId >= 0) steam.Fill(steamId, 200);
            var belt = _server.Factory.ConveyorAt(new Int3(_base.X + 1, _base.Y, _base.Z));
            if (belt != null && belt.CanAccept()) belt.Accept(_server.Content.ItemId("genesis:iron_ore"), 0f, 0f);
        }

        private void SpawnCreature(string id, Vector3 pos)
        {
            var def = _server.Content.Enemies.Get(id);
            if (def == null) return;
            _server.Enemies.Spawn(def, new Vec3(pos.x, pos.y, pos.z));
        }
    }
}
