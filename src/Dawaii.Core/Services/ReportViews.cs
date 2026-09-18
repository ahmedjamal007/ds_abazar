using Dawaii.Core.Models;

namespace Dawaii.Core.Services
{
    public class BestSellerRow
    {
        public int ItemId { get; set; }
        public string Name { get; set; }
        public int UnitsSold { get; set; }
        public decimal Revenue { get; set; }
    }

    public class DeadStockRow
    {
        public Item Item { get; set; }
        public int AvailableUnits { get; set; }
    }
}
