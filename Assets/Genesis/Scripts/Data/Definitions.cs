using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Genesis.Data
{
    public enum RenderLayer
    {
        Invisible = 0,
        Opaque = 1,
        Cutout = 2,
        Translucent = 3
    }

    /// <summary>Тип стороны машины (Mekanism-style side config).</summary>
    public enum SideType : byte
    {
        None = 0,
        ItemIn = 1,
        ItemOut = 2,
        FluidIn = 3,
        FluidOut = 4,
        SteamIn = 5,
        SteamOut = 6,
        EnergyIn = 7
    }

    public static class SideTypes
    {
        public static bool TryParse(string s, out SideType t)
        {
            switch (s)
            {
                case "none": t = SideType.None; return true;
                case "item_in": t = SideType.ItemIn; return true;
                case "item_out": t = SideType.ItemOut; return true;
                case "fluid_in": t = SideType.FluidIn; return true;
                case "fluid_out": t = SideType.FluidOut; return true;
                case "steam_in": t = SideType.SteamIn; return true;
                case "steam_out": t = SideType.SteamOut; return true;
                case "energy_in": t = SideType.EnergyIn; return true;
                default: t = SideType.None; return false;
            }
        }

        public static string ToKey(SideType t)
        {
            switch (t)
            {
                case SideType.ItemIn: return "item_in";
                case SideType.ItemOut: return "item_out";
                case SideType.FluidIn: return "fluid_in";
                case SideType.FluidOut: return "fluid_out";
                case SideType.SteamIn: return "steam_in";
                case SideType.SteamOut: return "steam_out";
                case SideType.EnergyIn: return "energy_in";
                default: return "none";
            }
        }

        public static bool IsFluid(this SideType t) =>
            t == SideType.FluidIn || t == SideType.FluidOut || t == SideType.SteamIn || t == SideType.SteamOut;

        public static bool IsInput(this SideType t) =>
            t == SideType.ItemIn || t == SideType.FluidIn || t == SideType.SteamIn || t == SideType.EnergyIn;
    }

    // ---------------------------------------------------------------- Blocks

    public sealed class BlockTextures
    {
        [JsonProperty("all")] public string All;
        [JsonProperty("top")] public string Top;
        [JsonProperty("bottom")] public string Bottom;
        [JsonProperty("side")] public string Side;
        [JsonProperty("front")] public string Front;
    }

    public sealed class TransportSpec
    {
        /// <summary>"conveyor" | "pipe"</summary>
        [JsonProperty("type")] public string Type;
        /// <summary>Конвейер: блоков в секунду. Труба: ед. жидкости в тик на сегмент.</summary>
        [JsonProperty("speed")] public float Speed = 1f;
        [JsonProperty("capacity")] public int Capacity = 100;
        [JsonProperty("tier")] public int Tier = 1;
    }

    public sealed class BlockDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("render")] public RenderLayer Render = RenderLayer.Opaque;
        [JsonProperty("solid")] public bool Solid = true;
        [JsonProperty("hardness")] public float Hardness = 1f;
        /// <summary>-1 = неразрушаемый (бедрок).</summary>
        [JsonProperty("unbreakable")] public bool Unbreakable;
        [JsonProperty("tool")] public string Tool;
        [JsonProperty("replaceable")] public bool Replaceable;
        [JsonProperty("liquid")] public bool Liquid;
        [JsonProperty("textures")] public BlockTextures Textures;
        [JsonProperty("drop")] public string Drop;
        [JsonProperty("drop_count")] public int DropCount = 1;
        [JsonProperty("orientable")] public bool Orientable;
        [JsonProperty("transport")] public TransportSpec Transport;
        /// <summary>Чистота залежи: 0 — не руда, 1 бедная, 2 обычная, 3 богатая.</summary>
        [JsonProperty("purity")] public int Purity;
        [JsonProperty("deposit_of")] public string DepositOf;
        /// <summary>Минимальный уровень инструмента (Tool) для добычи с дропом. 0 — можно руками.</summary>
        [JsonProperty("harvest_tier")] public int HarvestTier;
        /// <summary>Свечение 0–15 (факел 14, лампа 15).</summary>
        [JsonProperty("light")] public int Light;
        /// <summary>Форма: cube | cross (растения) | torch.</summary>
        [JsonProperty("shape")] public string Shape = "cube";
        /// <summary>Модель Blockbench (models/&lt;имя&gt;.bbmodel): блок рисуется ею вместо куба, физика — полный блок.</summary>
        [JsonProperty("model")] public string Model;
        /// <summary>Окраска по биому: "grass" (верх полностью, бока наполовину) или "foliage" (листва, трава).</summary>
        [JsonProperty("tint")] public string Tint;

        // ---- заполняется при загрузке
        [JsonIgnore] public ushort NumericId;
        [JsonIgnore] public string MachineId;
        /// <summary>Индексы слоёв текстур по граням (порядок Direction) + [6] = front.</summary>
        [JsonIgnore] public int[] FaceTexture = new int[7];
        [JsonIgnore] public bool Opaque => Render == RenderLayer.Opaque;
    }

    // ---------------------------------------------------------------- Items

    public sealed class ItemDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("max_stack")] public int MaxStack = Genesis.Core.GameConstants.MaxStackSize;
        [JsonProperty("places")] public string PlacesBlock;
        [JsonProperty("texture")] public string Texture;
        /// <summary>Модель Blockbench для предмета в руке (иначе — модель ставимого блока или объёмный спрайт).</summary>
        [JsonProperty("model")] public string Model;
        /// <summary>Свет в руке 0–15. Не задан — берётся свечение блока, который предмет ставит (факел светит в руке).</summary>
        [JsonProperty("light")] public int Light;
        /// <summary>Теплотворность: сколько тиков горит в горне/котле.</summary>
        [JsonProperty("fuel_ticks")] public int FuelTicks;
        [JsonProperty("tags")] public List<string> Tags = new List<string>();
        [JsonProperty("tool")] public ToolSpec Tool;
        /// <summary>Сколько единиц голода восстанавливает (0 — несъедобно).</summary>
        [JsonProperty("food")] public int Food;
        /// <summary>Ячейка хранения ME: вид (item | fluid) и ёмкость (предметы или mB).</summary>
        [JsonProperty("storage_cell")] public StorageCellSpec StorageCell;
        /// <summary>Беспроводной терминал ME (бесконечная дальность, привязка к контроллеру).</summary>
        [JsonProperty("me_wireless")] public bool MeWireless;

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public int TextureLayer = -1;
        [JsonIgnore] public bool IsFuel => FuelTicks > 0;
        [JsonIgnore] public bool IsFood => Food > 0;
    }

    public sealed class StorageCellSpec
    {
        [JsonProperty("kind")] public string Kind = "item";
        [JsonProperty("capacity")] public long Capacity = 1024;
        public bool IsFluid => Kind == "fluid";
    }

    public sealed class ToolSpec
    {
        /// <summary>pickaxe | axe | shovel | sword</summary>
        [JsonProperty("type")] public string Type;
        [JsonProperty("tier")] public int Tier = 1;
        /// <summary>Множитель скорости добычи подходящих блоков.</summary>
        [JsonProperty("speed")] public float Speed = 2f;
        [JsonProperty("durability")] public int Durability = 60;
        [JsonProperty("damage")] public float Damage = 1f;
    }

    // ---------------------------------------------------------------- Fluids

    public sealed class FluidDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("color")] public string Color = "#3366ff";
        [JsonProperty("gas")] public bool Gas;
        /// <summary>Блок жидкости в мире (вода) — источник для насоса.</summary>
        [JsonProperty("source_block")] public string SourceBlock;

        [JsonIgnore] public int NumericId;
    }

    // ---------------------------------------------------------------- Machines

    public sealed class EnergySpec
    {
        /// <summary>"none" | "fuel" | "steam" | "fe"</summary>
        [JsonProperty("type")] public string Type = "none";
        [JsonProperty("per_tick")] public int PerTick;
        /// <summary>Внутренний буфер FE (машины, генераторы, ячейки). 0 — по умолчанию PerTick×40.</summary>
        [JsonProperty("buffer")] public int Buffer;
        /// <summary>Макс. приём/отдача FE за тик для ячеек и генераторов (0 — без ограничения).</summary>
        [JsonProperty("rate")] public int Rate;

        public bool IsElectric => Type == "fe";
    }

    public sealed class TankSpec
    {
        [JsonProperty("name")] public string Name;
        /// <summary>Фильтр жидкости (id) или null — любая.</summary>
        [JsonProperty("fluid")] public string Fluid;
        [JsonProperty("capacity")] public int Capacity = 1000;
        /// <summary>"in" | "out"</summary>
        [JsonProperty("io")] public string Io = "in";
    }

    public sealed class MachineDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("era")] public string Era;
        /// <summary>"single_block" | "multiblock". Зарезервировано: мультиблоков пока нет.</summary>
        [JsonProperty("kind")] public string Kind = "single_block";
        /// <summary>Сетка крафта станции: 3 — верстак (3×3). 0 — нет.</summary>
        [JsonProperty("craft_grid")] public int CraftGrid;
        /// <summary>Логика машины: processor | furnace | boiler | pump | storage (расширяется модами).</summary>
        [JsonProperty("behavior")] public string Behavior = "processor";
        /// <summary>Зарезервировано: тиры машин пока не влияют на работу.</summary>
        [JsonProperty("tier")] public int Tier = 1;
        [JsonProperty("energy")] public EnergySpec Energy = new EnergySpec();
        [JsonProperty("sides")] public List<string> Sides = new List<string>();
        /// <summary>Конфигурация сторон по умолчанию: {"back":"item_in","front":"item_out"}.</summary>
        [JsonProperty("default_faces")] public Dictionary<string, string> DefaultFaces = new Dictionary<string, string>();
        /// <summary>Зарезервировано: модулей пока нет.</summary>
        [JsonProperty("module_slots")] public int ModuleSlots;
        [JsonProperty("input_slots")] public int InputSlots = 1;
        [JsonProperty("output_slots")] public int OutputSlots = 1;
        [JsonProperty("fuel_slots")] public int FuelSlots;
        [JsonProperty("tanks")] public List<TankSpec> Tanks = new List<TankSpec>();
        [JsonProperty("speed")] public float Speed = 1f;
        [JsonProperty("params")] public Dictionary<string, double> Params = new Dictionary<string, double>();
        [JsonProperty("textures")] public BlockTextures Textures;
        [JsonProperty("hardness")] public float Hardness = 2f;
        /// <summary>Анимация в мире (стиль Create): spin | piston | drill | arm | none.</summary>
        [JsonProperty("anim")] public string Anim = "spin";
        /// <summary>Модель Blockbench для блока машины (анимации «idle», «working»).</summary>
        [JsonProperty("model")] public string Model;
        /// <summary>Ориентация при установке: toward — лицом к игроку, away — туда, куда смотрит игрок.</summary>
        [JsonProperty("orient")] public string Orient = "toward";
        /// <summary>Что выпадает при поломке блока машины (по умолчанию — сама машина; "none" — ничего).</summary>
        [JsonProperty("drop")] public string Drop;
        /// <summary>Использовать рецепты другой машины (электрогорн = рецепты каменного горна).</summary>
        [JsonProperty("recipes_of")] public string RecipesOf;
        /// <summary>Роль в энергосети: consumer | generator | storage (по умолчанию из energy.type).</summary>
        [JsonProperty("power")] public string Power;
        /// <summary>
        /// Машина выполняет ручные крафты станции автоматически (автосборщик = крафты верстака без мини-игр).
        /// Рецепты генерируются при загрузке.
        /// </summary>
        [JsonProperty("crafts_of")] public string CraftsOf;
        /// <summary>Устройство ME-сети (контроллер, привод, терминал, шины, провайдер).</summary>
        [JsonIgnore] public bool IsMeDevice => Behavior != null && Behavior.StartsWith("me_", StringComparison.Ordinal);

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public SideType[] DefaultSideConfig = new SideType[6];

        [JsonIgnore] public int EnergyBuffer => Energy.Buffer > 0 ? Energy.Buffer : Math.Max(Energy.PerTick * 40, 1);
        /// <summary>Участвует ли машина в электросети.</summary>
        [JsonIgnore] public bool IsPowerNode => Power != null || Energy.IsElectric;
        [JsonIgnore] public string PowerRole => Power ?? (Energy.IsElectric ? "consumer" : null);

        public double Param(string key, double fallback) =>
            Params != null && Params.TryGetValue(key, out double v) ? v : fallback;
    }

    // ---------------------------------------------------------------- Recipes

    public sealed class ItemAmount
    {
        [JsonProperty("item")] public string Item;
        [JsonProperty("count")] public int Count = 1;
        [JsonProperty("chance")] public float Chance = 1f;

        [JsonIgnore] public int ItemId = -1;
    }

    public sealed class FluidAmount
    {
        [JsonProperty("fluid")] public string Fluid;
        [JsonProperty("amount")] public int Amount;

        [JsonIgnore] public int FluidId = -1;
    }

    public sealed class UnlockSpec
    {
        [JsonProperty("minigame")] public string Minigame;
        [JsonProperty("difficulty")] public int Difficulty = 1;
    }

    public sealed class RecipeDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("machine")] public string Machine;
        [JsonProperty("inputs")] public List<ItemAmount> Inputs = new List<ItemAmount>();
        [JsonProperty("fluid_inputs")] public List<FluidAmount> FluidInputs = new List<FluidAmount>();
        [JsonProperty("outputs")] public List<ItemAmount> Outputs = new List<ItemAmount>();
        [JsonProperty("fluid_outputs")] public List<FluidAmount> FluidOutputs = new List<FluidAmount>();
        [JsonProperty("byproducts")] public List<ItemAmount> Byproducts = new List<ItemAmount>();
        [JsonProperty("time_ticks")] public int TimeTicks = 20;
        /// <summary>Зарезервировано, сейчас не используется: машинные рецепты открываются деревом знаний (unlocks.recipes).</summary>
        [JsonProperty("unlock")] public UnlockSpec Unlock;

        [JsonIgnore] public int NumericId;
        /// <summary>Рецепт сгенерирован из ручного крафта (crafts_of) — открывается вместе с ним.</summary>
        [JsonIgnore] public string SourceCraft;
    }

    // ---------------------------------------------------------------- Crafts (ручной крафт на станциях)

    public sealed class MinigameSpec
    {
        /// <summary>forging | smelting | assembly | soldering</summary>
        [JsonProperty("template")] public string Template;
        [JsonProperty("difficulty")] public int Difficulty = 1;
    }

    /// <summary>
    /// Ручная операция: на станции (верстак, наковальня, горн) или «в руках».
    /// Если задана мини-игра — игрок выполняет её каждый раз; автоматизация — машинные рецепты.
    /// </summary>
    public sealed class CraftDef
    {
        [JsonProperty("id")] public string Id;
        /// <summary>"hand" или id машины-станции.</summary>
        [JsonProperty("station")] public string Station = "hand";
        [JsonProperty("inputs")] public List<ItemAmount> Inputs = new List<ItemAmount>();
        [JsonProperty("outputs")] public List<ItemAmount> Outputs = new List<ItemAmount>();
        [JsonProperty("minigame")] public MinigameSpec Minigame;

        /// <summary>
        /// Сетка крафта (как в Minecraft): строки шаблона, символ → ингредиент из key. Пробел — пустая клетка.
        /// Шаблон до 2×2 крафтится и в инвентаре, и в верстаке; 3×3 — только в верстаке.
        /// </summary>
        [JsonProperty("pattern")] public List<string> Pattern;
        /// <summary>
        /// Ингредиенты шаблона: "genesis:planks", "#logs" (любой предмет с тегом), "genesis:iron_plate*2" (2 штуки в клетке),
        /// "~#axes" (инструмент-катализатор: не тратится, а изнашивается).
        /// </summary>
        [JsonProperty("key")] public Dictionary<string, string> Key;
        /// <summary>Бесформенный рецепт: ингредиенты inputs в любых клетках (count — сколько клеток).</summary>
        [JsonProperty("shapeless")] public bool Shapeless;

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public bool IsHand => Station == "hand";
        /// <summary>Рецепт сетки (руки/верстак), а не операция на станции.</summary>
        [JsonIgnore] public bool IsGrid;
        [JsonIgnore] public int GridW, GridH;
        /// <summary>Клетки шаблона (GridW×GridH, null — пусто) или список для бесформенного.</summary>
        [JsonIgnore] public GridIngredient[] Cells;
        /// <summary>Нужная сетка: 2 (инвентарь) или 3 (верстак).</summary>
        [JsonIgnore] public int GridSize => Shapeless ? (Cells != null && Cells.Length > 4 ? 3 : 2) : (Math.Max(GridW, GridH) <= 2 ? 2 : 3);
    }

    /// <summary>Ингредиент клетки сетки крафта.</summary>
    public sealed class GridIngredient
    {
        public string Spec;
        public int ItemId = -1;  // конкретный предмет
        public string Tag;       // или любой с тегом
        public int Count = 1;    // штук в клетке
        public bool Keep;        // катализатор: не расходуется (инструмент изнашивается)
        /// <summary>Все подходящие предметы (для показа в справочнике).</summary>
        public int[] Matches = new int[0];

        public bool Accepts(int item)
        {
            if (item < 0) return false;
            if (ItemId >= 0) return item == ItemId;
            return Array.IndexOf(Matches, item) >= 0;
        }
    }

    // ---------------------------------------------------------------- Knowledge tree

    public sealed class KnowledgeTrigger
    {
        /// <summary>start | craft | place | obtain</summary>
        [JsonProperty("type")] public string Type = "start";
        /// <summary>id крафта / блока / предмета.</summary>
        [JsonProperty("target")] public string Target;
    }

    public sealed class KnowledgeUnlocks
    {
        [JsonProperty("crafts")] public List<string> Crafts = new List<string>();
        [JsonProperty("recipes")] public List<string> Recipes = new List<string>();
    }

    /// <summary>
    /// Узел дерева знаний. Открывается действием игрока (trigger), открывает крафты и машинные рецепты.
    /// Узел-рубеж (milestone) переводит мир в следующую эпоху.
    /// </summary>
    public sealed class KnowledgeNode
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("era")] public string Era = "wood";
        [JsonProperty("requires")] public List<string> Requires = new List<string>();
        [JsonProperty("trigger")] public KnowledgeTrigger Trigger = new KnowledgeTrigger();
        [JsonProperty("unlocks")] public KnowledgeUnlocks Unlocks = new KnowledgeUnlocks();
        [JsonProperty("milestone")] public bool Milestone;
        /// <summary>Предмет-иконка узла.</summary>
        [JsonProperty("icon")] public string Icon;
        /// <summary>Колонка/строка в графе (для отображения).</summary>
        [JsonProperty("row")] public int Row;

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public int EraIndex;
    }

    public static class Eras
    {
        public static readonly string[] All = { "wood", "stone", "iron", "steam", "electric", "space", "quantum" };

        public static int IndexOf(string era)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == era) return i;
            return -1;
        }
    }

    // ---------------------------------------------------------------- Enemies

    public sealed class EnemyDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("health")] public float Health = 10;
        [JsonProperty("damage")] public float Damage = 2;
        [JsonProperty("speed")] public float Speed = 3f;
        [JsonProperty("aggro")] public float Aggro = 12f;
        /// <summary>cave | ruin</summary>
        [JsonProperty("spawn")] public string Spawn = "cave";
        [JsonProperty("width")] public float Width = 0.7f;
        [JsonProperty("height")] public float Height = 1.4f;
        [JsonProperty("color")] public string Color = "#8a3a3a";
        /// <summary>Модель Blockbench (анимации «idle», «walk», «attack», «windup», «special»).</summary>
        [JsonProperty("model")] public string Model;

        // ---- Существа: поведение и появление
        /// <summary>hostile — нападает; passive — мирное (убегает, если ранить); neutral — нападает, только если ранить.</summary>
        [JsonProperty("kind")] public string Kind = "hostile";
        /// <summary>Биомы для spawn = "surface" (пусто — любые суши).</summary>
        [JsonProperty("biomes")] public List<string> Biomes = new List<string>();
        /// <summary>any | day | night</summary>
        [JsonProperty("time")] public string Time = "any";
        /// <summary>Размер группы при появлении [мин, макс].</summary>
        [JsonProperty("group")] public int[] Group = { 1, 1 };
        /// <summary>Вес при выборе из подходящих (чаще/реже).</summary>
        [JsonProperty("weight")] public float Weight = 1f;
        /// <summary>melee | leap | ranged | charge | spores</summary>
        [JsonProperty("attack")] public string Attack = "melee";
        /// <summary>Дальность особой атаки (прыжок, плевок, рывок).</summary>
        [JsonProperty("range")] public float Range = 6f;
        /// <summary>Перезарядка особой атаки, тики.</summary>
        [JsonProperty("cooldown")] public int Cooldown = 60;
        /// <summary>Подготовка («замах») перед особой атакой, тики: игрок видит и успевает увернуться.</summary>
        [JsonProperty("windup")] public int Windup = 0;
        /// <summary>Яд при попадании, тики (1 урон каждые 1,5 с).</summary>
        [JsonProperty("poison")] public int Poison;
        /// <summary>Доля здоровья, при которой существо убегает (0 — никогда).</summary>
        [JsonProperty("flee_health")] public float FleeHealth;
        /// <summary>Броня: доля поглощаемого урона, если бьют не тем инструментом (weak_to).</summary>
        [JsonProperty("armor")] public float Armor;
        [JsonProperty("weak_to")] public string WeakTo;
        /// <summary>Зовёт сородичей в радиусе 12, когда его ранят.</summary>
        [JsonProperty("pack")] public bool Pack;
        /// <summary>При гибели — облако спор (яд вокруг).</summary>
        [JsonProperty("death_cloud")] public bool DeathCloud;
        /// <summary>Прячется в панцирь при ударе: урон ×0.2 на 3 с.</summary>
        [JsonProperty("curl")] public bool Curl;
        /// <summary>Любимая еда: идёт за игроком с ней в руке; накормить двоих — появится третий.</summary>
        [JsonProperty("food")] public string Food;
        /// <summary>Несёт предмет раз в N тиков (яйца).</summary>
        [JsonProperty("lays")] public ItemAmount Lays;
        [JsonProperty("lays_every")] public int LaysEvery = 6000;

        [JsonIgnore] public int FoodItem = -1;
        [JsonIgnore] public bool IsPassive => Kind == "passive";
        [JsonIgnore] public bool IsHostile => Kind == "hostile";
        [JsonProperty("drops")] public List<ItemAmount> Drops = new List<ItemAmount>();

        [JsonIgnore] public int NumericId;
    }

    // ---------------------------------------------------------------- Textures

    /// <summary>
    /// Процедурное описание текстуры, если PNG нет. Позволяет играть без арта на ранних этапах.
    /// </summary>
    public sealed class TextureSpec
    {
        /// <summary>noise | ore | planks | log_side | log_top | leaves | brick | machine | machine_front | belt | pipe | grass_top | grass_side | water | glass | solid</summary>
        [JsonProperty("pattern")] public string Pattern = "noise";
        [JsonProperty("color")] public string Color = "#808080";
        [JsonProperty("accent")] public string Accent = "#404040";
    }
}
