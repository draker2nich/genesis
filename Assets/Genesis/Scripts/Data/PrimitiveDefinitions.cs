using System.Collections.Generic;
using Newtonsoft.Json;

namespace Genesis.Data
{
    // ================================================================ Крафт в двух руках

    /// <summary>
    /// Ручной крафт «в двух руках»: по предмету в правой и левой руке, зажать ПКМ. Порядок рук не важен.
    /// Пример: трава + трава → растительное волокно, палка + канат → обмотанная рукоять (остаётся в левой руке).
    /// </summary>
    public sealed class HandCraftDef
    {
        [JsonProperty("id")] public string Id;
        /// <summary>Первый ингредиент: "genesis:id" или "#тег".</summary>
        [JsonProperty("a")] public string A;
        [JsonProperty("b")] public string B;
        [JsonProperty("a_count")] public int ACount = 1;
        [JsonProperty("b_count")] public int BCount = 1;
        /// <summary>Ингредиент не тратится (инструмент при починке).</summary>
        [JsonProperty("keep_a")] public bool KeepA;
        [JsonProperty("keep_b")] public bool KeepB;
        /// <summary>Результат (null — только починка).</summary>
        [JsonProperty("output")] public ItemAmount Output;
        /// <summary>Результат кладётся в левую руку (если она освободилась), а не в хотбар.</summary>
        [JsonProperty("to_offhand")] public bool ToOffhand;
        /// <summary>Сколько держать ПКМ, секунды.</summary>
        [JsonProperty("seconds")] public float Seconds = 1.5f;
        /// <summary>Действие для подсказки и анимации: twist | braid | knap | wrap | haft | tie | repair | assemble.</summary>
        [JsonProperty("action")] public string Action = "twist";
        /// <summary>Починка: сохраняемый инструмент (keep_a/keep_b) получает полную прочность.</summary>
        [JsonProperty("repair")] public bool Repair;

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public int[] AMatches = new int[0];
        [JsonIgnore] public int[] BMatches = new int[0];
        [JsonIgnore] public int SecondsTicks => System.Math.Max(1, (int)System.Math.Round(Seconds * 20f));
    }

    // ================================================================ Режим создания (R)

    /// <summary>
    /// Обработка блока в режиме создания: навести прицел на блок, R, бить инструментом (или держать ПКМ).
    /// Бревно → 4 доски за 4 удара, доска → забор/полублок/палки (колесо выбора), булыжник → каменная наковальня,
    /// огонь трением (держать ПКМ палкой по доске). На рабочей поверхности (верстак, наковальня) обрабатываются
    /// предметы, лежащие на ней (inputs).
    /// </summary>
    public sealed class ShapingDef
    {
        [JsonProperty("id")] public string Id;
        /// <summary>Ключ названия для колеса выбора (по умолчанию shape.&lt;id&gt;).</summary>
        [JsonProperty("name")] public string NameKey;
        /// <summary>Предмет-иконка в колесе (по умолчанию — первый результат).</summary>
        [JsonProperty("icon")] public string Icon;
        /// <summary>Блоки, которые можно обрабатывать (для станций — блок машины).</summary>
        [JsonProperty("targets")] public List<string> Targets = new List<string>();
        /// <summary>Предметы на рабочей поверхности станции, которые тратятся (порядок не важен).</summary>
        [JsonProperty("inputs")] public List<ItemAmount> Inputs = new List<ItemAmount>();
        /// <summary>Подходящие типы инструментов в правой руке (universal, axe, pickaxe...). Пусто — смотри item.</summary>
        [JsonProperty("tools")] public List<string> Tools = new List<string>();
        /// <summary>Конкретный предмет в правой руке (огонь трением — палка). Не тратится.</summary>
        [JsonProperty("item")] public string Item;
        [JsonProperty("hits")] public int Hits = 4;
        /// <summary>Блок поворачивается на 90° после каждого удара.</summary>
        [JsonProperty("rotate")] public bool Rotate = true;
        /// <summary>strike — удары ЛКМ; hold — держать ПКМ (каждые interval секунд засчитывается «удар»).</summary>
        [JsonProperty("mode")] public string Mode = "strike";
        /// <summary>Минимум секунд между ударами.</summary>
        [JsonProperty("interval")] public float Interval = 0.35f;
        [JsonProperty("outputs")] public List<ItemAmount> Outputs = new List<ItemAmount>();
        /// <summary>Блок, который встаёт на место обработанного (каменная наковальня), иначе — воздух.</summary>
        [JsonProperty("result_block")] public string ResultBlock;
        /// <summary>Блок расходуется (бревно раскалывается). false — остаётся (доска для огня трением).</summary>
        [JsonProperty("consume_block")] public bool ConsumeBlock = true;
        /// <summary>Износ инструмента за удар.</summary>
        [JsonProperty("wear")] public int Wear = 1;

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public int ItemId = -1;
        [JsonIgnore] public int IconItem = -1;
        [JsonIgnore] public ushort[] TargetBlocks = new ushort[0];
        [JsonIgnore] public bool IsHold => Mode == "hold";
        [JsonIgnore] public bool IsStation => Inputs.Count > 0;
        [JsonIgnore] public int IntervalTicks => System.Math.Max(1, (int)System.Math.Floor(Interval * 20f * 0.8f));
    }

