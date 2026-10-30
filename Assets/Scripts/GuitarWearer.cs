using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Hangs a guitar on a person's torso the way a right-handed player holds it: body at the
    /// belly, neck pointing to the player's left and slightly up. Subclasses say where the torso
    /// is (from body tracking, a face, ...). The guitar pops in when a torso is found and shrinks
    /// away when it's lost.
    /// </summary>
    public abstract class GuitarWearer : MonoBehaviour
    {
        // Where the guitar's lower bout sits relative to the belly, in meters.
        const float k_RightOffset = 0.10f;
        const float k_ForwardOffset = 0.13f;
        const float k_BodyCenterZ = 0.10f;

        [Tooltip("Optional. A guitar model to wear. If empty, a ProceduralGuitar is built at runtime.")]
        [SerializeField] GameObject m_GuitarPrefab;

        [Tooltip("How far the neck is raised above horizontal, in degrees.")]
        [SerializeField] float m_NeckAngle = 25f;

        [Tooltip("How quickly the guitar catches up with the person. Higher is snappier, lower is smoother.")]
        [SerializeField] float m_FollowSharpness = 12f;

        [Tooltip("Seconds the guitar takes to pop in or shrink away.")]
        [SerializeField] float m_AppearDuration = 0.35f;

        [Tooltip("Seconds to keep the guitar after the person is lost, so short tracking drops don't make it blink.")]
        [SerializeField] float m_LostGracePeriod = 0.5f;

        GameObject m_Guitar;
        Vector3 m_BaseScale = Vector3.one;
        float m_Visibility;
        float m_LastSeenTime = float.NegativeInfinity;

        /// <summary>True while a person is being tracked.</summary>
        public bool IsTracking { get; private set; }

        /// <summary>False if the device or session can't do the tracking this wearer needs.</summary>
        public abstract bool IsSupported { get; }

        /// <summary>
        /// The person's belly position and their own right and up directions, in world space.
        /// The directions don't need to be normalized or exactly perpendicular.
        /// </summary>
        protected abstract bool TryGetTorso(out Vector3 belly, out Vector3 right, out Vector3 up);

        /// <summary>
        /// Wear a mirror image of the guitar (neck to the person's right, flipped model) to match
        /// a mirrored camera view, so on screen it still looks held right-handed.
        /// </summary>
        protected virtual bool Mirrored => false;

        /// <summary>
        /// Extra scale on top of the guitar's own size, e.g. to fit it on screen when the camera
        /// is close to the person.
        /// </summary>
        protected virtual float SizeScale => 1f;

        /// <summary>The guitar's own size before <see cref="SizeScale"/> (1 = full-size acoustic).</summary>
        protected float BaseSize => m_Guitar != null ? Mathf.Abs(m_BaseScale.z) : 1f;

        /// <summary>How far the neck is raised above horizontal, in degrees.</summary>
        protected float NeckAngle => m_NeckAngle;

        protected virtual void OnDisable()
        {
            IsTracking = false;
            m_Visibility = 0f;
            m_LastSeenTime = float.NegativeInfinity;
            if (m_Guitar != null)
                m_Guitar.SetActive(false);
        }

        protected virtual void Update()
        {
            IsTracking = TryGetGuitarPose(out var position, out var rotation);
            if (IsTracking)
            {
                m_LastSeenTime = Time.time;
                EnsureGuitar();

                var current = m_Guitar.transform;
                if (m_Visibility <= 0f)
                {
                    current.SetPositionAndRotation(position, rotation);
                }
                else
                {
                    float t = 1f - Mathf.Exp(-m_FollowSharpness * Time.deltaTime);
                    current.SetPositionAndRotation(
                        Vector3.Lerp(current.position, position, t),
                        Quaternion.Slerp(current.rotation, rotation, t));
                }
            }

            UpdateVisibility(Time.time - m_LastSeenTime <= m_LostGracePeriod);
        }

        bool TryGetGuitarPose(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;

            if (!TryGetTorso(out var belly, out var right, out var up))
                return false;
            if (up.sqrMagnitude < 1e-6f || right.sqrMagnitude < 1e-6f)
                return false;

            // The person's own frame: right, up and forward (out of their chest).
            up.Normalize();
            right = Vector3.ProjectOnPlane(right, up).normalized;
            var forward = Vector3.Cross(right, up);

            float angle = m_NeckAngle * Mathf.Deg2Rad;
            float side = Mirrored ? 1f : -1f;
            var neck = (right * (side * Mathf.Cos(angle)) + up * Mathf.Sin(angle)).normalized;

            // Guitar space: +Y is the face, +Z runs up the neck, the origin is the back of the lower bout.
            rotation = Quaternion.LookRotation(neck, forward);
            // Offsets are for a full-size guitar; shrink them with it.
            float size = BaseSize * SizeScale;
            position = belly + (right * k_RightOffset + forward * k_ForwardOffset - neck * k_BodyCenterZ) * size;
            return true;
        }

        void UpdateVisibility(bool show)
        {
            if (m_Guitar == null)
                return;

            float step = Time.deltaTime / Mathf.Max(0.01f, m_AppearDuration);
            m_Visibility = Mathf.Clamp01(m_Visibility + (show ? step : -step));

            // Overshoot slightly on the way in so it "pops", and shrink smoothly on the way out.
            float scale = show ? EaseOutBack(m_Visibility) : Mathf.SmoothStep(0f, 1f, m_Visibility);
            var baseScale = m_BaseScale * SizeScale;
            if (Mirrored)
                baseScale.x = -baseScale.x;
            m_Guitar.transform.localScale = baseScale * scale;

            bool active = m_Visibility > 0f;
            if (m_Guitar.activeSelf != active)
                m_Guitar.SetActive(active);
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
