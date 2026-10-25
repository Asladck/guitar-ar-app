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
    /// room), a tap strums all of them. Draws a row of chord buttons; the chosen chord is what
    /// the strings play.
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

        static readonly Chord[] k_Chords =
        {
            new Chord("Em", 0, 2, 2, 0, 0, 0),
            new Chord("Am", -1, 0, 2, 2, 1, 0),
            new Chord("C", -1, 3, 2, 0, 1, 0),
            new Chord("G", 3, 2, 0, 0, 0, 3),
            new Chord("D", -1, -1, 0, 2, 3, 2),
            new Chord("E", 0, 2, 2, 1, 0, 0),
        };

        const float k_MinVolume = 0.45f;
        const float k_StrumRoll = 0.012f;     // Seconds between strings when a tap strums them all.
        const float k_MaxRoll = 0.05f;
        const float k_Retrigger = 0.04f;      // A string can't be re-plucked sooner than this.
        const float k_AimableSpacing = 0.012f; // Below this string spacing (fraction of screen width), taps strum.
        const float k_TapReach = 0.045f;       // How close a tap must be to a string (fraction of screen width).

        static Rect s_ChordRect;
        static string s_LastTapInfo = "-";

        // Temporary on-screen diagnostics while the strings don't respond on device.
        bool m_HasCamera;
        int m_TouchCount;
        int m_PluckCount;
        string m_LastError;
        GUIStyle m_DiagnosticStyle;
        static GuitarStrummer s_Instance;

        readonly Dictionary<int, Vector2> m_LastPositions = new Dictionary<int, Vector2>();
        readonly HashSet<int> m_IgnoredTouches = new HashSet<int>();
        readonly HashSet<int> m_SeenTouches = new HashSet<int>();
        readonly List<(float along, GuitarStrings guitar, int stringIndex)> m_Crossings = new List<(float, GuitarStrings, int)>();
        readonly Dictionary<(GuitarStrings, int), float> m_LastPluck = new Dictionary<(GuitarStrings, int), float>();
        readonly List<int> m_Ended = new List<int>();

        int m_Chord;
        bool m_HasPlayed;
        string[] m_ChordLabels;
        GUIStyle m_ButtonStyle;

        /// <summary>A hint for the player, or null once they've played.</summary>
        public static string Hint => s_Instance != null && !s_Instance.m_HasPlayed && AnyPlayable() ? "Подними указательный палец и проведи рукой по струнам" : null;

        /// <summary>True if a screen position (pixels, origin bottom-left) is on the chord buttons.</summary>
        public static bool IsOverButton(Vector2 screenPosition)
        {
            var guiPoint = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            return s_ChordRect.Contains(guiPoint);
        }

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
                go.AddComponent<GuitarHandPlayer>();
            }
        }

        void Awake()
        {
            s_Instance = this;
            m_ChordLabels = new string[k_Chords.Length];
            for (int i = 0; i < k_Chords.Length; i++)
                m_ChordLabels[i] = k_Chords[i].Name;
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
            // Make the first chord's notes now so the first strum doesn't hitch.
            PrepareChord(m_Chord);
            var config = AudioSettings.GetConfiguration();
            Debug.Log($"[GuitarAR] Audio ready: {config.sampleRate} Hz, {config.speakerMode}, buffer {config.dspBufferSize}");
        }

        void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            // Clips made for the old output may be gone or at the wrong sample rate.
            GuitarSynth.ClearCache();
            PrepareChord(m_Chord);
            Debug.Log($"[GuitarAR] Audio configuration changed (device changed: {deviceWasChanged})");
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                PlayEvenWhenSilent();
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
                if (IsOverButton(position) || GuitarModeSwitcher.IsOverButton(position))
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
                        Play(guitar, i, 0.8f, i * k_StrumRoll);
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

        /// <summary>
        /// Strums the chosen chord: a down strum rolls from the low E up, an up strum (lighter)
        /// rolls back over the top four strings.
        /// </summary>
        public static void Strum(GuitarStrings guitar, bool down, float volume)
        {
            if (s_Instance == null)
                return;
            if (down)
            {
                for (int i = 0; i < GuitarStrings.StringCount; i++)
                    s_Instance.Play(guitar, i, volume, i * k_StrumRoll);
            }
            else
            {
                for (int i = GuitarStrings.StringCount - 1, n = 0; i >= 2; i--, n++)
                    s_Instance.Play(guitar, i, volume * 0.8f, n * k_StrumRoll * 0.8f);
            }
        }

        /// <summary>Plucks one string of the chosen chord.</summary>
        public static void PluckString(GuitarStrings guitar, int stringIndex, float volume)
        {
            if (s_Instance != null)
                s_Instance.Play(guitar, stringIndex, volume, 0f);
        }

        void Play(GuitarStrings guitar, int stringIndex, float volume, float delay)
        {
            int fret = k_Chords[m_Chord].Frets[stringIndex];
            if (fret < 0)
                return;

            var key = (guitar, stringIndex);
            if (m_LastPluck.TryGetValue(key, out float last) && Time.time - last < k_Retrigger)
                return;
            m_LastPluck[key] = Time.time;

            guitar.Pluck(stringIndex, GuitarSynth.OpenStrings[stringIndex] + fret, volume, delay);
            m_PluckCount++;
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
                    GuitarSynth.Note(GuitarSynth.OpenStrings[i] + frets[i]);
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

        void OnGUI()
        {
            DrawDiagnostics();
            if (!AnyPlayable())
            {
                s_ChordRect = default;
                return;
            }

            if (m_ButtonStyle == null)
                m_ButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(Screen.height * 0.02f), fontStyle = FontStyle.Bold };

            // Just above the mode buttons.
            float width = Screen.width * 0.9f;
            float height = Screen.height * 0.055f;
            float modeTop = Screen.height - Screen.height * 0.065f - Screen.height * 0.06f;
            s_ChordRect = new Rect((Screen.width - width) * 0.5f, modeTop - height - Screen.height * 0.015f, width, height);

            int selected = GUI.Toolbar(s_ChordRect, m_Chord, m_ChordLabels, m_ButtonStyle);
            if (selected != m_Chord)
            {
                m_Chord = selected;
                PrepareChord(m_Chord);
            }
        }

        void DrawDiagnostics()
        {
            if (m_DiagnosticStyle == null)
            {
                m_DiagnosticStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.RoundToInt(Screen.height * 0.013f),
                    wordWrap = true,
                    normal = { textColor = Color.yellow },
                };
            }

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

            string text = $"камера: {(m_HasCamera ? "да" : "НЕТ")} | гитар: {GuitarStrings.Active.Count}, играбельных: {playable}\n"
                + $"касаний: {m_TouchCount}, щипков: {m_PluckCount}, звук: {AudioSettings.GetConfiguration().sampleRate} Гц\n"
                + $"струна 1 на экране: {onScreen}\nпоследнее касание: {s_LastTapInfo}"
                + "\n" + GuitarHandPlayer.DiagnosticText
                + (m_LastError != null ? "\nошибка: " + m_LastError : "");
            GUI.Label(new Rect(Screen.width * 0.03f, Screen.height * 0.19f, Screen.width * 0.94f, Screen.height * 0.15f), text, m_DiagnosticStyle);
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern int GuitarAR_PlayEvenWhenSilent();
#endif

        /// <summary>
        /// iOS mutes Unity's default audio session with the silent switch, which would make the
        /// guitar seem broken; switch to the playback session that music apps use.
        /// </summary>
        static void PlayEvenWhenSilent()
        {
#if UNITY_IOS && !UNITY_EDITOR
            // Unity's output stops when the session's category changes under it; restart it.
            if (GuitarAR_PlayEvenWhenSilent() != 0)
                AudioSettings.Reset(AudioSettings.GetConfiguration());
#endif
        }
    }
}
