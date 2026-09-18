using System;
using System.Data.Common;
using System.IO;
using System.Linq;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Upgrade path for pharmacies running an earlier version, where batches stored one per-single-unit
    /// cost and selling prices were derived from a category's profit multiplier. V1.7 puts a BOX purchase
    /// price and a BOX selling price on every batch and deletes categories outright.
    ///
    /// The migration must scale the old per-unit figures back up into box prices so nobody's prices move,
    /// and must remove <c>items.category_id</c> — leaving that column behind would point a foreign key at
    /// the dropped categories table and break every later insert.
    /// </summary>
    [TestFixture]
    public class BoxPricingMigrationTests
    {
        private string _path;
        private SqliteConnectionFactory _db;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_box_" + Guid.NewGuid().ToString("N") + ".db");
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

        /// <summary>Builds the pre-V1.7 shape: categories with a multiplier, items pointing at them, and
        /// batches carrying a single per-unit purchase_price.</summary>
        private void CreateOldShapedDatabase()
        {
            using (DbConnection conn = _db.OpenConnection())
            {
                Exec(conn, "CREATE TABLE settings (key_name TEXT PRIMARY KEY, value TEXT)");
                Exec(conn, "CREATE TABLE categories (id INTEGER PRIMARY KEY AUTOINCREMENT, name_ar TEXT NOT NULL UNIQUE, name_en TEXT, profit_multiplier REAL NOT NULL DEFAULT 1.5)");
                Exec(conn,
                    "CREATE TABLE items (" +
                    " id INTEGER PRIMARY KEY AUTOINCREMENT, name_ar TEXT NOT NULL, name_en TEXT, generic_name TEXT," +
                    " category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL," +
                    " units_per_strip INTEGER NOT NULL DEFAULT 1, strips_per_box INTEGER NOT NULL DEFAULT 1," +
                    " purchase_price REAL NOT NULL DEFAULT 0, selling_price REAL," +
                    " min_quantity INTEGER NOT NULL DEFAULT 0, max_quantity INTEGER NOT NULL DEFAULT 0," +
                    " expiry_warn_days INTEGER, substitute_of INTEGER, is_active INTEGER NOT NULL DEFAULT 1," +
                    " created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                    " updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn,
                    "CREATE TABLE stock_batches (" +
                    " id INTEGER PRIMARY KEY AUTOINCREMENT, item_id INTEGER NOT NULL," +
                    " quantity_units INTEGER NOT NULL DEFAULT 0, expiry_date TEXT," +
                    " purchase_price REAL NOT NULL DEFAULT 0," +
                    " received_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                    " is_disposed INTEGER NOT NULL DEFAULT 0)");

                Exec(conn, "INSERT INTO categories (name_ar, profit_multiplier) VALUES ('مسكنات', 1.25)");
                // 4 strips × 1 unit = 4 units per box; cost 800/unit, sells 1000/unit.
                Exec(conn,
                    "INSERT INTO items (name_ar, category_id, units_per_strip, strips_per_box, purchase_price, selling_price) " +
                    "VALUES ('بنادول', 1, 1, 4, 800, 1000)");
                Exec(conn, "INSERT INTO stock_batches (item_id, quantity_units, expiry_date, purchase_price) VALUES (1, 40, '2027-01-01', 800)");
            }
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

        [Test]
        public void Upgrade_ScalesPerUnitPricesUpIntoBoxPrices()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            StockBatch batch = new SqliteStockRepository(_db).GetBatches(1).Single();

            Assert.That(batch.StripsPerBox, Is.EqualTo(4), "packaging carried over from the item");
            Assert.That(batch.BoxPurchasePrice, Is.EqualTo(3200m), "800/unit × 4 units per box");
            Assert.That(batch.BoxSellingPrice, Is.EqualTo(4000m), "1000/unit × 4 units per box");

            // And the derived figures land back exactly where the pharmacy was already trading.
            Assert.That(batch.StripPurchasePrice, Is.EqualTo(800m));
            Assert.That(batch.StripSellingPrice, Is.EqualTo(1000m));
            Assert.That(batch.PurchasePrice, Is.EqualTo(800m), "per-unit cost is unchanged, so FEFO costing is unchanged");
        }

        [Test]
        public void Upgrade_DropsCategoriesTableAndTheItemsColumn()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            Assert.That(Convert.ToInt32(Scalar(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='categories'")),
                Is.Zero, "the categories table is gone");
            Assert.That(Convert.ToInt32(Scalar(
                    "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name='category_id'")),
                Is.Zero, "items no longer carries a foreign key to a table that does not exist");
        }

        [Test]
        public void Upgrade_LeavesItemsInsertable_WithForeignKeysOn()
        {
            // The regression this guards: with category_id still referencing a dropped table, SQLite
            // fails every later INSERT with "no such table: main.categories".
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            var items = new SqliteItemRepository(_db);
            Assert.DoesNotThrow(() => items.Add(new Item
            {
                NameEn = "صنف بعد الترقية", UnitsPerStrip = 1, StripsPerBox = 4, IsActive = true
            }));
            Assert.That(items.Search("صنف بعد الترقية"), Is.Not.Empty);
        }

        [Test]
        public void Upgrade_IsIdempotent_AndKeepsEditedBoxPrices()
        {
            var init = new DatabaseInitializer(_db);
            init.ApplySchemaAndSeed();

            var stock = new SqliteStockRepository(_db);
            StockBatch batch = stock.GetBatches(1).Single();
            batch.BoxSellingPrice = 4500m;
            stock.UpdateBatch(batch);

            Assert.DoesNotThrow(() => init.ApplySchemaAndSeed());

            Assert.That(stock.GetBatches(1).Single().BoxSellingPrice, Is.EqualTo(4500m),
                "a later startup must not overwrite a price the pharmacy corrected");
        }
    }
}
