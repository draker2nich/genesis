using System.Collections.Generic;
using System.Text;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Справочник (Q): квесты эпохи без наград — пояснения механик. Слева список (выполненные отмечены),
    /// справа — описание выбранного квеста, условие выполнения и схема конструкции (если квест про неё).
    /// </summary>
    public sealed partial class GameHud
    {
        private Vector2 _questScroll, _questTextScroll;
        private string _questSelected;
        private int _handbookEpoch;

        private void DrawHandbookWindow()
        {
            var k = _ctx.Session.Knowledge;
            if (_handbookEpoch <= 0) _handbookEpoch = k.Epoch;
            float w = Mathf.Min(UiScale.W - 40, 860), h = Mathf.Min(UiScale.H - 60, 560);
            var p = Panel(w, h, _ctx.Loc.Get("ui.handbook"));

            // Вкладки эпох (будущие — закрыты)
            float tx = p.x + 12, ty = p.y + 40;
            for (int e = 1; e <= 3; e++)
            {
                bool open = e <= k.Epoch;
                var tr = new Rect(tx, ty, 200, 24);
                GUI.enabled = open;
                if (GUI.Button(tr, _ctx.Loc.Get("epoch." + e), e == _handbookEpoch ? UiSkin.TabActive : UiSkin.Tab))
                {
                    _handbookEpoch = e;
                    _questSelected = null;
                    _questScroll = Vector2.zero;
                }
                GUI.enabled = true;
                tx += 206;
            }

            var quests = k.Quests(_handbookEpoch);
            if (_questSelected == null || quests.Find(q => q.Id == _questSelected) == null)
            {
                _questSelected = null;
                foreach (var q in quests)
                    if (!k.IsUnlocked(q))
                    {
                        _questSelected = q.Id;
                        break;
                    }
                if (_questSelected == null && quests.Count > 0) _questSelected = quests[quests.Count - 1].Id;
            }

            // ---- Список квестов
            const float RowH = 38f;
            float listW = 300;
            var view = new Rect(p.x + 12, p.y + 72, listW, h - 84);
            var content = new Rect(0, 0, listW - 18, quests.Count * RowH);
            int done = 0;
            foreach (var q in quests) if (k.IsUnlocked(q)) done++;
            GUI.Label(new Rect(p.xMax - 200, p.y + 42, 188, 20), $"<size=11><color=#969aa0>{_ctx.Loc.Format("ui.handbook.progress", done, quests.Count)}</color></size>",
                new GUIStyle(_small) { alignment = TextAnchor.MiddleRight });
            _questScroll = GUI.BeginScrollView(view, _questScroll, content);
            for (int i = 0; i < quests.Count; i++)
            {
                var q = quests[i];
                bool isDone = k.IsUnlocked(q);
                bool sel = q.Id == _questSelected;
                var row = new Rect(0, i * RowH, content.width, RowH - 4);
                Fill(row, sel ? new Color(Accent.r, Accent.g, Accent.b, 0.35f) : (isDone ? new Color(0.18f, 0.32f, 0.2f, 0.85f) : SlotColor));
                if (q.Icon != null)
                {
                    var icon = _ctx.IconOf(_ctx.Content.Items.IndexOf(q.Icon));
                    if (icon != null) GUI.DrawTexture(new Rect(row.x + 4, row.y + 3, 28, 28), icon, ScaleMode.ScaleToFit);
                }
                string name = _ctx.Loc.Get(q.NameKey);
                GUI.Label(new Rect(row.x + 38, row.y + 7, row.width - 64, 22), isDone ? $"<color=#9fd49f>{name}</color>" : name, _small);
                if (isDone) GUI.Label(new Rect(row.xMax - 24, row.y + 6, 20, 22), "<color=#8fe08f>✓</color>", _small);
                if (row.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    _questSelected = q.Id;
                    _questTextScroll = Vector2.zero;
                    Event.current.Use();
                }
            }
            GUI.EndScrollView();

            // ---- Описание выбранного квеста
            var node = _questSelected != null ? _ctx.Content.Knowledge.Get(_questSelected) : null;
            var dr = new Rect(view.xMax + 12, p.y + 72, p.xMax - view.xMax - 24, h - 84);
            Fill(dr, new Color(0.08f, 0.09f, 0.1f, 0.6f));
            if (node == null) return;
            bool nodeDone = k.IsUnlocked(node);
            var titleStyle = new GUIStyle(_title) { wordWrap = true };
            GUI.Label(new Rect(dr.x + 10, dr.y + 6, dr.width - 20, 28), _ctx.Loc.Get(node.NameKey), titleStyle);
            string status = nodeDone ? $"<color=#8fe08f>{_ctx.Loc.Get("ui.handbook.done")}</color>" : $"<color=#f2a33a>{_ctx.Loc.Get("ui.handbook.active")}</color>";
            GUI.Label(new Rect(dr.x + 10, dr.y + 34, dr.width - 20, 18), $"<size=11>{status}</size>", _small);

            var sb = new StringBuilder();
            string descKey = node.NameKey + ".desc";
            string desc = _ctx.Loc.Get(descKey);
            if (desc != descKey) sb.Append(desc);
            sb.Append("\n\n<color=#969aa0>").Append(_ctx.Loc.Get("ui.handbook.goal")).Append(": ").Append(QuestGoal(node)).Append("</color>");
            string scheme = StructureScheme(node);
            if (scheme != null) sb.Append("\n\n").Append(scheme);

            var textStyle = new GUIStyle(_small) { wordWrap = true, richText = true, fontSize = 13 };
            var textView = new Rect(dr.x + 10, dr.y + 58, dr.width - 20, dr.height - 66);
            string text = sb.ToString();
            float textH = textStyle.CalcHeight(new GUIContent(text), textView.width - 18) + 10;
            _questTextScroll = GUI.BeginScrollView(textView, _questTextScroll, new Rect(0, 0, textView.width - 18, textH));
            GUI.Label(new Rect(0, 0, textView.width - 18, textH), text, textStyle);
            GUI.EndScrollView();
        }

        /// <summary>Условие выполнения квеста одной строкой.</summary>
        private string QuestGoal(KnowledgeNode n)
        {
            var t = n.Trigger;
            var c = _ctx.Content;
            switch (t.Type)
            {
                case "handcraft":
                {
                    var h = c.HandCrafts.Get(t.Target);
                    return _ctx.Loc.Format("ui.goal.handcraft", h?.Output != null ? _ctx.ItemName(h.Output.ItemId) : t.Target);
                }
                case "shape":
                {
                    var s = c.Shapings.Get(t.Target);
                    return _ctx.Loc.Format("ui.goal.shape", s != null ? _ctx.Loc.Get(s.NameKey) : t.Target);
                }
                case "build":
                {
                    var s = c.Structures.Get(t.Target);
                    return _ctx.Loc.Format("ui.goal.build", s != null ? _ctx.Loc.Get(s.NameKey) : t.Target);
                }
                case "use":
                    return _ctx.Loc.Get("use." + t.Target.Replace(':', '.'));
                case "event":
                    return _ctx.Loc.Get("ui.goal.event." + t.Target);
                case "craft":
                {
                    var cr = c.Crafts.Get(t.Target);
                    return _ctx.Loc.Format("ui.trigger.craft", cr != null && cr.Outputs.Count > 0 ? _ctx.ItemName(cr.Outputs[0].ItemId) : t.Target);
                }
                case "place":
                {
                    var b = c.Blocks.Get(t.Target);
                    return _ctx.Loc.Format("ui.trigger.place", b != null ? _ctx.Loc.Get(b.NameKey) : t.Target);
                }
                case "obtain":
                    return _ctx.Loc.Format("ui.trigger.obtain", _ctx.ItemName(c.Items.IndexOf(t.Target)));
                default:
                    return _ctx.Loc.Get("ui.trigger.start");
            }
        }

        /// <summary>Схема конструкции по слоям (вид сверху): буквы и легенда — для квестов «собрать».</summary>
        private string StructureScheme(KnowledgeNode n)
        {
            if (n.Trigger.Type != "build") return null;
            var st = _ctx.Content.Structures.Get(n.Trigger.Target);
            if (st == null) return null;
            var sb = new StringBuilder();
            sb.Append("<b>").Append(_ctx.Loc.Get("ui.handbook.scheme")).Append("</b>");
            for (int y = 0; y < st.Layers.Count; y++)
            {
                sb.Append("\n").Append(_ctx.Loc.Format("ui.handbook.layer", y + 1)).Append('\n');
                var layer = st.Layers[y];
                // Север (z+) — сверху: строки шаблона идут по z, выводим с конца
                for (int z = layer.Count - 1; z >= 0; z--)
                {
                    sb.Append("<color=#f2a33a>");
                    foreach (char ch in layer[z] ?? "") sb.Append(ch == ' ' ? "·" : ch.ToString()).Append(' ');
                    sb.Append("</color>\n");
                }
            }
            sb.Append('\n');
            var legend = new List<string>();
            if (st.Key != null)
                foreach (var kv in st.Key)
                {
                    string what = kv.Value == "*" ? _ctx.Loc.Get("ui.handbook.any_block")
                        : (_ctx.Content.Blocks.Get(kv.Value) != null ? _ctx.Loc.Get(_ctx.Content.Blocks.Get(kv.Value).NameKey) : kv.Value);
                    legend.Add(kv.Key + " — " + what);
                }
            sb.Append(string.Join("\n", legend));
            return sb.ToString();
        }
    }
}
