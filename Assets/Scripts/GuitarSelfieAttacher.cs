using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR
{
    /// <summary>
    /// Hangs the guitar on the person looking into the front camera. ARKit can't track bodies
    /// with the front camera, so the torso is estimated from the face: the belly is assumed to be
    /// straight down from the head, and the person is assumed to face the phone.
    /// </summary>
    [RequireComponent(typeof(ARFaceManager))]
    public class GuitarSelfieAttacher : GuitarWearer
    {
        [Tooltip("Distance from the middle of the face down to the belly, in meters.")]
        [SerializeField] float m_FaceToBelly = 0.50f;

        ARFaceManager m_FaceManager;
        ARCameraManager m_CameraManager;

        public override bool IsSupported => m_FaceManager != null && m_FaceManager.subsystem != null;

        void Awake()
        {
            m_FaceManager = GetComponent<ARFaceManager>();
            var cam = Camera.main;
            if (cam != null)
                m_CameraManager = cam.GetComponent<ARCameraManager>();
        }

        void OnEnable()
        {
            m_FaceManager.enabled = true;
            if (m_CameraManager != null)
                m_CameraManager.requestedFacingDirection = CameraFacingDirection.User;
        }

        protected override void OnDisable()
        {
            if (m_FaceManager != null)
                m_FaceManager.enabled = false;
            if (m_CameraManager != null)
                m_CameraManager.requestedFacingDirection = CameraFacingDirection.World;
            base.OnDisable();
        }

        protected override bool TryGetTorso(out Vector3 belly, out Vector3 right, out Vector3 up)
        {
            belly = right = up = default;

            var face = FindTrackedFace();
            var cam = Camera.main;
            if (face == null || cam == null)
                return false;

            // Use gravity for "up" so nodding or tilting the head doesn't swing the guitar around.
            up = Vector3.up;
            var forward = Vector3.ProjectOnPlane(cam.transform.position - face.transform.position, up);
            if (forward.sqrMagnitude < 1e-6f)
                return false;

            right = Vector3.Cross(up, forward);
            belly = face.transform.position - up * m_FaceToBelly;
            return true;
        }

        ARFace FindTrackedFace()
        {
            foreach (var face in m_FaceManager.trackables)
            {
                if (face.trackingState == TrackingState.Tracking)
                    return face;
            }
            return null;
        }
    }
}
