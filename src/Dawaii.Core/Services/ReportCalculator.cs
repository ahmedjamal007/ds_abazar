using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>Aggregated daily figures (FR-RPT-01). Profit is only populated for Admins (FR-RPT-05).</summary>
    public class DailyReport
    {
        public DateTime Date { get; set; }
        public int TransactionCount { get; set; }
        public decimal TotalSales { get; set; }
        public decimal CashTotal { get; set; }
        public decimal CreditTotal { get; set; }
        /// <summary>Invoices with anything returned against them, whole or partial.</summary>
        public int ReturnedCount { get; set; }

        /// <summary>Money refunded across those invoices.</summary>
        public decimal ReturnedTotal { get; set; }

        public bool ProfitVisible { get; set; }
        public decimal TotalProfit { get; set; }
    }

    /// <summary>Pure aggregation of sales into report figures — testable without a database.</summary>
    public static class ReportCalculator
    {
        public static DailyReport BuildDaily(DateTime date, IEnumerable<Sale> sales, bool includeProfit)
        {
            var all = sales.ToList();

            // Returns are netted off the invoice they came from rather than dropping it: an invoice
            // where the customer brought 5 of 50 boxes back is still a sale, for the 45 they kept.
            var standing = all.Where(s => s.Status != SaleStatus.Returned).ToList();
            var withReturn = all.Where(s => s.HasReturn).ToList();

            return new DailyReport
            {
                Date = date.Date,
                TransactionCount = standing.Count,
                TotalSales = standing.Sum(s => s.NetTotal),
                CashTotal = standing.Where(s => s.SaleType == SaleType.Cash).Sum(s => s.NetTotal),
                CreditTotal = standing.Where(s => s.SaleType == SaleType.Credit).Sum(s => s.NetTotal),
                ReturnedCount = withReturn.Count,
                ReturnedTotal = withReturn.Sum(s => s.RefundedTotal),
                ProfitVisible = includeProfit,
                TotalProfit = includeProfit ? standing.Sum(s => s.Profit) : 0m
            };
        }
    }
}
