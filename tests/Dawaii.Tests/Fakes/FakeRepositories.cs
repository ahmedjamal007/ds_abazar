using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Tests.Fakes
{
    public class FakeSettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public FakeSettingsRepository Seed(string k, string v) { _d[k] = v; return this; }
        public string Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public IReadOnlyDictionary<string, string> GetAll() => _d;
        public void Set(string key, string value) => _d[key] = value;
    }

    public class FakeAuditRepository : IAuditRepository
    {
        public readonly List<AuditEntry> Entries = new List<AuditEntry>();
        public void Add(AuditEntry entry) => Entries.Add(entry);
        public IReadOnlyList<AuditEntry> GetRecent(int limit) => Entries.AsEnumerable().Reverse().Take(limit).ToList();
    }

    public class FakeItemRepository : IItemRepository
    {
        public readonly List<Item> Items = new List<Item>();
        /// <summary>Ids that refuse deletion, simulating items referenced by sales (see SqliteItemRepository.Delete).</summary>
        public readonly HashSet<int> ItemsWithSales = new HashSet<int>();
        private int _id = 1;

        public Item GetById(int id) => Clone(Items.FirstOrDefault(i => i.Id == id));
        public IReadOnlyList<Item> GetByIds(IEnumerable<int> ids)
        {
            var set = new HashSet<int>(ids);
            return Items.Where(i => set.Contains(i.Id)).Select(Clone).ToList();
        }

        public IReadOnlyList<Item> Search(string term, bool activeOnly = true, int limit = 50)
        {
            IEnumerable<Item> q = Items;
            if (activeOnly) q = q.Where(i => i.IsActive);
            if (!string.IsNullOrWhiteSpace(term))
                q = q.Where(i => (i.NameEn ?? "").Contains(term) || (i.GenericName ?? "").Contains(term));
            return q.OrderBy(i => i.NameEn).Take(limit).Select(Clone).ToList();
        }

        public IReadOnlyList<Item> GetAll(bool activeOnly = true)
            => Items.Where(i => !activeOnly || i.IsActive).Select(Clone).ToList();

        public int Add(Item item) { item.Id = _id++; Items.Add(Clone(item)); return item.Id; }

        public void Update(Item item)
        {
            var e = Items.FirstOrDefault(i => i.Id == item.Id);
            if (e == null) return;
            e.NameEn = item.NameEn; e.GenericName = item.GenericName;
            e.UnitsPerStrip = item.UnitsPerStrip; e.StripsPerBox = item.StripsPerBox;
            e.PurchasePrice = item.PurchasePrice; e.SellingPrice = item.SellingPrice;
            e.MinQuantity = item.MinQuantity; e.ExpiryWarnDays = item.ExpiryWarnDays; e.IsActive = item.IsActive;
            e.SubstituteOf = item.SubstituteOf;
        }

        public void SetActive(int itemId, bool active)
        {
            var e = Items.FirstOrDefault(i => i.Id == itemId);
            if (e != null) e.IsActive = active;
        }

        public bool Delete(int itemId)
        {
            if (ItemsWithSales.Contains(itemId)) return false;
            var e = Items.FirstOrDefault(i => i.Id == itemId);
            if (e == null) return false;
            Items.Remove(e);
            return true;
        }

        public void UpdateSellingPrice(int itemId, decimal newPrice)
        {
            var e = Items.FirstOrDefault(i => i.Id == itemId);
            if (e != null) e.SellingPrice = newPrice;
        }

        public void UpdatePurchaseAndSellingPrice(int itemId, decimal purchasePerUnit, decimal sellingPerUnit)
        {
            var e = Items.FirstOrDefault(i => i.Id == itemId);
            if (e != null) { e.PurchasePrice = purchasePerUnit; e.SellingPrice = sellingPerUnit; e.ManualPrice = false; }
        }

        /// <summary>The shelf whose batches follow an item's price. Optional so older tests that never
        /// touch stock keep constructing this with no arguments.</summary>
        public FakeStockRepository Stock { get; set; }

        public void ApplySellingPrice(int itemId, decimal sellingPerUnit, bool manual)
        {
            var e = Items.FirstOrDefault(i => i.Id == itemId);
            if (e == null) throw new ValidationException("الصنف غير موجود.");
            e.SellingPrice = sellingPerUnit;
            e.ManualPrice = manual;
            Stock?.ApplySellingPriceToAllBatches(itemId, sellingPerUnit);
        }

        private static Item Clone(Item i) => i == null ? null : new Item
        {
            Id = i.Id, NameEn = i.NameEn, GenericName = i.GenericName,
            UnitsPerStrip = i.UnitsPerStrip, StripsPerBox = i.StripsPerBox,
            PurchasePrice = i.PurchasePrice, SellingPrice = i.SellingPrice, ManualPrice = i.ManualPrice, MinQuantity = i.MinQuantity,
            ExpiryWarnDays = i.ExpiryWarnDays, SubstituteOf = i.SubstituteOf, IsActive = i.IsActive, CreatedAt = i.CreatedAt, UpdatedAt = i.UpdatedAt
        };
    }

    public class FakePurchaseRepository : IPurchaseRepository
    {
        public readonly List<Purchase> Purchases = new List<Purchase>();
        private int _id = 1;

        public int Add(Purchase p) { p.Id = _id++; Purchases.Add(p); return p.Id; }

        public IReadOnlyList<Purchase> GetRange(DateTime fromInclusive, DateTime toExclusive)
            => Purchases.Where(p => p.CreatedAt >= fromInclusive && p.CreatedAt < toExclusive)
                        .OrderByDescending(p => p.CreatedAt).ToList();

        public decimal TotalInRange(DateTime fromInclusive, DateTime toExclusive)
            => Purchases.Where(p => p.CreatedAt >= fromInclusive && p.CreatedAt < toExclusive).Sum(p => p.Amount);

        public void Delete(int id) => Purchases.RemoveAll(p => p.Id == id);
    }

    public class FakeStockRepository : IStockRepository
    {
        public readonly List<StockBatch> Batches = new List<StockBatch>();
        public readonly List<StockAdjustment> Adjustments = new List<StockAdjustment>();
        private int _id = 1;

        public IReadOnlyList<StockBatch> GetBatches(int itemId, bool includeDisposed = false)
            => Batches.Where(b => b.ItemId == itemId && (includeDisposed || !b.IsDisposed)).Select(Clone).ToList();

        /// <summary>Ordered the way the real one is — soonest expiry first, undated last — because FEFO
        /// takes the first batch it is handed and a fake that returned insertion order would let a broken
        /// allocation pass here and fail against the database.</summary>
        public IReadOnlyList<StockBatch> GetSellableBatches(int itemId)
            => Batches.Where(b => b.ItemId == itemId && !b.IsDisposed && b.QuantityUnits > 0)
                      .OrderBy(b => b.ExpiryDate.HasValue ? 0 : 1)
                      .ThenBy(b => b.ExpiryDate ?? DateTime.MaxValue)
                      .ThenBy(b => b.Id)
                      .Select(Clone).ToList();

        public IReadOnlyList<StockBatch> GetExpiringBefore(DateTime cutoff)
            => Batches.Where(b => !b.IsDisposed && b.QuantityUnits > 0
                                  && b.ExpiryDate.HasValue && b.ExpiryDate.Value.Date <= cutoff.Date)
                      .OrderBy(b => b.ExpiryDate).ThenBy(b => b.Id).Select(Clone).ToList();

        public void ApplySellingPriceToAllBatches(int itemId, decimal perUnitSellingPrice)
        {
            foreach (StockBatch b in Batches.Where(x => x.ItemId == itemId && !x.IsDisposed))
                b.BoxSellingPrice = perUnitSellingPrice * Math.Max(1, b.StripsPerBox) * Math.Max(1, b.UnitsPerStrip);
        }

        public StockBatch GetBatch(int batchId) => Clone(Batches.FirstOrDefault(b => b.Id == batchId));

        public IReadOnlyList<StockBatch> GetActiveBatches()
            => Batches.Where(b => !b.IsDisposed && b.QuantityUnits > 0).Select(Clone).ToList();

        public int AddBatch(StockBatch batch) { batch.Id = _id++; Batches.Add(Clone(batch)); return batch.Id; }

        public void UpdateBatch(StockBatch batch)
        {
            var b = Batches.FirstOrDefault(x => x.Id == batch.Id);
            if (b == null) return;
            b.QuantityUnits = batch.QuantityUnits;
            b.ExpiryDate = batch.ExpiryDate;
            b.BatchNumber = batch.BatchNumber;
            b.StripsPerBox = batch.StripsPerBox;
            b.UnitsPerStrip = batch.UnitsPerStrip;
            b.BoxPurchasePrice = batch.BoxPurchasePrice;
            b.BoxSellingPrice = batch.BoxSellingPrice;
        }

        public StockBatch GetLatestBatch(int itemId)
            => Clone(Batches.Where(b => b.ItemId == itemId && !b.IsDisposed)
                            .OrderByDescending(b => b.ReceivedAt).ThenByDescending(b => b.Id)
                            .FirstOrDefault());

        public void SetBatchQuantity(int batchId, int quantityUnits)
        {
            var b = Batches.FirstOrDefault(x => x.Id == batchId);
            if (b != null) b.QuantityUnits = quantityUnits;
        }

        // Matches production (V1.4): disposing a lost batch hard-deletes its row.
        public void DisposeBatch(int batchId) => Batches.RemoveAll(x => x.Id == batchId);

        public void AddAdjustment(StockAdjustment adjustment) => Adjustments.Add(adjustment);

        public IReadOnlyDictionary<int, int> GetAvailableUnits(IEnumerable<int> itemIds)
        {
            var ids = itemIds.Distinct().ToList();
            return ids.ToDictionary(id => id,
                id => Batches.Where(b => b.ItemId == id && !b.IsDisposed).Sum(b => b.QuantityUnits));
        }

        public IReadOnlyDictionary<int, StockSummary> GetStockSummaries(IEnumerable<int> itemIds)
        {
            var ids = itemIds.Distinct().ToList();
            return ids.ToDictionary(id => id, id =>
            {
                var live = Batches.Where(b => b.ItemId == id && !b.IsDisposed).ToList();
                return new StockSummary
                {
                    AvailableUnits = live.Sum(b => b.QuantityUnits),
                    NearestExpiry = live.Where(b => b.QuantityUnits > 0 && b.ExpiryDate.HasValue)
                                        .Select(b => (DateTime?)b.ExpiryDate.Value.Date)
                                        .OrderBy(d => d).FirstOrDefault()
                };
            });
        }

        private static StockBatch Clone(StockBatch b) => b == null ? null : new StockBatch
        {
            Id = b.Id, ItemId = b.ItemId, QuantityUnits = b.QuantityUnits, ExpiryDate = b.ExpiryDate,
            BatchNumber = b.BatchNumber, StripsPerBox = b.StripsPerBox, UnitsPerStrip = b.UnitsPerStrip,
            BoxPurchasePrice = b.BoxPurchasePrice, BoxSellingPrice = b.BoxSellingPrice,
            ReceivedAt = b.ReceivedAt, IsDisposed = b.IsDisposed
            // PurchasePrice/SellingPrice/Strip* are derived properties — nothing to copy.
        };
    }
}
