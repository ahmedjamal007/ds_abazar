using System;

namespace Dawaii.Core.Models
{
    /// <summary>Append-only audit record (FR-USR-03).</summary>
    public class AuditEntry
    {
        public long Id { get; set; }
        public int? UserId { get; set; }
        public string Action { get; set; }
        public string Entity { get; set; }
        public int? EntityId { get; set; }
        public string Details { get; set; }
        public string Terminal { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
