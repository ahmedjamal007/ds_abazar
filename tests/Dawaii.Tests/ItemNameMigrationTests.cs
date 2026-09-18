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
    /// Upgrade path for a pharmacy whose catalog still carries three names. <c>name_ar</c> never held
    /// Arabic — it was the trade name on the box — and <c>name_en</c> had become either a copy of it or
    /// an empty column, so V2.0 keeps two names: the trade name and the scientific one.
    ///
    /// The whole risk of the change is the catalog: <c>name_ar</c> was the NOT NULL column every screen
    /// read the drug's name from, so dropping it without moving the names first would empty the shelf.
    /// These tests pin the three cases that decide whether a real pharmacy survives the upgrade.
    /// </summary>
    [TestFixture]
    public class ItemNameMigrationTests
    {
        private string _path;
        private SqliteConnectionFactory _db;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_name_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);
            CreateThreeNameDatabase();
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

        /// <summary>The pre-V2.0 shape, with the three rows a real catalog turned out to contain: a
        /// duplicated name, an empty <c>name_en</c>, and the rare pair where the two genuinely differ.</summary>
        private void CreateThreeNameDatabase()
        {
            using (DbConnection conn = _db.OpenConnection())
            {
                Exec(conn, "CREATE TABLE settings (key_name TEXT PRIMARY KEY, value TEXT)");
                Exec(conn,
                    "CREATE TABLE items (" +
                    " id INTEGER PRIMARY KEY AUTOINCREMENT, name_ar TEXT NOT NULL, name_en TEXT, generic_name TEXT," +
                    " units_per_strip INTEGER NOT NULL DEFAULT 1, strips_per_box INTEGER NOT NULL DEFAULT 1," +
                    " purchase_price REAL NOT NULL DEFAULT 0, selling_price REAL," +
                    " min_quantity INTEGER NOT NULL DEFAULT 0, max_quantity INTEGER NOT NULL DEFAULT 0," +
                    " expiry_warn_days INTEGER, substitute_of INTEGER, is_active INTEGER NOT NULL DEFAULT 1," +
                    " created_at TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                    " updated_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn, "CREATE INDEX ix_items_name_ar ON items(name_ar)");

                // 1: the common case — the same string filed twice.
                Exec(conn, "INSERT INTO items (name_ar, name_en, generic_name, selling_price) " +
                           "VALUES ('polymol', 'polymol', 'paracetamol', 5)");
                // 2: name_en never filled in. Its only name lives in the column about to be dropped.
                Exec(conn, "INSERT INTO items (name_ar, name_en, generic_name, selling_price) " +
                           "VALUES ('varoxa tablets', '', 'rivaroxaban', 7)");
                // 3: the two differ — name_en is the corrected spelling and must win.
                Exec(conn, "INSERT INTO items (name_ar, name_en, generic_name, selling_price) " +
                           "VALUES ('LAMIDINE 100MG TABLETS', 'LAMIVUDINE 100MG', 'lamivudine', 9)");
                // 4: no scientific name at all — the display falls back to the trade name alone.
                Exec(conn, "INSERT INTO items (name_ar, name_en, generic_name, selling_price) " +
                           "VALUES ('fourts', NULL, NULL, 3)");
            }
        }

        private Item[] Migrate()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();
            return new SqliteItemRepository(_db).GetAll(activeOnly: false).OrderBy(i => i.Id).ToArray();
        }

        [Test]
        public void Upgrade_DropsTheThirdName()
        {
            Migrate();
            Assert.That(ColumnNames(), Does.Not.Contain("name_ar"),
                "the third name is what the upgrade exists to remove");
            Assert.That(ColumnNames(), Contains.Item("name_en").And.Contains("generic_name"));
        }

        [Test]
        public void Upgrade_LeavesEveryDrugWithATradeName()
        {
            Item[] items = Migrate();
            Assert.That(items.Select(i => i.NameEn), Is.All.Not.Null.And.All.Not.Empty,
                "an item that lost its name is an item the pharmacy can no longer sell");
        }

        [Test]
        public void Upgrade_TakesTheOldNameOnlyWhenTheTradeNameWasEmpty()
        {
            Item[] items = Migrate();
            Assert.That(items[0].NameEn, Is.EqualTo("polymol"), "the duplicate collapses to one");
            Assert.That(items[1].NameEn, Is.EqualTo("varoxa tablets"), "an empty trade name inherits the old one");
            Assert.That(items[2].NameEn, Is.EqualTo("LAMIVUDINE 100MG"), "where both exist the corrected one wins");
            Assert.That(items[3].NameEn, Is.EqualTo("fourts"));
        }

        [Test]
        public void Upgrade_KeepsTheScientificNameAndWritesThePair()
        {
            Item[] items = Migrate();
            Assert.That(items[0].DisplayName, Is.EqualTo("polymol / paracetamol"));
            Assert.That(items[1].DisplayName, Is.EqualTo("varoxa tablets / rivaroxaban"));
            Assert.That(items[3].DisplayName, Is.EqualTo("fourts"), "no scientific name — the trade name stands alone");
        }

        [Test]
        public void Upgrade_IsIdempotent()
        {
            Migrate();
            Item[] again = Migrate();
            Assert.That(again[1].NameEn, Is.EqualTo("varoxa tablets"));
            Assert.That(again.Length, Is.EqualTo(4), "a second start must not duplicate or drop rows");
        }

        [Test]
        public void Upgrade_KeepsTheRestOfTheRow()
        {
            Item[] items = Migrate();
            Assert.That(items[2].SellingPrice, Is.EqualTo(9m), "the rebuild must carry every other column across");
            Assert.That(items.Select(i => i.Id), Is.EqualTo(new[] { 1, 2, 3, 4 }),
                "ids are referenced by stock, sales and barcodes — the rebuild must preserve them");
        }

        private string[] ColumnNames()
        {
            using (DbConnection conn = _db.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(items)";
                using (DbDataReader r = cmd.ExecuteReader())
                {
                    var names = new System.Collections.Generic.List<string>();
                    while (r.Read()) names.Add(r.GetString(1));
                    return names.ToArray();
                }
            }
        }

        private static void Exec(DbConnection conn, string sql)
        {
            using (DbCommand cmd = conn.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        }
    }
}
