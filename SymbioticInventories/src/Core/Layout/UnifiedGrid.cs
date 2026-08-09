using System;
using System.Collections.Generic;

namespace SymbioticInventories.Core.Layout
{
    /// <summary>
    /// One horizontal run of cells belonging to a ribbon, or a block of full rows.
    /// Each slice maps 1:1 onto one slot-grid element at draw time.
    /// </summary>
    public class RibbonSlice
    {
        /// <summary>Grid position of the slice's top-left cell.</summary>
        public int Row;
        public int Col;

        /// <summary>Columns this slice spans. A full-rows block spans the whole grid width.</summary>
        public int Cols;

        /// <summary>Rows this slice spans (1 for partial runs, >=1 for the full-rows block).</summary>
        public int Rows;

        /// <summary>Offset into the section's SlotIds where this slice's slots begin.</summary>
        public int SlotOffset;

        public int Count => Cols * Rows;
    }

    /// <summary>One section's contiguous run of cells in the unified flow.</summary>
    public class Ribbon
    {
        public InventorySection Section;

        /// <summary>Absolute cell index (row-major) where this section's slots begin.</summary>
        public int StartCell;

        /// <summary>At most three: leading partial row, block of full rows, trailing partial row.</summary>
        public readonly List<RibbonSlice> Slices = new();

        public int EndCell => StartCell + Section.SlotCount;   // exclusive
    }

    /// <summary>The whole flow: grid dimensions plus every section's ribbon.</summary>
    public class UnifiedPlan
    {
        public int Cols;
        public int Rows;

        /// <summary>Cells the flow consumed - slots plus the skipped tail of the on-body
        /// block's last row (the deliberate line break before off-body storage).</summary>
        public int TotalCells;
        public readonly List<Ribbon> Ribbons = new();
    }

    /// <summary>
    /// The unified layout, backward-L edition: the worn-bag block sits top-left - ONE BAG
    /// PER LINE, block width = the biggest bag - then a one-column blank gutter, and every
    /// off-body container (chests, vessels, boats, mounts) flows through the remaining
    /// space: beside the bag block first, then full-width below it. Alone, the window is
    /// exactly the bag block; a docked/narrow window degrades to bags-then-containers
    /// stacked, because a side region needs width to be worth having.
    ///
    /// Within its region a section is still a contiguous *ribbon* of cells - the flow still
    /// dissolves the packing problem; the L just gives the player's own storage a fixed,
    /// findable home that never reflows as world containers come and go.
    /// </summary>
    public static class UnifiedGrid
    {
        /// <summary>Minimum side-region width (columns) for the L to be worth it; below
        /// this the off-body flow stacks under the bags instead.</summary>
        private const int MinSideCols = 2;

