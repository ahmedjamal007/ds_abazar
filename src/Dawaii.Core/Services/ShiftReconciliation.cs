using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// What the drawer should hold at the end of a shift, and where the money went (V2.4).
    ///
    /// This arithmetic used to live inside the shift-report screen. It is the figure the pharmacist
    /// counts their cash against, so being unable to test it without standing up a WinForms control
    /// was the wrong trade — and the expense sums were written out twice in that screen, once for the
    /// tiles and once for the printed sheet, which is exactly how two numbers that must agree stop
    /// agreeing.
    /// </summary>
    public sealed class ShiftTill
    {
        /// <summary>Takings by channel. Only <see cref="Cash"/> is in the drawer; the rest went to a
        /// bank or wallet account and must never inflate what the cashier is expected to hand over.</summary>
        public decimal Cash { get; set; }
        public decimal Bankak { get; set; }
        public decimal Fawry { get; set; }
        public decimal Ocash { get; set; }

        /// <summary>Money advanced to staff. Medicine taken by staff is an expense but not a cash one.</summary>
        public decimal MoneyExpenses { get; set; }
        public decimal MedicineExpenses { get; set; }
        public decimal AllExpenses => MoneyExpenses + MedicineExpenses;

        /// <summary>Counter purchases, paid out of this drawer.</summary>
        public decimal Purchases { get; set; }

        /// <summary>Cash handed to medicine suppliers at the door. Bank transfers are excluded,
        /// because they never touch the till.</summary>
        public decimal SupplierCash { get; set; }

        /// <summary>
        /// What should be in the drawer: the cash that came in, less every cash outflow.
        ///
        /// Supplier cash was the outflow this formula did not know about for two versions. The
        /// supplier ledger arrived in V2.1 with its own table; this sum was written in V1.3 and was
        /// never told, so paying a distributor at the door showed up as the cashier being short by
        /// exactly that amount.
        /// </summary>
        public decimal ExpectedCash => Cash - MoneyExpenses - Purchases - SupplierCash;
    }

    public static class ShiftReconciliation
    {
        /// <summary>
        /// Adds a shift up.
        /// </summary>
        /// <param name="sales">Every sale in the period. Credit sales and fully returned ones are
        /// dropped here rather than by the caller: neither put money in the drawer, and leaving that
        /// to each screen is how one of them forgets.</param>
        /// <param name="employees">The per-employee rows, for the expenses drawn against the till.</param>
        /// <param name="purchases">Counter purchases paid out of the drawer.</param>
        /// <param name="supplierCash">Cash paid to suppliers, transfers excluded.</param>
        public static ShiftTill Build(
            IEnumerable<Sale> sales,
            IEnumerable<EmployeeDayRow> employees,
            decimal purchases,
            decimal supplierCash)
        {
            List<Sale> takings = (sales ?? Enumerable.Empty<Sale>())
                .Where(s => s.Status != SaleStatus.Returned && s.SaleType != SaleType.Credit)
                .ToList();

            List<EmployeeDayRow> staff = (employees ?? Enumerable.Empty<EmployeeDayRow>()).ToList();

            return new ShiftTill
            {
                // NetTotal, not Total: a refund comes back out of the drawer, so a partly returned
                // sale counts for what it actually left behind.
                Cash = Channel(takings, PaymentMethods.Cash),
                Bankak = Channel(takings, PaymentMethods.Bankak),
                Fawry = Channel(takings, PaymentMethods.Fawry),
                Ocash = Channel(takings, PaymentMethods.Ocash),

                MoneyExpenses = staff.Sum(r => r.MoneyExpenses),
                MedicineExpenses = staff.Sum(r => r.MedicineExpenses),

                Purchases = purchases,
                SupplierCash = supplierCash,
            };
        }

        private static decimal Channel(IEnumerable<Sale> takings, string method)
            => takings.Where(s => PaymentMethods.Normalize(s.PaymentMethod) == method)
                      .Sum(s => s.NetTotal);
    }
}
