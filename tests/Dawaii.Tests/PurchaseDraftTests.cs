using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.App;
using Dawaii.App.Forms;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Deliveries set aside half-typed (V2.3.2).
    ///
    /// The promise a draft makes is a negative one, and it is the whole point: setting an invoice
    /// aside must change NOTHING. No stock on the shelf, no price at the till, no money owed to the
    /// company. So most of these tests assert on what did not happen — a pharmacist who drafts an
    /// invoice and walks to the POS has to find the till exactly as they left it, or the feature is
    /// worse than losing the typing.
    /// </summary>
    [TestFixture]
    public class PurchaseDraftTests
    {
        private string _path;
        private AppServices _services;
        private User _admin;
        private int _supplierId;
        private Supplier _supplier;
        private int _panadol;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_draft_" + Guid.NewGuid().ToString("N") + ".db");
            _services = new AppServices(new SqliteConnectionFactory(_path));
            _services.Initializer.ApplySchemaAndSeed();
            _services.Initializer.EnsureDefaultAdmin();
            Session.Services = _services;
            _admin = _services.Users.GetByUsername("admin");
            Session.CurrentUser = _admin;

            _supplierId = _services.Suppliers.CreateSupplier(_admin, "شركة النيل");
            _supplier = _services.Suppliers.Get(_supplierId);

            _panadol = _services.Items.Add(new Item
            {
                NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10,
                PurchasePrice = 50m, SellingPrice = 60m, IsActive = true
            });
            _services.Stock.AddBatch(new StockBatch
            {
                ItemId = _panadol, QuantityUnits = 500, StripsPerBox = 10, UnitsPerStrip = 10,
                BoxPurchasePrice = 5000m, BoxSellingPrice = 6000m, ExpiryDate = DateTime.Today.AddYears(1)
            });

            PurchaseDrafts.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            PurchaseDrafts.Clear();
            Session.CurrentUser = null;
            Session.Services = null;
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        private static PurchaseInvoiceLine Line(int itemId, int boxes, decimal buy, decimal sell)
            => new PurchaseInvoiceLine
            {
                ItemId = itemId, ItemName = "panadol", QuantityBoxes = boxes, StripsPerBox = 10,
                BoxPurchasePrice = buy, BoxSellingPrice = sell,
                ExpiryDate = DateTime.Today.AddYears(1), BatchNumber = "LOT-1"
            };

        private PurchaseDrafts.Draft Draft(params PurchaseInvoiceLine[] lines)
            => PurchaseDrafts.Save(0, _supplier, "المندوب علي", "INV-9", DateTime.Today, lines);

        // ---------------- a draft changes nothing ----------------

        [Test]
        public void ADraft_PutsNoStockOnTheShelf()
        {
            int before = _services.Stock.GetBatches(_panadol).Sum(b => b.QuantityUnits);

            Draft(Line(_panadol, 10, 5200m, 7000m));

            Assert.That(_services.Stock.GetBatches(_panadol).Sum(b => b.QuantityUnits), Is.EqualTo(before),
                "the boxes are on the counter, not on the shelf — the system has not accepted them yet");
            Assert.That(_services.Stock.GetBatches(_panadol).Count, Is.EqualTo(1), "no batch was created");
        }

        [Test]
        public void ADraft_DoesNotMoveThePriceAtTheTill()
        {
            // The delivery carries a NEW price: 7,000 a box against the 6,000 on the shelf.
            Draft(Line(_panadol, 10, 5200m, 7000m));

            Item item = _services.Items.GetById(_panadol);
            Assert.That(UnitConverter.PriceOf(item, UnitType.Box), Is.EqualTo(6000m),
                "the POS keeps quoting the old price until the delivery is actually filed");
            Assert.That(item.PurchasePrice, Is.EqualTo(50m), "and the cost is untouched too");
        }

        [Test]
        public void ADraft_OwesTheCompanyNothing()
        {
            Draft(Line(_panadol, 10, 5200m, 7000m));

            Assert.That(_services.Suppliers.Get(_supplierId).Outstanding, Is.Zero);
            Assert.That(_services.Suppliers.TotalOutstanding(), Is.Zero,
                "a half-typed delivery must never appear as money owed");
            Assert.That(_services.Suppliers.GetInvoices(_supplierId), Is.Empty, "and no invoice exists");
        }

        [Test]
        public void ADraft_LeavesThePosAbleToSellWhatWasAlreadyInStock()
        {
            Draft(Line(_panadol, 10, 5200m, 7000m));

            // The customer who interrupted buys from the stock that was already there.
            var cart = new List<CartLine> { new CartLine { ItemId = _panadol, UnitType = UnitType.Strip, Quantity = 2 } };
            Sale sale = _services.Pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t");

            Assert.That(sale.Total, Is.EqualTo(1200m), "2 strips at the OLD 600 price");
            Assert.That(PurchaseDrafts.Count, Is.EqualTo(1), "and the delivery is still waiting");
        }

        // ---------------- the draft itself ----------------

        [Test]
        public void ADraft_KeepsEveryLineAsItWasTyped()
        {
            PurchaseDrafts.Draft d = Draft(Line(_panadol, 7, 5200m, 7000m), Line(_panadol, 3, 4000m, 5000m));

            Assert.That(d.Lines.Count, Is.EqualTo(2));
            Assert.That(d.Lines[0].QuantityBoxes, Is.EqualTo(7));
            Assert.That(d.Lines[0].BoxPurchasePrice, Is.EqualTo(5200m));
            Assert.That(d.Supplier.Id, Is.EqualTo(_supplierId));
            Assert.That(d.InvoiceNumber, Is.EqualTo("INV-9"));
            Assert.That(d.Representative, Is.EqualTo("المندوب علي"));
            Assert.That(d.Total, Is.EqualTo(7 * 5200m + 3 * 4000m));
        }

        [Test]
        public void ADraft_HoldsItsOwnCopy_SoLaterEditsDoNotLeakIntoIt()
        {
            var lines = new List<PurchaseInvoiceLine> { Line(_panadol, 7, 5200m, 7000m) };
            PurchaseDrafts.Draft d = PurchaseDrafts.Save(0, _supplier, "م", "INV-9", DateTime.Today, lines);

            // The screen goes on editing its own list after setting the draft aside.
            lines[0].QuantityBoxes = 999;
            lines.Add(Line(_panadol, 1, 1m, 2m));

            Assert.That(d.Lines.Count, Is.EqualTo(1), "the draft is a snapshot, not a live view");
            Assert.That(d.Lines[0].QuantityBoxes, Is.EqualTo(7));
        }

        [Test]
        public void Drafts_TakeTheLowestFreeNumber()
        {
            PurchaseDrafts.Draft a = Draft(Line(_panadol, 1, 1m, 2m));
            PurchaseDrafts.Draft b = Draft(Line(_panadol, 1, 1m, 2m));
            PurchaseDrafts.Draft c = Draft(Line(_panadol, 1, 1m, 2m));
            Assert.That(new[] { a.Number, b.Number, c.Number }, Is.EqualTo(new[] { 1, 2, 3 }));

            PurchaseDrafts.Remove(b.Number);
            Assert.That(Draft(Line(_panadol, 1, 1m, 2m)).Number, Is.EqualTo(2), "the freed slot is reused");
        }

        [Test]
        public void SavingAnExistingDraftAgain_ReplacesIt_RatherThanAddingASecond()
        {
            PurchaseDrafts.Draft first = Draft(Line(_panadol, 1, 1m, 2m));

            PurchaseDrafts.Save(first.Number, _supplier, "م2", "INV-X", DateTime.Today,
                new[] { Line(_panadol, 5, 1m, 2m) });

            Assert.That(PurchaseDrafts.Count, Is.EqualTo(1), "interrupted twice is still one delivery");
            PurchaseDrafts.Draft only = PurchaseDrafts.All.Single();
            Assert.That(only.Number, Is.EqualTo(first.Number));
            Assert.That(only.Lines.Single().QuantityBoxes, Is.EqualTo(5));
            Assert.That(only.InvoiceNumber, Is.EqualTo("INV-X"));
        }

        [Test]
        public void Logout_ForgetsEveryDraft()
        {
            Draft(Line(_panadol, 1, 1m, 2m));
            Draft(Line(_panadol, 1, 1m, 2m));

            PurchaseDrafts.Clear();     // what MainForm.Logout calls

            Assert.That(PurchaseDrafts.Count, Is.Zero,
                "a pending delivery belongs to the person typing it, not the next cashier");
        }

        // ---------------- and the delivery still files correctly afterwards ----------------

        [Test]
        public void FilingADraftedDelivery_DoesEverythingItAlwaysDid()
        {
            PurchaseDrafts.Draft d = Draft(Line(_panadol, 10, 5200m, 7000m));

            // What resuming the draft and pressing save amounts to.
            PurchaseInvoice invoice = _services.Suppliers.RecordInvoice(
                _admin, d.Supplier.Id, d.Representative, d.InvoiceNumber, d.InvoiceDate, d.Lines);
            PurchaseDrafts.Remove(d.Number);

            Assert.That(PurchaseDrafts.Count, Is.Zero, "the draft is no longer waiting for anyone");
            Assert.That(invoice.Total, Is.EqualTo(52000m));
            Assert.That(_services.Suppliers.Get(_supplierId).Outstanding, Is.EqualTo(52000m), "now it is owed");

            // 10 boxes × 10 strips × 10 units on top of the 500 already there.
            Assert.That(_services.Stock.GetBatches(_panadol).Sum(b => b.QuantityUnits), Is.EqualTo(1500));
            Assert.That(UnitConverter.PriceOf(_services.Items.GetById(_panadol), UnitType.Box),
                Is.EqualTo(7000m), "and only NOW does the till quote the new price");
        }
    }
}
