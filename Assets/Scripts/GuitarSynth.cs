using System.Collections.Generic;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Synthesizes plucked-string notes with the Karplus-Strong algorithm, so the app needs no
    /// audio files: a burst of noise circulates through a delay line one period long, and a
    /// gentle low-pass in the loop makes it ring and fade like a steel string.
    /// Clips are made once per note and cached.
    /// </summary>
    public static class GuitarSynth
    {
        /// <summary>MIDI notes of the open strings in standard tuning, low E first.</summary>
        public static readonly int[] OpenStrings = { 40, 45, 50, 55, 59, 64 };

        const float k_Duration = 3.5f;
        const float k_FadeOut = 0.3f;
        const float k_LowDecaySeconds = 5f;   // Ring time (to -60 dB) of the lowest string.
        const float k_HighDecaySeconds = 2.2f; // ...and of notes two octaves up.
        const float k_PickPosition = 0.18f;    // Where along the string it's plucked (0 = bridge).
        const float k_Peak = 0.6f;

        static readonly Dictionary<int, AudioClip> s_Clips = new Dictionary<int, AudioClip>();

        public static float Frequency(int midiNote) => 440f * Mathf.Pow(2f, (midiNote - 69) / 12f);

        public static AudioClip Note(int midiNote)
        {
            if (s_Clips.TryGetValue(midiNote, out var clip) && clip != null)
                return clip;

            int sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            var samples = Pluck(Frequency(midiNote), sampleRate, midiNote);
            clip = AudioClip.Create($"Note{midiNote}", samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            s_Clips[midiNote] = clip;
            return clip;
        }

        static float[] Pluck(float frequency, int sampleRate, int seed)
        {
            var output = new float[Mathf.CeilToInt(k_Duration * sampleRate)];

            // The two-point average in the loop delays by half a sample; a first-order all-pass
            // supplies the rest of the fractional period so high notes stay in tune.
            float period = sampleRate / frequency;
            int length = Mathf.Max(2, Mathf.FloorToInt(period - 0.5f));
            float fraction = period - 0.5f - length;
            float allpass = (1f - fraction) / (1f + fraction);

            // Higher notes die away faster, like real strings.
            float octavesUp = Mathf.Log(frequency / Frequency(OpenStrings[0]), 2f);
            float decaySeconds = Mathf.Lerp(k_LowDecaySeconds, k_HighDecaySeconds, Mathf.Clamp01(octavesUp / 2f));
            float loopGain = Mathf.Pow(10f, -3f / (decaySeconds * frequency));
            loopGain = Mathf.Min(0.9999f, loopGain / Mathf.Cos(Mathf.PI * frequency / sampleRate));

            // Excitation: soft noise, low-passed for a warmer pick, with the comb a pick makes by
            // plucking away from the bridge.
            var random = new System.Random(seed);
            var delay = new float[length];
            float smooth = 0f, smoother = 0f;
            for (int i = 0; i < length; i++)
            {
                smooth += 0.3f * ((float)random.NextDouble() * 2f - 1f - smooth);
                smoother += 0.3f * (smooth - smoother);
                delay[i] = smoother;
            }
            int pickOffset = Mathf.Clamp(Mathf.RoundToInt(length * k_PickPosition), 1, length - 1);
            var excitation = (float[])delay.Clone();
            for (int i = 0; i < length; i++)
                delay[i] = excitation[i] - excitation[(i + length - pickOffset) % length];

            int index = 0;
            float previous = 0f;
            float allpassIn = 0f, allpassOut = 0f;
            float dcIn = 0f, dcOut = 0f;
            float peak = 1e-6f;
            for (int n = 0; n < output.Length; n++)
            {
                float current = delay[index];
                float averaged = loopGain * 0.5f * (current + previous);
                previous = current;

                float tuned = allpass * averaged + allpassIn - allpass * allpassOut;
                allpassIn = averaged;
                allpassOut = tuned;
                delay[index] = tuned;
                index = (index + 1) % length;

                // Remove any DC offset so the note doesn't click when it stops.
                dcOut = current - dcIn + 0.995f * dcOut;
                dcIn = current;
                output[n] = dcOut;
                peak = Mathf.Max(peak, Mathf.Abs(dcOut));
            }

            int fadeSamples = Mathf.RoundToInt(k_FadeOut * sampleRate);
            float gain = k_Peak / peak;
            for (int n = 0; n < output.Length; n++)
            {
                float fade = Mathf.Clamp01((output.Length - n) / (float)fadeSamples);
                output[n] *= gain * fade;
            }
            return output;
        }
    }
}
