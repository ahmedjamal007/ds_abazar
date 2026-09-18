using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>Customer records and the debt ledger (FR-DBT-*). Cashiers may record customers and payments.</summary>
    public class DebtService
    {
        private readonly ICustomerRepository _customers;
        private readonly IDebtStore _debts;
        private readonly IAuditRepository _audit;

        /// <param name="audit">Optional so the existing unit tests, which construct this directly and do
        /// not assert on the audit log, keep compiling. The app always supplies it.</param>
        public DebtService(ICustomerRepository customers, IDebtStore debts, IAuditRepository audit = null)
        {
            _customers = customers;
            _debts = debts;
            _audit = audit;
        }

        /// <summary>
        /// Opens a customer account. Any signed-in member of staff may do this — a cashier meeting a new
        /// credit customer at the till is the everyday case, and that stays true.
        ///
        /// It takes the actor purely so the act is attributable: this was the one mutating method in the
        /// service layer with no <see cref="User"/> at all, so customer records appeared with no record
        /// of who created them and no caller could have supplied one.
        /// </summary>
        public int CreateCustomer(User actor, string name, string phone)
        {
            Guard.RequireUser(actor, "يجب تسجيل الدخول.");
            if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("اسم العميل مطلوب.");

            int id = _customers.Add(new Customer
            {
                Name = name.Trim(),
                Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim()
            });
            _audit?.Log(actor, "CreateCustomer", "customers", id, name.Trim());
            return id;
        }

        /// <summary>Corrects a customer's name and phone (Admin only — a cashier may add a customer at
        /// the till, but editing the ledger's account holders is the manager's).</summary>
        public void UpdateCustomer(User actingUser, int customerId, string name, string phone)
        {
            RequireAdmin(actingUser);
            if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("اسم العميل مطلوب.");

            Customer customer = _customers.GetById(customerId);
            if (customer == null) throw new ValidationException("العميل غير موجود.");

            customer.Name = name.Trim();
            customer.Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
            _customers.Update(customer);
        }

        /// <summary>
        /// Deletes a customer (Admin only). Refuses while money is outstanding — a debt is not settled by
        /// deleting who owes it — and returns false when they have invoices or ledger entries, which stay
        /// so the history keeps naming who bought and who paid.
        /// </summary>
        public bool DeleteCustomer(User actingUser, int customerId)
        {
            RequireAdmin(actingUser);
            Customer customer = _customers.GetById(customerId);
            if (customer == null) throw new ValidationException("العميل غير موجود.");
            if (customer.Balance != 0)
                throw new ValidationException("لا يمكن حذف عميل عليه رصيد — سوِّ الحساب أولاً.");

            return _customers.Delete(customerId);
        }

        /// <summary>
        /// Customers matching the term. The cap is deliberately generous rather than absent — the grid
        /// is not paged — and <see cref="SearchLimit"/> is public so the screen can say when it has been
        /// hit. A silent cut at 100 was how a pharmacy with a long book came to open a second account for
        /// a customer it already had, splitting that customer's debt across two records.
        /// </summary>
        public const int SearchLimit = 500;

        public IReadOnlyList<Customer> Search(string term) => _customers.Search(term, SearchLimit);
        public Customer Get(int id) => _customers.GetById(id);
        public IReadOnlyList<Customer> WithDebt() => _customers.GetWithDebt();

        /// <summary>
        /// Records a repayment against a customer's balance (FR-DBT-03).
        ///
        /// Paying more than is owed is deliberately allowed and leaves a NEGATIVE balance — store credit
        /// (D-06). The supplier ledger refuses the equivalent, and the asymmetry is intentional rather
        /// than an oversight: a supplier handed too much is a matter to settle with that company, while a
        /// customer who leaves money on account is an everyday counter transaction. Negative balances are
        /// excluded from <see cref="TotalOutstanding"/>, so credit never offsets another customer's debt.
        /// </summary>
        public DebtTransaction RecordPayment(User user, int customerId, decimal amount, string note = null)
        {
            if (user == null) throw new PermissionDeniedException("يجب تسجيل الدخول.");
            if (amount <= 0) throw new ValidationException("المبلغ يجب أن يكون أكبر من صفر.");
            if (_customers.GetById(customerId) == null) throw new ValidationException("العميل غير موجود.");
            return _debts.RecordPayment(customerId, decimal.Round(amount, 2), user.Id, note);
        }

        /// <summary>Manually adds to a customer's debt outside a sale (Admin only).</summary>
        public DebtTransaction RecordManualCharge(User user, int customerId, decimal amount, string note)
        {
            Guard.RequireAdmin(user, "إضافة دين يدوي متاح للمدير فقط.");
            if (amount <= 0) throw new ValidationException("المبلغ يجب أن يكون أكبر من صفر.");
            if (_customers.GetById(customerId) == null) throw new ValidationException("العميل غير موجود.");
            return _debts.RecordManualCharge(customerId, decimal.Round(amount, 2), user.Id, note);
        }

        /// <summary>Statement rows with running balance (FR-DBT-04).</summary>
        public IReadOnlyList<StatementRow> GetStatement(int customerId)
            => DebtLedger.BuildStatement(_customers.GetTransactions(customerId));

        /// <summary>Total money owed across all customers (FR-RPT-04).</summary>
        public decimal TotalOutstanding() => DebtLedger.TotalOutstanding(_customers.GetAll());

        private static void RequireAdmin(User user)
            => Guard.RequireAdmin(user, "تعديل بيانات العملاء أو حذفهم متاح للمدير فقط.");
    }
}
