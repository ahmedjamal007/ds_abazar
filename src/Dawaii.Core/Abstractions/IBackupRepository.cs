using System.Collections.Generic;
using Dawaii.Core.Models;

namespace Dawaii.Core.Abstractions
{
    public interface IBackupRepository
    {
        void Add(BackupRecord record);
        BackupRecord GetLatestSuccess();
        IReadOnlyList<BackupRecord> GetRecent(int limit);
    }
}
