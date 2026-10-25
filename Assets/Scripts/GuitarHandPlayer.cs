using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace GuitarAR
{
    /// <summary>
    /// Play the guitar with your real hand in front of the camera: with the index finger raised,
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
        const float k_MinConfidence = 0.3f;
        const float k_IndexRaisedRatio = 1.2f;  // Wrist-to-tip vs wrist-to-middle-knuckle when the finger is straight.
        const float k_StrumSpeed = 0.8f;        // Screen heights per second; faster passes are strums.
        const float k_StrumCooldown = 0.18f;    // Seconds before the same hand can strum again.
        const float k_MaxGap = 0.25f;           // Seconds; longer gaps between detections don't count as a movement.

        struct HandState
        {
            public bool Valid;
            public bool IndexRaised;
            public Vector2 Tip;
            public double Time;
            public float LastStrum;
        }

        static string s_Diagnostic = "руки: -";

        readonly float[] m_Points = new float[k_MaxHands * k_JointCount * 3];
        readonly HandState[] m_Hands = new HandState[k_MaxHands];
        readonly HandState[] m_Previous = new HandState[k_MaxHands];
        double m_LastTimestamp;
        ARSession m_Session;
        Texture2D m_Dot;

        /// <summary>A line for the on-screen diagnostics.</summary>
        public static string DiagnosticText => s_Diagnostic;

        void Update()
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (!AnyPlayable())
            {
                System.Array.Clear(m_Hands, 0, k_MaxHands);
                return;
            }

            if (m_Session == null)
                m_Session = Object.FindAnyObjectByType<ARSession>();
            var subsystem = m_Session != null ? m_Session.subsystem : null;
            if (subsystem == null || subsystem.nativePtr == System.IntPtr.Zero)
                return;

            GuitarHands_Process(subsystem.nativePtr, Screen.width, Screen.height, InterfaceOrientation());

            int count = GuitarHands_Get(m_Points, k_MaxHands, out double timestamp);
            if (timestamp == m_LastTimestamp)
                return;
            m_LastTimestamp = timestamp;
            OnHands(count, timestamp);
#endif
        }

        void OnHands(int count, double timestamp)
        {
            System.Array.Copy(m_Hands, m_Previous, k_MaxHands);
            int raised = 0;
            for (int h = 0; h < k_MaxHands; h++)
            {
                var hand = new HandState();
                if (h < count && TryRead(h, out var tip, out bool indexRaised))
                {
                    hand.Valid = true;
                    hand.Tip = tip;
                    hand.IndexRaised = indexRaised;
                    hand.Time = timestamp;
                    if (indexRaised)
                        raised++;
                }
                m_Hands[h] = hand;
            }

            // Vision doesn't keep hand order between frames; pair each hand with the nearest one before.
            if (m_Hands[0].Valid && m_Hands[1].Valid && m_Previous[0].Valid && m_Previous[1].Valid)
            {
                float kept = (m_Hands[0].Tip - m_Previous[0].Tip).sqrMagnitude + (m_Hands[1].Tip - m_Previous[1].Tip).sqrMagnitude;
                float swapped = (m_Hands[0].Tip - m_Previous[1].Tip).sqrMagnitude + (m_Hands[1].Tip - m_Previous[0].Tip).sqrMagnitude;
                if (swapped < kept)
                    (m_Hands[0], m_Hands[1]) = (m_Hands[1], m_Hands[0]);
            }

            for (int h = 0; h < k_MaxHands; h++)
            {
                m_Hands[h].LastStrum = m_Previous[h].LastStrum;
                var before = m_Previous[h];
                var now = m_Hands[h];
                if (now.Valid && before.Valid && now.IndexRaised && before.IndexRaised && now.Time - before.Time <= k_MaxGap)
                    Move(h, before.Tip, now.Tip, (float)(now.Time - before.Time));
            }

            s_Diagnostic = $"руки: {count}, палец поднят: {raised}" + (m_Hands[0].Valid ? $", кончик пальца {m_Hands[0].Tip:0}" : "");
        }

        void Move(int hand, Vector2 from, Vector2 to, float seconds)
        {
            var cam = Camera.main;
            if (cam == null || from == to)
                return;

            float speed = (to - from).magnitude / Mathf.Max(seconds, 1e-3f) / Screen.height;
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

                    if (GuitarStrummer.Intersect(from, to, bridge, nut, out float along))
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
                    if (Time.time - m_Hands[hand].LastStrum < k_StrumCooldown)
                        return;
                    m_Hands[hand].LastStrum = Time.time;
                    // Moving from the low E side towards the high E is a down strum.
                    bool down = Vector2.Dot(to - from, high - low) > 0f;
                    GuitarStrummer.Strum(guitar, down, volume);
                }
                else
                {
                    GuitarStrummer.PluckString(guitar, firstString, volume);
                }
                return;
            }
        }

        bool TryRead(int hand, out Vector2 indexTip, out bool indexRaised)
        {
            indexTip = default;
            indexRaised = false;
            if (!TryGetJoint(hand, Joint.IndexTip, out indexTip))
                return false;

            if (TryGetJoint(hand, Joint.Wrist, out var wrist) && TryGetJoint(hand, Joint.IndexPIP, out var knuckle))
            {
                float toKnuckle = (knuckle - wrist).magnitude;
                indexRaised = toKnuckle > 1f && (indexTip - wrist).magnitude > toKnuckle * k_IndexRaisedRatio;
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
            foreach (var hand in m_Hands)
            {
                if (!hand.Valid)
                    continue;
                GUI.color = hand.IndexRaised ? Color.green : Color.red;
                GUI.DrawTexture(new Rect(hand.Tip.x - size * 0.5f, Screen.height - hand.Tip.y - size * 0.5f, size, size), m_Dot);
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
        static extern int GuitarHands_Get([Out] float[] points, int maxHands, out double timestamp);
#endif
    }
}
