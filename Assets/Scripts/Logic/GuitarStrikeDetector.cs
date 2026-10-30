using System;

namespace GuitarAR
{
    public enum StrikeDirection { Down, Up }

    /// <summary>The strike detector's states; see <see cref="GuitarStrikeDetector"/>.</summary>
    public enum StrikeState { NoHand, Settling, Ready, Crossing, Rearming }

    /// <summary>
    /// Where the strike line is and what a swing over it must be like. Distances are in guitar
    /// lengths (the strings' length on screen, so the numbers don't change with how close the
    /// player stands); times in seconds.
    /// </summary>
    public class StrikeSettings
    {
        /// <summary>Where the line sits across the strings: 0 = between the middle strings, + towards the high E.</summary>
        public float LineOffset = 0f;
        /// <summary>Half the width of the band around the line: a swing starts beyond one edge and ends beyond the other.</summary>
        public float Band = 0.035f;
        /// <summary>The least a swing travels, from where it started to the far edge of the band.</summary>
        public float MinTravel = 0.1f;
        /// <summary>The least speed across the band, guitar lengths per second.</summary>
        public float MinSpeed = 0.5f;
        /// <summary>The least time between two strikes. 90 ms leaves room for sixteenths at 160 BPM (94 ms).</summary>
        public float MinInterval = 0.09f;
        /// <summary>Where along the strings the line counts: 0 = bridge, 1 = nut; past the bridge is where people strum.</summary>
        public float AlongMin = -0.45f;
        public float AlongMax = 0.65f;
        /// <summary>A longer gap between samples is a lost hand: the movement's history starts over.</summary>
        public float MaxGap = 0.2f;
        /// <summary>Samples a new history needs before a swing can start.</summary>
        public int SettleSamples = 2;
        /// <summary>A hand that stays in the band this long is waiting there, not crossing.</summary>
        public float MaxCrossingSeconds = 0.5f;

        public StrikeSettings Clone() => (StrikeSettings)MemberwiseClone();

        /// <summary>How far past the band the hand must go after a strike before the next one, unless it turns back first.</summary>
        public float RearmDistance => Band + 0.5f * MinTravel;

        /// <summary>How far the hand must come back to count as turning round.</summary>
        public float TurnBack => Math.Max(0.5f * Band, 0.015f);
    }

    public struct Strike
    {
        public StrikeDirection Direction;
        public double Time;          // When it crossed the line (camera clock, between samples).
        public double DetectedTime;  // The sample that confirmed it.
        public float Speed;          // Guitar lengths per second, across the band.
        public float Travel;         // Guitar lengths, from the swing's start.
        public float Strength;       // 0..1, from the speed.
    }

    /// <summary>
    /// Finds strikes in the strumming hand's movement, in the guitar's own coordinates: across
    /// the strings (d, + towards the high E) and along them (v). A strike is the hand passing
    /// over the strike line, judged on the segment between consecutive samples, so a quick swing
    /// that jumps over the line between two camera frames still counts.
    ///
    /// States: NoHand (no history) → Settling (a fresh history, waiting to be clear of the band)
    /// → Ready (on one side, the swing's start tracked) → Crossing (inside the band) → a strike or
    /// a rejection → Rearming (the hand carries on past the band; only once it has gone far
    /// enough, or turned round, is it Ready for the swing back). Hysteresis comes from the band:
    /// jitter smaller than it can't cross. One swing gives at most one strike, whatever the frame
    /// rate; the minimum interval is a last guard, not the main one.
    /// </summary>
    public class GuitarStrikeDetector
    {
        public StrikeSettings Settings;

        StrikeState m_State = StrikeState.NoHand;
        int m_Side;               // The side the hand is on or came from: -1 low E side, 1 high E side.
        int m_Samples;
        double m_LastTime;
        float m_LastD, m_LastV;
        float m_StartD;
        double m_EntryTime;       // When it entered the band.
        float m_LineV = float.NaN;
        double m_LineTime;
        float m_ExtremeD;         // While rearming, the farthest the hand went.
        double m_LastStrikeTime = double.NegativeInfinity;

        public GuitarStrikeDetector(StrikeSettings settings) => Settings = settings;

        public StrikeState State => m_State;

        /// <summary>Why the last movement that came near a strike didn't make one, for the diagnostics.</summary>
        public string LastRejection { get; private set; } = "-";

        public Strike LastStrike { get; private set; }

        /// <summary>The hand isn't seen, or its position can't be trusted: forget the movement at once.</summary>
        public void Lost(string reason)
        {
            if (m_State != StrikeState.NoHand)
                LastRejection = reason;
            m_State = StrikeState.NoHand;
            m_Samples = 0;
        }

