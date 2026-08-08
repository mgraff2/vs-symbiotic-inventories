using Vintagestory.API.Client;

namespace SymbioticInventories.Core.Layout
{
    /// <summary>
    /// Shared measurements, in Vintage Story's unscaled GUI units. Everything the layout
    /// system computes is in these units; the GUI scale is applied at draw time.
    /// </summary>
    public static class LayoutMetrics
    {
        public static readonly double SlotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
        public static readonly double SlotPad = GuiElementItemSlotGridBase.unscaledSlotPadding;

        /// <summary>Footprint of one slot including its padding - the atom of all measurement.</summary>
        public static readonly double Cell = SlotSize + SlotPad;
    }

    /// <summary>How the window presents itself. Toggled from the footer.</summary>
    public enum LayoutMode
    {
        /// <summary>Centered floating window.</summary>
        Auto,

        /// <summary>Locked to the left edge. Interacted with via the focus hotkey.</summary>
        DockLeft
    }
}
