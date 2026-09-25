using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.App.Printing;
using Dawaii.Core;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Printing the cart without selling it (V2.4).
    ///
    /// "طباعة السلة" puts the customer's total on paper before they commit to it. The dangerous half
    /// of that is the paper: a slip that looks like a receipt for a sale that never happened is
    /// something a customer could present as proof of purchase, or a cashier could read as a sale
    /// already rung up. So these tests cover both halves — that nothing is sold, and that what comes
    /// out of the printer says so.
    ///
    /// The quote is recognised by having no sale number, which a saved sale always has. That is
    /// deliberately not a flag someone could forget to set: any unsaved sale reaching a printer is
    /// labelled correctly however it got there.
    /// </summary>
    [TestFixture]
    public class PrintCartQuoteTests
    {
        private string _path;
        private SqliteConnectionFactory _db;
        private SqliteItemRepository _items;
        private SqliteStockRepository _stock;
        private PosService _pos;
        private ISaleStore _sales;
        private User _cashier;
        private int _panadol;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_quote_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            var init = new DatabaseInitializer(_db);
            init.ApplySchemaAndSeed();
            init.EnsureDefaultAdmin();

            _items = new SqliteItemRepository(_db);
            _stock = new SqliteStockRepository(_db);
            _sales = new SqliteSaleStore(_db);
            var customers = new SqliteCustomerRepository(_db);
            var settings = new SqliteSettingsRepository(_db);
            var audit = new SqliteAuditRepository(_db);
            _pos = new PosService(_items, _stock, _sales, customers, settings, audit);
            _cashier = new SqliteUserRepository(_db).GetByUsername("admin");

            _panadol = _items.Add(new Item
            {
                NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10,
                PurchasePrice = 50m, SellingPrice = 60m, IsActive = true
            });
            _stock.AddBatch(new StockBatch
            {
                ItemId = _panadol, QuantityUnits = 500, StripsPerBox = 10, UnitsPerStrip = 10,
                BoxPurchasePrice = 5000m, BoxSellingPrice = 6000m, ExpiryDate = DateTime.Today.AddYears(1)
            });
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        /// <summary>What the button does: builds the sale and never hands it to the store.</summary>
        private Sale Quote(decimal discount = 0m, params CartLine[] cart)
            => SaleBuilder.Build(cart.ToList(), _items.GetById, id => _stock.GetSellableBatches(id),
                new SaleHeader
                {
                    UserId = _cashier.Id, SaleType = SaleType.Cash,
                    Discount = discount, Terminal = "t1"
                });

        private static CartLine Line(int itemId, UnitType unit, int qty)
            => new CartLine { ItemId = itemId, UnitType = unit, Quantity = qty };

        private int UnitsInStock() => _stock.GetBatches(_panadol).Sum(b => b.QuantityUnits);

        // ---------------- nothing is sold ----------------

        [Test]
        public void PrintingTheCart_SellsNothing()
        {
            int before = UnitsInStock();

            Quote(0m, Line(_panadol, UnitType.Strip, 2));

            Assert.That(UnitsInStock(), Is.EqualTo(before),
                "the customer has not paid — the tablets are still on the shelf");
            Assert.That(_pos.SalesOn(DateTime.Today), Is.Empty, "and no invoice was written");
        }

        [Test]
        public void PrintingTheCartTwice_StillSellsNothing()
        {
            int before = UnitsInStock();

            Quote(0m, Line(_panadol, UnitType.Box, 1));
            Quote(0m, Line(_panadol, UnitType.Box, 1));

            Assert.That(UnitsInStock(), Is.EqualTo(before));
            Assert.That(_pos.SalesOn(DateTime.Today), Is.Empty,
                "a customer who asks for the price twice has still bought nothing");
        }

        [Test]
        public void AQuote_DoesNotStopTheSameCartBeingSoldAfterwards()
        {
            var cart = new List<CartLine> { Line(_panadol, UnitType.Strip, 2) };

            Quote(0m, cart.ToArray());                  // customer thinks about it...
            Sale sale = _pos.Complete(_cashier, cart, SaleType.Cash, null, 0m, "t1");   // ...and buys

            Assert.That(sale.SaleNumber, Is.GreaterThan(0));
            Assert.That(UnitsInStock(), Is.EqualTo(480), "20 units gone, exactly once");
        }

        // ---------------- but it is priced like the real thing ----------------

        [Test]
        public void AQuote_ShowsWhatTheCustomerWouldActuallyPay()
        {
            Sale quote = Quote(0m, Line(_panadol, UnitType.Strip, 2), Line(_panadol, UnitType.Unit, 5));

            // 2 strips at 600 + 5 tablets at 60 = 1,500.
            Assert.That(quote.Total, Is.EqualTo(1500m));
            Assert.That(quote.Lines.Count, Is.EqualTo(2));
        }

        [Test]
        public void AQuote_IncludesTheDiscountTypedOnScreen()
        {
            Sale plain = Quote(0m, Line(_panadol, UnitType.Strip, 2));
            Sale discounted = Quote(200m, Line(_panadol, UnitType.Strip, 2));

            Assert.That(plain.Total, Is.EqualTo(1200m));
            Assert.That(discounted.Total, Is.EqualTo(1000m),
                "quoting a price the cashier would not honour is worse than not quoting at all");
        }

        [Test]
        public void ACartThatCannotBeSold_CannotBeQuotedEither()
        {
            // 6 boxes = 600 units against 500 in stock.
            Assert.Throws<InsufficientStockException>(
                () => Quote(0m, Line(_panadol, UnitType.Box, 6)),
                "promising a price for stock that is not there would be a promise to a real customer");
        }

        // ---------------- and the paper says what it is ----------------

        [Test]
        public void AQuote_HasNoSaleNumber_WhichIsHowThePrintersKnow()
        {
            Sale quote = Quote(0m, Line(_panadol, UnitType.Strip, 2));

            Assert.That(quote.SaleNumber, Is.Zero,
                "a saved sale takes its number from its row, so zero can only mean never saved");
        }

        [Test]
        public void ThePrintedQuote_IsNotCalledAReceipt_AndCarriesNoInvoiceNumber()
        {
            Sale quote = Quote(0m, Line(_panadol, UnitType.Strip, 2));

            string paper = Flatten(SaleReceiptBuilder.Build(quote, new ReceiptInfo { PharmacyName = "صيدلية" }));

            Assert.That(paper, Does.Contain("عرض سعر"));
            Assert.That(paper, Does.Not.Contain("إيصال بيع"),
                "a customer must not be able to present this as proof of purchase");
            Assert.That(paper, Does.Contain("لم يتم الدفع"), "it says so in words, not just by omission");
            Assert.That(paper, Does.Not.Contain("فاتورة 0"), "and invents no invoice number");
        }

        [Test]
        public void ARealReceipt_IsStillCalledAReceipt_AndCarriesItsNumber()
        {
            // The same renderer, the other way round — otherwise the test above would pass on a
            // printer that had simply stopped saying anything at all.
            Sale sold = _pos.Complete(_cashier, new List<CartLine> { Line(_panadol, UnitType.Strip, 2) },
                SaleType.Cash, null, 0m, "t1");

            string paper = Flatten(SaleReceiptBuilder.Build(sold, new ReceiptInfo { PharmacyName = "صيدلية" }));

            Assert.That(paper, Does.Contain("إيصال بيع"));
            Assert.That(paper, Does.Contain("فاتورة " + sold.SaleNumber));
            Assert.That(paper, Does.Not.Contain("عرض سعر"));
            Assert.That(paper, Does.Not.Contain("لم يتم الدفع"));
        }

        /// <summary>Every piece of text on the slip, in one string, however the document nests it.</summary>
        private static string Flatten(ReceiptDocument doc)
        {
            var sb = new System.Text.StringBuilder();
            foreach (Element el in Walk(doc.Elements)) Describe(el, sb);
            return sb.ToString();
        }

        private static IEnumerable<Element> Walk(IEnumerable<Element> elements)
        {
            foreach (Element el in elements)
            {
                yield return el;
                var box = el as BoxElement;
                if (box != null)
                    foreach (Element child in Walk(box.Children)) yield return child;
            }
        }

        private static void Describe(Element el, System.Text.StringBuilder sb)
        {
            var text = el as TextElement;
            if (text != null) { sb.AppendLine(text.Content); return; }

            var row = el as RowElement;
            if (row != null && row.Cells != null) { sb.AppendLine(string.Join(" ", row.Cells)); return; }

            var table = el as TableElement;
            if (table == null) return;
            if (table.Header != null) sb.AppendLine(string.Join(" ", table.Header));
            foreach (string[] r in table.Rows) sb.AppendLine(string.Join(" ", r));
        }
    }
}
