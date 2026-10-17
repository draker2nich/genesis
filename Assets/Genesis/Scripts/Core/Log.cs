using System;

namespace Genesis.Core
{
    /// <summary>
    /// Логгер без зависимости от Unity. Клиент подменяет Sink на Debug.Log.
    /// </summary>
    public static class Log
    {
        public static Action<string> InfoSink = s => Console.WriteLine(s);
        public static Action<string> WarnSink = s => Console.WriteLine("[WARN] " + s);
        public static Action<string> ErrorSink = s => Console.Error.WriteLine("[ERROR] " + s);

        public static void Info(string msg) => InfoSink?.Invoke(msg);
        public static void Warn(string msg) => WarnSink?.Invoke(msg);
        public static void Error(string msg) => ErrorSink?.Invoke(msg);
    }

    /// <summary>Глобальные константы движка игры.</summary>
    public static class GameConstants
    {
        public const string BaseNamespace = "genesis";
        public const int TicksPerSecond = 20;
        public const float TickDuration = 1f / TicksPerSecond;
        public const int MaxStackSize = 128;
    }
}
