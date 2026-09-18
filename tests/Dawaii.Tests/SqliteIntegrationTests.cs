using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Dawaii.Core;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Real-database integration tests: every test runs against an actual SQLite file through the
    /// production data layer (initializer, repositories, transactional sale store) — no fakes.
    /// This is the permanent version of the manual smoke scripts used during development.
    /// </summary>
    [TestFixture]
    public class SqliteIntegrationTests
    {
        private string _path;
        private SqliteConnectionFactory _db;
        private DatabaseInitializer _init;
        private SqliteItemRepository _items;
        private SqliteStockRepository _stock;
        private SqliteUserRepository _users;
        private SqliteSettingsRepository _settings;
        private SqliteAuditRepository _audit;
        private SqliteCustomerRepository _customers;
        private SqliteSaleStore _sales;
        private PosService _pos;
        private InventoryService _inventory;
        private User _admin;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_it_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            _init = new DatabaseInitializer(_db);
            _init.ApplySchemaAndSeed();
            _init.EnsureDefaultAdmin();

            _items = new SqliteItemRepository(_db);
            _stock = new SqliteStockRepository(_db);
            _users = new SqliteUserRepository(_db);
            _settings = new SqliteSettingsRepository(_db);
            _audit = new SqliteAuditRepository(_db);
            _customers = new SqliteCustomerRepository(_db);
            _sales = new SqliteSaleStore(_db);
            _pos = new PosService(_items, _stock, _sales, _customers, _settings, _audit);
            _inventory = new InventoryService(_items, _stock, _settings, _audit, new SqliteItemCodeRepository(_db));
            _admin = _users.GetByUsername("admin");
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        [Test]
        public void FirstRun_CreatesSchemaSeedAndAdmin_LoginWorks()
        {
            Assert.That(_admin, Is.Not.Null, "seed admin exists");
            Assert.That(new AuthService(_users).Authenticate("admin", "admin123").Success, Is.True);
            Assert.That(_items.GetAll().Count, Is.GreaterThanOrEqualTo(20), "sample medicines seeded");
            // Migration column present and usable.
            var item = _items.GetAll()[0];
            item.SubstituteOf = _items.GetAll()[1].Id;
            _items.Update(item);
            Assert.That(_items.GetById(item.Id).SubstituteOf, Is.EqualTo(_items.GetAll()[1].Id));
        }

        [Test]
        public void BatchPricing_EndToEnd_DerivesStripPricesAndFollowsEdits()
        {
            // The whole V1.7 rule against a real database: box prices in, strip prices divided down,
            // and an edit to either the box price or the packaging re-deriving them.
            int id = _inventory.CreateItem(_admin, new Item
            {
                NameEn = "أملوديبين", UnitsPerStrip = 1, StripsPerBox = 4, SellingPrice = null
            });

            int batchId = _inventory.ReceiveStock(_admin, id, 10, DateTime.Today.AddYears(1),
                boxPurchasePrice: 3200m, boxSellingPrice: 4000m, stripsPerBox: 4, batchNumber: "LOT-9");

            StockBatch batch = _stock.GetBatch(batchId);
            Assert.That(batch.BatchNumber, Is.EqualTo("LOT-9"));
            Assert.That(batch.StripPurchasePrice, Is.EqualTo(800m), "3200 ÷ 4");
            Assert.That(batch.StripSellingPrice, Is.EqualTo(1000m), "4000 ÷ 4");
            Assert.That(batch.QuantityUnits, Is.EqualTo(40), "10 boxes × 4 strips × 1 unit");

            batch.BoxSellingPrice = 4500m;
            _inventory.UpdateBatch(_admin, batch);

            Assert.That(_stock.GetBatch(batchId).StripSellingPrice, Is.EqualTo(1125m), "4500 ÷ 4");
            Item item = _items.GetById(id);
            Assert.That(item.SellingPrice * item.UnitsPerBox, Is.EqualTo(4500m),
                "the newest batch's price is what the POS sells at");
        }

        [Test]
        public void DemoCatalog_StripPricesDivideDownFromTheBoxPrices()
        {
            // The shipped sample data must satisfy the rule it demonstrates.
            foreach (StockBatch b in _stock.GetActiveBatches())
            {
                Assert.That(b.StripPurchasePrice,
                    Is.EqualTo(decimal.Round(b.BoxPurchasePrice / b.StripsPerBox, 2, MidpointRounding.AwayFromZero)));
                Assert.That(b.StripSellingPrice,
                    Is.EqualTo(decimal.Round(b.BoxSellingPrice / b.StripsPerBox, 2, MidpointRounding.AwayFromZero)));
                Assert.That(b.BoxSellingPrice, Is.GreaterThanOrEqualTo(b.BoxPurchasePrice),
                    "demo stock should not be seeded at a loss");
            }
        }

        [Test]
        public void InitTwice_IsIdempotent()
        {
            Assert.DoesNotThrow(() => _init.ApplySchemaAndSeed());
            Assert.That(_users.GetAll().Count(u => u.Username == "admin"), Is.EqualTo(1));
        }

        [Test]
        public void Restart_DoesNotResurrectDeletedItems_OrDuplicateStock()
        {
            Item panadol = _items.Search("Panadol").Single();
            int batchesBefore = _stock.GetActiveBatches().Count;
            Assert.That(_items.Delete(panadol.Id), Is.True);

            _init.ApplySchemaAndSeed();   // simulated app restart

            Assert.That(_items.Search("Panadol"), Is.Empty, "deleted item stays deleted after restart");
            Assert.That(_stock.GetActiveBatches().Count,
                Is.EqualTo(batchesBefore - 1), "no demo batches re-added on restart");
        }

        [Test]
        public void Restart_AfterDeleteAll_CatalogStaysEmpty()
        {
            _inventory.DeleteAllItems(_admin, out _);
            _init.ApplySchemaAndSeed();   // simulated app restart

            Assert.That(_items.GetAll(activeOnly: false), Is.Empty, "demo catalog is not re-seeded");
        }

        [Test]
        public void ExistingLegacyDatabase_WithAnEmptyCatalog_IsNotTreatedAsNew()
        {
            _inventory.DeleteAllItems(_admin, out _);
            using (var conn = _db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // Simulates a database created before the demo_seeded marker was introduced.
                cmd.CommandText = "DELETE FROM settings WHERE key_name='demo_seeded'";
                cmd.ExecuteNonQuery();
            }

            _init.ApplySchemaAndSeed();

            Assert.That(_items.GetAll(activeOnly: false), Is.Empty,
                "an existing empty catalog is not a new database and must not receive demo rows");
        }

        [Test]
        public void Sale_RoundTrip_DecrementsStock_ReturnRestores_AllInRealTransactions()
        {
            Item panadol = _items.Search("Panadol").Single();
            int before = _stock.GetStockSummaries(new[] { panadol.Id })[panadol.Id].AvailableUnits;

            var cart = new List<CartLine> { new CartLine { ItemId = panadol.Id, UnitType = UnitType.Strip, Quantity = 2 } };
            Sale sale = _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "it-test");

            Assert.That(sale.SaleNumber, Is.EqualTo(sale.Id));
            int afterSale = _stock.GetStockSummaries(new[] { panadol.Id })[panadol.Id].AvailableUnits;
            Assert.That(afterSale, Is.EqualTo(before - 20));

            Sale loaded = _pos.GetSale(sale.Id);
            Assert.That(loaded.Lines.Single().TotalUnits, Is.EqualTo(20));
            Assert.That(loaded.Total, Is.EqualTo(sale.Total));

            _pos.ReturnSale(_admin, sale.Id, "integration return");
            int afterReturn = _stock.GetStockSummaries(new[] { panadol.Id })[panadol.Id].AvailableUnits;
            Assert.That(afterReturn, Is.EqualTo(before));
            Assert.That(_pos.GetSale(sale.Id).Status, Is.EqualTo(SaleStatus.Returned));
        }

        [Test]
        public void FindSale_ByPrintedInvoiceNumber_AgainstRealDatabase()
        {
            Item panadol = _items.Search("Panadol").Single();
            var cart = new List<CartLine> { new CartLine { ItemId = panadol.Id, UnitType = UnitType.Strip, Quantity = 1 } };
            Sale sale = _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "it-test");

            Assert.That(sale.SaleNumber, Is.EqualTo(sale.Id), "the invoice number is the id, as an integer");

            // As printed today, and as an old receipt still reads in either direction.
            string date = sale.CreatedAt.ToString("yyyyMMdd");
            Assert.That(_pos.FindSale(sale.SaleNumber.ToString()).Id, Is.EqualTo(sale.Id));
            Assert.That(_pos.FindSale(date + sale.Id).Id, Is.EqualTo(sale.Id));
            Assert.That(_pos.FindSale(sale.Id + date).Id, Is.EqualTo(sale.Id));

            // The found sale carries its lines, so the return screen can list them.
            Assert.That(_pos.FindSale(sale.SaleNumber.ToString()).Lines, Is.Not.Empty);

            Assert.That(_pos.FindSale("999999"), Is.Null);
        }

        [Test]
        public void OversellGuard_RejectsStaleAllocation_AndRollsBackAtomically()
        {
            Item item = _items.Search("Panadol").Single();
            StockBatch batch = _stock.GetBatches(item.Id).First();

            // A stale client thinks the batch still has its units and asks for more than exist.
            var sale = new Sale { UserId = _admin.Id, SaleType = SaleType.Cash, CreatedAt = DateTime.Now, Subtotal = 1, Total = 1 };
            sale.Lines.Add(new SaleLine
            {
                ItemId = item.Id, UnitType = UnitType.Unit, Quantity = batch.QuantityUnits + 50,
                UnitsEach = 1, UnitPrice = 1, LineTotal = 1,
                Allocations = { new SaleLineAllocation { BatchId = batch.Id, Units = batch.QuantityUnits + 50, UnitCost = 1 } }
            });

            Assert.Throws<InsufficientStockException>(() => _sales.Save(sale));

            // The whole transaction must roll back: no sale row, stock untouched.
            Assert.That(_sales.GetByDateRange(DateTime.Today, DateTime.Today.AddDays(1)), Is.Empty);
            Assert.That(_stock.GetBatch(batch.Id).QuantityUnits, Is.EqualTo(batch.QuantityUnits));
        }

        [Test]
        public void StockSummaries_MatchPerItemFefoMath()
        {
            Item item = _items.Search("Panadol").Single();
            _inventory.ReceiveStock(_admin, item.Id, 40, DateTime.Today.AddDays(20), 1m, 2m, 1);   // nearer expiry
            _inventory.ReceiveStock(_admin, item.Id, 10, null, 1m, 2m, 1);                  // undated
            int disposed = _inventory.ReceiveStock(_admin, item.Id, 99, DateTime.Today.AddDays(5), 1m, 2m, 1);
            _inventory.DisposeBatch(_admin, disposed);                                     // excluded

            var summary = _stock.GetStockSummaries(new[] { item.Id })[item.Id];
            var batches = _stock.GetBatches(item.Id);
            Assert.That(summary.AvailableUnits, Is.EqualTo(FefoAllocator.AvailableUnits(batches)));
            Assert.That(summary.NearestExpiry, Is.EqualTo(ExpiryEvaluator.NearestExpiry(batches)));
            Assert.That(summary.NearestExpiry, Is.EqualTo(DateTime.Today.AddDays(20)), "disposed batch must not win");
        }

        [Test]
        public void Sale_RecordsPaymentMethod_AndCreditIsNull()
        {
            Item panadol = _items.Search("Panadol").Single();
            var cart = new List<CartLine> { new CartLine { ItemId = panadol.Id, UnitType = UnitType.Strip, Quantity = 1 } };

            Sale bankak = _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t", "Bankak");
            Assert.That(_pos.GetSale(bankak.Id).PaymentMethod, Is.EqualTo("Bankak"));

            // A cash sale with no explicit method defaults to "Cash".
            Sale cash = _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t");
            Assert.That(_pos.GetSale(cash.Id).PaymentMethod, Is.EqualTo("Cash"));

            // Credit settles through the debt ledger, so it carries no payment method.
            Customer c = _customers.GetById(_customers.Add(new Customer { Name = "عميل" }));
            Sale credit = _pos.Complete(_admin, cart, SaleType.Credit, c.Id, 0m, "t", "Fawry");
            Assert.That(_pos.GetSale(credit.Id).PaymentMethod, Is.Null);
        }

        [Test]
        public void UnpricedItem_StoresNull_AndIsHiddenFromPosSearch()
        {
            int id = _items.Add(new Item { NameEn = "صنف بدون سعر", UnitsPerStrip = 1, StripsPerBox = 1, PurchasePrice = 10m, IsActive = true });

            Assert.That(_items.GetById(id).SellingPrice, Is.Null, "selling price round-trips as NULL");
            Assert.That(_inventory.Search("صنف بدون سعر").Any(v => v.Item.Id == id), Is.True, "inventory admin search still sees it");
            Assert.That(_inventory.SearchSellable("صنف بدون سعر").Any(v => v.Item.Id == id), Is.False, "POS search hides it until priced");

            // Once priced it becomes sellable.
            _items.UpdateSellingPrice(id, 15m);
            Assert.That(_inventory.SearchSellable("صنف بدون سعر").Any(v => v.Item.Id == id), Is.True);
        }

        [Test]
        public void LegacyDb_SellingPriceNotNull_MigratedToNullable_KeepingData()
        {
            string path = Path.Combine(Path.GetTempPath(), "dawaii_legacy_" + Guid.NewGuid().ToString("N") + ".db");
            var db = new SqliteConnectionFactory(path);
            try
            {
                // Fabricate a pre-migration items table (selling_price NOT NULL, no V1.2/1.3 columns).
                using (var conn = db.OpenConnection())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "CREATE TABLE items (" +
                        " id INTEGER PRIMARY KEY AUTOINCREMENT, name_ar TEXT NOT NULL, name_en TEXT, generic_name TEXT," +
                        " category_id INTEGER, units_per_strip INTEGER NOT NULL DEFAULT 1, strips_per_box INTEGER NOT NULL DEFAULT 1," +
                        " purchase_price REAL NOT NULL DEFAULT 0, selling_price REAL NOT NULL DEFAULT 0," +
                        " min_quantity INTEGER NOT NULL DEFAULT 0, expiry_warn_days INTEGER, is_active INTEGER NOT NULL DEFAULT 1," +
                        " created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                        " updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime')));" +
                        "INSERT INTO items (name_ar, purchase_price, selling_price) VALUES ('قديم', 5, 7.5);";
                    cmd.ExecuteNonQuery();
                }

                new DatabaseInitializer(db).ApplySchemaAndSeed();

                var items = new SqliteItemRepository(db);
                Item legacy = items.Search("قديم").Single();
                Assert.That(legacy.SellingPrice, Is.EqualTo(7.5m), "existing price survives the rebuild");

                // The rebuilt table accepts NULL selling prices.
                int id = items.Add(new Item { NameEn = "جديد", UnitsPerStrip = 1, StripsPerBox = 1, PurchasePrice = 1m, IsActive = true });
                Assert.That(items.GetById(id).SellingPrice, Is.Null);
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                foreach (string f in new[] { path, path + "-wal", path + "-shm" })
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        [Test]
        public void Search_FindsItemByAssignedBarcode()
        {
            Item panadol = _items.Search("Panadol").Single();
            using (var conn = _db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO item_codes (item_id, code) VALUES (" + panadol.Id + ", '6221033000123')";
                cmd.ExecuteNonQuery();
            }

            // Scanning/typing the barcode in any search box resolves to the item (V1.3).
            var byCode = _inventory.Search("6221033000123", 10);
            Assert.That(byCode.Count, Is.EqualTo(1));
            Assert.That(byCode[0].Item.Id, Is.EqualTo(panadol.Id));
        }

        [Test]
        public void SetStockLevels_PersistsMinAndMax()
        {
            Item panadol = _items.Search("Panadol").Single();
            _inventory.SetStockLevels(_admin, panadol.Id, 30, 500);

            Item reloaded = _items.GetById(panadol.Id);
            Assert.That(reloaded.MinQuantity, Is.EqualTo(30));
            Assert.That(reloaded.MaxQuantity, Is.EqualTo(500));

            // Max below min is rejected and nothing is written.
            Assert.Throws<Dawaii.Core.ValidationException>(() => _inventory.SetStockLevels(_admin, panadol.Id, 100, 50));
        }

        [TestCase(10, 4, 4000.00, 1000.00)]   // even pack: box 4000 -> strip 1000
        [TestCase(10, 3, 4000.00, 1333.33)]   // odd pack: box 4000 / 3 strips, must not drift
        [TestCase(1, 12, 300.00, 25.00)]      // single-unit strips
        public void BoxPrice_RoundTrips_StripDerived(int unitsPerStrip, int stripsPerBox, decimal boxPrice, decimal expectedStrip)
        {
            int unitsPerBox = unitsPerStrip * stripsPerBox;
            // The UI enters a box price and stores it per single unit (box / unitsPerBox).
            int id = _items.Add(new Item
            {
                NameEn = "سعر-اختبار", UnitsPerStrip = unitsPerStrip, StripsPerBox = stripsPerBox,
                SellingPrice = boxPrice / unitsPerBox, IsActive = true
            });

            Item read = _items.GetById(id);   // read path must NOT round the per-unit rate to 2 dp
            Assert.That(decimal.Round(UnitConverter.PriceOf(read, UnitType.Box), 2), Is.EqualTo(boxPrice),
                "box price must round-trip exactly");
            Assert.That(decimal.Round(UnitConverter.PriceOf(read, UnitType.Strip), 2), Is.EqualTo(expectedStrip),
                "strip price = box / strips per box");
        }

        [Test]
        public void DeleteItem_RemovesItemWithoutSales_ButKeepsItemsThatHaveSales()
        {
            // A fresh item with only stock can be deleted outright.
            int id = _items.Add(new Item { NameEn = "قابل-للحذف", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1, IsActive = true });
            _inventory.ReceiveStock(_admin, id, 10, DateTime.Today.AddYears(1), 0.5m, 1m, 1);
            Assert.That(_inventory.DeleteItem(_admin, id), Is.True);
            Assert.That(_items.GetById(id), Is.Null);

            // An item that has been sold must be kept (sales history stays intact).
            Item panadol = _items.Search("Panadol").Single();
            var cart = new List<CartLine> { new CartLine { ItemId = panadol.Id, UnitType = UnitType.Strip, Quantity = 1 } };
            _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t");
            Assert.That(_inventory.DeleteItem(_admin, panadol.Id), Is.False);
            Assert.That(_items.GetById(panadol.Id), Is.Not.Null);
        }

        [Test]
        public void DeleteItem_ReleasesItsBarcode_EvenWhenTheRowIsKeptForSales_V19()
        {
            var codeRepo = new SqliteItemCodeRepository(_db);
            var codes = new CodeService(codeRepo, _items, _audit);

            // A drug with only stock: deleting it takes the barcode with it.
            int scrapped = _items.Add(new Item { NameEn = "قابل-للحذف", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1, IsActive = true });
            codes.SetCode(_admin, scrapped, "6221033000999");
            Assert.That(_inventory.DeleteItem(_admin, scrapped), Is.True);
            Assert.That(codes.ResolveItemId("6221033000999"), Is.Null, "a deleted item keeps no barcode");

            // A drug that has been sold is kept for its history, but it leaves the catalog — so its
            // barcode is released too, or the replacement box could never be scanned in under it.
            Item panadol = _items.Search("Panadol").Single();
            codes.SetCode(_admin, panadol.Id, "6221033000123");
            var cart = new List<CartLine> { new CartLine { ItemId = panadol.Id, UnitType = UnitType.Strip, Quantity = 1 } };
            _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "t");

            Assert.That(_inventory.DeleteItem(_admin, panadol.Id), Is.False, "kept for its sales");
            Assert.That(codes.ResolveItemId("6221033000123"), Is.Null, "but its barcode is freed");

            int replacement = _items.Add(new Item { NameEn = "بنادول-جديد", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1, IsActive = true });
            Assert.DoesNotThrow(() => codes.SetCode(_admin, replacement, "6221033000123"),
                "the same barcode can be scanned onto the replacement drug");
        }

        [Test]
        public void ItemCode_IsOnePerItem_SecondCodeReplacesTheFirst_V19()
        {
            var codes = new CodeService(new SqliteItemCodeRepository(_db), _items, _audit);
            Item panadol = _items.Search("Panadol").Single();

            codes.SetCode(_admin, panadol.Id, "OLD-CODE");
            codes.SetCode(_admin, panadol.Id, "NEW-CODE");

            Assert.That(codes.GetCode(panadol.Id), Is.EqualTo("NEW-CODE"));
            Assert.That(codes.ResolveItemId("OLD-CODE"), Is.Null, "the replaced code stops resolving");
            using (var conn = _db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM item_codes WHERE item_id=" + panadol.Id;
                Assert.That(Convert.ToInt64(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "the unique index keeps exactly one row per item");
            }
        }

        /// <summary>
        /// A price change is the shelf's, not the shipment's (V2.2). Panadol bought again at a new price
        /// sells at that price whichever box comes off the shelf, so every batch is re-priced with it.
        /// The till already worked this way — it reads the item, not the batch — but the batches
        /// themselves kept yesterday's figure, so the stock screen contradicted the receipt.
        /// </summary>
        [Test]
        public void NewDelivery_RepricesEveryBatchOfTheDrug_SellingPriceOnly()
        {
            int id = _items.Add(new Item { NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10, IsActive = true });

            int oldBatch = _inventory.ReceiveStock(_admin, id, 5, DateTime.Today.AddYears(1), 800m, 1000m, 10);
            int newBatch = _inventory.ReceiveStock(_admin, id, 5, DateTime.Today.AddYears(2), 900m, 1500m, 10);

            Assert.That(_stock.GetBatch(oldBatch).BoxSellingPrice, Is.EqualTo(1500m),
                "the older batch now sells at today's price");
            Assert.That(_stock.GetBatch(newBatch).BoxSellingPrice, Is.EqualTo(1500m));

            Assert.That(_stock.GetBatch(oldBatch).BoxPurchasePrice, Is.EqualTo(800m),
                "what it cost is history — profit is measured against it and must not move");
            Assert.That(_stock.GetBatch(newBatch).BoxPurchasePrice, Is.EqualTo(900m));
        }

        /// <summary>
        /// Packaging changes between shipments, so matching the BOX price would leave two batches selling
        /// the same tablet at different prices. The per-unit price is what has to agree.
        /// </summary>
        [Test]
        public void Repricing_MatchesThePerUnitPrice_WhenPackagingDiffers()
        {
            int id = _items.Add(new Item { NameEn = "brufen", UnitsPerStrip = 10, StripsPerBox = 10, IsActive = true });

            // 10 strips a box, sold at 1000 a box = 10 per tablet.
            int tenStrip = _inventory.ReceiveStock(_admin, id, 5, DateTime.Today.AddYears(1), 800m, 1000m, 10);
            // The next delivery comes 4 strips to a box, priced at 800 a box = 20 per tablet.
            int fourStrip = _inventory.ReceiveStock(_admin, id, 5, DateTime.Today.AddYears(2), 600m, 800m, 4);

            StockBatch a = _stock.GetBatch(tenStrip), b = _stock.GetBatch(fourStrip);
            Assert.That(b.SellingPrice, Is.EqualTo(20m));
            Assert.That(a.SellingPrice, Is.EqualTo(20m),
                "the same tablet cannot cost the customer two different amounts");
            Assert.That(a.BoxSellingPrice, Is.EqualTo(2000m),
                "a 100-tablet box at 20 a tablet is 2000 — the box figure follows its own packaging");
            Assert.That(_items.GetById(id).SellingPrice, Is.EqualTo(20m));
        }

        /// <summary>
        /// The till commits once per sale, per return and per stock movement, and at SQLite's default
        /// (synchronous=FULL) each of those fsyncs the write-ahead log — measured at 3.5 ms against
        /// 0.05 ms, the largest single cost in the app. NORMAL under WAL is the documented pairing and
        /// stays crash-safe for the application; only a power cut or kernel panic can lose the last
        /// commits. It is set on the connection string, which is easy to change without noticing, so
        /// the value is pinned here: 0 = OFF, 1 = NORMAL, 2 = FULL.
        /// </summary>
        [Test]
        public void Connections_UseWalWithNormalSync()
        {
            using (System.Data.Common.DbConnection conn = _db.OpenConnection())
            using (System.Data.Common.DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA synchronous";
                Assert.That(Convert.ToInt32(cmd.ExecuteScalar()), Is.EqualTo(1),
                    "synchronous must be NORMAL — FULL fsyncs on every sale");

                cmd.CommandText = "PRAGMA journal_mode";
                Assert.That(Convert.ToString(cmd.ExecuteScalar()).ToLowerInvariant(), Is.EqualTo("wal"),
                    "NORMAL is only safe in WAL mode; the two go together");
            }
        }

        /// <summary>
        /// A batch is never deleted when it empties, so a pharmacy accumulates spent rows forever. They
        /// used to be read alongside the real stock: after a few years most of a drug's batches held
        /// zero, every sale paid to load them, and a spent long-expired batch could still be reported as
        /// the item's nearest expiry — warning the cashier about stock that is not on the shelf.
        /// </summary>
        [Test]
        public void SpentBatches_DoNotDriveStockFiguresOrExpiryWarnings()
        {
            int id = _items.Add(new Item { NameEn = "panadol", UnitsPerStrip = 1, StripsPerBox = 1, IsActive = true });

            // An old batch about to expire, then a fresh one that is not.
            _inventory.ReceiveStock(_admin, id, 5, DateTime.Today.AddDays(3), 100m, 200m, 1, "OLD");
            _inventory.ReceiveStock(_admin, id, 5, DateTime.Today.AddYears(2), 100m, 200m, 1, "NEW");

            // Sell the old one out. FEFO takes the nearest expiry first, so this empties OLD exactly.
            _pos.Complete(_admin, new[] { new CartLine { ItemId = id, UnitType = UnitType.Unit, Quantity = 5 } },
                SaleType.Cash, null, 0m, "T1", "Cash");

            Assert.That(_stock.GetSellableBatches(id).Select(b => b.BatchNumber), Is.EqualTo(new[] { "NEW" }),
                "the spent batch is still on file, but it is not stock any more");

            ItemStockView view = _inventory.GetView(id);
            Assert.That(view.AvailableUnits, Is.EqualTo(5));
            Assert.That(view.NearestExpiry, Is.EqualTo(DateTime.Today.AddYears(2)),
                "an empty box that expires on Friday is not the shelf's nearest expiry");
            Assert.That(_pos.NearExpiryWarning(id), Is.Null,
                "and it must not warn the cashier about stock that is not there");

            Assert.That(_stock.GetBatches(id, includeDisposed: true).Count, Is.EqualTo(2),
                "the batches screen still shows the history");
        }

        /// <summary>The dead-stock report projects items with a hand-written column list, which had
        /// drifted from what the item mapper reads. A column it does not select comes back as the type's
        /// default instead of an error, so the report quietly showed every stale drug with a max level of
        /// zero — indistinguishable from one genuinely not set.</summary>
        [Test]
        public void DeadStock_Report_Loads()
        {
            int id = _items.Add(new Item { NameEn = "راكد", GenericName = "still", UnitsPerStrip = 1, StripsPerBox = 1, SellingPrice = 1m, MaxQuantity = 400, IsActive = true });
            _inventory.ReceiveStock(_admin, id, 1, DateTime.Today.AddYears(1), 10m, 20m, 1, "DEAD-1");

            var rows = new SqliteReportRepository(_db).DeadStock(60);

            Assert.That(rows.Select(r => r.Item.NameEn), Contains.Item("راكد"),
                "stock that has never sold is exactly what this report is for");
            Assert.That(rows.Single(r => r.Item.NameEn == "راكد").Item.MaxQuantity, Is.EqualTo(400),
                "a column the projection forgets is not an error — it arrives as 0 and reads as real");
        }

        [Test]
        public void Search_With2000Items_StaysFast_NoNPlusOne()
        {
            // Seed 2000 items + a batch each on one connection (bulk, transactional).
            using (var conn = _db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                for (int i = 0; i < 2000; i++)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText =
                            "INSERT INTO items (name_en, units_per_strip, strips_per_box, selling_price, is_active) " +
                            $"VALUES ('perf-{i:D4}', 10, 10, 2.0, 1); " +
                            "INSERT INTO stock_batches (item_id, quantity_units, expiry_date, " +
                            "  strips_per_box, units_per_strip, box_purchase_price, box_selling_price) " +
                            "VALUES (last_insert_rowid(), 100, '2027-01-01', 10, 10, 100.0, 200.0);";
                        cmd.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }

            var sw = Stopwatch.StartNew();
            var views = _inventory.Search("perf", limit: 200);
            sw.Stop();

            Assert.That(views.Count, Is.EqualTo(200));
            Assert.That(views.All(v => v.AvailableUnits == 100), "stock joined correctly for every row");
            // Generous bound (NFR-01 targets 300ms on minimum hardware) — mainly guards a
            // regression back to one-query-per-item behaviour, which takes many seconds.
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(1500),
                $"search took {sw.ElapsedMilliseconds}ms — N+1 regression?");
        }
    }
}
