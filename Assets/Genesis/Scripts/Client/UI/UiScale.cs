using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Масштаб интерфейса. Интерфейс рассчитан примерно на 1280×720: в маленьком окне
    /// (телефон в альбомной ориентации) он уменьшается целиком, на 4K — увеличивается.
    /// В коде интерфейса вместо Screen.width/height — UiScale.W/H (размер «виртуального» экрана).
    /// </summary>
    public static class UiScale
    {
        public static float S { get; private set; } = 1f;
        public static float W => Screen.width / S;
        public static float H => Screen.height / S;

        /// <summary>Вызывать в начале OnGUI: считает масштаб и ставит матрицу.</summary>
        public static void Begin()
        {
            S = Compute();
            GUI.matrix = Matrix4x4.Scale(new Vector3(S, S, 1f));
        }

        private static float Compute()
        {
            float h = Screen.height, w = Screen.width;
            if (h <= 0 || w <= 0) return 1f;
            float s = Mathf.Min(h / 720f, w / 1180f);
            if (s >= 1f) s = Mathf.Max(1f, h / 1080f); // 1080p — как задумано, 4K — крупнее
            // На сенсорных экранах кнопки не меньше пальца: интерфейс не мельчим так сильно
            if (TouchControls.Enabled) s = Mathf.Max(s, Mathf.Min(h / 600f, 1.6f));
            return Mathf.Clamp(s, 0.5f, 3f);
        }

        /// <summary>Перевести экранную точку (из Camera.WorldToScreenPoint) в координаты GUI.</summary>
        public static Vector2 FromScreen(Vector3 screen) => new Vector2(screen.x / S, (Screen.height - screen.y) / S);
    }
}
