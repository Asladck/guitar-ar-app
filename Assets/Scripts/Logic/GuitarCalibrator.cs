using System;
using System.Collections.Generic;

namespace GuitarAR
{
    /// <summary>
    /// The short calibration: show both hands for a moment (the strumming hand's jitter is
    /// measured meanwhile), then make a few comfortable swings over the strings (their size,
    /// speed and middle are measured). From that come the strike settings: the band just wider
    /// than the jitter, the minimum swing and speed a fair share of the player's own, and the
    /// strike line in the middle of where they swing. Positions in guitar lengths, as for
    /// <see cref="GuitarStrikeDetector"/>.
    /// </summary>
    public class GuitarCalibrator
    {
        public enum Phase { ShowHands, Swing, Done, Failed }

        public const int SwingsWanted = 4;
        public const int SwingsNeeded = 3;
        const float k_ShowSeconds = 1f;
        const float k_TimeoutSeconds = 20f;

        readonly List<float> m_Residuals = new List<float>();
        readonly List<float> m_Amplitudes = new List<float>();
        readonly List<float> m_Speeds = new List<float>();
        readonly List<float> m_Middles = new List<float>();

        double m_Begin = double.NaN;
        double m_BothSince = double.NaN;
        float m_D1 = float.NaN, m_D2 = float.NaN;  // The last two positions, for the jitter.
        float m_Noise;

        // Swing tracking: the current extreme and which way the hand is going.
        bool m_HasExtreme;
        float m_Extreme;
        int m_Heading;
        float m_LastTurn = float.NaN;
        float m_PeakSpeed;
        double m_LastTime = double.NaN;
        float m_LastD;

        public Phase Current { get; private set; } = Phase.ShowHands;
        public int Swings => m_Amplitudes.Count;
        public float Noise => m_Noise;
        public StrikeSettings Result { get; private set; }

        /// <summary>
        /// A sample: the strumming hand's position (if it's seen) and whether the chord hand is
        /// seen too, at a time in seconds.
        /// </summary>
        public void Sample(double time, bool strumSeen, float d, bool chordSeen, StrikeSettings defaults)
        {
            if (Current == Phase.Done || Current == Phase.Failed)
                return;
            if (double.IsNaN(m_Begin))
                m_Begin = time;
            if (time - m_Begin > k_TimeoutSeconds)
            {
                if (Current == Phase.Swing && Swings >= SwingsNeeded)
                    Finish(defaults);
                else
                    Current = Phase.Failed;
                return;
            }

            if (Current == Phase.ShowHands)
            {
                if (!strumSeen || !chordSeen)
                {
                    m_BothSince = double.NaN;
                    m_D1 = m_D2 = float.NaN;
                    m_Residuals.Clear();
                    return;
                }
                if (double.IsNaN(m_BothSince))
                    m_BothSince = time;
                // Jitter: how far each position is from the middle of its neighbours.
                if (!float.IsNaN(m_D2))
                    m_Residuals.Add(m_D1 - 0.5f * (m_D2 + d));
                m_D2 = m_D1;
                m_D1 = d;
                if (time - m_BothSince >= k_ShowSeconds && m_Residuals.Count >= 8)
                {
                    float sum = 0f;
                    foreach (var r in m_Residuals)
                        sum += r * r;
                    m_Noise = (float)Math.Sqrt(sum / m_Residuals.Count / 1.5f);
                    Current = Phase.Swing;
                    m_Begin = time;
                }
                return;
            }

            if (!strumSeen)
            {
                m_LastTime = double.NaN;
                return;
            }
            if (!double.IsNaN(m_LastTime) && time > m_LastTime)
                m_PeakSpeed = Math.Max(m_PeakSpeed, Math.Abs(d - m_LastD) / (float)(time - m_LastTime));
            m_LastTime = time;
            m_LastD = d;

            float turnBack = Math.Max(4f * m_Noise, 0.02f);
            if (!m_HasExtreme)
            {
                m_HasExtreme = true;
                m_Extreme = d;
                m_Heading = 0;
                return;
            }
            if (m_Heading == 0)
            {
                if (Math.Abs(d - m_Extreme) >= turnBack)
                    m_Heading = Math.Sign(d - m_Extreme);
                else
                    return;
            }
            if ((d - m_Extreme) * m_Heading > 0f)
            {
                m_Extreme = d;
                return;
            }
            if ((m_Extreme - d) * m_Heading < turnBack)
                return;

            // Turned round at m_Extreme: the swing since the last turn is done.
            if (!float.IsNaN(m_LastTurn))
            {
                float amplitude = Math.Abs(m_Extreme - m_LastTurn);
                if (amplitude >= Math.Max(6f * m_Noise, 0.04f))
                {
                    m_Amplitudes.Add(amplitude);
                    m_Speeds.Add(m_PeakSpeed);
                    m_Middles.Add(0.5f * (m_Extreme + m_LastTurn));
                }
            }
            m_LastTurn = m_Extreme;
            m_Extreme = d;
            m_Heading = -m_Heading;
            m_PeakSpeed = 0f;
            if (Swings >= SwingsWanted)
                Finish(defaults);
        }

        void Finish(StrikeSettings defaults)
        {
            float amplitude = Median(m_Amplitudes);
            float speed = Median(m_Speeds);
            var settings = defaults.Clone();
            settings.Band = Clamp(Math.Max(4f * m_Noise, 0.12f * amplitude), 0.02f, 0.08f);
            settings.MinTravel = Clamp(0.4f * amplitude, 0.05f, 0.25f);
            settings.MinSpeed = Clamp(0.3f * speed, 0.25f, 1.5f);
            settings.LineOffset = Clamp(Median(m_Middles), -0.15f, 0.15f);
            Result = settings;
            Current = Phase.Done;
        }

        static float Clamp(float x, float min, float max) => Math.Max(min, Math.Min(max, x));

        static float Median(List<float> values)
        {
            var sorted = new List<float>(values);
            sorted.Sort();
            int n = sorted.Count;
            if (n == 0)
                return 0f;
            return n % 2 == 1 ? sorted[n / 2] : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);
        }
    }
}
