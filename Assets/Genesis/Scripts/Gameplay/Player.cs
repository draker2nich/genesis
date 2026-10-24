using System;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Gameplay
{
    /// <summary>Состояние игрока (на сервере — авторитетное; клиент двигает себя сам и сообщает позицию).</summary>
    public sealed class PlayerState
    {
        public const int HotbarSize = 9;
        public const int InventorySize = 36; // 9 хотбар + 27 основных; рюкзаки — позже

        public readonly int Id;
        public string Name;
        public Vec3 Position;     // ступни
        public Vec3 Velocity;
        public float Yaw;         // градусы, 0 = +Z
        public float Pitch;
        public bool OnGround;
        public bool Flying;
        /// <summary>Скорость в момент приземления (для урона от падения), 0 — не приземлялся в этом шаге.</summary>
        public float LandingSpeed;
        public int SelectedSlot;
        /// <summary>Креатив разрешён этому игроку (настройки мира; выставляет сервер при входе).</summary>
        public bool Creative;
        /// <summary>Секрет клиента, которым игрок «владеет» своим сохранением на сервере.</summary>
        public string Token = "";
        public readonly Inventory Inventory;
        /// <summary>Сетка крафта 3×3 (в инвентаре используется левый верхний угол 2×2). Индекс = y·3 + x.</summary>
        public readonly Inventory CraftGrid;
        /// <summary>Предмет «в руке курсора» при работе с окнами (как в Minecraft).</summary>
        public readonly Inventory Cursor;
        /// <summary>Текущий размер сетки: 2 — инвентарь, 3 — верстак.</summary>
        public int GridSize = 2;
        public Int3 GridStation;
        /// <summary>Мастерство: сколько раз выполнен каждый рецепт сетки (id → число).</summary>
        public readonly System.Collections.Generic.Dictionary<string, int> Mastery = new System.Collections.Generic.Dictionary<string, int>();

        // ---- выживание
        public const float MaxHealth = 20f;
        public const float MaxHunger = 20f;
        public float Health = MaxHealth;
        public float Hunger = MaxHunger;
        /// <summary>Растёт, когда сервер принудительно переносит игрока (возрождение) — клиент подхватывает позицию.</summary>
        public int TeleportVersion;
        /// <summary>Открытое окно ME (точка доступа: терминал или контроллер) — хост шлёт снимки сети.</summary>
        public Int3? MeView;
        /// <summary>Последнее время урона (для вспышки на экране), в тиках сервера.</summary>
        public long LastDamageTick = -1000;

        /// <summary>Яд: осталось тиков (урон раз в 30 тиков).</summary>
        public int PoisonTicks;
        /// <summary>Тик, раньше которого сервер не примет следующий удар (не сохраняется).</summary>
        public long NextAttackTick;
        /// <summary>Отбрасывание от удара: клиент применяет, когда меняется версия.</summary>
        public Vec3 Knockback;
        public int KnockbackVersion;

        // серверные таймеры (тики)
        internal int HungerTimer, RegenTimer, StarveTimer;

        public PlayerState(int id, string name, ContentDatabase content)
        {
            Id = id;
            Name = name;
            Inventory = new Inventory(content, InventorySize);
            CraftGrid = new Inventory(content, 9);
            Cursor = new Inventory(content, 1);
        }

        public Vec3 EyePosition => new Vec3(Position.X, Position.Y + PlayerPhysics.EyeHeight, Position.Z);

        public Vec3 LookDirection
        {
            get
            {
                double yaw = Yaw * Math.PI / 180.0, pitch = Pitch * Math.PI / 180.0;
                return new Vec3(
                    (float)(Math.Sin(yaw) * Math.Cos(pitch)),
                    (float)(-Math.Sin(pitch)),
                    (float)(Math.Cos(yaw) * Math.Cos(pitch)));
            }
        }

        public ItemStack SelectedStack => Inventory[SelectedSlot];

        public void Write(BinaryWriter w)
        {
            w.Write(Name ?? "");
            w.Write(Position.X);
            w.Write(Position.Y);
            w.Write(Position.Z);
            w.Write(Yaw);
            w.Write(Pitch);
            w.Write(Flying);
            w.Write(SelectedSlot);
            Inventory.Write(w);
            w.Write(Health);
            w.Write(Hunger);
            // v3: сетка крафта, курсор, мастерство
            CraftGrid.Write(w);
            Cursor.Write(w);
            w.Write(Mastery.Count);
            foreach (var kv in Mastery)
            {
                w.Write(kv.Key);
                w.Write(kv.Value);
            }
            // v4
            w.Write(Token ?? "");
        }

        /// <summary>Уровень мастерства рецепта: 0..3 (16, 64 и 256 крафтов).</summary>
        public int MasteryLevel(string craftId)
        {
            Mastery.TryGetValue(craftId, out int n);
            return n >= 256 ? 3 : (n >= 64 ? 2 : (n >= 16 ? 1 : 0));
        }

        /// <param name="version">1 — без здоровья/голода (сохранения этапов 0–2).</param>
        public void Read(BinaryReader r, int version)
        {
            Name = r.ReadString();
            Position = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            Yaw = r.ReadSingle();
            Pitch = r.ReadSingle();
            Flying = r.ReadBoolean();
            SelectedSlot = r.ReadInt32();
            Inventory.Read(r);
            if (version >= 2)
            {
                Health = r.ReadSingle();
                Hunger = r.ReadSingle();
            }
            if (version >= 3)
            {
                CraftGrid.Read(r);
                Cursor.Read(r);
                int n = r.ReadInt32();
                Mastery.Clear();
                for (int i = 0; i < n; i++) Mastery[r.ReadString()] = r.ReadInt32();
            }
            if (version >= 4) Token = r.ReadString();
        }
    }

    public struct MoveInput
    {
        public float Forward;  // -1..1
        public float Strafe;   // -1..1
        public bool Jump;
        public bool Sprint;
        public bool Descend;   // в полёте
    }

    /// <summary>Кинематическая AABB-физика игрока против вокселей. Без Unity-физики.</summary>
    public static class PlayerPhysics
    {
        public const float HalfWidth = 0.3f;
        public const float Height = 1.8f;
        public const float EyeHeight = 1.62f;
        public const float Gravity = 28f;
        public const float JumpVelocity = 8.6f;
        public const float WalkSpeed = 4.3f;
        public const float SprintSpeed = 6.2f;
        public const float FlySpeed = 14f;
        public const float FlySprintSpeed = 40f;
        private const float Epsilon = 0.001f;

        public static bool IsSolid(VoxelWorld world, int x, int y, int z)
        {
            if (y > WorldConstants.MaxY) return false;
            if (!world.IsLoaded(x, y, z)) return WorldConstants.InHorizontalBounds(x, z) && y >= WorldConstants.MinY;
            return world.Content.Block(world.GetBlock(x, y, z)).Solid;
        }

        public static void Step(VoxelWorld world, PlayerState p, MoveInput input, float dt)
        {
            double yaw = p.Yaw * Math.PI / 180.0;
            float fx = (float)Math.Sin(yaw), fz = (float)Math.Cos(yaw);
            float rx = fz, rz = -fx;

            float wishX = fx * input.Forward + rx * input.Strafe;
            float wishZ = fz * input.Forward + rz * input.Strafe;
            float len = (float)Math.Sqrt(wishX * wishX + wishZ * wishZ);
            if (len > 1f)
            {
                wishX /= len;
                wishZ /= len;
            }

            var v = p.Velocity;
            if (p.Flying)
            {
                float speed = input.Sprint ? FlySprintSpeed : FlySpeed;
                v.X = wishX * speed;
                v.Z = wishZ * speed;
                v.Y = (input.Jump ? speed * 0.6f : 0) - (input.Descend ? speed * 0.6f : 0);
            }
            else
            {
                float speed = input.Sprint ? SprintSpeed : WalkSpeed;
                float accel = p.OnGround ? 14f : 3f;
                v.X += (wishX * speed - v.X) * Math.Min(1f, accel * dt);
                v.Z += (wishZ * speed - v.Z) * Math.Min(1f, accel * dt);
                v.Y -= Gravity * dt;
                if (v.Y < -60f) v.Y = -60f;
                if (input.Jump && p.OnGround) v.Y = JumpVelocity;
            }

            var pos = p.Position;
            p.LandingSpeed = 0;
            float vyBefore = v.Y;
            MoveBody(world, ref pos, ref v, HalfWidth, Height, dt, out bool onGround, out _);
            if (onGround && !p.OnGround && !p.Flying) p.LandingSpeed = -vyBefore;
            p.OnGround = onGround;

            // Жёсткая граница мира
            float lim = WorldConstants.HalfWorld - 1f;
            pos.X = Math.Max(-lim, Math.Min(lim, pos.X));
            pos.Z = Math.Max(-lim, Math.Min(lim, pos.Z));
            if (pos.Y < WorldConstants.MinY - 10) pos.Y = WorldConstants.MinY + 2;

            p.Position = pos;
            p.Velocity = v;
        }

        /// <summary>
        /// Перемещение любого тела (игрок, враг) с коллизиями: сначала Y, потом X и Z.
        /// Обнуляет скорость по осям столкновения.
        /// </summary>
        public static void MoveBody(VoxelWorld world, ref Vec3 pos, ref Vec3 v, float halfWidth, float height, float dt,
            out bool onGround, out bool hitWall)
        {
            pos = MoveAxis(world, pos, 1, v.Y * dt, halfWidth, height, out bool hitY);
            onGround = hitY && v.Y < 0;
            if (hitY) v.Y = 0;
            pos = MoveAxis(world, pos, 0, v.X * dt, halfWidth, height, out bool hitX);
            if (hitX) v.X = 0;
            pos = MoveAxis(world, pos, 2, v.Z * dt, halfWidth, height, out bool hitZ);
            if (hitZ) v.Z = 0;
            hitWall = hitX || hitZ;
        }

        private static Vec3 MoveAxis(VoxelWorld world, Vec3 pos, int axis, float delta, float HalfWidth, float Height, out bool hit)
        {
            hit = false;
            if (Math.Abs(delta) < 1e-7f) return pos;

            // Дробим большие перемещения, чтобы не пролетать сквозь блоки
            int steps = (int)Math.Ceiling(Math.Abs(delta) / 0.45f);
            float d = delta / steps;
            for (int s = 0; s < steps; s++)
            {
                var next = pos;
                if (axis == 0) next.X += d;
                else if (axis == 1) next.Y += d;
                else next.Z += d;

                var box = Aabb.FromFeet(next, HalfWidth, Height);
                if (!Collides(world, box))
                {
                    pos = next;
                    continue;
                }

                hit = true;
                // Прижимаемся к грани блока
                if (axis == 0)
                    pos.X = d > 0 ? (float)Math.Floor(next.X + HalfWidth) - HalfWidth - Epsilon
                                  : (float)Math.Floor(next.X - HalfWidth) + 1 + HalfWidth + Epsilon;
                else if (axis == 1)
                    pos.Y = d > 0 ? (float)Math.Floor(next.Y + Height) - Height - Epsilon
                                  : (float)Math.Floor(next.Y) + 1 + Epsilon;
                else
                    pos.Z = d > 0 ? (float)Math.Floor(next.Z + HalfWidth) - HalfWidth - Epsilon
                                  : (float)Math.Floor(next.Z - HalfWidth) + 1 + HalfWidth + Epsilon;
                // Если «прижатое» положение тоже пересекается (угловой случай) — остаёмся на месте
                if (Collides(world, Aabb.FromFeet(pos, HalfWidth, Height)))
                {
                    if (axis == 0) pos.X = next.X - d;
                    else if (axis == 1) pos.Y = next.Y - d;
                    else pos.Z = next.Z - d;
                }
                break;
            }
            return pos;
        }

        public static bool Collides(VoxelWorld world, Aabb box)
        {
            int x0 = (int)Math.Floor(box.Min.X), x1 = (int)Math.Floor(box.Max.X - 1e-5f);
            int y0 = (int)Math.Floor(box.Min.Y), y1 = (int)Math.Floor(box.Max.Y - 1e-5f);
            int z0 = (int)Math.Floor(box.Min.Z), z1 = (int)Math.Floor(box.Max.Z - 1e-5f);
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                if (IsSolid(world, x, y, z)) return true;
            return false;
        }

        public static bool IntersectsBlock(PlayerState p, Int3 block) =>
            Aabb.FromFeet(p.Position, HalfWidth, Height).Intersects(Aabb.Block(block));
    }
}
