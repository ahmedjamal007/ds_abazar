using System;
using System.Collections.Generic;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteCustomerRepository : ICustomerRepository
    {
        private const string Cols = "id, name, phone, balance, created_at";
        private readonly IDbConnectionFactory _db;

        public SqliteCustomerRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Customer GetById(int id)
            => _db.QueryOne($"SELECT {Cols} FROM customers WHERE id=@id", Map, ("@id", id));

        public IReadOnlyList<Customer> Search(string term, int limit = 50)
        {
            term = (term ?? "").Trim();
            if (string.IsNullOrEmpty(term))
                return _db.Query($"SELECT {Cols} FROM customers ORDER BY name LIMIT @lim", Map, ("@lim", limit));
            return _db.Query(
                $"SELECT {Cols} FROM customers WHERE name LIKE @t OR phone LIKE @t ORDER BY name LIMIT @lim",
                Map, ("@t", "%" + term + "%"), ("@lim", limit));
        }

        public IReadOnlyList<Customer> GetAll()
            => _db.Query($"SELECT {Cols} FROM customers ORDER BY name", Map);

        public IReadOnlyList<Customer> GetWithDebt()
            => _db.Query($"SELECT {Cols} FROM customers WHERE balance <> 0 ORDER BY balance DESC", Map);

        public int Add(Customer c)
            => _db.InsertId(
                "INSERT INTO customers (name, phone, balance) VALUES (@n, @p, @b)",
                ("@n", c.Name), ("@p", Db.Text(c.Phone)), ("@b", Db.Money(c.Balance)));

        public void Update(Customer c)
            => _db.Execute("UPDATE customers SET name=@n, phone=@p WHERE id=@id",
                ("@n", c.Name), ("@p", Db.Text(c.Phone)), ("@id", c.Id));

        public bool Delete(int customerId)
        {
            long history = Convert.ToInt64(_db.Scalar(
                "SELECT (SELECT COUNT(*) FROM sales             WHERE customer_id=@id)" +
                "     + (SELECT COUNT(*) FROM debt_transactions WHERE customer_id=@id)", ("@id", customerId)));
            if (history > 0) return false;

            _db.Execute("DELETE FROM customers WHERE id=@id", ("@id", customerId));
            return true;
        }

        public IReadOnlyList<DebtTransaction> GetTransactions(int customerId)
            => _db.Query(
                "SELECT id, customer_id, type, amount, sale_id, user_id, note, created_at " +
                "FROM debt_transactions WHERE customer_id=@c ORDER BY created_at, id",
                r => new DebtTransaction
                {
                    Id = Db.GetInt(r, "id"),
                    CustomerId = Db.GetInt(r, "customer_id"),
                    Type = (DebtTransactionType)Enum.Parse(typeof(DebtTransactionType), Db.GetString(r, "type"), true),
                    Amount = Db.GetMoney(r, "amount"),
                    SaleId = Db.GetIntN(r, "sale_id"),
                    UserId = Db.GetInt(r, "user_id"),
                    Note = Db.GetStringN(r, "note"),
                    CreatedAt = Db.GetTime(r, "created_at")
                },
                ("@c", customerId));

        internal static Customer Map(DbDataReader r) => new Customer
        {
            Id = Db.GetInt(r, "id"),
            Name = Db.GetString(r, "name"),
            Phone = Db.GetStringN(r, "phone"),
            Balance = Db.GetMoney(r, "balance"),
            CreatedAt = Db.GetTime(r, "created_at")
        };
    }
}
