using System;

namespace Genesis.Core
{
    /// <summary>
    /// Возможности платформы. Ядро не знает про Unity — клиент выставляет флаги при старте.
    /// На платформах без потоков, нативного zlib или загрузки DLL флаги выключаются (тесты, серверные утилиты).
    /// </summary>
    public static class Platform
    {
        /// <summary>Можно запускать фоновые потоки и Task.Run.</summary>
        public static bool Threads = true;

        /// <summary>Работает GZipStream (иначе файлы пишутся без сжатия).</summary>
        public static bool Compression = true;

        /// <summary>Можно загружать код-моды (DLL).</summary>
        public static bool CodeMods = true;

        /// <summary>
        /// Вызывается после записи сохранения на диск (если платформе нужно что-то сделать с файлами после записи).
        /// </summary>
        public static Action AfterSave;
    }
}
