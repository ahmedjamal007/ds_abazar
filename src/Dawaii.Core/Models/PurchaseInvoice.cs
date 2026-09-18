using System;
using System.Collections.Generic;

namespace Dawaii.Core.Models
{
    /// <summary>
    /// One delivery from a supplier (V2.1): the invoice that came with the boxes. Its lines are what
    /// went onto the shelf, and its total is what the pharmacy owes for them.
    ///
    /// The total is the sum of the lines, never typed — an invoice whose stated total disagrees with
    /// what was actually received would put the payable and the stock out of step with each other.
    /// </summary>
    public class PurchaseInvoice
    {
        public int Id { get; set; }
        public int SupplierId { get; set; }

        /// <summary>Filled in on read, for screens that show the invoice away from its supplier.</summary>
        public string SupplierName { get; set; }

        /// <summary>The distributor or representative who brought the delivery — a name, a phone number,
        /// or both. Free text: it is how the pharmacy chases a wrong or missing box, not a foreign key.</summary>
        public string Representative { get; set; }

        /// <summary>The number printed on the supplier's own invoice. Free text — every company numbers
        /// its paperwork differently, and this has to match the paper in the folder.</summary>
        public string InvoiceNumber { get; set; }

        public DateTime InvoiceDate { get; set; }

        /// <summary>Sum of the lines — what the delivery cost.</summary>
        public decimal Total { get; set; }

        /// <summary>How much of the total has been handed over so far.</summary>
        public decimal AmountPaid { get; set; }

        /// <summary>How the money handed over at the door was paid — "Cash" or "Bank". Carried only as
        /// far as the payment row it creates; the invoice itself records what is owed, not how.</summary>
        public string PaymentMethod { get; set; }

        public int UserId { get; set; }

        /// <summary>Who filed this order, filled in on read. The manager's report is mostly this column:
        /// an order is a commitment of the pharmacy's money, so it has to say who made it.</summary>
        public string UserName { get; set; }
        public DateTime CreatedAt { get; set; }

        public List<PurchaseInvoiceLine> Lines { get; } = new List<PurchaseInvoiceLine>();

        /// <summary>How many medicines are on this invoice. Counted by the query, because a listing does
        /// not load the lines — a company with years of deliveries would be reading thousands of rows to
        /// print one number per line of the grid.</summary>
        public int LineCount { get; set; }

        /// <summary>What is still owed on this invoice. Never negative: overpaying a supplier is a
        /// credit to sort out with them, not a debt the pharmacy can be shown as owing less than zero.</summary>
        public decimal Outstanding => Math.Max(0m, decimal.Round(Total - AmountPaid, 2));

        /// <summary>Paid / partly paid / unpaid, derived from the two amounts rather than stored, so a
        /// payment can never leave the status saying something the numbers contradict.</summary>
        public PurchaseInvoiceStatus Status
            => Outstanding <= 0m ? PurchaseInvoiceStatus.Paid
             : AmountPaid > 0m ? PurchaseInvoiceStatus.PartiallyPaid
             : PurchaseInvoiceStatus.Unpaid;
    }
}
