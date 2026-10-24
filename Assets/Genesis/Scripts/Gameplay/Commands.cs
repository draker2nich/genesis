using Genesis.Core;
using Genesis.Data;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Команда игрока → серверу. В одиночной игре идёт напрямую в локальный сервер,
    /// в коопе (этап 2) сериализуется и передаётся через FishNet. Сервер валидирует всё.
    /// </summary>
    public abstract class GameCommand
    {
        public int PlayerId;
        public abstract void Apply(GameServer server, PlayerState player);
    }

    /// <summary>Позиция и взгляд — клиент-авторитетно (кооп с друзьями, без античита).</summary>
    public sealed class PlayerMoveCommand : GameCommand
    {
        public Vec3 Position;
        public float Yaw, Pitch;
        public bool Flying;

        public override void Apply(GameServer s, PlayerState p)
        {
            p.Position = Position;
            p.Yaw = Yaw;
            p.Pitch = Pitch;
            p.Flying = Flying && p.Creative; // полёт — только в креативе
        }
    }

    public sealed class BreakBlockCommand : GameCommand
    {
        public Int3 Pos;
        public override void Apply(GameServer s, PlayerState p) => s.TryBreakBlock(p, Pos);
    }

    public sealed class PlaceBlockCommand : GameCommand
    {
        public Int3 Pos;
        /// <summary>Направление взгляда игрока при установке (ориентация машин и лент).</summary>
        public Direction LookFacing;
        public override void Apply(GameServer s, PlayerState p) => s.TryPlaceSelected(p, Pos, LookFacing);
    }

    /// <summary>ПКМ по ленте с предметом в руке — положить предмет на ленту.</summary>
    public sealed class PutOnConveyorCommand : GameCommand
    {
        public Int3 Pos;
        public override void Apply(GameServer s, PlayerState p) => s.TryPutOnConveyor(p, Pos);
    }

    public sealed class RotateCommand : GameCommand
    {
        public Int3 Pos;
        public override void Apply(GameServer s, PlayerState p) => s.RotateEntity(p, Pos);
    }

    public sealed class SelectSlotCommand : GameCommand
    {
        public int Slot;
        public override void Apply(GameServer s, PlayerState p)
        {
            if (Slot >= 0 && Slot < PlayerState.HotbarSize) p.SelectedSlot = Slot;
        }
    }

    public enum MachineInventoryKind : byte
    {
        Input = 0,
        Output = 1,
        Fuel = 2
    }

    /// <summary>Клик по слоту машины: забрать стак в инвентарь игрока.</summary>
    public sealed class TakeFromMachineCommand : GameCommand
    {
        public Int3 Pos;
        public MachineInventoryKind Kind;
        public int Slot;
        public override void Apply(GameServer s, PlayerState p) => s.TakeFromMachine(p, Pos, Kind, Slot);
    }

    /// <summary>Клик по слоту игрока при открытой машине: положить стак в машину.</summary>
    public sealed class PutIntoMachineCommand : GameCommand
    {
        public Int3 Pos;
        public int PlayerSlot;
        public override void Apply(GameServer s, PlayerState p) => s.PutIntoMachine(p, Pos, PlayerSlot);
    }

    public sealed class SetSideCommand : GameCommand
    {
        public Int3 Pos;
        public RelativeSide Side;
        public SideType Type;
        public override void Apply(GameServer s, PlayerState p) => s.SetMachineSide(p, Pos, Side, Type);
    }

    /// <summary>Выбор рецепта машины в её интерфейсе (Recipe &lt; 0 — машина выбирает сама).</summary>
    public sealed class SetRecipeCommand : GameCommand
    {
        public Int3 Pos;
        public int Recipe = -1;
        public override void Apply(GameServer s, PlayerState p) => s.SetMachineRecipe(p, Pos, Recipe);
    }

    /// <summary>Переложить/поменять местами слоты инвентаря игрока.</summary>
    public sealed class SwapSlotsCommand : GameCommand
    {
        public int A, B;
        public override void Apply(GameServer s, PlayerState p)
        {
            var inv = p.Inventory;
            if (A < 0 || B < 0 || A >= inv.Size || B >= inv.Size || A == B) return;
            var a = inv[A];
            var b = inv[B];
            if (!a.IsEmpty && !b.IsEmpty && a.SameKind(b))
            {
                int moved = inv.InsertIntoSlot(B, a);
                inv[A] = a.WithCount(a.Count - moved);
                return;
            }
            inv[A] = b;
            inv[B] = a;
        }
    }

    /// <summary>Dev/креатив: выдать предметы.</summary>
    public sealed class CreativeGiveCommand : GameCommand
    {
        public int Item;
        public int Count;
        public override void Apply(GameServer s, PlayerState p)
        {
            if (!p.Creative) return;
            p.Inventory.Insert(new Genesis.Simulation.ItemStack(Item, Count));
        }
    }

    /// <summary>Выбросить предмет из слота в мир (Z — один, Shift+Z — весь стак).</summary>
    public sealed class DropSlotCommand : GameCommand
    {
        public int Slot;
        public bool All;
        public override void Apply(GameServer s, PlayerState p) => s.DropSlot(p, Slot, All);
    }

    /// <summary>Клик по слоту окна (как в Minecraft): ЛКМ/ПКМ, Shift — быстрый перенос.</summary>
    public sealed class SlotClickCommand : GameCommand
    {
        public SlotContainer Container;
        public int Slot;
        public int Button;
        public bool Shift;
        public override void Apply(GameServer s, PlayerState p) => s.ClickSlot(p, Container, Slot, Button, Shift);
    }

    /// <summary>Забрать результат сетки (после мини-игры — с её итогом).</summary>
    public sealed class GridCraftCommand : GameCommand
    {
        public bool Shift;
        public bool MinigameSuccess;
        public int CraftId = -1;
        public override void Apply(GameServer s, PlayerState p) => s.CraftFromGrid(p, Shift, MinigameSuccess, CraftId);
    }

    /// <summary>Открыть (размер 2/3, станция) или закрыть окно сетки.</summary>
    public sealed class OpenGridCommand : GameCommand
    {
        public bool Open;
        public int Size = 2;
        public Int3 Station;
        public override void Apply(GameServer s, PlayerState p)
        {
            if (Open) s.OpenGrid(p, Size, Station);
            else s.CloseGrid(p);
        }
    }

    /// <summary>Выбросить предмет с курсора (клик мимо окна).</summary>
    public sealed class DropCursorCommand : GameCommand
    {
        public bool All = true;
        public override void Apply(GameServer s, PlayerState p) => s.DropCursor(p, All);
    }

    /// <summary>ПКМ по существу (накормить).</summary>
    public sealed class InteractCommand : GameCommand
    {
        public int CreatureId;
        public override void Apply(GameServer s, PlayerState p) => s.Interact(p, CreatureId);
    }

    /// <summary>Разложить рецепт из справочника в сетку.</summary>
    public sealed class FillGridCommand : GameCommand
    {
        public int CraftId;
        public bool Max;
        public override void Apply(GameServer s, PlayerState p) => s.FillGrid(p, CraftId, Max);
    }
}

