using System;

namespace Genesis.Core
{
    /// <summary>
    /// Возможности платформы. Ядро не знает про Unity — клиент выставляет флаги при старте.
    /// В браузере (WebGL, Яндекс Игры) нет потоков, нативного zlib и загрузки DLL.
    /// </summary>
    public static class Platform
    {
        /// <summary>Можно запускать фоновые потоки и Task.Run (в WebGL — нет).</summary>
        public static bool Threads = true;

        /// <summary>Работает GZipStream (в WebGL нативного zlib нет — файлы пишутся без сжатия).</summary>
        public static bool Compression = true;

        /// <summary>Можно загружать код-моды (DLL).</summary>
        public static bool CodeMods = true;

        /// <summary>
        /// Вызывается после записи сохранения на диск. В браузере файловая система живёт в памяти, и её нужно
        /// явно сбросить в IndexedDB — иначе после перезагрузки страницы сохранение пропадёт.
        /// </summary>
        public static Action AfterSave;
    }
}
