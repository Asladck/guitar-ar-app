using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
#endif

namespace GuitarAR
{
    /// <summary>
    /// Play the guitar on screen: swipe across the strings to strum, tap one to pluck it.
    /// Works with several fingers at once and with any visible <see cref="GuitarStrings"/>.
    /// When the strings are too small on screen to aim at (a guitar on someone across the
    /// room), a tap strums all of them. The chosen chord (from the chord buttons or the left
    /// hand) is what the strings play.
    /// </summary>
    public class GuitarStrummer : MonoBehaviour
    {
        struct Chord
        {
            public string Name;
            public int[] Frets; // Low E first; -1 = not played.

            public Chord(string name, params int[] frets)
            {
                Name = name;
                Frets = frets;
            }
        }

        // The first four are the beginner chords, picked with 1-4 raised fingers; the others only
        // with the buttons or on the neck. Standard tuning E2 A2 D3 G3 B3 E4.
        static readonly Chord[] k_Chords =
        {
            new Chord("Am", -1, 0, 2, 2, 1, 0),   // x02210
            new Chord("C", -1, 3, 2, 0, 1, 0),    // x32010
            new Chord("G", 3, 2, 0, 0, 0, 3),     // 320003
            new Chord("F", -1, -1, 3, 2, 1, 1),   // xx3211
            new Chord("Em", 0, 2, 2, 0, 0, 0),
            new Chord("D", -1, -1, 0, 2, 3, 2),
            new Chord("E", 0, 2, 2, 1, 0, 0),
        };

        /// <summary>How many chords are picked with gestures (the first ones).</summary>
        public const int GestureChordCount = 4;

        const float k_MinVolume = 0.45f;
        // Seconds from one string to the next in a strum: 8-10 ms, so a six-string strum takes
        // 40-50 ms, harder strums quicker. Earlier 3.5-8 ms; 12 ms was heard as smeared.
        const float k_FastRoll = 0.008f;
        const float k_SlowRoll = 0.010f;
        const float k_MaxRoll = 0.05f;
        const float k_Retrigger = 0.04f;      // A string can't be re-plucked sooner than this.
        const float k_AimableSpacing = 0.012f; // Below this string spacing (fraction of screen width), taps strum.
        const float k_TapReach = 0.045f;       // How close a tap must be to a string (fraction of screen width).

        static string s_LastTapInfo = "-";

        // Temporary on-screen diagnostics while the strings don't respond on device.
        bool m_HasCamera;
        int m_TouchCount;
        int m_PluckCount;
        string m_LastError;
        static GuitarStrummer s_Instance;

        readonly Dictionary<int, Vector2> m_LastPositions = new Dictionary<int, Vector2>();
        readonly HashSet<int> m_IgnoredTouches = new HashSet<int>();
        readonly HashSet<int> m_SeenTouches = new HashSet<int>();
        readonly List<(float along, GuitarStrings guitar, int stringIndex)> m_Crossings = new List<(float, GuitarStrings, int)>();
        readonly Dictionary<(GuitarStrings, int), float> m_LastPluck = new Dictionary<(GuitarStrings, int), float>();
        readonly Dictionary<(GuitarStrings, int), double> m_LastScheduled = new Dictionary<(GuitarStrings, int), double>();
        readonly List<int> m_Ended = new List<int>();

        int m_Chord;
        bool m_HasPlayed;

        /// <summary>The chord the strings play, as an index into the chord buttons.</summary>
        public static int CurrentChord => s_Instance != null ? s_Instance.m_Chord : 0;

        public static int ChordCount => k_Chords.Length;

        /// <summary>When a string was last played (Time.time), to flash the chord on screen.</summary>
        public static float LastPlayTime { get; private set; } = float.NegativeInfinity;

        public static string ChordName(int chord) => k_Chords[chord].Name;

        /// <summary>Switches chord, as if its button was pressed.</summary>
        public static void SetChord(int chord)
        {
            if (s_Instance == null || chord < 0 || chord >= k_Chords.Length || chord == s_Instance.m_Chord)
                return;
            s_Instance.m_Chord = chord;
            PrepareChord(chord);
        }

        /// <summary>Whether anything has been played yet.</summary>
        public static bool HasPlayed => s_Instance != null && s_Instance.m_HasPlayed;

