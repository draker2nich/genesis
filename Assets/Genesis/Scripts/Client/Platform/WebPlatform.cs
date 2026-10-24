using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace Genesis.Client
{
    /// <summary>
    /// Браузерная сборка (WebGL): язык браузера и сброс сохранений в IndexedDB.
    /// Мост к JS — Plugins/WebGL/Genesis.jslib; в редакторе и на ПК — заглушки.
    /// </summary>
    public static class WebPlatform
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern string GN_BrowserLang();
        [DllImport("__Internal")] private static extern void GN_SyncFS();
        public const bool IsWebGL = true;
#else
        private static string GN_BrowserLang() => "";
        private static void GN_SyncFS() { }
        public const bool IsWebGL = false;
#endif

        /// <summary>Язык браузера (ISO 639-1, нижний регистр) или пустая строка.</summary>
        public static string Language
        {
            get
            {
                try
                {
                    string l = GN_BrowserLang() ?? "";
                    int dash = l.IndexOf('-');
                    return (dash > 0 ? l.Substring(0, dash) : l).ToLowerInvariant();
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        /// <summary>Язык игры: русский для RU/BE/KK/UK/UZ, английский для остальных, null — неизвестен.</summary>
        public static string GameLanguageFor(string lang)
        {
            switch ((lang ?? "").ToLowerInvariant())
            {
                case "ru":
                case "be":
                case "kk":
                case "uk":
                case "uz":
                    return "ru";
                case "":
                    return null;
                default:
                    return "en";
            }
        }

        /// <summary>Сбросить файловую систему (persistentDataPath) в IndexedDB: без этого сохранение пропадёт после F5.</summary>
        public static void SyncFileSystem() => GN_SyncFS();
    }
}
