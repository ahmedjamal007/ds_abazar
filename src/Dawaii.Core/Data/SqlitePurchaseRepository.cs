using System;
using System.Collections.Generic;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    /// <summary>Purchases log store (V1.4 "المشتريات"). Works over both backends via <see cref="DbExec"/>.</summary>
    public class SqlitePurchaseRepository : IPurchaseRepository
    {
        private readonly IDbConnectionFactory _db;

        public SqlitePurchaseRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public int Add(Purchase p)
            => _db.InsertId(
                "INSERT INTO purchases (supplier_name, description, quantity, unit_price, amount, note, user_id) " +
                "VALUES (@s, @d, @q, @up, @a, @n, @u)",
                ("@s", Db.Text(p.SupplierName)), ("@d", Db.Text(p.Description)), ("@q", p.Quantity),
                ("@up", Db.Money(p.UnitPrice)), ("@a", Db.Money(p.Amount)),
                ("@n", Db.Text(p.Note)), ("@u", p.UserId));

        public IReadOnlyList<Purchase> GetRange(DateTime fromInclusive, DateTime toExclusive)
            => _db.Query(
                "SELECT p.id, p.supplier_name, p.description, p.quantity, p.unit_price, p.amount, " +
                "       p.note, p.user_id, p.created_at, u.full_name, u.username " +
                "FROM purchases p LEFT JOIN users u ON p.user_id = u.id " +
                "WHERE p.created_at >= @f AND p.created_at < @t ORDER BY p.created_at DESC, p.id DESC",
                Map, ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)));

        public decimal TotalInRange(DateTime fromInclusive, DateTime toExclusive)
        {
            object v = _db.Scalar(
                "SELECT COALESCE(SUM(amount), 0) FROM purchases WHERE created_at >= @f AND created_at < @t",
                ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)));
            return decimal.Round(Convert.ToDecimal(v), 2);
        }

        public void Delete(int id)
            => _db.Execute("DELETE FROM purchases WHERE id=@id", ("@id", id));

        private static Purchase Map(DbDataReader r) => new Purchase
        {
            Id = Db.GetInt(r, "id"),
            SupplierName = Db.GetStringN(r, "supplier_name"),
            Description = Db.GetString(r, "description"),
            Quantity = Db.GetInt(r, "quantity"),
            UnitPrice = Db.GetMoney(r, "unit_price"),
            Amount = Db.GetMoney(r, "amount"),
            Note = Db.GetStringN(r, "note"),
            UserId = Db.GetInt(r, "user_id"),
            CreatedAt = Db.GetTime(r, "created_at"),
            UserName = Db.GetStringN(r, "full_name") ?? Db.GetStringN(r, "username")
        };
    }
}
