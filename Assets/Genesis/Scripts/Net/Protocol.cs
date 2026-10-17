using System;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;

namespace Genesis.Net
{
    public enum MsgType : byte
    {
        // клиент → хост
        Hello = 1,
        Command = 2,
        ChunkRequest = 3,

        // хост → клиент
        Welcome = 20,
        Reject = 21,
        ChunkPristine = 22,
        ChunkData = 23,
        BlockChange = 24,
        Players = 25,
        Inventory = 26,
        Machine = 27,
        Conveyor = 28,
        EntityRemoved = 29,
        Toast = 30,
        Vitals = 31,
        Knowledge = 32,
        Enemies = 33,
        Time = 34,
        MeSnapshot = 35,
        Drops = 36,
    }

    public static class Protocol
    {
        /// <summary>Меняется при несовместимых изменениях формата пакетов.</summary>
        public const int Version = 5;
        public const int DefaultPort = 27015;

        public static BinaryWriter Begin(MsgType type, out MemoryStream ms)
        {
            ms = new MemoryStream(64);
            var w = new BinaryWriter(ms);
            w.Write((byte)type);
            return w;
        }

        public static byte[] End(BinaryWriter w, MemoryStream ms)
        {
            w.Flush();
            return ms.ToArray();
        }

        public static void WritePos(BinaryWriter w, Int3 p)
        {
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
        }

