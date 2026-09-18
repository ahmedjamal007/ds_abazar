using System;
using System.Data.Common;
using MySql.Data.MySqlClient;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Shared-server backend for network mode (V1.2 req 6): a MySQL instance running as a Windows
    /// service on the manager's PC that starts automatically at boot (no need to open the app).
    /// Any number of counter PCs connect to it over the LAN. The same repositories run against this
    /// or SQLite via <see cref="IDbConnectionFactory"/>.
    /// </summary>
    public class MySqlConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public DbKind Kind => DbKind.MySql;
        public string SqliteFilePath => null;
        public string ConnectionString => _connectionString;
        public string LastInsertIdSql => "LAST_INSERT_ID()";

        public MySqlConnectionFactory(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string is required.", nameof(connectionString));
            _connectionString = connectionString;
        }

        public static string BuildConnectionString(string host, uint port, string database, string user, string password)
        {
            var b = new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = port,
                Database = database,
                UserID = user,
                Password = password,
                CharacterSet = "utf8mb4",
                ConnectionTimeout = 6,          // stay responsive if the manager PC is briefly unreachable
                DefaultCommandTimeout = 30,
                Pooling = true,
                AllowUserVariables = true,
                // MySQL 8 defaults to caching_sha2_password; allow the key exchange over the LAN.
                AllowPublicKeyRetrieval = true,
                SslMode = MySqlSslMode.Preferred,
                // Store/compare our TEXT-format timestamps consistently.
                ConvertZeroDateTime = true
            };
            return b.ConnectionString;
        }

        public DbConnection OpenConnection()
        {
            var conn = new MySqlConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
