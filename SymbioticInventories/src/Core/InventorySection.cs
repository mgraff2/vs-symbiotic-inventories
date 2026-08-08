using System;
using Vintagestory.API.Common;

namespace SymbioticInventories.Core
{
    /// <summary>
    /// What kind of storage a section represents. Drives ordering, colour and iconography.
    /// </summary>
    public enum SectionKind
    {
        Crafting,
        Character,
        Hotbar,
        BackpackSlots,
        Backpack,
        GroundContainer,
        Vehicle,
        Mount,
        Other
    }

    /// <summary>
    /// One contiguous, independently-labelled block of slots drawn in the master window.
    ///
    /// A section is deliberately *not* the same thing as an inventory: a single player
    /// backpack inventory is split into one section per equipped bag, because "which bag
    /// is this slot in" is the question the window exists to answer.
    /// </summary>
    public class InventorySection
    {
        /// <summary>Stable identity across rebuilds, so numbering and scroll position don't jump.</summary>
        public string Id;

        /// <summary>Primary label, e.g. "Leather Backpack" or "Reinforced Chest".</summary>
        public string Label;

        /// <summary>Secondary context, e.g. "Catboat - aft" or "3 blocks away".</summary>
        public string SubLabel;

        public SectionKind Kind;

        /// <summary>The badge number drawn over the grid. Assigned per-kind by the registry.</summary>
        public int Number;

        /// <summary>Accent colour as r,g,b in 0..1. Alpha is applied at draw time.</summary>
        public double[] Accent;

        public IInventory Inventory;

        /// <summary>Which slot ids of <see cref="Inventory"/> belong to this section.</summary>
        public int[] SlotIds;

        /// <summary>
        /// Correct packet route for this inventory. Player-owned inventories go straight
        /// down the client channel; absorbed block containers must use their originating
        /// dialog's sender so the block-entity envelope is preserved.
        /// </summary>
        public Action<object> SendPacket;

        /// <summary>
        /// Column count this section must be drawn at, or 0 to let the layout system choose.
        /// Only meaningful for the fixed top-strip sections (the crafting grid is square
        /// because its recipes are); flow sections are shaped by the unified grid.
        /// </summary>
        public int FixedColumns;

        /// <summary>
        /// Representative itemstack for the vessel row - the worn bag itself, or the
        /// container's block. Null when there is nothing sensible to show (entity
        /// containers); the badge tile still renders.
        /// </summary>
        public ItemStack Icon;

        /// <summary>
        /// Container-type key for group filtering: "backpack" for worn bags, the block
        /// code's first path part for block containers ("chest", "trunk", "storagevessel"),
        /// "entity" for creature/vehicle inventories. Sections sharing a key form one group
        /// in the vessel row with a single toggle.
        /// </summary>
        public string GroupKey = "other";

        public int SlotCount => SlotIds?.Length ?? 0;

        /// <summary>Sections that carry a visible number badge. Crafting/hotbar are unambiguous already.</summary>
        public bool Numbered => Kind == SectionKind.Backpack
                             || Kind == SectionKind.GroundContainer
                             || Kind == SectionKind.Vehicle
                             || Kind == SectionKind.Mount;
    }
}
