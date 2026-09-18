using System;

namespace Dawaii.Core.Models
{
    /// <summary>
    /// A sellable product. Prices are stored per SINGLE unit (see DECISIONS D-01).
    /// Unit tree: 1 Box = <see cref="StripsPerBox"/> strips; 1 Strip = <see cref="UnitsPerStrip"/> single units.
    ///
    /// The item's prices are not entered here (V1.7): they mirror the most recently received stock
    /// batch, whose BOX purchase and BOX selling prices the admin types in. The POS reads them from
    /// the item so a sale never has to care which batch FEFO happens to draw from.
    /// </summary>
    public class Item
    {
        public int Id { get; set; }

        /// <summary>The trade name the box is sold under ("polymol") — what staff search for and what
        /// prints on the receipt. Required.</summary>
        public string NameEn { get; set; }

        /// <summary>The scientific/generic name ("paracetamol"). Optional, but the whole point of
        /// keeping it is that two boxes with different trade names can be recognised as the same drug.</summary>
        public string GenericName { get; set; }

        public int UnitsPerStrip { get; set; } = 1;
        public int StripsPerBox { get; set; } = 1;

        /// <summary>Cost per single unit, from the latest received batch (authoritative cost for a
        /// given sale is the allocated batch's, via FEFO).</summary>
        public decimal PurchasePrice { get; set; }

        /// <summary>Selling price per single unit, from the latest received batch's box selling price.
        /// Null until the first batch arrives — unpriced items are hidden from the POS and cannot be sold.</summary>
        public decimal? SellingPrice { get; set; }

        /// <summary>
        /// True when the selling price was typed by hand on the price-management screen (V2.3), rather
        /// than derived from a delivery or a profit multiplier. A bulk multiplier leaves such items
        /// alone unless the user explicitly says to include them; a new delivery clears the flag, since
        /// the price typed on the invoice is a fresh deliberate entry.
        /// </summary>
        public bool ManualPrice { get; set; }

        /// <summary>Low-stock threshold, in single units (FR-INV-05).</summary>
        public int MinQuantity { get; set; }

        /// <summary>Optional target/ceiling stock level in single units (V1.3 "مخزون كامل"). 0 = not set.</summary>
        public int MaxQuantity { get; set; }

        /// <summary>Optional per-item override of the global near-expiry warning window.</summary>
        public int? ExpiryWarnDays { get; set; }

        /// <summary>When set, this drug is a substitute/alternative for the referenced item (V1.2 req 4).</summary>
        public int? SubstituteOf { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Single units contained in one box.</summary>
        public int UnitsPerBox => UnitsPerStrip * StripsPerBox;

        /// <summary>How a drug is written on screen: "polymol / paracetamol". Lists, search results and
        /// pickers all use this, so the same box is recognisable whichever of its two names the person
        /// happens to know. An item with no scientific name simply shows its trade name.
        /// The printed receipt does NOT use this — it truncates to a fixed width, and the trade name
        /// alone is what the customer needs to read back.</summary>
        public string DisplayName => Display(NameEn, GenericName);

        /// <summary>The same "name / scientific name" rule for callers that hold the two names loose —
        /// a report projection reading two columns, rather than a whole item.</summary>
        public static string Display(string tradeName, string genericName)
            => string.IsNullOrWhiteSpace(genericName)
                ? (tradeName ?? "")
                : tradeName + " / " + genericName;
    }
}
