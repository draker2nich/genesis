using System;
using System.Collections.Generic;
using Genesis.Client.Rendering;
using Genesis.Client.UI;
using Genesis.Core;
using Genesis.Gameplay;
using Genesis.Net;
using Genesis.World;
using UnityEngine;

namespace Genesis.Client
{
    /// <summary>Сборка картинки мира: материалы, рендер чанков, атмосфера; игра и панорама за главным меню.</summary>
    public sealed partial class GenesisBootstrap
    {
        private LocalSession _panorama;
        private readonly List<GameObject> _panoramaObjects = new List<GameObject>();
        private bool _panoramaFailed;
        private float _panoramaAngle;
        private float _panoramaHeight = -1;

        private sealed class WorldMaterials
        {
            public Material Opaque, Translucent, Line, Color;
        }

        private WorldMaterials CreateMaterials()
        {
            var voxelShader = Shader.Find("Genesis/Voxel");
            if (voxelShader == null) Debug.LogError("Shader Genesis/Voxel not found");
            var m = new WorldMaterials();

            m.Opaque = new Material(voxelShader) { name = "VoxelOpaque" };
            m.Opaque.SetTexture("_Blocks", _textures.Array);
            m.Opaque.SetFloat("_Cutoff", 0.5f);

            m.Translucent = new Material(voxelShader) { name = "VoxelTranslucent" };
            m.Translucent.SetTexture("_Blocks", _textures.Array);
            m.Translucent.SetFloat("_Cutoff", 0.01f);
            m.Translucent.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.Translucent.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.Translucent.SetFloat("_ZWrite", 0f);
            m.Translucent.renderQueue = 3000;
            m.Translucent.SetShaderPassEnabled("ShadowCaster", false);

            m.Line = new Material(Shader.Find("Genesis/Line")) { name = "Line" };
            m.Line.SetColor("_Color", new Color(0f, 0f, 0f, 0.85f));
            m.Color = new Material(Shader.Find("Genesis/Color")) { name = "Color" };
            return m;
        }

        private Camera _mainCamera;

        /// <summary>Одна камера на всё время работы: и для панорамы, и для игры.</summary>
        private Camera EnsureCamera()
        {
            if (_mainCamera == null) _mainCamera = Camera.main;
            if (_mainCamera == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                _mainCamera = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }
            var cam = _mainCamera;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(256f, (ViewRadius + 2) * 16f);
            cam.fieldOfView = GameSettings.Fov;
            cam.clearFlags = CameraClearFlags.SolidColor;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogEndDistance = ViewRadius * 16f - 6f;
            RenderSettings.fogStartDistance = RenderSettings.fogEndDistance * 0.4f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;
            return cam;
        }

        /// <summary>Рендер мира сессии: чанки, фабрика, атмосфера; withActors — ещё игроки и враги.</summary>
        private EnvironmentRenderer CreateWorldView(IGameSession session, Camera cam, WorldMaterials mats, List<GameObject> owner, bool withActors, long seed)
        {
            GameObject Make(string name)
            {
                var go = new GameObject(name);
                owner.Add(go);
                return go;
            }

            Make("WorldRenderer").AddComponent<WorldRenderer>().Init(session, new MeshBlockTable(_content), mats.Opaque, mats.Translucent, cam.transform);
            Make("FactoryRenderer").AddComponent<FactoryRenderer>().Init(session, mats.Opaque, cam.transform);
            Make("ModelBlocks").AddComponent<Rendering.Models.ModelBlockRenderer>().Init(session, Rendering.Models.ModelLibrary.Current, cam.transform);
            if (withActors)
            {
                Make("EnemyRenderer").AddComponent<EnemyRenderer>().Init(session, Shader.Find("Genesis/Color"));
                Make("PlayerAvatars").AddComponent<PlayerAvatars>().Init(session, Shader.Find("Genesis/Color"));
                Make("DroppedItems").AddComponent<DroppedItemRenderer>().Init(session, mats.Opaque, cam.transform);
            }
            var env = Make("Environment").AddComponent<EnvironmentRenderer>();
            env.Init(session, cam, seed);
            return env;
        }

        // ============================================================ Игра

