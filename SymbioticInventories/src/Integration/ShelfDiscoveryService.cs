using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

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

        /// <summary>Representative items of what this shelf is ALLOWED to store (an egg
        /// for the egg shelf), up to two - drawn as ghost hints in its empty cells.</summary>
        public ItemStack[] GhostIcons;

        /// <summary>The container handles inventory packets itself (barrels): its cells
        /// are REAL slots routed through the block-entity envelope - no synthetic clicks,
        /// no gesture rules, ordinary drag-and-drop.</summary>
        public bool RealSlots;

        /// <summary>Per-CELL ghost candidates (barrels: item-slot candidates for cell 0,
        /// liquid candidates for cell 1). A cell's array CYCLES through its options -
        /// the "flashing between liquids and salt" hint. Null: use GhostIcons.</summary>
        public ItemStack[][] CellGhosts;

        /// <summary>Vessel-row grouping key for this container family.</summary>
        public string GroupKey = "foodshelves";

        /// <summary>Wildcard codes of what this container accepts, when known. Used to
        /// read click INTENT: a held item that cannot go in is not a deposit attempt -
        /// the click takes instead (real bug: a quern in the hand deadened sack clicks).
        /// Null: unknown, assume anything fits.</summary>
        public string[] AcceptedCodes;
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
        private PropertyInfo invProp, shelfCountProp, segsProp, perSegProp, attrCheckProp;

        // Restriction lookup for the ghost hints: BE.AttributeCheck is the key into the
        // FoodShelves.Core mod system's restrictions dictionary; each entry's
        // CollectibleCodes are wildcard patterns of what the shelf accepts.
        private object fsCore;
        private FieldInfo restrictionsField;
        private readonly Dictionary<string, ItemStack[]> ghostCache = new();
        private readonly HashSet<string> geomLogged = new();
        private readonly HashSet<string> ghostMissLogged = new();

        /// <summary>Restriction code lists read straight from FoodShelves' shipped config
        /// assets (config/restrictions/**), keyed by filename stem. The client-side asset
        /// manager loads these even though the mod applies them server-side - unlike the
        /// runtime dictionary, which may be empty on the client.</summary>
        private Dictionary<string, string[]> assetRestrictions;

        // Stone Bake Oven soft dependency: its baking-top type carries no stable
        // namespace across versions, so resolve by short name over loaded assemblies.
        private Type bakingTopType;
        private bool bakingProbed;

        private Type BakingTopType()
        {
            if (bakingProbed) return bakingTopType;
            bakingProbed = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var ty in asm.GetTypes())
                    {
                        if (ty.Name == "BlockEntityOvenBakingTop") { bakingTopType = ty; return ty; }
                    }
                }
                catch { /* dynamic/unloadable assemblies: skip */ }
            }
            return null;
        }

        private Dictionary<string, string[]> AssetRestrictions()
        {
            if (assetRestrictions != null) return assetRestrictions;
            assetRestrictions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var asset in capi.Assets.GetMany("config/restrictions", "foodshelves"))
                {
                    try
                    {
                        var jo = Vintagestory.API.Datastructures.JsonObject.FromJson(asset.ToText());
                        var arr = jo["CollectibleCodes"]?.AsArray();
                        if (arr == null) continue;
                        var codes = new List<string>();
                        foreach (var c in arr)
                        {
                            var v = c.AsString();
                            if (!string.IsNullOrEmpty(v)) codes.Add(v);
                        }
                        if (codes.Count == 0) continue;

                        string stem = asset.Location.Path;
                        int slash = stem.LastIndexOf('/');
                        if (slash >= 0) stem = stem.Substring(slash + 1);
                        stem = stem.Replace(".json", "");
                        assetRestrictions[stem] = codes.ToArray();
                    }
                    catch { /* one bad file must not kill the rest */ }
                }
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Reading FoodShelves restriction assets failed: {0}", e.Message);
            }
            logger.Notification("[SymbioticInventories] Loaded {0} FoodShelves restriction file(s) for ghost hints.", assetRestrictions.Count);
            return assetRestrictions;
        }

        /// <summary>Opens the master window; wired by the mod system.</summary>
        public Action OpenWindow;

        public void Start(ICoreClientAPI api, ILogger log)
        {
            capi = api;
            logger = log;
            // Facade cells display live aggregates; keep them current as sacks fill/drain.
            api.Event.RegisterGameTickListener(UpdateFacades, 250);
            // The launch gesture below rides the in-world action stream.
            api.Input.InWorldAction += OnInWorldAction;
        }

        /// <summary>
        //// Launch gesture (user ask - shelves never open a dialog, so nothing triggered
        /// the window from them): right-clicking a FoodShelves container with an EMPTY
        /// hand on an EMPTY segment does nothing natively, so that free gesture opens the
        /// master window instead. Filled segments keep their native take, held items keep
        /// their native put - only the dead input is repurposed.
        /// </summary>
        private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
        {
            try
            {
                if (!on || action != EnumEntityAction.RightMouseDown || OpenWindow == null) return;

                var sel = capi.World?.Player?.CurrentBlockSelection;
                if (sel?.Position == null) return;
                var be = capi.World.BlockAccessor.GetBlockEntity(sel.Position);
                if (be == null) return;

                var t = BaseType();
                bool isShelf = t != null && t.IsInstanceOfType(be);
                bool isCrate = be is BlockEntityCrate;
                if (!isShelf && !isCrate) return;

                var hand = capi.World.Player.InventoryManager.ActiveHotbarSlot;
                if (hand != null && !hand.Empty) return;

                IInventory inv = isCrate
                    ? ((BlockEntityCrate)be).Inventory
                    : invProp.GetValue(be) as IInventory;
                if (inv == null) return;
                int per = isCrate ? inv.Count : Math.Max(1, (perSegProp?.GetValue(be) as int?) ?? 1);
                int seg = isCrate ? 0 : Math.Max(0, sel.SelectionBoxIndex);
                for (int i = seg * per; i < (seg + 1) * per && i < inv.Count; i++)
                {
                    if (!(inv[i]?.Empty ?? true)) return;   // has something: native take wins
                }

                OpenWindow();
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Shelf launch gesture failed: {0}", e.Message);
            }
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
            attrCheckProp = AccessTools.Property(beBaseType, "AttributeCheck");

            var coreType = AccessTools.TypeByName("FoodShelves.Core");
            restrictionsField = coreType == null ? null : AccessTools.Field(coreType, "restrictions");
            fsCore = coreType == null ? null : capi.ModLoader.GetModSystem(coreType.FullName);
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
            var t = BaseType();   // may be null: vanilla crates integrate regardless
            var center = capi.World?.Player?.Entity?.Pos?.AsBlockPos;
            if (center == null) return new List<AmbientShelf>();

            var ba = capi.World.BlockAccessor;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -3; dy <= 3; dy++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                var p = center.AddCopy(dx, dy, dz);
                var be = ba.GetBlockEntity(p);
                if (be == null) continue;

                // VANILLA BARRELS: they handle their own inventory packets (their dialog's
                // route), so - unlike shelves - their two cells are REAL slots: item and
                // liquid, ordinary drag-and-drop through the block-entity envelope. One
                // brick per barrel, a whole brewery wall fits the grid. SEALED barrels are
                // skipped: they are busy curing and even vanilla locks their dialog.
                if (be is BlockEntityBarrel barrel)
                {
                    if (barrel.Sealed || barrel.Inventory == null || barrel.Inventory.Count < 1) continue;
                    var bblock2 = ba.GetBlock(p);
                    var bstack2 = bblock2 != null && bblock2.Id != 0 ? new ItemStack(bblock2) : null;
                    found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                    {
                        Pos = p,
                        Inventory = barrel.Inventory,
                        Rows = 1,
                        Cols = barrel.Inventory.Count,
                        ItemsPerSegment = 1,
                        Facade = false,
                        RealSlots = true,
                        GroupKey = "barrel",
                        Label = bstack2?.GetName() ?? "?",
                        Icon = bstack2,
                        CellGhosts = BarrelGhosts()
                    }));
                    continue;
                }

                // VANILLA CRATES: the same species as the shelves - no dialog, put/take
                // by right-click, one item type in bulk - so they present as a one-cell
                // facade with the crate's live total (user ask). Native per-click
                // semantics; the launch gesture and markers apply like any shelf.
                if (be is BlockEntityCrate crate)
                {
                    var cinv = crate.Inventory;
                    if (cinv == null || cinv.Count == 0) continue;
                    var cblock = ba.GetBlock(p);
                    var cstack = cblock != null && cblock.Id != 0 ? new ItemStack(cblock) : null;
                    if (geomLogged.Add(cblock?.Code?.Path ?? "crate"))
                    {
                        logger.Notification("[SymbioticInventories] Crate '{0}': inv={1} -> facade 1x1",
                            cblock?.Code?.Path, cinv.Count);
                    }
                    found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                    {
                        Pos = p,
                        Inventory = cinv,
                        Rows = 1,
                        Cols = 1,
                        ItemsPerSegment = cinv.Count,   // the whole crate is one segment
                        Facade = true,
                        GroupKey = "crate",
                        Label = cstack?.GetName() ?? "?",
                        Icon = cstack
                    }));
                    continue;
                }

                // STONE BAKE OVEN baking surface: display-style (click-to-place doughs on
                // spots, no dialog), so it presents as a per-item grid like a shelf. The
                // firing state stays at the block - only the spots join the window.
                var btT = BakingTopType();
                if (btT != null && btT.IsInstanceOfType(be) && be is BlockEntityContainer btc
                    && btc.Inventory != null && btc.Inventory.Count > 0)
                {
                    int n = btc.Inventory.Count;
                    int bcols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
                    var bblock = ba.GetBlock(p);
                    var bstack = bblock != null && bblock.Id != 0 ? new ItemStack(bblock) : null;
                    if (geomLogged.Add(bblock?.Code?.Path ?? "bakingtop"))
                    {
                        logger.Notification("[SymbioticInventories] Baking top '{0}': inv={1} -> grid {2}x{3}",
                            bblock?.Code?.Path, n, bcols, (n + bcols - 1) / bcols);
                    }
                    found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                    {
                        Pos = p,
                        Inventory = btc.Inventory,
                        Rows = (n + bcols - 1) / bcols,
                        Cols = bcols,
                        ItemsPerSegment = 1,
                        Facade = false,
                        GroupKey = "oven",
                        Label = bstack?.GetName() ?? "?",
                        Icon = bstack
                    }));
                    continue;
                }

                if (t == null || !t.IsInstanceOfType(be)) continue;

                var inv = invProp.GetValue(be) as IInventory;
                if (inv == null || inv.Count == 0) continue;

                int shelves = (shelfCountProp?.GetValue(be) as int?) ?? 1;
                int segs = (segsProp?.GetValue(be) as int?) ?? 1;
                int perSeg = (perSegProp?.GetValue(be) as int?) ?? 1;

                // Multi-item segments ALWAYS present as facade cells-of-N (user rule: an
                // egg shelf whose columns hold 6 is cells of 6 - "1-6 maps easily") -
                // even when the declared geometry does not match the inventory exactly;
                // the segment count then derives from the inventory itself. Per-item
                // shelves keep their real X x Y slot grid when the geometry checks out;
                // only odd per-item shapes fall back to a flowing ribbon.
                bool geomOk = shelves >= 1 && segs >= 1 && perSeg >= 1
                           && shelves * segs * perSeg == inv.Count;
                bool facade = perSeg > 1;
                bool structured = geomOk && perSeg == 1 && segs <= 12;

                int fRows = 0, fCols = 0;
                if (facade)
                {
                    if (geomOk && segs <= 12)
                    {
                        fRows = shelves; fCols = segs;
                    }
                    else
                    {
                        int segCount = (inv.Count + perSeg - 1) / perSeg;
                        fCols = Math.Min(Math.Max(1, segCount), 12);
                        fRows = (segCount + fCols - 1) / fCols;
                    }
                }
                else if (structured)
                {
                    fRows = shelves; fCols = segs;
                }

                var block = ba.GetBlock(p);
                var stack = block != null && block.Id != 0 ? new ItemStack(block) : null;

                // One-time geometry note per shelf type: ground truth for shaping bugs.
                if (geomLogged.Add(block?.Code?.Path ?? "?"))
                {
                    logger.Notification(
                        "[SymbioticInventories] Shelf '{0}': shelves={1} segs={2} perSeg={3} inv={4} -> {5} {6}x{7}",
                        block?.Code?.Path, shelves, segs, perSeg, inv.Count,
                        facade ? "facade" : (structured ? "grid" : "ribbon"), fCols, fRows);
                }

                found.Add((dx * dx + dy * dy + dz * dz, new AmbientShelf
                {
                    Pos = p,
                    Inventory = inv,
                    Rows = fRows,
                    Cols = fCols,
                    ItemsPerSegment = Math.Max(1, perSeg),
                    Facade = facade,
                    Label = stack?.GetName() ?? "?",
                    Icon = stack,
                    GhostIcons = GhostsFor(attrCheckProp?.GetValue(be) as string),
                    AcceptedCodes = CodesFor(attrCheckProp?.GetValue(be) as string)
                }));
            }

            // Family first, then type, then distance: flour sacks sit together, each
            // shelf type together, barrels together (user ask) - adjacent bricks in the
            // grid and adjacent tiles in the vessel row. Distance only breaks ties within
            // a type, and coordinates keep the order stable while standing still.
            found.Sort((a, b) =>
            {
                int c = string.Compare(a.shelf.GroupKey, b.shelf.GroupKey, StringComparison.Ordinal);
                if (c != 0) return c;
                c = string.Compare(a.shelf.Label, b.shelf.Label, StringComparison.Ordinal);
                if (c != 0) return c;
                c = a.dist.CompareTo(b.dist);
                if (c != 0) return c;
                c = a.shelf.Pos.X.CompareTo(b.shelf.Pos.X);
                if (c != 0) return c;
                c = a.shelf.Pos.Y.CompareTo(b.shelf.Pos.Y);
                return c != 0 ? c : a.shelf.Pos.Z.CompareTo(b.shelf.Pos.Z);
            });
            return found.ConvertAll(f => f.shelf);
        }

        /// <summary>Cheap change signature for the tick watcher: recompose when the set of
        /// nearby shelves changes OR any cell flips between empty and filled (the ghost
        /// hints live in empty cells and are placed at compose time).</summary>
        public long Signature()
        {
            long sig = 17;
            foreach (var sh in Discover())
            {
                sig = sig * 31 + sh.Pos.GetHashCode();
                sig = sig * 31 + sh.Inventory.Count;
                foreach (var slot in sh.Inventory)
                {
                    sig = sig * 2 + ((slot?.Empty ?? true) ? 0 : 1);
                }
            }
            return sig;
        }

        private ItemStack[][] barrelGhosts;

        /// <summary>
        /// What can go in a barrel, read from the game's own BARREL RECIPE list (client-
        /// synced): every recipe ingredient, split into liquids (water, brine, milk...)
        /// for the liquid cell and solids (salt, hides, meat...) for the item cell -
        /// distinct by code, capped at eight each. The cells cycle through their lists.
        /// </summary>
        private ItemStack[][] BarrelGhosts()
        {
            if (barrelGhosts != null) return barrelGhosts;
            var items = new List<ItemStack>();
            var liquids = new List<ItemStack>();
            var seen = new HashSet<string>();
            try
            {
                var recipes = capi.ModLoader.GetModSystem<RecipeRegistrySystem>()?.BarrelRecipes;
                if (recipes != null)
                {
                    // INPUTS, not products: collect every recipe OUTPUT first and keep it
                    // out of the hints - a pickled vegetable is what comes OUT of the
                    // barrel, the hint must show the fresh vegetable that goes in.
                    var outputs = new HashSet<string>();
                    foreach (var r in recipes)
                    {
                        var os = r?.Output?.ResolvedItemstack;
                        if (os?.Collectible?.Code != null) outputs.Add(os.Collectible.Code.ToString());
                    }

                    foreach (var r in recipes)
                    {
                        if (r?.Ingredients == null) continue;
                        foreach (var ing in r.Ingredients)
                        {
                            var st = ing?.ResolvedItemStack;
                            if (st == null && ing?.Code != null)
                            {
                                // Wildcard ingredient ("any vegetable"): stand in the
                                // first live match that is not itself a barrel output.
                                foreach (var item in capi.World.SearchItems(ing.Code))
                                {
                                    if (item?.Code != null && !outputs.Contains(item.Code.ToString()))
                                    {
                                        st = new ItemStack(item);
                                        break;
                                    }
                                }
                                if (st == null)
                                {
                                    foreach (var bl in capi.World.SearchBlocks(ing.Code))
                                    {
                                        if (bl?.Code != null && !outputs.Contains(bl.Code.ToString()))
                                        {
                                            st = new ItemStack(bl);
                                            break;
                                        }
                                    }
                                }
                            }
                            if (st?.Collectible?.Code == null) continue;
                            string codeStr = st.Collectible.Code.ToString();
                            if (outputs.Contains(codeStr)) continue;   // products never hint
                            if (!seen.Add(codeStr)) continue;

                            bool isLiquid = BlockLiquidContainerBase.GetContainableProps(st) != null;
                            var target = isLiquid ? liquids : items;
                            if (target.Count < 8) target.Add(st.Clone());
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Barrel recipe scan failed: {0}", e.Message);
            }
            barrelGhosts = new[]
            {
                items.Count > 0 ? items.ToArray() : null,
                liquids.Count > 0 ? liquids.ToArray() : null
            };
            return barrelGhosts;
        }

        /// <summary>
        /// Up to two representative items of what a shelf accepts: BE.AttributeCheck keys
        /// the FoodShelves restrictions dictionary, whose CollectibleCodes are wildcard
        /// patterns ("*:flour-*"); the first live item matching each pattern stands in.
        /// One representative per pattern - a two-pattern shelf shows a split pair.
        /// Cached per key; every step guarded (absent = simply no ghost hints).
        /// </summary>
        private ItemStack[] GhostsFor(string key)
        {
            if (key == null || key.Length == 0) return null;
            if (ghostCache.TryGetValue(key, out var cached)) return cached;

            ItemStack[] result = null;
            try
            {
                var codes = CodesFor(key);
                if (codes != null)
                {
                    var foundGhosts = new List<ItemStack>();
                    foreach (var code in codes)
                    {
                        if (foundGhosts.Count >= 2) break;
                        var loc = new AssetLocation(code);
                        var items = capi.World.SearchItems(loc);
                        if (items != null && items.Length > 0)
                        {
                            foundGhosts.Add(new ItemStack(items[0]));
                            continue;
                        }
                        var blocks = capi.World.SearchBlocks(loc);
                        if (blocks != null && blocks.Length > 0)
                        {
                            foundGhosts.Add(new ItemStack(blocks[0]));
                        }
                    }
                    if (foundGhosts.Count > 0) result = foundGhosts.ToArray();
                }
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Ghost hint lookup for '{0}' failed: {1}", key, e.Message);
            }

            ghostCache[key] = result;
            return result;
        }

        private readonly Dictionary<string, string[]> codesCache = new();

        /// <summary>The wildcard codes a shelf accepts: runtime dictionary first
        /// (authoritative when populated), the mod's shipped config assets second
        /// (always available client-side). Cached per key; null when unknown.</summary>
        private string[] CodesFor(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (codesCache.TryGetValue(key, out var cached)) return cached;

            string[] codes = null;
            try
            {
                var dict = fsCore == null ? null : restrictionsField?.GetValue(fsCore) as System.Collections.IDictionary;
                if (dict != null && dict.Contains(key))
                {
                    var rd = dict[key];
                    codes = rd == null ? null
                        : AccessTools.Property(rd.GetType(), "CollectibleCodes")?.GetValue(rd) as string[];
                }
                if (codes == null || codes.Length == 0)
                {
                    AssetRestrictions().TryGetValue(key, out codes);
                }
                if ((codes == null || codes.Length == 0) && ghostMissLogged.Add(key))
                {
                    logger.Notification(
                        "[SymbioticInventories] No restriction codes for shelf key '{0}' (runtime entries: {1}, asset files: {2}).",
                        key, dict?.Count ?? -1, AssetRestrictions().Count);
                }
            }
            catch (Exception e)
            {
                logger.Warning("[SymbioticInventories] Restriction lookup for '{0}' failed: {1}", key, e.Message);
            }
            codesCache[key] = codes;
            return codes;
        }

        /// <summary>Whether the stack could go in, per the container's accepted codes.
        /// Unknown codes assume yes - native rules still decide server-side.</summary>
        private static bool CouldGoIn(string[] patterns, ItemStack st)
        {
            if (patterns == null || patterns.Length == 0) return true;
            var code = st?.Collectible?.Code;
            if (code == null) return false;
            foreach (var pat in patterns)
            {
                try
                {
                    if (Vintagestory.API.Util.WildcardUtil.Match(new AssetLocation(pat), code)) return true;
                }
                catch { /* malformed pattern: skip */ }
            }
            return false;
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

        // ---- native bulk gestures: the control-packet sandwich --------------------
        //
        // FoodShelves decides interaction QUANTITY server-side from two booleans on the
        // player's controls: ShiftKey picks put-vs-take on bulk slots, CtrlKey picks
        // whole-stack-vs-one. Those flags reach the server ONLY as tiny MoveKeyChange
        // packets (Packet_Client Id=21) riding the SAME ordered TCP stream as the hand-
        // interaction packet - so "flags down, interact, flags restored" is guaranteed to
        // be processed in exactly that order, and ONE interaction moves a whole stack with
        // the mod's own server-side rules. (Engine and FoodShelves 3.0.5 IL both verified
        // by two independent reviews.) This replaced a 64-step interaction pump whose
        // client-side state machine kept reversing deposits into takes - see git history.

        private Type pktClientType, pktMoveType;
        private FieldInfo pktIdField, pktMoveField, moveKeyField, moveDownField;
        private bool forgeProbed, forgeOk;

        private bool ForgeAvailable()
        {
            if (forgeProbed) return forgeOk;
            forgeProbed = true;

            pktClientType = AccessTools.TypeByName("Packet_Client");
            pktMoveType = AccessTools.TypeByName("Packet_MoveKeyChange");
            pktIdField = pktClientType == null ? null : AccessTools.Field(pktClientType, "Id");
            pktMoveField = pktClientType == null ? null : AccessTools.Field(pktClientType, "MoveKeyChange");
            moveKeyField = pktMoveType == null ? null : AccessTools.Field(pktMoveType, "Key");
            moveDownField = pktMoveType == null ? null : AccessTools.Field(pktMoveType, "Down");

            forgeOk = pktIdField != null && pktMoveField != null
                   && moveKeyField != null && moveDownField != null;
            if (!forgeOk)
                logger.Warning("[SymbioticInventories] MoveKeyChange packet shape changed on this game build - shelf clicks fall back to single items.");
            return forgeOk;
        }

        /// <summary>One MoveKeyChange packet: "this control flag is now down/up".</summary>
        private void SendMoveKey(int key, bool down)
        {
            var move = Activator.CreateInstance(pktMoveType);
            moveKeyField.SetValue(move, key);
            moveDownField.SetValue(move, down ? 1 : 0);
            var pkt = Activator.CreateInstance(pktClientType);
            pktIdField.SetValue(pkt, 21);
            pktMoveField.SetValue(pkt, move);
            capi.Network.SendPacketClient(pkt);
        }

        /// <summary>
        /// One native interaction with forged modifier flags. The client controls are
        /// flipped only around the synchronous OnBlockInteractStart call (so client
        /// prediction moves the same amount the server will), then restored before the
        /// input tick can ever observe them; the server-side flags are restored to the
        /// client's REAL current view, so physically held keys never desync. While
        /// mounted the flags land on the seat's controls (exactly as physical keys do),
        /// so the forge degrades to the plain single-item interaction there - parity
        /// with the in-world gesture, not a regression.
        /// </summary>
        private void InteractForged(AmbientShelf shelf, int slotIndex, bool ctrl, bool shift)
        {
            var player = capi.World.Player;
            if (player.Entity.MountedOn != null)
            {
                // Unreachable via InteractCell (it refuses mounted clicks outright), kept
                // as defense: control-key packets go to the mount's controls while riding,
                // so a forged whole-stack flag could never reach the shelf mod anyway.
                Interact(shelf, slotIndex);
                return;
            }
            if (!ForgeAvailable())
            {
                Interact(shelf, slotIndex);
                return;
            }

            int ctrlKey = (int)EnumEntityAction.CtrlKey;
            int shiftKey = (int)EnumEntityAction.ShiftKey;
            SendMoveKey(ctrlKey, ctrl);
            SendMoveKey(shiftKey, shift);

            var controls = player.Entity.Controls;
            bool c0 = controls.CtrlKey, s0 = controls.ShiftKey;
            controls.CtrlKey = ctrl;
            controls.ShiftKey = shift;
            try
            {
                Interact(shelf, slotIndex);
            }
            finally
            {
                controls.CtrlKey = c0;
                controls.ShiftKey = s0;
                SendMoveKey(ctrlKey, c0);
                SendMoveKey(shiftKey, s0);
            }
        }

        /// <summary>
        /// A click on a shelf cell. STATELESS - every gesture is exactly one native
        /// interaction (no jobs, no timers, nothing a second click could reverse):
        ///   holding a stack in the active hand -> pour the whole stack in (one op)
        ///   stack on the mouse cursor          -> shuttle to a free hotbar slot, pour
        ///   empty-handed                       -> take a whole stack out (one op)
        ///   SHIFT-click                        -> move exactly ONE, either direction
        /// </summary>
        public void InteractCell(AmbientShelf shelf, int slotIndex)
        {
            // From the saddle, shelf cells refuse outright (user decision): the server
            // hardcodes control-key packets to the MOUNT's controls while riding, so the
            // whole-stack signal can never reach the shelf mod - and a sack that dribbles
            // single items reads as broken. Refuse with the reason instead.
            if (capi.World.Player.Entity.MountedOn != null)
            {
                capi.TriggerIngameError(this, "si-mounted",
                    Vintagestory.API.Config.Lang.Get("symbioticinventories:mounted-bulk"));
                return;
            }

            bool shiftClick = capi.Input.KeyboardKeyState[(int)GlKeys.LShift]
                           || capi.Input.KeyboardKeyState[(int)GlKeys.RShift];
            var im = capi.World.Player.InventoryManager;
            var hotbar = im.GetHotbarInventory();
            var hand = hotbar[im.ActiveHotbarSlotNumber];
            var mouse = im.MouseItemSlot;

            // Shift-click: move exactly one - put if the hand holds something, else take.
            if (shiftClick)
            {
                InteractForged(shelf, slotIndex, ctrl: false, shift: !hand.Empty);
                return;
            }

            // Whole-stack pour straight from the active hand - the natural in-world flow.
            // ONLY when the held item could actually go in: a quern (or sword, or shovel)
            // in the hand is not deposit intent, and treating it as one deadened the
            // click entirely (real bug). Non-shelvable hand falls through to TAKE.
            if (!hand.Empty && CouldGoIn(shelf.AcceptedCodes, hand.Itemstack))
            {
                InteractForged(shelf, slotIndex, ctrl: true, shift: true);
                return;
            }

            // Mouse-carried stack: one shuttle click into an empty hotbar slot (ordered on
            // the same stream, so the server has the stack in hand before the interact),
            // ONE forged pour, selection restored immediately (restoring a selection moves
            // no items). Leftover - sack full - returns to the cursor after the server
            // settles; with one op that is one small callback, not a state machine.
            if (mouse != null && !mouse.Empty && CouldGoIn(shelf.AcceptedCodes, mouse.Itemstack))
            {
                int empty = -1;
                for (int i = 0; i < 10 && i < hotbar.Count; i++)
                {
                    if (hotbar[i].Empty) { empty = i; break; }
                }
                if (empty < 0)
                {
                    logger.Notification("[SymbioticInventories] Depositing from the cursor needs one empty hotbar slot (or hold the stack in your hand).");
                    return;
                }

                int restore = im.ActiveHotbarSlotNumber;
                im.ActiveHotbarSlotNumber = empty;
                ClickSlot(hotbar, empty);   // carried -> working hand
                try
                {
                    InteractForged(shelf, slotIndex, ctrl: true, shift: true);
                }
                finally
                {
                    im.ActiveHotbarSlotNumber = restore;
                }

                capi.Event.RegisterCallback(_ =>
                {
                    try
                    {
                        if (!hotbar[empty].Empty && im.MouseItemSlot.Empty) ClickSlot(hotbar, empty);
                    }
                    catch (Exception e)
                    {
                        logger.Warning("[SymbioticInventories] Leftover return failed: {0}", e.Message);
                    }
                }, 400);
                return;
            }

            // Empty-handed: take a whole stack, then lift it onto the CURSOR - a vessel
            // pickup (user ask: "to my hand, not my belt"). FoodShelves delivers the stack
            // into the player's inventory itself, wherever it fits; snapshot the hotbar
            // first, and after the server settles, the slot that GREW is where it landed -
            // one click moves it to the cursor. If it landed in the bags instead, or the
            // cursor is busy by then, it simply stays where the mod put it.
            var before = new int[hotbar.Count];
            for (int i = 0; i < hotbar.Count; i++) before[i] = hotbar[i].StackSize;

            InteractForged(shelf, slotIndex, ctrl: true, shift: false);

            // The interaction runs the mod's CLIENT prediction synchronously, so the slot
            // the stack landed in is known right now - lift it to the cursor in the same
            // breath. The lift packet rides the same ordered stream as the take, so the
            // server performs take -> lift back-to-back and the stack never visibly rests
            // in the belt (the earlier 350ms settle showed exactly that layover - user).
            // If prediction didn't place it (unusual), one delayed retry catches it.
            bool LiftLanded()
            {
                if (!im.MouseItemSlot.Empty) return true;   // cursor busy: leave it in the belt
                for (int i = 0; i < hotbar.Count && i < before.Length; i++)
                {
                    if (hotbar[i].StackSize > before[i])
                    {
                        ClickSlot(hotbar, i);   // the landed stack -> cursor
                        return true;
                    }
                }
                return false;
            }

            if (!LiftLanded())
            {
                capi.Event.RegisterCallback(_ =>
                {
                    try { LiftLanded(); }
                    catch (Exception e)
                    {
                        logger.Warning("[SymbioticInventories] Take-to-cursor lift failed: {0}", e.Message);
                    }
                }, 300);
            }
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
