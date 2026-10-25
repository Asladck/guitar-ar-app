using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR
{
    /// <summary>
    /// Play the guitar with your real right hand in front of the camera: with the index finger raised,
    /// move it across the virtual strings. A quick sweep over the strings is a strum (down or up,
    /// by direction), a slow pass over a string plucks it. With the index finger down, hand
    /// movement is ignored. Hands come from Apple Vision on the AR camera image (iOS only).
    /// </summary>
    public class GuitarHandPlayer : MonoBehaviour
    {
        // In the order the native plugin reports them.
        enum Joint { Wrist, ThumbTip, IndexMCP, IndexPIP, IndexTip, MiddlePIP, MiddleTip, RingPIP, RingTip, LittlePIP, LittleTip }

        const int k_MaxHands = 2;
        const int k_JointCount = 11;
        const int k_RightHand = 1;              // Vision's chirality for a right hand.
        const float k_MinConfidence = 0.2f;
        const float k_RaiseRatio = 1.2f;        // Wrist-to-tip vs wrist-to-middle-knuckle above which the index counts as raised...
        const float k_LowerRatio = 1.05f;       // ...and below which it counts as lowered again (in between, it stays as it was).
        const float k_StrumSpeed = 0.8f;        // Screen heights per second; faster passes are strums.
        const float k_SameStrumCooldown = 0.15f;  // Seconds before another strum in the same direction...
        const float k_OtherStrumCooldown = 0.06f; // ...or in the other direction (down-up strumming is quick).
        const float k_MaxGap = 0.4f;            // Seconds; a fingertip not seen for longer starts a new movement.
        const float k_BridgeOvershoot = 0.3f;   // Strings count as this much longer past the bridge, where people strum.
        const float k_PluckDeadband = 0.004f;   // Fraction of screen height; smaller moves are tracking jitter.

        static string s_Diagnostic = "рука: -";

        readonly float[] m_Points = new float[k_MaxHands * k_JointCount * 3];
        readonly int[] m_Chirality = new int[k_MaxHands];
        double m_LastTimestamp;
        ARSession m_Session;
        ARCameraManager m_CameraManager;
        Texture2D m_Dot;

        // The right hand's index fingertip, in screen pixels.
        bool m_HasTip;
        bool m_Raised;
        Vector2 m_Tip;
        double m_TipTime;
        float m_LastStrumTime = float.NegativeInfinity;
        bool m_LastStrumDown;

        /// <summary>A line for the on-screen diagnostics.</summary>
        public static string DiagnosticText => s_Diagnostic;

        void Update()
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (!AnyPlayable())
            {
                m_HasTip = false;
                return;
            }

            if (m_Session == null)
                m_Session = Object.FindAnyObjectByType<ARSession>();
            var subsystem = m_Session != null ? m_Session.subsystem : null;
            if (subsystem == null || subsystem.nativePtr == System.IntPtr.Zero)
                return;

            GuitarHands_Process(subsystem.nativePtr, Screen.width, Screen.height, InterfaceOrientation());

            int count = GuitarHands_Get(m_Points, m_Chirality, k_MaxHands, out double timestamp);
            if (timestamp == m_LastTimestamp)
                return;
            m_LastTimestamp = timestamp;
            OnHands(count, timestamp);
#endif
        }

        void OnHands(int count, double timestamp)
        {
            // Vision reads the front camera's image the other way round, so its right is your left.
            if (m_CameraManager == null && Camera.main != null)
                m_CameraManager = Camera.main.GetComponent<ARCameraManager>();
            bool frontCamera = m_CameraManager != null && m_CameraManager.currentFacingDirection == CameraFacingDirection.User;
            int rightHand = frontCamera ? -k_RightHand : k_RightHand;

            int right = -1, left = 0;
            for (int h = 0; h < count; h++)
            {
                if (m_Chirality[h] == rightHand && right < 0)
                    right = h;
                else if (m_Chirality[h] == -rightHand)
                    left++;
            }

            if (right < 0 || !TryRead(right, out var tip, out float ratio, out bool ratioKnown))
            {
                // A blurred or briefly missed hand keeps its last fingertip for a moment.
                if (m_HasTip && timestamp - m_TipTime > k_MaxGap)
                    m_HasTip = false;
                s_Diagnostic = $"рук: {count} (левых {left}), правая: не видно";
                return;
            }

            // Hysteresis, so the finger doesn't flicker between raised and lowered mid-strum.
            bool wasRaised = m_Raised;
            if (ratioKnown)
            {
                if (ratio > k_RaiseRatio)
                    m_Raised = true;
                else if (ratio < k_LowerRatio)
                    m_Raised = false;
            }

            if (m_HasTip && (m_Raised || wasRaised) && timestamp - m_TipTime <= k_MaxGap)
                Move(m_Tip, tip, (float)(timestamp - m_TipTime));

            m_HasTip = true;
            m_Tip = tip;
            m_TipTime = timestamp;
            s_Diagnostic = $"рук: {count} (левых {left}), правая: палец {(m_Raised ? "поднят" : "опущен")} ({ratio:0.00}), кончик {tip:0}";
        }

        void Move(Vector2 from, Vector2 to, float seconds)
        {
            var cam = Camera.main;
            float distance = (to - from).magnitude / Screen.height;
            if (cam == null || distance <= 0f)
                return;

            float speed = distance / Mathf.Max(seconds, 1e-3f);
            float volume = Mathf.Clamp(0.5f + 0.2f * speed, 0.5f, 1f);

            foreach (var guitar in GuitarStrings.Active)
            {
                if (!guitar.IsPlayable)
                    continue;

                int crossed = 0;
                int firstString = -1;
                float firstAlong = float.MaxValue;
                Vector2 low = default, high = default;
                for (int i = 0; i < GuitarStrings.StringCount; i++)
                {
                    if (!guitar.TryGetScreenSegment(i, cam, out var bridge, out var nut))
                        continue;
                    var middle = Vector2.Lerp(bridge, nut, 0.2f);
                    if (i == 0)
                        low = middle;
                    high = middle;

                    var pastBridge = bridge + (bridge - nut) * k_BridgeOvershoot;
                    if (GuitarStrummer.Intersect(from, to, pastBridge, nut, out float along))
                    {
                        crossed++;
                        if (along < firstAlong)
                        {
                            firstAlong = along;
                            firstString = i;
                        }
                    }
                }
                if (crossed == 0)
                    continue;

                if (speed >= k_StrumSpeed || crossed >= 3)
                {
                    // Moving from the low E side towards the high E is a down strum.
                    bool down = Vector2.Dot(to - from, high - low) > 0f;
                    float cooldown = down == m_LastStrumDown ? k_SameStrumCooldown : k_OtherStrumCooldown;
                    if (Time.time - m_LastStrumTime < cooldown)
                        return;
                    m_LastStrumTime = Time.time;
                    m_LastStrumDown = down;
                    GuitarStrummer.Strum(guitar, down, volume);
                }
                else if (distance >= k_PluckDeadband)
                {
                    GuitarStrummer.PluckString(guitar, firstString, volume);
                }
                return;
            }
        }

        bool TryRead(int hand, out Vector2 indexTip, out float ratio, out bool ratioKnown)
        {
            ratio = 0f;
            ratioKnown = false;
            if (!TryGetJoint(hand, Joint.IndexTip, out indexTip))
                return false;

            if (TryGetJoint(hand, Joint.Wrist, out var wrist) && TryGetJoint(hand, Joint.IndexPIP, out var knuckle))
            {
                float toKnuckle = (knuckle - wrist).magnitude;
                if (toKnuckle > 1f)
                {
                    ratio = (indexTip - wrist).magnitude / toKnuckle;
                    ratioKnown = true;
                }
            }
            return true;
        }

        /// <summary>A joint in screen pixels (origin bottom left), if it was seen confidently.</summary>
        bool TryGetJoint(int hand, Joint joint, out Vector2 position)
        {
            int i = (hand * k_JointCount + (int)joint) * 3;
            position = new Vector2(m_Points[i] * Screen.width, (1f - m_Points[i + 1]) * Screen.height);
            return m_Points[i + 2] >= k_MinConfidence;
        }

        static bool AnyPlayable()
        {
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    return true;
            }
            return false;
        }

        // Draws a dot on each index fingertip: green when it can play, red when the finger is down.
        void OnGUI()
        {
            if (m_Dot == null)
            {
                m_Dot = new Texture2D(1, 1);
                m_Dot.SetPixel(0, 0, Color.white);
                m_Dot.Apply();
            }

            GUI.depth = -10;
            float size = Screen.width * 0.04f;
            var color = GUI.color;
            if (m_HasTip)
            {
                GUI.color = m_Raised ? Color.green : Color.red;
                GUI.DrawTexture(new Rect(m_Tip.x - size * 0.5f, Screen.height - m_Tip.y - size * 0.5f, size, size), m_Dot);
            }
            GUI.color = color;
        }

        static int InterfaceOrientation()
        {
            // UIInterfaceOrientation values; Unity's landscape names are the other way round.
            switch (Screen.orientation)
            {
                case ScreenOrientation.PortraitUpsideDown: return 2;
                case ScreenOrientation.LandscapeLeft: return 4;
                case ScreenOrientation.LandscapeRight: return 3;
                default: return 1;
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void GuitarHands_Process(System.IntPtr nativeSession, float viewWidth, float viewHeight, int interfaceOrientation);

        [DllImport("__Internal")]
        static extern int GuitarHands_Get([Out] float[] points, [Out] int[] chirality, int maxHands, out double timestamp);
#endif
    }
}
