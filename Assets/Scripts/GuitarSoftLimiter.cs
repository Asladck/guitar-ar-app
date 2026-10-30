using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Keeps the final mix from clipping. A full strum is six strings sounding at once, on top of
    /// the previous strum still ringing; summed, that can go past full scale, which the phone
    /// plays as harsh crackling. When the sound gets too loud, its level is turned down at once
    /// and back up slowly; the waveform itself isn't bent, so a chord stays clean.
    /// Sits next to the AudioListener so it processes everything that's heard.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public class GuitarSoftLimiter : MonoBehaviour
    {
        const float k_Ceiling = 0.9f;
        const float k_ReleaseSeconds = 0.08f;

        float m_Envelope;
        float m_Release = 0.9997f;

        /// <summary>Adds the limiter to the camera's AudioListener if it isn't there yet.</summary>
        public static void EnsureOnListener()
        {
            var cam = Camera.main;
            if (cam == null || cam.GetComponent<AudioListener>() == null)
                return;
            if (cam.GetComponent<GuitarSoftLimiter>() == null)
                cam.gameObject.AddComponent<GuitarSoftLimiter>();
        }

        void Awake() => m_Release = Mathf.Exp(-1f / (k_ReleaseSeconds * AudioSettings.outputSampleRate));

        // Runs on the audio thread.
        void OnAudioFilterRead(float[] data, int channels)
        {
            for (int i = 0; i < data.Length; i += channels)
            {
                // The loudest channel sets the level for all, so the stereo picture holds.
                float peak = 0f;
                for (int c = 0; c < channels; c++)
                    peak = Mathf.Max(peak, Mathf.Abs(data[i + c]));
                m_Envelope = Mathf.Max(peak, m_Envelope * m_Release);
                if (m_Envelope <= k_Ceiling)
                    continue;
                float gain = k_Ceiling / m_Envelope;
                for (int c = 0; c < channels; c++)
                    data[i + c] *= gain;
            }
        }
    }
}
