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

        /// <summary>Bulk container (ItemsPerSegment > 1, e.g. a flour sack): rendered as
        /// ONE FACADE CELL per segment showing the live aggregate count, instead of its
        /// raw slots. Rows/Cols then describe the segment grid.</summary>
        public bool Facade;

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
            // Facade cells display live aggregates; keep them current as sacks fill/drain.
            api.Event.RegisterGameTickListener(UpdateFacades, 250);
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

                // Bulk containers (many items per segment - flour sacks, baskets) become
                // FACADE grids: one cell per segment carrying the live total, "a container
                // with an internal grid of 1" (user ask). Per-item shelves keep their real
                // X x Y slot grid when the declared geometry matches the inventory; only
                // genuinely odd shapes fall back to a flowing ribbon.
                bool geomOk = shelves >= 1 && segs >= 1 && perSeg >= 1
                           && shelves * segs * perSeg == inv.Count;
                bool facade = geomOk && perSeg > 1 && segs <= 12;
                bool structured = geomOk && perSeg == 1 && segs <= 12;

                var block = ba.GetBlock(p);
                var stack = block != null && block.Id != 0 ? new ItemStack(block) : null;

                found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                {
                    Pos = p,
                    Inventory = inv,
                    Rows = facade || structured ? shelves : 0,
                    Cols = facade ? segs : (structured ? segs : 0),
                    ItemsPerSegment = Math.Max(1, perSeg),
                    Facade = facade,
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

        // ---- facade cells ---------------------------------------------------------
        //
        // A bulk container renders as one DISPLAY-ONLY cell per segment: a DummyInventory
        // whose stack mirrors the segment's live total (icon + count, "flour x188"). The
        // slot grid never mutates it - clicks are intercepted upstream and drive the pump
        // - and a quarter-second tick keeps the numbers matching the real inventory.

        private class FacadeBinding
        {
            public DummyInventory Dummy;
            public IInventory Real;
            public int PerSeg;
        }

        private readonly List<FacadeBinding> facades = new();

        /// <summary>Sections rebuild on every compose; stale bindings go with them.</summary>
        public void ClearFacades() => facades.Clear();

        /// <summary>The display inventory for one bulk container: one cell per segment.</summary>
        public IInventory BuildFacade(AmbientShelf sh)
        {
            var b = new FacadeBinding
            {
                Dummy = new DummyInventory(capi, Math.Max(1, sh.Rows * sh.Cols)),
                Real = sh.Inventory,
                PerSeg = Math.Max(1, sh.ItemsPerSegment)
            };
            facades.Add(b);
            UpdateFacade(b);
            return b.Dummy;
        }

        private void UpdateFacades(float dt)
        {
            foreach (var b in facades) UpdateFacade(b);
        }

        private static void UpdateFacade(FacadeBinding b)
        {
            for (int s = 0; s < b.Dummy.Count; s++)
            {
                int sum = 0;
                ItemStack first = null;
                for (int i = s * b.PerSeg; i < (s + 1) * b.PerSeg && i < b.Real.Count; i++)
                {
                    var st = b.Real[i]?.Itemstack;
                    if (st == null) continue;
                    sum += st.StackSize;
                    first ??= st;
                }

                var slot = b.Dummy[s];
                if (first == null)
                {
                    slot.Itemstack = null;
                    continue;
                }
                if (slot.Itemstack == null || slot.Itemstack.Collectible != first.Collectible)
                {
                    slot.Itemstack = first.Clone();
                }
                slot.Itemstack.StackSize = sum;
            }
        }

        // ---- fixed-count transfer stepper -----------------------------------------
        //
        // Deterministic by construction (user spec after the adaptive pump proved
        // inconsistent): every job fires a number of interactions decided UP FRONT -
        // one per 30ms step, each a real server-validated block interaction - and stops
        // early only on unambiguous client-visible facts (hand empty, cell empty). No
        // batches, no refills, no progress heuristics.

        private class TransferJob
        {
            public AmbientShelf Shelf;
            public int Slot;
            public bool Deposit;

            /// <summary>Interactions left to fire - fixed when the job starts.</summary>
            public int Remaining;

            public int RestoreHotbarIndex = -1;
        }

        private TransferJob job;

        /// <summary>
        /// A click on a shelf cell - reads intent from what the player is holding:
        ///   holding a stack (active hotbar hand OR mouse cursor) -> POUR IT ALL IN
        ///   empty-handed                                          -> take a full stack out
        ///   SHIFT-click -> one single native interaction (moves 1; and with the mod's own
        ///   sneak+sprint combo physically held it stays its native whole-stack gesture)
        /// </summary>
        public void InteractCell(AmbientShelf shelf, int slotIndex)
        {
            if (job != null) { FinishJob(); return; }   // a click during a transfer stops it

            bool shift = capi.Input.KeyboardKeyState[(int)GlKeys.LShift]
                      || capi.Input.KeyboardKeyState[(int)GlKeys.RShift];
            if (shift) { Interact(shelf, slotIndex); return; }   // native single/combo gesture

            var im = capi.World.Player.InventoryManager;
            var mouse = im.MouseItemSlot;
            var hotbar = im.GetHotbarInventory();
            var hand = hotbar[im.ActiveHotbarSlotNumber];

            // ---- deposit straight from the ACTIVE HAND (the natural in-world flow:
            // "64 flour in my hand, click the sack") - no shuttling, the put interaction
            // consumes from exactly where the stack already is.
            if (!hand.Empty)
            {
                job = new TransferJob
                {
                    Shelf = shelf,
                    Slot = slotIndex,
                    Deposit = true,
                    Remaining = hand.StackSize
                };
                Step(0);
                return;
            }

            // ---- deposit a MOUSE-CARRIED stack: shuttle it into an empty hotbar slot
            // first (the interaction only consumes from the hand), then pour.
            if (mouse != null && !mouse.Empty)
            {
                int empty = -1;
                for (int i = 0; i < 10 && i < hotbar.Count; i++)
                {
                    if (hotbar[i].Empty) { empty = i; break; }
                }
                if (empty < 0)
                {
                    logger.Notification("[SymbioticInventories] Depositing needs one empty hotbar slot.");
                    return;
                }

                job = new TransferJob
                {
                    Shelf = shelf,
                    Slot = slotIndex,
                    Deposit = true,
                    RestoreHotbarIndex = im.ActiveHotbarSlotNumber
                };
                im.ActiveHotbarSlotNumber = empty;
                ClickSlot(hotbar, empty);          // carried stack -> working hand (applies locally)
                job.Remaining = hotbar[empty].StackSize;
                Step(0);
                return;
            }

            // ---- empty-handed: take one full stack's worth, counted from the client view
            // of the segment. Aimed at an empty hotbar slot so a hand-delivered take can
            // land on the cursor at cleanup.
            int per = Math.Max(1, shelf.ItemsPerSegment);
            int segStart = slotIndex / per * per;
            int inCell = 0;
            int maxStack = 64;
            for (int i = segStart; i < segStart + per && i < shelf.Inventory.Count; i++)
            {
                var st = shelf.Inventory[i]?.Itemstack;
                if (st == null) continue;
                inCell += st.StackSize;
                maxStack = Math.Max(1, st.Collectible?.MaxStackSize ?? 64);
            }
            if (inCell == 0) { Interact(shelf, slotIndex); return; }   // empty cell: native click

            job = new TransferJob
            {
                Shelf = shelf,
                Slot = slotIndex,
                Deposit = false,
                Remaining = Math.Min(inCell, maxStack)
            };
            for (int i = 0; i < 10 && i < hotbar.Count; i++)
            {
                if (hotbar[i].Empty)
                {
                    job.RestoreHotbarIndex = im.ActiveHotbarSlotNumber;
                    im.ActiveHotbarSlotNumber = i;
                    break;
                }
            }
            Step(0);
        }

        /// <summary>One step: one interaction, 30ms apart, until the fixed count is spent
        /// or the client-visible state says the job cannot continue (deposit hand empty /
        /// take cell empty). A throw cleans up instead of wedging the job.</summary>
        private void Step(float dt)
        {
            try
            {
                if (job == null) return;
                var im = capi.World.Player.InventoryManager;
                var hand = im.GetHotbarInventory()[im.ActiveHotbarSlotNumber];

                bool done = job.Remaining <= 0 || (job.Deposit && hand.Empty);
                if (!done && !job.Deposit)
                {
                    int per = Math.Max(1, job.Shelf.ItemsPerSegment);
                    int segStart = job.Slot / per * per;
                    int inCell = 0;
                    for (int i = segStart; i < segStart + per && i < job.Shelf.Inventory.Count; i++)
                    {
                        inCell += job.Shelf.Inventory[i]?.Itemstack?.StackSize ?? 0;
                    }
                    done = inCell == 0;
                }
                if (done) { FinishJob(); return; }

                Interact(job.Shelf, job.Slot);
                job.Remaining--;
                capi.Event.RegisterCallback(Step, 30);
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Transfer step failed: {0}", e.Message);
                FinishJob();
            }
        }

        /// <summary>
        /// Ends the job now, but does the CLEANUP after the server settles: the final puts
        /// are still in flight, so touching the working hand immediately picks up a ghost
        /// stack the server is about to consume - corrections then fight the restore and
        /// the whole hotbar feels wedged (real bug after a completed 188-flour pour). One
        /// beat of quiet, then: leftover to the cursor (if the cursor is free), selection
        /// restored, one log line saying what moved.
        /// </summary>
        private void FinishJob()
        {
            var j = job;
            job = null;
            if (j == null) return;
            // Cleanup only applies when we SWITCHED to a working slot. A deposit poured
            // straight from the player's real hand leaves its leftover exactly where it
            // is - in their hand, where they put it.
            if (j.RestoreHotbarIndex < 0) return;

            int handIndex = capi.World.Player.InventoryManager.ActiveHotbarSlotNumber;
            capi.Event.RegisterCallback(_ =>
            {
                try
                {
                    var im = capi.World.Player.InventoryManager;
                    var hotbar = im.GetHotbarInventory();
                    var hand = hotbar[handIndex];
                    if (!hand.Empty)
                    {
                        if (im.MouseItemSlot.Empty) ClickSlot(hotbar, handIndex);   // leftover -> cursor
                        else logger.Notification("[SymbioticInventories] Transfer leftover kept in hotbar slot {0} (cursor occupied).", handIndex + 1);
                    }
                    if (j.RestoreHotbarIndex >= 0) im.ActiveHotbarSlotNumber = j.RestoreHotbarIndex;
                }
                catch (Exception e)
                {
                    logger.Warning("[SymbioticInventories] Transfer cleanup failed: {0}", e.Message);
                }
            }, 700);
        }

        /// <summary>An ordinary slot click with the mouse-cursor slot - the exact packets a
        /// real click sends (recipe verified against GuiElementItemSlotGridBase.SlotClick).</summary>
        private void ClickSlot(IInventory inv, int slotId)
        {
            var player = capi.World.Player;
            var op = new ItemStackMoveOperation(capi.World, EnumMouseButton.Left,
                0, (EnumMergePriority)0, 0) { ActingPlayer = player };
            var packet = inv.ActivateSlot(slotId, player.InventoryManager.MouseItemSlot, ref op);
            if (packet != null) capi.Network.SendPacketClient(packet);
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
