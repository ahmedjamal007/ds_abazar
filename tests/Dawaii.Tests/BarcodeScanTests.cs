using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Simulates a USB barcode scanner with no hardware present: a scanner simply "types" the code's
    /// digits and presses Enter, so feeding RANDOM NUMBERS through the exact code paths the search
    /// boxes and POS call on Enter is a faithful test. Runs against a real SQLite database.
    /// </summary>
    [TestFixture]
    public class BarcodeScanTests
    {
        private string _path;
        private SqliteConnectionFactory _db;
        private SqliteItemRepository _items;
        private SqliteStockRepository _stock;
        private SqliteItemCodeRepository _codes;
        private SqliteUserRepository _users;
        private SqliteSettingsRepository _settings;
        private SqliteAuditRepository _audit;
        private SqliteCustomerRepository _customers;
        private SqliteSaleStore _sales;
        private CodeService _codeSvc;
        private InventoryService _inventory;
        private PosService _pos;
        private User _admin;
        private readonly Random _rng = new Random();

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_bc_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            var init = new DatabaseInitializer(_db);
            init.ApplySchemaAndSeed();
            init.EnsureDefaultAdmin();

            _items = new SqliteItemRepository(_db);
            _stock = new SqliteStockRepository(_db);
            _codes = new SqliteItemCodeRepository(_db);
            _users = new SqliteUserRepository(_db);
            _settings = new SqliteSettingsRepository(_db);
            _audit = new SqliteAuditRepository(_db);
            _customers = new SqliteCustomerRepository(_db);
            _sales = new SqliteSaleStore(_db);
            _codeSvc = new CodeService(_codes, _items, _audit);
            _inventory = new InventoryService(_items, _stock, _settings, _audit, _codes);
            _pos = new PosService(_items, _stock, _sales, _customers, _settings, _audit);
            _admin = _users.GetByUsername("admin");
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        /// <summary>A realistic random EAN-13-length numeric code, as a scanner would emit.</summary>
        private string RandomBarcode() => string.Concat(Enumerable.Range(0, 13).Select(_ => (char)('0' + _rng.Next(10))));

        [Test]
        public void RandomBarcode_Assigned_ThenScanResolvesAndSearchFinds_AndSells()
        {
            Item panadol = _items.Search("Panadol").Single();
            int before = _stock.GetStockSummaries(new[] { panadol.Id })[panadol.Id].AvailableUnits;

            string scanned = RandomBarcode();
            TestContext.WriteLine($"[SETUP] assigning random barcode {scanned} to {panadol.NameEn}");
            _codeSvc.SetCode(_admin, panadol.Id, scanned);

            // 1) The POS resolves a scanned code to its item (what happens when the box gets the code + Enter).
            Item resolved = _codeSvc.ResolveItem(scanned);
            TestContext.WriteLine($"[SCAN] {scanned} -> {(resolved?.NameEn ?? "NOT FOUND")}");
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved.Id, Is.EqualTo(panadol.Id));

            // 2) Typing/scanning the code in any search box finds the item.
            var found = _inventory.Search(scanned, 10);
            TestContext.WriteLine($"[SEARCH] \"{scanned}\" -> {found.Count} result(s)");
            Assert.That(found.Count, Is.EqualTo(1));
            Assert.That(found[0].Item.Id, Is.EqualTo(panadol.Id));

            // 3) Scan-to-sell: the resolved item goes into the cart and the sale decrements stock.
            var cart = new List<CartLine> { new CartLine { ItemId = resolved.Id, UnitType = UnitType.Strip, Quantity = 1 } };
            Sale sale = _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "scan-test", "Cash");
            int after = _stock.GetStockSummaries(new[] { panadol.Id })[panadol.Id].AvailableUnits;
            TestContext.WriteLine($"[SELL] invoice {sale.SaleNumber}: stock {before} -> {after}");
            Assert.That(after, Is.EqualTo(before - panadol.UnitsPerStrip));
        }

        [Test]
        public void UnknownRandomNumbers_ScanGracefully_NoCrash_NoMatch()
        {
            for (int i = 0; i < 5; i++)
            {
                string junk = RandomBarcode();
                Item resolved = _codeSvc.ResolveItem(junk);
                var found = _inventory.Search(junk, 10);
                TestContext.WriteLine($"[SCAN unknown] {junk} -> resolve={(resolved == null ? "null" : resolved.NameEn)}, search={found.Count}");
                Assert.That(resolved, Is.Null, "an unknown random code must resolve to nothing");
                Assert.That(found, Is.Empty, "an unknown random code must return no items (no crash)");
            }
        }

        [Test]
        public void SameCode_CannotBeAssignedToTwoItems()
        {
            var all = _items.GetAll();
            Item a = all[0], b = all[1];
            string code = RandomBarcode();
            _codeSvc.SetCode(_admin, a.Id, code);
            TestContext.WriteLine($"[SETUP] {code} -> {a.NameEn}; now trying to give the same code to {b.NameEn}");
            // FR-QRC-06: one code maps to at most one item.
            Assert.Throws<Dawaii.Core.DuplicateCodeException>(() => _codeSvc.SetCode(_admin, b.Id, code));
            Assert.That(_codeSvc.ResolveItem(code).Id, Is.EqualTo(a.Id), "the first item keeps the code");
        }

        [Test]
        public void FullStock_Persists_WhenBarcodeCountReachesZero()
        {
            // Mirrors what FullStockForm does when the scan count-down hits zero: receive units,
            // set min/max levels and assign the (newly captured) random barcode — all with numbers only.
            Item item = _items.Search("Brufen").Single();
            int before = _stock.GetStockSummaries(new[] { item.Id })[item.Id].AvailableUnits;
            string captured = RandomBarcode();
            TestContext.WriteLine($"[FULL-STOCK] {item.NameEn}: +30 boxes, min 20/max 400, barcode {captured}");

            // Quantity is entered in BOXES (V1.7); one strip per box here, so each box is one strip of units.
            int expectedAdded = 30 * item.UnitsPerStrip;
            _inventory.ReceiveStock(_admin, item.Id, 30, DateTime.Today.AddYears(1),
                boxPurchasePrice: 25m, boxSellingPrice: 40m, stripsPerBox: 1);
            _inventory.SetStockLevels(_admin, item.Id, 20, 400);
            _codeSvc.SetCode(_admin, item.Id, captured);

            Item reloaded = _items.GetById(item.Id);
            int after = _stock.GetStockSummaries(new[] { item.Id })[item.Id].AvailableUnits;
            Assert.That(after, Is.EqualTo(before + expectedAdded));
            Assert.That(reloaded.MinQuantity, Is.EqualTo(20));
            Assert.That(reloaded.MaxQuantity, Is.EqualTo(400));
            Assert.That(_codeSvc.ResolveItem(captured).Id, Is.EqualTo(item.Id), "the captured barcode now finds the item");
        }
    }
}
