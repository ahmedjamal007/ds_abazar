using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Turns a POS cart into a fully-costed <see cref="Sale"/> with FEFO batch allocations,
    /// price/cost snapshots and totals (FR-POS-03/04/05, FR-PRC-03). Pure and side-effect free:
    /// it does not touch the database — the transactional store persists what this returns.
    /// </summary>
    public static class SaleBuilder
    {
        public static Sale Build(
            IList<CartLine> cart,
            Func<int, Item> getItem,
            Func<int, IReadOnlyList<StockBatch>> getBatches,
            SaleHeader header)
        {
            if (cart == null || cart.Count == 0)
                throw new ValidationException("لا توجد أصناف في الفاتورة.");
            if (header == null) throw new ArgumentNullException(nameof(header));
            if (header.SaleType == SaleType.Credit && header.CustomerId == null)
                throw new ValidationException("البيع بالآجل يتطلب اختيار عميل.");
            if (header.Discount < 0) throw new ValidationException("الخصم غير صالح.");

            // Working copies of each item's batches so stock shared across lines is not double-counted.
            var working = new Dictionary<int, List<StockBatch>>();

            var sale = new Sale
            {
                UserId = header.UserId,
                CustomerId = header.CustomerId,
                SaleType = header.SaleType,
                Terminal = header.Terminal,
                Status = SaleStatus.Completed,
                CreatedAt = DateTime.Now
            };

            foreach (CartLine req in cart)
            {
                if (req.Quantity <= 0) throw new ValidationException("الكمية يجب أن تكون أكبر من صفر.");

                Item item = getItem(req.ItemId);
                if (item == null || !item.IsActive)
                    throw new ValidationException("أحد الأصناف غير متاح.");

                if (!working.TryGetValue(item.Id, out var batches))
                {
                    batches = (getBatches(item.Id) ?? new List<StockBatch>())
                        .Select(Clone).ToList();
                    working[item.Id] = batches;
                }

                int unitsEach = UnitConverter.UnitsIn(item, req.UnitType);

                // Multiplied in a long first: a quantity near int.MaxValue overflowed to a negative,
                // which the allocator then rejected as "الكمية يجب أن تكون أكبر من صفر" — an error that
                // told the cashier the opposite of what they had typed.
                long requested = (long)unitsEach * req.Quantity;
                if (requested > int.MaxValue)
                    throw new ValidationException("الكمية كبيرة أكثر من اللازم.");
                int totalUnits = (int)requested;

                // FEFO allocation against the remaining working stock.
                var allocations = FefoAllocator.Allocate(item.Id, batches, totalUnits);

                // Reduce working stock so the next line/scan sees the updated remaining.
                foreach (BatchAllocation a in allocations)
                {
                    StockBatch b = batches.First(x => x.Id == a.BatchId);
                    b.QuantityUnits -= a.Units;
                }

                // Unpriced items (NULL selling price) are hidden from the POS; guard here too in
                // case one arrives via a stale cart or a scanned code.
                if (!item.SellingPrice.HasValue)
                    throw new ValidationException($"الصنف \"{item.NameEn}\" بدون سعر بيع — استلم مخزوناً له أولاً ليُحسب سعره تلقائياً.");
                decimal unitPrice = item.SellingPrice.Value;
                decimal lineTotal = decimal.Round(unitPrice * totalUnits, 2);
                decimal lineCost = decimal.Round(allocations.Sum(a => a.Units * a.UnitCost), 2);

                sale.Lines.Add(new SaleLine
                {
                    ItemId = item.Id,
                    ItemName = item.NameEn,
                    UnitType = req.UnitType,
                    Quantity = req.Quantity,
                    UnitsEach = unitsEach,
                    UnitPrice = unitPrice,
                    LineTotal = lineTotal,
                    CostTotal = lineCost,
                    Allocations = allocations.Select(a => new SaleLineAllocation
                    {
                        BatchId = a.BatchId,
                        Units = a.Units,
                        UnitCost = a.UnitCost
                    }).ToList()
                });
            }

            sale.Subtotal = decimal.Round(sale.Lines.Sum(l => l.LineTotal), 2);
            if (header.Discount > sale.Subtotal)
                throw new ValidationException("الخصم أكبر من إجمالي الفاتورة.");
            sale.Discount = decimal.Round(header.Discount, 2);
            sale.Total = decimal.Round(sale.Subtotal - sale.Discount, 2);
            sale.CostTotal = decimal.Round(sale.Lines.Sum(l => l.CostTotal), 2);

            return sale;
        }

        // PurchasePrice is derived from the box price and packaging, so those are what get copied.
        private static StockBatch Clone(StockBatch b) => new StockBatch
        {
            Id = b.Id,
            ItemId = b.ItemId,
            QuantityUnits = b.QuantityUnits,
            ExpiryDate = b.ExpiryDate,
            BatchNumber = b.BatchNumber,
            StripsPerBox = b.StripsPerBox,
            UnitsPerStrip = b.UnitsPerStrip,
            BoxPurchasePrice = b.BoxPurchasePrice,
            BoxSellingPrice = b.BoxSellingPrice,
            ReceivedAt = b.ReceivedAt,
            IsDisposed = b.IsDisposed
        };
    }
}
