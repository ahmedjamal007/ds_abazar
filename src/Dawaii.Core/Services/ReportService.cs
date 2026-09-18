using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>Reporting (FR-RPT-*). Profit figures are computed only for Admins (FR-RPT-05).</summary>
    public class ReportService
    {
        private readonly ISaleStore _sales;
        private readonly IReportRepository _reports;

        public ReportService(ISaleStore sales, IReportRepository reports)
        {
            _sales = sales;
            _reports = reports;
        }

        public DailyReport Daily(User user, DateTime date)
        {
            bool includeProfit = user != null && user.IsAdmin;
            var sales = _sales.GetByDateRange(date.Date, date.Date.AddDays(1));
            return ReportCalculator.BuildDaily(date, sales, includeProfit);
        }

        public DailyReport Range(User user, DateTime fromInclusive, DateTime toExclusive)
        {
            bool includeProfit = user != null && user.IsAdmin;
            var sales = _sales.GetByDateRange(fromInclusive, toExclusive);
            return ReportCalculator.BuildDaily(fromInclusive, sales, includeProfit);
        }

        /// <summary>
        /// Best sellers over a period (Admin only, V2.3).
        ///
        /// The sidebar has always hidden التقارير from anyone but the manager, but nothing below the UI
        /// enforced it — the guard lived only in the screen that happened to call this. Reports are where
        /// the pharmacy's trading position is readable, so the rule belongs here, where every future
        /// caller inherits it.
        /// </summary>
        public IReadOnlyList<BestSellerRow> BestSellers(User user, DateTime fromInclusive, DateTime toExclusive, int limit = 20)
        {
            Guard.RequireAdmin(user, "التقارير متاحة للمدير فقط.");
            return _reports.BestSellers(fromInclusive, toExclusive, limit);
        }

        /// <summary>Stock that has not moved (Admin only) — see <see cref="BestSellers"/>.</summary>
        public IReadOnlyList<DeadStockRow> DeadStock(User user, int days = 60)
        {
            Guard.RequireAdmin(user, "التقارير متاحة للمدير فقط.");
            return _reports.DeadStock(days);
        }
    }
}