        /// <summary>True if a tap here would play the guitar, so it shouldn't also move it.</summary>
        public static bool IsOnStrings(Vector2 screenPosition)
        {
            var cam = Camera.main;
            if (cam == null)
                return false;
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable && NearestString(guitar, cam, screenPosition, out _, out _))
                    return true;
            }
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInScene()
        {
            if (Object.FindAnyObjectByType<GuitarStrummer>() == null)
            {
                var go = new GameObject("Guitar Strummer");
                go.AddComponent<GuitarStrummer>();
                go.AddComponent<GuitarMusic>();
                go.AddComponent<GuitarHandPlayer>();
                go.AddComponent<GuitarHud>();
            }
        }

        void Awake()
        {
            s_Instance = this;
        }

        void OnEnable()
        {
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
#if ENABLE_INPUT_SYSTEM
            EnhancedTouchSupport.Enable();
#endif
        }

        void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
#if ENABLE_INPUT_SYSTEM
            EnhancedTouchSupport.Disable();
#endif
        }

        void Start()
        {
            PlayEvenWhenSilent();
            GuitarSoftLimiter.EnsureOnListener();
            // Load and make every chord's notes now, so no strum waits for them.
            for (int chord = 0; chord < k_Chords.Length; chord++)
                PrepareChord(chord);
            var config = AudioSettings.GetConfiguration();
            Debug.Log($"[GuitarAR] Audio ready: {config.sampleRate} Hz, {config.speakerMode}, buffer {config.dspBufferSize}");
        }

        void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            // Clips made for the old output may be gone or at the wrong sample rate.
            GuitarSynth.ClearCache();
            GuitarSamples.ClearCache();
            for (int chord = 0; chord < k_Chords.Length; chord++)
                PrepareChord(chord);
            Debug.Log($"[GuitarAR] Audio configuration changed (device changed: {deviceWasChanged})");
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                PlayEvenWhenSilent();
            GuitarSoftLimiter.EnsureOnListener();
        }

        void Update()
        {
            try
            {
                ReadInput();
            }
            catch (System.Exception e)
            {
                m_LastError = e.GetType().Name + ": " + e.Message;
                Debug.LogException(e);
            }
        }

        void ReadInput()
        {
            var cam = Camera.main;
            m_HasCamera = cam != null;
            if (cam == null)
                return;

            m_SeenTouches.Clear();
#if ENABLE_INPUT_SYSTEM
            foreach (var touch in Touch.activeTouches)
            {
                var phase = touch.phase;
                if (phase != UnityEngine.InputSystem.TouchPhase.Ended && phase != UnityEngine.InputSystem.TouchPhase.Canceled)
                    Track(touch.touchId, touch.screenPosition, phase == UnityEngine.InputSystem.TouchPhase.Began, cam);
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed)
                Track(-1, mouse.position.ReadValue(), mouse.leftButton.wasPressedThisFrame, cam);
#else
            for (int i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);
                if (touch.phase != TouchPhase.Ended && touch.phase != TouchPhase.Canceled)
                    Track(touch.fingerId, touch.position, touch.phase == TouchPhase.Began, cam);
            }

            if (Input.GetMouseButton(0))
                Track(-1, Input.mousePosition, Input.GetMouseButtonDown(0), cam);
