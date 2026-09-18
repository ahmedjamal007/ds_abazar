namespace Dawaii.Core.Models
{
    /// <summary>Records which batch supplied how many units to a sale line (enables exact returns + profit).</summary>
    public class SaleLineAllocation
    {
        public int Id { get; set; }
        public int SaleLineId { get; set; }
        public int BatchId { get; set; }
        public int Units { get; set; }
        public decimal UnitCost { get; set; }

        public decimal CostTotal => Units * UnitCost;
    }
}
