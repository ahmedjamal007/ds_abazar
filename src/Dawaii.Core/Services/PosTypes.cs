using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    /// <summary>One requested line in the POS cart before the sale is built.</summary>
    public class CartLine
    {
        public int ItemId { get; set; }
        public UnitType UnitType { get; set; } = UnitType.Unit;
        public int Quantity { get; set; } = 1;
    }

    /// <summary>Non-line details needed to build a sale.</summary>
    public class SaleHeader
    {
        public int UserId { get; set; }
        public SaleType SaleType { get; set; } = SaleType.Cash;
        public int? CustomerId { get; set; }
        public decimal Discount { get; set; }
        public string Terminal { get; set; }
    }
}