#endif

            // Forget fingers that were lifted.
            m_Ended.Clear();
            foreach (var id in m_LastPositions.Keys)
            {
                if (!m_SeenTouches.Contains(id))
                    m_Ended.Add(id);
            }
            foreach (var id in m_Ended)
            {
                m_LastPositions.Remove(id);
                m_IgnoredTouches.Remove(id);
            }
        }

        void Track(int id, Vector2 position, bool began, Camera cam)
        {
            m_SeenTouches.Add(id);
            m_TouchCount++;

            if (began || !m_LastPositions.ContainsKey(id))
            {
                m_LastPositions[id] = position;
                // Touches that start on a button belong to the button.
                if (GuitarUI.IsOverUI(position))
                {
                    m_IgnoredTouches.Add(id);
                    s_LastTapInfo = "на кнопках";
                }
                else
                    m_IgnoredTouches.Remove(id);

                if (!m_IgnoredTouches.Contains(id))
                    Tap(position, cam);
                return;
            }

            var previous = m_LastPositions[id];
            m_LastPositions[id] = position;
            if (m_IgnoredTouches.Contains(id) || previous == position)
                return;

            Swipe(previous, position, cam);
        }

        void Tap(Vector2 position, Camera cam)
        {
            foreach (var guitar in GuitarStrings.Active)
            {
                if (!guitar.IsPlayable || !NearestString(guitar, cam, position, out int nearest, out float spacing))
                    continue;

                if (spacing < Screen.width * k_AimableSpacing)
                {
                    for (int i = 0; i < GuitarStrings.StringCount; i++)
                        Play(guitar, i, 0.8f, i * Roll(0.8f));
                }
                else
                {
                    Play(guitar, nearest, 0.8f, 0f);
                }
                return;
            }
        }

        void Swipe(Vector2 from, Vector2 to, Camera cam)
        {
            m_Crossings.Clear();
            foreach (var guitar in GuitarStrings.Active)
            {
                if (!guitar.IsPlayable)
                    continue;
                for (int i = 0; i < GuitarStrings.StringCount; i++)
                {
                    if (guitar.TryGetScreenSegment(i, cam, out var bridge, out var nut)
                        && Intersect(from, to, bridge, nut, out float along))
                        m_Crossings.Add((along, guitar, i));
                }
            }
            if (m_Crossings.Count == 0)
                return;

            // Faster swipes are louder.
            float speed = (to - from).magnitude / Mathf.Max(Time.deltaTime, 1e-3f) / Screen.height;
            float volume = Mathf.Clamp(k_MinVolume + 0.25f * speed, k_MinVolume, 1f);

            // Strings crossed later in this frame's finger movement sound slightly later.
            m_Crossings.Sort((a, b) => a.along.CompareTo(b.along));
            float roll = Mathf.Min(Time.deltaTime, k_MaxRoll);
            foreach (var crossing in m_Crossings)
                Play(crossing.guitar, crossing.stringIndex, volume, crossing.along * roll);
        }

        /// <summary>The time between strings in a strum this loud: louder strums are faster.</summary>
        static float Roll(float volume) => Mathf.Lerp(k_SlowRoll, k_FastRoll, Mathf.InverseLerp(0.5f, 1f, volume));

        /// <summary>Strums the chosen chord now (or after a delay in seconds).</summary>
        public static void Strum(GuitarStrings guitar, bool down, float volume, float delay = 0f)
        {
            if (s_Instance != null)
                StrumAt(guitar, down ? StrumKind.Down : StrumKind.Up, volume, AudioSettings.dspTime + delay, s_Instance.m_Chord);
        }

        /// <summary>
        /// One stroke of a chord at a time on the audio clock: the sounding strings one after
        /// another, low to high for a down stroke, high to low for an up one (a little lighter),
        /// or a palm-muted chuck. Each sounding string is played once.
        /// </summary>
        public static void StrumAt(GuitarStrings guitar, StrumKind kind, float volume, double start, int chord)
        {
            if (s_Instance == null || chord < 0 || chord >= k_Chords.Length)
                return;
            var frets = k_Chords[chord].Frets;
            float roll = kind == StrumKind.Mute ? Roll(volume) * 0.5f : Roll(volume);
            float level = kind == StrumKind.Up ? volume * 0.85f : kind == StrumKind.Mute ? volume * 0.9f : volume;
            int n = 0;
            for (int k = 0; k < GuitarStrings.StringCount; k++)
            {
                int i = kind == StrumKind.Up ? GuitarStrings.StringCount - 1 - k : k;
                if (frets[i] < 0)
                    continue;
                s_Instance.PlayAt(guitar, i, frets[i], level, start + n * roll, kind == StrumKind.Mute);
                n++;
            }
        }

        void PlayAt(GuitarStrings guitar, int stringIndex, int fret, float volume, double start, bool muted)
        {
            // The same string twice within a few milliseconds would only cut its own note short.
            var key = (guitar, stringIndex);
            if (m_LastScheduled.TryGetValue(key, out double last) && System.Math.Abs(start - last) < k_Retrigger)
                return;
            m_LastScheduled[key] = start;
            guitar.PluckAt(stringIndex, GuitarSynth.OpenStrings[stringIndex] + fret, volume, start, muted);
            m_PluckCount++;
            LastPlayTime = Time.time + Mathf.Max(0f, (float)(start - AudioSettings.dspTime));
            m_HasPlayed = true;
        }

        /// <summary>Plucks one string of the chosen chord.</summary>
        public static void PluckString(GuitarStrings guitar, int stringIndex, float volume)
        {
            if (s_Instance != null)
                s_Instance.Play(guitar, stringIndex, volume, 0f);
        }

        void Play(GuitarStrings guitar, int stringIndex, float volume, float delay, bool muted = false)
        {
            int fret = k_Chords[m_Chord].Frets[stringIndex];
            if (fret < 0)
                return;

            var key = (guitar, stringIndex);
            if (m_LastPluck.TryGetValue(key, out float last) && Time.time - last < k_Retrigger)
                return;
            m_LastPluck[key] = Time.time;

            guitar.Pluck(stringIndex, GuitarSynth.OpenStrings[stringIndex] + fret, volume, delay, muted);
            m_PluckCount++;
            LastPlayTime = Time.time;
            if (!m_HasPlayed)
                Debug.Log($"[GuitarAR] First pluck: string {stringIndex + 1}, volume {volume:0.00}");
            m_HasPlayed = true;
        }

        static void PrepareChord(int chord)
        {
            var frets = k_Chords[chord].Frets;
            for (int i = 0; i < frets.Length; i++)
            {
                if (frets[i] >= 0)
                    GuitarStrings.Prepare(i, GuitarSynth.OpenStrings[i] + frets[i]);
            }
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

        /// <summary>
        /// The string nearest a screen position, if it's within reach, and how far apart the
        /// strings are on screen (pixels).
        /// </summary>
        static bool NearestString(GuitarStrings guitar, Camera cam, Vector2 position, out int nearest, out float spacing)
        {
            nearest = -1;
            spacing = 0f;
            float best = float.MaxValue;
            Vector2 firstMiddle = default, lastMiddle = default;
            for (int i = 0; i < GuitarStrings.StringCount; i++)
            {
                if (!guitar.TryGetScreenSegment(i, cam, out var bridge, out var nut))
                {
                    s_LastTapInfo = $"струна {i + 1} за камерой";
                    return false;
                }

                // Measure spacing near the bridge end, where the strings are strummed.
                var middle = Vector2.Lerp(bridge, nut, 0.2f);
                if (i == 0)
                    firstMiddle = middle;
                lastMiddle = middle;

                float distance = DistanceToSegment(position, bridge, nut);
                if (distance < best)
                {
                    best = distance;
                    nearest = i;
                }
            }

            spacing = (lastMiddle - firstMiddle).magnitude / (GuitarStrings.StringCount - 1);
            float reach = Mathf.Max(Screen.width * k_TapReach, spacing * 0.6f);
            s_LastTapInfo = $"до струны {best:0} px (порог {reach:0}), между струнами {spacing:0} px";
            return best <= reach;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>Whether segment p1-p2 crosses q1-q2, and how far along p1-p2 (0..1).</summary>
        internal static bool Intersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2, out float along)
        {
            along = 0f;
            var r = p2 - p1;
            var s = q2 - q1;
            float denominator = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(denominator) < 1e-6f)
                return false;

            var qp = q1 - p1;
            float t = (qp.x * s.y - qp.y * s.x) / denominator;
            float u = (qp.x * r.y - qp.y * r.x) / denominator;
            along = t;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        /// <summary>Technical details for the diagnostics card, which is shown from the settings.</summary>
        public static string Diagnostics => s_Instance != null ? s_Instance.DiagnosticText() : "";

        string DiagnosticText()
        {
            int playable = 0;
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    playable++;
            }

            var cam = Camera.main;
            string onScreen = "-";
            if (cam != null && playable > 0 && GuitarStrings.Active[0].TryGetScreenSegment(0, cam, out var a, out var b))
                onScreen = $"{a:0} -> {b:0}";

            return $"камера: {(m_HasCamera ? "да" : "НЕТ")} | гитар: {GuitarStrings.Active.Count}, играбельных: {playable}\n"
                + $"касаний: {m_TouchCount}, щипков: {m_PluckCount}, звук: {(GuitarSamples.Count > 0 ? $"записи {GuitarSamples.Count}" : "синтез")}, {AudioSettings.GetConfiguration().sampleRate} Гц, dsp {AudioSettings.dspTime:0.0}, звучит {GuitarStrings.PlayingVoices}\n"
                + AudioSessionInfo() + "\n"
                + $"струна 1 на экране: {onScreen}\nпоследнее касание: {s_LastTapInfo}"
                + "\n" + GuitarHandPlayer.DiagnosticText
                + (m_LastError != null ? "\nошибка: " + m_LastError : "");
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern int GuitarAR_PlayEvenWhenSilent();

        [DllImport("__Internal")]
        static extern string GuitarAR_AudioInfo();
#endif

        /// <summary>
        /// iOS mutes Unity's default audio session with the silent switch, which would make the
        /// guitar seem broken; switch to the playback session that music apps use.
        /// </summary>
        static string AudioSessionInfo()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return "сессия: " + GuitarAR_AudioInfo();
#else
            return "сессия: -";
#endif
        }

        static void PlayEvenWhenSilent()
        {
#if UNITY_IOS && !UNITY_EDITOR
            GuitarAR_PlayEvenWhenSilent();
#endif
        }
    }
}
