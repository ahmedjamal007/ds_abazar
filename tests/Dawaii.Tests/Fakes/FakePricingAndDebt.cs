using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Tests.Fakes
{
    /// <summary>In-memory debt store mirroring the atomic ledger + balance update.</summary>
    public class FakeDebtStore : IDebtStore
    {
        private readonly FakeCustomerRepository _customers;
        public FakeDebtStore(FakeCustomerRepository customers) { _customers = customers; }

        public DebtTransaction RecordPayment(int customerId, decimal amount, int userId, string note)
            => _customers.Apply(customerId, DebtTransactionType.Payment, amount, null, userId, note);

        public DebtTransaction RecordManualCharge(int customerId, decimal amount, int userId, string note)
            => _customers.Apply(customerId, DebtTransactionType.Charge, amount, null, userId, note);
    }
}
