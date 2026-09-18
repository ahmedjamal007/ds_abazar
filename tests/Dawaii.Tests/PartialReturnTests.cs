using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Returning part of an invoice (FR-POS-08): the customer bought 50 boxes and brings 5 back. Those
    /// 5 go back into stock and are refunded; the other 45 stay sold.
    /// </summary>
    [TestFixture]
    public class PartialReturnTests
    {
        private FakeItemRepository _items;
        private FakeStockRepository _stock;
        private FakeCustomerRepository _customers;
        private FakeSaleStore _store;
        private PosService _pos;
        private User _admin;
        private int _panadolId, _brufenId, _batchId;

        [SetUp]
        public void SetUp()
        {
            _items = new FakeItemRepository();
            _stock = new FakeStockRepository();
            _customers = new FakeCustomerRepository();
            _store = new FakeSaleStore(_stock, _customers);
            _pos = new PosService(_items, _stock, _store, _customers,
                new FakeSettingsRepository().Seed("pos_expiry_warn_days", "30"), new FakeAuditRepository());

            _admin = new User { Id = 1, Role = Role.Admin, IsActive = true };

            // 1 box = 10 strips × 10 units = 100 units, priced at 2 per single unit (200 per box).
            _panadolId = _items.Add(new Item { NameEn = "بنادول", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 2m, IsActive = true });
            _brufenId = _items.Add(new Item { NameEn = "بروفين", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 3m, IsActive = true });
            _batchId = _stock.AddBatch(new StockBatch { ItemId = _panadolId, QuantityUnits = 10000, ExpiryDate = DateTime.Today.AddYears(1), BoxPurchasePrice = 100m, StripsPerBox = 1, UnitsPerStrip = 1 });
            _stock.AddBatch(new StockBatch { ItemId = _brufenId, QuantityUnits = 10000, ExpiryDate = DateTime.Today.AddYears(1), BoxPurchasePrice = 100m, StripsPerBox = 1, UnitsPerStrip = 1 });
        }

        private Sale Sell(params CartLine[] cart)
            => _pos.Complete(_admin, cart.ToList(), SaleType.Cash, null, 0m, "t1");

        private static CartLine Line(int itemId, int qty, UnitType unit = UnitType.Box)
            => new CartLine { ItemId = itemId, UnitType = unit, Quantity = qty };

        [Test]
        public void Return5Of50Boxes_RestoresOnly5_AndRefundsTheirValue()
        {
            Sale sale = Sell(Line(_panadolId, 50));            // 50 boxes = 5000 units, 10,000 total
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(5000));

            int lineId = sale.Lines.Single().Id;
            Return done = _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 5) }, "تالف");

            Assert.That(done.Total, Is.EqualTo(1000m), "5 boxes at 200 each");
            Assert.That(done.CompletedTheSale, Is.False);
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(5500), "500 units back in stock");

            Sale after = _pos.GetSale(sale.Id);
            Assert.That(after.Status, Is.EqualTo(SaleStatus.PartiallyReturned));
            Assert.That(after.ReturnedTotal, Is.EqualTo(1000m));
            Assert.That(after.NetTotal, Is.EqualTo(9000m), "the 45 boxes the customer kept");
            Assert.That(after.Lines.Single().RemainingQuantity, Is.EqualTo(45));
        }

        [Test]
        public void ReturningTheRestLater_ClosesTheInvoice()
        {
            Sale sale = Sell(Line(_panadolId, 50));
            int lineId = sale.Lines.Single().Id;

            _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 5) }, "دفعة أولى");
            _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 20) }, "دفعة ثانية");
            Assert.That(_pos.GetSale(sale.Id).Lines.Single().RemainingQuantity, Is.EqualTo(25));

            Return last = _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 25) }, "الباقي");

            Assert.That(last.CompletedTheSale, Is.True);
            Sale after = _pos.GetSale(sale.Id);
            Assert.That(after.Status, Is.EqualTo(SaleStatus.Returned));
            Assert.That(after.ReturnedTotal, Is.EqualTo(10000m), "the refunds add up to the whole invoice");
            Assert.That(after.NetTotal, Is.EqualTo(0m));
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(10000), "all stock back");
        }

        [Test]
        public void ReturnOneItemOfSeveral_LeavesTheOthersSold()
        {
            Sale sale = Sell(Line(_panadolId, 2), Line(_brufenId, 1));   // 400 + 300
            SaleLine panadol = sale.Lines.First(l => l.ItemId == _panadolId);

            _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(panadol.Id, 2) }, "غير مناسب");

            Sale after = _pos.GetSale(sale.Id);
            Assert.That(after.Status, Is.EqualTo(SaleStatus.PartiallyReturned));
            Assert.That(after.NetTotal, Is.EqualTo(300m), "only the بروفين is still sold");
            Assert.That(after.Lines.First(l => l.ItemId == _panadolId).RemainingQuantity, Is.EqualTo(0));
            Assert.That(after.Lines.First(l => l.ItemId == _brufenId).RemainingQuantity, Is.EqualTo(1));
        }

        [Test]
        public void CannotReturnMoreThanRemains()
        {
            Sale sale = Sell(Line(_panadolId, 10));
            int lineId = sale.Lines.Single().Id;

            Assert.Throws<ValidationException>(
                () => _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 11) }, "أكثر من المباع"));

            _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 6) }, "جزئي");
            Assert.Throws<ValidationException>(
                () => _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 5) }, "أكثر من المتبقي"));

            // The rejected attempts changed nothing.
            Assert.That(_pos.GetSale(sale.Id).Lines.Single().ReturnedQuantity, Is.EqualTo(6));
        }

        [Test]
        public void ReturnedInvoice_CannotBeReturnedAgain()
        {
            Sale sale = Sell(Line(_panadolId, 3));
            _pos.ReturnSale(_admin, sale.Id, "الكل");

            Assert.Throws<ValidationException>(() => _pos.ReturnSale(_admin, sale.Id, "مرة أخرى"));
            Assert.Throws<ValidationException>(
                () => _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(sale.Lines.Single().Id, 1) }, "مرة أخرى"));
        }

        [Test]
        public void WholeInvoiceReturn_AfterAPartialOne_TakesOnlyWhatIsLeft()
        {
            Sale sale = Sell(Line(_panadolId, 10));
            int lineId = sale.Lines.Single().Id;
            _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 4) }, "جزئي");

            Return rest = _pos.ReturnSale(_admin, sale.Id, "الباقي");

            Assert.That(rest.Lines.Single().Quantity, Is.EqualTo(6));
            Assert.That(rest.Total, Is.EqualTo(1200m), "6 boxes at 200");
            Assert.That(_pos.GetSale(sale.Id).Status, Is.EqualTo(SaleStatus.Returned));
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(10000));
        }

        [Test]
        public void DiscountedInvoice_RefundsTheDiscountedShare_AndClosesAtExactlyTheTotal()
        {
            // 3 boxes at 200 = 600, discounted to 550.
            Sale sale = _pos.Complete(_admin, new List<CartLine> { Line(_panadolId, 3) }, SaleType.Cash, null, 50m, "t1");
            Assert.That(sale.Total, Is.EqualTo(550m));
            int lineId = sale.Lines.Single().Id;

            Return first = _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 1) }, "واحد");
            Assert.That(first.Total, Is.EqualTo(decimal.Round(200m * 550m / 600m, 2)), "its share of the discounted price");

            Return rest = _pos.ReturnSale(_admin, sale.Id, "الباقي");
            Sale after = _pos.GetSale(sale.Id);
            Assert.That(first.Total + rest.Total, Is.EqualTo(550m), "the parts sum to the invoice exactly");
            Assert.That(after.ReturnedTotal, Is.EqualTo(550m));
            Assert.That(after.NetTotal, Is.EqualTo(0m));
        }

        [Test]
        public void CreditSale_PartialReturn_ReducesTheDebtByTheRefundOnly()
        {
            int customerId = _customers.Add(new Customer { Name = "أحمد" });
            Sale sale = _pos.Complete(_admin, new List<CartLine> { Line(_panadolId, 10) }, SaleType.Credit, customerId, 0m, "t1");
            Assert.That(_customers.GetById(customerId).Balance, Is.EqualTo(2000m));

            _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(sale.Lines.Single().Id, 3) }, "جزئي");

            Assert.That(_customers.GetById(customerId).Balance, Is.EqualTo(1400m), "owes for the 7 boxes kept");
        }

        [Test]
        public void ZeroOrEmptyQuantities_AreRejected()
        {
            Sale sale = Sell(Line(_panadolId, 5));
            int lineId = sale.Lines.Single().Id;

            Assert.Throws<ValidationException>(
                () => _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(lineId, 0) }, "لا شيء"));
            Assert.Throws<ValidationException>(
                () => _pos.ReturnItems(_admin, sale.Id, new ReturnRequest[0], "لا شيء"));
            Assert.Throws<ValidationException>(
                () => _pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(99999, 1) }, "سطر غير موجود"));
        }

        [Test]
        public void ReturnSpansTheBatchesTheUnitsCameFrom()
        {
            // Two batches: the sale eats the first one and part of the second (FEFO).
            var stock = new FakeStockRepository();
            var store = new FakeSaleStore(stock, _customers);
            var pos = new PosService(_items, stock, store, _customers,
                new FakeSettingsRepository().Seed("pos_expiry_warn_days", "30"), new FakeAuditRepository());

            int older = stock.AddBatch(new StockBatch { ItemId = _panadolId, QuantityUnits = 250, ExpiryDate = DateTime.Today.AddMonths(1), BoxPurchasePrice = 100m, StripsPerBox = 1, UnitsPerStrip = 1 });
            int newer = stock.AddBatch(new StockBatch { ItemId = _panadolId, QuantityUnits = 500, ExpiryDate = DateTime.Today.AddMonths(9), BoxPurchasePrice = 100m, StripsPerBox = 1, UnitsPerStrip = 1 });

            Sale sale = pos.Complete(_admin, new List<CartLine> { Line(_panadolId, 4) }, SaleType.Cash, null, 0m, "t1"); // 400 units
            Assert.That(stock.GetBatch(older).QuantityUnits, Is.EqualTo(0));
            Assert.That(stock.GetBatch(newer).QuantityUnits, Is.EqualTo(350));

            // Returning 2 boxes (200 units) gives back the first 200 units sold — all from the older batch.
            pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(sale.Lines.Single().Id, 2) }, "جزئي");
            Assert.That(stock.GetBatch(older).QuantityUnits, Is.EqualTo(200));
            Assert.That(stock.GetBatch(newer).QuantityUnits, Is.EqualTo(350));

            // The next 2 boxes take the older batch's last 50 units and 150 from the newer one.
            pos.ReturnItems(_admin, sale.Id, new[] { new ReturnRequest(sale.Lines.Single().Id, 2) }, "الباقي");
            Assert.That(stock.GetBatch(older).QuantityUnits, Is.EqualTo(250));
            Assert.That(stock.GetBatch(newer).QuantityUnits, Is.EqualTo(500));
        }
    }
}
