using System;
using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>
    /// Transactional persistence for sales and returns (NFR-02). Implementations must perform the
    /// entire mutation — sale, lines, allocations, stock decrement, debt/customer update, audit —
    /// inside a single InnoDB transaction so a power loss can never leave a partial sale.
    /// </summary>
    public interface ISaleStore
    {
        /// <summary>Persists a built sale atomically; throws <see cref="InsufficientStockException"/>
        /// if another terminal consumed the stock first (concurrency guard, acceptance §6.5).</summary>
        Sale Save(Sale sale);

        /// <summary>Full sale with lines and allocations, or null.</summary>
        Sale GetById(int id);

        /// <summary>
        /// Full sale with lines looked up by the printed invoice number, or null. This is the number
        /// on the customer's receipt, so it is what the return screen searches by.
        /// </summary>
        Sale GetBySaleNumber(int saleNumber);

        IReadOnlyList<Sale> GetByDateRange(DateTime fromInclusive, DateTime toExclusive);

        /// <summary>Returns everything still outstanding on a sale: restores stock, records the return,
        /// reverses any debt (FR-POS-08).</summary>
        Return ReturnSale(int saleId, int userId, string reason);

        /// <summary>
        /// Returns only the requested quantities — 5 of the 50 boxes sold, say. Restores those units to
        /// the batches they were sold from, refunds their share of the invoice and leaves the rest of it
        /// standing. Throws <see cref="ValidationException"/> if a quantity exceeds what is outstanding.
        /// </summary>
        Return ReturnItems(int saleId, int userId, string reason, IList<ReturnRequest> requests);
    }
}
