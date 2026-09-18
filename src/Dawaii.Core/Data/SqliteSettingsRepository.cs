using System;
using System.Collections.Generic;
using Dawaii.Core.Abstractions;

namespace Dawaii.Core.Data
{
    public class SqliteSettingsRepository : ISettingsRepository
    {
        private readonly IDbConnectionFactory _db;

        public SqliteSettingsRepository(IDbConnectionFactory db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public string Get(string key)
        {
            object v = _db.Scalar("SELECT value FROM settings WHERE key_name=@k", ("@k", key));
            return v == null || v == DBNull.Value ? null : Convert.ToString(v);
        }

        public IReadOnlyDictionary<string, string> GetAll()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in _db.Query("SELECT key_name, value FROM settings",
                r => new KeyValuePair<string, string>(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1))))
                dict[kv.Key] = kv.Value;
            return dict;
        }

        public void Set(string key, string value)
        {
            // Upsert syntax differs by backend.
            string sql = _db.Kind == DbKind.MySql
                ? "INSERT INTO settings (key_name, value) VALUES (@k, @v) ON DUPLICATE KEY UPDATE value=@v"
                : "INSERT INTO settings (key_name, value) VALUES (@k, @v) ON CONFLICT(key_name) DO UPDATE SET value=@v";
            _db.Execute(sql, ("@k", key), ("@v", Db.Text(value)));
        }
    }
}
