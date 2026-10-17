using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Client.UI;
using Genesis.Modding;
using Genesis.Net;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>Главное меню: миры, подключение к другу, моды, настройки.</summary>
    public sealed partial class GenesisBootstrap
    {
        private enum MenuScreen { Main, Play, Join, Mods, Settings }

        private MenuScreen _screen = MenuScreen.Main;
        private Vector2 _worldsScroll, _modsScroll;
        private List<WorldEntry> _worlds;
        private string _deleteArmed;
        private string _modsStatus;
        private bool _modsChanged;

        private sealed class WorldEntry
        {
            public string Name;
            public DateTime Modified;
        }

        private string SavesRoot => Path.Combine(Application.persistentDataPath, "saves");

        private void RefreshWorlds()
        {
            _worlds = new List<WorldEntry>();
            if (!Directory.Exists(SavesRoot)) return;
            foreach (var dir in Directory.GetDirectories(SavesRoot))
            {
                string meta = Path.Combine(dir, "world.dat");
                if (!File.Exists(meta)) continue;
                _worlds.Add(new WorldEntry { Name = Path.GetFileName(dir), Modified = File.GetLastWriteTime(meta) });
            }
            _worlds.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        }

        // ============================================================ Отрисовка

        private void OnGUI()
        {
            if (_mode == Mode.Playing)
            {
                if (InGameSettings && _ctx != null && !_ctx.Paused) InGameSettings = false;
                if (InGameSettings)
                {
                    UiSkin.Ensure();
                    UiSkin.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0, 0, 0, 0.45f));
                    DrawSettingsScreen();
                }
                return;
            }
            UiSkin.Ensure();
            DrawMenuBackground();

            if (_mode == Mode.Connecting)
            {
                var r = CenterPanel(420, 150);
                UiSkin.PanelTitle(r, _loc.Get("menu.join"));
                GUI.Label(new Rect(r.x + 16, r.y + 50, r.width - 32, 24), _loc.Get("menu.connecting") + " " + _address + Dots(), UiSkin.Label);
                if (GUI.Button(new Rect(r.x + 16, r.yMax - 48, r.width - 32, 34), _loc.Get("menu.cancel"), UiSkin.Button)) ReturnToMenu(null);
                return;
            }

            switch (_screen)
            {
                case MenuScreen.Main: DrawMainScreen(); break;
                case MenuScreen.Play: DrawPlayScreen(); break;
                case MenuScreen.Join: DrawJoinScreen(); break;
                case MenuScreen.Mods: DrawModsScreen(); break;
                case MenuScreen.Settings: DrawSettingsScreen(); break;
            }

            GUI.Label(new Rect(12, Screen.height - 24, 400, 20), "Genesis MVP · stage 5 · crafting, life & maps", UiSkin.Tiny);
        }

        private static string Dots() => new string('.', 1 + (int)(Time.unscaledTime * 2) % 3);

        /// <summary>Фон: панорама мира (если показывается) с затемнением, иначе градиент «пыльного неба».</summary>
        private void DrawMenuBackground()
        {
            if (_panoramaActive)
            {
                // Виньетка поверх живой панорамы
                UiSkin.Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.02f, 0.03f, 0.05f, 0.25f));
                for (int i = 0; i < 24; i++)
                {
                    float a = 0.35f * (1f - i / 24f);
                    UiSkin.Fill(new Rect(0, i * 6, Screen.width, 6), new Color(0, 0, 0, a * 0.8f));
                    UiSkin.Fill(new Rect(0, Screen.height - (i + 1) * 6, Screen.width, 6), new Color(0, 0, 0, a));
                }
                return;
            }
            int bands = 48;
            for (int i = 0; i < bands; i++)
            {
                float t = i / (float)(bands - 1);
                var c = Color.Lerp(new Color(0.16f, 0.2f, 0.27f), new Color(0.04f, 0.045f, 0.06f), t);
                UiSkin.Fill(new Rect(0, Screen.height * i / (float)bands, Screen.width, Screen.height / (float)bands + 1), c);
            }
        }

        private static Rect CenterPanel(float w, float h, float yOffset = 0)
        {
            w = Mathf.Min(w, Screen.width - 24);
            h = Mathf.Min(h, Screen.height - 24);
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f + yOffset, w, h);
            UiSkin.DrawPanel(r);
            return r;
        }

        private void DrawLogo(float centerX, float y, float px)
        {
            const string logo = "GENESIS";
            float w = PixelFont.Width(logo, px);
            float bob = Mathf.Sin(Time.unscaledTime * 1.3f) * px * 0.5f;
            PixelFont.Draw(centerX - w / 2f, y + bob, logo, px,
                new Color(1f, 0.82f, 0.45f), new Color(0.85f, 0.42f, 0.12f), new Color(0, 0, 0, 0.55f));
        }

        // ============================================================ Главный экран

        private void DrawMainScreen()
        {
            float px = Mathf.Clamp(Screen.width / 140f, 5f, 11f);
            float logoY = Screen.height * 0.16f;
            DrawLogo(Screen.width / 2f, logoY, px);
            var tag = new GUIStyle(UiSkin.Center) { fontSize = 15 };
            tag.normal.textColor = UiSkin.TextDim;
            GUI.Label(new Rect(0, logoY + px * 8 + 10, Screen.width, 24), _loc.Get("menu.tagline"), tag);

            float w = 300, x = (Screen.width - w) / 2f, y = logoY + px * 8 + 60;
            if (GUI.Button(new Rect(x, y, w, 40), _loc.Get("menu.play"), UiSkin.AccentButton))
            {
                RefreshWorlds();
                _screen = MenuScreen.Play;
            }
            y += 48;
            if (GUI.Button(new Rect(x, y, w, 36), _loc.Get("menu.join"), UiSkin.Button)) _screen = MenuScreen.Join;
            y += 44;
            if (GUI.Button(new Rect(x, y, w / 2 - 4, 36), _loc.Get("menu.mods") + $" ({_mods.LoadOrder.Count})", UiSkin.Button)) _screen = MenuScreen.Mods;
            if (GUI.Button(new Rect(x + w / 2 + 4, y, w / 2 - 4, 36), _loc.Get("menu.settings"), UiSkin.Button)) _screen = MenuScreen.Settings;
            y += 44;
            if (GUI.Button(new Rect(x, y, w, 36), _loc.Get("menu.quit"), UiSkin.Button))
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
            y += 50;
            DrawMenuError(x, y, w);
        }

        private void DrawMenuError(float x, float y, float w)
        {
            if (string.IsNullOrEmpty(_menuError)) return;
            var err = new GUIStyle(UiSkin.Label) { wordWrap = true, alignment = TextAnchor.UpperCenter };
            err.normal.textColor = UiSkin.Danger;
            GUI.Label(new Rect(x - 60, y, w + 120, 60), _menuError, err);
        }

        private bool BackButton(Rect panel)
        {
            if (GUI.Button(new Rect(panel.x + 16, panel.yMax - 50, 120, 34), "← " + _loc.Get("menu.back"), UiSkin.Button))
            {
                _screen = MenuScreen.Main;
                _deleteArmed = null;
                return true;
            }
            return false;
        }

        private void Field(ref float y, float x, float w, string label, ref string value, int max = 32)
        {
            GUI.Label(new Rect(x, y + 5, 150, 24), label, UiSkin.Label);
            value = GUI.TextField(new Rect(x + 150, y, w - 150, 30), value ?? "", max, UiSkin.Field);
            y += 38;
        }

        // ============================================================ Игра: миры

        private void DrawPlayScreen()
        {
            var p = CenterPanel(720, 520);
            UiSkin.PanelTitle(p, _loc.Get("menu.play"));
            float x = p.x + 16, y = p.y + 48;
            float listW = 340;

            // Список миров
            GUI.Label(new Rect(x, y, listW, 22), _loc.Get("menu.worlds"), UiSkin.Label);
            var view = new Rect(x, y + 26, listW, p.height - 140);
            UiSkin.Fill(view, UiSkin.PanelDark);
            int count = _worlds?.Count ?? 0;
            _worldsScroll = GUI.BeginScrollView(view, _worldsScroll, new Rect(0, 0, listW - 18, Mathf.Max(count, 1) * 54));
            if (count == 0) GUI.Label(new Rect(10, 10, listW - 30, 40), _loc.Get("menu.no_worlds"), UiSkin.Tiny);
            for (int i = 0; i < count; i++)
            {
                var wld = _worlds[i];
                var row = new Rect(4, i * 54 + 4, listW - 26, 48);
                bool selected = wld.Name == _worldName;
                bool hover = row.Contains(Event.current.mousePosition);
                UiSkin.DrawSlot(row, hover || selected);
                if (selected) UiSkin.Fill(new Rect(row.x, row.y, 3, row.height), UiSkin.Accent);
                GUI.Label(new Rect(row.x + 12, row.y + 6, row.width - 20, 22), wld.Name, UiSkin.Label);
                GUI.Label(new Rect(row.x + 12, row.y + 26, row.width - 20, 18), wld.Modified.ToString("dd.MM.yyyy HH:mm"), UiSkin.Tiny);
                if (hover && Event.current.type == EventType.MouseDown)
                {
                    Event.current.Use();
                    _worldName = wld.Name;
                    _deleteArmed = null;
                    if (Event.current.clickCount == 2) StartLocal(false);
                }
            }
            GUI.EndScrollView();

            // Правая колонка: новый мир / действия
            float rx = x + listW + 20, rw = p.xMax - 16 - rx, ry = y;
            GUI.Label(new Rect(rx, ry, rw, 22), _loc.Get("menu.player"), UiSkin.Label);
            ry += 26;
            _playerName = GUI.TextField(new Rect(rx, ry, rw, 30), _playerName ?? "", 24, UiSkin.Field);
            ry += 44;
            GUI.Label(new Rect(rx, ry, rw, 22), _loc.Get("menu.world_name"), UiSkin.Label);
            ry += 26;
            _worldName = GUI.TextField(new Rect(rx, ry, rw, 30), _worldName ?? "", 32, UiSkin.Field);
            ry += 40;
            GUI.Label(new Rect(rx, ry, rw, 22), _loc.Get("menu.seed") + "  <size=11><color=#969aa0>" + _loc.Get("menu.seed_hint") + "</color></size>", UiSkin.Label);
            ry += 26;
            _seedText = GUI.TextField(new Rect(rx, ry, rw - 40, 30), _seedText ?? "", 20, UiSkin.Field);
            if (GUI.Button(new Rect(rx + rw - 34, ry, 34, 30), "?", UiSkin.Button))
                _seedText = UnityEngine.Random.Range(1, int.MaxValue).ToString();
            ry += 48;

            bool exists = _worlds != null && _worlds.Exists(w => w.Name == _worldName);
            if (GUI.Button(new Rect(rx, ry, rw, 40), exists ? _loc.Get("menu.continue") : _loc.Get("menu.create"), UiSkin.AccentButton))
                StartLocal(false);
            ry += 48;
            if (GUI.Button(new Rect(rx, ry, rw, 34), _loc.Get("menu.host"), UiSkin.Button)) StartLocal(true);
            ry += 40;
            GUI.Label(new Rect(rx, ry, rw, 34), $"<size=11><color=#969aa0>{_loc.Get("menu.host_hint")} {TransportName()} · {_loc.Get("menu.port")} {_portText}</color></size>",
                new GUIStyle(UiSkin.Small) { wordWrap = true });
            ry += 40;

            if (exists)
            {
                string label = _deleteArmed == _worldName ? _loc.Get("menu.confirm_delete") : _loc.Get("menu.delete");
                var style = new GUIStyle(UiSkin.Button);
                style.normal.textColor = UiSkin.Danger;
                if (GUI.Button(new Rect(rx, p.yMax - 50, rw, 34), label, style))
                {
                    if (_deleteArmed == _worldName)
                    {
                        try { Directory.Delete(Path.Combine(SavesRoot, _worldName), true); }
                        catch (Exception e) { _menuError = e.Message; }
                        _deleteArmed = null;
                        RefreshWorlds();
                    }
                    else _deleteArmed = _worldName;
                }
            }

            BackButton(p);
            DrawMenuError(p.x + 160, p.yMax + 8, p.width - 320);
        }

        private string TransportName()
        {
            var providers = TransportRegistry.All;
            return providers.Count > 0 ? providers[_transportIndex % providers.Count].Name : "-";
        }

        // ============================================================ Подключение

        private void DrawJoinScreen()
        {
            var p = CenterPanel(460, 380);
            UiSkin.PanelTitle(p, _loc.Get("menu.join"));
            float x = p.x + 16, w = p.width - 32, y = p.y + 52;
            Field(ref y, x, w, _loc.Get("menu.player"), ref _playerName, 24);
            Field(ref y, x, w, _loc.Get("menu.address"), ref _address, 64);
            Field(ref y, x, w, _loc.Get("menu.port"), ref _portText, 6);
            GUI.Label(new Rect(x, y + 5, 150, 24), _loc.Get("menu.transport"), UiSkin.Label);
            if (GUI.Button(new Rect(x + 150, y, w - 150, 30), TransportName(), UiSkin.Button))
                _transportIndex = (_transportIndex + 1) % Math.Max(1, TransportRegistry.All.Count);
            y += 46;
            if (GUI.Button(new Rect(x, y, w, 40), _loc.Get("menu.connect"), UiSkin.AccentButton)) StartRemote();
            BackButton(p);
            DrawMenuError(p.x, p.yMax + 8, p.width);
        }

        // ============================================================ Моды

        private void DrawModsScreen()
        {
            var p = CenterPanel(760, 540);
            UiSkin.PanelTitle(p, _loc.Get("menu.mods"));
            float x = p.x + 16, w = p.width - 32, y = p.y + 48;

            var mods = _mods.Mods;
            var view = new Rect(x, y, w, p.height - 150);
            UiSkin.Fill(view, UiSkin.PanelDark);
            const float RowH = 64;
            _modsScroll = GUI.BeginScrollView(view, _modsScroll, new Rect(0, 0, w - 18, mods.Count * RowH));
            for (int i = 0; i < mods.Count; i++)
            {
                var m = mods[i];
                var row = new Rect(4, i * RowH + 4, w - 26, RowH - 6);
                UiSkin.DrawSlot(row, false);
                UiSkin.Fill(new Rect(row.x, row.y, 3, row.height), m.Active ? UiSkin.Good : (m.Enabled ? UiSkin.Danger : UiSkin.TextDim));

                string title = $"<b>{m.Manifest.Name}</b>  <size=11><color=#969aa0>{m.Id} · v{m.Manifest.Version}" +
                               (m.Manifest.Author != null ? " · " + m.Manifest.Author : "") + $" · {SourceName(m.Source)}" +
                               (m.HasCode ? " · " + _loc.Get("menu.mods_code") : "") + "</color></size>";
                GUI.Label(new Rect(row.x + 12, row.y + 6, row.width - 160, 22), title, UiSkin.Label);
                string desc = m.Errors.Count > 0 ? $"<color=#eb5c50>{string.Join("; ", m.Errors)}</color>" : (m.Manifest.Description ?? "");
                GUI.Label(new Rect(row.x + 12, row.y + 28, row.width - 160, 28), $"<size=11>{desc}</size>", new GUIStyle(UiSkin.Small) { wordWrap = true });

                if (!m.IsBase)
                {
                    var btn = new Rect(row.xMax - 136, row.y + 12, 124, 30);
                    if (GUI.Button(btn, m.Enabled ? _loc.Get("menu.mods_enabled") : _loc.Get("menu.mods_disabled"), m.Enabled ? UiSkin.TabActive : UiSkin.Tab))
                    {
                        _mods.SetEnabled(m.Id, !m.Enabled);
                        _modsChanged = true;
                    }
                }
                else GUI.Label(new Rect(row.xMax - 136, row.y + 18, 124, 22), _loc.Get("menu.mods_builtin"), UiSkin.Tiny);
            }
            GUI.EndScrollView();

            y = view.yMax + 10;
            float bw = (w - 16) / 3f;
            if (GUI.Button(new Rect(x, y, bw, 34), _loc.Get("menu.mods_folder"), UiSkin.Button))
            {
                Directory.CreateDirectory(UserModsRoot);
                Application.OpenURL("file://" + UserModsRoot);
            }
            GUI.enabled = _modsChanged;
            if (GUI.Button(new Rect(x + bw + 8, y, bw, 34), _loc.Get("menu.mods_apply"), _modsChanged ? UiSkin.AccentButton : UiSkin.Button))
            {
                try
                {
                    LoadContent();
                    _modsStatus = _loc.Get("menu.mods_applied");
                }
                catch (Exception e)
                {
                    _modsStatus = e.Message;
                }
                _modsChanged = false;
            }
            GUI.enabled = ModLoader.Workshop != null && ModLoader.Workshop.Available;
            if (GUI.Button(new Rect(x + 2 * (bw + 8), y, bw, 34), _loc.Get("menu.mods_publish"), UiSkin.Button))
            {
                var local = _mods.Mods.Find(m => m.Source == ModSource.Local && m.Active);
                if (local != null)
                {
                    _modsStatus = "Workshop: " + local.Id + "...";
                    ModLoader.Workshop.Publish(local, "Update from Genesis", err => _modsStatus = err ?? "Workshop: OK");
                }
            }
            GUI.enabled = true;
            y += 40;
            string status = _modsStatus ?? (ModLoader.Workshop != null && ModLoader.Workshop.Available ? "" : _loc.Get("menu.mods_workshop_off"));
            GUI.Label(new Rect(x + 140, y, w - 140, 20), $"<size=11><color=#969aa0>{status}</color></size>", UiSkin.Small);
            BackButton(p);
        }

        private string SourceName(ModSource s)
        {
            switch (s)
            {
                case ModSource.Builtin: return _loc.Get("menu.mods_src_builtin");
                case ModSource.Workshop: return "Workshop";
                default: return _loc.Get("menu.mods_src_local");
            }
        }

        // ============================================================ Настройки

        /// <summary>Настройки поверх игры (из меню паузы). Графика применяется сразу.</summary>
        public bool InGameSettings;

        private void DrawSettingsScreen()
        {
            var p = CenterPanel(520, 500);
            UiSkin.PanelTitle(p, _loc.Get("menu.settings"));
            float x = p.x + 16, w = p.width - 32, y = p.y + 54;

            GUI.Label(new Rect(x, y + 5, 200, 24), _loc.Get("settings.language"), UiSkin.Label);
            if (GUI.Button(new Rect(x + 220, y, w - 220, 30), GameSettings.Language == "ru" ? "Русский" : "English", UiSkin.Button))
            {
                GameSettings.Language = GameSettings.Language == "ru" ? "en" : "ru";
                _loc.Language = GameSettings.Language;
                GameSettings.Save();
            }
            y += 42;
            GameSettings.ViewRadius = Mathf.RoundToInt(Slider(ref y, x, w, _loc.Get("settings.view"), GameSettings.ViewRadius, 4, 16, GameSettings.ViewRadius + " " + _loc.Get("settings.chunks")));
            GameSettings.Fov = Mathf.Round(Slider(ref y, x, w, _loc.Get("settings.fov"), GameSettings.Fov, 55, 100, GameSettings.Fov.ToString("0") + "°"));
            GameSettings.MouseSensitivity = Slider(ref y, x, w, _loc.Get("settings.mouse"), GameSettings.MouseSensitivity, 0.3f, 8f, GameSettings.MouseSensitivity.ToString("0.0"));
            GameSettings.Shadows = Toggle(ref y, x, w, _loc.Get("settings.shadows"), GameSettings.Shadows);
            GameSettings.PostProcessing = Toggle(ref y, x, w, _loc.Get("settings.post"), GameSettings.PostProcessing);
            GameSettings.Clouds = Toggle(ref y, x, w, _loc.Get("settings.clouds"), GameSettings.Clouds);
            GameSettings.ViewBobbing = Toggle(ref y, x, w, _loc.Get("settings.bob"), GameSettings.ViewBobbing);
            ViewRadius = GameSettings.ViewRadius;
            if (GUI.changed) ApplyGraphicsSettings();
            if (InGameSettings)
                GUI.Label(new Rect(x, p.yMax - 84, w, 22), _loc.Get("settings.view_next"), UiSkin.Tiny);

            if (BackButton(p))
            {
                GameSettings.Save();
                ApplyGraphicsSettings();
                InGameSettings = false;
            }
        }

        private float Slider(ref float y, float x, float w, string label, float value, float min, float max, string shown)
        {
            GUI.Label(new Rect(x, y + 3, 200, 24), label, UiSkin.Label);
            float v = GUI.HorizontalSlider(new Rect(x + 220, y + 9, w - 300, 20), value, min, max);
            GUI.Label(new Rect(x + w - 70, y + 3, 70, 24), shown, UiSkin.Label);
            y += 36;
            return v;
        }

        private bool Toggle(ref float y, float x, float w, string label, bool value)
        {
            GUI.Label(new Rect(x, y + 5, 200, 24), label, UiSkin.Label);
            if (GUI.Button(new Rect(x + 220, y, 110, 30), value ? _loc.Get("settings.on") : _loc.Get("settings.off"), value ? UiSkin.TabActive : UiSkin.Tab))
                value = !value;
            y += 38;
            return value;
        }
    }
}
