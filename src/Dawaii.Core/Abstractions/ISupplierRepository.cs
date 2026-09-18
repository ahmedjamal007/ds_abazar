using System;
using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>
    /// Suppliers, their invoices and what is still owed on them (V2.1). Saving an invoice is the one
    /// operation here that must be atomic: it writes the invoice, its lines AND the stock batches those
    /// lines put on the shelf. A half-written delivery would leave the pharmacy either owing money for
    /// boxes it cannot sell or selling boxes it never recorded owing for.
    /// </summary>
    public interface ISupplierRepository
    {
        Supplier GetById(int id);

        /// <summary>Suppliers matching the term, each carrying its outstanding total and invoice count.</summary>
        IReadOnlyList<Supplier> Search(string term, int limit = 100);

        /// <summary>Only the companies still owed money, most owed first.</summary>
        IReadOnlyList<Supplier> WithOutstanding();

        int Add(Supplier supplier);
        void Update(Supplier supplier);

        /// <summary>Removes the supplier. Returns false — changing nothing — when invoices are filed
        /// against them, because those name who the stock was bought from.</summary>
        bool Delete(int supplierId);

        /// <summary>Every invoice for one supplier, newest first. Lines are not loaded.</summary>
        IReadOnlyList<PurchaseInvoice> GetInvoices(int supplierId);

        /// <summary>
        /// Finds invoices by the things written on the paper: the invoice number, the representative who
        /// brought it, or the company's name. <paramref name="supplierId"/> narrows the hunt to one
        /// company; pass null to search every company at once, which is how an invoice is found when all
        /// anyone remembers is its number.
        /// </summary>
        IReadOnlyList<PurchaseInvoice> SearchInvoices(int? supplierId, string term, int limit = 200);

        /// <summary>Every order filed in the period, newest first, each naming the company it went to and
        /// the employee who placed it. The manager's report is built on this.</summary>
        IReadOnlyList<PurchaseInvoice> InvoicesInRange(DateTime fromInclusive, DateTime toExclusive);

        /// <summary>One invoice with its lines filled in.</summary>
        PurchaseInvoice GetInvoice(int invoiceId);

        /// <summary>
        /// Writes the invoice, its lines and a stock batch per line in a single transaction, and returns
        /// the new invoice id. Each line's <see cref="PurchaseInvoiceLine.StockBatchId"/> is filled in.
        /// </summary>
        int CreateInvoice(PurchaseInvoice invoice, int actingUserId);

        /// <summary>
        /// Rewrites an invoice that was filed wrongly — its header, its lines, and the stock those lines
        /// put on the shelf — in one transaction, and returns the recomputed total (V2.3).
        ///
        /// A line carrying an id is matched to the stored line and corrected in place; a line with no id
        /// is new and gets a batch of its own; a stored line the caller does not send back is removed
        /// along with its batch.
        ///
        /// Quantities are corrected by DIFFERENCE, never by assignment: a line changed from 10 boxes to
        /// 8 takes two boxes off whatever is left of its batch. Assigning the new quantity outright would
        /// silently restock every unit sold since the delivery arrived. The same rule is what refuses a
        /// correction that would push a batch below zero — those units are in customers' hands, and
        /// editing a piece of paper does not bring them back.
        /// </summary>
        decimal UpdateInvoice(PurchaseInvoice invoice, int actingUserId);

        /// <summary>Records money paid against an invoice and moves that invoice's amount_paid, in one
        /// transaction so the payment row and the running total cannot disagree.</summary>
        void AddPayment(SupplierPayment payment);

        /// <summary>Payments made against one invoice, oldest first.</summary>
        IReadOnlyList<SupplierPayment> GetPayments(int invoiceId);

        /// <summary>Every payment to any supplier in the period, for the drawer reconciliation.</summary>
        IReadOnlyList<SupplierPayment> PaymentsInRange(DateTime fromInclusive, DateTime toExclusive);

        /// <summary>What every supplier is still owed, in total.</summary>
        decimal TotalOutstanding();
    }
}
