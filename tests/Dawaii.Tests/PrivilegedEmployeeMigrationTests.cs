using System;
using System.Data.Common;
using System.IO;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using NUnit.Framework;

namespace Dawaii.Tests
{
    /// <summary>
    /// Upgrade path for a pharmacy created before V1.8 added the third role, "موظف ذو امتيازات".
    /// Its users table constrains role to Admin/Cashier, so the database itself would reject the
    /// promotion — the manager would click "تغيير الصلاحية" and get a CHECK constraint error.
    /// SQLite cannot widen a CHECK, so the table is rebuilt; these tests guard what that rebuild
    /// must not lose: the accounts, their password hashes and who is still the manager.
    /// </summary>
    [TestFixture]
    public class PrivilegedEmployeeMigrationTests
    {
        private string _path;
        private SqliteConnectionFactory _db;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "dawaii_role_" + Guid.NewGuid().ToString("N") + ".db");
            _db = new SqliteConnectionFactory(_path);

            using (DbConnection conn = _db.OpenConnection())
            {
                Exec(conn, "CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, username TEXT NOT NULL UNIQUE, " +
                           "password_hash TEXT NOT NULL, full_name TEXT, " +
                           "role TEXT NOT NULL DEFAULT 'Cashier' CHECK (role IN ('Admin','Cashier')), " +
                           "is_active INTEGER NOT NULL DEFAULT 1, " +
                           "created_at TEXT NOT NULL DEFAULT (datetime('now','localtime')))");
                Exec(conn, "INSERT INTO users (id, username, password_hash, full_name, role) " +
                           "VALUES (1, 'admin', 'hash-admin', 'المدير', 'Admin')");
                Exec(conn, "INSERT INTO users (id, username, password_hash, full_name, role, is_active) " +
                           "VALUES (2, 'sara', 'hash-sara', 'سارة', 'Cashier', 0)");
            }
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

        [Test]
        public void Upgrade_LetsTheManagerPromoteAnEmployee()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            var users = new SqliteUserRepository(_db);
            User sara = users.GetByUsername("sara");
            sara.Role = Role.FullEmployee;
            users.Update(sara);                       // the old CHECK would have refused this write

            Assert.That(users.GetByUsername("sara").Role, Is.EqualTo(Role.FullEmployee));
            Assert.That(users.GetByUsername("sara").CanManagePurchasing, Is.True);
        }

        [Test]
        public void Upgrade_KeepsExistingAccountsIntactAcrossTheRebuild()
        {
            new DatabaseInitializer(_db).ApplySchemaAndSeed();

            var users = new SqliteUserRepository(_db);
            User admin = users.GetById(1);
            User sara = users.GetById(2);

            Assert.That(admin.Username, Is.EqualTo("admin"));
            Assert.That(admin.Role, Is.EqualTo(Role.Admin), "the manager is still the manager");
            Assert.That(admin.PasswordHash, Is.EqualTo("hash-admin"), "everyone can still log in");
            Assert.That(sara.FullName, Is.EqualTo("سارة"));
            Assert.That(sara.Role, Is.EqualTo(Role.Cashier), "nobody is promoted by the upgrade itself");
            Assert.That(sara.IsActive, Is.False, "a disabled account stays disabled");
        }

        [Test]
        public void Upgrade_IsIdempotent()
        {
            var initializer = new DatabaseInitializer(_db);
            initializer.ApplySchemaAndSeed();
            initializer.ApplySchemaAndSeed();   // a second startup must not rebuild the table again

            Assert.That(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM users")), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='users_rebuild'")),
                Is.Zero, "no leftover scratch table");
        }

        private object Scalar(string sql)
        {
            using (DbConnection conn = _db.OpenConnection())
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                return cmd.ExecuteScalar();
            }
        }

        private static void Exec(DbConnection conn, string sql)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
