using System;

namespace Dawaii.Core.Models
{
    /// <summary>
    /// A company the pharmacy buys its medicines from (V2.1 "الموردون"). Deliberately thin: the name on
    /// the invoice is the only thing the pharmacy actually needs to file a purchase against. Everything
    /// else worth knowing about a supplier — who represents them, what they delivered, what is still
    /// owed — lives on the individual invoices, because it changes from one delivery to the next.
    ///
    /// The outstanding balance is NOT stored here. A customer's balance is cached because every sale
    /// touches it; a supplier's is the sum of what its own invoices still owe, and reading it from the
    /// invoices means the two can never drift apart.
    /// </summary>
    public class Supplier
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Filled in by the repository when a listing needs it: what this company is still owed
        /// across every invoice. Not a stored column — see the class note.</summary>
        public decimal Outstanding { get; set; }

        /// <summary>How many invoices this supplier has, for the listing.</summary>
        public int InvoiceCount { get; set; }
    }
}
