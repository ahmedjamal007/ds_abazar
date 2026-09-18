using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>
    /// Transactional debt operations: each records a <see cref="DebtTransaction"/> and updates the
    /// customer's cached balance inside one transaction so the balance can never drift (D-06).
    /// Credit-sale charges are handled by <see cref="ISaleStore"/>; this covers standalone
    /// repayments and manual charges.
    /// </summary>
    public interface IDebtStore
    {
        DebtTransaction RecordPayment(int customerId, decimal amount, int userId, string note);
        DebtTransaction RecordManualCharge(int customerId, decimal amount, int userId, string note);
    }
}
