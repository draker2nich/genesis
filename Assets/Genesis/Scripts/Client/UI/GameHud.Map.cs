using System;
using Genesis.Client.Map;
using Genesis.Core;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Миникарта в углу экрана и большая карта (M): колесо — масштаб, ЛКМ и перетаскивание — сдвиг,
    /// ПКМ — поставить метку, Shift+ПКМ — убрать ближайшую, «Ко мне» — центр на игроке.
    /// Север — вверх. Видно только исследованное (где были загружены чанки).
    /// </summary>
    public sealed partial class GameHud
    {
        private Texture2D _arrowTex, _dotTex;
        private float _mapZoom = 2f;
        private Vector2 _mapCenter;          // мировые X, Z центра большой карты
        private bool _mapFollow = true;
        private bool _mapDragging;
        private Vector2 _mapDragFrom;

        private static readonly Color MapBack = new Color(0.07f, 0.08f, 0.09f, 1f);
        private static readonly Color32[] WaypointColors =
        {
            new Color32(242, 163, 58, 255), new Color32(95, 211, 196, 255), new Color32(235, 92, 80, 255),
            new Color32(150, 120, 240, 255), new Color32(120, 214, 120, 255)
        };

        private void EnsureMapTextures()
        {
            if (_arrowTex != null) return;
            const int N = 16;
            _arrowTex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            _dotTex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                // Стрелка вверх (строка 0 — низ текстуры): узкая к верху
                float half = (y + 1) * 0.42f;
                bool arrow = Mathf.Abs(x - 7.5f) <= (N - y) * 0.42f && y >= 1 && !(y < 6 && Mathf.Abs(x - 7.5f) < (6 - y) * 0.9f);
                bool edge = arrow && (Mathf.Abs(x - 7.5f) > (N - y) * 0.42f - 1.2f || y == 1);
                _arrowTex.SetPixel(x, y, arrow ? (edge ? new Color(0.1f, 0.08f, 0.05f) : Color.white) : Color.clear);
                float d = new Vector2(x - 7.5f, y - 7.5f).magnitude;
                _dotTex.SetPixel(x, y, d < 6f ? (d > 4.6f ? new Color(0.05f, 0.05f, 0.05f) : Color.white) : Color.clear);
            }
            _arrowTex.Apply();
            _dotTex.Apply();
        }

        // ============================================================ Общая отрисовка карты

        /// <summary>Нарисовать исследованные регионы в прямоугольнике r с центром center (мировые X,Z) и масштабом scale (пикс/блок).</summary>
        private void DrawMapArea(Rect r, Vector2 center, float scale)
        {
            var map = _ctx.Map;
            Fill(r, MapBack);
            if (map == null) return;
            GUI.BeginGroup(r);
            float halfW = r.width / 2f, halfH = r.height / 2f;
            float minX = center.x - halfW / scale, maxX = center.x + halfW / scale;
            float minZ = center.y - halfH / scale, maxZ = center.y + halfH / scale;
            int rx0 = Int3.FloorDiv(Mathf.FloorToInt(minX), WorldMap.RegionSize), rx1 = Int3.FloorDiv(Mathf.FloorToInt(maxX), WorldMap.RegionSize);
            int rz0 = Int3.FloorDiv(Mathf.FloorToInt(minZ), WorldMap.RegionSize), rz1 = Int3.FloorDiv(Mathf.FloorToInt(maxZ), WorldMap.RegionSize);
            for (int rz = rz0; rz <= rz1; rz++)
            for (int rx = rx0; rx <= rx1; rx++)
            {
                var tex = map.RegionTexture(rx, rz);
                if (tex == null) continue;
                float sx = (rx * WorldMap.RegionSize - center.x) * scale + halfW;
                float sy = (rz * WorldMap.RegionSize - center.y) * scale + halfH;
                GUI.DrawTexture(new Rect(sx, sy, WorldMap.RegionSize * scale, WorldMap.RegionSize * scale), tex);
            }
            GUI.EndGroup();
        }

        private Vector2 WorldToMap(Rect r, Vector2 center, float scale, float x, float z) =>
            new Vector2(r.x + r.width / 2f + (x - center.x) * scale, r.y + r.height / 2f + (z - center.y) * scale);

        private void DrawMarker(Texture2D tex, Vector2 p, float size, float angle, Color color, Rect clip)
        {
            if (!clip.Contains(p)) return;
            var old = GUI.matrix;
            var oldColor = GUI.color;
            GUIUtility.RotateAroundPivot(angle, p);
            GUI.color = color;
            GUI.DrawTexture(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), tex);
            GUI.matrix = old;
            GUI.color = oldColor;
        }

        private void DrawMarkers(Rect r, Vector2 center, float scale, bool labels)
        {
            EnsureMapTextures();
            // Бункер (точка старта)
            var sp = _ctx.Session.World.Generator.SpawnPoint;
            var bp = WorldToMap(r, center, scale, sp.X + 0.5f, sp.Z + 0.5f);
            DrawMarker(_dotTex, bp, 10, 0, new Color(0.95f, 0.85f, 0.3f), r);
            if (labels && r.Contains(bp)) GUI.Label(new Rect(bp.x + 7, bp.y - 9, 140, 18), $"<size=11>{_ctx.Loc.Get("ui.map.bunker")}</size>", _small);

            // Метки
            if (_ctx.Map != null)
                foreach (var w in _ctx.Map.Waypoints)
                {
                    var p = WorldToMap(r, center, scale, w.X + 0.5f, w.Z + 0.5f);
                    DrawMarker(_dotTex, p, 11, 45, w.Color, r);
                    if (labels && r.Contains(p)) UiSkin.Shadowed(new Rect(p.x + 8, p.y - 9, 160, 18), $"<size=11>{w.Name}</size>", _small);
                }

            // Другие игроки
            foreach (var o in _ctx.Session.OtherPlayers)
            {
                var p = WorldToMap(r, center, scale, o.Position.X, o.Position.Z);
                DrawMarker(_arrowTex, p, 12, 180f - o.Yaw, new Color(0.4f, 0.75f, 1f), r);
                if (labels && r.Contains(p)) UiSkin.Shadowed(new Rect(p.x + 8, p.y - 9, 160, 18), $"<size=11>{o.Name}</size>", _small);
            }

            // Игрок
            var me = _ctx.Player.Position;
            var mp = WorldToMap(r, center, scale, me.X, me.Z);
            DrawMarker(_arrowTex, mp, 14, 180f - _ctx.Player.Yaw, Color.white, r);
        }

        // ============================================================ Миникарта

        private void DrawMinimap()
        {
            if (_ctx.Map == null || _ctx.Window != UiWindow.None) return;
            const float Size = 164f;
            var outer = new Rect(Screen.width - Size - 18, 44, Size + 8, Size + 26);
            UiSkin.DrawPanel(outer);
            var r = new Rect(outer.x + 4, outer.y + 4, Size, Size);
            var me = _ctx.Player.Position;
            var center = new Vector2(me.X, me.Z);
            DrawMapArea(r, center, 1.25f);
            DrawMarkers(r, center, 1.25f, false);
            // Север
            GUI.Label(new Rect(r.x + r.width / 2f - 6, r.y + 1, 14, 16), "<size=11><b>N</b></size>", _small);
            var col = _ctx.Session.World.Generator.ColumnAt(Mathf.FloorToInt(me.X), Mathf.FloorToInt(me.Z));
            GUI.Label(new Rect(outer.x + 6, r.yMax + 2, Size, 18),
                $"<size=10>{Mathf.FloorToInt(me.X)}, {Mathf.FloorToInt(me.Y)}, {Mathf.FloorToInt(me.Z)} · {_ctx.Loc.Get("biome." + col.Biome.ToString().ToLowerInvariant())}</size>", _small);
        }

        // ============================================================ Большая карта

        private void DrawMapWindow()
        {
            float w = Screen.width - 60, h = Screen.height - 60;
            var p = Panel(w, h, _ctx.Loc.Get("ui.map"));
            var r = new Rect(p.x + 10, p.y + 40, w - 20, h - 76);
            var me = _ctx.Player.Position;
            if (_mapFollow) _mapCenter = new Vector2(me.X, me.Z);

            var e = Event.current;
            if (r.Contains(e.mousePosition))
            {
                if (e.type == EventType.ScrollWheel)
                {
                    // Масштаб вокруг точки под курсором
                    var before = MapToWorld(r, e.mousePosition);
                    _mapZoom = Mathf.Clamp(_mapZoom * (e.delta.y > 0 ? 0.8f : 1.25f), 0.2f, 8f);
                    var after = MapToWorld(r, e.mousePosition);
                    _mapCenter += before - after;
                    _mapFollow = false;
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && e.button == 0)
                {
                    _mapDragging = true;
                    _mapDragFrom = e.mousePosition;
                    e.Use();
                }
                else if (e.type == EventType.MouseDown && e.button == 1)
                {
                    var wpos = MapToWorld(r, e.mousePosition);
                    if (e.shift) RemoveNearestWaypoint(wpos);
                    else AddWaypointAt(wpos);
                    e.Use();
                }
            }
            if (_mapDragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    _mapCenter -= (e.mousePosition - _mapDragFrom) / _mapZoom;
                    _mapDragFrom = e.mousePosition;
                    _mapFollow = false;
                    e.Use();
                }
                else if (e.rawType == EventType.MouseUp) _mapDragging = false;
            }

            DrawMapArea(r, _mapCenter, _mapZoom);
            DrawMarkers(r, _mapCenter, _mapZoom, true);

            // Нижняя строка: координаты под курсором, кнопки
            float by = r.yMax + 6;
            if (GUI.Button(new Rect(p.x + 10, by, 110, 26), _ctx.Loc.Get("ui.map.me"))) _mapFollow = true;
            if (GUI.Button(new Rect(p.x + 126, by, 34, 26), "+")) _mapZoom = Mathf.Min(8f, _mapZoom * 1.25f);
            if (GUI.Button(new Rect(p.x + 164, by, 34, 26), "−")) _mapZoom = Mathf.Max(0.2f, _mapZoom * 0.8f);
            string info = $"{_ctx.Loc.Get("ui.map.help")}";
            if (r.Contains(e.mousePosition))
            {
                var wpos = MapToWorld(r, e.mousePosition);
                int x = Mathf.FloorToInt(wpos.x), z = Mathf.FloorToInt(wpos.y);
                string biome = _ctx.Map != null && _ctx.Map.IsExplored(x, z)
                    ? _ctx.Loc.Get("biome." + _ctx.Session.World.Generator.ColumnAt(x, z).Biome.ToString().ToLowerInvariant())
                    : _ctx.Loc.Get("ui.map.unknown");
                info = $"X {x}  Z {z} · {biome}    " + info;
            }
            GUI.Label(new Rect(p.x + 210, by + 4, w - 230, 22), $"<size=11>{info}</size>", _small);
        }

        private Vector2 MapToWorld(Rect r, Vector2 screen) =>
            _mapCenter + new Vector2(screen.x - (r.x + r.width / 2f), screen.y - (r.y + r.height / 2f)) / _mapZoom;

        private void AddWaypointAt(Vector2 wpos)
        {
            if (_ctx.Map == null) return;
            int n = _ctx.Map.Waypoints.Count;
            int x = Mathf.FloorToInt(wpos.x), z = Mathf.FloorToInt(wpos.y);
            int y = _ctx.Session.World.Generator.ColumnAt(x, z).Height + 1;
            _ctx.Map.AddWaypoint($"{_ctx.Loc.Get("ui.map.waypoint")} {n + 1}", new Int3(x, y, z), WaypointColors[n % WaypointColors.Length]);
        }

        private void RemoveNearestWaypoint(Vector2 wpos)
        {
            if (_ctx.Map == null) return;
            int best = -1;
            float bestD = 24f / _mapZoom;
            bestD *= bestD;
            for (int i = 0; i < _ctx.Map.Waypoints.Count; i++)
            {
                var w = _ctx.Map.Waypoints[i];
                float d = (new Vector2(w.X, w.Z) - wpos).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            if (best >= 0) _ctx.Map.Waypoints.RemoveAt(best);
        }
    }
}
