using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Services;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteEmployeeRepository : IEmployeeRepository
    {
        private readonly IDbConnectionFactory _db;

        public SqliteEmployeeRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        // ---------------- Attendance ----------------

        public int AddAttendance(AttendanceEntry e)
            => _db.InsertId(
                "INSERT INTO attendance (user_id, login_at, terminal) VALUES (@u, @t, @term)",
                ("@u", e.UserId), ("@t", Db.Time(e.LoginAt)), ("@term", Db.Text(e.Terminal)));

        public IReadOnlyList<AttendanceEntry> GetAttendance(DateTime fromInclusive, DateTime toExclusive, int? userId = null)
        {
            string where = "login_at >= @f AND login_at < @t" + (userId.HasValue ? " AND user_id=@u" : "");
            var ps = new List<(string, object)> { ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)) };
            if (userId.HasValue) ps.Add(("@u", userId.Value));
            return _db.Query(
                $"SELECT id, user_id, login_at, terminal FROM attendance WHERE {where} ORDER BY login_at",
                r => new AttendanceEntry
                {
                    Id = Db.GetInt(r, "id"),
                    UserId = Db.GetInt(r, "user_id"),
                    LoginAt = Db.GetTime(r, "login_at"),
                    Terminal = Db.GetStringN(r, "terminal")
                }, ps.ToArray());
        }

        // ---------------- Profile ----------------

        public EmployeeProfile GetProfile(int userId)
            => _db.QueryOne(
                "SELECT user_id, monthly_salary, notes FROM employee_profiles WHERE user_id=@u",
                r => new EmployeeProfile
                {
                    UserId = Db.GetInt(r, "user_id"),
                    MonthlySalary = Db.GetMoney(r, "monthly_salary"),
                    Notes = Db.GetStringN(r, "notes")
                }, ("@u", userId));

        public void UpsertProfile(EmployeeProfile p)
        {
            string sql = _db.Kind == DbKind.MySql
                ? "INSERT INTO employee_profiles (user_id, monthly_salary, notes) VALUES (@u, @s, @n) " +
                  "ON DUPLICATE KEY UPDATE monthly_salary=@s, notes=@n"
                : "INSERT INTO employee_profiles (user_id, monthly_salary, notes) VALUES (@u, @s, @n) " +
                  "ON CONFLICT(user_id) DO UPDATE SET monthly_salary=@s, notes=@n";
            _db.Execute(sql, ("@u", p.UserId), ("@s", Db.Money(p.MonthlySalary)), ("@n", Db.Text(p.Notes)));
        }

        // ---------------- Leaves ----------------

        public int AddLeave(LeaveEntry l)
            => _db.InsertId(
                "INSERT INTO leaves (user_id, from_date, to_date, reason, created_by) " +
                "VALUES (@u, @f, @t, @r, @by)",
                ("@u", l.UserId), ("@f", DateOnly(l.FromDate)), ("@t", DateOnly(l.ToDate)),
                ("@r", Db.Text(l.Reason)), ("@by", l.CreatedBy));

        public IReadOnlyList<LeaveEntry> GetLeaves(int userId)
            => _db.Query(
                "SELECT id, user_id, from_date, to_date, reason, created_by, created_at " +
                "FROM leaves WHERE user_id=@u ORDER BY from_date DESC",
                r => new LeaveEntry
                {
                    Id = Db.GetInt(r, "id"),
                    UserId = Db.GetInt(r, "user_id"),
                    FromDate = Db.GetTime(r, "from_date"),
                    ToDate = Db.GetTime(r, "to_date"),
                    Reason = Db.GetStringN(r, "reason"),
                    CreatedBy = Db.GetInt(r, "created_by"),
                    CreatedAt = Db.GetTime(r, "created_at")
                }, ("@u", userId));

        public void RemoveLeave(int leaveId)
            => _db.Execute("DELETE FROM leaves WHERE id=@id", ("@id", leaveId));

        // ---------------- Deductions ----------------

        public int AddDeduction(DeductionEntry d)
            => _db.InsertId(
                "INSERT INTO deductions (user_id, amount, reason, created_by) " +
                "VALUES (@u, @a, @r, @by)",
                ("@u", d.UserId), ("@a", Db.Money(d.Amount)), ("@r", d.Reason), ("@by", d.CreatedBy));

        public IReadOnlyList<DeductionEntry> GetDeductions(int userId, DateTime? fromInclusive = null, DateTime? toExclusive = null)
        {
            string where = "user_id=@u";
            var ps = new List<(string, object)> { ("@u", userId) };
            if (fromInclusive.HasValue) { where += " AND created_at >= @f"; ps.Add(("@f", Db.Time(fromInclusive.Value))); }
            if (toExclusive.HasValue) { where += " AND created_at < @t"; ps.Add(("@t", Db.Time(toExclusive.Value))); }
            return _db.Query(
                $"SELECT id, user_id, amount, reason, created_by, created_at FROM deductions WHERE {where} ORDER BY created_at DESC",
                r => new DeductionEntry
                {
                    Id = Db.GetInt(r, "id"),
                    UserId = Db.GetInt(r, "user_id"),
                    Amount = Db.GetMoney(r, "amount"),
                    Reason = Db.GetString(r, "reason"),
                    CreatedBy = Db.GetInt(r, "created_by"),
                    CreatedAt = Db.GetTime(r, "created_at")
                }, ps.ToArray());
        }

        public void RemoveDeduction(int deductionId)
            => _db.Execute("DELETE FROM deductions WHERE id=@id", ("@id", deductionId));

        // ---------------- Expenses ----------------

        public int AddExpense(EmployeeExpense e)
            => _db.InsertId(
                "INSERT INTO employee_expenses (user_id, type, amount, item_id, units, note) " +
                "VALUES (@u, @ty, @a, @i, @n, @note)",
                ("@u", e.UserId), ("@ty", e.Type.ToString()), ("@a", Db.Money(e.Amount)),
                ("@i", Db.IntN(e.ItemId)), ("@n", Db.IntN(e.Units)), ("@note", Db.Text(e.Note)));

        /// <summary>
        /// The expense row and the stock it consumes, written together or not at all (V2.3).
        ///
        /// Before this, a medicine expense inserted a row and left the shelf untouched: the medicine
        /// walked out of the pharmacy and the database went on believing it was there, cumulatively and
        /// undetectably. The decrement carries the same <c>quantity_units &gt;= n</c> guard the POS uses,
        /// so a race with a sale on the last box rolls the whole expense back instead of driving a batch
        /// negative.
        /// </summary>
        public int AddMedicineExpense(EmployeeExpense e, IReadOnlyList<BatchAllocation> allocations)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (allocations == null || allocations.Count == 0)
                throw new ValidationException("لا توجد كمية لخصمها من المخزون.");

            using (DbConnection conn = _db.OpenConnection())
            using (DbTransaction tx = conn.BeginTransaction())
            {
                try
                {
                    int id = InsertScalar(conn, tx,
                        "INSERT INTO employee_expenses (user_id, type, amount, item_id, units, note) " +
                        "VALUES (@u, @ty, @a, @i, @n, @note)",
                        ("@u", e.UserId), ("@ty", e.Type.ToString()), ("@a", Db.Money(e.Amount)),
                        ("@i", Db.IntN(e.ItemId)), ("@n", Db.IntN(e.Units)), ("@note", Db.Text(e.Note)));

                    foreach (BatchAllocation a in allocations)
                    {
                        int affected = Exec(conn, tx,
                            "UPDATE stock_batches SET quantity_units = quantity_units - @u " +
                            "WHERE id=@b AND is_disposed=0 AND quantity_units >= @u",
                            ("@u", a.Units), ("@b", a.BatchId));
                        if (affected != 1)
                            throw new InsufficientStockException(e.ItemId ?? 0, a.Units, 0);

                        // One adjustment row per batch, so the shrinkage names the employee, the batch
                        // it came off and why — the same trail a disposal leaves.
                        Exec(conn, tx,
                            "INSERT INTO stock_adjustments (item_id, batch_id, delta_units, reason, user_id) " +
                            "VALUES (@i, @b, @d, @r, @u)",
                            ("@i", e.ItemId ?? 0), ("@b", a.BatchId), ("@d", -a.Units),
                            ("@r", "مصروف موظف (دواء)"), ("@u", e.UserId));
                    }

                    tx.Commit();
                    return id;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        public IReadOnlyList<EmployeeExpense> GetExpenses(DateTime fromInclusive, DateTime toExclusive, int? userId = null)
        {
            string where = "e.created_at >= @f AND e.created_at < @t" + (userId.HasValue ? " AND e.user_id=@u" : "");
            var ps = new List<(string, object)> { ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)) };
            if (userId.HasValue) ps.Add(("@u", userId.Value));
            return _db.Query(
                "SELECT e.id, e.user_id, e.type, e.amount, e.item_id, e.units, e.note, e.created_at, i.name_en " +
                $"FROM employee_expenses e LEFT JOIN items i ON e.item_id = i.id WHERE {where} ORDER BY e.created_at",
                r => new EmployeeExpense
                {
                    Id = Db.GetInt(r, "id"),
                    UserId = Db.GetInt(r, "user_id"),
                    Type = (ExpenseType)Enum.Parse(typeof(ExpenseType), Db.GetString(r, "type"), true),
                    Amount = Db.GetMoney(r, "amount"),
                    ItemId = Db.GetIntN(r, "item_id"),
                    Units = Db.GetIntN(r, "units"),
                    Note = Db.GetStringN(r, "note"),
                    CreatedAt = Db.GetTime(r, "created_at"),
                    ItemName = Db.GetStringN(r, "name_en")
                }, ps.ToArray());
        }

        private static string DateOnly(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static int Exec(DbConnection conn, DbTransaction tx, string sql, params (string, object)[] ps)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ps);
                return cmd.ExecuteNonQuery();
            }
        }

        private int InsertScalar(DbConnection conn, DbTransaction tx, string sql, params (string, object)[] ps)
        {
            Exec(conn, tx, sql, ps);
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + _db.LastInsertIdSql;
                cmd.Transaction = tx;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
