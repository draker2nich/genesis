using System;
using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Simulation;
using Genesis.World;

namespace Genesis.Gameplay
{
    /// <summary>Состояние существа (для анимаций на клиенте и логики).</summary>
    public enum CreatureState : byte
    {
        Normal = 0,
        Attack = 1,   // удар
        Windup = 2,   // замах перед особой атакой — видно заранее, можно увернуться
        Special = 3,  // прыжок / рывок / выброс спор
        Flee = 4,
        Curled = 5,   // спрятался в панцирь
        Stunned = 6   // оглушён (врезался в стену после рывка) — получает двойной урон
    }

    /// <summary>Существо мира: враг или мирное животное (имя класса историческое).</summary>
    public sealed class Enemy
    {
        public int Id;
        public EnemyDef Def;
        public Vec3 Position;
        public Vec3 Velocity;
        public Vec3 Home;          // точка появления: пещерные и руинные враги держатся «поводка»
        public float Yaw;
        public float Health;
        public bool OnGround;
        public int AttackCooldown;
        public int SpecialCooldown;
        public int WanderTimer;
        public float WanderX, WanderZ;
        public int TargetPlayer = -1;
        public int HurtTicks;      // для вспышки на клиенте

        public CreatureState State;
        public int StateTicks;
        public Vec3 ChargeDir;
        public bool LeapHit;
        public int FleeTicks;
        public Vec3 FleeFrom;
        public int AlertTicks;     // встревожен стаей — видит дальше
        public bool Provoked;      // нейтральное существо ранили
        public int LoveTicks;      // накормлено — ищет пару
        public int BreedCooldown;
        public int LayTimer;
        /// <summary>Домашнее животное (кормили или родилось на ферме): не исчезает вдали от игроков и сохраняется.</summary>
        public bool Domestic;
    }

    /// <summary>Снаряд существа (кислотный плевок).</summary>
    public sealed class Projectile
    {
        public int Id;
        public Vec3 Position, Velocity;
        public float Damage;
        public int Poison;
        public int Life = 100;
    }

    /// <summary>Облако спор: травит игроков внутри.</summary>
    public sealed class SporeCloud
    {
        public int Id;
        public Vec3 Position;
        public float Radius = 3f;
        public int Ticks = 100;
    }

    /// <summary>
    /// Существа: мирные животные на поверхности (еда, материалы, приручение кормом и разведение) и враги —
    /// в пещерах, руинах и на поверхности ночью. Враги не идут к базе: пещерные и руинные держатся «поводка»
    /// вокруг места появления. Особые атаки: прыжок, плевок кислотой, рывок с оглушением, облако спор;
    /// у некоторых — броня, стайность, бегство при ранении, панцирь.
    /// </summary>
    public sealed class EnemyManager
    {
        public const int MaxHostilePerPlayer = 6;
        public const int MaxPassivePerPlayer = 10;
        public const float LeashRadius = 20f;
        /// <summary>Поводок ночных существ поверхности: дальше не преследуют, возвращаются.</summary>
        public const float SurfaceLeashRadius = 32f;
        /// <summary>Свет, который считается «рукотворным» (факел, лампа): возле него враги не появляются и не заходят.</summary>
        public const int BaseLightLevel = 10;
        public const float DespawnRadius = 72f;
        public const float PassiveDespawnRadius = 110f;
        public const float AttackRange = 1.6f;

        private readonly GameServer _server;
        private readonly List<Enemy> _enemies = new List<Enemy>();
        private readonly List<Projectile> _projectiles = new List<Projectile>();
        private readonly List<SporeCloud> _clouds = new List<SporeCloud>();
        private int _nextId = 1;
        private Random64 _rng;
        private long _tick;

        public IReadOnlyList<Enemy> All => _enemies;
        public IReadOnlyList<Projectile> Projectiles => _projectiles;
        public IReadOnlyList<SporeCloud> Clouds => _clouds;

        /// <summary>Можно отключить (мирные тесты); мирного режима в игре нет.</summary>
        public bool SpawningEnabled = true;

        // ---- «Безопасные зоны»: чанки с машинами или яркими источниками света (база игрока)
        private readonly Dictionary<Int3, int> _machinesInChunk = new Dictionary<Int3, int>();
        private readonly Dictionary<Int3, int> _lightsInChunk = new Dictionary<Int3, int>();

