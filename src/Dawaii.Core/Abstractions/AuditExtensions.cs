using System;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    /// <summary>Convenience for the common "who did what to which record" audit rows (FR-USR-03).</summary>
    public static class AuditExtensions
    {
        public static void Log(this IAuditRepository audit, User user,
            string action, string entity, int? entityId, string details)
        {
            audit.Add(new AuditEntry
            {
                UserId = user?.Id,
                Action = action,
                Entity = entity,
                EntityId = entityId,
                Details = details,
                CreatedAt = DateTime.Now
            });
        }
    }
}
