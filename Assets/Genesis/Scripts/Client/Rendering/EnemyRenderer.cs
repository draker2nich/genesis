using System.Collections.Generic;
using Genesis.Client.Rendering.Models;
using Genesis.Net;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>
    /// Враги: модель Blockbench из описания (анимации «walk» / «idle», вспышка при попадании) или, если модели нет,
    /// коробки цвета из описания. Полоска здоровья над головой.
    /// </summary>
    public sealed class EnemyRenderer : MonoBehaviour
    {
        private IGameSession _session;
        private Shader _shader;

        private sealed class View
        {
            public GameObject Root;
            public Material Mat;
            public Color BaseColor;
            public Vector3 Shown;
            public float Yaw;
            public BbModel Model;
            public ModelPose Pose;
            public float Speed;
        }

        // Создаётся лениво: Unity не разрешает создавать его в инициализаторе поля MonoBehaviour
        private MaterialPropertyBlock _propsBlock;
        private MaterialPropertyBlock _props => _propsBlock ?? (_propsBlock = new MaterialPropertyBlock());
        private static readonly int InstLight = Shader.PropertyToID("_InstLight");

        private readonly Dictionary<int, View> _views = new Dictionary<int, View>();
        private readonly List<int> _remove = new List<int>();
        private readonly HashSet<int> _seen = new HashSet<int>();

        private LightProbe _probe;

        private void OnDestroy() => _probe?.Dispose();

        public void Init(IGameSession session, Shader shader)
        {
            _session = session;
            _probe = new LightProbe(session.World, new MeshBlockTable(session.Content));
            _shader = shader;
        }

        private View Create(EnemyView e)
        {
            var root = new GameObject("Enemy " + e.Id);
            root.transform.SetParent(transform, false);
            var mat = new Material(_shader);
            ColorUtility.TryParseHtmlString(e.Def.Color, out var color);
            mat.SetColor("_Color", color);
            var pos0 = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);

            var model = ModelLibrary.Current?.Get(e.Def.Model);
            if (model != null)
            {
                root.transform.position = pos0;
                return new View { Root = root, Mat = mat, BaseColor = color, Shown = pos0, Yaw = e.Yaw, Model = model, Pose = new ModelPose(model) };
            }

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(e.Def.Width, e.Def.Height * 0.8f, e.Def.Width);
            body.transform.localPosition = new Vector3(0, e.Def.Height * 0.4f, 0);
            Shade(body, mat);

            var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(head.GetComponent<Collider>());
            head.transform.SetParent(root.transform, false);
            float hs = e.Def.Width * 0.75f;
            head.transform.localScale = new Vector3(hs, e.Def.Height * 0.25f, hs);
            head.transform.localPosition = new Vector3(0, e.Def.Height * 0.88f, e.Def.Width * 0.2f);
            Shade(head, mat);

            var pos = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
            root.transform.position = pos;
            return new View { Root = root, Mat = mat, BaseColor = color, Shown = pos, Yaw = e.Yaw };
        }

        private static void Shade(GameObject go, Material mat)
        {
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mesh = go.GetComponent<MeshFilter>().mesh;
            var normals = mesh.normals;
            var colors = new Color32[mesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                byte s = (byte)(normals[i].y > 0.5f ? 255 : (normals[i].y < -0.5f ? 120 : 190));
                colors[i] = new Color32(s, s, s, 255);
            }
            mesh.colors32 = colors;
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            _seen.Clear();
            float k = 1f - Mathf.Exp(-Time.deltaTime * 12f);
            foreach (var e in _session.Enemies)
            {
                _seen.Add(e.Id);
                if (!_views.TryGetValue(e.Id, out var v)) _views[e.Id] = v = Create(e);
                var target = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
                var before = v.Shown;
                v.Shown = (target - v.Shown).sqrMagnitude > 25f ? target : Vector3.Lerp(v.Shown, target, k);
                v.Yaw = Mathf.LerpAngle(v.Yaw, e.Yaw, k);
                v.Root.transform.SetPositionAndRotation(v.Shown, Quaternion.Euler(0, v.Yaw, 0));
                if (v.Model != null)
                {
                    var d = v.Shown - before;
                    d.y = 0;
                    float speed = Time.deltaTime > 0 ? d.magnitude / Time.deltaTime : 0;
                    v.Speed = Mathf.Lerp(v.Speed, speed, 1f - Mathf.Exp(-Time.deltaTime * 8f));
                    bool walking = v.Speed > 0.35f;
                    v.Pose.Play(AnimFor(v.Pose, e.State, walking));
                    v.Pose.Update(Time.deltaTime, walking ? Mathf.Clamp(v.Speed / Mathf.Max(0.5f, e.Def.Speed), 0.6f, 1.6f) : 1f);
                    _probe.Sample(new Genesis.Core.Int3(Mathf.FloorToInt(v.Shown.x), Mathf.FloorToInt(v.Shown.y + 0.5f), Mathf.FloorToInt(v.Shown.z)), out byte sky, out byte blk);
                    _props.SetVector(InstLight, new Vector4(sky / 255f, blk / 255f, e.Hurt > 0 ? 1f : 0f, 0f));
                    ModelLibrary.Current.Draw(v.Model, v.Root.transform.localToWorldMatrix, v.Pose.Bones, _props);
                    continue;
                }
                var baseCol = v.BaseColor * ActorLight.Factor(_probe, v.Shown);
                baseCol.a = 1f;
                v.Mat.SetColor("_Color", e.Hurt > 0 ? Color.Lerp(baseCol, new Color(1f, 0.35f, 0.3f), 0.7f) : baseCol);
            }
            DrawProjectiles();
            _remove.Clear();
            foreach (var id in _views.Keys)
                if (!_seen.Contains(id)) _remove.Add(id);
            foreach (var id in _remove)
            {
                Destroy(_views[id].Root);
                Destroy(_views[id].Mat);
                _views.Remove(id);
            }
        }

        /// <summary>Анимация по состоянию существа; если такой в модели нет — ходьба/покой.</summary>
        private static string AnimFor(ModelPose pose, Genesis.Gameplay.CreatureState state, bool walking)
        {
            string want = null;
            switch (state)
            {
                case Genesis.Gameplay.CreatureState.Attack: want = "attack"; break;
                case Genesis.Gameplay.CreatureState.Windup: want = "windup"; break;
                case Genesis.Gameplay.CreatureState.Special: want = pose.HasAnimation("special") ? "special" : "attack"; break;
                case Genesis.Gameplay.CreatureState.Curled: want = "curl"; break;
                case Genesis.Gameplay.CreatureState.Stunned: want = "stunned"; break;
                case Genesis.Gameplay.CreatureState.Flee: want = "walk"; break;
            }
            if (want != null && pose.HasAnimation(want)) return want;
            return walking ? "walk" : "idle";
        }

        // ---- Снаряды (кислота) и облака спор: простые светящиеся кубики
        private Mesh _cube;
        private Material _acidMat, _sporeMat;

        private void DrawProjectiles()
        {
            if (_session.Projectiles.Count == 0) return;
            if (_cube == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _cube = go.GetComponent<MeshFilter>().sharedMesh;
                Destroy(go);
                _acidMat = new Material(_shader);
                _acidMat.SetColor("_Color", new Color(0.55f, 1f, 0.25f));
                _sporeMat = new Material(_shader);
                _sporeMat.SetColor("_Color", new Color(0.62f, 0.7f, 0.35f));
            }
            float t = Time.time;
            foreach (var p in _session.Projectiles)
            {
                var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
                if (!p.Cloud)
                {
                    Graphics.DrawMesh(_cube, Matrix4x4.TRS(pos, Quaternion.Euler(t * 300f, t * 200f, 0), Vector3.one * 0.22f), _acidMat, 0);
                    continue;
                }
                // Облако: клубы, медленно кружащие внутри радиуса
                for (int i = 0; i < 14; i++)
                {
                    float a = i * 2.39f + t * (0.4f + (i % 3) * 0.15f);
                    float r = p.Radius * (0.25f + 0.7f * Mathf.Repeat(i * 0.37f, 1f));
                    var o = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(t * 0.9f + i) * 0.6f + (i % 4) * 0.25f - 0.3f, Mathf.Sin(a) * r);
                    float size = 0.35f + 0.2f * Mathf.Sin(t * 2f + i);
                    Graphics.DrawMesh(_cube, Matrix4x4.TRS(pos + o, Quaternion.Euler(i * 30f, t * 40f + i * 50f, 0), Vector3.one * size), _sporeMat, 0);
                }
            }
        }

        private void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null || _session == null) return;
            foreach (var e in _session.Enemies)
            {
                if (!_views.TryGetValue(e.Id, out var v)) continue;
                if (e.Health >= e.Def.Health) continue;
                var sp = cam.WorldToScreenPoint(v.Shown + new Vector3(0, e.Def.Height + 0.35f, 0));
                if (sp.z <= 0 || sp.z > 32) continue;
                var r = new Rect(sp.x - 25, Screen.height - sp.y, 50, 6);
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, 0.6f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = new Color(0.85f, 0.2f, 0.2f);
                GUI.DrawTexture(new Rect(r.x + 1, r.y + 1, (r.width - 2) * Mathf.Clamp01(e.Health / e.Def.Health), r.height - 2), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }
    }
}
