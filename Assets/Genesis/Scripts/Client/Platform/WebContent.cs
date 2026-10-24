using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Genesis.Client
{
    /// <summary>
    /// Контент игры в браузере. В WebGL StreamingAssets — это адрес на сервере, а не папка: читать JSON и PNG
    /// через System.IO нельзя. Поэтому сборка для Яндекс Игр упаковывает StreamingAssets/Game в один файл
    /// game.pak (см. Editor/YandexBuild.cs), а здесь он скачивается и распаковывается в файловую систему
    /// браузера (в памяти) — дальше обычный загрузчик контента работает без изменений.
    /// </summary>
    public static class WebContent
    {
        public const string PackName = "game.pak";
        private const int Magic = 0x314B5047; // "GPK1"

        /// <summary>Скачать и распаковать пакет; done получает путь к распакованной папке Game.</summary>
        public static IEnumerator Prepare(MonoBehaviour host, Action<string> done, Action<string> status)
        {
            string url = Application.streamingAssetsPath.TrimEnd('/') + "/" + PackName;
            string root = Path.Combine(Application.temporaryCachePath, "Game");
            status?.Invoke("Genesis…");
            using (var req = UnityWebRequest.Get(url))
            {
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    status?.Invoke($"Genesis… {Mathf.RoundToInt(req.downloadProgress * 100f)}%");
                    yield return null;
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[Genesis] content pack download failed: " + req.error);
                    status?.Invoke("Error: " + req.error);
                    yield break;
                }
                try
                {
                    Unpack(req.downloadHandler.data, root);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    status?.Invoke("Error: " + e.Message);
                    yield break;
                }
            }
            // Язык площадки нужен до загрузки строк: даём SDK до 3 секунд на инициализацию
            float t0 = Time.realtimeSinceStartup;
            while (!WebPortal.SdkReady && Time.realtimeSinceStartup - t0 < 3f) yield return null;
            done(root);
        }

        /// <summary>Распаковать пакет в папку (перезаписывая).</summary>
        public static void Unpack(byte[] data, string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("bad content pack");
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string rel = r.ReadString().Replace('\\', '/');
                    int len = r.ReadInt32();
                    var bytes = r.ReadBytes(len);
                    if (rel.Contains("..")) continue;
                    string path = Path.Combine(root, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, bytes);
                }
            }
        }

        /// <summary>Упаковать папку (вызывается из скрипта сборки в редакторе).</summary>
        public static byte[] Pack(string root)
        {
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(Magic);
                    int count = 0;
                    foreach (var f in files) if (!f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) count++;
                    w.Write(count);
                    foreach (var f in files)
                    {
                        if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        string rel = f.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
                        var bytes = File.ReadAllBytes(f);
                        w.Write(rel);
                        w.Write(bytes.Length);
                        w.Write(bytes);
                    }
                    w.Flush();
                    return ms.ToArray();
                }
            }
        }
    }
}
