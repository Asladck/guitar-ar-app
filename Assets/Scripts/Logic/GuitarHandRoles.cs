namespace GuitarAR
{
    /// <summary>
    /// Which detected hand strums and which picks the chord. The tracker says whether each hand
    /// is a left or right one as the camera sees it; the front camera shows the player mirrored,
    /// so there it's the other way round. A left-handed player strums with the left hand, and the
    /// roles can also be swapped by hand. When the tracker can't tell the hands apart, the one
    /// nearer the guitar's body strums and the one up the neck picks the chord.
    /// </summary>
    public static class GuitarHandRoles
    {
        public const int Left = -1, Right = 1, Unknown = 0;

        /// <summary>Which of the player's own hands strums: Right or Left.</summary>
        public static int StrummingHand(bool leftHanded, bool swapped) => leftHanded != swapped ? Left : Right;

        /// <summary>
        /// Picks the strumming and chord hands (indices, -1 if none) from count hands, each with the
        /// tracker's chirality and where it is along the strings (0 bridge .. 1 nut; NaN if unknown).
        /// </summary>
        public static void Assign(int count, int[] chirality, float[] along, bool frontCamera, bool leftHanded, bool swapped,
            out int strum, out int chord)
        {
            strum = chord = -1;
            int strumming = StrummingHand(leftHanded, swapped);
            int mirror = frontCamera ? -1 : 1;

            int known = 0;
            for (int h = 0; h < count; h++)
            {
                int own = chirality[h] * mirror;
                if (own == Unknown)
                    continue;
                known++;
                if (own == strumming && strum < 0)
                    strum = h;
                else if (own == -strumming && chord < 0)
                    chord = h;
            }
            if (count == known && (count < 2 || (strum >= 0 && chord >= 0)))
                return;

            // The tracker couldn't tell (or saw two of the same hand): go by place on the guitar.
            strum = chord = -1;
            if (count == 1)
            {
                if (float.IsNaN(along[0]) || along[0] < 0.6f)
                    strum = 0;
                else
                    chord = 0;
                return;
            }
            if (count >= 2)
            {
                bool firstNearer = float.IsNaN(along[1]) || (!float.IsNaN(along[0]) && along[0] <= along[1]);
                strum = firstNearer ? 0 : 1;
                chord = firstNearer ? 1 : 0;
            }
        }
    }
}
