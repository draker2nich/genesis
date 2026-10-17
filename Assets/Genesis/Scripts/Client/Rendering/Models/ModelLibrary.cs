using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Core;
using Genesis.Data;
using Genesis.Modding;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering.Models
{
    /// <summary>
    /// Библиотека моделей Blockbench. Модель «ns:имя» ищется как models/&lt;ns&gt;/&lt;имя&gt;.bbmodel в любом источнике
    /// контента или models/&lt;имя&gt;.bbmodel в источнике с тем же id (игра — StreamingAssets/Game, моды игроков);
    /// поздние моды переопределяют ранние. Модели грузятся один раз и кешируются (и промахи тоже).
    /// </summary>
    public sealed class ModelLibrary
    {
        /// <summary>Текущая библиотека (пересоздаётся при перезагрузке контента).</summary>
        public static ModelLibrary Current;

        private readonly IReadOnlyList<ModInfo> _sources;
        private readonly Dictionary<string, BbModel> _cache = new Dictionary<string, BbModel>(StringComparer.Ordinal);
        private readonly Dictionary<BbModel, Material[]> _mats = new Dictionary<BbModel, Material[]>();
        private readonly Dictionary<BbModel, Material[]> _overlayMats = new Dictionary<BbModel, Material[]>();
        private readonly Shader _shader;

        public ModelLibrary(IReadOnlyList<ModInfo> sources)
        {
            _sources = sources ?? new List<ModInfo>();
            _shader = Shader.Find("Genesis/Model");
            if (_shader == null) Debug.LogError("Shader Genesis/Model not found");
        }

        public BbModel Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_cache.TryGetValue(id, out var m)) return m;
            m = null;
            string path = Find(id);
            if (path != null)
            {
                try
                {
                    m = BbModel.Load(id, path);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Models] {id}: {path}: {e.Message}");
                }
            }
            else Debug.LogWarning($"[Models] model '{id}' not found (models/<name>.bbmodel)");
            _cache[id] = m;
            return m;
        }

        private string Find(string id)
        {
            int colon = id.IndexOf(':');
            string ns = colon > 0 ? id.Substring(0, colon) : "genesis";
            string name = colon > 0 ? id.Substring(colon + 1) : id;
            string file = name + ".bbmodel";
            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                string p = Path.Combine(_sources[i].Folder, "models", ns, file);
                if (File.Exists(p)) return p;
                if (_sources[i].Id == ns)
                {
                    p = Path.Combine(_sources[i].Folder, "models", file);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        /// <summary>
        /// Подгрузить модели блоков, машин, предметов и врагов. Модели, которых нет, снимаются с определений
        /// блоков — такие блоки рисуются обычным кубом.
        /// </summary>
        public void Preload(ContentDatabase content)
        {
            int ok = 0;
            foreach (var b in content.Blocks.Items)
            {
                if (b.Model == null) continue;
                if (Get(b.Model) != null) ok++;
                else b.Model = null;
            }
            foreach (var it in content.Items.Items)
                if (it.Model != null && Get(it.Model) != null) ok++;
            foreach (var e in content.Enemies.Items)
                if (e.Model != null && Get(e.Model) != null) ok++;
            if (ok > 0) Debug.Log($"[Models] {ok} model references loaded");
        }

        /// <summary>Материалы модели (по одному на текстуру). overlay — поверх мира (предмет в руке).</summary>
        public Material[] Materials(BbModel m, bool overlay = false)
        {
            var dict = overlay ? _overlayMats : _mats;
            if (dict.TryGetValue(m, out var arr)) return arr;
            arr = new Material[m.Textures.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                var mat = new Material(_shader) { name = m.Id + "#" + i };
                mat.SetTexture("_MainTex", m.Textures[i]);
                mat.SetFloat("_Cutoff", 0.5f);
                if (overlay)
                {
                    mat.SetFloat("_ZTest", (float)CompareFunction.Always);
                    mat.renderQueue = 4000;
                    mat.SetShaderPassEnabled("ShadowCaster", false);
                    mat.SetShaderPassEnabled("DepthOnly", false);
                }
                arr[i] = mat;
            }
            dict[m] = arr;
            return arr;
        }

        public void Dispose()
        {
            foreach (var arr in _mats.Values)
                foreach (var mat in arr) UnityEngine.Object.Destroy(mat);
            foreach (var arr in _overlayMats.Values)
                foreach (var mat in arr) UnityEngine.Object.Destroy(mat);
            _mats.Clear();
            _overlayMats.Clear();
        }

        // ============================================================ Отрисовка

        /// <summary>Нарисовать модель в этом кадре. bones == null — поза покоя.</summary>
        public void Draw(BbModel m, Matrix4x4 world, Matrix4x4[] bones, MaterialPropertyBlock props,
            bool castShadows = true, bool overlay = false, int layer = 0, Camera camera = null)
        {
            var mats = Materials(m, overlay);
            var shadows = castShadows && !overlay ? ShadowCastingMode.On : ShadowCastingMode.Off;
            for (int b = 0; b < m.Bones.Length; b++)
            {
                var bone = m.Bones[b];
                if (bone.Mesh == null) continue;
                var mtx = bones != null ? world * bones[b] : world;
                for (int s = 0; s < bone.SubmeshTexture.Length; s++)
                    Graphics.DrawMesh(bone.Mesh, mtx, mats[bone.SubmeshTexture[s]], layer, camera, s, props, shadows, !overlay);
            }
        }

        /// <summary>
        /// Матрица для модели блока: блочный формат (0..16) заполняет клетку, модель существа ставится на центр
        /// клетки. Перёд модели (север в Blockbench) поворачивается в сторону facing.
        /// </summary>
        public static Matrix4x4 BlockMatrix(BbModel m, Int3 pos, Direction facing)
        {
            var off = facing.Offset();
            var rot = Quaternion.LookRotation(new Vector3(off.X, 0, off.Z) == Vector3.zero ? Vector3.forward : new Vector3(off.X, 0, off.Z));
            if (m.IsBlockFormat)
            {
                // Модель занимает x 0..1, y 0..1, z -1..0 — сдвигаем в клетку и вращаем вокруг центра
                return Matrix4x4.TRS(new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f), rot, Vector3.one) *
                       Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.5f));
            }
            return Matrix4x4.TRS(new Vector3(pos.X + 0.5f, pos.Y, pos.Z + 0.5f), rot, Vector3.one);
        }
    }
}
