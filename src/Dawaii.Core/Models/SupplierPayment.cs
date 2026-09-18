using System;

namespace Dawaii.Core.Models
{
    /// <summary>Money handed to a supplier against one of their invoices (V2.1). Every payment names the
    /// invoice it settles, so "what is still owed on this delivery" and "what is owed to this company"
    /// are the same question asked at two levels rather than two separately maintained numbers.</summary>
    public class SupplierPayment
    {
        public int Id { get; set; }
        public int SupplierId { get; set; }
        public int InvoiceId { get; set; }
        public decimal Amount { get; set; }
        public int UserId { get; set; }
        /// <summary>How the company was paid: "Cash" (out of the till) or "Bank". NULL on rows written
        /// before V2.3, and read as cash — which is how deliveries were actually being settled.</summary>
        public string PaymentMethod { get; set; }

        /// <summary>True unless the payment was explicitly recorded as a bank transfer. Only cash comes
        /// out of the drawer, so only cash belongs in the shift report's expected-cash figure.</summary>
        public bool IsCash => !string.Equals(PaymentMethod, "Bank", System.StringComparison.OrdinalIgnoreCase);

        public string Note { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
