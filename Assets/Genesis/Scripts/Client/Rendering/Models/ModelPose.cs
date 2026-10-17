using UnityEngine;

namespace Genesis.Client.Rendering.Models
{
    /// <summary>
    /// Поза модели: матрицы костей (пространство модели Unity) для анимации в момент времени.
    /// Повторяет правила Blockbench: поворот кадра вычитается по X и Y и прибавляется по Z, смещение — минус по X.
    /// Между анимациями — плавный переход (смешивание матриц за BlendTime).
    /// </summary>
    public sealed class ModelPose
    {
        public const float BlendTime = 0.2f;

        public readonly BbModel Model;
        public readonly Matrix4x4[] Bones;
        private readonly Matrix4x4[] _bb, _prev;
        private BbModel.Anim _anim, _prevAnim;
        private float _time, _prevTime, _blend = 1f;
        private float _life;

        public string Current => _anim?.Name;

        public ModelPose(BbModel model)
        {
            Model = model;
            int n = model.Bones.Length;
            Bones = new Matrix4x4[n];
            _bb = new Matrix4x4[n];
            _prev = new Matrix4x4[n];
            for (int i = 0; i < n; i++) Bones[i] = Matrix4x4.identity;
        }

        /// <summary>Сменить анимацию (если такой нет — поза покоя). Повторный вызов с той же — ничего не делает.</summary>
        public void Play(string name, bool restart = false)
        {
            BbModel.Anim next = null;
            if (name != null) Model.Animations.TryGetValue(name, out next);
            if (next == _anim && !restart) return;
            _prevAnim = _anim;
            _prevTime = _time;
            _anim = next;
            _time = 0;
            _blend = _prevAnim != null || next != null ? 0f : 1f;
        }

        public bool HasAnimation(string name) => name != null && Model.Animations.ContainsKey(name);

        /// <summary>Анимация «once» доиграла.</summary>
        public bool Finished => _anim != null && _anim.Loop != "loop" && _time >= _anim.Length;

        public void Update(float dt, float speed = 1f)
        {
            _time += dt * speed;
            _prevTime += dt * speed;
            _life += dt;
            _blend = Mathf.Min(1f, _blend + dt / BlendTime);

            Compute(_anim, _time, Bones);
            if (_blend < 1f)
            {
                Compute(_prevAnim, _prevTime, _prev);
                for (int i = 0; i < Bones.Length; i++) Bones[i] = Lerp(_prev[i], Bones[i], _blend);
            }
        }

        /// <summary>
        /// Дополнительно повернуть кость (и всех её потомков) вокруг её точки вращения — например, голову по
        /// направлению взгляда. Вызывать после Update. rot — в пространстве модели Unity.
        /// </summary>
        public void RotateBone(string name, Quaternion rot)
        {
            var bones = Model.Bones;
            int head = -1;
            for (int i = 0; i < bones.Length; i++)
                if (string.Equals(bones[i].Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    head = i;
                    break;
                }
            if (head < 0) return;
            var p = BbModel.ToUnity(bones[head].Pivot);
            var x = Matrix4x4.Translate(p) * Matrix4x4.Rotate(rot) * Matrix4x4.Translate(-p);
            var old = Bones[head];
            var change = old * x * old.inverse;
            for (int i = head; i < bones.Length; i++)
                if (i == head || IsDescendant(i, head)) Bones[i] = change * Bones[i];
        }

        private bool IsDescendant(int bone, int ancestor)
        {
            for (int p = Model.Bones[bone].Parent; p >= 0; p = Model.Bones[p].Parent)
                if (p == ancestor) return true;
            return false;
        }

        private void Compute(BbModel.Anim anim, float time, Matrix4x4[] result)
        {
            var bones = Model.Bones;
            float t = time;
            if (anim != null && anim.Length > 0)
                t = anim.Loop == "loop" ? Mathf.Repeat(time, anim.Length) : Mathf.Min(time, anim.Length);

            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                var rot = b.Rotation;
                var pos = Vector3.zero;
                var scl = Vector3.one;
                if (anim != null && anim.Tracks.TryGetValue(i, out var tr))
                {
                    if (tr.Rotation.Length > 0)
                    {
                        var r = Sample(tr.Rotation, t, _life, Vector3.zero);
                        rot += new Vector3(-r.x, -r.y, r.z);
                    }
                    if (tr.Position.Length > 0)
                    {
                        var p = Sample(tr.Position, t, _life, Vector3.zero);
                        pos = new Vector3(-p.x, p.y, p.z);
                    }
                    if (tr.Scale.Length > 0) scl = Sample(tr.Scale, t, _life, Vector3.one);
                }
                var local = Matrix4x4.Translate(b.Pivot + pos) * BbModel.RotationBb(rot) * Matrix4x4.Scale(scl) * Matrix4x4.Translate(-b.Pivot);
                _bb[i] = b.Parent >= 0 ? _bb[b.Parent] * local : local;
                result[i] = BbModel.BbToUnity * _bb[i] * BbModel.UnityToBb;
            }
        }

        private static Vector3 Value(in BbModel.Key k, float anim, float life) =>
            new Vector3(k.X.Eval(anim, life), k.Y.Eval(anim, life), k.Z.Eval(anim, life));

        private static Vector3 Sample(BbModel.Key[] keys, float t, float life, Vector3 fallback)
        {
            if (keys.Length == 0) return fallback;
            if (t <= keys[0].Time || keys.Length == 1) return Value(keys[0], t, life);
            int last = keys.Length - 1;
            if (t >= keys[last].Time) return Value(keys[last], t, life);
            int i = 0;
            while (i < last - 1 && keys[i + 1].Time <= t) i++;
            var a = keys[i];
            var b = keys[i + 1];
            float span = b.Time - a.Time;
            float u = span > 1e-5f ? (t - a.Time) / span : 1f;
            var va = Value(a, t, life);
            var vb = Value(b, t, life);
            if (a.Interp == 1) return va;
            if (a.Interp == 2 || b.Interp == 2)
            {
                // Catmull-Rom по соседним кадрам
                var p0 = i > 0 ? Value(keys[i - 1], t, life) : va;
                var p3 = i + 2 <= last ? Value(keys[i + 2], t, life) : vb;
                float u2 = u * u, u3 = u2 * u;
                return 0.5f * (2f * va + (-p0 + vb) * u + (2f * p0 - 5f * va + 4f * vb - p3) * u2 + (-p0 + 3f * va - 3f * vb + p3) * u3);
            }
            return Vector3.LerpUnclamped(va, vb, u);
        }

        private static Matrix4x4 Lerp(Matrix4x4 a, Matrix4x4 b, float t)
        {
            var r = new Matrix4x4();
            for (int i = 0; i < 16; i++) r[i] = Mathf.Lerp(a[i], b[i], t);
            return r;
        }
    }
}
