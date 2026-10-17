using System.Collections.Generic;
using Genesis.Core;
using Genesis.Gameplay;
using Genesis.Net;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Атмосфера: время суток → цвета неба, тумана и света для шейдеров, солнце с тенями,
    /// небесная сфера (солнце, луна, звёзды), воксельные облака, пост-обработка URP.
    /// </summary>
    public sealed class EnvironmentRenderer : MonoBehaviour
    {
        private IGameSession _session;
        private Camera _cam;
        private Light _sun;
        private Material _skyMat;
        private Transform _dome;
        private CloudLayer _clouds;
        private Volume _volume;
        private bool _urp;

        // Атмосфера биома под игроком: оттенок тумана и его плотность (плавно меняются)
        private Color _biomeFog = Color.white, _biomeFogTarget = Color.white;
        private float _fogDensity = 1f, _fogDensityTarget = 1f, _biomeCheckAt;
        private float _fogStart = -1f, _fogEnd;

        public Color HorizonColor { get; private set; }

        // Ключевые цвета (мир после катастрофы — небо чуть пыльное)
        private static readonly Color DayZenith = new Color(0.30f, 0.49f, 0.78f);
        private static readonly Color DayHorizon = new Color(0.72f, 0.78f, 0.84f);
        private static readonly Color DuskZenith = new Color(0.26f, 0.30f, 0.52f);
        private static readonly Color DuskHorizon = new Color(0.96f, 0.56f, 0.34f);
        private static readonly Color NightZenith = new Color(0.010f, 0.014f, 0.038f);
        private static readonly Color NightHorizon = new Color(0.045f, 0.06f, 0.11f);

        private static readonly Color DayLight = new Color(1.0f, 0.98f, 0.94f);
        private static readonly Color DuskLight = new Color(1.0f, 0.76f, 0.58f);
        private static readonly Color NightLight = new Color(0.55f, 0.64f, 0.95f);
        private static readonly Color BlockLight = new Color(1.12f, 0.86f, 0.58f);

        private static readonly int DaylightId = Shader.PropertyToID("_GenesisDaylight");
        private static readonly int SkyLightId = Shader.PropertyToID("_GenesisSkyLight");
        private static readonly int BlockLightId = Shader.PropertyToID("_GenesisBlockLight");
        private static readonly int SunDirId = Shader.PropertyToID("_GenesisSunDir");
        private static readonly int ShadowStrengthId = Shader.PropertyToID("_GenesisShadowStrength");

        public void Init(IGameSession session, Camera cam, long seed)
        {
            _session = session;
            _cam = cam;
            _urp = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;

            // Солнце: направленный свет только ради теней (освещение считает наш шейдер)
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(transform, false);
            _sun = sunGo.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.intensity = 1f;
            _sun.color = Color.white;
            _sun.shadows = LightShadows.Soft;
            _sun.shadowStrength = 1f;
            _sun.shadowBias = 0.06f;
            _sun.shadowNormalBias = 0.35f;
            RenderSettings.sun = _sun;

            if (_urp)
            {
                var asset = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
                asset.shadowDistance = 96f;
                asset.shadowCascadeCount = 2;
                asset.supportsHDR = true; // без HDR светящиеся блоки не дают bloom
                var camData = cam.GetUniversalAdditionalCameraData();
                camData.renderShadows = true;
                // Сглаживание краёв: пиксельные кромки блоков и листвы не «искрят» при повороте
                camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                camData.antialiasingQuality = AntialiasingQuality.High;
                CreatePostProcessing();
            }
            cam.allowHDR = true;

            // Небесная сфера
            var skyShader = Shader.Find("Genesis/Sky");
            if (skyShader != null)
            {
                var dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(dome.GetComponent<Collider>());
                dome.name = "SkyDome";
                dome.transform.SetParent(transform, false);
                _skyMat = new Material(skyShader);
                var r = dome.GetComponent<MeshRenderer>();
                r.sharedMaterial = _skyMat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                _dome = dome.transform;
            }

            var cloudShader = Shader.Find("Genesis/Cloud");
            if (cloudShader != null)
            {
                var cgo = new GameObject("Clouds");
                cgo.transform.SetParent(transform, false);
                _clouds = cgo.AddComponent<CloudLayer>();
                _clouds.Init(seed, new Material(cloudShader), cam.transform);
            }
            ApplySettings();
            LateUpdate();
        }

        private void CreatePostProcessing()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.5f);
            // Порог выше обычной яркости освещённого дневного блока: светятся только факелы/лампы.
            // Высокое качество фильтра и ограничение яркости убирают «блёстки» — мерцающие одиночные
            // пиксели (снег, блики воды, далёкий факел в 1 пиксель), которые вспыхивали при движении камеры.
            bloom.threshold.Override(1.2f);
            bloom.scatter.Override(0.65f);
            bloom.clamp.Override(3f);
            bloom.highQualityFiltering.Override(true);
            var tonemap = profile.Add<Tonemapping>(true);
            tonemap.mode.Override(TonemappingMode.Neutral);
            var color = profile.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.15f);
            color.contrast.Override(12f);
            color.saturation.Override(14f);
            // Тёплый «рисованный» тон в духе Hytale: чуть теплее баланс, холодные тени
            var wb = profile.Add<WhiteBalance>(true);
            wb.temperature.Override(6f);
            var smh = profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.96f, 0.98f, 1.06f, 0f));
            smh.highlights.Override(new Vector4(1.04f, 1.01f, 0.96f, 0f));
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.24f);
            vignette.smoothness.Override(0.45f);

            var go = new GameObject("PostProcessing");
            go.transform.SetParent(transform, false);
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 10;
            _volume.sharedProfile = profile;
        }

        private void UpdateBiomeAtmosphere()
        {
            float dt = Time.deltaTime;
            if (Time.time >= _biomeCheckAt)
            {
                _biomeCheckAt = Time.time + 0.5f;
                var p = _cam.transform.position;
                var col = _session.World.Generator.ColumnAt(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z));
                switch (col.Biome)
                {
                    case Genesis.World.Biome.Swamp: _biomeFogTarget = new Color(0.82f, 0.95f, 0.78f); _fogDensityTarget = 2.2f; break;
                    case Genesis.World.Biome.Desert: _biomeFogTarget = new Color(1.12f, 1.0f, 0.82f); _fogDensityTarget = 1.25f; break;
                    case Genesis.World.Biome.Badlands: _biomeFogTarget = new Color(1.15f, 0.92f, 0.78f); _fogDensityTarget = 1.2f; break;
                    case Genesis.World.Biome.Wasteland: _biomeFogTarget = new Color(1.0f, 0.88f, 0.76f); _fogDensityTarget = 1.6f; break;
                    case Genesis.World.Biome.Tundra:
                    case Genesis.World.Biome.SnowyPeaks: _biomeFogTarget = new Color(0.94f, 1.0f, 1.08f); _fogDensityTarget = 1.3f; break;
                    case Genesis.World.Biome.Taiga: _biomeFogTarget = new Color(0.9f, 0.98f, 1.02f); _fogDensityTarget = 1.15f; break;
                    case Genesis.World.Biome.Forest:
                    case Genesis.World.Biome.BirchForest: _biomeFogTarget = new Color(0.95f, 1.03f, 0.95f); _fogDensityTarget = 1.05f; break;
                    default: _biomeFogTarget = Color.white; _fogDensityTarget = 1f; break;
                }
                // Под землёй — плотнее и темнее
                if (p.y < col.Height - 6) _fogDensityTarget *= 1.5f;
            }
            float k = 1f - Mathf.Exp(-dt * 0.8f);
            _biomeFog = Color.Lerp(_biomeFog, _biomeFogTarget, k);
            _fogDensity = Mathf.Lerp(_fogDensity, _fogDensityTarget, k);
        }

        /// <summary>Применить настройки графики (тени, пост-обработка, облака).</summary>
        public void ApplySettings()
        {
            if (_volume != null) _volume.enabled = GameSettings.PostProcessing;
            if (_urp) _cam.GetUniversalAdditionalCameraData().renderPostProcessing = GameSettings.PostProcessing;
            if (_clouds != null) _clouds.gameObject.SetActive(GameSettings.Clouds);
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            float phase = _session.DayPhase;
            float sunHeight = GameServer.SunHeight(phase);
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.14f, 0.22f, sunHeight));
            float dusk = Mathf.Clamp01(1f - Mathf.Abs(sunHeight) * 3.2f) * (sunHeight > -0.3f ? 1f : 0f);

            var zenith = Color.Lerp(NightZenith, DayZenith, day);
            zenith = Color.Lerp(zenith, DuskZenith, dusk * 0.45f);
            var horizon = Color.Lerp(NightHorizon, DayHorizon, day);
            horizon = Color.Lerp(horizon, DuskHorizon, dusk * 0.75f * Mathf.Max(day, 0.35f));
            HorizonColor = horizon;

            float angle = phase * Mathf.PI * 2f;
            var sunDir = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0.35f).normalized;

            Shader.SetGlobalFloat(DaylightId, day);
            var skyLight = Color.Lerp(NightLight, Color.Lerp(DayLight, DuskLight, dusk), day);
            Shader.SetGlobalColor(SkyLightId, skyLight);
            Shader.SetGlobalColor(BlockLightId, BlockLight);
            Shader.SetGlobalVector(SunDirId, sunDir);
            float shadowStrength = GameSettings.Shadows ? 0.55f * Mathf.Clamp01(sunHeight * 5f) : 0f;
            Shader.SetGlobalFloat(ShadowStrengthId, shadowStrength);

            // Атмосфера биома: туманные болота, жаркое марево пустынь, морозная дымка тундры
            UpdateBiomeAtmosphere();
            if (_fogStart < 0f)
            {
                _fogStart = RenderSettings.fogStartDistance;
                _fogEnd = RenderSettings.fogEndDistance;
            }
            RenderSettings.fogStartDistance = _fogStart / _fogDensity;
            RenderSettings.fogEndDistance = Mathf.Max(_fogStart / _fogDensity + 8f, _fogEnd / Mathf.Sqrt(_fogDensity));

            // Туман и фон камеры = цвет горизонта
            var fog = Color.Lerp(horizon, zenith, 0.15f);
            fog = Color.Lerp(fog, fog * _biomeFog, 0.6f * Mathf.Max(day, 0.25f));
            RenderSettings.fogColor = fog;
            _cam.backgroundColor = fog;
            RenderSettings.ambientLight = Color.white;

            // Свет для теней: солнце днём, ночью теней нет
            bool sunUp = sunHeight > 0.03f && GameSettings.Shadows;
            _sun.shadows = sunUp ? LightShadows.Soft : LightShadows.None;
            // Направление теней меняется ступеньками по 0.4°: при плавном движении солнца карта теней
            // каждый кадр сдвигается на доли текселя, и края теней «рябят» мерцающими пикселями.
            const float step = 0.4f * Mathf.Deg2Rad;
            float qAngle = Mathf.Round(angle / step) * step;
            var shadowDir = new Vector3(Mathf.Cos(qAngle), Mathf.Sin(qAngle), 0.35f).normalized;
            _sun.transform.rotation = Quaternion.LookRotation(-shadowDir, Vector3.up);

            if (_skyMat != null)
            {
                _skyMat.SetColor("_ZenithColor", zenith);
                _skyMat.SetColor("_HorizonColor", horizon);
                _skyMat.SetColor("_GroundColor", Color.Lerp(horizon, Color.black, 0.55f));
                _skyMat.SetColor("_SunColor", Color.Lerp(new Color(1f, 0.55f, 0.3f), new Color(1f, 0.93f, 0.75f), Mathf.Clamp01(sunHeight * 3f)));
                _skyMat.SetVector("_SunDir", sunDir);
                _skyMat.SetFloat("_Night", 1f - day);
                float r = _cam.farClipPlane * 0.9f;
                _dome.position = _cam.transform.position;
                _dome.localScale = Vector3.one * r * 2f;
            }

            if (_clouds != null)
            {
                var tint = Color.Lerp(new Color(0.11f, 0.12f, 0.17f), Color.white, day);
                tint = Color.Lerp(tint, new Color(1f, 0.78f, 0.62f), dusk * 0.5f * day);
                _clouds.SetColors(tint, horizon);
            }
        }
    }

    /// <summary>Слой объёмных облаков из блоков 12×4×12 на высоте ~200, медленно плывёт по ветру.</summary>
    public sealed class CloudLayer : MonoBehaviour
    {
        private const float Cell = 12f;
        private const float Height = 200f;
        private const float Thickness = 4f;
        private const int Radius = 22;
        private const float Speed = 0.7f;

        private PerlinNoise _noise;
        private Material _mat;
        private Transform _cam;
        private Mesh _mesh;
        private int _cx = int.MinValue, _cz = int.MinValue;
        private readonly List<Vector3> _v = new List<Vector3>();
        private readonly List<Color32> _c = new List<Color32>();
        private readonly List<int> _t = new List<int>();

        public void Init(long seed, Material mat, Transform cam)
        {
            _noise = new PerlinNoise(seed ^ 0xC10D);
            _mat = mat;
            _cam = cam;
            _mesh = new Mesh { name = "Clouds" };
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            _mat.SetFloat("_FadeStart", Radius * Cell * 0.55f);
            _mat.SetFloat("_FadeEnd", Radius * Cell * 0.95f);
        }

        public void SetColors(Color tint, Color horizon)
        {
            tint.a = 0.88f;
            _mat.SetColor("_Color", tint);
            _mat.SetColor("_HorizonColor", horizon);
        }

        private bool Filled(int x, int z)
        {
            double n = _noise.Fbm(x * 0.085, z * 0.085, 3);
            return n > 0.12;
        }

        private void LateUpdate()
        {
            float scroll = Time.time * Speed;
            transform.position = new Vector3(-scroll, 0, 0);
            int cx = Mathf.FloorToInt((_cam.position.x + scroll) / Cell);
            int cz = Mathf.FloorToInt(_cam.position.z / Cell);
            if (cx == _cx && cz == _cz) return;
            _cx = cx;
            _cz = cz;
            Rebuild();
        }

        private void Rebuild()
        {
            _v.Clear();
            _c.Clear();
            _t.Clear();
            for (int dz = -Radius; dz <= Radius; dz++)
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                if (dx * dx + dz * dz > Radius * Radius) continue;
                int x = _cx + dx, z = _cz + dz;
                if (!Filled(x, z)) continue;
                var min = new Vector3(x * Cell, Height, z * Cell);
                var max = new Vector3(min.x + Cell, Height + Thickness, min.z + Cell);
                Quad(new Vector3(min.x, max.y, min.z), new Vector3(min.x, max.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(max.x, max.y, min.z), 255);
                Quad(new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z), new Vector3(min.x, min.y, min.z), 190);
                if (!Filled(x + 1, z)) Quad(new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), new Vector3(max.x, min.y, max.z), 222);
                if (!Filled(x - 1, z)) Quad(new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z), new Vector3(min.x, min.y, min.z), 222);
                if (!Filled(x, z + 1)) Quad(new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(min.x, min.y, max.z), 236);
                if (!Filled(x, z - 1)) Quad(new Vector3(min.x, min.y, min.z), new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, min.y, min.z), 236);
            }
            _mesh.Clear();
            _mesh.indexFormat = IndexFormat.UInt32;
            _mesh.SetVertices(_v);
            _mesh.SetColors(_c);
            _mesh.SetTriangles(_t, 0, false);
            _mesh.bounds = new Bounds(new Vector3(_cx * Cell, Height, _cz * Cell), new Vector3(Radius * Cell * 2.5f, 40, Radius * Cell * 2.5f));
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, byte shade)
        {
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            var col = new Color32(shade, shade, shade, 255);
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }
    }
}
