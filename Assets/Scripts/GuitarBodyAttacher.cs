using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR
{
    /// <summary>
    /// Hangs the guitar on the torso of a person tracked by ARKit body tracking, the way a
    /// right-handed player holds it: body at the belly, neck pointing to the player's left and
    /// slightly up. The guitar pops in when a person is found and shrinks away when they're lost.
    /// </summary>
    [RequireComponent(typeof(ARHumanBodyManager))]
    public class GuitarBodyAttacher : MonoBehaviour
    {
        // ARKit skeleton joint indices (see the AR Foundation samples' BoneController).
        const int k_Hips = 1;
        const int k_Spine3 = 14;
        const int k_Spine7 = 18;
        const int k_LeftArm = 20;
        const int k_RightArm = 64;

        // Where the guitar's lower bout sits relative to the torso, in meters.
        const float k_RightOffset = 0.10f;
        const float k_ForwardOffset = 0.13f;
        const float k_BodyCenterZ = 0.10f;

        [Tooltip("Optional. A guitar model to wear. If empty, a ProceduralGuitar is built at runtime.")]
        [SerializeField] GameObject m_GuitarPrefab;

        [Tooltip("How far the neck is raised above horizontal, in degrees.")]
        [SerializeField] float m_NeckAngle = 25f;

        [Tooltip("How quickly the guitar catches up with the body. Higher is snappier, lower is smoother.")]
        [SerializeField] float m_FollowSharpness = 12f;

        [Tooltip("Seconds the guitar takes to pop in or shrink away.")]
        [SerializeField] float m_AppearDuration = 0.35f;

        [Tooltip("Seconds to keep the guitar after the body is lost, so short tracking drops don't make it blink.")]
        [SerializeField] float m_LostGracePeriod = 0.5f;

        ARHumanBodyManager m_BodyManager;
        GameObject m_Guitar;
        Vector3 m_BaseScale = Vector3.one;
        float m_Visibility;
        float m_LastSeenTime = float.NegativeInfinity;
        bool m_HasPose;

        /// <summary>True while a person's skeleton is being tracked.</summary>
        public bool IsTrackingBody { get; private set; }

        /// <summary>False if the device or session can't do body tracking.</summary>
        public bool IsSupported => m_BodyManager != null && m_BodyManager.subsystem != null;

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

        void OnDisable()
        {
            if (m_BodyManager != null)
                m_BodyManager.enabled = false;

            IsTrackingBody = false;
            m_HasPose = false;
            m_Visibility = 0f;
            if (m_Guitar != null)
                m_Guitar.SetActive(false);
        }

        void Update()
        {
            IsTrackingBody = TryGetTorsoPose(out var position, out var rotation);
            if (IsTrackingBody)
            {
                m_LastSeenTime = Time.time;
                EnsureGuitar();

                if (!m_HasPose || m_Visibility <= 0f)
                {
                    m_Guitar.transform.SetPositionAndRotation(position, rotation);
                    m_HasPose = true;
                }
                else
                {
                    float t = 1f - Mathf.Exp(-m_FollowSharpness * Time.deltaTime);
                    var current = m_Guitar.transform;
                    current.SetPositionAndRotation(
                        Vector3.Lerp(current.position, position, t),
                        Quaternion.Slerp(current.rotation, rotation, t));
                }
            }

            bool show = Time.time - m_LastSeenTime <= m_LostGracePeriod;
            UpdateVisibility(show);
        }

        void UpdateVisibility(bool show)
        {
            if (m_Guitar == null)
                return;

            float step = Time.deltaTime / Mathf.Max(0.01f, m_AppearDuration);
            m_Visibility = Mathf.Clamp01(m_Visibility + (show ? step : -step));

            // Overshoot slightly on the way in so it "pops", and shrink smoothly on the way out.
            float scale = show ? EaseOutBack(m_Visibility) : Mathf.SmoothStep(0f, 1f, m_Visibility);
            m_Guitar.transform.localScale = m_BaseScale * scale;

            bool active = m_Visibility > 0f;
            if (m_Guitar.activeSelf != active)
                m_Guitar.SetActive(active);
        }

        bool TryGetTorsoPose(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;

            var body = FindTrackedBody();
            if (body == null)
                return false;

            var joints = body.joints;
            if (!joints.IsCreated || joints.Length <= k_RightArm)
                return false;

            var t = body.transform;
            var hips = t.TransformPoint(joints[k_Hips].anchorPose.position);
            var belly = t.TransformPoint(joints[k_Spine3].anchorPose.position);
            var chest = t.TransformPoint(joints[k_Spine7].anchorPose.position);
            var leftShoulder = t.TransformPoint(joints[k_LeftArm].anchorPose.position);
            var rightShoulder = t.TransformPoint(joints[k_RightArm].anchorPose.position);

            var up = chest - hips;
            var right = rightShoulder - leftShoulder;
            if (up.sqrMagnitude < 1e-4f || right.sqrMagnitude < 1e-4f)
                return false;

            // The person's own frame: right, up and forward (out of their chest).
            up.Normalize();
            right = Vector3.ProjectOnPlane(right, up).normalized;
            var forward = Vector3.Cross(right, up);

            float angle = m_NeckAngle * Mathf.Deg2Rad;
            var neck = (-right * Mathf.Cos(angle) + up * Mathf.Sin(angle)).normalized;

            // Guitar space: +Y is the face, +Z runs up the neck, the origin is the back of the lower bout.
            rotation = Quaternion.LookRotation(neck, forward);
            position = belly + right * k_RightOffset + forward * k_ForwardOffset - neck * k_BodyCenterZ;
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

        void EnsureGuitar()
        {
            if (m_Guitar != null)
                return;

            if (m_GuitarPrefab != null)
            {
                m_Guitar = Instantiate(m_GuitarPrefab);
            }
            else
            {
                m_Guitar = new GameObject("Worn Guitar");
                m_Guitar.AddComponent<ProceduralGuitar>();
            }

            m_BaseScale = m_Guitar.transform.localScale;
            m_Guitar.transform.localScale = Vector3.zero;
            m_Visibility = 0f;
        }

        static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float p = x - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }
    }
}
