using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Newtonsoft.Json;

namespace Genesis.Modding
{
    public enum ModSource
    {
        Builtin,
        Local,
        Workshop
    }

    public sealed class ModInfo
    {
        public ModManifest Manifest;
        public string Folder;
        public ModSource Source;
        /// <summary>Включён пользователем.</summary>
        public bool Enabled = true;
        /// <summary>Реально загружается (включён и все зависимости в порядке).</summary>
        public bool Active;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Assemblies = new List<string>();
        public int CodeEntryPoints;

        public string Id => Manifest.Id;
        public bool IsBase => Id == ModLoader.BaseModId;
        public bool HasCode => Assemblies.Count > 0;
    }

    /// <summary>Steam Workshop: установленные предметы и публикация. Реализация — в отдельной сборке со Steamworks.NET.</summary>
    public interface IWorkshopProvider
    {
        bool Available { get; }
        /// <summary>Папки установленных (подписанных) модов.</summary>
        IEnumerable<string> InstalledFolders();
        /// <summary>Опубликовать или обновить мод. Результат: null — успех, иначе текст ошибки.</summary>
        void Publish(ModInfo mod, string changeNote, Action<string> done);
        /// <summary>Вызывать каждый кадр (колбэки Steam).</summary>
        void Update();
    }

    /// <summary>
    /// Загрузчик модов: поиск (локальная папка Mods + Workshop), манифесты, зависимости и порядок,
    /// включение/отключение, загрузка JSON-контента и DLL, подключение событий к серверу.
    /// Контент самой игры (ядро, папка StreamingAssets/Game, id «genesis») — не мод: грузится первым,
    /// не отключается и не показывается в списке модов. Моды — только то, что добавляют игроки.
    /// </summary>
    public sealed class ModLoader
    {
        public const string BaseModId = "genesis";
        /// <summary>Папка ядра игры внутри StreamingAssets.</summary>
        public const string CoreFolderName = "Game";
        public static IWorkshopProvider Workshop;

        public readonly List<ModInfo> Mods = new List<ModInfo>();
        public readonly ModEvents Events = new ModEvents();
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _configPath;
        private readonly List<ModInfo> _order = new List<ModInfo>();

        public IReadOnlyList<ModInfo> LoadOrder => _order;

        /// <param name="configPath">Файл с настройками включения модов (null — не сохранять).</param>
        public ModLoader(string configPath)
        {
            _configPath = configPath;
            LoadConfig();
        }

        // ============================================================ Поиск

        /// <param name="localRoots">Папки с модами игроков.</param>
        /// <param name="extraFolders">Папки отдельных модов (по умолчанию — подписки Workshop).</param>
        /// <param name="coreFolder">Папка контента самой игры (StreamingAssets/Game).</param>
        public void Discover(IEnumerable<string> localRoots, IEnumerable<string> extraFolders = null, string coreFolder = null)
        {
            Mods.Clear();
            if (coreFolder != null && Directory.Exists(coreFolder)) TryAdd(coreFolder, ModSource.Builtin);
            foreach (var root in localRoots)
            {
                if (root == null || !Directory.Exists(root)) continue;
                var dirs = Directory.GetDirectories(root);
                Array.Sort(dirs, StringComparer.Ordinal);
                foreach (var d in dirs) TryAdd(d, Path.GetFileName(d) == BaseModId ? ModSource.Builtin : ModSource.Local);
            }
            var workshop = extraFolders ?? (Workshop != null && Workshop.Available ? Workshop.InstalledFolders() : Enumerable.Empty<string>());
            foreach (var d in workshop) TryAdd(d, ModSource.Workshop);
            ResolveOrder();
        }

        private void TryAdd(string folder, ModSource source)
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) return;

