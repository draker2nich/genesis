using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Data;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Прогресс мира: текущая эпоха и открытые узлы дерева знаний. Общий для всех игроков (кооп — одна команда).
    /// Узел открывается действием (сделать вручную / построить / получить), а открывает крафты и
    /// машинные рецепты — «сделал сам → открыл автоматизацию».
    /// </summary>
    public sealed class KnowledgeState
    {
        private readonly ContentDatabase _content;
        private readonly HashSet<string> _unlocked = new HashSet<string>();
        private readonly Dictionary<string, List<KnowledgeNode>> _craftGate = new Dictionary<string, List<KnowledgeNode>>();
        private readonly Dictionary<string, List<KnowledgeNode>> _recipeGate = new Dictionary<string, List<KnowledgeNode>>();

        public int Era { get; private set; }

        /// <summary>Узел открыт (id) — для уведомлений игрокам.</summary>
        public event Action<KnowledgeNode> NodeUnlocked;
        public event Action<int> EraAdvanced;
        /// <summary>Любое изменение — для репликации.</summary>
        public event Action Changed;

        public KnowledgeState(ContentDatabase content)
        {
            _content = content;
            foreach (var n in content.Knowledge.Items)
            {
                foreach (var c in n.Unlocks.Crafts) Gate(_craftGate, c, n);
                foreach (var r in n.Unlocks.Recipes) Gate(_recipeGate, r, n);
            }
            Cascade();
        }

        private static void Gate(Dictionary<string, List<KnowledgeNode>> map, string id, KnowledgeNode n)
        {
            if (!map.TryGetValue(id, out var list)) map[id] = list = new List<KnowledgeNode>();
            list.Add(n);
        }

        public string EraName => Era < Eras.All.Length ? Eras.All[Era] : Eras.All[Eras.All.Length - 1];

        /// <summary>Номер текущей эпохи (1 — первобытная, 2 — промышленная, 3 — мега-эпоха).</summary>
        public int Epoch => Eras.EpochOf(Era);

        /// <summary>
        /// Квесты справочника в порядке прохождения: по эпохе, затем по order. Служебные узлы (quest: false) не входят.
        /// </summary>
        public List<KnowledgeNode> Quests(int epoch)
        {
            var list = new List<KnowledgeNode>();
            foreach (var n in _content.Knowledge.Items)
                if (n.Quest && Eras.EpochOf(n.EraIndex) == epoch) list.Add(n);
            list.Sort((a, b) => a.EraIndex != b.EraIndex ? a.EraIndex.CompareTo(b.EraIndex)
                : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.NumericId.CompareTo(b.NumericId));
            return list;
        }

        public bool IsUnlocked(KnowledgeNode n) => _unlocked.Contains(n.Id);
        public bool IsUnlocked(string nodeId) => _unlocked.Contains(nodeId);
        public IEnumerable<string> UnlockedIds => _unlocked;

        /// <summary>Узел можно открыть сейчас: эпоха достигнута, предпосылки открыты.</summary>
        public bool IsAvailable(KnowledgeNode n)
        {
            if (_unlocked.Contains(n.Id) || n.EraIndex > Era) return false;
            foreach (var r in n.Requires)
                if (!_unlocked.Contains(r)) return false;
            return true;
        }

        /// <summary>Крафт без узла-замка доступен всегда; иначе — если открыт любой из узлов.</summary>
        public bool IsCraftUnlocked(CraftDef c) => IsGateOpen(_craftGate, c.Id);
        public bool IsRecipeUnlocked(RecipeDef r)
        {
            if (r.SourceCraft != null)
            {
                var c = _content.Crafts.Get(r.SourceCraft);
                return c != null && IsCraftUnlocked(c);
            }
            return IsGateOpen(_recipeGate, r.Id);
        }

        private bool IsGateOpen(Dictionary<string, List<KnowledgeNode>> map, string id)
        {
            if (!map.TryGetValue(id, out var nodes)) return true;
            foreach (var n in nodes)
                if (_unlocked.Contains(n.Id)) return true;
            return false;
        }

        /// <summary>Узел, который открывает рецепт (для подсказки в UI).</summary>
        public KnowledgeNode GateOfRecipe(string recipeId) =>
            _recipeGate.TryGetValue(recipeId, out var l) && l.Count > 0 ? l[0] : null;

        public KnowledgeNode GateOfCraft(string craftId) =>
            _craftGate.TryGetValue(craftId, out var l) && l.Count > 0 ? l[0] : null;

        // ============================================================ События игры

        /// <summary>Сообщить о действии игрока: craft/place/obtain + id.</summary>
        public void Notify(string triggerType, string target)
        {
            bool any = false;
            foreach (var n in _content.Knowledge.Items)
            {
                if (n.Trigger.Type != triggerType || n.Trigger.Target != target) continue;
                if (!IsAvailable(n)) continue;
                UnlockInternal(n);
                any = true;
            }
            if (any) Cascade();
        }

        /// <summary>Есть ли доступные узлы с таким триггером (чтобы не сканировать инвентари зря).</summary>
        public bool WantsTrigger(string triggerType)
        {
            foreach (var n in _content.Knowledge.Items)
                if (n.Trigger.Type == triggerType && IsAvailable(n)) return true;
            return false;
        }

        public IEnumerable<KnowledgeNode> AvailableWithTrigger(string triggerType)
        {
            foreach (var n in _content.Knowledge.Items)
                if (n.Trigger.Type == triggerType && IsAvailable(n)) yield return n;
        }

        /// <summary>Принудительно открыть (креатив/отладка/тесты).</summary>
        public void ForceUnlock(string nodeId)
        {
            var n = _content.Knowledge.Get(nodeId);
            if (n == null || _unlocked.Contains(n.Id)) return;
            if (n.EraIndex > Era) Era = n.EraIndex;
            UnlockInternal(n);
            Cascade();
        }

        private void UnlockInternal(KnowledgeNode n)
        {
            _unlocked.Add(n.Id);
            NodeUnlocked?.Invoke(n);
            if (n.Milestone && n.EraIndex == Era && Era < Eras.All.Length - 1)
            {
                Era++;
                EraAdvanced?.Invoke(Era);
            }
            Changed?.Invoke();
        }

        /// <summary>Открыть все доступные узлы с триггером «start» (в т.ч. после смены эпохи).</summary>
        private void Cascade()
        {
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var n in _content.Knowledge.Items)
                {
                    if (n.Trigger.Type != "start" || !IsAvailable(n)) continue;
                    UnlockInternal(n);
                    progress = true;
                }
            }
        }

        // ============================================================ Сохранение / репликация

        public void Write(BinaryWriter w)
        {
            w.Write(2); // 2: века новой схемы эпох (первобытная, паровой, электрический, космический, квантовый)
            w.Write(Era);
            w.Write(_unlocked.Count);
            foreach (var id in _unlocked) w.Write(id);
        }

        public void Read(BinaryReader r)
        {
            int ver = r.ReadInt32();
            if (ver > 2) throw new InvalidDataException("knowledge version " + ver);
            Era = r.ReadInt32();
            // Сохранения до эпох: дерево/камень/железо → первобытная эпоха, дальше — со сдвигом
            if (ver < 2) Era = Eras.FromLegacyIndex(Era);
            if (Era < 0 || Era >= Eras.All.Length) Era = 0;
            _unlocked.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                string id = r.ReadString();
                if (_content.Knowledge.Contains(id)) _unlocked.Add(id);
            }
            Cascade();
            Changed?.Invoke();
        }

        /// <summary>Применить состояние хоста (клиент): без каскадов и событий-уведомлений.</summary>
        public void ApplyReplica(int era, IEnumerable<string> ids)
        {
            Era = era;
            _unlocked.Clear();
            foreach (var id in ids) _unlocked.Add(id);
            Changed?.Invoke();
        }
    }
}
