using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Items, stock batches, expiry and low-stock (FR-INV-*, FR-EXP-*).
    /// V1.8: the stockroom is opened per employee, not to everyone. The manager and any employee they
    /// promoted to "موظف ذو امتيازات" (<see cref="Role.FullEmployee"/>) may add, edit or delete items
    /// and receive/adjust/write off stock — the person who unpacks the shipment is the person who
    /// records it. A plain cashier still only searches and views. Every mutation is written to the
    /// audit log under its author, so "who changed this price" stays answerable.
    /// Pricing (V1.7) is entered, not calculated: each stock batch carries a BOX purchase price and a
    /// BOX selling price typed in on receipt, and strip/unit prices are divided down from those. There
    /// is no price list, no category and no profit multiplier. An item's own prices mirror its most
    /// recently received batch so the POS has a single price per item to sell at.
    /// </summary>
    public class InventoryService
    {
        private readonly IItemRepository _items;
        private readonly IStockRepository _stock;
        private readonly ISettingsRepository _settings;
        private readonly IAuditRepository _audit;
        private readonly IItemCodeRepository _codes;

        public InventoryService(IItemRepository items, IStockRepository stock,
            ISettingsRepository settings, IAuditRepository audit, IItemCodeRepository codes)
        {
            _items = items;
            _stock = stock;
            _settings = settings;
            _audit = audit;
            _codes = codes;
        }

        // ---------------- Reads ----------------

        public IReadOnlyList<ItemStockView> Search(string term, int limit = 50)
        {
            var items = _items.Search(term ?? string.Empty, activeOnly: true, limit: limit);
            return BuildViews(items);
        }

        /// <summary>POS search: only items with an assigned selling price are sellable — items whose
        /// price is still NULL (received but never priced) are hidden from the employee POS.</summary>
        public IReadOnlyList<ItemStockView> SearchSellable(string term, int limit = 50)
        {
            var items = _items.Search(term ?? string.Empty, activeOnly: true, limit: limit)
                .Where(i => i.SellingPrice.HasValue).ToList();
            return BuildViews(items);
        }

        public ItemStockView GetView(int itemId)
        {
            Item item = _items.GetById(itemId);
            if (item == null) return null;
            var batches = _stock.GetSellableBatches(itemId);
            return new ItemStockView
            {
                Item = item,
                AvailableUnits = FefoAllocator.AvailableUnits(batches),
                NearestExpiry = ExpiryEvaluator.NearestExpiry(batches)
            };
        }

        public IReadOnlyList<ItemStockView> GetLowStock()
            => BuildViews(_items.GetAll(activeOnly: true)).Where(v => v.IsLowStock).ToList();

        /// <summary>Batches expiring within <paramref name="windowDays"/> (default from settings), sorted soonest first.</summary>
        public IReadOnlyList<NearExpiryRow> GetNearExpiry(int? windowDays = null, DateTime? asOf = null)
        {
            DateTime now = asOf ?? DateTime.Now;
            int window = windowDays ?? GetInt("expiry_warn_days", 90);

            // Narrowed in SQL. This used to read every active batch in the pharmacy and filter in
            // memory, which is tens of thousands of rows once a shelf has a few years behind it — for a
            // list that is normally a handful of boxes.
            var batches = _stock.GetExpiringBefore(now.Date.AddDays(window))
                                .Where(b => ExpiryEvaluator.IsNearExpiry(b, now, window)).ToList();
            var itemMap = _items.GetByIds(batches.Select(b => b.ItemId).Distinct()).ToDictionary(i => i.Id);

            return batches
                .Where(b => itemMap.ContainsKey(b.ItemId))
                .Select(b => new NearExpiryRow
                {
                    Item = itemMap[b.ItemId],
                    Batch = b,
                    DaysUntilExpiry = ExpiryEvaluator.DaysUntilExpiry(b, now) ?? int.MaxValue
                })
                .OrderBy(r => r.DaysUntilExpiry)
                .ToList();
        }

        // ---------------- Catalog (any signed-in staff) ----------------

        public int CreateItem(User actingUser, Item item)
        {
            RequireStaff(actingUser);
            Validate(item);
            if (item.MinQuantity <= 0) item.MinQuantity = GetInt("low_stock_default", 0);
            int id = _items.Add(item);
            Audit(actingUser, "CreateItem", "items", id, item.NameEn);
            return id;
        }

        /// <summary>Saves catalog edits (names, packaging, substitute). Prices are NOT edited here — they
        /// belong to the stock batch and are carried onto the item from the newest one, so the stored
        /// prices are preserved rather than taken from the passed-in copy (callers that rebuild an Item
        /// from form fields would otherwise blank them).</summary>
        public void UpdateItemDetails(User actingUser, Item item)
        {
            RequireStaff(actingUser);
            Validate(item);
            Item stored = _items.GetById(item.Id);
            if (stored != null)
            {
                item.PurchasePrice = stored.PurchasePrice;
                item.SellingPrice = stored.SellingPrice;
            }
            _items.Update(item);
            Audit(actingUser, "UpdateItem", "items", item.Id, item.NameEn);
        }

        /// <summary>Sets the low-stock (min) and target ceiling (max) levels in single units (V1.3 "مخزون كامل").</summary>
        public void SetStockLevels(User actingUser, int itemId, int minUnits, int maxUnits)
        {
            RequireStaff(actingUser);
            if (minUnits < 0 || maxUnits < 0) throw new ValidationException("حدود المخزون يجب ألا تكون سالبة.");
            if (maxUnits > 0 && maxUnits < minUnits) throw new ValidationException("الحد الأعلى يجب أن يكون أكبر من الحد الأدنى.");
            Item item = _items.GetById(itemId);
            if (item == null) throw new ValidationException("الصنف غير موجود.");
            item.MinQuantity = minUnits;
            item.MaxQuantity = maxUnits;
            _items.Update(item);
            Audit(actingUser, "SetStockLevels", "items", itemId, $"min {minUnits}, max {maxUnits}");
        }

        /// <summary>Deletes an item and its stock/codes/history. Returns false if it has sales (kept for
        /// records — the caller should offer to deactivate instead).
        /// The barcode goes either way: deleting the row cascades it, and an item kept only for its sales
        /// history is out of the catalog, so letting it keep a (unique) code would block reusing that
        /// barcode on the drug that replaces it.</summary>
        /// <summary>
        /// Deletes one item, or keeps it and returns false when it has sales history.
        ///
        /// Either way the barcode is released (V1.9). A barcode is the manufacturer's number for the
        /// PRODUCT, not for this row: a drug being retired has to give it up so the replacement record
        /// can be scanned in under the same number. The caller is expected to follow a refusal by
        /// deactivating the item, and must tell the user the barcode has gone — an item left active
        /// without one still sells, but only by name.
        /// </summary>
        public bool DeleteItem(User actingUser, int itemId)
        {
            RequireStaff(actingUser);
            Item item = _items.GetById(itemId);
            bool deleted = _items.Delete(itemId);
            if (deleted) Audit(actingUser, "DeleteItem", "items", itemId, item?.NameEn);
            else _codes.RemoveForItem(itemId);
            return deleted;
        }

        /// <summary>
        /// Deletes every item in the catalog (active and inactive) with its stock and history. Items
        /// referenced by sales are kept for the records and returned via <paramref name="withSales"/> so
        /// the caller can offer to deactivate them instead — releasing their barcodes on the way, the
        /// same rule <see cref="DeleteItem"/> follows.
        ///
        /// Each item's deletion is its own transaction, so a failure partway leaves the catalog partly
        /// emptied. That is recoverable — running it again continues from where it stopped — but only if
        /// the operator is told, so the progress so far is audited and named in the error rather than
        /// vanishing with the exception and leaving the screen reporting that nothing happened.
        /// </summary>
        public int DeleteAllItems(User actingUser, out IReadOnlyList<Item> withSales)
        {
            RequireStaff(actingUser);
            var kept = new List<Item>();
            int deleted = 0;
            try
            {
                foreach (Item item in _items.GetAll(activeOnly: false))
                {
                    if (_items.Delete(item.Id)) deleted++;
                    else { _codes.RemoveForItem(item.Id); kept.Add(item); }
                }
            }
            catch (Exception ex)
            {
                Audit(actingUser, "DeleteAllItems", "items", null,
                    $"INTERRUPTED after {deleted} deleted, {kept.Count} kept: {ex.Message}");
                throw new DomainException(
                    "توقف الحذف بعد حذف " + deleted + " صنف. " +
                    "قائمة الأصناف الآن ناقصة — أعد المحاولة لإكمال الحذف أو استعد نسخة احتياطية.\n\n" +
                    "السبب: " + ex.Message);
            }

            withSales = kept;
            Audit(actingUser, "DeleteAllItems", "items", null, $"deleted {deleted}, kept {kept.Count} with sales");
            return deleted;
        }

        public void SetItemActive(User actingUser, int itemId, bool active)
        {
            RequireStaff(actingUser);
            _items.SetActive(itemId, active);
            Audit(actingUser, active ? "ActivateItem" : "DeactivateItem", "items", itemId, null);
        }

        // ---------------- Pricing ----------------
        // There is no pricing screen. Prices come from the stock batch that was typed in: box purchase
        // price, box selling price, strips per box. Strip and unit prices are divided down from those
        // (see BatchPricing) and are never stored as editable values.

        /// <summary>Copies the item's current prices from its newest remaining batch, so the POS sells at
        /// the latest shipment's price. An item whose last batch is gone keeps its final known price but
        /// its cost/selling figures stop moving; an item that never had one stays unpriced (NULL) and so
        /// stays hidden from the POS.</summary>
        private void SyncItemPricesFromLatestBatch(int itemId)
        {
            StockBatch latest = _stock.GetLatestBatch(itemId);
            if (latest == null) return;

            Item item = _items.GetById(itemId);
            if (item == null) return;

            // The batch's packaging is the item's packaging going forward, so box prices shown on the
            // catalog screens rebuild to exactly what was typed on the batch.
            if (item.StripsPerBox != latest.StripsPerBox)
            {
                item.StripsPerBox = latest.StripsPerBox;
                _items.Update(item);
            }
            _items.UpdatePurchaseAndSellingPrice(itemId, latest.PurchasePrice, latest.SellingPrice);

            // The new price is the shelf's price, not just this shipment's: every batch of the drug is
            // brought to it so the batches screen and the till never disagree about what it sells for.
            // Costs stay as they were — each batch keeps what it was actually bought for.
            _stock.ApplySellingPriceToAllBatches(itemId, latest.SellingPrice);
        }

        // ---------------- Stock (any signed-in staff) ----------------

        /// <summary>
        /// Records a received shipment (FR-INV-02). The user enters the quantity in BOXES and the box
        /// purchase/selling prices; strip and unit prices are divided down from them and never typed.
        /// e.g. 10 boxes at 3200/box buy and 4000/box sell with 4 strips per box stores 10 boxes of
        /// stock and prices a strip at 800 cost / 1000 sale.
        /// The item then mirrors this batch's prices, so it is the newest shipment the POS sells at.
        /// </summary>
        /// <param name="quantityBoxes">How many boxes arrived (converted to single units for storage).</param>
        public int ReceiveStock(User actingUser, int itemId, int quantityBoxes, DateTime? expiry,
            decimal boxPurchasePrice, decimal boxSellingPrice, int stripsPerBox, string batchNumber = null)
        {
            RequireStaff(actingUser);
            if (quantityBoxes <= 0) throw new ValidationException("عدد العلب يجب أن يكون أكبر من صفر.");
            BatchPricing.ValidateStripsPerBox(stripsPerBox);
            BatchPricing.ValidatePrice(boxPurchasePrice, "سعر شراء العلبة");
            BatchPricing.ValidatePrice(boxSellingPrice, "سعر بيع العلبة");

            Item item = _items.GetById(itemId);
            if (item == null) throw new ValidationException("الصنف غير موجود.");

            int unitsPerStrip = Math.Max(1, item.UnitsPerStrip);
            var batch = new StockBatch
            {
                ItemId = itemId,
                QuantityUnits = quantityBoxes * BatchPricing.UnitsPerBox(stripsPerBox, unitsPerStrip),
                ExpiryDate = expiry,
                BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim(),
                StripsPerBox = stripsPerBox,
                UnitsPerStrip = unitsPerStrip,
                BoxPurchasePrice = boxPurchasePrice,
                BoxSellingPrice = boxSellingPrice,
                ReceivedAt = DateTime.Now
            };
            int id = _stock.AddBatch(batch);

            SyncItemPricesFromLatestBatch(itemId);

            Audit(actingUser, "ReceiveStock", "stock_batches", id,
                $"{quantityBoxes} box(es), batch {batch.BatchNumber ?? "-"}, " +
                $"buy {boxPurchasePrice:0.00}/box (strip {batch.StripPurchasePrice:0.00}), " +
                $"sell {boxSellingPrice:0.00}/box (strip {batch.StripSellingPrice:0.00})");
            return id;
        }

        /// <summary>
        /// Edits an existing batch's own details: batch number, expiry, box purchase/selling prices and
        /// strips per box. Strip prices are recomputed from the new values automatically — they are
        /// derived properties, so there is nothing to keep in step by hand. If this is the item's newest
        /// batch, the item's prices follow it, which is how a corrected purchase or selling price reaches
        /// the POS.
        /// </summary>
        public void UpdateBatch(User actingUser, StockBatch edited)
        {
            RequireStaff(actingUser);
            if (edited == null) throw new ValidationException("بيانات الدفعة غير صالحة.");
            BatchPricing.ValidateStripsPerBox(edited.StripsPerBox);
            BatchPricing.ValidatePrice(edited.BoxPurchasePrice, "سعر شراء العلبة");
            BatchPricing.ValidatePrice(edited.BoxSellingPrice, "سعر بيع العلبة");
            if (edited.QuantityUnits < 0) throw new ValidationException("الكمية يجب ألا تكون سالبة.");

            StockBatch stored = _stock.GetBatch(edited.Id);
            if (stored == null) throw new ValidationException("الدفعة غير موجودة.");

            edited.ItemId = stored.ItemId;               // never let an edit move a batch to another item
            edited.UnitsPerStrip = Math.Max(1, edited.UnitsPerStrip);
            edited.BatchNumber = string.IsNullOrWhiteSpace(edited.BatchNumber) ? null : edited.BatchNumber.Trim();
            _stock.UpdateBatch(edited);

            SyncItemPricesFromLatestBatch(stored.ItemId);

            Audit(actingUser, "UpdateBatch", "stock_batches", edited.Id,
                $"buy {edited.BoxPurchasePrice:0.00}/box, sell {edited.BoxSellingPrice:0.00}/box, " +
                $"{edited.StripsPerBox} strip(s)/box -> strip sell {edited.StripSellingPrice:0.00}");
        }

        public void AdjustStock(User actingUser, int batchId, int deltaUnits, string reason)
        {
            RequireStaff(actingUser);
            if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("يجب إدخال سبب التعديل.");
            StockBatch batch = _stock.GetBatch(batchId);
            if (batch == null) throw new ValidationException("الدفعة غير موجودة.");

            int newQty = batch.QuantityUnits + deltaUnits;
            if (newQty < 0) throw new ValidationException("لا يمكن أن يصبح المخزون سالباً.");

            _stock.SetBatchQuantity(batchId, newQty);
            _stock.AddAdjustment(new StockAdjustment
            {
                ItemId = batch.ItemId,
                BatchId = batchId,
                DeltaUnits = deltaUnits,
                Reason = reason.Trim(),
                UserId = actingUser.Id,
                CreatedAt = DateTime.Now
            });
            Audit(actingUser, "AdjustStock", "stock_batches", batchId, $"{deltaUnits}: {reason}");
        }

        /// <summary>Writes off an (expired) batch: records the loss (FR-EXP-03) and then hard-deletes the
        /// batch row (V1.4 — "after I lose the batch, delete it"). The loss value is kept as a stock
        /// adjustment for the reports/audit even though the batch itself is gone. Idempotent: a batch that
        /// no longer exists (e.g. already written off, or its item was deleted) is a no-op returning false —
        /// the caller need not pre-check stale rows.</summary>
        public bool DisposeBatch(User actingUser, int batchId, string reason = "انتهاء الصلاحية")
        {
            RequireStaff(actingUser);
            StockBatch batch = _stock.GetBatch(batchId);
            if (batch == null || batch.IsDisposed) return false;   // already gone / already disposed — nothing to do

            int lost = batch.QuantityUnits;
            _stock.DisposeBatch(batchId);
            if (lost > 0)
                _stock.AddAdjustment(new StockAdjustment
                {
                    ItemId = batch.ItemId,
                    BatchId = batchId,
                    DeltaUnits = -lost,
                    Reason = reason,
                    UserId = actingUser.Id,
                    CreatedAt = DateTime.Now
                });
            // Writing off the newest shipment falls the item's prices back to the one before it.
            SyncItemPricesFromLatestBatch(batch.ItemId);

            Audit(actingUser, "DisposeBatch", "stock_batches", batchId,
                $"loss {lost} units, value {(lost * batch.PurchasePrice):0.00}");
            return true;
        }

        // ---------------- helpers ----------------

        private IReadOnlyList<ItemStockView> BuildViews(IReadOnlyList<Item> items)
        {
            // One aggregate query for the whole list instead of per-item batch loads (NFR-01).
            var summaries = _stock.GetStockSummaries(items.Select(i => i.Id));
            var views = new List<ItemStockView>(items.Count);
            foreach (Item item in items)
            {
                summaries.TryGetValue(item.Id, out StockSummary s);
                views.Add(new ItemStockView
                {
                    Item = item,
                    AvailableUnits = s.AvailableUnits,
                    NearestExpiry = s.NearestExpiry
                });
            }
            return views;
        }

        private static void Validate(Item item)
        {
            if (item == null) throw new ValidationException("بيانات الصنف غير صالحة.");
            if (string.IsNullOrWhiteSpace(item.NameEn)) throw new ValidationException("اسم الصنف مطلوب.");
            if (item.UnitsPerStrip < 1) throw new ValidationException("عدد الحبات في الشريط يجب أن يكون 1 على الأقل.");
            if (item.StripsPerBox < 1) throw new ValidationException("عدد الأشرطة في العلبة يجب أن يكون 1 على الأقل.");
            if ((item.SellingPrice ?? 0) < 0 || item.PurchasePrice < 0) throw new ValidationException("الأسعار يجب ألا تكون سالبة.");
        }

        private int GetInt(string key, int fallback)
            => int.TryParse(_settings.Get(key), out int v) ? v : fallback;

        private static void RequireStaff(User user)
            => Guard.RequireInventoryAccess(user,
                "إدارة الأصناف والمخزون متاحة للمدير أو الموظف ذي الامتيازات فقط.");

        private void Audit(User user, string action, string entity, int? entityId, string details)
            => _audit.Log(user, action, entity, entityId, details);
    }
}
