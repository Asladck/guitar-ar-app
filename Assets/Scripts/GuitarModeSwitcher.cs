using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace GuitarAR
{
    public enum GuitarMode
    {
        /// <summary>The guitar hangs on a person seen by the back camera (body tracking).</summary>
        Body,
        /// <summary>The guitar hangs on the person looking into the front camera.</summary>
        Selfie,
        /// <summary>Tap a detected plane to put the guitar there.</summary>
        Surface,
    }

    /// <summary>
    /// Switches between wearing the guitar on a tracked body (back camera), on yourself
    /// (front camera) and placing it on a surface. ARKit runs one of body tracking, face
    /// tracking or plane detection per session, so only one mode's manager is enabled at a time.
    /// Draws the on-screen mode buttons and hints.
    /// </summary>
    public class GuitarModeSwitcher : MonoBehaviour
    {
        [SerializeField] GuitarMode m_StartMode = GuitarMode.Body;

        // In GuitarMode order.
        static readonly string[] k_ModeLabels = { "На человеке", "Селфи", "На поверхности" };

        static Rect s_ButtonRect;

        ARPlaneManager m_PlaneManager;
        GuitarPlacer m_Placer;
        GuitarBodyAttacher m_BodyAttacher;
        GuitarSelfieAttacher m_SelfieAttacher;
        GuitarMode m_Mode;
        GUIStyle m_ButtonStyle;
        GUIStyle m_HintStyle;
        GUIStyle m_DiagnosticStyle;

        public GuitarMode Mode => m_Mode;

        /// <summary>
        /// True if a screen position (pixels, origin bottom-left, as from touch input) is on the
        /// mode buttons, so taps on it don't also place the guitar.
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
            if (origin == null)
                return;

            var go = origin.gameObject;
            if (go.GetComponent<GuitarBodyAttacher>() == null)
                go.AddComponent<GuitarBodyAttacher>();
            if (go.GetComponent<GuitarSelfieAttacher>() == null)
                go.AddComponent<GuitarSelfieAttacher>();
            if (go.GetComponent<GuitarModeSwitcher>() == null)
                go.AddComponent<GuitarModeSwitcher>();
        }

        void Awake()
        {
            m_PlaneManager = GetComponent<ARPlaneManager>();
            m_Placer = GetComponent<GuitarPlacer>();
            m_BodyAttacher = GetComponent<GuitarBodyAttacher>();
            m_SelfieAttacher = GetComponent<GuitarSelfieAttacher>();
        }

        void Start()
        {
            SetMode(m_StartMode);
        }

        public void SetMode(GuitarMode mode)
        {
            m_Mode = mode;
            bool surface = mode == GuitarMode.Surface;

            // Turn the old mode off before the new one on, so the session never asks for both.
            if (m_BodyAttacher != null && mode != GuitarMode.Body)
                m_BodyAttacher.enabled = false;
            if (m_SelfieAttacher != null && mode != GuitarMode.Selfie)
                m_SelfieAttacher.enabled = false;

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

            if (m_BodyAttacher != null && mode == GuitarMode.Body)
                m_BodyAttacher.enabled = true;
            if (m_SelfieAttacher != null && mode == GuitarMode.Selfie)
                m_SelfieAttacher.enabled = true;
        }

        void OnGUI()
        {
            EnsureStyles();

            float width = Screen.width * 0.9f;
            float height = Screen.height * 0.065f;
            s_ButtonRect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - Screen.height * 0.06f, width, height);

            int selected = GUI.Toolbar(s_ButtonRect, (int)m_Mode, k_ModeLabels, m_ButtonStyle);
            if (selected != (int)m_Mode)
                SetMode((GuitarMode)selected);

            if (m_Mode == GuitarMode.Selfie && m_SelfieAttacher != null)
            {
                var diagnostic = m_SelfieAttacher.DiagnosticText();
                if (!string.IsNullOrEmpty(diagnostic))
                {
                    var rect = new Rect(Screen.width * 0.03f, Screen.height * 0.2f, Screen.width * 0.94f, Screen.height * 0.6f);
                    GUI.Label(rect, diagnostic, m_DiagnosticStyle);
                }
            }

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

            if (m_Mode == GuitarMode.Selfie)
            {
                if (m_SelfieAttacher == null)
                    return null;
                if (!m_SelfieAttacher.IsSupported)
                    return "Это устройство не поддерживает переднюю AR-камеру";
                return m_SelfieAttacher.IsTracking ? null : "Посмотри в переднюю камеру";
            }

            if (m_BodyAttacher == null)
                return null;
            if (!m_BodyAttacher.IsSupported)
                return "Это устройство не поддерживает отслеживание тела";
            return m_BodyAttacher.IsTracking ? null : "Наведи камеру на человека, чтобы он был виден целиком";
        }

        void EnsureStyles()
        {
            if (m_ButtonStyle != null)
                return;

            int fontSize = Mathf.RoundToInt(Screen.height * 0.018f);
            m_ButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = fontSize, fontStyle = FontStyle.Bold };
            m_HintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                normal = { textColor = Color.white },
            };
            m_DiagnosticStyle = new GUIStyle(m_HintStyle)
            {
                fontSize = Mathf.RoundToInt(fontSize * 0.55f),
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = Color.yellow },
            };
        }
    }
}
