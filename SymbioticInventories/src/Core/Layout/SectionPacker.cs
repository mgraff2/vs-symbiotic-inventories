using System;
using System.Collections.Generic;

namespace SymbioticInventories.Core.Layout
{
    /// <summary>
    /// Fits every open inventory onto one screen.
    ///
    /// The problem is 2D strip packing, which is NP-hard in general - but the input here is
    /// tiny (rarely more than a dozen sections) and, more importantly, the *best* packing is
    /// not what anyone wants. Densest-first would reorder the player's bags every time a chest
    /// opened, so a slot that was second from the left moves somewhere else mid-session. That
    /// is worse than a little wasted space.
    ///
    /// So: order is fixed and meaningful (bands, then registry order within a band), and the
    /// only thing that varies is how wide each section is drawn. The packer builds one
    /// candidate layout per allowed max-column value, scores each against the real screen
    /// budget, and keeps the winner. A handful of full layout passes over a dozen boxes is
    /// free, and it turns "does it fit?" from a guess into a measurement.
    /// </summary>
    public static class SectionPacker
    {
        /// <summary>
        /// Candidate width caps. Both directions genuinely help, which is why the winner has
        /// to be found by trying rather than by reasoning: narrower boxes are taller but pack
        /// more per shelf, wider boxes are shorter but fewer fit side by side. Neither is
        /// monotonically better, so scoring beats a formula.
        /// </summary>
        private static readonly int[] MaxColCandidates = { 4, 5, 6, 8, 10, 12, 16 };

        private const int MinCols = 3;

        /// <summary>Builds the layout for the given budget.</summary>
        public static LayoutPlan Pack(IReadOnlyList<InventorySection> sections, LayoutBudget budget)
        {
            // DockLeft differs from Auto only in its budget - a narrow, tall content area. The
            // packers then wrap early and fill downward all by themselves, which is exactly the
            // locked-to-the-edge behaviour, so there is no separate strategy to maintain.
            bool captions = budget.Mode != LayoutMode.DockLeft;
            var plan = PackBands(sections, budget.MaxWidth, budget.MaxHeight, captions);
            plan.Mode = budget.Mode;
            return plan;
        }

        private static LayoutPlan PackBands(IReadOnlyList<InventorySection> sections, double maxWidth, double maxHeight, bool captions)
        {
            LayoutPlan best = null;
            double bestScore = double.MaxValue;

            // Three strategies crossed with every width cap: shelf packing tie-broken by
            // proportions, shelf packing tie-broken by width-filling, and skyline jigsaw.
            // Neither family dominates - the jigsaw wins mixed loads by interlocking tall and
            // short boxes (typical: 466 vs 626), the shelf DP wins uniform chest-heavy loads
            // where interlocking has nothing to grab (heavy: 830 vs 942). Building every
            // candidate and scoring them is the only honest way to know which is which; a few
            // dozen passes over a dozen boxes costs nothing.
            foreach (bool jigsaw in new[] { false, true })
            {
                foreach (bool widest in jigsaw ? new[] { false } : new[] { false, true })
                {
                    foreach (int maxCols in MaxColCandidates)
                    {
                        var plan = Build(sections, maxWidth, maxCols, widest, captions, jigsaw);
                        double score = Score(plan, maxWidth, maxHeight);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = plan;
                        }
                    }
                }
            }

            best ??= new LayoutPlan();
            best.Overflows = best.Height > maxHeight;
            return best;
        }

        // ---- candidate construction ---------------------------------------------

