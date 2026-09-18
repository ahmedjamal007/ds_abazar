using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    public interface ICustomerRepository
    {
        Customer GetById(int id);
        IReadOnlyList<Customer> Search(string term, int limit = 50);
        IReadOnlyList<Customer> GetAll();
        IReadOnlyList<Customer> GetWithDebt();
        int Add(Customer customer);
        void Update(Customer customer);

        /// <summary>
        /// Removes the customer. Returns false — changing nothing — when they have invoices or ledger
        /// entries, which stay so the pharmacy's history keeps naming who bought and who owed.
        /// </summary>
        bool Delete(int customerId);

        /// <summary>Transactions read (FR-DBT-04 statement).</summary>
        IReadOnlyList<DebtTransaction> GetTransactions(int customerId);
    }
}
