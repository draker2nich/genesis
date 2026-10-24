using UnityEditor;
using UnityEngine;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// Меню Genesis → Скриншоты для площадок: входит в Play Mode и запускает ScreenshotDirector,
    /// который сам строит сцены и снимает 3 кадра 1920×1080 в PokiPage/screenshots.
    /// </summary>
    [InitializeOnLoad]
    public static class ScreenshotMenu
    {
        private const string Flag = "genesis.captureScreens";

        static ScreenshotMenu()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Flag, false)) return;
                SessionState.SetBool(Flag, false);
                ScreenshotDirector.Begin();
            };
        }

        [MenuItem("Genesis/Скриншоты для площадок")]
        public static void Capture()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL &&
                !EditorUtility.DisplayDialog("Genesis", "Активная платформа — WebGL: в редакторе генерация мира идёт медленнее. Всё равно снять?", "Снять", "Отмена"))
                return;
            if (EditorApplication.isPlaying)
            {
                ScreenshotDirector.Begin();
                return;
            }
            SessionState.SetBool(Flag, true);
            EditorApplication.isPlaying = true;
            Debug.Log("[Screens] Вход в Play Mode… Съёмка займёт около минуты, не трогайте мышь в окне Game.");
        }
    }
}
