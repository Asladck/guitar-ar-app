using UnityEngine;

namespace GuitarAR
{
    /// <summary>
    /// Smooths a tracked point's jitter without making it lag when it moves fast: the One Euro
    /// filter (Casiez, Roussel and Vogel, CHI 2012). Smoothing is strong while the point is
    /// nearly still and fades away as it speeds up. Positions in screen pixels; speeds are
    /// counted in screen heights per second.
    /// </summary>
    public class GuitarPointFilter
    {
        readonly float m_MinCutoff;   // Hz, while still.
        readonly float m_Beta;        // How much the cutoff rises with speed.
        readonly float m_SpeedCutoff; // Hz, for the speed itself.

        const float k_MaxPrediction = 0.12f;  // Seconds; a point isn't carried on further ahead than this.

        Vector2 m_Value;
        Vector2 m_Velocity;
        float m_Cutoff;
        double m_Time;
        bool m_HasValue;

        public GuitarPointFilter(float minCutoff, float beta, float speedCutoff)
        {
            m_MinCutoff = minCutoff;
            m_Beta = beta;
            m_SpeedCutoff = speedCutoff;
        }

        public Vector2 Value => m_Value;

        /// <summary>Forgets the point, so the next one is taken as it is.</summary>
        public void Reset() => m_HasValue = false;

        /// <summary>The smoothed point for one measured at a time in seconds.</summary>
        public Vector2 Filter(Vector2 point, double time)
        {
            if (!m_HasValue || time <= m_Time)
            {
                m_HasValue = true;
                m_Value = point;
                m_Velocity = Vector2.zero;
                m_Cutoff = 0f;
                m_Time = time;
                return point;
            }

            float dt = (float)(time - m_Time);
            m_Time = time;
            m_Velocity = Vector2.Lerp(m_Velocity, (point - m_Value) / dt, Alpha(m_SpeedCutoff, dt));
            float speed = m_Velocity.magnitude / Mathf.Max(1, Screen.height);
            m_Cutoff = m_MinCutoff + m_Beta * speed;
            m_Value = Vector2.Lerp(m_Value, point, Alpha(m_Cutoff, dt));
            return m_Value;
        }

        /// <summary>
        /// Where the point is likely to be at a later time: carried on at its speed, to show it
        /// where it is now when it was measured a moment ago, and to make up for the smoothing's
        /// own lag (1 / (2π cutoff) of movement). Only the given share of that is carried on,
        /// since the speed is a guess, and nothing while the point is nearly still (screen heights
        /// per second), where carrying on would only add jitter.
        /// </summary>
        public Vector2 Predict(double time, float share, float stillSpeed, float movingSpeed)
        {
            if (!m_HasValue || m_Cutoff <= 0f)
                return m_Value;
            float speed = m_Velocity.magnitude / Mathf.Max(1, Screen.height);
            float moving = Mathf.InverseLerp(stillSpeed, movingSpeed, speed);
            float ahead = share * (Mathf.Max(0f, (float)(time - m_Time)) + 1f / (2f * Mathf.PI * m_Cutoff));
            return m_Value + m_Velocity * (moving * Mathf.Min(ahead, k_MaxPrediction));
        }

        static float Alpha(float cutoff, float dt) => 1f / (1f + 1f / (2f * Mathf.PI * cutoff * dt));
    }
}
