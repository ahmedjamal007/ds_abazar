using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Dawaii.Core.Services;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Upgrade path for a pharmacy whose sales were written before V1.8, when the invoice number was
    /// the text "yyyyMMdd-id" and a sale was either Completed or Returned outright.
    ///
    /// The migration has to rebuild the sales table — SQLite can neither retype a column nor widen the
    /// status CHECK — so these tests guard the part that would hurt most if it went wrong: real sales,
    /// their lines and their customers' debt surviving the rebuild intact.
    /// </summary>
    [TestFixture]
    public class InvoiceNumberMigrationTests
    {
        private string _path;
        private SqliteConnectionFactory _db;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_num_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            CreateOldShapedDatabase();
        }

        [TearDown]
        public void TearDown()
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            foreach (string f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* file lock — harmless */ }
        }

        /// <summary>The pre-V1.8 sales shape, with one completed and one already-returned invoice.</summary>
        private void CreateOldShapedDatabase()
        {
            using (DbConnection conn = _db.OpenConnection())
            {
                Exec(conn, "CREATE TABLE settings (key_name TEXT PRIMARY KEY, value TEXT)");
                Exec(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, username TEXT NOT NULL UNIQUE, " +
                           "password_hash TEXT NOT NULL, full_name TEXT, role TEXT NOT NULL DEFAULT 'Cashier', " +
                           "is_active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn, "CREATE TABLE customers (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, phone TEXT, " +
                           "balance REAL NOT NULL DEFAULT 0, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn, "CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, name_ar TEXT NOT NULL, name_en TEXT, " +
                           "generic_name TEXT, units_per_strip INTEGER NOT NULL DEFAULT 1, strips_per_box INTEGER NOT NULL DEFAULT 1, " +
                           "purchase_price REAL NOT NULL DEFAULT 0, selling_price REAL, min_quantity INTEGER NOT NULL DEFAULT 0, " +
                           "max_quantity INTEGER NOT NULL DEFAULT 0, expiry_warn_days INTEGER, substitute_of INTEGER, " +
                           "is_active INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')), " +
                           "updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn,
                    "CREATE TABLE sales (" +
                    " id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    " sale_number TEXT UNIQUE," +
                    " user_id INTEGER NOT NULL REFERENCES users(id)," +
                    " customer_id INTEGER REFERENCES customers(id)," +
                    " sale_type TEXT NOT NULL DEFAULT 'Cash' CHECK (sale_type IN ('Cash','Credit'))," +
                    " payment_method TEXT," +
                    " subtotal REAL NOT NULL DEFAULT 0, discount REAL NOT NULL DEFAULT 0," +
                    " total REAL NOT NULL DEFAULT 0, cost_total REAL NOT NULL DEFAULT 0," +
                    " status TEXT NOT NULL DEFAULT 'Completed' CHECK (status IN ('Completed','Returned'))," +
                    " terminal TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn, "CREATE TABLE sale_lines (id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                           "sale_id INTEGER NOT NULL REFERENCES sales(id) ON DELETE CASCADE, " +
                           "item_id INTEGER NOT NULL, unit_type TEXT NOT NULL DEFAULT 'Unit', quantity INTEGER NOT NULL, " +
                           "units_each INTEGER NOT NULL, unit_price REAL NOT NULL, line_total REAL NOT NULL, " +
                           "cost_total REAL NOT NULL DEFAULT 0)");

                Exec(conn, "INSERT INTO users (id, username, password_hash, role) VALUES (1, 'admin', 'x', 'Admin')");
                Exec(conn, "INSERT INTO customers (id, name, balance) VALUES (1, 'أحمد', 500)");
                Exec(conn, "INSERT INTO items (id, name_ar, units_per_strip, strips_per_box, selling_price) " +
                           "VALUES (1, 'بنادول', 10, 10, 1)");
                // Invoice 7 as it was numbered before the upgrade — the one staff could not look up.
                Exec(conn, "INSERT INTO sales (id, sale_number, user_id, sale_type, subtotal, total, cost_total, status, created_at) " +
                           "VALUES (7, '20260801-7', 1, 'Cash', 500, 500, 300, 'Completed', '2026-08-01 00:42:50')");
                Exec(conn, "INSERT INTO sale_lines (id, sale_id, item_id, unit_type, quantity, units_each, unit_price, line_total, cost_total) " +
                           "VALUES (1, 7, 1, 'Box', 5, 100, 1, 500, 300)");
                Exec(conn, "INSERT INTO sales (id, sale_number, user_id, customer_id, sale_type, subtotal, total, cost_total, status, created_at) " +
                           "VALUES (8, '20260801-8', 1, 1, 'Credit', 200, 200, 120, 'Returned', '2026-08-01 01:10:00')");
            }
        }

        [Test]
        public void Upgrade_TurnsTheInvoiceNumberIntoAnInteger()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            Assert.That(Convert.ToString(Scalar("SELECT type FROM pragma_table_info('sales') WHERE name='sale_number'")),
                Is.EqualTo("INTEGER"), "the column itself is an integer, so an integer search matches it");
            Assert.That(Convert.ToInt32(Scalar("SELECT sale_number FROM sales WHERE id=7")), Is.EqualTo(7));
            Assert.That(Convert.ToInt32(Scalar("SELECT sale_number FROM sales WHERE id=8")), Is.EqualTo(8));
        }

        [Test]
        public void Upgrade_LeavesTheOldInvoiceFindableByItsPrintedNumber()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();
            var store = new SqliteSaleStore(_db);

            // The number now printed, plus what an already-printed receipt reads as on an RTL screen.
            Assert.That(store.GetBySaleNumber(7)?.Id, Is.EqualTo(7));
            foreach (int candidate in SaleNumberParser.Candidates("720260801"))
                if (store.GetBySaleNumber(candidate) != null) Assert.Pass();
            Assert.Fail("an invoice from an old receipt must still be findable");
        }

        [Test]
        public void Upgrade_KeepsSalesLinesAndDebtIntactAcrossTheTableRebuild()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            Sale sale = new SqliteSaleStore(_db).GetById(7);
            Assert.That(sale, Is.Not.Null);
            Assert.That(sale.Total, Is.EqualTo(500m));
            Assert.That(sale.CostTotal, Is.EqualTo(300m));
            Assert.That(sale.CreatedAt, Is.EqualTo(new DateTime(2026, 8, 1, 0, 42, 50)));
            Assert.That(sale.Lines.Single().Quantity, Is.EqualTo(5), "the line survived the rebuild");
            Assert.That(sale.Lines.Single().RemainingQuantity, Is.EqualTo(5), "nothing returned yet");

            Assert.That(Convert.ToDecimal(Scalar("SELECT balance FROM customers WHERE id=1")), Is.EqualTo(500m));
        }

        [Test]
        public void Upgrade_BackfillsAlreadyReturnedInvoicesAsFullyRefunded()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            Sale returned = new SqliteSaleStore(_db).GetById(8);

            Assert.That(returned.Status, Is.EqualTo(SaleStatus.Returned));
            Assert.That(returned.ReturnedTotal, Is.EqualTo(200m), "a return recorded before V1.8 refunded everything");
            Assert.That(returned.ReturnedCost, Is.EqualTo(120m));
            Assert.That(returned.NetTotal, Is.Zero, "so reports still read it as contributing nothing");
        }

        [Test]
        public void Upgrade_AcceptsThePartiallyReturnedStatus()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            // The old table's CHECK constraint allowed only Completed/Returned; if the rebuild had not
            // happened this write would fail outright.
            using (DbConnection conn = _db.OpenConnection())
                Exec(conn, "UPDATE sales SET status='PartiallyReturned' WHERE id=7");

            Assert.That(new SqliteSaleStore(_db).GetById(7).Status, Is.EqualTo(SaleStatus.PartiallyReturned));
        }

        [Test]
        public void Upgrade_IsIdempotent()
        {
            var initializer = new DatabaseInitializer(_db);
            initializer.ApplySchemaAndSeed();
            initializer.ApplySchemaAndSeed();   // a second startup must not rebuild or renumber anything

            Assert.That(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sales")), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(Scalar("SELECT sale_number FROM sales WHERE id=7")), Is.EqualTo(7));
            Assert.That(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sale_lines WHERE sale_id=7")), Is.EqualTo(1));
        }

        private static void Exec(DbConnection conn, string sql)
        {
            using (DbCommand cmd = conn.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }

        private object Scalar(string sql)
        {
            using (DbConnection conn = _db.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                object v = cmd.ExecuteScalar();
                return v == DBNull.Value ? null : v;
            }
        }
    }
}
