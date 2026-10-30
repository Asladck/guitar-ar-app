using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// The playable strings of one guitar: where they are on screen, three voices per string, and a
    /// little wobble when one is plucked. Looks for children named String1..String6 (String1 is
    /// the low E); a guitar model needs the same names to be playable.
    /// <see cref="GuitarStrummer"/> turns touches into plucks.
    /// </summary>
    public class GuitarStrings : MonoBehaviour
    {
        public const int StringCount = 6;

        const float k_WobbleAmplitude = 0.004f;  // Meters.
        const float k_WobbleFrequency = 12f;
        const float k_WobbleDecay = 0.45f;       // Seconds.
        const float k_StereoSpread = 0.35f;
        // A new note starts on a free voice while the old one ends; three, so a stroke can already
        // be scheduled while the one before it is still to start (slow frames, quick patterns).
        // At most 6 x 3 voices per guitar sound at once, and the mix gain and the limiter keep
        // them from clipping.
        const int k_VoicesPerString = 3;
        const float k_Overlap = 0.005f;          // Seconds the old note rings under the new one before fading.
        const float k_MixGain = 0.45f;           // Six strings at full volume would add up past 1 and clip.

        static readonly List<GuitarStrings> s_Active = new List<GuitarStrings>();

        readonly Transform[] m_Strings = new Transform[StringCount];
        readonly Vector3[] m_RestPositions = new Vector3[StringCount];
        readonly float[] m_PluckTimes = new float[StringCount];
        readonly float[] m_PluckStrengths = new float[StringCount];
        readonly AudioSource[] m_Voices = new AudioSource[StringCount * k_VoicesPerString];
        readonly GuitarVoiceFade[] m_Fades = new GuitarVoiceFade[StringCount * k_VoicesPerString];
        readonly int[] m_LastVoice = new int[StringCount];
        readonly double[] m_VoiceStart = new double[StringCount * k_VoicesPerString];  // AudioSettings.dspTime.

        /// <summary>How many string voices are sounding right now, on all guitars.</summary>
        public static int PlayingVoices
        {
            get
            {
                int playing = 0;
                foreach (var guitar in s_Active)
                {
                    foreach (var voice in guitar.m_Voices)
                    {
                        if (voice != null && voice.isPlaying)
                            playing++;
                    }
                }
                return playing;
            }
        }

        /// <summary>Guitars that are enabled and shown, so they can be played.</summary>
        public static IReadOnlyList<GuitarStrings> Active => s_Active;

        /// <summary>False while the guitar is hidden or shrunk away.</summary>
        public bool IsPlayable => isActiveAndEnabled && transform.lossyScale.sqrMagnitude > 0.01f && m_Strings[0] != null;

        void Awake()
        {
            for (int i = 0; i < StringCount; i++)
            {
                m_Strings[i] = transform.Find($"String{i + 1}");
                if (m_Strings[i] != null)
                    m_RestPositions[i] = m_Strings[i].localPosition;
                m_PluckTimes[i] = float.NegativeInfinity;

                // Each voice on its own object, so its fade (a filter) works on that voice alone.
                for (int v = 0; v < k_VoicesPerString; v++)
                {
                    var holder = new GameObject($"Voice{i + 1}.{v + 1}");
                    holder.transform.SetParent(transform, false);
                    var voice = holder.AddComponent<AudioSource>();
                    voice.playOnAwake = false;
                    voice.spatialBlend = 0f;
                    voice.panStereo = Mathf.Lerp(-k_StereoSpread, k_StereoSpread, i / (StringCount - 1f));
                    m_Voices[i * k_VoicesPerString + v] = voice;
                    m_Fades[i * k_VoicesPerString + v] = holder.AddComponent<GuitarVoiceFade>();
                }
            }
        }

        void OnEnable() => s_Active.Add(this);

        void OnDisable()
        {
            s_Active.Remove(this);
            foreach (var voice in m_Voices)
            {
                if (voice != null)
                    voice.Stop();
            }
            for (int i = 0; i < StringCount; i++)
            {
                m_PluckTimes[i] = float.NegativeInfinity;
                if (m_Strings[i] != null)
                    m_Strings[i].localPosition = m_RestPositions[i];
            }
        }

        /// <summary>A string's ends (bridge, nut) in screen pixels. False if it's behind the camera.</summary>
        public bool TryGetScreenSegment(int stringIndex, Camera cam, out Vector2 bridge, out Vector2 nut)
        {
            bridge = nut = default;
            var s = m_Strings[stringIndex];
            if (s == null)
                return false;

            // Unity's cylinder runs from y = -1 to 1 in its own space.
            var a = cam.WorldToScreenPoint(s.TransformPoint(new Vector3(0f, -1f, 0f)));
            var b = cam.WorldToScreenPoint(s.TransformPoint(new Vector3(0f, 1f, 0f)));
            if (a.z <= 0f || b.z <= 0f)
                return false;

            bridge = a;
            nut = b;
            return true;
        }

        /// <summary>Gets a string's note ready now, so playing it later doesn't hitch.</summary>
        public static void Prepare(int stringIndex, int midiNote)
        {
            if (GuitarSamples.Count > 0)
            {
                GuitarSamples.Prepare(stringIndex, midiNote - GuitarSynth.OpenStrings[stringIndex]);
                return;
            }
            GuitarSynth.Prepare(midiNote);
            GuitarSynth.Prepare(midiNote, true);
        }

        /// <summary>
        /// Plays a string as the given MIDI note, at a volume from 0 to 1, after a short delay in
        /// seconds (so a strum rolls across the strings instead of hitting them all at once).
        /// </summary>
        public void Pluck(int stringIndex, int midiNote, float volume, float delay = 0f, bool muted = false)
            => PluckAt(stringIndex, midiNote, volume, AudioSettings.dspTime + Mathf.Max(0f, delay), muted);

        /// <summary>
        /// Plays a string as the given MIDI note at a time on the audio clock (AudioSettings.dspTime),
        /// or at once if that's already now. The note is the string's recording when there is one,
        /// synthesized otherwise. A string that's plucked again stops ringing when the new note
        /// starts, like a real one.
        /// </summary>
        public void PluckAt(int stringIndex, int midiNote, float volume, double start, bool muted = false)
        {
            // Start on the next voice and end the ringing one when the new note sounds, not now:
            // stopping it at once would leave a silent gap until a delayed note starts.
            int previous = stringIndex * k_VoicesPerString + m_LastVoice[stringIndex];
            m_LastVoice[stringIndex] = (m_LastVoice[stringIndex] + 1) % k_VoicesPerString;
            int next = stringIndex * k_VoicesPerString + m_LastVoice[stringIndex];
            var voice = m_Voices[next];

            double now = AudioSettings.dspTime;
            bool later = start > now + 0.001;
            if (!later)
                start = now;
            voice.Stop();
            m_Fades[next].Cancel();
            int fret = midiNote - GuitarSynth.OpenStrings[stringIndex];
            if (GuitarSamples.TryGet(stringIndex, fret, volume, muted, out var clip, out float pitch))
            {
                voice.clip = clip;
                voice.pitch = pitch;
            }
            else
            {
                voice.clip = GuitarSynth.Note(midiNote, muted);
                voice.pitch = 1f;
            }
            voice.volume = volume * k_MixGain;
            if (later)
                voice.PlayScheduled(start);
            else
                voice.Play();
            m_VoiceStart[next] = start;
            if (m_Voices[previous].isPlaying)
            {
                // Fade the old note out under the new one, then stop it once it's silent.
                m_Fades[previous].FadeOutAt(start + k_Overlap);
                m_Voices[previous].SetScheduledEndTime(start + k_Overlap + GuitarVoiceFade.Seconds + 0.05);
            }

            float delay = (float)(start - now);
            m_PluckTimes[stringIndex] = Time.time + delay;
            m_PluckStrengths[stringIndex] = volume;
        }

        /// <summary>
        /// Stops the music on all guitars: notes scheduled for later never start, and what rings
        /// fades out over the given seconds instead of being cut off.
        /// </summary>
        public static void SilenceAll(float fadeSeconds)
        {
            foreach (var guitar in s_Active)
                guitar.Silence(fadeSeconds);
        }

        void Silence(float fadeSeconds)
        {
            double now = AudioSettings.dspTime;
            for (int v = 0; v < m_Voices.Length; v++)
            {
                var voice = m_Voices[v];
                if (voice == null || !voice.isPlaying)
                    continue;
                if (m_VoiceStart[v] > now + 0.005)
                {
                    voice.Stop();  // Not started yet: cancel.
                    continue;
                }
                double fadeAt = now + 0.005;
                m_Fades[v].FadeOutAt(fadeAt, fadeSeconds);
                voice.SetScheduledEndTime(fadeAt + fadeSeconds + 0.05);
            }
            for (int i = 0; i < StringCount; i++)
            {
                if (m_PluckTimes[i] > Time.time)
                    m_PluckTimes[i] = float.NegativeInfinity;
            }
        }

        void Update()
        {
            for (int i = 0; i < StringCount; i++)
            {
                var s = m_Strings[i];
                if (s == null)
                    continue;

                float age = Time.time - m_PluckTimes[i];
                if (age < 0f || age > k_WobbleDecay * 5f)
                {
                    s.localPosition = m_RestPositions[i];
                    continue;
                }

                // A fast, fading side-to-side shake reads as a vibrating string.
                float offset = k_WobbleAmplitude * m_PluckStrengths[i] * Mathf.Exp(-age / k_WobbleDecay)
                    * Mathf.Sin(age * k_WobbleFrequency * 2f * Mathf.PI);
                s.localPosition = m_RestPositions[i] + Vector3.right * offset;
            }
        }
    }
}
