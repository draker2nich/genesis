using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Первобытные механики на стороне клиента: две руки (F), крафт в руках (удерживать ПКМ), режим создания (R)
    /// с колесом выбора, подсказки под прицелом и предметы на полу.
    /// </summary>
    public sealed partial class PlayerController
    {
        private float _craftTimer;
        private float _strikeCooldown;
        private float _createPressedAt = -1f;
        private bool _wheelSticky;

        // ============================================================ Руки

        private void SwapHands()
        {
            _ctx.Send(new SwapHandsCommand());
            _craftTimer = 0;
            Hand?.Swing();
        }

        /// <summary>
        /// Крафт в двух руках: подходящая пара → подсказка; удерживать ПКМ — прогресс, по готовности — команда
        /// серверу (и дальше по кругу, пока хватает материалов). true — ПКМ занят крафтом.
        /// </summary>
        private bool UpdateHandCraft(InputSnapshot input, bool allowed)
        {
            var main = _ctx.Player.SelectedStack;
            var off = _ctx.Player.OffhandStack;
            var craft = allowed ? HandCrafting.Find(_ctx.Content, main, off, out _) : null;
            if (craft != _ctx.HandCraft) _craftTimer = 0;
            _ctx.HandCraft = craft;
            bool crafting = craft != null && input.Use;
            if (Hand != null) Hand.Crafting = crafting;
            if (OffHand != null) OffHand.Crafting = crafting;
            if (craft == null)
            {
                _craftTimer = 0;
                _ctx.HandCraftProgress = 0;
                return false;
            }
            if (input.Use)
            {
                _craftTimer += Time.deltaTime;
                if (_craftTimer >= craft.Seconds)
                {
                    _craftTimer = 0;
                    _ctx.Send(new HandCraftCommand { CraftId = craft.NumericId });
                    Hand?.Swing();
                    OffHand?.Swing();
                }
            }
            else _craftTimer = 0; // отпустил раньше — прогресс сбрасывается, предметы остаются в руках
            _ctx.HandCraftProgress = Mathf.Clamp01(_craftTimer / Mathf.Max(0.05f, craft.Seconds));
            return true;
        }

        // ============================================================ Режим создания

        /// <summary>Варианты для блока под прицелом: обработка (с подходящим предметом в руке) и сборка конструкции.</summary>
        private List<CreationOption> CollectOptions(VoxelWorld world, Int3 target, out bool needTool)
        {
            var list = new List<CreationOption>();
            var held = _ctx.Player.SelectedStack;
            var all = Shaping.Options(_ctx.Content, world, _ctx.Session.Factory, target, held, false);
            needTool = false;
            foreach (var s in all)
            {
                if (!Shaping.ToolOk(_ctx.Content, s, held))
                {
                    needTool = true;
                    continue;
                }
                list.Add(new CreationOption { Shaping = s, Icon = s.IconItem });
            }
            var match = Structures.Find(_ctx.Content, world, target, _body.Position);
            if (match != null)
                list.Add(new CreationOption { Structure = match.Def, Icon = _ctx.Content.Items.IndexOf(match.Def.Result) });
            if (list.Count > 0) needTool = false;
            return list;
        }

        /// <summary>true — ввод занят режимом создания (активен или открыто колесо).</summary>
        private bool UpdateCreation(InputSnapshot input, VoxelWorld world)
        {
            var c = _ctx.Creation;
            _strikeCooldown -= Time.deltaTime;

            // ---- Колесо выбора: R зажата, мышь выбирает сектор, отпустить R — выбрать
            if (c.WheelOpen)
            {
                c.WheelCursor += new Vector2(input.LookX, input.LookY) * 6f;
                if (c.WheelCursor.magnitude > 120f) c.WheelCursor = c.WheelCursor.normalized * 120f;
                c.WheelHover = -1;
                if (c.WheelCursor.magnitude > 24f && c.Options.Count > 0)
                {
                    // Сектор 0 — сверху, дальше по часовой стрелке
                    float a = Mathf.Atan2(c.WheelCursor.x, c.WheelCursor.y) * Mathf.Rad2Deg;
                    if (a < 0) a += 360f;
                    float step = 360f / c.Options.Count;
                    c.WheelHover = Mathf.FloorToInt((a + step / 2f) / step) % c.Options.Count;
                }
                bool pick = false, close = false;
                if (_wheelSticky)
                {
                    // Колесо открыто коротким нажатием R: выбор — ЛКМ, отмена — снова R
                    if (input.AttackDown) pick = true;
                    else if (input.Create) close = true;
                }
                else if (input.CreateUp || !input.CreateHeld)
                {
                    if (c.WheelHover >= 0) pick = true;
                    else if (Time.time - _createPressedAt < 0.25f) _wheelSticky = true;
                    else close = true;
                }
                if (pick && c.WheelHover >= 0 && _ctx.HasTarget)
                {
                    c.WheelOpen = false;
                    EnterCreation(world, c.Options[c.WheelHover], _ctx.Target);
                }
                else if (pick || close) c.WheelOpen = false;
                return true;
            }

            // ---- Активный режим
            if (c.Active)
            {
                if (input.Create)
                {
                    ExitCreation();
                    return true;
                }
                if (c.FinishedAt >= 0f)
                {
                    if (Time.time - c.FinishedAt > 0.35f) ExitCreation();
                    return true;
                }
                // Блок исчез или сменился (сломали, собрали) — выходим
                if (!world.IsLoaded(c.Pos.X, c.Pos.Y, c.Pos.Z) || world.GetBlock(c.Pos) != c.Block ||
                    (_body.EyePosition - new Vec3(c.Pos.X + 0.5f, c.Pos.Y + 0.5f, c.Pos.Z + 0.5f)).LengthSquared > (GameServer.ReachDistance + 1.5f) * (GameServer.ReachDistance + 1.5f))
                {
                    ExitCreation();
                    return true;
                }
                c.Angle = Mathf.MoveTowards(c.Angle, c.TargetAngle, Time.deltaTime * 720f);
                bool aimed = _ctx.HasTarget && AnchorOf(_ctx.Target) == c.Pos;
                var opt = c.Option;

                if (opt.Structure != null)
                {
                    if (input.AttackDown && aimed)
                    {
                        _ctx.Send(new BuildStructureCommand { Pos = _ctx.Target, StructureId = opt.Structure.NumericId });
                        Hand?.Swing();
                        c.FinishedAt = Time.time;
                    }
                    return true;
                }

                var s = opt.Shaping;
                if (s.IsHold)
                {
                    // Огонь трением: держать ПКМ; отпустил — прогресс медленно падает
                    if (input.Use && aimed && Shaping.ToolOk(_ctx.Content, s, _ctx.Player.SelectedStack))
                    {
                        c.HoldTimer += Time.deltaTime;
                        if (Hand != null) Hand.Crafting = true;
                        if (c.HoldTimer >= s.Interval)
                        {
                            c.HoldTimer = 0;
                            Hit(c);
                        }
                    }
                    else
                    {
                        c.HoldTimer = Mathf.Max(0f, c.HoldTimer - Time.deltaTime * 0.5f);
                        if (Hand != null) Hand.Crafting = false;
                    }
                    return true;
                }

                if (input.Attack && aimed && _strikeCooldown <= 0f)
                {
                    if (!Shaping.ToolOk(_ctx.Content, s, _ctx.Player.SelectedStack))
                    {
                        if (input.AttackDown) _ctx.ShowToast(_ctx.Loc.Get("ui.shape.need_tool"));
                        return true;
                    }
                    _strikeCooldown = Mathf.Max(0.3f, s.Interval + 0.05f);
                    Hit(c);
                }
                return true;
            }

            // ---- Вход: R по блоку
            if (input.Create && _ctx.HasTarget)
            {
                var options = CollectOptions(world, _ctx.Target, out bool needTool);
                if (options.Count == 0)
                {
                    _ctx.ShowToast(_ctx.Loc.Get(needTool ? "ui.shape.need_tool" : "ui.create.nothing"));
                    return true;
                }
                if (options.Count == 1)
                {
                    EnterCreation(world, options[0], _ctx.Target);
                    return true;
                }
                c.Options.Clear();
                c.Options.AddRange(options);
                c.WheelOpen = true;
                c.WheelCursor = Vector2.zero;
                c.WheelHover = -1;
                _createPressedAt = Time.time;
                _wheelSticky = false;
                return true;
            }
            return false;
        }

        /// <summary>Клетка, которую обрабатываем: для станции — её начало.</summary>
        private Int3 AnchorOf(Int3 pos)
        {
            var m = MultiBlock.OwnerOf(_ctx.Session.Factory, pos);
            return m != null ? m.Pos : pos;
        }

        private void Hit(CreationState c)
        {
            var s = c.Option.Shaping;
            _ctx.Send(new ShapeHitCommand { Pos = _ctx.Target, ShapingId = s.NumericId });
            Hand?.Swing();
            c.Hits++;
            if (s.Rotate) c.TargetAngle += 90f;
            if (c.Hits >= s.Hits) c.FinishedAt = Time.time; // блок раскалывается — режим закроется сам
        }

        private void EnterCreation(VoxelWorld world, CreationOption option, Int3 target)
        {
            var c = _ctx.Creation;
            c.Active = true;
            c.Option = option;
            c.Pos = AnchorOf(target);
            c.Block = world.GetBlock(c.Pos);
            c.Hits = 0;
            c.HoldTimer = 0;
            c.Angle = c.TargetAngle = 0;
            c.FinishedAt = -1f;
            c.WheelOpen = false;
            _strikeCooldown = 0.15f;
        }

        public void ExitCreation()
        {
            var c = _ctx.Creation;
            c.Active = false;
            c.WheelOpen = false;
            c.Option = null;
            c.FinishedAt = -1f;
            if (Hand != null) Hand.Crafting = false;
        }

        // ============================================================ Подсказки

        private Int3 _hintTarget;
        private int _hintItem = int.MinValue, _hintOff = int.MinValue;
        private bool _hintHad;
        private float _hintAt;
        private string _createHint;

        /// <summary>Подсказка под прицелом: крафт в руках, действие ПКМ, режим создания.</summary>
        private void UpdateHints(VoxelWorld world)
        {
            var c = _ctx.Creation;
            var loc = _ctx.Loc;
            if (c.Active)
            {
                _ctx.Hint = c.Option.Structure != null
                    ? loc.Get("hint.create.build")
                    : loc.Get(c.Option.Shaping.IsHold ? "hint.create.hold" : "hint.create.strike");
                return;
            }
            var held = _ctx.Player.SelectedStack;
            var off = _ctx.Player.OffhandStack;
            var craft = HandCrafting.Find(_ctx.Content, held, off, out _);
            if (craft != null)
            {
                _ctx.Hint = loc.Format("hint.handcraft", loc.Get("hand." + craft.Action));
                return;
            }
            if (_ctx.TargetDrop >= 0)
            {
                _ctx.Hint = loc.Get("hint.pickup");
                return;
            }
            if (!_ctx.HasTarget)
            {
                _ctx.Hint = null;
                return;
            }

            // Пересчитываем дорогое (конструкции) только при смене цели или предмета
            int heldKey = held.IsEmpty ? -1 : held.Item, offKey = off.IsEmpty ? -1 : off.Item;
            if (!_hintHad || _hintTarget != _ctx.Target || _hintItem != heldKey || _hintOff != offKey || Time.time - _hintAt > 0.5f)
            {
                _hintHad = true;
                _hintTarget = _ctx.Target;
                _hintItem = heldKey;
                _hintOff = offKey;
                _hintAt = Time.time;
                var options = CollectOptions(world, _ctx.Target, out bool needTool);
                _createHint = options.Count == 1 ? loc.Format("hint.create.one", loc.Get(options[0].NameKey))
                    : options.Count > 1 ? loc.Get("hint.create.many")
                    : needTool ? loc.Get("hint.create.tool") : null;
            }

            var def = world.GetBlockDef(_ctx.Target);
            var owner = MultiBlock.OwnerOf(_ctx.Session.Factory, _ctx.Target);
            ushort anchor = owner != null ? world.GetBlock(owner.Pos) : world.GetBlock(_ctx.Target);
            string use = null;
            if (def.Action == "handbook") use = loc.Get(_ctx.Player.HasHandbook ? "hint.handbook.have" : "hint.handbook.take");
            else if (def.Action == "sleep") use = loc.Get("hint.sleep");
            else
            {
                var inter = Interactions.Find(_ctx.Content, held, anchor);
                if (inter != null) use = loc.Format("hint.use", loc.Get("use." + inter.Id.Replace(':', '.')));
                else if (owner != null && owner.Def.Surface)
                    use = !held.IsEmpty ? loc.Get("hint.surface.put") : (!owner.Input.IsEmpty ? loc.Get("hint.surface.take") : null);
            }
            if (use != null && _createHint != null) _ctx.Hint = use + "   ·   " + _createHint;
            else _ctx.Hint = use ?? _createHint;
        }

        // ============================================================ Предметы на полу

        /// <summary>Положенный на пол предмет под прицелом (ближе блока): луч против небольшой сферы.</summary>
        private void UpdateDropTarget(Vec3 eye, Vec3 look)
        {
            _ctx.TargetDrop = -1;
            float best = _ctx.HasTarget ? _lastHit.Distance + 0.3f : GameServer.ReachDistance;
            foreach (var d in _ctx.Session.DroppedItems)
            {
                if (!d.Placed) continue;
                var c = new Vec3(d.Position.X, d.Position.Y, d.Position.Z);
                var oc = c - eye;
                float t = oc.X * look.X + oc.Y * look.Y + oc.Z * look.Z;
                if (t < 0 || t > best) continue;
                var closest = new Vec3(eye.X + look.X * t, eye.Y + look.Y * t, eye.Z + look.Z * t);
                if ((closest - c).LengthSquared > 0.3f * 0.3f) continue;
                best = t;
                _ctx.TargetDrop = d.Id;
            }
        }
    }
}
