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
        /// One right-click on a chest also opens every touching chest of the same kind, so
        /// the whole wall docks into the master window at once.
        /// </summary>
        public bool OpenAdjacentChests { get; set; } = true;
    }
}
