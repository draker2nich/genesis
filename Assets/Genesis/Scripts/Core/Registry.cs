using System;
using System.Collections.Generic;

namespace Genesis.Core
{
    /// <summary>
    /// Реестр строковых ID ("genesis:iron_plate") → числовой индекс.
    /// Числовые ID используются в рантайме (чанки, инвентари), строковые — в контенте и сохранениях.
    /// </summary>
    public sealed class Registry<T> where T : class
    {
        private readonly List<T> _items = new List<T>();
        private readonly List<string> _ids = new List<string>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count => _items.Count;
        public IReadOnlyList<T> Items => _items;

        public int Register(string id, T item)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Empty id");
            if (_index.ContainsKey(id)) throw new InvalidOperationException($"Duplicate id '{id}' in registry of {typeof(T).Name}");
            int idx = _items.Count;
            _items.Add(item);
            _ids.Add(id);
            _index[id] = idx;
            return idx;
        }

        /// <summary>Заменить существующую запись (моды могут переопределять базовый контент).</summary>
        public int RegisterOrReplace(string id, T item)
        {
            if (_index.TryGetValue(id, out int idx))
            {
                _items[idx] = item;
                return idx;
            }
            return Register(id, item);
        }

        public T Get(int index) => (uint)index < (uint)_items.Count ? _items[index] : null;

        public T Get(string id) => _index.TryGetValue(id, out int idx) ? _items[idx] : null;

        public bool TryGet(string id, out T item)
        {
            if (_index.TryGetValue(id, out int idx))
            {
                item = _items[idx];
                return true;
            }
            item = null;
            return false;
        }

        public int IndexOf(string id) => _index.TryGetValue(id, out int idx) ? idx : -1;

        public string IdOf(int index) => (uint)index < (uint)_ids.Count ? _ids[index] : null;

        public bool Contains(string id) => _index.ContainsKey(id);
    }
}
