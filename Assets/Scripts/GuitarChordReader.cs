using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Picks the chord with the chord hand (the left one, for a right-handed player). With
    /// gestures (the default): one raised finger is Am, two C, three G, four F; the fingers are
    /// read from their joint angles (<see cref="GuitarHandGestures"/>) and a gesture must be held
    /// 150 ms before it switches, so passing through other gestures on the way doesn't. On the
    /// neck: where the fingertips sit on the virtual fretboard is compared with each chord's
    /// fingering. Unknown gestures and a lost hand keep the last chord. A chord change is only
    /// state: the next stroke, whenever the music plays it, uses it.
    /// </summary>
    public class GuitarChordReader
    {
        public enum Mode { Neck, Gestures, Off }

        // The four fingers that fret: index, middle, ring, little.
        public const int FingerCount = 4;

        const float k_NeckHoldSeconds = 0.25f;
        public const float GestureHoldSeconds = 0.15f;
        const float k_StringWeight = 0.3f;     // Strings are only millimetres apart on screen; trust frets more.
        const float k_MaxCost = 0.6f;          // Mean squared distance (in frets) above which nothing matches.
        const float k_MaxFret = 6f;

        // Fingerings in GuitarStrummer's chord order: (string, fret) with string 0 = low E.
        static readonly Vector2Int[][] k_Shapes =
        {
            new[] { new Vector2Int(2, 2), new Vector2Int(3, 2), new Vector2Int(4, 1) },     // Am
            new[] { new Vector2Int(1, 3), new Vector2Int(2, 2), new Vector2Int(4, 1) },     // C
            new[] { new Vector2Int(0, 3), new Vector2Int(1, 2), new Vector2Int(5, 3) },     // G
            new[] { new Vector2Int(2, 3), new Vector2Int(3, 2), new Vector2Int(4, 1) },     // F (the index bars B and E)
            new[] { new Vector2Int(1, 2), new Vector2Int(2, 2) },                           // Em
            new[] { new Vector2Int(3, 2), new Vector2Int(4, 3), new Vector2Int(5, 2) },     // D
            new[] { new Vector2Int(1, 2), new Vector2Int(2, 2), new Vector2Int(3, 1) },     // E
        };

        // The gesture for each chord, in GuitarStrummer's chord order; only the first four have one.
        static readonly HandGesture[] k_Gestures = { HandGesture.OneFinger, HandGesture.TwoFingers, HandGesture.ThreeFingers, HandGesture.FourFingers };
        static readonly string[] k_GestureHints = { "1 палец", "2 пальца", "3 пальца", "4 пальца" };
        static readonly HandJoint[] k_Tips = { HandJoint.IndexTip, HandJoint.MiddleTip, HandJoint.RingTip, HandJoint.LittleTip };

        readonly GuitarGestureHold m_GestureHold = new GuitarGestureHold(GestureHoldSeconds);
        readonly Vector2[] m_Tips = new Vector2[FingerCount];
        readonly bool[] m_TipSeen = new bool[FingerCount];

        readonly Vector2[] m_Positions = new Vector2[FingerCount];   // (string, fret) of each fingertip on the neck.
        readonly bool[] m_OnNeck = new bool[FingerCount];
        readonly Vector2[] m_Bridges = new Vector2[GuitarStrings.StringCount];
        readonly Vector2[] m_Nuts = new Vector2[GuitarStrings.StringCount];

        int m_Candidate = -1;
        double m_CandidateSince;

        public Mode CurrentMode = Mode.Gestures;

        /// <summary>A line for the on-screen diagnostics.</summary>
        public string Diagnostic { get; private set; } = "левая: -";

        /// <summary>The gesture that picks a chord ("1 палец"), or null if it has none.</summary>
        public static string GestureHint(int chord) => chord >= 0 && chord < k_GestureHints.Length ? k_GestureHints[chord] : null;

        /// <summary>The gesture seen now and how long it's been held, for the hint on screen.</summary>
        public HandGesture Shown { get; private set; }

        /// <summary>
        /// Reads one detection of the chord hand (joints in screen pixels, origin bottom left).
        /// </summary>
        public void Read(GuitarHandPose pose, double timestamp)
        {
            if (CurrentMode == Mode.Off)
            {
                Diagnostic = "аккорд-рука: аккорд кнопками";
                return;
            }

            string detail;
            if (CurrentMode == Mode.Neck)
            {
                for (int f = 0; f < FingerCount; f++)
                {
                    m_TipSeen[f] = pose.Seen(k_Tips[f], 0.2f);
                    m_Tips[f] = new Vector2(pose.X[(int)k_Tips[f]], pose.Y[(int)k_Tips[f]]);
                }
                Hold(MatchNeck(m_Tips, m_TipSeen, out detail), timestamp);
            }
            else
            {
                Shown = GuitarHandGestures.Read(pose);
                var confirmed = m_GestureHold.Update(Shown, timestamp);
                int chord = System.Array.IndexOf(k_Gestures, confirmed);
                detail = $"жест {Shown}" + (chord >= 0 ? $" → {GuitarStrummer.ChordName(chord)}" : "");
                if (chord >= 0 && chord != GuitarStrummer.CurrentChord)
                    GuitarStrummer.SetChord(chord);
            }
            Diagnostic = $"аккорд-рука: {detail}";
        }

        /// <summary>The chord hand wasn't seen; the last chord stays.</summary>
        public void Lost()
        {
            m_Candidate = -1;
            m_GestureHold.Lost();
            Shown = HandGesture.Unknown;
            if (CurrentMode != Mode.Off)
                Diagnostic = "аккорд-рука: не видно";
        }

        void Hold(int chord, double timestamp)
        {
            if (chord < 0 || chord == GuitarStrummer.CurrentChord)
            {
                m_Candidate = -1;
                return;
            }
            if (chord != m_Candidate)
            {
                m_Candidate = chord;
                m_CandidateSince = timestamp;
                return;
            }
            if (timestamp - m_CandidateSince >= k_NeckHoldSeconds)
            {
                GuitarStrummer.SetChord(chord);
                m_Candidate = -1;
            }
        }

        int MatchNeck(Vector2[] tips, bool[] seen, out string detail)
        {
            detail = "гриф не виден";
            var guitar = PlayableGuitar();
            var cam = Camera.main;
            if (guitar == null || cam == null || !TryGetStrings(guitar, cam))
                return -1;

            int onNeck = 0;
            for (int f = 0; f < FingerCount; f++)
            {
                m_OnNeck[f] = seen[f] && TryNeckPosition(tips[f], out m_Positions[f]);
                if (m_OnNeck[f])
                    onNeck++;
            }
            detail = $"пальцев на грифе {onNeck}";
            if (onNeck < 2)
                return -1;

            int best = -1;
            float bestCost = float.MaxValue;
            for (int chord = 0; chord < k_Shapes.Length; chord++)
            {
                float cost = ShapeCost(k_Shapes[chord]);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = chord;
                }
            }
            if (best < 0 || bestCost > k_MaxCost)
                return -1;
            detail += $" → {GuitarStrummer.ChordName(best)} ({bestCost:0.00})";
            return best;
        }

        /// <summary>
        /// How far the fingertips are from a fingering: each fretted note takes its own fingertip,
        /// choosing the assignment that fits best. Fingers not needed may hover anywhere.
        /// </summary>
        float ShapeCost(Vector2Int[] shape)
        {
            float best = float.MaxValue;
            AssignCost(shape, 0, 0, 0f, ref best);
            return best == float.MaxValue ? best : best / shape.Length;
        }

        void AssignCost(Vector2Int[] shape, int note, int used, float cost, ref float best)
        {
            if (cost >= best)
                return;
            if (note == shape.Length)
            {
                best = cost;
                return;
            }

            // Fingers sit just behind the fret wire, a little over half way into the fret's space.
            var target = new Vector2(shape[note].x, shape[note].y - 0.4f);
            for (int f = 0; f < FingerCount; f++)
            {
                if (!m_OnNeck[f] || (used & (1 << f)) != 0)
                    continue;
                var d = m_Positions[f] - target;
                AssignCost(shape, note + 1, used | (1 << f), cost + k_StringWeight * d.x * d.x + d.y * d.y, ref best);
            }
        }

        /// <summary>
        /// Where a screen point is on the fretboard: x across the strings (0 = low E, 5 = high E),
        /// y along the neck in frets from the nut. False if it's off the first frets.
        /// </summary>
        bool TryNeckPosition(Vector2 point, out Vector2 position)
        {
            position = default;
            int last = GuitarStrings.StringCount - 1;

            // Along: project onto the middle of the strings, bridge (0) to nut (1).
            var bridge = (m_Bridges[0] + m_Bridges[last]) * 0.5f;
            var nut = (m_Nuts[0] + m_Nuts[last]) * 0.5f;
            var along = nut - bridge;
            if (along.sqrMagnitude < 1f)
                return false;
            float t = Vector2.Dot(point - bridge, along) / along.sqrMagnitude;
            float fromNut = 1f - t; // As a fraction of the scale length.
            if (fromNut <= 0f || fromNut >= 0.5f)
                return false;
            float fret = -12f * Mathf.Log(1f - fromNut, 2f);

            // Across: between the outer strings at that point.
            var low = Vector2.Lerp(m_Bridges[0], m_Nuts[0], t);
            var high = Vector2.Lerp(m_Bridges[last], m_Nuts[last], t);
            var across = high - low;
            if (across.sqrMagnitude < 1f)
                return false;
            float stringPosition = last * Vector2.Dot(point - low, across) / across.sqrMagnitude;

            if (fret > k_MaxFret || stringPosition < -1.5f || stringPosition > last + 1.5f)
                return false;
            position = new Vector2(stringPosition, fret);
            return true;
        }

        /// <summary>On the neck, dots where the current chord's fingers go.</summary>
        public void DrawGuide()
        {
            if (CurrentMode != Mode.Neck)
                return;
            var guitar = PlayableGuitar();
            var cam = Camera.main;
            if (guitar == null || cam == null || !TryGetStrings(guitar, cam))
                return;

            float size = 13f * GuitarUI.Pt;
            foreach (var note in k_Shapes[GuitarStrummer.CurrentChord])
            {
                // Fret n's space ends at fromNut = 1 - 2^(-n/12); fingers go just behind the wire.
                float fromNut = 1f - Mathf.Pow(2f, -(note.y - 0.4f) / 12f);
                var p = Vector2.Lerp(m_Bridges[note.x], m_Nuts[note.x], 1f - fromNut);
                var center = new Vector2(p.x, Screen.height - p.y);
                GuitarUI.Dot(center, size * 1.4f, new Color(0f, 0f, 0f, 0.45f));
                GuitarUI.Dot(center, size, GuitarUI.Guide);
            }
        }

        bool TryGetStrings(GuitarStrings guitar, Camera cam)
        {
            for (int i = 0; i < GuitarStrings.StringCount; i++)
            {
                if (!guitar.TryGetScreenSegment(i, cam, out m_Bridges[i], out m_Nuts[i]))
                    return false;
            }
            return true;
        }

        static GuitarStrings PlayableGuitar()
        {
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    return guitar;
            }
            return null;
        }
    }
}
