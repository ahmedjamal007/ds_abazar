using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;
using Dawaii.Core.Services;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Transactional persistence of sales and returns (NFR-02). Everything for one sale — the sale,
    /// its lines and allocations, the stock decrements, any debt/customer change and the audit row —
    /// happens inside ONE database transaction, so a power cut can never leave a partial sale.
    /// Stock is decremented with a `quantity_units >= n` guard so a stale UI (or a second terminal in
    /// network mode) that oversells causes a clean rollback instead of negative stock.
    /// Works against both SQLite and MySQL via <see cref="IDbConnectionFactory"/>.
    /// </summary>
    public class SqliteSaleStore : ISaleStore
    {
        private readonly IDbConnectionFactory _factory;

        public SqliteSaleStore(IDbConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public Sale Save(Sale sale)
        {
            using (var conn = _factory.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    int saleId = InsertSale(conn, tx, sale);
                    // The invoice number is the row id: a plain integer the cashier can read off the
                    // receipt and type back into the return screen without it reversing on an RTL screen.
                    Exec(conn, tx, "UPDATE sales SET sale_number=@n WHERE id=@id",
                        ("@n", saleId), ("@id", saleId));

                    foreach (SaleLine line in sale.Lines)
                    {
                        int lineId = InsertLine(conn, tx, saleId, line);
                        foreach (SaleLineAllocation a in line.Allocations)
                        {
                            int affected = Exec(conn, tx,
                                "UPDATE stock_batches SET quantity_units = quantity_units - @u " +
                                "WHERE id=@b AND is_disposed=0 AND quantity_units >= @u",
                                ("@u", a.Units), ("@b", a.BatchId));
                            if (affected != 1)
                                throw new InsufficientStockException(line.ItemId, a.Units, 0);

                            Exec(conn, tx,
                                "INSERT INTO sale_line_allocations (sale_line_id, batch_id, units, unit_cost) " +
                                "VALUES (@l, @b, @u, @c)",
                                ("@l", lineId), ("@b", a.BatchId), ("@u", a.Units), ("@c", Db.Money(a.UnitCost)));
                        }
                    }

                    if (sale.SaleType == SaleType.Credit && sale.CustomerId.HasValue)
                    {
                        Exec(conn, tx,
                            "INSERT INTO debt_transactions (customer_id, type, amount, sale_id, user_id, note) " +
                            "VALUES (@c, 'Charge', @a, @s, @u, 'بيع آجل')",
                            ("@c", sale.CustomerId.Value), ("@a", Db.Money(sale.Total)), ("@s", saleId), ("@u", sale.UserId));
                        Exec(conn, tx, "UPDATE customers SET balance = balance + @a WHERE id=@c",
                            ("@a", Db.Money(sale.Total)), ("@c", sale.CustomerId.Value));
                    }

                    SqliteAuditRepository.AddOnConnection(conn, tx, new AuditEntry
                    {
                        UserId = sale.UserId, Action = "Sale", Entity = "sales", EntityId = saleId,
                        Details = $"{sale.SaleType} total {sale.Total:0.00}", Terminal = sale.Terminal
                    });

                    tx.Commit();
                    sale.Id = saleId;
                    sale.SaleNumber = saleId;
                    return sale;
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        public Return ReturnSale(int saleId, int userId, string reason)
            => Refund(saleId, userId, reason, null);

        public Return ReturnItems(int saleId, int userId, string reason, IList<ReturnRequest> requests)
        {
            if (requests == null || requests.Count == 0)
                throw new ValidationException("حدد الكمية المراد إرجاعها.");
            return Refund(saleId, userId, reason, requests);
        }

        /// <summary>
        /// Applies a return — whole invoice when <paramref name="requests"/> is null, otherwise exactly
        /// the requested quantities — inside one transaction: stock back on the batches it came off,
        /// the return and its lines recorded, the invoice's running refund totals and status updated,
        /// and any credit reversed. Nothing is written until the quantities validate.
        /// </summary>
        private Return Refund(int saleId, int userId, string reason, IList<ReturnRequest> requests)
        {
            using (var conn = _factory.OpenConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    Sale sale = LoadSale(conn, tx, saleId);
                    if (sale == null) throw new ValidationException("الفاتورة غير موجودة.");
                    LoadLines(conn, tx, sale);

                    ReturnPlan plan = ReturnCalculator.Plan(sale, requests ?? ReturnCalculator.EverythingOutstanding(sale));

                    // Put the units back on the very batches that supplied them, skipping whatever
                    // earlier returns already took, so batch costs and expiry stay truthful.
                    foreach (ReturnLine rl in plan.Lines)
                    {
                        SaleLine line = sale.Lines.First(l => l.Id == rl.SaleLineId);
                        RestoreUnits(conn, tx, rl.SaleLineId, line.ReturnedUnits, rl.Units);
                    }

                    int returnId = InsertScalar(conn, tx,
                        "INSERT INTO returns (sale_id, user_id, reason, total) VALUES (@s, @u, @r, @t)",
                        ("@s", saleId), ("@u", userId), ("@r", Db.Text(reason)), ("@t", Db.Money(plan.Total)));

                    foreach (ReturnLine rl in plan.Lines)
                    {
                        rl.ReturnId = returnId;
                        rl.Id = InsertScalar(conn, tx,
                            "INSERT INTO return_lines (return_id, sale_line_id, item_id, quantity, units, amount, cost) " +
                            "VALUES (@r, @l, @i, @q, @u, @a, @c)",
                            ("@r", returnId), ("@l", rl.SaleLineId), ("@i", rl.ItemId), ("@q", rl.Quantity),
                            ("@u", rl.Units), ("@a", Db.Money(rl.Amount)), ("@c", Db.Money(rl.Cost)));
                    }

                    Exec(conn, tx,
                        "UPDATE sales SET status=@st, returned_total=@rt, returned_cost=@rc WHERE id=@id",
                        ("@st", plan.CompletesSale ? "Returned" : "PartiallyReturned"),
                        ("@rt", Db.Money(sale.ReturnedTotal + plan.Total)),
                        ("@rc", Db.Money(sale.ReturnedCost + plan.Cost)),
                        ("@id", saleId));

                    // A credit customer owes less by exactly what was refunded.
                    if (sale.SaleType == SaleType.Credit && sale.CustomerId.HasValue && plan.Total != 0m)
                    {
                        Exec(conn, tx,
                            "INSERT INTO debt_transactions (customer_id, type, amount, sale_id, user_id, note) " +
                            "VALUES (@c, 'Payment', @a, @s, @u, 'إرجاع فاتورة')",
                            ("@c", sale.CustomerId.Value), ("@a", Db.Money(plan.Total)), ("@s", saleId), ("@u", userId));
                        Exec(conn, tx, "UPDATE customers SET balance = balance - @a WHERE id=@c",
                            ("@a", Db.Money(plan.Total)), ("@c", sale.CustomerId.Value));
                    }

                    SqliteAuditRepository.AddOnConnection(conn, tx, new AuditEntry
                    {
                        UserId = userId, Action = "Return", Entity = "sales", EntityId = saleId,
                        Details = $"return {(plan.CompletesSale ? "full" : "partial")} " +
                                  $"{plan.Lines.Sum(l => l.Quantity)} item(s) total {plan.Total:0.00}: {reason}"
                    });

                    tx.Commit();
                    return new Return
                    {
                        Id = returnId, SaleId = saleId, UserId = userId, Reason = reason,
                        Total = plan.Total, Cost = plan.Cost, CompletedTheSale = plan.CompletesSale,
                        CreatedAt = DateTime.Now, Lines = plan.Lines
                    };
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        /// <summary>
        /// Gives <paramref name="units"/> back to the batches a sale line consumed, in the order it
        /// consumed them, after skipping the <paramref name="alreadyReturned"/> units earlier returns
        /// already restored. Walking the allocations this way means a customer returning 5 of 50 boxes
        /// credits the batch those 5 actually came out of, not an arbitrary one.
        /// </summary>
        private static void RestoreUnits(DbConnection conn, DbTransaction tx, int saleLineId, int alreadyReturned, int units)
        {
            var allocations = new List<(int batch, int units)>();
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT batch_id, units FROM sale_line_allocations " +
                                  "WHERE sale_line_id=@l ORDER BY id";
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ("@l", saleLineId));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        allocations.Add((Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1))));
            }

            int skip = alreadyReturned, outstanding = units;
            foreach (var a in allocations)
            {
                if (outstanding == 0) break;
                int available = a.units;
                if (skip > 0)
                {
                    int skipped = Math.Min(skip, available);
                    skip -= skipped;
                    available -= skipped;
                }
                if (available <= 0) continue;

                int give = Math.Min(available, outstanding);

                // Only onto a batch that is still on the shelf. Without the is_disposed guard the units
                // were credited to a written-off batch, where the POS cannot see them and the full-stock
                // view can — two stock figures permanently disagreeing over goods that physically came
                // back over the counter. A disposed batch means its stock was destroyed, so the refund
                // is refused rather than quietly restocking a row nobody can sell from.
                int affected = Exec(conn, tx,
                    "UPDATE stock_batches SET quantity_units = quantity_units + @u " +
                    "WHERE id=@b AND is_disposed=0",
                    ("@u", give), ("@b", a.batch));
                if (affected != 1)
                    throw new ValidationException(
                        "تعذّر الإرجاع: الدفعة التي بيع منها هذا الصنف لم تعد في المخزون (تم إتلافها). " +
                        "سجّل الإرجاع كتسوية مخزون بدلاً من ذلك.");

                outstanding -= give;
            }

            if (outstanding > 0)
                throw new ValidationException("لا يمكن إرجاع كمية أكبر من المسجلة على الفاتورة.");
        }

        public Sale GetById(int id)
        {
            using (var conn = _factory.OpenConnection())
            {
                Sale sale = LoadSale(conn, null, id);
                if (sale == null) return null;
                LoadLines(conn, null, sale);
                return sale;
            }
        }

        public Sale GetBySaleNumber(int saleNumber)
        {
            if (saleNumber <= 0) return null;
            using (var conn = _factory.OpenConnection())
            {
                Sale sale = LoadSaleByNumber(conn, saleNumber);
                if (sale == null) return null;
                LoadLines(conn, null, sale);
                return sale;
            }
        }

        public IReadOnlyList<Sale> GetByDateRange(DateTime fromInclusive, DateTime toExclusive)
            => _factory.Query(
                "SELECT id, sale_number, user_id, customer_id, sale_type, payment_method, subtotal, discount, total, " +
                "cost_total, returned_total, returned_cost, status, terminal, created_at FROM sales " +
                "WHERE created_at >= @f AND created_at < @t ORDER BY created_at",
                MapSale, ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)));

        // ---------------- helpers ----------------

        private int InsertSale(DbConnection conn, DbTransaction tx, Sale s)
            => InsertScalar(conn, tx,
                "INSERT INTO sales (user_id, customer_id, sale_type, payment_method, subtotal, discount, total, cost_total, status, terminal, created_at) " +
                "VALUES (@u, @c, @st, @pm, @sub, @dis, @tot, @cost, 'Completed', @term, @at)",
                ("@u", s.UserId), ("@c", Db.IntN(s.CustomerId)), ("@st", s.SaleType.ToString()), ("@pm", Db.Text(s.PaymentMethod)),
                ("@sub", Db.Money(s.Subtotal)), ("@dis", Db.Money(s.Discount)), ("@tot", Db.Money(s.Total)),
                ("@cost", Db.Money(s.CostTotal)), ("@term", Db.Text(s.Terminal)), ("@at", Db.Time(s.CreatedAt)));

        private int InsertLine(DbConnection conn, DbTransaction tx, int saleId, SaleLine l)
            => InsertScalar(conn, tx,
                "INSERT INTO sale_lines (sale_id, item_id, unit_type, quantity, units_each, unit_price, line_total, cost_total) " +
                "VALUES (@s, @i, @ut, @q, @ue, @up, @lt, @ct)",
                ("@s", saleId), ("@i", l.ItemId), ("@ut", l.UnitType.ToString()), ("@q", l.Quantity),
                ("@ue", l.UnitsEach), ("@up", Db.Money(l.UnitPrice)), ("@lt", Db.Money(l.LineTotal)), ("@ct", Db.Money(l.CostTotal)));

        private static Sale LoadSale(DbConnection conn, DbTransaction tx, int id)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, sale_number, user_id, customer_id, sale_type, payment_method, subtotal, discount, total, " +
                                  "cost_total, returned_total, returned_cost, status, terminal, created_at FROM sales WHERE id=@id";
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ("@id", id));
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? MapSale(r) : null;
            }
        }

        private static Sale LoadSaleByNumber(DbConnection conn, int saleNumber)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, sale_number, user_id, customer_id, sale_type, payment_method, subtotal, discount, total, " +
                                  "cost_total, returned_total, returned_cost, status, terminal, created_at FROM sales WHERE sale_number=@n";
                DbExec.AddParams(cmd, ("@n", saleNumber));
                using (var r = cmd.ExecuteReader())
                    return r.Read() ? MapSale(r) : null;
            }
        }

        private static void LoadLines(DbConnection conn, DbTransaction tx, Sale sale)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                // The trailing subquery is how much of each line earlier returns already took back,
                // which is what caps a new return and what the return screen shows as "remaining".
                cmd.CommandText = "SELECT sl.id, sl.item_id, sl.unit_type, sl.quantity, sl.units_each, sl.unit_price, " +
                                  "sl.line_total, sl.cost_total, i.name_en, " +
                                  "COALESCE((SELECT SUM(rl.units) FROM return_lines rl WHERE rl.sale_line_id = sl.id), 0) " +
                                  "FROM sale_lines sl " +
                                  "JOIN items i ON sl.item_id = i.id WHERE sl.sale_id=@s ORDER BY sl.id";
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ("@s", sale.Id));
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        sale.Lines.Add(new SaleLine
                        {
                            Id = Convert.ToInt32(r.GetValue(0)), SaleId = sale.Id, ItemId = Convert.ToInt32(r.GetValue(1)),
                            UnitType = (UnitType)Enum.Parse(typeof(UnitType), r.GetString(2), true),
                            Quantity = Convert.ToInt32(r.GetValue(3)), UnitsEach = Convert.ToInt32(r.GetValue(4)),
                            UnitPrice = decimal.Round(Convert.ToDecimal(r.GetValue(5)), 2),
                            LineTotal = decimal.Round(Convert.ToDecimal(r.GetValue(6)), 2),
                            CostTotal = decimal.Round(Convert.ToDecimal(r.GetValue(7)), 2),
                            ItemName = r.GetString(8),
                            ReturnedUnits = Convert.ToInt32(r.GetValue(9))
                        });
            }
        }

        private static Sale MapSale(DbDataReader r) => new Sale
        {
            Id = Db.GetInt(r, "id"),
            // Older rows carried a composite text number; the id has always been the real identity.
            SaleNumber = Db.GetIntN(r, "sale_number") ?? Db.GetInt(r, "id"),
            UserId = Db.GetInt(r, "user_id"),
            CustomerId = Db.GetIntN(r, "customer_id"),
            SaleType = (SaleType)Enum.Parse(typeof(SaleType), Db.GetString(r, "sale_type"), true),
            PaymentMethod = Db.GetStringN(r, "payment_method"),
            Subtotal = Db.GetMoney(r, "subtotal"),
            Discount = Db.GetMoney(r, "discount"),
            Total = Db.GetMoney(r, "total"),
            CostTotal = Db.GetMoney(r, "cost_total"),
            ReturnedTotal = Db.GetMoney(r, "returned_total"),
            ReturnedCost = Db.GetMoney(r, "returned_cost"),
            Status = (SaleStatus)Enum.Parse(typeof(SaleStatus), Db.GetString(r, "status"), true),
            Terminal = Db.GetStringN(r, "terminal"),
            CreatedAt = Db.GetTime(r, "created_at")
        };

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

        private int InsertScalar(DbConnection conn, DbTransaction tx, string insertSql, params (string, object)[] ps)
        {
            Exec(conn, tx, insertSql, ps);
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + _factory.LastInsertIdSql;
                cmd.Transaction = tx;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
