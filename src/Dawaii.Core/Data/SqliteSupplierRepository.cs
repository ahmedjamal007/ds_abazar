using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Suppliers and their purchase invoices (V2.1). Works on both backends through the shared factory.
    ///
    /// A supplier's outstanding balance is computed from its invoices rather than cached on the row.
    /// The customer ledger caches its balance because every sale moves it and the till has to be fast;
    /// a supplier's moves only when a delivery arrives or a payment is made, so summing the invoices is
    /// cheap and removes the whole class of bug where a cached total drifts from the rows behind it.
    /// </summary>
    public class SqliteSupplierRepository : ISupplierRepository
    {
        private const string InvoiceCols =
            "i.id, i.supplier_id, i.representative, i.invoice_number, i.invoice_date, " +
            "i.total, i.amount_paid, i.user_id, i.created_at, " +
            "COALESCE(NULLIF(TRIM(u.full_name), ''), u.username) AS user_name, " +
            "(SELECT COUNT(*) FROM purchase_invoice_lines l WHERE l.invoice_id = i.id) AS line_count";

        /// <summary>Every invoice read joins the company and the person who filed it, so no screen has to
        /// go back for a name it will always want to show.</summary>
        private const string InvoiceFrom =
            "FROM purchase_invoices i " +
            "JOIN suppliers s ON s.id = i.supplier_id " +
            "LEFT JOIN users u ON u.id = i.user_id ";

        /// <summary>The outstanding/invoice-count columns every supplier listing needs. Left-joined so a
        /// company with no invoices yet still appears, with zeroes rather than dropping out of the list.</summary>
        private const string SupplierSelect =
            "SELECT s.id, s.name, s.created_at, " +
            "       COALESCE(SUM(CASE WHEN i.total - i.amount_paid > 0 THEN i.total - i.amount_paid ELSE 0 END), 0) AS outstanding, " +
            "       COUNT(i.id) AS invoice_count " +
            "FROM suppliers s LEFT JOIN purchase_invoices i ON i.supplier_id = s.id ";

        private readonly IDbConnectionFactory _db;

        public SqliteSupplierRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        // ---------------- Suppliers ----------------

        public Supplier GetById(int id)
            => _db.QueryOne(SupplierSelect + "WHERE s.id=@id GROUP BY s.id, s.name, s.created_at",
                MapSupplier, ("@id", id));

        public IReadOnlyList<Supplier> Search(string term, int limit = 100)
        {
            term = (term ?? "").Trim();
            if (string.IsNullOrEmpty(term))
                return _db.Query(SupplierSelect + "GROUP BY s.id, s.name, s.created_at ORDER BY s.name LIMIT @lim",
                    MapSupplier, ("@lim", limit));

            return _db.Query(
                SupplierSelect + "WHERE s.name LIKE @t GROUP BY s.id, s.name, s.created_at ORDER BY s.name LIMIT @lim",
                MapSupplier, ("@t", "%" + term + "%"), ("@lim", limit));
        }

        public IReadOnlyList<Supplier> WithOutstanding()
            => _db.Query(
                SupplierSelect + "GROUP BY s.id, s.name, s.created_at HAVING outstanding > 0 ORDER BY outstanding DESC",
                MapSupplier);

        public int Add(Supplier s)
            => _db.InsertId("INSERT INTO suppliers (name) VALUES (@n)", ("@n", s.Name));

        public void Update(Supplier s)
            => _db.Execute("UPDATE suppliers SET name=@n WHERE id=@id", ("@n", s.Name), ("@id", s.Id));

        public bool Delete(int supplierId)
        {
            long invoices = Convert.ToInt64(_db.Scalar(
                "SELECT COUNT(*) FROM purchase_invoices WHERE supplier_id=@id", ("@id", supplierId)));
            if (invoices > 0) return false;

            _db.Execute("DELETE FROM suppliers WHERE id=@id", ("@id", supplierId));
            return true;
        }

        public decimal TotalOutstanding()
        {
            object v = _db.Scalar(
                "SELECT COALESCE(SUM(CASE WHEN total - amount_paid > 0 THEN total - amount_paid ELSE 0 END), 0) " +
                "FROM purchase_invoices");
            return v == null || v == DBNull.Value ? 0m : decimal.Round(Convert.ToDecimal(v), 2);
        }

        // ---------------- Invoices ----------------

        public IReadOnlyList<PurchaseInvoice> GetInvoices(int supplierId)
            => _db.Query(
                "SELECT " + InvoiceCols + ", s.name AS supplier_name " + InvoiceFrom +
                "WHERE i.supplier_id=@s ORDER BY i.invoice_date DESC, i.id DESC",
                MapInvoice, ("@s", supplierId));

        public IReadOnlyList<PurchaseInvoice> SearchInvoices(int? supplierId, string term, int limit = 200)
        {
            term = (term ?? "").Trim();

            // The supplier filter and the text filter are independent: either, both or neither. Building
            // the WHERE from the parts that are actually present keeps one query serving all four cases
            // rather than four near-identical ones drifting apart.
            var where = new List<string>();
            var ps = new List<(string, object)> { ("@lim", limit) };
            if (supplierId.HasValue)
            {
                where.Add("i.supplier_id = @s");
                ps.Add(("@s", supplierId.Value));
            }
            if (term.Length > 0)
            {
                where.Add("(i.invoice_number LIKE @t OR i.representative LIKE @t OR s.name LIKE @t)");
                ps.Add(("@t", "%" + term + "%"));
            }

            string sql =
                "SELECT " + InvoiceCols + ", s.name AS supplier_name " + InvoiceFrom +
                (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                "ORDER BY i.invoice_date DESC, i.id DESC LIMIT @lim";

            return _db.Query(sql, MapInvoice, ps.ToArray());
        }

        public IReadOnlyList<PurchaseInvoice> InvoicesInRange(DateTime fromInclusive, DateTime toExclusive)
            => _db.Query(
                "SELECT " + InvoiceCols + ", s.name AS supplier_name " + InvoiceFrom +
                "WHERE i.invoice_date >= @f AND i.invoice_date < @t " +
                "ORDER BY i.invoice_date DESC, i.id DESC",
                MapInvoice, ("@f", DateParam(fromInclusive)), ("@t", DateParam(toExclusive)));

        public PurchaseInvoice GetInvoice(int invoiceId)
        {
            PurchaseInvoice invoice = _db.QueryOne(
                "SELECT " + InvoiceCols + ", s.name AS supplier_name " + InvoiceFrom + "WHERE i.id=@id",
                MapInvoice, ("@id", invoiceId));
            if (invoice == null) return null;

            invoice.Lines.AddRange(_db.Query(
                "SELECT id, invoice_id, item_id, item_name, quantity_boxes, strips_per_box, " +
                "       box_purchase_price, box_selling_price, expiry_date, batch_number, stock_batch_id " +
                "FROM purchase_invoice_lines WHERE invoice_id=@id ORDER BY id",
                MapLine, ("@id", invoiceId)));
            return invoice;
        }

        /// <summary>
        /// The invoice, its lines and the stock they put on the shelf, written together or not at all.
        /// The batch is written here rather than through the inventory service because both have to sit
        /// inside this one transaction — a rolled-back invoice must take its stock with it, or the
        /// pharmacy ends up selling boxes it has no record of owing for.
        /// </summary>
        public int CreateInvoice(PurchaseInvoice invoice, int actingUserId)
        {
            using (DbConnection conn = _db.OpenConnection())
            using (DbTransaction tx = conn.BeginTransaction())
            {
                try
                {
                    int invoiceId = InsertScalar(conn, tx,
                        "INSERT INTO purchase_invoices " +
                        "  (supplier_id, representative, invoice_number, invoice_date, total, amount_paid, user_id) " +
                        "VALUES (@s, @r, @n, @d, @t, @p, @u)",
                        ("@s", invoice.SupplierId), ("@r", Db.Text(invoice.Representative)),
                        ("@n", Db.Text(invoice.InvoiceNumber)), ("@d", DateParam(invoice.InvoiceDate)),
                        ("@t", Db.Money(invoice.Total)), ("@p", Db.Money(invoice.AmountPaid)),
                        ("@u", actingUserId));

                    foreach (PurchaseInvoiceLine line in invoice.Lines)
                    {
                        int stripsPerBox = Math.Max(1, line.StripsPerBox);
                        int unitsPerStrip = Math.Max(1, UnitsPerStripOf(conn, tx, line.ItemId));

                        int batchId = InsertScalar(conn, tx,
                            "INSERT INTO stock_batches (item_id, quantity_units, expiry_date, batch_number, " +
                            "  strips_per_box, units_per_strip, box_purchase_price, box_selling_price, is_disposed) " +
                            "VALUES (@i, @q, @e, @bn, @spb, @ups, @bp, @bs, 0)",
                            ("@i", line.ItemId),
                            ("@q", line.QuantityBoxes * stripsPerBox * unitsPerStrip),
                            ("@e", DateParamN(line.ExpiryDate)), ("@bn", Db.Text(line.BatchNumber)),
                            ("@spb", stripsPerBox), ("@ups", unitsPerStrip),
                            ("@bp", Db.Money(line.BoxPurchasePrice)), ("@bs", Db.Money(line.BoxSellingPrice)));

                        InsertScalar(conn, tx,
                            "INSERT INTO purchase_invoice_lines " +
                            "  (invoice_id, item_id, item_name, quantity_boxes, strips_per_box, " +
                            "   box_purchase_price, box_selling_price, expiry_date, batch_number, stock_batch_id) " +
                            "VALUES (@inv, @i, @nm, @q, @spb, @bp, @bs, @e, @bn, @batch)",
                            ("@inv", invoiceId), ("@i", line.ItemId), ("@nm", line.ItemName ?? ""),
                            ("@q", line.QuantityBoxes), ("@spb", stripsPerBox),
                            ("@bp", Db.Money(line.BoxPurchasePrice)), ("@bs", Db.Money(line.BoxSellingPrice)),
                            ("@e", DateParamN(line.ExpiryDate)), ("@bn", Db.Text(line.BatchNumber)),
                            ("@batch", batchId));

                        // The newest shipment is what the POS sells at, so the item mirrors this batch —
                        // the same rule ReceiveStock applies, applied here so it lands inside the transaction.
                        MirrorOntoItem(conn, tx, line.ItemId, stripsPerBox, unitsPerStrip,
                            line.BoxPurchasePrice, line.BoxSellingPrice);
                        RepriceEveryBatch(conn, tx, line.ItemId,
                            PerUnit(line.BoxSellingPrice, stripsPerBox, unitsPerStrip));

                        line.StockBatchId = batchId;
                        line.InvoiceId = invoiceId;
                    }

                    // Money handed over at the door is a payment like any other, so it is filed as one
                    // rather than living only in the invoice's amount_paid.
                    if (invoice.AmountPaid > 0m)
                        Exec(conn, tx,
                            "INSERT INTO supplier_payments (supplier_id, invoice_id, amount, user_id, note, payment_method) " +
                            "VALUES (@s, @inv, @a, @u, @note, @pm)",
                            ("@s", invoice.SupplierId), ("@inv", invoiceId), ("@a", Db.Money(invoice.AmountPaid)),
                            ("@u", actingUserId), ("@note", "دفعة عند استلام الفاتورة"),
                            ("@pm", Db.Text(invoice.PaymentMethod ?? "Cash")));

                    SqliteAuditRepository.AddOnConnection(conn, tx, new AuditEntry
                    {
                        UserId = actingUserId,
                        Action = "PurchaseInvoice",
                        Entity = "purchase_invoices",
                        EntityId = invoiceId,
                        Details = invoice.Lines.Count + " line(s), total " + invoice.Total.ToString("0.00") +
                                  ", paid " + invoice.AmountPaid.ToString("0.00")
                    });

                    tx.Commit();
                    invoice.Id = invoiceId;
                    return invoiceId;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        /// <summary>
        /// Corrects an invoice that was filed wrongly, moving the shelf with it (V2.3).
        ///
        /// The awkward part is that the stock this invoice created has been sold from since. The batch
        /// row no longer holds what was delivered — it holds what is LEFT — so the correction is applied
        /// as a difference: a line dropping from 10 boxes to 8 removes two boxes' worth of units from
        /// whatever remains. Writing the new quantity straight into the batch would quietly put every
        /// unit sold since the delivery back on the shelf.
        ///
        /// Prices are mirrored onto the drug itself only when this line is still the item's newest
        /// shipment. Correcting a typo on a six-month-old invoice must not stamp that invoice's prices
        /// over the ones the POS is quoting today.
        /// </summary>
        public decimal UpdateInvoice(PurchaseInvoice edited, int actingUserId)
        {
            if (edited == null) throw new ArgumentNullException(nameof(edited));

            using (DbConnection conn = _db.OpenConnection())
            using (DbTransaction tx = conn.BeginTransaction())
            {
                try
                {
                    // Read the invoice as it stands right now, inside the transaction: every check below
                    // is against what is on file at this moment, not what the screen showed when it opened.
                    int storedSupplierId = 0;
                    decimal amountPaid = 0m;
                    bool exists = false;
                    ForEach(conn, tx, "SELECT supplier_id, amount_paid FROM purchase_invoices WHERE id=@id",
                        r =>
                        {
                            storedSupplierId = Db.GetInt(r, "supplier_id");
                            amountPaid = Db.GetMoney(r, "amount_paid");
                            exists = true;
                        },
                        ("@id", edited.Id));
                    if (!exists) throw new ValidationException("الفاتورة غير موجودة.");

                    var stored = new Dictionary<int, StoredLine>();
                    ForEach(conn, tx,
                        "SELECT l.id, l.item_id, l.item_name, l.quantity_boxes, l.strips_per_box, " +
                        "       l.box_purchase_price, l.box_selling_price, l.stock_batch_id, " +
                        "       b.quantity_units, b.units_per_strip, b.is_disposed " +
                        "FROM purchase_invoice_lines l " +
                        "LEFT JOIN stock_batches b ON b.id = l.stock_batch_id " +
                        "WHERE l.invoice_id=@id",
                        r =>
                        {
                            var s = new StoredLine
                            {
                                Id = Db.GetInt(r, "id"),
                                ItemId = Db.GetInt(r, "item_id"),
                                ItemName = Db.GetString(r, "item_name"),
                                QuantityBoxes = Db.GetInt(r, "quantity_boxes"),
                                StripsPerBox = Db.GetInt(r, "strips_per_box"),
                                BoxPurchasePrice = Db.GetMoney(r, "box_purchase_price"),
                                BoxSellingPrice = Db.GetMoney(r, "box_selling_price"),
                                BatchId = Db.GetIntN(r, "stock_batch_id"),
                                BatchRemaining = Db.GetIntN(r, "quantity_units"),
                                BatchUnitsPerStrip = Db.GetIntN(r, "units_per_strip"),
                                BatchDisposed = (Db.GetIntN(r, "is_disposed") ?? 0) != 0
                            };
                            stored[s.Id] = s;
                        },
                        ("@id", edited.Id));

                    var kept = new HashSet<int>();
                    foreach (PurchaseInvoiceLine line in edited.Lines)
                    {
                        StoredLine before;
                        if (line.Id > 0 && stored.TryGetValue(line.Id, out before))
                        {
                            kept.Add(line.Id);
                            CorrectLine(conn, tx, before, line);
                        }
                        else
                        {
                            // A medicine the invoice was missing: it arrives now exactly as it would have
                            // on the day, with a batch of its own.
                            line.Id = 0;
                            line.InvoiceId = edited.Id;
                            WriteNewLine(conn, tx, edited.Id, line);
                        }
                    }

                    foreach (StoredLine gone in stored.Values)
                        if (!kept.Contains(gone.Id)) RemoveLine(conn, tx, gone);

                    decimal total = decimal.Round(edited.Lines.Sum(l => l.LineTotal), 2);
                    if (total < amountPaid)
                        throw new ValidationException(
                            "الإجمالي الجديد (" + total.ToString("0.00") + ") أقل من المبلغ المدفوع بالفعل (" +
                            amountPaid.ToString("0.00") + "). عدّل الدفعات أولاً.");

                    Exec(conn, tx,
                        "UPDATE purchase_invoices SET supplier_id=@s, representative=@r, invoice_number=@n, " +
                        "  invoice_date=@d, total=@t WHERE id=@id",
                        ("@s", edited.SupplierId), ("@r", Db.Text(edited.Representative)),
                        ("@n", Db.Text(edited.InvoiceNumber)), ("@d", DateParam(edited.InvoiceDate)),
                        ("@t", Db.Money(total)), ("@id", edited.Id));

                    // Money already handed over was paid to a company, not to an invoice number. Moving the
                    // invoice to the company it actually came from has to take its payments with it, or the
                    // two companies' balances both go wrong in opposite directions.
                    if (edited.SupplierId != storedSupplierId)
                        Exec(conn, tx, "UPDATE supplier_payments SET supplier_id=@s WHERE invoice_id=@id",
                            ("@s", edited.SupplierId), ("@id", edited.Id));

                    SqliteAuditRepository.AddOnConnection(conn, tx, new AuditEntry
                    {
                        UserId = actingUserId,
                        Action = "EditPurchaseInvoice",
                        Entity = "purchase_invoices",
                        EntityId = edited.Id,
                        Details = edited.Lines.Count + " line(s), total " + total.ToString("0.00") +
                                  (edited.SupplierId != storedSupplierId
                                      ? ", moved to supplier " + edited.SupplierId
                                      : "")
                    });

                    tx.Commit();
                    edited.Total = total;
                    edited.AmountPaid = amountPaid;
                    return total;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        /// <summary>
        /// Brings one stored line into line with what the user corrected it to, and moves its batch by
        /// the difference. Refuses a reduction that the shelf cannot pay for — units already sold are
        /// gone, and an invoice edit is not a way to un-sell them.
        /// </summary>
        private static void CorrectLine(DbConnection conn, DbTransaction tx, StoredLine before, PurchaseInvoiceLine now)
        {
            int stripsPerBox = Math.Max(1, now.StripsPerBox);
            int unitsPerStrip = Math.Max(1, before.BatchUnitsPerStrip ?? UnitsPerStripOf(conn, tx, before.ItemId));

            int deliveredBefore = before.QuantityBoxes * Math.Max(1, before.StripsPerBox) * unitsPerStrip;
            int deliveredNow = now.QuantityBoxes * stripsPerBox * unitsPerStrip;
            int delta = deliveredNow - deliveredBefore;

            if (before.BatchId.HasValue && !before.BatchDisposed)
            {
                int remaining = before.BatchRemaining ?? 0;
                if (remaining + delta < 0)
                    throw new ValidationException(
                        "لا يمكن تخفيض كمية \"" + before.ItemName + "\" إلى هذا الحد: لم يتبقَّ من الدفعة سوى " +
                        remaining + " وحدة، والباقي تم بيعه أو صرفه.");

                Exec(conn, tx,
                    "UPDATE stock_batches SET quantity_units = quantity_units + @delta, strips_per_box=@spb, " +
                    "  box_purchase_price=@bp, box_selling_price=@bs, expiry_date=@e, batch_number=@bn " +
                    "WHERE id=@id",
                    ("@delta", delta), ("@spb", stripsPerBox),
                    ("@bp", Db.Money(now.BoxPurchasePrice)), ("@bs", Db.Money(now.BoxSellingPrice)),
                    ("@e", DateParamN(now.ExpiryDate)), ("@bn", Db.Text(now.BatchNumber)),
                    ("@id", before.BatchId.Value));
            }
            else if (delta != 0)
            {
                // No batch left to move: it was disposed, or the link was lost. The paperwork can still be
                // corrected, but silently inventing stock to match it would be worse than saying so.
                throw new ValidationException(
                    "لا يمكن تعديل كمية \"" + before.ItemName + "\": دفعتها لم تعد على الرف (تم إتلافها أو حذفها).");
            }

            Exec(conn, tx,
                "UPDATE purchase_invoice_lines SET quantity_boxes=@q, strips_per_box=@spb, " +
                "  box_purchase_price=@bp, box_selling_price=@bs, expiry_date=@e, batch_number=@bn " +
                "WHERE id=@id",
                ("@q", now.QuantityBoxes), ("@spb", stripsPerBox),
                ("@bp", Db.Money(now.BoxPurchasePrice)), ("@bs", Db.Money(now.BoxSellingPrice)),
                ("@e", DateParamN(now.ExpiryDate)), ("@bn", Db.Text(now.BatchNumber)),
                ("@id", before.Id));

            now.ItemId = before.ItemId;
            now.StockBatchId = before.BatchId;

            // Only a correction to the CURRENT shipment may touch the drug's own prices. Fixing a typo on
            // an old invoice must leave today's shelf price alone — that price came from a later delivery.
            bool sellingChanged = now.BoxSellingPrice != before.BoxSellingPrice;
            bool buyingChanged = now.BoxPurchasePrice != before.BoxPurchasePrice ||
                                 stripsPerBox != Math.Max(1, before.StripsPerBox);
            if ((sellingChanged || buyingChanged) && before.BatchId.HasValue &&
                IsNewestBatch(conn, tx, before.ItemId, before.BatchId.Value))
            {
                MirrorOntoItem(conn, tx, before.ItemId, stripsPerBox, unitsPerStrip,
                    now.BoxPurchasePrice, now.BoxSellingPrice);
                if (sellingChanged)
                    RepriceEveryBatch(conn, tx, before.ItemId,
                        PerUnit(now.BoxSellingPrice, stripsPerBox, unitsPerStrip));
            }
        }

        /// <summary>
        /// Takes a line off the invoice and its batch off the shelf. Refuses once anything has been sold
        /// from that batch: the sale records which batch it came out of, and deleting the batch would
        /// leave those sales pointing at nothing. A delivery that has been sold from is a delivery that
        /// happened — correct its quantity instead of pretending it never arrived.
        /// </summary>
        private static void RemoveLine(DbConnection conn, DbTransaction tx, StoredLine line)
        {
            if (line.BatchId.HasValue)
            {
                long allocations = Convert.ToInt64(ScalarOn(conn, tx,
                    "SELECT COUNT(*) FROM sale_line_allocations WHERE batch_id=@b", ("@b", line.BatchId.Value)));
                if (allocations > 0)
                    throw new ValidationException(
                        "لا يمكن حذف سطر \"" + line.ItemName + "\": تم البيع من دفعته. عدّل الكمية بدل حذف السطر.");
            }

            // The line points at the batch, so it goes first.
            Exec(conn, tx, "DELETE FROM purchase_invoice_lines WHERE id=@id", ("@id", line.Id));
            if (line.BatchId.HasValue && !line.BatchDisposed)
                Exec(conn, tx, "DELETE FROM stock_batches WHERE id=@id", ("@id", line.BatchId.Value));
        }

        /// <summary>A medicine added to an invoice after the fact: the same batch-and-line pair, and the
        /// same repricing, that filing the delivery in the first place would have written.</summary>
        private void WriteNewLine(DbConnection conn, DbTransaction tx, int invoiceId, PurchaseInvoiceLine line)
        {
            int stripsPerBox = Math.Max(1, line.StripsPerBox);
            int unitsPerStrip = Math.Max(1, UnitsPerStripOf(conn, tx, line.ItemId));

            int batchId = InsertScalar(conn, tx,
                "INSERT INTO stock_batches (item_id, quantity_units, expiry_date, batch_number, " +
                "  strips_per_box, units_per_strip, box_purchase_price, box_selling_price, is_disposed) " +
                "VALUES (@i, @q, @e, @bn, @spb, @ups, @bp, @bs, 0)",
                ("@i", line.ItemId), ("@q", line.QuantityBoxes * stripsPerBox * unitsPerStrip),
                ("@e", DateParamN(line.ExpiryDate)), ("@bn", Db.Text(line.BatchNumber)),
                ("@spb", stripsPerBox), ("@ups", unitsPerStrip),
                ("@bp", Db.Money(line.BoxPurchasePrice)), ("@bs", Db.Money(line.BoxSellingPrice)));

            line.Id = InsertScalar(conn, tx,
                "INSERT INTO purchase_invoice_lines " +
                "  (invoice_id, item_id, item_name, quantity_boxes, strips_per_box, " +
                "   box_purchase_price, box_selling_price, expiry_date, batch_number, stock_batch_id) " +
                "VALUES (@inv, @i, @nm, @q, @spb, @bp, @bs, @e, @bn, @batch)",
                ("@inv", invoiceId), ("@i", line.ItemId), ("@nm", line.ItemName ?? ""),
                ("@q", line.QuantityBoxes), ("@spb", stripsPerBox),
                ("@bp", Db.Money(line.BoxPurchasePrice)), ("@bs", Db.Money(line.BoxSellingPrice)),
                ("@e", DateParamN(line.ExpiryDate)), ("@bn", Db.Text(line.BatchNumber)),
                ("@batch", batchId));

            MirrorOntoItem(conn, tx, line.ItemId, stripsPerBox, unitsPerStrip,
                line.BoxPurchasePrice, line.BoxSellingPrice);
            RepriceEveryBatch(conn, tx, line.ItemId, PerUnit(line.BoxSellingPrice, stripsPerBox, unitsPerStrip));

            line.StockBatchId = batchId;
            line.InvoiceId = invoiceId;
        }

        public void AddPayment(SupplierPayment p)
        {
            using (DbConnection conn = _db.OpenConnection())
            using (DbTransaction tx = conn.BeginTransaction())
            {
                try
                {
                    Exec(conn, tx,
                        "INSERT INTO supplier_payments (supplier_id, invoice_id, amount, user_id, note, payment_method) " +
                        "VALUES (@s, @i, @a, @u, @n, @pm)",
                        ("@s", p.SupplierId), ("@i", p.InvoiceId), ("@a", Db.Money(p.Amount)),
                        ("@u", p.UserId), ("@n", Db.Text(p.Note)), ("@pm", Db.Text(p.PaymentMethod ?? "Cash")));

                    Exec(conn, tx,
                        "UPDATE purchase_invoices SET amount_paid = amount_paid + @a WHERE id=@i",
                        ("@a", Db.Money(p.Amount)), ("@i", p.InvoiceId));

                    tx.Commit();
                }
                catch { tx.Rollback(); throw; }
            }
        }

        public IReadOnlyList<SupplierPayment> GetPayments(int invoiceId)
            => _db.Query(
                "SELECT id, supplier_id, invoice_id, amount, user_id, note, payment_method, created_at " +
                "FROM supplier_payments WHERE invoice_id=@i ORDER BY created_at, id",
                MapPayment, ("@i", invoiceId));

        /// <summary>
        /// Every payment made to any supplier in the period. The shift report needs this to work out
        /// what is actually left in the drawer: money handed to a distributor leaves the till exactly
        /// the way an employee's cash expense does, and until V2.3 nothing outside this repository ever
        /// read the table, so those payments showed up as an unexplained shortage against the cashier.
        /// </summary>
        public IReadOnlyList<SupplierPayment> PaymentsInRange(DateTime fromInclusive, DateTime toExclusive)
            => _db.Query(
                "SELECT id, supplier_id, invoice_id, amount, user_id, note, payment_method, created_at " +
                "FROM supplier_payments WHERE created_at >= @f AND created_at < @t ORDER BY created_at, id",
                MapPayment, ("@f", Db.Time(fromInclusive)), ("@t", Db.Time(toExclusive)));

        // ---------------- helpers ----------------

        /// <summary>One invoice line as it is stored, together with the state of the batch it created —
        /// what is LEFT of it, not what was delivered. Correcting the line needs both.</summary>
        private sealed class StoredLine
        {
            public int Id;
            public int ItemId;
            public string ItemName;
            public int QuantityBoxes;
            public int StripsPerBox;
            public decimal BoxPurchasePrice;
            public decimal BoxSellingPrice;
            public int? BatchId;
            public int? BatchRemaining;
            public int? BatchUnitsPerStrip;
            public bool BatchDisposed;
        }

        /// <summary>True when this batch is the item's most recent shipment — the one whose prices the
        /// catalog is currently quoting, and so the only one a price correction may propagate from.</summary>
        private static bool IsNewestBatch(DbConnection conn, DbTransaction tx, int itemId, int batchId)
        {
            object v = ScalarOn(conn, tx,
                "SELECT MAX(id) FROM stock_batches WHERE item_id=@i AND is_disposed=0", ("@i", itemId));
            return v != null && v != DBNull.Value && Convert.ToInt32(v) == batchId;
        }

        /// <summary>The newest shipment is what the POS sells at, so the drug mirrors it — the same rule
        /// receiving stock by hand applies, kept here in one place for filing, correcting and adding.</summary>
        private static void MirrorOntoItem(DbConnection conn, DbTransaction tx, int itemId,
            int stripsPerBox, int unitsPerStrip, decimal boxPurchase, decimal boxSelling)
            => Exec(conn, tx,
                "UPDATE items SET strips_per_box=@spb, purchase_price=@pp, selling_price=@sp, " +
                "  manual_price=0, updated_at=@now WHERE id=@i",
                ("@spb", stripsPerBox),
                ("@pp", Db.Money(PerUnit(boxPurchase, stripsPerBox, unitsPerStrip))),
                ("@sp", Db.Money(PerUnit(boxSelling, stripsPerBox, unitsPerStrip))),
                ("@now", Db.Time(DateTime.Now)), ("@i", itemId));

        /// <summary>A new selling price is the drug's price everywhere, including the boxes already on the
        /// shelf: the customer pays today's price whichever crate the strip is lifted out of.</summary>
        private static void RepriceEveryBatch(DbConnection conn, DbTransaction tx, int itemId, decimal perUnitSelling)
            => Exec(conn, tx,
                "UPDATE stock_batches SET box_selling_price = @p * strips_per_box * units_per_strip " +
                "WHERE item_id=@i AND is_disposed=0",
                ("@p", Db.Money(perUnitSelling)), ("@i", itemId));

        /// <summary>Reads rows inside an open transaction, so a check and the write it guards see the
        /// same database.</summary>
        private static void ForEach(DbConnection conn, DbTransaction tx, string sql,
            Action<DbDataReader> onRow, params (string, object)[] ps)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ps);
                using (DbDataReader r = cmd.ExecuteReader())
                    while (r.Read()) onRow(r);
            }
        }

        private static object ScalarOn(DbConnection conn, DbTransaction tx, string sql, params (string, object)[] ps)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ps);
                return cmd.ExecuteScalar();
            }
        }

        /// <summary>Single units in one strip, as the item is packaged. Read inside the transaction so
        /// the quantity written to the batch matches the item as it stands at that moment.</summary>
        private static int UnitsPerStripOf(DbConnection conn, DbTransaction tx, int itemId)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT units_per_strip FROM items WHERE id=@i";
                cmd.Transaction = tx;
                DbExec.AddParams(cmd, ("@i", itemId));
                object v = cmd.ExecuteScalar();
                return v == null || v == DBNull.Value ? 1 : Convert.ToInt32(v);
            }
        }

        /// <summary>Box price divided down to one single unit — what the item stores and the POS sells at.</summary>
        private static decimal PerUnit(decimal boxPrice, int stripsPerBox, int unitsPerStrip)
        {
            int unitsPerBox = Math.Max(1, stripsPerBox) * Math.Max(1, unitsPerStrip);
            return decimal.Round(boxPrice / unitsPerBox, 4);
        }

        private static string DateParam(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static object DateParamN(DateTime? d)
            => d.HasValue ? (object)d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value;

        private static Supplier MapSupplier(DbDataReader r) => new Supplier
        {
            Id = Db.GetInt(r, "id"),
            Name = Db.GetString(r, "name"),
            CreatedAt = Db.GetTime(r, "created_at"),
            Outstanding = Db.GetMoney(r, "outstanding"),
            InvoiceCount = Db.GetInt(r, "invoice_count")
        };

        private static PurchaseInvoice MapInvoice(DbDataReader r) => new PurchaseInvoice
        {
            Id = Db.GetInt(r, "id"),
            SupplierId = Db.GetInt(r, "supplier_id"),
            SupplierName = Db.GetStringN(r, "supplier_name"),
            Representative = Db.GetStringN(r, "representative"),
            InvoiceNumber = Db.GetStringN(r, "invoice_number"),
            InvoiceDate = Db.GetTime(r, "invoice_date"),
            Total = Db.GetMoney(r, "total"),
            AmountPaid = Db.GetMoney(r, "amount_paid"),
            UserId = Db.GetInt(r, "user_id"),
            CreatedAt = Db.GetTime(r, "created_at"),
            UserName = Db.GetStringN(r, "user_name"),
            LineCount = Db.GetInt(r, "line_count")
        };

        private static PurchaseInvoiceLine MapLine(DbDataReader r) => new PurchaseInvoiceLine
        {
            Id = Db.GetInt(r, "id"),
            InvoiceId = Db.GetInt(r, "invoice_id"),
            ItemId = Db.GetInt(r, "item_id"),
            ItemName = Db.GetString(r, "item_name"),
            QuantityBoxes = Db.GetInt(r, "quantity_boxes"),
            StripsPerBox = Db.GetInt(r, "strips_per_box"),
            BoxPurchasePrice = Db.GetMoney(r, "box_purchase_price"),
            BoxSellingPrice = Db.GetMoney(r, "box_selling_price"),
            ExpiryDate = Db.GetTimeN(r, "expiry_date"),
            BatchNumber = Db.GetStringN(r, "batch_number"),
            StockBatchId = Db.GetIntN(r, "stock_batch_id")
        };

        private static SupplierPayment MapPayment(DbDataReader r) => new SupplierPayment
        {
            Id = Db.GetInt(r, "id"),
            SupplierId = Db.GetInt(r, "supplier_id"),
            InvoiceId = Db.GetInt(r, "invoice_id"),
            Amount = Db.GetMoney(r, "amount"),
            UserId = Db.GetInt(r, "user_id"),
            Note = Db.GetStringN(r, "note"),
            PaymentMethod = Db.GetStringN(r, "payment_method"),
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
                cmd.CommandText = "SELECT " + _db.LastInsertIdSql;
                cmd.Transaction = tx;
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
