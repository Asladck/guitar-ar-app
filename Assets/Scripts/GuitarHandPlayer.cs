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
        enum Joint { Wrist, ThumbTip, IndexMCP, IndexPIP, IndexTip, MiddlePIP, MiddleTip, RingPIP, RingTip, LittlePIP, LittleTip, IndexDIP }

        const int k_MaxHands = 2;
        const int k_JointCount = 12;
        const int k_RightHand = 1;              // Vision's chirality for a right hand.
        const float k_MinConfidence = 0.2f;
        const float k_TipConfidence = 0.3f;     // Below this the index tip is guessed from the joint before it.
        const float k_TipFromDip = 0.8f;        // The fingertip's last bone, as a share of the one before it.
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

        /// <summary>
        /// A strumming pattern as written on a rhythm grid: each stroke sits on a slot of the bar.
        /// Strokes on neighbouring slots come too quickly to hit one by one, so they form a group,
        /// and one strike plays the whole group in time.
        /// </summary>
        struct Pattern
        {
            public string Name;
            public Stroke[] Strokes;
            public int[] Slots;        // The slot of each stroke.
            public int Length;         // Slots in a bar.
            public int SlotsPerBeat;

            public Pattern(string name, int length, int slotsPerBeat, string grid)
            {
                Name = name;
                Length = length;
                SlotsPerBeat = slotsPerBeat;
                var strokes = new System.Collections.Generic.List<Stroke>();
                var slots = new System.Collections.Generic.List<int>();
                for (int slot = 0; slot < grid.Length; slot++)
                {
                    switch (grid[slot])
                    {
                        case 'D': strokes.Add(Stroke.Down); slots.Add(slot); break;
                        case 'U': strokes.Add(Stroke.Up); slots.Add(slot); break;
                        case 'X': strokes.Add(Stroke.Mute); slots.Add(slot); break;
                        case 'B': strokes.Add(Stroke.Bass); slots.Add(slot); break;
                    }
                }
                Strokes = strokes.ToArray();
                Slots = slots.ToArray();
            }

            /// <summary>The stroke after the group that starts at stroke index start.</summary>
            public int GroupEnd(int start)
            {
                int end = start + 1;
                while (end < Strokes.Length && Slots[end] == Slots[end - 1] + 1)
                    end++;
                return end;
            }
        }

        // D = down, U = up, X = muted down (глушение), B = bass, . = rest; one character per slot.
        static readonly Pattern[] k_Patterns =
        {
            // Plays whatever the hand does.
            new Pattern("Свободный", 1, 1, ""),
            // Eighths, «раз-и-два-и-три-и-четыре-и»: ↓ _ ✕ ↑ _ ↑ ✕ ↑. Strikes: ↓, ✕↑, ↑✕↑.
            new Pattern("Шестёрка", 8, 2, "D.XU.UXU"),
            // Triplets, the plain version without muting: ↓ _ ↓ | _ ↑ ↑ | _ ↑ ↓ | _ ↓ ↑.
            // Strikes: ↓, ↓, ↑↑, ↑↓, ↓↑.
            new Pattern("Восьмёрка", 12, 3, "D.D.UU.UD.DU"),
        };

        // Rhythm: the player's tempo is learned from their strikes. With strum help on, a strike
        // that comes a little early is held back to land on the beat.
        const int k_TempoSamples = 4;
        const float k_DefaultSlot = 0.15f;     // Seconds per slot until the player's tempo is known.
        const float k_MaxGroupStep = 0.11f;    // Seconds; the strokes of a group follow each other at least this quickly.
        const float k_MinSlot = 0.07f;         // Shortest and longest slot that count towards the tempo.
        const float k_MaxSlot = 0.5f;
        const float k_MaxRhythmDelay = 0.07f;  // Seconds; the most a strike is held back.
        // How far from when a group is due (in slots) a strike may come to play it: the next group...
        const float k_NextGroupSlots = 1.25f;
        const float k_NextGroupShare = 0.6f;   // ...or this share of the slots to it, if that's more...
        const float k_OtherGroupSlots = 1f;    // ...and another group, the player having got there unseen.

        readonly float[] m_SlotSamples = new float[k_TempoSamples];
        int m_SlotSampleCount;
        float m_Slot;                           // The player's tempo: seconds per slot, 0 while unknown.
        double m_LastSounded = double.NegativeInfinity;
        double m_BarStart;
        int m_PreviousSlot = -1;
        double m_GroupBusyUntil = double.NegativeInfinity;

        // Later strokes of a group, waiting for their time.
        struct PendingStroke
        {
            public double Time;
            public GuitarStrings Guitar;
            public Stroke Stroke;
            public float Volume;
        }

        readonly System.Collections.Generic.List<PendingStroke> m_Pending = new System.Collections.Generic.List<PendingStroke>();

        static int s_Pattern;
        int m_PatternStep;

        static string s_Diagnostic = "рука: -";
        static bool s_Assist = true;

        readonly float[] m_Points = new float[k_MaxHands * k_JointCount * 3];
        readonly int[] m_Chirality = new int[k_MaxHands];
        double m_LastTimestamp;
        ARSession m_Session;
        ARCameraManager m_CameraManager;
        readonly GuitarChordReader m_ChordReader = new GuitarChordReader();
        const string k_ChordModeKey = "GuitarAR.ChordMode";

        // The right hand's index fingertip, in screen pixels: as tracked, and with its jitter
        // smoothed for slow moves (fast ones would lag).
        bool m_HasTip;
        bool m_Raised;
        Vector2 m_Tip;
        double m_TipTime;
        readonly GuitarPointFilter m_TipFilter = new GuitarPointFilter(1.5f, 5f, 1f);
        // The ring on screen: lighter smoothing and a quicker speed, so it can be carried on to
        // where the finger is now (tuned on simulated tracking: a third of the error while moving).
        readonly GuitarPointFilter m_RingFilter = new GuitarPointFilter(3f, 20f, 8f);
        float m_TipConfidence;
        double m_ShownTime;                     // Camera time of the frame on screen.
        float m_FrameRate;                      // Hand detections per second.
        float m_LastStrumTime = float.NegativeInfinity;
        bool m_LastStrumDown;

        public enum HandState { NotSeen, FingerDown, Ready }

        /// <summary>A line for the on-screen diagnostics.</summary>
        public static string DiagnosticText => s_Diagnostic;

        public static int PatternCount => k_Patterns.Length;

        public static string PatternName(int pattern) => k_Patterns[pattern].Name;

        /// <summary>Strum help: quick passes through a wide zone strum the whole chord.</summary>
        public static bool StrumAssist
        {
            get => s_Assist;
            set
            {
                s_Assist = value;
                Save(k_AssistKey, value ? 1 : 0);
            }
        }

        /// <summary>The strumming pattern; choosing one starts it from its first stroke.</summary>
        public int CurrentPattern
        {
            get => s_Pattern;
            set
            {
                if (value == s_Pattern || value < 0 || value >= k_Patterns.Length)
                    return;
                s_Pattern = value;
                m_PatternStep = 0;
                m_PreviousSlot = -1;
                m_GroupBusyUntil = double.NegativeInfinity;
                m_Pending.Clear();
                Save(k_PatternKey, value);
            }
        }

        /// <summary>How the left hand picks the chord.</summary>
        public GuitarChordReader.Mode ChordMode
        {
            get => m_ChordReader.CurrentMode;
            set
            {
                m_ChordReader.CurrentMode = value;
                Save(k_ChordModeKey, (int)value);
            }
        }

        public GuitarChordReader ChordReader => m_ChordReader;

        /// <summary>Whether the right hand is seen, and whether its index finger is up to play.</summary>
        public HandState RightHand => !m_HasTip ? HandState.NotSeen : m_Raised ? HandState.Ready : HandState.FingerDown;

        /// <summary>
        /// The right index fingertip in screen pixels (origin bottom left), while it's seen: where
        /// it is in the camera image on screen now. Finding the hand takes a frame or two, so the
        /// last place found is carried on along the finger's movement.
        /// </summary>
        public Vector2 Fingertip => m_RingFilter.Predict(m_ShownTime, 0.7f, 0.1f, 0.3f);

        /// <summary>
        /// The pattern's strokes as symbols, one entry per group that a single strike plays, and
        /// which group the next strike plays. No groups for free strumming.
        /// </summary>
        public int PatternGroups(System.Collections.Generic.List<string> groups)
        {
            groups.Clear();
            var pattern = k_Patterns[s_Pattern];
            for (int start = 0; start < pattern.Strokes.Length;)
            {
                int end = pattern.GroupEnd(start);
                var group = new System.Text.StringBuilder();
                for (int i = start; i < end; i++)
                    group.Append(Symbol(pattern.Strokes[i]));
                groups.Add(group.ToString());
                start = end;
            }

            // Which group the step is at, counted in groups.
            bool restarts = Time.time - m_LastStrumTime > k_PatternRestart;
            int next = 0;
            for (int start = 0; !restarts && start < m_PatternStep && start < pattern.Strokes.Length; start = pattern.GroupEnd(start))
                next++;
            return next;
        }

        /// <summary>
        /// The player's tempo in beats per minute, and whether this moment is on a beat. False
        /// while the tempo isn't known or strum help is off.
        /// </summary>
        public bool TryGetBeat(out int beatsPerMinute, out bool onBeat)
        {
            beatsPerMinute = 0;
            onBeat = false;
            var pattern = k_Patterns[s_Pattern];
            if (!s_Assist || m_Slot <= 0f || Time.time - m_LastStrumTime > k_PatternRestart || pattern.Strokes.Length == 0)
                return false;
            float beat = m_Slot * pattern.SlotsPerBeat;
            float phase = (float)((Time.timeAsDouble - m_BarStart) / beat);
            onBeat = phase - Mathf.Floor(phase) < 0.2f;
            beatsPerMinute = Mathf.RoundToInt(60f / beat);
            return true;
        }

        static void Save(string key, int value)
        {
            try
            {
                PlayerPrefs.SetInt(key, value);
            }
            catch (System.Exception)
            {
            }
        }

        void Awake()
        {
            try
            {
                s_Assist = PlayerPrefs.GetInt(k_AssistKey, 1) != 0;
                s_Pattern = Mathf.Clamp(PlayerPrefs.GetInt(k_PatternKey, 0), 0, k_Patterns.Length - 1);
                m_ChordReader.CurrentMode = (GuitarChordReader.Mode)Mathf.Clamp(PlayerPrefs.GetInt(k_ChordModeKey, 0), 0, 2);
            }
            catch (System.Exception)
            {
                s_Assist = true;
            }
        }

        void Update()
        {
            PlayPending();
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
            m_ShownTime = GuitarHands_ShownTime();

            int count = GuitarHands_Get(m_Points, m_Chirality, k_MaxHands, out double timestamp);
            if (timestamp == m_LastTimestamp)
                return;
            if (m_LastTimestamp > 0 && timestamp > m_LastTimestamp)
                m_FrameRate = Mathf.Lerp(m_FrameRate, 1f / (float)(timestamp - m_LastTimestamp), 0.1f);
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

            int right = -1, left = -1;
            for (int h = 0; h < count; h++)
            {
                if (m_Chirality[h] == rightHand && right < 0)
                    right = h;
                else if (m_Chirality[h] == -rightHand && left < 0)
                    left = h;
            }

            // The left hand picks the chord.
            if (left >= 0)
                ReadLeftHand(left, timestamp);
            else
                m_ChordReader.Lost();

            if (right < 0 || !TryRead(right, out var tip, out float ratio, out bool ratioKnown))
            {
                // A blurred or briefly missed hand keeps its last fingertip for a moment.
                if (m_HasTip && timestamp - m_TipTime > k_MaxGap)
                {
                    m_HasTip = false;
                    m_TipFilter.Reset();
                    m_RingFilter.Reset();
                }
                s_Diagnostic = $"рук: {count}, правая: не видно, {m_FrameRate:0} кадр/с\n{m_ChordReader.Diagnostic}";
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

            if (m_HasTip && timestamp - m_TipTime > k_MaxGap)
            {
                m_TipFilter.Reset();
                m_RingFilter.Reset();
            }
            m_RingFilter.Filter(tip, timestamp);
            var smoothFrom = m_TipFilter.Value;
            var smoothTip = m_TipFilter.Filter(tip, timestamp);
            if (m_HasTip && (m_Raised || wasRaised) && timestamp - m_TipTime <= k_MaxGap)
                Move(m_Tip, tip, smoothFrom, smoothTip, (float)(timestamp - m_TipTime));

            m_HasTip = true;
            m_Tip = tip;
            m_TipTime = timestamp;
            s_Diagnostic = $"рук: {count}, правая: палец {(m_Raised ? "поднят" : "опущен")} ({ratio:0.00}), кончик {tip:0} ({m_TipConfidence:0.00}), {m_FrameRate:0} кадр/с\n{m_ChordReader.Diagnostic}";
        }

        static readonly Joint[] k_FingerTips = { Joint.IndexTip, Joint.MiddleTip, Joint.RingTip, Joint.LittleTip };
        static readonly Joint[] k_FingerKnuckles = { Joint.IndexPIP, Joint.MiddlePIP, Joint.RingPIP, Joint.LittlePIP };

        readonly Vector2[] m_LeftTips = new Vector2[GuitarChordReader.FingerCount];
        readonly Vector2[] m_LeftKnuckles = new Vector2[GuitarChordReader.FingerCount];
        readonly bool[] m_LeftSeen = new bool[GuitarChordReader.FingerCount];

        void ReadLeftHand(int hand, double timestamp)
        {
            bool wristSeen = TryGetJoint(hand, Joint.Wrist, out var wrist);
            for (int f = 0; f < GuitarChordReader.FingerCount; f++)
            {
                m_LeftSeen[f] = TryGetJoint(hand, k_FingerTips[f], out m_LeftTips[f])
                    & TryGetJoint(hand, k_FingerKnuckles[f], out m_LeftKnuckles[f]);
            }
            m_ChordReader.Read(wrist, wristSeen, m_LeftTips, m_LeftKnuckles, m_LeftSeen, timestamp);
        }

        /// <summary>
        /// The fingertip moved from one place to another (as tracked, and smoothed). A fast move
        /// is taken as tracked, so a strum isn't late; a slow one smoothed, so the tracking's
        /// jitter doesn't graze strings.
        /// </summary>
        void Move(Vector2 trackedFrom, Vector2 trackedTo, Vector2 smoothFrom, Vector2 smoothTo, float seconds)
        {
            var cam = Camera.main;
            float speed = (trackedTo - trackedFrom).magnitude / Screen.height / Mathf.Max(seconds, 1e-3f);
            bool fast = speed >= Mathf.Min(k_AssistStrumSpeed, k_StrumSpeed);
            var from = fast ? trackedFrom : smoothFrom;
            var to = fast ? trackedTo : smoothTo;
            float distance = (to - from).magnitude / Screen.height;
            if (cam == null || distance <= 0f)
                return;

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

            var pattern = k_Patterns[s_Pattern];
            if (pattern.Strokes.Length == 0)
            {
                m_LastStrumTime = Time.time;
                m_LastStrumDown = down;
                float freeDelay = Rhythm(sinceLast, m_PreviousSlot < 0 ? 0 : 1);
                m_PreviousSlot = 0;
                GuitarStrummer.Strum(guitar, down, volume, freeDelay);
                return;
            }

            // The player's strike sets the timing; the pattern decides how it sounds. A strike plays
            // a whole group of quick strokes, spaced at the player's tempo.
            if (sinceLast > k_PatternRestart || m_PatternStep >= pattern.Strokes.Length)
            {
                m_PatternStep = 0;
                m_PreviousSlot = -1;
                m_GroupBusyUntil = double.NegativeInfinity;
            }
            // While a group plays, the hand is making its strokes: those passes are already heard.
            if (Time.timeAsDouble < m_GroupBusyUntil)
                return;
            // A strike the wrong way plays nothing, also before the pattern has begun: it waits
            // for its first stroke.
            int step = NextGroup(pattern, down, sinceLast);
            if (step < 0)
                return;  // The hand coming back between strokes, as it does when strumming.

            m_LastStrumTime = Time.time;
            m_LastStrumDown = down;
            m_PatternStep = step;
            int slot = pattern.Slots[m_PatternStep];
            int gap = m_PreviousSlot < 0 ? 0 : (slot - m_PreviousSlot + pattern.Length) % pattern.Length;
            if (gap == 0 && m_PreviousSlot >= 0)
                gap = pattern.Length;
            float delay = Rhythm(sinceLast, gap);
            if (m_PatternStep == 0)
                m_BarStart = Time.timeAsDouble + delay;

            // A group is a quick flick of the wrist, however slowly the bar goes.
            float slotSeconds = m_Slot > 0f ? m_Slot : k_DefaultSlot;
            float groupStep = Mathf.Min(slotSeconds, k_MaxGroupStep);
            int end = pattern.GroupEnd(m_PatternStep);
            Play(guitar, pattern.Strokes[m_PatternStep], volume, delay);
            for (int i = m_PatternStep + 1; i < end; i++)
            {
                m_Pending.Add(new PendingStroke
                {
                    Time = Time.timeAsDouble + delay + (pattern.Slots[i] - slot) * groupStep,
                    Guitar = guitar,
                    Stroke = pattern.Strokes[i],
                    Volume = volume,
                });
            }
            m_GroupBusyUntil = Time.timeAsDouble + delay + (pattern.Slots[end - 1] - slot) * groupStep + 0.5f * slotSeconds;

            m_PreviousSlot = slot;
            m_PatternStep = end % pattern.Strokes.Length;
        }

        /// <summary>
        /// The group a strike in this direction plays, or -1 if it plays none. A strike goes the way
        /// of a group's first stroke (a muted stroke is a down stroke). Once the tempo is known, it
        /// also has to come about when that group is due: the next group, or another one if a
        /// strike wasn't seen and the player is already further on. Anything else is the hand
        /// coming back between strokes, as it does when strumming.
        /// </summary>
        int NextGroup(Pattern pattern, bool down, float sinceLast)
        {
            bool fits = IsDown(pattern.Strokes[m_PatternStep]) == down;
            if (m_PreviousSlot < 0 || m_Slot <= 0f)
                return fits ? m_PatternStep : -1;

            float elapsed = sinceLast / m_Slot;
            int best = -1;
            float bestMiss = float.MaxValue;
            for (int start = 0; start < pattern.Strokes.Length; start = pattern.GroupEnd(start))
            {
                if (IsDown(pattern.Strokes[start]) != down)
                    continue;
                int gap = (pattern.Slots[start] - m_PreviousSlot + pattern.Length) % pattern.Length;
                if (gap == 0)
                    gap = pattern.Length;
                float miss = Mathf.Abs(gap - elapsed);
                // The next group may come later or sooner as the player's tempo drifts.
                float allowed = start == m_PatternStep ? Mathf.Max(k_NextGroupSlots, k_NextGroupShare * gap) : k_OtherGroupSlots;
                if (miss <= allowed && (miss < bestMiss || (miss == bestMiss && start == m_PatternStep)))
                {
                    best = start;
                    bestMiss = miss;
                }
            }
            return best;
        }

        static bool IsDown(Stroke stroke) => stroke != Stroke.Up;

        static void Play(GuitarStrings guitar, Stroke stroke, float volume, float delay)
        {
            switch (stroke)
            {
                case Stroke.Bass: GuitarStrummer.Bass(guitar, volume, delay); break;
                case Stroke.Down: GuitarStrummer.Strum(guitar, true, volume, delay); break;
                case Stroke.Up: GuitarStrummer.Strum(guitar, false, volume * 0.9f, delay); break;
                case Stroke.Mute: GuitarStrummer.MutedStrum(guitar, volume, delay); break;
            }
        }

        void PlayPending()
        {
            double now = Time.timeAsDouble;
            for (int i = 0; i < m_Pending.Count; i++)
            {
                var pending = m_Pending[i];
                if (pending.Time > now)
                    continue;
                if (pending.Guitar != null && pending.Guitar.IsPlayable)
                    Play(pending.Guitar, pending.Stroke, pending.Volume, 0f);
                m_Pending.RemoveAt(i);
                i--;
            }
        }

        /// <summary>
        /// Learns the player's tempo from a strike that comes gapSlots after the previous one (0 if
        /// unknown) and, with strum help on, says how long to hold it back to land on the beat.
        /// Only early strikes are nudged (a sound can't be played before the strike), and only a little.
        /// </summary>
        float Rhythm(float sinceLastStrike, int gapSlots)
        {
            double now = Time.timeAsDouble;
            if (sinceLastStrike > k_PatternRestart)
            {
                m_SlotSampleCount = 0;
                m_Slot = 0f;
            }
            else if (gapSlots > 0)
            {
                float slot = sinceLastStrike / gapSlots;
                if (slot >= k_MinSlot && slot <= k_MaxSlot)
                {
                    m_SlotSamples[m_SlotSampleCount % k_TempoSamples] = slot;
                    m_SlotSampleCount++;
                    m_Slot = Median();
                }
            }

            float delay = 0f;
            if (s_Assist && gapSlots > 0 && m_Slot > 0f && m_SlotSampleCount >= 2)
            {
                float expected = m_Slot * gapSlots;
                float early = (float)(m_LastSounded + expected - now);
                if (early > 0f && early <= Mathf.Min(k_MaxRhythmDelay, expected * 0.25f))
                    delay = early;
            }
            m_LastSounded = now + delay;
            return delay;
        }

        float Median()
        {
            int n = Mathf.Min(m_SlotSampleCount, k_TempoSamples);
            var sorted = new float[n];
            System.Array.Copy(m_SlotSamples, sorted, n);
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
                default: return "×";
            }
        }

        bool TryRead(int hand, out Vector2 indexTip, out float ratio, out bool ratioKnown)
        {
            ratio = 0f;
            ratioKnown = false;
            m_TipConfidence = Confidence(hand, Joint.IndexTip);
            if (!TryGetJoint(hand, Joint.IndexTip, out indexTip, k_TipConfidence))
            {
                // A tip that's blurred or hidden: carry on from the joint before it, along the finger.
                bool pipSeen = TryGetJoint(hand, Joint.IndexPIP, out var pip);
                if (!TryGetJoint(hand, Joint.IndexDIP, out var dip, k_TipConfidence))
                    return false;
                indexTip = pipSeen ? dip + (dip - pip) * k_TipFromDip : dip;
            }

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
        bool TryGetJoint(int hand, Joint joint, out Vector2 position, float minConfidence = k_MinConfidence)
        {
            int i = (hand * k_JointCount + (int)joint) * 3;
            position = new Vector2(m_Points[i] * Screen.width, (1f - m_Points[i + 1]) * Screen.height);
            return m_Points[i + 2] >= minConfidence;
        }

        float Confidence(int hand, Joint joint) => m_Points[(hand * k_JointCount + (int)joint) * 3 + 2];

        static bool AnyPlayable()
        {
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    return true;
            }
            return false;
        }

        static int InterfaceOrientation()
        {
            // UIInterfaceOrientation values, only for when iOS can't be asked (the plugin asks it).
            // Unity's landscape left (home button on the right) is UIKit's landscape right.
            switch (Screen.orientation)
            {
                case ScreenOrientation.PortraitUpsideDown: return 2;
                case ScreenOrientation.LandscapeLeft: return 3;
                case ScreenOrientation.LandscapeRight: return 4;
                default: return 1;
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void GuitarHands_Process(System.IntPtr nativeSession, float viewWidth, float viewHeight, int interfaceOrientation);

        [DllImport("__Internal")]
        static extern int GuitarHands_Get([Out] float[] points, [Out] int[] chirality, int maxHands, out double timestamp);

        [DllImport("__Internal")]
        static extern double GuitarHands_ShownTime();
#endif
    }
}
