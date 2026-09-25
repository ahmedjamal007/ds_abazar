using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Dawaii.App;
using Dawaii.App.Modules;
using Dawaii.App.Printing;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Printing;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Reprinting an invoice already on file (V2.4).
    ///
    /// Two things have to hold. The paper must be the same paper — a reprint the customer can check
    /// against a neighbour's original, item for item and figure for figure. And the database must not
    /// notice: reprinting is reading, and a screen that could move stock or a balance while "just
    /// printing" would be the worst kind of bug to find months later in a stocktake.
    ///
    /// The printing call itself is not exercised — it ends at a real printer — but everything up to
    /// it is, including the document that would be sent.
    /// </summary>
    [TestFixture]
    [Apartment(System.Threading.ApartmentState.STA)]
    public class ReprintModuleTests
    {
        private string _path;
        private AppServices _services;
        private ReprintModule _mod;
        private Sale _sale;
        private int _panadol, _brufen, _customerId;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_reprint_" + Guid.NewGuid().ToString("N") + ".db");
            _services = new AppServices(new SqliteConnectionFactory(_path));
            _services.Initializer.ApplySchemaAndSeed();
            _services.Initializer.EnsureDefaultAdmin();
            Session.Services = _services;
            Session.CurrentUser = _services.Users.GetByUsername("admin");

            _panadol = Drug("zz-panadol", 60m);
            _brufen = Drug("zz-brufen", 80m);
            _customerId = _services.Customers.Add(new Customer { Name = "زبون" });

            // One real sale to reprint: two drugs, a discount, on credit so a balance exists to watch.
            _sale = _services.Pos.Complete(
                Session.CurrentUser,
                new List<CartLine>
                {
                    new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 2 },
                    new CartLine { ItemId = _brufen,  UnitType = UnitType.Unit,  Quantity = 5 },
                },
                SaleType.Credit, _customerId, 100m, "t1");

            _mod = new ReprintModule();
            _mod.CreateControl();
        }

        [TearDown]
        public void TearDown()
        {
            _mod?.Dispose();
            Session.CurrentUser = null;
            Session.Services = null;
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private int Drug(string name, decimal perUnit)
        {
            int id = _services.Items.Add(new Item
            {
                NameEn = name, UnitsPerStrip = 10, StripsPerBox = 10,
                PurchasePrice = perUnit / 2, SellingPrice = perUnit, IsActive = true
            });
            _services.Stock.AddBatch(new StockBatch
            {
                ItemId = id, QuantityUnits = 1000, StripsPerBox = 10, UnitsPerStrip = 10,
                BoxPurchasePrice = perUnit * 50, BoxSellingPrice = perUnit * 100,
                ExpiryDate = DateTime.Today.AddYears(1)
            });
            return id;
        }

        // ---------------- reaching into the module ----------------

        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private T Field<T>(string name) => (T)typeof(ReprintModule).GetField(name, Any).GetValue(_mod);
        private TextBox Number => Field<TextBox>("_number");
        private DataGridView Grid => Field<DataGridView>("_grid");
        private Sale Shown => Field<Sale>("_sale");
        private bool CanPrint => Field<Control>("_print").Enabled;
        private string Summary => Field<Label>("_summary").Text;

        /// <summary>Searches the way Enter in the number box does, minus the message box.</summary>
        private string Search(string query)
        {
            Number.Text = query;
            object outcome = typeof(ReprintModule).GetMethod("Lookup", Any).Invoke(_mod, new object[0]);
            return outcome.ToString();
        }

        // ---------------- finding the invoice ----------------

        [Test]
        public void TypingTheNumberOffTheReceipt_BringsUpThatInvoice()
        {
            Assert.That(Search(_sale.SaleNumber.ToString()), Is.EqualTo("Shown"));

            Assert.That(Shown.SaleNumber, Is.EqualTo(_sale.SaleNumber));
            Assert.That(Summary, Does.Contain("فاتورة " + _sale.SaleNumber));
            Assert.That(CanPrint, Is.True, "there is now something to reprint");
        }

        [Test]
        public void TheItemsShown_AreTheOnesOnTheOriginalInvoice()
        {
            Search(_sale.SaleNumber.ToString());

            Assert.That(Grid.Rows.Count, Is.EqualTo(2));

            var names = Grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells[0].Value).ToList();
            Assert.That(names, Is.EquivalentTo(new[] { "zz-panadol", "zz-brufen" }));

            DataGridViewRow panadol = Grid.Rows.Cast<DataGridViewRow>()
                .First(r => (string)r.Cells[0].Value == "zz-panadol");
            Assert.That(panadol.Cells[1].Value, Is.EqualTo("شريط"));
            Assert.That(panadol.Cells[2].Value, Is.EqualTo("2"));
            Assert.That((string)panadol.Cells[3].Value, Does.Contain("600"),
                "the strip price the customer was charged, not the per-tablet price it is stored as");
        }

        [Test]
        public void AnInvoiceNumberThatDoesNotExist_SaysSoAndShowsNothing()
        {
            Assert.That(Search("999999"), Is.EqualTo("NoSuchInvoice"));

            Assert.That(Shown, Is.Null);
            Assert.That(Grid.Rows.Count, Is.Zero, "no stale invoice left on screen to be reprinted");
            Assert.That(CanPrint, Is.False, "and nothing to press print on");
        }

        [Test]
        public void AnEmptyBox_IsNotASearch()
        {
            Assert.That(Search("   "), Is.EqualTo("NothingTyped"));
            Assert.That(CanPrint, Is.False);
        }

        [Test]
        public void SearchingAgainForNothing_ClearsTheInvoiceThatWasOnScreen()
        {
            Search(_sale.SaleNumber.ToString());
            Assert.That(CanPrint, Is.True);

            Search("999999");

            Assert.That(Shown, Is.Null, "printing the previous customer's invoice by accident would be worse than a slow search");
            Assert.That(CanPrint, Is.False);
        }

        // ---------------- the paper is the same paper ----------------

        [Test]
        public void TheReprintedReceipt_IsIdenticalToTheOriginal()
        {
            var info = new ReceiptInfo { PharmacyName = "صيدلية", CashierName = "admin" };

            string original = Flatten(SaleReceiptBuilder.Build(_sale, info));

            Search(_sale.SaleNumber.ToString());
            string reprint = Flatten(SaleReceiptBuilder.Build(Shown, info));

            Assert.That(reprint, Is.EqualTo(original),
                "a customer must be able to hold the reprint against the original and see the same thing");
        }

        [Test]
        public void TheReprint_IsAReceipt_NotAQuote()
        {
            Search(_sale.SaleNumber.ToString());
            string paper = Flatten(SaleReceiptBuilder.Build(Shown, new ReceiptInfo()));

            Assert.That(paper, Does.Contain("إيصال بيع"));
            Assert.That(paper, Does.Contain("فاتورة " + _sale.SaleNumber));
            Assert.That(paper, Does.Not.Contain("عرض سعر"),
                "this one really was sold — it must not carry the unsold-cart wording");
        }

        // ---------------- and nothing moves ----------------

        [Test]
        public void LookingUpAndRenderingAnInvoice_ChangesNothingAtAll()
        {
            string before = Snapshot();

            // Everything the screen does short of the printer, three times over.
            for (int i = 0; i < 3; i++)
            {
                Search(_sale.SaleNumber.ToString());
                SaleReceiptBuilder.Build(Shown, new ReceiptInfo());
            }

            Assert.That(Snapshot(), Is.EqualTo(before),
                "reprinting is reading: no stock, no totals, no payments, no balance may move");
        }

        [Test]
        public void AnInvoiceCanBeReprintedAgainAndAgain()
        {
            for (int i = 0; i < 5; i++)
                Assert.That(Search(_sale.SaleNumber.ToString()), Is.EqualTo("Shown"),
                    "the program keeps no count — refusing the fifth copy would punish the customer " +
                    "for the pharmacy's printer");

            Assert.That(_services.Pos.SalesOn(DateTime.Today).Count, Is.EqualTo(1),
                "and five reprints are still one sale");
        }

        /// <summary>Everything a reprint must not touch, in one string.</summary>
        private string Snapshot()
        {
            var sales = _services.Pos.SalesOn(DateTime.Today);
            return string.Join("|",
                "sales=" + sales.Count,
                "totals=" + sales.Sum(s => s.Total),
                "returned=" + sales.Sum(s => s.ReturnedTotal),
                "panadol=" + _services.Stock.GetBatches(_panadol).Sum(b => b.QuantityUnits),
                "brufen=" + _services.Stock.GetBatches(_brufen).Sum(b => b.QuantityUnits),
                "balance=" + _services.Customers.GetById(_customerId).Balance,
                "price=" + _services.Items.GetById(_panadol).SellingPrice);
        }

        // ---------------- reading the rendered slip ----------------

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
