using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Genesis.Client.Editor
{
    /// <summary>
    /// При открытии проекта сам создаёт и настраивает URP (тени, мягкие тени, HDR, пост-обработка),
    /// если конвейер ещё не назначен. Вручную — меню Genesis → Setup URP Pipeline.
    /// </summary>
    [InitializeOnLoad]
    public static class GenesisEditorMenu
    {
        private const string SettingsDir = "Assets/Genesis/Settings";
        private const string PostDataPath = "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset";
        private const string AutoKey = "Genesis.UrpAutoSetup.v2";

        static GenesisEditorMenu()
        {
            EditorApplication.delayCall += AutoSetup;
        }

        private static void AutoSetup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            string project = Application.dataPath;
            if (EditorPrefs.GetString(AutoKey, "") == project) return;
            EditorPrefs.SetString(AutoKey, project);
            SetupUrp();
        }

        [MenuItem("Genesis/Setup URP Pipeline")]
        public static void SetupUrp()
        {
            var urp = GraphicsSettings.renderPipelineAsset as UniversalRenderPipelineAsset;
            if (urp == null)
            {
                Directory.CreateDirectory(SettingsDir);
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, SettingsDir + "/GenesisRenderer.asset");
                urp = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(urp, SettingsDir + "/GenesisURP.asset");
                GraphicsSettings.renderPipelineAsset = urp;
                Debug.Log("[Genesis] URP asset created and assigned.");
            }
            // Во всех уровнях качества — тот же ассет (иначе уровень может переопределить конвейер)
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
            }
            QualitySettings.SetQualityLevel(current, false);

            Configure(urp);
            AssetDatabase.SaveAssets();
            Debug.Log("[Genesis] URP configured: HDR, soft shadows, post-processing.");
        }

        private static void Configure(UniversalRenderPipelineAsset urp)
        {
            urp.supportsHDR = true;
            urp.shadowDistance = 96f;
            urp.shadowCascadeCount = 2;
            urp.msaaSampleCount = 2;

            var so = new SerializedObject(urp);
            SetBool(so, "m_MainLightShadowsSupported", true);
            SetBool(so, "m_SoftShadowsSupported", true);
            SetInt(so, "m_MainLightShadowmapResolution", 2048);
            SetInt(so, "m_MainLightRenderingMode", 1); // PerPixel
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(urp);

            // Пост-обработка требует PostProcessData у рендерера
            var post = AssetDatabase.LoadAssetAtPath<PostProcessData>(PostDataPath);
            var rso = new SerializedObject(urp);
            var list = rso.FindProperty("m_RendererDataList");
            if (list == null || post == null) return;
            for (int i = 0; i < list.arraySize; i++)
            {
                var data = list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
                if (data == null) continue;
                var dso = new SerializedObject(data);
                var p = dso.FindProperty("postProcessData") ?? dso.FindProperty("m_PostProcessData");
                if (p != null && p.objectReferenceValue == null)
                {
                    p.objectReferenceValue = post;
                    dso.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(data);
                }
            }
        }

        private static void SetBool(SerializedObject so, string name, bool v)
        {
            var p = so.FindProperty(name);
            if (p != null) p.boolValue = v;
        }

        private static void SetInt(SerializedObject so, string name, int v)
        {
            var p = so.FindProperty(name);
            if (p != null) p.intValue = v;
        }

        [MenuItem("Genesis/Open Saves Folder")]
        public static void OpenSaves()
        {
            string dir = Path.Combine(Application.persistentDataPath, "saves");
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }
    }
}
