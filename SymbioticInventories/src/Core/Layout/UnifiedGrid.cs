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

            var onBody = new List<InventorySection>();
            var offBody = new List<InventorySection>();
            foreach (var s in flow)
                if (s.SlotCount > 0) (IsOnBody(s.Kind) ? onBody : offBody).Add(s);

            // Bag-block width: the biggest bag, capped by the window (an oversized bag wraps).
            int bagW = 0;
            foreach (var s in onBody) bagW = Math.Max(bagW, Math.Min(s.SlotCount, cols));

            // Just the player's own storage open: the window is exactly the bag block.
            plan.Cols = (onBody.Count > 0 && offBody.Count == 0) ? bagW : cols;

            // ---- the bag block: one bag per line ----------------------------------
            int bagRows = 0;
            foreach (var s in onBody)
            {
                int n = s.SlotCount;
                var ribbon = new Ribbon { Section = s, StartCell = bagRows * plan.Cols };

                int full = n / bagW;
                if (full > 0)
                    ribbon.Slices.Add(new RibbonSlice { Row = bagRows, Col = 0, Cols = bagW, Rows = full, SlotOffset = 0 });
                int tail = n - full * bagW;
                if (tail > 0)
                    ribbon.Slices.Add(new RibbonSlice { Row = bagRows + full, Col = 0, Cols = tail, Rows = 1, SlotOffset = full * bagW });

                bagRows += (n + bagW - 1) / bagW;
                plan.Ribbons.Add(ribbon);
                plan.TotalCells += n;
            }

            plan.Rows = bagRows;
            if (offBody.Count == 0) return plan;

            // ---- off-body containers: beside the bags, then below (the backward L) ----
            // The gutter column (bagW) stays blank so the two territories read apart.
            // A window too narrow for a useful side region stacks the containers below.
            bool lShape = onBody.Count > 0 && cols >= bagW + 1 + MinSideCols;
            (int a, int b) Span(int r) => lShape && r < bagRows ? (bagW + 1, cols) : (0, cols);

            int row = lShape || onBody.Count == 0 ? 0 : bagRows;
            int col = Span(row).a;

            foreach (var s in offBody)
            {
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
            }

            return plan;
        }

        /// <summary>Sections the player carries, as opposed to world/entity storage.</summary>
        public static bool IsOnBody(SectionKind kind)
            => kind is SectionKind.Crafting or SectionKind.Hotbar
                    or SectionKind.BackpackSlots or SectionKind.Backpack;

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
            int bagW = 0; bool off = false;
            foreach (var s in flow)
            {
                if (s.SlotCount <= 0) continue;
                if (IsOnBody(s.Kind)) bagW = Math.Max(bagW, s.SlotCount);
                else off = true;
            }
            if (bagW == 0 || !off) return cols;
            return Math.Min(Math.Max(cols, bagW + 1 + 8), Math.Max(cols, colsScreen));
        }
    }
}
