using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// The music: takes strike events from <see cref="GuitarHandPlayer"/> and hands the
    /// <see cref="GuitarRhythmEngine"/>'s sounds to the strings at their time on the audio clock
    /// (AudioSettings.dspTime), so the beat doesn't depend on the camera's or the screen's frame
    /// rate. The chord is read when a stroke is handed out, so a new chord is heard from the
    /// next stroke on. Stopping cancels strokes not yet started and fades out what rings.
    /// Easy also stops by itself when the strumming hand has been gone for half a second, and
    /// then waits for a new, deliberate swing (or the Play button).
    /// </summary>
    public class GuitarMusic : MonoBehaviour
    {
        const float k_HandGoneSeconds = 0.5f;
        const float k_StopFadeSeconds = 0.3f;
        const float k_EasyVolume = 0.9f;
        const string k_ModeKey = "GuitarAR.PlayMode";
        const string k_BpmKey = "GuitarAR.Bpm";
        const string k_PatternKey = "GuitarAR.Pattern";
        const string k_MetronomeKey = "GuitarAR.Metronome";
        const float k_DefaultBpm = 90f;
        const int k_DefaultPattern = 1;  // Поп.

        readonly GuitarRhythmEngine m_Engine = new GuitarRhythmEngine();
        readonly List<ScheduledSound> m_Sounds = new List<ScheduledSound>();
        readonly AudioSource[] m_Clicks = new AudioSource[2];
        AudioClip m_Click, m_AccentClick;
        int m_NextClick;
        float m_HandSeenTime = float.NegativeInfinity;
        bool m_HandSinceStart;  // Started with the button, the music doesn't wait for a hand it never had.

        // For the screen: what happened last, and when.
        public string LastChange { get; private set; }
        public float LastChangeTime { get; private set; } = float.NegativeInfinity;
        public bool LastAssistSnapped { get; private set; }
        public float LastStrikeTime { get; private set; } = float.NegativeInfinity;

        // For the diagnostics.
        string m_LastTiming = "-";
        int m_Skipped;

        public PlayMode Mode
        {
            get => m_Engine.Mode;
            set
            {
                if (value == m_Engine.Mode)
                    return;
                Stop(null);
                m_Engine.Mode = value;
                Save(k_ModeKey, (int)value);
            }
        }

        public float Bpm
        {
            get => m_Engine.Bpm;
            set
            {
                m_Engine.Bpm = Mathf.Round(value);
                try
                {
                    PlayerPrefs.SetFloat(k_BpmKey, m_Engine.Bpm);
                }
                catch (System.Exception)
                {
                }
            }
        }

        public int Pattern
        {
            get => System.Array.IndexOf(StrumPattern.All, m_Engine.Pattern);
            set
            {
                if (value < 0 || value >= StrumPattern.All.Length)
                    return;
                m_Engine.Pattern = StrumPattern.All[value];
                Save(k_PatternKey, value);
            }
        }

        public bool Metronome
        {
            get => m_Engine.Metronome;
            set
            {
                m_Engine.Metronome = value;
                Save(k_MetronomeKey, value ? 1 : 0);
                if (!value)
                    StopClicks();
            }
        }

        /// <summary>Easy: whether the pattern is playing.</summary>
        public bool IsPlaying => m_Engine.IsPlaying;

        public StrumPattern CurrentPattern => m_Engine.Pattern;

        /// <summary>Where the music is: beat 0..3, how far into it, the slot in the pattern. False while nothing keeps time.</summary>
        public bool TryGetPosition(out int beat, out float phase, out int slot) => m_Engine.TryGetPosition(AudioSettings.dspTime, out beat, out phase, out slot);

        public string DiagnosticText => $"музыка: {Mode}, {Bpm:0} уд/мин, {(IsPlaying ? "играет" : "стоит")}, пропущено долей {m_Skipped}\n{m_LastTiming}";

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
                m_Engine.Mode = (PlayMode)Mathf.Clamp(PlayerPrefs.GetInt(k_ModeKey, (int)PlayMode.Easy), 0, 2);
                m_Engine.Bpm = PlayerPrefs.GetFloat(k_BpmKey, k_DefaultBpm);
                m_Engine.Pattern = StrumPattern.All[Mathf.Clamp(PlayerPrefs.GetInt(k_PatternKey, k_DefaultPattern), 0, StrumPattern.All.Length - 1)];
                m_Engine.Metronome = PlayerPrefs.GetInt(k_MetronomeKey, 0) != 0;
            }
            catch (System.Exception)
            {
            }

            for (int i = 0; i < m_Clicks.Length; i++)
            {
                m_Clicks[i] = gameObject.AddComponent<AudioSource>();
                m_Clicks[i].playOnAwake = false;
                m_Clicks[i].spatialBlend = 0f;
                m_Clicks[i].volume = 0.35f;
            }
            m_Click = MakeClick("Click", 1000f);
            m_AccentClick = MakeClick("AccentClick", 1500f);
        }

        /// <summary>The strumming hand was seen in a detection that can be trusted.</summary>
        public void StrummingHandSeen()
        {
            m_HandSeenTime = Time.unscaledTime;
            m_HandSinceStart = true;
        }

        /// <summary>
        /// A strike from the hand. frameAge is how old the camera frame it was found in is (seconds),
        /// only for the diagnostics.
        /// </summary>
        public void OnStrike(Strike strike, double frameAge)
        {
            double now = AudioSettings.dspTime;
            LastStrikeTime = Time.unscaledTime;
            bool wasPlaying = m_Engine.IsPlaying;
            if (m_Engine.OnStrike(strike.Direction, strike.Strength, now, out var sound))
            {
                LastAssistSnapped = sound.Time > now;
                PlayStroke(sound.Kind, 0.6f + 0.4f * sound.Strength, sound.Time);
                m_LastTiming = $"удар {strike.Direction}: кадр был {frameAge * 1000:0} мс назад, звук запланирован через {(sound.Time - now) * 1000:0} мс после обработки"
                    + " (это не полная задержка от движения до звука: её надо мерить отдельно)";
            }
            else if (!wasPlaying && m_Engine.IsPlaying)
            {
                m_HandSinceStart = true;
                Changed("Играем! Кулак или ■ — стоп");
                m_LastTiming = $"старт: кадр был {frameAge * 1000:0} мс назад, первый удар через {m_Engine.StartLead * 1000:0} мс после обработки";
                GuitarDiagnosticsRecorder.Event(strike.DetectedTime, "easy start");
            }
            HandOut(now);
        }

        /// <summary>Easy: the Play button.</summary>
        public void Play()
        {
            if (Mode != PlayMode.Easy || m_Engine.IsPlaying)
                return;
            m_Engine.Start(AudioSettings.dspTime);
            m_HandSinceStart = Time.unscaledTime - m_HandSeenTime < k_HandGoneSeconds;
            Changed("Играем! Кулак или ■ — стоп");
            HandOut(AudioSettings.dspTime);
        }

        /// <summary>Stops: nothing new is played, strokes not yet started are cancelled and what rings fades out.</summary>
        public void Stop(string reason)
        {
            bool was = m_Engine.IsPlaying;
            m_Engine.Stop();
            GuitarStrings.SilenceAll(k_StopFadeSeconds);
            StopClicks();
            if (was && reason != null)
            {
                Changed(reason == "кнопка" ? "Стоп" : $"Стоп: {reason}. Взмахни, чтобы начать снова");
                GuitarDiagnosticsRecorder.Event(0, "easy stop: " + reason);
            }
        }

        void Changed(string message)
        {
            LastChange = message;
            LastChangeTime = Time.unscaledTime;
        }

        void Update()
        {
            if (!AnyPlayable())
            {
                if (m_Engine.IsPlaying)
                    Stop("гитара пропала");
                return;
            }
            if (m_Engine.IsPlaying && m_HandSinceStart && Time.unscaledTime - m_HandSeenTime > k_HandGoneSeconds)
                Stop("рука пропала");
            HandOut(AudioSettings.dspTime);
        }

        void HandOut(double now)
        {
            m_Sounds.Clear();
            m_Skipped += m_Engine.Update(now, m_Sounds);
            foreach (var sound in m_Sounds)
            {
                if (sound.IsClick)
                    PlayClick(sound.Time, sound.Accent);
                else
                    PlayStroke(sound.Kind, k_EasyVolume * sound.Strength, sound.Time);
            }
        }

        static void PlayStroke(StrumKind kind, float volume, double time)
        {
            int chord = GuitarStrummer.CurrentChord;
            foreach (var guitar in GuitarStrings.Active)
            {
                if (guitar.IsPlayable)
                    GuitarStrummer.StrumAt(guitar, kind, volume, time, chord);
            }
        }

        void PlayClick(double time, bool accent)
        {
            var source = m_Clicks[m_NextClick];
            m_NextClick = (m_NextClick + 1) % m_Clicks.Length;
            source.Stop();
            source.clip = accent ? m_AccentClick : m_Click;
            source.PlayScheduled(time);
        }

        void StopClicks()
        {
            foreach (var source in m_Clicks)
            {
                if (source != null)
                    source.Stop();
            }
        }

        /// <summary>A short, soft tick: a sine burst dying away in about 30 ms.</summary>
        static AudioClip MakeClick(string name, float frequency)
        {
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            int length = Mathf.CeilToInt(0.04f * rate);
            var data = new float[length];
            for (int n = 0; n < length; n++)
            {
                float t = n / (float)rate;
                data[n] = Mathf.Sin(2f * Mathf.PI * frequency * t) * Mathf.Exp(-t / 0.008f) * Mathf.Min(1f, n / (0.001f * rate));
            }
            var clip = AudioClip.Create(name, length, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
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
    }
}