namespace Genesis.Gameplay
{
    /// <summary>Ручная операция (крафт) на станции или в руках; для мини-игр — с результатом.</summary>
    public sealed class CraftCommand : GameCommand
    {
        public int CraftId;
        public Int3 Station;
        public bool MinigameSuccess;
        public override void Apply(GameServer s, PlayerState p) => s.Craft(p, CraftId, Station, MinigameSuccess);
    }

    public sealed class AttackCommand : GameCommand
    {
        public int EnemyId;
        public override void Apply(GameServer s, PlayerState p) => s.Attack(p, EnemyId);
    }

    public sealed class EatCommand : GameCommand
    {
        public override void Apply(GameServer s, PlayerState p) => s.Eat(p);
    }

    /// <summary>Урон, посчитанный клиентом (падение) — движение клиент-авторитетно.</summary>
    public sealed class DamageSelfCommand : GameCommand
    {
        public float Amount;
        public override void Apply(GameServer s, PlayerState p) => s.DamagePlayer(p, System.Math.Min(Amount, 40f), "fall");
    }

    /// <summary>Dev/креатив: открыть все узлы текущей эпохи и перейти в следующую.</summary>
    public sealed class CreativeAdvanceEraCommand : GameCommand
    {
        public override void Apply(GameServer s, PlayerState p)
        {
            if (!p.Creative) return;
            int era = s.Knowledge.Era;
            foreach (var n in s.Content.Knowledge.Items)
                if (n.EraIndex <= era) s.Knowledge.ForceUnlock(n.Id);
        }
    }
}

namespace Genesis.Gameplay
{
    // ================================================================ ME-сеть

    /// <summary>Открыть/закрыть окно ME. Wireless — доступ через беспроводной терминал в руке.</summary>
    public sealed class MeViewCommand : GameCommand
    {
        public Int3 Access;
        public bool Wireless;
        public bool Open;
        public override void Apply(GameServer s, PlayerState p) => s.MeView(p, Access, Wireless, Open);
    }

    public sealed class MeExtractCommand : GameCommand
    {
        public Int3 Access;
        public int Item;
        public int Count;
        public override void Apply(GameServer s, PlayerState p) => s.MeExtract(p, Access, Item, Count);
    }

