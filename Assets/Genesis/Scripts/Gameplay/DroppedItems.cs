using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Gameplay
{
    /// <summary>Предмет, лежащий в мире (выпал из блока, врага, выброшен игроком).</summary>
    public sealed class DroppedItem
    {
        public int Id;
        public ItemStack Stack;
        public Vec3 Position;
        public Vec3 Velocity;
        /// <summary>Возраст в тиках (исчезает через DespawnTicks).</summary>
        public int Age;
        /// <summary>Сколько тиков нельзя подобрать (брошенный игроком предмет не возвращается сразу).</summary>
        public int PickupDelay;
        public bool OnGround;
        /// <summary>Кто выбросил — ему нельзя подобрать раньше, чем через ThrowerDelay.</summary>
        public int ThrownBy;
    }

    /// <summary>
    /// Предметы в мире: падают с гравитацией, отскакивают от блоков, скользят по льду, сливаются в стопки,
    /// притягиваются к игроку рядом и подбираются. Исчезают через 5 минут.
    /// </summary>
    public sealed class DroppedItems
    {
        public const int DespawnTicks = 20 * 60 * 5;
        public const float PickupRadius = 1.4f;
        public const float MagnetRadius = 4.5f;
        private const float Gravity = 22f;
        private const float Half = 0.125f;

        private readonly Dictionary<int, DroppedItem> _items = new Dictionary<int, DroppedItem>();
        private readonly List<int> _remove = new List<int>();
        private readonly VoxelWorld _world;
        private readonly ContentDatabase _content;
        private Random64 _rng = new Random64(0x17E35);
        private int _next = 1;

        /// <summary>Предметы, которые не исчезают со временем (например, заполненная ячейка ME).</summary>
        public Func<ItemStack, bool> NeverDespawn;

        /// <summary>Игрок подобрал: кто, что.</summary>
        public event Action<PlayerState, ItemStack> PickedUp;

        public DroppedItems(VoxelWorld world, ContentDatabase content)
        {
            _world = world;
            _content = content;
        }

        public IEnumerable<DroppedItem> All => _items.Values;
        public int Count => _items.Count;
        public DroppedItem Get(int id) => _items.TryGetValue(id, out var d) ? d : null;

        /// <summary>Положить предмет в мир. scatter — случайный разлёт (как у выпавших из блока).</summary>
        public DroppedItem Spawn(ItemStack stack, Vec3 pos, Vec3 velocity, int pickupDelay = 8, bool scatter = false)
        {
            if (stack.IsEmpty) return null;
            if (scatter)
            {
                velocity = velocity + new Vec3((_rng.NextFloat() - 0.5f) * 2.4f, 2.5f + _rng.NextFloat() * 1.5f, (_rng.NextFloat() - 0.5f) * 2.4f);
            }
            var d = new DroppedItem { Id = _next++, Stack = stack, Position = pos, Velocity = velocity, PickupDelay = pickupDelay };
            _items[d.Id] = d;
            return d;
        }

        public void Remove(int id) => _items.Remove(id);

        public void Clear() => _items.Clear();

        // ============================================================ Тик

        public void Tick(IEnumerable<PlayerState> players, long tick)
        {
            const float dt = GameConstants.TickDuration;
            _remove.Clear();
            foreach (var d in _items.Values)
            {
                d.Age++;
                if (d.Age > DespawnTicks - 20 && NeverDespawn != null && NeverDespawn(d.Stack)) d.Age = 0;
                if (d.PickupDelay > 0) d.PickupDelay--;
                if (d.Age > DespawnTicks || d.Stack.IsEmpty || d.Position.Y < WorldConstants.MinY - 8)
                {
                    _remove.Add(d.Id);
                    continue;
                }
                var cell = d.Position.Floor();
                if (!_world.IsLoaded(cell.X, cell.Y, cell.Z)) continue; // чанк выгружен — предмет «заморожен»

                // Притяжение к ближайшему игроку
                PlayerState target = null;
                float best = MagnetRadius * MagnetRadius;
                foreach (var p in players)
                {
                    if (p.Health <= 0) continue;
                    if (d.PickupDelay > 0) continue;
                    var c = new Vec3(p.Position.X, p.Position.Y + 0.6f, p.Position.Z);
                    float dsq = (c - d.Position).LengthSquared;
                    if (dsq < best)
                    {
                        best = dsq;
                        target = p;
                    }
                }
                if (target != null)
                {
                    var c = new Vec3(target.Position.X, target.Position.Y + 0.6f, target.Position.Z);
                    var to = c - d.Position;
                    if (to.LengthSquared < PickupRadius * PickupRadius)
                    {
                        int put = target.Inventory.Insert(d.Stack);
                        if (put > 0)
                        {
                            var picked = d.Stack.WithCount(put);
                            d.Stack = d.Stack.WithCount(d.Stack.Count - put);
                            PickedUp?.Invoke(target, picked);
                            if (d.Stack.IsEmpty)
                            {
                                _remove.Add(d.Id);
                                continue;
                            }
                        }
                    }
                    else if (target.Inventory.Insert(d.Stack, true) > 0)
                    {
                        var pull = to.Normalized * 9f;
                        d.Velocity = d.Velocity + (pull - d.Velocity) * 0.25f;
                        d.OnGround = false;
                    }
                }

                Move(d, dt);
            }

            // Слияние одинаковых рядом (раз в полсекунды)
            if (tick % 10 == 0) Merge();
            foreach (var id in _remove) _items.Remove(id);
        }

        private void Move(DroppedItem d, float dt)
        {
            var v = d.Velocity;
            if (InWater(d.Position)) v = new Vec3(v.X * 0.9f, Math.Min(v.Y + 6f * dt, 1.2f), v.Z * 0.9f); // всплывает
            else v.Y -= Gravity * dt;

            var p = d.Position;
            // Внутри блока (выпал из сломанного или завален) — выталкиваем вверх
            if (Solid(p.X, p.Y, p.Z))
            {
                p.Y = (float)Math.Floor(p.Y) + 1f + Half;
                v.Y = Math.Max(v.Y, 0);
            }

            float nx = p.X + v.X * dt;
            if (Solid(nx + Math.Sign(v.X) * Half, p.Y, p.Z)) { v.X = -v.X * 0.2f; nx = p.X; }
            float nz = p.Z + v.Z * dt;
            if (Solid(nx, p.Y, nz + Math.Sign(v.Z) * Half)) { v.Z = -v.Z * 0.2f; nz = p.Z; }
            float ny = p.Y + v.Y * dt;
            d.OnGround = false;
            if (v.Y <= 0 && Solid(nx, ny - Half, nz))
            {
                ny = (float)Math.Floor(ny - Half) + 1f + Half;
                v.Y = v.Y < -4f ? -v.Y * 0.25f : 0f; // небольшой отскок
                d.OnGround = v.Y == 0;
                var below = _world.GetBlockDef(new Int3((int)Math.Floor(nx), (int)Math.Floor(ny - Half - 0.01f), (int)Math.Floor(nz)));
                float friction = below.Id == "genesis:ice" ? 0.98f : 0.6f;
                v.X *= friction;
                v.Z *= friction;
            }
            else if (v.Y > 0 && Solid(nx, ny + Half, nz))
            {
                v.Y = 0;
                ny = p.Y;
            }
            d.Position = new Vec3(nx, ny, nz);
            d.Velocity = v;
        }

        private bool Solid(float x, float y, float z)
        {
            var c = new Int3((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
            if (!_world.IsLoaded(c.X, c.Y, c.Z)) return true;
            return _world.GetBlockDef(c).Solid;
        }

        private bool InWater(Vec3 p)
        {
            var c = p.Floor();
            return _world.IsLoaded(c.X, c.Y, c.Z) && _world.GetBlockDef(c).Liquid;
        }

        private void Merge()
        {
            var list = new List<DroppedItem>(_items.Values);
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.Stack.IsEmpty || _remove.Contains(a.Id)) continue;
                int max = _content.Item(a.Stack.Item)?.MaxStack ?? GameConstants.MaxStackSize;
                for (int j = i + 1; j < list.Count; j++)
                {
                    var b = list[j];
                    if (b.Stack.IsEmpty || _remove.Contains(b.Id) || !a.Stack.SameKind(b.Stack)) continue;
                    if ((a.Position - b.Position).LengthSquared > 0.8f * 0.8f) continue;
                    int move = Math.Min(max - a.Stack.Count, b.Stack.Count);
                    if (move <= 0) continue;
                    a.Stack = a.Stack.WithCount(a.Stack.Count + move);
                    b.Stack = b.Stack.WithCount(b.Stack.Count - move);
                    a.Age = Math.Min(a.Age, b.Age);
                    if (b.Stack.IsEmpty) _remove.Add(b.Id);
                }
            }
        }

        // ============================================================ Сохранение

        public void Write(BinaryWriter w)
        {
            w.Write(1);
            w.Write(_items.Count);
            foreach (var d in _items.Values)
            {
                w.Write(_content.Items.IdOf(d.Stack.Item) ?? "");
                w.Write(d.Stack.Count);
                w.Write(d.Stack.Meta);
                w.Write(d.Position.X);
                w.Write(d.Position.Y);
                w.Write(d.Position.Z);
                w.Write(d.Age);
            }
        }

        public void Read(BinaryReader r)
        {
            r.ReadInt32();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                int count = r.ReadInt32(), meta = r.ReadInt32();
                var pos = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int age = r.ReadInt32();
                int item = _content.Items.IndexOf(id);
                if (item < 0) continue;
                var d = Spawn(new ItemStack(item, count, meta), pos, Vec3.Zero, 0);
                d.Age = age;
            }
        }
    }
}
