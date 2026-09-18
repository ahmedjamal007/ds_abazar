using System;

namespace Dawaii.Core.Models
{
    public class DebtTransaction
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public DebtTransactionType Type { get; set; }

        /// <summary>Positive magnitude of the charge/payment.</summary>
        public decimal Amount { get; set; }

        public int? SaleId { get; set; }
        public int UserId { get; set; }
        public string Note { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Signed effect on the running balance: +Amount for a Charge, -Amount for a Payment.</summary>
        public decimal SignedAmount => Type == DebtTransactionType.Charge ? Amount : -Amount;
    }
}
