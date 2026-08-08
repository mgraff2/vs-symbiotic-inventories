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
        public int TotalSlots;
        public readonly List<Ribbon> Ribbons = new();
    }

    /// <summary>
    /// The unified-flow layout: every storage section's slots pour into ONE row-major grid,
    /// and a section is a contiguous *ribbon* of cells outlined in its accent colour - the
    /// way a text selection spans line breaks - rather than a free-floating rectangle.
    ///
    /// This dissolves the packing problem instead of solving it. Rectangles of mismatched
    /// container sizes can never tile a window without gaps or scrolling; a flow is maximally
    /// dense *by construction* - every row is full except the last - and fluid, because the
    /// column count is whatever fits the window. There is nothing left to search.
    /// </summary>
    public static class UnifiedGrid
    {
        /// <summary>
        /// Lays the sections' slots into a flow of the given width, in the order given.
        /// Order is the stability story: earlier sections' ribbons are unaffected by later
        /// ones, so bags stay put as containers open and close after them.
        /// </summary>
        public static UnifiedPlan Compute(IReadOnlyList<InventorySection> flow, int cols)
        {
            cols = Math.Max(1, cols);
            var plan = new UnifiedPlan { Cols = cols };

            int cell = 0;
            foreach (var s in flow)
            {
                int n = s.SlotCount;
                if (n <= 0) continue;

                var ribbon = new Ribbon { Section = s, StartCell = cell };

                int offset = 0;
                int remaining = n;

                // Leading partial row: from the current column to the row's end.
                int col = cell % cols;
                if (col > 0)
                {
                    int run = Math.Min(remaining, cols - col);
                    ribbon.Slices.Add(new RibbonSlice
                    {
                        Row = cell / cols, Col = col, Cols = run, Rows = 1, SlotOffset = offset
                    });
                    offset += run; remaining -= run; cell += run;
                }

                // Block of full rows, as ONE slice - one grid element regardless of height.
                int fullRows = remaining / cols;
                if (fullRows > 0)
                {
                    ribbon.Slices.Add(new RibbonSlice
                    {
                        Row = cell / cols, Col = 0, Cols = cols, Rows = fullRows, SlotOffset = offset
                    });
                    int c = fullRows * cols;
                    offset += c; remaining -= c; cell += c;
                }

                // Trailing partial row.
                if (remaining > 0)
                {
                    ribbon.Slices.Add(new RibbonSlice
                    {
                        Row = cell / cols, Col = 0, Cols = remaining, Rows = 1, SlotOffset = offset
                    });
                    cell += remaining;
                }

                plan.Ribbons.Add(ribbon);
            }

            plan.TotalSlots = cell;
            plan.Rows = (int)Math.Ceiling(cell / (double)cols);
            return plan;
        }

        /// <summary>
        /// Columns that make the flow fit the given viewport without scrolling, if any width
        /// can: enough columns that the row count fits the height, but never more than the
        /// window is wide. Fluidity as arithmetic - no candidate search.
        /// </summary>
        public static int ChooseCols(int totalSlots, double maxWidth, double maxHeight, int minCols = 8)
        {
            int colsScreen = Math.Max(minCols, (int)(maxWidth / LayoutMetrics.Cell));
            if (totalSlots <= 0) return Math.Min(12, colsScreen);

            int maxRows = Math.Max(1, (int)(maxHeight / LayoutMetrics.Cell));
            int colsForFit = (int)Math.Ceiling(totalSlots / (double)maxRows);

            return Math.Clamp(Math.Max(colsForFit, 12), minCols, colsScreen);
        }
    }
}
