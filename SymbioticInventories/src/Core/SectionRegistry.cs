using System;
using System.Collections.Generic;
using System.Linq;
using SymbioticInventories.Integration;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace SymbioticInventories.Core
{
    /// <summary>
    /// Turns "what the player currently has access to" into the ordered list of sections
    /// the master window draws.
    /// </summary>
    public class SectionRegistry
    {
        private readonly ICoreClientAPI capi;
        private readonly DialogCaptureService capture;
        private readonly ShelfDiscoveryService shelves;

        public SectionRegistry(ICoreClientAPI capi, DialogCaptureService capture, ShelfDiscoveryService shelves)
        {
            this.capi = capi;
            this.capture = capture;
            this.shelves = shelves;
        }

        public List<InventorySection> Build()
        {
            var sections = new List<InventorySection>();
            var player = capi.World?.Player;
            if (player == null) return sections;

            var im = player.InventoryManager;
            int badge = 0;

            AddCrafting(sections, im);
            AddBagSlots(sections, im);
            AddBackpacks(sections, im, ref badge);
            AddCaptured(sections, ref badge);
            AddShelves(sections, ref badge);
            // Deliberately no hotbar section: the vanilla hotbar HUD is permanently on
            // screen anyway, so a copy in the window would only spend rows repeating the one
            // inventory the player can already always see.

            return sections;
        }

        /// <summary>
        /// FoodShelves ambient containers near the player: crock shelves, bread/pie
        /// shelves, sacks, baskets. Each keeps its real spatial arrangement - one shelf
        /// level per row (FixedColumns) - and lays into the flow as a rigid brick. Cells
        /// are synthetic-interaction (no packet route exists): a click acts as a real
        /// right-click on that cell's segment. Added LAST so bricks band together below
        /// the flowing ribbons.
        /// </summary>
        private void AddShelves(List<InventorySection> sections, ref int badge)
        {
            if (shelves == null) return;
            shelves.ClearFacades();
            foreach (var shelf in shelves.Discover())
            {
                var sh = shelf;
                int n = ++badge;

                // Bulk containers show FACADE cells: one display-only cell per segment
                // carrying the live total ("flour x188"); a click on cell c acts on that
                // segment (its first inventory slot indexes the segment for the service).
                var inv = sh.Facade ? shelves.BuildFacade(sh) : sh.Inventory;
                int slots = sh.Cols > 0 ? sh.Rows * sh.Cols : sh.Inventory.Count;
                int per = Math.Max(1, sh.ItemsPerSegment);

                sections.Add(new InventorySection
                {
                    Id = "shelf:" + sh.Pos,
                    Label = sh.Label,
                    Kind = SectionKind.GroundContainer,
                    Number = n,
                    Accent = SectionPalette.ForNumber(n),
                    Inventory = inv,
                    SlotIds = Enumerable.Range(0, Math.Min(slots, inv.Count)).ToArray(),
                    FixedColumns = sh.Cols,
                    Icon = sh.Icon,
                    GhostIcons = sh.GhostIcons,
                    GroupKey = "foodshelves",
                    SendPacket = _ => { },   // no packet route - cells are click-synthesized
                    OnCellClick = sh.Facade
                        ? c => shelves.InteractCell(sh, c * per)
                        : slot => shelves.InteractCell(sh, slot)
                });
            }
        }

        // ---- player inventories --------------------------------------------------

        private void AddCrafting(List<InventorySection> sections, IPlayerInventoryManager im)
        {
            var inv = im.GetOwnInventory(GlobalConstants.craftingInvClassName);
            if (inv == null) return;

            // The crafting inventory is an NxN grid plus a trailing output slot.
            int gridCells = inv.Count - 1;
            int side = (int)Math.Round(Math.Sqrt(Math.Max(gridCells, 1)));
            if (side * side != gridCells) side = 3;

            sections.Add(new InventorySection
            {
                Id = "crafting",
                Label = Lang.Get("symbioticinventories:section-crafting"),
                Kind = SectionKind.Crafting,
                Accent = SectionPalette.Neutral,
                Inventory = inv,
                SlotIds = Enumerable.Range(0, side * side).ToArray(),
                FixedColumns = side,   // a crafting grid is square because its recipes are
                SendPacket = SendPlayerPacket
            });
        }

        /// <summary>The four equipped-bag slots themselves - the bags, not their contents.</summary>
        private void AddBagSlots(List<InventorySection> sections, IPlayerInventoryManager im)
        {
            var inv = im.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (inv == null) return;

            var ids = new List<int>();
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i] is ItemSlotBackpack) ids.Add(i);
            }
            if (ids.Count == 0) return;

            sections.Add(new InventorySection
            {
                Id = "bagslots",
                Label = Lang.Get("symbioticinventories:section-bagslots"),
                Kind = SectionKind.BackpackSlots,
                Accent = SectionPalette.Neutral,
                Inventory = inv,
                SlotIds = ids.ToArray(),
                FixedColumns = Math.Max(ids.Count, 1),   // the worn bags read as one row
                SendPacket = SendPlayerPacket
            });
        }

        /// <summary>
        /// One section per equipped bag. This is the core of the feature: the backpack
        /// inventory is a single flat slot list, and BagIndex is the only thing that says
        /// which physical bag a slot lives in.
        /// </summary>
        private void AddBackpacks(List<InventorySection> sections, IPlayerInventoryManager im, ref int badge)
        {
            var inv = im.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (inv == null) return;

            var byBag = new Dictionary<int, List<int>>();
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i] is ItemSlotBagContent c)
                {
                    if (!byBag.TryGetValue(c.BagIndex, out var list)) byBag[c.BagIndex] = list = new List<int>();
                    list.Add(i);
                }
            }

            foreach (var bagIndex in byBag.Keys.OrderBy(k => k))
            {
                int n = ++badge;
                sections.Add(new InventorySection
                {
                    Id = "backpack:" + bagIndex,
                    Label = BagName(inv, bagIndex),
                    SubLabel = Lang.Get("symbioticinventories:worn"),
                    Kind = SectionKind.Backpack,
                    Number = n,
                    Accent = SectionPalette.ForNumber(n),
                    Inventory = inv,
                    SlotIds = byBag[bagIndex].ToArray(),
                    Icon = BagStack(inv, bagIndex),
                    GroupKey = "backpack",
                    SendPacket = SendPlayerPacket
                });
            }
        }

        /// <summary>Reads the bag's own item name so the label matches what the player is wearing.</summary>
        private string BagName(IInventory inv, int bagIndex)
        {
            var stack = BagStack(inv, bagIndex);
            return stack?.GetName() ?? Lang.Get("symbioticinventories:section-backpack-n", bagIndex + 1);
        }

        /// <summary>The n-th worn bag's own itemstack, for the vessel row.</summary>
        private ItemStack BagStack(IInventory inv, int bagIndex)
        {
            int seen = 0;
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i] is ItemSlotBackpack)
                {
                    if (seen++ == bagIndex) return inv[i].Itemstack;
                }
            }
            return null;
        }

        // ---- captured containers -------------------------------------------------

        private void AddCaptured(List<InventorySection> sections, ref int badge)
        {
            foreach (var cap in capture.Captured)
            {
                if (cap.Inventory == null || cap.Inventory.Count == 0) continue;

                int n = ++badge;
                sections.Add(new InventorySection
                {
                    Id = "captured:" + cap.Sequence,
                    Label = cap.Title,
                    SubLabel = DescribeWhere(cap),
                    Kind = ClassifyCaptured(cap),
                    Number = n,
                    Accent = SectionPalette.ForNumber(n),
                    Inventory = cap.Inventory,
                    SlotIds = Enumerable.Range(0, cap.Inventory.Count).ToArray(),
                    Icon = CapturedIcon(cap),
                    PortraitEntity = cap.OwningEntity,
                    GroupKey = CapturedGroupKey(cap),
                    SendPacket = cap.SendPacket
                });
            }
        }

        /// <summary>
        /// Distinguishes a crate lashed to a boat from a chest on the ground. The test is
        /// behavioural rather than a type check against any particular boat mod, so any
        /// vessel built on the vanilla seat/attachable behaviours classifies correctly.
        /// </summary>
        private SectionKind ClassifyCaptured(CapturedDialog cap)
        {
            if (cap.OwningEntity == null) return SectionKind.GroundContainer;

            var ent = cap.OwningEntity;
            bool seatable = ent.GetBehavior<EntityBehaviorSeatable>() != null;
            bool ridable = ent.GetBehavior<EntityBehaviorRideable>() != null;

            if (seatable && !ridable) return SectionKind.Vehicle; // boats, carts, rafts
            if (ridable) return SectionKind.Mount;                // elk, horses, pack animals
            return SectionKind.Mount;
        }

        /// <summary>
        /// The vessel-row icon: a block container shows its own block; an entity container
        /// (elk saddlebag, boat crate) shows the attached bag item whose contents this dialog
        /// is displaying - so the tile pictures the actual saddlebag instead of a blank box.
        /// </summary>
        private ItemStack CapturedIcon(CapturedDialog cap)
        {
            if (cap.BlockPosition != null)
            {
                var block = capi.World.BlockAccessor.GetBlock(cap.BlockPosition);
                return block == null || block.Id == 0 ? null : new ItemStack(block);
            }
            return AttachedBagStack(cap);
        }

        /// <summary>
        /// Finds the held-bag item on the owning entity that this dialog is showing. Prefers
        /// the bag whose slot count matches the open inventory (disambiguates a saddlebag from
        /// a pannier); falls back to the first container bag on the animal.
        /// </summary>
        private ItemStack AttachedBagStack(CapturedDialog cap)
        {
            var inv = cap.OwningEntity?.GetBehavior<EntityBehaviorAttachable>()?.Inventory;
            if (inv == null) return null;

            ItemStack first = null;
            foreach (var slot in inv)
            {
                if (slot == null || slot.Empty) continue;
                var bag = slot.Itemstack.Collectible?.GetCollectibleInterface<IHeldBag>();
                if (bag == null) continue;
                first ??= slot.Itemstack;
                if (bag.GetQuantitySlots(slot.Itemstack) == cap.Inventory.Count) return slot.Itemstack;
            }
            return first;
        }

        /// <summary>Container-type key: block code's first path part, so all chests group
        /// together, all vessels together, regardless of variant/material. Entity containers
        /// group by the bag item so all saddlebags sit together.</summary>
        private string CapturedGroupKey(CapturedDialog cap)
        {
            if (cap.BlockPosition == null)
                return AttachedBagStack(cap)?.Collectible?.Code?.FirstCodePart() ?? "entity";
            var block = capi.World.BlockAccessor.GetBlock(cap.BlockPosition);
            return block?.Code?.FirstCodePart() ?? "container";
        }

        private string DescribeWhere(CapturedDialog cap)
        {
            var player = capi.World?.Player?.Entity;

            if (cap.OwningEntity != null)
            {
                var mount = player?.MountedOn?.Entity;
                if (mount != null && mount.EntityId == cap.OwningEntity.EntityId)
                {
                    return Lang.Get("symbioticinventories:aboard");
                }
                return cap.OwningEntity.GetName();
            }

            if (cap.BlockPosition != null && player != null)
            {
                int dist = (int)Math.Round(player.Pos.AsBlockPos.DistanceTo(cap.BlockPosition));
                return Lang.Get("symbioticinventories:blocks-away", dist);
            }

            return null;
        }

        // ---- packet routing ------------------------------------------------------

        /// <summary>Player-owned inventories talk straight down the client channel.</summary>
        private void SendPlayerPacket(object packet) => capi.Network.SendPacketClient(packet);
    }
}
