using System;

namespace Dawaii.Core.Models
{
    /// <summary>
    /// One medicine on a supplier's invoice: how many boxes arrived, what they cost, and what they will
    /// be sold at. Saving the invoice turns each line into a stock batch, and <see cref="StockBatchId"/>
    /// is the link back to it — so a line can always be read as "this is the shipment on the shelf".
    ///
    /// The name is snapshotted alongside the item id for the same reason a sale line snapshots it: the
    /// invoice is a record of what was delivered on the day, and renaming a drug later must not rewrite
    /// the paperwork it arrived on.
    /// </summary>
    public class PurchaseInvoiceLine
    {
        public int Id { get; set; }
        public int InvoiceId { get; set; }
        public int ItemId { get; set; }
        public string ItemName { get; set; }

        /// <summary>Boxes delivered. Quantity is counted in boxes here because that is how a supplier
        /// sells and how the invoice is written; the batch converts it to single units.</summary>
        public int QuantityBoxes { get; set; }

        /// <summary>Packaging for THIS shipment — the same box can come 10 strips one month and 4 the
        /// next, and the price per strip depends on it.</summary>
        public int StripsPerBox { get; set; } = 1;

        public decimal BoxPurchasePrice { get; set; }

        /// <summary>What a box of this will be sold at. Entered on the invoice because the moment the
        /// pharmacy learns the new cost is the moment it decides the new price.</summary>
        public decimal BoxSellingPrice { get; set; }

        public DateTime? ExpiryDate { get; set; }
        public string BatchNumber { get; set; }

        /// <summary>The stock batch this line created, once the invoice is saved.</summary>
        public int? StockBatchId { get; set; }

        /// <summary>What this line adds to the invoice total.</summary>
        public decimal LineTotal => decimal.Round(BoxPurchasePrice * QuantityBoxes, 2);
    }
}
