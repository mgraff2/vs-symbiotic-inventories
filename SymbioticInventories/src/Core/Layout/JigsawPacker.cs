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

            // Same slot count, same shape - decided once per pack, before placement. Left to
            // choose per-box, seven identical trunks came out as five different rectangles
            // (3x12 next to 6x6 next to 9x4), and the window read as a jumble even though it
            // packed tight. Identical pieces are what make a jigsaw look like one.
            var uniformShapes = ChooseUniformShapes(boxes, maxWidth, maxCols);

            foreach (var box in boxes)
            {
                // Best placement by lexicographic priority: lowest resulting top edge, then
                // least buried air, then leftmost. Waste matters because a rectangle resting
                // across an uneven skyline permanently entombs every notch beneath it - the
                // pre-waste packer left visible holes under short sections that a same-height
                // placement one notch over would have filled.
                (double top, double waste, double x) bestKey = (double.MaxValue, 0, 0);
                (int cols, int rows, double x, double y) best = default;
                bool found = false;

                foreach (var (cols, rows) in CandidateShapes(box.Section, maxWidth, maxCols, uniformShapes))
                {
                    double w = cols * LayoutMetrics.Cell + LayoutMetrics.BoxGapX;
                    double h = rows * LayoutMetrics.Cell + HeaderOf(box) + LayoutMetrics.BoxGapY;
                    if (w > maxWidth + LayoutMetrics.BoxGapX + 0.001) continue;

                    for (int i = 0; i < skyline.Count; i++)
                    {
                        if (!TryFit(skyline, i, w, out double x, out double y, out double waste)) continue;

                        var key = (top: y + h, waste, x);
                        if (!found
                            || key.top < bestKey.top - 0.001
                            || (Math.Abs(key.top - bestKey.top) <= 0.001 && key.waste < bestKey.waste - 0.001)
                            || (Math.Abs(key.top - bestKey.top) <= 0.001 && Math.Abs(key.waste - bestKey.waste) <= 0.001 && key.x < bestKey.x))
                        {
                            found = true;
                            bestKey = key;
                            best = (cols, rows, x, y);
                        }
                    }
                }

                if (!found)
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

                // An L-shaped section (partial last row) occupies its true silhouette, not
                // its bounding box: the foot columns go the full height, the columns above
                // the notch stop one row short. That notch is now real skyline for later
                // sections to interlock into - the whole point of allowing L shapes.
                int foot = box.Section.SlotCount % best.cols;
                if (foot > 0 && best.rows > 1)
                {
                    double footW = foot * LayoutMetrics.Cell;
                    Occupy(skyline, best.x, footW + LayoutMetrics.BoxGapX,
                        best.y + box.H + LayoutMetrics.BoxGapY);
                    Occupy(skyline, best.x + footW + LayoutMetrics.BoxGapX,
                        box.W - footW, best.y + box.H - LayoutMetrics.Cell + LayoutMetrics.BoxGapY);
                }
                else
                {
                    Occupy(skyline, best.x, box.W + LayoutMetrics.BoxGapX, best.y + box.H + LayoutMetrics.BoxGapY);
                }
            }

            double top = 0;
            foreach (var box in boxes) top = Math.Max(top, box.Y + box.H);
            return top;
        }

        private static double HeaderOf(LayoutBox box) => LayoutMetrics.HeaderH;

        /// <summary>
        /// Picks one rectangle per repeated slot count: the shape whose group packs into the
        /// fewest rows at this width (each row costs the shape's full height), tie-broken
        /// wider. Sections with a unique count keep their per-box flexibility - uniformity
        /// only means something when there are siblings to match.
        /// </summary>
        private static Dictionary<int, (int cols, int rows)> ChooseUniformShapes(
            List<LayoutBox> boxes, double maxWidth, int maxCols)
        {
            var result = new Dictionary<int, (int, int)>();

            var byCount = new Dictionary<int, int>();
            foreach (var b in boxes)
            {
                if (b.Section.FixedColumns > 0 || b.Section.SlotCount <= 0) continue;
                byCount.TryGetValue(b.Section.SlotCount, out int k);
                byCount[b.Section.SlotCount] = k + 1;
            }

            foreach (var (slots, groupSize) in byCount)
            {
                if (groupSize < 2) continue;

                double bestTotal = double.MaxValue;
                (int cols, int rows) bestShape = default;

                foreach (var (cols, rows) in DivisorShapes(slots, maxWidth, maxCols))
                {
                    double w = cols * LayoutMetrics.Cell + LayoutMetrics.BoxGapX;
                    double h = LayoutMetrics.HeaderH + rows * LayoutMetrics.Cell + LayoutMetrics.BoxGapY;

                    int perRow = Math.Max(1, (int)((maxWidth + LayoutMetrics.BoxGapX) / w));
                    double total = Math.Ceiling(groupSize / (double)perRow) * h;

                    // Strictly-better total wins; equal totals go to the wider shape, which
                    // reads better and leaves taller gaps for other sections to slot into.
                    if (total < bestTotal - 0.001 || (Math.Abs(total - bestTotal) <= 0.001 && cols > bestShape.cols))
                    {
                        bestTotal = total;
                        bestShape = (cols, rows);
                    }
                }

                if (bestTotal < double.MaxValue) result[slots] = bestShape;
            }

            return result;
        }

        /// <summary>
        /// Legal rectangles for a section, no ragged rows: exact divisors of the slot count
        /// only, between 2 wide and the cap. A fixed-shape section offers just its true shape;
        /// a section whose slot count has a uniform shape offers exactly that.
        /// </summary>
        private static IEnumerable<(int cols, int rows)> CandidateShapes(
            InventorySection s, double maxWidth, int maxCols,
            Dictionary<int, (int cols, int rows)> uniformShapes)
        {
            int n = s.SlotCount;
            if (n <= 0) yield break;

            int cap = Math.Min(maxCols, Math.Max(1, (int)(maxWidth / LayoutMetrics.Cell)));

            if (s.FixedColumns > 0)
            {
                yield return (Math.Min(s.FixedColumns, cap), (int)Math.Ceiling(n / (double)Math.Min(s.FixedColumns, cap)));
                yield break;
            }

            if (uniformShapes.TryGetValue(n, out var uniform))
            {
                yield return uniform;
                yield break;
            }

            foreach (var shape in DivisorShapes(n, maxWidth, maxCols)) yield return shape;
        }

        /// <summary>
        /// Aspect-legal shapes, widest first. Any width is legal, not just exact divisors:
        /// a non-divisor width makes the last row partial - a left-aligned tetris L - which
        /// the engine's slot grid renders natively and whose notch the skyline lets the next
        /// section interlock into. Only a 1-cell foot is rejected; a lone dangling slot reads
        /// as a rendering error rather than a shape.
        /// </summary>
        private static IEnumerable<(int cols, int rows)> DivisorShapes(int n, double maxWidth, int maxCols)
        {
            int cap = Math.Min(maxCols, Math.Max(1, (int)(maxWidth / LayoutMetrics.Cell)));

            bool any = false;
            for (int c = Math.Min(n, cap); c >= 2; c--)
            {
                int rem = n % c;
                if (rem == 1 && n > c) continue;   // no 1-cell feet
                int r = (int)Math.Ceiling(n / (double)c);

                // Aspect cap: nothing flatter or thinner than 4:1 (bounding box). Without
                // this, the very first box on an empty skyline scores best as its flattest
                // shape - every candidate lands at y=0, so (y+h) rewards minimal h - and a
                // 16-slot bag becomes a 16x1 smear paying a full caption strip for one row.
                if (c > r * 4 || r > c * 4) continue;

                any = true;
                yield return (c, r);
            }
            if (!any) yield return (Math.Min(n, cap), (int)Math.Ceiling(n / (double)Math.Min(n, cap)));
        }

        /// <summary>
        /// Can a rectangle of width w sit with its left edge at segment i? Its resting height
        /// is the max skyline height across its span, and <paramref name="waste"/> is the
        /// buried air under it - skyline notches this placement would seal off forever.
        /// </summary>
        private static bool TryFit(List<Segment> skyline, int i, double w, out double x, out double y, out double waste)
        {
            x = skyline[i].X;
            y = skyline[i].Y;
            waste = 0;

            double remaining = w;
            for (int j = i; j < skyline.Count && remaining > 0.001; j++)
            {
                y = Math.Max(y, skyline[j].Y);
                remaining -= skyline[j].W;
            }
            if (remaining > 0.001) return false;

            // Second pass now that the resting height is known.
            remaining = w;
            for (int j = i; j < skyline.Count && remaining > 0.001; j++)
            {
                double covered = Math.Min(remaining, skyline[j].W);
                waste += (y - skyline[j].Y) * covered;
                remaining -= covered;
            }
            return true;
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
