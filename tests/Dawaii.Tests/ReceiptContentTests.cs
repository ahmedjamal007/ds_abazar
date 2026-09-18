using System;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using NUnit.Framework;

namespace Dawaii.Tests
{
    [TestFixture]
    public class ReceiptContentTests
    {
        private static Sale SampleSale()
        {
            var sale = new Sale
            {
                Id = 7, SaleNumber = 7, SaleType = SaleType.Cash,
                CreatedAt = new DateTime(2026, 7, 9, 10, 30, 0),
                Subtotal = 220m, Discount = 20m, Total = 200m
            };
            sale.Lines.Add(new SaleLine { ItemId = 1, ItemName = "بنادول", UnitType = UnitType.Box, Quantity = 1, UnitsEach = 100, UnitPrice = 2m, LineTotal = 200m });
            sale.Lines.Add(new SaleLine { ItemId = 2, ItemName = "فيتامين", UnitType = UnitType.Unit, Quantity = 20, UnitsEach = 1, UnitPrice = 1m, LineTotal = 20m });
            return sale;
        }

        [Test]
        public void BuildText_ContainsHeaderItemsAndTotals()
        {
            string text = ReceiptContent.BuildText(SampleSale(), new ReceiptInfo { PharmacyName = "صيدلية دوائي", Currency = "ج.س" });

            Assert.That(text, Does.Contain("صيدلية دوائي"));
            Assert.That(text, Does.Contain("بنادول"));
            Assert.That(text, Does.Contain("فيتامين"));
            Assert.That(text, Does.Contain("رقم: 7"));
            Assert.That(text, Does.Contain("200.00"));   // total
            Assert.That(text, Does.Contain("20.00"));    // discount
        }

        [Test]
        public void BuildLines_DiscountLineOnlyWhenPresent()
        {
            var sale = SampleSale();
            sale.Discount = 0m;
            var lines = ReceiptContent.BuildLines(sale, new ReceiptInfo());
            Assert.That(lines, Has.None.Contains("الخصم"));
        }

        [Test]
        public void NullReceiptPrinter_IsNotAvailable_AndDoesNothing()
        {
            IReceiptPrinter printer = new NullReceiptPrinter();
            Assert.That(printer.IsAvailable, Is.False);
            Assert.DoesNotThrow(() => printer.Print(SampleSale(), new ReceiptInfo()));
        }
    }
}
