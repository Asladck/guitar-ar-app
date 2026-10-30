using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR
{
    /// <summary>
    /// Turns the hands in front of the camera into musical events. Hands come from Apple Vision
    /// on the AR camera image (iOS only, Plugins/iOS/GuitarHandTracking.mm): 21 joints each, a
    /// new set per processed camera frame. Each set is handled once, with the camera frame's own
    /// time, never re-used on later video frames:
    /// - the hands get their roles (strumming and chord hand: <see cref="GuitarHandRoles"/>,
    ///   with the front camera's mirror, left-handed mode and the manual swap);
    /// - the strumming hand's palm (or index fingertip) is put in the guitar's own coordinates,
    ///   smoothed (One Euro) and given to the <see cref="GuitarStrikeDetector"/>, whose strikes
    ///   go to <see cref="GuitarMusic"/> as events;
    /// - the chord hand's gesture goes to the <see cref="GuitarChordReader"/>;
    /// - a fist on the strumming hand pauses strikes, and held, stops the music.
    /// A hand that isn't seen, or whose joints aren't trusted, stops giving events at once; when
    /// it's back its movement starts a new history, so nothing is made up across the gap.
    /// </summary>
    public class GuitarHandPlayer : MonoBehaviour
    {
        public enum MotionSource { Palm, IndexFinger }

        public enum HandState { NotSeen, Paused, Ready }

        const int k_MaxHands = 2;
        const int k_JointCount = GuitarHandPose.JointCount;
        const float k_TipConfidence = 0.3f;     // Below this the index tip isn't trusted for strikes.
        const float k_TipFromDip = 0.8f;        // The fingertip's last bone, as a share of the one before it (for the ring only).
        const int k_TipBackFrames = 5;          // Frames the fingertip must be seen again before strikes go back to it from the palm.
        const float k_StopFistSeconds = 0.4f;   // A fist held this long stops Easy.
        const float k_MinGuitarPixels = 20f;    // A guitar shorter on screen than this can't be played.
        // A guitar that moves this much on screen between two hand detections has jumped (a new
        // pose, a new person): the hand's movement relative to it would be made up, so it starts over.
        const float k_GuitarJump = 0.12f;       // Guitar lengths.
        const float k_GuitarResize = 0.15f;     // Share of its length.
        const float k_GuitarTurn = 12f;         // Degrees.

        // One Euro filter on the strumming hand, in guitar lengths. Started from minCutoff 1 Hz and
        // beta 0, then beta raised until a swing at about 2 lengths a second lags under 0.06 lengths
        // (checked on simulated tracking in Tools/logic-tests); the speed's own cutoff 5 Hz lets
        // the smoothing open within a couple of frames.
        public const float FilterMinCutoff = 1f;
        public const float FilterBeta = 6f;
        public const float FilterSpeedCutoff = 5f;

        const string k_ChordModeKey = "GuitarAR.ChordMode";
        const string k_SourceKey = "GuitarAR.MotionSource";
        const string k_LeftHandedKey = "GuitarAR.LeftHanded";
        const string k_SwapKey = "GuitarAR.SwapHands";
        const string k_StrikeKey = "GuitarAR.Strike.";

        static MotionSource s_Source = MotionSource.Palm;
        static bool s_LeftHanded;
        static bool s_Swapped;
        static string s_Diagnostic = "руки: -";

        readonly float[] m_Points = new float[k_MaxHands * k_JointCount * 3];
        readonly int[] m_Chirality = new int[k_MaxHands];
        readonly GuitarHandPose[] m_Poses = { new GuitarHandPose(), new GuitarHandPose() };
        readonly float[] m_Along = new float[k_MaxHands];
        double m_LastTimestamp;
        double m_ShownTime;                     // Camera time of the frame on screen.
        float m_FrameRate;                      // Hand detections per second.
        ARSession m_Session;
        ARCameraManager m_CameraManager;
        GuitarMusic m_Music;

        readonly GuitarChordReader m_ChordReader = new GuitarChordReader();
        readonly StrikeSettings m_Defaults = new StrikeSettings();
        StrikeSettings m_Settings;
        GuitarStrikeDetector m_Detector;
        readonly GuitarOneEuroFilter m_Filter = new GuitarOneEuroFilter(FilterMinCutoff, FilterBeta, FilterSpeedCutoff);
        readonly GuitarGestureHold m_FistHold = new GuitarGestureHold(k_StopFistSeconds);
        GuitarCalibrator m_Calibrator;
        string m_CalibrationResult;
        float m_CalibrationResultTime = float.NegativeInfinity;

        // The guitar on screen at the last detection: its bridge end, unit axes along and across
        // the strings, and its length, in pixels.
        bool m_HasGuitar;
        Vector2 m_Origin, m_Along1, m_Across1;
        float m_Length;

        // The strumming hand.
        HandState m_State = HandState.NotSeen;
        bool m_UsingPalm = true;                 // In finger mode, false while the fingertip is trusted.
        int m_TipBackCount;
        bool m_FingerUp = true;
        readonly GuitarPointFilter m_RingFilter = new GuitarPointFilter(3f, 20f, 8f);
        bool m_ChordSeen;
        string m_LastReset = "-";

        /// <summary>A few lines for the on-screen diagnostics.</summary>
        public static string DiagnosticText => s_Diagnostic;

        /// <summary>What strikes: the palm's centre (easier) or the index fingertip (more precise).</summary>
        public static MotionSource Source
        {
            get => s_Source;
            set
            {
                s_Source = value;
                Save(k_SourceKey, (int)value);
            }
        }

        /// <summary>Left-handed: strum with the left hand and wear the guitar the other way round.</summary>
        public static bool LeftHanded
        {
            get => s_LeftHanded;
            set
            {
                s_LeftHanded = value;
                Save(k_LeftHandedKey, value ? 1 : 0);
            }
        }

        /// <summary>The hands' roles swapped by hand, e.g. if the tracker mixes them up.</summary>
        public static bool SwapHands
        {
            get => s_Swapped;
            set
            {
                s_Swapped = value;
                Save(k_SwapKey, value ? 1 : 0);
            }
        }

        /// <summary>How the chord hand picks the chord.</summary>
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

        /// <summary>Whether the strumming hand is seen, and whether it can strike now (not a fist, finger up).</summary>
        public HandState StrummingHand => m_State;

        /// <summary>Whether the chord hand was seen in the last detection.</summary>
        public bool ChordHandSeen => m_ChordSeen;

        /// <summary>
        /// Where the strumming point is on screen now (pixels, origin bottom left), while it's
        /// seen. Finding the hand takes a frame or two, so the last place found is carried on
        /// along its movement.
        /// </summary>
        public Vector2 StrikePoint => m_RingFilter.Predict(m_ShownTime, 0.7f, 0.1f, 0.3f);

        /// <summary>Whether strikes come from the palm right now (in finger mode, while the fingertip is lost).</summary>
        public bool StrikingWithPalm => m_UsingPalm;

        /// <summary>The detector's settings now (calibrated, or the starting values).</summary>
        public StrikeSettings Settings => m_Settings;

        public bool IsCalibrating => m_Calibrator != null;

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
            m_Music = GetComponent<GuitarMusic>();
            if (m_Music == null)
                m_Music = gameObject.AddComponent<GuitarMusic>();
            m_Settings = m_Defaults.Clone();
            try
            {
                s_Source = (MotionSource)Mathf.Clamp(PlayerPrefs.GetInt(k_SourceKey, 0), 0, 1);
                s_LeftHanded = PlayerPrefs.GetInt(k_LeftHandedKey, 0) != 0;
                s_Swapped = PlayerPrefs.GetInt(k_SwapKey, 0) != 0;
                m_ChordReader.CurrentMode = (GuitarChordReader.Mode)Mathf.Clamp(PlayerPrefs.GetInt(k_ChordModeKey, (int)GuitarChordReader.Mode.Gestures), 0, 2);
                if (PlayerPrefs.HasKey(k_StrikeKey + "Band"))
                {
                    m_Settings.Band = PlayerPrefs.GetFloat(k_StrikeKey + "Band", m_Settings.Band);
                    m_Settings.MinTravel = PlayerPrefs.GetFloat(k_StrikeKey + "MinTravel", m_Settings.MinTravel);
                    m_Settings.MinSpeed = PlayerPrefs.GetFloat(k_StrikeKey + "MinSpeed", m_Settings.MinSpeed);
                    m_Settings.LineOffset = PlayerPrefs.GetFloat(k_StrikeKey + "LineOffset", m_Settings.LineOffset);
                }
            }
            catch (System.Exception)
            {
            }
            m_Detector = new GuitarStrikeDetector(m_Settings);
        }

        // ---- Calibration ----

        /// <summary>Starts the short calibration: both hands shown, then a few swings.</summary>
        public void StartCalibration()
        {
            if (m_Music != null)
                m_Music.Stop("калибровка");
            m_Calibrator = new GuitarCalibrator();
            m_CalibrationResult = null;
        }

        public void CancelCalibration()
        {
            m_Calibrator = null;
            m_CalibrationResult = null;
        }

        /// <summary>Puts the starting values back.</summary>
        public void ResetCalibration()
        {
            ApplySettings(m_Defaults.Clone());
            try
            {
                foreach (var name in new[] { "Band", "MinTravel", "MinSpeed", "LineOffset" })
                    PlayerPrefs.DeleteKey(k_StrikeKey + name);
            }
            catch (System.Exception)
            {
            }
        }

        void ApplySettings(StrikeSettings settings)
        {
            m_Settings = settings;
            m_Detector.Settings = settings;
            m_Detector.Lost("новые настройки");
        }

        /// <summary>What the calibration asks for now, or its result for a moment after; null otherwise.</summary>
        public string CalibrationHint
        {
            get
            {
                if (m_Calibrator == null)
                    return Time.time - m_CalibrationResultTime < 3f ? m_CalibrationResult : null;
                if (m_Calibrator.Current == GuitarCalibrator.Phase.ShowHands)
                    return "Калибровка 1/2: покажи обе руки и подержи их секунду";
                return $"Калибровка 2/2: сделай {GuitarCalibrator.SwingsWanted} удобных взмаха вверх-вниз над струнами ({Mathf.Min(m_Calibrator.Swings, GuitarCalibrator.SwingsWanted)}/{GuitarCalibrator.SwingsWanted})";
            }
        }

        void FeedCalibration(double timestamp, bool strumSeen, float d)
        {
            m_Calibrator.Sample(timestamp, strumSeen, d, m_ChordSeen, m_Defaults);
            if (m_Calibrator.Current == GuitarCalibrator.Phase.Done)
            {
                var s = m_Calibrator.Result;
                ApplySettings(s);
                try
                {
                    PlayerPrefs.SetFloat(k_StrikeKey + "Band", s.Band);
                    PlayerPrefs.SetFloat(k_StrikeKey + "MinTravel", s.MinTravel);
                    PlayerPrefs.SetFloat(k_StrikeKey + "MinSpeed", s.MinSpeed);
                    PlayerPrefs.SetFloat(k_StrikeKey + "LineOffset", s.LineOffset);
                }
                catch (System.Exception)
                {
                }
                m_CalibrationResult = "Калибровка готова: взмахни, чтобы играть";
                GuitarDiagnosticsRecorder.Event(timestamp, $"calibrated band={s.Band:0.000} travel={s.MinTravel:0.000} speed={s.MinSpeed:0.00} line={s.LineOffset:0.000} noise={m_Calibrator.Noise:0.0000}");
            }
            else if (m_Calibrator.Current == GuitarCalibrator.Phase.Failed)
            {
                m_CalibrationResult = "Калибровка не удалась: настройки прежние, попробуй ещё раз";
            }
            else
            {
                return;
            }
            m_CalibrationResultTime = Time.time;
            m_Calibrator = null;
        }

        // ---- Tracking ----

        void Update()
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (!AnyPlayable())
            {
                StrumHandLost("гитары нет");
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
                return;  // Nothing new: the last detection was already used.
            if (m_LastTimestamp > 0 && timestamp > m_LastTimestamp)
                m_FrameRate = Mathf.Lerp(m_FrameRate, 1f / (float)(timestamp - m_LastTimestamp), 0.1f);
            m_LastTimestamp = timestamp;
            OnHands(count, timestamp);
#endif
        }

        void OnHands(int count, double timestamp)
        {
            for (int h = 0; h < count; h++)
            {
                var pose = m_Poses[h];
                for (int j = 0; j < k_JointCount; j++)
                {
                    int i = (h * k_JointCount + j) * 3;
                    // Normalized, origin top left, to screen pixels, origin bottom left.
                    pose.X[j] = m_Points[i] * Screen.width;
                    pose.Y[j] = (1f - m_Points[i + 1]) * Screen.height;
                    pose.Confidence[j] = m_Points[i + 2];
                }
            }

            UpdateGuitarFrame();
            for (int h = 0; h < count; h++)
                m_Along[h] = m_HasGuitar && m_Poses[h].TryPalm(out float px, out float py) ? Along(new Vector2(px, py)) : float.NaN;

            if (m_CameraManager == null && Camera.main != null)
                m_CameraManager = Camera.main.GetComponent<ARCameraManager>();
            bool frontCamera = m_CameraManager != null && m_CameraManager.currentFacingDirection == CameraFacingDirection.User;
            GuitarHandRoles.Assign(count, m_Chirality, m_Along, frontCamera, s_LeftHanded, s_Swapped, out int strum, out int chord);
            GuitarDiagnosticsRecorder.Hands(timestamp, count, m_Poses, m_Chirality, strum, chord);

            m_ChordSeen = chord >= 0;
            if (chord >= 0)
                m_ChordReader.Read(m_Poses[chord], timestamp);
            else
                m_ChordReader.Lost();

            if (strum >= 0)
                ReadStrummingHand(m_Poses[strum], timestamp);
            else
                StrumHandLost("рука не видна", timestamp);

            UpdateDiagnostic(count, timestamp);
        }

        /// <summary>The guitar's place on screen now; resets the movement if it jumped since the last detection.</summary>
        void UpdateGuitarFrame()
        {
            var guitar = PlayableGuitar();
            var cam = Camera.main;
            bool had = m_HasGuitar;
            var origin = m_Origin;
            var along = m_Along1;
            float length = m_Length;
            m_HasGuitar = false;
            if (guitar == null || cam == null)
                return;
            int last = GuitarStrings.StringCount - 1;
            if (!guitar.TryGetScreenSegment(0, cam, out var lowBridge, out var lowNut)
                || !guitar.TryGetScreenSegment(last, cam, out var highBridge, out var highNut))
                return;

            var bridge = (lowBridge + highBridge) * 0.5f;
            var nut = (lowNut + highNut) * 0.5f;
            m_Length = (nut - bridge).magnitude;
            if (m_Length < k_MinGuitarPixels)
                return;
            m_Origin = bridge;
            m_Along1 = (nut - bridge) / m_Length;
            // Across: from the low E to the high E, square to the strings.
            var lowToHigh = (highBridge + highNut) * 0.5f - (lowBridge + lowNut) * 0.5f;
            var across = lowToHigh - m_Along1 * Vector2.Dot(lowToHigh, m_Along1);
            if (across.sqrMagnitude < 1e-6f)
                across = new Vector2(-m_Along1.y, m_Along1.x);
            m_Across1 = across.normalized;
            m_HasGuitar = true;

            if (had && ((m_Origin - origin).magnitude / m_Length > k_GuitarJump
                || Mathf.Abs(m_Length - length) / m_Length > k_GuitarResize
                || Vector2.Angle(m_Along1, along) > k_GuitarTurn))
            {
                ResetMovement("гитара сдвинулась");
            }
        }

        float Along(Vector2 point) => Vector2.Dot(point - m_Origin, m_Along1) / m_Length;

        float Across(Vector2 point) => Vector2.Dot(point - m_Origin, m_Across1) / m_Length;

        void ResetMovement(string reason)
        {
            m_Detector.Lost(reason);
            m_Filter.Reset();
            m_LastReset = reason;
        }

        void ReadStrummingHand(GuitarHandPose pose, double timestamp)
        {
            if (!m_HasGuitar)
            {
                StrumHandLost("гитара не видна", timestamp);
                return;
            }

            // Where it strikes from: the palm, or the index tip while it's seen well.
            bool palmSeen = pose.TryPalm(out float palmX, out float palmY);
            bool tipSeen = pose.Seen(HandJoint.IndexTip, k_TipConfidence) && pose.Seen(HandJoint.IndexPIP);
            bool usePalm;
            if (s_Source == MotionSource.Palm)
            {
                usePalm = true;
            }
            else
            {
                m_TipBackCount = tipSeen ? m_TipBackCount + 1 : 0;
                usePalm = m_UsingPalm ? m_TipBackCount < k_TipBackFrames : !tipSeen;
            }
            if (usePalm != m_UsingPalm)
            {
                // A different point moves differently: start its history over, without a strike.
                ResetMovement(usePalm ? "палец потерян, бьёт ладонь" : "палец снова виден");
                m_UsingPalm = usePalm;
            }
            if (usePalm ? !palmSeen : !tipSeen)
            {
                StrumHandLost("точки руки не уверенные", timestamp);
                return;
            }
            var point = usePalm ? new Vector2(palmX, palmY) : new Vector2(pose.X[(int)HandJoint.IndexTip], pose.Y[(int)HandJoint.IndexTip]);
            m_RingFilter.Filter(usePalm ? point : RingTip(pose), timestamp);

            // A fist (or, striking with the finger, a lowered index) plays nothing; a fist held stops Easy.
            var gesture = GuitarHandGestures.Read(pose);
            if (gesture != HandGesture.Unknown)
            {
                if (m_FistHold.Update(gesture, timestamp) == HandGesture.Fist && m_Music != null && m_Music.IsPlaying)
                    m_Music.Stop("кулак");
            }
            if (s_Source == MotionSource.IndexFinger)
            {
                var index = GuitarHandGestures.Finger(pose, 0, pose.Size());
                if (index != FingerState.Unknown)
                    m_FingerUp = index == FingerState.Straight;
            }
            else
            {
                m_FingerUp = true;
            }
            bool paused = gesture == HandGesture.Fist || !m_FingerUp;
            m_State = paused ? HandState.Paused : HandState.Ready;
            if (m_Music != null)
                m_Music.StrummingHandSeen();

            m_Filter.Filter(Across(point), Along(point), timestamp, out float d, out float v);
            if (m_Calibrator != null)
            {
                FeedCalibration(timestamp, true, d);
                m_Detector.Lost("калибровка");
                return;
            }
            if (paused)
            {
                m_Detector.Lost(gesture == HandGesture.Fist ? "кулак" : "палец опущен");
                return;
            }

            if (m_Detector.Process(timestamp, d, v))
            {
                var strike = m_Detector.LastStrike;
                GuitarDiagnosticsRecorder.Event(timestamp, $"strike {strike.Direction} at={strike.Time:0.000} speed={strike.Speed:0.00} travel={strike.Travel:0.000}");
                if (m_Music != null)
                    m_Music.OnStrike(strike, m_ShownTime - timestamp);
            }
        }

        /// <summary>The index fingertip for the ring on screen: carried on from the joint before it when it's blurred.</summary>
        static Vector2 RingTip(GuitarHandPose pose)
        {
            var tip = new Vector2(pose.X[(int)HandJoint.IndexTip], pose.Y[(int)HandJoint.IndexTip]);
            if (pose.Seen(HandJoint.IndexTip, k_TipConfidence) || !pose.Seen(HandJoint.IndexDIP))
                return tip;
            var dip = new Vector2(pose.X[(int)HandJoint.IndexDIP], pose.Y[(int)HandJoint.IndexDIP]);
            var pip = new Vector2(pose.X[(int)HandJoint.IndexPIP], pose.Y[(int)HandJoint.IndexPIP]);
            return pose.Seen(HandJoint.IndexPIP) ? dip + (dip - pip) * k_TipFromDip : dip;
        }

        void StrumHandLost(string reason, double timestamp = 0)
        {
            if (m_State != HandState.NotSeen)
            {
                m_RingFilter.Reset();
                m_FistHold.Lost();
            }
            m_State = HandState.NotSeen;
            ResetMovement(reason);
            if (m_Calibrator != null && timestamp > 0)
                FeedCalibration(timestamp, false, 0f);
        }

        void UpdateDiagnostic(int count, double timestamp)
        {
            string source = s_Source == MotionSource.Palm ? "ладонь" : m_UsingPalm ? "палец→ладонь" : "палец";
            float age = (float)(m_ShownTime - timestamp) * 1000f;
            var s = m_Settings;
            s_Diagnostic = $"трекинг: {m_FrameRate:0} кадр/с, возраст кадра {age:0} мс, рук {count}, бьёт: {source}\n"
                + $"удар: {m_Detector.State}, отклонено: {m_Detector.LastRejection}, сброс: {m_LastReset}\n"
                + $"порог: полоса {s.Band:0.000}, ход {s.MinTravel:0.000}, скорость {s.MinSpeed:0.00} дл/с, линия {s.LineOffset:+0.000;-0.000}\n"
                + m_ChordReader.Diagnostic;
        }

        // ---- Helpers ----

        static GuitarStrings PlayableGuitar()
        {
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    return guitar;
            }
            return null;
        }

        static bool AnyPlayable() => PlayableGuitar() != null;

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
