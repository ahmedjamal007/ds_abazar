using System;
using System.Collections.Generic;

namespace Dawaii.Core.Models
{
    /// <summary>A recorded return against a sale (FR-POS-08). May cover part of the invoice.</summary>
    public class Return
    {
        public int Id { get; set; }
        public int SaleId { get; set; }
        public int UserId { get; set; }
        public string Reason { get; set; }

        /// <summary>Money refunded by this return (not necessarily the whole invoice).</summary>
        public decimal Total { get; set; }

        /// <summary>Cost of the goods this return put back into stock.</summary>
        public decimal Cost { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>True when this return took back the last outstanding unit of the invoice.</summary>
        public bool CompletedTheSale { get; set; }

        public List<ReturnLine> Lines { get; set; } = new List<ReturnLine>();
    }

    /// <summary>One item's share of a return: how much came back, and what it was worth.</summary>
    public class ReturnLine
    {
        public int Id { get; set; }
        public int ReturnId { get; set; }
        public int SaleLineId { get; set; }
        public int ItemId { get; set; }

        /// <summary>Count in the sale line's unit type — 5 of the 50 boxes sold.</summary>
        public int Quantity { get; set; }

        /// <summary>Single units restored to stock (<see cref="Quantity"/> × the line's units each).</summary>
        public int Units { get; set; }

        public decimal Amount { get; set; }
        public decimal Cost { get; set; }

        /// <summary>Item name for display; not persisted here (it lives on the item).</summary>
        public string ItemName { get; set; }
    }

    /// <summary>
    /// What the return screen asks for: take <see cref="Quantity"/> back off this sale line. The
    /// quantity is in the unit the line was sold in, which is the only unit the customer can hand back.
    /// </summary>
    public class ReturnRequest
    {
        public ReturnRequest() { }

        public ReturnRequest(int saleLineId, int quantity)
        {
            SaleLineId = saleLineId;
            Quantity = quantity;
        }

        public int SaleLineId { get; set; }
        public int Quantity { get; set; }
    }
}
