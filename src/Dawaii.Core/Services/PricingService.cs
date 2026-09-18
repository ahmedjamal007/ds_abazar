using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Price increases (V2.3 "زيادة الأسعار"): a multiplier over the CURRENT SELLING PRICE, rounded to
    /// a practical figure, previewed, and applied to many items at once — or one price typed by hand.
    ///
    /// The multiplier is applied to what the drug sells for today, not to its cost. That is the
    /// pharmacy's actual routine: prices are set by the delivery that brought the box in, and then,
    /// as the currency moves, everything on the shelf is raised by a percentage. A first cut
    /// multiplied cost and treated the shelf price as a fallback; the owner corrected it.
    ///
    /// This sits on top of the pricing rule the app already had rather than beside it. Nothing here
    /// stores a strip or box price: the service works out the new BOX price, divides it down to the per
    /// unit figure the item stores (<see cref="BatchPricing.UnitFromBox"/>), and writes that — so the
    /// POS, the receipts, the batches screen and the reports all see the change through the same
    /// column they always read. Every batch of the drug is brought to the new price too, the same rule
    /// a delivery applies, so the till and the stockroom never disagree about what a box sells for.
    ///
    /// Two kinds of price are kept apart. A price the multiplier calculated is just that; a price
    /// someone typed is flagged <see cref="Item.ManualPrice"/> and a later bulk run leaves it alone
    /// unless told otherwise. Both are previewed before anything is written, and applying is one
    /// explicit step.
    /// </summary>
    public class PricingService
    {
        /// <summary>Settings key: the rounding step. 0 or missing = by magnitude.</summary>
        public const string RoundingStepKey = "price_rounding_step";

        private readonly IItemRepository _items;
        private readonly ISettingsRepository _settings;
        private readonly IAuditRepository _audit;

        public PricingService(IItemRepository items, ISettingsRepository settings, IAuditRepository audit)
        {
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _settings = settings;
            _audit = audit;
        }

        /// <summary>The rounding step the pharmacy configured, or 0 for the magnitude rule.</summary>
        public decimal RoundingStep
        {
            get
            {
                string raw = _settings?.Get(RoundingStepKey);
                return decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal v) && v > 0m ? v : 0m;
            }
        }

        /// <summary>Items for the price screen — the same search the stockroom uses, active items only.</summary>
        public IReadOnlyList<Item> Search(User actor, string term, int limit = 500)
        {
            Require(actor);
            return _items.Search(term, activeOnly: true, limit: limit);
        }

        /// <summary>
        /// Works out what the given items would sell for at <paramref name="multiplier"/> times their
        /// current selling price. Nothing is written. Every item comes back with a row saying either
        /// what its new price would be, or why it has none: no price on file, hand-priced and
        /// protected, or already at that price.
        /// </summary>
        /// <param name="includeManual">Recalculate items whose price was set by hand. Off by default: a
        /// price someone typed deliberately should not be overwritten by a blanket multiplier.</param>
        public IReadOnlyList<PricePlanRow> Preview(User actor, IReadOnlyList<int> itemIds, decimal multiplier,
            bool includeManual = false)
        {
            Require(actor);
            if (multiplier <= 0m) throw new ValidationException("معامل الزيادة يجب أن يكون أكبر من صفر.");
            if (multiplier > 100m) throw new ValidationException("معامل الزيادة غير معقول (أكبر من 100).");
            if (itemIds == null || itemIds.Count == 0) return new List<PricePlanRow>();

            decimal step = RoundingStep;
            var rows = new List<PricePlanRow>();

            foreach (Item item in _items.GetByIds(itemIds))
            {
                var row = new PricePlanRow { Item = item, Multiplier = multiplier };
                bool hasPrice = item.SellingPrice.HasValue && item.SellingPrice.Value > 0m;

                if (!hasPrice)
                {
                    row.Status = PricePlanStatus.NoPrice;
                }
                else if (item.ManualPrice && !includeManual)
                {
                    row.Status = PricePlanStatus.ManualSkipped;
                }
                else
                {
                    PricePlanFigures figures = BatchPricing.PlanFromSellingPrice(
                        item.SellingPrice.Value, item.StripsPerBox, item.UnitsPerStrip, multiplier, step);
                    row.New = figures;

                    // "Unchanged" is judged on the box price, which is what the user sees and compares.
                    row.Status = row.CurrentBoxPrice.HasValue && row.CurrentBoxPrice.Value == figures.BoxPrice
                        ? PricePlanStatus.Unchanged
                        : PricePlanStatus.Planned;
                }
                rows.Add(row);
            }

            // Keep the caller's order, so the grid does not reshuffle under the user.
            var order = itemIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
            return rows.OrderBy(r => order.TryGetValue(r.Item.Id, out int i) ? i : int.MaxValue).ToList();
        }

        /// <summary>
        /// The figures for one price typed by hand: the box price the user entered, divided down.
        /// Manual prices are NOT rounded — the user typed exactly what they want — but they are checked:
        /// never negative, and the user is told if it is below cost rather than being refused, because a
        /// deliberate loss-leader is a pharmacist's decision to make.
        /// </summary>
        public PricePlanFigures FiguresForBoxPrice(Item item, decimal boxPrice)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            BatchPricing.ValidatePrice(boxPrice, "سعر البيع");
            int strips = Math.Max(1, item.StripsPerBox);
            int units = Math.Max(1, item.UnitsPerStrip);
            return new PricePlanFigures
            {
                RawStripPrice = BatchPricing.StripFromBox(boxPrice, strips),
                StripPrice = BatchPricing.StripFromBox(boxPrice, strips),
                BoxPrice = boxPrice,
                UnitPrice = BatchPricing.UnitFromBox(boxPrice, strips, units),
                WasRounded = false
            };
        }

        /// <summary>
        /// Writes the confirmed prices. Each item is its own transaction — the item's per-unit price and
        /// every one of its batches together — so a failure on one drug leaves the others either fully
        /// applied or untouched, never half-priced. Failures are reported, not swallowed.
        /// </summary>
        public PriceApplyResult Apply(User actor, IReadOnlyList<PriceChange> changes)
        {
            Require(actor);
            var result = new PriceApplyResult();
            if (changes == null || changes.Count == 0) return result;

            foreach (PriceChange change in changes)
            {
                try
                {
                    if (change.SellingPerUnit < 0m)
                        throw new ValidationException("سعر البيع لا يمكن أن يكون سالباً.");

                    Item item = _items.GetById(change.ItemId);
                    if (item == null) throw new ValidationException("الصنف غير موجود.");

                    decimal before = item.SellingPrice ?? 0m;
                    _items.ApplySellingPrice(change.ItemId, change.SellingPerUnit, change.Manual);

                    _audit?.Log(actor, change.Manual ? "ManualPrice" : "MultiplierPrice", "items", change.ItemId,
                        item.NameEn + ": box " +
                        (before * item.UnitsPerBox).ToString("0.00") + " -> " +
                        (change.SellingPerUnit * item.UnitsPerBox).ToString("0.00"));
                    result.Applied++;
                }
                catch (DomainException ex)
                {
                    result.Failures.Add("#" + change.ItemId + ": " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Only the people who already set prices — by entering a delivery — may change them here.</summary>
        private static void Require(User user)
            => Guard.RequireInventoryAccess(user, "إدارة الأسعار متاحة للمدير أو الموظف ذي الامتيازات فقط.");
    }
}
