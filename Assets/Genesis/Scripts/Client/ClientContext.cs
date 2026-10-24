using Genesis.Client.Rendering;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Net;
using UnityEngine;

namespace Genesis.Client
{
    public enum UiWindow
    {
        None,
        Inventory,
        Creative,
        Machine,
        /// <summary>Справочник (Q): квесты эпохи с пояснениями механик.</summary>
        Handbook,
        Stats,
        Me,
        Map
    }

    /// <summary>Вариант в колесе режима создания: обработка блока или сборка конструкции.</summary>
    public sealed class CreationOption
    {
        public ShapingDef Shaping;
        public StructureDef Structure;
        public string NameKey => Shaping != null ? Shaping.NameKey : Structure.NameKey;
        /// <summary>Предмет-иконка.</summary>
        public int Icon;
    }

    /// <summary>Режим создания (R): что обрабатываем, сколько ударов, колесо выбора.</summary>
    public sealed class CreationState
    {
        public bool Active;
        public CreationOption Option;
        /// <summary>Блок (для станции — её начало), который обрабатываем.</summary>
        public Int3 Pos;
        public ushort Block;
        public int Hits;
        public float HoldTimer;
        /// <summary>Визуальный поворот блока (градусы) — догоняет TargetAngle.</summary>
        public float Angle, TargetAngle;
        public float FinishedAt = -1f;

        public bool WheelOpen;
        public readonly System.Collections.Generic.List<CreationOption> Options = new System.Collections.Generic.List<CreationOption>();
        public Vector2 WheelCursor;
        public int WheelHover = -1;

        public int TotalHits => Option?.Shaping != null ? Option.Shaping.Hits : 1;
    }

    /// <summary>Общее состояние клиента: ссылки на сервер, контент, локального игрока и состояние UI.</summary>
    public sealed class ClientContext
    {
        public IGameSession Session;
        public ContentDatabase Content;
        public Localization Loc;
        public BlockTextureFactory Textures;

        /// <summary>Серверное состояние локального игрока (инвентарь). В коопе станет реплицированной копией.</summary>
        public PlayerState Player;

        public UiWindow Window = UiWindow.None;
        /// <summary>Меню паузы открыто (Esc или окно игры потеряло фокус).</summary>
        public bool Paused;
        /// <summary>Нужен ли захват мыши: нет окон, мини-игры и паузы.</summary>
        public bool WantsCursorLock => Window == UiWindow.None && Minigame == null && !Paused;
        public Int3 OpenMachine;
        public bool ShowDebug = true;

        // Цель взгляда
        public bool HasTarget;
        public Int3 Target;
        public float BreakProgress;

        // Тосты
        public string Toast;
        public float ToastUntil;

        // Активная мини-игра (модальная)
        public Genesis.Client.UI.Minigame Minigame;

        /// <summary>Карта исследованного мира.</summary>
        public Genesis.Client.Map.WorldMap Map;
        public CraftDef MinigameCraft;
        public Int3 MinigameStation;
        /// <summary>Мини-игра запущена с результата сетки крафта (а не со станции).</summary>
        public bool MinigameGrid;
        public float MinigameCloseAt;

        // Цель-враг под прицелом
        public int TargetEnemy = -1;
        /// <summary>Положенный на пол предмет под прицелом (−1 — нет): ПКМ подбирает.</summary>
        public int TargetDrop = -1;

        // ---- Первобытная эпоха
        public readonly CreationState Creation = new CreationState();
        /// <summary>Крафт, доступный в руках прямо сейчас (null — нет), и прогресс удержания ПКМ 0..1.</summary>
        public HandCraftDef HandCraft;
        public float HandCraftProgress;
        /// <summary>Подсказка под прицелом: что сделает ПКМ / R.</summary>
        public string Hint;

        public void Send(GameCommand cmd) => Session.Send(cmd);

        public void ShowToast(string text, float seconds = 2.5f)
        {
            Toast = text;
            ToastUntil = Time.time + seconds;
        }

        /// <summary>Тост от сервера: ключи локализации через «|» склеиваются («Открыто: Плавка железа»).</summary>
        public void ShowServerToast(string keys)
        {
            var parts = keys.Split('|');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(": ");
                sb.Append(Loc.Get(parts[i]));
            }
            ShowToast(sb.ToString(), parts.Length > 1 ? 3.5f : 2.5f);
        }

        public bool InMinigame => Minigame != null;

        public Texture2D IconOf(int item)
        {
            var def = Content.Item(item);
            if (def == null || def.TextureLayer < 0 || def.TextureLayer >= Textures.Icons.Length) return null;
            return Textures.Icons[def.TextureLayer];
        }

        public string ItemName(int item)
        {
            var def = Content.Item(item);
            return def != null ? Loc.Get(def.NameKey) : "?";
        }
    }
}