        /// <summary>
        /// Lays the sections into the backward-L (or its degenerate forms), in the order
        /// given. Order is the stability story: earlier sections' ribbons are unaffected by
        /// later ones, so bags stay put as containers open and close after them.
        /// </summary>
        public static UnifiedPlan Compute(IReadOnlyList<InventorySection> flow, int cols)
        {
            cols = Math.Max(1, cols);
            var plan = new UnifiedPlan();

            // The LEFT BLOCK is the player's own territory: worn bags first, then - after
            // one blank row - the mount's saddlebags, which ride with the player and belong
            // visually with them rather than in the world-container flow.
            var bags = new List<InventorySection>();
            var mounts = new List<InventorySection>();
            var offBody = new List<InventorySection>();
            foreach (var s in flow)
            {
                if (s.SlotCount <= 0) continue;
                if (IsOnBody(s.Kind)) bags.Add(s);
                else if (s.Kind == SectionKind.Mount) mounts.Add(s);
                else offBody.Add(s);
            }

            // Saddlebags render as TWO adjacent rows, not one long line - a 12-slot bag is
            // a squat 6x2, so it neither widens the block past the worn bags nor reads as
            // one endless strip.
            static int MountW(InventorySection s) => (s.SlotCount + 1) / 2;

            // Left-block width: the biggest line, capped by the window (oversized wraps).
            int blockW = 0;
            foreach (var s in bags) blockW = Math.Max(blockW, Math.Min(s.SlotCount, cols));
            foreach (var s in mounts) blockW = Math.Max(blockW, Math.Min(MountW(s), cols));

            // Just the player's own territory open: the window is exactly the left block.
            plan.Cols = (blockW > 0 && offBody.Count == 0) ? blockW : cols;

            // ---- the left block: one section per line (mounts: per 2-row brick) ----
            int leftRows = 0;
            void AddLine(InventorySection s, int wrapW)
            {
                int n = s.SlotCount;
                var ribbon = new Ribbon { Section = s, StartCell = leftRows * plan.Cols };

                int full = n / wrapW;
                if (full > 0)
                    ribbon.Slices.Add(new RibbonSlice { Row = leftRows, Col = 0, Cols = wrapW, Rows = full, SlotOffset = 0 });
                int tail = n - full * wrapW;
                if (tail > 0)
                    ribbon.Slices.Add(new RibbonSlice { Row = leftRows + full, Col = 0, Cols = tail, Rows = 1, SlotOffset = full * wrapW });

                leftRows += (n + wrapW - 1) / wrapW;
                plan.Ribbons.Add(ribbon);
                plan.TotalCells += n;
            }

            foreach (var s in bags) AddLine(s, blockW);
            if (bags.Count > 0 && mounts.Count > 0) leftRows++;   // blank row: you / your mount
            foreach (var s in mounts) AddLine(s, Math.Min(MountW(s), blockW));

            plan.Rows = leftRows;
            if (offBody.Count == 0) return plan;

            // ---- world containers: beside the left block, then below (the backward L) ----
            // The gutter column (blockW) stays blank so the territories read apart, and the
            // side span extends ONE ROW past the left block: that row keeps the container
            // flow continuous on the right while leaving a blank buffer line under the
            // block itself (both user asks - no hole mid-flow, and a breath of air between
            // the saddlebags and the full-width container rows). The stacked fallback keeps
            // its blank row: there it is the sole separator from the left block.
            bool lShape = leftRows > 0 && cols >= blockW + 1 + MinSideCols;

            (int a, int b) Span(int r)
            {
                if (lShape && r <= leftRows) return (blockW + 1, cols);
                if (!lShape && leftRows > 0 && r == leftRows) return (0, 0);   // stacked: blank separator row
                return (0, cols);
            }

            int row = lShape ? 0 : leftRows;
            int col = Span(row).a;

            // Brick banding state: FixedColumns sections (FoodShelves) keep their real
            // X x Y arrangement as rigid rectangles, laid side by side - one blank column
            // between - along a band; a full band opens the next one below. Ribbons and
            // bricks share the same flow; the registry orders bricks last in practice.
            int bandRow = -1, bandCol = 0, bandH = 0;

            foreach (var s in offBody)
            {
                int w = s.FixedColumns;
                bool brick = w > 0 && w <= cols;

                if (!brick)
                {
                    // A ribbon after bricks resumes the flow below the band.
                    if (bandRow >= 0) { row = bandRow + bandH; col = Span(row).a; bandRow = -1; }

                    int remaining = s.SlotCount, offset = 0;
                    var ribbon = new Ribbon { Section = s };

                    while (remaining > 0)
                    {
                        var (a, b) = Span(row);
                        if (col < a) col = a;
                        if (col >= b) { row++; col = Span(row).a; continue; }

                        if (offset == 0) ribbon.StartCell = row * cols + col;

                        int run = Math.Min(remaining, b - col);
                        var last = ribbon.Slices.Count > 0 ? ribbon.Slices[^1] : null;
                        if (last != null && last.Col == col && last.Cols == run && last.Row + last.Rows == row)
                            last.Rows++;   // equal spans stack into one multi-row slice/element
                        else
                            ribbon.Slices.Add(new RibbonSlice { Row = row, Col = col, Cols = run, Rows = 1, SlotOffset = offset });

                        offset += run; remaining -= run; col += run;
                        plan.Rows = Math.Max(plan.Rows, row + 1);
                    }

                    plan.Ribbons.Add(ribbon);
                    plan.TotalCells += s.SlotCount;
                    continue;
                }

                // ---- rigid brick ------------------------------------------------
                int n = s.SlotCount;
                int h = (n + w - 1) / w;

                while (true)
                {
                    if (bandRow < 0)
                    {
                        // Fresh band: the first row at/after the cursor whose span holds
                        // the brick (skips blank separator rows and too-narrow side rows).
                        int r = row + (col > Span(row).a ? 1 : 0);
                        while (Span(r).b - Span(r).a < w) r++;
                        bandRow = r; bandCol = Span(r).a; bandH = 0;
                    }
                    if (bandCol + w <= Span(bandRow).b) break;
                    // Band full: open the next one below it.
                    row = bandRow + bandH; col = Span(row).a; bandRow = -1;
                }

                var brickRibbon = new Ribbon { Section = s, StartCell = bandRow * cols + bandCol };
                int full = n / w;
                if (full > 0)
                    brickRibbon.Slices.Add(new RibbonSlice { Row = bandRow, Col = bandCol, Cols = w, Rows = full, SlotOffset = 0 });
                int tail = n - full * w;
                if (tail > 0)
                    brickRibbon.Slices.Add(new RibbonSlice { Row = bandRow + full, Col = bandCol, Cols = tail, Rows = 1, SlotOffset = full * w });

                plan.Ribbons.Add(brickRibbon);
                plan.TotalCells += n;
                plan.Rows = Math.Max(plan.Rows, bandRow + h);

                bandH = Math.Max(bandH, h);
                bandCol += w + 1;   // one blank column between bricks
            }

            return plan;
        }

