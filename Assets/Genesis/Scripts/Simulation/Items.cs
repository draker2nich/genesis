using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Data;

namespace Genesis.Simulation
{
    /// <summary>Стак предметов. Item — числовой ID из ContentDatabase.Items.</summary>
    [Serializable]
    public struct ItemStack : IEquatable<ItemStack>
    {
        public int Item;
        public int Count;
        /// <summary>Доп. данные стака: для инструментов — износ. Стаки с разным Meta не объединяются.</summary>
        public int Meta;

        public ItemStack(int item, int count, int meta = 0)
        {
            Item = item;
            Count = count;
            Meta = meta;
        }

        public static readonly ItemStack Empty = new ItemStack(-1, 0);

        public bool IsEmpty => Count <= 0 || Item < 0;

        public ItemStack WithCount(int count) => count <= 0 ? Empty : new ItemStack(Item, count, Meta);

        public bool SameKind(ItemStack o) => Item == o.Item && Meta == o.Meta;

        public bool Equals(ItemStack o) => (IsEmpty && o.IsEmpty) || (Item == o.Item && Count == o.Count && Meta == o.Meta);
        public override bool Equals(object obj) => obj is ItemStack s && Equals(s);
        public override int GetHashCode() => IsEmpty ? 0 : Item * 397 ^ Count;
        public override string ToString() => IsEmpty ? "empty" : $"{Count}x#{Item}";
    }

    /// <summary>Инвентарь из слотов. Стак ограничен MaxStack предмета (по умолчанию 128).</summary>
    public sealed class Inventory
    {
        private readonly ItemStack[] _slots;
        private readonly ContentDatabase _content;

        public event Action Changed;

        public Inventory(ContentDatabase content, int size)
        {
            _content = content;
            _slots = new ItemStack[size];
            for (int i = 0; i < size; i++) _slots[i] = ItemStack.Empty;
        }

        public int Size => _slots.Length;

        public ItemStack this[int slot]
        {
            get => _slots[slot];
            set
            {
                _slots[slot] = value.IsEmpty ? ItemStack.Empty : value;
                Changed?.Invoke();
            }
        }

        public int MaxStackOf(int item)
        {
            var def = _content.Item(item);
            return def != null ? def.MaxStack : Genesis.Core.GameConstants.MaxStackSize;
        }

        public bool IsEmpty
        {
            get
            {
                foreach (var s in _slots)
                    if (!s.IsEmpty) return false;
                return true;
            }
        }

        public int CountOf(int item)
        {
            int n = 0;
            foreach (var s in _slots)
                if (!s.IsEmpty && s.Item == item) n += s.Count;
            return n;
        }

        /// <summary>Сколько предметов item в стаках, которые разрешает allow.</summary>
        public int CountOf(int item, Func<ItemStack, bool> allow)
        {
            int n = 0;
            foreach (var s in _slots)
                if (!s.IsEmpty && s.Item == item && (allow == null || allow(s))) n += s.Count;
            return n;
        }

        /// <summary>
        /// Изъять count штук item: сначала из стаков без Meta (новые инструменты, пустые ячейки не трогаем
        /// раньше обычных), потом из остальных, которые разрешает allow. Возвращает сколько изъято.
        /// </summary>
        public int Extract(int item, int count, Func<ItemStack, bool> allow)
        {
            int remaining = count;
            for (int pass = 0; pass < 2 && remaining > 0; pass++)
                for (int i = _slots.Length - 1; i >= 0 && remaining > 0; i--)
                {
                    var s = _slots[i];
                    if (s.IsEmpty || s.Item != item) continue;
                    if (pass == 0 && s.Meta != 0) continue;
                    if (pass == 1 && (s.Meta == 0 || (allow != null && !allow(s)))) continue;
                    int take = Math.Min(s.Count, remaining);
                    _slots[i] = s.WithCount(s.Count - take);
                    remaining -= take;
                }
            int extracted = count - remaining;
            if (extracted > 0) Changed?.Invoke();
            return extracted;
        }

