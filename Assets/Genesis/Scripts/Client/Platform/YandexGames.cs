using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Яндекс Игры: готовность игры, разметка геймплея, полноэкранная реклама в паузах, язык, сохранение в IndexedDB.
    /// Работает только в WebGL-сборке на шаблоне «Yandex» (WebGLTemplates/Yandex); в редакторе и на ПК — заглушки.
    /// </summary>
    public static class YandexGames
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void YG_Ready();
        [DllImport("__Internal")] private static extern void YG_GameplayStart();
        [DllImport("__Internal")] private static extern void YG_GameplayStop();
        [DllImport("__Internal")] private static extern void YG_ShowFullscreenAd();
        [DllImport("__Internal")] private static extern string YG_GetLang();
        [DllImport("__Internal")] private static extern int YG_IsReady();
        [DllImport("__Internal")] private static extern void YG_SyncFS();
        public const bool IsWebGL = true;
#else
        private static void YG_Ready() { }
        private static void YG_GameplayStart() { }
        private static void YG_GameplayStop() { }
        private static void YG_ShowFullscreenAd() { }
        private static string YG_GetLang() => "";
        private static int YG_IsReady() => 0;
        private static void YG_SyncFS() { }
        public const bool IsWebGL = false;
#endif

        /// <summary>Минимальный интервал между нашими запросами рекламы (частоту дополнительно ограничивает Яндекс).</summary>
        public const float AdInterval = 180f;

        private static bool _readySent;
        private static bool _gameplay;
        private static float _lastAdTime = -AdInterval;
        private static Action _afterAd;

        /// <summary>Сейчас показывается полноэкранная реклама: игра и звук на паузе.</summary>
        public static bool AdShowing { get; private set; }

        /// <summary>Площадка поставила игру на паузу (вкладка скрыта, открыт оверлей Яндекса).</summary>
        public static bool PlatformPaused { get; set; }

        public static bool SdkReady => YG_IsReady() == 1;

        /// <summary>Язык интерфейса Яндекс Игр в формате ISO 639-1 (пусто — неизвестен).</summary>
        public static string Language
        {
            get
            {
                try { return YG_GetLang() ?? ""; }
                catch (Exception) { return ""; }
            }
        }

        /// <summary>Язык игры по требованию площадки: русский для RU/BE/KK/UK/UZ, английский для остальных.</summary>
        public static string GameLanguageFor(string yandexLang)
        {
            switch ((yandexLang ?? "").ToLowerInvariant())
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

        /// <summary>Игра готова к взаимодействию (показано главное меню) — один раз.</summary>
        public static void Ready()
        {
            if (_readySent) return;
            _readySent = true;
            YG_Ready();
        }

        /// <summary>Сообщить площадке, идёт ли сейчас геймплей (вызывать каждый кадр — дёргает SDK только при смене).</summary>
        public static void SetGameplay(bool active)
        {
            if (active == _gameplay) return;
            _gameplay = active;
            if (active) YG_GameplayStart();
            else YG_GameplayStop();
        }

        /// <summary>
        /// Полноэкранная реклама в логической паузе (переход из меню в мир, выход в меню).
        /// then вызывается после рекламы — или сразу, если показывать рано/нельзя.
        /// </summary>
        public static void ShowFullscreenAd(Action then)
        {
            if (AdShowing) return; // реклама уже идёт — повторный клик игнорируем
            if (!IsWebGL || Time.realtimeSinceStartup - _lastAdTime < AdInterval)
            {
                then?.Invoke();
                return;
            }
            _lastAdTime = Time.realtimeSinceStartup;
            _afterAd = then;
            AdShowing = true;
            SetGameplay(false);
            AudioListener.pause = true;
            YG_ShowFullscreenAd();
        }

        /// <summary>Вызывается из JS (через GenesisBootstrap.OnYandexAdClosed).</summary>
        internal static void OnAdClosed()
        {
            AdShowing = false;
            AudioListener.pause = PlatformPaused;
            var a = _afterAd;
            _afterAd = null;
            a?.Invoke();
        }

        /// <summary>Сбросить сохранения в IndexedDB браузера.</summary>
        public static void SyncFileSystem() => YG_SyncFS();
    }
}
