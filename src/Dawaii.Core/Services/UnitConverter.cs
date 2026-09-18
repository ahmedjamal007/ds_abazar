using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Converts between box / strip / single unit and computes prices (FR-POS-04).
    /// Prices are stored per single unit (DECISIONS D-01), so a Box price is simply the
    /// single-unit price times the number of single units in a box — no rounding drift.
    /// </summary>
    public static class UnitConverter
    {
        /// <summary>Single units contained in one of the given unit type for this item.</summary>
        public static int UnitsIn(Item item, UnitType type)
        {
            switch (type)
            {
                case UnitType.Box: return item.UnitsPerBox;
                case UnitType.Strip: return item.UnitsPerStrip;
                default: return 1;
            }
        }

        /// <summary>Total single units for a quantity expressed in the given unit type.</summary>
        public static int ToUnits(Item item, int quantity, UnitType type)
            => quantity * UnitsIn(item, type);

        /// <summary>Selling price for one of the given unit type (single-unit price × units in it).
        /// An unpriced item (NULL selling price) yields 0 — such items are hidden from the POS.</summary>
        public static decimal PriceOf(Item item, UnitType type)
            => (item.SellingPrice ?? 0m) * UnitsIn(item, type);

        /// <summary>Line total for a quantity in a unit type, rounded to 2 decimals (SDG).</summary>
        public static decimal LineTotal(Item item, int quantity, UnitType type)
            => decimal.Round(PriceOf(item, type) * quantity, 2);

        /// <summary>Human, RTL-friendly label for a unit type.</summary>
        public static string LabelAr(UnitType type)
        {
            switch (type)
            {
                case UnitType.Box: return "علبة";
                case UnitType.Strip: return "شريط";
                default: return "حبة";
            }
        }
    }
}
