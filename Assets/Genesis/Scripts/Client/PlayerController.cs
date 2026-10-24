using Genesis.Client.Rendering;
using Genesis.Core;
using Genesis.Gameplay;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Локальный игрок: ввод → физика (клиент-авторитетно) → камера; взаимодействие с блоками через команды серверу.
    /// </summary>
    public sealed class PlayerController : MonoBehaviour
    {
        private ClientContext _ctx;
        private Camera _cam;
        private BlockHighlight _highlight;
        private PlayerState _body; // локальное «тело» для физики (без инвентаря)

        private Int3 _breakTarget;
        private float _breakTime;
        private float _useCooldown;
        private bool _ready;
        private int _teleportSeen;
        private int _knockSeen;
        private float _attackCooldown;
        private float _enemyHitDistance = float.PositiveInfinity;

        public EffectsRenderer Effects;
        public HeldItemRenderer Hand;

        // Покачивание камеры и рывок FOV при беге
        private float _bobPhase, _bobAmount, _fovKick, _swingTimer;

        public void Init(ClientContext ctx, Camera cam, BlockHighlight highlight)
        {
            _ctx = ctx;
            _cam = cam;
            _highlight = highlight;
            _body = new PlayerState(ctx.Player.Id, ctx.Player.Name, ctx.Content)
            {
                Position = ctx.Player.Position,
                Yaw = ctx.Player.Yaw,
                Pitch = ctx.Player.Pitch,
                Flying = ctx.Player.Flying
            };
            _teleportSeen = ctx.Player.TeleportVersion;
            _knockSeen = ctx.Player.KnockbackVersion;
            _ctx.Paused = false;
            LockCursor(true);
        }

        /// <summary>Поставить камеру: позиция глаз, рыскание и наклон (съёмка скриншотов). Игрок зависает в полёте.</summary>
        public void SetView(Vector3 eye, float yaw, float pitch)
        {
            _body.Position = new Vec3(eye.x, eye.y - PlayerPhysics.EyeHeight, eye.z);
            _body.Velocity = Vec3.Zero;
            _body.Flying = true;
            _body.Yaw = yaw;
            _body.Pitch = pitch;
            _bobAmount = 0f;
            _ready = true;
            UpdateCamera();
        }

        public static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        /// <summary>
        /// Единственное место, где решается судьба курсора: каждый кадр приводим его к состоянию игры
        /// (окно/мини-игра/пауза — свободен и виден, иначе — захвачен и скрыт). Раньше курсор переключали
        /// в десятке мест, и после мини-игры, закрытия машины, Alt+Tab он «застревал» не в том состоянии.
        /// </summary>
        private void ApplyCursor()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            // Система отобрала захват (Alt+Tab, клик вне окна в редакторе) — ставим паузу, как Minecraft
            if (_lockApplied && !locked && !Application.isFocused && _ctx.WantsCursorLock) _ctx.Paused = true;
            // В браузере Esc снимает захват мыши сам (и до игры может не дойти) — тоже пауза. Захват браузер даёт
            // не сразу, а по следующему клику, поэтому считаем потерей только то, что уже было захвачено.
            if (WebPlatform.IsWebGL)
            {
                if (locked) _sawLocked = true;
                else if (_sawLocked && _ctx.WantsCursorLock)
                {
                    _ctx.Paused = true;
                    _sawLocked = false;
                }
                if (!_ctx.WantsCursorLock) _sawLocked = false;
            }
            bool want = _ctx.WantsCursorLock && Application.isFocused;
            if (want != locked || Cursor.visible == want) LockCursor(want);
            _lockApplied = want;
        }

        private bool _lockApplied;
        private bool _sawLocked;

        private void OnApplicationFocus(bool focus)
        {
            if (!focus && _ctx != null && _ctx.WantsCursorLock) _ctx.Paused = true;
        }

        private void Update()
        {
            if (_ctx == null) return;
            var world = _ctx.Session.World;
            var input = InputReader.Read();

            // Сервер перенёс игрока (возрождение) — принимаем позицию
            if (_ctx.Player.TeleportVersion != _teleportSeen)
            {
                _teleportSeen = _ctx.Player.TeleportVersion;
                _body.Position = _ctx.Player.Position;
                _body.Velocity = Vec3.Zero;
                _ready = false;
            }

            // Удар существа — отбрасывает
            if (_ctx.Player.KnockbackVersion != _knockSeen)
            {
                _knockSeen = _ctx.Player.KnockbackVersion;
                var kb = _ctx.Player.Knockback;
                if (!_body.Flying) _body.Velocity = new Vec3(_body.Velocity.X + kb.X, kb.Y, _body.Velocity.Z + kb.Z);
            }

            // Ждём, пока прогрузятся чанки под ногами
            if (!_ready)
            {
                var f = _body.Position.Floor();
                _ready = world.IsLoaded(f.X, f.Y, f.Z) && world.IsLoaded(f.X, f.Y - 16, f.Z);
                UpdateCamera();
                HandleUiKeys(input);
                ApplyCursor();
                return;
            }

            HandleUiKeys(input);
            ApplyCursor();
            bool uiOpen = !_ctx.WantsCursorLock;

            // ---- Взгляд
            bool aiming = Cursor.lockState == CursorLockMode.Locked || TouchControls.Enabled; // на сенсорном экране захвата мыши нет
            if (!uiOpen && aiming)
            {
                _body.Yaw = Mathf.Repeat(_body.Yaw + input.LookX, 360f);
                _body.Pitch = Mathf.Clamp(_body.Pitch - input.LookY, -89f, 89f);
            }

            // ---- Движение
            var move = new MoveInput();
            if (!uiOpen)
            {
                move.Forward = input.Forward;
                move.Strafe = input.Strafe;
                move.Jump = input.Jump;
                move.Sprint = input.Sprint;
                move.Descend = input.Descend;
                if (!_ctx.Player.Creative && _body.Flying) _body.Flying = false; // креатив выключили
                if (input.ToggleFly && _ctx.Player.Creative)
                {
                    _body.Flying = !_body.Flying;
                    _body.Velocity = Vec3.Zero;
                }
            }

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            int sub = Mathf.CeilToInt(dt / 0.02f);
            float landing = 0;
            for (int i = 0; i < sub; i++)
            {
                PlayerPhysics.Step(world, _body, move, dt / sub);
                landing = Mathf.Max(landing, _body.LandingSpeed);
            }
            // Урон от падения: безопасно примерно до 3–4 блоков
            if (landing > 13f && !_body.Flying)
                _ctx.Send(new DamageSelfCommand { Amount = Mathf.Floor((landing - 13f) * 0.9f) + 1f });

            _ctx.Send(new PlayerMoveCommand
            {
                Position = _body.Position,
                Yaw = _body.Yaw,
                Pitch = _body.Pitch,
                Flying = _body.Flying
            });

            UpdateCamera();

            // ---- Хотбар
            if (input.HotbarKey >= 0) SelectSlot(input.HotbarKey);
            if (!uiOpen && input.Scroll != 0)
                SelectSlot((_ctx.Player.SelectedSlot + input.Scroll + PlayerState.HotbarSize) % PlayerState.HotbarSize);

            // ---- Цель и взаимодействие
            UpdateTarget(world);
            if (uiOpen || !aiming)
            {
                _ctx.BreakProgress = 0;
                Effects?.SetBreaking(false, default, 0);
                return;
            }
            _attackCooldown -= Time.deltaTime;
            if (_ctx.TargetEnemy >= 0 && _enemyHitDistance < (_ctx.HasTarget ? _lastHit.Distance : float.PositiveInfinity))
            {
                // Враг ближе блока — бьём его, а не ломаем блок
                _ctx.BreakProgress = 0;
                _breakTime = 0;
                Effects?.SetBreaking(false, default, 0);
                if (input.Attack && _attackCooldown <= 0)
                {
                    _attackCooldown = 0.45f;
                    Hand?.Swing();
                    _ctx.Send(new AttackCommand { EnemyId = _ctx.TargetEnemy });
                }
            }
            else HandleBreaking(input, world);
            HandleUse(input, world);

            if (input.Drop)
            {
                _ctx.Send(new DropSlotCommand { Slot = _ctx.Player.SelectedSlot, All = input.DropAll });
                Hand?.Swing();
            }

            if (input.Rotate && _ctx.HasTarget)
                _ctx.Send(new RotateCommand { Pos = _ctx.Target });
        }

        private void HandleUiKeys(InputSnapshot input)
        {
            if (_ctx.InMinigame)
            {
                // Мини-игра модальная: Esc — бросить (материалы не тратятся)
                if (input.Escape) _ctx.Minigame = null;
                return;
            }
            if (input.Escape)
            {
                var boot = GetComponent<GenesisBootstrap>();
                if (_ctx.Window != UiWindow.None) _ctx.Window = UiWindow.None;
                else if (_ctx.Paused && boot != null && boot.InGameSettings) boot.InGameSettings = false; // из настроек — назад в меню паузы
                else _ctx.Paused = !_ctx.Paused;
            }
            if (input.Inventory) ToggleWindow(UiWindow.Inventory);
            if (input.Creative && _ctx.Player.Creative) ToggleWindow(UiWindow.Creative);
            if (input.Knowledge) ToggleWindow(UiWindow.Knowledge);
            if (input.Stats) ToggleWindow(UiWindow.Stats);
            if (input.Recipes) ToggleWindow(UiWindow.Recipes);
            if (input.Map) ToggleWindow(UiWindow.Map);
            if (input.Debug) _ctx.ShowDebug = !_ctx.ShowDebug;
            if (input.Save) GetComponent<GenesisBootstrap>()?.SaveNow();

        }

        private void OpenMe(Int3 access, bool wireless)
        {
            _ctx.Session.OpenMe(access, wireless);
            _ctx.Window = UiWindow.Me;
            _ctx.Paused = false;
        }

        public void ToggleWindow(UiWindow w)
        {
            _ctx.Window = _ctx.Window == w ? UiWindow.None : w;
            if (_ctx.Window != UiWindow.None) _ctx.Paused = false;
        }

        private void SelectSlot(int slot)
        {
            _ctx.Player.SelectedSlot = slot; // мгновенный отклик в UI
            _ctx.Send(new SelectSlotCommand { Slot = slot });
        }

        private void UpdateCamera()
        {
            var eye = _body.EyePosition;
            float dt = Time.deltaTime;
            float speed = Mathf.Sqrt(_body.Velocity.X * _body.Velocity.X + _body.Velocity.Z * _body.Velocity.Z);
            bool walking = _body.OnGround && !_body.Flying && speed > 0.5f;
            _bobAmount = Mathf.MoveTowards(_bobAmount, walking ? Mathf.Clamp01(speed / 6f) : 0f, dt * 4f);
            if (walking) _bobPhase += dt * speed * 1.9f;
            float bob = GameSettings.ViewBobbing ? _bobAmount : 0f;
            var right = Quaternion.Euler(0, _body.Yaw, 0) * Vector3.right;
            var offset = right * (Mathf.Cos(_bobPhase) * 0.035f * bob) + Vector3.up * (Mathf.Abs(Mathf.Sin(_bobPhase)) * 0.06f * bob);
            _cam.transform.position = new Vector3(eye.X, eye.Y, eye.Z) + offset;
            _cam.transform.rotation = Quaternion.Euler(_body.Pitch, _body.Yaw, Mathf.Cos(_bobPhase) * 0.6f * bob);

            bool sprinting = speed > PlayerPhysics.WalkSpeed + 0.4f;
            _fovKick = Mathf.MoveTowards(_fovKick, sprinting ? 1f : 0f, dt * 5f);
            _cam.fieldOfView = GameSettings.Fov + 8f * Mathf.SmoothStep(0, 1, _fovKick);

            if (Hand != null)
            {
                Hand.BobPhase = _bobPhase;
                Hand.BobAmount = bob;
            }
        }

        private void UpdateTarget(VoxelWorld world)
        {
            var eye = _body.EyePosition;
            var look = _body.LookDirection;
            if (VoxelRaycast.Cast(world, eye, look, GameServer.ReachDistance, out var hit))
            {
                _ctx.HasTarget = true;
                _ctx.Target = hit.Block;
                _lastHit = hit;
                _highlight.Show(hit.Block);
            }
            else
            {
                _ctx.HasTarget = false;
                _highlight.Hide();
            }

            // Враги под прицелом: луч против AABB
            _ctx.TargetEnemy = -1;
            _enemyHitDistance = float.PositiveInfinity;
            foreach (var e in _ctx.Session.Enemies)
            {
                float hw = e.Def.Width / 2f;
                var min = new Vec3(e.Position.X - hw, e.Position.Y, e.Position.Z - hw);
                var max = new Vec3(e.Position.X + hw, e.Position.Y + e.Def.Height, e.Position.Z + hw);
                if (RayBox(eye, look, min, max, out float t) && t <= GameServer.AttackReach && t < _enemyHitDistance)
                {
                    _enemyHitDistance = t;
                    _ctx.TargetEnemy = e.Id;
                }
            }
        }

        private static bool RayBox(Vec3 o, Vec3 d, Vec3 min, Vec3 max, out float tHit)
        {
            float tmin = 0f, tmax = float.PositiveInfinity;
            tHit = 0;
            if (!Slab(o.X, d.X, min.X, max.X, ref tmin, ref tmax)) return false;
            if (!Slab(o.Y, d.Y, min.Y, max.Y, ref tmin, ref tmax)) return false;
            if (!Slab(o.Z, d.Z, min.Z, max.Z, ref tmin, ref tmax)) return false;
            tHit = tmin;
            return true;
        }

        private static bool Slab(float o, float d, float min, float max, ref float tmin, ref float tmax)
        {
            if (Mathf.Abs(d) < 1e-6f) return o >= min && o <= max;
            float t1 = (min - o) / d, t2 = (max - o) / d;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tmin = Mathf.Max(tmin, t1);
            tmax = Mathf.Min(tmax, t2);
            return tmin <= tmax;
        }

        private VoxelHit _lastHit;

        private void HandleBreaking(InputSnapshot input, VoxelWorld world)
        {
            Effects?.SetBreaking(false, default, 0);
            if (input.AttackDown) Hand?.Swing();
            if (!input.Attack || !_ctx.HasTarget)
            {
                _breakTime = 0;
                _ctx.BreakProgress = 0;
                return;
            }
            // Пока ломаем — рука машет
            _swingTimer -= Time.deltaTime;
            if (_swingTimer <= 0)
            {
                _swingTimer = 0.28f;
                Hand?.Swing();
            }
            if (_ctx.Target != _breakTarget)
            {
                _breakTarget = _ctx.Target;
                _breakTime = 0;
            }

            var def = world.GetBlockDef(_breakTarget);
            if (def.Unbreakable)
            {
                _ctx.BreakProgress = 0;
                return;
            }
            // Время зависит от инструмента: подходящий ускоряет, без нужного уровня — долго и без дропа
            var tool = Mining.ToolOf(_ctx.Content, _ctx.Player.SelectedStack);
            float need = Mining.BreakTime(def, tool);
            _breakTime += Time.deltaTime;
            _ctx.BreakProgress = Mathf.Clamp01(_breakTime / need);
            Effects?.SetBreaking(true, _breakTarget, _ctx.BreakProgress);
            if (_breakTime >= need)
            {
                _ctx.Send(new BreakBlockCommand { Pos = _breakTarget });
                _breakTime = -0.15f; // небольшая пауза перед следующим блоком
            }
        }

        private void HandleUse(InputSnapshot input, VoxelWorld world)
        {
            _useCooldown -= Time.deltaTime;
            if (!input.Use) return;
            if (!input.UseDown && _useCooldown > 0) return;
            _useCooldown = 0.22f;

            var factory = _ctx.Session.Factory;
            var held = _ctx.Player.SelectedStack;

            // Накормить существо его любимой едой (приручение, разведение)
            if (input.UseDown && _ctx.TargetEnemy >= 0 && !held.IsEmpty)
            {
                foreach (var en in _ctx.Session.Enemies)
                    if (en.Id == _ctx.TargetEnemy && en.Def.FoodItem == held.Item)
                    {
                        _ctx.Send(new InteractCommand { CreatureId = en.Id });
                        Hand?.Swing();
                        return;
                    }
            }
            var heldDef = held.IsEmpty ? null : _ctx.Content.Item(held.Item);
            bool targetsMachine = _ctx.HasTarget && factory.MachineAt(_ctx.Target) != null;

            // Беспроводной терминал: ПКМ по контроллеру — привязать, иначе открыть сеть
            if (heldDef != null && heldDef.MeWireless && input.UseDown)
            {
                var tm = _ctx.HasTarget ? factory.MachineAt(_ctx.Target) : null;
                if (tm != null && tm.Def.Behavior == "me_controller")
                    _ctx.Send(new MeLinkCommand { Controller = _ctx.Target });
                else
                    OpenMe(default, true);
                return;
            }

            // Еда: ПКМ (если не открываем машину; Shift+ПКМ — поставить, если еду можно ставить)
            if (heldDef != null && heldDef.IsFood && !targetsMachine && !input.Sneak)
            {
                if (input.UseDown && _ctx.Player.Hunger < PlayerState.MaxHunger)
                {
                    _ctx.Send(new EatCommand());
                    Hand?.Swing();
                }
                return;
            }

            if (!_ctx.HasTarget) return;
            var target = _ctx.Target;

            // Открыть машину (Shift — поставить блок рядом, а не открывать)
            var targetMachine = factory.MachineAt(target);
            if (input.UseDown && !input.Sneak && targetMachine != null &&
                (targetMachine.Def.Behavior == "me_terminal" || targetMachine.Def.Behavior == "me_controller"))
            {
                OpenMe(target, false);
                return;
            }
            if (input.UseDown && !input.Sneak && targetMachine != null)
            {
                _ctx.OpenMachine = target;
                _ctx.Window = UiWindow.Machine;
                return;
            }

            var stack = _ctx.Player.SelectedStack;
            if (stack.IsEmpty) return;
            var item = _ctx.Content.Item(stack.Item);

            // Предмет, который нельзя поставить, кладём на ленту
            if (factory.ConveyorAt(target) != null && (item == null || item.PlacesBlock == null))
            {
                _ctx.Send(new PutOnConveyorCommand { Pos = target });
                Hand?.Swing();
                return;
            }

            if (item == null || item.PlacesBlock == null) return;
            Hand?.Swing();
            var look = _body.LookDirection;
            _ctx.Send(new PlaceBlockCommand
            {
                Pos = world.GetBlockDef(target).Replaceable ? target : _lastHit.Adjacent, // трава заменяется
                LookFacing = Directions.FromLook(look.X, look.Z)
            });
        }
    }
}
