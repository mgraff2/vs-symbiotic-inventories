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
    }
}
