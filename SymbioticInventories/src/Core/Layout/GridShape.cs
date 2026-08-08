using System;

namespace SymbioticInventories.Core.Layout
{
    /// <summary>
    /// Decides how many columns a given slot count should be drawn at.
    ///
    /// This is the smallest decision in the layout system and the one that most affects how
    /// "right" the window looks, because a container the player already knows has a shape in
    /// their head. The rules, in order:
    ///
    ///   1. Prefer an *exact* divisor of the slot count, so the last row is never ragged. A
    ///      ragged final row is the single most obvious sign of a generated layout.
    ///   2. Among exact divisors, pick the one whose resulting aspect ratio sits closest to
    ///      the target - slightly wider than square, because screens are wider than tall and
    ///      a wide block scans faster than a tall one.
    ///   3. Only if no divisor lands in the allowed range, fall back to a near-square guess
    ///      and accept the ragged row.
    /// </summary>
    public static class GridShape
    {
        /// <summary>Wider than square. 1.6 keeps a 32-slot chest at 8x4 rather than 6x6.</summary>
        public const double TargetAspect = 1.6;

        /// <summary>
        /// Columns to draw <paramref name="slots"/> in, constrained to [minCols, maxCols].
        ///
        /// <paramref name="preferWidest"/> switches the tie-break from "nicest proportions" to
        /// "use all the width you are given". Both are needed, and neither is right on its own:
        /// in a wide window a 16-slot bag reads best as 4x4, but pinned to the left edge in an
        /// 8-wide column that same 4x4 wastes half the column and doubles the height. The
        /// packer tries both and scores the results rather than guessing.
        /// </summary>
        public static int ChooseColumns(int slots, int minCols, int maxCols, bool preferWidest = false)
        {
            if (slots <= 0) return 1;

            // Tiny sections (the four worn-bag slots, a 3x3 crafting grid) read best as a
            // single row; splitting them helps nothing.
            if (slots <= minCols) return slots;

            int lo = Math.Max(1, minCols);
            int hi = Math.Max(lo, maxCols);

            int best = -1;
            double bestScore = double.MaxValue;

            for (int cols = lo; cols <= hi; cols++)
            {
                if (slots % cols != 0) continue;
                int rows = slots / cols;
                // Widest-first still walks divisors, so the block stays solid either way; only
                // the preference order changes.
                double score = preferWidest ? -cols : AspectPenalty(cols, rows);
                if (score < bestScore) { bestScore = score; best = cols; }
            }

            if (best > 0) return best;

            // No exact divisor in range: near-square, biased wide, then clamped.
            int guess = (int)Math.Round(Math.Sqrt(slots * TargetAspect));
            return Math.Clamp(guess, lo, hi);
        }

        /// <summary>
        /// How far a cols x rows block sits from the target aspect, measured in log space so
        /// that "twice too wide" and "twice too tall" are penalised equally. Comparing raw
        /// ratios would quietly favour wide blocks, since being too wide is unbounded above
        /// while being too tall is squeezed into 0..1.
        /// </summary>
        private static double AspectPenalty(int cols, int rows)
            => Math.Abs(Math.Log((double)cols / rows) - Math.Log(TargetAspect));
    }
}
