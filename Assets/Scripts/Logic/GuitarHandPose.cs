using System;

namespace GuitarAR
{
    /// <summary>The 21 hand joints the native plugin reports, in its order (the same as MediaPipe's).</summary>
    public enum HandJoint
    {
        Wrist,
        ThumbCMC, ThumbMP, ThumbIP, ThumbTip,
        IndexMCP, IndexPIP, IndexDIP, IndexTip,
        MiddleMCP, MiddlePIP, MiddleDIP, MiddleTip,
        RingMCP, RingPIP, RingDIP, RingTip,
        LittleMCP, LittlePIP, LittleDIP, LittleTip,
    }

    /// <summary>
    /// One detection of one hand: each joint's position (any 2D units, usually screen pixels) and
    /// how sure the tracker is of it (0..1). Plain C#, so the gesture and strike logic can be
    /// checked outside Unity (Tools/logic-tests).
    /// </summary>
    public class GuitarHandPose
    {
        public const int JointCount = 21;
        public const float MinConfidence = 0.3f;

        public readonly float[] X = new float[JointCount];
        public readonly float[] Y = new float[JointCount];
        public readonly float[] Confidence = new float[JointCount];

        public void Set(HandJoint joint, float x, float y, float confidence)
        {
            X[(int)joint] = x;
            Y[(int)joint] = y;
            Confidence[(int)joint] = confidence;
        }

        public void CopyFrom(GuitarHandPose other)
        {
            Array.Copy(other.X, X, JointCount);
            Array.Copy(other.Y, Y, JointCount);
            Array.Copy(other.Confidence, Confidence, JointCount);
        }

        public bool Seen(HandJoint joint, float minConfidence = MinConfidence) => Confidence[(int)joint] >= minConfidence;

        /// <summary>
        /// The middle of the palm: the wrist and the knuckles at the base of the fingers. False if
        /// the wrist or more than one of those knuckles wasn't seen.
        /// </summary>
        public bool TryPalm(out float x, out float y)
        {
            x = y = 0f;
            if (!Seen(HandJoint.Wrist))
                return false;
            float sx = X[(int)HandJoint.Wrist], sy = Y[(int)HandJoint.Wrist];
            int n = 1;
            for (var j = HandJoint.IndexMCP; j <= HandJoint.LittleMCP; j += 4)
            {
                if (!Seen(j))
                    continue;
                sx += X[(int)j];
                sy += Y[(int)j];
                n++;
            }
            if (n < 4)
                return false;
            x = sx / n;
            y = sy / n;
            return true;
        }

        /// <summary>The hand's size: wrist to the middle finger's knuckle. 0 if not seen.</summary>
        public float Size()
        {
            if (!Seen(HandJoint.Wrist) || !Seen(HandJoint.MiddleMCP))
                return 0f;
            return Distance(HandJoint.Wrist, HandJoint.MiddleMCP);
        }

        public float Distance(HandJoint a, HandJoint b)
        {
            float dx = X[(int)a] - X[(int)b], dy = Y[(int)a] - Y[(int)b];
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
