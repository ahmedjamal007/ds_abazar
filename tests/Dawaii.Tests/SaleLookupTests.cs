using System;
using System.Collections.Generic;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using Dawaii.Tests.Fakes;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Finding the invoice to return. The number is a plain integer so it can be typed back exactly as
    /// printed; receipts printed before V1.8 carry the old composite "yyyyMMdd-id", which staff read off
    /// a right-to-left screen in reverse, so those still have to resolve too.
    /// </summary>
    [TestFixture]
    public class SaleLookupTests
    {
        private FakeItemRepository _items;
        private FakeStockRepository _stock;
        private FakeCustomerRepository _customers;
        private FakeSaleStore _store;
        private PosService _pos;
        private User _cashier;
        private int _itemId;

        [SetUp]
        public void SetUp()
        {
            _items = new FakeItemRepository();
            _stock = new FakeStockRepository();
            _customers = new FakeCustomerRepository();
            _store = new FakeSaleStore(_stock, _customers);
            _pos = new PosService(_items, _stock, _store, _customers,
                new FakeSettingsRepository().Seed("pos_expiry_warn_days", "30"), new FakeAuditRepository());

            _cashier = new User { Id = 2, Role = Role.Cashier, IsActive = true };
            _itemId = _items.Add(new Item { NameEn = "بنادول", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 2m, IsActive = true });
            _stock.AddBatch(new StockBatch { ItemId = _itemId, QuantityUnits = 1000, ExpiryDate = DateTime.Today.AddYears(1), BoxPurchasePrice = 1m, StripsPerBox = 1, UnitsPerStrip = 1 });
        }

        private Sale SellOne()
            => _pos.Complete(_cashier, new List<CartLine> { new CartLine { ItemId = _itemId, UnitType = UnitType.Strip, Quantity = 1 } },
                SaleType.Cash, null, 0m, "t1");

        [Test]
        public void SaleNumber_IsTheInvoiceId_AsAnInteger()
        {
            Sale sale = SellOne();
            Assert.That(sale.SaleNumber, Is.EqualTo(sale.Id));
        }

        [Test]
        public void FindSale_ByPrintedNumber()
        {
            Sale sale = SellOne();
            Assert.That(_pos.FindSale(sale.SaleNumber.ToString())?.Id, Is.EqualTo(sale.Id));
            Assert.That(_pos.FindSale("  " + sale.SaleNumber + "  ")?.Id, Is.EqualTo(sale.Id));
        }

        [Test]
        public void FindSale_ByArabicIndicDigits()
        {
            Sale sale = SellOne();
            string arabic = "";
            foreach (char c in sale.SaleNumber.ToString())
                arabic += (char)('٠' + (c - '0'));
            Assert.That(_pos.FindSale(arabic)?.Id, Is.EqualTo(sale.Id));
        }

        [Test]
        public void FindSale_ByLegacyCompositeNumber_InEitherReading()
        {
            Sale sale = SellOne();
            string date = sale.CreatedAt.ToString("yyyyMMdd");

            // As it was stored, as it was typed without the dash, and as it reads on an RTL screen.
            Assert.That(_pos.FindSale($"{date}-{sale.Id}")?.Id, Is.EqualTo(sale.Id));
            Assert.That(_pos.FindSale($"{date}{sale.Id}")?.Id, Is.EqualTo(sale.Id));
            Assert.That(_pos.FindSale($"{sale.Id}{date}")?.Id, Is.EqualTo(sale.Id));
        }

        [Test]
        public void FindSale_UnknownOrEmpty_ReturnsNull()
        {
            SellOne();
            Assert.That(_pos.FindSale("9999"), Is.Null);
            Assert.That(_pos.FindSale("  "), Is.Null);
            Assert.That(_pos.FindSale(null), Is.Null);
            Assert.That(_pos.FindSale("99999999999999999999"), Is.Null);   // too long to be an id
            Assert.That(_pos.FindSale("أ ب ج"), Is.Null);
        }

        [Test]
        public void Candidates_PrefersThePlainNumber_ThenTheLegacyReadings()
        {
            Assert.That(SaleNumberParser.Candidates("7"), Is.EqualTo(new[] { 7 }));
            Assert.That(SaleNumberParser.Candidates("720260801"), Contains.Item(7));
            Assert.That(SaleNumberParser.Candidates("202608017"), Contains.Item(7));
            Assert.That(SaleNumberParser.Candidates("20260801-123"), Contains.Item(123));

            // A run of eight digits that is not a date is just a number, not a date plus an id.
            Assert.That(SaleNumberParser.Candidates("999999999"), Is.EqualTo(new[] { 999999999 }));
            Assert.That(SaleNumberParser.Candidates("لا أرقام"), Is.Empty);
        }
    }
}
