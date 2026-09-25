using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// A quantity of a drug expressed the way a pharmacist counts it (V2.6).
    ///
    /// Stock is STORED in single units — individual tablets — because that is the only unit every
    /// sale can be reduced to: someone buys a box, someone else buys four tablets, and both have to
    /// come out of the same number. It is not, however, how anybody thinks about a shelf. "1,240
    /// tablets" is a number nobody can picture; "12 boxes and 4 strips" is a thing you can look at.
    ///
    /// So this converts one into the other, and it is arithmetic rather than formatting: the split
    /// depends on the item's own packaging, which differs per drug and — for older batches — per
    /// shipment. Keeping it here means the Telegram bot, the stock screens and anything later all
    /// divide it up the same way.
    /// </summary>
    public readonly struct PackQuantity
    {
        /// <summary>Whole boxes.</summary>
        public int Boxes { get; }

        /// <summary>Whole strips left over after the boxes.</summary>
        public int Strips { get; }

        /// <summary>Single tablets left over after the strips.</summary>
        public int Units { get; }

        /// <summary>The total, in single units — what the database actually holds.</summary>
        public int TotalUnits { get; }

        /// <summary>Total expressed in whole strips, ignoring any loose remainder.</summary>
        public int TotalStrips { get; }

        private PackQuantity(int boxes, int strips, int units, int totalUnits, int totalStrips)
        {
            Boxes = boxes;
            Strips = strips;
            Units = units;
            TotalUnits = totalUnits;
            TotalStrips = totalStrips;
        }

        /// <summary>
        /// Splits a count of single units by an item's packaging.
        ///
        /// Both divisors are floored at 1. A catalogue entry with zero strips per box is not
        /// hypothetical — a drug sold loose, or a half-finished entry — and dividing by it would
        /// throw somewhere far from the cause, in a bot reply or a stock list.
        /// </summary>
        public static PackQuantity Of(Item item, int totalUnits)
        {
            if (item == null) return new PackQuantity(0, 0, totalUnits, totalUnits, 0);
            return Of(item.UnitsPerStrip, item.StripsPerBox, totalUnits);
        }

        /// <summary>
        /// Splits a count of single units by explicit packaging — a batch's own, for instance.
        ///
        /// Only reports a level of packaging the data actually records. A zero means "not known", and
        /// the first version of this floored both divisors to 1, which turned 17 units of a drug with
        /// NO recorded packaging into "17 boxes" — packaging invented out of a missing field, shown to
        /// a manager deciding whether to reorder. An item genuinely packed one-per-box says so with a
        /// 1, and is reported as boxes; an item that says nothing is reported as loose units.
        /// </summary>
        public static PackQuantity Of(int unitsPerStrip, int stripsPerBox, int totalUnits)
        {
            // Negative stock should not exist, but a corrupt row must produce a readable answer
            // rather than nonsense wrapped around zero.
            if (totalUnits <= 0) return new PackQuantity(0, 0, 0, totalUnits < 0 ? totalUnits : 0, 0);

            int perStrip = unitsPerStrip > 0 ? unitsPerStrip : 0;
            if (perStrip == 0)
            {
                // No strip size recorded: every unit is loose, and saying anything else would be
                // making it up.
                return new PackQuantity(0, 0, totalUnits, totalUnits, 0);
            }

            int strips = totalUnits / perStrip;
            int loose = totalUnits % perStrip;

            if (stripsPerBox <= 0)
            {
                // Strips are known, boxes are not. Reporting boxes here would make every strip look
                // like a box.
                return new PackQuantity(0, strips, loose, totalUnits, strips);
            }

            int perBox = perStrip * stripsPerBox;
            int boxes = totalUnits / perBox;
            int afterBoxes = totalUnits % perBox;

            return new PackQuantity(boxes, afterBoxes / perStrip, afterBoxes % perStrip, totalUnits, strips);
        }

        /// <summary>True when there is nothing on the shelf at all.</summary>
        public bool IsEmpty => TotalUnits <= 0;
    }
}
