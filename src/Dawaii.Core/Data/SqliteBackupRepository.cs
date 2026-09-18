using System;
using System.Collections.Generic;
using System.Data.Common;
using Dawaii.Core.Abstractions;
using Dawaii.Core.Models;

namespace Dawaii.Core.Data
{
    public class SqliteBackupRepository : IBackupRepository
    {
        private readonly IDbConnectionFactory _db;

        public SqliteBackupRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public void Add(BackupRecord b)
            => _db.Execute("INSERT INTO backups (file_path, size_bytes, status) VALUES (@f, @s, @st)",
                ("@f", b.FilePath), ("@s", b.SizeBytes), ("@st", b.Status.ToString()));

        public BackupRecord GetLatestSuccess()
            => _db.QueryOne(
                "SELECT id, file_path, size_bytes, status, created_at FROM backups " +
                "WHERE status='Success' ORDER BY created_at DESC, id DESC LIMIT 1", Map);

        public IReadOnlyList<BackupRecord> GetRecent(int limit)
            => _db.Query(
                "SELECT id, file_path, size_bytes, status, created_at FROM backups " +
                "ORDER BY created_at DESC, id DESC LIMIT @n", Map, ("@n", limit));

        private static BackupRecord Map(DbDataReader r) => new BackupRecord
        {
            Id = r.GetInt32(0),
            FilePath = r.GetString(1),
            SizeBytes = Convert.ToInt64(r.GetValue(2)),
            Status = (BackupStatus)Enum.Parse(typeof(BackupStatus), r.GetString(3), true),
            CreatedAt = Db.GetTime(r, "created_at")
        };
    }
}
