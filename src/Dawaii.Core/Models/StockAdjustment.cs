using System;

namespace Dawaii.Core.Models
{
    /// <summary>Manual stock correction (damage, loss, count fix) — Admin only, reason mandatory (FR-INV-04).</summary>
    public class StockAdjustment
    {
        public int Id { get; set; }
        public int ItemId { get; set; }
        public int? BatchId { get; set; }

        /// <summary>Signed change in single units: negative = loss/damage, positive = correction.</summary>
        public int DeltaUnits { get; set; }

        public string Reason { get; set; }
        public int UserId { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
