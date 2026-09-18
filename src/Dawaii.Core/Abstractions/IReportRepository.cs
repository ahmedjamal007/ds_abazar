using System;
using System.Collections.Generic;
using Dawaii.Core.Services;

namespace Dawaii.Core.Abstractions
{
    public interface IReportRepository
    {
        /// <summary>Best-selling items by units sold in a period (FR-RPT-02).</summary>
        IReadOnlyList<BestSellerRow> BestSellers(DateTime fromInclusive, DateTime toExclusive, int limit);

        /// <summary>Items with stock that had no completed sale in the last <paramref name="days"/> days (FR-RPT-02).</summary>
        IReadOnlyList<DeadStockRow> DeadStock(int days);
    }
}
