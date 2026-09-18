using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class SaleBuilderTests
    {
        private Dictionary<int, Item> _items;
        private Dictionary<int, List<StockBatch>> _batches;

        [SetUp]
        public void SetUp()
        {
            // Panadol: 1 box = 100 units @ 2 each; Amoxil: strip of 10 @ 5; Vitamin: single @ 3
            _items = new Dictionary<int, Item>
            {
                [1] = new Item { Id = 1, NameEn = "بنادول", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 2m, IsActive = true },
                [2] = new Item { Id = 2, NameEn = "أموكسيل", UnitsPerStrip = 10, StripsPerBox = 5, SellingPrice = 5m, IsActive = true },
                [3] = new Item { Id = 3, NameEn = "فيتامين س", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 3m, IsActive = true },
                [4] = new Item { Id = 4, NameEn = "بدون سعر", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = null, IsActive = true },
            };
            _batches = new Dictionary<int, List<StockBatch>>
            {
                [1] = new List<StockBatch> { new StockBatch { Id = 11, ItemId = 1, QuantityUnits = 500, ExpiryDate = new DateTime(2027, 1, 1), BoxPurchasePrice = 1m, StripsPerBox = 1, UnitsPerStrip = 1 } },
                [2] = new List<StockBatch> { new StockBatch { Id = 21, ItemId = 2, QuantityUnits = 50, ExpiryDate = new DateTime(2026, 6, 1), BoxPurchasePrice = 3m, StripsPerBox = 1, UnitsPerStrip = 1 } },
                [3] = new List<StockBatch> { new StockBatch { Id = 31, ItemId = 3, QuantityUnits = 20, ExpiryDate = new DateTime(2028, 1, 1), BoxPurchasePrice = 1.5m, StripsPerBox = 1, UnitsPerStrip = 1 } },
            };
        }

        private Sale Build(IList<CartLine> cart, SaleHeader header = null)
            => SaleBuilder.Build(cart, id => _items[id], id => _batches[id],
                header ?? new SaleHeader { UserId = 1, SaleType = SaleType.Cash });

        [Test]
        public void Build_ThreeItemsIncludingAStrip_TotalsCorrect()
        {
            // Acceptance §6.1: sell 3 items, one by strip.
            var cart = new List<CartLine>
            {
                new CartLine { ItemId = 1, UnitType = UnitType.Box,   Quantity = 1 }, // 100 * 2 = 200
                new CartLine { ItemId = 2, UnitType = UnitType.Strip, Quantity = 1 }, // 10  * 5 = 50
                new CartLine { ItemId = 3, UnitType = UnitType.Unit,  Quantity = 4 }, // 4   * 3 = 12
            };

            Sale sale = Build(cart);

            Assert.That(sale.Lines.Count, Is.EqualTo(3));
            Assert.That(sale.Subtotal, Is.EqualTo(262m));
            Assert.That(sale.Total, Is.EqualTo(262m));
            // Cost: 100*1 + 10*3 + 4*1.5 = 100 + 30 + 6 = 136
            Assert.That(sale.CostTotal, Is.EqualTo(136m));
            Assert.That(sale.Profit, Is.EqualTo(126m));
        }

        [Test]
        public void Build_AllocatesFromBatches_AndSnapshotsCost()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = 2, UnitType = UnitType.Strip, Quantity = 2 } };
            Sale sale = Build(cart);

            SaleLine line = sale.Lines.Single();
            Assert.That(line.TotalUnits, Is.EqualTo(20));
            Assert.That(line.Allocations.Single().BatchId, Is.EqualTo(21));
            Assert.That(line.Allocations.Single().Units, Is.EqualTo(20));
            Assert.That(line.CostTotal, Is.EqualTo(60m)); // 20 * 3
        }

        [Test]
        public void Build_SameItemTwice_SharesStock_NoDoubleSpend()
        {
            _batches[2][0].QuantityUnits = 15; // only 15 units available
            var cart = new List<CartLine>
            {
                new CartLine { ItemId = 2, UnitType = UnitType.Unit, Quantity = 10 },
                new CartLine { ItemId = 2, UnitType = UnitType.Unit, Quantity = 6 }, // 10+6=16 > 15
            };
            Assert.Throws<InsufficientStockException>(() => Build(cart));
        }

        [Test]
        public void Build_Discount_ReducesTotalNotCost()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = 1, UnitType = UnitType.Box, Quantity = 1 } };
            Sale sale = Build(cart, new SaleHeader { UserId = 1, SaleType = SaleType.Cash, Discount = 50m });
            Assert.That(sale.Subtotal, Is.EqualTo(200m));
            Assert.That(sale.Total, Is.EqualTo(150m));
            Assert.That(sale.CostTotal, Is.EqualTo(100m));
        }

        [Test]
        public void Build_DiscountGreaterThanSubtotal_Throws()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = 3, UnitType = UnitType.Unit, Quantity = 1 } };
            Assert.Throws<ValidationException>(() => Build(cart, new SaleHeader { UserId = 1, Discount = 999m }));
        }

        [Test]
        public void Build_CreditWithoutCustomer_Throws()
        {
            var cart = new List<CartLine> { new CartLine { ItemId = 3, UnitType = UnitType.Unit, Quantity = 1 } };
            Assert.Throws<ValidationException>(() =>
                Build(cart, new SaleHeader { UserId = 1, SaleType = SaleType.Credit, CustomerId = null }));
        }

        [Test]
        public void Build_EmptyCart_Throws()
        {
            Assert.Throws<ValidationException>(() => Build(new List<CartLine>()));
        }

        [Test]
        public void Build_InactiveItem_Throws()
        {
            _items[1].IsActive = false;
            var cart = new List<CartLine> { new CartLine { ItemId = 1, UnitType = UnitType.Box, Quantity = 1 } };
            Assert.Throws<ValidationException>(() => Build(cart));
        }

        [Test]
        public void Build_UnpricedItem_Throws()
        {
            // NULL selling price = not priced yet; such items are hidden from the POS and unsellable.
            _batches[4] = new List<StockBatch> { new StockBatch { Id = 41, ItemId = 4, QuantityUnits = 10, BoxPurchasePrice = 1m, StripsPerBox = 1, UnitsPerStrip = 1 } };
            var cart = new List<CartLine> { new CartLine { ItemId = 4, UnitType = UnitType.Unit, Quantity = 1 } };
            Assert.Throws<ValidationException>(() => Build(cart));
        }
    }
}
