using System.Threading;
using UnityEngine;

namespace Genesis.Client.UI
{
    /// <summary>
    /// Процедурный звук главного меню без аудиофайлов: ветер над пустошью (бурый шум с порывами),
    /// треск и гул костра, короткий щелчок при выборе пункта. Синтез — в OnAudioFilterRead (аудиопоток),
    /// с главного потока только Tick()/Click(). Появляется и исчезает плавно.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class MenuAmbience : MonoBehaviour
    {
        /// <summary>Общая громкость эмбиента (0..1).</summary>
        public float Volume = 0.55f;

        private int _rate = 48000;
        private System.Random _rng;
        private volatile float _target;
        private float _vol;
        private double _time;

        // Ветер
        private float _brownL, _brownR, _windHpL, _windHpR;
        // Костёр
        private int _crackLeft, _crackLen;
        private float _crackAmp, _crackPan, _crackLp, _fireLp, _fireLp2;
        // Щелчки интерфейса
        private int _tickRequests, _clickRequests;
        private int _blipLeft, _blipLen;
        private float _blipFreq, _blipAmp, _blipPhase;

        private void Awake()
        {
            _rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            _rng = new System.Random(20261031);
            var src = GetComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;
            src.spatialBlend = 0f;
            src.volume = 1f;
            src.Play(); // без клипа: звук целиком из OnAudioFilterRead
            _target = 1f;
        }

        /// <summary>Тихий щелчок — наведение на пункт.</summary>
        public void Tick() => Interlocked.Increment(ref _tickRequests);

        /// <summary>Щелчок пониже и погромче — выбор пункта.</summary>
        public void Click() => Interlocked.Increment(ref _clickRequests);

        private void OnDisable() => _target = 0f;

        private float Noise() => (float)(_rng.NextDouble() * 2.0 - 1.0);

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_rng == null) return;
            float dt = 1f / _rate;
            float target = _target * Volume;
            int frames = data.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                _time += dt;
                float t = (float)_time;
                _vol += (target - _vol) * 0.00004f; // ~0.5 с на появление

                // ---- Ветер: бурый шум (утечка интегратора), срезанные низы, медленные порывы
                _brownL = (_brownL + Noise() * 0.02f) * 0.9985f;
                _brownR = (_brownR + Noise() * 0.02f) * 0.9985f;
                _windHpL += (_brownL - _windHpL) * 0.0008f;
                _windHpR += (_brownR - _windHpR) * 0.0008f;
                float gust = 0.5f + 0.3f * Mathf.Sin(t * 0.69f) + 0.2f * Mathf.Sin(t * 1.7f + 1.3f) + 0.1f * Mathf.Sin(t * 0.23f + 4f);
                gust = Mathf.Max(0.12f, gust);
                float windL = (_brownL - _windHpL) * gust * 2.2f;
                float windR = (_brownR - _windHpR) * gust * 2.2f;

                // ---- Костёр: низкий гул + редкие щелчки углей
                _fireLp += (Noise() - _fireLp) * 0.015f;
                _fireLp2 += (_fireLp - _fireLp2) * 0.015f;
                float rumble = _fireLp2 * 0.9f;
                if (_crackLeft <= 0 && _rng.NextDouble() < 7.0 / _rate)
                {
                    _crackLen = (int)(_rate * (0.0015 + _rng.NextDouble() * 0.01));
                    _crackLeft = _crackLen;
                    _crackAmp = (float)(0.05 + _rng.NextDouble() * _rng.NextDouble() * 0.45);
                    _crackPan = (float)(_rng.NextDouble() * 0.6 - 0.3);
                }
                float crack = 0f;
                if (_crackLeft > 0)
                {
                    float env = (float)_crackLeft / _crackLen;
                    float raw = Noise() * _crackAmp * env * env;
                    _crackLp += (raw - _crackLp) * 0.5f; // чуть мягче «цифрового» щелчка
                    crack = raw - _crackLp * 0.6f;
                    _crackLeft--;
                }

                // ---- Щелчки интерфейса
                if (_blipLeft <= 0)
                {
                    if (Interlocked.Exchange(ref _clickRequests, 0) > 0) StartBlip(620f, 0.16f, 0.07f);
                    else if (Interlocked.Exchange(ref _tickRequests, 0) > 0) StartBlip(1400f, 0.06f, 0.03f);
                }
                float blip = 0f;
                if (_blipLeft > 0)
                {
                    float k = 1f - (float)_blipLeft / _blipLen;
                    _blipPhase += _blipFreq * (1f - 0.35f * k) * dt;
                    blip = Mathf.Sin(_blipPhase * 2f * Mathf.PI) * _blipAmp * Mathf.Exp(-k * 6f);
                    _blipLeft--;
                }

                float ambL = (windL * 0.55f + rumble * 0.25f + crack * (1f - _crackPan)) * _vol;
                float ambR = (windR * 0.55f + rumble * 0.25f + crack * (1f + _crackPan)) * _vol;
                float uiGain = _target > 0f ? Volume : 0f;
                float l = ambL + blip * uiGain, r = ambR + blip * uiGain;

                int o = i * channels;
                if (channels == 1) data[o] = (l + r) * 0.5f;
                else
                {
                    data[o] = l;
                    data[o + 1] = r;
                    for (int c = 2; c < channels; c++) data[o + c] = 0f;
                }
            }
        }

        private void StartBlip(float freq, float amp, float seconds)
        {
            _blipFreq = freq;
            _blipAmp = amp;
            _blipLen = Mathf.Max(1, (int)(_rate * seconds));
            _blipLeft = _blipLen;
            _blipPhase = 0f;
        }
    }
}
