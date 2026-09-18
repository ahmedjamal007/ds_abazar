using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>
    /// Barcode/QR storage. An item carries at most ONE code and a code identifies at most one item
    /// (V1.9, FR-QRC-01/06): a drug has a single barcode printed on its box, so the code is a property
    /// of the item rather than a list hanging off it. Items with no manufacturer barcode get an
    /// internal QR code instead — same single slot.
    /// </summary>
    public interface IItemCodeRepository
    {
        /// <summary>Finds the item holding a scanned value, or null (FR-QRC-03). Case-insensitive, so a
        /// QR read back as "dw-7" still finds "DW-7" — and both backends behave the same way.</summary>
        ItemCode FindByCode(string code);

        /// <summary>The item's code, or null when it has none.</summary>
        ItemCode GetByItem(int itemId);

        /// <summary>Sets the item's code, replacing whatever it had.</summary>
        void SetForItem(int itemId, string code);

        /// <summary>Drops the item's code, freeing that barcode for another item. No-op if it has none.</summary>
        void RemoveForItem(int itemId);
    }
}
