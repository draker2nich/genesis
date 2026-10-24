using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace Genesis.Client
{
    public enum PortalKind
    {
        None,
        Yandex,
        Poki,
        /// <summary>Обычный сайт без SDK площадки (itch.io, свой хостинг): без рекламы и событий.</summary>
        Web
    }

    /// <summary>
    /// Единый вход для браузерных площадок: игра зовёт WebPortal, а он — SDK той площадки, на шаблоне которой
    /// собрана сборка (Яндекс Игры или Poki). У площадок разные правила рекламы и разметки геймплея — они здесь:
    /// <list type="bullet">
    /// <item>Яндекс: полноэкранная реклама при входе в мир и выходе в меню, не чаще раза в 3 минуты;</item>
    /// <item>Poki: commercialBreak только при возвращении в игру (вход в мир, выход из паузы), без своих таймеров;
    /// gameplayStart — по первому вводу игрока в мире, а не при загрузке.</item>
    /// </list>
    /// </summary>
    public static class WebPortal
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern string GP_Portal();
#else
        private static string GP_Portal() => "";
#endif

        public static bool IsWebGL => YandexGames.IsWebGL;

        private static PortalKind? _kind;

        public static PortalKind Kind
        {
            get
            {
                if (_kind == null)
                {
                    string p = "";
                    try { p = IsWebGL ? GP_Portal() ?? "" : ""; }
                    catch (Exception) { }
                    _kind = p == "poki" ? PortalKind.Poki
                        : p == "yandex" ? PortalKind.Yandex
                        : p == "web" ? PortalKind.Web
                        : PortalKind.None;
                }
                return _kind.Value;
            }
        }

        public static bool IsPoki => Kind == PortalKind.Poki;
        public static bool IsYandex => Kind == PortalKind.Yandex;

        /// <summary>Короткое меню «Играть» (Poki и обычные сайты вроде itch.io).</summary>
        public static bool QuickMenu => IsWebGL && !IsYandex;

        /// <summary>Запустить SDK как можно раньше (Poki просит init при старте).</summary>
        public static void Init()
        {
            if (IsPoki) PokiGames.Init();
        }

        /// <summary>SDK готов (или его нет) — можно спрашивать язык.</summary>
        public static bool SdkReady
        {
            get
            {
                switch (Kind)
                {
                    case PortalKind.Yandex: return YandexGames.SdkReady;
                    case PortalKind.Poki: return PokiGames.Initialized;
                    default: return true;
                }
            }
        }

        /// <summary>Язык интерфейса площадки (Poki — язык браузера).</summary>
        public static string Language => IsYandex ? YandexGames.Language : PokiGames.Language; // Poki и сайты — язык браузера

        /// <summary>Язык игры: русский для RU/BE/KK/UK/UZ, английский для остальных, null — неизвестен.</summary>
        public static string GameLanguageFor(string lang) => YandexGames.GameLanguageFor(lang);

        /// <summary>Загрузка закончена, меню доступно.</summary>
        public static void Ready()
        {
            if (IsPoki) PokiGames.LoadingFinished();
            else if (IsYandex) YandexGames.Ready();
        }

        // ------------------------------------------------------------ Геймплей

        private static bool _inputSinceWorld;

        /// <summary>Открыт мир: Poki засчитает геймплей только после первого действия игрока.</summary>
        public static void WorldOpened() => _inputSinceWorld = false;

        /// <summary>Игрок что-то нажал/коснулся экрана в мире.</summary>
        public static void NoteInput() => _inputSinceWorld = true;

        /// <summary>Сообщать каждый кадр; SDK дёргается только при смене состояния.</summary>
        public static void SetGameplay(bool active)
        {
            if (IsPoki) PokiGames.SetGameplay(active && _inputSinceWorld);
            else if (IsYandex) YandexGames.SetGameplay(active);
        }

        public static bool AdShowing => YandexGames.AdShowing || PokiGames.AdShowing;

        /// <summary>Площадка поставила игру на паузу (Яндекс: оверлей, скрытая вкладка).</summary>
        public static bool PlatformPaused => YandexGames.PlatformPaused;

        // ------------------------------------------------------------ Реклама

        /// <summary>Перед входом в мир из меню (обе площадки считают это естественной паузой).</summary>
        public static void BreakBeforeGameplay(Action then)
        {
            if (IsPoki) PokiGames.CommercialBreak(then);
            else if (IsYandex) YandexGames.ShowFullscreenAd(then);
            else then?.Invoke();
        }

        /// <summary>Выход в главное меню: Яндекс — реклама, Poki — нет (там реклама только перед возвращением в игру).</summary>
        public static void BreakOnMenu()
        {
            if (IsYandex) YandexGames.ShowFullscreenAd(null);
        }

        /// <summary>Выход из паузы обратно в игру: на Poki — рекламная пауза, на Яндексе — сразу.</summary>
        public static void BreakOnResume(Action then)
        {
            if (IsPoki) PokiGames.CommercialBreak(then);
            else then?.Invoke();
        }

        /// <summary>Сбросить сохранения в IndexedDB браузера (одинаково для всех площадок).</summary>
        public static void SyncFileSystem() => YandexGames.SyncFileSystem();
    }
}
