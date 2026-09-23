using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Price changes (V2.3 "تعديل الأسعار"): a multiplier over the CURRENT SELLING PRICE — raising it
    /// or lowering it — rounded to a practical figure, previewed, and applied to many items at once;
    /// or one price typed by hand.
    ///
    /// The multiplier is applied to what the drug sells for today, not to its cost. That is the
    /// pharmacy's actual routine: prices are set by the delivery that brought the box in, and then,
    /// as the currency moves, everything on the shelf is moved by a percentage.
    ///
    /// Increase and decrease are the same arithmetic and share one engine
    /// (<see cref="BatchPricing.PlanFromSellingPrice"/>); the <see cref="PriceOperation"/> only decides
    /// which multipliers are legal and what the change is called. A decrease is
    /// <c>price × 0.90</c>, never <c>price − 0.90</c>.
    ///
    /// Everything a caller needs to decide is decided HERE, not in a screen: who may reprice, whether a
    /// multiplier suits the operation, whether a hand-set price is protected, how the figure is rounded,
    /// whether the result falls below cost, and whether the stored price still matches what the preview
    /// was built from. A Telegram bot or a report would get the same answers as the WinForms grid.
    ///
    /// Nothing here stores a strip or box price: the service works out the new BOX price, divides it
    /// down to the per-unit figure the item stores (<see cref="BatchPricing.UnitFromBox"/>), and writes
    /// that — so the POS, the receipts, the batches screen and the reports all see the change through
    /// the same column they always read. Every batch of the drug is brought to the new price too, the
    /// same rule a delivery applies, so the till and the stockroom never disagree about a box price.
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
        /// current selling price, raising or lowering per <paramref name="operation"/>. Nothing is
        /// written. Every item comes back with a row saying either what its new price would be, or why
        /// it has none: no price on file, hand-priced and protected, or already at that price.
        /// </summary>
        /// <param name="includeManual">Recalculate items whose price was set by hand. Off by default: a
        /// price someone typed deliberately should not be overwritten by a blanket multiplier.</param>
        /// <param name="pendingManualIds">Items the caller is already holding a hand-typed, not yet
        /// applied price for. They are protected by the same rule as stored manual prices — the service
        /// owns that decision, the caller merely says which items are in that state. Optional.</param>
        public IReadOnlyList<PricePlanRow> Preview(User actor, IReadOnlyList<int> itemIds,
            PriceOperation operation, decimal multiplier, bool includeManual = false,
            IReadOnlyCollection<int> pendingManualIds = null)
        {
            Require(actor);
            if (operation == PriceOperation.Manual)
                throw new ValidationException("السعر اليدوي يُدخل لصنف واحد، وليس بمعامل.");
            PriceOperations.Validate(operation, multiplier);
            if (itemIds == null || itemIds.Count == 0) return new List<PricePlanRow>();

            var protectedPending = pendingManualIds as HashSet<int>
                ?? new HashSet<int>(pendingManualIds ?? (IReadOnlyCollection<int>)new int[0]);

            decimal step = RoundingStep;
            var rows = new List<PricePlanRow>();

            foreach (Item item in _items.GetByIds(itemIds))
            {
                var row = new PricePlanRow { Item = item, Multiplier = multiplier, Operation = operation };
                bool hasPrice = item.SellingPrice.HasValue && item.SellingPrice.Value > 0m;
                bool handPriced = item.ManualPrice || protectedPending.Contains(item.Id);

                if (!hasPrice)
                {
                    row.Status = PricePlanStatus.NoPrice;
                }
                else if (handPriced && !includeManual)
                {
                    row.Status = PricePlanStatus.ManualSkipped;
                }
                else
                {
                    PricePlanFigures figures = BatchPricing.PlanFromSellingPrice(
                        item.SellingPrice.Value, item.StripsPerBox, item.UnitsPerStrip, multiplier, step);
                    row.New = figures;

                    // "Unchanged" is judged on the box price, which is what the user sees and compares.
                    // Rounding can make a real multiplier land back on the current price; that is not a
                    // change and must not become a pending row.
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
        /// A price typed by hand for one item, as a plan row — so a manual price is described exactly
        /// the way a calculated one is, including whether it falls below cost.
        ///
        /// Manual prices are NOT rounded: the user typed the figure they want. They are checked —
        /// never negative — and a below-cost result is reported, not refused, because a deliberate
        /// loss-leader is a pharmacist's decision to make.
        /// </summary>
        public PricePlanRow PreviewManual(User actor, int itemId, decimal boxPrice)
        {
            Require(actor);
            Item item = _items.GetById(itemId);
            if (item == null) throw new ValidationException("الصنف غير موجود.");
            return ManualRowFor(item, boxPrice);
        }

        /// <summary>The same calculation for an item the caller already holds — used by the edit dialog,
        /// which recomputes on every keystroke and must not hit the database each time.</summary>
        public PricePlanRow PreviewManual(Item item, decimal boxPrice)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return ManualRowFor(item, boxPrice);
        }

        private static PricePlanRow ManualRowFor(Item item, decimal boxPrice)
        {
            BatchPricing.ValidatePrice(boxPrice, "سعر البيع");
            int strips = Math.Max(1, item.StripsPerBox);
            int units = Math.Max(1, item.UnitsPerStrip);

            return new PricePlanRow
            {
                Item = item,
                Operation = PriceOperation.Manual,
                Multiplier = 0m,
                Status = PricePlanStatus.Planned,
                New = new PricePlanFigures
                {
                    RawStripPrice = BatchPricing.StripFromBox(boxPrice, strips),
                    StripPrice = BatchPricing.StripFromBox(boxPrice, strips),
                    BoxPrice = boxPrice,
                    UnitPrice = BatchPricing.UnitFromBox(boxPrice, strips, units),
                    WasRounded = false
                }
            };
        }

        /// <summary>
        /// Writes the confirmed prices, revalidating each one against the database first.
        ///
        /// A preview may have been built minutes ago. Before writing, every change is checked again:
        /// the item still exists, the price is not negative, and — when the caller captured it — the
        /// stored price still matches what the preview was calculated from. If another terminal
        /// received a delivery or another manager repriced the drug in the meantime, that item fails
        /// with a clear reason and is left for the user to look at, instead of silently erasing their
        /// change.
        ///
        /// Each item is its own transaction (the item's per-unit price and every one of its batches
        /// together), so one bad drug cannot leave another half-priced. Failures are returned
        /// structured, not swallowed, and the successful ones still stand.
        /// </summary>
        public PriceApplyResult Apply(User actor, IReadOnlyList<PriceChange> changes)
        {
            Require(actor);
            var result = new PriceApplyResult();
            if (changes == null || changes.Count == 0) return result;

            foreach (PriceChange change in changes)
            {
                Item item = null;
                try
                {
                    item = _items.GetById(change.ItemId);
                    if (item == null) throw new ValidationException("الصنف لم يعد موجوداً.");
                    if (change.SellingPerUnit < 0m)
                        throw new ValidationException("سعر البيع لا يمكن أن يكون سالباً.");

                    // Someone else moved this price after the preview was taken.
                    if (change.ExpectedCurrentUnitPrice.HasValue &&
                        (item.SellingPrice ?? 0m) != change.ExpectedCurrentUnitPrice.Value)
                        throw new ValidationException(
                            "تغيّر سعر الصنف من جهاز آخر بعد الاحتساب (أصبح " +
                            UnitConverter.PriceOf(item, UnitType.Box).ToString("0.00") +
                            " للعلبة). أعد الاحتساب.");

                    decimal beforeBox = UnitConverter.PriceOf(item, UnitType.Box);
                    _items.ApplySellingPrice(change.ItemId, change.SellingPerUnit, change.Manual);

                    decimal afterBox = change.SellingPerUnit * UnitConverter.UnitsIn(item, UnitType.Box);
                    _audit?.Log(actor, AuditAction(change.Operation), "items", change.ItemId,
                        item.NameEn + ": box " + beforeBox.ToString("0.00") + " -> " + afterBox.ToString("0.00") +
                        (change.Multiplier.HasValue
                            ? " (" + PriceOperations.LabelAr(change.Operation) + " ×" + change.Multiplier.Value.ToString("0.00") + ")"
                            : " (يدوي)"));
                    result.Applied++;
                }
                catch (DomainException ex)
                {
                    result.Failures.Add(new PriceApplyFailure
                    {
                        ItemId = change.ItemId,
                        ItemName = item?.NameEn,
                        Reason = ex.Message
                    });
                }
            }
            return result;
        }

        private static string AuditAction(PriceOperation op)
        {
            switch (op)
            {
                case PriceOperation.Increase: return "PriceIncrease";
                case PriceOperation.Decrease: return "PriceDecrease";
                default: return "ManualPrice";
            }
        }

        /// <summary>Only the people who already set prices — by entering a delivery — may change them
        /// here. The same check guards preview and apply, so no caller can reach the write without it.</summary>
        private static void Require(User user)
            => Guard.RequireInventoryAccess(user, "تعديل الأسعار متاح للمدير أو الموظف ذي الامتيازات فقط.");
    }
}