        private static LayoutPlan Build(IReadOnlyList<InventorySection> sections, double maxWidth, int maxCols, bool widest, bool captions, bool jigsaw)
        {
            var plan = new LayoutPlan { ChosenMaxCols = maxCols, ShowBandCaptions = captions };

            // Hard ceiling: a section can never be wider than the content area, however much
            // the candidate would like it to be.
            int absoluteMaxCols = Math.Max(1, (int)Math.Floor(maxWidth / LayoutMetrics.Cell));
            int cap = Math.Min(maxCols, absoluteMaxCols);

            double y = 0;

            foreach (var band in GroupIntoBands(sections))
            {
                if (band.Boxes.Count == 0) continue;

                band.Y = y;
                double innerY = plan.ShowBandCaptions ? LayoutMetrics.BandCaptionH : 0;

                if (band.Key == "storage" && jigsaw)
                {
                    // Opened storage interlocks like puzzle pieces - a tall chest and two
                    // short bags share the same rows instead of each paying for a shelf.
                    // The jigsaw packs from Y=0, so shift its result below the caption.
                    double used = JigsawPacker.Pack(band.Boxes, maxWidth, cap);
                    foreach (var box in band.Boxes) box.Y += innerY;
                    innerY += used;
                }
                else
                {
                    foreach (var box in band.Boxes) Measure(box, cap, absoluteMaxCols, widest);

                    // Essentials and hotbar stay on shelves: they are one or two boxes whose
                    // position must be boringly predictable, and shelf breaking is optimal via
                    // the same DP as Knuth's line-breaking - order fixed, only breaks move.
                    foreach (var shelf in BreakIntoShelves(band.Boxes, maxWidth))
                    {
                        double shelfX = 0;
                        double shelfH = 0;
                        foreach (var box in shelf)
                        {
                            box.X = shelfX;
                            box.Y = innerY;
                            shelfX += box.W + LayoutMetrics.BoxGapX;
                            shelfH = Math.Max(shelfH, box.H);
                        }
                        innerY += shelfH + LayoutMetrics.BoxGapY;
                    }
                    if (band.Boxes.Count > 0) innerY -= LayoutMetrics.BoxGapY;
                }

                band.H = innerY;

                // Bands are placed in content space, so shift each box onto the band.
                foreach (var box in band.Boxes) box.Y += band.Y;

                y += band.H + LayoutMetrics.BandGapY;
                plan.Bands.Add(band);
            }

            if (plan.Bands.Count > 0) y -= LayoutMetrics.BandGapY;

            plan.Height = Math.Max(y, 0);
            plan.Width = MeasureWidth(plan);
            return plan;
        }

        /// <summary>
        /// Splits an ordered box list into shelves so that the total of the shelves' heights is
        /// minimal, subject to each shelf fitting <paramref name="maxWidth"/>.
        ///
        /// O(n^2) dynamic program over break points. n is the number of open inventories, so
        /// this is a handful of iterations; the exact answer is cheaper than reasoning about
        /// which heuristic would have been good enough.
        /// </summary>
        private static List<List<LayoutBox>> BreakIntoShelves(List<LayoutBox> boxes, double maxWidth)
        {
            int n = boxes.Count;
            var result = new List<List<LayoutBox>>();
            if (n == 0) return result;

            var cost = new double[n + 1];
            var prev = new int[n + 1];
            for (int i = 1; i <= n; i++) cost[i] = double.PositiveInfinity;

            for (int end = 1; end <= n; end++)
            {
                double width = 0;
                double height = 0;
                for (int start = end - 1; start >= 0; start--)
                {
                    var box = boxes[start];
                    width += box.W;
                    if (start < end - 1) width += LayoutMetrics.BoxGapX;
                    height = Math.Max(height, box.H);

                    // A single box wider than the budget still has to go somewhere, so it is
                    // allowed to sit alone on an over-wide shelf rather than making the whole
                    // band unsolvable.
                    bool overflows = width > maxWidth + 0.001;
                    if (overflows && start < end - 1) break;

                    double candidate = cost[start] + height + LayoutMetrics.BoxGapY;
                    if (candidate < cost[end])
                    {
                        cost[end] = candidate;
                        prev[end] = start;
                    }

                    if (overflows) break;
                }
            }

            // Walk the chosen break points back to front, then flip.
            var shelves = new List<List<LayoutBox>>();
            for (int end = n; end > 0; end = prev[end])
            {
                var shelf = new List<LayoutBox>();
                for (int i = prev[end]; i < end; i++) shelf.Add(boxes[i]);
                shelves.Add(shelf);
            }
            shelves.Reverse();
            return shelves;
        }

