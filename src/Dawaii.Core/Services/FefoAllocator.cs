using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>One batch's contribution to satisfying a requested quantity.</summary>
    public class BatchAllocation
    {
        public int BatchId { get; set; }
        public int Units { get; set; }
        public decimal UnitCost { get; set; }
        public System.DateTime? ExpiryDate { get; set; }
    }

    /// <summary>
    /// First-Expiry-First-Out stock allocation (FR-INV-03, DECISIONS D-05).
    /// Consumes the nearest-expiry non-disposed batch first; batches with no expiry date sort last.
    /// </summary>
    public static class FefoAllocator
    {
        /// <summary>Total sellable single units across all non-disposed batches with stock.</summary>
        public static int AvailableUnits(IEnumerable<StockBatch> batches)
            => batches.Where(Sellable).Sum(b => b.QuantityUnits);

        /// <summary>Orders sellable batches in FEFO order: dated (nearest first), then undated, then oldest received.</summary>
        public static IEnumerable<StockBatch> InFefoOrder(IEnumerable<StockBatch> batches)
            => batches.Where(Sellable)
                      .OrderBy(b => b.ExpiryDate.HasValue ? 0 : 1)
                      .ThenBy(b => b.ExpiryDate ?? System.DateTime.MaxValue)
                      .ThenBy(b => b.Id);

        /// <summary>
        /// Allocates <paramref name="requestedUnits"/> single units across batches in FEFO order.
        /// Throws <see cref="InsufficientStockException"/> if there is not enough sellable stock.
        /// </summary>
        public static IReadOnlyList<BatchAllocation> Allocate(
            int itemId, IEnumerable<StockBatch> batches, int requestedUnits)
        {
            if (requestedUnits <= 0)
                throw new ValidationException("الكمية يجب أن تكون أكبر من صفر.");

            var ordered = InFefoOrder(batches).ToList();
            int available = ordered.Sum(b => b.QuantityUnits);
            if (available < requestedUnits)
                throw new InsufficientStockException(itemId, requestedUnits, available);

            var result = new List<BatchAllocation>();
            int remaining = requestedUnits;
            foreach (StockBatch b in ordered)
            {
                if (remaining <= 0) break;
                int take = System.Math.Min(remaining, b.QuantityUnits);
                if (take <= 0) continue;
                result.Add(new BatchAllocation
                {
                    BatchId = b.Id,
                    Units = take,
                    UnitCost = b.PurchasePrice,
                    ExpiryDate = b.ExpiryDate
                });
                remaining -= take;
            }
            return result;
        }

        private static bool Sellable(StockBatch b) => !b.IsDisposed && b.QuantityUnits > 0;
    }
}