        /// <summary>Снимок слотов (для отката операции целиком, с сохранением Meta).</summary>
        public ItemStack[] Snapshot()
        {
            var copy = new ItemStack[_slots.Length];
            Array.Copy(_slots, copy, _slots.Length);
            return copy;
        }

        public void Restore(ItemStack[] snapshot)
        {
            Array.Copy(snapshot, _slots, Math.Min(snapshot.Length, _slots.Length));
            Changed?.Invoke();
        }

        /// <summary>Вставить стак. Возвращает сколько вставлено.</summary>
        public int Insert(ItemStack stack, bool simulate = false)
        {
            if (stack.IsEmpty) return 0;
            int max = MaxStackOf(stack.Item);
            int remaining = stack.Count;

            // Сначала дополняем существующие стаки
            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                var s = _slots[i];
                if (s.IsEmpty || !s.SameKind(stack) || s.Count >= max) continue;
                int add = Math.Min(max - s.Count, remaining);
                if (!simulate) _slots[i] = new ItemStack(s.Item, s.Count + add, s.Meta);
                remaining -= add;
            }
            // Потом пустые слоты
            for (int i = 0; i < _slots.Length && remaining > 0; i++)
            {
                if (!_slots[i].IsEmpty) continue;
                int add = Math.Min(max, remaining);
                if (!simulate) _slots[i] = new ItemStack(stack.Item, add, stack.Meta);
                remaining -= add;
            }

            int inserted = stack.Count - remaining;
            if (!simulate && inserted > 0) Changed?.Invoke();
            return inserted;
        }

        /// <summary>Вставить в конкретный слот. Возвращает сколько вставлено.</summary>
        public int InsertIntoSlot(int slot, ItemStack stack, bool simulate = false)
        {
            if (stack.IsEmpty) return 0;
            var s = _slots[slot];
            int max = MaxStackOf(stack.Item);
            if (!s.IsEmpty && !s.SameKind(stack)) return 0;
            int have = s.IsEmpty ? 0 : s.Count;
            int add = Math.Min(max - have, stack.Count);
            if (add <= 0) return 0;
            if (!simulate)
            {
                _slots[slot] = new ItemStack(stack.Item, have + add, stack.Meta);
                Changed?.Invoke();
            }
            return add;
        }

        /// <summary>Проверить, поместятся ли все стаки разом.</summary>
        public bool CanInsertAll(IReadOnlyList<ItemStack> stacks)
        {
            if (stacks.Count == 0) return true;
            // Моделируем на копии
            var copy = new ItemStack[_slots.Length];
            Array.Copy(_slots, copy, _slots.Length);
            foreach (var st in stacks)
            {
                if (st.IsEmpty) continue;
                int max = MaxStackOf(st.Item);
                int remaining = st.Count;
                for (int i = 0; i < copy.Length && remaining > 0; i++)
                {
                    if (copy[i].IsEmpty || !copy[i].SameKind(st) || copy[i].Count >= max) continue;
                    int add = Math.Min(max - copy[i].Count, remaining);
                    copy[i] = new ItemStack(st.Item, copy[i].Count + add, st.Meta);
                    remaining -= add;
                }
                for (int i = 0; i < copy.Length && remaining > 0; i++)
                {
                    if (!copy[i].IsEmpty) continue;
                    int add = Math.Min(max, remaining);
                    copy[i] = new ItemStack(st.Item, add, st.Meta);
                    remaining -= add;
                }
                if (remaining > 0) return false;
            }
            return true;
        }

        /// <summary>Изъять count предметов item из любых слотов. Возвращает сколько изъято.</summary>
        public int Extract(int item, int count, bool simulate = false)
        {
            int remaining = count;
            for (int i = _slots.Length - 1; i >= 0 && remaining > 0; i--)
            {
                var s = _slots[i];
                if (s.IsEmpty || s.Item != item) continue;
                int take = Math.Min(s.Count, remaining);
                if (!simulate) _slots[i] = s.WithCount(s.Count - take);
                remaining -= take;
            }
            int extracted = count - remaining;
            if (!simulate && extracted > 0) Changed?.Invoke();
            return extracted;
        }

