using System;
using System.Collections.Generic;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteUserRepository : IUserRepository
    {
        private const string Cols =
            "id, username, password_hash, full_name, role, is_active, created_at";

        private readonly IDbConnectionFactory _db;

        public SqliteUserRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public User GetByUsername(string username)
            => _db.QueryOne($"SELECT {Cols} FROM users WHERE username=@u LIMIT 1", Map, ("@u", username));

        public User GetById(int id)
            => _db.QueryOne($"SELECT {Cols} FROM users WHERE id=@id", Map, ("@id", id));

        public IReadOnlyList<User> GetAll()
            => _db.Query($"SELECT {Cols} FROM users ORDER BY username", Map);

        public int Count()
            => Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM users"));

        public int Add(User user)
            => _db.InsertId(
                "INSERT INTO users (username, password_hash, full_name, role, is_active) " +
                "VALUES (@u, @p, @f, @r, @a)",
                ("@u", user.Username), ("@p", user.PasswordHash), ("@f", Db.Text(user.FullName)),
                ("@r", user.Role.ToString()), ("@a", user.IsActive ? 1 : 0));

        public void Update(User user)
            => _db.Execute("UPDATE users SET username=@u, full_name=@f, role=@r, is_active=@a WHERE id=@id",
                ("@u", user.Username), ("@f", Db.Text(user.FullName)), ("@r", user.Role.ToString()),
                ("@a", user.IsActive ? 1 : 0), ("@id", user.Id));

        /// <summary>
        /// An employee's HR records belong to the employee and go with them; anything that carries money
        /// or stock does not, because a sale, a refund, a write-off or a debt entry has to stay
        /// attributable to whoever made it. The presence of any of those refuses the delete outright,
        /// including rows the employee created for OTHER staff (a leave or deduction they granted),
        /// which would otherwise be orphaned.
        /// </summary>
        public bool Delete(int userId)
        {
            long history = Convert.ToInt64(_db.Scalar(
                "SELECT (SELECT COUNT(*) FROM sales             WHERE user_id=@id)" +
                "     + (SELECT COUNT(*) FROM returns           WHERE user_id=@id)" +
                "     + (SELECT COUNT(*) FROM debt_transactions WHERE user_id=@id)" +
                "     + (SELECT COUNT(*) FROM stock_adjustments WHERE user_id=@id)" +
                "     + (SELECT COUNT(*) FROM employee_expenses WHERE user_id=@id)" +
                "     + (SELECT COUNT(*) FROM purchases         WHERE user_id=@id)" +
                "     + (SELECT COUNT(*) FROM leaves            WHERE created_by=@id)" +
                "     + (SELECT COUNT(*) FROM deductions        WHERE created_by=@id)", ("@id", userId)));
            if (history > 0) return false;

            using (var conn = _db.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    foreach (string sql in new[]
                    {
                        "DELETE FROM attendance         WHERE user_id=@id",
                        "DELETE FROM leaves             WHERE user_id=@id",
                        "DELETE FROM deductions         WHERE user_id=@id",
                        "DELETE FROM employee_profiles  WHERE user_id=@id",
                        "DELETE FROM users              WHERE id=@id"
                    })
                        using (DbCommand cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = sql;
                            cmd.Transaction = tx;
                            DbExec.AddParams(cmd, ("@id", userId));
                            cmd.ExecuteNonQuery();
                        }
                    tx.Commit();
                    return true;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        public void SetActive(int userId, bool active)
            => _db.Execute("UPDATE users SET is_active=@a WHERE id=@id",
                ("@a", active ? 1 : 0), ("@id", userId));

        public void UpdatePasswordHash(int userId, string passwordHash)
            => _db.Execute("UPDATE users SET password_hash=@p WHERE id=@id",
                ("@p", passwordHash), ("@id", userId));

        internal static User Map(DbDataReader r) => new User
        {
            Id = Db.GetInt(r, "id"),
            Username = Db.GetString(r, "username"),
            PasswordHash = Db.GetString(r, "password_hash"),
            FullName = Db.GetStringN(r, "full_name"),
            Role = (Role)Enum.Parse(typeof(Role), Db.GetString(r, "role"), true),
            IsActive = Db.GetBool(r, "is_active"),
            CreatedAt = Db.GetTime(r, "created_at")
        };
    }
}
