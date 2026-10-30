using System;

namespace GuitarAR
{
    /// <summary>
    /// The One Euro filter (Casiez, Roussel and Vogel, CHI 2012) for a 2D point, with the real time
    /// between samples: strong smoothing while the point is nearly still, less as it speeds up, so
    /// jitter goes but a quick swing doesn't lag. Cutoffs in Hz; Beta per unit of speed, in
    /// whatever units the points are in (the strike detector uses guitar lengths per second).
    /// </summary>
    public class GuitarOneEuroFilter
    {
        public float MinCutoff;
        public float Beta;
        public float SpeedCutoff;

        float m_X, m_Y, m_SpeedX, m_SpeedY;
        double m_Time;
        bool m_HasValue;

        public GuitarOneEuroFilter(float minCutoff, float beta, float speedCutoff)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            SpeedCutoff = speedCutoff;
        }

        public bool HasValue => m_HasValue;

        /// <summary>Forgets the history: the next point is taken as it is.</summary>
        public void Reset() => m_HasValue = false;

        public void Filter(float x, float y, double time, out float outX, out float outY)
        {
            if (!m_HasValue || time <= m_Time)
            {
                m_HasValue = true;
                m_X = x;
                m_Y = y;
                m_SpeedX = m_SpeedY = 0f;
                m_Time = time;
                outX = x;
                outY = y;
                return;
            }

            float dt = (float)(time - m_Time);
            m_Time = time;
            float a = Alpha(SpeedCutoff, dt);
            m_SpeedX += a * ((x - m_X) / dt - m_SpeedX);
            m_SpeedY += a * ((y - m_Y) / dt - m_SpeedY);
            float speed = (float)Math.Sqrt(m_SpeedX * m_SpeedX + m_SpeedY * m_SpeedY);
            float b = Alpha(MinCutoff + Beta * speed, dt);
            m_X += b * (x - m_X);
            m_Y += b * (y - m_Y);
            outX = m_X;
            outY = m_Y;
        }

        static float Alpha(float cutoff, float dt) => 1f / (1f + 1f / (2f * (float)Math.PI * cutoff * dt));
    }
}
