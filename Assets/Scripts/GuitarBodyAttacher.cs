using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR
{
    /// <summary>
    /// Hangs the guitar on a person seen by the back camera, using ARKit 3D body tracking.
    /// </summary>
    [RequireComponent(typeof(ARHumanBodyManager))]
    public class GuitarBodyAttacher : GuitarWearer
    {
        // ARKit skeleton joint indices (see the AR Foundation samples' BoneController).
        const int k_Hips = 1;
        const int k_Spine3 = 14;
        const int k_Spine7 = 18;
        const int k_LeftArm = 20;
        const int k_RightArm = 64;

        ARHumanBodyManager m_BodyManager;

        public override bool IsSupported => m_BodyManager != null && m_BodyManager.subsystem != null;

        void Awake()
        {
            m_BodyManager = GetComponent<ARHumanBodyManager>();
        }

        void OnEnable()
        {
            m_BodyManager.enabled = true;
            m_BodyManager.pose3DRequested = true;
            m_BodyManager.pose3DScaleEstimationRequested = true;
        }

        protected override void OnDisable()
        {
            if (m_BodyManager != null)
                m_BodyManager.enabled = false;
            base.OnDisable();
        }

        protected override bool TryGetTorso(out Vector3 belly, out Vector3 right, out Vector3 up)
        {
            belly = right = up = default;

            var body = FindTrackedBody();
            if (body == null)
                return false;

            var joints = body.joints;
            if (!joints.IsCreated || joints.Length <= k_RightArm)
                return false;

            var t = body.transform;
            var hips = t.TransformPoint(joints[k_Hips].anchorPose.position);
            var chest = t.TransformPoint(joints[k_Spine7].anchorPose.position);
            var leftShoulder = t.TransformPoint(joints[k_LeftArm].anchorPose.position);
            var rightShoulder = t.TransformPoint(joints[k_RightArm].anchorPose.position);

            belly = t.TransformPoint(joints[k_Spine3].anchorPose.position);
            up = chest - hips;
            right = rightShoulder - leftShoulder;
            return true;
        }

        ARHumanBody FindTrackedBody()
        {
            foreach (var body in m_BodyManager.trackables)
            {
                if (body.trackingState == TrackingState.Tracking)
                    return body;
            }
            return null;
        }
    }
}
