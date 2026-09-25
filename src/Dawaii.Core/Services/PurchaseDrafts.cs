using System;
using System.Collections.Generic;
using System.Linq;
using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>
    /// Deliveries half-typed and set aside (V2.3.2 "مسودات الفواتير").
    ///
    /// A pharmacist is entering a delivery, a customer walks in, and the till has to be served now.
    /// Until this existed the only way out was to close the invoice screen, which threw away every
    /// line already typed; a forty-line delivery was twenty minutes of work lost to one interruption.
    ///
    /// A draft is ONLY typing. Nothing here touches the database: no stock batch, no price change, no
    /// payable to the company. The boxes sit on the counter and the system has not accepted them yet,
    /// which is exactly right — a half-entered delivery must never be money owed or stock on sale. It
    /// becomes real only when the invoice is saved, through the same transaction it always was.
    ///
    /// The store is static and cleared at logout, the same lifetime the POS carts have: a pending
    /// delivery belongs to the person entering it, not to the next cashier who signs in. It is also
    /// memory only — an app crash takes the draft with it. That is a deliberate trade: the alternative,
    /// a draft row in <c>purchase_invoices</c>, would have to be filtered out of four money queries
    /// (supplier balance, total outstanding, invoice count, the listing join), and one missed filter
    /// would show a company as owed for a delivery nobody has confirmed.
    ///
    /// Only NEW invoices are drafted. An edit of an invoice already on file is deliberately not kept:
    /// coming back to a stale edit an hour later could overwrite a correction somebody else made in
    /// the meantime, and the screen has no way to notice.
    /// </summary>
    public static class PurchaseDrafts
    {
        /// <summary>One delivery in progress. Holds what the screen holds — nothing derived.</summary>
        public sealed class Draft
        {
            /// <summary>Stable for the draft's life; the lowest free number, so the list stays short.</summary>
            public int Number;

            public Supplier Supplier;
            public string Representative;
            public string InvoiceNumber;
            public DateTime InvoiceDate;
            public readonly List<PurchaseInvoiceLine> Lines = new List<PurchaseInvoiceLine>();
            public DateTime SavedAt;

            public decimal Total => Lines.Sum(l => l.LineTotal);

            /// <summary>How the draft reads in a list: company, number if it has one, and its size.</summary>
            public string Describe()
            {
                string name = Supplier?.Name ?? "—";
                string number = string.IsNullOrWhiteSpace(InvoiceNumber) ? "بدون رقم" : InvoiceNumber;
                return "مسودة " + Number + " — " + name + " — " + number +
                       " — " + Lines.Count + " صنف — " + Total.ToString("#,##0.00");
            }
        }

        private static readonly List<Draft> Items = new List<Draft>();

        /// <summary>Every draft waiting, oldest number first.</summary>
        public static IReadOnlyList<Draft> All => Items.OrderBy(d => d.Number).ToList();

        public static int Count => Items.Count;

        /// <summary>Forgets every draft — called at logout, so the next user starts clean.</summary>
        public static void Clear() => Items.Clear();

        public static Draft ById(int number) => Items.FirstOrDefault(d => d.Number == number);

        /// <summary>
        /// Files a draft under <paramref name="number"/>, replacing one already there. Pass 0 for a
        /// draft that has never been set aside before and a free number is taken.
        /// </summary>
        public static Draft Save(int number, Supplier supplier, string representative, string invoiceNumber,
            DateTime invoiceDate, IEnumerable<PurchaseInvoiceLine> lines)
        {
            Draft draft = number > 0 ? ById(number) : null;
            if (draft == null)
            {
                int free = 1;
                while (Items.Any(d => d.Number == free)) free++;
                draft = new Draft { Number = free };
                Items.Add(draft);
            }

            draft.Supplier = supplier;
            draft.Representative = representative;
            draft.InvoiceNumber = invoiceNumber;
            draft.InvoiceDate = invoiceDate;
            draft.SavedAt = DateTime.Now;

            // A copy, not the screen's own list: the form goes on editing its lines after this returns,
            // and a draft that changed underneath the user would be worse than no draft at all.
            draft.Lines.Clear();
            draft.Lines.AddRange(lines.Select(Copy));
            return draft;
        }

        public static void Remove(int number)
        {
            Draft draft = ById(number);
            if (draft != null) Items.Remove(draft);
        }

        /// <summary>
        /// A detached copy of one line. The invoice screen hands its own objects to the line editor and
        /// replaces them in place, so a draft holding the same references would follow those edits.
        /// </summary>
        private static PurchaseInvoiceLine Copy(PurchaseInvoiceLine l) => new PurchaseInvoiceLine
        {
            Id = l.Id,
            InvoiceId = l.InvoiceId,
            ItemId = l.ItemId,
            ItemName = l.ItemName,
            QuantityBoxes = l.QuantityBoxes,
            StripsPerBox = l.StripsPerBox,
            BoxPurchasePrice = l.BoxPurchasePrice,
            BoxSellingPrice = l.BoxSellingPrice,
            ExpiryDate = l.ExpiryDate,
            BatchNumber = l.BatchNumber,
            StockBatchId = l.StockBatchId
        };
    }
}
