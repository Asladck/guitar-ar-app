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
        ARSession m_Session;
        TrackingMode m_PreviousTrackingMode = TrackingMode.PositionAndRotation;

        public override bool IsSupported => m_FaceManager != null && m_FaceManager.subsystem != null;

        // The front camera image is shown mirrored like a selfie, so the guitar has to be a mirror
        // image too. A plain rotation can't do that: it either holds it left-handed or shows its back.
        protected override bool Mirrored => true;

        void Awake()
        {
            m_FaceManager = GetComponent<ARFaceManager>();
            var cam = Camera.main;
            if (cam != null)
                m_CameraManager = cam.GetComponent<ARCameraManager>();
            m_Session = Object.FindAnyObjectByType<ARSession>();
        }

        void OnEnable()
        {
            m_FaceManager.enabled = true;
            if (m_CameraManager != null)
                m_CameraManager.requestedFacingDirection = CameraFacingDirection.User;

            // The session asks for full position tracking by default. Only the back-camera
            // configuration (world tracking with face tracking on the side) offers that, so
            // ARKit would keep the back camera. Dropping the request lets it pick the front one.
            if (m_Session != null)
            {
                m_PreviousTrackingMode = m_Session.requestedTrackingMode;
                m_Session.requestedTrackingMode = TrackingMode.DontCare;
                if (m_Session.subsystem != null && !(m_Session.subsystem.configurationChooser is FrontCameraConfigurationChooser))
                    m_Session.subsystem.configurationChooser = new FrontCameraConfigurationChooser();
            }
        }

        /// <summary>
        /// What the session is actually running, for an on-screen hint while the front camera
        /// hasn't come up. Null once it's on the front camera.
        /// </summary>
        public string DiagnosticText()
        {
            if (m_CameraManager == null || m_Session == null || m_Session.subsystem == null)
                return null;
            if (m_CameraManager.currentFacingDirection == CameraFacingDirection.User)
                return null;

            var subsystem = m_Session.subsystem;
            var config = subsystem.currentConfiguration;
            var text = $"camera: {m_CameraManager.currentFacingDirection}\nrequested: {subsystem.requestedFeatures.ToStringList()}\n" +
                       $"running: {(config.HasValue ? config.Value.descriptor.capabilities.ToStringList() : "none")}";
            using (var descriptors = subsystem.GetConfigurationDescriptors(Unity.Collections.Allocator.Temp))
            {
                if (descriptors.IsCreated)
                {
                    foreach (var d in descriptors)
                        text += $"\n[{d.rank}] {d.capabilities.ToStringList()}";
                }
            }
            return text;
        }

        protected override void OnDisable()
        {
            if (m_FaceManager != null)
                m_FaceManager.enabled = false;
            if (m_CameraManager != null)
                m_CameraManager.requestedFacingDirection = CameraFacingDirection.World;
            if (m_Session != null)
                m_Session.requestedTrackingMode = m_PreviousTrackingMode;
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
