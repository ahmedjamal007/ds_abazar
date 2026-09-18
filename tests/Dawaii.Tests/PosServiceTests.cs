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
    [TestFixture]
    public class PosServiceTests
    {
        private FakeItemRepository _items;
        private FakeStockRepository _stock;
        private FakeCustomerRepository _customers;
        private FakeSaleStore _store;
        private FakeSettingsRepository _settings;
        private FakeAuditRepository _audit;
        private PosService _pos;
        private User _admin, _cashier;
        private int _panadolId, _batchId;

        [SetUp]
        public void SetUp()
        {
            _items = new FakeItemRepository();
            _stock = new FakeStockRepository();
            _customers = new FakeCustomerRepository();
            _store = new FakeSaleStore(_stock, _customers);
            _settings = new FakeSettingsRepository().Seed("pos_expiry_warn_days", "30");
            _audit = new FakeAuditRepository();
            _pos = new PosService(_items, _stock, _store, _customers, _settings, _audit);

            _admin = new User { Id = 1, Role = Role.Admin, IsActive = true };
            _cashier = new User { Id = 2, Role = Role.Cashier, IsActive = true };

            _panadolId = _items.Add(new Item { NameEn = "بنادول", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 2m, IsActive = true });
            _batchId = _stock.AddBatch(new StockBatch { ItemId = _panadolId, QuantityUnits = 100, ExpiryDate = DateTime.Today.AddYears(1), BoxPurchasePrice = 1m, StripsPerBox = 1, UnitsPerStrip = 1 });
        }

        private List<CartLine> Cart(int qty, UnitType type)
            => new List<CartLine> { new CartLine { ItemId = _panadolId, UnitType = type, Quantity = qty } };

        [Test]
        public void Complete_CashSale_ReducesStock()
        {
            Sale sale = _pos.Complete(_cashier, Cart(1, UnitType.Strip), SaleType.Cash, null, 0m, "t1");

            Assert.That(sale.Id, Is.GreaterThan(0));
            Assert.That(sale.Total, Is.EqualTo(20m));
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(90));
        }

        [Test]
        public void Complete_CreditSale_IncreasesCustomerBalance()
        {
            int cust = _customers.Add(new Customer { Name = "أحمد" });
            Sale sale = _pos.Complete(_cashier, Cart(1, UnitType.Box), SaleType.Credit, cust, 0m, "t1");

            Assert.That(sale.Total, Is.EqualTo(200m));
            Assert.That(_customers.GetById(cust).Balance, Is.EqualTo(200m));
        }

        [Test]
        public void Complete_SellLastUnits_ThenSellAgain_Insufficient()
        {
            _pos.Complete(_cashier, Cart(10, UnitType.Strip), SaleType.Cash, null, 0m, "t1"); // 100 units -> 0 left
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(0));
            Assert.Throws<InsufficientStockException>(() => _pos.Complete(_cashier, Cart(1, UnitType.Unit), SaleType.Cash, null, 0m, "t2"));
        }

        [Test]
        public void ReturnSale_RestoresStock()
        {
            Sale sale = _pos.Complete(_cashier, Cart(1, UnitType.Box), SaleType.Cash, null, 0m, "t1");
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(0));

            _pos.ReturnSale(_admin, sale.Id, "عيب في المنتج");

            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(100));
            Assert.That(_pos.GetSale(sale.Id).Status, Is.EqualTo(SaleStatus.Returned));
        }

        [Test]
        public void ReturnSale_Credit_ReversesDebt()
        {
            int cust = _customers.Add(new Customer { Name = "أحمد" });
            Sale sale = _pos.Complete(_cashier, Cart(1, UnitType.Box), SaleType.Credit, cust, 0m, "t1");
            Assert.That(_customers.GetById(cust).Balance, Is.EqualTo(200m));

            _pos.ReturnSale(_admin, sale.Id, "إرجاع");

            Assert.That(_customers.GetById(cust).Balance, Is.EqualTo(0m));
        }

        [Test]
        public void ReturnSale_ByCashier_Allowed_D15()
        {
            // Owner decision D-15: employees process returns at the counter.
            Sale sale = _pos.Complete(_cashier, Cart(1, UnitType.Strip), SaleType.Cash, null, 0m, "t1");
            _pos.ReturnSale(_cashier, sale.Id, "إرجاع من الموظف");
            Assert.That(_pos.GetSale(sale.Id).Status, Is.EqualTo(SaleStatus.Returned));
            Assert.That(_stock.GetBatch(_batchId).QuantityUnits, Is.EqualTo(100));
        }

        [Test]
        public void ReturnSale_NotLoggedIn_Denied()
        {
            Sale sale = _pos.Complete(_cashier, Cart(1, UnitType.Unit), SaleType.Cash, null, 0m, "t1");
            Assert.Throws<PermissionDeniedException>(() => _pos.ReturnSale(null, sale.Id, "x"));
        }

        [Test]
        public void NearExpiryWarning_WithinWindow_ReturnsDate()
        {
            int soonItem = _items.Add(new Item { NameEn = "قريب", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1m, IsActive = true });
            _stock.AddBatch(new StockBatch { ItemId = soonItem, QuantityUnits = 5, ExpiryDate = DateTime.Today.AddDays(10), BoxPurchasePrice = 1m, StripsPerBox = 1, UnitsPerStrip = 1 });

            Assert.That(_pos.NearExpiryWarning(soonItem), Is.Not.Null);
            Assert.That(_pos.NearExpiryWarning(_panadolId), Is.Null); // expires in a year
        }

        [Test]
        public void SalesOn_ReturnsTodaySales()
        {
            _pos.Complete(_cashier, Cart(1, UnitType.Unit), SaleType.Cash, null, 0m, "t1");
            Assert.That(_pos.SalesOn(DateTime.Today).Count, Is.EqualTo(1));
        }
    }
}