        /// <summary>Sections the player carries, as opposed to world/entity storage.</summary>
        public static bool IsOnBody(SectionKind kind)
            => kind is SectionKind.Crafting or SectionKind.Hotbar
                    or SectionKind.BackpackSlots or SectionKind.Backpack;

        /// <summary>Sections living in the left block: worn bags plus the mount's bags -
        /// everything that travels with the player.</summary>
        public static bool IsLeftBlock(SectionKind kind)
            => IsOnBody(kind) || kind == SectionKind.Mount;

        /// <summary>
        /// Columns for the flow, chosen to fill the landscape instead of stacking into a tall
        /// column. Screens are wider than tall, so the grid should be too: aim for the grid's
        /// aspect to match the viewport's - cols*rows ≈ N and cols/rows ≈ screenW/screenH give
        /// cols ≈ sqrt(N · screenAspect). Then guarantee it is at least wide enough not to
        /// scroll when the height could hold it, and never wider than the screen. Fluidity as
        /// arithmetic - no candidate search.
        /// </summary>
        public static int ChooseCols(int totalSlots, double maxWidth, double maxHeight, int minCols = 8)
        {
            int colsScreen = Math.Max(minCols, (int)(maxWidth / LayoutMetrics.Cell));
            if (totalSlots <= 0) return Math.Min(12, colsScreen);

            int rowsScreen = Math.Max(1, (int)(maxHeight / LayoutMetrics.Cell));

            // Landscape target: match the viewport's proportions.
            double aspect = colsScreen / (double)rowsScreen;
            int target = (int)Math.Ceiling(Math.Sqrt(totalSlots * aspect));

            // Don't scroll if the screen could hold everything in rowsScreen rows.
            int colsForFit = (int)Math.Ceiling(totalSlots / (double)rowsScreen);

            return Math.Clamp(Math.Max(target, colsForFit), minCols, colsScreen);
        }

        /// <summary>
        /// Widens the column count so the backward-L has a usable side region beside the
        /// bag block: gutter + at least 8 columns, screen width permitting. Without this a
        /// small load (one basket) picks few columns and the L degenerates to a stack even
        /// on a wide screen. No-op when there is nothing to put beside the bags.
        /// </summary>
        public static int EnsureSideRoom(int cols, IReadOnlyList<InventorySection> flow, int colsScreen)
        {
            int blockW = 0; bool off = false;
            foreach (var s in flow)
            {
                if (s.SlotCount <= 0) continue;
                if (s.Kind == SectionKind.Mount) blockW = Math.Max(blockW, (s.SlotCount + 1) / 2);
                else if (IsLeftBlock(s.Kind)) blockW = Math.Max(blockW, s.SlotCount);
                else off = true;
            }
            if (blockW == 0 || !off) return cols;
            return Math.Min(Math.Max(cols, blockW + 1 + 8), Math.Max(cols, colsScreen));
        }
    }
}
