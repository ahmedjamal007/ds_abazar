using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// What a price change is doing (V2.3.2). Increase and decrease are the SAME arithmetic —
    /// <c>new = current × multiplier</c> — and run through one calculation engine; the operation only
    /// decides which multipliers are legal and what the screen and the audit log call the change.
    ///
    /// A decrease is never "current − multiplier": 0.90 means the price keeps 90% of itself, so the
    /// same engine that raises a price also lowers one, and rounding behaves identically in both.
    /// </summary>
    public enum PriceOperation
    {
        /// <summary>Raise the shelf price. Multiplier must be greater than 1.</summary>
        Increase,

        /// <summary>Lower the shelf price. Multiplier must be between 0 and 1, exclusive.</summary>
        Decrease,

        /// <summary>A price typed by hand — no multiplier.</summary>
        Manual
    }

    /// <summary>Arabic names for the operations, so the screen, the confirmation and the audit agree.</summary>
    public static class PriceOperations
    {
        public static string LabelAr(PriceOperation op)
        {
            switch (op)
            {
                case PriceOperation.Increase: return "زيادة";
                case PriceOperation.Decrease: return "تخفيض";
                default: return "يدوي";
            }
        }

        /// <summary>
        /// Checks the multiplier against the operation. Exactly 1.00 is refused for both: it is the
        /// commonest slip — the user picks the wrong button, or forgets to type — and it would raise a
        /// preview full of "unchanged" rows that looks like the app ignored them.
        /// </summary>
        public static void Validate(PriceOperation op, decimal multiplier)
        {
            if (multiplier <= 0m)
                throw new ValidationException("المعامل يجب أن يكون أكبر من صفر.");
            if (multiplier > 100m)
                throw new ValidationException("المعامل غير معقول (أكبر من 100).");

            if (op == PriceOperation.Increase)
            {
                if (multiplier == 1m)
                    throw new ValidationException("معامل 1.00 لا يغيّر شيئاً. للزيادة استخدم معاملاً أكبر من 1.00 (مثال: 1.30).");
                if (multiplier < 1m)
                    throw new ValidationException(
                        "معامل الزيادة يجب أن يكون أكبر من 1.00 (مثال: 1.30 لزيادة 30%). " +
                        "لتخفيض الأسعار استخدم زر \"تخفيض الأسعار\".");
            }
            else if (op == PriceOperation.Decrease)
            {
                if (multiplier == 1m)
                    throw new ValidationException("معامل 1.00 لا يغيّر شيئاً. للتخفيض استخدم معاملاً أقل من 1.00 (مثال: 0.90).");
                if (multiplier > 1m)
                    throw new ValidationException(
                        "معامل التخفيض يجب أن يكون أقل من 1.00 (مثال: 0.90 لتخفيض 10%). " +
                        "لزيادة الأسعار استخدم زر \"زيادة الأسعار\".");
            }
        }

        /// <summary>"زيادة 30%" / "تخفيض 10%" — what a bare multiplier actually means, for the screen.</summary>
        public static string Describe(PriceOperation op, decimal multiplier)
        {
            decimal percent = decimal.Round(System.Math.Abs(multiplier - 1m) * 100m, 2);
            return LabelAr(op) + " " + percent.ToString("0.##") + "%";
        }
    }

    /// <summary>The three prices that follow from one rounded strip price — see
    /// <see cref="BatchPricing.PlanFromSellingPrice"/>.</summary>
    public class PricePlanFigures
    {
        /// <summary>Current strip price × multiplier, before rounding — shown so the user sees what the
        /// rounding did.</summary>
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

        /// <summary>The item has no selling price on file, so there is nothing to raise or lower. (Such
        /// an item is hidden from the POS anyway; it gets a price from its first delivery.)</summary>
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

        /// <summary>Zero for a hand-typed price, which has no multiplier.</summary>
        public decimal Multiplier { get; set; }

        public PriceOperation Operation { get; set; }
        public PricePlanStatus Status { get; set; }

        /// <summary>Null unless <see cref="Status"/> is <see cref="PricePlanStatus.Planned"/>.</summary>
        public PricePlanFigures New { get; set; }

        /// <summary>True when this price was typed by hand rather than calculated.</summary>
        public bool IsManual => Operation == PriceOperation.Manual;

        /// <summary>
        /// True when the new box price would sit below the cost on file — a warning, never a refusal.
        /// Computed here, in the domain, for calculated AND hand-typed prices alike, so no screen has
        /// to reproduce the comparison (and get the units wrong).
        /// </summary>
        public bool BelowCost => New != null && Item.PurchasePrice > 0m && New.BoxPrice < CostPerBox;

        /// <summary>What a box costs the pharmacy. Uses the same unit conversion the selling price
        /// uses, so the two sides of the below-cost comparison can never disagree about box size.</summary>
        public decimal CostPerBox => UnitConverter.CostOf(Item, UnitType.Box);
        public decimal CostPerStrip => UnitConverter.CostOf(Item, UnitType.Strip);

        public decimal? CurrentBoxPrice => Item.SellingPrice.HasValue ? UnitConverter.PriceOf(Item, UnitType.Box) : (decimal?)null;
        public decimal? CurrentStripPrice => Item.SellingPrice.HasValue ? UnitConverter.PriceOf(Item, UnitType.Strip) : (decimal?)null;

        /// <summary>
        /// The item's stored per-unit price at the moment this plan was built. Carried into
        /// <see cref="PriceChange.ExpectedCurrentUnitPrice"/> so applying can tell whether somebody
        /// else moved the price between the preview and the confirmation.
        /// </summary>
        public decimal? PricedAt => Item.SellingPrice;
    }

    /// <summary>A price the user has confirmed and wants written.</summary>
    public class PriceChange
    {
        public int ItemId { get; set; }

        /// <summary>Per single unit — the figure the item stores. Box and strip rebuild from it.</summary>
        public decimal SellingPerUnit { get; set; }

        /// <summary>What produced this price, for validation and the audit trail.</summary>
        public PriceOperation Operation { get; set; }

        /// <summary>The multiplier used, or null for a hand-typed price.</summary>
        public decimal? Multiplier { get; set; }

        /// <summary>
        /// What the item's per-unit price was when the preview was built. <see cref="PricingService.Apply"/>
        /// refuses the change if the stored price has moved since — another terminal received a delivery,
        /// or another manager repriced it — rather than silently overwriting their work.
        /// Null skips the check.
        /// </summary>
        public decimal? ExpectedCurrentUnitPrice { get; set; }

        /// <summary>True when a person typed this price, so a later bulk multiplier leaves it alone.</summary>
        public bool Manual => Operation == PriceOperation.Manual;
    }

    /// <summary>One item that could not be written, and why — structured, so a caller can match it back
    /// to its row without parsing a message.</summary>
    public class PriceApplyFailure
    {
        public int ItemId { get; set; }
        public string ItemName { get; set; }
        public string Reason { get; set; }

        public override string ToString()
            => (string.IsNullOrWhiteSpace(ItemName) ? "#" + ItemId : ItemName) + ": " + Reason;
    }

    public class PriceApplyResult
    {
        public int Applied { get; set; }
        public List<PriceApplyFailure> Failures { get; } = new List<PriceApplyFailure>();

        /// <summary>The ids that failed, for a caller deciding what to keep on screen.</summary>
        public HashSet<int> FailedItemIds
        {
            get
            {
                var ids = new HashSet<int>();
                foreach (PriceApplyFailure f in Failures) ids.Add(f.ItemId);
                return ids;
            }
        }
    }
}