        public static Int3 ReadPos(BinaryReader r) => new Int3(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        public static void WriteVec(BinaryWriter w, Vec3 v)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        public static Vec3 ReadVec(BinaryReader r) => new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        /// <summary>FNV-1a по байтам — для отслеживания изменений состояния.</summary>
        public static ulong HashBytes(byte[] data)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < data.Length; i++)
                {
                    h ^= data[i];
                    h *= 1099511628211UL;
                }
                return h;
            }
        }
    }

    // ================================================================ Команды

    /// <summary>Сериализация команд игрока для отправки хосту.</summary>
    public static class CommandCodec
    {
        public static void Write(BinaryWriter w, GameCommand cmd)
        {
            switch (cmd)
            {
                case PlayerMoveCommand c:
                    w.Write((byte)1);
                    Protocol.WriteVec(w, c.Position);
                    w.Write(c.Yaw);
                    w.Write(c.Pitch);
                    w.Write(c.Flying);
                    break;
                case BreakBlockCommand c:
                    w.Write((byte)2);
                    Protocol.WritePos(w, c.Pos);
                    break;
                case PlaceBlockCommand c:
                    w.Write((byte)3);
                    Protocol.WritePos(w, c.Pos);
                    w.Write((byte)c.LookFacing);
                    break;
                case PutOnConveyorCommand c:
                    w.Write((byte)4);
                    Protocol.WritePos(w, c.Pos);
                    break;
                case RotateCommand c:
                    w.Write((byte)5);
                    Protocol.WritePos(w, c.Pos);
                    break;
                case SelectSlotCommand c:
                    w.Write((byte)6);
                    w.Write(c.Slot);
                    break;
                case TakeFromMachineCommand c:
                    w.Write((byte)7);
                    Protocol.WritePos(w, c.Pos);
                    w.Write((byte)c.Kind);
                    w.Write(c.Slot);
                    break;
                case PutIntoMachineCommand c:
                    w.Write((byte)8);
                    Protocol.WritePos(w, c.Pos);
                    w.Write(c.PlayerSlot);
                    break;
                case SetSideCommand c:
                    w.Write((byte)9);
                    Protocol.WritePos(w, c.Pos);
                    w.Write((byte)c.Side);
                    w.Write((byte)c.Type);
                    break;
                case SwapSlotsCommand c:
                    w.Write((byte)10);
                    w.Write(c.A);
                    w.Write(c.B);
                    break;
                case CreativeGiveCommand c:
                    w.Write((byte)11);
                    w.Write(c.Item);
                    w.Write(c.Count);
                    break;
                case DropSlotCommand c:
                    w.Write((byte)12);
                    w.Write(c.Slot);
                    w.Write(c.All);
                    break;
                case SlotClickCommand c:
                    w.Write((byte)28);
                    w.Write((byte)c.Container);
                    w.Write(c.Slot);
                    w.Write((byte)c.Button);
                    w.Write(c.Shift);
                    break;
                case GridCraftCommand c:
                    w.Write((byte)29);
                    w.Write(c.Shift);
                    w.Write(c.MinigameSuccess);
                    w.Write(c.CraftId);
                    break;
                case OpenGridCommand c:
                    w.Write((byte)30);
                    w.Write(c.Open);
                    w.Write((byte)c.Size);
                    Protocol.WritePos(w, c.Station);
                    break;
                case DropCursorCommand c:
                    w.Write((byte)31);
                    w.Write(c.All);
                    break;
                case InteractCommand c:
                    w.Write((byte)33);
                    w.Write(c.CreatureId);
                    break;
                case FillGridCommand c:
                    w.Write((byte)32);
                    w.Write(c.CraftId);
                    w.Write(c.Max);
                    break;
                case CraftCommand c:
                    w.Write((byte)13);
                    w.Write(c.CraftId);
                    Protocol.WritePos(w, c.Station);
                    w.Write(c.MinigameSuccess);
                    break;
                case AttackCommand c:
                    w.Write((byte)14);
                    w.Write(c.EnemyId);
                    break;
                case EatCommand _:
                    w.Write((byte)15);
                    break;
                case DamageSelfCommand c:
                    w.Write((byte)16);
                    w.Write(c.Amount);
                    break;
                case CreativeAdvanceEraCommand _:
                    w.Write((byte)17);
                    break;
                case MeViewCommand c:
                    w.Write((byte)18);
                    Protocol.WritePos(w, c.Access);
                    w.Write(c.Wireless);
                    w.Write(c.Open);
                    break;
                case MeExtractCommand c:
                    w.Write((byte)19);
                    Protocol.WritePos(w, c.Access);
                    w.Write(c.Item);
                    w.Write(c.Count);
                    break;
                case MeInsertCommand c:
                    w.Write((byte)20);
                    Protocol.WritePos(w, c.Access);
                    w.Write(c.PlayerSlot);
                    break;
                case MeCraftCommand c:
                    w.Write((byte)21);
                    Protocol.WritePos(w, c.Access);
                    w.Write(c.Item);
                    w.Write(c.Count);
                    break;
                case MeStockCommand c:
                    w.Write((byte)22);
                    Protocol.WritePos(w, c.Access);
                    w.Write(c.Item);
                    w.Write(c.Target);
                    break;
                case MeCancelCommand c:
                    w.Write((byte)23);
                    Protocol.WritePos(w, c.Access);
                    break;
                case MeFilterCommand c:
                    w.Write((byte)24);
                    Protocol.WritePos(w, c.Pos);
                    w.Write(c.Slot);
                    w.Write(c.Item);
                    break;
                case MeFluidFilterCommand c:
                    w.Write((byte)25);
                    Protocol.WritePos(w, c.Pos);
                    w.Write(c.Fluid);
                    break;
                case MePatternCommand c:
                    w.Write((byte)26);
                    Protocol.WritePos(w, c.Pos);
                    w.Write(c.Recipe);
                    w.Write(c.Enabled);
                    break;
                case MeLinkCommand c:
                    w.Write((byte)27);
                    Protocol.WritePos(w, c.Controller);
                    break;
                default:
                    throw new NotSupportedException("Command not serializable: " + cmd.GetType().Name);
            }
        }

        public static GameCommand Read(BinaryReader r)
        {
            byte type = r.ReadByte();
            switch (type)
            {
                case 1:
                    return new PlayerMoveCommand
                    {
                        Position = Protocol.ReadVec(r),
                        Yaw = r.ReadSingle(),
                        Pitch = r.ReadSingle(),
                        Flying = r.ReadBoolean()
                    };
                case 2: return new BreakBlockCommand { Pos = Protocol.ReadPos(r) };
                case 3: return new PlaceBlockCommand { Pos = Protocol.ReadPos(r), LookFacing = (Direction)r.ReadByte() };
                case 4: return new PutOnConveyorCommand { Pos = Protocol.ReadPos(r) };
                case 5: return new RotateCommand { Pos = Protocol.ReadPos(r) };
                case 6: return new SelectSlotCommand { Slot = r.ReadInt32() };
                case 7:
                    return new TakeFromMachineCommand
                    {
                        Pos = Protocol.ReadPos(r),
                        Kind = (MachineInventoryKind)r.ReadByte(),
                        Slot = r.ReadInt32()
                    };
                case 8: return new PutIntoMachineCommand { Pos = Protocol.ReadPos(r), PlayerSlot = r.ReadInt32() };
                case 9:
                    return new SetSideCommand
                    {
                        Pos = Protocol.ReadPos(r),
                        Side = (RelativeSide)r.ReadByte(),
                        Type = (SideType)r.ReadByte()
                    };
                case 10: return new SwapSlotsCommand { A = r.ReadInt32(), B = r.ReadInt32() };
                case 11: return new CreativeGiveCommand { Item = r.ReadInt32(), Count = r.ReadInt32() };
                case 12: return new DropSlotCommand { Slot = r.ReadInt32(), All = r.ReadBoolean() };
                case 28: return new SlotClickCommand { Container = (SlotContainer)r.ReadByte(), Slot = r.ReadInt32(), Button = r.ReadByte(), Shift = r.ReadBoolean() };
                case 29: return new GridCraftCommand { Shift = r.ReadBoolean(), MinigameSuccess = r.ReadBoolean(), CraftId = r.ReadInt32() };
                case 30: return new OpenGridCommand { Open = r.ReadBoolean(), Size = r.ReadByte(), Station = Protocol.ReadPos(r) };
                case 31: return new DropCursorCommand { All = r.ReadBoolean() };
                case 32: return new FillGridCommand { CraftId = r.ReadInt32(), Max = r.ReadBoolean() };
                case 33: return new InteractCommand { CreatureId = r.ReadInt32() };
                case 13: return new CraftCommand { CraftId = r.ReadInt32(), Station = Protocol.ReadPos(r), MinigameSuccess = r.ReadBoolean() };
                case 14: return new AttackCommand { EnemyId = r.ReadInt32() };
                case 15: return new EatCommand();
                case 16: return new DamageSelfCommand { Amount = r.ReadSingle() };
                case 17: return new CreativeAdvanceEraCommand();
                case 18: return new MeViewCommand { Access = Protocol.ReadPos(r), Wireless = r.ReadBoolean(), Open = r.ReadBoolean() };
                case 19: return new MeExtractCommand { Access = Protocol.ReadPos(r), Item = r.ReadInt32(), Count = r.ReadInt32() };
                case 20: return new MeInsertCommand { Access = Protocol.ReadPos(r), PlayerSlot = r.ReadInt32() };
                case 21: return new MeCraftCommand { Access = Protocol.ReadPos(r), Item = r.ReadInt32(), Count = r.ReadInt32() };
                case 22: return new MeStockCommand { Access = Protocol.ReadPos(r), Item = r.ReadInt32(), Target = r.ReadInt64() };
                case 23: return new MeCancelCommand { Access = Protocol.ReadPos(r) };
                case 24: return new MeFilterCommand { Pos = Protocol.ReadPos(r), Slot = r.ReadInt32(), Item = r.ReadInt32() };
                case 25: return new MeFluidFilterCommand { Pos = Protocol.ReadPos(r), Fluid = r.ReadInt32() };
                case 26: return new MePatternCommand { Pos = Protocol.ReadPos(r), Recipe = r.ReadInt32(), Enabled = r.ReadBoolean() };
                case 27: return new MeLinkCommand { Controller = Protocol.ReadPos(r) };
                default: throw new InvalidDataException("Unknown command type " + type);
            }
        }
    }

    // ================================================================ Состояние фабрики

    /// <summary>Сериализация состояния машин, лент и инвентарей (числовые ID — контент сверен по хешу).</summary>
    public static class ReplicationCodec
    {
        /// <summary>Инвентарь, сетка крафта, курсор и мастерство игрока.</summary>
        public static void WritePlayerItems(BinaryWriter w, PlayerState p)
        {
            WriteInventory(w, p.Inventory);
            WriteInventory(w, p.CraftGrid);
            WriteInventory(w, p.Cursor);
            w.Write((byte)p.GridSize);
            w.Write((ushort)p.Mastery.Count);
            foreach (var kv in p.Mastery)
            {
                w.Write(kv.Key);
                w.Write(kv.Value);
            }
        }

        public static void ReadPlayerItems(BinaryReader r, PlayerState p)
        {
            ReadInventory(r, p.Inventory);
            ReadInventory(r, p.CraftGrid);
            ReadInventory(r, p.Cursor);
            p.GridSize = r.ReadByte();
            int n = r.ReadUInt16();
            p.Mastery.Clear();
            for (int i = 0; i < n; i++) p.Mastery[r.ReadString()] = r.ReadInt32();
        }

        public static void WriteInventory(BinaryWriter w, Inventory inv)
        {
            w.Write((byte)inv.Size);
            for (int i = 0; i < inv.Size; i++)
            {
                var s = inv[i];
                if (s.IsEmpty)
                {
                    w.Write((ushort)0);
                    continue;
                }
                w.Write((ushort)(s.Item + 1));
                w.Write((ushort)s.Count);
                w.Write((ushort)s.Meta);
            }
        }

        public static void ReadInventory(BinaryReader r, Inventory inv)
        {
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                int item = r.ReadUInt16() - 1;
                ItemStack stack;
                if (item < 0) stack = ItemStack.Empty;
                else
                {
                    int count = r.ReadUInt16();
                    stack = new ItemStack(item, count, r.ReadUInt16());
                }
                if (i < inv.Size && !inv[i].Equals(stack)) inv[i] = stack;
            }
        }

        public static void WriteMachine(BinaryWriter w, MachineInstance m)
        {
            Protocol.WritePos(w, m.Pos);
            w.Write((ushort)m.Def.NumericId);
            w.Write((byte)m.Facing);
            for (int i = 0; i < 6; i++) w.Write((byte)m.Sides[i]);
            w.Write((byte)m.Status);
            w.Write(m.CurrentRecipe != null ? m.CurrentRecipe.NumericId : -1);
            w.Write(m.Progress);
            w.Write(m.Activity);
            w.Write(m.Heat);
            w.Write(m.BurnLeft);
            w.Write(m.BurnTotal);
            w.Write(m.HasSource);
            w.Write(m.EnergyStored);
            w.Write(m.EnergyRate);
            w.Write(m.MeOnline);
            for (int i = 0; i < m.Filter.Length; i++) w.Write(m.Filter[i]);
            w.Write(m.FluidFilter);
            w.Write((ushort)m.Patterns.Count);
            foreach (var p in m.Patterns) w.Write(p);
            WriteInventory(w, m.Input);
            WriteInventory(w, m.Output);
            WriteInventory(w, m.Fuel);
            w.Write((byte)m.Tanks.Count);
            foreach (var t in m.Tanks)
            {
                w.Write((short)t.Fluid);
                w.Write(t.Amount);
            }
        }

        /// <summary>Применить снапшот машины к реплике фабрики.</summary>
        public static MachineInstance ReadMachine(BinaryReader r, Factory f)
        {
            var pos = Protocol.ReadPos(r);
            var def = f.Content.Machines.Get(r.ReadUInt16());
            var facing = (Direction)r.ReadByte();
            if (def == null) throw new InvalidDataException("unknown machine id");
            var m = f.EnsureMachine(def, pos, facing);
            bool sidesChanged = false;
            for (int i = 0; i < 6; i++)
            {
                var side = (SideType)r.ReadByte();
                if (m.Sides[i] != side)
                {
                    m.Sides[i] = side;
                    sidesChanged = true;
                }
            }
            m.Status = (MachineStatus)r.ReadByte();
            int recipe = r.ReadInt32();
            m.CurrentRecipe = recipe >= 0 ? f.Content.Recipes.Get(recipe) : null;
            m.Progress = r.ReadSingle();
            m.Activity = r.ReadSingle();
            m.Heat = r.ReadSingle();
            m.BurnLeft = r.ReadInt32();
            m.BurnTotal = r.ReadInt32();
            m.HasSource = r.ReadBoolean();
            m.EnergyStored = r.ReadSingle();
            m.EnergyRate = r.ReadSingle();
            m.MeOnline = r.ReadBoolean();
            for (int i = 0; i < m.Filter.Length; i++) m.Filter[i] = r.ReadInt32();
            m.FluidFilter = r.ReadInt32();
            int pc = r.ReadUInt16();
            m.Patterns.Clear();
            for (int i = 0; i < pc; i++) m.Patterns.Add(r.ReadInt32());
            ReadInventory(r, m.Input);
            ReadInventory(r, m.Output);
            ReadInventory(r, m.Fuel);
            int tanks = r.ReadByte();
            for (int i = 0; i < tanks; i++)
            {
                int fluid = r.ReadInt16();
                int amount = r.ReadInt32();
                if (i < m.Tanks.Count)
                {
                    m.Tanks[i].Fluid = fluid;
                    m.Tanks[i].Amount = amount;
                }
            }
            _ = sidesChanged;
            return m;
        }

        public static void WriteConveyor(BinaryWriter w, Conveyor c)
        {
            Protocol.WritePos(w, c.Pos);
            w.Write(c.Block.NumericId);
            w.Write((byte)c.Facing);
            w.Write((byte)c.Items.Count);
            foreach (var it in c.Items)
            {
                w.Write((ushort)it.Item);
                w.Write((byte)Math.Round(Math.Max(0, Math.Min(1, it.Pos)) * 250));
            }
        }

        public static Conveyor ReadConveyor(BinaryReader r, Factory f)
        {
            var pos = Protocol.ReadPos(r);
            var block = f.Content.Blocks.Get(r.ReadUInt16());
            var facing = (Direction)r.ReadByte();
            if (block == null) throw new InvalidDataException("unknown conveyor block");
            var c = f.EnsureConveyor(block, pos, facing);
            int n = r.ReadByte();
            c.Items.Clear();
            for (int i = 0; i < n; i++)
            {
                int item = r.ReadUInt16();
                float p = r.ReadByte() / 250f;
                c.Items.Add(new BeltItem { Item = item, Pos = p, PrevPos = p });
            }
            return c;
        }
    }
}
