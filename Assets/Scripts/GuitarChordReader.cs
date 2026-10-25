using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Picks the chord from the left hand. On the neck: where the fingertips sit on the virtual
    /// fretboard is compared with each chord's fingering, and the closest one wins. With gestures:
    /// the fingers held up choose the chord. A chord has to hold for a moment before it switches,
    /// and losing the hand keeps the last chord.
    /// </summary>
    public class GuitarChordReader
    {
        public enum Mode { Neck, Gestures, Off }

        // The four fingers that fret: index, middle, ring, little.
        public const int FingerCount = 4;

        const float k_HoldSeconds = 0.25f;
        const float k_ExtendedRatio = 1.15f;   // Wrist-to-tip vs wrist-to-middle-knuckle of a straight finger.
        const float k_StringWeight = 0.3f;     // Strings are only millimetres apart on screen; trust frets more.
        const float k_MaxCost = 0.6f;          // Mean squared distance (in frets) above which nothing matches.
        const float k_MaxFret = 6f;

        // Fingerings in GuitarStrummer's chord order: (string, fret) with string 0 = low E.
        static readonly Vector2Int[][] k_Shapes =
        {
            new[] { new Vector2Int(1, 2), new Vector2Int(2, 2) },                           // Em
            new[] { new Vector2Int(2, 2), new Vector2Int(3, 2), new Vector2Int(4, 1) },     // Am
            new[] { new Vector2Int(1, 3), new Vector2Int(2, 2), new Vector2Int(4, 1) },     // C
            new[] { new Vector2Int(0, 3), new Vector2Int(1, 2), new Vector2Int(5, 3) },     // G
            new[] { new Vector2Int(3, 2), new Vector2Int(4, 3), new Vector2Int(5, 2) },     // D
            new[] { new Vector2Int(1, 2), new Vector2Int(2, 2), new Vector2Int(3, 1) },     // E
        };

        // Fingers up (index, middle, ring, little) for each chord in gesture mode.
        static readonly bool[][] k_Gestures =
        {
            new[] { true, false, false, false },  // Em: index
            new[] { true, true, false, false },   // Am: index and middle
            new[] { true, true, true, false },    // C: three
            new[] { true, true, true, true },     // G: four
            new[] { true, false, false, true },   // D: index and little
            new[] { false, false, false, true },  // E: little finger
        };

        static readonly string[] k_GestureHints = { "указательный", "2 пальца", "3 пальца", "4 пальца", "указательный + мизинец", "мизинец" };

        readonly Vector2[] m_Positions = new Vector2[FingerCount];   // (string, fret) of each fingertip on the neck.
        readonly bool[] m_OnNeck = new bool[FingerCount];
        readonly Vector2[] m_Bridges = new Vector2[GuitarStrings.StringCount];
        readonly Vector2[] m_Nuts = new Vector2[GuitarStrings.StringCount];

        int m_Candidate = -1;
        double m_CandidateSince;

        public Mode CurrentMode = Mode.Neck;

        /// <summary>A line for the on-screen diagnostics.</summary>
        public string Diagnostic { get; private set; } = "левая: -";

        public static string GestureHint(int chord) => k_GestureHints[chord];

        /// <summary>
        /// Reads one detection of the left hand: fingertips and middle knuckles in screen pixels,
        /// and whether each was seen.
        /// </summary>
        public void Read(Vector2 wrist, bool wristSeen, Vector2[] tips, Vector2[] knuckles, bool[] seen, double timestamp)
        {
            if (CurrentMode == Mode.Off)
            {
                Diagnostic = "левая: аккорд кнопками";
                return;
            }

            string detail;
            int chord = CurrentMode == Mode.Neck
                ? MatchNeck(tips, seen, out detail)
                : MatchGesture(wrist, wristSeen, tips, knuckles, seen, out detail);
            Diagnostic = $"левая: {detail}";
            Hold(chord, timestamp);
        }

        /// <summary>The left hand wasn't seen; the last chord stays.</summary>
        public void Lost()
        {
            m_Candidate = -1;
            if (CurrentMode != Mode.Off)
                Diagnostic = "левая: не видно";
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
            if (timestamp - m_CandidateSince >= k_HoldSeconds)
            {
                GuitarStrummer.SetChord(chord);
                m_Candidate = -1;
            }
        }

        int MatchGesture(Vector2 wrist, bool wristSeen, Vector2[] tips, Vector2[] knuckles, bool[] seen, out string detail)
        {
            detail = "жест не виден";
            if (!wristSeen)
                return -1;

            var up = new bool[FingerCount];
            int raised = 0;
            for (int f = 0; f < FingerCount; f++)
            {
                if (!seen[f])
                    return -1;
                float toKnuckle = (knuckles[f] - wrist).magnitude;
                up[f] = toKnuckle > 1f && (tips[f] - wrist).magnitude > toKnuckle * k_ExtendedRatio;
                if (up[f])
                    raised++;
            }

            detail = $"пальцев поднято {raised}";
            for (int chord = 0; chord < k_Gestures.Length; chord++)
            {
                bool same = true;
                for (int f = 0; f < FingerCount && same; f++)
                    same = up[f] == k_Gestures[chord][f];
                if (same)
                {
                    detail += $" → {GuitarStrummer.ChordName(chord)}";
                    return chord;
                }
            }
            return -1;
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

        /// <summary>
        /// Shows how to pick chords: on the neck, dots where the current chord's fingers go; with
        /// gestures, which fingers to hold up for each chord.
        /// </summary>
        public void DrawGuide(Texture2D dot, GUIStyle labelStyle)
        {
            if (CurrentMode == Mode.Gestures)
            {
                var legend = new System.Text.StringBuilder();
                for (int chord = 0; chord < k_Gestures.Length; chord++)
                    legend.Append($"{GuitarStrummer.ChordName(chord)}: {k_GestureHints[chord]}   ");
                GUI.Label(new Rect(Screen.width * 0.05f, Screen.height * 0.49f, Screen.width * 0.9f, Screen.height * 0.05f), legend.ToString(), labelStyle);
                return;
            }
            if (CurrentMode != Mode.Neck)
                return;
            var guitar = PlayableGuitar();
            var cam = Camera.main;
            if (guitar == null || cam == null || !TryGetStrings(guitar, cam))
                return;

            float size = Screen.width * 0.022f;
            var color = GUI.color;
            GUI.color = new Color(0.2f, 0.9f, 1f, 0.9f);
            foreach (var note in k_Shapes[GuitarStrummer.CurrentChord])
            {
                // Fret n's space ends at fromNut = 1 - 2^(-n/12); fingers go just behind the wire.
                float fromNut = 1f - Mathf.Pow(2f, -(note.y - 0.4f) / 12f);
                var p = Vector2.Lerp(m_Bridges[note.x], m_Nuts[note.x], 1f - fromNut);
                GUI.DrawTexture(new Rect(p.x - size * 0.5f, Screen.height - p.y - size * 0.5f, size, size), dot);
            }
            GUI.color = color;
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
