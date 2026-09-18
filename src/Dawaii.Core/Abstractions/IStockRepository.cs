using System;
using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>Aggregated live stock for one item (single query for whole lists, NFR-01).</summary>
    public struct StockSummary
    {
        public int AvailableUnits;
        public DateTime? NearestExpiry;
    }

    public interface IStockRepository
    {
        /// <summary>
        /// Available units + nearest expiry per item in ONE query — used by search/list screens
        /// instead of loading every item's batches individually (NFR-01 at 20k items).
        /// </summary>
        IReadOnlyDictionary<int, StockSummary> GetStockSummaries(IEnumerable<int> itemIds);

        /// <summary>Every batch the item has ever had, spent ones included — the batches screen shows
        /// them as history. Selling and stock figures want <see cref="GetSellableBatches"/> instead.</summary>
        IReadOnlyList<StockBatch> GetBatches(int itemId, bool includeDisposed = false);

        /// <summary>
        /// Only the batches with stock left in them, soonest to expire first — what FEFO allocates from
        /// and what every "how much is on the shelf" figure is built on.
        ///
        /// A batch is never deleted when it empties, so after a few years most of a drug's rows hold
        /// zero and can never be sold again. Reading them anyway made every sale slower as the pharmacy
        /// got older, and let a spent, long-expired batch masquerade as the item's nearest expiry.
        /// </summary>
        IReadOnlyList<StockBatch> GetSellableBatches(int itemId);

        /// <summary>
        /// Sellable batches dated on or before <paramref name="cutoff"/> — the near-expiry watch list,
        /// narrowed by the database rather than by loading every batch in the pharmacy and filtering in
        /// memory. On a shelf with years of history that difference is tens of thousands of rows.
        /// </summary>
        IReadOnlyList<StockBatch> GetExpiringBefore(DateTime cutoff);

        /// <summary>
        /// Re-prices every batch of an item to <paramref name="perUnitSellingPrice"/> (V2.2). A price
        /// change applies to the whole shelf, not just the shipment that carried it: the customer buying
        /// panadol pays today's price whichever box comes off the shelf, so leaving older batches at
        /// yesterday's figure only made the batches screen contradict the till.
        ///
        /// Each batch's BOX price is worked out from its own packaging, because a drug can arrive 10
        /// strips to a box one month and 4 the next — the per-unit price is what has to match, not the
        /// box figure. Purchase prices are never touched: what a batch cost is history, and profit is
        /// measured against it.
        /// </summary>
        void ApplySellingPriceToAllBatches(int itemId, decimal perUnitSellingPrice);
        StockBatch GetBatch(int batchId);

        /// <summary>All non-disposed batches with stock (for low-stock / near-expiry reports).</summary>
        IReadOnlyList<StockBatch> GetActiveBatches();

        int AddBatch(StockBatch batch);

        /// <summary>Saves an edit to a batch's own details (batch number, expiry, box prices, strips per
        /// box). Strip prices are never persisted — they are always derived from the box prices.</summary>
        void UpdateBatch(StockBatch batch);

        /// <summary>The newest non-disposed batch for an item — the one whose box prices the item's
        /// current selling price mirrors. Null when the item has no stock yet.</summary>
        StockBatch GetLatestBatch(int itemId);

        /// <summary>Sets a batch's remaining quantity (used by adjustments and returns).</summary>
        void SetBatchQuantity(int batchId, int quantityUnits);

        void DisposeBatch(int batchId);

        void AddAdjustment(StockAdjustment adjustment);

        /// <summary>Sum of sellable single units per item id.</summary>
        IReadOnlyDictionary<int, int> GetAvailableUnits(IEnumerable<int> itemIds);
    }
}