        private static void Measure(LayoutBox box, int cap, int absoluteMaxCols, bool widest)
        {
            int slots = box.Section.SlotCount;

            // The section may declare a natural width (crafting grid is square by definition,
            // the hotbar is one row by definition). Honour it, but never past the screen.
            int cols = box.Section.FixedColumns > 0
                ? Math.Min(box.Section.FixedColumns, absoluteMaxCols)
                : GridShape.ChooseColumns(slots, MinCols, cap, widest);

            cols = Math.Max(1, cols);
            int rows = (int)Math.Ceiling(slots / (double)cols);

            box.Cols = cols;
            box.Rows = rows;
            box.W = cols * LayoutMetrics.Cell;
            box.H = LayoutMetrics.HeaderH + rows * LayoutMetrics.Cell;
        }

        private static double MeasureWidth(LayoutPlan plan)
        {
            double w = 0;
            foreach (var box in plan.AllBoxes()) w = Math.Max(w, box.X + box.W);
            return w;
        }

        // ---- scoring -------------------------------------------------------------

        /// <summary>
        /// Lower is better. Overflowing the screen dominates everything else, because a
        /// window taller than the screen is the one outcome that is actually unusable;
        /// among layouts that fit, compact and tidy win.
        /// </summary>
        private static double Score(LayoutPlan plan, double maxWidth, double maxHeight)
        {
            double score = 0;

            double overflow = plan.Height - maxHeight;
            if (overflow > 0) score += 10_000 + overflow * 100;

            // Prefer shorter. Height is the scarce axis: the rail and the bands already fix
            // the width, and vertical is where this window ran off the screen to begin with.
            score += plan.Height;

            // Mild tidiness term: penalise ragged shelves, so equally-short layouts prefer
            // the one whose rows are more evenly filled.
            score += Raggedness(plan, maxWidth) * 0.25;

            // Mild proportion term, so a layout does not win by flattening every bag into a
            // single 16-wide strip that happens to be one unit shorter. Only breaks ties -
            // fitting on the screen still outranks looking good.
            foreach (var box in plan.AllBoxes())
                score += Math.Abs(Math.Log((double)box.Cols / Math.Max(box.Rows, 1)) - Math.Log(GridShape.TargetAspect)) * 6;

            return score;
        }

        /// <summary>Total unused horizontal space across all shelves.</summary>
        private static double Raggedness(LayoutPlan plan, double maxWidth)
        {
            double waste = 0;
            foreach (var band in plan.Bands)
            {
                // Group boxes by their shelf, identified by shared Y.
                var shelfWidths = new Dictionary<double, double>();
                foreach (var box in band.Boxes)
                {
                    shelfWidths.TryGetValue(box.Y, out double w);
                    shelfWidths[box.Y] = Math.Max(w, box.X + box.W);
                }
                foreach (var w in shelfWidths.Values) waste += Math.Max(0, maxWidth - w);
            }
            return waste;
        }

        // ---- banding -------------------------------------------------------------

        /// <summary>
        /// Groups sections into the three horizontal bands the window is read in: what you
        /// are carrying, and what you have opened. Fixed order - the whole point is that a
        /// given bag is always in the same place. No hotbar band: the vanilla hotbar HUD is
        /// permanently on screen, so a copy here would only repeat it.
        /// </summary>
        private static List<LayoutBand> GroupIntoBands(IReadOnlyList<InventorySection> sections)
        {
            // Essentials are split out purely so they can be pinned: the crafting grid has to
            // be reachable at all times, and once the window scrolls, "in the layout" and "on
            // screen" stop being the same thing. Kept deliberately small.
            var essentials = new LayoutBand { Key = "essentials", Title = "Essentials", Pinned = true };

            // Backpacks and opened containers jigsaw together in one band. Registry order puts
            // backpacks first, and skyline placement of earlier boxes never depends on later
            // ones - so a bag keeps its exact position as chests open and close after it.
            // Stability comes free; separate bands would only waste the interlock.
            var storage = new LayoutBand { Key = "storage", Title = "Storage" };

            foreach (var s in sections)
            {
                var target = s.Kind switch
                {
                    SectionKind.Crafting => essentials,
                    SectionKind.BackpackSlots => essentials,
                    _ => storage
                };
                target.Boxes.Add(new LayoutBox { Section = s });
            }

            return new List<LayoutBand> { essentials, storage };
        }
    }
}
