using System.Collections.Generic;

namespace Dawaii.Core.Models
{
    public class SaleLine
    {
        public int Id { get; set; }
        public int SaleId { get; set; }
        public int ItemId { get; set; }
        public UnitType UnitType { get; set; }

        /// <summary>Count in the chosen <see cref="UnitType"/> (e.g. 2 boxes).</summary>
        public int Quantity { get; set; }

        /// <summary>Single units per one <see cref="UnitType"/> at sale time (snapshot).</summary>
        public int UnitsEach { get; set; }

        /// <summary>Selling price per single unit at sale time (FR-PRC-01 snapshot).</summary>
        public decimal UnitPrice { get; set; }

        public decimal LineTotal { get; set; }
        public decimal CostTotal { get; set; }

        /// <summary>Total single units this line removes from stock (Quantity * UnitsEach).</summary>
        public int TotalUnits => Quantity * UnitsEach;

        /// <summary>Single units already given back against this line by earlier returns (V1.8).</summary>
        public int ReturnedUnits { get; set; }

        /// <summary>How many of the sold <see cref="UnitType"/> have already come back.</summary>
        public int ReturnedQuantity => UnitsEach > 0 ? ReturnedUnits / UnitsEach : 0;

        /// <summary>How many of the sold <see cref="UnitType"/> the customer may still return.</summary>
        public int RemainingQuantity => Quantity - ReturnedQuantity;

        /// <summary>Single units still outstanding on this line.</summary>
        public int RemainingUnits => TotalUnits - ReturnedUnits;

        /// <summary>Item name captured for display/receipt (not persisted; convenience for UI).</summary>
        public string ItemName { get; set; }

        public List<SaleLineAllocation> Allocations { get; set; } = new List<SaleLineAllocation>();
    }
}
