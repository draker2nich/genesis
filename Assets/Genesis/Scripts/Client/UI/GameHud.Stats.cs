using System.Collections.Generic;
using Genesis.Core;
using Genesis.Data;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>Этап 4: статистика производства и калькулятор соотношений машин (ТЗ, раздел 7).</summary>
    public sealed partial class GameHud
    {
        private Vector2 _statsScroll;
        private int _calcRecipe;
        private string _calcTarget = "60";

        private void DrawStatsWindow()
        {
            float w = Mathf.Min(Screen.width - 40, 860), h = Mathf.Min(Screen.height - 60, 560);
            var p = Panel(w, h, _ctx.Loc.Get("ui.stats"));
            float colW = w * 0.52f;
            DrawStatsTable(new Rect(p.x + 12, p.y + 40, colW - 18, h - 52));
            DrawCalculator(new Rect(p.x + colW + 6, p.y + 40, w - colW - 18, h - 52));
        }

        private void DrawStatsTable(Rect r)
        {
            var stats = _ctx.Session.Factory.Stats;
            if (!_ctx.Session.CanSave)
            {
                GUI.Label(r, _ctx.Loc.Get("ui.stats.host_only"), _small);
                return;
            }
            var items = new List<int>(stats.Items);
            items.Sort((a, b) => stats.ProducedPerMinute(b).CompareTo(stats.ProducedPerMinute(a)));

            Fill(new Rect(r.x, r.y, r.width, 22), new Color(1, 1, 1, 0.08f));
            GUI.Label(new Rect(r.x + 34, r.y + 2, r.width * 0.45f, 20), _ctx.Loc.Get("ui.stats.item"), _small);
            GUI.Label(new Rect(r.x + r.width * 0.5f, r.y + 2, 90, 20), _ctx.Loc.Get("ui.stats.produced"), _small);
            GUI.Label(new Rect(r.x + r.width * 0.75f, r.y + 2, 90, 20), _ctx.Loc.Get("ui.stats.consumed"), _small);

            if (items.Count == 0)
            {
                GUI.Label(new Rect(r.x, r.y + 30, r.width, 20), _ctx.Loc.Get("ui.stats.empty"), _small);
                return;
            }

            const float RowH = 26;
            var view = new Rect(r.x, r.y + 26, r.width, r.height - 26);
            _statsScroll = GUI.BeginScrollView(view, _statsScroll, new Rect(0, 0, r.width - 18, items.Count * RowH));
            for (int i = 0; i < items.Count; i++)
            {
                int item = items[i];
                var row = new Rect(0, i * RowH, r.width - 18, RowH - 2);
                if (i % 2 == 0) Fill(row, new Color(1, 1, 1, 0.03f));
                var icon = _ctx.IconOf(item);
                if (icon != null) GUI.DrawTexture(new Rect(row.x + 4, row.y + 1, 22, 22), icon, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(row.x + 32, row.y + 3, row.width * 0.45f, 20), _ctx.ItemName(item), _small);
                long prod = stats.ProducedPerMinute(item), cons = stats.ConsumedPerMinute(item);
                GUI.Label(new Rect(row.x + row.width * 0.5f, row.y + 3, 90, 20), prod > 0 ? $"<color=#7fd67f>{prod}</color>" : "—", _small);
                GUI.Label(new Rect(row.x + row.width * 0.75f, row.y + 3, 90, 20), cons > 0 ? $"<color=#f0a060>{cons}</color>" : "—", _small);
            }
            GUI.EndScrollView();
        }

        private void DrawCalculator(Rect r)
        {
            Fill(r, new Color(1, 1, 1, 0.04f));
            GUI.Label(new Rect(r.x + 10, r.y + 6, r.width - 20, 22), "<b>" + _ctx.Loc.Get("ui.calc") + "</b>", _label);

            // Только открытые машинные рецепты
            var recipes = new List<RecipeDef>();
            foreach (var rec in _ctx.Content.Recipes.Items)
                if (_ctx.Session.Knowledge.IsRecipeUnlocked(rec) && rec.Outputs.Count > 0) recipes.Add(rec);
            if (recipes.Count == 0) return;
            _calcRecipe = (_calcRecipe % recipes.Count + recipes.Count) % recipes.Count;
            var recipe = recipes[_calcRecipe];
            var machine = _ctx.Content.Machines.Get(recipe.Machine);
            var output = recipe.Outputs[0];

            float y = r.y + 36;
            GUI.Label(new Rect(r.x + 10, y, 120, 20), _ctx.Loc.Get("ui.calc.recipe"), _small);
            if (GUI.Button(new Rect(r.x + 120, y - 2, 28, 24), "<")) _calcRecipe--;
            var icon = _ctx.IconOf(output.ItemId);
            if (icon != null) GUI.DrawTexture(new Rect(r.x + 154, y - 4, 28, 28), icon, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(r.x + 186, y, r.width - 230, 20), _ctx.ItemName(output.ItemId), _small);
            if (GUI.Button(new Rect(r.xMax - 38, y - 2, 28, 24), ">")) _calcRecipe++;
            y += 28;
            GUI.Label(new Rect(r.x + 120, y, r.width - 130, 20),
                $"<color=#aaaaaa>{(machine != null ? _ctx.Loc.Get(machine.NameKey) : recipe.Machine)}, {recipe.TimeTicks / 20f:0.##} s</color>", _small);
            y += 30;

            GUI.Label(new Rect(r.x + 10, y + 3, 160, 20), _ctx.Loc.Get("ui.calc.target"), _small);
            _calcTarget = GUI.TextField(new Rect(r.x + 170, y, 90, 24), _calcTarget, 8);
            y += 36;

            float speed = machine != null ? Mathf.Max(0.01f, machine.Speed) : 1f;
            float perMachine = output.Count * 1200f * speed / Mathf.Max(1, recipe.TimeTicks);
            float.TryParse(_calcTarget, out float target);
            target = Mathf.Max(0, target);
            float machines = perMachine > 0 ? target / perMachine : 0;

            GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), _ctx.Loc.Format("ui.calc.per_machine", perMachine.ToString("0.#")), _small);
            y += 26;
            GUI.Label(new Rect(r.x + 10, y, r.width - 20, 26),
                $"{_ctx.Loc.Get("ui.calc.machines")}: <b><color=#f5a623>{machines:0.##}</color></b>  (→ {Mathf.CeilToInt(machines - 1e-4f)})", _label);
            y += 36;

            GUI.Label(new Rect(r.x + 10, y, r.width - 20, 20), _ctx.Loc.Get("ui.calc.inputs") + ":", _small);
            y += 22;
            float crafts = output.Count > 0 ? target / output.Count : 0;
            foreach (var input in recipe.Inputs)
            {
                var ic = _ctx.IconOf(input.ItemId);
                if (ic != null) GUI.DrawTexture(new Rect(r.x + 14, y, 22, 22), ic, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(r.x + 42, y + 2, r.width - 52, 20), $"{_ctx.ItemName(input.ItemId)} — {input.Count * crafts:0.#}", _small);
                y += 24;
            }
            foreach (var fi in recipe.FluidInputs)
            {
                var fd = _ctx.Content.Fluids.Get(fi.FluidId);
                GUI.Label(new Rect(r.x + 42, y + 2, r.width - 52, 20), $"{(fd != null ? _ctx.Loc.Get(fd.NameKey) : fi.Fluid)} — {fi.Amount * crafts:0}", _small);
                y += 24;
            }
            if (machine != null && machine.Energy.Type == "steam" && machine.Energy.PerTick > 0)
            {
                GUI.Label(new Rect(r.x + 42, y + 2, r.width - 52, 20),
                    $"{_ctx.Loc.Get("fluid.genesis.steam")} — {machine.Energy.PerTick * 1200f * machines:0}", _small);
            }
        }
    }
}
