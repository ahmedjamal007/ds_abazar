using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Dawaii.Core.Security;
using MySql.Data.MySqlClient;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Ensures the database exists and is up to date for whichever backend is active. For SQLite it
    /// creates the file; for MySQL it assumes the installer created the (empty) database and applies
    /// the schema/seed into it. Both schema/seed scripts are idempotent, and column additions from
    /// older versions are applied by <see cref="ApplyMigrations"/>.
    /// </summary>
    public class DatabaseInitializer
    {
        private readonly IDbConnectionFactory _factory;

        public DatabaseInitializer(IDbConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public bool CanConnect(out string error)
        {
            error = null;
            try
            {
                using (var conn = _factory.OpenConnection())
                    return conn.State == System.Data.ConnectionState.Open;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void ApplySchemaAndSeed()
        {
            // A database is new only when it did not have its settings table before schema setup.
            // Do not infer this from an empty items table: an existing pharmacy may intentionally
            // have deleted every catalog item, and its demo data must never be resurrected.
            bool databaseWasNew = !TableExists("settings");
            if (_factory.Kind == DbKind.Sqlite)
            {
                string dir = Path.GetDirectoryName(_factory.SqliteFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string schema = ReadResource("Sql.schema.sql");
                PrepareDataForSchema();
                RunScript(schema);
                ApplyMigrations(schema);
                RunScript(ReadResource("Sql.seed.sql"));
                SeedDemoDataOnce("Sql.seed.demo.sql", databaseWasNew);
            }
            else
            {
                string schema = ReadResource("Sql.schema.mysql.sql");
                PrepareDataForSchema();
                RunScript(schema);
                ApplyMigrations(schema);
                RunScript(ReadResource("Sql.seed.mysql.sql"));
                SeedDemoDataOnce("Sql.seed.demo.mysql.sql", databaseWasNew);
            }
        }

        /// <summary>
        /// Makes the data satisfy the rules the schema script is about to impose, before it runs.
        ///
        /// The script creates <c>ux_item_codes_item</c>, and every database written before V1.9 holds
        /// several barcodes for some items. Creating that index over such data fails, and because the
        /// whole script runs as one statement batch the failure took the entire schema run with it —
        /// the pharmacy could not open the program at all, only an error naming item_codes. Dropping
        /// the surplus codes here means the index the script then creates always succeeds.
        ///
        /// Nothing here may assume a table exists: on a brand-new database none of them do yet.
        /// </summary>
        private void PrepareDataForSchema()
        {
            if (TableExists("item_codes")) KeepOneCodePerItem();
        }

        /// <summary>
        /// Inserts the demo catalog (sample medicines, starter stock, sample customers) exactly once,
        /// on a brand-new database. The 'demo_seeded' settings flag records that this ran, so demo
        /// rows the user later deletes are never re-inserted on the next startup (they used to be,
        /// because the idempotent seed re-created any missing id — and the batch inserts, having no
        /// fixed ids, added duplicate stock on every run). Databases created before the flag existed
        /// already contain the demo, so they are just marked without re-seeding.
        /// </summary>
        private void SeedDemoDataOnce(string resource, bool databaseWasNew)
        {
            if (_factory.Scalar("SELECT value FROM settings WHERE key_name='demo_seeded'") != null)
                return;

            if (databaseWasNew) RunScript(ReadResource(resource));

            string ignore = _factory.Kind == DbKind.MySql ? "INSERT IGNORE" : "INSERT OR IGNORE";
            _factory.Execute($"{ignore} INTO settings (key_name, value) VALUES ('demo_seeded','1')");
        }

        /// <summary>
        /// Brings a database created by an older version up to this one (both backends): columns the
        /// new code reads are added, data is reshaped where a feature changed meaning, and anything
        /// this version no longer defines is dropped. <paramref name="schemaScript"/> is the schema
        /// this build just applied — it is the authority on what "no longer defined" means.
        /// </summary>
        private void ApplyMigrations(string schemaScript)
        {
            EnsureColumn("items", "substitute_of", "INTEGER");
            EnsureColumn("items", "max_quantity", "INTEGER");     // V1.3 "مخزون كامل" ceiling
            EnsureColumn("sales", "payment_method", "TEXT");      // V1.3 كاش/بنكك/فوري
            // V2.3: how a supplier was paid. NULL on rows written before this build, which the
            // drawer reconciliation reads as cash — the way distributors were in fact being paid.
            EnsureColumn("supplier_payments", "payment_method", "TEXT");
            RebuildItemsTable();                                  // nullable price + drop category_id
            MigrateToBoxPricedBatches();                          // V1.7 box prices live on the batch
            MigrateToIntegerInvoiceNumbers();                     // V1.8 int invoice no. + partial returns
            MigrateToPrivilegedEmployeeRole();                    // V1.8 third role: موظف ذو امتيازات
            MigrateToSingleCodePerItem();                         // V1.9 one barcode per item
            DropItemTradeNameDuplicate();                         // V2.0 two names: trade + scientific
            DropObsoleteObjects(schemaScript);                    // price_history, categories, …

            // After every rebuild of the items table above, which copy a fixed column list and would
            // drop a column added before them.
            EnsureColumn("items", "manual_price", "INTEGER NOT NULL DEFAULT 0");   // V2.3 إدارة الأسعار

            // The POS briefly stored أوكاش sales with the Arabic word as the code (the label/code pair
            // was entered backwards). Idempotent; PaymentMethods.Normalize also reads the old form.
            if (TableExists("sales"))
                _factory.Execute("UPDATE sales SET payment_method='Ocash' WHERE payment_method IN ('اوكاش','أوكاش')");
        }

        /// <summary>
        /// Drops every table (and, on SQLite, every standalone index) that this version's schema no
        /// longer defines — the leftovers of retired features such as the price list and categories.
        /// A restored backup from any older version therefore ends up shaped exactly like a fresh
        /// install, while the pharmacy's own rows — items, stock, sales, debts, staff — are untouched,
        /// because every table holding them is still in the schema.
        ///
        /// The keep-list is read out of the schema script this same build shipped and applied moments
        /// ago, so it cannot drift from the code the way a hand-written list of dead tables does. If
        /// that parse returns implausibly few objects the prune is skipped altogether: on a live
        /// pharmacy's database, doing nothing is always better than guessing.
        /// </summary>
        private void DropObsoleteObjects(string schemaScript)
        {
            HashSet<string> defined = SchemaObjectNames(schemaScript);
            if (defined.Count < 15) return;                       // parse failed — leave the data alone

            var doomed = new List<string>();
            foreach (string table in ListNames(
                _factory.Kind == DbKind.MySql
                    ? "SELECT table_name FROM information_schema.tables " +
                      "WHERE table_schema = DATABASE() AND table_type='BASE TABLE'"
                    : "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"))
                if (!defined.Contains(table)) doomed.Add(table);

            // Obsolete tables can reference each other, so a drop may have to wait for its dependant.
            // Three passes clear any chain either backend will have; whatever survives is left in place.
            for (int pass = 0; pass < 3 && doomed.Count > 0; pass++)
            {
                var left = new List<string>();
                foreach (string table in doomed)
                    if (!TryExecute("DROP TABLE IF EXISTS " + Quote(table))) left.Add(table);
                doomed = left;
            }

            if (_factory.Kind == DbKind.MySql) return;            // MySQL declares its indexes inline

            foreach (string index in ListNames(
                "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'"))
                if (!defined.Contains(index)) TryExecute("DROP INDEX IF EXISTS " + Quote(index));
        }

        /// <summary>Names of the tables and indexes a schema script creates.</summary>
        private static HashSet<string> SchemaObjectNames(string schemaScript)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(schemaScript,
                @"CREATE\s+(?:UNIQUE\s+)?(?:TABLE|INDEX)\s+(?:IF\s+NOT\s+EXISTS\s+)?[`""\[]?(\w+)",
                RegexOptions.IgnoreCase))
                names.Add(m.Groups[1].Value);
            return names;
        }

        private List<string> ListNames(string sql)
        {
            var names = new List<string>();
            using (var conn = _factory.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) names.Add(Convert.ToString(r[0]));
            }
            return names;
        }

        private string Quote(string identifier)
            => _factory.Kind == DbKind.MySql ? "`" + identifier + "`" : "\"" + identifier + "\"";

        /// <summary>
        /// V1.7 moved pricing onto the stock batch: the admin types a BOX purchase price and a BOX
        /// selling price per shipment and everything else divides down from them. Categories, profit
        /// multipliers and the price list are gone.
        ///
        /// Older batches stored only a per-single-unit <c>purchase_price</c>, so this multiplies that
        /// back up into a box price using the item's packaging, and seeds the box SELLING price from the
        /// item's existing per-unit selling price. Every pharmacy therefore keeps the exact prices it was
        /// already selling at — nothing re-prices itself on upgrade.
        /// </summary>
        private void MigrateToBoxPricedBatches()
        {
            if (ColumnExists("stock_batches", "box_purchase_price")) return;

            bool mysql = _factory.Kind == DbKind.MySql;
            string money = mysql ? "DECIMAL(12,2) NOT NULL DEFAULT 0" : "REAL NOT NULL DEFAULT 0";
            string count = mysql ? "INT NOT NULL DEFAULT 1" : "INTEGER NOT NULL DEFAULT 1";
            string text = mysql ? "VARCHAR(100) NULL" : "TEXT";

            _factory.Execute($"ALTER TABLE stock_batches ADD COLUMN batch_number {text}");
            _factory.Execute($"ALTER TABLE stock_batches ADD COLUMN strips_per_box {count}");
            _factory.Execute($"ALTER TABLE stock_batches ADD COLUMN units_per_strip {count}");
            _factory.Execute($"ALTER TABLE stock_batches ADD COLUMN box_purchase_price {money}");
            _factory.Execute($"ALTER TABLE stock_batches ADD COLUMN box_selling_price {money}");

            // Carry each item's packaging onto its batches, then scale the old per-unit prices up to
            // box prices. Items never priced (NULL selling) leave the box selling price at 0.
            _factory.Execute(
                "UPDATE stock_batches SET " +
                "  strips_per_box  = COALESCE((SELECT i.strips_per_box  FROM items i WHERE i.id = stock_batches.item_id), 1), " +
                "  units_per_strip = COALESCE((SELECT i.units_per_strip FROM items i WHERE i.id = stock_batches.item_id), 1)");
            _factory.Execute(
                "UPDATE stock_batches SET " +
                "  box_purchase_price = purchase_price * strips_per_box * units_per_strip, " +
                "  box_selling_price  = COALESCE((SELECT i.selling_price FROM items i WHERE i.id = stock_batches.item_id), 0) " +
                "                       * strips_per_box * units_per_strip");

            // The old per-unit column is now derived. Dropping it is a tidy-up, not a requirement —
            // older SQLite builds cannot DROP COLUMN, and an unread leftover column harms nothing.
            TryExecute("ALTER TABLE stock_batches DROP COLUMN purchase_price");
        }

        /// <summary>
        /// V1.8 changed two things about the sales table that have to move together:
        ///  * <c>sale_number</c> used to be the text "yyyyMMdd-id". The return screen is right-to-left,
        ///    so Windows renders that reversed ("7-20260801") and staff could not type back the number
        ///    printed on the receipt they were holding — no invoice could be found. It is now a plain
        ///    integer equal to the row id, which reads the same in either direction.
        ///  * returns became partial (5 of 50 boxes), so the status gained 'PartiallyReturned' and the
        ///    invoice carries running <c>returned_total</c>/<c>returned_cost</c>.
        /// Old invoice numbers are replaced by the id — nothing else referenced them, and the id is what
        /// receipts printed alongside the composite number anyway. Sales already fully returned are
        /// backfilled as having refunded everything, so reports keep reading them the same way.
        /// </summary>
        private void MigrateToIntegerInvoiceNumbers()
        {
            if (ColumnExists("sales", "returned_total")) return;   // already current

            if (_factory.Kind == DbKind.MySql)
            {
                _factory.Execute("ALTER TABLE sales ADD COLUMN returned_total DECIMAL(12,2) NOT NULL DEFAULT 0");
                _factory.Execute("ALTER TABLE sales ADD COLUMN returned_cost  DECIMAL(12,2) NOT NULL DEFAULT 0");
                _factory.Execute("ALTER TABLE sales MODIFY status " +
                                 "ENUM('Completed','PartiallyReturned','Returned') NOT NULL DEFAULT 'Completed'");
                // Retype only after the values are already plain integers, or the cast truncates them.
                _factory.Execute("UPDATE sales SET sale_number = CAST(id AS CHAR)");
                _factory.Execute("ALTER TABLE sales MODIFY sale_number INT UNSIGNED NULL");
                _factory.Execute("UPDATE sales SET returned_total = total, returned_cost = cost_total WHERE status='Returned'");
                return;
            }

            // SQLite cannot widen a CHECK constraint or retype a column, so the table is rebuilt once.
            // Foreign keys are off on this connection only; sale_lines/returns/debt_transactions keep
            // pointing at "sales" across the rename because every id is copied unchanged.
            const string cols =
                "id, sale_number, user_id, customer_id, sale_type, payment_method, subtotal, discount, " +
                "total, cost_total, returned_total, returned_cost, status, terminal, created_at";
            const string selectCols =
                "id, id, user_id, customer_id, sale_type, payment_method, subtotal, discount, " +
                "total, cost_total, " +
                "CASE WHEN status='Returned' THEN total      ELSE 0 END, " +
                "CASE WHEN status='Returned' THEN cost_total ELSE 0 END, " +
                "status, terminal, created_at";
            using (var conn = _factory.OpenConnection())
            {
                Exec(conn, "PRAGMA foreign_keys=OFF");
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        Exec(conn,
                            "CREATE TABLE sales_rebuild (" +
                            "  id             INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "  sale_number    INTEGER UNIQUE," +
                            "  user_id        INTEGER NOT NULL REFERENCES users(id)," +
                            "  customer_id    INTEGER REFERENCES customers(id)," +
                            "  sale_type      TEXT NOT NULL DEFAULT 'Cash' CHECK (sale_type IN ('Cash','Credit'))," +
                            "  payment_method TEXT," +
                            "  subtotal       REAL NOT NULL DEFAULT 0," +
                            "  discount       REAL NOT NULL DEFAULT 0," +
                            "  total          REAL NOT NULL DEFAULT 0," +
                            "  cost_total     REAL NOT NULL DEFAULT 0," +
                            "  returned_total REAL NOT NULL DEFAULT 0," +
                            "  returned_cost  REAL NOT NULL DEFAULT 0," +
                            "  status         TEXT NOT NULL DEFAULT 'Completed' " +
                            "                 CHECK (status IN ('Completed','PartiallyReturned','Returned'))," +
                            "  terminal       TEXT," +
                            "  created_at     TEXT NOT NULL DEFAULT (datetime('now','localtime')))", tx);
                        Exec(conn, $"INSERT INTO sales_rebuild ({cols}) SELECT {selectCols} FROM sales", tx);
                        Exec(conn, "DROP TABLE sales", tx);
                        Exec(conn, "ALTER TABLE sales_rebuild RENAME TO sales", tx);
                        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_sales_created  ON sales(created_at)", tx);
                        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_sales_customer ON sales(customer_id)", tx);
                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
                Exec(conn, "PRAGMA foreign_keys=ON");
            }
        }

        /// <summary>
        /// V1.9 gives an item exactly one barcode. Stock entry used to capture one barcode per unit, so
        /// older databases hold a list per item; the oldest row is kept — that is the manufacturer
        /// barcode the drug was first scanned in with — and the rest are dropped, releasing those codes
        /// for other items. The unique index then makes the rule the database's, so no future write can
        /// re-create a list. Codes deleted here were duplicates of a drug that is already findable by its
        /// remaining code, so nothing becomes unscannable.
        /// </summary>
        private void MigrateToSingleCodePerItem()
        {
            KeepOneCodePerItem();
            // SQLite already has the index from the schema script; MySQL declares it inside its CREATE
            // TABLE, which does nothing to a table that already exists, so this is what adds it there.
            // Fails harmlessly once present (neither backend has a portable IF NOT EXISTS here).
            TryExecute("CREATE UNIQUE INDEX ux_item_codes_item ON item_codes(item_id)");
        }

        /// <summary>Drops every barcode but the oldest one per item. Safe to run when there are none.</summary>
        private void KeepOneCodePerItem()
        {
            // The extra SELECT layer materialises the ids first: MySQL refuses a subquery that reads the
            // same table a DELETE is writing, SQLite does not care either way.
            _factory.Execute(
                "DELETE FROM item_codes WHERE id NOT IN " +
                "(SELECT id FROM (SELECT MIN(id) AS id FROM item_codes GROUP BY item_id) keep)");
        }

        /// <summary>
        /// V1.8 added a third role, "موظف ذو امتيازات" (<c>FullEmployee</c>) — a cashier the manager
        /// trusts with the stockroom. Databases created before it constrain the column to Admin/Cashier
        /// (a CHECK on SQLite, an ENUM on MySQL), so promoting an employee would be rejected by the
        /// database itself. This widens the column; every existing account keeps the role it has.
        /// </summary>
        private void MigrateToPrivilegedEmployeeRole()
        {
            if (_factory.Kind == DbKind.MySql)
            {
                object type = _factory.Scalar(
                    "SELECT column_type FROM information_schema.columns " +
                    "WHERE table_schema = DATABASE() AND table_name='users' AND column_name='role'");
                if (Convert.ToString(type).IndexOf("FullEmployee", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;                                        // already current
                _factory.Execute("ALTER TABLE users MODIFY role " +
                                 "ENUM('Admin','Cashier','FullEmployee') NOT NULL DEFAULT 'Cashier'");
                return;
            }

            object ddl = _factory.Scalar("SELECT sql FROM sqlite_master WHERE type='table' AND name='users'");
            string sql = Convert.ToString(ddl);
            if (string.IsNullOrEmpty(sql) ||
                sql.IndexOf("FullEmployee", StringComparison.OrdinalIgnoreCase) >= 0)
                return;                                            // fresh database, or already current

            // SQLite cannot widen a CHECK constraint, so the table is rebuilt once. Foreign keys are off
            // on this connection only; sales/audit_log keep pointing at "users" across the rename because
            // every id is copied unchanged.
            const string cols = "id, username, password_hash, full_name, role, is_active, created_at";
            using (var conn = _factory.OpenConnection())
            {
                Exec(conn, "PRAGMA foreign_keys=OFF");
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        Exec(conn,
                            "CREATE TABLE users_rebuild (" +
                            "  id            INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "  username      TEXT NOT NULL UNIQUE," +
                            "  password_hash TEXT NOT NULL," +
                            "  full_name     TEXT," +
                            "  role          TEXT NOT NULL DEFAULT 'Cashier' " +
                            "                CHECK (role IN ('Admin','Cashier','FullEmployee'))," +
                            "  is_active     INTEGER NOT NULL DEFAULT 1," +
                            "  created_at    TEXT NOT NULL DEFAULT (datetime('now','localtime')))", tx);
                        Exec(conn, $"INSERT INTO users_rebuild ({cols}) SELECT {cols} FROM users", tx);
                        Exec(conn, "DROP TABLE users", tx);
                        Exec(conn, "ALTER TABLE users_rebuild RENAME TO users", tx);
                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
                Exec(conn, "PRAGMA foreign_keys=ON");
            }
        }

        /// <summary>
        /// Brings the items table to its current shape. Two old-version problems are fixed in one pass,
        /// because on SQLite both need the same table rebuild:
        ///  * <c>selling_price</c> used to be NOT NULL DEFAULT 0; unpriced items are stored as NULL now.
        ///  * <c>category_id</c> referenced the categories table, which V1.7 drops. Leaving the column
        ///    behind would point a foreign key at a table that no longer exists, and SQLite fails every
        ///    later INSERT into items with "no such table: main.categories".
        /// MySQL can alter/drop in place. SQLite cannot, so the table is rebuilt once (FKs off on this
        /// connection only; child tables keep referencing "items" across the rename). Prices, including
        /// explicit zeros, are copied verbatim — no pharmacy's prices change on upgrade.
        /// </summary>
        private void RebuildItemsTable()
        {
            bool sellingPriceIsNotNull, hasCategory = ColumnExists("items", "category_id");

            if (_factory.Kind == DbKind.MySql)
            {
                object isNullable = _factory.Scalar(
                    "SELECT IS_NULLABLE FROM information_schema.columns " +
                    "WHERE table_schema = DATABASE() AND table_name='items' AND column_name='selling_price'");
                sellingPriceIsNotNull = string.Equals(Convert.ToString(isNullable), "NO", StringComparison.OrdinalIgnoreCase);

                if (sellingPriceIsNotNull)
                    _factory.Execute("ALTER TABLE items MODIFY selling_price DECIMAL(12,2) NULL DEFAULT NULL");

                if (hasCategory)
                {
                    // The FK has to go before the column it constrains.
                    object fk = _factory.Scalar(
                        "SELECT constraint_name FROM information_schema.key_column_usage " +
                        "WHERE table_schema = DATABASE() AND table_name='items' AND column_name='category_id' " +
                        "AND referenced_table_name IS NOT NULL");
                    string name = Convert.ToString(fk);
                    if (!string.IsNullOrEmpty(name))
                        TryExecute($"ALTER TABLE items DROP FOREIGN KEY `{name}`");
                    TryExecute("ALTER TABLE items DROP INDEX ix_items_category");
                    _factory.Execute("ALTER TABLE items DROP COLUMN category_id");
                }
                return;
            }

            sellingPriceIsNotNull = SqliteColumnIsNotNull("items", "selling_price");
            if (!sellingPriceIsNotNull && !hasCategory) return;   // already current

            const string cols =
                "id, name_ar, name_en, generic_name, units_per_strip, strips_per_box, " +
                "purchase_price, selling_price, min_quantity, max_quantity, expiry_warn_days, " +
                "substitute_of, is_active, created_at, updated_at";
            // max_quantity may hold NULLs when it was added by EnsureColumn (plain nullable INTEGER)
            // on a database older than V1.3 — the rebuilt column is NOT NULL, so coalesce on copy.
            const string selectCols =
                "id, name_ar, name_en, generic_name, units_per_strip, strips_per_box, " +
                "purchase_price, selling_price, min_quantity, COALESCE(max_quantity, 0), expiry_warn_days, " +
                "substitute_of, is_active, created_at, updated_at";
            using (var conn = _factory.OpenConnection())
            {
                Exec(conn, "PRAGMA foreign_keys=OFF");
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        Exec(conn,
                            "CREATE TABLE items_rebuild (" +
                            "  id               INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "  name_ar          TEXT NOT NULL," +
                            "  name_en          TEXT," +
                            "  generic_name     TEXT," +
                            "  units_per_strip  INTEGER NOT NULL DEFAULT 1," +
                            "  strips_per_box   INTEGER NOT NULL DEFAULT 1," +
                            "  purchase_price   REAL NOT NULL DEFAULT 0," +
                            "  selling_price    REAL," +
                            "  min_quantity     INTEGER NOT NULL DEFAULT 0," +
                            "  max_quantity     INTEGER NOT NULL DEFAULT 0," +
                            "  expiry_warn_days INTEGER," +
                            "  substitute_of    INTEGER REFERENCES items(id)," +
                            "  is_active        INTEGER NOT NULL DEFAULT 1," +
                            "  created_at       TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                            "  updated_at       TEXT NOT NULL DEFAULT (datetime('now','localtime')))", tx);
                        Exec(conn, $"INSERT INTO items_rebuild ({cols}) SELECT {selectCols} FROM items", tx);
                        Exec(conn, "DROP TABLE items", tx);
                        Exec(conn, "ALTER TABLE items_rebuild RENAME TO items", tx);
                        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_items_name_ar ON items(name_ar)", tx);
                        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_items_name_en ON items(name_en)", tx);
                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
                Exec(conn, "PRAGMA foreign_keys=ON");
            }
        }

        /// <summary>
        /// V2.0 leaves a drug with two names instead of three. <c>name_ar</c> never actually held
        /// Arabic — it was the trade name printed on the box, and <c>name_en</c> had drifted into
        /// being a copy of it (or was simply left empty), so the catalogue stored the same string
        /// twice under two headings and the screens had to guess which to show. A drug is now
        /// written "name / scientific name" from <c>name_en</c> and <c>generic_name</c>.
        ///
        /// No name is thrown away. An item whose <c>name_en</c> was never filled in inherits its
        /// <c>name_ar</c>; where both were present <c>name_en</c> wins, because it is the later and
        /// corrected spelling of the two (the older column kept whatever typo it was first entered
        /// with). Only then is the column dropped.
        ///
        /// SQLite gets a full table rebuild rather than a DROP COLUMN: the old column is NOT NULL, so
        /// on a build too old to drop it every future insert — which no longer supplies it — would
        /// fail the constraint. The rebuild also moves NOT NULL onto <c>name_en</c>, where it now
        /// belongs, which no ALTER could have done.
        /// </summary>
        private void DropItemTradeNameDuplicate()
        {
            if (!TableExists("items") || !ColumnExists("items", "name_ar")) return;

            _factory.Execute(
                "UPDATE items SET name_en = name_ar WHERE name_en IS NULL OR TRIM(name_en) = ''");

            if (_factory.Kind == DbKind.MySql)
            {
                _factory.Execute("ALTER TABLE items MODIFY name_en VARCHAR(200) NOT NULL");
                TryExecute("ALTER TABLE items DROP INDEX ix_items_name_ar");
                _factory.Execute("ALTER TABLE items DROP COLUMN name_ar");
                return;
            }

            const string cols =
                "id, name_en, generic_name, units_per_strip, strips_per_box, " +
                "purchase_price, selling_price, min_quantity, max_quantity, expiry_warn_days, " +
                "substitute_of, is_active, created_at, updated_at";
            using (var conn = _factory.OpenConnection())
            {
                Exec(conn, "PRAGMA foreign_keys=OFF");
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        Exec(conn,
                            "CREATE TABLE items_rebuild (" +
                            "  id               INTEGER PRIMARY KEY AUTOINCREMENT," +
                            "  name_en          TEXT NOT NULL," +
                            "  generic_name     TEXT," +
                            "  units_per_strip  INTEGER NOT NULL DEFAULT 1," +
                            "  strips_per_box   INTEGER NOT NULL DEFAULT 1," +
                            "  purchase_price   REAL NOT NULL DEFAULT 0," +
                            "  selling_price    REAL," +
                            "  min_quantity     INTEGER NOT NULL DEFAULT 0," +
                            "  max_quantity     INTEGER NOT NULL DEFAULT 0," +
                            "  expiry_warn_days INTEGER," +
                            "  substitute_of    INTEGER REFERENCES items(id)," +
                            "  is_active        INTEGER NOT NULL DEFAULT 1," +
                            "  created_at       TEXT NOT NULL DEFAULT (datetime('now','localtime'))," +
                            "  updated_at       TEXT NOT NULL DEFAULT (datetime('now','localtime')))", tx);
                        Exec(conn, $"INSERT INTO items_rebuild ({cols}) SELECT {cols} FROM items", tx);
                        Exec(conn, "DROP TABLE items", tx);
                        Exec(conn, "ALTER TABLE items_rebuild RENAME TO items", tx);
                        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_items_name_en ON items(name_en)", tx);
                        Exec(conn, "CREATE INDEX IF NOT EXISTS ix_items_generic ON items(generic_name)", tx);
                        tx.Commit();
                    }
                    catch { tx.Rollback(); throw; }
                }
                Exec(conn, "PRAGMA foreign_keys=ON");
            }
        }

        /// <summary>Runs a statement that is fine to fail — dropping an index/constraint an older
        /// database may never have created. Returns false if it did fail.</summary>
        private bool TryExecute(string sql)
        {
            try { _factory.Execute(sql); return true; }
            catch { return false; /* not present — nothing to drop */ }
        }

        private static void Exec(DbConnection conn, string sql, DbTransaction tx = null)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                cmd.ExecuteNonQuery();
            }
        }

        private bool SqliteColumnIsNotNull(string table, string column)
        {
            using (var conn = _factory.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table})";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(Convert.ToString(r["name"]), column, StringComparison.OrdinalIgnoreCase))
                            return Convert.ToInt32(r["notnull"]) != 0;
            }
            return false;
        }

        /// <summary>
        /// Adds a column an older database is missing, in the type each backend spells it with.
        ///
        /// The MySQL side used to be hard-coded to "INT NULL" whatever the SQLite type said, so
        /// upgrading a network-mode database gave <c>sales.payment_method</c> — a TEXT column holding
        /// "Cash"/"Bankak"/"Fawry" — an integer type. Fresh installs were unaffected, because the schema
        /// script creates the column correctly and this never runs; only an upgraded server hit it,
        /// which is the configuration least likely to be tested and most likely to be a live pharmacy.
        /// </summary>
        private void EnsureColumn(string table, string column, string sqliteType)
        {
            if (ColumnExists(table, column)) return;
            _factory.Execute($"ALTER TABLE {table} ADD COLUMN {column} {ColumnType(sqliteType)}");
        }

        /// <summary>Maps the schema's SQLite type onto the running backend.</summary>
        private string ColumnType(string sqliteType)
        {
            if (_factory.Kind != DbKind.MySql) return sqliteType;

            // Swap the base type word and keep whatever constraint follows it ("NOT NULL DEFAULT 0"),
            // so a column the code reads as non-null is not created nullable and full of NULLs.
            string t = (sqliteType ?? "").Trim();
            int space = t.IndexOf(' ');
            string baseType = (space < 0 ? t : t.Substring(0, space)).ToUpperInvariant();
            string tail = space < 0 ? " NULL" : t.Substring(space);

            string mysqlBase =
                baseType.StartsWith("TEXT") ? "TEXT" :
                baseType.StartsWith("REAL") || baseType.StartsWith("NUMERIC") ? "DOUBLE" :
                "INT";
            return mysqlBase + tail;
        }

        private bool ColumnExists(string table, string column)
        {
            if (_factory.Kind == DbKind.MySql)
            {
                object n = _factory.Scalar(
                    "SELECT COUNT(*) FROM information_schema.columns " +
                    "WHERE table_schema = DATABASE() AND table_name=@t AND column_name=@c",
                    ("@t", table), ("@c", column));
                return Convert.ToInt32(n) > 0;
            }

            // SQLite: PRAGMA table_info
            using (var conn = _factory.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({table})";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (string.Equals(Convert.ToString(r["name"]), column, StringComparison.OrdinalIgnoreCase))
                            return true;
            }
            return false;
        }

        private bool TableExists(string table)
        {
            if (_factory.Kind == DbKind.MySql)
            {
                object n = _factory.Scalar(
                    "SELECT COUNT(*) FROM information_schema.tables " +
                    "WHERE table_schema = DATABASE() AND table_name=@t", ("@t", table));
                return Convert.ToInt32(n) > 0;
            }

            using (var conn = _factory.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t";
                DbExec.AddParams(cmd, ("@t", table));
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>Creates the default admin only if the users table is empty. Returns true if created.</summary>
        public bool EnsureDefaultAdmin(string username = "admin", string password = "admin123")
        {
            if (Convert.ToInt32(_factory.Scalar("SELECT COUNT(*) FROM users")) > 0)
                return false;
            _factory.Execute(
                "INSERT INTO users (username, password_hash, full_name, role, is_active) " +
                "VALUES (@u, @p, @f, 'Admin', 1)",
                ("@u", username), ("@p", PasswordHasher.Hash(password)), ("@f", "المدير"));
            return true;
        }

        private void RunScript(string sql)
        {
            using (var conn = _factory.OpenConnection())
            {
                if (conn is MySqlConnection mysql)
                {
                    // MySql.Data runs multi-statement scripts via MySqlScript, not a single command.
                    new MySqlScript(mysql, sql).Execute();
                }
                else
                {
                    using (var tx = conn.BeginTransaction())
                    using (DbCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;   // SQLite runs all statements in the script
                        cmd.Transaction = tx;
                        cmd.ExecuteNonQuery();
                        tx.Commit();
                    }
                }
            }
        }

        private static string ReadResource(string relativeName)
        {
            Assembly asm = typeof(DatabaseInitializer).Assembly;
            string fullName = asm.GetName().Name + "." + relativeName;
            using (Stream s = asm.GetManifestResourceStream(fullName))
            {
                if (s == null)
                    throw new InvalidOperationException("Embedded SQL resource not found: " + fullName);
                using (var reader = new StreamReader(s))
                    return reader.ReadToEnd();
            }
        }
    }
}
