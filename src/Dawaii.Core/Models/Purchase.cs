using System;

namespace Dawaii.Core.Models
{
    /// <summary>
    /// A purchase the pharmacy made from someone who came to sell goods — bags (أكياس) and other
    /// miscellaneous supplies (V1.4 "المشتريات"). This is a standalone spend log, not tied to the
    /// medicine catalog or stock: it records who sold it, what it was, quantity, price and the total
    /// paid. Cash purchases reduce the expected cash in the drawer on the shift report.
    /// </summary>
    public class Purchase
    {
        public int Id { get; set; }

        /// <summary>Who the goods were bought from (المورد / البائع). Optional.</summary>
        public string SupplierName { get; set; }

        /// <summary>What was bought (البيان) — e.g. "أكياس". Required.</summary>
        public string Description { get; set; }

        public int Quantity { get; set; }

        /// <summary>Price per one unit (سعر الوحدة).</summary>
        public decimal UnitPrice { get; set; }

        /// <summary>Total paid = <see cref="Quantity"/> × <see cref="UnitPrice"/> (الإجمالي).</summary>
        public decimal Amount { get; set; }

        public string Note { get; set; }

        /// <summary>The user who recorded the purchase.</summary>
        public int UserId { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>Recording user's name, captured for display (not persisted on the row).</summary>
        public string UserName { get; set; }
    }
}
