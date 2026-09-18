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
    /// Fifty months of a busy pharmacy, driven through the production services against a real SQLite
    /// file. Marked Explicit so it never runs in the normal suite — it writes millions of rows.
    ///
    ///   dotnet test --filter "FullyQualifiedName~SoakTests" -c Release
    ///
    /// The point is not to assert a result. It is to find the failures that only appear at size:
    /// a query that is fine at 500 rows and hopeless at 500,000, an id that overflows, a transaction
    /// that deadlocks against itself, a file that grows without bound. So it reports timings per
    /// simulated month and fails with the day, the operation and the row counts if anything throws.
    /// </summary>
    [TestFixture]
    [Explicit("Writes millions of rows; run deliberately.")]
    [Category("Soak")]
    public class SoakTests
    {
        // A day in the pharmacy the user described.
        private const int ItemLinesSoldPerDay = 850;
        private const int OrdersPerDay = 6;
        private const double ReturnRate = 0.20;
        private const int DaysPerMonth = 30;

        /// <summary>Fifty months by default — the run the pharmacy asked for. Shorten it with
        /// DAWAII_SOAK_MONTHS when comparing two builds, so both cover the same span.</summary>
        private static readonly int Months =
            int.TryParse(Environment.GetEnvironmentVariable("DAWAII_SOAK_MONTHS"), out int m) && m > 0 ? m : 50;

        private const int CatalogSize = 600;      // roughly the real pharmacy's catalogue
        private const int SupplierCount = 12;

        /// <summary>
        /// Supply has to match demand or the simulation stops resembling a pharmacy. A first run ordered
        /// 5-50 boxes a line and sold single tablets, delivering roughly 175 units for every one sold;
        /// stock piled up, batches per drug climbed without limit, and the slowdown that produced said
        /// more about the harness than about the app. Customers here buy the way they really do — mostly
        /// strips, often a whole box — and orders top up by a few boxes a line, so the shelf reaches a
        /// steady state and old batches actually get used up.
        /// </summary>
        private const int MaxBoxesPerOrderLine = 6;

        private string _path;
        private SqliteConnectionFactory _db;
        private SqliteItemRepository _items;
        private SqliteStockRepository _stock;
        private SqliteSaleStore _saleStore;
        private SqliteCustomerRepository _customers;
        private PosService _pos;
        private InventoryService _inventory;
        private SupplierService _suppliers;
        private User _admin;

        /// <summary>NUnit buffers TestContext output until the test ends, and this one runs for hours,
        /// so progress also goes to a file that can be watched while it runs.</summary>
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "dawaii_soak_progress.log");

        private readonly List<int> _itemIds = new List<int>();
        private readonly List<int> _supplierIds = new List<int>();
        private readonly Random _rng = new Random(20260825);

        // Where the time actually goes, so a slowdown points at a phase rather than at "the app".
        private double _orderMs, _saleMs, _returnMs;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_soak_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            var init = new DatabaseInitializer(_db);
            init.ApplySchemaAndSeed();
            init.EnsureDefaultAdmin();

            _items = new SqliteItemRepository(_db);
            _stock = new SqliteStockRepository(_db);
            _saleStore = new SqliteSaleStore(_db);
            _customers = new SqliteCustomerRepository(_db);
            var settings = new SqliteSettingsRepository(_db);
            var audit = new SqliteAuditRepository(_db);
            var users = new SqliteUserRepository(_db);

            _pos = new PosService(_items, _stock, _saleStore, _customers, settings, audit);
            _inventory = new InventoryService(_items, _stock, settings, audit, new SqliteItemCodeRepository(_db));
            _suppliers = new SupplierService(new SqliteSupplierRepository(_db), _items, audit);
            _admin = users.GetByUsername("admin");
        }

        [TearDown]
        public void TearDown()
        {
            SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* file lock — harmless */ }
        }

        private static void Log(string line)
        {
            TestContext.WriteLine(line);
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine); }
            catch { /* the log is a convenience, never a reason to fail the run */ }
        }

        [Test]
        public void FiftyMonths_OfARealPharmacysTraffic()
        {
            try { File.Delete(LogPath); } catch { }
            Log($"START catalog={CatalogSize} suppliers={SupplierCount} " +
                $"lines/day={ItemLinesSoldPerDay} orders/day={OrdersPerDay} months={Months} " +
                $"sync={SyncMode()}");

            var seedWatch = Stopwatch.StartNew();
            Seed();
            seedWatch.Stop();
            Log($"seeded in {seedWatch.Elapsed.TotalSeconds:0.0}s");

            var overall = Stopwatch.StartNew();
            DateTime day = new DateTime(2022, 1, 1);
            long sales = 0, lines = 0, returns = 0, orders = 0;
            string phase = "start";
            int dayIndex = 0;

            try
            {
                for (int month = 1; month <= Months; month++)
                {
                    var monthWatch = Stopwatch.StartNew();
                    long monthSales = 0;

                    for (int d = 0; d < DaysPerMonth; d++, dayIndex++, day = day.AddDays(1))
                    {
                        phase = "orders";
                        var w = Stopwatch.StartNew();
                        for (int o = 0; o < OrdersPerDay; o++) { PlaceOrder(day); orders++; }
                        _orderMs += w.Elapsed.TotalMilliseconds;

                        phase = "sales";
                        w.Restart();
                        int linesToday = 0;
                        var todaysSales = new List<int>();
                        while (linesToday < ItemLinesSoldPerDay)
                        {
                            int inCart = 1 + _rng.Next(4);          // 1..4 medicines a customer
                            Sale sale = MakeSale(inCart);
                            if (sale == null) continue;             // nothing sellable — see StockRanOut

                            todaysSales.Add(sale.Id);
                            linesToday += sale.Lines.Count;
                            lines += sale.Lines.Count;
                            sales++; monthSales++;
                        }

                        _saleMs += w.Elapsed.TotalMilliseconds;

                        phase = "returns";
                        w.Restart();
                        foreach (int saleId in todaysSales)
                        {
                            if (_rng.NextDouble() >= ReturnRate) continue;
                            _pos.ReturnSale(_admin, saleId, "إرجاع");
                            returns++;
                        }
                        _returnMs += w.Elapsed.TotalMilliseconds;
                    }

                    monthWatch.Stop();
                    Report(month, monthWatch, monthSales, sales, lines, returns, orders);
                }
            }
            catch (Exception ex)
            {
                overall.Stop();
                Log("=== FAILED ===");
                Log($"day index {dayIndex} ({day:yyyy-MM-dd}) during '{phase}'");
                Log($"sales={sales} lines={lines} returns={returns} orders={orders}");
                Log($"db size = {DbSizeMb():0.0} MB, elapsed {overall.Elapsed}");
                Log(ex.ToString());
                throw;
            }

            overall.Stop();
            Log($"COMPLETED {Months} months in {overall.Elapsed}");
            Log($"sales={sales} lines={lines} returns={returns} orders={orders}");
            Log($"db size = {DbSizeMb():0.0} MB, live batches = {BatchCount()}");
        }

        /// <summary>Prints the month's timings and the numbers that matter for spotting decay: a stable
        /// pharmacy should take about the same time in month 50 as in month 1.</summary>
        private void Report(int month, Stopwatch watch, long monthSales,
            long sales, long lines, long returns, long orders)
        {
            double perSale = monthSales == 0 ? 0 : watch.Elapsed.TotalMilliseconds / monthSales;
            Log($"month {month,2}  {watch.Elapsed.TotalSeconds,7:0.0}s  {perSale,6:0.00} ms/sale  " +
                $"sales={sales,8} lines={lines,9} returns={returns,7} orders={orders,5}  " +
                $"batches={BatchCount(),7}  db={DbSizeMb(),7:0.0}MB  mem={GC.GetTotalMemory(false) / (1024 * 1024),5}MB  " +
                $"[order {_orderMs / 1000.0,6:0.0}s  sale {_saleMs / 1000.0,7:0.0}s  return {_returnMs / 1000.0,6:0.0}s]");
        }

        /// <summary>Live stock batches. The number worth watching: FEFO reads every batch an item has,
        /// so a shelf that only ever grows is the thing that would make month 50 slower than month 1.</summary>
        private long BatchCount()
        {
            try
            {
                using (System.Data.Common.DbConnection conn = _db.OpenConnection())
                using (System.Data.Common.DbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM stock_batches WHERE is_disposed=0";
                    return Convert.ToInt64(cmd.ExecuteScalar());
                }
            }
            catch { return -1; }
        }

        /// <summary>The durability setting in force, so a run's log says which build produced it.</summary>
        private string SyncMode()
        {
            try
            {
                using (System.Data.Common.DbConnection conn = _db.OpenConnection())
                using (System.Data.Common.DbCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA synchronous";
                    int v = Convert.ToInt32(cmd.ExecuteScalar());
                    return v == 0 ? "OFF" : v == 1 ? "NORMAL" : "FULL";
                }
            }
            catch { return "?"; }
        }

        private double DbSizeMb()
        {
            double total = 0;
            foreach (string f in new[] { _path, _path + "-wal" })
                if (File.Exists(f)) total += new FileInfo(f).Length;
            return total / (1024.0 * 1024.0);
        }

        // ---------------- the pharmacy's day ----------------

        private void Seed()
        {
            for (int i = 0; i < SupplierCount; i++)
                _supplierIds.Add(_suppliers.CreateSupplier(_admin, "شركة " + i));

            for (int i = 0; i < CatalogSize; i++)
                _itemIds.Add(_items.Add(new Item
                {
                    NameEn = "drug-" + i,
                    GenericName = "generic-" + i,
                    UnitsPerStrip = 10,
                    StripsPerBox = 10,
                    IsActive = true
                }));

            // Open with stock on the shelf so day one can sell.
            // Opening stock: a few boxes of each, the way a shelf actually looks on day one.
            foreach (int id in _itemIds)
                _inventory.ReceiveStock(_admin, id, 6, DateTime.Today.AddYears(3), 1000m, 1400m, 10);
        }

        /// <summary>One supplier order: a company, a representative, and 10-30 medicines.</summary>
        private void PlaceOrder(DateTime day)
        {
            int supplierId = _supplierIds[_rng.Next(_supplierIds.Count)];
            int lineCount = 10 + _rng.Next(21);

            var lines = new List<PurchaseInvoiceLine>();
            var used = new HashSet<int>();
            for (int i = 0; i < lineCount; i++)
            {
                int itemId = _itemIds[_rng.Next(_itemIds.Count)];
                if (!used.Add(itemId)) continue;    // one line per drug, as a real invoice is written

                lines.Add(new PurchaseInvoiceLine
                {
                    ItemId = itemId,
                    QuantityBoxes = 1 + _rng.Next(MaxBoxesPerOrderLine),
                    StripsPerBox = 10,
                    BoxPurchasePrice = 800m + _rng.Next(400),
                    BoxSellingPrice = 1400m + _rng.Next(600),
                    ExpiryDate = day.AddYears(2),
                    BatchNumber = "LOT-" + _rng.Next(100000)
                });
            }

            PurchaseInvoice invoice = _suppliers.RecordInvoice(_admin, supplierId, "مندوب " + _rng.Next(20),
                "INV-" + day.ToString("yyyyMMdd") + "-" + _rng.Next(1000), day, lines);

            // Most deliveries get paid; some sit on the books, which is what builds the payable.
            double roll = _rng.NextDouble();
            if (roll < 0.55) _suppliers.SettleInvoice(_admin, invoice.Id);
            else if (roll < 0.80) _suppliers.RecordPayment(_admin, invoice.Id,
                decimal.Round(invoice.Total / 2m, 2), "دفعة جزئية");
        }

        /// <summary>One customer at the till. Returns null when nothing in the cart had stock — the
        /// caller retries rather than counting it, so a shelf that empties shows up as a slow day
        /// rather than as a silent under-count.</summary>
        private Sale MakeSale(int lineCount)
        {
            var cart = new List<CartLine>();
            var used = new HashSet<int>();
            for (int i = 0; i < lineCount; i++)
            {
                int itemId = _itemIds[_rng.Next(_itemIds.Count)];
                if (!used.Add(itemId)) continue;

                // What a customer actually walks out with: usually a strip, often a whole box, sometimes
                // a few loose tablets.
                double roll = _rng.NextDouble();
                UnitType unit = roll < 0.30 ? UnitType.Box : roll < 0.80 ? UnitType.Strip : UnitType.Unit;
                cart.Add(new CartLine { ItemId = itemId, UnitType = unit, Quantity = 1 + _rng.Next(2) });
            }
            if (cart.Count == 0) return null;

            try
            {
                return _pos.Complete(_admin, cart, SaleType.Cash, null, 0m, "SOAK", "Cash");
            }
            catch (InsufficientStockException)
            {
                // Expected and uninteresting: FEFO ran a drug out between the order and the sale.
                return null;
            }
            catch (ValidationException)
            {
                // Unpriced item, or similar — also a legitimate refusal rather than a defect.
                return null;
            }
        }
    }
}
