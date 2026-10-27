using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Recordings of a real guitar, one for each string and fret, played gently (mf) and hard (ff):
    /// the classical guitar of the University of Iowa Electronic Music Studios, cut into notes by
    /// Tools/make-guitar-samples.py into Resources/GuitarSamples. Each string plays its own
    /// recording of the note; a fret that wasn't recorded borrows the nearest one, retuned.
    /// A muted note is the start of the recording, dulled and damped fast, with the slap of the palm.
    /// </summary>
    public static class GuitarSamples
    {
        const string k_Folder = "GuitarSamples";
        const float k_SoftVolume = 0.6f;          // At this volume and below only the gentle take plays...
        const float k_HardVolume = 0.95f;         // ...from this up only the hard one; between, a mix.
        const int k_MaxBorrow = 3;                // Frets a recording may be retuned to cover.
        const int k_MaxFret = 24;
        const float k_MutedSeconds = 0.09f;
        const float k_MutedDecaySeconds = 0.012f;  // The palm stops the string almost at once...
        const float k_MutedCutoff = 1500f;         // ...and dulls it: its high harmonics go first. Hz.
        const float k_SlapLevel = 0.3f;            // The palm's slap on the string, a thud of noise...
        const float k_SlapSeconds = 0.004f;        // ...over in a few milliseconds...
        const float k_SlapCutoff = 3000f;          // ...with little hiss in it. Hz.

        static readonly string[] k_Dynamics = { "mf", "ff" };
        static readonly Dictionary<AudioClip, AudioClip> s_Muted = new Dictionary<AudioClip, AudioClip>();
        static AudioClip[] s_Clips;               // By Index(dynamic, string, fret); null where none.
        static int s_Count;
        static int s_TakeFrame = -1;
        static float s_TakeDraw;

        /// <summary>How many recordings were found; 0 means the notes are synthesized.</summary>
        public static int Count
        {
            get
            {
                Load();
                return s_Count;
            }
        }

        /// <summary>Forget the made muted notes, after Unity's audio output was restarted.</summary>
        public static void ClearCache() => s_Muted.Clear();

        /// <summary>
        /// The recording to play for a string and fret at a volume from 0 to 1, and the pitch to
        /// play it at. False if there's none near enough, and the note should be synthesized.
        /// </summary>
        public static bool TryGet(int stringIndex, int fret, float volume, bool muted, out AudioClip clip, out float pitch)
        {
            clip = null;
            pitch = 1f;
            Load();
            if (s_Count == 0 || stringIndex < 0 || stringIndex >= GuitarStrings.StringCount || fret < 0 || fret > k_MaxFret)
                return false;

            // Harder strokes use the hard take more often, so repeated strums don't all sound alike.
            // The strings of one strum (all played in one frame) share the take, so it sounds
            // even: rather the same take a fret away, retuned, than the other take.
            if (s_TakeFrame != Time.frameCount)
            {
                s_TakeFrame = Time.frameCount;
                s_TakeDraw = Random.value;
            }
            int first = s_TakeDraw < Mathf.InverseLerp(k_SoftVolume, k_HardVolume, volume) ? 1 : 0;
            if (!TryFind(first, stringIndex, fret, 1, out clip, out int recordedFret)
                && !TryFind(1 - first, stringIndex, fret, 0, out clip, out recordedFret)
                && !TryFind(first, stringIndex, fret, k_MaxBorrow, out clip, out recordedFret)
                && !TryFind(1 - first, stringIndex, fret, k_MaxBorrow, out clip, out recordedFret))
                return false;

            pitch = Mathf.Pow(2f, (fret - recordedFret) / 12f);
            if (muted)
            {
                clip = Muted(clip);
                if (clip == null)
                    return false;
            }
            return true;
        }

        /// <summary>Gets a note ready now, so playing it later doesn't hitch.</summary>
        public static void Prepare(int stringIndex, int fret)
        {
            for (int dynamic = 0; dynamic < k_Dynamics.Length; dynamic++)
            {
                if (TryFind(dynamic, stringIndex, fret, k_MaxBorrow, out var clip, out _))
                    Muted(clip);
            }
        }

        static bool TryFind(int dynamic, int stringIndex, int fret, int maxBorrow, out AudioClip clip, out int recordedFret)
        {
            for (int distance = 0; distance <= maxBorrow; distance++)
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    int candidate = fret + side * distance;
                    if (candidate < 0 || candidate > k_MaxFret)
                        continue;
                    clip = s_Clips[Index(dynamic, stringIndex, candidate)];
                    if (clip != null)
                    {
                        recordedFret = candidate;
                        return true;
                    }
                }
            }
            clip = null;
            recordedFret = 0;
            return false;
        }

        static int Index(int dynamic, int stringIndex, int fret) => (dynamic * GuitarStrings.StringCount + stringIndex) * (k_MaxFret + 1) + fret;

        static void Load()
        {
            if (s_Clips != null)
                return;
            s_Clips = new AudioClip[k_Dynamics.Length * GuitarStrings.StringCount * (k_MaxFret + 1)];

            // Named like "mf_s1_f3": dynamic, string (1 is the low E), fret.
            foreach (var clip in Resources.LoadAll<AudioClip>(k_Folder))
            {
                var parts = clip.name.Split('_');
                if (parts.Length != 3 || parts[1].Length < 2 || parts[2].Length < 2)
                    continue;
                int dynamic = System.Array.IndexOf(k_Dynamics, parts[0]);
                if (dynamic < 0
                    || !int.TryParse(parts[1].Substring(1), out int stringNumber) || stringNumber < 1 || stringNumber > GuitarStrings.StringCount
                    || !int.TryParse(parts[2].Substring(1), out int fret) || fret < 0 || fret > k_MaxFret)
                    continue;
                s_Clips[Index(dynamic, stringNumber - 1, fret)] = clip;
                s_Count++;
            }
            Debug.Log($"[GuitarAR] Guitar recordings: {s_Count}");
        }

        /// <summary>The palm-muted "chuck": the pluck's first moment, dull and dying at once, and the palm's slap.</summary>
        static AudioClip Muted(AudioClip source)
        {
            if (s_Muted.TryGetValue(source, out var muted) && muted != null)
                return muted;

            if (source.loadState != AudioDataLoadState.Loaded)
                source.LoadAudioData();
            int rate = source.frequency;
            int channels = source.channels;
            int length = Mathf.Min(source.samples, Mathf.CeilToInt(k_MutedSeconds * rate));
            var data = new float[length * channels];
            if (!source.GetData(data, 0))
                return null;

            float peak = 0f;
            foreach (var x in data)
                peak = Mathf.Max(peak, Mathf.Abs(x));
            float toneSmoothing = 1f - Mathf.Exp(-2f * Mathf.PI * k_MutedCutoff / rate);
            float slapSmoothing = 1f - Mathf.Exp(-2f * Mathf.PI * k_SlapCutoff / rate);
            var noise = new System.Random(source.name.GetHashCode());
            int fade = Mathf.Max(1, Mathf.RoundToInt(0.01f * rate));
            for (int c = 0; c < channels; c++)
            {
                float tone = 0f, slap = 0f;
                for (int n = 0; n < length; n++)
                {
                    int i = n * channels + c;
                    tone += toneSmoothing * (data[i] - tone);
                    slap += slapSmoothing * ((float)noise.NextDouble() * 2f - 1f - slap);
                    float end = Mathf.Clamp01((length - n) / (float)fade);
                    data[i] = (tone * Mathf.Exp(-n / (k_MutedDecaySeconds * rate))
                        + slap * k_SlapLevel * peak * Mathf.Exp(-n / (k_SlapSeconds * rate))) * end;
                }
            }
            muted = AudioClip.Create(source.name + ".muted", length, channels, rate, false);
            muted.SetData(data, 0);
            s_Muted[source] = muted;
            return muted;
        }
    }
}
