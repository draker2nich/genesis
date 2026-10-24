using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Genesis.Data
{
    /// <summary>
    /// Весь игровой контент: блоки, предметы, жидкости, машины, рецепты.
    /// Заполняется из JSON-модов (базовый контент — мод "genesis").
    /// После Freeze() — только чтение, потокобезопасно.
    /// </summary>
    public sealed class ContentDatabase
    {
        public const string AirId = "genesis:air";

        public readonly Registry<BlockDef> Blocks = new Registry<BlockDef>();
        public readonly Registry<ItemDef> Items = new Registry<ItemDef>();
        public readonly Registry<FluidDef> Fluids = new Registry<FluidDef>();
        public readonly Registry<MachineDef> Machines = new Registry<MachineDef>();
        public readonly Registry<RecipeDef> Recipes = new Registry<RecipeDef>();
        public readonly Registry<CraftDef> Crafts = new Registry<CraftDef>();
        public readonly Registry<KnowledgeNode> Knowledge = new Registry<KnowledgeNode>();
        public readonly Registry<EnemyDef> Enemies = new Registry<EnemyDef>();
        public readonly Dictionary<string, TextureSpec> TextureSpecs = new Dictionary<string, TextureSpec>();

        /// <summary>Список имён текстур, индекс = слой в Texture2DArray.</summary>
        public readonly List<string> TextureNames = new List<string>();
        private readonly Dictionary<string, int> _textureIndex = new Dictionary<string, int>();

        private readonly Dictionary<int, List<RecipeDef>> _recipesByMachine = new Dictionary<int, List<RecipeDef>>();
        private static readonly List<RecipeDef> NoRecipes = new List<RecipeDef>();

        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Frozen { get; private set; }

        /// <summary>
        /// Отпечаток набора контента (ID и их порядок). Хост и клиент обязаны совпадать —
        /// по сети передаются числовые ID.
        /// </summary>
        public ulong ContentHash { get; private set; }

        public BlockDef Air { get; private set; }
        public ushort AirNumeric => 0;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public ContentDatabase()
        {
            Air = new BlockDef
            {
                Id = AirId,
                NameKey = "block.genesis.air",
                Render = RenderLayer.Invisible,
                Solid = false,
                Replaceable = true,
                Hardness = 0
            };
            Air.NumericId = (ushort)Blocks.Register(AirId, Air);
        }

        // ------------------------------------------------------------ Загрузка

        /// <summary>Загрузить мод из папки: blocks/, items/, fluids/, machines/, recipes/, textures.json.</summary>
        public void LoadModDirectory(string root)
        {
            if (!Directory.Exists(root))
            {
                Errors.Add($"Mod directory not found: {root}");
                return;
            }
            foreach (var f in Files(root, "fluids")) AddFluid(Parse<FluidDef>(f), f);
            foreach (var f in Files(root, "blocks")) AddBlock(Parse<BlockDef>(f), f);
            foreach (var f in Files(root, "items")) AddItem(Parse<ItemDef>(f), f);
            foreach (var f in Files(root, "machines")) AddMachine(Parse<MachineDef>(f), f);
            foreach (var f in Files(root, "recipes")) AddRecipe(Parse<RecipeDef>(f), f);
            foreach (var f in Files(root, "crafts")) AddCraft(Parse<CraftDef>(f), f);
            foreach (var f in Files(root, "knowledge")) AddKnowledge(Parse<KnowledgeNode>(f), f);
            foreach (var f in Files(root, "enemies")) AddEnemy(Parse<EnemyDef>(f), f);

            string tex = Path.Combine(root, "textures.json");
            if (File.Exists(tex))
            {
                var specs = SafeDeserialize<Dictionary<string, TextureSpec>>(File.ReadAllText(tex), tex);
                if (specs != null)
                    foreach (var kv in specs) TextureSpecs[kv.Key] = kv.Value;
            }
        }

        private IEnumerable<string> Files(string root, string sub)
        {
            string dir = Path.Combine(root, sub);
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            var files = Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal); // детерминированный порядок
            return files;
        }

        private T Parse<T>(string file) where T : class => SafeDeserialize<T>(File.ReadAllText(file), file);

        private T SafeDeserialize<T>(string json, string source) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json, JsonSettings);
            }
            catch (Exception e)
            {
                Errors.Add($"{source}: JSON error: {e.Message}");
                return null;
            }
        }

        /// <summary>Загрузка из строк — для тестов и кода-модов.</summary>
        public void AddBlockJson(string json) => AddBlock(SafeDeserialize<BlockDef>(json, "<json>"), "<json>");
        public void AddItemJson(string json) => AddItem(SafeDeserialize<ItemDef>(json, "<json>"), "<json>");
        public void AddFluidJson(string json) => AddFluid(SafeDeserialize<FluidDef>(json, "<json>"), "<json>");
        public void AddMachineJson(string json) => AddMachine(SafeDeserialize<MachineDef>(json, "<json>"), "<json>");
        public void AddRecipeJson(string json) => AddRecipe(SafeDeserialize<RecipeDef>(json, "<json>"), "<json>");
        public void AddCraftJson(string json) => AddCraft(SafeDeserialize<CraftDef>(json, "<json>"), "<json>");
        public void AddKnowledgeJson(string json) => AddKnowledge(SafeDeserialize<KnowledgeNode>(json, "<json>"), "<json>");

        private bool CheckId(string id, string src)
        {
            if (Frozen)
            {
                Errors.Add($"{src}: database is frozen");
                return false;
            }
            if (string.IsNullOrEmpty(id) || id.IndexOf(':') <= 0)
            {
                Errors.Add($"{src}: invalid id '{id}' (expected 'namespace:name')");
                return false;
            }
            return true;
        }

        public void AddBlock(BlockDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            if (def.NameKey == null) def.NameKey = "block." + def.Id.Replace(':', '.');
            int idx = Blocks.RegisterOrReplace(def.Id, def);
            if (idx > ushort.MaxValue) { Errors.Add($"{src}: too many blocks"); return; }
            def.NumericId = (ushort)idx;
        }

        public void AddItem(ItemDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            if (def.NameKey == null) def.NameKey = "item." + def.Id.Replace(':', '.');
            if (def.MaxStack <= 0) def.MaxStack = GameConstants.MaxStackSize;
            def.NumericId = Items.RegisterOrReplace(def.Id, def);
        }

        public void AddFluid(FluidDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            if (def.NameKey == null) def.NameKey = "fluid." + def.Id.Replace(':', '.');
            def.NumericId = Fluids.RegisterOrReplace(def.Id, def);
        }

        public void AddMachine(MachineDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            if (def.NameKey == null) def.NameKey = "machine." + def.Id.Replace(':', '.');
            def.NumericId = Machines.RegisterOrReplace(def.Id, def);
        }

        public void AddRecipe(RecipeDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            def.NumericId = Recipes.RegisterOrReplace(def.Id, def);
        }

        public void AddCraft(CraftDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            def.NumericId = Crafts.RegisterOrReplace(def.Id, def);
        }

        public void AddKnowledge(KnowledgeNode def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            if (def.NameKey == null) def.NameKey = "knowledge." + def.Id.Replace(':', '.');
            def.NumericId = Knowledge.RegisterOrReplace(def.Id, def);
        }

        public void AddEnemy(EnemyDef def, string src)
        {
            if (def == null || !CheckId(def.Id, src)) return;
            if (def.NameKey == null) def.NameKey = "enemy." + def.Id.Replace(':', '.');
            def.NumericId = Enemies.RegisterOrReplace(def.Id, def);
        }

        // ------------------------------------------------------------ Финализация

        /// <summary>
        /// Связывает ссылки, создаёт блоки/предметы для машин, проверяет целостность.
        /// Возвращает true, если ошибок нет.
        /// </summary>
        public bool Freeze()
        {
            if (Frozen) return Errors.Count == 0;

            // Каждой машине — блок и предмет с тем же id (если не заданы явно).
            foreach (var m in Machines.Items)
            {
                if (!Blocks.Contains(m.Id))
                {
                    AddBlock(new BlockDef
                    {
                        Id = m.Id,
                        NameKey = m.NameKey,
                        Hardness = m.Hardness,
                        Tool = "pickaxe",
                        Orientable = true,
                        Drop = m.Drop,
                        Textures = m.Textures ?? new BlockTextures { All = "genesis:machine_casing" }
                    }, m.Id);
                }
                Blocks.Get(m.Id).MachineId = m.Id;
                Blocks.Get(m.Id).Orientable = true;
                if (m.Model != null && Blocks.Get(m.Id).Model == null) Blocks.Get(m.Id).Model = m.Model;

                if (!Items.Contains(m.Id))
                    AddItem(new ItemDef { Id = m.Id, NameKey = m.NameKey, PlacesBlock = m.Id }, m.Id);

                BuildDefaultSides(m);
            }

            // Каждому ставимому блоку — предмет с тем же id (если не задан явно).
            var noDrop = new List<BlockDef>();
            foreach (var b in Blocks.Items)
            {
                if (b == Air || b.Liquid || b.Unbreakable) continue;
                // "drop": "none" — ничего не выпадает (трава, папоротник), но предмет для креатива есть
                if (b.Drop == "none")
                {
                    noDrop.Add(b);
                    b.Drop = b.Id;
                }
                if (b.Drop == null) b.Drop = b.Id;
                if (!Items.Contains(b.Id))
                    AddItem(new ItemDef { Id = b.Id, NameKey = b.NameKey, PlacesBlock = b.Id }, b.Id);
            }

            // Проверка ссылок блоков и предметов.
            foreach (var b in Blocks.Items)
            {
                if (b.Drop != null && !Items.Contains(b.Drop))
                {
                    // Блок без отдельного предмета — создаём предмет, ставящий этот блок.
                    if (b.Drop == b.Id)
                        AddItem(new ItemDef { Id = b.Id, NameKey = b.NameKey, PlacesBlock = b.Id }, b.Id);
                    else
                        Errors.Add($"block {b.Id}: drop '{b.Drop}' not found");
                }
                if (b.DepositOf != null && !Items.Contains(b.DepositOf))
                    Errors.Add($"block {b.Id}: deposit_of '{b.DepositOf}' not found");
            }
            foreach (var b in noDrop) b.Drop = null;
            foreach (var it in Items.Items)
            {
                if (it.PlacesBlock != null && !Blocks.Contains(it.PlacesBlock))
                    Errors.Add($"item {it.Id}: places '{it.PlacesBlock}' not found");
            }
            foreach (var f in Fluids.Items)
            {
                if (f.SourceBlock != null && !Blocks.Contains(f.SourceBlock))
                    Errors.Add($"fluid {f.Id}: source_block '{f.SourceBlock}' not found");
            }

            foreach (var m in Machines.Items)
            {
                foreach (var t in m.Tanks)
                    if (t.Fluid != null && !Fluids.Contains(t.Fluid))
                        Errors.Add($"machine {m.Id}: tank fluid '{t.Fluid}' not found");
            }

            // Сетка крафта: разобрать шаблоны, посчитать входы (для автосборщика, ME и справочника)
            foreach (var c in Crafts.Items)
                if (c.Pattern != null || c.Shapeless) BuildGrid(c);

            // Машинные рецепты из ручных крафтов станций (crafts_of); верстак — все рецепты сетки
            foreach (var m in Machines.Items)
            {
                if (m.CraftsOf == null) continue;
                if (!Machines.Contains(m.CraftsOf)) Errors.Add($"machine {m.Id}: crafts_of '{m.CraftsOf}' not found");
                int ticks = (int)m.Param("craft_ticks", 40);
                foreach (var c in Crafts.Items)
                {
                    if (c.Station != m.CraftsOf && !(c.IsGrid && m.CraftsOf == "genesis:workbench")) continue;
                    if (c.Inputs.Count == 0) continue;
                    // Рецепт с катализатором (~#axes) машине не подходит: инструмента у неё нет, а без проверки
                    // она делала бы «доски топором» вообще без топора
                    if (c.Cells != null && Array.Exists(c.Cells, g => g != null && g.Keep)) continue;
                    var r = new RecipeDef
                    {
                        Id = m.Id + "." + c.Id.Replace(':', '.'),
                        Machine = m.Id,
                        TimeTicks = ticks,
                        SourceCraft = c.Id
                    };
                    foreach (var i in c.Inputs) r.Inputs.Add(new ItemAmount { Item = i.Item, Count = i.Count });
                    foreach (var o in c.Outputs) r.Outputs.Add(new ItemAmount { Item = o.Item, Count = o.Count });
                    if (!Recipes.Contains(r.Id)) AddRecipe(r, m.Id);
                }
            }

            // Рецепты.
            foreach (var r in Recipes.Items)
            {
                var machine = Machines.Get(r.Machine);
                if (machine == null)
                {
                    Errors.Add($"recipe {r.Id}: machine '{r.Machine}' not found");
                    continue;
                }
                ResolveItems(r.Inputs, r.Id);
                ResolveItems(r.Outputs, r.Id);
                ResolveItems(r.Byproducts, r.Id);
                ResolveFluids(r.FluidInputs, r.Id);
                ResolveFluids(r.FluidOutputs, r.Id);
                if (r.TimeTicks <= 0) r.TimeTicks = 1;

                if (!_recipesByMachine.TryGetValue(machine.NumericId, out var list))
                    _recipesByMachine[machine.NumericId] = list = new List<RecipeDef>();
                list.Add(r);
            }

            // Ручные крафты
            foreach (var c in Crafts.Items)
            {
                if (!c.IsHand && !Machines.Contains(c.Station))
                    Errors.Add($"craft {c.Id}: station '{c.Station}' not found");
                ResolveItems(c.Inputs, c.Id);
                ResolveItems(c.Outputs, c.Id);
                if (c.Minigame != null && c.Minigame.Template != "forging" && c.Minigame.Template != "smelting" &&
                    c.Minigame.Template != "assembly" && c.Minigame.Template != "soldering")
                    Errors.Add($"craft {c.Id}: unknown minigame template '{c.Minigame.Template}'");
            }

            // Дерево знаний
            foreach (var n in Knowledge.Items)
            {
                n.EraIndex = Eras.IndexOf(n.Era);
                if (n.EraIndex < 0) Errors.Add($"knowledge {n.Id}: unknown era '{n.Era}'");
                foreach (var r in n.Requires)
                    if (!Knowledge.Contains(r)) Errors.Add($"knowledge {n.Id}: requires unknown '{r}'");
                foreach (var c in n.Unlocks.Crafts)
                    if (!Crafts.Contains(c)) Errors.Add($"knowledge {n.Id}: unlocks unknown craft '{c}'");
                foreach (var r in n.Unlocks.Recipes)
                    if (!Recipes.Contains(r)) Errors.Add($"knowledge {n.Id}: unlocks unknown recipe '{r}'");
                var t = n.Trigger ?? (n.Trigger = new KnowledgeTrigger());
                switch (t.Type)
                {
                    case "start": break;
                    case "craft": if (!Crafts.Contains(t.Target)) Errors.Add($"knowledge {n.Id}: trigger craft '{t.Target}' not found"); break;
                    case "place": if (!Blocks.Contains(t.Target)) Errors.Add($"knowledge {n.Id}: trigger block '{t.Target}' not found"); break;
                    case "obtain": if (!Items.Contains(t.Target)) Errors.Add($"knowledge {n.Id}: trigger item '{t.Target}' not found"); break;
                    default: Errors.Add($"knowledge {n.Id}: unknown trigger '{t.Type}'"); break;
                }
                if (n.Icon != null && !Items.Contains(n.Icon)) Errors.Add($"knowledge {n.Id}: icon '{n.Icon}' not found");
            }

            foreach (var e in Enemies.Items)
            {
                ResolveItems(e.Drops, e.Id);
                if (e.Food != null)
                {
                    e.FoodItem = Items.IndexOf(e.Food);
                    if (e.FoodItem < 0) Errors.Add($"creature {e.Id}: food '{e.Food}' not found");
                }
                if (e.Lays != null) ResolveItems(new List<ItemAmount> { e.Lays }, e.Id);
                if (e.Group == null || e.Group.Length < 2) e.Group = new[] { 1, 1 };
            }

            // Машины, разделяющие рецепты другой машины (electric_furnace → stone_furnace)
            foreach (var m in Machines.Items)
            {
                if (m.RecipesOf == null) continue;
                var target = Machines.Get(m.RecipesOf);
                if (target == null)
                {
                    Errors.Add($"machine {m.Id}: recipes_of '{m.RecipesOf}' not found");
                    continue;
                }
                // Свой список: рецепты цели + собственные. Список цели не трогаем — раньше собственные рецепты
                // электропечи попадали и в каменный горн
                var combined = _recipesByMachine.TryGetValue(target.NumericId, out var shared)
                    ? new List<RecipeDef>(shared)
                    : new List<RecipeDef>();
                if (_recipesByMachine.TryGetValue(m.NumericId, out var own))
                    foreach (var r in own) if (!combined.Contains(r)) combined.Add(r);
                _recipesByMachine[m.NumericId] = combined;
            }

            BuildTextureTable();
            ContentHash = ComputeHash();
            Frozen = true;
            return Errors.Count == 0;
        }

        private void BuildDefaultSides(MachineDef m)
        {
            for (int i = 0; i < 6; i++) m.DefaultSideConfig[i] = SideType.None;
            foreach (var s in m.Sides)
                if (!SideTypes.TryParse(s, out _)) Errors.Add($"machine {m.Id}: unknown side type '{s}'");

            if (m.DefaultFaces != null)
            {
                foreach (var kv in m.DefaultFaces)
                {
                    if (!Directions.TryParseRelative(kv.Key, out var rel))
                    {
                        Errors.Add($"machine {m.Id}: unknown face '{kv.Key}'");
                        continue;
                    }
                    if (!SideTypes.TryParse(kv.Value, out var type))
                    {
                        Errors.Add($"machine {m.Id}: unknown side type '{kv.Value}'");
                        continue;
                    }
                    m.DefaultSideConfig[(int)rel] = type;
                }
            }
        }

        private void ResolveItems(List<ItemAmount> list, string owner)
        {
            if (list == null) return;
            foreach (var a in list)
            {
                a.ItemId = Items.IndexOf(a.Item);
                if (a.ItemId < 0) Errors.Add($"recipe {owner}: item '{a.Item}' not found");
                if (a.Count <= 0) a.Count = 1;
            }
        }

        private void ResolveFluids(List<FluidAmount> list, string owner)
        {
            if (list == null) return;
            foreach (var a in list)
            {
                a.FluidId = Fluids.IndexOf(a.Fluid);
                if (a.FluidId < 0) Errors.Add($"recipe {owner}: fluid '{a.Fluid}' not found");
            }
        }

        /// <summary>
        /// Добавить служебную текстуру в массив (трещины, частицы) — можно и после Freeze,
        /// до сборки текстур клиентом. На отпечаток контента не влияет.
        /// </summary>
        public int EnsureTexture(string name) => TextureIndex(name);

        public int TextureLayerOf(string name) => _textureIndex.TryGetValue(name, out int i) ? i : 0;

        private int TextureIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "genesis:missing";
            if (_textureIndex.TryGetValue(name, out int idx)) return idx;
            idx = TextureNames.Count;
            TextureNames.Add(name);
            _textureIndex[name] = idx;
            return idx;
        }

        private void BuildTextureTable()
        {
            TextureIndex("genesis:missing");
            foreach (var b in Blocks.Items)
            {
                if (b == Air) continue;
                var t = b.Textures ?? new BlockTextures { All = b.Id };
                string all = t.All ?? b.Id;
                string side = t.Side ?? all;
                int sideIdx = TextureIndex(side);
                int top = TextureIndex(t.Top ?? all);
                int bottom = TextureIndex(t.Bottom ?? all);
                b.FaceTexture[(int)Direction.North] = sideIdx;
                b.FaceTexture[(int)Direction.South] = sideIdx;
                b.FaceTexture[(int)Direction.East] = sideIdx;
                b.FaceTexture[(int)Direction.West] = sideIdx;
                b.FaceTexture[(int)Direction.Up] = top;
                b.FaceTexture[(int)Direction.Down] = bottom;
                b.FaceTexture[6] = TextureIndex(t.Front ?? side);
            }
            foreach (var it in Items.Items)
            {
                if (it.Texture != null) it.TextureLayer = TextureIndex(it.Texture);
                else if (it.PlacesBlock != null) it.TextureLayer = Blocks.Get(it.PlacesBlock).FaceTexture[6];
                else it.TextureLayer = TextureIndex(it.Id);
            }
        }

        private static readonly JsonSerializerSettings FingerprintSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        };

        /// <summary>
        /// Отпечаток контента: ID в порядке регистрации И значения определений (рецепты, время крафта,
        /// прочность, свойства блоков). Раньше учитывались только ID — у друга с изменённым рецептом
        /// хеш совпадал, и справочник показывал неверные рецепты.
        /// </summary>
        private ulong ComputeHash()
        {
            var sb = new System.Text.StringBuilder();
            void Section<T>(Registry<T> reg) where T : class
            {
                for (int i = 0; i < reg.Count; i++)
                {
                    sb.Append(reg.IdOf(i)).Append('=');
                    sb.Append(Fingerprint(reg.Get(i))).Append('|');
                }
                sb.Append('#');
            }
            Section(Blocks);
            Section(Items);
            Section(Fluids);
            Section(Machines);
            Section(Recipes);
            Section(Crafts);
            Section(Knowledge);
            Section(Enemies);
            return Hash.String(sb.ToString());
        }

        private static string Fingerprint(object def)
        {
            if (def == null) return "";
            try
            {
                return JsonConvert.SerializeObject(def, FingerprintSettings);
            }
            catch (Exception)
            {
                return def.GetType().Name;
            }
        }

        // ------------------------------------------------------------ Запросы

        // ------------------------------------------------------------ Сетка крафта

        private void BuildGrid(CraftDef c)
        {
            c.IsGrid = true;
            var cells = new List<GridIngredient>();
            if (c.Shapeless)
            {
                // inputs: count — число клеток с этим предметом
                foreach (var i in c.Inputs)
                    for (int k = 0; k < Math.Max(1, i.Count); k++) cells.Add(ParseIngredient(i.Item, c.Id));
                c.GridW = c.GridH = cells.Count <= 4 ? 2 : 3;
                if (cells.Count > 9) Errors.Add($"craft {c.Id}: shapeless recipe needs more than 9 cells");
                c.Cells = cells.ToArray();
                // inputs бесформенного рецепта — это спецификации клеток (могут быть теги «#logs», катализаторы «~»):
                // для автосборщика и справочника пересчитываем их в конкретные предметы
                c.Inputs.Clear();
                SumCellsIntoInputs(c);
                return;
            }

            // Обрезать пустые края шаблона (" S " → "S"), как в Minecraft
            int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
            for (int yy = 0; yy < c.Pattern.Count; yy++)
                for (int xx = 0; xx < (c.Pattern[yy] ?? "").Length; xx++)
                    if (c.Pattern[yy][xx] != ' ')
                    {
                        x0 = Math.Min(x0, xx);
                        x1 = Math.Max(x1, xx);
                        y0 = Math.Min(y0, yy);
                        y1 = Math.Max(y1, yy);
                    }
            if (x1 >= 0)
            {
                var cropped = new List<string>();
                for (int yy = y0; yy <= y1; yy++)
                {
                    string row = c.Pattern[yy] ?? "";
                    row = row.PadRight(x1 + 1);
                    cropped.Add(row.Substring(x0, x1 - x0 + 1));
                }
                c.Pattern = cropped;
            }

            int h = c.Pattern.Count, w = 0;
            foreach (var row in c.Pattern) w = Math.Max(w, row.Length);
            if (h == 0 || h > 3 || w > 3)
            {
                Errors.Add($"craft {c.Id}: pattern must be 1..3 rows of 1..3 symbols");
                c.IsGrid = false;
                return;
            }
            c.GridW = w;
            c.GridH = h;
            c.Cells = new GridIngredient[w * h];
            var parsed = new Dictionary<char, GridIngredient>();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                string row = c.Pattern[y];
                char ch = x < row.Length ? row[x] : ' ';
                if (ch == ' ') continue;
                if (!parsed.TryGetValue(ch, out var ing))
                {
                    if (c.Key == null || !c.Key.TryGetValue(ch.ToString(), out var spec))
                    {
                        Errors.Add($"craft {c.Id}: symbol '{ch}' is not in key");
                        continue;
                    }
                    parsed[ch] = ing = ParseIngredient(spec, c.Id);
                }
                c.Cells[y * w + x] = ing;
            }

            // Входы для автосборщика/справочника: сумма по клеткам (катализаторы не тратятся — не входят)
            if (c.Inputs.Count == 0) SumCellsIntoInputs(c);
        }

        private void SumCellsIntoInputs(CraftDef c)
        {
            var sum = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var ing in c.Cells)
            {
                if (ing == null || ing.Keep || ing.Matches.Length == 0) continue;
                int item = ing.Matches[0];
                if (!sum.ContainsKey(item)) order.Add(item);
                sum[item] = (sum.TryGetValue(item, out var n) ? n : 0) + ing.Count;
            }
            foreach (var item in order) c.Inputs.Add(new ItemAmount { Item = Items.IdOf(item), Count = sum[item] });
        }

        /// <summary>"[~][#]id[*N]": ~ — катализатор, # — тег, *N — штук в клетке.</summary>
        private GridIngredient ParseIngredient(string spec, string owner)
        {
            var g = new GridIngredient { Spec = spec };
            string s = (spec ?? "").Trim();
            if (s.StartsWith("~", StringComparison.Ordinal))
            {
                g.Keep = true;
                s = s.Substring(1);
            }
            int star = s.LastIndexOf('*');
            if (star > 0 && int.TryParse(s.Substring(star + 1), out var count))
            {
                g.Count = Math.Max(1, count);
                s = s.Substring(0, star);
            }
            var matches = new List<int>();
            if (s.StartsWith("#", StringComparison.Ordinal))
            {
                g.Tag = s.Substring(1);
                for (int i = 0; i < Items.Count; i++)
                    if (Items.Get(i).Tags != null && Items.Get(i).Tags.Contains(g.Tag)) matches.Add(i);
                if (matches.Count == 0) Errors.Add($"craft {owner}: no items with tag '{g.Tag}'");
            }
            else
            {
                g.ItemId = Items.IndexOf(s);
                if (g.ItemId < 0) Errors.Add($"craft {owner}: item '{s}' not found");
                else matches.Add(g.ItemId);
            }
            g.Matches = matches.ToArray();
            return g;
        }

        public BlockDef Block(ushort id) => Blocks.Get(id) ?? Air;
        public ItemDef Item(int id) => Items.Get(id);

        public IReadOnlyList<RecipeDef> RecipesFor(int machineNumericId) =>
            _recipesByMachine.TryGetValue(machineNumericId, out var list) ? list : NoRecipes;

        public ushort BlockId(string id)
        {
            int idx = Blocks.IndexOf(id);
            if (idx < 0) throw new KeyNotFoundException($"Block '{id}' not registered");
            return (ushort)idx;
        }

        public int ItemId(string id)
        {
            int idx = Items.IndexOf(id);
            if (idx < 0) throw new KeyNotFoundException($"Item '{id}' not registered");
            return idx;
        }

        public int FluidId(string id)
        {
            int idx = Fluids.IndexOf(id);
            if (idx < 0) throw new KeyNotFoundException($"Fluid '{id}' not registered");
            return idx;
        }

        public TextureSpec TextureSpecFor(string name) =>
            TextureSpecs.TryGetValue(name, out var s) ? s : null;
    }
}
