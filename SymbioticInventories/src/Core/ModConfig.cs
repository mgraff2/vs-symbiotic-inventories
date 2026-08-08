namespace SymbioticInventories.Core
{
    /// <summary>
    /// User-facing feature toggles, persisted to ModConfig/symbioticinventories.json.
    ///
    /// Every feature that changes gameplay-visible behaviour belongs here with a switch in
    /// the options dialog - the mod should never make the player edit a JSON file to turn
    /// something off.
    /// </summary>
    public class ModConfig
    {
        public const string Filename = "symbioticinventories.json";

        /// <summary>
        /// One right-click on a chest also opens every chest of the same kind within
        /// <see cref="AdjacentOpenRadius"/> blocks, so the whole wall docks into the master
        /// window at once.
        /// </summary>
        public bool OpenAdjacentChests { get; set; } = true;

        /// <summary>
        /// How far (in blocks, box radius) around the clicked container to look. Radius
        /// rather than face-contiguity on purpose: real chest walls have shelf boards and
        /// air gaps between rows that a flood fill cannot cross.
        /// </summary>
        public int AdjacentOpenRadius { get; set; } = 3;

        /// <summary>
        /// Item categories that sort into the worn backpacks first (they are the earliest
        /// cells in the flow), so the things you reach for stay on you rather than being
        /// pushed out into chests. Anything checked here is prioritised; everything else fills
        /// the containers afterwards. The belt/hotbar is never touched by sorting at all.
        /// </summary>
        public bool SortPrioritizeTools { get; set; } = true;
        public bool SortPrioritizeFood { get; set; } = true;
        public bool SortPrioritizeSeeds { get; set; } = false;
        public bool SortPrioritizeOre { get; set; } = false;

        /// <summary>
        /// Sub-filter for the Food category: only prioritise food that will spoil within this
        /// many days. 0 means all food regardless of freshness. Non-perishing food (dried,
        /// preserved) is never matched when this is above 0.
        /// </summary>
        public int SortFoodMaxSpoilDays { get; set; } = 0;

        /// <summary>
        /// Order food within its group by remaining freshness - soonest to spoil first - so
        /// what you should eat next sits at the front. Off: food orders by type like everything
        /// else.
        /// </summary>
        public bool SortFoodByFreshness { get; set; } = false;

        /// <summary>
        /// Replace the vanilla inventory key (E) with the master window. Off restores the
        /// normal inventory/character screen on that key.
        /// </summary>
        public bool OpenOnInventoryKey { get; set; } = true;

        /// <summary>
        /// Show the inventory of the mount the player is riding (elk saddlebags, etc.) in the
        /// master window automatically, without opening it by hand.
        /// </summary>
        public bool ShowMountInventory { get; set; } = true;

        /// <summary>
        /// Also pull in container-carrying entities (pack animals, moored boats) within this
        /// many blocks, 0 to only show the one you are mounted on. For boats, every crate on
        /// the vessel is opened at once.
        /// </summary>
        public int NearbyEntityRadius { get; set; } = 0;
    }
}
