using System;
using System.Data.Common;
using System.Data.SQLite;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Local single-file SQLite backend (default, zero setup). WAL journalling + a busy timeout let
    /// the app's several short-lived connections coexist; foreign keys are enforced per connection.
    /// </summary>
    public class SqliteConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public DbKind Kind => DbKind.Sqlite;
        public string SqliteFilePath { get; }
        public string ConnectionString => _connectionString;
        public string LastInsertIdSql => "last_insert_rowid()";

        public SqliteConnectionFactory(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", nameof(databasePath));
            SqliteFilePath = databasePath;

            var b = new SQLiteConnectionStringBuilder
            {
                DataSource = databasePath,
                Version = 3,
                ForeignKeys = true,
                BusyTimeout = 5000,
                JournalMode = SQLiteJournalModeEnum.Wal,
                // WAL's companion setting. At the default (FULL) SQLite fsyncs the log on every single
                // commit, which measured 3.5 ms against 0.05 ms here — and a commit happens per sale,
                // per return and per stock movement, so it was the largest single cost in the app.
                //
                // NORMAL still fsyncs at each checkpoint, and under WAL it remains crash-safe for the
                // application: killing Dawaii, or Windows killing it, cannot corrupt or lose a committed
                // sale. What it gives up is the last few commits in a power cut or a kernel panic, where
                // the log is written but not yet flushed. That is the documented WAL recommendation and
                // the trade the owner chose: a till that keeps up, backed by the nightly backup.
                SyncMode = SynchronizationModes.Normal,
                DateTimeKind = DateTimeKind.Local,
                FailIfMissing = false,
                // Reuse native connections across the app's short-lived opens — significant win
                // since every repository call opens a connection (disabled by default in SQLite).
                Pooling = true
            };
            _connectionString = b.ConnectionString;
        }

        public DbConnection OpenConnection()
        {
            var conn = new SQLiteConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
