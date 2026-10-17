using System.Collections.Generic;
using Genesis.Client.Rendering.Models;
using Genesis.Net;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>Простые аватары других игроков (тело + голова) с плавным движением и табличкой с ником.</summary>
    public sealed class PlayerAvatars : MonoBehaviour
    {
        private IGameSession _session;
        private Material _bodyMat, _headMat;
        private GUIStyle _nameStyle;

        private sealed class Avatar
        {
            public GameObject Root;
            public Transform Head;
            public Renderer[] Renderers;
            public Vector3 Shown;
            public float Yaw;
            public ModelPose Pose;
            public float Speed;
        }

        /// <summary>Модель игрока: models/player.bbmodel (анимации «idle», «walk»; кость «head» смотрит по взгляду).</summary>
        public const string PlayerModel = "genesis:player";
        // Создаётся лениво: Unity не разрешает создавать его в инициализаторе поля MonoBehaviour
        private MaterialPropertyBlock _propsBlock;
        private MaterialPropertyBlock _props => _propsBlock ?? (_propsBlock = new MaterialPropertyBlock());
        private static readonly int InstLight = Shader.PropertyToID("_InstLight");

        private readonly Dictionary<int, Avatar> _avatars = new Dictionary<int, Avatar>();
        private readonly List<int> _remove = new List<int>();

        private LightProbe _probe;
        private MaterialPropertyBlock _block;

        private void OnDestroy() => _probe?.Dispose();

        public void Init(IGameSession session, Shader unlit)
        {
            _session = session;
            _probe = new LightProbe(session.World, new MeshBlockTable(session.Content));
            _bodyMat = new Material(unlit);
            _bodyMat.SetColor("_Color", new Color(0.25f, 0.55f, 0.85f, 1f));
            _headMat = new Material(unlit);
            _headMat.SetColor("_Color", new Color(0.92f, 0.78f, 0.62f, 1f));
        }

        private static GameObject Box(Transform parent, Vector3 localPos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Цвет вершин = затенение граней; шейдер Genesis/Color умножает его на _Color
            var mf = go.GetComponent<MeshFilter>();
            var mesh = mf.mesh;
            var normals = mesh.normals;
            var colors = new Color32[mesh.vertexCount];
            for (int i = 0; i < colors.Length; i++)
            {
                byte shade = (byte)(normals[i].y > 0.5f ? 255 : (normals[i].y < -0.5f ? 140 : 200));
                colors[i] = new Color32(shade, shade, shade, 255);
            }
            mesh.colors32 = colors;
            return go;
        }

        private Avatar Create(RemotePlayerView p)
        {
            var root = new GameObject("Player " + p.Name);
            root.transform.SetParent(transform, false);
            var model = ModelLibrary.Current?.Get(PlayerModel);
            if (model != null)
            {
                var p0 = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
                root.transform.position = p0;
                return new Avatar { Root = root, Head = root.transform, Shown = p0, Yaw = p.Yaw, Renderers = new Renderer[0], Pose = new ModelPose(model) };
            }
            Box(root.transform, new Vector3(0, 0.7f, 0), new Vector3(0.55f, 1.4f, 0.3f), _bodyMat);
            var head = Box(root.transform, new Vector3(0, 1.62f, 0), new Vector3(0.45f, 0.45f, 0.45f), _headMat);
            var pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
            root.transform.position = pos;
            return new Avatar { Root = root, Head = head.transform, Shown = pos, Yaw = p.Yaw, Renderers = root.GetComponentsInChildren<Renderer>() };
        }

        private void LateUpdate()
        {
            if (_session == null) return;
            var seen = new HashSet<int>();
            float k = 1f - Mathf.Exp(-Time.deltaTime * 15f);
            foreach (var p in _session.OtherPlayers)
            {
                seen.Add(p.Id);
                if (!_avatars.TryGetValue(p.Id, out var a)) _avatars[p.Id] = a = Create(p);
                var target = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
                var before = a.Shown;
                a.Shown = (target - a.Shown).sqrMagnitude > 64f ? target : Vector3.Lerp(a.Shown, target, k);
                a.Yaw = Mathf.LerpAngle(a.Yaw, p.Yaw, k);
                a.Root.transform.position = a.Shown;
                a.Root.transform.rotation = Quaternion.Euler(0, a.Yaw, 0);
                if (a.Pose != null)
                {
                    var d = a.Shown - before;
                    d.y = 0;
                    float speed = Time.deltaTime > 0 ? d.magnitude / Time.deltaTime : 0;
                    a.Speed = Mathf.Lerp(a.Speed, speed, 1f - Mathf.Exp(-Time.deltaTime * 8f));
                    bool walking = a.Speed > 0.4f;
                    a.Pose.Play(walking ? "walk" : "idle");
                    a.Pose.Update(Time.deltaTime, walking ? Mathf.Clamp(a.Speed / 4.3f, 0.6f, 1.8f) : 1f);
                    a.Pose.RotateBone("head", Quaternion.Euler(p.Pitch, 0, 0));
                    _probe.Sample(new Genesis.Core.Int3(Mathf.FloorToInt(a.Shown.x), Mathf.FloorToInt(a.Shown.y + 1f), Mathf.FloorToInt(a.Shown.z)), out byte sky, out byte blk);
                    _props.SetVector(InstLight, new Vector4(sky / 255f, blk / 255f, 0f, 0f));
                    ModelLibrary.Current.Draw(a.Pose.Model, a.Root.transform.localToWorldMatrix, a.Pose.Bones, _props);
                    continue;
                }
                a.Head.localRotation = Quaternion.Euler(p.Pitch, 0, 0);
                // Яркость по освещению места: ночью и в пещерах аватар не светится
                _block = _block ?? new MaterialPropertyBlock();
                float light = ActorLight.Factor(_probe, a.Shown);
                foreach (var r in a.Renderers)
                {
                    var baseColor = r.sharedMaterial.GetColor("_Color");
                    r.GetPropertyBlock(_block);
                    _block.SetColor("_Color", new Color(baseColor.r * light, baseColor.g * light, baseColor.b * light, 1f));
                    r.SetPropertyBlock(_block);
                }
            }
            _remove.Clear();
            foreach (var id in _avatars.Keys)
                if (!seen.Contains(id)) _remove.Add(id);
            foreach (var id in _remove)
            {
                Destroy(_avatars[id].Root);
                _avatars.Remove(id);
            }
        }

        private void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null || _session == null) return;
            if (_nameStyle == null)
            {
                _nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 13, fontStyle = FontStyle.Bold };
                _nameStyle.normal.textColor = Color.white;
            }
            foreach (var p in _session.OtherPlayers)
            {
                if (!_avatars.TryGetValue(p.Id, out var a)) continue;
                var sp = cam.WorldToScreenPoint(a.Shown + new Vector3(0, 2.15f, 0));
                if (sp.z <= 0 || sp.z > 64) continue;
                var r = new Rect(sp.x - 80, Screen.height - sp.y - 12, 160, 24);
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, 0.45f);
                var size = _nameStyle.CalcSize(new GUIContent(p.Name));
                GUI.DrawTexture(new Rect(sp.x - size.x / 2 - 6, r.y + 2, size.x + 12, 20), Texture2D.whiteTexture);
                GUI.color = old;
                GUI.Label(r, p.Name, _nameStyle);
            }
        }
    }
}
