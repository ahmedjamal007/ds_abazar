using System;

namespace Dawaii.Core.Models
{
    public class BackupRecord
    {
        public int Id { get; set; }
        public string FilePath { get; set; }
        public long SizeBytes { get; set; }
        public BackupStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
