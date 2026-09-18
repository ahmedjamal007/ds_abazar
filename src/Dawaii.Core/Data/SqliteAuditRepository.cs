using System;
using System.Collections.Generic;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteAuditRepository : IAuditRepository
    {
        private const string InsertSql =
            "INSERT INTO audit_log (user_id, action, entity, entity_id, details, terminal) " +
            "VALUES (@u, @a, @e, @eid, @d, @t)";

        private readonly IDbConnectionFactory _db;

        public SqliteAuditRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public void Add(AuditEntry e) => _db.Execute(InsertSql, Params(e));

        public IReadOnlyList<AuditEntry> GetRecent(int limit)
            => _db.Query(
                "SELECT id, user_id, action, entity, entity_id, details, terminal, created_at " +
                "FROM audit_log ORDER BY id DESC LIMIT @n",
                r => new AuditEntry
                {
                    Id = Db.GetLong(r, "id"),
                    UserId = Db.GetIntN(r, "user_id"),
                    Action = Db.GetString(r, "action"),
                    Entity = Db.GetStringN(r, "entity"),
                    EntityId = Db.GetIntN(r, "entity_id"),
                    Details = Db.GetStringN(r, "details"),
                    Terminal = Db.GetStringN(r, "terminal"),
                    CreatedAt = Db.GetTime(r, "created_at")
                },
                ("@n", limit));

        /// <summary>Inserts an audit row on an existing open connection/transaction (used inside sale/return commits).</summary>
        internal static void AddOnConnection(DbConnection conn, DbTransaction tx, AuditEntry e)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = InsertSql;
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, Params(e));
                cmd.ExecuteNonQuery();
            }
        }

        private static (string, object)[] Params(AuditEntry e) => new[]
        {
            ("@u", Db.IntN(e.UserId)),
            ("@a", (object)e.Action),
            ("@e", Db.Text(e.Entity)),
            ("@eid", Db.IntN(e.EntityId)),
            ("@d", Db.Text(e.Details)),
            ("@t", Db.Text(e.Terminal))
        };
    }
}
