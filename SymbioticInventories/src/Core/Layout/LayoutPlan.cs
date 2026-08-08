using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace SymbioticInventories.Core.Layout
{
    /// <summary>
    /// Shared measurements, in Vintage Story's unscaled GUI units. Everything the layout
    /// system computes is in these units; the GUI scale is applied by the engine at draw time.
    /// </summary>
    public static class LayoutMetrics
    {
        public static readonly double SlotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        public static readonly double SlotPad = GuiElementItemSlotGridBase.unscaledSlotPadding;

        /// <summary>Footprint of one slot including its padding - the atom of all measurement.</summary>
        public static readonly double Cell = SlotSize + SlotPad;

        /// <summary>Caption strip above each section's grid.</summary>
        public const double HeaderH = 24;

        /// <summary>Gap between neighbouring sections on the same shelf.</summary>
        public const double BoxGapX = 10;

        /// <summary>Gap between shelves within a band.</summary>
        public const double BoxGapY = 10;

        /// <summary>Gap between bands, plus room for the band caption.</summary>
        public const double BandCaptionH = 20;
        public const double BandGapY = 14;
    }

    /// <summary>How the window arranges itself. Toggled from the footer.</summary>
    public enum LayoutMode
    {
        /// <summary>Centered floating window; opened storage is jigsaw-packed for density.</summary>
        Auto,

        /// <summary>Locked to the left edge. Interacted with via the focus hotkey.</summary>
        DockLeft
    }

    /// <summary>The space a layout has to work in, and how it should use it.</summary>
    public class LayoutBudget
    {
        public double MaxWidth;
        public double MaxHeight;
        public LayoutMode Mode = LayoutMode.Auto;
    }

    /// <summary>One section, measured and placed. Coordinates are relative to the content area.</summary>
    public class LayoutBox
    {
        public InventorySection Section;
        public int Cols;
        public int Rows;

        public double X;
        public double Y;
        public double W;
        public double H;

        /// <summary>Top-left of the slot grid itself, below the caption strip.</summary>
        public double GridY => Y + LayoutMetrics.HeaderH;
    }

    /// <summary>A titled horizontal group of sections - "Player", "Storage", "Hotbar".</summary>
    public class LayoutBand
    {
        public string Key;
        public string Title;
        public readonly List<LayoutBox> Boxes = new();
        public double Y;
        public double H;

        /// <summary>
        /// Held above the scroll region and always visible. Only the essentials band is
        /// pinned - pinning more would eat the scroll viewport it is supposed to protect.
        /// </summary>
        public bool Pinned;
    }

    /// <summary>The finished layout: where every section goes and how big the window must be.</summary>
    public class LayoutPlan
    {
        public readonly List<LayoutBand> Bands = new();

        public double Width;
        public double Height;

        /// <summary>True when even the best candidate exceeded the height budget.</summary>
        public bool Overflows;

        public LayoutMode Mode = LayoutMode.Auto;

        /// <summary>
        /// Whether bands carry their "CARRIED" / "OPENED STORAGE" captions. Off when docked:
        /// captions cost vertical units, and in a narrow column that is a whole extra row of
        /// slots for labels the section plates already imply.
        /// </summary>
        public bool ShowBandCaptions = true;

        /// <summary>Max columns the winning candidate was built with. Diagnostic only.</summary>
        public int ChosenMaxCols;

        public IEnumerable<LayoutBox> AllBoxes()
        {
            foreach (var band in Bands)
                foreach (var box in band.Boxes)
                    yield return box;
        }
    }
}
