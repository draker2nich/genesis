using System;
using System.Collections.Generic;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Newtonsoft.Json;

namespace Genesis.Modding
{
    /// <summary>Описание мода (mod.json в корне папки мода).</summary>
    public sealed class ModManifest
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("version")] public string Version = "1.0.0";
        [JsonProperty("author")] public string Author;
        [JsonProperty("description")] public string Description;
        /// <summary>Моды, без которых этот не работает (грузятся раньше).</summary>
        [JsonProperty("dependencies")] public List<string> Dependencies = new List<string>();
        /// <summary>Необязательные моды, после которых грузиться, если они есть.</summary>
        [JsonProperty("load_after")] public List<string> LoadAfter = new List<string>();
        /// <summary>ID предмета Steam Workshop (заполняется при публикации).</summary>
        [JsonProperty("workshop_id")] public ulong WorkshopId;
    }

    /// <summary>
    /// Точка входа код-мода. Класс с этим интерфейсом и конструктором без параметров
    /// ищется во всех DLL папки мода (корень и assemblies/).
    /// </summary>
    public interface IGenesisMod
    {
        /// <summary>
        /// Вызывается после загрузки JSON-контента всех модов, до заморозки базы:
        /// можно добавлять контент кодом и регистрировать логику машин.
        /// </summary>
        void OnLoad(IModContext ctx);
    }

    /// <summary>API, доступный код-моду.</summary>
    public interface IModContext
    {
        ModManifest Manifest { get; }
        /// <summary>Папка мода на диске.</summary>
        string Folder { get; }
        /// <summary>База контента (ещё не заморожена в OnLoad): AddItemJson, AddMachineJson и т.п.</summary>
        ContentDatabase Content { get; }
        Localization Localization { get; }
        /// <summary>Своя логика машин: поле "behavior" в JSON машины.</summary>
        void RegisterBehavior(string name, IMachineBehavior behavior);
        ModEvents Events { get; }
        void Log(string message);
        void Warn(string message);
    }

    /// <summary>
    /// События игры для модов. Подписки переживают перезапуск мира:
    /// хост переподключает их к каждому новому GameServer.
    /// </summary>
    public sealed class ModEvents
    {
        /// <summary>Мир создан/загружен (одиночная игра или хост). На клиенте в коопе не вызывается.</summary>
        public event Action<GameServer> ServerStarted;
        public event Action<GameServer> Tick;
        public event Action<GameServer, PlayerState> PlayerJoined;
        public event Action<GameServer, PlayerState, Genesis.Core.Int3, BlockDef> BlockPlaced;
        public event Action<GameServer, PlayerState, Genesis.Core.Int3, BlockDef> BlockBroken;
        public event Action<GameServer, PlayerState, CraftDef> Crafted;

        internal void Attach(GameServer s)
        {
            Safe(() => ServerStarted?.Invoke(s));
            s.Ticked += server => Tick?.Invoke(server);
            s.PlayerJoined += p => Safe(() => PlayerJoined?.Invoke(s, p));
            s.BlockPlaced += (p, pos, b) => Safe(() => BlockPlaced?.Invoke(s, p, pos, b));
            s.BlockBroken += (p, pos, b) => Safe(() => BlockBroken?.Invoke(s, p, pos, b));
            s.Crafted += (p, c) => Safe(() => Crafted?.Invoke(s, p, c));
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception e) { Genesis.Core.Log.Error("Mod event handler failed: " + e); }
        }
    }
}
