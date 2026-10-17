using Genesis.Core;
using UnityEngine;

namespace Genesis.Client.Rendering
{
    /// <summary>Яркость существа в точке (для неосвещённого шейдера аватаров и врагов): небо × время суток или свет блоков.</summary>
    public static class ActorLight
    {
        private static readonly int DaylightId = Shader.PropertyToID("_GenesisDaylight");

        public static float Factor(LightProbe probe, Vector3 feet)
        {
            probe.Sample(new Int3(Mathf.FloorToInt(feet.x), Mathf.FloorToInt(feet.y + 1f), Mathf.FloorToInt(feet.z)), out byte sky, out byte blk);
            float day = Shader.GetGlobalFloat(DaylightId);
            float s = Mathf.Pow(sky / 255f, 1.6f) * Mathf.Lerp(0.12f, 1f, day);
            float b = Mathf.Pow(blk / 255f, 1.35f);
            return Mathf.Clamp(Mathf.Max(s, b) + 0.05f, 0.05f, 1f);
        }
    }
}
