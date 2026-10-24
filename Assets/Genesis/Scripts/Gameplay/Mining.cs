using Genesis.Data;
using Genesis.Simulation;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Правила добычи: время ломания и дроп в зависимости от инструмента.
    /// Используются и клиентом (полоска прогресса), и сервером (проверка, дроп, износ).
    /// </summary>
    public static class Mining
    {
        public const float HandDamage = 1f;

        public static ToolSpec ToolOf(ContentDatabase content, ItemStack held)
        {
            if (held.IsEmpty) return null;
            return content.Item(held.Item)?.Tool;
        }

        /// <summary>
        /// Подходит ли инструмент по типу к блоку. Рубило (universal) подходит ко всему: им копают, рубят и дробят,
        /// но медленнее, чем специализированным инструментом (скорость задаётся в его описании).
        /// </summary>
        public static bool IsCorrectTool(BlockDef block, ToolSpec tool) =>
            tool != null && block.Tool != null && (tool.Type == block.Tool || tool.IsUniversal);

        /// <summary>Будет ли дроп: блоку с harvest_tier нужен подходящий инструмент не ниже уровня.</summary>
        public static bool CanHarvest(BlockDef block, ToolSpec tool)
        {
            if (block.HarvestTier <= 0) return true;
            return IsCorrectTool(block, tool) && tool.Tier >= block.HarvestTier;
        }

        /// <summary>Время ломания в секундах.</summary>
        public static float BreakTime(BlockDef block, ToolSpec tool)
        {
            if (block.Unbreakable) return float.PositiveInfinity;
            float h = System.Math.Max(0f, block.Hardness);
            if (!CanHarvest(block, tool)) return System.Math.Max(0.1f, h * 5f);
            if (IsCorrectTool(block, tool)) return System.Math.Max(0.05f, h * 1.5f / System.Math.Max(0.1f, tool.Speed));
            return System.Math.Max(0.05f, h * 1.5f);
        }

        public static float AttackDamage(ToolSpec tool) => tool != null ? tool.Damage : HandDamage;
    }
}
