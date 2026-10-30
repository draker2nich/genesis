using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Client.Rendering;
using Genesis.Client.UI;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Net;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Живая сцена за главным меню в духе Metro: не облёт с высоты, а «место» — лагерь выжившего в пустоши,
    /// камера на уровне глаз сидящего у костра. Каждый пункт меню — поворот головы к своему предмету:
    /// «Играть» → стол с журналом, «Подключиться» → терминал связи, «Моды» → ящик, «Настройки» → костёр.
    /// Рядом с костром — грядка: росток, который разрастается вместе с прогрессом последнего мира (дни в нём).
    ///
    /// Сцена строится во временном мире без сохранения. Место ищется один раз по генератору (биом Wasteland,
    /// ровная площадка) и кешируется в PlayerPrefs для пары «seed + версия генератора».
    /// </summary>
    public sealed partial class GenesisBootstrap
    {
        /// <summary>Фаза суток в меню: солнце только что село, горизонт ещё тлеет (0.5 — закат).</summary>
        private const float MenuPhase = 0.507f;
        /// <summary>Высота глаз над землёй: сидим у костра.</summary>
        private const float MenuEyeHeight = 1.3f;
        /// <summary>Узкий «киношный» угол обзора — сцена ближе и собраннее, чем в игре.</summary>
        private const float MenuFov = 58f;
        /// <summary>Сколько секунд камера поворачивается к новому предмету.</summary>
        private const float MenuTurnTime = 0.55f;

        private LocalSession _panorama;
        private readonly List<GameObject> _panoramaObjects = new List<GameObject>();
        private bool _panoramaFailed;
        private EnvironmentRenderer _panoramaEnv;
        private WorldRenderer _panoramaRenderer;
        private MenuAmbience _ambience;
        private float _panoramaStartedAt;
        /// <summary>Затемнение сцены при появлении: 1 — чёрный экран, 0 — сцена видна.</summary>
        private float _menuFade = 1f;

        // Площадка: _siteOrigin — первый блок воздуха над землёй под камерой; оси — «вперёд» и «вправо» от зрителя
        private Int3 _siteOrigin, _siteFwd = new Int3(0, 0, 1), _siteRight = new Int3(1, 0, 0);

        // Камера
        private float _camYaw, _camPitch, _camYawVel, _camPitchVel;
        private bool _camSnap;
        private Vector2 _mouseParallax;

        // Последний мир: пункт «Продолжить» и размер грядки
        private string _latestWorld;
        private int _latestDays;

        // ============================================================ Жизненный цикл

        private void StartPanorama()
        {
            if (_panorama != null || _panoramaFailed || _session != null) return;
            try
            {
                bool threads = Genesis.Core.Platform.Threads;
                var server = GameServer.Open(_content, null, DefaultSeed, threads ? Mathf.Clamp(SystemInfo.processorCount - 2, 1, 4) : 0);
                if (!threads) server.World.MainThreadBudgetMs = 6;
                server.ViewRadius = threads ? 6 : 4;
                server.VerticalRadius = 3;
                server.Enemies.SpawningEnabled = false;
                _panorama = new LocalSession(server, "panorama");

                ReadLatestWorld();
                FindMenuSite(server.World.Generator);
                var eye = MenuEye();
                _panorama.LocalPlayer.Position = new Vec3(eye.x, eye.y, eye.z);

                var mats = CreateMaterials();
                var cam = EnsureCamera();
                cam.farClipPlane = 7 * 16f;
                cam.fieldOfView = MenuFov;
                _panoramaEnv = CreateWorldView(_panorama, cam, mats, _panoramaObjects, false, DefaultSeed);
                foreach (var go in _panoramaObjects)
                {
                    var wr = go != null ? go.GetComponent<WorldRenderer>() : null;
                    if (wr != null) _panoramaRenderer = wr;
                }

                BuildMenuScene(server);
                SetMenuTime(server);

                var audioGo = new GameObject("MenuAmbience");
                _panoramaObjects.Add(audioGo);
                audioGo.AddComponent<AudioSource>();
                _ambience = audioGo.AddComponent<MenuAmbience>();

                _camSnap = true;
                _menuFade = 1f;
                _panoramaStartedAt = Time.unscaledTime;
                _panoramaActive = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Genesis] Menu panorama disabled: " + e.Message);
                _panoramaFailed = true;
                StopPanorama();
            }
        }

        private void StopPanorama()
        {
            _panoramaActive = false;
            if (_panorama != null)
            {
                _panorama.Dispose();
                _panorama = null;
            }
            foreach (var go in _panoramaObjects) if (go != null) Destroy(go);
            _panoramaObjects.Clear();
            _panoramaEnv = null;
            _panoramaRenderer = null;
            _ambience = null;
        }

        private void UpdatePanorama()
        {
            if (_panorama == null)
            {
                if (_mode == Mode.Menu && _session == null) StartPanorama();
                return;
            }
            var cam = _mainCamera;
            if (cam == null) return;
            float dt = Time.unscaledDeltaTime, t = Time.unscaledTime;
            cam.fieldOfView = MenuFov; // настройки FOV в меню относятся к игре, не к сцене

            // Куда смотреть: предмет текущего экрана и где он должен оказаться по горизонтали экрана
            MenuViewTarget(out var look, out float screenX);
            var eye = MenuEye();
            var d = look - eye;
            float objYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float objPitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;
            float halfH = Mathf.Atan(Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * cam.aspect);
            float offset = Mathf.Atan(Mathf.Tan(halfH) * (2f * screenX - 1f)) * Mathf.Rad2Deg;
            float targetYaw = objYaw - offset, targetPitch = objPitch - 3f;

            if (_camSnap)
            {
                _camYaw = targetYaw;
                _camPitch = targetPitch;
                _camYawVel = _camPitchVel = 0f;
                _camSnap = false;
            }
            else
            {
                _camYaw = Mathf.SmoothDampAngle(_camYaw, targetYaw, ref _camYawVel, MenuTurnTime, Mathf.Infinity, dt);
                _camPitch = Mathf.SmoothDampAngle(_camPitch, targetPitch, ref _camPitchVel, MenuTurnTime, Mathf.Infinity, dt);
            }

            // Живая камера: лёгкий параллакс за мышью, «дыхание» и медленный дрейф головы
            var mouse = Input.mousePosition;
            var m = new Vector2(mouse.x / Mathf.Max(1, Screen.width) - 0.5f, mouse.y / Mathf.Max(1, Screen.height) - 0.5f) * 2f;
            m = Vector2.ClampMagnitude(m, 1.4f);
            _mouseParallax = Vector2.Lerp(_mouseParallax, m, 1f - Mathf.Exp(-dt * 2.5f));
            float swayYaw = (Mathf.PerlinNoise(t * 0.09f, 0.3f) - 0.5f) * 1.6f;
            float swayPitch = (Mathf.PerlinNoise(0.7f, t * 0.11f) - 0.5f) * 1.0f;
            float breath = Mathf.Sin(t * 1.05f) * 0.012f;
            float yaw = _camYaw + swayYaw + _mouseParallax.x * 1.4f;
            float pitch = _camPitch + swayPitch - _mouseParallax.y * 0.8f;
            float roll = Mathf.Sin(t * 0.37f) * 0.25f;
            cam.transform.SetPositionAndRotation(eye + Vector3.up * breath, Quaternion.Euler(pitch, yaw, roll));

            _panorama.LocalPlayer.Position = new Vec3(eye.x, eye.y, eye.z);
            _panorama.Update(Time.deltaTime, Time.time);
            SetMenuTime(_panorama.Server); // время стоит: всегда тот же час после заката

            // Костёр «дышит»: блочный свет слегка мерцает
            if (_panoramaEnv != null)
                _panoramaEnv.BlockLightScale = 1f + (Mathf.PerlinNoise(t * 2.7f, 5.1f) - 0.5f) * 0.22f
                                                  + (Mathf.PerlinNoise(t * 9.3f, 1.7f) - 0.5f) * 0.08f;

            // Проявление из темноты, когда догрузились чанки и меши вокруг (не дольше 6 с)
            bool settled = _panorama.World.PendingCount == 0 && (_panoramaRenderer == null || _panoramaRenderer.QueuedChunks == 0);
            float since = t - _panoramaStartedAt;
            if ((settled && since > 0.4f) || since > 6f) _menuFade = Mathf.MoveTowards(_menuFade, 0f, dt / 1.6f);
        }

        private static void SetMenuTime(GameServer server)
        {
            long day = server.WorldTime / GameServer.DayLength;
            server.WorldTime = day * GameServer.DayLength + (long)(MenuPhase * GameServer.DayLength);
        }

        // ============================================================ Ракурсы

        private Vector3 MenuEye() => SitePoint(0f, MenuEyeHeight, 0f);

        /// <summary>
        /// Точка, на которую смотрим, и её место на экране (0 — левый край, 1 — правый): предмет оказывается
        /// по другую сторону от панели экрана, чтобы панель его не закрывала.
        /// </summary>
        private void MenuViewTarget(out Vector3 look, out float screenX)
        {
            if (_mode == Mode.Connecting)
            {
                look = SitePoint(-6f, 0.8f, 6f); // терминал связи
                screenX = 0.5f;
                return;
            }
            switch (_screen)
            {
                case MenuScreen.Play: look = SitePoint(5f, 1.05f, 2f); screenX = 0.75f; break;   // стол с журналом
                case MenuScreen.Join: look = SitePoint(-6f, 0.8f, 6f); screenX = 0.26f; break;   // терминал связи
                case MenuScreen.Mods: look = SitePoint(-4f, 0.55f, 2f); screenX = 0.27f; break;  // ящик
                case MenuScreen.Settings: look = SitePoint(1f, 0.45f, 4f); screenX = 0.72f; break; // костёр
                default: look = SitePoint(2.5f, 0.9f, 11f); screenX = 0.6f; break;             // лагерь и горизонт
            }
        }

        /// <summary>С какой стороны экрана панель подменю: -1 слева, +1 справа, 0 по центру.</summary>
        private static float PanelSideFor(MenuScreen s)
        {
            switch (s)
            {
                case MenuScreen.Main: return 0f;
                case MenuScreen.Join:
                case MenuScreen.Mods: return 1f;
                default: return -1f;
            }
        }

        // ============================================================ Координаты площадки

        /// <summary>Блок площадки: x — вправо от зрителя, y — вверх от земли (0 — первый слой над землёй), z — вперёд.</summary>
        private Int3 SiteBlock(int x, int y, int z) =>
            new Int3(_siteOrigin.X + _siteRight.X * x + _siteFwd.X * z,
                     _siteOrigin.Y + y,
                     _siteOrigin.Z + _siteRight.Z * x + _siteFwd.Z * z);

        /// <summary>Точка площадки: (x, z) — центр блока SiteBlock(x, *, z), y — высота над землёй.</summary>
        private Vector3 SitePoint(float x, float y, float z)
        {
            var o = new Vector3(_siteOrigin.X + 0.5f, _siteOrigin.Y, _siteOrigin.Z + 0.5f);
            return o + new Vector3(_siteRight.X, 0f, _siteRight.Z) * x + new Vector3(_siteFwd.X, 0f, _siteFwd.Z) * z + Vector3.up * y;
        }

        // ============================================================ Поиск места

        private void FindMenuSite(WorldGenerator gen)
        {
            string key = "genesis.menu_site." + gen.Seed + "." + WorldGenerator.Version;
            if (!TryParseSite(PlayerPrefs.GetString(key, ""), out int sx, out int sz, out int dir))
            {
                // Сначала настоящая пустошь; если в этом мире её нет рядом — любая сухая ровная суша
                if (!SearchSite(gen, true, 256, 3600, out sx, out sz) && !SearchSite(gen, false, 64, 1500, out sx, out sz))
                {
                    sx = 40;
                    sz = 40;
                }
                dir = PickViewDirection(gen, sx, sz);
                PlayerPrefs.SetString(key, sx + ";" + sz + ";" + dir);
                PlayerPrefs.Save();
            }
            var f = Directions.Horizontal[dir & 3].Offset();
            _siteFwd = new Int3(f.X, 0, f.Z);
            _siteRight = new Int3(f.Z, 0, -f.X);
            var col = gen.ColumnAt(sx, sz);
            int ground = Math.Max(gen.SurfaceY(sx, sz, col), WorldConstants.SeaLevel + 1);
            _siteOrigin = new Int3(sx, ground + 1, sz);
        }

        private static bool TryParseSite(string s, out int x, out int z, out int dir)
        {
            x = z = dir = 0;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(';');
            return parts.Length == 3 && int.TryParse(parts[0], out x) && int.TryParse(parts[1], out z) && int.TryParse(parts[2], out dir);
        }

        /// <summary>Обход колец от центра мира: первая подходящая площадка.</summary>
        private static bool SearchSite(WorldGenerator gen, bool wasteland, int r0, int r1, out int sx, out int sz)
        {
            const int Step = 64;
            int limit = WorldConstants.HalfWorld - 200;
            for (int r = r0; r <= r1; r += Step)
            {
                int n = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * r / Step));
                float a0 = r * 0.618f;
                for (int i = 0; i < n; i++)
                {
                    float a = a0 + i * 2f * Mathf.PI / n;
                    int x = Mathf.RoundToInt(Mathf.Cos(a) * r), z = Mathf.RoundToInt(Mathf.Sin(a) * r);
                    if (Math.Abs(x) > limit || Math.Abs(z) > limit) continue;
                    if (IsMenuSite(gen, x, z, wasteland))
                    {
                        sx = x;
                        sz = z;
                        return true;
                    }
                }
            }
            sx = sz = 0;
            return false;
        }

        /// <summary>Ровная (перепад ≤ 3) суша 25×25 без каньонов и парящих островов над головой.</summary>
        private static bool IsMenuSite(WorldGenerator gen, int x, int z, bool wasteland)
        {
            var c = gen.Column(x, z);
            if (wasteland ? c.Biome != Biome.Wasteland : !IsDryLand(c.Biome)) return false;
            if (c.Height < WorldConstants.SeaLevel + 3 || c.Canyon > 0.05f || c.Island > 0.02f || c.Amp > 2f) return false;
            int lo = c.Height, hi = c.Height, other = 0;
            for (int dz = -12; dz <= 12; dz += 4)
            for (int dx = -12; dx <= 12; dx += 4)
            {
                var k = gen.Column(x + dx, z + dz);
                if (k.Height < WorldConstants.SeaLevel + 2 || k.Canyon > 0.05f || k.Island > 0.02f) return false;
                lo = Math.Min(lo, k.Height);
                hi = Math.Max(hi, k.Height);
                if (hi - lo > 3) return false;
                if (wasteland && k.Biome != Biome.Wasteland && ++other > 4) return false;
            }
            return true;
        }

        private static bool IsDryLand(Biome b) => b == Biome.Plains || b == Biome.Meadow || b == Biome.Badlands || b == Biome.Desert;

        /// <summary>
        /// Куда смотреть с площадки: впереди — рельеф средней высоты (силуэты на горизонте, но не стена) и без моря;
        /// запад в приоритете — там догорает закат.
        /// </summary>
        private static int PickViewDirection(WorldGenerator gen, int x, int z)
        {
            int h0 = gen.Column(x, z).Height;
            int best = 0;
            float bestScore = float.MinValue;
            for (int d = 0; d < 4; d++)
            {
                var dirEnum = Directions.Horizontal[d];
                var f = dirEnum.Offset();
                int maxAhead = int.MinValue, water = 0;
                for (int t = 24; t <= 96; t += 12)
                {
                    var c = gen.Column(x + f.X * t, z + f.Z * t);
                    maxAhead = Math.Max(maxAhead, c.Height);
                    if (c.Height < WorldConstants.SeaLevel) water++;
                }
                float score = -Mathf.Abs(maxAhead - h0 - 10) - water * 3f;
                if (dirEnum == Direction.West) score += 6f;
                else if (dirEnum != Direction.East) score += 2f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = d;
                }
            }
            return best;
        }

        // ============================================================ Прогресс последнего мира

        /// <summary>Самый свежий мир и сколько дней в нём прожито (WorldTime из world.dat).</summary>
        private void ReadLatestWorld()
        {
            _latestWorld = null;
            _latestDays = 0;
            try
            {
                RefreshWorlds();
                if (_worlds == null || _worlds.Count == 0) return;
                _latestWorld = _worlds[0].Name;
                long time = -1;
                SafeFile.TryRead(Path.Combine(SavesRoot, _latestWorld, "world.dat"), r =>
                {
                    int ver = r.ReadInt32();
                    if (ver < 1 || ver > 64) return; // чужой формат — дни не знаем
                    r.ReadInt64(); // seed
                    if (ver >= 2) time = r.ReadInt64();
                });
                if (time > 0) _latestDays = (int)Math.Min(time / GameServer.DayLength, 100000);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Genesis] Menu: can't read latest world: " + e.Message);
            }
        }

        // ============================================================ Постройка сцены

        /// <summary>
        /// Лагерь: площадка зачищается и выравнивается, вокруг всё живое «умирает» (на случай не-пустоши),
        /// затем грядка по прогрессу и предметы лагеря.
        /// </summary>
        private void BuildMenuScene(GameServer server)
        {
            var world = server.World;
            var content = server.Content;

            // Чанки площадки — сразу; остальное догрузится в фоне за затемнением
            var a = SiteBlock(-15, -6, -6);
            var b = SiteBlock(15, 24, 24);
            var c0 = WorldConstants.ChunkOf(new Int3(Math.Min(a.X, b.X), a.Y, Math.Min(a.Z, b.Z)));
            var c1 = WorldConstants.ChunkOf(new Int3(Math.Max(a.X, b.X), b.Y, Math.Max(a.Z, b.Z)));
            for (int cx = c0.X; cx <= c1.X; cx++)
            for (int cy = c0.Y; cy <= c1.Y; cy++)
            for (int cz = c0.Z; cz <= c1.Z; cz++)
                world.LoadNow(new Int3(cx, cy, cz));

            ushort Id(string name)
            {
                var def = content.Blocks.Get("genesis:" + name);
                return def != null ? def.NumericId : content.AirNumeric;
            }

            ushort air = content.AirNumeric;
            ushort ash = Id("ash"), deadGrass = Id("dead_grass"), dirt = Id("dirt"), deadLog = Id("dead_log"), deadBush = Id("dead_bush");
            if (ash == air) ash = deadGrass;

            void Set(int x, int y, int z, ushort id)
            {
                var p = SiteBlock(x, y, z);
                if (world.IsLoaded(p.X, p.Y, p.Z)) world.SetBlock(p, id);
            }

            ushort Wither(BlockDef def)
            {
                string id = def.Id;
                if (id == null) return def.NumericId;
                if (id == "genesis:grass") return deadGrass;
                if (id.EndsWith("leaves")) return air;
                if (id.EndsWith("_log") && id != "genesis:dead_log") return deadLog;
                if (id == "genesis:tall_grass" || id == "genesis:fern" || id == "genesis:berry_bush" || id.StartsWith("genesis:flower_"))
                    return deadBush;
                return def.NumericId;
            }

            // ---- Площадка: внутри — ровная земля из пепла и сухой травы и пусто над ней, снаружи — всё живое засыхает
            long noiseSeed = DefaultSeed ^ 0x3E5;
            for (int z = -6; z <= 24; z++)
            for (int x = -15; x <= 15; x++)
            {
                bool inner = x >= -12 && x <= 12 && z >= -4 && z <= 20;
                double roll = Hash.Unit(Hash.Coord(noiseSeed, x, z));
                for (int y = -6; y <= 24; y++)
                {
                    var p = SiteBlock(x, y, z);
                    if (!world.IsLoaded(p.X, p.Y, p.Z)) continue;
                    var def = world.GetBlockDef(p);
                    if (inner)
                    {
                        if (y >= 0)
                        {
                            if (def.NumericId != air) world.SetBlock(p, air);
                        }
                        else if (y == -1) world.SetBlock(p, roll < 0.62 ? ash : deadGrass);
                        else if (y >= -4 && !def.Solid) world.SetBlock(p, dirt);
                        continue;
                    }
                    ushort dead = Wither(def);
                    if (dead != def.NumericId) world.SetBlock(p, dead);
                }
            }

            PlantGarden(Set, Id);

            // ---- Лагерь
            Set(1, 0, 4, Id("campfire"));
            Set(-1, 0, 4, Id("kindling"));
            Set(2, 0, 2, Id("plank_slab"));          // чурбак напротив
            Set(5, 0, 2, Id("lab_table"));           // «Играть»: стол с журналом миров
            Set(5, 1, 2, Id("handbook"));
            Set(-6, 0, 6, Id("lab_terminal"));       // «Подключиться»: терминал связи
            PlaceMachine(server, SiteBlock(-4, 0, 2), "genesis:chest"); // «Моды»: ящик с инструментами
            Set(5, 0, 9, Id("cryo_capsule_open"));   // капсула, из которой всё началось
            Set(5, 1, 9, Id("cryo_capsule_top"));
            Set(3, 0, 7, Id("fence"));
            Set(3, 1, 7, Id("torch"));
            ushort pebbles = Id("pebbles");
            Set(2, 0, 6, pebbles);
            Set(-2, 0, 9, pebbles);
            Set(7, 0, 5, pebbles);
            Set(-3, 0, 12, pebbles);

            // Обломок стены слева в глубине — тёмный силуэт за текстом меню
            ushort bricks = Id("ruin_bricks"), rubble = Id("concrete_rubble");
            int[] wall = { 2, 4, 3, 5, 1, 3, 2 };
            for (int i = 0; i < wall.Length; i++)
            {
                for (int y = 0; y < wall[i]; y++) Set(-12 + i, y, 16, bricks);
                if (i % 2 == 0) Set(-12 + i, 0, 15, rubble);
            }
            Set(-7, 0, 13, rubble);
            Set(8, 0, 12, rubble);

            // Сухое дерево справа в глубине
            for (int y = 0; y < 6; y++) Set(10, y, 15, deadLog);
            Set(11, 3, 15, deadLog);
            Set(12, 4, 15, deadLog);
            Set(9, 4, 15, deadLog);
        }

        /// <summary>
        /// Грядка у костра. Миров ещё нет — один росток на вскопанной земле. Дальше пятно травы растёт с днями
        /// в последнем мире (≈√дней), в нём появляются папоротник, цветы, ягоды, а после 20 дней — молодое дерево.
        /// </summary>
        private void PlantGarden(Action<int, int, int, ushort> set, Func<string, ushort> id)
        {
            const int Gx = 1, Gz = 9;
            ushort grass = id("grass"), farmland = id("farmland"), tallGrass = id("tall_grass"), fern = id("fern"), berry = id("berry_bush");
            ushort[] flowers = { id("flower_red"), id("flower_yellow"), id("flower_blue") };

            set(Gx, -1, Gz, farmland != 0 ? farmland : grass);
            set(Gx, 0, Gz, fern != 0 ? fern : tallGrass);
            if (_latestWorld == null) return;

            int days = _latestDays;
            float radius = Mathf.Min(6.5f, 1.2f + Mathf.Sqrt(days) * 1.05f);
            int r = Mathf.CeilToInt(radius + 1f);
            long seed = DefaultSeed ^ 0x6A4D;
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                ulong h = Hash.Coord(seed, dx, dz);
                float dist = Mathf.Sqrt(dx * dx + dz * dz) + ((float)Hash.Unit(h) - 0.5f) * 1.4f;
                if (dist > radius) continue;
                set(Gx + dx, -1, Gz + dz, grass);
                double roll = Hash.Unit(Hash.SplitMix(h));
                ushort plant = 0;
                if (roll < 0.30) plant = tallGrass;
                else if (roll < 0.40 && days >= 2) plant = fern;
                else if (roll < 0.47 && days >= 5) plant = flowers[(int)(h % 3)];
                else if (roll < 0.50 && days >= 10) plant = berry;
                if (plant != 0) set(Gx + dx, 0, Gz + dz, plant);
            }

            if (days >= 20)
            {
                ushort log = id("oak_log"), leaves = id("leaves");
                int tx = Gx - 2, tz = Gz + 2, top = days >= 60 ? 4 : 3;
                for (int y = 0; y < top; y++) set(tx, y, tz, log);
                for (int y = top - 1; y <= top + 1; y++)
                for (int lz = -2; lz <= 2; lz++)
                for (int lx = -2; lx <= 2; lx++)
                {
                    int rr = lx * lx + lz * lz + (y - top) * (y - top) * 2;
                    if (rr > (y == top + 1 ? 2 : 5) || (lx == 0 && lz == 0 && y < top)) continue;
                    set(tx + lx, y, tz + lz, leaves);
                }
            }
        }

        /// <summary>Машина (ящик) с сущностью фабрики, «лицом» к зрителю.</summary>
        private void PlaceMachine(GameServer server, Int3 p, string blockId)
        {
            var def = server.Content.Blocks.Get(blockId);
            if (def == null || !server.World.IsLoaded(p.X, p.Y, p.Z)) return;
            int dx = _siteOrigin.X - p.X, dz = _siteOrigin.Z - p.Z;
            Direction facing = Math.Abs(dx) > Math.Abs(dz)
                ? (dx > 0 ? Direction.East : Direction.West)
                : (dz > 0 ? Direction.North : Direction.South);
            server.Factory.OnBlockRemoved(p);
            if (server.World.SetBlock(p, def.NumericId)) server.Factory.OnBlockPlaced(p, def, facing);
        }
    }
}