            ModManifest manifest = null;
            var info = new ModInfo { Folder = folder, Source = source };
            string path = Path.Combine(folder, "mod.json");
            if (source == ModSource.Builtin && !File.Exists(path)) path = Path.Combine(folder, "game.json");
            if (File.Exists(path))
            {
                try
                {
                    manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(path));
                }
                catch (Exception e)
                {
                    info.Errors.Add("mod.json: " + e.Message);
                }
            }
            manifest = manifest ?? new ModManifest();
            if (string.IsNullOrWhiteSpace(manifest.Id)) manifest.Id = name;
            if (string.IsNullOrWhiteSpace(manifest.Name)) manifest.Name = manifest.Id;
            manifest.Dependencies = manifest.Dependencies ?? new List<string>();
            manifest.LoadAfter = manifest.LoadAfter ?? new List<string>();
            info.Manifest = manifest;

            // id ядра игры занят: мод не может подменить игру целиком (переопределять отдельные вещи — можно)
            if (manifest.Id == BaseModId && source != ModSource.Builtin) return;

            var existing = Mods.Find(m => m.Id == manifest.Id);
            if (existing != null)
            {
                // Локальная копия важнее подписки из Workshop (удобно при разработке мода)
                if (existing.Source == ModSource.Workshop && source != ModSource.Workshop) Mods.Remove(existing);
                else return;
            }

            foreach (var dir in new[] { folder, Path.Combine(folder, "assemblies") })
            {
                if (!Directory.Exists(dir)) continue;
                var dlls = Directory.GetFiles(dir, "*.dll");
                Array.Sort(dlls, StringComparer.Ordinal);
                info.Assemblies.AddRange(dlls);
            }
            info.Enabled = info.IsBase || !_disabled.Contains(info.Id);
            Mods.Add(info);
        }

        // ============================================================ Порядок и зависимости

        private void ResolveOrder()
        {
            _order.Clear();
            var byId = Mods.ToDictionary(m => m.Id, StringComparer.Ordinal);

            foreach (var m in Mods)
            {
                m.Active = m.Enabled && m.Errors.Count == 0;
                foreach (var dep in m.Manifest.Dependencies)
                {
                    if (dep == BaseModId) continue;
                    if (!byId.ContainsKey(dep)) m.Errors.Add("missing dependency: " + dep);
                }
            }

            // Отключённая зависимость выключает и зависящих (до неподвижной точки)
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var m in Mods)
                {
                    if (!m.Active) continue;
                    foreach (var dep in m.Manifest.Dependencies)
                    {
                        if (dep == BaseModId) continue;
                        if (!byId.TryGetValue(dep, out var d) || !d.Active)
                        {
                            m.Active = false;
                            changed = true;
                        }
                    }
                }
            }

            // Топологическая сортировка: базовый мод первым, дальше зависимости и load_after, при равенстве — по id
            var active = Mods.Where(m => m.Active).ToList();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            var baseMod = active.Find(m => m.IsBase);
            if (baseMod != null)
            {
                _order.Add(baseMod);
                placed.Add(baseMod.Id);
            }
            var pending = active.Where(m => !m.IsBase).OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            while (pending.Count > 0)
            {
                ModInfo next = null;
                foreach (var m in pending)
                {
                    bool ready = m.Manifest.Dependencies.All(d => d == BaseModId || placed.Contains(d)) &&
                                 m.Manifest.LoadAfter.All(d => placed.Contains(d) || !pending.Exists(p => p.Id == d));
                    if (ready)
                    {
                        next = m;
                        break;
                    }
                }
                if (next == null)
                {
                    foreach (var m in pending)
                    {
                        m.Errors.Add("dependency cycle");
                        m.Active = false;
                    }
                    break;
                }
                _order.Add(next);
                placed.Add(next.Id);
                pending.Remove(next);
            }
        }

        public void SetEnabled(string id, bool enabled)
        {
            if (id == BaseModId) return;
            if (enabled) _disabled.Remove(id);
            else _disabled.Add(id);
            var m = Mods.Find(x => x.Id == id);
            if (m != null) m.Enabled = enabled;
            SaveConfig();
            foreach (var x in Mods) x.Errors.RemoveAll(e => e.StartsWith("missing dependency", StringComparison.Ordinal) || e == "dependency cycle");
            ResolveOrder();
        }

        /// <summary>Отпечаток набора модов для сверки хоста и клиента: «id@версия» через запятую.</summary>
        public string Fingerprint => string.Join(",", _order.Select(m => m.Id + "@" + m.Manifest.Version));

        // ============================================================ Загрузка

        /// <summary>JSON-контент и локализация всех активных модов в порядке загрузки.</summary>
        public void LoadContent(ContentDatabase content, Localization loc)
        {
            foreach (var m in _order)
            {
                content.LoadModDirectory(m.Folder);
                loc?.LoadModDirectory(m.Folder);
            }
        }

        /// <summary>Код-моды: загрузить DLL и вызвать OnLoad у всех IGenesisMod. Вызывать до content.Freeze().</summary>
        public void LoadCode(ContentDatabase content, Localization loc)
        {
            if (!Genesis.Core.Platform.CodeMods) return; // браузер: загрузка DLL невозможна
            foreach (var m in _order)
            {
                if (!m.HasCode) continue;
                var ctx = new ModContext(m, content, loc, Events);
                foreach (var dll in m.Assemblies)
                {
                    Assembly asm;
                    try
                    {
                        asm = Assembly.LoadFrom(dll);
                    }
                    catch (Exception e)
                    {
                        m.Errors.Add(Path.GetFileName(dll) + ": " + e.Message);
                        continue;
                    }
                    foreach (var type in SafeTypes(asm, m))
                    {
                        if (type == null || type.IsAbstract || type.IsInterface) continue;
                        if (!typeof(IGenesisMod).IsAssignableFrom(type)) continue;
                        if (type.GetConstructor(Type.EmptyTypes) == null) continue;
                        try
                        {
                            var mod = (IGenesisMod)Activator.CreateInstance(type);
                            mod.OnLoad(ctx);
                            m.CodeEntryPoints++;
                        }
                        catch (Exception e)
                        {
                            m.Errors.Add(type.FullName + ".OnLoad: " + (e.InnerException ?? e).Message);
                            Genesis.Core.Log.Error($"[Mod {m.Id}] {type.FullName}.OnLoad failed: {e}");
                        }
                    }
                }
            }
        }

        private static IEnumerable<Type> SafeTypes(Assembly asm, ModInfo m)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                m.Errors.Add(asm.GetName().Name + ": some types failed to load (" + e.LoaderExceptions.FirstOrDefault()?.Message + ")");
                return e.Types.Where(t => t != null);
            }
        }

        /// <summary>Подключить события модов к созданному миру (одиночная игра или хост).</summary>
        public void AttachServer(GameServer server)
        {
            server.ModList = Fingerprint;
            Events.Attach(server);
        }

        // ============================================================ Настройки

        private sealed class Config
        {
            [JsonProperty("disabled")] public List<string> Disabled = new List<string>();
        }

        private void LoadConfig()
        {
            if (_configPath == null || !File.Exists(_configPath)) return;
            try
            {
                var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(_configPath));
                if (cfg?.Disabled != null) foreach (var id in cfg.Disabled) _disabled.Add(id);
            }
            catch (Exception e)
            {
                Genesis.Core.Log.Warn("mods config: " + e.Message);
            }
        }

        private void SaveConfig()
        {
            if (_configPath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(new Config { Disabled = _disabled.OrderBy(x => x).ToList() }, Formatting.Indented));
            }
            catch (Exception e)
            {
                Genesis.Core.Log.Warn("mods config: " + e.Message);
            }
        }
    }

    internal sealed class ModContext : IModContext
    {
        private readonly ModInfo _mod;

        public ModContext(ModInfo mod, ContentDatabase content, Localization loc, ModEvents events)
        {
            _mod = mod;
            Content = content;
            Localization = loc;
            Events = events;
        }

        public ModManifest Manifest => _mod.Manifest;
        public string Folder => _mod.Folder;
        public ContentDatabase Content { get; }
        public Localization Localization { get; }
        public ModEvents Events { get; }

        public void RegisterBehavior(string name, IMachineBehavior behavior) => MachineBehaviors.Register(name, behavior);
        public void Log(string message) => Genesis.Core.Log.Info($"[Mod {_mod.Id}] {message}");
        public void Warn(string message) => Genesis.Core.Log.Warn($"[Mod {_mod.Id}] {message}");
    }
}
