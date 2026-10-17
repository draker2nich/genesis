using Genesis.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Genesis.Client.Rendering
{
    /// <summary>Рамка вокруг блока под прицелом.</summary>
    public sealed class BlockHighlight : MonoBehaviour
    {
        private MeshRenderer _renderer;

        public void Init(Material lineMaterial)
        {
            var mesh = new Mesh { name = "BlockOutline" };
            const float e = 0.002f;
            float a = -e, b = 1 + e;
            var v = new[]
            {
                new Vector3(a, a, a), new Vector3(b, a, a), new Vector3(b, a, b), new Vector3(a, a, b),
                new Vector3(a, b, a), new Vector3(b, b, a), new Vector3(b, b, b), new Vector3(a, b, b)
            };
            var idx = new[]
            {
                0, 1, 1, 2, 2, 3, 3, 0,
                4, 5, 5, 6, 6, 7, 7, 4,
                0, 4, 1, 5, 2, 6, 3, 7
            };
            var colors = new Color32[8];
            for (int i = 0; i < 8; i++) colors[i] = new Color32(255, 255, 255, 255);
            mesh.vertices = v;
            mesh.colors32 = colors;
            mesh.SetIndices(idx, MeshTopology.Lines, 0);

            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            _renderer = gameObject.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = lineMaterial;
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            Hide();
        }

        public void Show(Int3 block)
        {
            transform.position = new Vector3(block.X, block.Y, block.Z);
            _renderer.enabled = true;
        }

        public void Hide()
        {
            if (_renderer != null) _renderer.enabled = false;
        }
    }
}
