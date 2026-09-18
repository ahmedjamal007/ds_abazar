using System;
using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>Standalone purchases log — goods bought from people who come to sell (V1.4 "المشتريات").</summary>
    public interface IPurchaseRepository
    {
        int Add(Purchase purchase);

        /// <summary>Purchases recorded in the period, newest first, with the recording user's name.</summary>
        IReadOnlyList<Purchase> GetRange(DateTime fromInclusive, DateTime toExclusive);

        /// <summary>Total amount paid for purchases in the period.</summary>
        decimal TotalInRange(DateTime fromInclusive, DateTime toExclusive);

        void Delete(int id);
    }
}
