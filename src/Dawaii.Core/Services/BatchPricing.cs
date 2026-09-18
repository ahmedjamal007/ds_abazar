using System;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// The whole of pharmacy pricing. The admin types the BOX purchase price and the BOX selling price
    /// on each stock batch (V1.7), and every other figure is divided down from those:
    ///
    /// <code>
    /// strip price = box price ÷ strips per box      (3200 ÷ 4 = 800)
    /// unit price  = strip price ÷ units per strip
    /// </code>
    ///
    /// Strip and unit prices are ALWAYS computed, never stored as editable values, so changing the box
    /// price or the strips-per-box count re-derives them with no way for the two to drift apart.
    ///
    /// V2.3 adds the one thing this rule used to say it did not have: a multiplier that raises the
    /// shelf price, applied on the زيادة الأسعار screen, with the result rounded to a price a customer
    /// can actually be charged. Both live here rather than in a second utility, so there is still
    /// exactly one place that knows how a box, a strip and a single unit relate.
    /// </summary>
    public static class BatchPricing
    {
        // ---------------- price increase multiplier + practical rounding (V2.3) ----------------

        /// <summary>
        /// Rounds a selling price to a figure a pharmacy can actually charge.
        ///
        /// The step scales with the price: nobody quotes 4,876.25 for a strip, and nobody quotes 47.5
        /// either. <paramref name="step"/> overrides the scale when the pharmacy has a house rule
        /// (a setting on the price screen); zero means "by magnitude":
        ///
        /// <code>
        ///   under 200        → nearest 5        47.5    → 50
        ///   200 – 999        → nearest 10       432.9   → 430,  520 → 520
        ///   1,000 – 4,999    → nearest 50       4876.25 → 4900
        ///   5,000 – 19,999   → nearest 100      5566.2  → 5600
        ///   20,000 and up    → nearest 500
        /// </code>
        ///
        /// The tiers are placed so the most rounding can ever take is about 2.5% of the price at any
        /// boundary. A first cut used "nearest 50" from 500 up, and 520 became 500 — a 3.8% cut in the
        /// margin at exactly the price a strip of a common drug sells for, while 5,566 → 5,600 gave up
        /// 0.6%. Rounding is meant to make the price sayable, not to quietly reprice the shelf.
        ///
        /// Halves round away from zero (5,550 → 5,600), which is what a shopkeeper does.
        /// </summary>
        public static decimal RoundToPractical(decimal price, decimal step = 0m)
        {
            if (price <= 0m) return 0m;
            decimal s = step > 0m ? step : StepFor(price);
            decimal rounded = decimal.Round(price / s, 0, MidpointRounding.AwayFromZero) * s;
            // A house step bigger than the price itself (step 1000 on a 78-pound item) must not round
            // it to nothing: a positive price is never less than one step.
            return rounded > 0m ? rounded : s;
        }

        /// <summary>The rounding step this price falls into under the magnitude rule.</summary>
        public static decimal StepFor(decimal price)
        {
            if (price < 200m) return 5m;
            if (price < 1000m) return 10m;
            if (price < 5000m) return 50m;
            if (price < 20000m) return 100m;
            return 500m;
        }

        /// <summary>
        /// The selling prices that follow from raising an item's current price by a multiplier.
        ///
        /// The multiplier is applied to the STRIP price and the result rounded there, and the box is then
        /// rebuilt as strip × strips-per-box. Rounding the box instead can leave the strip — the unit a
        /// customer most often buys — at something like 1,866.67; rounding the strip keeps both clean and
        /// keeps the box an exact multiple of the strip, which is the relationship the rest of the app
        /// relies on. A box that is a single strip rounds the box directly, which is the same thing.
        ///
        /// There is no floor: a multiplier under 1 is a discount the pharmacist chose. The screen shows
        /// a warning when the result lands below a known cost, and leaves the decision to them.
        /// </summary>
        public static PricePlanFigures PlanFromSellingPrice(decimal sellingPerUnit, int stripsPerBox, int unitsPerStrip,
            decimal multiplier, decimal step = 0m)
        {
            if (sellingPerUnit <= 0m) throw new ValidationException("لا يوجد سعر بيع حالي للصنف.");
            if (multiplier <= 0m) throw new ValidationException("معامل الزيادة يجب أن يكون أكبر من صفر.");

            int strips = Math.Max(1, stripsPerBox);
            int units = Math.Max(1, unitsPerStrip);

            decimal rawStrip = sellingPerUnit * units * multiplier;
            decimal strip = RoundToPractical(rawStrip, step);
            decimal box = strip * strips;
            return new PricePlanFigures
            {
                RawStripPrice = rawStrip,
                StripPrice = strip,
                BoxPrice = box,
                UnitPrice = UnitFromBox(box, strips, units),
                WasRounded = strip != decimal.Round(rawStrip, 2, MidpointRounding.AwayFromZero)
            };
        }

        /// <summary>Price of one strip: the box price split across the strips in a box. A box that is a
        /// single strip prices the strip at the box price. Rounded to 2dp (SDG).</summary>
        public static decimal StripFromBox(decimal boxPrice, int stripsPerBox)
            => stripsPerBox <= 0
                ? decimal.Round(boxPrice, 2, MidpointRounding.AwayFromZero)
                : decimal.Round(boxPrice / stripsPerBox, 2, MidpointRounding.AwayFromZero);

        /// <summary>Price of one single unit (حبة), kept at full precision so that
        /// <c>unit × unitsPerBox</c> reproduces the entered box price exactly — the POS shows box and
        /// strip prices built back up from this, and rounding here would make them drift.</summary>
        public static decimal UnitFromBox(decimal boxPrice, int stripsPerBox, int unitsPerStrip)
        {
            int unitsPerBox = UnitsPerBox(stripsPerBox, unitsPerStrip);
            return unitsPerBox <= 0 ? boxPrice : boxPrice / unitsPerBox;
        }

        /// <summary>Single units in one box, with both factors floored at 1 so a box always holds
        /// at least one unit (guards divide-by-zero on partially filled forms).</summary>
        public static int UnitsPerBox(int stripsPerBox, int unitsPerStrip)
            => Math.Max(1, stripsPerBox) * Math.Max(1, unitsPerStrip);

        /// <summary>Validates a price typed by the user: never negative.</summary>
        public static decimal ValidatePrice(decimal price, string fieldAr)
        {
            if (price < 0) throw new ValidationException($"{fieldAr} يجب ألا يكون سالباً.");
            return price;
        }

        /// <summary>Validates the strips-per-box count: at least one strip in a box.</summary>
        public static int ValidateStripsPerBox(int stripsPerBox)
        {
            if (stripsPerBox < 1) throw new ValidationException("عدد الأشرطة في العلبة يجب أن يكون 1 على الأقل.");
            return stripsPerBox;
        }
    }
}
