using System;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class InventoryServiceTests
    {
        private FakeItemRepository _items;
        private FakeStockRepository _stock;
        private FakeSettingsRepository _settings;
        private FakeAuditRepository _audit;
        private FakeItemCodeRepository _codes;
        private InventoryService _svc;
        private User _admin, _cashier, _fullEmployee;

        [SetUp]
        public void SetUp()
        {
            _items = new FakeItemRepository();
            _stock = new FakeStockRepository();
            _settings = new FakeSettingsRepository().Seed("expiry_warn_days", "90").Seed("low_stock_default", "10");
            _audit = new FakeAuditRepository();
            _codes = new FakeItemCodeRepository();
            _svc = new InventoryService(_items, _stock, _settings, _audit, _codes);
            _admin = new User { Id = 1, Role = Role.Admin, IsActive = true };
            _cashier = new User { Id = 2, Role = Role.Cashier, IsActive = true };
            _fullEmployee = new User { Id = 3, Role = Role.FullEmployee, IsActive = true };  // موظف ذو امتيازات
        }

        /// <summary>A one-unit-per-box item, so a quantity in boxes equals a quantity in single units and
        /// the stock assertions below read naturally. Pricing tests set their own packaging.</summary>
        private Item NewItem(string name = "صنف", decimal price = 2m, int minQty = 0)
            => new Item { NameEn = name, UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = price, MinQuantity = minQty };

        /// <summary>Receives whole boxes with a box purchase/selling price. Defaults keep the stock-focused
        /// tests readable when the exact prices do not matter to what they assert.</summary>
        private int Receive(int itemId, int boxes, DateTime? expiry = null,
            decimal boxBuy = 1m, decimal boxSell = 2m, int stripsPerBox = 1, string lot = null)
            => _svc.ReceiveStock(_admin, itemId, boxes, expiry, boxBuy, boxSell, stripsPerBox, lot);

        [Test]
        public void ReceiveStock_DerivesStripPrices_ByDividingTheBoxPrices()
        {
            // The spec's worked example: 3200 buy / 4000 sell per box, 4 strips per box.
            var item = NewItem("أملوديبين"); item.SellingPrice = null; item.UnitsPerStrip = 1;
            int id = _svc.CreateItem(_admin, item);

            int batchId = Receive(id, boxes: 10, boxBuy: 3200m, boxSell: 4000m, stripsPerBox: 4);

            StockBatch batch = _stock.GetBatch(batchId);
            Assert.That(batch.StripPurchasePrice, Is.EqualTo(800m), "3200 ÷ 4");
            Assert.That(batch.StripSellingPrice, Is.EqualTo(1000m), "4000 ÷ 4");
            Assert.That(batch.BoxPurchasePrice, Is.EqualTo(3200m), "the entered box price is stored as typed");
            Assert.That(batch.BoxSellingPrice, Is.EqualTo(4000m));
        }

        [Test]
        public void ReceiveStock_StoresQuantityInBoxes_AsSingleUnits()
        {
            var item = NewItem("علب"); item.SellingPrice = null; item.UnitsPerStrip = 5;
            int id = _svc.CreateItem(_admin, item);

            Receive(id, boxes: 10, boxBuy: 100m, boxSell: 150m, stripsPerBox: 4);

            // 10 boxes × 4 strips × 5 units = 200 single units.
            Assert.That(_svc.GetView(id).AvailableUnits, Is.EqualTo(200));
        }

        [Test]
        public void ReceiveStock_RecordsTheBatchNumber()
        {
            int id = _svc.CreateItem(_admin, NewItem("مرقّم"));
            int batchId = Receive(id, 5, lot: "LOT-77");
            Assert.That(_stock.GetBatch(batchId).BatchNumber, Is.EqualTo("LOT-77"));
        }

        [Test]
        public void ReceiveStock_CarriesTheBatchPricesOntoTheItem()
        {
            // The POS sells from the item, so the newest shipment's prices must land there.
            var item = NewItem("بنادول"); item.SellingPrice = null; item.UnitsPerStrip = 1;
            int id = _svc.CreateItem(_admin, item);

            Receive(id, 10, boxBuy: 3200m, boxSell: 4000m, stripsPerBox: 4);

            Item after = _items.GetById(id);
            Assert.That(after.StripsPerBox, Is.EqualTo(4), "the batch's packaging becomes the item's");
            Assert.That(after.SellingPrice * after.UnitsPerBox, Is.EqualTo(4000m), "box price rebuilds exactly");
            Assert.That(after.PurchasePrice * after.UnitsPerBox, Is.EqualTo(3200m));
        }

        [Test]
        public void ReceiveStock_ASecondBatch_RepricesTheItemToTheNewestShipment()
        {
            var item = NewItem("متغير"); item.SellingPrice = null; item.UnitsPerStrip = 1;
            int id = _svc.CreateItem(_admin, item);

            Receive(id, 10, boxBuy: 3200m, boxSell: 4000m, stripsPerBox: 4);
            Receive(id, 10, boxBuy: 3600m, boxSell: 4500m, stripsPerBox: 4);

            Item after = _items.GetById(id);
            Assert.That(after.SellingPrice * after.UnitsPerBox, Is.EqualTo(4500m), "sells at the latest price");
        }

        [Test]
        public void ReceiveStock_RejectsNegativePricesAndBadPackaging()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            Assert.Throws<ValidationException>(() => Receive(id, 1, boxBuy: -1m));
            Assert.Throws<ValidationException>(() => Receive(id, 1, boxSell: -1m));
            Assert.Throws<ValidationException>(() => Receive(id, 1, stripsPerBox: 0));
            Assert.Throws<ValidationException>(() => Receive(id, 0), "boxes must be greater than zero");
        }

        [Test]
        public void ReceiveStock_AsFullEmployee_Allowed()
        {
            // The promoted employee unpacks the shipment, so receiving stock is their job too — by
            // hand as well as through a supplier order.
            int id = _svc.CreateItem(_admin, NewItem());
            int batchId = _svc.ReceiveStock(_fullEmployee, id, 1, null, 100m, 150m, 1);
            Assert.That(_stock.GetBatch(batchId).QuantityUnits, Is.GreaterThan(0));
        }

        [Test]
        public void ReceiveStock_AsPlainCashierOrSignedOut_Denied()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            Assert.Throws<PermissionDeniedException>(
                () => _svc.ReceiveStock(_cashier, id, 1, null, 100m, 150m, 1));
            Assert.Throws<PermissionDeniedException>(
                () => _svc.ReceiveStock(null, id, 1, null, 100m, 150m, 1));
        }

        [Test]
        public void UpdateBatch_ChangingTheBoxSellingPrice_RecalculatesTheStripPrice()
        {
            // The spec's dynamic-update example: 4000 → 4500 over 4 strips moves 1000 → 1125.
            var item = NewItem("محدَّث"); item.SellingPrice = null; item.UnitsPerStrip = 1;
            int id = _svc.CreateItem(_admin, item);
            int batchId = Receive(id, 10, boxBuy: 3200m, boxSell: 4000m, stripsPerBox: 4);
            Assert.That(_stock.GetBatch(batchId).StripSellingPrice, Is.EqualTo(1000m));

            StockBatch edited = _stock.GetBatch(batchId);
            edited.BoxSellingPrice = 4500m;
            _svc.UpdateBatch(_admin, edited);

            Assert.That(_stock.GetBatch(batchId).StripSellingPrice, Is.EqualTo(1125m), "4500 ÷ 4");
            Assert.That(_items.GetById(id).SellingPrice * _items.GetById(id).UnitsPerBox, Is.EqualTo(4500m),
                "the newest batch's new price reaches the POS");
        }

        [Test]
        public void UpdateBatch_ChangingStripsPerBox_RecalculatesBothStripPrices()
        {
            var item = NewItem("تغليف"); item.SellingPrice = null; item.UnitsPerStrip = 1;
            int id = _svc.CreateItem(_admin, item);
            int batchId = Receive(id, 10, boxBuy: 3200m, boxSell: 4000m, stripsPerBox: 4);

            StockBatch edited = _stock.GetBatch(batchId);
            edited.StripsPerBox = 8;                     // same box prices, twice the strips
            _svc.UpdateBatch(_admin, edited);

            StockBatch after = _stock.GetBatch(batchId);
            Assert.That(after.StripPurchasePrice, Is.EqualTo(400m), "3200 ÷ 8");
            Assert.That(after.StripSellingPrice, Is.EqualTo(500m), "4000 ÷ 8");
        }

        [Test]
        public void UpdateBatch_ChangingThePurchasePrice_LeavesTheSellingPriceAlone()
        {
            // Nothing is derived from cost any more — a cost correction must not move what customers pay.
            var item = NewItem("تكلفة"); item.SellingPrice = null; item.UnitsPerStrip = 1;
            int id = _svc.CreateItem(_admin, item);
            int batchId = Receive(id, 10, boxBuy: 3200m, boxSell: 4000m, stripsPerBox: 4);

            StockBatch edited = _stock.GetBatch(batchId);
            edited.BoxPurchasePrice = 3600m;
            _svc.UpdateBatch(_admin, edited);

            StockBatch after = _stock.GetBatch(batchId);
            Assert.That(after.StripPurchasePrice, Is.EqualTo(900m), "3600 ÷ 4");
            Assert.That(after.StripSellingPrice, Is.EqualTo(1000m), "selling price is untouched");
        }

        [Test]
        public void UpdateBatch_AsFullEmployee_Allowed()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            int batchId = Receive(id, 5);
            StockBatch edited = _stock.GetBatch(batchId);
            edited.BoxSellingPrice = 999m;
            _svc.UpdateBatch(_fullEmployee, edited);
            Assert.That(_stock.GetBatch(batchId).BoxSellingPrice, Is.EqualTo(999m));
        }

        [Test]
        public void UpdateBatch_AsPlainCashier_Denied()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            int batchId = Receive(id, 5);
            Assert.Throws<PermissionDeniedException>(
                () => _svc.UpdateBatch(_cashier, _stock.GetBatch(batchId)));
        }

        [Test]
        public void StripPrice_OfASingleStripBox_IsTheBoxPrice()
        {
            int id = _svc.CreateItem(_admin, NewItem("علبة شريط واحد"));
            int batchId = Receive(id, 5, boxBuy: 250m, boxSell: 400m, stripsPerBox: 1);

            StockBatch batch = _stock.GetBatch(batchId);
            Assert.That(batch.StripPurchasePrice, Is.EqualTo(250m));
            Assert.That(batch.StripSellingPrice, Is.EqualTo(400m));
        }

        [Test]
        public void SearchSellable_HidesItemsWithoutSellingPrice()
        {
            _svc.CreateItem(_admin, NewItem("مسعّر", price: 2m));
            var unpriced = NewItem("غير مسعّر", price: 0m);
            unpriced.SellingPrice = null;   // new stock entries leave the price unassigned
            _svc.CreateItem(_admin, unpriced);

            Assert.That(_svc.Search("").Count, Is.EqualTo(2), "admin search sees everything");
            var sellable = _svc.SearchSellable("");
            Assert.That(sellable.Count, Is.EqualTo(1), "POS only sees priced items");
            Assert.That(sellable[0].Item.NameEn, Is.EqualTo("مسعّر"));
        }

        [Test]
        public void UpdateItemDetails_PersistsEditedFields()
        {
            // Mirrors the ItemsModule edit flow: load item, build an edited copy, save by id.
            int id = _svc.CreateItem(_admin, NewItem("قديم", price: 5m));

            var edited = new Item
            {
                Id = id, NameEn = "New", GenericName = "Novum", UnitsPerStrip = 5, StripsPerBox = 4,
                PurchasePrice = 9m, SellingPrice = 5m
            };
            _svc.UpdateItemDetails(_admin, edited);

            Item after = _items.GetById(id);
            Assert.That(after.NameEn, Is.EqualTo("New"));
            Assert.That(after.GenericName, Is.EqualTo("Novum"));
            Assert.That(after.UnitsPerStrip, Is.EqualTo(5));
            Assert.That(after.StripsPerBox, Is.EqualTo(4));
            Assert.That(after.PurchasePrice, Is.Zero,
                "prices belong to the stock batch — an item edit must not move them, " +
                "so the edited 9m is ignored and the stored 0 is kept");
        }

        [Test]
        public void UpdateItemDetails_AsFullEmployee_Allowed()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            var edited = new Item { Id = id, NameEn = "معدَّل", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1m };
            _svc.UpdateItemDetails(_fullEmployee, edited);
            Assert.That(_items.GetById(id).NameEn, Is.EqualTo("معدَّل"));
        }

        [Test]
        public void CreateItem_AsFullEmployee_Allowed()
        {
            int id = _svc.CreateItem(_fullEmployee, NewItem("جديد على الطلب"));
            Assert.That(_items.GetById(id), Is.Not.Null,
                "a drug the pharmacy has never stocked is opened by whoever takes the delivery");
        }

        [Test]
        public void UpdateItemDetails_AsPlainCashier_Denied()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            var edited = new Item { Id = id, NameEn = "x", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1m };
            Assert.Throws<PermissionDeniedException>(() => _svc.UpdateItemDetails(_cashier, edited));
        }

        [Test]
        public void CreateItem_AsFullEmployee_Allowed_V18()
        {
            // The catalog is open to the promoted employee — they may add the drug they just received.
            int id = _svc.CreateItem(_fullEmployee, NewItem("موظف"));
            Assert.That(_items.GetById(id), Is.Not.Null);
        }

        [Test]
        public void CreateItem_AsPlainCashier_Denied()
        {
            Assert.Throws<PermissionDeniedException>(() => _svc.CreateItem(_cashier, NewItem()));
        }

        [Test]
        public void CreateItem_AsDisabledOrSignedOutUser_Denied()
        {
            // A disabled account keeps its role but must not act — deactivating an employee has to
            // withdraw stockroom access as well, not just stop them logging in.
            var disabled = new User { Id = 4, Role = Role.FullEmployee, IsActive = false };
            Assert.Throws<PermissionDeniedException>(() => _svc.CreateItem(disabled, NewItem()));
            Assert.Throws<PermissionDeniedException>(() => _svc.CreateItem(null, NewItem()));
        }

        [Test]
        public void CreateItem_AppliesDefaultMinQuantity()
        {
            int id = _svc.CreateItem(_admin, NewItem(minQty: 0));
            Assert.That(_items.GetById(id).MinQuantity, Is.EqualTo(10)); // from low_stock_default
        }

        [Test]
        public void CreateItem_InvalidUnits_Throws()
        {
            var bad = NewItem();
            bad.UnitsPerStrip = 0;
            Assert.Throws<ValidationException>(() => _svc.CreateItem(_admin, bad));
        }

        [Test]
        public void DeleteAllItems_AsCashier_Denied_ButAllowedForFullEmployee()
        {
            _svc.CreateItem(_admin, NewItem());
            Assert.Throws<PermissionDeniedException>(() => _svc.DeleteAllItems(_cashier, out _));
            Assert.That(_svc.DeleteAllItems(_fullEmployee, out _), Is.EqualTo(1));
        }

        [Test]
        public void DeleteAllItems_RemovesActiveAndInactive()
        {
            _svc.CreateItem(_admin, NewItem("أ"));
            int inactive = _svc.CreateItem(_admin, NewItem("ب"));
            _svc.SetItemActive(_admin, inactive, false);

            int deleted = _svc.DeleteAllItems(_admin, out var withSales);

            Assert.That(deleted, Is.EqualTo(2));
            Assert.That(withSales, Is.Empty);
            Assert.That(_items.Items, Is.Empty);
        }

        [Test]
        public void DeleteAllItems_KeepsItemsWithSales_AndReturnsThem()
        {
            int sold = _svc.CreateItem(_admin, NewItem("مباع"));
            _svc.CreateItem(_admin, NewItem("عادي"));
            _items.ItemsWithSales.Add(sold);

            int deleted = _svc.DeleteAllItems(_admin, out var withSales);

            Assert.That(deleted, Is.EqualTo(1));
            Assert.That(withSales.Single().Id, Is.EqualTo(sold));
            Assert.That(_items.Items.Single().Id, Is.EqualTo(sold));
        }

        [Test]
        public void ReceiveStock_AddsBatch_AndSearchShowsAvailability()
        {
            int id = _svc.CreateItem(_admin, NewItem("بنادول"));
            Receive(id, 100, DateTime.Today.AddYears(1), boxBuy: 1m, boxSell: 2.0m);

            var view = _svc.Search("بنادول").Single();
            Assert.That(view.AvailableUnits, Is.EqualTo(100));
            Assert.That(view.NearestExpiry, Is.EqualTo(DateTime.Today.AddYears(1)));
        }

        [Test]
        public void GetLowStock_FlagsItemsAtOrBelowThreshold()
        {
            int low = _svc.CreateItem(_admin, NewItem("قليل", minQty: 50));
            int ok = _svc.CreateItem(_admin, NewItem("كافٍ", minQty: 5));
            Receive(low, 40, DateTime.Today.AddYears(1), boxBuy: 1m, boxSell: 2.0m); // 40 <= 50 -> low
            Receive(ok, 40, DateTime.Today.AddYears(1), boxBuy: 1m, boxSell: 2.0m);  // 40 > 5  -> ok

            var lowItems = _svc.GetLowStock().Select(v => v.Item.Id).ToList();
            Assert.That(lowItems, Does.Contain(low));
            Assert.That(lowItems, Does.Not.Contain(ok));
        }

        [Test]
        public void AdjustStock_RequiresReason()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            int batch = Receive(id, 10, null, boxBuy: 1m, boxSell: 2.0m);
            Assert.Throws<ValidationException>(() => _svc.AdjustStock(_admin, batch, -5, "  "));
        }

        [Test]
        public void AdjustStock_CannotGoNegative()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            int batch = Receive(id, 10, null, boxBuy: 1m, boxSell: 2.0m);
            Assert.Throws<ValidationException>(() => _svc.AdjustStock(_admin, batch, -20, "تلف"));
        }

        [Test]
        public void AdjustStock_Valid_UpdatesQuantityAndRecords()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            int batch = Receive(id, 10, null, boxBuy: 1m, boxSell: 2.0m);
            _svc.AdjustStock(_admin, batch, -3, "كسر");

            Assert.That(_stock.GetBatch(batch).QuantityUnits, Is.EqualTo(7));
            Assert.That(_stock.Adjustments.Single().DeltaUnits, Is.EqualTo(-3));
        }

        [Test]
        public void DisposeBatch_RemovesFromSellableStock_AndRecordsLoss()
        {
            int id = _svc.CreateItem(_admin, NewItem());
            int batch = Receive(id, 8, DateTime.Today.AddDays(-1), boxBuy: 2m, boxSell: 4.0m);
            Assert.That(_svc.DisposeBatch(_admin, batch), Is.True);

            Assert.That(_svc.GetView(id).AvailableUnits, Is.EqualTo(0));
            Assert.That(_stock.Adjustments.Single().DeltaUnits, Is.EqualTo(-8));
        }

        [Test]
        public void DisposeBatch_IsIdempotent_ForMissingOrAlreadyDisposedBatch()
        {
            // A batch that never existed (e.g. its item was deleted) is a no-op, not an error.
            Assert.That(_svc.DisposeBatch(_admin, batchId: 999), Is.False);

            int id = _svc.CreateItem(_admin, NewItem());
            int batch = Receive(id, 5, DateTime.Today.AddDays(-1), boxBuy: 1m, boxSell: 2.0m);
            Assert.That(_svc.DisposeBatch(_admin, batch), Is.True);
            Assert.That(_svc.DisposeBatch(_admin, batch), Is.False, "disposing again does nothing");
            Assert.That(_stock.Adjustments.Count, Is.EqualTo(1), "no duplicate loss recorded");
        }

        [Test]
        public void GetNearExpiry_SkipsBatchesWhoseItemWasDeleted()
        {
            int id = _svc.CreateItem(_admin, NewItem("بنادول"));
            Receive(id, 5, DateTime.Today.AddDays(-1), boxBuy: 1m, boxSell: 2.0m);   // expired batch
            Assert.That(_svc.GetNearExpiry(windowDays: 30).Count, Is.EqualTo(1));

            // Delete the item but leave an orphan batch behind: GetNearExpiry must skip it (not throw)
            // because there is no item to display for it — "don't handle data that isn't there".
            _items.Delete(id);
            Assert.DoesNotThrow(() => _svc.GetNearExpiry(windowDays: 30));
            Assert.That(_svc.GetNearExpiry(windowDays: 30), Is.Empty, "orphan batch has no item -> skipped");
        }

        [Test]
        public void GetNearExpiry_ReturnsSoonAndExpired_SortedByDate()
        {
            int id = _svc.CreateItem(_admin, NewItem("قريب"));
            Receive(id, 5, DateTime.Today.AddDays(10), boxBuy: 1m, boxSell: 2.0m);   // near
            Receive(id, 5, DateTime.Today.AddDays(-2), boxBuy: 1m, boxSell: 2.0m);   // expired
            Receive(id, 5, DateTime.Today.AddYears(1), boxBuy: 1m, boxSell: 2.0m);   // far -> excluded

            var rows = _svc.GetNearExpiry(windowDays: 30);
            Assert.That(rows.Count, Is.EqualTo(2));
            Assert.That(rows.First().IsExpired, Is.True, "Expired batch (most negative days) sorts first.");
        }
    }
}
