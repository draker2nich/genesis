using UnityEngine;

namespace Genesis.Client
{
    /// <summary>Настройки игрока (PlayerPrefs).</summary>
    public static class GameSettings
    {
        public static string Language = "ru";
        public static int ViewRadius = 8;
        public static float MouseSensitivity = 2.2f;
        public static float Fov = 75f;
        public static bool Shadows = true;
        public static bool PostProcessing = true;
        public static bool ViewBobbing = true;
        public static bool Clouds = true;
        /// <summary>Яркость 0..1: нижняя граница освещённости (ночью и под землёй).</summary>
        public static float Brightness = 0.35f;

        private const string Prefix = "genesis.settings.";

        /// <summary>Игрок уже выбирал язык.</summary>
        public static bool HasSavedLanguage => PlayerPrefs.HasKey(Prefix + "lang");

        public static void Load()
        {
            bool mobile = Application.isMobilePlatform;
            Language = PlayerPrefs.GetString(Prefix + "lang", Application.systemLanguage == SystemLanguage.Russian ? "ru" : "en");
            // На телефонах по умолчанию ближе дальность и без теней — ровнее кадр
            ViewRadius = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "view", mobile ? 5 : 8), 4, 16);
            MouseSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "mouse", 2.2f), 0.3f, 8f);
            Fov = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "fov", 75f), 55f, 100f);
            Shadows = PlayerPrefs.GetInt(Prefix + "shadows", mobile ? 0 : 1) == 1;
            // Постобработка URP на телефонах дорогая — по умолчанию выключена; игрок может включить
            PostProcessing = PlayerPrefs.GetInt(Prefix + "post", mobile ? 0 : 1) == 1;
            Brightness = Mathf.Clamp01(PlayerPrefs.GetFloat(Prefix + "brightness", 0.35f));
            ViewBobbing = PlayerPrefs.GetInt(Prefix + "bob", 1) == 1;
            Clouds = PlayerPrefs.GetInt(Prefix + "clouds", 1) == 1;
            InputReader.MouseSensitivity = MouseSensitivity;
        }

        public static void Save()
        {
            PlayerPrefs.SetString(Prefix + "lang", Language);
            PlayerPrefs.SetInt(Prefix + "view", ViewRadius);
            PlayerPrefs.SetFloat(Prefix + "mouse", MouseSensitivity);
            PlayerPrefs.SetFloat(Prefix + "fov", Fov);
            PlayerPrefs.SetInt(Prefix + "shadows", Shadows ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "post", PostProcessing ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "bob", ViewBobbing ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "clouds", Clouds ? 1 : 0);
            PlayerPrefs.SetFloat(Prefix + "brightness", Brightness);
            PlayerPrefs.Save();
            InputReader.MouseSensitivity = MouseSensitivity;
        }
    }
}
