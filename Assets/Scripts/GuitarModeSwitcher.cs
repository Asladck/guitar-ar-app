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
    /// <see cref="GuitarHud"/> draws its buttons and hints.
    /// </summary>
    public class GuitarModeSwitcher : MonoBehaviour
    {
        [SerializeField] GuitarMode m_StartMode = GuitarMode.Body;

        ARPlaneManager m_PlaneManager;
        GuitarPlacer m_Placer;
        GuitarBodyAttacher m_BodyAttacher;
        GuitarSelfieAttacher m_SelfieAttacher;
        GuitarMode m_Mode;

        public GuitarMode Mode => m_Mode;

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

        /// <summary>What the front camera session is doing while it hasn't come up, for the diagnostics.</summary>
        public string SelfieDiagnostic => m_Mode == GuitarMode.Selfie && m_SelfieAttacher != null ? m_SelfieAttacher.DiagnosticText() : null;

        /// <summary>What to do next in this mode, or null when the guitar is on.</summary>
        public string Hint()
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
    }
}
