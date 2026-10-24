using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using Genesis.Gameplay;
using Genesis.Simulation;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Интерфейс MVP на IMGUI: хотбар, прицел, отладка, инвентарь, креатив-меню, окно машины
    /// в стиле Mekanism (слоты, шкалы, настройка сторон). Позже переедет на UI Toolkit.
    /// </summary>
    public sealed partial class GameHud : MonoBehaviour
    {
        private ClientContext _ctx;
        private GUIStyle _label, _small, _title, _count, _center;
        private Texture2D _white;
        private int _pickedSlot = -1;
        private Vector2 _creativeScroll;
        private float _fps, _fpsTimer;
        private int _frames;

        private const float Slot = 44f;
        private const float Gap = 4f;

        private static readonly Color PanelColor = UiSkin.Panel;
        private static readonly Color SlotColor = UiSkin.Slot;
        private static readonly Color SlotHover = UiSkin.SlotHover;
        private static readonly Color Accent = UiSkin.Accent;

        public void Init(ClientContext ctx)
        {
            _ctx = ctx;
        }

        private void EnsureStyles()
        {
            UiSkin.Ensure();
            if (_label != null) return;
            _white = Texture2D.whiteTexture;
            _label = new GUIStyle(UiSkin.Label) { richText = true };
            _small = new GUIStyle(UiSkin.Small) { richText = true };
            _title = new GUIStyle(UiSkin.Header) { richText = true };
            _count = new GUIStyle(UiSkin.Count);
            _center = new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter };
        }

        private void Update()
        {
            // Окно ME закрыто — перестать получать снимки сети
            if (_ctx != null && _ctx.Window != UiWindow.Me) _ctx.Session.CloseMe();
            if (_ctx != null) SyncGridWindow();
            if (_ctx != null && _ctx.Window == UiWindow.None && GUIUtility.keyboardControl != 0) GUIUtility.keyboardControl = 0;
            _hoverItem = -1;
            UpdateMinigame();
            _frames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.5f)
            {
                _fps = _frames / _fpsTimer;
                _frames = 0;
                _fpsTimer = 0;
            }
        }

        private void OnGUI()
        {
            if (_ctx == null) return;
            EnsureStyles();
            UiScale.Begin();
            // Кнопки и поля — в стиле игры (общий скин Unity возвращаем в конце кадра)
            var oldButton = GUI.skin.button;
            var oldField = GUI.skin.textField;
            GUI.skin.button = UiSkin.Button;
            GUI.skin.textField = UiSkin.Field;
            try { DrawGui(); }
            finally
            {
                GUI.skin.button = oldButton;
                GUI.skin.textField = oldField;
            }
        }

        private void DrawGui()
        {

            var feet = _ctx.Player.Position.Floor();
            if (!_ctx.Session.World.IsLoaded(feet.X, feet.Y, feet.Z))
            {
                Fill(new Rect(0, 0, UiScale.W, UiScale.H), new Color(0.05f, 0.05f, 0.06f, 1f));
                GUI.Label(new Rect(0, 0, UiScale.W, UiScale.H), _ctx.Loc.Get("ui.loading"), _center);
                return;
            }

            DrawDamageFlash();
            DrawCrosshair();
            DrawHotbar();
            DrawVitals();
            DrawMinimap();
            DrawTargetInfo();
            if (_ctx.ShowDebug) DrawDebug();
            DrawToast();
            TouchControls.Suspended = _ctx.Window != UiWindow.None || _ctx.Paused || _ctx.InMinigame;
            if (!TouchControls.Suspended) TouchControls.Draw(_ctx.Loc);
            else if (TouchControls.Enabled && _ctx.Window != UiWindow.None && !_ctx.InMinigame &&
                     GUI.Button(new Rect(UiScale.W - 60, 8, 52, 52), "X", UiSkin.Button))
                _ctx.Window = UiWindow.None; // на телефоне нет Esc — кнопка закрытия окна

            switch (_ctx.Window)
            {
                case UiWindow.Inventory: DrawInventoryWindow(); break;
                case UiWindow.Creative: DrawCreativeWindow(); break;
                case UiWindow.Machine: DrawMachineWindow(); break;
                case UiWindow.Knowledge: DrawKnowledgeWindow(); break;
                case UiWindow.Stats: DrawStatsWindow(); break;
                case UiWindow.Me: DrawMeWindow(); break;
                case UiWindow.Recipes: DrawRecipesWindow(); break;
                case UiWindow.Map: DrawMapWindow(); break;
                default:
                    if (_ctx.Paused) DrawPauseMenu();
                    break;
            }
            if (_ctx.InMinigame) DrawMinigame();
            DrawCursorStack();
        }

        private void DrawPauseMenu()
        {
            var boot = GetComponent<GenesisBootstrap>();
            if (boot != null && boot.InGameSettings) return;
            Fill(new Rect(0, 0, UiScale.W, UiScale.H), new Color(0, 0, 0, 0.35f));
            var local = _ctx.Session as Genesis.Net.LocalSession;
            if (_worldSettingsEdit != null && local != null)
            {
                DrawWorldSettingsPanel(local);
                return;
            }
            float w = 300, h = (_ctx.Session.CanSave ? 230 : 190) + (local != null ? 40 : 0);
            var p = Panel(w, h, _ctx.Loc.Get("menu.pause"));
            float y = p.y + 48;
            if (GUI.Button(new Rect(p.x + 16, y, w - 32, 32), _ctx.Loc.Get("menu.resume"), UiSkin.AccentButton))
                _ctx.Paused = false;
            y += 40;
            if (boot != null && GUI.Button(new Rect(p.x + 16, y, w - 32, 32), _ctx.Loc.Get("menu.settings")))
                boot.InGameSettings = true;
            y += 40;
            if (local != null)
            {
                if (GUI.Button(new Rect(p.x + 16, y, w - 32, 32), _ctx.Loc.Get("ws.title")))
                    _worldSettingsEdit = local.Server.Settings.Clone();
                y += 40;
            }
            if (_ctx.Session.CanSave)
            {
                if (GUI.Button(new Rect(p.x + 16, y, w - 32, 32), _ctx.Loc.Get("menu.save")))
                    GetComponent<GenesisBootstrap>()?.SaveNow();
                y += 40;
            }
            if (GUI.Button(new Rect(p.x + 16, y, w - 32, 32), _ctx.Loc.Get("menu.to_menu")))
                GetComponent<GenesisBootstrap>()?.ReturnToMenu(null);
        }

        /// <summary>Черновик настроек мира в меню паузы хоста (null — панель закрыта).</summary>
        private WorldSettings _worldSettingsEdit;

        private void DrawWorldSettingsPanel(Genesis.Net.LocalSession local)
        {
            float w = 360, h = 250;
            var p = Panel(w, h, _ctx.Loc.Get("ws.title"));
            float y = p.y + 48;
            y += UiSkin.WorldSettingsEditor(p.x + 16, y, w - 32, _worldSettingsEdit, _ctx.Loc) + 8;
            float bw = (w - 40) / 2f;
            if (GUI.Button(new Rect(p.x + 16, p.yMax - 46, bw, 32), _ctx.Loc.Get("ws.apply"), UiSkin.AccentButton))
            {
                local.Server.ApplySettings(local.LocalPlayer, _worldSettingsEdit);
                _worldSettingsEdit = null;
            }
            if (GUI.Button(new Rect(p.x + 24 + bw, p.yMax - 46, bw, 32), _ctx.Loc.Get("ws.cancel")))
                _worldSettingsEdit = null;
        }

        // ================================================================ Примитивы

        private void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private void Bar(Rect r, float t, Color c) => UiSkin.Bar(r, t, c);

        /// <summary>Рисует слот; возвращает true при клике.</summary>
        private bool DrawSlot(Rect r, ItemStack stack, bool highlight)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            UiSkin.DrawSlot(r, hover);
            if (highlight)
            {
                Fill(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(Accent.r, Accent.g, Accent.b, 0.18f));
                Fill(new Rect(r.x, r.y, r.width, 2), Accent);
                Fill(new Rect(r.x, r.yMax - 2, r.width, 2), Accent);
                Fill(new Rect(r.x, r.y, 2, r.height), Accent);
                Fill(new Rect(r.xMax - 2, r.y, 2, r.height), Accent);
            }
            if (!stack.IsEmpty)
            {
                var icon = _ctx.IconOf(stack.Item);
                if (icon != null) GUI.DrawTexture(new Rect(r.x + 6, r.y + 6, r.width - 12, r.height - 12), icon, ScaleMode.ScaleToFit);
                if (stack.Count > 1) UiSkin.Shadowed(new Rect(r.x, r.y, r.width - 4, r.height - 2), stack.Count.ToString(), _count);
                var tool = _ctx.Content.Item(stack.Item)?.Tool;
                if (tool != null && stack.Meta > 0)
                {
                    float left = 1f - (float)stack.Meta / tool.Durability;
                    var bar = new Rect(r.x + 5, r.yMax - 7, r.width - 10, 3);
                    Fill(bar, new Color(0, 0, 0, 0.8f));
                    Fill(new Rect(bar.x, bar.y, bar.width * left, bar.height), Color.Lerp(new Color(0.9f, 0.2f, 0.1f), new Color(0.3f, 0.9f, 0.3f), left));
                }
                if (hover) _tooltip = _ctx.ItemName(stack.Item);
            }
            return hover && Event.current.type == EventType.MouseDown && Event.current.button == 0 && Consume();
        }

        private static bool Consume()
        {
            Event.current.Use();
            return true;
        }

        private string _tooltip;
        private int _nameKey = int.MinValue;
        private float _nameShownAt;
        private Genesis.Client.Rendering.WorldRenderer _renderer;

        private void DrawTooltip()
        {
            if (string.IsNullOrEmpty(_tooltip)) return;
            var m = Event.current.mousePosition;
            var size = _small.CalcSize(new GUIContent(_tooltip));
            var r = new Rect(m.x + 14, m.y + 10, size.x + 14, size.y + 8);
            if (r.xMax > UiScale.W) r.x = m.x - r.width - 6;
            Fill(r, UiSkin.PanelDark);
            Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, r.height - 2), new Color(0.12f, 0.08f, 0.16f, 0.97f));
            Fill(new Rect(r.x + 1, r.y + 1, r.width - 2, 1), new Color(0.45f, 0.25f, 0.75f, 1f));
            GUI.Label(new Rect(r.x + 7, r.y + 4, size.x, size.y), _tooltip, _small);
            _tooltip = null;
        }

        private Rect Panel(float w, float h, string title, float offsetX = 0f)
        {
            var r = new Rect((UiScale.W - w) / 2f + offsetX, (UiScale.H - h) / 2f, w, h);
            return PanelAt(r, title);
        }

        private Rect PanelAt(Rect r, string title)
        {
            UiSkin.DrawPanel(r);
            UiSkin.PanelTitle(r, title);
            return r;
        }

        // ================================================================ HUD

        private void DrawCrosshair()
        {
            float cx = UiScale.W / 2f, cy = UiScale.H / 2f;
            var shadow = new Color(0, 0, 0, 0.55f);
            Fill(new Rect(cx - 10, cy - 2, 20, 4), shadow);
            Fill(new Rect(cx - 2, cy - 10, 4, 20), shadow);
            var c = _ctx.TargetEnemy >= 0 ? UiSkin.Danger : new Color(1, 1, 1, 0.9f);
            Fill(new Rect(cx - 9, cy - 1, 18, 2), c);
            Fill(new Rect(cx - 1, cy - 9, 2, 18), c);
        }

        private void DrawHotbar()
        {
            float w = PlayerState.HotbarSize * (Slot + Gap) - Gap;
            float x0 = (UiScale.W - w) / 2f;
            float y0 = UiScale.H - Slot - 14;
            TouchControls.HotbarRect = _ctx.Window == UiWindow.None ? new Rect(x0, y0, w, Slot) : Rect.zero;
            TouchControls.HotbarSlots = PlayerState.HotbarSize;
            UiSkin.DrawPanel(new Rect(x0 - 6, y0 - 6, w + 12, Slot + 12));
            var inv = _ctx.Player.Inventory;
            for (int i = 0; i < PlayerState.HotbarSize; i++)
            {
                var r = new Rect(x0 + i * (Slot + Gap), y0, Slot, Slot);
                bool selected = i == _ctx.Player.SelectedSlot;
                if (selected) r = new Rect(r.x - 2, r.y - 4, r.width + 4, r.height + 4);
                DrawSlot(r, inv[i], selected);
                GUI.Label(new Rect(r.x + 4, r.y + 2, 14, 14), (i + 1).ToString(), UiSkin.Tiny);
            }
            // Название выбранного предмета — всплывает при смене слота и плавно гаснет
            var sel = inv[_ctx.Player.SelectedSlot];
            int key = sel.IsEmpty ? -1 : sel.Item * 100 + _ctx.Player.SelectedSlot;
            if (key != _nameKey)
            {
                _nameKey = key;
                _nameShownAt = Time.time;
            }
            float alpha = Mathf.Clamp01(2.5f - (Time.time - _nameShownAt));
            if (!sel.IsEmpty && _ctx.Window == UiWindow.None && alpha > 0)
            {
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, alpha);
                UiSkin.Shadowed(new Rect(0, y0 - 64, UiScale.W, 22), _ctx.ItemName(sel.Item), _center);
                GUI.color = old;
            }
        }

        private void DrawTargetInfo()
        {
            if (!_ctx.HasTarget || _ctx.Window != UiWindow.None) return;
            var world = _ctx.Session.World;
            var def = world.GetBlockDef(_ctx.Target);
            string text = _ctx.Loc.Get(def.NameKey);
            var m = _ctx.Session.Factory.MachineAt(_ctx.Target);
            if (m != null)
                text += $"  <color=#f5a623>[{StatusText(m.Status)}]</color>";
            var conv = _ctx.Session.Factory.ConveyorAt(_ctx.Target);
            if (conv != null) text += $"  ({conv.Items.Count}/4)";
            var size = _label.CalcSize(new GUIContent(text));
            var r = new Rect((UiScale.W - size.x) / 2f - 10, 8, size.x + 20, 28);
            UiSkin.DrawPanel(r);
            if (_ctx.BreakProgress > 0)
                UiSkin.Bar(new Rect(r.x + 4, r.yMax - 5, (r.width - 8), 4), _ctx.BreakProgress, Accent);
            UiSkin.Shadowed(new Rect(r.x + 10, r.y + 4, size.x, 22), text, _label);
        }

        private string StatusText(MachineStatus s)
        {
            switch (s)
            {
                case MachineStatus.Working: return _ctx.Loc.Get("ui.status.working");
                case MachineStatus.NoInput: return _ctx.Loc.Get("ui.status.no_input");
                case MachineStatus.OutputBlocked: return _ctx.Loc.Get("ui.status.output_blocked");
                case MachineStatus.NoEnergy: return _ctx.Loc.Get("ui.status.no_energy");
                case MachineStatus.NoRecipe: return _ctx.Loc.Get("ui.status.no_recipe");
                case MachineStatus.NoSource: return _ctx.Loc.Get("ui.status.no_source");
                case MachineStatus.NoDeposit: return _ctx.Loc.Get("ui.status.no_deposit");
                default: return _ctx.Loc.Get("ui.status.idle");
            }
        }

        private void DrawDebug()
        {
            var p = _ctx.Player.Position;
            var gen = _ctx.Session.World.Generator;
            var col = gen.Column((int)Mathf.Floor(p.X), (int)Mathf.Floor(p.Z));
            if (_renderer == null) _renderer = FindObjectOfType<Genesis.Client.Rendering.WorldRenderer>();
            var renderer = _renderer;
            string ore = col.OreIndex >= 0 ? $"{gen.OreName(col.OreIndex)} (purity {col.OrePurity})" : "-";
            string text =
                $"Genesis MVP  |  {_fps:0} FPS  |  {_ctx.Session.StatusLine}  |  F3 hide\n" +
                $"XYZ: {p.X:0.0} / {p.Y:0.0} / {p.Z:0.0}\n" +
                $"Biome: {col.Biome}  surface {col.Height}  ore: {ore}\n" +
                $"Chunks: {_ctx.Session.World.LoadedCount} loaded, {_ctx.Session.World.PendingCount} gen, " +
                $"{(renderer != null ? renderer.VisibleChunks : 0)} meshes, {(renderer != null ? renderer.QueuedChunks : 0)} queued\n" +
                $"Factory: {_ctx.Session.Factory.Machines.Count} machines, {_ctx.Session.Factory.Conveyors.Count} belts, " +
                $"{_ctx.Session.Factory.Networks.Count} pipe nets, {_ctx.Session.Factory.PowerNetworks.Count} power nets, tick {_ctx.Session.Factory.TickCount}\n" +
                $"<size=11>{_ctx.Loc.Get("ui.help")}</size>";
            var size = new Vector2(Mathf.Min(560, UiScale.W - 20), 120);
            Fill(new Rect(6, 6, size.x, size.y), new Color(0.03f, 0.04f, 0.05f, 0.55f));
            GUI.Label(new Rect(12, 9, size.x - 12, size.y), text, _small);
        }

        private void DrawToast()
        {
            if (string.IsNullOrEmpty(_ctx.Toast) || Time.time > _ctx.ToastUntil) return;
            var size = _label.CalcSize(new GUIContent(_ctx.Toast));
            var r = new Rect((UiScale.W - size.x) / 2f - 14, UiScale.H * 0.3f, size.x + 28, 32);
            UiSkin.DrawPanel(r);
            Fill(new Rect(r.x + 3, r.y + 3, 3, r.height - 6), Accent);
            UiSkin.Shadowed(r, _ctx.Toast, _center);
        }

        // ================================================================ Инвентарь игрока

        /// <summary>Сетка инвентаря игрока (27 основных + хотбар снизу). onClick(slot).</summary>
        private void DrawPlayerGrid(float x, float y, System.Action<int> onClick)
        {
            var inv = _ctx.Player.Inventory;
            for (int row = 0; row < 3; row++)
            for (int col = 0; col < 9; col++)
            {
                int slot = PlayerState.HotbarSize + row * 9 + col;
                var r = new Rect(x + col * (Slot + Gap), y + row * (Slot + Gap), Slot, Slot);
                if (DrawSlot(r, inv[slot], slot == _pickedSlot)) onClick(slot);
            }
            float hy = y + 3 * (Slot + Gap) + 8;
            for (int col = 0; col < 9; col++)
            {
                var r = new Rect(x + col * (Slot + Gap), hy, Slot, Slot);
                if (DrawSlot(r, inv[col], col == _pickedSlot || col == _ctx.Player.SelectedSlot && _pickedSlot < 0)) onClick(col);
            }
        }

        private static float GridWidth => 9 * (Slot + Gap) - Gap;
        private static float GridHeight => 4 * (Slot + Gap) + 8 - Gap;

        private void DrawInventoryWindow()
        {
            DrawGridWindow(2, _ctx.Loc.Get("ui.inventory"));
        }

        private void DrawInventoryWindowOld()
        {
            float w = GridWidth + 24, h = GridHeight + 60;
            var p = Panel(w, h, _ctx.Loc.Get("ui.inventory"), -(CraftPanelWidth + 12) / 2f);
            DrawCraftPanel(new Rect(p.xMax + 12, p.y, CraftPanelWidth, Mathf.Max(h, 420)), "hand", default(Int3));
            DrawPlayerGrid(p.x + 12, p.y + 44, slot =>
            {
                if (_pickedSlot < 0)
                {
                    if (!_ctx.Player.Inventory[slot].IsEmpty) _pickedSlot = slot;
                }
                else
                {
                    _ctx.Send(new SwapSlotsCommand { A = _pickedSlot, B = slot });
                    _pickedSlot = -1;
                }
            });
            DrawTooltip();
        }

        // ================================================================ Креатив (dev)

        private void DrawCreativeWindow()
        {
            float w = GridWidth + 40, h = Mathf.Min(UiScale.H - 40, 520);
            var p = Panel(w, h, _ctx.Loc.Get("ui.creative"));
            if (GUI.Button(new Rect(p.xMax - 230, p.y + 8, 218, 24), _ctx.Loc.Get("era." + _ctx.Session.Knowledge.EraName) + " → +1 (dev)"))
                _ctx.Send(new CreativeAdvanceEraCommand());
            var items = _ctx.Content.Items.Items;
            int perRow = 9;
            int rows = (items.Count + perRow - 1) / perRow;
            var view = new Rect(p.x + 12, p.y + 44, w - 24, h - 56);
            var content = new Rect(0, 0, view.width - 18, rows * (Slot + Gap));
            _creativeScroll = GUI.BeginScrollView(view, _creativeScroll, content);
            for (int i = 0; i < items.Count; i++)
            {
                var r = new Rect((i % perRow) * (Slot + Gap), (i / perRow) * (Slot + Gap), Slot, Slot);
                if (DrawSlot(r, new ItemStack(i, 1), false))
                {
                    bool one = Event.current.shift;
                    _ctx.Send(new CreativeGiveCommand { Item = i, Count = one ? 1 : items[i].MaxStack });
                }
            }
            GUI.EndScrollView();
            DrawTooltip();
        }

        // ================================================================ Окно машины

        private void DrawMachineWindow()
        {
            var factory = _ctx.Session.Factory;
            var m = factory.MachineAt(_ctx.OpenMachine);
            if (m == null)
            {
                _ctx.Window = UiWindow.None;
                return;
            }

            if (m.Def.Behavior == "station")
            {
                DrawStationWindow(m);
                return;
            }
            if (m.Def.Behavior == "me_import" || m.Def.Behavior == "me_export")
            {
                DrawBusWindow(m);
                return;
            }
            if (m.Def.Behavior == "me_provider")
            {
                DrawProviderWindow(m);
                return;
            }

            float w = GridWidth + 24 + 230;
            var machineRecipes = (m.Def.Behavior == "processor" || m.Def.Behavior == "furnace")
                ? _ctx.Content.RecipesFor(m.Def.NumericId) : null;
            int recipeRows = machineRecipes != null && machineRecipes.Count > 1 ? (machineRecipes.Count + 1 + 6) / 7 : 0;
            float h = GridHeight + 300 + Mathf.Max(0, (m.Input.Size - 27 + 8) / 9) * (Slot + Gap);
            h = Mathf.Max(h, GridHeight + 40 + 200 + (recipeRows > 0 ? 24 + recipeRows * 30 : 0) + 24);
            bool hasCrafts = HasCrafts(m.Def.Id);
            var p = Panel(w, h, _ctx.Loc.Get(m.Def.NameKey), hasCrafts ? -(CraftPanelWidth + 12) / 2f : 0f);
            if (hasCrafts) DrawCraftPanel(new Rect(p.xMax + 12, p.y, CraftPanelWidth, h), m.Def.Id, m.Pos);
            float x = p.x + 12, y = p.y + 40;

            // Статус
            GUI.Label(new Rect(x, y, 400, 20), $"<color=#f5a623>{StatusText(m.Status)}</color>", _label);
            y += 26;

            // Слоты: вход → прогресс → выход
            float sx = x;
            for (int i = 0; i < m.Input.Size && i < 9; i++)
            {
                var r = new Rect(sx + (i % 9) * (Slot + Gap), y + (i / 9) * (Slot + Gap), Slot, Slot);
                if (DrawSlot(r, m.Input[i], false))
                    _ctx.Send(new TakeFromMachineCommand { Pos = m.Pos, Kind = MachineInventoryKind.Input, Slot = i });
            }
            if (m.Input.Size > 9)
            {
                // Сундук: показываем остальные строки
                for (int i = 9; i < m.Input.Size; i++)
                {
                    var r = new Rect(sx + (i % 9) * (Slot + Gap), y + (i / 9) * (Slot + Gap), Slot, Slot);
                    if (DrawSlot(r, m.Input[i], false))
                        _ctx.Send(new TakeFromMachineCommand { Pos = m.Pos, Kind = MachineInventoryKind.Input, Slot = i });
                }
            }

            if (m.Def.Behavior != "storage")
            {
                float px = sx + Mathf.Max(1, Mathf.Min(m.Input.Size, 9)) * (Slot + Gap) + 10;
                if (m.Input.Size == 0) px = sx;
                if (m.CurrentRecipe != null || m.Input.Size > 0)
                {
                    Bar(new Rect(px, y + Slot / 2 - 5, 90, 10), m.ProgressFraction, Accent);
                    GUI.Label(new Rect(px, y + Slot / 2 + 6, 120, 18), _ctx.Loc.Get("ui.progress"), _small);
                    px += 100;
                }
                for (int i = 0; i < m.Output.Size; i++)
                {
                    var r = new Rect(px + i * (Slot + Gap), y, Slot, Slot);
                    if (DrawSlot(r, m.Output[i], false))
                        _ctx.Send(new TakeFromMachineCommand { Pos = m.Pos, Kind = MachineInventoryKind.Output, Slot = i });
                }

                // Топливо
                float fy = y + Slot + 10;
                if (m.Fuel.Size > 0)
                {
                    for (int i = 0; i < m.Fuel.Size; i++)
                    {
                        var r = new Rect(sx + i * (Slot + Gap), fy, Slot, Slot);
                        if (DrawSlot(r, m.Fuel[i], false))
                            _ctx.Send(new TakeFromMachineCommand { Pos = m.Pos, Kind = MachineInventoryKind.Fuel, Slot = i });
                    }
                    Bar(new Rect(sx + m.Fuel.Size * (Slot + Gap) + 10, fy + Slot / 2 - 5, 90, 10), m.BurnFraction, new Color(0.95f, 0.4f, 0.1f));
                    GUI.Label(new Rect(sx + m.Fuel.Size * (Slot + Gap) + 10, fy + Slot / 2 + 6, 120, 18), _ctx.Loc.Get("ui.fuel"), _small);
                }

                // Баки
                float ty = fy + (m.Fuel.Size > 0 ? Slot + 14 : 0);
                foreach (var t in m.Tanks)
                {
                    string fluidName = t.Fluid >= 0 ? _ctx.Loc.Get(_ctx.Content.Fluids.Get(t.Fluid).NameKey) : "—";
                    var color = Color.cyan;
                    if (t.Fluid >= 0) ColorUtility.TryParseHtmlString(_ctx.Content.Fluids.Get(t.Fluid).Color, out color);
                    Bar(new Rect(sx, ty, 260, 14), (float)t.Amount / t.Capacity, color);
                    GUI.Label(new Rect(sx + 268, ty - 2, 260, 18), $"{fluidName}: {t.Amount}/{t.Capacity}", _small);
                    ty += 20;
                }
                if (m.Def.Behavior == "boiler")
                {
                    Bar(new Rect(sx, ty, 260, 14), m.Heat, new Color(1f, 0.3f, 0.1f));
                    GUI.Label(new Rect(sx + 268, ty - 2, 200, 18), $"Heat {m.Heat * 100:0}%", _small);
                }
            }

            // Энергия
            if (m.Def.IsPowerNode)
            {
                float ey = p.yMax - GridHeight - 44;
                Bar(new Rect(x, ey, 260, 14), m.EnergyCapacity > 0 ? m.EnergyStored / m.EnergyCapacity : 0, new Color(0.95f, 0.85f, 0.2f));
                string info = $"{_ctx.Loc.Get("ui.energy")}: {m.EnergyStored:0}/{m.EnergyCapacity:0} FE";
                if (m.EnergyRate > 0.01f) info += "   " + _ctx.Loc.Format("ui.energy.gen", m.EnergyRate.ToString("0.#"));
                else if (m.EnergyRate < -0.01f) info += "   " + _ctx.Loc.Format("ui.energy.use", (-m.EnergyRate).ToString("0.#"));
                GUI.Label(new Rect(x + 268, ey - 2, 420, 18), info, _small);
                foreach (var net in factory.PowerNetworks)
                {
                    if (!net.Consumers.Contains(m)) continue;
                    GUI.Label(new Rect(x, ey + 16, 300, 18), _ctx.Loc.Format("ui.energy.net", (net.Satisfaction * 100f).ToString("0")), _small);
                    break;
                }
            }

            // Настройка сторон (справа)
            DrawSideConfig(m, p.xMax - 230, p.y + 40);
            if (recipeRows > 0) DrawRecipeSelector(m, machineRecipes, p.xMax - 230, p.y + 40 + 200);

            // Инвентарь игрока снизу: клик — положить в машину
            float gy = p.yMax - GridHeight - 12;
            DrawPlayerGrid(x, gy, slot => _ctx.Send(new PutIntoMachineCommand { Pos = m.Pos, PlayerSlot = slot }));
            DrawTooltip();
        }

        /// <summary>
        /// Выбор рецепта машины: «Авто» или конкретный рецепт. Выбранный рецепт машина делает всегда,
        /// и лента/манипулятор кладут в неё только его сырьё. Рамка-пунктир — заказ автокрафта ME.
        /// </summary>
        private void DrawRecipeSelector(MachineInstance m, IReadOnlyList<RecipeDef> recipes, float x, float y)
        {
            GUI.Label(new Rect(x, y, 220, 20), _ctx.Loc.Get("ui.recipe_select"), _label);
            y += 24;
            const float cell = 28, gap = 2;
            for (int i = 0; i <= recipes.Count; i++)
            {
                var r = new Rect(x + (i % 7) * (cell + gap), y + (i / 7) * (cell + gap), cell, cell);
                bool hover = r.Contains(Event.current.mousePosition);
                var recipe = i == 0 ? null : recipes[i - 1];
                bool selected = m.LockedRecipe == recipe;
                Fill(r, selected ? new Color(Accent.r, Accent.g, Accent.b, 0.55f) : (hover ? new Color(1, 1, 1, 0.16f) : SlotColor));
                if (recipe == null)
                {
                    GUI.Label(r, "A", new GUIStyle(_label) { alignment = TextAnchor.MiddleCenter });
                    if (hover) _tooltip = _ctx.Loc.Get("ui.recipe_auto");
                }
                else
                {
                    int outItem = recipe.Outputs.Count > 0 ? recipe.Outputs[0].ItemId : -1;
                    var icon = outItem >= 0 ? _ctx.IconOf(outItem) : null;
                    if (icon != null) GUI.DrawTexture(new Rect(r.x + 4, r.y + 4, cell - 8, cell - 8), icon, ScaleMode.ScaleToFit);
                    if (m.QueuedRuns > 0 && m.QueuedRecipe == recipe)
                    {
                        Fill(new Rect(r.x, r.yMax - 3, r.width, 3), new Color(0.45f, 0.75f, 1f));
                    }
                    if (hover)
                    {
                        var sb = new System.Text.StringBuilder();
                        sb.Append(outItem >= 0 ? _ctx.ItemName(outItem) : recipe.Id);
                        sb.Append("  ←  ");
                        for (int k = 0; k < recipe.Inputs.Count; k++)
                        {
                            if (k > 0) sb.Append(", ");
                            sb.Append(recipe.Inputs[k].Count).Append("× ").Append(_ctx.ItemName(recipe.Inputs[k].ItemId));
                        }
                        _tooltip = sb.ToString();
                    }
                }
                if (hover && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    _ctx.Send(new SetRecipeCommand { Pos = m.Pos, Recipe = recipe != null ? recipe.NumericId : -1 });
                    Event.current.Use();
                }
            }
        }

        private static readonly string[] FaceKeys = { "front", "back", "left", "right", "top", "bottom" };

        private void DrawSideConfig(MachineInstance m, float x, float y)
        {
            GUI.Label(new Rect(x, y, 220, 20), _ctx.Loc.Get("ui.sides"), _label);
            y += 24;

            var allowed = new List<SideType> { SideType.None };
            foreach (var s in m.Def.Sides)
                if (SideTypes.TryParse(s, out var t) && !allowed.Contains(t)) allowed.Add(t);

            for (int i = 0; i < 6; i++)
            {
                var current = m.Sides[i];
                var r = new Rect(x, y + i * 28, 210, 24);
                Fill(r, SlotColor);
                GUI.Label(new Rect(r.x + 6, r.y + 3, 70, 20), _ctx.Loc.Get("face." + FaceKeys[i]), _small);
                var btn = new Rect(r.x + 76, r.y + 2, 130, 20);
                Fill(btn, SideColor(current));
                GUI.Label(btn, _ctx.Loc.Get("side." + SideTypes.ToKey(current)), new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter });
                if (btn.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown)
                {
                    int idx = allowed.IndexOf(current);
                    int dirStep = Event.current.button == 1 ? -1 : 1;
                    var next = allowed[(idx + dirStep + allowed.Count) % allowed.Count];
                    _ctx.Send(new SetSideCommand { Pos = m.Pos, Side = (RelativeSide)i, Type = next });
                    Event.current.Use();
                }
            }
        }

        private static Color SideColor(SideType t)
        {
            switch (t)
            {
                case SideType.ItemIn: return new Color(0.2f, 0.4f, 0.8f);
                case SideType.ItemOut: return new Color(0.85f, 0.45f, 0.1f);
                case SideType.FluidIn: return new Color(0.15f, 0.55f, 0.75f);
                case SideType.FluidOut: return new Color(0.1f, 0.35f, 0.55f);
                case SideType.SteamIn: return new Color(0.55f, 0.55f, 0.6f);
                case SideType.SteamOut: return new Color(0.4f, 0.4f, 0.45f);
                case SideType.EnergyIn: return new Color(0.75f, 0.2f, 0.2f);
                default: return new Color(0.15f, 0.15f, 0.15f);
            }
        }
    }
}
