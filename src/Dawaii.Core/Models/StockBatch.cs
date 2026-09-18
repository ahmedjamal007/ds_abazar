using System;
using Dawaii.Core.Services;

namespace Dawaii.Core.Models
{
    /// <summary>
    /// A received shipment of an item with its own batch number, expiry and prices (FR-INV-02).
    /// <see cref="QuantityUnits"/> is remaining stock counted in SINGLE units.
    ///
    /// Pricing (V1.7): the admin types only the BOX purchase price, the BOX selling price and how many
    /// strips are in a box. Every strip/unit figure below is a computed property derived from those
    /// three — they have no setters, so a stored strip price can never disagree with its box price.
    /// </summary>
    public class StockBatch
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public int QuantityUnits { get; set; }
        public DateTime? ExpiryDate { get; set; }

        /// <summary>Supplier/manufacturer batch (lot) number as printed on the carton. Optional.</summary>
        public string BatchNumber { get; set; }

        /// <summary>Strips contained in one box for THIS shipment (packaging can change between
        /// shipments, so it is captured per batch). At least 1.</summary>
        public int StripsPerBox { get; set; } = 1;

        /// <summary>Single units in one strip, carried from the item at receipt so this batch can
        /// derive a per-unit price on its own. At least 1.</summary>
        public int UnitsPerStrip { get; set; } = 1;

        /// <summary>What one BOX cost from the supplier — entered by the user.</summary>
        public decimal BoxPurchasePrice { get; set; }

        /// <summary>What one BOX sells for — entered by the user. No markup is applied to it.</summary>
        public decimal BoxSellingPrice { get; set; }

        public DateTime ReceivedAt { get; set; }
        public bool IsDisposed { get; set; }

        // ---------------- derived, read-only ----------------

        /// <summary>Purchase price of one strip = box purchase ÷ strips per box (3200 ÷ 4 = 800).</summary>
        public decimal StripPurchasePrice => BatchPricing.StripFromBox(BoxPurchasePrice, StripsPerBox);

        /// <summary>Selling price of one strip = box selling ÷ strips per box (4000 ÷ 4 = 1000).</summary>
        public decimal StripSellingPrice => BatchPricing.StripFromBox(BoxSellingPrice, StripsPerBox);

        /// <summary>Cost of one single unit — what FEFO charges each allocated unit at.</summary>
        public decimal PurchasePrice => BatchPricing.UnitFromBox(BoxPurchasePrice, StripsPerBox, UnitsPerStrip);

        /// <summary>Selling price of one single unit.</summary>
        public decimal SellingPrice => BatchPricing.UnitFromBox(BoxSellingPrice, StripsPerBox, UnitsPerStrip);

        /// <summary>Single units contained in one box of this batch.</summary>
        public int UnitsPerBox => BatchPricing.UnitsPerBox(StripsPerBox, UnitsPerStrip);
    }
}
