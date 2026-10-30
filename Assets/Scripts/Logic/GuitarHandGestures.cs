using System;

namespace GuitarAR
{
    /// <summary>What a hand shows. One to four fingers pick a chord; a fist stops the music.</summary>
    public enum HandGesture { Unknown, OneFinger, TwoFingers, ThreeFingers, FourFingers, Fist }

    public enum FingerState { Unknown, Straight, Bent }

    /// <summary>
    /// Reads gestures from a hand's joints. A finger is straight when its joints are nearly in line
    /// (the angles at its middle and end knuckles) and its tip reaches well past its knuckle, seen
    /// from the wrist; bent when it's clearly curled or its tip is back at the knuckle or nearer
    /// the wrist; anything in between is unknown, so
    /// a half-raised finger never picks a chord. The thumb isn't counted. Only where the joints
    /// are relative to each other matters, so it works whichever way the hand is turned.
    /// </summary>
    public static class GuitarHandGestures
    {
        // Degrees of bend (0 = straight) at the middle knuckle, plus half of it at the end knuckle.
        public static float StraightBend = 40f;
        public static float BentBend = 70f;
        // How much farther from the wrist the fingertip is than its knuckle, in hand sizes (wrist
        // to middle knuckle): about 0.6-0.75 for a straight finger, below 0 for a curled one.
        public static float StraightReach = 0.3f;
        public static float BentReach = 0.1f;

        static readonly HandJoint[] k_Mcp = { HandJoint.IndexMCP, HandJoint.MiddleMCP, HandJoint.RingMCP, HandJoint.LittleMCP };

        /// <summary>The state of finger f (0 index .. 3 little).</summary>
        public static FingerState Finger(GuitarHandPose pose, int f, float size)
        {
            var mcp = k_Mcp[f];
            var pip = mcp + 1;
            var dip = mcp + 2;
            var tip = mcp + 3;
            if (!pose.Seen(mcp) || !pose.Seen(pip) || !pose.Seen(tip))
                return FingerState.Unknown;

            float bend = Angle(pose, mcp, pip, pose.Seen(dip) ? dip : tip);
            if (pose.Seen(dip))
                bend += 0.5f * Angle(pose, pip, dip, tip);
            float reach = (pose.Distance(tip, HandJoint.Wrist) - pose.Distance(mcp, HandJoint.Wrist)) / size;

            if (bend <= StraightBend && reach >= StraightReach)
                return FingerState.Straight;
            if (bend >= BentBend || reach <= BentReach)
                return FingerState.Bent;
            return FingerState.Unknown;
        }

        /// <summary>The gesture this pose shows, or Unknown.</summary>
        public static HandGesture Read(GuitarHandPose pose)
        {
            float size = pose.Size();
            if (size <= 1e-4f)
                return HandGesture.Unknown;

            int straight = 0;
            int bentFrom = -1;  // The first bent finger: those after it must all be bent too.
            for (int f = 0; f < 4; f++)
            {
                var state = Finger(pose, f, size);
                if (state == FingerState.Unknown)
                    return HandGesture.Unknown;
                if (state == FingerState.Straight)
                {
                    if (bentFrom >= 0)
                        return HandGesture.Unknown;  // E.g. index down, middle up: not one of ours.
                    straight++;
                }
                else if (bentFrom < 0)
                {
                    bentFrom = f;
                }
            }
            switch (straight)
            {
                case 0: return HandGesture.Fist;
                case 1: return HandGesture.OneFinger;
                case 2: return HandGesture.TwoFingers;
                case 3: return HandGesture.ThreeFingers;
                default: return HandGesture.FourFingers;
            }
        }

        /// <summary>The bend at joint b of a-b-c in degrees: 0 when a, b and c are in line.</summary>
        static float Angle(GuitarHandPose pose, HandJoint a, HandJoint b, HandJoint c)
        {
            float x1 = pose.X[(int)b] - pose.X[(int)a], y1 = pose.Y[(int)b] - pose.Y[(int)a];
            float x2 = pose.X[(int)c] - pose.X[(int)b], y2 = pose.Y[(int)c] - pose.Y[(int)b];
            float l1 = (float)Math.Sqrt(x1 * x1 + y1 * y1), l2 = (float)Math.Sqrt(x2 * x2 + y2 * y2);
            if (l1 < 1e-6f || l2 < 1e-6f)
                return 0f;
            float cos = Math.Max(-1f, Math.Min(1f, (x1 * x2 + y1 * y2) / (l1 * l2)));
            return (float)(Math.Acos(cos) * 180.0 / Math.PI);
        }
    }

    /// <summary>
    /// Confirms a gesture only after it has been shown steadily for a while. An unknown or
    /// changing gesture starts the wait over, and the last confirmed gesture stays meanwhile,
    /// also while the hand is lost.
    /// </summary>
    public class GuitarGestureHold
    {
        public float HoldSeconds;

        HandGesture m_Candidate = HandGesture.Unknown;
        double m_Since;

        public GuitarGestureHold(float holdSeconds) => HoldSeconds = holdSeconds;

        public HandGesture Confirmed { get; private set; } = HandGesture.Unknown;

        /// <summary>The gesture shown now, if it's been held for long enough; else Unknown.</summary>
        public HandGesture Update(HandGesture shown, double time)
        {
            if (shown == HandGesture.Unknown)
            {
                m_Candidate = HandGesture.Unknown;
                return HandGesture.Unknown;
            }
            if (shown != m_Candidate)
            {
                m_Candidate = shown;
                m_Since = time;
            }
            if (time - m_Since >= HoldSeconds - 1e-6)
            {
                Confirmed = shown;
                return shown;
            }
            return HandGesture.Unknown;
        }

        /// <summary>How long the gesture shown now has been held, in seconds.</summary>
        public float Held(double time) => m_Candidate == HandGesture.Unknown ? 0f : (float)(time - m_Since);

        /// <summary>The hand was lost: the wait starts over, the confirmed gesture stays.</summary>
        public void Lost() => m_Candidate = HandGesture.Unknown;
    }
}
