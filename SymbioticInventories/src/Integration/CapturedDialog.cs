using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SymbioticInventories.Integration
{
    /// <summary>
    /// A vanilla container dialog that Symbiotic Inventories has taken over.
    ///
    /// The original dialog object is deliberately left alive and registered: its
    /// OnGuiOpened/OnGuiClosed pair is what performs the open/close handshake with the
    /// server, and that handshake is what keeps the inventory synced to this client.
    /// We suppress only its *rendering* and re-draw its slots inside the master window.
    /// </summary>
    public class CapturedDialog
    {
        public GuiDialog Dialog;

        /// <summary>The inventory the dialog was showing.</summary>
        public IInventory Inventory;

        /// <summary>The dialog's own packet sender, which wraps slot ops in the right envelope.</summary>
        public Action<object> SendPacket;

        /// <summary>Set for block containers (chest, vessel, basket, ground bag); null for entity ones.</summary>
        public BlockPos BlockPosition;

        /// <summary>Set for entity containers (saddlebags, boat crates); null for block ones.</summary>
        public Entity OwningEntity;

        public string Title;

        /// <summary>Monotonic capture order, so section numbers stay stable while a dialog is open.</summary>
        public long Sequence;
    }
}
