using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>Pure near-expiry / expired calculations (FR-EXP-01, FR-POS-09).</summary>
    public static class ExpiryEvaluator
    {
        /// <summary>Whole days from <paramref name="asOf"/> until the batch expires (negative = already expired).</summary>
        public static int? DaysUntilExpiry(StockBatch batch, DateTime asOf)
            => batch.ExpiryDate.HasValue
                ? (int?)(batch.ExpiryDate.Value.Date - asOf.Date).Days
                : null;

        public static bool IsExpired(StockBatch batch, DateTime asOf)
            => batch.ExpiryDate.HasValue && batch.ExpiryDate.Value.Date < asOf.Date;

        /// <summary>True if the batch expires within <paramref name="windowDays"/> (including already expired).</summary>
        public static bool IsNearExpiry(StockBatch batch, DateTime asOf, int windowDays)
        {
            int? d = DaysUntilExpiry(batch, asOf);
            return d.HasValue && d.Value <= windowDays;
        }

        /// <summary>
        /// The nearest expiry date among a set of an item's sellable batches, or null if none dated.
        /// Used to show "nearest expiry" per search result (FR-POS-02).
        /// </summary>
        public static DateTime? NearestExpiry(IEnumerable<StockBatch> batches)
            => batches.Where(b => !b.IsDisposed && b.QuantityUnits > 0 && b.ExpiryDate.HasValue)
                      .Select(b => b.ExpiryDate.Value)
                      .DefaultIfEmpty(DateTime.MaxValue)
                      .Min() is DateTime min && min != DateTime.MaxValue
                ? (DateTime?)min
                : null;
    }
}