        /// <summary>Создать рендер, игрока и интерфейс для готовой сессии.</summary>
        private void BuildGame()
        {
            StopPanorama();
            PlayerPrefs.SetString(PrefName, _playerName);
            PlayerPrefs.SetString(PrefAddress, _address);

            var mats = CreateMaterials();
            var cam = EnsureCamera();
            _cam = cam;

            _ctx = new ClientContext
            {
                Session = _session,
                Content = _content,
                Loc = _loc,
                Textures = _textures,
                Player = _session.LocalPlayer
            };
            _session.Toast += key => _ctx.ShowServerToast(key);

            _env = CreateWorldView(_session, cam, mats, _spawned, true, _session.World.Generator.Seed);

            // Карта исследованного мира
            string mapFile = _mapFile ?? System.IO.Path.Combine(Application.persistentDataPath, "maps",
                SafeName(_address + "_" + _session.World.Generator.Seed) + ".dat");
            _ctx.Map = Spawn("WorldMap").AddComponent<Genesis.Client.Map.WorldMap>();
            _ctx.Map.Init(_ctx, mapFile);

            var highlight = Spawn("BlockHighlight").AddComponent<BlockHighlight>();
            highlight.Init(mats.Line);

            var fx = Spawn("Effects").AddComponent<EffectsRenderer>();
            fx.Init(_ctx, mats.Opaque, cam);

            var hand = Spawn("HeldItem").AddComponent<HeldItemRenderer>();
            hand.Init(_ctx, mats.Opaque, cam);

            var controller = gameObject.AddComponent<PlayerController>();
            controller.Init(_ctx, cam, highlight);
            controller.Effects = fx;
            controller.Hand = hand;
            _components.Add(controller);

            var hud = gameObject.AddComponent<GameHud>();
            hud.Init(_ctx);
            _components.Add(hud);

            _nextAutosave = Time.time + AutosaveMinutes * 60f;
            _mode = Mode.Playing;
            WebPortal.WorldOpened(); // Poki: gameplayStart — по первому действию игрока в мире
        }

        /// <summary>Настройки изменились в игре (меню паузы) — применить к камере и окружению.</summary>
        public void ApplyGraphicsSettings()
        {
            if (_cam != null) _cam.fieldOfView = GameSettings.Fov;
            _env?.ApplySettings();
        }

        // ============================================================ Панорама за меню

        /// <summary>Временный мир без сохранения: камера медленно облетает пейзаж, пока открыто меню.</summary>
        private void StartPanorama()
        {
            if (_panorama != null || _panoramaFailed || _session != null) return;
            try
            {
                var server = GameServer.Open(_content, null, DefaultSeed, Genesis.Core.Platform.Threads ? Mathf.Clamp(SystemInfo.processorCount - 2, 1, 4) : 0);
                if (!Genesis.Core.Platform.Threads) server.World.MainThreadBudgetMs = 6;
                server.ViewRadius = Genesis.Core.Platform.Threads ? 6 : 4;
                server.VerticalRadius = 3;
                server.Enemies.SpawningEnabled = false;
                server.WorldTime = GameServer.DayLength / 10; // утро — тёплый низкий свет
                _panorama = new LocalSession(server, "panorama");
                _panorama.LocalPlayer.Position = new Vec3(40, 120, 40);

                var mats = CreateMaterials();
                var cam = EnsureCamera();
                cam.farClipPlane = 7 * 16f;
                CreateWorldView(_panorama, cam, mats, _panoramaObjects, false, DefaultSeed);
                _panoramaHeight = -1;
                _panoramaActive = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Genesis] Menu panorama disabled: " + e.Message);
                _panoramaFailed = true;
                StopPanorama();
            }
        }

        private void StopPanorama()
        {
            _panoramaActive = false;
            if (_panorama != null)
            {
                _panorama.Dispose();
                _panorama = null;
            }
            foreach (var go in _panoramaObjects) if (go != null) Destroy(go);
            _panoramaObjects.Clear();
        }

        private void UpdatePanorama()
        {
            if (_panorama == null)
            {
                if (_mode == Mode.Menu && _session == null) StartPanorama();
                return;
            }
            var cam = _mainCamera;
            if (cam == null) return;

            // Камера по кругу на высоте ~35 блоков над рельефом, взгляд чуть вниз к центру
            _panoramaAngle += Time.deltaTime * 2.2f;
            float a = _panoramaAngle * Mathf.Deg2Rad;
            const float R = 70f;
            var center = new Vector3(40, 0, 40);
            var pos = center + new Vector3(Mathf.Cos(a) * R, 0, Mathf.Sin(a) * R);
            var gen = _panorama.World.Generator;
            float ground = Mathf.Max(gen.Column(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z)).Height,
                                     gen.Column((int)center.x, (int)center.z).Height, WorldConstants.SeaLevel);
            float targetH = ground + 34f;
            _panoramaHeight = _panoramaHeight < 0 ? targetH : Mathf.Lerp(_panoramaHeight, targetH, Time.deltaTime * 0.5f);
            pos.y = _panoramaHeight;
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation((center + Vector3.up * (_panoramaHeight - 22f)) - pos, Vector3.up);

            _panorama.LocalPlayer.Position = new Vec3(pos.x, pos.y - 20f, pos.z);
            _panorama.Update(Time.deltaTime, Time.time);
        }
    }
}
