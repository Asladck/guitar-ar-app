using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Keeps the final mix from clipping. A full strum is six strings sounding at once, on top of
    /// the previous strum still ringing; summed, that can go past full scale, which the phone
    /// plays as harsh crackling. Quiet sound passes untouched, loud peaks are rounded off.
    /// Sits next to the AudioListener so it processes everything that's heard.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public class GuitarSoftLimiter : MonoBehaviour
    {
        const float k_Threshold = 0.6f;

        /// <summary>Adds the limiter to the camera's AudioListener if it isn't there yet.</summary>
        public static void EnsureOnListener()
        {
            var cam = Camera.main;
            if (cam == null || cam.GetComponent<AudioListener>() == null)
                return;
            if (cam.GetComponent<GuitarSoftLimiter>() == null)
                cam.gameObject.AddComponent<GuitarSoftLimiter>();
        }

        // Runs on the audio thread.
        void OnAudioFilterRead(float[] data, int channels)
        {
            const float headroom = 1f - k_Threshold;
            for (int i = 0; i < data.Length; i++)
            {
                float x = data[i];
                float magnitude = x < 0f ? -x : x;
                if (magnitude <= k_Threshold)
                    continue;
                // Above the threshold, ease towards full scale instead of cutting off at it.
                float over = (magnitude - k_Threshold) / headroom;
                float shaped = k_Threshold + headroom * (over / (1f + over));
                data[i] = x < 0f ? -shaped : shaped;
            }
        }
    }
}
