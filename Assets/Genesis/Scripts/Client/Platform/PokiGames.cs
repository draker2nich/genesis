using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using AOT;
#endif
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Poki: инициализация SDK, конец загрузки, разметка геймплея, рекламные паузы (commercialBreak).
    /// Вызовы идут напрямую в window.PokiSDK (см. Plugins/WebGL/Poki.jslib), поэтому мост работает и на нашем
    /// шаблоне «Poki», и на официальном шаблоне Poki. В редакторе и на ПК — заглушки.
    /// </summary>
    public static class PokiGames
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void PK_Init(Action onReady);
        [DllImport("__Internal")] private static extern void PK_LoadingFinished();
        [DllImport("__Internal")] private static extern void PK_GameplayStart();
        [DllImport("__Internal")] private static extern void PK_GameplayStop();
        [DllImport("__Internal")] private static extern void PK_CommercialBreak(Action onDone);
        [DllImport("__Internal")] private static extern string PK_BrowserLang();
#else
        private static void PK_Init(Action onReady) => onReady?.Invoke();
        private static void PK_LoadingFinished() { }
        private static void PK_GameplayStart() { }
        private static void PK_GameplayStop() { }
        private static void PK_CommercialBreak(Action onDone) => onDone?.Invoke();
        private static string PK_BrowserLang() => "";
#endif

        private static bool _initStarted, _loadingSent, _gameplay;
        private static Action _afterBreak;

        /// <summary>SDK ответил на init (или его нет — игра всё равно запускается).</summary>
        public static bool Initialized { get; private set; }

        /// <summary>Идёт рекламная пауза: ввод и звук выключены, мир стоит.</summary>
        public static bool AdShowing { get; private set; }

        /// <summary>Язык браузера (ISO 639-1), у Poki нет своего — берём navigator.language.</summary>
        public static string Language
        {
            get
            {
                try
                {
                    string l = PK_BrowserLang() ?? "";
                    int dash = l.IndexOf('-');
                    return (dash > 0 ? l.Substring(0, dash) : l).ToLowerInvariant();
                }
                catch (Exception)
                {
                    return "";
                }
            }
        }

        /// <summary>Как можно раньше при запуске.</summary>
        public static void Init()
        {
            if (_initStarted) return;
            _initStarted = true;
            PK_Init(OnInit);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [MonoPInvokeCallback(typeof(Action))]
#endif
        private static void OnInit() => Initialized = true;

        /// <summary>Загрузка закончена, показано главное меню — один раз.</summary>
        public static void LoadingFinished()
        {
            if (_loadingSent) return;
            _loadingSent = true;
            PK_LoadingFinished();
        }

        /// <summary>gameplayStart/Stop только при смене состояния (Poki запрещает два одинаковых события подряд).</summary>
        public static void SetGameplay(bool active)
        {
            if (active == _gameplay || AdShowing) return; // во время рекламы — никаких событий
            _gameplay = active;
            if (active) PK_GameplayStart();
            else PK_GameplayStop();
        }

        /// <summary>
        /// Рекламная пауза перед возвращением в игру. Порядок по документации Poki: gameplayStop → звук и ввод
        /// выключены → commercialBreak → по окончании then (дальше игра сама вызовет gameplayStart).
        /// Покажет ли Poki рекламу — решает Poki; своих таймеров нет (это запрещено).
        /// </summary>
        public static void CommercialBreak(Action then)
        {
            if (AdShowing) return;
            SetGameplay(false);
            AdShowing = true;
            _afterBreak = then;
            AudioListener.pause = true;
            PK_CommercialBreak(OnBreakDone);
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [MonoPInvokeCallback(typeof(Action))]
#endif
        private static void OnBreakDone()
        {
            AdShowing = false;
            AudioListener.pause = false;
            var a = _afterBreak;
            _afterBreak = null;
            a?.Invoke();
        }
    }
}
