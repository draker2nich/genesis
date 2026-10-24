using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// Сборка для Яндекс Игр: меню Genesis → Build → Yandex Games (WebGL).
    /// 1) настраивает WebGL (шаблон Yandex, Brotli с запасной распаковкой, WebGL 2, память, без фоновой работы);
    /// 2) упаковывает StreamingAssets/Game в game.pak (в браузере нельзя читать папки) и на время сборки убирает
    ///    исходную папку, чтобы контент не попал в сборку дважды;
    /// 3) собирает в Builds/Yandex и пакует в Builds/genesis_yandex.zip (index.html в корне архива);
    /// 4) проверяет требования площадки: размер до 100 МБ, имена файлов без пробелов и кириллицы.
    /// </summary>
    public static class YandexBuild
    {
        private const string SceneDir = "Assets/Genesis/Scenes";
        private const string ScenePath = SceneDir + "/Main.unity";
        private const long MaxArchiveBytes = 100L * 1024 * 1024;

        [MenuItem("Genesis/Build/Yandex Games (WebGL)")]
        public static void BuildYandex()
        {
            WebBuild.Build("Yandex", "Yandex", "genesis_yandex.zip", ConfigurePlayer, Check);
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Genesis";
            if (string.IsNullOrEmpty(PlayerSettings.companyName) || PlayerSettings.companyName == "DefaultCompany")
                PlayerSettings.companyName = "Genesis";
            PlayerSettings.runInBackground = false;                // вкладка скрыта — игра и звук на паузе
            PlayerSettings.WebGL.template = "PROJECT:Yandex";      // Assets/WebGLTemplates/Yandex
            WebBuild.SetWebGL("Yandex", "nameFilesAsHashes", false);
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            PlayerSettings.WebGL.decompressionFallback = true;     // работает и без заголовков Content-Encoding на сервере
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;
            // Свойства, которые есть не во всех версиях 2022.3, — через рефлексию (нет свойства — пропускаем)
            WebBuild.SetWebGL("Yandex", "showDiagnostics", false);
            WebBuild.SetWebGL("Yandex", "initialMemorySize", 256);
            WebBuild.SetWebGL("Yandex", "maximumMemorySize", 2048);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 }); // WebGL 2
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.WebGL, ManagedStrippingLevel.Low);
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;
        }

        /// <summary>Проверки из требований Яндекс Игр (п. 1.21, 1.22).</summary>
        private static bool Check(string outDir, string zipPath)
        {
            bool ok = true;
            if (!File.Exists(Path.Combine(outDir, "index.html")))
            {
                Debug.LogError("[Yandex] В корне сборки нет index.html.");
                ok = false;
            }
            long total = 0;
            var bad = new Regex(@"[\sЀ-ӿ]");
            foreach (var f in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories))
            {
                total += new FileInfo(f).Length;
                string rel = f.Substring(outDir.Length).TrimStart('/', '\\');
                if (bad.IsMatch(rel))
                {
                    Debug.LogError($"[Yandex] Пробел или кириллица в имени файла: {rel}");
                    ok = false;
                }
            }
            Debug.Log($"[Yandex] Размер сборки (распакованной): {total / (1024f * 1024f):0.0} МБ, архива: {new FileInfo(zipPath).Length / (1024f * 1024f):0.0} МБ");
            if (total > MaxArchiveBytes)
            {
                Debug.LogError("[Yandex] Больше 100 МБ — площадка не примет архив.");
                ok = false;
            }
            return ok;
        }
    }
}
