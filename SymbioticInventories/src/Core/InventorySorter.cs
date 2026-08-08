using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SymbioticInventories.Core
{
    /// <summary>
    /// Sorts the contents of the visible containers, globally: items are grouped by
    /// category, categories ordered, items ordered within each category, and the whole
    /// sequence laid across the visible containers in flow order.
    ///
    /// Container-boundary rule: if a category would start near the end of one container and
    /// overrun into the next, and the next container could hold it whole, the category
    /// starts at the next container instead - the skipped cells stay empty. Categories too
    /// big for any single container just flow. Best effort, as requested.
    ///
    /// Execution is synthesized clicks: every move is the mouse-cursor slot picking up and
    /// putting down via <see cref="IInventory.ActivateSlot"/>, sending exactly the packets a
    /// real click sends (recipe verified against GuiElementItemSlotGridBase.SlotClick IL).
    /// The server validates every step, so a rejected move degrades to "that stack stays
    /// put", never desync. AutoMerge also coalesces partial stacks of the same item as a
    /// side effect - free stack consolidation.
    /// </summary>
    public static class InventorySorter
    {
        /// <summary>
        /// Category proxy: the collectible code's first path part ("plank", "ore", "log").
        /// Vintage Story has no universal item-category API; this groups the way players
        /// actually think about stock.
        /// </summary>
        private static string CategoryOf(ItemStack st)
            => st?.Collectible?.Code?.FirstCodePart() ?? "~";

        private static string ItemKeyOf(ItemStack st)
        {
            if (st?.Collectible?.Code == null) return null;
            return (st.Class == EnumItemClass.Block ? "b:" : "i:") + st.Collectible.Code;
        }

        /// <summary>Sorts across the given sections (visible flow order). Returns moves made.</summary>
        public static int Sort(ICoreClientAPI capi, IReadOnlyList<InventorySection> visible)
        {
            // Flow cells, in order, with their container index for the boundary rule.
            var cells = new List<(InventorySection s, int slotId, int container)>();
            for (int ci = 0; ci < visible.Count; ci++)
            {
                foreach (var id in visible[ci].SlotIds) cells.Add((visible[ci], id, ci));
            }
            if (cells.Count == 0) return 0;

            ItemSlot SlotAt(int i) => cells[i].s.Inventory[cells[i].slotId];
            string KeyAt(int i)
            {
                var slot = SlotAt(i);
                return slot == null || slot.Empty ? null : ItemKeyOf(slot.Itemstack);
            }

            // ---- desired arrangement -------------------------------------------
            var stacks = new List<ItemStack>();
            for (int i = 0; i < cells.Count; i++)
            {
                var slot = SlotAt(i);
                if (slot != null && !slot.Empty) stacks.Add(slot.Itemstack);
            }

            var groups = stacks
                .GroupBy(CategoryOf)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g
                    .OrderBy(ItemKeyOf, StringComparer.Ordinal)
                    .ThenByDescending(st => st.StackSize)
                    .ToList())
                .ToList();

            // Container extents in cell space.
            var containerStart = new Dictionary<int, int>();
            var containerEnd = new Dictionary<int, int>();   // exclusive
            for (int i = 0; i < cells.Count; i++)
            {
                int c = cells[i].container;
                if (!containerStart.ContainsKey(c)) containerStart[c] = i;
                containerEnd[c] = i + 1;
            }

            var desired = new string[cells.Count];
            int pos = 0;
            foreach (var group in groups)
            {
                int container = cells[Math.Min(pos, cells.Count - 1)].container;
                int remaining = containerEnd[container] - pos;

                // The boundary rule: only skip ahead when the group genuinely starts mid-
                // container, would overrun, and the NEXT container could hold it whole.
                if (group.Count > remaining && containerStart.TryGetValue(container + 1, out int nextStart))
                {
                    int nextSize = containerEnd[container + 1] - nextStart;
                    if (group.Count <= nextSize) pos = nextStart;
                }

                foreach (var st in group)
                {
                    if (pos >= cells.Count) break;   // more stacks than cells cannot happen, but stay safe
                    desired[pos++] = ItemKeyOf(st);
                }
            }

            // ---- execution: synthesized clicks ---------------------------------
            var player = capi.World.Player;
            var mouseSlot = player.InventoryManager.GetOwnInventory("mouse")?[0];
            if (mouseSlot == null || !mouseSlot.Empty) return 0;   // never sort with an item on the cursor

            int moves = 0;
            int guard = cells.Count * 6;

            void Click(int i)
            {
                var op = new ItemStackMoveOperation(capi.World, EnumMouseButton.Left,
                    0, (EnumMergePriority)0, 0) { ActingPlayer = player };
                var packet = cells[i].s.Inventory.ActivateSlot(cells[i].slotId, mouseSlot, ref op);
                if (packet != null) cells[i].s.SendPacket(packet);
                moves++;
            }

            string MouseKey() => mouseSlot.Empty ? null : ItemKeyOf(mouseSlot.Itemstack);

            for (int i = 0; i < cells.Count && guard > 0; i++)
            {
                while (KeyAt(i) != desired[i] && guard-- > 0)
                {
                    if (mouseSlot.Empty)
                    {
                        if (desired[i] == null)
                        {
                            Click(i);   // cell should be empty: pick its stack up, place it later
                            continue;
                        }
                        // Fetch the wanted item from any later misplaced cell.
                        int src = -1;
                        for (int j = i + 1; j < cells.Count; j++)
                        {
                            if (KeyAt(j) == desired[i] && KeyAt(j) != desired[j]) { src = j; break; }
                        }
                        if (src < 0) break;   // nothing available: best effort, move on
                        Click(src);
                        Click(i);   // place (or swap into) the target cell
                    }
                    else
                    {
                        // Cursor holds a displaced stack: put it where it belongs, or park it
                        // in a cell that wants nothing.
                        string mk = MouseKey();
                        int dst = -1;
                        for (int j = i; j < cells.Count; j++)
                        {
                            if (desired[j] == mk && KeyAt(j) != mk) { dst = j; break; }
                        }
                        if (dst < 0)
                        {
                            for (int j = cells.Count - 1; j >= 0; j--)
                            {
                                if (desired[j] == null && KeyAt(j) == null) { dst = j; break; }
                            }
                        }
                        if (dst < 0) break;   // nowhere sensible: leave it for the final flush
                        Click(dst);
                    }
                }
            }

            // Never end with a stack glued to the cursor: flush it into any empty cell.
            if (!mouseSlot.Empty)
            {
                for (int j = cells.Count - 1; j >= 0; j--)
                {
                    if (KeyAt(j) == null) { Click(j); break; }
                }
            }

            return moves;
        }
    }
}
