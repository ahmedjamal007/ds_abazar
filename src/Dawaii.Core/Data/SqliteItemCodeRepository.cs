using System;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// One row per item (enforced by the ux_item_codes_item unique index), one item per code (the
    /// UNIQUE on the code column). Lookups compare with UPPER() on both sides rather than a collation,
    /// because SQLite's TEXT compares case-sensitively while MySQL's utf8mb4_unicode_ci does not — this
    /// way a scanned code resolves identically on the local and the shared backend. The table holds one
    /// row per item, so not using the index for that comparison costs nothing.
    /// </summary>
    public class SqliteItemCodeRepository : IItemCodeRepository
    {
        private readonly IDbConnectionFactory _db;

        public SqliteItemCodeRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public ItemCode FindByCode(string code)
            => _db.QueryOne("SELECT id, item_id, code FROM item_codes WHERE UPPER(code)=UPPER(@c) LIMIT 1",
                Map, ("@c", code ?? string.Empty));

        public ItemCode GetByItem(int itemId)
            => _db.QueryOne("SELECT id, item_id, code FROM item_codes WHERE item_id=@i LIMIT 1",
                Map, ("@i", itemId));

        public void SetForItem(int itemId, string code)
        {
            // Replace, not append: the item's previous code is released in the same transaction that
            // takes the new one, so re-labelling a drug never leaves its old barcode owned.
            using (var conn = _db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    Exec(conn, tx, "DELETE FROM item_codes WHERE item_id=@i", ("@i", (object)itemId));
                    Exec(conn, tx, "INSERT INTO item_codes (item_id, code) VALUES (@i, @c)",
                        ("@i", (object)itemId), ("@c", (object)code));
                    tx.Commit();
                }
                catch (DbException ex) when (IsUniqueViolation(ex))
                {
                    // UNIQUE(code) violated — the code belongs to another item (FR-QRC-06).
                    tx.Rollback();
                    throw new DuplicateCodeException(code, 0);
                }
                catch { tx.Rollback(); throw; }
            }
        }

        public void RemoveForItem(int itemId)
            => _db.Execute("DELETE FROM item_codes WHERE item_id=@i", ("@i", itemId));

        private static void Exec(DbConnection conn, DbTransaction tx, string sql,
            params (string, object)[] ps)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ps);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>True for a unique/duplicate-key error on either backend (SQLite constraint / MySQL 1062).</summary>
        private static bool IsUniqueViolation(DbException ex)
        {
            string m = ex.Message ?? "";
            return m.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("constraint", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ItemCode Map(DbDataReader r) => new ItemCode
        {
            Id = r.GetInt32(0),
            ItemId = r.GetInt32(1),
            Code = r.GetString(2)
        };
    }
}