    // ================================================================ Конструкции

    /// <summary>
    /// Конструкция из блоков, которая в режиме создания (R + удар инструментом) превращается в машину или блок:
    /// два забора с полублоками → верстак, булыжники «шахматкой» 3×3×2 → печка, два снопа → лежанка.
    /// Шаблон задаётся слоями снизу вверх: layers[y][z] — строка по x. Символ — ключ из key, пробел — неважно что.
    /// В key "*" — любой твёрдый блок.
    /// </summary>
    public sealed class StructureDef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string NameKey;
        [JsonProperty("layers")] public List<List<string>> Layers = new List<List<string>>();
        [JsonProperty("key")] public Dictionary<string, string> Key = new Dictionary<string, string>();
        /// <summary>Результат: id машины или блока.</summary>
        [JsonProperty("result")] public string Result;
        /// <summary>Где встаёт результат (координаты шаблона x, y, z).</summary>
        [JsonProperty("result_at")] public int[] ResultAt = { 0, 0, 0 };
        /// <summary>Куда смотрит результат в исходной ориентации шаблона: north (+z) | south | east | west.</summary>
        [JsonProperty("result_facing")] public string ResultFacing = "north";
        /// <summary>Подходящие типы инструментов для удара (пусто — любой инструмент).</summary>
        [JsonProperty("tools")] public List<string> Tools = new List<string>();

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public int SizeX, SizeY, SizeZ;
        /// <summary>Клетки шаблона [x, y, z] → блок (0xFFFF — любой твёрдый, 0xFFFE — неважно).</summary>
        [JsonIgnore] public ushort[] Cells;
        [JsonIgnore] public ushort ResultBlock;
        [JsonIgnore] public Genesis.Core.Direction Facing = Genesis.Core.Direction.North;

        public const ushort Any = 0xFFFF;
        public const ushort Ignore = 0xFFFE;

        public int CellIndex(int x, int y, int z) => (y * SizeZ + z) * SizeX + x;
    }

    // ================================================================ Предмет по блоку

    /// <summary>
    /// ПКМ предметом по блоку: уголёк по хворосту → костёр, глина по печке (4 раза) → обмазанная печь,
    /// мотыга по земле → грядка.
    /// </summary>
    public sealed class InteractionDef
    {
        [JsonProperty("id")] public string Id;
        /// <summary>Предмет в руке: "genesis:id" или "#тег".</summary>
        [JsonProperty("held")] public string Held;
        [JsonProperty("targets")] public List<string> Targets = new List<string>();
        /// <summary>Сколько раз нужно применить (обмазка печи — 4).</summary>
        [JsonProperty("uses")] public int Uses = 1;
        /// <summary>Сколько предметов тратится за применение (инструменты не тратятся, а изнашиваются — wear).</summary>
        [JsonProperty("consume")] public int Consume = 1;
        [JsonProperty("wear")] public int Wear;
        /// <summary>Во что превращается блок после последнего применения.</summary>
        [JsonProperty("result_block")] public string ResultBlock;
        /// <summary>Что получает игрок.</summary>
        [JsonProperty("give")] public ItemAmount Give;

        [JsonIgnore] public int NumericId;
        [JsonIgnore] public int[] HeldMatches = new int[0];
        [JsonIgnore] public ushort[] TargetBlocks = new ushort[0];
        [JsonIgnore] public ushort ResultBlockId;
    }
}
