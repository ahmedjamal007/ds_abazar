using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>What a return will take back, priced and validated but not yet persisted.</summary>
    public class ReturnPlan
    {
        public List<ReturnLine> Lines { get; set; } = new List<ReturnLine>();

        /// <summary>Money to refund for this return.</summary>
        public decimal Total { get; set; }

        /// <summary>Cost of the goods coming back into stock.</summary>
        public decimal Cost { get; set; }

        /// <summary>True when this return takes back the invoice's last outstanding unit.</summary>
        public bool CompletesSale { get; set; }

        /// <summary>Single units to restore, by sale line.</summary>
        public IEnumerable<KeyValuePair<int, int>> UnitsByLine
            => Lines.Select(l => new KeyValuePair<int, int>(l.SaleLineId, l.Units));
    }

    /// <summary>
    /// Works out what a partial return is worth (FR-POS-08). A customer who bought 50 boxes may bring
    /// 5 back, so returns are priced per line and per quantity against what is still outstanding.
    ///
    /// Pure arithmetic — no database — so the money rules are testable on their own.
    /// </summary>
    public static class ReturnCalculator
    {
        /// <summary>
        /// Validates the requested quantities against what is still outstanding on the invoice and
        /// prices them. Throws <see cref="ValidationException"/> if anything is out of range, so a
        /// caller can never half-apply a return.
        /// </summary>
        public static ReturnPlan Plan(Sale sale, IEnumerable<ReturnRequest> requests)
        {
            if (sale == null) throw new ValidationException("الفاتورة غير موجودة.");
            if (sale.Status == SaleStatus.Returned) throw new ValidationException("الفاتورة مُرجعة بالفعل.");

            var wanted = (requests ?? Enumerable.Empty<ReturnRequest>())
                .Where(r => r != null && r.Quantity > 0).ToList();
            if (wanted.Count == 0) throw new ValidationException("حدد الكمية المراد إرجاعها.");

            // The same line twice in one request is a UI slip, not two returns — add them up.
            var merged = wanted.GroupBy(r => r.SaleLineId)
                               .Select(g => new ReturnRequest(g.Key, g.Sum(r => r.Quantity)))
                               .ToList();

            var plan = new ReturnPlan();
            decimal priceRatio = DiscountRatio(sale);

            foreach (ReturnRequest req in merged)
            {
                SaleLine line = sale.Lines.FirstOrDefault(l => l.Id == req.SaleLineId);
                if (line == null) throw new ValidationException("صنف غير موجود في هذه الفاتورة.");
                if (req.Quantity > line.RemainingQuantity)
                    throw new ValidationException(
                        $"لا يمكن إرجاع {req.Quantity} من \"{line.ItemName}\" — المتبقي {line.RemainingQuantity} فقط.");

                int units = req.Quantity * line.UnitsEach;
                plan.Lines.Add(new ReturnLine
                {
                    SaleLineId = line.Id,
                    ItemId = line.ItemId,
                    ItemName = line.ItemName,
                    Quantity = req.Quantity,
                    Units = units,
                    // The invoice discount was spread across everything sold, so a refund carries its share.
                    Amount = decimal.Round(units * line.UnitPrice * priceRatio, 2),
                    Cost = decimal.Round(units * UnitCost(line), 2)
                });
            }

            plan.CompletesSale = sale.Lines.All(l =>
                l.RemainingQuantity == plan.Lines.Where(p => p.SaleLineId == l.Id).Sum(p => p.Quantity));

            // Rounding each line separately can drift a piaster or two from the invoice. When this
            // return closes the invoice, the refund is exactly what is still outstanding, so a fully
            // returned sale always nets to zero however it was split up.
            if (plan.CompletesSale)
            {
                Settle(plan.Lines, sale.Total - sale.ReturnedTotal, l => l.Amount, (l, v) => l.Amount = v);
                Settle(plan.Lines, sale.CostTotal - sale.ReturnedCost, l => l.Cost, (l, v) => l.Cost = v);
            }

            plan.Total = plan.Lines.Sum(l => l.Amount);
            plan.Cost = plan.Lines.Sum(l => l.Cost);
            return plan;
        }

        /// <summary>Everything still outstanding on the invoice — what "return the whole invoice" means.</summary>
        public static IList<ReturnRequest> EverythingOutstanding(Sale sale)
            => sale.Lines.Where(l => l.RemainingQuantity > 0)
                         .Select(l => new ReturnRequest(l.Id, l.RemainingQuantity))
                         .ToList();

        /// <summary>
        /// How much of the sold price a refund actually pays back: the invoice total over its subtotal,
        /// which is 1 when nothing was discounted.
        /// </summary>
        private static decimal DiscountRatio(Sale sale)
            => sale.Subtotal > 0m ? sale.Total / sale.Subtotal : 1m;

        private static decimal UnitCost(SaleLine line)
            => line.TotalUnits > 0 ? line.CostTotal / line.TotalUnits : 0m;

        /// <summary>Pushes any rounding difference onto the largest line so the parts sum to the whole.</summary>
        private static void Settle(List<ReturnLine> lines, decimal target,
            Func<ReturnLine, decimal> get, Action<ReturnLine, decimal> set)
        {
            if (lines.Count == 0) return;
            decimal drift = decimal.Round(target - lines.Sum(get), 2);
            if (drift == 0m) return;

            ReturnLine biggest = lines.OrderByDescending(get).First();
            set(biggest, decimal.Round(get(biggest) + drift, 2));
        }
    }
}
