using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// The playable strings of one guitar: where they are on screen, a voice per string, and a
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

        static readonly List<GuitarStrings> s_Active = new List<GuitarStrings>();

        readonly Transform[] m_Strings = new Transform[StringCount];
        readonly Vector3[] m_RestPositions = new Vector3[StringCount];
        readonly float[] m_PluckTimes = new float[StringCount];
        readonly float[] m_PluckStrengths = new float[StringCount];
        readonly AudioSource[] m_Voices = new AudioSource[StringCount];

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

                var voice = gameObject.AddComponent<AudioSource>();
                voice.playOnAwake = false;
                voice.spatialBlend = 0f;
                voice.panStereo = Mathf.Lerp(-k_StereoSpread, k_StereoSpread, i / (StringCount - 1f));
                m_Voices[i] = voice;
            }
        }

        void OnEnable() => s_Active.Add(this);

        void OnDisable()
        {
            s_Active.Remove(this);
            for (int i = 0; i < StringCount; i++)
            {
                if (m_Voices[i] != null)
                    m_Voices[i].Stop();
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

        /// <summary>
        /// Plays a string as the given MIDI note, at a volume from 0 to 1, after a short delay in
        /// seconds (so a strum rolls across the strings instead of hitting them all at once).
        /// A string that's plucked again stops ringing first, like a real one.
        /// </summary>
        public void Pluck(int stringIndex, int midiNote, float volume, float delay = 0f)
        {
            var voice = m_Voices[stringIndex];
            voice.Stop();
            voice.clip = GuitarSynth.Note(midiNote);
            voice.volume = volume;
            if (delay > 0f)
                voice.PlayScheduled(AudioSettings.dspTime + delay);
            else
                voice.Play();

            m_PluckTimes[stringIndex] = Time.time + delay;
            m_PluckStrengths[stringIndex] = volume;
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
