using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Builds a simple acoustic guitar out of primitives so the app has something to show
    /// before a real 3D model is imported. The guitar lies flat, face up, with its neck
    /// pointing along local +Z and its pivot at the bottom of the body (so it sits on a plane).
    /// Dimensions are in meters, roughly a full-size acoustic (~1 m long).
    /// </summary>
    public class ProceduralGuitar : MonoBehaviour
    {
        [SerializeField] Color m_BodyColor = new Color(0.78f, 0.52f, 0.25f);
        [SerializeField] Color m_NeckColor = new Color(0.45f, 0.27f, 0.12f);
        [SerializeField] Color m_FretboardColor = new Color(0.16f, 0.10f, 0.06f);
        [SerializeField] Color m_MetalColor = new Color(0.85f, 0.85f, 0.80f);

        const int k_StringCount = 6;
        const int k_FretCount = 12;

        // Key heights (body bottom is y = 0).
        const float k_BodyTop = 0.10f;
        const float k_NeckZStart = 0.33f;
        const float k_NutZ = 0.78f;
        const float k_BridgeZ = -0.08f;

        void Awake()
        {
            if (transform.childCount == 0)
                Build();
        }

        public void Build()
        {
            // Body: two flat discs (lower and upper bout) give the figure-eight outline.
            Part(PrimitiveType.Cylinder, "LowerBout", new Vector3(0f, 0.05f, 0f), new Vector3(0.38f, 0.05f, 0.40f), m_BodyColor);
            Part(PrimitiveType.Cylinder, "UpperBout", new Vector3(0f, 0.05f, 0.24f), new Vector3(0.29f, 0.05f, 0.29f), m_BodyColor);
            Part(PrimitiveType.Cylinder, "SoundHole", new Vector3(0f, k_BodyTop + 0.0005f, 0.20f), new Vector3(0.09f, 0.001f, 0.09f), Color.black);
            Part(PrimitiveType.Cube, "Bridge", new Vector3(0f, k_BodyTop + 0.005f, k_BridgeZ), new Vector3(0.12f, 0.01f, 0.03f), m_FretboardColor);

            // Neck, fretboard and headstock.
            float neckLength = k_NutZ - k_NeckZStart;
            float neckCenterZ = (k_NutZ + k_NeckZStart) * 0.5f;
            Part(PrimitiveType.Cube, "Neck", new Vector3(0f, 0.0875f, neckCenterZ), new Vector3(0.05f, 0.025f, neckLength), m_NeckColor);
            Part(PrimitiveType.Cube, "Fretboard", new Vector3(0f, 0.103f, neckCenterZ - 0.05f), new Vector3(0.055f, 0.006f, neckLength + 0.10f), m_FretboardColor);
            Part(PrimitiveType.Cube, "Nut", new Vector3(0f, 0.108f, k_NutZ), new Vector3(0.055f, 0.006f, 0.006f), Color.white);
            var headstock = Part(PrimitiveType.Cube, "Headstock", new Vector3(0f, 0.085f, k_NutZ + 0.095f), new Vector3(0.08f, 0.02f, 0.18f), m_NeckColor);
            headstock.localRotation = Quaternion.Euler(12f, 0f, 0f);

            // Frets: spacing follows the real 12th-root-of-two rule along the scale length.
            float scaleLength = k_NutZ - k_BridgeZ;
            for (int i = 1; i <= k_FretCount; i++)
            {
                float distanceFromNut = scaleLength - scaleLength / Mathf.Pow(2f, i / 12f);
                Part(PrimitiveType.Cube, $"Fret{i}", new Vector3(0f, 0.1065f, k_NutZ - distanceFromNut), new Vector3(0.055f, 0.002f, 0.002f), m_MetalColor);
            }

            // Strings run from bridge to nut, spread slightly wider at the bridge.
            float stringLength = k_NutZ - k_BridgeZ;
            for (int i = 0; i < k_StringCount; i++)
            {
                float t = i / (float)(k_StringCount - 1) - 0.5f;
                float xNut = t * 0.042f;
                float xBridge = t * 0.055f;
                var start = new Vector3(xBridge, 0.112f, k_BridgeZ);
                var end = new Vector3(xNut, 0.112f, k_NutZ);
                float thickness = Mathf.Lerp(0.0022f, 0.0010f, i / (float)(k_StringCount - 1));
                var s = Part(PrimitiveType.Cylinder, $"String{i + 1}", (start + end) * 0.5f, new Vector3(thickness, stringLength * 0.5f, thickness), m_MetalColor);
                s.localRotation = Quaternion.FromToRotation(Vector3.up, end - start);
            }

            // Tuning pegs, three per side of the headstock.
            for (int i = 0; i < k_StringCount; i++)
            {
                float side = i < 3 ? -1f : 1f;
                float z = k_NutZ + 0.04f + (i % 3) * 0.045f;
                var peg = Part(PrimitiveType.Cylinder, $"Peg{i + 1}", new Vector3(side * 0.055f, 0.095f + (z - k_NutZ) * 0.21f, z), new Vector3(0.012f, 0.015f, 0.012f), m_MetalColor);
                peg.localRotation = Quaternion.Euler(0f, 0f, 90f);
            }

            // One collider on the root for future interaction (tap to select, strum, etc.).
            var box = gameObject.GetComponent<BoxCollider>();
            if (box == null)
                box = gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.06f, 0.36f);
            box.size = new Vector3(0.40f, 0.12f, 1.05f);
        }

        Transform Part(PrimitiveType type, string partName, Vector3 localPosition, Vector3 localScale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = partName;

            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying)
                    Destroy(col);
                else
                    DestroyImmediate(col);
            }

            // CreatePrimitive uses the active render pipeline's default material (URP Lit),
            // so tinting an instance of it keeps the right shader.
            var r = go.GetComponent<Renderer>();
            r.material.color = color;

            var t = go.transform;
            t.SetParent(transform, false);
            t.localPosition = localPosition;
            t.localScale = localScale;
            return t;
        }
    }
}
