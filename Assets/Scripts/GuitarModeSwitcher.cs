using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace GuitarAR
{
    public enum GuitarMode
    {
        /// <summary>The guitar hangs on a person found by body tracking.</summary>
        Body,
        /// <summary>Tap a detected plane to put the guitar there.</summary>
        Surface,
    }

    /// <summary>
    /// Switches between wearing the guitar on a tracked body and placing it on a surface.
    /// ARKit can't run body tracking and plane detection in one session, so only one of
    /// the two managers is enabled at a time. Draws the on-screen switch button and hints.
    /// </summary>
    public class GuitarModeSwitcher : MonoBehaviour
    {
        [SerializeField] GuitarMode m_StartMode = GuitarMode.Body;

        static Rect s_ButtonRect;

        ARPlaneManager m_PlaneManager;
        GuitarPlacer m_Placer;
        GuitarBodyAttacher m_BodyAttacher;
        GuitarMode m_Mode;
        GUIStyle m_ButtonStyle;
        GUIStyle m_HintStyle;

        public GuitarMode Mode => m_Mode;

        /// <summary>
        /// True if a screen position (pixels, origin bottom-left, as from touch input) is on the
        /// switch button, so taps on it don't also place the guitar.
        /// </summary>
        public static bool IsOverButton(Vector2 screenPosition)
        {
            var guiPoint = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            return s_ButtonRect.Contains(guiPoint);
        }

        // Older scenes were built before body tracking existed; add what's missing so a
        // rebuild is enough and the scene doesn't have to be regenerated.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInScene()
        {
            var origin = Object.FindAnyObjectByType<XROrigin>();
            if (origin == null || origin.GetComponent<GuitarModeSwitcher>() != null)
                return;

            var go = origin.gameObject;
            if (go.GetComponent<ARHumanBodyManager>() == null)
                go.AddComponent<ARHumanBodyManager>();
            if (go.GetComponent<GuitarBodyAttacher>() == null)
                go.AddComponent<GuitarBodyAttacher>();
            go.AddComponent<GuitarModeSwitcher>();
        }

        void Awake()
        {
            m_PlaneManager = GetComponent<ARPlaneManager>();
            m_Placer = GetComponent<GuitarPlacer>();
            m_BodyAttacher = GetComponent<GuitarBodyAttacher>();
        }

        void Start()
        {
            SetMode(m_StartMode);
        }

        public void SetMode(GuitarMode mode)
        {
            m_Mode = mode;
            bool surface = mode == GuitarMode.Surface;

            if (m_BodyAttacher != null)
                m_BodyAttacher.enabled = !surface;

            if (m_PlaneManager != null)
            {
                m_PlaneManager.enabled = surface;
                foreach (var plane in m_PlaneManager.trackables)
                    plane.gameObject.SetActive(surface);
            }

            if (m_Placer != null)
            {
                m_Placer.enabled = surface;
                if (m_Placer.PlacedGuitar != null)
                    m_Placer.PlacedGuitar.SetActive(surface);
            }
        }

        void OnGUI()
        {
            EnsureStyles();

            float width = Screen.width * 0.6f;
            float height = Screen.height * 0.065f;
            s_ButtonRect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - Screen.height * 0.06f, width, height);

            string label = m_Mode == GuitarMode.Body ? "Режим: на теле" : "Режим: на поверхности";
            if (GUI.Button(s_ButtonRect, label, m_ButtonStyle))
                SetMode(m_Mode == GuitarMode.Body ? GuitarMode.Surface : GuitarMode.Body);

            string hint = Hint();
            if (!string.IsNullOrEmpty(hint))
            {
                var hintRect = new Rect(Screen.width * 0.05f, Screen.height * 0.08f, Screen.width * 0.9f, Screen.height * 0.1f);
                GUI.Label(hintRect, hint, m_HintStyle);
            }
        }

        string Hint()
        {
            if (m_Mode == GuitarMode.Surface)
                return m_Placer != null && m_Placer.PlacedGuitar == null ? "Найди пол или стол и нажми на плоскость" : null;

            if (m_BodyAttacher == null)
                return null;
            if (!m_BodyAttacher.IsSupported)
                return "Это устройство не поддерживает отслеживание тела";
            return m_BodyAttacher.IsTrackingBody ? null : "Наведи камеру на человека, чтобы он был виден целиком";
        }

        void EnsureStyles()
        {
            if (m_ButtonStyle != null)
                return;

            int fontSize = Mathf.RoundToInt(Screen.height * 0.022f);
            m_ButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = fontSize, fontStyle = FontStyle.Bold };
            m_HintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = Color.white },
            };
        }
    }
}
