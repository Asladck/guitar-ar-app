using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace GuitarAR
{
    /// <summary>
    /// Tap on a detected plane to place the guitar there. Tapping again moves it.
    /// The guitar is turned so its neck points away from the viewer.
    /// </summary>
    [RequireComponent(typeof(ARRaycastManager))]
    public class GuitarPlacer : MonoBehaviour
    {
        [Tooltip("Optional. A guitar model to place. If empty, a ProceduralGuitar is built at runtime.")]
        [SerializeField] GameObject m_GuitarPrefab;

        static readonly List<ARRaycastHit> s_Hits = new List<ARRaycastHit>();

        ARRaycastManager m_RaycastManager;
        GameObject m_Guitar;

        public GameObject PlacedGuitar => m_Guitar;

        void Awake()
        {
            m_RaycastManager = GetComponent<ARRaycastManager>();
        }

        void Update()
        {
            if (!TryGetTapPosition(out var screenPosition) || GuitarModeSwitcher.IsOverButton(screenPosition))
                return;

            // Taps on the strings or chord buttons play the guitar instead of moving it.
            if (GuitarStrummer.IsOverButton(screenPosition) || GuitarHandPlayer.IsOverButton(screenPosition) || (m_Guitar != null && GuitarStrummer.IsOnStrings(screenPosition)))
                return;

            if (!m_RaycastManager.Raycast(screenPosition, s_Hits, TrackableType.PlaneWithinPolygon))
                return;

            var pose = s_Hits[0].pose;
            var rotation = FacingRotation(pose.position);

            if (m_Guitar == null)
                m_Guitar = CreateGuitar(pose.position, rotation);
            else
                m_Guitar.transform.SetPositionAndRotation(pose.position, rotation);
        }

        GameObject CreateGuitar(Vector3 position, Quaternion rotation)
        {
            if (m_GuitarPrefab != null)
                return Instantiate(m_GuitarPrefab, position, rotation);

            var go = new GameObject("Guitar");
            go.transform.SetPositionAndRotation(position, rotation);
            go.AddComponent<ProceduralGuitar>();
            return go;
        }

        static Quaternion FacingRotation(Vector3 position)
        {
            var cam = Camera.main;
            if (cam == null)
                return Quaternion.identity;

            var away = position - cam.transform.position;
            away.y = 0f;
            return away.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(away.normalized, Vector3.up) : Quaternion.identity;
        }

        static bool TryGetTapPosition(out Vector2 position)
        {
#if ENABLE_INPUT_SYSTEM
            var touchscreen = Touchscreen.current;
            if (touchscreen != null && touchscreen.primaryTouch.press.wasPressedThisFrame)
            {
                position = touchscreen.primaryTouch.position.ReadValue();
                return true;
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                position = mouse.position.ReadValue();
                return true;
            }
#else
            if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
            {
                position = Input.GetTouch(0).position;
                return true;
            }

            if (Input.GetMouseButtonDown(0))
            {
                position = Input.mousePosition;
                return true;
            }
#endif
            position = default;
            return false;
        }
    }
}
