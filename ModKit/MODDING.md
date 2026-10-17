# Моддинг Genesis

Контент самой игры (пространство имён `genesis`) лежит в `Assets/StreamingAssets/Game` и устроен так же, как мод, — всё, что в нём есть, доступно модам и может ими переопределяться. Но это не мод: игра грузится всегда первой и не отключается, а мод с id `genesis` не загрузится.

## Где лежат моды

- `<persistentDataPath>/Mods/` — моды игрока. Кнопка «Папка модов» в главном меню открывает её.
- Подписки Steam Workshop — подхватываются автоматически, если установлен Steamworks.NET и запущен Steam.

Если мод с тем же `id` есть и локально, и в Workshop, побеждает локальная копия (удобно при разработке).
Моды включаются и выключаются в меню «Моды» → «Применить» (контент перезагружается без перезапуска игры).

## Структура мода

```
my_mod/
  mod.json            манифест
  blocks/*.json       блоки
  items/*.json        предметы (инструменты, еда, ячейки ME...)
  fluids/*.json       жидкости и газы
  machines/*.json     машины
  recipes/*.json      машинные рецепты
  crafts/*.json       ручные операции (станция + мини-игра)
  knowledge/*.json    узлы дерева знаний
  enemies/*.json      враги
  textures.json       процедурные текстуры: имя → { pattern, color, accent }
  textures/*.png      PNG 32×32 или 64×64 (переопределяют процедурные)
  textures/<ns>/*.png PNG для чужого пространства имён (ретекстур игры)
  models/*.bbmodel    модели Blockbench (блоки, машины, предметы, существа) — см. CONTENT.md
  models/<ns>/*.bbmodel  замена моделей игры
  lang/en.json, lang/ru.json   строки: ключ → текст
  assemblies/*.dll    код (необязательно)
  preview.png         картинка для Workshop
```

`mod.json`:

```json
{
  "id": "bronze",
  "name": "Bronze Age",
  "version": "1.0.0",
  "author": "you",
  "description": "...",
  "dependencies": ["genesis"],
  "load_after": []
}
```

Порядок загрузки: `genesis` → дальше по зависимостям, при равенстве по `id`. Поздний мод может переопределить запись раннего с тем же `id`.
Если зависимость отсутствует или выключена — мод не загружается, причина видна в меню.

## Идентификаторы

Все ID — `пространство:имя` (`bronze:bronze_ingot`). Ключи строк выводятся из ID: `item.bronze.bronze_ingot`, `block.…`, `machine.…`, `knowledge.…`, `fluid.…`, `enemy.…`.

## Справочник полей

**Блок**: `render` (opaque/cutout/translucent/invisible), `solid`, `hardness`, `tool` (pickaxe/axe/shovel), `harvest_tier`, `drop`, `drop_count`, `textures` (all/top/bottom/side/front), `light` (0–15), `shape` (cube/cross/torch), `model` (модель Blockbench), `tint` (grass/foliage — окраска по биому), `drop: "none"` — ничего не выпадает, `transport` ({type: conveyor/pipe/wire/me, speed, capacity}), `deposit_of` + `purity` (залежь для бура).

**Рецепт сетки** (`crafts/`): `pattern` + `key` (ингредиенты `id`, `#тег`, `id*N`, `~катализатор`) или `shapeless` + `inputs`; `outputs`; `minigame`. Подробно — `CONTENT.md`.

**Существо** (`enemies/`): `kind`, `spawn`, `biomes`, `time`, `group`, `attack`, `range`, `cooldown`, `windup`, `poison`, `flee_health`, `armor`, `weak_to`, `pack`, `death_cloud`, `curl`, `food`, `lays`, `model`.

**Предмет**: `model` (модель в руке), `max_stack`, `places` (какой блок ставит), `fuel_ticks`, `food`, `tool` ({type, tier, speed, durability, damage}), `storage_cell` ({kind: item/fluid, capacity})., `light` (0–15 — свет в руке; по умолчанию как у ставимого блока)

**Машина**: `behavior` (processor, boiler, pump, storage, station, drill, inserter, tank, generator, battery, me_* или своя из кода), `energy` ({type: none/fuel/steam/fe, per_tick, buffer, rate}), `input_slots`, `output_slots`, `fuel_slots`, `tanks`, `sides`, `default_faces`, `speed`, `params`, `anim` (spin/piston/drill/arm/rotor/none), `orient` (toward/away), `recipes_of` (взять рецепты другой машины), `crafts_of` (выполнять ручные крафты станции автоматически).

**Рецепт**: `machine`, `inputs`, `fluid_inputs`, `outputs`, `fluid_outputs`, `byproducts` (с `chance`), `time_ticks` (20 тиков = 1 с).

**Ручная операция**: `station` (`hand` или id машины-станции), `inputs`, `outputs`, `minigame` ({template: forging/smelting/assembly/soldering, difficulty 1–5}).

**Узел знаний**: `era`, `requires`, `trigger` ({type: start/craft/place/obtain, target}), `unlocks` ({crafts, recipes}), `milestone` (переводит в следующую эпоху), `icon`, `row`.

## Код-моды (C#)

Игра работает на Mono, поэтому DLL модов загружаются как есть. Пример — `examples/trash_can`:

```csharp
public sealed class TrashCanMod : IGenesisMod
{
    public void OnLoad(IModContext ctx)
    {
        ctx.RegisterBehavior("trash_can:void", new VoidBehavior()); // логика машины
        ctx.Events.BlockBroken += (server, player, pos, block) => { ... };
        ctx.Content.AddItemJson("{\"id\":\"trash_can:junk\"}");     // контент кодом
    }
}
```

`OnLoad` вызывается после загрузки JSON всех модов и до заморозки базы — можно добавлять контент.
События (`ctx.Events`): `ServerStarted`, `Tick` (20/с), `PlayerJoined`, `BlockPlaced`, `BlockBroken`, `Crafted`. Они срабатывают на стороне, где идёт симуляция (одиночная игра или хост).
Логика машин — `IMachineBehavior.Tick(MachineInstance m, Factory f)`; доступны инвентари, баки, энергия, статистика.

Сборка: `dotnet build -c Release` в папке `src` (путь к сборкам игры — свойство `GenesisManaged`, по умолчанию `Library/ScriptAssemblies` проекта Unity). Результат кладётся в `assemblies/`.

## Кооп

Хост и клиенты должны иметь одинаковый набор модов. При несовпадении подключение отклоняется со списками модов обеих сторон.

## Публикация в Workshop

1. Установи Steamworks.NET (Package Manager → git URL `https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net`), положи `steam_appid.txt` с AppID игры.
2. Положи мод в пользовательскую папку модов, добавь `preview.png`.
3. Меню «Моды» → «В Workshop». ID публикации запишется в `mod.json` (`workshop_id`), следующие публикации обновят тот же предмет.
