using System;
using System.Collections.Generic;
using System.Data.Common;

namespace Dawaii.Core.Data
{
    /// <summary>
    /// Provider-agnostic one-statement helpers over <see cref="IDbConnectionFactory"/> (works with both
    /// SQLite and MySQL). Repositories don't repeat the open-connection/command/parameter boilerplate.
    /// Multi-statement transactions (sale/debt stores, initializer) manage their own connection.
    /// </summary>
    internal static class DbExec
    {
        public static int Execute(this IDbConnectionFactory f, string sql, params (string Name, object Value)[] ps)
        {
            using (var conn = f.OpenConnection())
            using (var cmd = Build(conn, sql, ps))
                return cmd.ExecuteNonQuery();
        }

        public static object Scalar(this IDbConnectionFactory f, string sql, params (string Name, object Value)[] ps)
        {
            using (var conn = f.OpenConnection())
            using (var cmd = Build(conn, sql, ps))
                return cmd.ExecuteScalar();
        }

        /// <summary>Runs an INSERT then reads the generated id (per-connection) for the active backend.</summary>
        public static int InsertId(this IDbConnectionFactory f, string sql, params (string Name, object Value)[] ps)
        {
            using (var conn = f.OpenConnection())
            {
                using (var cmd = Build(conn, sql, ps))
                    cmd.ExecuteNonQuery();
                using (var idCmd = conn.CreateCommand())
                {
                    idCmd.CommandText = "SELECT " + f.LastInsertIdSql;
                    return Convert.ToInt32(idCmd.ExecuteScalar());
                }
            }
        }

        public static List<T> Query<T>(this IDbConnectionFactory f, string sql,
            Func<DbDataReader, T> map, params (string Name, object Value)[] ps)
        {
            var list = new List<T>();
            using (var conn = f.OpenConnection())
            using (var cmd = Build(conn, sql, ps))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(map(r));
            return list;
        }

        /// <summary>First row mapped, or null when the query returns nothing.</summary>
        public static T QueryOne<T>(this IDbConnectionFactory f, string sql,
            Func<DbDataReader, T> map, params (string Name, object Value)[] ps) where T : class
        {
            using (var conn = f.OpenConnection())
            using (var cmd = Build(conn, sql, ps))
            using (var r = cmd.ExecuteReader())
                return r.Read() ? map(r) : null;
        }

        private static DbCommand Build(DbConnection conn, string sql, (string Name, object Value)[] ps)
        {
            DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParams(cmd, ps);
            return cmd;
        }

        /// <summary>Adds named parameters to any provider's command (used by repos and the stores).</summary>
        public static void AddParams(DbCommand cmd, params (string Name, object Value)[] ps)
        {
            foreach (var p in ps)
            {
                DbParameter param = cmd.CreateParameter();
                param.ParameterName = p.Name;
                param.Value = p.Value ?? DBNull.Value;
                cmd.Parameters.Add(param);
            }
        }
    }
}
