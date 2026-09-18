using System;
using System.Collections.Generic;

namespace Dawaii.Core.Models
{
    public class Sale
    {
        public int Id { get; set; }

        /// <summary>
        /// The invoice number printed on the receipt and typed into the return screen: a plain
        /// integer, equal to <see cref="Id"/>. It used to be "yyyyMMdd-id", which these right-to-left
        /// screens render reversed, so staff could not type back the number they were holding.
        /// </summary>
        public int SaleNumber { get; set; }

        public int UserId { get; set; }
        public int? CustomerId { get; set; }
        public SaleType SaleType { get; set; }

        /// <summary>How a non-credit sale was settled — a code from <see cref="Services.PaymentMethods"/>:
        /// "Cash" (كاش), "Bankak" (بنكك), "Fawry" (فوري) or "Ocash" (أوكاش). Null for credit (V1.3).</summary>
        public string PaymentMethod { get; set; }

        public decimal Subtotal { get; set; }
        public decimal Discount { get; set; }
        public decimal Total { get; set; }

        /// <summary>Total cost of goods consumed by this sale (profit = Total - CostTotal).</summary>
        public decimal CostTotal { get; set; }

        /// <summary>Money refunded off this invoice so far (V1.8 partial returns).</summary>
        public decimal ReturnedTotal { get; set; }

        /// <summary>Cost of the goods that came back off this invoice so far.</summary>
        public decimal ReturnedCost { get; set; }

        public SaleStatus Status { get; set; } = SaleStatus.Completed;
        public string Terminal { get; set; }
        public DateTime CreatedAt { get; set; }

        public List<SaleLine> Lines { get; set; } = new List<SaleLine>();

        /// <summary>
        /// What was actually refunded. A fully returned invoice refunded everything by definition —
        /// which is also how returns recorded before V1.8 read, since those have no per-line rows.
        /// </summary>
        public decimal RefundedTotal => Status == SaleStatus.Returned ? Total : ReturnedTotal;

        /// <summary>Cost of the returned goods, with the same whole-invoice rule as <see cref="RefundedTotal"/>.</summary>
        public decimal RefundedCost => Status == SaleStatus.Returned ? CostTotal : ReturnedCost;

        /// <summary>Revenue the pharmacy kept: the invoice less anything refunded.</summary>
        public decimal NetTotal => Total - RefundedTotal;

        /// <summary>Cost of the goods the customer kept.</summary>
        public decimal NetCost => CostTotal - RefundedCost;

        /// <summary>Profit on what the customer kept.</summary>
        public decimal Profit => NetTotal - NetCost;

        /// <summary>True once anything at all has been returned against this invoice.</summary>
        public bool HasReturn => Status != SaleStatus.Completed;
    }
}
