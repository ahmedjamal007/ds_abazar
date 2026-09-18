using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    public interface IAuditRepository
    {
        void Add(AuditEntry entry);
        IReadOnlyList<AuditEntry> GetRecent(int limit);
    }
}
