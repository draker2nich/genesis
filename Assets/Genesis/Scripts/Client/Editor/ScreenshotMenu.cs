using UnityEditor;
using UnityEngine;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// Меню Genesis → Скриншоты: входит в Play Mode и запускает ScreenshotDirector,
    /// который сам строит сцены и снимает 3 кадра 1920×1080 в Screenshots/.
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

        [MenuItem("Genesis/Скриншоты")]
        public static void Capture()
        {
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
