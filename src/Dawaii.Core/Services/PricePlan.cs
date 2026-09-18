using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>The three prices that follow from one rounded strip price — see
    /// <see cref="BatchPricing.PlanFromSellingPrice"/>.</summary>
    public class PricePlanFigures
    {
        /// <summary>Strip cost × multiplier, before rounding — shown so the user sees what the rounding did.</summary>
        public decimal RawStripPrice { get; set; }
        public decimal StripPrice { get; set; }
        public decimal BoxPrice { get; set; }

        /// <summary>Per single unit, full precision — what <c>items.selling_price</c> stores and the POS reads.</summary>
        public decimal UnitPrice { get; set; }

        /// <summary>True when rounding moved the strip price from the raw figure.</summary>
        public bool WasRounded { get; set; }
    }

    /// <summary>Why a row in a price plan did or did not get a new price.</summary>
    public enum PricePlanStatus
    {
        /// <summary>A new price was calculated and is waiting to be applied.</summary>
        Planned,

        /// <summary>The item has no selling price on file, so there is nothing to increase. (Such an
        /// item is hidden from the POS anyway; it gets a price from its first delivery.)</summary>
        NoPrice,

        /// <summary>The item's price was set by hand and the plan was told to leave such items alone.</summary>
        ManualSkipped,

        /// <summary>The calculated price equals the current one — nothing to change.</summary>
        Unchanged
    }

    /// <summary>
    /// One item in a price plan (V2.3): what it costs, what it sells for now, what it would sell for
    /// after the multiplier, and why — or why not. Built for the preview grid; nothing here is saved
    /// until the user confirms.
    /// </summary>
    public class PricePlanRow
    {
        public Item Item { get; set; }
        public decimal Multiplier { get; set; }
        public PricePlanStatus Status { get; set; }

        /// <summary>True when the new box price would sit below the cost on file — shown as a warning,
        /// not refused: a multiplier under 1 is a discount the pharmacist chose, and cost is only known
        /// for drugs that came in through a recorded delivery.</summary>
        public bool BelowCost => New != null && Item.PurchasePrice > 0m && New.BoxPrice < CostPerBox;

        /// <summary>What a box costs the pharmacy — cost per unit × units per box.</summary>
        public decimal CostPerBox => Item.PurchasePrice * BatchPricing.UnitsPerBox(Item.StripsPerBox, Item.UnitsPerStrip);
        public decimal CostPerStrip => Item.PurchasePrice * System.Math.Max(1, Item.UnitsPerStrip);

        public decimal? CurrentBoxPrice => Item.SellingPrice.HasValue ? UnitConverter.PriceOf(Item, UnitType.Box) : (decimal?)null;
        public decimal? CurrentStripPrice => Item.SellingPrice.HasValue ? UnitConverter.PriceOf(Item, UnitType.Strip) : (decimal?)null;

        /// <summary>Null unless <see cref="Status"/> is <see cref="PricePlanStatus.Planned"/>.</summary>
        public PricePlanFigures New { get; set; }

        /// <summary>True when the pending price was typed by hand rather than calculated.</summary>
        public bool IsManual { get; set; }
    }

    /// <summary>A price the user has confirmed and wants written: the item and its new per-unit price.</summary>
    public class PriceChange
    {
        public int ItemId { get; set; }

        /// <summary>Per single unit — the figure the item stores. Box and strip rebuild from it.</summary>
        public decimal SellingPerUnit { get; set; }

        /// <summary>Marks the item as hand-priced so a later bulk multiplier leaves it alone.</summary>
        public bool Manual { get; set; }
    }

    public class PriceApplyResult
    {
        public int Applied { get; set; }
        public List<string> Failures { get; } = new List<string>();
    }
}
