using System;
using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// Браузерная сборка: меню Genesis → Build → WebGL. Переключение на WebGL, упаковка StreamingAssets/Game
    /// в game.pak (в браузере нельзя читать папки), временный вынос исходной папки, сборка и ZIP.
    /// Шаблон — Assets/WebGLTemplates/Genesis. Без сжатия: не каждый хостинг отдаёт Content-Encoding для .gz/.br.
    /// Результат — Builds/WebGL и Builds/genesis_webgl.zip (index.html в корне архива).
    /// </summary>
    public static class WebBuild
    {
        private const string SceneDir = "Assets/Genesis/Scenes";
        private const string ScenePath = SceneDir + "/Main.unity";
        private const string Label = "WebGL";

        [MenuItem("Genesis/Build/WebGL")]
        public static void BuildWebGL()
        {
            Build("WebGL", "genesis_webgl.zip");
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Genesis";
            if (string.IsNullOrEmpty(PlayerSettings.companyName) || PlayerSettings.companyName == "DefaultCompany")
                PlayerSettings.companyName = "Genesis";
            PlayerSettings.runInBackground = false;
            PlayerSettings.WebGL.template = "PROJECT:Genesis";          // Assets/WebGLTemplates/Genesis
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;
            // Свойства, которые есть не во всех версиях 2022.3, — через рефлексию (нет свойства — пропускаем)
            SetWebGL("nameFilesAsHashes", true);
            SetWebGL("showDiagnostics", false);
            SetWebGL("initialMemorySize", 256);
            SetWebGL("maximumMemorySize", 2048);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { GraphicsDeviceType.OpenGLES3 }); // WebGL 2
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.WebGL, ScriptingImplementation.IL2CPP);
            PlayerSettings.stripEngineCode = true;
            // Newtonsoft и контент защищены Assets/link.xml; если в браузере пустой контент — поставьте Low
            PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.WebGL, ManagedStrippingLevel.Medium);
            PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.WebGL, Il2CppCompilerConfiguration.Master);
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;
        }

        private static bool Check(string outDir, string zipPath)
        {
            if (!File.Exists(Path.Combine(outDir, "index.html")))
            {
                Debug.LogError($"[{Label}] В корне сборки нет index.html.");
                return false;
            }
            float mb = 1024f * 1024f;
            Debug.Log($"[{Label}] Build: {SizeOf(outDir, "Build") / mb:0.0} МБ; контент (game.pak): {SizeOf(outDir, "StreamingAssets") / mb:0.0} МБ; " +
                      $"ZIP: {new FileInfo(zipPath).Length / mb:0.0} МБ.");
            return true;
        }

        /// <param name="folder">Папка сборки внутри Builds/.</param>
        /// <param name="zipName">Имя архива в Builds/.</param>
        private static void Build(string folder, string zipName)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                if (!EditorUtility.DisplayDialog("Genesis", "Переключить платформу на WebGL? Это займёт несколько минут (переимпорт ассетов).", "Переключить", "Отмена"))
                    return;
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL))
                {
                    Debug.LogError($"[{Label}] WebGL Build Support не установлен (Unity Hub → Installs → Add modules).");
                    return;
                }
            }

            string project = Path.GetDirectoryName(Application.dataPath);
            string outDir = Path.Combine(project, "Builds", folder);
            string zipPath = Path.Combine(project, "Builds", zipName);

            ConfigurePlayer();
            string scene = EnsureScene();

            string streaming = Path.Combine(Application.dataPath, "StreamingAssets");
            string game = Path.Combine(streaming, "Game");
            string pak = Path.Combine(streaming, WebContent.PackName);
            // Не в Temp: Unity очищает Temp при закрытии, и после сбоя посреди сборки контент пропал бы навсегда
            string backup = Path.Combine(project, "GenesisGameBackup");
            if (!Directory.Exists(game) || !Directory.Exists(Path.Combine(game, "blocks")))
            {
                Debug.LogError($"[{Label}] Папка StreamingAssets/Game отсутствует или пуста" +
                               (Directory.Exists(backup) ? " — её копия лежит в GenesisGameBackup (рядом с Assets), верните её." : "."));
                return;
            }

            File.WriteAllBytes(pak, WebContent.Pack(game));
            Debug.Log($"[{Label}] game.pak: {new FileInfo(pak).Length / 1024} КБ");

            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.Move(game, backup);
            if (File.Exists(game + ".meta")) File.Move(game + ".meta", backup + ".meta");
            AssetDatabase.Refresh();

            BuildReport report;
            try
            {
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    locationPathName = outDir,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None
                });
            }
            finally
            {
                if (Directory.Exists(game))
                {
                    // Кто-то (импорт ассетов) успел создать папку заново — не затираем, кладём контент рядом
                    Debug.LogError($"[{Label}] StreamingAssets/Game появилась во время сборки; контент возвращён в Game_restored — проверьте и переименуйте.");
                    game += "_restored";
                }
                Directory.Move(backup, game);
                if (File.Exists(backup + ".meta")) File.Move(backup + ".meta", game + ".meta");
                if (File.Exists(pak)) File.Delete(pak);
                if (File.Exists(pak + ".meta")) File.Delete(pak + ".meta");
                AssetDatabase.Refresh();
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[{Label}] Сборка не удалась: {report.summary.result}, ошибок: {report.summary.totalErrors}");
                return;
            }

            if (File.Exists(zipPath)) File.Delete(zipPath);
            CreateZip(outDir, zipPath);
            bool ok = Check(outDir, zipPath);
            EditorUtility.RevealInFinder(zipPath);
            Debug.Log(ok
                ? $"[{Label}] Готово: {zipPath}"
                : $"[{Label}] Архив собран, но есть замечания — см. сообщения выше.");
        }

        private static void SetWebGL(string property, object value)
        {
            try
            {
                var p = typeof(PlayerSettings.WebGL).GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p != null && p.CanWrite) p.SetValue(null, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Label}] PlayerSettings.WebGL.{property}: {e.Message}");
            }
        }

        /// <summary>ZIP с содержимым папки (index.html — в корне архива).</summary>
        public static void CreateZip(string dir, string zipPath)
        {
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(dir.Length).TrimStart('/', '\\').Replace('\\', '/');
                    var level = f.EndsWith(".br") || f.EndsWith(".gz") || f.EndsWith(".png") || f.EndsWith(".jpg")
                        ? System.IO.Compression.CompressionLevel.NoCompression
                        : System.IO.Compression.CompressionLevel.Optimal;
                    var entry = zip.CreateEntry(rel, level);
                    using (var src = File.OpenRead(f))
                    using (var dst = entry.Open())
                        src.CopyTo(dst);
                }
            }
        }

        /// <summary>Сцена для сборки: игра создаёт себя сама в любой сцене, нужна хотя бы одна пустая.</summary>
        public static string EnsureScene()
        {
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled && File.Exists(s.path)) return s.path;
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory(SceneDir);
                var created = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(created, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            return ScenePath;
        }

        /// <summary>Сумма размеров файлов папки (байт), опционально только в подпапке.</summary>
        public static long SizeOf(string dir, string sub = null)
        {
            string root = sub != null ? Path.Combine(dir, sub) : dir;
            if (!Directory.Exists(root)) return 0;
            long total = 0;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) total += new FileInfo(f).Length;
            return total;
        }
    }
}
