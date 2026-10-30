using System.Threading;
using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Ends one string voice's note smoothly at a set moment on the audio clock. Stopping a voice
    /// cuts its waveform off mid-swing, which clicks; a few milliseconds of fade don't, and still
    /// sound like the string being stopped by the next stroke. Sits next to the voice's AudioSource.
    /// </summary>
    public class GuitarVoiceFade : MonoBehaviour
    {
        public const float Seconds = 0.012f;

        double m_FadeStart = double.PositiveInfinity;  // AudioSettings.dspTime; read on the audio thread.
        float m_FadeSeconds = Seconds;
        int m_Rate = 48000;

        /// <summary>Fades the note out from this moment (AudioSettings.dspTime), over the given seconds.</summary>
        public void FadeOutAt(double dspTime, float seconds = Seconds)
        {
            Volatile.Write(ref m_FadeSeconds, Mathf.Max(0.001f, seconds));
            Interlocked.Exchange(ref m_FadeStart, dspTime);
        }

        /// <summary>Plays the next note in full.</summary>
        public void Cancel() => Interlocked.Exchange(ref m_FadeStart, double.PositiveInfinity);

        void Awake() => m_Rate = AudioSettings.outputSampleRate;

        // Runs on the audio thread.
        void OnAudioFilterRead(float[] data, int channels)
        {
            double fadeStart = Volatile.Read(ref m_FadeStart);
            if (double.IsPositiveInfinity(fadeStart))
                return;

            double bufferStart = AudioSettings.dspTime;
            float seconds = Volatile.Read(ref m_FadeSeconds);
            int frames = data.Length / channels;
            for (int n = 0; n < frames; n++)
            {
                double t = bufferStart + n / (double)m_Rate - fadeStart;
                if (t <= 0.0)
                    continue;
                float gain = t >= seconds ? 0f : 0.5f + 0.5f * Mathf.Cos((float)(t / seconds) * Mathf.PI);
                for (int c = 0; c < channels; c++)
                    data[n * channels + c] *= gain;
            }
        }
    }
}
