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

        private const string Prefix = "genesis.settings.";

        public static void Load()
        {
            Language = PlayerPrefs.GetString(Prefix + "lang", Application.systemLanguage == SystemLanguage.Russian ? "ru" : "en");
            ViewRadius = Mathf.Clamp(PlayerPrefs.GetInt(Prefix + "view", 8), 4, 16);
            MouseSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "mouse", 2.2f), 0.3f, 8f);
            Fov = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "fov", 75f), 55f, 100f);
            Shadows = PlayerPrefs.GetInt(Prefix + "shadows", 1) == 1;
            PostProcessing = PlayerPrefs.GetInt(Prefix + "post", 1) == 1;
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
            PlayerPrefs.Save();
            InputReader.MouseSensitivity = MouseSensitivity;
        }
    }
}
