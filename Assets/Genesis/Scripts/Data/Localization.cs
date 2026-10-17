using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Genesis.Data
{
    /// <summary>
    /// Строки интерфейса и названия. Файлы: &lt;mod&gt;/lang/&lt;code&gt;.json, плоский словарь ключ → строка.
    /// Фолбэк: текущий язык → английский → сам ключ.
    /// </summary>
    public sealed class Localization
    {
        public const string Fallback = "en";

        private readonly Dictionary<string, Dictionary<string, string>> _tables =
            new Dictionary<string, Dictionary<string, string>>();

        public string Language { get; set; } = "ru";

        public void LoadModDirectory(string modRoot)
        {
            string dir = Path.Combine(modRoot, "lang");
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                if (dict == null) continue;
                if (!_tables.TryGetValue(code, out var table)) _tables[code] = table = new Dictionary<string, string>();
                foreach (var kv in dict) table[kv.Key] = kv.Value;
            }
        }

        public void Add(string language, string key, string value)
        {
            if (!_tables.TryGetValue(language, out var table)) _tables[language] = table = new Dictionary<string, string>();
            table[key] = value;
        }

        public string Get(string key)
        {
            if (key == null) return "";
            if (_tables.TryGetValue(Language, out var t) && t.TryGetValue(key, out var s)) return s;
            if (_tables.TryGetValue(Fallback, out t) && t.TryGetValue(key, out s)) return s;
            return key;
        }

        public string Format(string key, params object[] args) => string.Format(Get(key), args);

        public IEnumerable<string> Languages => _tables.Keys;
    }
}
