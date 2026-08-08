using System;
using System.Collections.Generic;

namespace SymbioticInventories.Core.Layout
{
    /// <summary>
    /// Dense 2D packing for the opened-storage region: sections interlock like puzzle pieces
    /// instead of sitting on shelves, so a tall chest and two short bags share a row of the
    /// window rather than each paying for their own.
    ///
    /// Skyline algorithm. The skyline is the staircase-shaped upper edge of everything placed
    /// so far; each section tries all of its legal rectangle shapes at every notch in the
    /// staircase and takes the placement that keeps the skyline lowest. Placement order is
    /// still registry order - the *shapes* flex, the *sequence* does not, so the same set of
    /// open containers always produces the same layout.
    ///
    /// Sections remain solid rectangles throughout. Tetris-style concave pieces would pack
    /// tighter still, but a bag bent around a corner stops being readable as one bag, and
    /// answering "which slots are bag 3?" at a glance is the mod's whole purpose.
    /// </summary>
    public static class JigsawPacker
    {
        /// <summary>One flat segment of the skyline: [X, X+W) at height Y.</summary>
        private struct Segment
        {
            public double X, W, Y;
        }

        /// <summary>
        /// Packs boxes into a strip of the given width. Sets X/Y/Cols/Rows/W/H on every box
        /// and returns the total height used.
        /// </summary>
        public static double Pack(List<LayoutBox> boxes, double maxWidth, int maxCols)
        {
            var skyline = new List<Segment> { new() { X = 0, W = maxWidth, Y = 0 } };

            foreach (var box in boxes)
            {
                double bestScore = double.MaxValue;
                (int cols, int rows, double x, double y) best = default;

                foreach (var (cols, rows) in CandidateShapes(box.Section, maxWidth, maxCols))
                {
                    double w = cols * LayoutMetrics.Cell + LayoutMetrics.BoxGapX;
                    double h = rows * LayoutMetrics.Cell + HeaderOf(box) + LayoutMetrics.BoxGapY;
                    if (w > maxWidth + LayoutMetrics.BoxGapX + 0.001) continue;

                    for (int i = 0; i < skyline.Count; i++)
                    {
                        if (!TryFit(skyline, i, w, out double x, out double y)) continue;

                        // Primary: keep the skyline low. Secondary: hug the left edge, so
                        // equal-height options fill left-to-right instead of scattering.
                        double score = (y + h) * 1000 + x;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = (cols, rows, x, y);
                        }
                    }
                }

                if (bestScore == double.MaxValue)
                {
                    // Nothing fits nowhere only if the strip is narrower than one cell;
                    // place at the top of the skyline rather than losing the section.
                    best = (Math.Max(1, (int)(maxWidth / LayoutMetrics.Cell)), 1, 0, Top(skyline));
                    best.rows = (int)Math.Ceiling(box.Section.SlotCount / (double)best.cols);
                }

                box.Cols = best.cols;
                box.Rows = best.rows;
                box.X = best.x;
                box.Y = best.y;
                box.W = best.cols * LayoutMetrics.Cell;
                box.H = HeaderOf(box) + best.rows * LayoutMetrics.Cell;

                Occupy(skyline, best.x, box.W + LayoutMetrics.BoxGapX, best.y + box.H + LayoutMetrics.BoxGapY);
            }

            double top = 0;
            foreach (var box in boxes) top = Math.Max(top, box.Y + box.H);
            return top;
        }

        private static double HeaderOf(LayoutBox box) => LayoutMetrics.HeaderH;

        /// <summary>
        /// Legal rectangles for a section, no ragged rows: exact divisors of the slot count
        /// only, between 2 wide and the cap. A fixed-shape section offers just its true shape.
        /// </summary>
        private static IEnumerable<(int cols, int rows)> CandidateShapes(InventorySection s, double maxWidth, int maxCols)
        {
            int n = s.SlotCount;
            if (n <= 0) yield break;

            int cap = Math.Min(maxCols, Math.Max(1, (int)(maxWidth / LayoutMetrics.Cell)));

            if (s.FixedColumns > 0)
            {
                yield return (Math.Min(s.FixedColumns, cap), (int)Math.Ceiling(n / (double)Math.Min(s.FixedColumns, cap)));
                yield break;
            }

            bool any = false;
            for (int c = Math.Min(n, cap); c >= 2; c--)
            {
                if (n % c != 0) continue;
                int r = n / c;

                // Aspect cap: nothing flatter or thinner than 4:1. Without this, the very
                // first box on an empty skyline scores best as its flattest shape - every
                // candidate lands at y=0, so (y+h) rewards minimal h - and a 16-slot bag
                // becomes a 16x1 smear paying a full caption strip for one row of slots.
                // Measured on the heavy scenario, unconstrained shapes cost +78 units of
                // height versus the shelf baseline; capped, the jigsaw beats it.
                if (c > r * 4 || r > c * 4) continue;

                any = true;
                yield return (c, r);
            }
            if (!any) yield return (Math.Min(n, cap), (int)Math.Ceiling(n / (double)Math.Min(n, cap)));
        }

        /// <summary>
        /// Can a rectangle of width w sit with its left edge at segment i? Its resting height
        /// is the max skyline height across its span; the wasted area under it is implicit in
        /// the height score, which is what keeps the packer honest without a separate term.
        /// </summary>
        private static bool TryFit(List<Segment> skyline, int i, double w, out double x, out double y)
        {
            x = skyline[i].X;
            y = skyline[i].Y;

            double remaining = w;
            for (int j = i; j < skyline.Count && remaining > 0.001; j++)
            {
                y = Math.Max(y, skyline[j].Y);
                remaining -= skyline[j].W;
            }
            return remaining <= 0.001;
        }

        /// <summary>Flattens the skyline across [x, x+w) to the new height.</summary>
        private static void Occupy(List<Segment> skyline, double x, double w, double newY)
        {
            var result = new List<Segment>();
            double x2 = x + w;

            foreach (var seg in skyline)
            {
                double segEnd = seg.X + seg.W;
                if (segEnd <= x + 0.001 || seg.X >= x2 - 0.001)
                {
                    result.Add(seg);
                    continue;
                }
                if (seg.X < x) result.Add(new Segment { X = seg.X, W = x - seg.X, Y = seg.Y });
                if (segEnd > x2) result.Add(new Segment { X = x2, W = segEnd - x2, Y = seg.Y });
            }

            result.Add(new Segment { X = x, W = w, Y = newY });
            result.Sort((a, b) => a.X.CompareTo(b.X));

            // Merge equal-height neighbours so the segment list stays small.
            var merged = new List<Segment>();
            foreach (var seg in result)
            {
                if (merged.Count > 0 && Math.Abs(merged[^1].Y - seg.Y) < 0.001
                    && Math.Abs(merged[^1].X + merged[^1].W - seg.X) < 0.001)
                {
                    var last = merged[^1];
                    last.W += seg.W;
                    merged[^1] = last;
                }
                else merged.Add(seg);
            }

            skyline.Clear();
            skyline.AddRange(merged);
        }

        private static double Top(List<Segment> skyline)
        {
            double top = 0;
            foreach (var seg in skyline) top = Math.Max(top, seg.Y);
            return top;
        }
    }
}
