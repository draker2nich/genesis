using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// Сборка для Poki: меню Genesis → Build → Poki (WebGL). Настройки — по руководству Poki для Unity
    /// (sdk.poki.com/unity): шаблон Poki, без сжатия (Poki сжимает сам), имена файлов-хеши, кеширование,
    /// WASM, вырезание неиспользуемого кода движка. Результат — Builds/Poki (её можно перетащить в Poki Inspector)
    /// и Builds/genesis_poki.zip.
    /// </summary>
    public static class PokiBuild
    {
        /// <summary>Рекомендация Poki для первоначальной загрузки.</summary>
        private const long TargetInitialBytes = 8L * 1024 * 1024;

        [MenuItem("Genesis/Build/Poki (WebGL)")]
        public static void BuildPoki()
        {
            WebBuild.Build("Poki", "Poki", "genesis_poki.zip", ConfigurePlayer, Check);
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Genesis";
            if (string.IsNullOrEmpty(PlayerSettings.companyName) || PlayerSettings.companyName == "DefaultCompany")
                PlayerSettings.companyName = "Genesis";
            PlayerSettings.runInBackground = false;
            PlayerSettings.WebGL.template = "PROJECT:Poki";             // Assets/WebGLTemplates/Poki
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled; // Poki: «Compression: Disabled»
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;
            WebBuild.SetWebGL("Poki", "nameFilesAsHashes", true);
            WebBuild.SetWebGL("Poki", "showDiagnostics", false);
            WebBuild.SetWebGL("Poki", "initialMemorySize", 256);
            WebBuild.SetWebGL("Poki", "maximumMemorySize", 2048);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 }); // WebGL 2
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.stripEngineCode = true;
            // Medium: меньше размер; Newtonsoft и контент защищены Assets/link.xml. Если в браузере пустой контент
            // или ошибки про отсутствующие типы — поставьте Low (как в сборке для Яндекса).
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.WebGL, ManagedStrippingLevel.Medium);
            PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.WebGL, Il2CppCompilerConfiguration.Master);
            // Заставку Unity Poki просит убрать; в Unity 2022 Personal её отключить нельзя — тогда она останется
            try
            {
                PlayerSettings.SplashScreen.show = false;
                PlayerSettings.SplashScreen.showUnityLogo = false;
            }
            catch (System.Exception)
            {
            }
            // Игровая область Poki — 16:9 (640×360, 836×470, 1031×580)
            PlayerSettings.defaultWebScreenWidth = 836;
            PlayerSettings.defaultWebScreenHeight = 470;
        }

        private static bool Check(string outDir, string zipPath)
        {
            bool ok = true;
            if (!File.Exists(Path.Combine(outDir, "index.html")))
            {
                Debug.LogError("[Poki] В корне сборки нет index.html.");
                ok = false;
            }
            long initial = WebBuild.SizeOf(outDir, "Build");
            long content = WebBuild.SizeOf(outDir, "StreamingAssets");
            float mb = 1024f * 1024f;
            Debug.Log($"[Poki] Первоначальная загрузка (папка Build): {initial / mb:0.0} МБ; контент (game.pak): {content / mb:0.0} МБ; " +
                      $"ZIP: {new FileInfo(zipPath).Length / mb:0.0} МБ. Poki сожмёт файлы при раздаче (≈ в 3–4 раза).");
            if (initial + content > TargetInitialBytes * 3)
                Debug.LogWarning("[Poki] Даже после сжатия сборка, скорее всего, больше рекомендуемых 8 МБ. " +
                                 "Poki принимает Unity-игры по своему руководству оптимизации — см. POKI.md, раздел «Размер».");
            return ok;
        }
    }
}
