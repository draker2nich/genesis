using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// Сборка для itch.io: меню Genesis → Build → itch.io (WebGL). Шаблон Itch — без SDK площадок,
    /// без рекламы и событий геймплея; меню как на Poki («Играть» сразу в мир). Без сжатия: itch.io не
    /// отдаёт заголовок Content-Encoding для .gz/.br, а распаковка в браузере медленнее.
    /// Результат — Builds/Itch и Builds/genesis_itch.zip (его и загружать на itch.io как HTML-игру).
    /// </summary>
    public static class ItchBuild
    {
        [MenuItem("Genesis/Build/itch.io (WebGL)")]
        public static void BuildItch()
        {
            WebBuild.Build("Itch", "Itch", "genesis_itch.zip", ConfigurePlayer, Check);
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Genesis";
            if (string.IsNullOrEmpty(PlayerSettings.companyName) || PlayerSettings.companyName == "DefaultCompany")
                PlayerSettings.companyName = "Genesis";
            PlayerSettings.runInBackground = false;
            PlayerSettings.WebGL.template = "PROJECT:Itch";             // Assets/WebGLTemplates/Itch
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;
            WebBuild.SetWebGL("Itch", "nameFilesAsHashes", true);
            WebBuild.SetWebGL("Itch", "showDiagnostics", false);
            WebBuild.SetWebGL("Itch", "initialMemorySize", 256);
            WebBuild.SetWebGL("Itch", "maximumMemorySize", 2048);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 }); // WebGL 2
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.stripEngineCode = true;
            // Как в сборке для Poki; если в браузере пустой контент — поставьте Low
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.WebGL, ManagedStrippingLevel.Medium);
            PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.WebGL, Il2CppCompilerConfiguration.Master);
            // Размер встраивания на странице itch.io (Embed options → Manually set size)
            PlayerSettings.defaultWebScreenWidth = 960;
            PlayerSettings.defaultWebScreenHeight = 540;
        }

        private static bool Check(string outDir, string zipPath)
        {
            if (!File.Exists(Path.Combine(outDir, "index.html")))
            {
                Debug.LogError("[Itch] В корне сборки нет index.html — itch.io не запустит архив.");
                return false;
            }
            float mb = 1024f * 1024f;
            Debug.Log($"[Itch] ZIP: {new FileInfo(zipPath).Length / mb:0.0} МБ. Загрузите его на itch.io: Kind of project → HTML, " +
                      "отметьте «This file will be played in the browser». Размер окна 960×540, Fullscreen button, Mobile friendly.");
            return true;
        }
    }
}
