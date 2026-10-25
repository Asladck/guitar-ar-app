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

        const float k_AssistStrumSpeed = 0.6f;  // With strum help on, passes at least this fast are strums, slower ones plucks.
        const float k_AssistMargin = 1.5f;      // With strum help on, the strum zone reaches this many string gaps past the outer strings.
        const string k_AssistKey = "GuitarAR.StrumAssist";
        const string k_PatternKey = "GuitarAR.StrumPattern";
        const float k_PatternRestart = 1.5f;    // Seconds without a strike after which a strumming pattern starts over.

        enum Stroke { Bass, Down, Up, Mute }

        struct Pattern
        {
            public string Name;
            public Stroke[] Strokes;

            public Pattern(string name, params Stroke[] strokes)
            {
                Name = name;
                Strokes = strokes;
            }
        }

        // Each strike plays the next stroke of the chosen pattern. Index 0 plays what the hand does.
        static readonly Pattern[] k_Patterns =
        {
            new Pattern("Свободный"),
            // Вниз, вниз с глушением, вверх, вверх, вниз с глушением, вверх.
            new Pattern("Шестёрка", Stroke.Down, Stroke.Mute, Stroke.Up, Stroke.Up, Stroke.Mute, Stroke.Up),
            // Вниз, вниз с глушением, вверх, вниз, вниз с глушением, вверх, вниз, вверх.
            new Pattern("Восьмёрка", Stroke.Down, Stroke.Mute, Stroke.Up, Stroke.Down, Stroke.Mute, Stroke.Up, Stroke.Down, Stroke.Up),
        };

        // Rhythm help (part of strum help): learns the player's tempo from their strikes and, when a
        // strike comes a little early, holds its sound back to the beat so the strumming sounds even.
        const int k_TempoSamples = 4;
        const float k_MinBeat = 0.12f;         // Seconds; shortest and longest gap between strikes
        const float k_MaxBeat = 1.0f;          // that count towards the tempo.
        const float k_MaxRhythmDelay = 0.07f;  // Seconds; the most a strike is held back.

        readonly float[] m_Beats = new float[k_TempoSamples];
        int m_BeatCount;
        float m_Beat;                           // The player's tempo: seconds between strikes, 0 while unknown.
        double m_LastSounded = double.NegativeInfinity;

        static int s_Pattern;
        static Rect s_PatternRect;
        int m_PatternStep;
        string[] m_PatternLabels;
        GUIStyle m_PatternStyle;

        static string s_Diagnostic = "рука: -";
        static Rect s_AssistRect;
        static bool s_Assist = true;

        readonly float[] m_Points = new float[k_MaxHands * k_JointCount * 3];
        readonly int[] m_Chirality = new int[k_MaxHands];
        double m_LastTimestamp;
        ARSession m_Session;
        ARCameraManager m_CameraManager;
        Texture2D m_Dot;
        GUIStyle m_ButtonStyle;

        // The right hand's index fingertip, in screen pixels.
        bool m_HasTip;
        bool m_Raised;
        Vector2 m_Tip;
        double m_TipTime;
        float m_LastStrumTime = float.NegativeInfinity;
        bool m_LastStrumDown;

        /// <summary>A line for the on-screen diagnostics.</summary>
        public static string DiagnosticText => s_Diagnostic;

        /// <summary>True if a screen position (pixels, origin bottom-left) is on the strum help button.</summary>
        public static bool IsOverButton(Vector2 screenPosition)
        {
            var guiPoint = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            return s_AssistRect.Contains(guiPoint) || s_PatternRect.Contains(guiPoint);
        }

        void Awake()
        {
            try
            {
                s_Assist = PlayerPrefs.GetInt(k_AssistKey, 1) != 0;
                s_Pattern = Mathf.Clamp(PlayerPrefs.GetInt(k_PatternKey, 0), 0, k_Patterns.Length - 1);
            }
            catch (System.Exception)
            {
                s_Assist = true;
            }
        }

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
                if (s_Assist ? AssistedMove(guitar, cam, from, to, speed, distance, volume) : FreeMove(guitar, cam, from, to, speed, distance, volume))
                    return;
            }
        }

        /// <summary>
        /// Strum help: a quick pass anywhere through a wide zone around the strings strums the whole
        /// chord, so grazing a string on the way doesn't make stray notes; only slow passes pluck.
        /// </summary>
        bool AssistedMove(GuitarStrings guitar, Camera cam, Vector2 from, Vector2 to, float speed, float distance, float volume)
        {
            if (!TryGetStrings(guitar, cam, s_Bridges, s_Nuts))
                return false;

            var low = Vector2.Lerp(s_Bridges[0], s_Nuts[0], 0.2f);
            var high = Vector2.Lerp(s_Bridges[GuitarStrings.StringCount - 1], s_Nuts[GuitarStrings.StringCount - 1], 0.2f);
            var gap = (high - low) / (GuitarStrings.StringCount - 1);

            if (speed >= k_AssistStrumSpeed)
            {
                // The strum zone: the strings plus imaginary ones past the outer strings.
                int lines = GuitarStrings.StringCount + 2;
                for (int line = 0; line < lines; line++)
                {
                    Vector2 bridge, nut;
                    if (line == 0)
                    {
                        bridge = s_Bridges[0] - gap * k_AssistMargin;
                        nut = s_Nuts[0] - gap * k_AssistMargin;
                    }
                    else if (line == lines - 1)
                    {
                        bridge = s_Bridges[GuitarStrings.StringCount - 1] + gap * k_AssistMargin;
                        nut = s_Nuts[GuitarStrings.StringCount - 1] + gap * k_AssistMargin;
                    }
                    else
                    {
                        bridge = s_Bridges[line - 1];
                        nut = s_Nuts[line - 1];
                    }

                    if (GuitarStrummer.Intersect(from, to, bridge + (bridge - nut) * k_BridgeOvershoot, nut, out _))
                    {
                        StrumOnce(guitar, AcrossStrings(from, to, gap), volume);
                        return true;
                    }
                }
                return false;
            }

            if (distance < k_PluckDeadband)
                return false;
            int nearest = FirstCrossed(from, to);
            if (nearest < 0)
                return false;
            GuitarStrummer.PluckString(guitar, nearest, volume);
            return true;
        }

        /// <summary>Without strum help: strings crossed fast (or several at once) strum, one crossed slowly plucks.</summary>
        bool FreeMove(GuitarStrings guitar, Camera cam, Vector2 from, Vector2 to, float speed, float distance, float volume)
        {
            if (!TryGetStrings(guitar, cam, s_Bridges, s_Nuts))
                return false;

            int crossed = 0;
            for (int i = 0; i < GuitarStrings.StringCount; i++)
            {
                if (GuitarStrummer.Intersect(from, to, s_Bridges[i] + (s_Bridges[i] - s_Nuts[i]) * k_BridgeOvershoot, s_Nuts[i], out _))
                    crossed++;
            }
            if (crossed == 0)
                return false;

            if (speed >= k_StrumSpeed || crossed >= 3)
            {
                var low = Vector2.Lerp(s_Bridges[0], s_Nuts[0], 0.2f);
                var high = Vector2.Lerp(s_Bridges[GuitarStrings.StringCount - 1], s_Nuts[GuitarStrings.StringCount - 1], 0.2f);
                StrumOnce(guitar, AcrossStrings(from, to, high - low), volume);
            }
            else if (distance >= k_PluckDeadband)
            {
                GuitarStrummer.PluckString(guitar, FirstCrossed(from, to), volume);
            }
            return true;
        }

        static readonly Vector2[] s_Bridges = new Vector2[GuitarStrings.StringCount];
        static readonly Vector2[] s_Nuts = new Vector2[GuitarStrings.StringCount];

        static bool TryGetStrings(GuitarStrings guitar, Camera cam, Vector2[] bridges, Vector2[] nuts)
        {
            for (int i = 0; i < GuitarStrings.StringCount; i++)
            {
                if (!guitar.TryGetScreenSegment(i, cam, out bridges[i], out nuts[i]))
                    return false;
            }
            return true;
        }

        /// <summary>The first string (in s_Bridges/s_Nuts) the movement crosses, or -1.</summary>
        static int FirstCrossed(Vector2 from, Vector2 to)
        {
            int first = -1;
            float firstAlong = float.MaxValue;
            for (int i = 0; i < GuitarStrings.StringCount; i++)
            {
                var bridge = s_Bridges[i] + (s_Bridges[i] - s_Nuts[i]) * k_BridgeOvershoot;
                if (GuitarStrummer.Intersect(from, to, bridge, s_Nuts[i], out float along) && along < firstAlong)
                {
                    firstAlong = along;
                    first = i;
                }
            }
            return first;
        }

        /// <summary>
        /// True for a down strum: movement from the low E side towards the high E, counting only the
        /// part of the movement across the strings, not along them.
        /// </summary>
        static bool AcrossStrings(Vector2 from, Vector2 to, Vector2 lowToHigh)
        {
            var along = s_Nuts[0] - s_Bridges[0];
            var across = along.sqrMagnitude > 1e-6f ? lowToHigh - along * (Vector2.Dot(lowToHigh, along) / along.sqrMagnitude) : lowToHigh;
            return Vector2.Dot(to - from, across) > 0f;
        }

        void StrumOnce(GuitarStrings guitar, bool down, float volume)
        {
            float cooldown = down == m_LastStrumDown ? k_SameStrumCooldown : k_OtherStrumCooldown;
            if (Time.time - m_LastStrumTime < cooldown)
                return;
            float sinceLast = Time.time - m_LastStrumTime;
            m_LastStrumTime = Time.time;
            m_LastStrumDown = down;
            float delay = s_Assist ? RhythmDelay(sinceLast) : 0f;

            var strokes = k_Patterns[s_Pattern].Strokes;
            if (strokes.Length == 0)
            {
                GuitarStrummer.Strum(guitar, down, volume, delay);
                return;
            }

            // The player's strike sets the timing; the pattern decides how it sounds.
            if (sinceLast > k_PatternRestart || m_PatternStep >= strokes.Length)
                m_PatternStep = 0;
            switch (strokes[m_PatternStep])
            {
                case Stroke.Bass: GuitarStrummer.Bass(guitar, volume, delay); break;
                case Stroke.Down: GuitarStrummer.Strum(guitar, true, volume, delay); break;
                case Stroke.Up: GuitarStrummer.Strum(guitar, false, volume, delay); break;
                case Stroke.Mute: GuitarStrummer.MutedStrum(guitar, volume, delay); break;
            }
            m_PatternStep = (m_PatternStep + 1) % strokes.Length;
        }

        /// <summary>
        /// How long to hold back a strike so it lands on the player's own beat, and learns the beat.
        /// Only early strikes are nudged (a sound can't be played before the strike), and only a little.
        /// </summary>
        float RhythmDelay(float sinceLastStrike)
        {
            double now = Time.timeAsDouble;
            if (sinceLastStrike > k_PatternRestart)
            {
                m_BeatCount = 0;
                m_Beat = 0f;
            }
            else if (sinceLastStrike >= k_MinBeat && sinceLastStrike <= k_MaxBeat)
            {
                m_Beats[m_BeatCount % k_TempoSamples] = sinceLastStrike;
                m_BeatCount++;
                m_Beat = Median();
            }

            float delay = 0f;
            if (m_Beat > 0f && m_BeatCount >= 2)
            {
                float early = (float)(m_LastSounded + m_Beat - now);
                if (early > 0f && early <= Mathf.Min(k_MaxRhythmDelay, m_Beat * 0.25f))
                    delay = early;
            }
            m_LastSounded = now + delay;
            return delay;
        }

        float Median()
        {
            int n = Mathf.Min(m_BeatCount, k_TempoSamples);
            var sorted = new float[n];
            System.Array.Copy(m_Beats, sorted, n);
            System.Array.Sort(sorted);
            return n % 2 == 1 ? sorted[n / 2] : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        static string Symbol(Stroke stroke)
        {
            switch (stroke)
            {
                case Stroke.Bass: return "Б";
                case Stroke.Down: return "↓";
                case Stroke.Up: return "↑";
                default: return "✕";
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

            if (!AnyPlayable())
            {
                s_AssistRect = default;
                s_PatternRect = default;
                return;
            }
            if (m_ButtonStyle == null)
                m_ButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(Screen.height * 0.016f), fontStyle = FontStyle.Bold };
            s_AssistRect = new Rect(Screen.width * 0.05f, Screen.height * 0.345f, Screen.width * 0.9f, Screen.height * 0.045f);
            DrawPatterns();
            bool assist = GUI.Toggle(s_AssistRect, s_Assist, s_Assist ? "Помощь с боем: вкл" : "Помощь с боем: выкл", m_ButtonStyle);
            if (assist != s_Assist)
            {
                s_Assist = assist;
                try
                {
                    PlayerPrefs.SetInt(k_AssistKey, assist ? 1 : 0);
                }
                catch (System.Exception)
                {
                }
            }
        }

        void DrawPatterns()
        {
            if (m_PatternLabels == null)
            {
                m_PatternLabels = new string[k_Patterns.Length];
                for (int i = 0; i < k_Patterns.Length; i++)
                    m_PatternLabels[i] = k_Patterns[i].Name;
                m_PatternStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(Screen.height * 0.026f),
                    alignment = TextAnchor.MiddleCenter,
                    richText = true,
                    normal = { textColor = Color.white },
                };
            }

            s_PatternRect = new Rect(Screen.width * 0.05f, Screen.height * 0.395f, Screen.width * 0.9f, Screen.height * 0.045f);
            int selected = GUI.Toolbar(s_PatternRect, s_Pattern, m_PatternLabels, m_ButtonStyle);
            if (selected != s_Pattern)
            {
                s_Pattern = selected;
                m_PatternStep = 0;
                try
                {
                    PlayerPrefs.SetInt(k_PatternKey, selected);
                }
                catch (System.Exception)
                {
                }
            }

            // The pattern's strokes, the next one highlighted.
            var strokes = k_Patterns[s_Pattern].Strokes;
            bool restarts = Time.time - m_LastStrumTime > k_PatternRestart;
            var text = new System.Text.StringBuilder();
            if (strokes.Length == 0)
                text.Append("бей как хочешь");
            for (int i = 0; i < strokes.Length; i++)
            {
                bool next = restarts ? i == 0 : i == m_PatternStep;
                text.Append(next ? $"<color=#ffd400><b>{Symbol(strokes[i])}</b></color>" : Symbol(strokes[i]));
                if (i < strokes.Length - 1)
                    text.Append("  ");
            }
            if (s_Assist && m_Beat > 0f && !restarts)
            {
                // A light that flashes on the player's beat, to keep time by.
                float phase = (float)((Time.timeAsDouble - m_LastSounded) / m_Beat);
                bool onBeat = phase - Mathf.Floor(phase) < 0.2f;
                text.Append(onBeat ? "   <color=#ffd400>●</color>" : "   <color=#ffffff55>●</color>");
                text.Append($"  <size={Mathf.RoundToInt(Screen.height * 0.016f)}>{Mathf.RoundToInt(60f / m_Beat)} уд/мин</size>");
            }
            GUI.Label(new Rect(Screen.width * 0.05f, Screen.height * 0.445f, Screen.width * 0.9f, Screen.height * 0.04f), text.ToString(), m_PatternStyle);
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
