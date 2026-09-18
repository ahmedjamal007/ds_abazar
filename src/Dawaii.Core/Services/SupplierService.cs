using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Suppliers, their invoices and what the pharmacy still owes them (V2.1 "الموردون").
    ///
    /// This is the buying side of the same shape the customer ledger has on the selling side: a company,
    /// the invoices filed against it, and a balance that is the sum of what those invoices still owe.
    /// The difference is direction — a customer's balance is money coming in, a supplier's is money
    /// going out — so the two are deliberately kept in separate tables rather than sharing one ledger
    /// that would have to be read with a sign convention in mind.
    ///
    /// The buying side is what a "موظف ذو امتيازات" is for from V2.2 — they no longer reach the catalog
    /// or the stockroom, and place orders instead, which is how stock now arrives for them. Deleting a
    /// supplier stays the manager's.
    /// </summary>
    public class SupplierService
    {
        private readonly ISupplierRepository _suppliers;
        private readonly IItemRepository _items;
        private readonly IAuditRepository _audit;

        public SupplierService(ISupplierRepository suppliers, IItemRepository items, IAuditRepository audit)
        {
            _suppliers = suppliers;
            _items = items;
            _audit = audit;
        }

        // ---------------- Companies ----------------

        /// <summary>Opens a supplier. The company name is the only thing asked for — everything else
        /// worth knowing changes from one delivery to the next and belongs on the invoice.</summary>
        public int CreateSupplier(User actor, string name)
        {
            RequireBuyer(actor);
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ValidationException("اسم الشركة مطلوب.");

            int id = _suppliers.Add(new Supplier { Name = name });
            _audit.Log(actor, "CreateSupplier", "suppliers", id, name);
            return id;
        }

        public void RenameSupplier(User actor, int supplierId, string name)
        {
            RequireBuyer(actor);
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ValidationException("اسم الشركة مطلوب.");

            Supplier supplier = _suppliers.GetById(supplierId);
            if (supplier == null) throw new ValidationException("الشركة غير موجودة.");

            supplier.Name = name;
            _suppliers.Update(supplier);
            _audit.Log(actor, "RenameSupplier", "suppliers", supplierId, name);
        }

        /// <summary>
        /// Deletes a supplier (Admin only). Refuses while money is outstanding — a payable is not
        /// settled by deleting who it is owed to — and returns false when invoices are filed against
        /// them, which stay so the stock's history keeps naming where it came from.
        /// </summary>
        public bool DeleteSupplier(User admin, int supplierId)
        {
            Guard.RequireAdmin(admin, "حذف الشركات متاح للمدير فقط.");
            Supplier supplier = _suppliers.GetById(supplierId);
            if (supplier == null) throw new ValidationException("الشركة غير موجودة.");
            if (supplier.Outstanding > 0m)
                throw new ValidationException("لا يمكن حذف شركة لها رصيد مستحق — سوِّ الحساب أولاً.");

            bool deleted = _suppliers.Delete(supplierId);
            if (deleted) _audit.Log(admin, "DeleteSupplier", "suppliers", supplierId, supplier.Name);
            return deleted;
        }

        /// <summary>Companies matching the term — see <see cref="DebtService.SearchLimit"/> for why the
        /// cap is stated rather than left at the repository default.</summary>
        public const int SearchLimit = 500;

        public IReadOnlyList<Supplier> Search(string term) => _suppliers.Search(term, SearchLimit);
        public IReadOnlyList<Supplier> WithOutstanding() => _suppliers.WithOutstanding();
        public Supplier Get(int id) => _suppliers.GetById(id);

        /// <summary>Everything the pharmacy still owes, across every company.</summary>
        public decimal TotalOutstanding() => _suppliers.TotalOutstanding();

        // ---------------- Invoices ----------------

        public IReadOnlyList<PurchaseInvoice> GetInvoices(int supplierId) => _suppliers.GetInvoices(supplierId);

        /// <summary>Finds invoices by number, representative or company name. Pass a supplier id to stay
        /// inside one company's file, or null to search every company — which is what is needed when all
        /// anyone has to go on is the number printed on the paper.</summary>
        public IReadOnlyList<PurchaseInvoice> SearchInvoices(int? supplierId, string term)
            => _suppliers.SearchInvoices(supplierId, term);
        /// <summary>The manager's purchases report: every order in the period, who placed it and when,
        /// and what is still owed on it. Admin only — an order names what the pharmacy committed and to
        /// whom, which is the manager's business rather than the person who typed it in.</summary>
        public IReadOnlyList<PurchaseInvoice> OrdersInRange(User admin, DateTime fromInclusive, DateTime toExclusive)
        {
            Guard.RequireAdmin(admin, "تقرير المشتريات متاح للمدير فقط.");
            return _suppliers.InvoicesInRange(fromInclusive, toExclusive);
        }

        public PurchaseInvoice GetInvoice(int invoiceId) => _suppliers.GetInvoice(invoiceId);
        public IReadOnlyList<SupplierPayment> GetPayments(int invoiceId) => _suppliers.GetPayments(invoiceId);

        /// <summary>
        /// Cash handed to suppliers in the period — money that physically left the till.
        ///
        /// The shift report subtracts this from the expected drawer cash. Before V2.3 it did not exist:
        /// paying a distributor 150,000 at the door left the report expecting 150,000 more than the
        /// drawer held, and the manager read that as the cashier being short.
        /// </summary>
        public decimal CashPaidToSuppliers(DateTime fromInclusive, DateTime toExclusive)
            => decimal.Round(
                _suppliers.PaymentsInRange(fromInclusive, toExclusive)
                          .Where(p => p.IsCash)
                          .Sum(p => p.Amount), 2);

        /// <summary>
        /// Files a delivery: the invoice, its lines, and a stock batch for every line. The total is the
        /// sum of the lines and is never accepted from the caller — an invoice whose stated total
        /// disagreed with what was actually received would put the payable and the shelf out of step.
        /// </summary>
        /// <param name="amountPaidNow">Money handed over on the spot. Zero for an unpaid delivery; the
        /// full total for one paid at the door; anything between for a part payment.</param>
        public PurchaseInvoice RecordInvoice(User actor, int supplierId, string representative,
            string invoiceNumber, DateTime invoiceDate, IReadOnlyList<PurchaseInvoiceLine> lines,
            decimal amountPaidNow = 0m)
        {
            RequireBuyer(actor);
            if (_suppliers.GetById(supplierId) == null) throw new ValidationException("الشركة غير موجودة.");
            if (lines == null || lines.Count == 0)
                throw new ValidationException("الفاتورة يجب أن تحتوي على صنف واحد على الأقل.");

            var invoice = new PurchaseInvoice
            {
                SupplierId = supplierId,
                Representative = Trimmed(representative),
                InvoiceNumber = Trimmed(invoiceNumber),
                InvoiceDate = invoiceDate.Date,
                UserId = actor.Id,
                CreatedAt = DateTime.Now
            };

            foreach (PurchaseInvoiceLine line in lines)
            {
                ValidateLine(line);
                Item item = _items.GetById(line.ItemId);
                if (item == null) throw new ValidationException("الصنف غير موجود.");

                // The name is snapshotted now: the invoice records what was delivered on the day, and
                // renaming a drug later must not rewrite the paperwork it arrived on.
                line.ItemName = item.DisplayName;
                invoice.Lines.Add(line);
            }

            invoice.Total = decimal.Round(invoice.Lines.Sum(l => l.LineTotal), 2);

            decimal paid = decimal.Round(amountPaidNow, 2);
            if (paid < 0m) throw new ValidationException("المبلغ المدفوع غير صالح.");
            if (paid > invoice.Total)
                throw new ValidationException("المبلغ المدفوع أكبر من إجمالي الفاتورة.");
            invoice.AmountPaid = paid;

            _suppliers.CreateInvoice(invoice, actor.Id);
            return invoice;
        }

        /// <summary>
        /// Corrects an invoice that was typed in wrongly (V2.3) — the company it was filed against, the
        /// representative, the number, the date, and the medicines on it.
        ///
        /// Until now a delivery could be filed once and never touched again, so a mistyped quantity or
        /// price was permanent and the only way out was a second invoice cancelling the first — leaving
        /// the company's file describing a delivery that never happened. Correcting it in place keeps
        /// the paperwork and the shelf saying the same thing.
        ///
        /// The total is still never accepted from the caller, and payments already recorded are left
        /// alone: what was handed over is a fact, and the repository refuses a correction that would
        /// drop the total below it.
        /// </summary>
        /// <param name="lines">The invoice as it should now read. A line carrying an <see cref="PurchaseInvoiceLine.Id"/>
        /// is an existing one being corrected; one without is a medicine being added; any stored line
        /// left out is removed, along with its batch, if nothing has been sold from it.</param>
        public PurchaseInvoice EditInvoice(User actor, int invoiceId, int supplierId, string representative,
            string invoiceNumber, DateTime invoiceDate, IReadOnlyList<PurchaseInvoiceLine> lines)
        {
            RequireBuyer(actor);
            if (_suppliers.GetById(supplierId) == null) throw new ValidationException("الشركة غير موجودة.");

            PurchaseInvoice current = _suppliers.GetInvoice(invoiceId);
            if (current == null) throw new ValidationException("الفاتورة غير موجودة.");
            if (lines == null || lines.Count == 0)
                throw new ValidationException("الفاتورة يجب أن تحتوي على صنف واحد على الأقل.");

            var edited = new PurchaseInvoice
            {
                Id = invoiceId,
                SupplierId = supplierId,
                Representative = Trimmed(representative),
                InvoiceNumber = Trimmed(invoiceNumber),
                InvoiceDate = invoiceDate.Date,
                UserId = current.UserId,
                CreatedAt = current.CreatedAt
            };

            foreach (PurchaseInvoiceLine line in lines)
            {
                ValidateLine(line);

                // A line already on file keeps the name it was delivered under — that snapshot is the
                // whole point of storing it. Only a medicine being added now needs one taken.
                if (line.Id <= 0)
                {
                    Item item = _items.GetById(line.ItemId);
                    if (item == null) throw new ValidationException("الصنف غير موجود.");
                    line.ItemName = item.DisplayName;
                }
                edited.Lines.Add(line);
            }

            _suppliers.UpdateInvoice(edited, actor.Id);
            return _suppliers.GetInvoice(invoiceId);
        }

        /// <summary>
        /// Records money paid against one invoice. Refuses to overpay: a supplier handed more than the
        /// invoice asks for is a credit to sort out with them, not something the ledger should absorb
        /// by quietly showing a negative payable.
        /// </summary>
        public void RecordPayment(User actor, int invoiceId, decimal amount, string note = null,
            string paymentMethod = "Cash")
        {
            RequireBuyer(actor);
            if (amount <= 0m) throw new ValidationException("المبلغ يجب أن يكون أكبر من صفر.");

            PurchaseInvoice invoice = _suppliers.GetInvoice(invoiceId);
            if (invoice == null) throw new ValidationException("الفاتورة غير موجودة.");

            decimal paid = decimal.Round(amount, 2);
            if (paid > invoice.Outstanding)
                throw new ValidationException(
                    "المبلغ أكبر من المتبقي على الفاتورة (" + invoice.Outstanding.ToString("0.00") + ").");

            _suppliers.AddPayment(new SupplierPayment
            {
                SupplierId = invoice.SupplierId,
                InvoiceId = invoiceId,
                Amount = paid,
                UserId = actor.Id,
                Note = Trimmed(note),
                PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "Cash" : paymentMethod.Trim(),
                CreatedAt = DateTime.Now
            });
            _audit.Log(actor, "SupplierPayment", "purchase_invoices", invoiceId, paid.ToString("0.00"));
        }

        /// <summary>Settles whatever is left on an invoice in one go — the "مدفوعة بالكامل" button.</summary>
        public void SettleInvoice(User actor, int invoiceId, string paymentMethod = "Cash")
        {
            PurchaseInvoice invoice = _suppliers.GetInvoice(invoiceId);
            if (invoice == null) throw new ValidationException("الفاتورة غير موجودة.");
            if (invoice.Outstanding <= 0m) return;      // already settled — nothing to record

            RecordPayment(actor, invoiceId, invoice.Outstanding, "سداد كامل المتبقي", paymentMethod);
        }

        // ---------------- guards ----------------

        private static void ValidateLine(PurchaseInvoiceLine line)
        {
            if (line.QuantityBoxes <= 0) throw new ValidationException("عدد العلب يجب أن يكون أكبر من صفر.");
            if (line.StripsPerBox <= 0) throw new ValidationException("عدد الأشرطة في العلبة يجب أن يكون أكبر من صفر.");
            if (line.BoxPurchasePrice < 0m) throw new ValidationException("سعر شراء العلبة غير صالح.");
            if (line.BoxSellingPrice < 0m) throw new ValidationException("سعر بيع العلبة غير صالح.");
        }

        /// <summary>The buying side is what a "موظف ذو امتيازات" is for (V2.2), alongside the manager.</summary>
        private static void RequireBuyer(User user)
        {
            if (user == null) throw new PermissionDeniedException("يجب تسجيل الدخول.");
            Guard.RequirePurchasingAccess(user,
                "إدارة المشتريات والموردين متاحة للمدير و\"موظف ذو امتيازات\" فقط.");
        }

        private static string Trimmed(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
