using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Client.Rendering;
using Genesis.Client.UI;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Modding;
using Genesis.Net;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Точка входа. Создаётся автоматически в любой сцене при нажатии Play (сцену настраивать не нужно).
    /// Загружает контент-моды, показывает главное меню и поднимает сессию:
    /// одиночная игра / хост (тот же локальный сервер + сеть) / подключение к другу.
    /// </summary>
    public sealed partial class GenesisBootstrap : MonoBehaviour
    {
        public long DefaultSeed = 20261015;
        public int ViewRadius = 8;
        public float AutosaveMinutes = 5f;

        private enum Mode { Menu, Connecting, Playing }

        private Mode _mode = Mode.Menu;
        private ContentDatabase _content;
        private Localization _loc;
        private BlockTextureFactory _textures;
        private string _modsRoot;
        private ModLoader _mods;
        /// <summary>Пользовательские моды (доступна на запись в любой сборке).</summary>
        private static string UserModsRoot => Path.Combine(Application.persistentDataPath, "Mods");
        /// <summary>За меню показывается живая панорама мира.</summary>
        private bool _panoramaActive;

        private IGameSession _session;
        private ClientContext _ctx;
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<Component> _components = new List<Component>();
        private float _nextAutosave;
        private Camera _cam;
        private EnvironmentRenderer _env;

        // Поля меню
        private string _playerName = "Player";
        private string _worldName = "world1";
        private string _seedText;
        private string _address = "127.0.0.1";
        private string _portText = Protocol.DefaultPort.ToString();
        private int _transportIndex;
        private string _menuError;

        private const string PrefName = "genesis.name";
        private const string PrefAddress = "genesis.address";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (FindObjectOfType<GenesisBootstrap>() != null) return;
            new GameObject("Genesis").AddComponent<GenesisBootstrap>();
        }

        private void Awake()
        {
            Application.targetFrameRate = 144;
            QualitySettings.vSyncCount = 1;
            Genesis.Core.Log.InfoSink = Debug.Log;
            Genesis.Core.Log.WarnSink = Debug.LogWarning;
            Genesis.Core.Log.ErrorSink = Debug.LogError;
            GameSettings.Load();
            ViewRadius = GameSettings.ViewRadius;

            _modsRoot = Path.Combine(Application.streamingAssetsPath, ModLoader.CoreFolderName);
            LoadContent();

            TransportRegistry.EnsureBuiltIn();
            _seedText = DefaultSeed.ToString();
            _playerName = PlayerPrefs.GetString(PrefName, "Player");
            _address = PlayerPrefs.GetString(PrefAddress, "127.0.0.1");
            GameCursor.Apply();
            PlayerController.LockCursor(false);
            StartPanorama();
        }

        /// <summary>
        /// Загрузить весь контент: базовый мод «genesis» и включённые моды (JSON, затем DLL), заморозить базу,
        /// собрать текстуры. Повторно вызывается из экрана модов после изменений.
        /// </summary>
        private void LoadContent()
        {
            var lang = GameSettings.Language;
            _content = new ContentDatabase();
            _loc = new Localization { Language = lang };
            _mods = new ModLoader(Path.Combine(Application.persistentDataPath, "mods.json"));
            Directory.CreateDirectory(UserModsRoot);
            _mods.Discover(new[] { UserModsRoot }, null, _modsRoot);
            foreach (var m in _mods.LoadOrder) Debug.Log($"[Mods] {m.Id} {m.Manifest.Version} ({m.Source}){(m.HasCode ? " +code" : "")}");
            _mods.LoadContent(_content, _loc);
            _mods.LoadCode(_content, _loc);
            if (!_content.Freeze())
                foreach (var e in _content.Errors) Debug.LogError("[Content] " + e);
            Debug.Log($"[Genesis] Content: {_content.Blocks.Count} blocks, {_content.Items.Count} items, " +
                      $"{_content.Machines.Count} machines, {_content.Recipes.Count} recipes, hash {_content.ContentHash:X16}");

            _textures = new BlockTextureFactory();
            _textures.Build(_content, _modsRoot, _mods.LoadOrder);

            // Модели Blockbench (models/*.bbmodel в игре и модах)
            Rendering.Models.ModelLibrary.Current?.Dispose();
            Rendering.Models.ModelLibrary.Current = new Rendering.Models.ModelLibrary(_mods.LoadOrder);
            Rendering.Models.ModelLibrary.Current.Preload(_content);
        }

        private static int Workers => Mathf.Clamp(SystemInfo.processorCount - 2, 1, 6);

        // ============================================================ Запуск сессий

        private void StartLocal(bool host)
        {
            _menuError = null;
            StopPanorama();
            try
            {
                long seed = long.TryParse(_seedText, out var s) ? s : DefaultSeed;
                string saveDir = Path.Combine(Application.persistentDataPath, "saves", SafeName(_worldName));
                _mapFile = Path.Combine(saveDir, "map.dat");
                var server = GameServer.Open(_content, saveDir, seed, Workers);
                server.ViewRadius = ViewRadius;
                _mods.AttachServer(server);
                var local = new LocalSession(server, _playerName);
                local.NetLog += msg => Debug.Log("[Net] " + msg);
                if (host)
                {
                    var provider = TransportRegistry.All[_transportIndex % TransportRegistry.All.Count];
                    int port = int.TryParse(_portText, out var p) ? p : Protocol.DefaultPort;
                    try
                    {
                        local.StartHosting(provider.CreateServer(port), provider.Name);
                    }
                    catch (Exception e)
                    {
                        local.Dispose();
                        _menuError = "Host failed: " + e.Message;
                        return;
                    }
                }
                Debug.Log($"[Genesis] World '{_worldName}' seed {server.Seed} at {saveDir}");
                _session = local;
                BuildGame();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                _menuError = e.Message;
            }
        }

        private void StartRemote()
        {
            _menuError = null;
            StopPanorama();
            var provider = TransportRegistry.All[_transportIndex % TransportRegistry.All.Count];
            int port = int.TryParse(_portText, out var p) ? p : Protocol.DefaultPort;
            try
            {
                _mapFile = null; // для подключения — после приветствия (нужен seed)
                var remote = new RemoteSession(_content, provider.CreateClient(_address.Trim(), port), _playerName, Workers)
                {
                    ViewRadius = ViewRadius,
                    ModList = _mods.Fingerprint
                };
                _session = remote;
                _mode = Mode.Connecting;
            }
            catch (Exception e)
            {
                _menuError = e.Message;
            }
        }

        /// <summary>Файл карты текущего мира (для подключения к другу — в папке maps по адресу и seed).</summary>
        private string _mapFile;

        private static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "world1" : s.Trim();
        }

        private GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        /// <summary>Закрыть сессию и вернуться в меню.</summary>
        public void ReturnToMenu(string error)
        {
            if (_session != null)
            {
                if (_session.CanSave)
                {
                    try { _session.Save(); } catch (Exception e) { Debug.LogException(e); }
                }
                _session.Dispose();
                _session = null;
            }
            foreach (var c in _components) if (c != null) Destroy(c);
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _components.Clear();
            _spawned.Clear();
            _ctx = null;
            _env = null;
            _menuError = error;
            _mode = Mode.Menu;
            PlayerController.LockCursor(false);
            StartPanorama();
        }

        // ============================================================ Цикл

        private void Update()
        {
            ModLoader.Workshop?.Update();
            UpdatePanorama();
            if (_session == null) return;
            _session.Update(Time.deltaTime, Time.time);

            if (_session.State == SessionState.Failed)
            {
                ReturnToMenu(_session.Error ?? "Connection failed");
                return;
            }
            if (_mode == Mode.Connecting && _session.State == SessionState.Playing)
            {
                BuildGame();
                return;
            }

            if (_mode == Mode.Playing && _session.CanSave && Time.time >= _nextAutosave)
            {
                _nextAutosave = Time.time + AutosaveMinutes * 60f;
                SaveNow();
            }
        }

        public void SaveNow()
        {
            if (_session == null || !_session.CanSave) return;
            try
            {
                _session.Save();
                _ctx?.Map?.Save();
                _ctx?.ShowToast(_loc.Get("ui.saved"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void OnApplicationQuit()
        {
            if (_session == null) return;
            if (_session.CanSave)
            {
                try { _session.Save(); } catch (Exception e) { Debug.LogException(e); }
            }
            _session.Dispose();
            _session = null;
        }

        private void OnDestroy()
        {
            _session?.Dispose();
            _session = null;
        }

    }
}
