namespace Dawaii.Core.Models
{
    /// <summary>A QR/barcode value (stored as text) associated with exactly one item (FR-QRC-01/06).</summary>
    public class ItemCode
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public string Code { get; set; }
    }
}
