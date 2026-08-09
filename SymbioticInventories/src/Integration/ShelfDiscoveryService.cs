using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SymbioticInventories.Integration
{
    /// <summary>One nearby FoodShelves container, with its flattened shelf geometry.</summary>
    public class AmbientShelf
    {
        public BlockPos Pos;
        public IInventory Inventory;

        /// <summary>Rows = shelf levels, Cols = segments x items-per-segment: the shelf's
        /// real spatial arrangement flattened into an X x Y grid block. Cols = 0 means the
        /// geometry did not match the inventory (bulk sacks, modded oddities) and the
        /// section flows as an ordinary ribbon instead.</summary>
        public int Rows, Cols;

        /// <summary>Slots per clickable segment - maps a slot index back to the segment
        /// selection box a real right-click would hit.</summary>
        public int ItemsPerSegment;

        public string Label;
        public ItemStack Icon;
    }

    /// <summary>
    /// Discovery + interaction for FoodShelves-style ambient containers: crock shelves,
    /// bread/pie/cheese shelves, flour sacks, baskets, display cases. These blocks have NO
    /// dialog at all - putting and taking is done by right-clicking shelf segments in the
    /// world - so there is nothing to capture, and no inventory packet route to reuse
    /// (their block entities never override OnReceivedClientPacket; a slot packet would be
    /// silently dropped server-side, verified against FoodShelves 3.0.2).
    ///
    /// Instead: sections render the block entity's client-synced inventory (block-entity
    /// sync keeps it live), and a CLICK on a cell synthesizes the real block interaction
    /// against that cell's segment selection box - the same trick as chain-open and mount
    /// discovery. Empty hand takes from the segment; a shelvable item in the ACTIVE HOTBAR
    /// slot puts. The server runs FoodShelves' own OnInteract, so every placement rule
    /// (what fits on which shelf) is enforced by the mod itself - including on shelf types
    /// this mod has never seen.
    /// </summary>
    public class ShelfDiscoveryService
    {
        private ICoreClientAPI capi;
        private ILogger logger;

        // FoodShelves is a SOFT dependency: resolved by name once, absent = feature off.
        private Type beBaseType;
        private bool probed;
        private PropertyInfo invProp, shelfCountProp, segsProp, perSegProp;

        public void Start(ICoreClientAPI api, ILogger log)
        {
            capi = api;
            logger = log;
        }

        private Type BaseType()
        {
            if (probed) return beBaseType;
            probed = true;

            beBaseType = AccessTools.TypeByName("FoodShelves.BEBaseFSContainer");
            if (beBaseType == null)
            {
                logger.Notification("[SymbioticInventories] FoodShelves not installed - shelf integration off.");
                return null;
            }

            invProp = AccessTools.Property(beBaseType, "Inventory");
            shelfCountProp = AccessTools.Property(beBaseType, "ShelfCount");
            segsProp = AccessTools.Property(beBaseType, "SegmentsPerShelf");
            perSegProp = AccessTools.Property(beBaseType, "ItemsPerSegment");
            if (invProp == null)
            {
                logger.Warning("[SymbioticInventories] FoodShelves found but its Inventory member moved - shelf integration off.");
                beBaseType = null;
            }
            return beBaseType;
        }

        /// <summary>Every FoodShelves container within the radius, nearest first (then by
        /// coordinates, so section order - and therefore ribbon layout - is stable while
        /// the player stands still).</summary>
        public List<AmbientShelf> Discover(int radius = 5)
        {
            var found = new List<(int dist, AmbientShelf shelf)>();
            var t = BaseType();
            var center = capi.World?.Player?.Entity?.Pos?.AsBlockPos;
            if (t == null || center == null) return new List<AmbientShelf>();

            var ba = capi.World.BlockAccessor;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -3; dy <= 3; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                var p = center.AddCopy(dx, dy, dz);
                var be = ba.GetBlockEntity(p);
                if (be == null || !t.IsInstanceOfType(be)) continue;

                var inv = invProp.GetValue(be) as IInventory;
                if (inv == null || inv.Count == 0) continue;

                int shelves = (shelfCountProp?.GetValue(be) as int?) ?? 1;
                int segs = (segsProp?.GetValue(be) as int?) ?? 1;
                int perSeg = (perSegProp?.GetValue(be) as int?) ?? 1;

                // The flattened X x Y only holds when the declared geometry matches the
                // inventory exactly; bulk containers (a flour sack is 1x1xN) and anything
                // with extra slots flow as ordinary ribbons instead.
                int cols = segs * perSeg;
                bool structured = shelves >= 1 && cols >= 1 && cols <= 12
                               && shelves * cols == inv.Count;

                var block = ba.GetBlock(p);
                var stack = block != null && block.Id != 0 ? new ItemStack(block) : null;

                found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                {
                    Pos = p,
                    Inventory = inv,
                    Rows = structured ? shelves : 0,
                    Cols = structured ? cols : 0,
                    ItemsPerSegment = Math.Max(1, perSeg),
                    Label = stack?.GetName() ?? "?",
                    Icon = stack
                }));
            }

            found.Sort((a, b) =>
            {
                int c = a.dist.CompareTo(b.dist);
                if (c != 0) return c;
                c = a.shelf.Pos.X.CompareTo(b.shelf.Pos.X);
                if (c != 0) return c;
                c = a.shelf.Pos.Y.CompareTo(b.shelf.Pos.Y);
                return c != 0 ? c : a.shelf.Pos.Z.CompareTo(b.shelf.Pos.Z);
            });
            return found.ConvertAll(f => f.shelf);
        }

        /// <summary>Cheap change signature for the tick watcher: recompose only when the
        /// set of nearby shelves (or their slot counts) actually changes.</summary>
        public long Signature()
        {
            long sig = 17;
            foreach (var sh in Discover())
            {
                sig = sig * 31 + sh.Pos.GetHashCode();
                sig = sig * 31 + sh.Inventory.Count;
            }
            return sig;
        }

        /// <summary>
        /// A click on a shelf cell. Empty cursor: plain take (the real interaction pulls
        /// the item into the player's inventory). Carrying a stack: deposit it - "treat it
        /// like any grid slot" (user ask) - via the hotbar shuttle below.
        /// </summary>
        public void InteractCell(AmbientShelf shelf, int slotIndex)
        {
            var mouse = capi.World.Player?.InventoryManager?.MouseItemSlot;
            if (mouse != null && !mouse.Empty)
            {
                DepositCarried(shelf, slotIndex);
                return;
            }
            Interact(shelf, slotIndex);
        }

        /// <summary>
        /// Deposits the MOUSE-CARRIED stack into a shelf cell. The block's put interaction
        /// only consumes from the ACTIVE HOTBAR hand, so the carried stack shuttles
        /// through it: swap carried into the active hotbar slot (an ordinary,
        /// server-validated slot click - the sorter's old recipe, verified against
        /// GuiElementItemSlotGridBase.SlotClick IL), fire the real put interaction, then
        /// swap the leftover back to the cursor. All three travel one ordered channel, so
        /// the server always applies swap -> put -> swap-back in sequence; the swap-back
        /// waits a beat so its client-side prediction runs against the server-corrected
        /// hand contents instead of the stale full stack.
        /// </summary>
        private void DepositCarried(AmbientShelf shelf, int slotIndex)
        {
            var player = capi.World.Player;
            var im = player.InventoryManager;
            var hotbar = im.GetHotbarInventory();
            int active = im.ActiveHotbarSlotNumber;
            if (hotbar == null || active < 0) { Interact(shelf, slotIndex); return; }

            void SwapHand()
            {
                var op = new ItemStackMoveOperation(capi.World, EnumMouseButton.Left,
                    0, (EnumMergePriority)0, 0) { ActingPlayer = player };
                var packet = hotbar.ActivateSlot(active, im.MouseItemSlot, ref op);
                if (packet != null) capi.Network.SendPacketClient(packet);
            }

            SwapHand();                        // carried -> hand, old hand -> cursor
            Interact(shelf, slotIndex);        // server: FoodShelves TryPut from the hand
            capi.Event.RegisterCallback(_ => SwapHand(), 450);   // leftover -> cursor, hand restored
        }

        /// <summary>
        /// Acts on one shelf cell exactly as a right-click on its segment's selection box
        /// would: client-side OnBlockInteractStart plus the Start/StopBlockUse hand packets
        /// (verified pattern from chain-open). The server runs the mod's own interaction -
        /// perms, claims, range, and shelf rules all enforced there.
        /// </summary>
        private void Interact(AmbientShelf shelf, int slotIndex)
        {
            try
            {
                var block = capi.World.BlockAccessor.GetBlock(shelf.Pos);
                var sel = new BlockSelection
                {
                    Position = shelf.Pos.Copy(),
                    Face = BlockFacing.UP,
                    HitPosition = new Vec3d(0.5, 0.5, 0.5),
                    SelectionBoxIndex = Math.Max(0, slotIndex / Math.Max(1, shelf.ItemsPerSegment))
                };
                block.OnBlockInteractStart(capi.World, capi.World.Player, sel);
                capi.Network.SendHandInteraction(2, sel, null,
                    EnumHandInteract.BlockInteract, (int)EnumHandInteractNw.StartBlockUse,
                    false, (EnumItemUseCancelReason)0);
                capi.Network.SendHandInteraction(2, sel, null,
                    EnumHandInteract.BlockInteract, (int)EnumHandInteractNw.StopBlockUse,
                    false, (EnumItemUseCancelReason)0);
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Shelf interact at {0} failed: {1}", shelf.Pos, e.Message);
            }
        }
    }
}
