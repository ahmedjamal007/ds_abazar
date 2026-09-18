using System;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>An item plus its live stock summary for POS/inventory lists (FR-POS-02).</summary>
    public class ItemStockView
    {
        public Item Item { get; set; }
        public int AvailableUnits { get; set; }
        public DateTime? NearestExpiry { get; set; }

        public bool IsLowStock => AvailableUnits <= Item.MinQuantity;
    }

    /// <summary>A single near-expiry (or expired) batch with its item, for expiry lists (FR-EXP-01).</summary>
    public class NearExpiryRow
    {
        public Item Item { get; set; }
        public StockBatch Batch { get; set; }
        public int DaysUntilExpiry { get; set; }
        public bool IsExpired => DaysUntilExpiry < 0;

        /// <summary>Value still at risk in this batch (remaining units × cost).</summary>
        public decimal ValueAtRisk => Batch.QuantityUnits * Batch.PurchasePrice;
    }
}