        /// <summary>Изъять из слота до count штук.</summary>
        public ItemStack ExtractFromSlot(int slot, int count, bool simulate = false)
        {
            var s = _slots[slot];
            if (s.IsEmpty || count <= 0) return ItemStack.Empty;
            int take = Math.Min(count, s.Count);
            if (!simulate)
            {
                _slots[slot] = s.WithCount(s.Count - take);
                Changed?.Invoke();
            }
            return new ItemStack(s.Item, take, s.Meta);
        }

        /// <summary>Первый непустой слот (для выталкивания).</summary>
        public int FirstNonEmpty()
        {
            for (int i = 0; i < _slots.Length; i++)
                if (!_slots[i].IsEmpty) return i;
            return -1;
        }

        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = ItemStack.Empty;
            Changed?.Invoke();
        }

        public IEnumerable<ItemStack> NonEmpty()
        {
            foreach (var s in _slots)
                if (!s.IsEmpty) yield return s;
        }

        // ------------------------------------------------------------ Сериализация (строковые ID)

        public void Write(BinaryWriter w)
        {
            // Отрицательный размер = формат v2 (с Meta). Старые сохранения (положительный размер) читаются.
            w.Write(-_slots.Length - 1);
            foreach (var s in _slots)
            {
                if (s.IsEmpty)
                {
                    w.Write("");
                    continue;
                }
                w.Write(_content.Items.IdOf(s.Item) ?? "");
                w.Write(s.Count);
                w.Write(s.Meta);
            }
        }

        public void Read(BinaryReader r)
        {
            int n = r.ReadInt32();
            bool withMeta = n < 0;
            if (withMeta) n = -n - 1;
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                if (id.Length == 0)
                {
                    if (i < _slots.Length) _slots[i] = ItemStack.Empty;
                    continue;
                }
                int count = r.ReadInt32();
                int meta = withMeta ? r.ReadInt32() : 0;
                int item = _content.Items.IndexOf(id);
                if (i < _slots.Length) _slots[i] = item < 0 ? ItemStack.Empty : new ItemStack(item, count, meta);
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Бак для жидкости/газа.</summary>
    public sealed class FluidTank
    {
        public readonly string Name;
        public readonly int Capacity;
        /// <summary>-1 — любая жидкость.</summary>
        public readonly int Filter;
        public readonly bool IsOutput;
        /// <summary>Бак-хранилище: и принимает, и отдаёт (направление задают стороны fluid_in/fluid_out).</summary>
        public readonly bool IsBuffer;

        public int Fluid = -1;
        public int Amount;

        /// <summary>Можно ли заливать в бак снаружи.</summary>
        public bool AcceptsFill => !IsOutput || IsBuffer;

        public FluidTank(string name, int capacity, int filter, bool isOutput, bool isBuffer = false)
        {
            Name = name;
            Capacity = capacity;
            Filter = filter;
            IsOutput = isOutput;
            IsBuffer = isBuffer;
            if (filter >= 0) Fluid = filter;
        }

        public int Space => Capacity - Amount;
        public bool IsEmpty => Amount <= 0;

        public bool Accepts(int fluid) =>
            fluid >= 0 && (Filter < 0 || Filter == fluid) && (Amount == 0 || Fluid == fluid);

        public int Fill(int fluid, int amount, bool simulate = false)
        {
            if (amount <= 0 || !Accepts(fluid)) return 0;
            int add = Math.Min(amount, Space);
            if (!simulate && add > 0)
            {
                Fluid = fluid;
                Amount += add;
            }
            return add;
        }

        public int Drain(int amount, bool simulate = false)
        {
            int take = Math.Min(amount, Amount);
            if (!simulate && take > 0)
            {
                Amount -= take;
                if (Amount == 0 && Filter < 0) Fluid = -1;
            }
            return take;
        }
    }
}
