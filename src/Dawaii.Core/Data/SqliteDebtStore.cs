using System;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Records debt payments/charges and updates the cached customer balance atomically (D-06):
    /// the ledger row and the balance change always commit together. Works on SQLite and MySQL.
    /// </summary>
    public class SqliteDebtStore : IDebtStore
    {
        private readonly IDbConnectionFactory _factory;

        public SqliteDebtStore(IDbConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public DebtTransaction RecordPayment(int customerId, decimal amount, int userId, string note)
            => Record(customerId, DebtTransactionType.Payment, amount, userId, note, balanceDelta: -amount);

        public DebtTransaction RecordManualCharge(int customerId, decimal amount, int userId, string note)
            => Record(customerId, DebtTransactionType.Charge, amount, userId, note, balanceDelta: amount);

        private DebtTransaction Record(int customerId, DebtTransactionType type, decimal amount,
            int userId, string note, decimal balanceDelta)
        {
            using (var conn = _factory.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    int id;
                    using (DbCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO debt_transactions (customer_id, type, amount, user_id, note) " +
                                          "VALUES (@c, @t, @a, @u, @n)";
                        cmd.Transaction = tx;
                        DbExec.AddParams(cmd,
                            ("@c", customerId), ("@t", type.ToString()), ("@a", Db.Money(amount)),
                            ("@u", userId), ("@n", Db.Text(note)));
                        cmd.ExecuteNonQuery();
                    }
                    using (DbCommand idCmd = conn.CreateCommand())
                    {
                        idCmd.CommandText = "SELECT " + _factory.LastInsertIdSql;
                        idCmd.Transaction = tx;
                        id = Convert.ToInt32(idCmd.ExecuteScalar());
                    }
                    using (DbCommand cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE customers SET balance = balance + @d WHERE id=@c";
                        cmd.Transaction = tx;
                        DbExec.AddParams(cmd, ("@d", Db.Money(balanceDelta)), ("@c", customerId));
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    return new DebtTransaction { Id = id, CustomerId = customerId, Type = type, Amount = amount, UserId = userId, Note = note, CreatedAt = DateTime.Now };
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }
    }
}
