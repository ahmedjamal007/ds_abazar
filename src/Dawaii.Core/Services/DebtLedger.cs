using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>One statement line: a transaction and the running balance after it.</summary>
    public class StatementRow
    {
        public DebtTransaction Transaction { get; set; }
        public decimal RunningBalance { get; set; }
    }

    /// <summary>Pure debt-balance calculations (FR-DBT-04). A Charge adds to the balance, a Payment subtracts.</summary>
    public static class DebtLedger
    {
        public static IReadOnlyList<StatementRow> BuildStatement(IEnumerable<DebtTransaction> transactions)
        {
            var ordered = transactions.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToList();
            var rows = new List<StatementRow>(ordered.Count);
            decimal running = 0m;
            foreach (DebtTransaction t in ordered)
            {
                running += t.SignedAmount;
                rows.Add(new StatementRow { Transaction = t, RunningBalance = running });
            }
            return rows;
        }

        public static decimal Balance(IEnumerable<DebtTransaction> transactions)
            => transactions.Sum(t => t.SignedAmount);

        public static decimal TotalOutstanding(IEnumerable<Customer> customers)
            => customers.Where(c => c.Balance > 0).Sum(c => c.Balance);
    }
}