    public sealed class MeInsertCommand : GameCommand
    {
        public Int3 Access;
        public int PlayerSlot;
        public override void Apply(GameServer s, PlayerState p) => s.MeInsert(p, Access, PlayerSlot);
    }

    public sealed class MeCraftCommand : GameCommand
    {
        public Int3 Access;
        public int Item;
        public int Count;
        public override void Apply(GameServer s, PlayerState p) => s.MeCraft(p, Access, Item, Count);
    }

    /// <summary>Автоподдержание запаса: Target 0 — убрать правило.</summary>
    public sealed class MeStockCommand : GameCommand
    {
        public Int3 Access;
        public int Item;
        public long Target;
        public override void Apply(GameServer s, PlayerState p) => s.MeStock(p, Access, Item, Target);
    }

    public sealed class MeCancelCommand : GameCommand
    {
        public Int3 Access;
        public override void Apply(GameServer s, PlayerState p) => s.MeCancel(p, Access);
    }

    /// <summary>Слот фильтра шины: Item −1 — очистить.</summary>
    public sealed class MeFilterCommand : GameCommand
    {
        public Int3 Pos;
        public int Slot;
        public int Item;
        public override void Apply(GameServer s, PlayerState p) => s.MeFilter(p, Pos, Slot, Item);
    }

    public sealed class MeFluidFilterCommand : GameCommand
    {
        public Int3 Pos;
        public int Fluid;
        public override void Apply(GameServer s, PlayerState p) => s.MeFluidFilter(p, Pos, Fluid);
    }

    public sealed class MePatternCommand : GameCommand
    {
        public Int3 Pos;
        public int Recipe;
        public bool Enabled;
        public override void Apply(GameServer s, PlayerState p) => s.MePattern(p, Pos, Recipe, Enabled);
    }

    /// <summary>Привязать беспроводной терминал в руке к контроллеру.</summary>
    public sealed class MeLinkCommand : GameCommand
    {
        public Int3 Controller;
        public override void Apply(GameServer s, PlayerState p) => s.MeLink(p, Controller);
    }
}

namespace Genesis.Gameplay
{
    // ================================================================ Первобытная эпоха

    /// <summary>F — поменять местами правую и левую руку.</summary>
    public sealed class SwapHandsCommand : GameCommand
    {
        public override void Apply(GameServer s, PlayerState p) => s.SwapHands(p);
    }

    /// <summary>Крафт в двух руках (ПКМ удерживался нужное время).</summary>
    public sealed class HandCraftCommand : GameCommand
    {
        public int CraftId;
        public override void Apply(GameServer s, PlayerState p) => s.HandCraft(p, CraftId);
    }

    /// <summary>Удар в режиме создания (или «тик» удержания ПКМ).</summary>
    public sealed class ShapeHitCommand : GameCommand
    {
        public Int3 Pos;
        public int ShapingId;
        public override void Apply(GameServer s, PlayerState p) => s.ShapeHitAt(p, Pos, ShapingId);
    }

    /// <summary>Собрать конструкцию ударом инструмента в режиме создания.</summary>
    public sealed class BuildStructureCommand : GameCommand
    {
        public Int3 Pos;
        public int StructureId = -1;
        public override void Apply(GameServer s, PlayerState p) => s.BuildStructure(p, Pos, StructureId);
    }

    /// <summary>ПКМ предметом по блоку (уголёк по хворосту, глина по печке, мотыга по земле).</summary>
    public sealed class UseOnBlockCommand : GameCommand
    {
        public Int3 Pos;
        public override void Apply(GameServer s, PlayerState p) => s.UseOnBlock(p, Pos);
    }

    /// <summary>ПКМ по блоку с действием (справочник, лежанка).</summary>
    public sealed class UseBlockCommand : GameCommand
    {
        public Int3 Pos;
        public override void Apply(GameServer s, PlayerState p) => s.UseBlockAction(p, Pos);
    }

    /// <summary>Положить предмет на рабочую поверхность (Put) или забрать с неё.</summary>
    public sealed class SurfaceCommand : GameCommand
    {
        public Int3 Pos;
        public bool Put;
        public override void Apply(GameServer s, PlayerState p) => s.UseSurface(p, Pos, Put);
    }

    /// <summary>Положить предмет из руки на пол (на верх блока Pos).</summary>
    public sealed class PlaceOnFloorCommand : GameCommand
    {
        public Int3 Pos;
        public bool All;
        public override void Apply(GameServer s, PlayerState p) => s.PlaceOnFloor(p, Pos, All);
    }

    /// <summary>Подобрать лежащий предмет.</summary>
    public sealed class PickupDropCommand : GameCommand
    {
        public int DropId;
        public override void Apply(GameServer s, PlayerState p) => s.PickupDrop(p, DropId);
    }
}