        /// <summary>
        /// A new sample of the hand: d across the strings and v along them, at a time in seconds.
        /// True if it completed a strike (then see <see cref="LastStrike"/>).
        /// </summary>
        public bool Process(double time, float d, float v)
        {
            d -= Settings.LineOffset;
            if (m_State != StrikeState.NoHand)
            {
                if (time <= m_LastTime)
                    return false;
                if (time - m_LastTime > Settings.MaxGap)
                    Lost("перерыв в трекинге");
            }

            if (m_State == StrikeState.NoHand)
            {
                m_State = StrikeState.Settling;
                m_Samples = 1;
                Store(time, d, v);
                return false;
            }

            double t0 = m_LastTime;
            float d0 = m_LastD, v0 = m_LastV;
            Store(time, d, v);
            TrackLine(t0, d0, v0, time, d, v);
            float band = Settings.Band;

            switch (m_State)
            {
                case StrikeState.Settling:
                    m_Samples++;
                    if (m_Samples >= Settings.SettleSamples && Math.Abs(d) > band)
                        Arm(Math.Sign(d), time, d);
                    return false;

                case StrikeState.Rearming:
                    if (d * m_Side < -band)
                    {
                        // Back across without a swing out first: nothing to play.
                        LastRejection = "без замаха";
                        m_Side = -m_Side;
                        m_ExtremeD = d;
                        return false;
                    }
                    if (d * m_Side > m_ExtremeD * m_Side)
                        m_ExtremeD = d;
                    bool farEnough = m_ExtremeD * m_Side >= Settings.RearmDistance;
                    bool turned = m_ExtremeD * m_Side > band && (m_ExtremeD - d) * m_Side >= Settings.TurnBack;
                    if (!farEnough && !turned)
                        return false;
                    // Ready for the swing back, from the farthest point.
                    m_State = StrikeState.Ready;
                    m_StartD = m_ExtremeD;
                    if (!turned)
                        return false;
                    goto case StrikeState.Ready;

                case StrikeState.Ready:
                {
                    int s = m_Side;
                    if (d * s > m_StartD * s)
                    {
                        m_StartD = d;
                    }
                    if (d * s > band)
                        return false;  // Still on its side.
                    double near = Cross(t0, d0, time, d, s * band);
                    if (d * s < -band)
                        return Judge(near, Cross(t0, d0, time, d, -s * band), time, d);  // Over the whole band in one step.
                    m_State = StrikeState.Crossing;
                    m_EntryTime = near;
                    return false;
                }

                case StrikeState.Crossing:
                {
                    int s = m_Side;
                    if (d * s < -band)
                        return Judge(m_EntryTime, Cross(t0, d0, time, d, -s * band), time, d);
                    if (d * s > band)
                    {
                        LastRejection = "вернулась назад";
                        Arm(s, time, d);
                        return false;
                    }
                    if (time - m_EntryTime > Settings.MaxCrossingSeconds)
                    {
                        LastRejection = "стоит у струн";
                        m_State = StrikeState.Settling;
                        m_Samples = Settings.SettleSamples;
                    }
                    return false;
                }
            }
            return false;
        }

        void Store(double time, float d, float v)
        {
            m_LastTime = time;
            m_LastD = d;
            m_LastV = v;
        }

        void Arm(int side, double time, float d)
        {
            m_State = StrikeState.Ready;
            m_Side = side;
            m_StartD = d;
            m_LineV = float.NaN;
        }

        /// <summary>Remembers where along the strings the hand crossed the line.</summary>
        void TrackLine(double t0, float d0, float v0, double t1, float d1, float v1)
        {
            if ((d0 <= 0f) == (d1 <= 0f) || d0 == d1)
                return;
            float k = d0 / (d0 - d1);
            m_LineV = v0 + (v1 - v0) * k;
            m_LineTime = t0 + (t1 - t0) * k;
        }

        /// <summary>When the segment from (t0, d0) to (t1, d1) reaches d = at.</summary>
        static double Cross(double t0, float d0, double t1, float d1, float at)
        {
            if (d1 == d0)
                return t1;
            double k = Math.Max(0.0, Math.Min(1.0, (at - d0) / (double)(d1 - d0)));
            return t0 + (t1 - t0) * k;
        }

        /// <summary>The hand went over the band, entering it at near and leaving at far: a strike, or why not.</summary>
        bool Judge(double near, double far, double time, float d)
        {
            int s = m_Side;
            var settings = Settings;
            float travel = (-s * settings.Band - m_StartD) * -s;
            float speed = 2f * settings.Band / (float)Math.Max(far - near, 1e-3);
            double lineTime = float.IsNaN(m_LineV) ? far : m_LineTime;

            string rejection = null;
            if (float.IsNaN(m_LineV) || m_LineV < settings.AlongMin || m_LineV > settings.AlongMax)
                rejection = "мимо струн";
            else if (travel < settings.MinTravel)
                rejection = "короткий взмах";
            else if (speed < settings.MinSpeed)
                rejection = "медленно";
            else if (lineTime - m_LastStrikeTime < settings.MinInterval)
                rejection = "слишком часто";

            // Either way the hand is now past the band, on the other side.
            m_State = StrikeState.Rearming;
            m_Side = -s;
            m_ExtremeD = d;
            m_LineV = float.NaN;

            if (rejection != null)
            {
                LastRejection = rejection;
                return false;
            }

            m_LastStrikeTime = lineTime;
            LastStrike = new Strike
            {
                Direction = s < 0 ? StrikeDirection.Down : StrikeDirection.Up,
                Time = lineTime,
                DetectedTime = time,
                Speed = speed,
                Travel = travel,
                Strength = Math.Max(0f, Math.Min(1f, (speed / settings.MinSpeed - 1f) / 3f)),
            };
            return true;
        }
    }
}