        public EnemyManager(GameServer server)
        {
            _server = server;
            _rng = new Random64(Hash.SplitMix((ulong)server.Seed ^ 0xE1E1E1UL));
            server.Factory.MachineAdded += m => Count(_machinesInChunk, WorldConstants.ChunkOf(m.Pos), +1);
            server.Factory.MachineRemoved += pos => Count(_machinesInChunk, WorldConstants.ChunkOf(pos), -1);
            server.World.ChunkLoaded += OnChunkLoaded;
            server.World.ChunkUnloaded += c => _lightsInChunk.Remove(c);
            server.World.BlockChanged += OnBlockChanged;
        }

        private static void Count(Dictionary<Int3, int> map, Int3 key, int delta)
        {
            int n = (map.TryGetValue(key, out int v) ? v : 0) + delta;
            if (n <= 0) map.Remove(key);
            else map[key] = n;
        }

        private bool IsBaseLight(ushort block) => _server.Content.Block(block).Light >= BaseLightLevel;

        private void OnChunkLoaded(Chunk chunk)
        {
            var data = new ushort[WorldConstants.ChunkVolume];
            chunk.CopyTo(data);
            int n = 0;
            ushort last = ushort.MaxValue;
            bool lastLit = false;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != last)
                {
                    last = data[i];
                    lastLit = IsBaseLight(last);
                }
                if (lastLit) n++;
            }
            if (n > 0) _lightsInChunk[chunk.Coord] = n;
            else _lightsInChunk.Remove(chunk.Coord);
        }

        private void OnBlockChanged(Int3 pos, ushort oldBlock, ushort newBlock)
        {
            bool was = IsBaseLight(oldBlock), now = IsBaseLight(newBlock);
            if (was == now) return;
            Count(_lightsInChunk, WorldConstants.ChunkOf(pos), now ? +1 : -1);
        }

        /// <summary>
        /// Точка у базы игрока: в этом или соседнем чанке стоят машины или горят факелы/лампы.
        /// Враги здесь не появляются, не заходят сюда и не преследуют игрока внутри (ТЗ: «не атакуют базу»).
        /// </summary>
        public bool IsSafeZone(Vec3 p)
        {
            if (_machinesInChunk.Count == 0 && _lightsInChunk.Count == 0) return false;
            var c = WorldConstants.ChunkOf(p.Floor());
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var k = new Int3(c.X + dx, c.Y + dy, c.Z + dz);
                if (_machinesInChunk.ContainsKey(k) || _lightsInChunk.ContainsKey(k)) return true;
            }
            return false;
        }

        public Enemy Get(int id)
        {
            foreach (var e in _enemies)
                if (e.Id == id) return e;
            return null;
        }

        public Enemy Spawn(EnemyDef def, Vec3 pos)
        {
            var e = new Enemy
            {
                Id = _nextId++,
                Def = def,
                Position = pos,
                Home = pos,
                Health = def.Health,
                Yaw = _rng.NextFloat() * 360f,
                LayTimer = def.LaysEvery / 2 + _rng.NextInt(Math.Max(1, def.LaysEvery))
            };
            _enemies.Add(e);
            return e;
        }

        private bool IsDay => GameServer.SunHeight(_server.DayPhase) > 0.05f;

        public void Tick()
        {
            _tick++;
            var world = _server.World;

            if (SpawningEnabled && _tick % 40 == 7) TrySpawn();

            for (int i = _enemies.Count - 1; i >= 0; i--)
            {
                var e = _enemies[i];
                float despawn = e.Def.IsPassive ? PassiveDespawnRadius : DespawnRadius;
                // Домашние животные не исчезают: вдали от игроков они просто «замирают» (чанк не загружен)
                bool gone = e.Health <= 0 || (!e.Domestic && !AnyPlayerWithin(e.Position, despawn));
                // Ночные существа на поверхности уходят с рассветом (если никто не видит рядом)
                if (!gone && e.Def.Time == "night" && IsDay && e.Def.Spawn == "surface" && !AnyPlayerWithin(e.Position, 20) && _rng.Chance(0.01)) gone = true;
                if (gone)
                {
                    _enemies.RemoveAt(i);
                    continue;
                }
                TickEnemy(e, world);
            }
            TickProjectiles(world);
            TickClouds();
        }

        private bool AnyPlayerWithin(Vec3 p, float r)
        {
            foreach (var pl in _server.Players)
                if ((pl.Position - p).LengthSquared <= r * r) return true;
            return false;
        }

        // ============================================================ ИИ

        private void TickEnemy(Enemy e, VoxelWorld world)
        {
            const float dt = GameConstants.TickDuration;
            if (e.AttackCooldown > 0) e.AttackCooldown--;
            if (e.SpecialCooldown > 0) e.SpecialCooldown--;
            if (e.HurtTicks > 0) e.HurtTicks--;
            if (e.AlertTicks > 0) e.AlertTicks--;
            if (e.BreedCooldown > 0) e.BreedCooldown--;
            if (e.LoveTicks > 0) e.LoveTicks--;

            var feet = e.Position.Floor();
            if (!world.IsLoaded(feet.X, feet.Y, feet.Z)) return;

            float wishX = 0, wishZ = 0, speed = e.Def.Speed;
            bool jump = false;

            if (e.State == CreatureState.Curled || e.State == CreatureState.Stunned)
            {
                if (--e.StateTicks <= 0) e.State = CreatureState.Normal;
            }
            else if (e.Def.IsPassive) PassiveBrain(e, ref wishX, ref wishZ, ref speed, ref jump);
            else HostileBrain(e, ref wishX, ref wishZ, ref speed, ref jump);

            if (e.State == CreatureState.Attack && --e.StateTicks <= 0) e.State = CreatureState.Normal;

            if (wishX != 0 || wishZ != 0)
                e.Yaw = (float)(Math.Atan2(wishX, wishZ) * 180.0 / Math.PI);

            var v = e.Velocity;
            bool charging = e.State == CreatureState.Special && e.Def.Attack == "charge";
            bool leaping = e.State == CreatureState.Special && e.Def.Attack == "leap";
            if (!leaping)
            {
                float accel = e.OnGround ? (charging ? 30f : 10f) : 2f;
                v.X += (wishX * speed - v.X) * Math.Min(1f, accel * dt);
                v.Z += (wishZ * speed - v.Z) * Math.Min(1f, accel * dt);
            }
            if (jump && e.OnGround) v.Y = PlayerPhysics.JumpVelocity * 0.9f;
            v.Y -= PlayerPhysics.Gravity * dt;
            if (v.Y < -50f) v.Y = -50f;

            var pos = e.Position;
            PlayerPhysics.MoveBody(world, ref pos, ref v, e.Def.Width / 2f, e.Def.Height, dt, out bool onGround, out bool hitWall);
            if (charging && hitWall)
            {
                // Рывок в стену — оглушение: беззащитен, двойной урон
                e.State = CreatureState.Stunned;
                e.StateTicks = 40;
                v = Vec3.Zero;
            }
            else if (hitWall && onGround && (wishX != 0 || wishZ != 0)) v.Y = PlayerPhysics.JumpVelocity * 0.95f;
            if (leaping && onGround && e.Velocity.Y <= 0)
            {
                e.State = CreatureState.Normal;
                e.SpecialCooldown = e.Def.Cooldown;
            }
            e.Position = pos;
            e.Velocity = v;
            e.OnGround = onGround;
        }

        // ------------------------------------------------------------ Мирные

        private void PassiveBrain(Enemy e, ref float wishX, ref float wishZ, ref float speed, ref bool jump)
        {
            // Несёт яйца
            if (e.Def.Lays != null && e.Def.Lays.ItemId >= 0 && --e.LayTimer <= 0)
            {
                e.LayTimer = e.Def.LaysEvery + _rng.NextInt(Math.Max(1, e.Def.LaysEvery / 2));
                _server.Drops.Spawn(new ItemStack(e.Def.Lays.ItemId, Math.Max(1, e.Def.Lays.Count)), e.Position + new Vec3(0, 0.3f, 0), Vec3.Zero, 20);
            }

            if (e.FleeTicks > 0)
            {
                e.FleeTicks--;
                e.State = CreatureState.Flee;
                var away = e.Position - e.FleeFrom;
                Dir(away, out wishX, out wishZ);
                speed *= 1.6f;
                if (_rng.Chance(0.05)) jump = true;
                return;
            }
            if (e.State == CreatureState.Flee) e.State = CreatureState.Normal;

            // Пара для разведения
            if (e.LoveTicks > 0)
            {
                foreach (var o in _enemies)
                {
                    if (o == e || o.Def != e.Def || o.LoveTicks <= 0) continue;
                    var to = o.Position - e.Position;
                    if (to.LengthSquared > 10 * 10) continue;
                    if (to.LengthSquared < 1.6f * 1.6f)
                    {
                        e.LoveTicks = o.LoveTicks = 0;
                        e.BreedCooldown = o.BreedCooldown = 20 * 60 * 5;
                        var baby = Spawn(e.Def, e.Position + new Vec3(0.5f, 0.2f, 0.5f));
                        baby.Health = e.Def.Health;
                        baby.Domestic = true;
                        baby.Home = baby.Position;
                        return;
                    }
                    Dir(to, out wishX, out wishZ);
                    speed *= 0.8f;
                    return;
                }
            }

            // Идёт за игроком с любимой едой в руке
            if (e.Def.FoodItem >= 0)
            {
                foreach (var p in _server.Players)
                {
                    var held = p.SelectedStack;
                    if (held.IsEmpty || held.Item != e.Def.FoodItem) continue;
                    var to = p.Position - e.Position;
                    if (to.LengthSquared > 9 * 9) continue;
                    if (to.LengthSquared > 2.2f * 2.2f)
                    {
                        Dir(to, out wishX, out wishZ);
                        speed *= 0.7f;
                    }
                    else e.Yaw = (float)(Math.Atan2(to.X, to.Z) * 180.0 / Math.PI);
                    return;
                }
            }

            Wander(e, ref wishX, ref wishZ, ref speed, true);
        }

        // ------------------------------------------------------------ Враждебные

        private void HostileBrain(Enemy e, ref float wishX, ref float wishZ, ref float speed, ref bool jump)
        {
            var def = e.Def;
            // Поводок есть у всех: пещерные и руинные — 20 блоков, ночные с поверхности — 32. К базе не идут.
            float leash = def.Spawn == "surface" ? SurfaceLeashRadius : LeashRadius;
            float aggro = def.Aggro * (e.AlertTicks > 0 ? 2f : 1f);
            PlayerState target = null;
            if (def.IsHostile || e.Provoked)
            {
                float best = aggro * aggro;
                foreach (var p in _server.Players)
                {
                    if (p.Health <= 0) continue;
                    float d = (p.Position - e.Position).LengthSquared;
                    bool inLeash = (p.Position - e.Home).LengthSquared < (leash + 6) * (leash + 6);
                    if (d < best && inLeash && !IsSafeZone(p.Position))
                    {
                        best = d;
                        target = p;
                    }
                }
            }
            e.TargetPlayer = target != null ? target.Id : -1;

            // Особые атаки, уже начатые, продолжаются и без цели
            if (e.State == CreatureState.Special && def.Attack == "charge")
            {
                wishX = e.ChargeDir.X;
                wishZ = e.ChargeDir.Z;
                speed = def.Speed * 3.2f;
                if (--e.StateTicks <= 0)
                {
                    e.State = CreatureState.Normal;
                    e.SpecialCooldown = def.Cooldown;
                }
                if (target != null && Horizontal(target.Position - e.Position) < 1.5f && Math.Abs(target.Position.Y - e.Position.Y) < 2f)
                {
                    Hit(e, target, def.Damage * 1.5f, 11f);
                    e.State = CreatureState.Normal;
                    e.SpecialCooldown = def.Cooldown;
                }
                return;
            }
            if (e.State == CreatureState.Special && def.Attack == "leap")
            {
                if (!e.LeapHit && target != null && (target.Position - e.Position).LengthSquared < 1.6f * 1.6f)
                {
                    e.LeapHit = true;
                    Hit(e, target, def.Damage * 1.4f, 7f);
                }
                return;
            }

            // Бегство при ранении
            if (def.FleeHealth > 0 && e.Health < def.Health * def.FleeHealth && target != null)
            {
                e.FleeTicks = Math.Max(e.FleeTicks, 60);
                e.FleeFrom = target.Position;
            }
            if (e.FleeTicks > 0)
            {
                e.FleeTicks--;
                e.State = CreatureState.Flee;
                Dir(e.Position - e.FleeFrom, out wishX, out wishZ);
                speed *= 1.4f;
                return;
            }
            if (e.State == CreatureState.Flee) e.State = CreatureState.Normal;

            if (target == null)
            {
                if (e.State == CreatureState.Windup) e.State = CreatureState.Normal;
                // Ушёл за поводок или забрёл к базе — домой
                if ((e.Home - e.Position).LengthSquared > leash * leash || IsSafeZone(e.Position))
                {
                    Dir(e.Home - e.Position, out wishX, out wishZ);
                    return;
                }
                Wander(e, ref wishX, ref wishZ, ref speed, false);
                return;
            }

            var toT = target.Position - e.Position;
            float dist = Horizontal(toT);
            Dir(toT, out float dx, out float dz);

            // Замах перед особой атакой: стоит и смотрит на цель
            if (e.State == CreatureState.Windup)
            {
                e.Yaw = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                if (--e.StateTicks > 0) return;
                Release(e, target, dist, dx, dz);
                return;
            }

            bool canSpecial = e.SpecialCooldown == 0 && e.OnGround;
            switch (def.Attack)
            {
                case "leap":
                    if (canSpecial && dist > 2.5f && dist < def.Range) { StartSpecial(e, target, dist, dx, dz); return; }
                    break;
                case "charge":
                    if (canSpecial && dist > 3f && dist < def.Range) { StartSpecial(e, target, dist, dx, dz); return; }
                    break;
                case "spores":
                    if (canSpecial && dist < def.Range) { StartSpecial(e, target, dist, dx, dz); return; }
                    break;
                case "ranged":
                    // Держит дистанцию и плюётся
                    if (canSpecial && dist < def.Range && dist > 2.5f) { StartSpecial(e, target, dist, dx, dz); return; }
                    if (dist < def.Range * 0.45f)
                    {
                        wishX = -dx;
                        wishZ = -dz;
                        return;
                    }
                    if (dist < def.Range * 0.8f)
                    {
                        // Обходит по кругу
                        wishX = dz * 0.6f;
                        wishZ = -dx * 0.6f;
                        e.Yaw = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                        return;
                    }
                    break;
            }

            // Ближний бой
            wishX = dx;
            wishZ = dz;
            if (dist < AttackRange && Math.Abs(toT.Y) < 2f)
            {
                wishX = wishZ = 0;
                e.Yaw = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                if (e.AttackCooldown == 0)
                {
                    e.AttackCooldown = 20;
                    e.State = CreatureState.Attack;
                    e.StateTicks = 8;
                    Hit(e, target, def.Damage, 4f);
                }
            }
        }

        private void StartSpecial(Enemy e, PlayerState target, float dist, float dx, float dz)
        {
            if (e.Def.Windup > 0)
            {
                e.State = CreatureState.Windup;
                e.StateTicks = e.Def.Windup;
                return;
            }
            Release(e, target, dist, dx, dz);
        }

        private void Release(Enemy e, PlayerState target, float dist, float dx, float dz)
        {
            var def = e.Def;
            switch (def.Attack)
            {
                case "leap":
                {
                    float t = 0.55f;
                    float hs = Math.Min(14f, dist / t);
                    e.Velocity = new Vec3(dx * hs, 7.5f, dz * hs);
                    e.OnGround = false;
                    e.State = CreatureState.Special;
                    e.LeapHit = false;
                    break;
                }
                case "charge":
                    e.ChargeDir = new Vec3(dx, 0, dz);
                    e.State = CreatureState.Special;
                    e.StateTicks = 30;
                    break;
                case "ranged":
                {
                    // Навесной плевок с упреждением по высоте
                    var from = e.Position + new Vec3(0, e.Def.Height * 0.8f, 0);
                    var aim = target.Position + new Vec3(0, 1.0f, 0);
                    var to = aim - from;
                    float h = Horizontal(to);
                    float speedH = 11f;
                    float t = Math.Max(0.2f, h / speedH);
                    float vy = to.Y / t + 0.5f * 14f * t;
                    _projectiles.Add(new Projectile
                    {
                        Id = _nextId++,
                        Position = from,
                        Velocity = new Vec3(to.X / h * speedH, vy, to.Z / h * speedH),
                        Damage = def.Damage,
                        Poison = def.Poison
                    });
                    e.State = CreatureState.Attack;
                    e.StateTicks = 8;
                    e.SpecialCooldown = def.Cooldown;
                    break;
                }
                case "spores":
                    _clouds.Add(new SporeCloud { Id = _nextId++, Position = e.Position + new Vec3(0, 0.6f, 0), Radius = 3.2f, Ticks = 80 });
                    e.State = CreatureState.Attack;
                    e.StateTicks = 10;
                    e.SpecialCooldown = def.Cooldown;
                    break;
                default:
                    e.State = CreatureState.Normal;
                    break;
            }
        }

        private void Hit(Enemy e, PlayerState target, float damage, float knock)
        {
            _server.DamagePlayer(target, damage * _server.Settings.Difficulty, "enemy");
            if (e.Def.Poison > 0) target.PoisonTicks = Math.Max(target.PoisonTicks, e.Def.Poison);
            var dir = target.Position - e.Position;
            Dir(dir, out float x, out float z);
            target.Knockback = new Vec3(x * knock, 4f, z * knock);
            target.KnockbackVersion++;
        }

        private void Wander(Enemy e, ref float wishX, ref float wishZ, ref float speed, bool herd)
        {
            if (--e.WanderTimer <= 0)
            {
                e.WanderTimer = 40 + _rng.NextInt(100);
                bool idle = _rng.Chance(0.45);
                double a = _rng.NextDouble() * Math.PI * 2;
                float x = idle ? 0 : (float)Math.Sin(a), z = idle ? 0 : (float)Math.Cos(a);
                // Стадо держится вместе: тянет к центру сородичей
                if (herd && !idle)
                {
                    var center = Vec3.Zero;
                    int n = 0;
                    foreach (var o in _enemies)
                        if (o != e && o.Def == e.Def && (o.Position - e.Position).LengthSquared < 16 * 16)
                        {
                            center = center + o.Position;
                            n++;
                        }
                    if (n > 0)
                    {
                        var to = center / n - e.Position;
                        if (to.LengthSquared > 25)
                        {
                            Dir(to, out x, out z);
                        }
                    }
                }
                e.WanderX = x;
                e.WanderZ = z;
            }
            wishX = e.WanderX;
            wishZ = e.WanderZ;
            speed *= 0.4f;
        }

        private static float Horizontal(Vec3 v) => (float)Math.Sqrt(v.X * v.X + v.Z * v.Z);

        private static void Dir(Vec3 v, out float x, out float z)
        {
            float l = Horizontal(v);
            if (l < 1e-4f)
            {
                x = z = 0;
                return;
            }
            x = v.X / l;
            z = v.Z / l;
        }

        // ============================================================ Снаряды и облака

        private void TickProjectiles(VoxelWorld world)
        {
            const float dt = GameConstants.TickDuration;
            for (int i = _projectiles.Count - 1; i >= 0; i--)
            {
                var p = _projectiles[i];
                p.Velocity = p.Velocity + new Vec3(0, -14f * dt, 0);
                p.Position = p.Position + p.Velocity * dt;
                bool remove = --p.Life <= 0;
                var c = p.Position.Floor();
                if (!world.IsLoaded(c.X, c.Y, c.Z) || world.GetBlockDef(c).Solid) remove = true;
                foreach (var pl in _server.Players)
                {
                    if (pl.Health <= 0) continue;
                    var d = p.Position - pl.Position;
                    if (Math.Abs(d.X) < 0.5f && Math.Abs(d.Z) < 0.5f && d.Y > -0.1f && d.Y < 1.9f)
                    {
                        _server.DamagePlayer(pl, p.Damage, "acid");
                        if (p.Poison > 0) pl.PoisonTicks = Math.Max(pl.PoisonTicks, p.Poison);
                        remove = true;
                        break;
                    }
                }
                if (remove) _projectiles.RemoveAt(i);
            }
        }

        private void TickClouds()
        {
            for (int i = _clouds.Count - 1; i >= 0; i--)
            {
                var c = _clouds[i];
                foreach (var pl in _server.Players)
                {
                    var d = pl.Position + new Vec3(0, 0.9f, 0) - c.Position;
                    if (d.LengthSquared < c.Radius * c.Radius) pl.PoisonTicks = Math.Max(pl.PoisonTicks, 50);
                }
                if (--c.Ticks <= 0) _clouds.RemoveAt(i);
            }
        }

        // ============================================================ Появление

        private void TrySpawn()
        {
            var content = _server.Content;
            if (content.Enemies.Count == 0) return;
            foreach (var p in _server.Players)
            {
                int hostile = 0, passive = 0;
                foreach (var e in _enemies)
                {
                    float d = (e.Position - p.Position).LengthSquared;
                    if (e.Def.IsPassive) { if (d < 64 * 64) passive++; }
                    else if (d < 48 * 48) hostile++;
                }
                if (hostile < MaxHostilePerPlayer) TrySpawnNear(p, false);
                if (passive < MaxPassivePerPlayer && _rng.Chance(0.35)) TrySpawnNear(p, true);
            }
        }

        private void TrySpawnNear(PlayerState p, bool passive)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                float r = (passive ? 22 : 14) + _rng.NextFloat() * 24;
                int x = (int)Math.Floor(p.Position.X + Math.Sin(a) * r);
                int z = (int)Math.Floor(p.Position.Z + Math.Cos(a) * r);
                int y = (int)Math.Floor(p.Position.Y) + _rng.NextInt(-10, 11);
                if (Math.Abs(x) < 16 && z > -16 && z < 56) continue; // не у входа в бункер
                if (!FindFloor(x, y, z, passive, out int fy, out string place)) continue;
                if (!passive)
                {
                    if (place == "surface" && !_server.Settings.SurfaceEnemies) continue; // настройка мира
                    if (IsSafeZone(new Vec3(x + 0.5f, fy, z + 0.5f))) continue;         // у базы не появляются
                }
                var def = Pick(x, z, place, passive);
                if (def == null) continue;
                int count = def.Group[0] + _rng.NextInt(Math.Max(1, def.Group[1] - def.Group[0] + 1));
                for (int k = 0; k < count; k++)
                {
                    int ox = x + (k == 0 ? 0 : _rng.NextInt(-3, 4)), oz = z + (k == 0 ? 0 : _rng.NextInt(-3, 4));
                    if (k > 0 && !FindFloor(ox, fy + 2, oz, passive, out int ofy, out _)) continue;
                    int yy = k == 0 ? fy : FloorAt(ox, fy + 2, oz);
                    if (yy == int.MinValue) continue;
                    Spawn(def, new Vec3(ox + 0.5f, yy, oz + 0.5f));
                }
                return;
            }
        }

        private int FloorAt(int x, int y, int z)
        {
            var world = _server.World;
            for (int dy = 0; dy < 8; dy++)
            {
                int fy = y - dy;
                if (!world.IsLoaded(x, fy, z) || !world.IsLoaded(x, fy + 2, z)) return int.MinValue;
                var below = world.GetBlockDef(new Int3(x, fy - 1, z));
                var at = world.GetBlockDef(new Int3(x, fy, z));
                var above = world.GetBlockDef(new Int3(x, fy + 1, z));
                if (below.Solid && !at.Solid && !above.Solid && !at.Liquid) return fy;
            }
            return int.MinValue;
        }

        /// <summary>Пол для появления и тип места: cave (глубоко под рельефом), ruin, surface (под открытым небом).</summary>
        private bool FindFloor(int x, int y, int z, bool passive, out int fy, out string place)
        {
            place = null;
            var world = _server.World;
            if (passive)
            {
                // Мирные — только на поверхности: ищем верх колонки рядом с уровнем игрока
                int top = y + 24;
                fy = int.MinValue;
                for (int yy = top; yy > y - 24; yy--)
                {
                    if (!world.IsLoaded(x, yy, z)) return false;
                    var def = world.GetBlockDef(new Int3(x, yy - 1, z));
                    if (def.Solid)
                    {
                        if (world.GetBlockDef(new Int3(x, yy, z)).Liquid) return false;
                        fy = yy;
                        break;
                    }
                }
                if (fy == int.MinValue) return false;
                if (!world.GetBlockDef(new Int3(x, fy, z)).Replaceable && world.GetBlock(new Int3(x, fy, z)) != 0) return false;
                place = "surface";
                return true;
            }

            fy = FloorAt(x, y, z);
            if (fy == int.MinValue) return false;
            var below = world.GetBlockDef(new Int3(x, fy - 1, z));
            int surface = world.Generator.ColumnAt(x, z).Height;
            if (below.Id == "genesis:ruin_bricks" || below.Id == "genesis:concrete_rubble" || below.Id == "genesis:mossy_cobblestone") place = "ruin";
            else if (surface - fy > 8) place = "cave";
            else place = "surface";
            return true;
        }

        private EnemyDef Pick(int x, int z, string place, bool passive)
        {
            var col = _server.World.Generator.ColumnAt(x, z);
            string biome = col.Biome.ToString();
            bool day = IsDay;
            float total = 0;
            var list = new List<EnemyDef>();
            foreach (var d in _server.Content.Enemies.Items)
            {
                if (d.IsPassive != passive || d.Spawn != place) continue;
                if (place == "surface")
                {
                    if (d.Time == "day" && !day) continue;
                    if (d.Time == "night" && day) continue;
                    if (d.Biomes.Count > 0 && !d.Biomes.Exists(b => string.Equals(b, biome, StringComparison.OrdinalIgnoreCase))) continue;
                }
                list.Add(d);
                total += d.Weight;
            }
            if (list.Count == 0) return null;
            float r = _rng.NextFloat() * total;
            foreach (var d in list)
            {
                r -= d.Weight;
                if (r <= 0) return d;
            }
            return list[list.Count - 1];
        }

        // ============================================================ Бой и взаимодействие

        /// <summary>Нанести урон. Возвращает true, если существо погибло.</summary>
        public bool Damage(Enemy e, float amount, Vec3 from, string toolType = null)
        {
            var def = e.Def;
            if (e.State == CreatureState.Curled) amount *= 0.2f;
            if (e.State == CreatureState.Stunned) amount *= 2f;
            if (def.Armor > 0 && toolType != def.WeakTo) amount *= 1f - def.Armor;
            e.Health -= amount;
            e.HurtTicks = 6;
            var push = e.Position - from;
            push.Y = 0;
            var n = push.Normalized;
            if (e.State != CreatureState.Curled) e.Velocity = new Vec3(n.X * 6f, 5f, n.Z * 6f);
            bool died = e.Health <= 0;

            if (!died && def.Curl && e.State != CreatureState.Curled)
            {
                e.State = CreatureState.Curled;
                e.StateTicks = 60;
            }
            if (def.IsPassive)
            {
                // Паника в стаде
                foreach (var o in _enemies)
                    if (o.Def == def && (o.Position - e.Position).LengthSquared < 12 * 12)
                    {
                        o.FleeTicks = 100;
                        o.FleeFrom = from;
                    }
            }
            else
            {
                e.Provoked = true;
                if (def.Pack)
                    foreach (var o in _enemies)
                        if (o != e && o.Def == def && (o.Position - e.Position).LengthSquared < 12 * 12)
                        {
                            o.AlertTicks = 200;
                            o.Provoked = true;
                        }
            }
            if (died && def.DeathCloud) _clouds.Add(new SporeCloud { Id = _nextId++, Position = e.Position + new Vec3(0, 0.5f, 0), Radius = 3.5f, Ticks = 100 });
            return died;
        }

        /// <summary>Накормить мирное существо его любимой едой: лечит и запускает поиск пары.</summary>
        public bool Feed(Enemy e)
        {
            if (!e.Def.IsPassive || e.Def.FoodItem < 0) return false;
            e.Health = Math.Min(e.Def.Health, e.Health + 4);
            e.Domestic = true;
            if (e.BreedCooldown > 0) return true;
            e.LoveTicks = 600;
            return true;
        }

        // ============================================================ Сохранение

        /// <summary>Сохраняются мирные животные (ферма переживает перезапуск); враги появляются заново.</summary>
        public void Write(System.IO.BinaryWriter w)
        {
            w.Write(1);
            int n = 0;
            foreach (var e in _enemies) if (e.Def.IsPassive && e.Health > 0) n++;
            w.Write(n);
            foreach (var e in _enemies)
            {
                if (!e.Def.IsPassive || e.Health <= 0) continue;
                w.Write(e.Def.Id);
                w.Write(e.Position.X);
                w.Write(e.Position.Y);
                w.Write(e.Position.Z);
                w.Write(e.Home.X);
                w.Write(e.Home.Y);
                w.Write(e.Home.Z);
                w.Write(e.Health);
                w.Write(e.Domestic);
                w.Write(e.BreedCooldown);
                w.Write(e.LayTimer);
            }
        }

        public void Read(System.IO.BinaryReader r)
        {
            _enemies.Clear();
            r.ReadInt32();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                var pos = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var home = new Vec3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float health = r.ReadSingle();
                bool domestic = r.ReadBoolean();
                int breed = r.ReadInt32();
                int lay = r.ReadInt32();
                var def = _server.Content.Enemies.Get(id);
                if (def == null) continue;
                var e = Spawn(def, pos);
                e.Home = home;
                e.Health = Math.Min(def.Health, health);
                e.Domestic = domestic;
                e.BreedCooldown = breed;
                e.LayTimer = lay;
            }
        }

        public void Clear() => _enemies.Clear();

        public List<ItemStack> RollDrops(Enemy e)
        {
            var list = new List<ItemStack>();
            foreach (var d in e.Def.Drops)
                if (d.ItemId >= 0 && _rng.Chance(d.Chance)) list.Add(new ItemStack(d.ItemId, d.Count));
            return list;
        }
    }
}
